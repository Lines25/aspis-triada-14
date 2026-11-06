using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

[RegisterComponent]
public sealed partial class OrganizationConsoleComponent : Component
{
}

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class OrganizationAffiliationComponent : Component
{
    [DataField, AutoNetworkedField]
    public string OrganizationId = string.Empty;

    [DataField, AutoNetworkedField]
    public string OrganizationName = string.Empty;

    [DataField, AutoNetworkedField]
    public string Color = "#FFFFFF";

    [DataField, AutoNetworkedField]
    public bool IsOwner;

    [DataField, AutoNetworkedField]
    public OrganizationMemberRole Role;
}
