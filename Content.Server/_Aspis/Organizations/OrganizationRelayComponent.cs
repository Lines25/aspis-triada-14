namespace Content.Server._Aspis.Organizations;

[RegisterComponent]
public sealed partial class OrganizationRelayComponent : Component
{
    public Guid OrganizationId;
    public bool Registered;
}
