using System;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Non-networked round-local site identity. This is neither a ship deed nor
/// a company-owned vessel. Contracts must verify the target hull separately.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationConstructionYardComponent : Component
{
    public Guid OrganizationId;
    public Guid AssignmentId;
    public string QuotaCode = string.Empty;
    public bool IsWorkPlatform;
    public EntityUid ContractGrid = EntityUid.Invalid;
}
