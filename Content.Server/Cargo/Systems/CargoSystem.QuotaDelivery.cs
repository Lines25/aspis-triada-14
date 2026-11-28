using System.Linq;
using Content.Server._Aspis.Organizations;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Cargo;
using Content.Server._NF.Trade;
using Content.Server.Station.Components;
using Robust.Shared.Map.Components;
using Content.Shared._NF.Trade;

namespace Content.Server.Cargo.Systems;

/// <summary>
/// Authenticated Cargo Requests -> physical quota crates -> real A-D trade stations.
/// No client-controlled reason text can assign a quota manifest. The authorized
/// order object is registered directly by the server, and each crate gets a fresh ID.
/// </summary>
public sealed partial class CargoSystem
{
    private sealed class QuotaOrderAuthorization(Guid organization, string code, int total, int start,
        int[] postTargets, bool frontier, EntityUid sourceStation)
    {
        public readonly Guid Organization = organization;
        public readonly string Code = code;
        public readonly int Total = total;
        public readonly int Start = start;
        public readonly int[] PostTargets = postTargets;
        public readonly bool Frontier = frontier;
        public readonly EntityUid SourceStation = sourceStation;
    }

    private const string QuotaTradeCratePrototype = "CrateTradeSecureNormal";
    private readonly Dictionary<CargoOrderData, QuotaOrderAuthorization> _quotaOrders = new();

    // The actual Auric station is marked by its gameMap prototype, not a
    // player-visible name that can change or be copied by another grid.
    public bool IsQuotaTradeHub(EntityUid? grid)
    {
        return grid is { Valid: true } id && !Deleted(id) &&
               _station.GetOwningStation(id) is { Valid: true } station &&
               IsQuotaTradeHubStation(station);
    }

    private bool IsQuotaTradeHubStation(EntityUid station) =>
        !TerminatingOrDeleted(station) && HasComp<OrganizationTradeHubComponent>(station) &&
        TryComp<StationDataComponent>(station, out var data) &&
        data.Grids.Any(grid => !TerminatingOrDeleted(grid) && HasComp<MapGridComponent>(grid)) &&
        TryComp<TradeCrateDestinationComponent>(station, out var destination) &&
        destination.DestinationProto.Id == "Trade";

    public bool HasQuotaTradeHub()
    {
        var hubs = EntityQueryEnumerator<OrganizationTradeHubComponent>();
        while (hubs.MoveNext(out var station, out _))
            if (IsQuotaTradeHubStation(station))
                return true;
        return false;
    }

    public bool HasAllQuotaTradeDestinations() =>
        Enumerable.Range(0, 4).All(post => TryGetQuotaTradeDestination(post, out _));

    private bool IsActiveFrontierAuthorization(QuotaOrderAuthorization authorization) =>
        EntityManager.System<OrganizationSystem>().IsActiveFrontierCargoOrder(authorization.Organization,
            authorization.Code, authorization.Total);

    private bool CanDispatchQuotaOrder(CargoOrderData order, EntityUid? grid)
    {
        if (!_quotaOrders.TryGetValue(order, out var auth))
            return true;

        // The linked pad must belong to the same server-authorized station
        // as the issuing Cargo Requests console, not a mutable grid name.
        return grid is { Valid: true } id && !Deleted(id) &&
               _station.GetOwningStation(id) == auth.SourceStation && IsQuotaTradeHub(id) &&
               (auth.Frontier ? IsActiveFrontierAuthorization(auth) : HasAllQuotaTradeDestinations());
    }

    private void ResetQuotaDeliveryRound() => _quotaOrders.Clear();

