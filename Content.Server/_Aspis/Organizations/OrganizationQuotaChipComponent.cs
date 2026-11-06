using System;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Server-only cargo manifest attached to a physical printed token.
/// The client never controls its organization, quota code or issuance time.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationQuotaChipComponent : Component
{
    public Guid Organization;
    public string Code = string.Empty;
    public TimeSpan IssuedAt;
    public bool IsFactionFieldSupply;
}
