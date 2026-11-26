using System;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// One server-issued physical inspection instrument, bound to a quota. The
/// client cannot choose its organization or contract by editing the item name.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationQuotaInspectionScannerComponent : Component
{
    public Guid Organization;
    public string Code = string.Empty;
}

/// <summary>
/// Server-only audit of one physical crate. A hash of the actual nested goods
/// invalidates the inspection as soon as contents or stacks change.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationQuotaInspectionStampComponent : Component
{
    public Guid Organization;
    public string Code = string.Empty;
    public string Reference = string.Empty;
    public string ContentsFingerprint = string.Empty;
}
