using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared._Mono.ShipRepair.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Server-only verification of the actual docked contract hull. Rebuilt
/// machinery may legitimately have a DIFFERENT entity UID from the fixture
/// originally stored in ShipRepairDataComponent: its anchored prototype,
/// position and rotation are authoritative, not a stale NetEntity identity.
/// </summary>
public sealed partial class OrganizationSystem
{
    private bool TryInspectConstructionHull(EntityUid hull,
        out int presentParts, out int requiredParts, out string report)
    {
        presentParts = 0;
        requiredParts = 0;
        report = "Еталон корабля не знайдено.";
        if (TerminatingOrDeleted(hull) ||
            !TryComp<MapGridComponent>(hull, out var grid) ||
            !TryComp<ShipRepairDataComponent>(hull, out var repairData) ||
            !TryComp<OrganizationConstructionYardComponent>(hull, out var yard) ||
            yard.IsWorkPlatform || yard.ContractGrid != hull)
            return false;

        // Do not trust an old source NetEntity: once machinery has been
        // dismantled and actually rebuilt, its EntityUid is legitimately new.
        // Collect only physical, anchored children on this specific grid.
        var candidates = new List<EntityUid>();
        var children = Transform(hull).ChildEnumerator;
        try
        {
            while (children.MoveNext(out var child))
            {
                if (TerminatingOrDeleted(child))
                    continue;
                var xform = Transform(child);
                if (xform.Anchored && xform.ParentUid == hull)
                    candidates.Add(child);
            }
        }
        finally
        {
            children.Dispose();
        }

        var consumed = new HashSet<EntityUid>();
        var matchingTiles = 0;
        var expectedTiles = 0;
        var matchingMachines = 0;
        var expectedMachines = 0;

        foreach (var (chunkPos, chunk) in repairData.Chunks)
        {
            if (chunk.Tiles == null ||
                chunk.Tiles.Length != repairData.ChunkSize * repairData.ChunkSize)
            {
                report = "Пошкоджено серверний кресленик.";
                return false;
            }
            for (var x = 0; x < repairData.ChunkSize; x++)
            for (var y = 0; y < repairData.ChunkSize; y++)
            {
                var expectedTile = chunk.Tiles[x + y * repairData.ChunkSize];
                if (expectedTile == Tile.Empty.TypeId)
                    continue;
                expectedTiles++;
                var tile = chunkPos * repairData.ChunkSize + new Vector2i(x, y);
                if (_organizationMapSystem.GetTileRef(hull, grid, tile).Tile.TypeId == expectedTile)
                    matchingTiles++;
            }

            foreach (var spec in chunk.Entities.Values)
            {
                expectedMachines++;
                if (spec.ProtoIndex < 0 || spec.ProtoIndex >= repairData.EntityPalette.Count)
                    continue;

                var requiredProto = repairData.EntityPalette[spec.ProtoIndex].Id;
                bool Qualifies(EntityUid entity)
                {
                    if (consumed.Contains(entity) || TerminatingOrDeleted(entity))
                        return false;
                    var xform = Transform(entity);
                    if (!xform.Anchored || xform.ParentUid != hull ||
                        Vector2.DistanceSquared(xform.LocalPosition, spec.LocalPosition) > 0.01f ||
                        xform.LocalRotation != spec.Rotation)
                        return false;
                    var prototype = MetaData(entity).EntityPrototype;
                    return prototype != null &&
                        string.Equals(prototype.ID, requiredProto, StringComparison.Ordinal);
                }

                // Usually the original still exists. If not, accept an actual
                // reconstructed prototype occupying the correct grid tile.
                EntityUid matched = EntityUid.Invalid;
                if (spec.OriginalEntity is { } original &&
                    GetEntity(original) is { Valid: true } existing &&
                    Qualifies(existing))
                    matched = existing;
                else
                {
                    foreach (var candidate in candidates)
                    {
                        if (!Qualifies(candidate))
                            continue;
                        matched = candidate;
                        break;
                    }
                }

                if (!matched.Valid)
                    continue;
                consumed.Add(matched);
                matchingMachines++;
            }
        }

        presentParts = matchingTiles + matchingMachines;
        requiredParts = expectedTiles + expectedMachines;
        if (requiredParts == 0)
        {
            report = "Порожній еталон корабля.";
            return false;
        }
        report = $"Обшивка {matchingTiles}/{expectedTiles}; суднове обладнання " +
            $"{matchingMachines}/{expectedMachines}.";
        return matchingTiles == expectedTiles && matchingMachines == expectedMachines;
    }
}
