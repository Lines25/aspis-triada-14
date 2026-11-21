using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class AspisAgroDetectorComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Active;

    // Seconds between flashes; nearer targets are easier to identify.
    [DataField, AutoNetworkedField]
    public float Period = 1.4f;
}
