using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._NF.Shipyard.Components;
using Robust.Shared.Network;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.IoC;
using Robust.Shared.Map.Components;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    [Dependency] private AccessReaderSystem _reader = default!;
    private const int MaxOrganizationAccessSlots = OrganizationAccessPricing.MaxSlots;
    private const long OrganizationAccessCardPrice = OrganizationAccessPricing.PrintedCardPrice;

    private static string AccessRoleKey(OrganizationPermissionTier tier, Guid customId) =>
        customId != Guid.Empty ? $"custom:{customId:D}" : tier switch
        {
            OrganizationPermissionTier.Owner => "owner",
            OrganizationPermissionTier.Deputy => "deputy",
            OrganizationPermissionTier.Officer => "officer",
            _ => "member"
        };

    private int GetRoleAccessMask(Organization org, string roleKey)
    {
        // The owner must never get locked out of company property simply
        // because there is no editable access mask for the fixed owner role.
        if (string.Equals(roleKey, "owner", StringComparison.OrdinalIgnoreCase))
        {
            var purchased = 0;
            foreach (var profile in org.AccessProfiles)
                if (profile.Slot is >= 1 and <= MaxOrganizationAccessSlots)
                    purchased |= 1 << (profile.Slot - 1);
            return purchased;
        }
        return Math.Clamp(org.AccessRoleGrants.FirstOrDefault(g =>
            string.Equals(g.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase))?.AccessMask ?? 0, 0, 31);
    }

    private OrganizationRoleAccessGrantEntry[] BuildRoleAccessGrantEntries(Organization org)
    {
        var list = new List<OrganizationRoleAccessGrantEntry>
        {
            new(AccessRoleKey(OrganizationPermissionTier.Owner, Guid.Empty), GetRoleAccessMask(org, "owner")),
            new(AccessRoleKey(OrganizationPermissionTier.Deputy, Guid.Empty), GetRoleAccessMask(org, "deputy")),
            new(AccessRoleKey(OrganizationPermissionTier.Officer, Guid.Empty), GetRoleAccessMask(org, "officer")),
            new(AccessRoleKey(OrganizationPermissionTier.Member, Guid.Empty), GetRoleAccessMask(org, "member"))
        };
        list.AddRange(org.CustomRoles.Select(c => new OrganizationRoleAccessGrantEntry(
            AccessRoleKey(OrganizationPermissionTier.Member, c.Id), GetRoleAccessMask(org, $"custom:{c.Id:D}"))));
        return list.ToArray();
    }

    private bool HasPurchasedAccessSlot(Organization org, int slot) =>
        slot is >= 1 and <= MaxOrganizationAccessSlots && org.AccessProfiles.Any(a => a.Slot == slot);

    private bool IsOnOwnedProperty(EntityUid target, Guid orgId)
    {
        if (TerminatingOrDeleted(target) || Transform(target).GridUid is not { } gridUid)
            return false;
        if (_spawnedOrganizationOutposts.TryGetValue(orgId, out var outpost) && !TerminatingOrDeleted(outpost.Grid) && outpost.Grid == gridUid)
            return true;
        if (TryComp<OrganizationFleetShuttleComponent>(gridUid, out var fleet) && fleet.OrganizationId == orgId)
            return true;

        // Ordinary purchased Shipyard shuttles have ShipOwnership, not the
        // OrganizationFleetShuttle marker (which is only for leased vessels).
        // Bind to the founder's account, not to any member's private ship.
        if (!_organizations.TryGetValue(orgId, out var organization) ||
            !TryComp<ShipOwnershipComponent>(gridUid, out var shipOwnership) ||
            !Guid.TryParse(organization.Owner.UserId, out var founderId))
            return false;
        return shipOwnership.OwnerUserId == new NetUserId(founderId);
    }

    private bool HasMatchingOrganizationAccessCard(EntityUid user, Guid orgId, int requiredSlot)
    {
        if (requiredSlot is < 1 or > MaxOrganizationAccessSlots ||
            !IsOriginalController(user) ||
            !TryGetCharacter(user, out var key, out _) ||
            !_organizations.TryGetValue(orgId, out var org) ||
            org.InsolvencyState != OrganizationInsolvencyState.Active ||
            !org.Members.TryGetValue(key, out var member) ||
            (CurrentMemberAccessMask(org, member) & (1 << (requiredSlot - 1))) == 0)
            return false;

        SynchronizeOrdinaryCompanyId(user, key);
        foreach (var item in _reader.FindPotentialAccessItems(user))
        {
            if (!TryComp<OrganizationAccessCardComponent>(item, out var card) || card.Organization != orgId)
                continue;
            if (TryComp<OrganizationIssuedCardIdentityComponent>(item, out var issued) &&
                (issued.UserId != key.UserId || issued.Slot != key.Slot))
                continue;
            if ((card.AccessMask & (1 << (requiredSlot - 1))) != 0)
                return true;
        }
        return false;
    }

    private void OnOrganizationPropertyReaderCheck(EntityUid uid, OrganizationPropertyAccessReaderComponent reader,
        OrganizationPropertyAccessCheckEvent args)
    {
        args.Authorized = HasAnyMatchingCorporatePermission(args.User, uid, reader);
    }

    private void OnOrganizationPropertyReaderConfigured(EntityUid uid, AccessReaderComponent component, InteractUsingEvent args)
    {
        // InteractUsingEvent has no CanReach property. Validate proximity with
        // the server's shared interaction system, as the outpost reader already does.
        if (args.Handled || !IsOriginalController(args.User) ||
            !_organizationOutpostInteraction.InRangeUnobstructed(args.User, uid) ||
            !TryComp<OrganizationAccessCardComponent>(args.Used, out var card))
            return;

        if (card.PrimarySlot is < 1 or > MaxOrganizationAccessSlots ||
            !IsOnOwnedProperty(uid, card.Organization) ||
            !TryGetCharacter(args.User, out var key, out _))
            return;

        if (!_organizations.TryGetValue(card.Organization, out var org) || org.Owner != key ||
            !HasPurchasedAccessSlot(org, card.PrimarySlot) ||
            (card.AccessMask & (1 << (card.PrimarySlot - 1))) == 0)
            return;

        var access = EnsureComp<OrganizationPropertyAccessReaderComponent>(uid);
        access.Organization = card.Organization;
        access.RequiredSlot = card.PrimarySlot;
        EnsureComp<OrganizationDoorAccessMaskComponent>(uid).AllowedMask =
            1 << (card.PrimarySlot - 1);
        Dirty(uid, access);
        // The corporate authorization is performed by BeforeDoorOpenedEvent.
        // Leaving the stock station tags in place would incorrectly reject a
        // valid company card even after our custom access check passed.
        component.AccessLists.Clear();
        Dirty(uid, component);
        if (HasComp<OrganizationOutpostDoorComponent>(uid))
            RemComp<OrganizationOutpostDoorComponent>(uid);
        _quotaPopup.PopupEntity($"Двері налаштовано на корпоративний доступ: {card.PrimarySlot}. {org.AccessProfiles.FirstOrDefault(a => a.Slot == card.PrimarySlot)?.Title ?? string.Empty}",
            args.User, args.User);
        args.Handled = true;
    }

    private void OnPurchaseAccessSlot(EntityUid uid, OrganizationConsoleComponent comp, OrganizationPurchaseAccessSlotMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        if (args.Slot is < 1 or > MaxOrganizationAccessSlots || HasPurchasedAccessSlot(org, args.Slot))
        {
            Refresh(uid, actor, "Цей слот уже придбано або номер доступу недійсний.");
            return;
        }

        // Slots are strictly sequential: clients cannot buy slot 5 first,
        // nor can they obtain a later slot by forging a UI message.
        var expectedSlot = Enumerable.Range(1, MaxOrganizationAccessSlots)
            .FirstOrDefault(candidate => !HasPurchasedAccessSlot(org, candidate));
        if (args.Slot != expectedSlot)
        {
            Refresh(uid, actor, $"Спочатку придбайте доступ №{expectedSlot}.");
            return;
        }

        var price = OrganizationAccessPricing.NextSlotPrice(expectedSlot - 1);
        if (price <= 0 || org.Treasury < price)
        {
            Refresh(uid, actor, $"Недостатньо коштів для нового доступу: потрібно {price:N0} $.");
            return;
        }
        if (!Commit(() =>
        {
            org.Treasury -= price;
            org.AccessProfiles.Add(new OrganizationAccessProfileRecord { Slot = args.Slot, Title = $"Доступ {args.Slot}" });
            org.AccessProfiles.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }))
        {
            Refresh(uid, actor, "Не вдалося зберегти купівлю, кошти не списано.");
            return;
        }
        Refresh(uid, actor, $"Придбано корпоративний доступ №{args.Slot} за {price:N0} $.");
    }

    private void OnRenameAccessSlot(EntityUid uid, OrganizationConsoleComponent comp, OrganizationRenameAccessSlotMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            string.IsNullOrWhiteSpace(args.Title))
            return;
        var title = args.Title.Trim();
        if (!ValidTitle(title))
            return;
        var slot = org.AccessProfiles.FirstOrDefault(a => a.Slot == args.Slot);
        if (slot == null)
            return;
        if (!Commit(() => { slot.Title = title; }))
        {
            Refresh(uid, actor, "Не вдалося зберегти назву доступу.");
            return;
        }
        Refresh(uid, actor, $"Доступ №{args.Slot} перейменовано.");
    }

    private void OnSetRoleAccessMask(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetRoleAccessMaskMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            string.IsNullOrWhiteSpace(args.RoleKey) || args.RoleKey.Length > 80 ||
            !TryResolvePrintedRole(org, args.RoleKey, out _, out _))
            return;
        var allowedMask = 0;
        foreach (var slot in org.AccessProfiles)
            allowedMask |= 1 << (slot.Slot - 1);
        var mask = args.AccessMask & allowedMask;
        if (string.Equals(args.RoleKey, "owner", StringComparison.OrdinalIgnoreCase))
        {
            Refresh(uid, actor, "Власник автоматично має всі придбані доступи.");
            return;
        }
        if (!Commit(() =>
        {
            var grant = org.AccessRoleGrants.FirstOrDefault(g => string.Equals(g.RoleKey, args.RoleKey, StringComparison.OrdinalIgnoreCase));
            if (grant == null)
                org.AccessRoleGrants.Add(new OrganizationAccessRoleGrantRecord { RoleKey = args.RoleKey, AccessMask = mask });
            else
                grant.AccessMask = mask;
        }))
        {
            Refresh(uid, actor, "Не вдалося зберегти права доступу.");
            return;
        }
        Refresh(uid, actor, "Права корпоративного доступу для посади оновлено.");
    }

    private bool TryResolvePrintedRole(Organization org, string roleKey, out string title, out int mask)
    {
        title = roleKey;
        mask = GetRoleAccessMask(org, roleKey);
        switch (roleKey)
        {
            case "owner": title = org.OwnerTitle; return true;
            case "deputy": title = org.DeputyTitle; return true;
            case "officer": title = org.OfficerTitle; return true;
            case "member": title = org.MemberTitle; return true;
        }
        if (roleKey.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(roleKey[7..], out var customId) &&
            org.CustomRoles.FirstOrDefault(c => c.Id == customId) is { } custom)
        {
            title = custom.Title;
            return true;
        }
        return false;
    }

    private void OnSetAccessSlotRoles(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationSetAccessSlotRolesMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        if (!HasPurchasedAccessSlot(org, args.Slot) || args.RoleKeys == null ||
            args.RoleKeys.Length > 16 || args.RoleKeys.Any(key =>
                string.IsNullOrWhiteSpace(key) || key.Length > 80 ||
                !TryResolvePrintedRole(org, key, out _, out _)))
        {
            Refresh(uid, actor, "Невірний список посад або доступ ще не придбано.");
            return;
        }
        var chosen = args.RoleKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bit = 1 << (args.Slot - 1);
        if (!Commit(() =>
        {
            foreach (var role in BuildRoleAccessGrantEntries(org))
            {
                if (role.RoleKey == "owner")
                    continue; // owner always possesses each purchased access
                var grant = org.AccessRoleGrants.FirstOrDefault(g =>
                    string.Equals(g.RoleKey, role.RoleKey, StringComparison.OrdinalIgnoreCase));
                var mask = ((grant?.AccessMask ?? 0) & ~bit) |
                    (chosen.Contains(role.RoleKey) ? bit : 0);
                if (grant == null)
                    org.AccessRoleGrants.Add(new OrganizationAccessRoleGrantRecord
                    { RoleKey = role.RoleKey, AccessMask = mask });
                else
                    grant.AccessMask = mask;
            }
        }))
        {
            Refresh(uid, actor, "Не вдалося зберегти вибрані посади.");
            return;
        }
        Refresh(uid, actor, $"Власників доступу №{args.Slot} оновлено.");
    }

    // The request never chooses a job or access slot. The authoritative
    // character membership determines the job, and the owner always receives
    // every purchased corporate access.
    private void OnPrintAccessCard(EntityUid uid, OrganizationConsoleComponent comp, OrganizationPrintAccessCardMessage args)
    {
        if (!AuthorizedBusiness(uid, args.Actor) || !IsOriginalController(args.Actor) ||
            !TryGetCharacter(args.Actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            !org.Members.TryGetValue(key, out var member))
            return;

        // Reject forged requests from previous versions of the UI.
        if (args.RoleKey != "self" || args.PrimarySlot != 0)
            return;

        if (!HasCompletedCorporateWaitingPeriod(member))
        {
            var remaining = (int)Math.Ceiling((CorporateIdWaitingPeriod -
                (DateTime.UtcNow - member.JoinedAtUtc)).TotalMinutes);
            Refresh(uid, args.Actor, $"ID можна друкувати через {Math.Max(1, remaining)} хв після вступу.");
            return;
        }

        if (org.Treasury < OrganizationAccessCardPrice)
        {
            Refresh(uid, args.Actor, $"Недостатньо коштів на ID-картку: потрібно {OrganizationAccessCardPrice:N0} $.");
            return;
        }

        var roleKey = MemberAccessRoleKey(org, member);
        if (!TryResolvePrintedRole(org, roleKey, out var roleTitle, out var accessMask))
            return;
        var available = 0;
        foreach (var profile in org.AccessProfiles)
            if (profile.Slot is >= 1 and <= MaxOrganizationAccessSlots)
                available |= 1 << (profile.Slot - 1);
        accessMask &= available;
        var primarySlot = Enumerable.Range(1, MaxOrganizationAccessSlots)
            .FirstOrDefault(slot => (accessMask & (1 << (slot - 1))) != 0);
        if (primarySlot == 0)
        {
            Refresh(uid, args.Actor, "Вашій посаді ще не призначено корпоративних доступів.");
            return;
        }
        if (!_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>("AspisOrganizationAccessCard"))
        {
            Refresh(uid, args.Actor, "Прототип корпоративної ID-картки не завантажився.");
            return;
        }
        if (!Commit(() => { org.Treasury -= OrganizationAccessCardPrice; }))
        {
            Refresh(uid, args.Actor, "Не вдалося зберегти випуск картки, кошти не списано.");
            return;
        }
        var printed = Spawn("AspisOrganizationAccessCard", Transform(uid).Coordinates);
        var card = EnsureComp<OrganizationAccessCardComponent>(printed);
        card.Organization = org.Id;
        card.AccessMask = accessMask;
        card.PrimarySlot = primarySlot;
        card.IssuedForRole = roleTitle;
        card.OrganizationName = org.Name;
        var identity = EnsureComp<OrganizationIssuedCardIdentityComponent>(printed);
        identity.UserId = key.UserId;
        identity.Slot = key.Slot;
        Dirty(printed, card);
        _organizationMetadata.SetEntityName(printed, $"ID компанії {org.Name}: {roleTitle}");
        _quotaPopup.PopupEntity($"Надруковано ID-картку для посади «{roleTitle}».", args.Actor, args.Actor);
        Refresh(uid, args.Actor, $"Видано ID-картку за {OrganizationAccessCardPrice:N0} $.");
    }

}
