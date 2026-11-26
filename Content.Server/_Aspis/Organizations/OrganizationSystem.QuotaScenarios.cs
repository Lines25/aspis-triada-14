using System;
using System.Collections.Generic;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Q1: server-owned quota scenario catalog. A scenario selects a physical proof
/// pipeline and a workload profile; the difficulty slot remains independent.
/// Adding a new scenario without a server proof pipeline is deliberately forbidden.
/// All durations are first-pass balance estimates, not measured play times.
/// </summary>
public sealed partial class OrganizationSystem
{
    private enum QuotaProofKind : byte
    {
        CargoRoute,
        LongHaulRoute,
        VerifiedExport,
        FactionRelay,
        FactionSurvey,
        // Distinct physical proof: the server compares the real docked ship to its repair snapshot.
        ConstructionHull
    }

    private sealed record QuotaScenarioDefinition(
        string Id,
        OrganizationKind Kind,
        OrganizationQuotaWorkflow Workflow,
        QuotaProofKind Proof,
        int MinSlot,
        int MaxSlot,
        int SetupMinutes,
        int MinutesPerUnit,
        int SafetyMinutes,
        int MaxContractMinutes,
        int RewardPercent = 100,
        int MinDestinations = 0,
        int MaxDestinations = 0);

    // Only existing, server-verifiable objectives are enabled in Q1.
    // Security/Services have no physical proof integration yet: don't create fake quotas.
    // Multiple delivery scenarios already differ in the real A-D delivery layout.
    private static readonly QuotaScenarioDefinition[] QuotaScenarioCatalog =
    [
        new("company.delivery.direct", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.CargoRoute, 0, 2, 14, 2, 20, 180, 95, 1, 1),
        new("company.delivery.multistop", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.CargoRoute, 0, 2, 17, 3, 22, 210, 110, 2, 4),
        new("company.delivery.priority", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.CargoRoute, 0, 1, 12, 2, 17, 150, 108, 1, 1),
        new("company.delivery.circuit", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.CargoRoute, 1, 2, 22, 3, 24, 230, 122, 3, 4),
        new("company.delivery.heavy", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.CargoRoute, 2, 2, 28, 4, 28, 240, 130, 1, 2),
        // Authenticated manifests, without a manual scanner step.
        new("company.delivery.registered", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.CargoRoute, 1, 2, 24, 4, 30, 240, 118, 2, 3),
        // Protected, physical 35-minute outbound and inbound legs; the route
        // controller, not a cargo sale or FTL cooldown, provides the proof.
        new("company.delivery.frontier", OrganizationKind.Company, OrganizationQuotaWorkflow.CargoDelivery,
            QuotaProofKind.LongHaulRoute, 3, 3, 100, 0, 100, 240, 240, 1, 4),
        new("company.research.export", OrganizationKind.Company, OrganizationQuotaWorkflow.ResearchExport,
            QuotaProofKind.VerifiedExport, 0, 3, 21, 12, 25, 210),
        new("company.research.inspected", OrganizationKind.Company, OrganizationQuotaWorkflow.ResearchExport,
            QuotaProofKind.VerifiedExport, 2, 3, 30, 15, 30, 240, 115),
        // Two physically different data carriers in each sealed crate.
        new("company.research.dualarchive", OrganizationKind.Company, OrganizationQuotaWorkflow.ResearchExport,
            QuotaProofKind.VerifiedExport, 2, 3, 30, 15, 30, 240, 123),
        new("company.trade.export", OrganizationKind.Company, OrganizationQuotaWorkflow.TradeExport,
            QuotaProofKind.VerifiedExport, 0, 3, 15, 2, 20, 180),
        new("company.agriculture.export", OrganizationKind.Company, OrganizationQuotaWorkflow.AgriculturalExport,
            QuotaProofKind.VerifiedExport, 0, 3, 16, 3, 25, 210),
        // Each crate combines distinct seed items and fresh produce, not just
        // an arbitrary count of one farmable component.
        new("company.agriculture.seedreserve", OrganizationKind.Company, OrganizationQuotaWorkflow.AgriculturalExport,
            QuotaProofKind.VerifiedExport, 1, 3, 24, 3, 28, 240, 118),
        new("company.mining.export", OrganizationKind.Company, OrganizationQuotaWorkflow.MiningExport,
            QuotaProofKind.VerifiedExport, 0, 3, 19, 3, 28, 210),
        new("company.mining.inspected", OrganizationKind.Company, OrganizationQuotaWorkflow.MiningExport,
            QuotaProofKind.VerifiedExport, 1, 3, 25, 4, 30, 240, 112),
        // A real multi-ore expedition shipment: every physical crate must
        // contain different raw ore stack kinds in non-trivial quantities.
        // This is enforced during Cargo preflight AND after sale confirmation.
        new("company.mining.assorted", OrganizationKind.Company, OrganizationQuotaWorkflow.MiningExport,
            QuotaProofKind.VerifiedExport, 1, 3, 22, 3, 30, 240, 120),
        new("company.industry.export", OrganizationKind.Company, OrganizationQuotaWorkflow.IndustrialExport,
            QuotaProofKind.VerifiedExport, 0, 3, 19, 3, 28, 210),
        new("company.industry.inspected", OrganizationKind.Company, OrganizationQuotaWorkflow.IndustrialExport,
            QuotaProofKind.VerifiedExport, 1, 3, 25, 4, 30, 240, 112),
        // One server-owned repair grid, four bounded workload/expiration bands.
        // Work + safety fits exactly inside the chosen difficulty deadline.
        new("company.construction.repair", OrganizationKind.Company, OrganizationQuotaWorkflow.ConstructionYard,
            QuotaProofKind.ConstructionHull, 0, 0, 20, 24, 10, 60),
        new("company.construction.sections", OrganizationKind.Company, OrganizationQuotaWorkflow.ConstructionYard,
            QuotaProofKind.ConstructionHull, 1, 1, 28, 50, 20, 100),
        new("company.construction.overhaul", OrganizationKind.Company, OrganizationQuotaWorkflow.ConstructionYard,
            QuotaProofKind.ConstructionHull, 2, 2, 35, 95, 35, 165),
        new("company.construction.shipbuilding", OrganizationKind.Company, OrganizationQuotaWorkflow.ConstructionYard,
            QuotaProofKind.ConstructionHull, 3, 3, 55, 150, 35, 240),
        new("company.pharma.firstaid", OrganizationKind.Company, OrganizationQuotaWorkflow.PharmaceuticalExport,
            QuotaProofKind.VerifiedExport, 0, 0, 24, 12, 25, 180),
        new("company.pharma.syringes", OrganizationKind.Company, OrganizationQuotaWorkflow.PharmaceuticalExport,
            QuotaProofKind.VerifiedExport, 1, 1, 28, 18, 25, 210),
        new("company.pharma.pills", OrganizationKind.Company, OrganizationQuotaWorkflow.PharmaceuticalExport,
            QuotaProofKind.VerifiedExport, 2, 2, 32, 21, 25, 230),
        new("company.pharma.specialkits", OrganizationKind.Company, OrganizationQuotaWorkflow.PharmaceuticalExport,
            QuotaProofKind.VerifiedExport, 3, 3, 40, 26, 30, 240),
        new("company.pharma.inspected", OrganizationKind.Company, OrganizationQuotaWorkflow.PharmaceuticalExport,
            QuotaProofKind.VerifiedExport, 2, 3, 40, 26, 30, 240, 115),
        new("faction.planet.relay", OrganizationKind.Faction, OrganizationQuotaWorkflow.SectorService,
            QuotaProofKind.FactionRelay, 0, 0, 22, 6, 20, 130),
        new("faction.wreck.survey", OrganizationKind.Faction, OrganizationQuotaWorkflow.SectorService,
            QuotaProofKind.FactionSurvey, 1, 3, 25, 6, 25, 200)
    ];

