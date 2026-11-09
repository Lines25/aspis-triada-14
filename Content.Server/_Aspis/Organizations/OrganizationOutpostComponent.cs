using System;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>Round-local identity of a deployed organization's grid.</summary>
[RegisterComponent]
public sealed partial class OrganizationOutpostComponent : Component
{
    public Guid OrganizationId;
}

/// <summary>
/// Corporate stations are legitimate sector stations, but login eligibility is
/// disabled until their role-specific spawn beacons have been validated.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationOutpostStationComponent : Component
{
    public Guid OrganizationId;
}
