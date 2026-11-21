using System;
using Content.Shared.Parallax.Biomes.Layers;
using System.Collections.Generic;
using Robust.Shared.Maths;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Deterministic and continuous wheatlands terrain. Called by the native tile
/// selector so road and wheat tiles stream and unload normally with biome chunks.
/// Station and landing clearances are based on the documented Q2.4C positions.
/// </summary>
public static class AspisWheatTerrain
{
    public static bool AppliesTo(List<IBiomeLayer> layers)
    {
        foreach (var layer in layers)
            if (layer is BiomeTileLayer tile && tile.Tile.ToString() == "FloorAspisWheat")
                return true;
        return false;
    }

    public static bool IsStationClear(int x, int y) => x >= 72 && x <= 120 && y >= 2 && y <= 46;
    public static bool IsLandingClear(int x, int y) => x >= 4 && x <= 44 && y >= 4 && y <= 44;

    // Connected 3-tile corridors. Smooth curves are functions of absolute tile
    // coordinates (NOT chunk seed), so no seams when a chunk is unloaded/reloaded.
    public static bool IsRoad(int x, int y)
    {
        var eastWest = -9f + 8f * MathF.Sin(x * 0.015f) + 3f * MathF.Sin(x * 0.041f + 0.75f);
        var northSouth = 133f + 10f * MathF.Sin(y * 0.018f + 0.2f) + 3f * MathF.Sin(y * 0.053f);
        return MathF.Abs(y - eastWest) <= 1.5f || MathF.Abs(x - northSouth) <= 1.5f;
    }

    public static string GetFloor(int x, int y)
    {
        if (IsStationClear(x,y) || IsLandingClear(x,y)) return "FloorDesert";
        return IsRoad(x,y) ? "FloorLowDesert" : "FloorAspisWheat";
    }
}
