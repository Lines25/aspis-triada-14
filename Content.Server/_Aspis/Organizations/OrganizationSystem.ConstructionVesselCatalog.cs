using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Shared._NF.Shipyard;
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Uses the live civilian Shipyard catalogue, not handmade surrogate ships.
/// The original saved YAML includes its genuine plating, engine rooms, doors,
/// equipment, and docking ports. The job is a repair of THAT exact ship.
/// </summary>
public sealed partial class OrganizationSystem
{
    private readonly List<string> _recentConstructionVesselModels = new();

    private static bool FitsConstructionGrade(VesselPrototype proto, int slot)
    {
        if (!proto.Purchasable || proto.Abstract ||
            proto.Group != ShipyardConsoleUiKey.Shipyard ||
            !string.IsNullOrWhiteSpace(proto.Access) ||
            !proto.ShuttlePath.ToString().Contains("/Shuttles/", StringComparison.OrdinalIgnoreCase))
            return false;
        // Each successive tier uses genuinely larger civilian catalogue ships.
        return slot switch
        {
            0 => (proto.Category is VesselSize.Small or VesselSize.Medium) && proto.Price >= 18000,
            1 => proto.Category == VesselSize.Medium && proto.Price >= 35000,
            2 => (proto.Category is VesselSize.Medium or VesselSize.Large) && proto.Price >= 65000,
            _ => proto.Category == VesselSize.Large && proto.Price >= 100000
        };
    }

