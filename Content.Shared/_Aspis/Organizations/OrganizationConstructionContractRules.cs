using System;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Stable contract categories. A shipyard contract must not be handled as an
/// IndustrialExport cargo sale. These are balance PROPOSALS, not live rewards.
/// </summary>
public enum OrganizationConstructionContractKind : byte
{
    MinorHullRepair,
    CompartmentReconstruction,
    CapitalShipRepair,
    NewShipConstruction
}

public static class OrganizationConstructionContractRules
{
    public const int MaximumYardsPerRound = 8; // Live platforms; completed contracts reuse their existing post.

    public static OrganizationConstructionContractKind KindForSlot(int slot) =>
        (OrganizationConstructionContractKind)Math.Clamp(slot, 0, 3);

    public static string Title(int slot) => KindForSlot(slot) switch
    {
        OrganizationConstructionContractKind.MinorHullRepair => "Ремонт службового катера",
        OrganizationConstructionContractKind.CompartmentReconstruction => "Реконструкція транспортного шатла",
        OrganizationConstructionContractKind.CapitalShipRepair => "Капітальний ремонт спеціалізованого корабля",
        _ => "Будівництво великого корабля"
    };

    public static int DeadlineMinutes(int slot) => KindForSlot(slot) switch
    {
        OrganizationConstructionContractKind.MinorHullRepair => 60,
        OrganizationConstructionContractKind.CompartmentReconstruction => 100,
        OrganizationConstructionContractKind.CapitalShipRepair => 165,
        _ => 240
    };

    // One hull job per contract. High-grade jobs replace much more of the hull
    // and are deliberately more valuable than an ordinary cargo export.
    // Consumed by the existing market-demand and daily-revenue cap, not a free grant.
    public static int RewardEffortPercent(int slot) => KindForSlot(slot) switch
    {
        OrganizationConstructionContractKind.MinorHullRepair => 550,
        OrganizationConstructionContractKind.CompartmentReconstruction => 1100,
        OrganizationConstructionContractKind.CapitalShipRepair => 2200,
        _ => 4500
    };

    // 100 basis points = 1 percentage point; subject to sector market capacity.
    public const int MaximumSectorInfluenceBasisPointsPerContract = 110;
    public static int ProposedSectorInfluenceBasisPoints(int slot) =>
        KindForSlot(slot) switch
        {
            OrganizationConstructionContractKind.MinorHullRepair => 8,
            OrganizationConstructionContractKind.CompartmentReconstruction => 20,
            OrganizationConstructionContractKind.CapitalShipRepair => 60,
            _ => 110
        };
}
