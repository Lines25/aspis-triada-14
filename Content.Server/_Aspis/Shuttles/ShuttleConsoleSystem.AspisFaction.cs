using System;
using System.Collections.Generic;
using Content.Server._Aspis.Organizations;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Ghost;
using Content.Shared.Shuttles.Components;
using Robust.Server.Player;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server.Shuttles.Systems;

/// <summary>
/// Extra server-side authorization for Aspis private expedition routes.
/// Ordinary shuttle destinations and rescue/administrative FTL are unchanged.
/// </summary>
public sealed partial class ShuttleConsoleSystem
{
    // Shuttle FTL itself refuses departure near powered consoles (~512 m).
    // The authorized staging corridor therefore extends beyond that exclusion.
    private const float FactionExpeditionTradeMallLaunchRadius = 1200f;
    [Dependency] private ItemSlotsSystem _aspisFactionDiskSlots = default!;
    [Dependency] private IGameTiming _aspisFactionTiming = default!;
    [Dependency] private IPlayerManager _aspisFactionPlayers = default!;

    private bool CanUseAspisFactionFTL(EntityUid shuttle, EntityUid console,
        MapId destinationMap, out string reason)
    {
        reason = string.Empty;
        var destination = _mapSystem.GetMapOrInvalid(destinationMap);
        var source = Transform(shuttle);
        if (TryComp<AspisFactionExpeditionDestinationComponent>(destination, out var route))
        {
            if (route.RouteClosed ||
                _aspisFactionTiming.CurTime < route.LaunchReadyAt)
            {
                reason = "Експедиційні координати ще готуються або маршрут уже закрито.";
                return false;
            }
            if (route.AssignedShuttle is { } assigned && assigned != shuttle)
            {
                reason = "Експедиційний маршрут уже закріплено за іншим шатлом.";
                return false;
            }
            if (!_aspisFactionDiskSlots.TryGetSlot(console,
                    SharedShuttleConsoleComponent.DiskSlotName, out var slot) ||
                slot.Item is not { } disk ||
                !TryComp<AspisFactionCoordinatesDiskComponent>(disk, out var stamp) ||
                stamp.Organization != route.Organization || stamp.Assignment != route.Assignment)
            {
                reason = "Вставте виданий фракції диск координат у FTL-консоль.";
                return false;
            }
            if (!EntityManager.System<OrganizationSystem>()
                    .HasAuthorizedFactionTraveler(shuttle, route.Organization))
            {
                reason = "У шатлі повинен бути учасник фракції, яка отримала координати.";
                return false;
            }
            // First leg must actually depart the Trade Mall vicinity. A disk
            // carried during a battle elsewhere is not an escape destination.
            if (route.AssignedShuttle == null && !IsNearTradeMall(shuttle, source))
            {
                reason = "Перший виліт за фракційними координатами доступний тільки біля Trade Mall.";
                return false;
            }
        }
        // The initial survey is one round-trip. Before allowing return from
        // its map, ensure the ship is not abandoning online players on the site.
        if (source.MapUid is { } from &&
            TryComp<AspisFactionExpeditionDestinationComponent>(from, out var origin) &&
            destination != from && origin.AssignedShuttle == shuttle)
        {
            var docked = new HashSet<EntityUid>();
            _shuttle.GetAllDockedShuttlesIgnoringFTLLock(shuttle, docked);
            foreach (var session in _aspisFactionPlayers.Sessions)
            {
                if (session.AttachedEntity is not { Valid: true } person ||
                    Deleted(person) || HasComp<GhostComponent>(person))
                    continue;
                var personXform = Transform(person);
                if (personXform.MapUid == from && personXform.GridUid != shuttle &&
                    (personXform.GridUid is not { } aboard || !docked.Contains(aboard)))
                {
                    reason = "Не можна залишати учасників експедиції поза шатлом.";
                    return false;
                }
            }
        }
        return true;
    }

    // Called only after all normal shuttle flight checks succeeded, immediately
    // before beginning FTL. Prevents two ships simultaneously claiming one disk.
    private void ReserveAspisFactionRoute(EntityUid shuttle, MapId targetMap)
    {
        var map = _mapSystem.GetMapOrInvalid(targetMap);
        if (TryComp<AspisFactionExpeditionDestinationComponent>(map, out var route))
            route.AssignedShuttle ??= shuttle;
    }

    private bool IsNearTradeMall(EntityUid shuttle, TransformComponent shuttleXform)
    {
        var query = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var grid, out _, out var xform))
        {
            if (xform.MapUid != shuttleXform.MapUid ||
                !MetaData(grid).EntityName.Contains("Trade Mall", StringComparison.OrdinalIgnoreCase))
                continue;
            if ((xform.MapPosition.Position - shuttleXform.MapPosition.Position).LengthSquared() <=
                FactionExpeditionTradeMallLaunchRadius * FactionExpeditionTradeMallLaunchRadius)
                return true;
        }
        return false;
    }
}
