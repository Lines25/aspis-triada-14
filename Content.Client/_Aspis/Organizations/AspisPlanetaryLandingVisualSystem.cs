using Content.Shared._Aspis.Organizations;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// The server now animates the REAL transform while the camera follows the
/// player's physical body. This client only keeps a stable face-toward-camera
/// silhouette during descent; it never resizes or offsets any sprite.
/// </summary>
public sealed class AspisPlanetaryLandingVisualSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AspisPlanetaryLandingComponent, ComponentRemove>(OnRemove);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<AspisPlanetaryLandingComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var landing, out var sprite))
        {
            if (landing.VisualReady)
                continue;
            landing.VisualReady = true;
            landing.OriginalDirectionOverride = sprite.DirectionOverride;
            landing.OriginalDirectionOverrideEnabled = sprite.EnableDirectionOverride;
            sprite.DirectionOverride = Direction.South;
            sprite.EnableDirectionOverride = true;
        }
    }

    private void OnRemove(EntityUid uid, AspisPlanetaryLandingComponent landing, ComponentRemove args)
    {
        if (!landing.VisualReady || TerminatingOrDeleted(uid) ||
            !TryComp(uid, out SpriteComponent? sprite))
            return;
        sprite.DirectionOverride = landing.OriginalDirectionOverride;
        sprite.EnableDirectionOverride = landing.OriginalDirectionOverrideEnabled;
    }
}
