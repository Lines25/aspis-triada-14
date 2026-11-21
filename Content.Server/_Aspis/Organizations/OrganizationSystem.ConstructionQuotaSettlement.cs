using System;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Q2.2C.1 — construction quota is verified by SERVER state of the real hull,
/// never by PDA/UI requests, cargo sales, client-provided references or an admin
/// test yard. The site stays in ordinary shared sector space throughout.
/// </summary>
public sealed partial class OrganizationSystem
{
    private static readonly TimeSpan ConstructionInspectionHold = TimeSpan.FromSeconds(20);

    private static bool IsConstructionQuota(Organization org) =>
        org.Kind == OrganizationKind.Company &&
        org.Industry == OrganizationIndustry.Construction;

    private bool TryFindConstructionContract(SectorQuotaClaim claim,
        out ConstructionYardRecord? record)
    {
        record = null;
        if (!_constructionYards.TryGetValue(claim.FactionAssignmentId, out var yard) ||
            yard.Closed ||
            yard.OrganizationId != claim.Organization ||
            yard.AssignmentId != claim.FactionAssignmentId ||
            !string.Equals(yard.QuotaCode, claim.Code, StringComparison.Ordinal) ||
            yard.DifficultySlot != claim.DifficultySlot)
            return false;

        if (TerminatingOrDeleted(yard.WorkPlatform) ||
            TerminatingOrDeleted(yard.ContractHull) ||
            !TryComp<OrganizationConstructionYardComponent>(yard.WorkPlatform, out var stationTag) ||
            !TryComp<OrganizationConstructionYardComponent>(yard.ContractHull, out var hullTag) ||
            !stationTag.IsWorkPlatform || hullTag.IsWorkPlatform ||
            stationTag.ContractGrid != yard.ContractHull ||
            hullTag.ContractGrid != yard.ContractHull ||
            stationTag.OrganizationId != claim.Organization ||
            hullTag.OrganizationId != claim.Organization ||
            stationTag.AssignmentId != claim.FactionAssignmentId ||
            hullTag.AssignmentId != claim.FactionAssignmentId ||
            stationTag.QuotaCode != claim.Code || hullTag.QuotaCode != claim.Code ||
            Transform(yard.WorkPlatform).MapID != yard.MapId ||
            Transform(yard.ContractHull).MapID != yard.MapId)
            return false;

        record = yard;
        return true;
    }

    /// <summary>
    /// Called by the existing 30-second quota sweep and also whenever an
    /// organization quota board is refreshed. A 20-second continuously finished
    /// physical snapshot is required before an independent economic settlement.
    /// </summary>
    private void InspectConstructionQuotaClaims()
    {
        var now = _gameTiming.CurTime;
        foreach (var (key, claim) in _sectorQuotaClaims)
        {
            if (key.Kind != OrganizationKind.Company ||
                claim.Completed || now >= claim.Deadline ||
                !_organizations.TryGetValue(claim.Organization, out var org) ||
                !IsConstructionQuota(org))
                continue;

            if (!TryFindConstructionContract(claim, out var yard) || yard == null)
            {
                claim.LastInspectionReport = "Контрактний корпус недоступний або має невірну прив’язку.";
                claim.LastInspectionQuality = 0;
                continue;
            }

            var complete = TryInspectConstructionHull(yard.ContractHull,
                out var present, out var required, out var report);
            var percent = required > 0
                ? Math.Clamp(present * 100 / required, 0, 100) : 0;
            claim.LastInspectionQuality = percent;
            var location = $"{yard.DesignName}. Верф: X={yard.WorldPosition.X:0}, Y={yard.WorldPosition.Y:0}. ";
            claim.LastInspectionReport = location + report;

            if (!complete || required <= 0)
            {
                // Restoring only most of the tiles never grants even a partial payout.
                yard.CompleteSince = null;
                continue;
            }

            if (yard.CompleteSince == null)
            {
                yard.CompleteSince = now;
                claim.LastInspectionReport = location +
                    "Корпус відновлено. Триває контрольна інспекція (20 с).";
                continue;
            }

            if (now - yard.CompleteSince.Value < ConstructionInspectionHold)
            {
                claim.LastInspectionReport = location +
                    "Корпус відновлено. Очікується завершення контрольної інспекції.";
                continue;
            }

            // No double award even if the UI/poll calls us again immediately.
            // Both grids and their server identity have been revalidated above.
            claim.Completed = true;
            claim.DeliveredByPost[0] = 1;
            claim.VerifiedQualityTotal = 100;
            yard.Closed = true;
            claim.LastInspectionQuality = 100;
            claim.LastInspectionReport = location +
                $"Контракт прийнято: {required}/{required} еталонних конструкцій.";
            claim.SettlementPending = !TryPayVerifiedQuota(org, key.Slot,
                key.Category, claim.Code, claim.Units);
            NotifyQuotaAssignee(claim, claim.SettlementPending
                ? $"Корабель за квотою {claim.Code} прийнято. Виплата очікує розрахунку."
                : $"Корабель за квотою {claim.Code} прийнято. Винагороду зараховано.",
                !claim.SettlementPending);
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"Construction quota hull verified: {claim.Code}, org={org.Id}, " +
                $"hull={yard.ContractHull}, matched={required}, pending={claim.SettlementPending}");
        }
    }
}
