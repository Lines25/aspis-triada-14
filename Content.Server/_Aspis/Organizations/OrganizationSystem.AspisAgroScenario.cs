using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// The user's Quotas.cs has shipyard/frontier changes unavailable in older
/// snapshots, so this is an additive extension (no full Quotas.cs replacement).
/// Slots 0..3 remain genuine regular agricultural cargo exports. Slot 4 is
/// the sole planetary herbicide expedition.
/// </summary>
public sealed partial class OrganizationSystem
{
    private readonly Dictionary<string, int> _aspisAgroStages = new(StringComparer.Ordinal);

    // Verified crop prototypes are declared in the installed hydroponics catalog.
    // Slot 4 is the distinct planetary mission, never an agricultural Cargo sale.
    private static readonly (string Title, string Produce, string Seeds, string Label)[] AspisAgriculturalCargo =
    [
        ("Експорт пшениці", "WheatBushel", "WheatSeeds", "пшениця"),
        ("Картопляна поставка", "FoodPotato", "PotatoSeeds", "картопля"),
        ("Контракт томатів", "FoodTomato", "TomatoSeeds", "томати"),
        ("Великий кукурудзяний конвой", "FoodCorn", "CornSeeds", "кукурудза")
    ];

    static OrganizationSystem()
    {
        // Replace only the legacy generic agriculture export scenario.
        // Preserve construction yards, frontier delivery, other industries and factions.
        QuotaScenarioCatalog = [
            .. QuotaScenarioCatalog.Where(s => s.Id != "company.agriculture.export"),
            new QuotaScenarioDefinition("company.agriculture.wheat", OrganizationKind.Company,
                OrganizationQuotaWorkflow.AgriculturalExport, QuotaProofKind.VerifiedExport,
                0, 0, 16, 3, 25, 210, RewardPercent: 100),
            new QuotaScenarioDefinition("company.agriculture.potato", OrganizationKind.Company,
                OrganizationQuotaWorkflow.AgriculturalExport, QuotaProofKind.VerifiedExport,
                1, 1, 20, 4, 28, 210, RewardPercent: 115),
            new QuotaScenarioDefinition("company.agriculture.tomato", OrganizationKind.Company,
                OrganizationQuotaWorkflow.AgriculturalExport, QuotaProofKind.VerifiedExport,
                2, 2, 24, 5, 30, 240, RewardPercent: 135),
            new QuotaScenarioDefinition("company.agriculture.corn", OrganizationKind.Company,
                OrganizationQuotaWorkflow.AgriculturalExport, QuotaProofKind.VerifiedExport,
                3, 3, 30, 6, 35, 270, RewardPercent: 160),
            new QuotaScenarioDefinition("company.agriculture.planetary", OrganizationKind.Company,
                OrganizationQuotaWorkflow.AgriculturalExport, QuotaProofKind.VerifiedExport,
                AspisPlanetaryAgriculturalSlot, AspisPlanetaryAgriculturalSlot,
                10, 1, 25, 180, RewardPercent: 320)
        ];
    }

