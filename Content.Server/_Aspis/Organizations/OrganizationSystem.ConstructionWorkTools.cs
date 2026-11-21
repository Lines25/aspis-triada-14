using System;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Hands;
using Content.Shared.Eye;
using Content.Shared.Inventory.Events;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.GameTicking;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private const string ConstructionWorkToolPrototype = "AspisConstructionWorkSRD";
    private const int ConstructionWorkToolCount = 2;

    private void EnsureConstructionYardWorkTools(EntityUid platform, Guid organizationId)
    {
        if (TerminatingOrDeleted(platform) ||
            !_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(ConstructionWorkToolPrototype))
            return;

        var existing = 0;
        var tools = EntityQueryEnumerator<OrganizationConstructionWorkToolComponent>();
        while (tools.MoveNext(out var uid, out var component))
        {
            if (!TerminatingOrDeleted(uid) && component.Platform == platform)
                existing++;
        }
        // Only two tools per live platform, including when reusing that yard.
        for (var index = existing; index < ConstructionWorkToolCount; index++)
        {
            // The original build_post map already has a real Rack at (1.5, 3.5).
            // Both devices appear physically on that one rack, not on two floor tiles.
            var pos = new Vector2(1.5f, 3.5f);
            var uid = Spawn(ConstructionWorkToolPrototype, new EntityCoordinates(platform, pos));
            var tag = EnsureComp<OrganizationConstructionWorkToolComponent>(uid);
            tag.OrganizationId = organizationId;
            tag.Platform = platform;
            tag.NextRechargeAt = _gameTiming.CurTime + TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>
    /// The tool must be physically ON the company's yard or its active docked
    /// repair hull. Any holder must be an actual member of that company.
    /// Unheld tools may recharge in the workshop, but nobody else can use them.
    /// </summary>
    internal void GetConstructionToolPermissions(Guid organizationId, EntityUid platform,
        EntityUid? currentGrid, EntityUid? holder, out bool mayRecharge, out bool mayRepair)
    {
        mayRecharge = false;
        mayRepair = false;
        if (currentGrid == null || TerminatingOrDeleted(platform) ||
            !_organizations.TryGetValue(organizationId, out var org) ||
            org.Kind != OrganizationKind.Company ||
            org.Industry != OrganizationIndustry.Construction ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;

        var activeHull = EntityUid.Invalid;
        foreach (var yard in _constructionYards.Values)
        {
            if (yard.OrganizationId != organizationId || yard.WorkPlatform != platform ||
                yard.Closed || TerminatingOrDeleted(yard.ContractHull))
                continue;
            activeHull = yard.ContractHull;
            break;
        }
        if (!activeHull.Valid || (currentGrid != platform && currentGrid != activeHull))
            return;

        if (holder != null &&
            (!TryGetCharacter(holder.Value, out var key, out _) ||
             !IsOriginalController(holder.Value) || !org.Members.ContainsKey(key)))
            return;

        mayRecharge = true;
        mayRepair = currentGrid == activeHull && holder != null;
    }
}

/// <summary>
/// Checks access every server update. The ShipRepairTool component exists ONLY while an
/// authorized company worker stands on their active contract ship. This gates
/// both repair interactions and their do-after completion; outside the berth,
/// the borrowed device is just an inert item.
/// </summary>
public sealed class OrganizationConstructionWorkToolSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedChargesSystem _charges = default!;
    [Dependency] private SharedEyeSystem _eyes = default!;

    private static readonly TimeSpan RechargePeriod = TimeSpan.FromSeconds(5);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        // The Mono SRD's own eye hook only responds to equip events when the
        // ShipRepairTool component is already present. Our locked-down device
        // receives that component AFTER it has been equipped, so we must
        // explicitly issue the visibility permission. Track ordinary equip
        // events too, to avoid double-counting during hand/belt swaps.
        SubscribeLocalEvent<OrganizationConstructionWorkToolComponent, GotEquippedHandEvent>(OnWorkToolHandEquipped);
        SubscribeLocalEvent<OrganizationConstructionWorkToolComponent, GotUnequippedHandEvent>(OnWorkToolHandUnequipped);
        SubscribeLocalEvent<OrganizationConstructionWorkToolComponent, GotEquippedEvent>(OnWorkToolEquipped);
        SubscribeLocalEvent<OrganizationConstructionWorkToolComponent, GotUnequippedEvent>(OnWorkToolUnequipped);
        SubscribeLocalEvent<OrganizationConstructionWorkToolComponent, ComponentShutdown>(OnWorkToolShutdown);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        // Holder state is round-local and never persisted.
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        // Access is checked every frame, not once per second. In particular,
        // leaving the contract hull must turn off this tool immediately.
        var organizations = EntityManager.System<OrganizationSystem>();
        var tools = EntityQueryEnumerator<OrganizationConstructionWorkToolComponent, LimitedChargesComponent>();
        while (tools.MoveNext(out var uid, out var bound, out var charges))
        {
            if (TerminatingOrDeleted(uid))
                continue;

            var holder = FindWorkToolHolder(uid);
            organizations.GetConstructionToolPermissions(bound.OrganizationId,
                bound.Platform, Transform(uid).GridUid, holder,
                out var canRecharge, out var canRepair);

            // Grant the missing SRD-eye state. Without RepairDataEye, Mono's
            // client-side ghost renderer explicitly clears ALL repair previews.
            // A networked ShipRepairTool component alone is not sufficient.
            UpdateRepairEye(bound, canRepair ? holder : null);

            if (canRepair)
            {
                if (!HasComp<ShipRepairToolComponent>(uid))
                {
                    var repair = EnsureComp<ShipRepairToolComponent>(uid);
                    ConfigureRepair(repair);
                    Dirty(uid, repair);
                }
            }
            else if (HasComp<ShipRepairToolComponent>(uid))
            {
                RemComp<ShipRepairToolComponent>(uid);
            }

            if (!canRecharge)
            {
                // No off-site regeneration, and no stored charging backlog.
                bound.NextRechargeAt = now + RechargePeriod;
                continue;
            }

            if (now < bound.NextRechargeAt)
                continue;

            // Add at most one charge per completed interval, up to a small cap.
            // The existing ShipRepairTool consumes these charges for real repairs.
            _charges.AddCharges((uid, charges), 1);
            bound.NextRechargeAt = now + RechargePeriod;
        }
    }


    /// <summary>
    /// The built-in scanner only emits its eye event during an actual equip.
    /// When a component is dynamically enabled on a device already in a hand,
    /// no such equip event happens, so create the identical eye state ourselves.
    /// The native SRD equipment handler continues to maintain its own count.
    /// </summary>
    private void UpdateRepairEye(OrganizationConstructionWorkToolComponent tool, EntityUid? holder)
    {
        if (tool.RepairEyeHolder == holder)
            return;

        if (tool.RepairEyeHolder is { } oldHolder)
            RemoveRepairEye(oldHolder);
        tool.RepairEyeHolder = null;

        if (holder is not { } user || TerminatingOrDeleted(user))
            return;
        var eye = EnsureComp<RepairDataEyeComponent>(user);
        var first = eye.Count <= 0;
        eye.Count++;
        tool.RepairEyeHolder = user;
        if (first)
            _eyes.RefreshVisibilityMask(user);
    }

    private void RemoveRepairEye(EntityUid user)
    {
        if (TerminatingOrDeleted(user) || !TryComp<RepairDataEyeComponent>(user, out var eye))
            return;
        eye.Count = Math.Max(0, eye.Count - 1);
        if (eye.Count > 0)
            return;
        RemComp<RepairDataEyeComponent>(user);
        _eyes.RefreshVisibilityMask(user);
    }

    // If Mono itself handled an equip while ShipRepairTool was already present,
    // it increments/decrements the counter. Only record/clear our ownership,
    // otherwise the worker may acquire permanent see-through-floor vision.
    private void OnWorkToolHandEquipped(Entity<OrganizationConstructionWorkToolComponent> ent,
        ref GotEquippedHandEvent args)
    {
        if (HasComp<ShipRepairToolComponent>(ent.Owner))
            ent.Comp.RepairEyeHolder = args.User;
    }

    private void OnWorkToolHandUnequipped(Entity<OrganizationConstructionWorkToolComponent> ent,
        ref GotUnequippedHandEvent args)
    {
        // On unequip the built-in Mono SRD hook already decrements the eye.
        if (HasComp<ShipRepairToolComponent>(ent.Owner))
            ent.Comp.RepairEyeHolder = null;
    }

    private void OnWorkToolEquipped(Entity<OrganizationConstructionWorkToolComponent> ent,
        ref GotEquippedEvent args)
    {
        if (HasComp<ShipRepairToolComponent>(ent.Owner))
            ent.Comp.RepairEyeHolder = args.Equipee;
    }

    private void OnWorkToolUnequipped(Entity<OrganizationConstructionWorkToolComponent> ent,
        ref GotUnequippedEvent args)
    {
        if (HasComp<ShipRepairToolComponent>(ent.Owner))
            ent.Comp.RepairEyeHolder = null;
    }

    private void OnWorkToolShutdown(Entity<OrganizationConstructionWorkToolComponent> ent,
        ref ComponentShutdown args)
    {
        if (ent.Comp.RepairEyeHolder is not { } user)
            return;
        ent.Comp.RepairEyeHolder = null;
        RemoveRepairEye(user);
    }

    private static void ConfigureRepair(ShipRepairToolComponent repair)
    {
        repair.EnableTileRepair = true;
        repair.EnableEntityRepair = true;
        repair.TileRepairTime = 2.0f;
        repair.TileRepairCost = 2;
        repair.RepairTimeMultiplier = 1.25f;
        repair.EntitySearchRadius = 0.36f;
        repair.MultiRepairPower = 0f;
        repair.GhostRenderRadius = 6f;
    }

    private EntityUid? FindWorkToolHolder(EntityUid uid)
    {
        var current = Transform(uid).ParentUid;
        for (var i = 0; i < 12 && current.Valid && !TerminatingOrDeleted(current); i++)
        {
            if (HasComp<MobStateComponent>(current))
                return current;
            var next = Transform(current).ParentUid;
            if (next == current)
                break;
            current = next;
        }
        return null;
    }
}
