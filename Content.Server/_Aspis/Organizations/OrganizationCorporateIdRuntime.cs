using System;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>Marks ordinary ID cards supplied with dynamic corporate permissions.</summary>
[RegisterComponent]
public sealed partial class OrganizationAutoIdComponent : Component
{
}

/// <summary>Identifies the real character to whom a company ID is issued.</summary>
[RegisterComponent]
public sealed partial class OrganizationIssuedCardIdentityComponent : Component
{
    public string UserId = string.Empty;
    public int Slot;
}

/// <summary>Round-local bond between a leased shuttle and the physical console ID.</summary>
[RegisterComponent]
public sealed partial class OrganizationLeaseCardBindingComponent : Component
{
    public Guid Organization;
    public EntityUid Grid;
}

/// <summary>
/// A company console receives an actual ID in this slot; leasing checks the
/// physical item on the server and not an ID supplied by a network message.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationConsoleLeaseSlotComponent : Component
{
    public const string SlotId = "Aspis-OrganizationConsole-LeaseId";
    public ItemSlot CardSlot = new();
}
