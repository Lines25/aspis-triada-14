using System;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace Content.Server._Aspis.Organizations;

[RegisterComponent]
public sealed partial class AspisAgroQuotaItemComponent : Component
{
    [DataField] public string ClaimCode = string.Empty;
}

public sealed partial class OrganizationSystem
{
    [Dependency] private AspisPlanetaryAirspaceSystem _aspisPlanetaryRoutes = default!;
    public const int AspisPlanetaryAgriculturalSlot = 4; // fifth, never slots 0..3

    /// <summary>Do not give gear for the four ordinary agricultural exports.</summary>
    private bool TryIssueAspisAgroQuotaEquipment(EntityUid actor, out string error)
    {
        error = string.Empty;
        if (!TryGetCharacter(actor, out var key, out _))
            return true;
        var claim = _sectorQuotaClaims.FirstOrDefault(q =>
            q.Key.Kind == OrganizationKind.Company &&
            q.Key.Category == (int)OrganizationIndustry.Agriculture &&
            q.Key.Slot == AspisPlanetaryAgriculturalSlot &&
            q.Value.Assignee == key && !q.Value.Completed).Value;
        if (claim is null)
            return true;
        if (!_prototypes.HasIndex<EntityPrototype>("AspisAgroSprayerFlatpack"))
        {
            error = "Флетпак розпилювача не зареєстровано.";
            return false;
        }
        if (!_aspisPlanetaryRoutes.TryIssueTestRoute(actor, out error, out var disk))
            return false;
        var flatpack = Spawn("AspisAgroSprayerFlatpack", Transform(actor).Coordinates);
        EnsureComp<AspisAgroQuotaItemComponent>(disk).ClaimCode = claim.Code;
        EnsureComp<AspisAgroQuotaItemComponent>(flatpack).ClaimCode = claim.Code;
        _hands.TryPickupAnyHand(actor, flatpack);
        // Parachutes are not a free quota reward. They are sold via Cargo.
        return true;
    }

    /// <summary>Server-authoritative current claim for a company participant.</summary>
    public bool TryGetActiveAspisAgroQuotaCode(EntityUid actor, out string code)
    {
        code = string.Empty;
        if (!_store.Available || !TryGetCharacter(actor, out var key, out _))
            return false;
        var org = FindMembership(key);
        if (org is null || org.Kind != OrganizationKind.Company ||
            org.Industry != OrganizationIndustry.Agriculture ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return false;
        var claim = _sectorQuotaClaims.FirstOrDefault(pair =>
            pair.Key.Kind == OrganizationKind.Company &&
            pair.Key.Category == (int)OrganizationIndustry.Agriculture &&
            pair.Key.Slot == AspisPlanetaryAgriculturalSlot &&
            pair.Value.Organization == org.Id && !pair.Value.Completed &&
            _gameTiming.CurTime < pair.Value.Deadline).Value;
        if (claim is null) return false;
        code = claim.Code;
        return true;
    }

    public bool IsAspisAgroQuotaActive(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        return _sectorQuotaClaims.Values.Any(c => c.Code == code &&
            !c.Completed && _gameTiming.CurTime < c.Deadline);
    }
}

/// <summary>
/// Round-local cleanup. After a claim is released, completed or expired,
/// its tagged disk/flatpack is deleted even if dropped or inside a backpack.
/// No blind deletion of player equipment or organization registry records.
/// </summary>
public sealed class AspisAgroQuotaItemCleanupSystem : EntitySystem
{
    [Dependency] private OrganizationSystem _organizations = default!;
    private float _elapsed;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 0.3f) return;
        _elapsed = 0f;
        var items = EntityQueryEnumerator<AspisAgroQuotaItemComponent>();
        while (items.MoveNext(out var uid, out var tag))
        {
            if (!_organizations.IsAspisAgroQuotaActive(tag.ClaimCode) && !Deleted(uid))
                QueueDel(uid);
        }
    }
}