    private static bool AspisAgriculturalCargoMatches(string id, int slot)
    {
        if (slot is < 0 or >= 4)
            return false;
        var cargo = AspisAgriculturalCargo[slot];
        return string.Equals(id, cargo.Produce, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(id, cargo.Seeds, StringComparison.OrdinalIgnoreCase);
    }

    private static string AspisAgriculturalCargoTitle(int slot) =>
        slot is >= 0 and < 4 ? AspisAgriculturalCargo[slot].Title : "Планетарна агроекспедиція";

    private string AspisAgriculturalCargoDescription(int slot, int units, int destination)
    {
        if (slot is < 0 or >= 4)
            return string.Empty;
        var crop = AspisAgriculturalCargo[slot];
        var target = destination is >= 0 and < 4 &&
            _quotaCargoSystem.TryGetQuotaTradeDestination(destination, out _)
            ? $"поста {(char)('A' + destination)}"
            : "доступного торговельного поста Cargo";
        return $"Потрібна культура: {crop.Label} (урожай або насіння тільки цієї культури). " +
               $"Підготуйте {units} ящиків, щонайменше " +
               $"{OrganizationQuotaRules.ExportItemsPerCrate(OrganizationQuotaWorkflow.AgriculturalExport, slot)} " +
               $"одиниць у кожному. Прикріпіть накладну цього контракту; " +
               $"продайте промарковані ящики через Cargo на {target}. " +
               "Інші рослини в залік не йдуть.";
    }

    private void PrepareAspisAgriculturalOffers()
    {
        // Repair only the four round-local export offers; an already claimed
        // contract keeps its immutable frozen offer and proof references.
        for (var slot = 0; slot < AspisPlanetaryAgriculturalSlot; slot++)
        {
            var key = (OrganizationKind.Company, (int)OrganizationIndustry.Agriculture, slot);
            var current = GetOrCreateQuotaOffer(key.Item1, key.Item2, key.Item3);
            if (current is null)
                continue;
            var count = current.Units == 1 ? OrganizationQuotaRules.Units(slot) : current.Units;
            var availablePosts = Enumerable.Range(0, 4)
                .Where(post => _quotaCargoSystem.TryGetQuotaTradeDestination(post, out _))
                .ToArray();
            var destination = availablePosts.Length > 0 ? availablePosts[slot % availablePosts.Length] : 0;
            var alreadyValid = current.Units == count &&
                current.PostTargets[destination] == count &&
                current.PostTargets.Sum() == count;
            if (alreadyValid)
                continue;
            var scenario = current.Scenario;
            var work = QuotaWorkEstimateMinutes(scenario, count, slot, current.PharmaceuticalVariant);
            var deadline = Math.Max(current.Minutes, work + scenario.SafetyMinutes);
            deadline = Math.Min(deadline, scenario.MaxContractMinutes);
            var postTargets = new int[4];
            postTargets[destination] = count;
            _sectorQuotaOffers[key] = new SectorQuotaOffer(count, deadline, postTargets,
                current.PharmaceuticalVariant, scenario, work, current.ScenarioVariant);
        }
    }

    private OrganizationQuotaEntry[] DecorateAspisAgriculturalQuotas(OrganizationQuotaEntry[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Workflow != OrganizationQuotaWorkflow.AgriculturalExport)
                continue;
            if (entry.Slot == AspisPlanetaryAgriculturalSlot)
            {
                entry.Title = "Планетарна агроекспедиція: гербіцид";
                entry.Description = "П'яте, окреме планетарне завдання. Отримайте диск і один флетпак, " +
                    "приземліться, заберіть одну марковану колбу з агростанції, " +
                    "заправте один модуль на шатлі, злетіть і запустіть зелений аерозоль. " +
                    "Підвищена фінансова винагорода, секторний вплив і бонус прибутку.";
                entry.FactionSteps = BuildAspisAgroSteps(entry.Code);
                entry.EstimatedReward = Math.Min(24_000,
                    Math.Max(0, entry.EstimatedReward) * 8);
                entry.SectorInfluenceBp += AspisAgroInfluenceBonusBp;
                entry.IncomeBonusPercent = Math.Min(
                    OrganizationQuotaRules.MaximumIncomeBonusPercent,
                    entry.IncomeBonusPercent + AspisAgroIncomeBonusPercent);
            }
            else if (entry.Slot is >= 0 and < AspisPlanetaryAgriculturalSlot)
            {
                entry.Title = AspisAgriculturalCargoTitle(entry.Slot);
                var destination = Array.FindIndex(entry.PostTargets, count => count > 0);
                entry.Description = AspisAgriculturalCargoDescription(
                    entry.Slot, entry.Units, destination);
            }
        }
        // All other industries and user-authored frontier/shipyard cases remain untouched.
        return entries;
    }

    public void MarkAspisAgroProgress(EntityUid actor, int stage)
    {
        if (stage is < 1 or > 3 || !TryGetCharacter(actor, out var character, out _)) return;
        var member = FindMembership(character);
        if (member == null || member.Kind != OrganizationKind.Company ||
            member.Industry != OrganizationIndustry.Agriculture) return;
        var active = _sectorQuotaClaims.FirstOrDefault(x =>
            x.Key.Kind == OrganizationKind.Company &&
            x.Key.Category == (int)OrganizationIndustry.Agriculture &&
            x.Key.Slot == AspisPlanetaryAgriculturalSlot &&
            x.Value.Organization == member.Id && !x.Value.Completed &&
            _gameTiming.CurTime < x.Value.Deadline);
        if (active.Value == null) return;
        var code = active.Value.Code;
        _aspisAgroStages[code] = Math.Max(_aspisAgroStages.GetValueOrDefault(code), stage);
    }

    private OrganizationFactionStepEntry[] BuildAspisAgroSteps(string code)
    {
        var step = _aspisAgroStages.GetValueOrDefault(code);
        var done = _sectorQuotaClaims.Values.Any(x => x.Code == code && x.Completed);
        return
        [
            new() { Title = "1. Отримати обладнання", Details = "Прийміть завдання, візьміть диск і флетпак розпилювача.", Completed = !string.IsNullOrEmpty(code) },
            new() { Title = "2. Забрати гербіцид", Details = "На планеті знайдіть агростанцію й натисніть на резервуар. Лише одна колба.", Completed = step >= 2 || done },
            new() { Title = "3. Заправити шатл", Details = "Розгорніть один флетпак на шатлі та застосуйте колбу до розпилювача.", Completed = step >= 3 || done },
            new() { Title = "4. Розпилити", Details = "Злетіть у повітряний простір, активуйте одну дозу; зелений аерозоль має з’явитися за шатлом і над планетою.", Completed = done }
        ];
    }
}