    /// <summary>
    /// A-D are the physical Auric Trade Depots with CargoA through CargoD
    /// destination prototypes. The central Trade hub and optional markets
    /// must never shift the assignment or count as a depot.
    /// </summary>
    public bool TryGetQuotaTradeDestination(int post, out EntityUid destination)
    {
        destination = EntityUid.Invalid;
        if (!OrganizationTradeDestinationRules.TryGetDepotId(post, out var depotId))
            return false;
        foreach (var uid in _destinations)
        {
            if (TerminatingOrDeleted(uid) ||
                !TryComp<StationDataComponent>(uid, out var data) ||
                !data.Grids.Any(grid => !TerminatingOrDeleted(grid) && HasComp<MapGridComponent>(grid)) ||
                !TryComp<TradeCrateDestinationComponent>(uid, out var component) ||
                component.DestinationProto.Id != depotId)
                continue;
            // An ambiguous duplicate is an invalid destination, not an arbitrary choice.
            if (destination.Valid)
            {
                destination = EntityUid.Invalid;
                return false;
            }
            destination = uid;
        }
        return destination.Valid;
    }

    public bool IsQuotaExportAtDestination(EntityUid crate, int post)
    {
        return TryGetQuotaTradeDestination(post, out var destination) &&
            _station.GetOwningStation(crate) == destination;
    }

    public bool IsQuotaCrateAtDestination(EntityUid crate, OrganizationQuotaCrateComponent manifest)
    {
        return TryGetQuotaTradeDestination(manifest.Post, out var destination) &&
            manifest.DestinationStation == destination &&
            _station.GetOwningStation(crate) == destination;
    }

    private bool StampQuotaCrate(CargoOrderData order, EntityUid crate)
    {
        if (!_quotaOrders.TryGetValue(order, out var authorization))
            return true;
        if (order.NumDispatched < 1 || order.NumDispatched > order.OrderQuantity)
            return false;

        // PopFrontOrder increments NumDispatched before FulfillOrder is called.
        var absoluteIndex = authorization.Start + order.NumDispatched - 1;
        if (absoluteIndex < 0 || absoluteIndex >= authorization.Total)
            return false;
        var post = 0;
        var upperBound = 0;
        for (; post < 4; post++)
        {
            upperBound += authorization.PostTargets[post];
            if (absoluteIndex < upperBound)
                break;
        }
        if (post >= 4)
            return false;
        // Frontier crates are delivered to the remote receiving terminal,
        // not to ordinary sector trade posts. The source hub supplies the icon.
        var destination = authorization.SourceStation;
        if (!authorization.Frontier && !TryGetQuotaTradeDestination(post, out destination))
            return false;

        // Keep the same crate visuals, station labels and destination icon as
        // Frontier's ordinary trade crates, not a generic steel crate.
        if (!TryComp<TradeCrateComponent>(crate, out var trade) ||
            !TryComp<TradeCrateDestinationComponent>(destination, out var destComp))
            return false;
        trade.DestinationStation = destination;
        // The separate quota settlement is the ONLY payout for these crates.
        // They must never generate additional market cash when sold or stolen.
        trade.ValueAtDestination = 0;
        trade.ValueElsewhere = 0;
        _appearance.SetData(crate, TradeCrateVisuals.DestinationIcon, destComp.DestinationProto.Id);
        _label.Label(crate, authorization.Frontier
            ? $"Далекий рубіж / {(char)('A' + post)}"
            : MetaData(destination).EntityName);

        var tag = EnsureComp<OrganizationQuotaCrateComponent>(crate);
        tag.Organization = authorization.Organization;
        tag.Code = authorization.Code;
        tag.Reference = $"CRATE-{Guid.NewGuid():N}";
        tag.Post = post;
        tag.DestinationStation = authorization.Frontier ? EntityUid.Invalid : destination;
        _metaSystem.SetEntityName(crate, authorization.Frontier
            ? $"Далекий рубіж: {(char)('A' + post)} — {authorization.Code}"
            : $"Квота {authorization.Code} | Пост {(char)('A' + post)} | {MetaData(destination).EntityName}");
        _adminLogger.Add(Content.Shared.Database.LogType.Action, Content.Shared.Database.LogImpact.Low,
            $"Quota crate dispatched: {tag.Reference} code={tag.Code} post={(char)('A' + post)} destination={destination}");
        return true;
    }
}
