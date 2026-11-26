using System;
using Robust.Shared.GameObjects;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// The reusable SRD is issued by a company work platform. Binding and recharge
/// are server-only round-local data; the client only needs the component type
/// for parsing the entity prototype.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationConstructionWorkToolComponent : Component
{
    public Guid OrganizationId;
    public EntityUid Platform = EntityUid.Invalid;
    public TimeSpan NextRechargeAt;
    // Tracks one SRD-eye permission, even when the repair component is added after pickup.
    public EntityUid? RepairEyeHolder;
}
