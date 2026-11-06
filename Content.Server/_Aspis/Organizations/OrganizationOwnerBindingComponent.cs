namespace Content.Server._Aspis.Organizations;

[RegisterComponent]
public sealed partial class OrganizationOwnerBindingComponent : Component
{
    public Guid OrganizationId;
    public Guid MemberId;
}
