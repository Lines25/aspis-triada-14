using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Second (surface-map) phase of atmospheric descent.
/// Separate from ChasmFallingComponent: the latter deletes its owner on timeout.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AspisPlanetaryLandingComponent : Component
{
    [DataField, AutoNetworkedField]
    public TimeSpan AnimationTime = TimeSpan.FromSeconds(5);

    [DataField, AutoNetworkedField]
    public TimeSpan StartAt;

    [DataField, AutoNetworkedField]
    public float StartScale = 0.20f;

    // Client-only visual state, never saved to the registry.
    public Vector2 OriginalScale = Vector2.One;
    public Vector2 OriginalOffset = Vector2.Zero;
    public bool VisualReady;
    public Robust.Shared.Maths.Direction OriginalDirectionOverride;
    public bool OriginalDirectionOverrideEnabled;
}