    private static int QuotaWorkEstimateMinutes(QuotaScenarioDefinition scenario, int units,
        int slot, int pharmaceuticalVariant)
    {
        var extra = scenario.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport &&
            pharmaceuticalVariant >= 0
            ? OrganizationQuotaRules.PharmaceuticalExtraMinutes(pharmaceuticalVariant, slot)
            : 0;
        return checked(scenario.SetupMinutes + units * scenario.MinutesPerUnit + extra);
    }

    /// <summary>
    /// The offer is not published if its estimated work plus slack exceeds
    /// the scenario deadline cap. Never silently offer an impossible job.
    /// Scenario selection happens only once per shared category/slot offer.
    /// </summary>
    private bool TryChooseQuotaScenario(OrganizationKind kind, OrganizationQuotaWorkflow workflow,
        int slot, int units, int legacyMinutes, int pharmaceuticalVariant,
        out QuotaScenarioDefinition scenario, out int workMinutes, out int deadlineMinutes)
    {
        var eligible = new List<(QuotaScenarioDefinition Scenario, int Work, int Deadline)>();
        foreach (var candidate in QuotaScenarioCatalog)
        {
            if (candidate.Kind != kind || candidate.Workflow != workflow ||
                slot < candidate.MinSlot || slot > candidate.MaxSlot)
                continue;
            // Fail closed if this server has not loaded the physical scanner.
            if (QuotaRequiresInspection(candidate) &&
                !_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(QuotaInspectionScannerProto))
                continue;
            if (candidate.Proof == QuotaProofKind.LongHaulRoute &&
                (!_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(FrontierRouteNavigatorPrototype) ||
                 _frontierRoutesAllocatedThisRound >= MaximumFrontierRoutesPerRound))
                continue;
            var work = QuotaWorkEstimateMinutes(candidate, units, slot, pharmaceuticalVariant);
            var deadline = Math.Max(legacyMinutes, work + candidate.SafetyMinutes);
            if (work <= 0 || deadline > candidate.MaxContractMinutes)
                continue;
            eligible.Add((candidate, work, deadline));
        }

        if (eligible.Count == 0)
        {
            scenario = null!;
            workMinutes = 0;
            deadlineMinutes = 0;
            return false;
        }

        var selected = eligible[_random.Next(eligible.Count)];
        scenario = selected.Scenario;
        workMinutes = selected.Work;
        deadlineMinutes = selected.Deadline;
        return true;
    }

