using System;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

/// <summary>Station-specific permissions; never represented by global AccessLevel tags.</summary>
public enum OrganizationOutpostAccessTier : byte
{
    Visitor = 0,
    Member = 1,
    Deputy = 2,
    Owner = 3
}

[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationOutpostDoorComponent : Component
{
    [DataField] public Guid Organization;
    [DataField] public OrganizationOutpostAccessTier RequiredTier = OrganizationOutpostAccessTier.Member;
}

/// <summary>Non-ID engineering token. Cannot be inserted into ship registrars or ID consoles.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationOutpostConfiguratorComponent : Component
{
    [DataField] public Guid Organization;
    [DataField] public OrganizationOutpostAccessTier SelectedTier = OrganizationOutpostAccessTier.Member;
}

/// <summary>Saved inside a ship blueprint. Organization is bound on grid load, never trusted from YAML.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationOutpostSpawnBeaconComponent : Component
{
    [DataField] public Guid Organization;
    [DataField] public OrganizationOutpostAccessTier Tier;
    // Round-local verification only. Never trust a saved true from a player ship file.
    public bool Validated;
    public Vector2 ValidatedLocalPosition;
}

/// <summary>The server decides dynamically using the body's actual company membership.</summary>
public sealed class OrganizationOutpostAccessCheckEvent(EntityUid user) : EntityEventArgs
{
    public EntityUid User = user;
    public bool Authorized;
}
