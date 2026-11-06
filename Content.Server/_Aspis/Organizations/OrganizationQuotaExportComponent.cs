namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Server-issued seal identifying one genuine container used in an export quota.
/// A printed paper invoice is consumed on attachment; no client-supplied IDs
/// are trusted as delivery proofs.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationQuotaExportComponent : Component
{
    public Guid Organization;
    public string Code = string.Empty;
    public string Reference = string.Empty;
    public bool Verified;
    // Written only by the server when a pharmaceutical waybill is attached.
    // Every physical item, nesting relationship, stack count and medicine
    // mixture is checked again by Cargo before the shipment is accepted.
    // An invoice merely placed inside a crate has no authenticated seal.
    public string PharmaceuticalSeal = string.Empty;
}
