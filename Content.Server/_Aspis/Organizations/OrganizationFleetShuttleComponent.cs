using System;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>Round-local marker for a shuttle issued to an organization's quota crew.</summary>
[RegisterComponent]
public sealed partial class OrganizationFleetShuttleComponent : Component
{
    public Guid OrganizationId;
    // Round-local trace for future server-side damage attribution.
    public string RenterUserId = string.Empty;
    public int RenterSlot;
}
