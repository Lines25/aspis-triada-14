using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Mind.Components;
using Content.Shared.Maps;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using Content.Shared.Mobs.Components;
using Content.Shared.Parallax.Biomes;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Aspis.Organizations;

[RegisterComponent]
public sealed partial class AspisWheatPatchComponent : Component { }

/// <summary>
/// The entire wheatlands biome is now native streamed tiles (FloorAspisWheat),
/// so 0 vegetation entities are spawned while a player runs across terrain.
/// One optional cosmetic veil per character suggests feet submerged in wheat.
/// </summary>
public sealed class AspisWheatFieldSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    private readonly HashSet<EntityUid> _surfaces = new();
    private float _elapsed;
    public void RegisterSurface(EntityUid surfaceMap) => _surfaces.Add(surfaceMap);

    // The former sprite veil was deliberately deleted. The client modifies
    // only humanoid leg layers locally when standing on a wheat tile.
    // No networked entity is created just because someone walked through crops.
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 5f)
            return;
        _elapsed = 0f;
        _surfaces.RemoveWhere(uid => Deleted(uid));
    }

    /// <summary>
    /// Shuttle landing is reserved on the planet grid only. No crop entities are
    /// created; the terrain below the ship becomes harmless FloorDesert.
    /// </summary>
    public void ClearShipLanding(EntityUid surfaceMap, EntityUid ship)
    {
        if (!_surfaces.Contains(surfaceMap) ||
            !TryComp<MapGridComponent>(surfaceMap, out var grid) ||
            !TryComp<MapGridComponent>(ship, out var shipGrid) ||
            !TryComp<BiomeComponent>(surfaceMap, out var biome)) return;

        var box = Transform(ship).WorldMatrix.TransformBox(shipGrid.LocalAABB).Enlarged(2f);
        var centre = box.Center;
        const int extent = 48;
        var minX = Math.Max((int)MathF.Floor(box.Left), (int)centre.X - extent);
        var maxX = Math.Min((int)MathF.Ceiling(box.Right), (int)centre.X + extent);
        var minY = Math.Max((int)MathF.Floor(box.Bottom), (int)centre.Y - extent);
        var maxY = Math.Min((int)MathF.Ceiling(box.Top), (int)centre.Y + extent);
        var floor = new Tile(_tiles["FloorDesert"].TileId);
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        {
            var index = new Vector2i(x, y);
            if (_maps.TryGetTileRef(surfaceMap, grid, index, out var old) && !old.Tile.IsEmpty)
            {
                // Modified landing footprint survives biome unload/reload without
                // reappearing as wheat under physical ship hull tiles.
                var chunk = SharedMapSystem.GetChunkIndices(index, 8) * 8;
                biome.ModifiedTiles.GetOrNew(chunk).Add(index);
                tiles.Add((index, floor));
            }
        }
        if (tiles.Count != 0) _maps.SetTiles(surfaceMap, grid, tiles);
    }
}
