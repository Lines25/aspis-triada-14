using System.Linq;
using Content.Server._Aspis.Organizations;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Cargo;
using Content.Server._NF.Trade;
using Content.Shared._NF.Trade;

namespace Content.Server.Cargo.Systems;

/// <summary>
/// Authenticated Cargo Requests -> physical quota crates -> real A-D trade stations.
/// No client-controlled reason text can assign a quota manifest. The authorized
/// order object is registered directly by the server, and each crate gets a fresh ID.
/// </summary>
public sealed partial class CargoSystem
{
    private sealed class QuotaOrderAuthorization(Guid organization, string code, int total, int start, int[] postTargets)
    {
        public readonly Guid Organization = organization;
        public readonly string Code = code;
        public readonly int Total = total;
        public readonly int Start = start;
        public readonly int[] PostTargets = postTargets;
    }

    private const string QuotaTradeCratePrototype = "CrateTradeSecureNormal";
    private readonly Dictionary<CargoOrderData, QuotaOrderAuthorization> _quotaOrders = new();

    private bool IsQuotaTradeMall(EntityUid? grid) =>
        grid is { Valid: true } id &&
        MetaData(id).EntityName.Contains("Trade Mall", StringComparison.OrdinalIgnoreCase);

    private void ResetQuotaDeliveryRound() => _quotaOrders.Clear();

    /// <summary>
    /// A-D are the four first active trade destinations, sorted by station name.
    /// They are real trade stations already used by Frontier cargo, not virtual counters.
    /// A quota batch cannot be issued unless all four destinations exist.
    /// </summary>
    public bool TryGetQuotaTradeDestination(int post, out EntityUid destination)
    {
        destination = EntityUid.Invalid;
        if (post is < 0 or > 3)
            return false;
        var active = _destinations
            .Where(uid => TryComp<TradeCrateDestinationComponent>(uid, out _))
            .OrderBy(uid => MetaData(uid).EntityName, StringComparer.Ordinal)
            .ToArray();
        if (active.Length < 4)
            return false;
        destination = active[post];
        return true;
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

    private void StampQuotaCrate(CargoOrderData order, EntityUid crate)
    {
        if (!_quotaOrders.TryGetValue(order, out var authorization) ||
            order.NumDispatched is < 1 or > 40)
            return;

        // PopFrontOrder increments NumDispatched before FulfillOrder is called.
        var absoluteIndex = authorization.Start + order.NumDispatched - 1;
        if (absoluteIndex < 0 || absoluteIndex >= authorization.Total)
            return;
        var post = 0;
        var upperBound = 0;
        for (; post < 4; post++)
        {
            upperBound += authorization.PostTargets[post];
            if (absoluteIndex < upperBound)
                break;
        }
        if (post >= 4)
            return;
        if (!TryGetQuotaTradeDestination(post, out var destination))
            return;

        // Keep the same crate visuals, station labels and destination icon as
        // Frontier's ordinary trade crates, not a generic steel crate.
        if (!TryComp<TradeCrateComponent>(crate, out var trade) ||
            !TryComp<TradeCrateDestinationComponent>(destination, out var destComp))
            return;
        trade.DestinationStation = destination;
        // The separate quota settlement is the ONLY payout for these crates.
        // They must never generate additional market cash when sold or stolen.
        trade.ValueAtDestination = 0;
        trade.ValueElsewhere = 0;
        _appearance.SetData(crate, TradeCrateVisuals.DestinationIcon, destComp.DestinationProto.Id);
        _label.Label(crate, MetaData(destination).EntityName);

        var tag = EnsureComp<OrganizationQuotaCrateComponent>(crate);
        tag.Organization = authorization.Organization;
        tag.Code = authorization.Code;
        tag.Reference = $"CRATE-{Guid.NewGuid():N}";
        tag.Post = post;
        tag.DestinationStation = destination;
        _metaSystem.SetEntityName(crate,
            $"Квота {authorization.Code} | Пост {(char)('A' + post)} | {MetaData(destination).EntityName}");
        _adminLogger.Add(Content.Shared.Database.LogType.Action, Content.Shared.Database.LogImpact.Low,
            $"Quota crate dispatched: {tag.Reference} code={tag.Code} post={(char)('A' + post)} destination={destination}");
    }
}
