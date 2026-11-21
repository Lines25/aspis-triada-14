using System;
using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Server-owned route key. This is not an FTL destination disk and cannot set
/// ship position or issue cargo proofs from a client-supplied text field.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationFrontierNavigatorComponent : Component
{
    public Guid Organization;
    public string Code = string.Empty;
}
