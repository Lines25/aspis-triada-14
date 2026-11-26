using System;
using System.Linq;
using System.Collections.Generic;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Only the server-side AgriculturalFlightSystem calls this AFTER a physically
/// loaded herbicide flask and a measured 80%+ flight over the designated plot.
/// No BUI, cargo name, client-supplied percentage or unverified bank credit
/// can complete an organization quota. Reuses the existing treasury settlement.
/// </summary>
public sealed partial class OrganizationSystem
{
    private readonly HashSet<string> _aspisAgroBonusesCredited = new(StringComparer.Ordinal);
    private const int AspisAgroPayoutWeight = 8;
    private const int AspisAgroInfluenceBonusBp = 35; // +0.35% subject to sector cap
    private const int AspisAgroIncomeBonusPercent = 5;

    /// <summary>
    /// The physical sprayer's anchored status, actual dose, FTL air map and
    /// claimant are checked by AgriculturalFlightSystem before this call.
    /// It is explicitly ONE flask, not a fabricated 96-cell crop proof.
    /// </summary>
    public bool TryCompleteAspisAgriculturalFieldQuota(EntityUid pilot, bool validatedHerbicide)
    {
        if (!_store.Available || !validatedHerbicide ||
            !TryGetCharacter(pilot, out var key, out _))
            return false;
        var org = FindMembership(key);
        if (org == null || org.Kind != OrganizationKind.Company ||
            org.Industry != OrganizationIndustry.Agriculture ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return false;
        ExpireSectorQuotas();
        var found = _sectorQuotaClaims.FirstOrDefault(pair =>
            pair.Value.Organization == org.Id &&
            pair.Key.Category == (int)OrganizationIndustry.Agriculture &&
            pair.Key.Slot == AspisPlanetaryAgriculturalSlot &&
            pair.Value.Assignee == key && !pair.Value.Completed &&
            _gameTiming.CurTime < pair.Value.Deadline);
        if (found.Value == null)
            return false;
        var claim = found.Value;
        var record = "aspis-agro-" + Guid.NewGuid().ToString("N");
        if (_acceptedCargoProofs.Contains(record))
            return false;
        claim.DeliveredByPost[0] = claim.Units;
        claim.VerifiedQualityTotal = claim.Units * 100;
        claim.ProcessedCargoReferences.Add(record);
        _acceptedCargoProofs.Add(record);
        claim.Completed = true;

        // The planetary expedition uses one physical dose but demands a ship,
        // planetary landing, an off-site retrieval and atmospheric operation.
        // Higher payout weight is charged to the NORMAL economic sector budget.
        var paid = TryPayVerifiedQuota(org, found.Key.Slot, found.Key.Category,
            claim.Code, Math.Max(1, claim.Units) * AspisAgroPayoutWeight);
        claim.SettlementPending = !paid;
        if (paid)
            TryAwardAspisAgroBonus(claim.Code, org);
        NotifyQuotaAssignee(claim, claim.SettlementPending
            ? $"Планетарну квоту {claim.Code} виконано! Виплата очікує вільного попиту."
            : $"Планетарну квоту {claim.Code} виконано! Підвищену винагороду зараховано.",
            paid);
        return true;
    }

    private void TryAwardAspisAgroBonus(string code, Organization org)
    {
        if (_aspisAgroBonusesCredited.Contains(code) || !_store.Available)
            return;
        var free = Math.Max(0, MarketCapacityBp -
            _organizations.Values.Sum(o => o.SectorInfluenceBp));
        if (!Commit(() =>
        {
            org.SectorInfluenceBp += Math.Min(free, AspisAgroInfluenceBonusBp);
            org.QuotaIncomeBonusPercent = Math.Min(
                OrganizationQuotaRules.MaximumIncomeBonusPercent,
                org.QuotaIncomeBonusPercent + AspisAgroIncomeBonusPercent);
            org.QuotaBonusCycles = Math.Max(org.QuotaBonusCycles, 5);
        }))
            return;
        _aspisAgroBonusesCredited.Add(code);
    }

    /// <summary>Issue once even when the base payout settles on a later cycle.</summary>
    public void ProcessAspisAgroDeferredBonuses()
    {
        foreach (var pair in _sectorQuotaClaims)
        {
            if (pair.Key.Kind != OrganizationKind.Company ||
                pair.Key.Category != (int)OrganizationIndustry.Agriculture ||
                pair.Key.Slot != AspisPlanetaryAgriculturalSlot ||
                !pair.Value.Completed ||
                !_organizations.TryGetValue(pair.Value.Organization, out var org))
                continue;
            // Give the economic system a chance to settle the SAME higher
            // planetary payout weight when demand was temporarily exhausted.
            if (pair.Value.SettlementPending &&
                TryPayVerifiedQuota(org, pair.Key.Slot, pair.Key.Category,
                    pair.Value.Code,
                    Math.Max(1, pair.Value.Units) * AspisAgroPayoutWeight))
            {
                pair.Value.SettlementPending = false;
                NotifyQuotaAssignee(pair.Value,
                    $"Підвищену винагороду за квоту {pair.Value.Code} виплачено!", true);
            }
            if (!pair.Value.SettlementPending &&
                !_aspisAgroBonusesCredited.Contains(pair.Value.Code))
                TryAwardAspisAgroBonus(pair.Value.Code, org);
        }
    }

}