    private static string QuotaCompanyScenarioTitle(QuotaScenarioDefinition scenario,
        OrganizationIndustry industry, int category, int slot, int pharmaceuticalVariant)
    {
        if (scenario.Workflow == OrganizationQuotaWorkflow.AgriculturalExport)
            return $"Агроекспедиція {slot + 1}: пшенична планета";
        if (scenario.Id == "company.pharma.inspected")
            return $"Контроль якості: {OrganizationQuotaRules.PharmaceuticalTitle(slot, pharmaceuticalVariant)}";
        if (scenario.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
            return OrganizationQuotaRules.PharmaceuticalTitle(slot, pharmaceuticalVariant);
        if (scenario.Id == FrontierScenarioId)
            return "Далекий рубіж: міжсекторне постачання";
        if (scenario.Id == "company.delivery.registered")
            return "Реєстрове постачання";
        if (scenario.Id == "company.delivery.priority")
            return "Пріоритетний вантаж";
        if (scenario.Id == "company.delivery.circuit")
            return "Кільцевий торговий маршрут";
        if (scenario.Id == "company.delivery.heavy")
            return "Велике гуртове постачання";
        if (scenario.Id == "company.research.inspected")
            return "Сертифікований науковий експорт";
        if (scenario.Id == "company.mining.inspected")
            return "Інспектована партія руди";
        if (scenario.Id == "company.mining.assorted")
            return slot >= 3 ? "Геологічна партія: три рудні фракції" :
                "Геологічна партія: змішана руда";
        if (scenario.Id == "company.research.dualarchive")
            return "Технологічний архів: подвійна атестація";
        if (scenario.Id == "company.agriculture.seedreserve")
            return "Аграрний резерв: насіння і продукція";
        if (scenario.Id == "company.industry.inspected")
            return "Сертифіковані промислові матеріали";
        if (scenario.Id == "company.delivery.direct")
            return new[] { "Пряма доставка", "Прямий вантажний рейс",
                "Велике пряме постачання", "Критичний вантажний рейс" }[slot];
        return OrganizationQuotaRules.Title(OrganizationKind.Company, industry, category, slot);
    }

    private static string QuotaCompanyScenarioDescription(QuotaScenarioDefinition scenario,
        OrganizationIndustry industry, int category, int slot, int units, int pharmaceuticalVariant,
        int[] postTargets, int workMinutes)
    {
        string description;
        if (scenario.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
        {
            description = OrganizationQuotaRules.PharmaceuticalDescription(slot, units, pharmaceuticalVariant);
        }
        else if (scenario.Id == FrontierScenarioId)
        {
            description = $"Перевезіть {units} фізичних ящиків компанії до одного віддаленого торгового пункту. " +
                "Вантаж розподіляється між категоріями A, B, C, D, які вибираються на терміналі приймання. " +
                $"Завантажте всі {units} ящиків із серверними накладними на один шатл. " +
                "Використайте маршрутний модуль на консолі пілота: після узгодження " +
                "координат шатл вирушить у фізичний коридор дальнього рейсу. " +
                "Час у дорозі залежить від тяги й характеристик корабля; під час " +
                "автопілота ручне керування заблоковане. " +
                "Розвантажте ящики на віддаленому посту й поверніть той самий " +
                "шатл до сектора. Розрахунок проводиться після повернення.";
        }
        else if (scenario.Id == "company.mining.assorted")
        {
            var oreTypes = slot >= 3 ? 3 : 2;
            var totalOre = OrganizationQuotaRules.ExportItemsPerCrate(
                OrganizationQuotaWorkflow.MiningExport, slot);
            var minimumPerType = Math.Max(3, totalOre / (oreTypes + 1));
            description = $"Сформуйте {units} окремих геологічних партій у фізичних ящиках. " +
                $"У кожному ящику потрібно щонайменше {totalOre} одиниць сирої руди, " +
                $"не менше {oreTypes} різних рудних фракцій, по {minimumPerType} " +
                "або більше одиниць кожної. Оформіть накладну на кожний ящик. " +
                "Cargo перевіряє фактичний вміст перед прийманням.";
        }
        else if (scenario.Id == "company.research.dualarchive")
        {
            description = $"Сформуйте {units} реальних наукових ящиків. " +
                "У кожному має бути диск із ненульовим обсягом дослідницьких даних " +
                "та окремий технологічний диск зі схемами. Обидва носії " +
                "перевіряються сервером під час здачі через Cargo.";
        }
        else if (scenario.Id == "company.agriculture.seedreserve")
        {
            var required = OrganizationQuotaRules.ExportItemsPerCrate(
                OrganizationQuotaWorkflow.AgriculturalExport, slot);
            var seeds = Math.Max(2, required / 4);
            var produce = Math.Max(3, required / 3);
            description = $"Поставте {units} ящиків для аграрного резерву. " +
                $"У кожному: щонайменше {seeds} одиниць насіння та {produce} " +
                "окремих одиниць урожаю, із загальною комплектністю не меншою " +
                $"за {required}. Накладна прив'язана до фізичного ящика.";
        }
        else if (scenario.Id is "company.delivery.direct" or "company.delivery.priority")
        {
            var post = Array.FindIndex(postTargets, count => count > 0);
            description = post < 0
                ? "Маршрут не визначено: зверніться до адміністрації."
                : $"Доставте {units} вантажних ящиків до одного поста {(char)('A' + post)}. " +
                  "Оформіть їх через Cargo Requests. Кожен ящик має серверну накладну; " +
                  "достатньо фізичного приймання на правильному посту.";
        }
        else if (scenario.Id is "company.delivery.circuit" or "company.delivery.heavy" or
                 "company.delivery.registered")
        {
            var stops = new List<string>();
            for (var i = 0; i < postTargets.Length; i++)
                if (postTargets[i] > 0)
                    stops.Add($"{(char)('A' + i)}: {postTargets[i]} ящ.");
            description = $"Розвезіть {units} фізичних ящиків із накладними Cargo Requests. " +
                $"Пункти: {string.Join(", ", stops)}. " +
                "Кожну партію приймають тільки на вказаному реальному торговому посту.";
        }
        else
        {
            description = OrganizationQuotaRules.DescriptionForUnits(OrganizationKind.Company,
                industry, category, slot, units);
        }
        if (QuotaRequiresInspection(scenario))
            description += "\nОБОВ'ЯЗКОВА ІНСПЕКЦІЯ: отримайте прив'язаний до замовлення " +
                "сканер, оформіть вантажні ящики та використайте сканер на КОЖНОМУ ящику " +
                "перед відправленням. При зміні вмісту експортного ящика потрібна повторна перевірка. " +
                "Неперевірений вантаж Cargo не прийме.";
        return description + $"\nПланова активна робота: приблизно {workMinutes} хв; " +
               "це оцінка складності, а не гарантований час виконання.";
    }
}
