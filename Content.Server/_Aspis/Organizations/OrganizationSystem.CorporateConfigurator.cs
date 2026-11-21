using System;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Access.Components;
using Content.Shared.Doors.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Corporate mode for the normal AccessConfigurator. The normal station access
/// UI and all of its stock functionality remain unchanged for non-company IDs.
/// Only the server writes corporate door permissions.
/// </summary>
public sealed partial class OrganizationSystem
{
    public bool TryGetCorporateConfiguratorSlots(EntityUid cardUid, EntityUid target,
        out string[] titles, out int[] ids, out bool[] available, out int selected, out string notice)
    {
        titles = [];
        ids = [];
        available = [];
        selected = 0;
        notice = string.Empty;

        if (!TryComp<OrganizationAccessCardComponent>(cardUid, out var companyCard))
            return false;

        // The presence of the corporate component *always* switches the device
        // away from station tags, even if this company has been dissolved.
        if (!_organizations.TryGetValue(companyCard.Organization, out var org))
        {
            notice = "Компанію не знайдено. Ця картка більше не діє.";
            return true;
        }

        var doorValid = target.Valid && !TerminatingOrDeleted(target) &&
            HasComp<AccessReaderComponent>(target) && HasComp<DoorComponent>(target) &&
            IsOnOwnedProperty(target, org.Id);
        var slots = org.AccessProfiles
            .Where(profile => profile.Slot is >= 1 and <= MaxOrganizationAccessSlots)
            .OrderBy(profile => profile.Slot).ToArray();
        ids = slots.Select(profile => profile.Slot).ToArray();
        titles = slots.Select(profile => profile.Title).ToArray();
        available = slots.Select(profile => doorValid &&
            (companyCard.AccessMask & (1 << (profile.Slot - 1))) != 0).ToArray();
        if (doorValid && TryComp<OrganizationPropertyAccessReaderComponent>(target, out var door) &&
            door.Organization == org.Id)
            selected = GetCorporateDoorMask(target, door);

        notice = !doorValid
            ? "Оберіть двері на аванпості, орендованому або придбаному власником шатлі. " +
              "Звичайні двері станції не належать компанії."
            : slots.Length == 0
                ? "Компанія ще не придбала корпоративні доступи."
                : "Власник може позначити кілька доступів для одних дверей. Будь-який з них відкриє двері.";
        return true;
    }

    public bool TryConfigureCorporateDoor(EntityUid configurator, EntityUid user,
        EntityUid cardUid, EntityUid target, int slot)
    {
        if (slot is < 1 or > MaxOrganizationAccessSlots ||
            !TryComp<OrganizationAccessCardComponent>(cardUid, out var companyCard) ||
            !_organizations.TryGetValue(companyCard.Organization, out var org) ||
            !HasPurchasedAccessSlot(org, slot) ||
            (companyCard.AccessMask & (1 << (slot - 1))) == 0 ||
            !target.Valid || TerminatingOrDeleted(target) ||
            !HasComp<AccessReaderComponent>(target) || !HasComp<DoorComponent>(target) ||
            !IsOnOwnedProperty(target, org.Id) ||
            !IsOriginalController(user) ||
            !TryGetCharacter(user, out var key, out _) || org.Owner != key ||
            !_organizationOutpostInteraction.InRangeUnobstructed(user, configurator) ||
            !_organizationOutpostInteraction.InRangeUnobstructed(user, target))
            return false;

        var access = EnsureComp<OrganizationPropertyAccessReaderComponent>(target);
        if (access.Organization != Guid.Empty && access.Organization != org.Id)
            return false;
        var oldMask = access.Organization == org.Id ? GetCorporateDoorMask(target, access) : 0;
        var bit = 1 << (slot - 1);
        // The last alternative cannot be removed accidentally, which would
        // leave the door with no corporate restriction.
        var newMask = (oldMask & bit) != 0 && oldMask != bit
            ? oldMask & ~bit : oldMask | bit;
        access.Organization = org.Id;
        access.RequiredSlot = Enumerable.Range(1, MaxOrganizationAccessSlots)
            .First(i => (newMask & (1 << (i - 1))) != 0);
        EnsureComp<OrganizationDoorAccessMaskComponent>(target).AllowedMask = newMask;
        Dirty(target, access);

        if (HasComp<OrganizationOutpostDoorComponent>(target))
            RemComp<OrganizationOutpostDoorComponent>(target);

        // Normal access tags may not veto valid corporate cards. The corporate
        // BeforeDoorOpenedEvent independently rejects unauthorized players.
        if (TryComp<AccessReaderComponent>(target, out var vanilla))
        {
            vanilla.AccessLists.Clear();
            Dirty(target, vanilla);
        }
        _quotaPopup.PopupEntity($"Корпоративні доступи оновлено: {newMask} (маска).",
            user, user);
        return true;
    }
}
