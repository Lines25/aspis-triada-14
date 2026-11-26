using Content.Shared.Weather;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// Full-screen acid haze during the quarantined planetary weather state.
/// One local overlay draw per frame; no server-side entities follow players.
/// </summary>
public sealed class AspisAcidHazeVisualSystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlays = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IResourceCache _resources = default!;
    private AspisAcidHazeOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new AspisAcidHazeOverlay(EntityManager, _player, _resources);
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        if (_overlay != null)
            _overlays.RemoveOverlay(_overlay);
        base.Shutdown();
    }
}

internal sealed class AspisAcidHazeOverlay : Overlay
{
    private readonly IEntityManager _entities;
    private readonly IPlayerManager _players;
    private readonly IResourceCache _cache;
    private Texture? _mask;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    internal AspisAcidHazeOverlay(IEntityManager entities,
        IPlayerManager players, IResourceCache cache)
    {
        _entities = entities;
        _players = players;
        _cache = cache;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_players.LocalEntity is not { } player ||
            !_entities.TryGetComponent<TransformComponent>(player, out var transform) ||
            transform.MapUid is not { } map ||
            transform.MapID != args.MapId ||
            (transform.GridUid is { } grid && grid != map) ||
            !_entities.TryGetComponent<WeatherComponent>(map, out var weather))
            return;

        var hasAcidRain = false;
        foreach (var proto in weather.Weather.Keys)
        {
            if (proto.Id != "AspisAgroAcidRain")
                continue;
            hasAcidRain = true;
            break;
        }

        if (!hasAcidRain)
            return;

        if (_mask == null)
        {
            // Resolve the texture once via the existing Robust cache API.
            if (!_cache.TryGetResource<TextureResource>(
                    new ResPath("/Textures/_Aspis/Effects/acid_haze_screen.png"),
                    out var textureResource))
                return;

            _mask = textureResource.Texture;
        }

        // Screen-space radius follows the player/camera, never any world tile.
        args.ScreenHandle.DrawTextureRect(_mask, args.ViewportBounds);
    }
}
