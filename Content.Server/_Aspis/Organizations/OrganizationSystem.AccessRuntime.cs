using System;
using System.Linq;
using Content.Shared.Access.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    // Applied to both printed cards and ordinary IDs: prevents join-print-leave abuse.
    private static readonly TimeSpan CorporateIdWaitingPeriod = TimeSpan.FromMinutes(15);
    private readonly System.Collections.Generic.Dictionary<Guid, EntityUid> _fleetCardBindings = new();

    private bool HasCompletedCorporateWaitingPeriod(Member member) =>
        member.Role == OrganizationMemberRole.Owner ||
        member.JoinedAtUtc == DateTime.MinValue ||
        DateTime.UtcNow - member.JoinedAtUtc >= CorporateIdWaitingPeriod;

    private string MemberAccessRoleKey(Organization org, Member member) =>
        member.Role == OrganizationMemberRole.Owner ? "owner" :
        member.Id == org.DeputyMemberId ? "deputy" :
        member.Role == OrganizationMemberRole.Member && member.CustomRoleId != Guid.Empty &&
        org.CustomRoles.Any(r => r.Id == member.CustomRoleId)
            ? $"custom:{member.CustomRoleId:D}" :
        member.Role == OrganizationMemberRole.Officer ? "officer" : "member";

    private int CurrentMemberAccessMask(Organization org, Member member)
    {
        if (!HasCompletedCorporateWaitingPeriod(member))
            return 0;
        var allowed = 0;
        foreach (var profile in org.AccessProfiles)
            if (profile.Slot is >= 1 and <= MaxOrganizationAccessSlots)
                allowed |= 1 << (profile.Slot - 1);
        return GetRoleAccessMask(org, MemberAccessRoleKey(org, member)) & allowed;
    }

    // This runs periodically AND on-demand during a door access check.
    // Never adopt an existing printed corporate card or an auto ID belonging to
    // a different character; only normal IDs actually carried by this actor.
    private void SynchronizeOrdinaryCompanyId(EntityUid actor, CharacterKey key)
    {
        var org = IsTdfOfficer(actor) ? null : FindMembership(key);
        foreach (var item in _reader.FindPotentialAccessItems(actor))
        {
            if (!TryComp<IdCardComponent>(item, out _))
                continue;
            var auto = HasComp<OrganizationAutoIdComponent>(item);
            if (TryComp<OrganizationIssuedCardIdentityComponent>(item, out var owner) &&
                (owner.UserId != key.UserId || owner.Slot != key.Slot))
                continue;
            // Never overwrite an independently printed corporate card.
            if (HasComp<OrganizationAccessCardComponent>(item) && !auto)
                continue;
            if (org == null || !org.Members.TryGetValue(key, out var member) ||
                org.InsolvencyState != OrganizationInsolvencyState.Active)
            {
                if (auto)
                {
                    RemComp<OrganizationAutoIdComponent>(item);
                    RemComp<OrganizationAccessCardComponent>(item);
                    RemComp<OrganizationIssuedCardIdentityComponent>(item);
                }
                continue;
            }
            var mask = CurrentMemberAccessMask(org, member);
            var roleKey = MemberAccessRoleKey(org, member);
            var title = TryResolvePrintedRole(org, roleKey, out var displayTitle, out _)
                ? displayTitle : roleKey;
            var primary = Enumerable.Range(1, MaxOrganizationAccessSlots)
                .FirstOrDefault(slot => (mask & (1 << (slot - 1))) != 0);
            var card = EnsureComp<OrganizationAccessCardComponent>(item);
            if (card.Organization != org.Id || card.AccessMask != mask ||
                card.PrimarySlot != primary || card.IssuedForRole != title ||
                card.OrganizationName != org.Name)
            {
                card.Organization = org.Id;
                card.AccessMask = mask;
                card.PrimarySlot = primary;
                card.IssuedForRole = title;
                card.OrganizationName = org.Name;
                Dirty(item, card);
            }
            EnsureComp<OrganizationAutoIdComponent>(item);
            var identity = EnsureComp<OrganizationIssuedCardIdentityComponent>(item);
            identity.UserId = key.UserId;
            identity.Slot = key.Slot;
        }
    }

    private int GetCorporateDoorMask(EntityUid uid, OrganizationPropertyAccessReaderComponent reader)
    {
        if (TryComp<OrganizationDoorAccessMaskComponent>(uid, out var multi))
            return multi.AllowedMask & 31;
        return reader.RequiredSlot is >= 1 and <= MaxOrganizationAccessSlots
            ? 1 << (reader.RequiredSlot - 1) : 0;
    }

    private bool HasAnyMatchingCorporatePermission(EntityUid actor, EntityUid door,
        OrganizationPropertyAccessReaderComponent reader)
    {
        var permitted = GetCorporateDoorMask(door, reader);
        for (var slot = 1; slot <= MaxOrganizationAccessSlots; slot++)
            if ((permitted & (1 << (slot - 1))) != 0 &&
                HasMatchingOrganizationAccessCard(actor, reader.Organization, slot))
                return true;
        return false;
    }

    private void OnLeaseConsoleInitialized(EntityUid uid, OrganizationConsoleComponent comp, ComponentInit args)
    {
        var lease = EnsureComp<OrganizationConsoleLeaseSlotComponent>(uid);
        _organizationConfiguratorSlots.AddItemSlot(uid, OrganizationConsoleLeaseSlotComponent.SlotId, lease.CardSlot);
    }

    private void OnLeaseConsoleShutdown(EntityUid uid, OrganizationConsoleComponent comp, ComponentShutdown args)
    {
        if (TryComp<OrganizationConsoleLeaseSlotComponent>(uid, out var lease))
            _organizationConfiguratorSlots.RemoveItemSlot(uid, lease.CardSlot);
    }
}
