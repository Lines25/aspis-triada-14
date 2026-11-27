using System;
using System.Numerics;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

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
    [DataField("organization")] public string OrganizationKey = string.Empty;
    public Guid Organization
    {
        get => Guid.TryParse(OrganizationKey, out var id) ? id : Guid.Empty;
        set => OrganizationKey = value == Guid.Empty ? string.Empty : value.ToString("D");
    }
    [DataField] public OrganizationOutpostAccessTier RequiredTier = OrganizationOutpostAccessTier.Member;
}

/// <summary>Non-ID engineering token. Cannot be inserted into ship registrars or ID consoles.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationOutpostConfiguratorComponent : Component
{
    public Guid Organization;
    // Legacy tier retained for existing printed devices and saved maps.
    [DataField] public OrganizationOutpostAccessTier SelectedTier = OrganizationOutpostAccessTier.Member;
    [DataField] public bool AdvancedUi;
    [DataField] public int SelectedSlot = 1;
    public EntityUid TargetDoor = EntityUid.Invalid; // Round-local, never save target from YAML
    public const string CardSlotId = "AspisOutpostAccessConfigurator-card";
    [DataField] public ItemSlot CardSlot = new();
}

/// <summary>Saved inside a ship blueprint. Organization is bound on grid load, never trusted from YAML.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationOutpostSpawnBeaconComponent : Component
{
    public Guid Organization;
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

/// <summary>Separate from the stock access-configurator GUI: company-local slots are not station tags.</summary>
[Serializable, NetSerializable]
public enum OrganizationOutpostConfiguratorUiKey : byte { Key }

[Serializable, NetSerializable]
public sealed class OrganizationOutpostConfiguratorSlotEntry(int slot, string title, bool available)
{
    public int Slot = slot;
    public string Title = title;
    public bool Available = available;
}

[Serializable, NetSerializable]
public sealed class OrganizationOutpostConfiguratorUiState(
    string targetName, string cardName, bool authorized, int selectedSlot,
    OrganizationOutpostConfiguratorSlotEntry[] slots) : BoundUserInterfaceState
{
    public string TargetName = targetName;
    public string CardName = cardName;
    public bool Authorized = authorized;
    public int SelectedSlot = selectedSlot;
    public OrganizationOutpostConfiguratorSlotEntry[] Slots = slots;
}

[Serializable, NetSerializable]
public sealed class OrganizationOutpostConfiguratorSelectSlotMessage(int slot) : BoundUserInterfaceMessage
{
    public int Slot = slot;
}
