using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Humanoid;
using Content.Shared.Maps;
using Content.Shared.Parallax.Biomes;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// One local GPU post-shader for the ENTIRE humanoid sprite (including suit),
/// not per-leg color fields. No entity overlay, no network churn and no
/// obscured items/projectiles. Restores original sprite rendering on exit.
/// </summary>
public sealed class AspisWheatImmersionVisualSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    private const string ShaderId = "aspis-wheat-lower-half";
    private readonly HashSet<EntityUid> _faded = new();
    private readonly HashSet<EntityUid> _current = new();
    private float _elapsed;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 0.14f)
            return;
        _elapsed = 0f;
        _current.Clear();
        var wheatId = _tiles["FloorAspisWheat"].TileId;
        var query = EntityQueryEnumerator<HumanoidAppearanceComponent, SpriteComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var sprite, out var xform))
        {
            // A falling actor is still ABOVE wheat, despite being parented to
            // the surface map. Never mask legs until touchdown removes landing.
            if (HasComp<AspisPlanetaryLandingComponent>(uid))
            {
                Restore(uid, sprite);
                continue;
            }
            if (xform.MapUid is not { } map ||
                !TryComp<AspisPlanetaryRadarComponent>(map, out var planet) || !planet.Surface ||
                // Reject actual buildings and shuttles; standing on the planet
                // biome grid itself is valid, not an automatic rejection.
                (xform.GridUid is { } otherGrid && otherGrid != map) ||
                !TryComp<MapGridComponent>(map, out var grid))
            {
                Restore(uid, sprite);
                continue;
            }
            var index = new Vector2i((int)MathF.Floor(xform.WorldPosition.X),
                (int)MathF.Floor(xform.WorldPosition.Y));
            if (!_maps.TryGetTileRef(map, grid, index, out var tile) ||
                tile.Tile.TypeId != wheatId)
            {
                Restore(uid, sprite);
                continue;
            }
            _current.Add(uid);
            if (_faded.Add(uid))
            {
                var shader = _proto.Index<ShaderPrototype>("AspisWheatLowerFade").Instance();
                _sprites.SetPostShader((uid, sprite),
                    new SpriteComponent.PostShaderArgs(ShaderId, shader));
            }
        }
        foreach (var uid in new List<EntityUid>(_faded))
        {
            if (_current.Contains(uid)) continue;
            if (TryComp<SpriteComponent>(uid, out var sprite))
                Restore(uid, sprite);
            else
                _faded.Remove(uid);
        }
    }

    private void Restore(EntityUid uid, SpriteComponent sprite)
    {
        if (!_faded.Remove(uid)) return;
        _sprites.RemovePostShader((uid, sprite), ShaderId);
    }
}
