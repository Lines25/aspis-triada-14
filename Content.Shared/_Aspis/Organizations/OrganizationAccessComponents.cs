using System;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Reader for organization-owned property (company shuttles, outposts, etc.)
/// using one of up to five purchased organization-local access slots.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationPropertyAccessReaderComponent : Component
{
    // Guid is not a supported YAML data field in this build. Persist its canonical text.
    [DataField("organization")] public string OrganizationKey = string.Empty;
    public Guid Organization
    {
        get => Guid.TryParse(OrganizationKey, out var id) ? id : Guid.Empty;
        set => OrganizationKey = value == Guid.Empty ? string.Empty : value.ToString("D");
    }
    [DataField] public int RequiredSlot = 1;
}

/// <summary>
/// Physical organization-issued card. The engine does not treat these as
/// global station access tags; they work only with organization property readers.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class OrganizationAccessCardComponent : Component
{
    public Guid Organization;
    [DataField] public int AccessMask;
    [DataField] public int PrimarySlot = 1;
    [DataField] public string IssuedForRole = string.Empty;
    [DataField] public string OrganizationName = string.Empty;
}

public sealed class OrganizationPropertyAccessCheckEvent(EntityUid user) : EntityEventArgs
{
    public EntityUid User = user;
    public bool Authorized;
}

/// <summary>
/// Progressive price for the NEXT company-local access slot. Slot numbers
/// are labels; the price is based on how many profiles have been purchased.
/// Kept in Shared so the UI never advertises a different price than the server.
/// </summary>
public static class OrganizationAccessPricing
{
    public const int MaxSlots = 5;
    public const long PrintedCardPrice = 10_000;

    public static long NextSlotPrice(int purchasedCount) => purchasedCount switch
    {
        <= 0 => 100_000,
        1 => 240_000,
        2 => 520_000,
        3 => 810_000,
        4 => 1_100_000,
        _ => 0
    };
}
