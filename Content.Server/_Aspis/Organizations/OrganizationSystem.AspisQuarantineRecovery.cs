using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Optional, one-time quarantine salvage reward after the agricultural field
/// task has been verified. No extra charge is issued by clicking PDA buttons.
/// </summary>
public sealed partial class OrganizationSystem
{
    private readonly HashSet<string> _aspisQuarantinePaid = new(StringComparer.Ordinal);
    private const int AspisSampleCorporateGrant = 12_000;
    private const int AspisSamplePlayerBonus = 2_000;
    private const int AspisSampleSectorInfluenceBp = 18;

    /// <summary>
    /// Cargo's physical sale event is the only payout path for miracle flora.
    /// There is no actor requirement: the original agricultural company owns
    /// the completed claim, not whoever happens to sell the specimen.
    /// </summary>
    public bool TryAwardAspisQuarantineSampleCargo(string claimCode)
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(claimCode) ||
            _aspisQuarantinePaid.Contains(claimCode))
            return false;
        var record = _sectorQuotaClaims.FirstOrDefault(pair =>
            pair.Value.Code == claimCode && pair.Value.Completed &&
            pair.Key.Kind == OrganizationKind.Company &&
            pair.Key.Category == (int) OrganizationIndustry.Agriculture &&
            pair.Key.Slot == AspisPlanetaryAgriculturalSlot);
        if (record.Value is null ||
            !_organizations.TryGetValue(record.Value.Organization, out var org) ||
            org.Kind != OrganizationKind.Company ||
            org.Industry != OrganizationIndustry.Agriculture ||
            org.InsolvencyState != OrganizationInsolvencyState.Active ||
            org.Treasury > MaxTreasury - AspisSampleCorporateGrant)
            return false;
        var room = Math.Max(0, MarketCapacityBp -
            _organizations.Values.Sum(o => o.SectorInfluenceBp));
        if (!Commit(() =>
        {
            org.Treasury += AspisSampleCorporateGrant;
            org.SectorInfluenceBp += Math.Min(room, AspisSampleSectorInfluenceBp);
            org.CompanyExperience += 10;
        }))
            return false;
        _aspisQuarantinePaid.Add(claimCode);
        return true;
    }

    public bool TryAwardAspisQuarantineSample(EntityUid actor, string claimCode)
    {
        if (!_store.Available || string.IsNullOrWhiteSpace(claimCode) ||
            _aspisQuarantinePaid.Contains(claimCode) ||
            !TryGetCharacter(actor, out var key, out _))
            return false;
        var org = FindMembership(key);
        if (org is null || org.Kind != OrganizationKind.Company ||
            org.Industry != OrganizationIndustry.Agriculture ||
            org.InsolvencyState != OrganizationInsolvencyState.Active ||
            org.Treasury > MaxTreasury - AspisSampleCorporateGrant ||
            !_sectorQuotaClaims.Any(pair => pair.Value.Code == claimCode &&
                pair.Value.Organization == org.Id && pair.Value.Completed &&
                pair.Key.Category == (int) OrganizationIndustry.Agriculture &&
                pair.Key.Slot == AspisPlanetaryAgriculturalSlot))
            return false;
        var room = Math.Max(0, MarketCapacityBp -
            _organizations.Values.Sum(o => o.SectorInfluenceBp));
        if (!Commit(() =>
        {
            org.Treasury += AspisSampleCorporateGrant;
            org.SectorInfluenceBp += Math.Min(room, AspisSampleSectorInfluenceBp);
            org.CompanyExperience += 10;
        }))
            return false;
        // A one-time personal retrieval premium is separate from organization
        // treasury and does not alter the core quota's economic settlement.
        _aspisQuarantinePaid.Add(claimCode);
        if (!_bank.TryBankDeposit(actor, AspisSamplePlayerBonus))
            Logger.Warning($"Aspis Agro: could not deposit individual quarantine premium for {claimCode}.");
        return true;
    }
}
