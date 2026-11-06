using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Networked, presentation-only affiliation of a physically owned vessel.
/// Actual membership is always resolved on the server; this component
/// never grants an organization role or station access.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class OrganizationShipIFFComponent : Component
{
    [DataField, AutoNetworkedField]
    public string OrganizationName = string.Empty;

    [DataField, AutoNetworkedField]
    public string OrganizationId = string.Empty;
}
