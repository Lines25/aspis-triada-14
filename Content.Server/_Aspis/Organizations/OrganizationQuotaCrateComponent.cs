namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Physical server-issued crate for a specific organization quota and trade post.
/// Attached only by CargoSystem when an authenticated quota order is dispatched.
/// This is not a general cargo label and cannot be created by a client message.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationQuotaCrateComponent : Component
{
    public Guid Organization;
    public string Code = string.Empty;
    public string Reference = string.Empty;
    public int Post;
    // Marked before the normal sale deletes the entity, to distinguish delivery from loss.
    public bool DeliveryAccepted;
    public EntityUid DestinationStation = EntityUid.Invalid;
}
