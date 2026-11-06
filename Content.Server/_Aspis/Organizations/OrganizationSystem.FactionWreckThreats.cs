using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Database;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics.Components;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// v47: One-time, bounded enemies for author-curated expedition grids. Site
/// creation plans the layout but does NOT start any hostile AI until the first
/// authorized shuttle leaves for it. No respawns, no rewards for kills.
/// </summary>
public sealed partial class OrganizationSystem
{
    private static readonly (int X, int Y)[] WreckNeighborSteps =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1)
    ];

    private static Vector2 WreckTileCenter((int X, int Y) tile) =>
        new(tile.X + 0.5f, tile.Y + 0.5f);

    private static int WreckOpenNeighborCount(
        HashSet<(int X, int Y)> floor, (int X, int Y) tile)
    {
        var count = 0;
        foreach (var step in WreckNeighborSteps)
        {
            if (floor.Contains((tile.X + step.X, tile.Y + step.Y)))
                count++;
        }
        return count;
    }

    private static Dictionary<(int X, int Y), int> WreckFloorDistances(
        HashSet<(int X, int Y)> floor, (int X, int Y) origin)
    {
        var distance = new Dictionary<(int X, int Y), int> { [origin] = 0 };
        var pending = new Queue<(int X, int Y)>();
        pending.Enqueue(origin);
        while (pending.TryDequeue(out var current))
        {
            var nextDepth = distance[current] + 1;
            foreach (var step in WreckNeighborSteps)
            {
                var next = (current.X + step.X, current.Y + step.Y);
                if (!floor.Contains(next) || !distance.TryAdd(next, nextDepth))
                    continue;
                pending.Enqueue(next);
            }
        }
        return distance;
    }


    // A floor tile alone is not a usable position: old placement accepted
    // coordinates inside walls, airlocks, tables and machinery.
    private bool WreckTileClear(EntityUid gridUid, MapGridComponent grid, (int X, int Y) tile)
    {
        var lookup = EntityManager.System<EntityLookupSystem>();
        var tileRef = _organizationMapSystem.GetTileRef(gridUid, grid,
            new Vector2i(tile.X, tile.Y));
        foreach (var entity in lookup.GetLocalEntitiesIntersecting(tileRef,
                     flags: LookupFlags.Dynamic | LookupFlags.Static))
        {
            if (entity == gridUid)
                continue;
            // Lattice is a walkable entity across open space, not an interior
            // floor suitable for a recorder, console, or search beacon.
            if (MetaData(entity).EntityPrototype?.ID.Contains("Lattice", StringComparison.OrdinalIgnoreCase) == true)
                return false;
            if (!TryComp<PhysicsComponent>(entity, out var physics) ||
                !physics.Hard || !physics.CanCollide)
                continue;
            if ((physics.CollisionLayer & (int)CollisionGroup.MobMask) != 0)
                return false;
        }
        return true;
    }

    private static int WreckInteriorScore(HashSet<(int X, int Y)> floor,
        (int X, int Y) tile)
    {
        var count = 0;
        foreach (var step in WreckNeighborSteps)
        {
            if (floor.Contains((tile.X + step.X * 2, tile.Y + step.Y * 2)))
                count++;
        }
        return count;
    }

    private bool TryPlanFactionWreckLayout(EntityUid gridUid, MapGridComponent grid,
        int minimumConnectedFloor, int minimumTraversalTiles, int hostileCount,
        out Vector2 recorderPos, out Vector2 inspectionPos,
        out List<Vector2> enemyPositions, out string failure)
    {
        failure = string.Empty;
        recorderPos = default;
        inspectionPos = default;
        enemyPositions = new List<Vector2>();

        // MapGrid floor continuity is a coarse reachability filter. Wall and
        // door access remain features of the authored map; this is not a full
        // navigation-path guarantee through the entire station.
        var allFloor = new HashSet<(int X, int Y)>();
        var tiles = _organizationMapSystem.GetAllTiles(gridUid, grid);
        while (tiles.MoveNext(out var tile))
        {
            if (tile.Value.Tile.IsEmpty)
                continue;
            var index = tile.Value.GridIndices;
            allFloor.Add((index.X, index.Y));
        }
        if (allFloor.Count < minimumConnectedFloor)
        {
            failure = $"недостатня площа: {allFloor.Count} < {minimumConnectedFloor}";
            return false;
        }

        var remaining = new HashSet<(int X, int Y)>(allFloor);
        var largest = new HashSet<(int X, int Y)>();
        while (remaining.Count > 0)
        {
            var origin = remaining.First();
            var island = new HashSet<(int X, int Y)> { origin };
            var pending = new Queue<(int X, int Y)>();
            pending.Enqueue(origin);
            remaining.Remove(origin);
            while (pending.TryDequeue(out var current))
            {
                foreach (var step in WreckNeighborSteps)
                {
                    var next = (current.X + step.X, current.Y + step.Y);
                    if (!remaining.Remove(next))
                        continue;
                    island.Add(next);
                    pending.Enqueue(next);
                }
            }
            if (island.Count > largest.Count)
                largest = island;
        }
        if (largest.Count < minimumConnectedFloor)
        {
            failure = $"замала зв'язна область: {largest.Count} < {minimumConnectedFloor}";
            return false;
        }

        // Avoid mission devices and NPCs on exposed single-tile ledges.
        var open = largest.Where(tile => WreckOpenNeighborCount(largest, tile) >= 3).ToArray();
        // Narrow but connected ship corridors often have only two neighbors.
        // Rejecting all of them prevented otherwise valid authored wrecks.
        if (open.Length < 24)
            open = largest.Where(tile => WreckOpenNeighborCount(largest, tile) >= 2).ToArray();
        // Filter by PHYSICS, not merely presence of floor. Include static
        // obstacles such as walls, consoles, beds and tables in the test.
        open = open.Where(tile => WreckTileClear(gridUid, grid, tile)).ToArray();
        if (open.Length < 24)
        {
            failure = $"немає 24 вільних від перешкод клітин: {open.Length}";
            return false;
        }
        // Prefer interior compartments while preserving distant traversal.
        var interior = open.Where(tile => WreckInteriorScore(largest, tile) >= 3).ToArray();
        if (interior.Length >= 24)
            open = interior;

        // Two BFS sweeps yield distant endpoints in the SAME connected island.
        // It replaces the prior ~6m straight-line objective placement.
        var firstDistances = WreckFloorDistances(largest, open[0]);
        var farA = open.OrderByDescending(t => firstDistances[t]).First();
        var fromA = WreckFloorDistances(largest, farA);
        var farB = open.OrderByDescending(t => fromA[t]).First();
        var pathLength = fromA[farB];
        if (pathLength < minimumTraversalTiles)
        {
            failure = $"короткий маршрут: {pathLength} < {minimumTraversalTiles}";
            return false;
        }
        inspectionPos = WreckTileCenter(farA);
        recorderPos = WreckTileCenter(farB);

        // Lambdas cannot capture out parameters: use ordinary local aliases.
        var inspectionPoint = inspectionPos;
        var recorderPoint = recorderPos;
        var placedEnemies = enemyPositions;

        // Spread encounters across the traversable island, rather than
        // stacking enemies on the recorder or at the map origin/FTL arrival.
        var viableEnemies = open.Where(tile =>
            fromA[tile] >= 3 && fromA[tile] <= pathLength - 3 &&
            Vector2.DistanceSquared(WreckTileCenter(tile), inspectionPoint) >= 16f &&
            Vector2.DistanceSquared(WreckTileCenter(tile), recorderPoint) >= 16f).ToArray();

        for (var i = 0; i < hostileCount; i++)
        {
            var desiredDepth = pathLength * (i + 1) / (hostileCount + 1);
            // Randomize among good locations, never all in a single room.
            var eligible = viableEnemies
                .Where(tile => placedEnemies.All(existing =>
                    Vector2.DistanceSquared(existing, WreckTileCenter(tile)) >= 9f))
                .OrderBy(tile => Math.Abs(fromA[tile] - desiredDepth))
                .Take(16).ToArray();
            if (eligible.Length == 0)
                break;
            placedEnemies.Add(WreckTileCenter(eligible[_random.Next(eligible.Length)]));
        }

        // A template with no meaningful distribution does not qualify as a
        // dangerous expedition. The loader will attempt the next template.
        if (placedEnemies.Count < Math.Max(2, hostileCount / 3))
        {
            failure = $"не вдалося розставити ворогів: {placedEnemies.Count}/{hostileCount}, " +
                $"ділянок={viableEnemies.Length}, маршрут={pathLength}";
            return false;
        }
        return true;
    }

    private void DeployFactionWreckHostiles(SectorQuotaClaim claim)
    {
        if (claim.FactionWreckHostilesSpawned || claim.FactionWreckReturned ||
            claim.FactionWreckGrid is not { } grid || Deleted(grid) ||
            claim.FactionWreckMap is not { } map || Deleted(map))
            return;
        claim.FactionWreckHostilesSpawned = true;
        foreach (var (prototype, position) in claim.FactionWreckHostilePlan)
        {
            if (!_prototypes.HasIndex<EntityPrototype>(prototype))
                continue;
            try
            {
                var hostile = Spawn(prototype, new EntityCoordinates(grid, position));
                if (!hostile.Valid || Deleted(hostile) ||
                    Transform(hostile).GridUid != grid)
                {
                    if (hostile.Valid && !Deleted(hostile))
                        QueueDel(hostile);
                    continue;
                }
                claim.FactionWreckHostilesDeployed++;
            }
            catch (Exception ex)
            {
                _log.GetSawmill("organizations.faction-sites").Warning(
                    $"Unable to place hostile {prototype} at {claim.Code}: {ex.Message}");
            }
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Faction expedition enemies deployed: {claim.Code}, " +
            $"grid={grid}, count={claim.FactionWreckHostilesDeployed}");
    }
}