    private bool TryLoadConstructionShipyardVessel(MapId mapId, EntityUid platform, int slot,
        Guid assignment, out EntityUid hull,
        out Entity<DockingComponent> northDock, out Entity<DockingComponent> shipDock,
        out string vesselName, out string shortName, out string failure)
    {
        hull = EntityUid.Invalid;
        northDock = default;
        shipDock = default;
        vesselName = string.Empty;
        shortName = string.Empty;
        failure = "Цивільний Shipyard не має придатних для ремонту шатлів зі справним стикуванням.";
        if (!TryComp<MapGridComponent>(platform, out var platformGrid))
            return false;

        var stationPorts = _constructionDocking.GetDocks(platform)
            .Where(d => !d.Comp.Docked && Transform(d.Owner).Anchored &&
                Transform(d.Owner).LocalPosition.Y > 4.75f &&
                (d.Comp.DockType & DockType.Airlock) != DockType.None)
            .OrderBy(d => MathF.Abs(Transform(d.Owner).LocalPosition.X - 1.5f))
            .ToArray();
        if (stationPorts.Length == 0)
        {
            failure = "На верфі зайняті всі північні стикувальні порти.";
            return false;
        }

        var candidates = _prototypes.EnumeratePrototypes<VesselPrototype>()
            .Where(p => FitsConstructionGrade(p, slot)).ToArray();
        if (candidates.Length == 0)
        {
            failure = "Цивільний Shipyard не завантажив моделі кораблів цього рівня.";
            return false;
        }
        var random = new Random(assignment.GetHashCode());
        var selected = candidates.OrderBy(p => _recentConstructionVesselModels.Contains(p.ID) ? 1 : 0)
            .ThenBy(_ => random.Next()).Take(32).ToArray();

        foreach (var vessel in selected)
        {
            EntityUid? candidateUid = null;
            var accepted = false;
            try
            {
                if (!_organizationGridLoader.TryLoadGrid(mapId, vessel.ShuttlePath,
                        out var loaded) || loaded is null)
                    continue;
                candidateUid = loaded.Value.Owner;
                if (!TryComp<MapGridComponent>(candidateUid.Value, out var grid) ||
                    grid.LocalAABB.Width < 12f || grid.LocalAABB.Height < 12f ||
                    grid.LocalAABB.Width > 145f || grid.LocalAABB.Height > 145f ||
                    _organizationMapSystem.GetFilledTileCount((candidateUid.Value, grid)) < 125)
                    continue;

                // In Shipyard, most ships have multiple doors at different
                // angles. For each one, solve the rigid alignment exactly,
                // rejecting any orientation that overlaps the yard platform.
                foreach (var postDock in stationPorts)
                foreach (var vesselDock in _constructionDocking.GetDocks(candidateUid.Value)
                             .Where(d => !d.Comp.Docked && Transform(d.Owner).Anchored &&
                                 (d.Comp.DockType & DockType.Airlock) != DockType.None))
                {
                    var postAngle = _organizationOutpostTransforms.GetWorldRotation(postDock.Owner);
                    var vesselAngle = Transform(vesselDock.Owner).LocalRotation;
                    var rotation = postAngle + Angle.FromDegrees(180) - vesselAngle;
                    var stationDoorPosition = _organizationOutpostTransforms.GetWorldPosition(postDock.Owner);
                    var outside = postAngle.RotateVec(new Vector2(0f, -1f));
                    var desiredShipDoorPosition = stationDoorPosition + outside;
                    var shipPosition = desiredShipDoorPosition -
                        rotation.RotateVec(Transform(vesselDock.Owner).LocalPosition);

                    var shipBounds = GetRotatedConstructionShipBounds(grid.LocalAABB, shipPosition, rotation);
                    var postBounds = GetRotatedConstructionShipBounds(platformGrid.LocalAABB,
                        Transform(platform).WorldPosition,
                        _organizationOutpostTransforms.GetWorldRotation(platform));
                    if (ConstructionBoundsOverlap(shipBounds, postBounds, 0.1f))
                        continue;

                    // Do not place this ship over another vessel, such as a
                    // completed hull awaiting a safe, empty-crew departure.
                    var nearby = new List<Entity<MapGridComponent>>();
                    _organizationMapSystem.FindGridsIntersecting(mapId,
                        new Box2(shipBounds.BottomLeft, shipBounds.TopRight), ref nearby, false, false);
                    if (nearby.Any(g => g.Owner != platform && g.Owner != candidateUid.Value &&
                        !TerminatingOrDeleted(g.Owner)))
                        continue;

                    _organizationOutpostTransforms.SetWorldRotation(candidateUid.Value, rotation);
                    _organizationOutpostTransforms.SetWorldPosition(candidateUid.Value, shipPosition);
                    if (Transform(candidateUid.Value).MapID != mapId)
                        continue;

                    northDock = postDock;
                    shipDock = vesselDock;
                    hull = candidateUid.Value;
                    vesselName = vessel.Name;
                    shortName = vessel.ID.Length > 18 ? vessel.ID[..18] : vessel.ID;
                    _recentConstructionVesselModels.Add(vessel.ID);
                    if (_recentConstructionVesselModels.Count > 12)
                        _recentConstructionVesselModels.RemoveAt(0);
                    accepted = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.GetSawmill("organizations.construction-yards")
                    .Warning($"Skipped Shipyard vessel {vessel.ID}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!accepted && candidateUid is { } uid && !TerminatingOrDeleted(uid))
                    QueueDel(uid);
            }
        }
        return false;
    }

    private static Box2 GetRotatedConstructionShipBounds(Box2 local, Vector2 position, Angle angle)
    {
        var corners = new[]
        {
            new Vector2(local.BottomLeft.X, local.BottomLeft.Y), new Vector2(local.BottomLeft.X, local.TopRight.Y),
            new Vector2(local.TopRight.X, local.BottomLeft.Y), new Vector2(local.TopRight.X, local.TopRight.Y)
        };
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var corner in corners)
        {
            var point = position + angle.RotateVec(corner);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return new Box2(min, max);
    }

    private static bool ConstructionBoundsOverlap(Box2 a, Box2 b, float gap) =>
        a.BottomLeft.X < b.TopRight.X - gap && a.TopRight.X > b.BottomLeft.X + gap &&
        a.BottomLeft.Y < b.TopRight.Y - gap && a.TopRight.Y > b.BottomLeft.Y + gap;

    private bool TryDamageConstructionShipyardVessel(EntityUid ship, EntityUid dock,
        int slot, Guid assignment, out string failure)
    {
        failure = string.Empty;
        if (!TryComp<MapGridComponent>(ship, out var grid) ||
            TerminatingOrDeleted(dock) || Transform(dock).GridUid != ship)
        {
            failure = "Відсутній грід корабля або його стикувальний порт.";
            return false;
        }
        var tiles = new Dictionary<Vector2i, Tile>();
        foreach (var tile in _organizationMapSystem.GetAllTiles(ship, grid))
        {
            if (!tile.Tile.IsEmpty)
                tiles[tile.GridIndices] = tile.Tile;
        }
        if (tiles.Count < 125)
        {
            failure = "Контрактний шатл занадто малий для будівельних робіт.";
            return false;
        }

        var random = new Random(assignment.GetHashCode() ^ 0x514C1);
        var portPosition = Transform(dock).LocalPosition;
        var portTile = new Vector2i((int)MathF.Floor(portPosition.X),
            (int)MathF.Floor(portPosition.Y));
        // Some authentic Shipyard hulls place their docking airlock on the
        // edge of a tile-less apron. Start topology validation from the
        // closest actual plating tile without changing the original port.
        if (!tiles.ContainsKey(portTile))
            portTile = tiles.Keys.OrderBy(p =>
                Vector2.DistanceSquared(new Vector2(p.X + 0.5f, p.Y + 0.5f), portPosition))
                .First();
        var protectedTiles = new HashSet<Vector2i>(tiles.Keys.Where(p =>
            Vector2.DistanceSquared(new Vector2(p.X + 0.5f, p.Y + 0.5f), portPosition) <= 9f * 9f));
        protectedTiles.Add(portTile);
        // Work on the connected plating island containing the docking hatch.
        var reachable = ConstructionConnectedTiles(tiles.Keys.ToHashSet(), portTile);
        if (reachable.Count < 100)
        {
            failure = "Шатл не має суцільного корпусу біля стикувального шлюзу.";
            return false;
        }
        var boundary = reachable.Where(p => !protectedTiles.Contains(p) &&
            ConstructionDirections.Any(d => !reachable.Contains(p + d))).ToArray();
        if (boundary.Length < 10)
            boundary = reachable.Where(p => !protectedTiles.Contains(p)).ToArray();
        if (boundary.Length == 0)
        {
            failure = "Немає безпечних зон для пошкодження обшивки.";
            return false;
        }

        var removed = new HashSet<Vector2i>();
        var blastCenters = new List<Vector2i>();
        var desiredBlasts = 4 + slot * 4;
        var targetFraction = 0.08f + slot * 0.055f;
        var maxRemoved = Math.Max(14, (int)(reachable.Count * targetFraction));
        var intact = new HashSet<Vector2i>(reachable);
        for (var attempt = 0; attempt < desiredBlasts * 16 &&
             blastCenters.Count < desiredBlasts && removed.Count < maxRemoved; attempt++)
        {
            var center = boundary[random.Next(boundary.Length)];
            if (!intact.Contains(center))
                continue;
            var radius = 2.5f + slot * 0.48f + (float)random.NextDouble() * 1.4f;
            var candidate = new HashSet<Vector2i>();
            foreach (var tile in intact)
            {
                if (protectedTiles.Contains(tile) ||
                    Vector2.DistanceSquared(new Vector2(tile.X, tile.Y),
                        new Vector2(center.X, center.Y)) > radius * radius ||
                    random.NextDouble() < 0.13)
                    continue;
                candidate.Add(tile);
            }
            if (candidate.Count < 2 || candidate.Count > maxRemoved - removed.Count)
                continue;
            var remaining = new HashSet<Vector2i>(intact);
            remaining.ExceptWith(candidate);
            if (!remaining.Contains(portTile) ||
                ConstructionConnectedTiles(remaining, portTile).Count != remaining.Count)
                continue; // never sever the ship's only connected plating island
            intact = remaining;
            removed.UnionWith(candidate);
            blastCenters.Add(center);
        }
        if (removed.Count < Math.Max(10, reachable.Count / 80))
        {
            failure = "Не вдалося зробити достатньо пошкоджень без розриву конструкції шатла.";
            return false;
        }

        // Make damaged fixtures reconstructible with the actual ShipRepairTool.
        // Without this, some original shipyard walls do not have ShipRepairable
        // and would be permanently missing from the contract's snapshot.
        var removableEntities = new List<EntityUid>();
        var children = Transform(ship).ChildEnumerator;
        try
        {
            while (children.MoveNext(out var entity))
            {
                if (TerminatingOrDeleted(entity) || entity == dock ||
                    HasComp<DockingComponent>(entity) || HasComp<MobStateComponent>(entity))
                    continue;
                var xform = Transform(entity);
                if (!xform.Anchored || xform.ParentUid != ship)
                    continue;
                var index = new Vector2i((int)MathF.Floor(xform.LocalPosition.X),
                    (int)MathF.Floor(xform.LocalPosition.Y));
                if (!removed.Contains(index) ||
                    MetaData(entity).EntityPrototype is not { } sourceProto)
                    continue;

                // Mono's SRD checks the TARGET PROTOTYPE for ShipRepairable,
                // not just the live, snapshotted entity. Adding the component
                // dynamically alone produces ghosts that cannot be rebuilt.
                // Existing ShipRepairable prototypes work unchanged. For a few
                // standard ship structural types, replace the snapshot target
                // with an _Aspis repair-capable subclass of the same original.
                // All other entities stay intact; never create an impossible
                // contract by deleting an object that SRD cannot reconstruct.
                var nativeRepairable = sourceProto.TryComp<ShipRepairableComponent>(out _, Factory);
                var repairTo = nativeRepairable ? null : ConstructionRepairTarget(sourceProto.ID);
                if (!nativeRepairable &&
                    (repairTo == null || !_prototypes.HasIndex<EntityPrototype>(repairTo)))
                    continue;

                var repairable = EnsureComp<ShipRepairableComponent>(entity);
                if (repairTo != null)
                    repairable.RepairTo = new EntProtoId(repairTo);
                repairable.RepairTime = Math.Max(2f, 2f + slot);
                repairable.RepairCost = 2 + slot * 2;
                removableEntities.Add(entity);
            }
        }
        finally
        {
            children.Dispose();
        }

        // Full original ship snapshot, before any tile/entity deletions.
        _constructionShipRepair.GenerateRepairData(ship);
        if (!TryComp<ShipRepairDataComponent>(ship, out var snapshot) ||
            snapshot.Chunks.Count == 0)
        {
            failure = "Не вдалося створити еталон реального корабля.";
            return false;
        }
        foreach (var entity in removableEntities)
        {
            if (!TerminatingOrDeleted(entity))
                QueueDel(entity);
        }
        _organizationMapSystem.SetTiles(ship, grid,
            removed.Select(index => (index, Tile.Empty)).ToList());

        // Scatter actual physical debris in and around the ruptured sections.
        // This is a controlled simulation of impacts, NOT an uncontrolled
        // ExplosionSystem call that could destroy the berth or split the grid.
        string[] debris = ["ScrapSteel", "ScrapGlass", "ScrapAirlock1", "ScrapTube"];
        foreach (var center in blastCenters)
        {
            for (var n = 0; n < 2 + slot / 2; n++)
            {
                var type = debris[random.Next(debris.Length)];
                if (!_prototypes.HasIndex<EntityPrototype>(type))
                    continue;
                var local = new Vector2(center.X + 0.5f + (float)(random.NextDouble() - 0.5) * 5,
                    center.Y + 0.5f + (float)(random.NextDouble() - 0.5) * 5);
                Spawn(type, new EntityCoordinates(ship, local));
            }
        }
        return true;
    }

    // A small conservative compatibility palette. The subtype has the same
    // original physics/appearance and adds a valid ShipRepairable prototype.
    // Do not map arbitrary machines: stateful devices must not be replaced by
    // visually similar but unconfigured copies.
    private static string? ConstructionRepairTarget(string prototypeId) => prototypeId switch
    {
        "WallShuttle" => "AspisYardShuttleWall",
        "WallShuttleInterior" => "AspisYardInteriorWall",
        "Window" => "AspisYardHullWindow",
        "WallSolid" => "AspisContractSolidWall",
        "WallReinforced" => "AspisContractReinforcedWall",
        "ReinforcedWindow" => "AspisContractReinforcedWindow",
        "ShuttleWindow" => "AspisContractShuttleWindow",
        "WallRiveted" => "AspisContractRivetedWall",
        _ => null
    };

    private static readonly Vector2i[] ConstructionDirections =
    [
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    ];

    private static HashSet<Vector2i> ConstructionConnectedTiles(HashSet<Vector2i> tiles, Vector2i start)
    {
        var result = new HashSet<Vector2i>();
        if (!tiles.Contains(start))
            return result;
        var queue = new Queue<Vector2i>();
        queue.Enqueue(start);
        result.Add(start);
        while (queue.TryDequeue(out var index))
        {
            foreach (var direction in ConstructionDirections)
            {
                var next = index + direction;
                if (tiles.Contains(next) && result.Add(next))
                    queue.Enqueue(next);
            }
        }
        return result;
    }
}
