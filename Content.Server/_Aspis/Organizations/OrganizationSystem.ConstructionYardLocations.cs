using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Station.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Q2.2A.1: all construction yards are on the ORDINARY shared sector space map.
/// This planner does not create an FTL destination, a new map or a free shuttle.
/// It is only called by the server, never with a client-provided map/position.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const float ConstructionYardFirstRing = 900f;
    private const float ConstructionYardRingStep = 350f;
    private const int ConstructionYardRingCount = 9;
    private const int ConstructionYardSpokesPerRing = 16;
    private const float ConstructionYardSafetyBuffer = 175f;

    private readonly List<ConstructionYardReservation> _constructionYardReservations = new();

    private readonly record struct ConstructionYardReservation(
        MapId MapId, Vector2 WorldPosition, float ClearanceRadius);

    /// <summary>
    /// Match the already implemented corporate-outpost selection. This is the
    /// common sector map, not a private expedition instance or an FTL map.
    /// </summary>
    private bool TryGetConstructionSector(
        out EntityUid sectorMap, out MapId mapId, out Vector2 sectorOrigin)
    {
        sectorMap = EntityUid.Invalid;
        mapId = default;
        sectorOrigin = default;

        var principal = _organizationStationSystem.GetStations()
            .Where(station => !HasComp<OrganizationOutpostStationComponent>(station))
            .Where(station => TryComp<StationDataComponent>(station, out var data) &&
                              data.Grids.Count > 0)
            .OrderByDescending(station => Name(station).Contains(
                "Venmar Station", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(station =>
                _organizationStationSystem.GetTileCount(Comp<StationDataComponent>(station)))
            .FirstOrDefault();

        if (!principal.Valid)
            return false;

        var anchor = _organizationStationSystem.GetLargestGrid(
            Comp<StationDataComponent>(principal));
        if (anchor is not { } grid || Transform(grid).MapUid is not { } map)
            return false;

        sectorMap = map;
        mapId = Transform(grid).MapID;
        sectorOrigin = Transform(grid).WorldPosition;
        return true;
    }

    /// <summary>
    /// The size must cover the work platform, contract hull AND an approach
    /// corridor for a visiting ordinary shuttle. No FTL access is necessary.
    /// </summary>
    private bool TryFindConstructionYardPosition(
        MapId mapId, Vector2 sectorOrigin, float footprintRadius, out Vector2 position)
    {
        position = default;
        if (!float.IsFinite(footprintRadius) || footprintRadius < 1f ||
            footprintRadius > 800f ||
            !float.IsFinite(sectorOrigin.X) || !float.IsFinite(sectorOrigin.Y))
            return false;

        var clearance = MathF.Max(75f, footprintRadius) + ConstructionYardSafetyBuffer;
        var nearby = new List<Entity<MapGridComponent>>();
        for (var ring = 0; ring < ConstructionYardRingCount; ring++)
        {
            var distance = ConstructionYardFirstRing + ring * ConstructionYardRingStep;
            for (var spoke = 0; spoke < ConstructionYardSpokesPerRing; spoke++)
            {
                var angle = (spoke + (ring % 2 == 0 ? 0f : 0.5f)) *
                            (MathF.PI * 2f / ConstructionYardSpokesPerRing);
                var candidate = sectorOrigin +
                    new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
                if (_constructionYardReservations.Any(reservation =>
                    reservation.MapId == mapId &&
                    Vector2.DistanceSquared(candidate, reservation.WorldPosition) <
                    MathF.Pow(clearance + reservation.ClearanceRadius, 2f)))
                    continue;

                nearby.Clear();
                _organizationMapSystem.FindGridsIntersecting(
                    mapId, Box2.CenteredAround(candidate, new Vector2(clearance * 2f)),
                    ref nearby, false, false);
                if (nearby.Count != 0)
                    continue;

                position = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Only register a successful placement. Failure must not consume a slot.
    /// This cache is round-only and is cleared by OnRoundRestart.
    /// </summary>
    private void ReserveConstructionYardPosition(MapId mapId,
        Vector2 position, float footprintRadius)
    {
        _constructionYardReservations.Add(new ConstructionYardReservation(
            mapId, position, MathF.Max(75f, footprintRadius) + ConstructionYardSafetyBuffer));
    }
}
