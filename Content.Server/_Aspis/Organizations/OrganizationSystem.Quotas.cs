using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Database;
using Content.Shared.UserInterface;
using Robust.Shared.Enums;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Round-local shared quotas. For logistics, only authenticated crates physically
/// sold on the correct Cargo trade post advance progress. Console and PDA requests
/// cannot submit cargo proof or create payouts. Export proofs require a valid
/// manifest and verified inventory on actual cargo pallet sale.
/// </summary>
public sealed partial class OrganizationSystem
{
    private sealed class SectorQuotaOffer(int units, int minutes, int[] postTargets, int pharmaceuticalVariant, int scenarioVariant = 0)
    {
        public readonly int Units = units;
        public readonly int Minutes = minutes;
        public readonly int[] PostTargets = postTargets;
        public readonly int PharmaceuticalVariant = pharmaceuticalVariant;
        public readonly int ScenarioVariant = scenarioVariant;
    }

    private sealed class SectorQuotaClaim(Guid organization, CharacterKey assignee, string assigneeName,
        string code, TimeSpan deadline, SectorQuotaOffer offer, int difficultySlot)
    {
        public readonly Guid Organization = organization;
        public readonly CharacterKey Assignee = assignee;
        public readonly string AssigneeName = assigneeName;
        public readonly string Code = code;
        public readonly int DifficultySlot = difficultySlot;
        public readonly Guid FactionAssignmentId = Guid.NewGuid();
        // Stable within the assignment. Map UIDs and X/Y are deliberately round-local.
        public string FactionSiteCode => "EX-" + FactionAssignmentId.ToString("N")[..8].ToUpperInvariant();
        public string FactionCreationError = string.Empty;
        public bool FactionSiteBound;
        public EntityUid? FactionSiteMapUid;
        public string FactionSiteMapLabel = string.Empty;
        public float FactionSiteX;
        public float FactionSiteY;
        public readonly TimeSpan Deadline = deadline;
        public readonly int Units = offer.Units;
        public readonly int TimeLimitMinutes = offer.Minutes;
        public readonly int[] PostTargets = (int[]) offer.PostTargets.Clone();
        public readonly int PharmaceuticalVariant = offer.PharmaceuticalVariant;
        public readonly int FactionScenarioVariant = offer.ScenarioVariant;
        public bool Completed;
        // Physical delivery is final even if the economic budget cannot settle immediately.
        public bool SettlementPending;
        public int CargoRequestedCrates;
        public readonly int[] DeliveredByPost = new int[4];
        public readonly HashSet<string> ProcessedCargoReferences = new(StringComparer.Ordinal);
        public readonly HashSet<string> LostCargoReferences = new(StringComparer.Ordinal);
        // Weighted shipment quality, only validated by the physical cargo sale.
        public int VerifiedQualityTotal;
        public string LastInspectionReport = string.Empty;
        public int LastInspectionQuality;
        public int RejectedShipments;
        // Only for faction activities. Server events are authoritative.
        public int FactionStage;
        public bool FactionMilitaryPrepared;
        public bool FactionSciencePrepared;
        public EntityUid? FactionAntenna;
        // Round-only physical survey unit, replaced by the real task-bound relay
        // when the same kit is used on it. Never write this UID to storage.
        public EntityUid? FactionSurveyBeacon;
        public EntityUid? FactionFieldKit;
        public TimeSpan FactionSignalReadyAt;
        // v43: separate, temporary space exploration site and on-grid recorder.
        // These UIDs must never be serialized to OrganizationStore.
        public EntityUid? FactionPlanetMap;
        public string FactionPlanetName = string.Empty;
        public bool FactionPlanetVisited;
        public EntityUid? FactionWreckMap;
        public EntityUid? FactionWreckGrid;
        public EntityUid? FactionWreckRecorder;
        public EntityUid? FactionWreckInspectionTerminal;
        // Locations relative to the original grid; moving the ship/grid itself
        // does not invalidate the inspection, relocating a device does.
        public System.Numerics.Vector2 FactionWreckRecorderPosition;
        public System.Numerics.Vector2 FactionWreckInspectionPosition;
        public bool FactionWreckInspectionComplete;
        // v47: hostiles are planned once, but spawned only when the first shuttle
        // commits to FTL. Old round-local maps cannot create fresh waves.
        public readonly List<(string Prototype, System.Numerics.Vector2 Position)> FactionWreckHostilePlan = new();
        public bool FactionWreckHostilesSpawned;
        public int FactionWreckHostilesDeployed;
        public string FactionWreckHazard = string.Empty;
        public int FactionWreckMinimumRouteTiles;
        // Round-local progress: material replacement precedes a real timed tool action.
        public bool FactionWreckMaterialsPrepared;
        public FactionWreckActivityKind FactionWreckActivity;
        public string FactionWreckName = string.Empty;
        public EntityUid? FactionWreckDisk;
        public EntityUid? FactionWreckShuttle;
        public TimeSpan FactionWreckLaunchReadyAt;
        public TimeSpan FactionWreckDiskPrintedAt;
        public bool FactionWreckVisited;
        public bool FactionWreckReturned;
        public bool FactionKitOrdered;
        public int FactionKitOrders;
        public TimeSpan FactionKitLastOrderedAt;
        public EntityUid? FactionKitInvoice;
        public readonly HashSet<string> RejectedPharmaceuticalReferences = new(StringComparer.Ordinal);
        public int Delivered => DeliveredByPost.Sum();
    }

    private readonly Dictionary<(OrganizationKind Kind, int Category, int Slot), SectorQuotaClaim> _sectorQuotaClaims = new();
    // One shared offer per category/difficulty/round. Refreshing a console never re-rolls it.
    private readonly Dictionary<(OrganizationKind Kind, int Category, int Slot), SectorQuotaOffer> _sectorQuotaOffers = new();

    private SectorQuotaOffer GetOrCreateQuotaOffer(OrganizationKind kind, int category, int slot)
    {
        var id = (kind, category, slot);
        if (_sectorQuotaOffers.TryGetValue(id, out var offer))
            return offer;
        if (kind == OrganizationKind.Faction)
        {
            // Server-side scenario is frozen per offer, and copied to the claim.
            // Four actual difficulty grades. The planetary deployment is grade I;
            // the three survey variants use progressively tougher sites.
            offer = new SectorQuotaOffer(slot == 0 ? 4 : Math.Min(10, 3 + slot * 2),
                slot switch { 0 => 90, 1 => 60, 2 => 80, _ => 100 },
                [0, 0, 0, 0], 0, _random.Next(0, 6));
            _sectorQuotaOffers.Add(id, offer);
            return offer;
        }
        // A research shipment requires genuine disks, while mining and farming
        // require filled crates. Do not price either as 40 empty logistics crates.
        var workflow = kind == OrganizationKind.Faction
            ? OrganizationQuotaWorkflow.SectorService
            : OrganizationQuotaRules.WorkflowFor(kind, (OrganizationIndustry) category);
        var minimum = workflow switch
        {
            OrganizationQuotaWorkflow.ResearchExport => Math.Max(1, slot + 1),
            OrganizationQuotaWorkflow.MedicalTreatment => Math.Max(1, slot + 1),
            OrganizationQuotaWorkflow.PharmaceuticalExport => slot switch { 0 => 2, 1 => 2, 2 => 2, _ => 1 },
            _ => OrganizationQuotaRules.MinimumUnits(slot)
        };
        var maximum = workflow switch
        {
            OrganizationQuotaWorkflow.ResearchExport => (slot + 1) * 2,
            OrganizationQuotaWorkflow.MedicalTreatment => slot + 2,
            OrganizationQuotaWorkflow.PharmaceuticalExport => slot switch { 0 => 4, 1 => 4, 2 => 4, _ => 3 },
            _ => OrganizationQuotaRules.MaximumUnits(slot)
        };
        var units = _random.Next(minimum, maximum + 1);
        // This is a server-side selection from *loaded* reagent prototypes.
        // The menu never offers a formulation that exists only in this C# list.
        var pharmaceuticalVariant = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport
            ? ChoosePharmaceuticalVariant(slot) : 0;
        var minutes = workflow switch
        {
            OrganizationQuotaWorkflow.PharmaceuticalExport => 50 + slot * 20 +
                units * 5 + _random.Next(0, 4) * 5 +
                OrganizationQuotaRules.PharmaceuticalExtraMinutes(pharmaceuticalVariant, slot),
            OrganizationQuotaWorkflow.MedicalTreatment => 45 + slot * 20 + _random.Next(0, 4) * 5,
            _ => OrganizationQuotaRules.TimeLimitForUnits(slot, units, _random.Next(0, 4))
        };
        var targets = new int[4];
        if (workflow == OrganizationQuotaWorkflow.CargoDelivery)
        {
            // Logistics uses stable, randomized destinations. Exports use verified
            // cargo content instead of A-D delivery targets.
            var numberOfPosts = _random.Next(2, Math.Min(4, units) + 1);
            var firstPost = _random.Next(0, 5 - numberOfPosts);
            for (var i = 0; i < numberOfPosts; i++)
                targets[firstPost + i] = 1;
            for (var i = numberOfPosts; i < units; i++)
                targets[firstPost + _random.Next(numberOfPosts)]++;
        }
        else if (workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
        {
            // A pharmacy consignment has one physical receiving station. This
            // destination is the same for every company competing for the offer.
            targets[_random.Next(0, 4)] = units;
        }
        else
        {
            targets[0] = units;
        }
        // The formulation belongs to the shared offer, never to the company
        // claiming it or to a client-side random roll.
        offer = new SectorQuotaOffer(units, minutes, targets, pharmaceuticalVariant);
        _sectorQuotaOffers.Add(id, offer);
        return offer;
    }
    // Every candidate is vetted against the actual installed fork resources.
    // In a complex kit the *pair* must exist, otherwise no player can fulfil it.
    private int ChoosePharmaceuticalVariant(int slot)
    {
        if (slot == 0)
            return 0; // Ordinary first-aid kits do not require a reagent.

        // Loaded medical prototypes alone are insufficient: a reagent should
        // also have a synthesis route in this server's chemistry catalog.
        var producedReagents = _prototypes.EnumeratePrototypes<ReactionPrototype>()
            .SelectMany(reaction => reaction.Products.Keys)
            .Select(reagent => reagent.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = new List<int>();
        for (var variant = 0; variant < OrganizationQuotaRules.PharmaceuticalFormulaCount; variant++)
        {
            var id = OrganizationQuotaRules.PharmaceuticalReagent(variant);
            if (!_prototypes.TryIndex<ReagentPrototype>(id, out var recipe) || recipe.Abstract ||
                !producedReagents.Contains(id))
                continue;
            if (slot == 3)
            {
                var secondary = OrganizationQuotaRules.PharmaceuticalReagent(
                    OrganizationQuotaRules.PharmaceuticalSecondaryVariant(variant));
                if (!_prototypes.TryIndex<ReagentPrototype>(secondary, out var second) ||
                    second.Abstract || !producedReagents.Contains(secondary))
                    continue;
            }
            available.Add(variant);
        }
        if (available.Count == 0)
            return -1; // No eligible medicine exists on this server. Do not publish an impossible offer.

        var preferredComplexity = slot == 1 ? 1 : 2;
        var preferred = available.Where(v =>
            OrganizationQuotaRules.PharmaceuticalComplexity(v) >= preferredComplexity).ToList();
        if (preferred.Count > 0)
            available = preferred;
        return available[_random.Next(0, available.Count)];
    }

    private int _roundQuotaSerial;
    private TimeSpan _nextQuotaSweep;
    // Prevents a physical shipment from satisfying multiple different companies' jobs.
    private readonly HashSet<string> _acceptedCargoProofs = new(StringComparer.Ordinal);

    private static IEnumerable<int> QuotaCategories(Organization org)
    {
        if (org.Kind == OrganizationKind.Company)
        {
            if (org.Industry != OrganizationIndustry.None)
                yield return (int)org.Industry;
            yield break;
        }

        OrganizationFactionFocus[] focuses = org.FactionFoci is { Length: > 0 }
            ? org.FactionFoci : [org.FactionFocus];
        foreach (var focus in focuses.Where(f => f != OrganizationFactionFocus.None &&
            f != OrganizationFactionFocus.Other).Distinct().Take(3))
            yield return (int)focus;
    }

    private void ExpireSectorQuotas()
    {
        var now = _gameTiming.CurTime;
        foreach (var pair in _sectorQuotaClaims.ToArray())
        {
            var claim = pair.Value;
            if (claim.SettlementPending && _organizations.TryGetValue(claim.Organization, out var organization) &&
                organization.InsolvencyState == OrganizationInsolvencyState.Active &&
                TryPayVerifiedQuota(organization, pair.Key.Slot, pair.Key.Category, claim.Code, claim.Units,
                    Math.Clamp(claim.VerifiedQualityTotal / Math.Max(1, claim.Units), 50, 100)))
            {
                claim.SettlementPending = false;
                NotifyQuotaAssignee(claim, $"Винагороду за квоту {claim.Code} зараховано!", true);
                _adminLog.Add(LogType.Action, LogImpact.Low,
                    $"Deferred quota settlement completed: {claim.Code}");
            }
            if (claim.Completed || now < claim.Deadline)
                continue;
            if (pair.Key.Kind == OrganizationKind.Faction)
            {
                RemoveFactionOperationObjects(claim);
                CloseFactionWreckRoute(claim);
            }
            _sectorQuotaClaims.Remove(pair.Key);
            // Expired shared contracts can reappear with a different scope and deadline.
            _sectorQuotaOffers.Remove(pair.Key);
        }
    }

    private OrganizationQuotaEntry[] BuildSectorQuotaBoard(Organization org, CharacterKey viewer)
    {
        ExpireSectorQuotas();
        var entries = new List<OrganizationQuotaEntry>();
        foreach (var category in QuotaCategories(org))
        {
            // Grade I builds a planetary relay; grades II-IV are separate
            // survey offers with increasing route and hostile requirements.
            for (var slot = 0; slot < OrganizationQuotaRules.SlotsPerCategory; slot++)
            {
                if (org.Kind == OrganizationKind.Faction && slot == 0 &&
                    !IsFactionAntennaFocus((OrganizationFactionFocus) category))
                    continue;
                var key = (org.Kind, category, slot);
                _sectorQuotaClaims.TryGetValue(key, out var claim);
                if (claim != null && claim.Organization != org.Id)
                    continue;
                // A completed faction job must not permanently occupy its slot.
                // Its results are in the persistent node/finding archive.
                if (org.Kind == OrganizationKind.Faction && claim?.Completed == true)
                    claim = null;
                // All survey sites are still reachable until round-end; stop
                // offering new ones after hitting our small global map budget.
                if (org.Kind == OrganizationKind.Faction && claim == null &&
                    (slot > 0 ? _factionWreckMapsCreatedThisRound >= MaxFactionWreckMapsPerRound
                        : _factionPlanetMapsCreatedThisRound >= MaxFactionPlanetMapsPerRound))
                    continue;

                var offer = GetOrCreateQuotaOffer(org.Kind, category, slot);
                var units = claim?.Units ?? offer.Units;
                var minutes = claim?.TimeLimitMinutes ?? offer.Minutes;
                var mine = claim != null && claim.Organization == org.Id;
                var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
                var pharmaceutical = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport;
                var variant = claim?.PharmaceuticalVariant ?? offer.PharmaceuticalVariant;
                if (pharmaceutical && variant < 0)
                    continue; // Do not show a recipe unavailable on this server.
                var faction = org.Kind == OrganizationKind.Faction;
                var scenario = claim?.FactionScenarioVariant ?? offer.ScenarioVariant;
                entries.Add(new OrganizationQuotaEntry(category, slot,
                    faction ? (slot > 0 ? FactionWreckTitle((OrganizationFactionFocus)category, mine ? claim : null, scenario) : FactionAntennaTitle(org, scenario)) : pharmaceutical ? OrganizationQuotaRules.PharmaceuticalTitle(slot, variant) :
                        OrganizationQuotaRules.Title(org.Kind, org.Industry, category, slot),
                    faction ? (slot > 0 ? FactionWreckDescription((OrganizationFactionFocus)category, mine ? claim : null, scenario) : FactionAntennaDescription(org, claim, scenario)) : pharmaceutical ?
                        OrganizationQuotaRules.PharmaceuticalDescription(slot, units, variant) :
                        OrganizationQuotaRules.DescriptionForUnits(org.Kind, org.Industry, category, slot, units),
                    (faction && slot > 0 ?
                        FactionWreckWorkflow(mine ? claim!.FactionWreckActivity :
                            FactionWreckActivityFor((OrganizationFactionFocus)category)) :
                        OrganizationQuotaRules.WorkflowName(workflow)), units,
                    OrganizationQuotaRules.IncomeBonusPercent(slot),
                    OrganizationQuotaRules.InfluenceBasisPoints(slot), mine, mine && claim!.Completed)
                {
                    Workflow = workflow,
                    Code = mine ? claim!.Code : string.Empty,
                    AssignedTo = mine ? claim!.AssigneeName : string.Empty,
                    AssignedToMe = mine && claim!.Assignee == viewer,
                    SettlementPending = mine && claim!.SettlementPending,
                    TimeLimitMinutes = minutes,
                    IsFactionOperation = faction,
                    FactionSteps = faction ? BuildFactionTaskSteps(org, mine ? claim : null, slot, category) : [],
                    FactionStageName = faction
                        ? (slot > 0 ? FactionWreckStageName(mine ? claim : null) : FactionStageName(claim, org))
                        : string.Empty,
                    FactionSiteLabel = faction && mine
                        ? (slot > 0 ? FactionWreckLocation(claim!) : FactionSiteDescription(claim!, viewer))
                        : string.Empty,
                    FactionAuthorityPoints = org.FactionAuthorityPoints,
                    EstimatedReward = faction ? 0 : EstimateQuotaNetReward(org, slot, units, variant),
                    SecondsRemaining = mine ? Math.Max(0, (int)(claim!.Deadline - _gameTiming.CurTime).TotalSeconds) :
                        minutes * 60,
                    Delivered = mine ? (faction ? (slot > 0
                        ? Math.Min(3, claim!.FactionStage) : Math.Min(4, claim!.FactionStage))
                        : claim!.Delivered) : 0,
                    LastInspectionReport = mine ? claim!.LastInspectionReport : string.Empty,
                    LastInspectionQuality = mine ? claim!.LastInspectionQuality : 0,
                    RejectedShipments = mine ? claim!.RejectedShipments : 0,
                    DeliveredByPost = mine ? (int[])claim!.DeliveredByPost.Clone() : [0, 0, 0, 0],
                    PostTargets = (int[]) (mine ? claim!.PostTargets : offer.PostTargets).Clone()
                });
            }
        }
        return entries.ToArray();
    }

    private bool TryGetQuotaMember(EntityUid uid, EntityUid actor, Guid organization, int category, int slot,
        out CharacterKey key, out Organization org)
    {
        key = default;
        org = default!;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out key, out _) ||
            !IsOriginalController(actor) ||
            !_organizations.TryGetValue(organization, out var memberOrg) || memberOrg is null ||
            !memberOrg.Members.ContainsKey(key) ||
            memberOrg.InsolvencyState != OrganizationInsolvencyState.Active || !_store.Available ||
            slot is < 0 or >= OrganizationQuotaRules.SlotsPerCategory ||
            !QuotaCategories(memberOrg).Contains(category))
            return false;
        org = memberOrg;
        return true;
    }

    private void RefreshSectorQuotaSubscribers(EntityUid originalConsole, EntityUid originalActor,
        string originalNotice)
    {
        Refresh(originalConsole, originalActor, originalNotice);
        var consoles = EntityQueryEnumerator<OrganizationConsoleComponent, UserInterfaceComponent>();
        while (consoles.MoveNext(out var console, out _, out var interfaceComp))
        {
            foreach (var session in _players.Sessions)
            {
                if (session.Status != SessionStatus.InGame ||
                    session.AttachedEntity is not { Valid: true } viewer ||
                    (console == originalConsole && viewer == originalActor) ||
                    !_ui.IsUiOpen((console, interfaceComp), OrganizationConsoleUiKey.Key, viewer))
                    continue;
                Refresh(console, viewer);
            }
        }
    }

    private void OnClaimQuota(EntityUid uid, OrganizationConsoleComponent comp, OrganizationClaimQuotaMessage args)
    {
        if (!TryGetQuotaMember(uid, args.Actor, args.Organization, args.Category, args.Slot,
                out var character, out var org))
            return;
        if (org.Kind == OrganizationKind.Faction &&
            (!_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(FactionCoordinatesDiskProto) ||
             args.Slot is < 0 or > 3 ||
             (args.Slot == 0 &&
              (!IsFactionAntennaFocus((OrganizationFactionFocus)args.Category) ||
               !_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(FactionAntennaEntity) ||
               !_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(QuotaManifestPrototype))) ||
             (args.Slot > 0 &&
              (!_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(FactionWreckRecorderEntity) ||
               !_prototypes.HasIndex<Robust.Shared.Prototypes.EntityPrototype>(FactionWreckInspectionEntity) ||
               _factionWreckMapsCreatedThisRound >= MaxFactionWreckMapsPerRound)) ||
             (args.Slot == 0 && _factionPlanetMapsCreatedThisRound >= MaxFactionPlanetMapsPerRound)))
        {
            Refresh(uid, args.Actor, "Цей тип завдання зараз недоступний або ліміт експедиційних місць вичерпано.");
            return;
        }
        ExpireSectorQuotas();
        var id = (org.Kind, args.Category, args.Slot);
        if (_sectorQuotaClaims.TryGetValue(id, out var existing) &&
            !(org.Kind == OrganizationKind.Faction && existing.Completed && existing.Organization == org.Id))
        {
            Refresh(uid, args.Actor, "Це замовлення вже зарезервоване.");
            return;
        }
        if (_sectorQuotaClaims.Values.Any(q => q.Organization == org.Id && (!q.Completed || q.SettlementPending)))
        {
            Refresh(uid, args.Actor, "Спочатку завершіть або скасуйте своє активне замовлення.");
            return;
        }

        if (org.Kind == OrganizationKind.Faction && FactionNextTaskSeconds(org) > 0)
        {
            var remaining = FactionNextTaskSeconds(org);
            Refresh(uid, args.Actor,
                $"Наступне завдання можна прийняти через {remaining / 60} хв {remaining % 60} с. " +
                "Авторитет фракції поступово скорочує цей час.");
            return;
        }
        var offer = GetOrCreateQuotaOffer(org.Kind, args.Category, args.Slot);
        if (OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry) ==
            OrganizationQuotaWorkflow.PharmaceuticalExport && offer.PharmaceuticalVariant < 0)
        {
            Refresh(uid, args.Actor, "Сервер не має реагентів для цього рецепта.");
            return;
        }
        var code = $"QT-{(int)org.Kind:X1}-{args.Category:X2}-{args.Slot + 1}-{++_roundQuotaSerial:X5}";
        var deadline = _gameTiming.CurTime + TimeSpan.FromMinutes(offer.Minutes);
        var newClaim = new SectorQuotaClaim(org.Id, character,
            org.Members[character].Name, code, deadline, offer, args.Slot);
        if (org.Kind == OrganizationKind.Faction && args.Slot > 0)
            newClaim.FactionWreckActivity = FactionWreckActivityFor((OrganizationFactionFocus)args.Category);
        if (org.Kind == OrganizationKind.Faction &&
            !(args.Slot > 0 ? TryCreateFactionWreckSite(newClaim) : TryCreateFactionPlanetSite(newClaim)))
        {
            Refresh(uid, args.Actor,
                string.IsNullOrWhiteSpace(newClaim.FactionCreationError)
                    ? "Не вдалося створити місце завдання. Подробиці — у журналі organizations.faction-sites."
                    : newClaim.FactionCreationError);
            return;
        }
        _sectorQuotaClaims[id] = newClaim;
        if (org.Kind == OrganizationKind.Faction)
        {
            // The next offer may have a new story, without rerolling the active job.
            _sectorQuotaOffers.Remove(id);
            PrintFactionCoordinatesDisk(args.Actor, character, org, newClaim, true);
        }
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Quota accepted: code={code}, organization={org.Id}, assignee={character.UserId}");
        RefreshSectorQuotaSubscribers(uid, args.Actor,
            $"Замовлення {code} закріплено за вами. Перегляньте його в КПК.");
    }

    private void OnReleaseQuota(EntityUid uid, OrganizationConsoleComponent comp, OrganizationReleaseQuotaMessage args)
    {
        if (!TryGetQuotaMember(uid, args.Actor, args.Organization, args.Category, args.Slot,
                out var character, out var org))
            return;
        var id = (org.Kind, args.Category, args.Slot);
        if (!_sectorQuotaClaims.TryGetValue(id, out var claim) || claim.Organization != org.Id ||
            claim.Completed)
            return;
        // A partially delivered shipment cannot be released and immediately
        // reaccepted for another payout, nor can another member cancel it.
        if (claim.Assignee != character && character != org.Owner)
        {
            Refresh(uid, args.Actor, "Скасувати завдання може виконавець або власник.");
            return;
        }
        if (claim.Delivered > 0 || claim.CargoRequestedCrates > 0 ||
            (org.Kind == OrganizationKind.Faction && (claim.FactionKitOrdered || claim.FactionWreckVisited ||
                 claim.FactionWreckInspectionComplete)))
        {
            Refresh(uid, args.Actor, "Відмова після замовлення вантажу або підтвердженої доставки недоступна.");
            return;
        }
        if (org.Kind == OrganizationKind.Faction)
        {
            if (claim.FactionStage > 0 && claim.FactionWreckMap == null)
            {
                Refresh(uid, args.Actor, "Розпочате польове завдання не можна миттєво скасувати.");
                return;
            }
            // For a survey mission the map may still contain a player's
            // shuttle or stranded character. It stays accessible until round
            // restart; canceling merely invalidates the task-bound recorder.
            RemoveFactionOperationObjects(claim);
            CloseFactionWreckRoute(claim);
            // Even a cancelled reservation consumes an expedition map for the
            // round. Prevent accept/cancel loops from spawning safe FTL havens.
            ScheduleNextFactionTask(org);
        }
        _sectorQuotaClaims.Remove(id);
        RefreshSectorQuotaSubscribers(uid, args.Actor,
            org.Kind == OrganizationKind.Faction
                ? "Завдання скасовано. Наступне можна взяти після затримки."
                : "Завдання повернулося до загального списку.");
    }

    // Backward-compatible client request. It never completes the quota.
    private void OnCompleteQuota(EntityUid uid, OrganizationConsoleComponent comp, OrganizationCompleteQuotaMessage args)
    {
        if (!TryGetQuotaMember(uid, args.Actor, args.Organization, args.Category, args.Slot,
                out _, out _))
            return;
        Refresh(uid, args.Actor,
            "Здача предмета з рук більше не підтримується. Потрібне підтвердження доставки від карго.");
    }

    /// <summary>
    /// SERVER ONLY integration point. Call only after verifying a real physical
    /// crate on cargo pallet sale, either at its correct A-D trade destination
    /// or as a stamped export crate with validated nested contents. Reference
    /// must be generated by the server, never supplied through UI text.
    /// </summary>
    public bool TryRecordVerifiedQuotaCargo(Guid organizationId, string code,
        string cargoReference, int post, int verifiedCrates, int qualityPercent = 100)
    {
        if (string.IsNullOrWhiteSpace(code) || cargoReference.Length is < 6 or > 128 ||
            verifiedCrates is < 1 or > 40 || qualityPercent is < 50 or > 100 || !_store.Available ||
            _acceptedCargoProofs.Contains(cargoReference) ||
            !_organizations.TryGetValue(organizationId, out var org) || org is null ||
            org.Kind != OrganizationKind.Company ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return false;
        ExpireSectorQuotas();
        var key = _sectorQuotaClaims.Keys.FirstOrDefault(k =>
            _sectorQuotaClaims[k].Organization == organizationId &&
            _sectorQuotaClaims[k].Code == code);
        if (!_sectorQuotaClaims.TryGetValue(key, out var claim) || claim.Completed ||
            _gameTiming.CurTime >= claim.Deadline ||
            claim.ProcessedCargoReferences.Contains(cargoReference) ||
            (OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry) ==
                OrganizationQuotaWorkflow.CargoDelivery && claim.Delivered >= claim.CargoRequestedCrates))
            return false;
        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        var hasDestination = workflow is OrganizationQuotaWorkflow.CargoDelivery or
            OrganizationQuotaWorkflow.PharmaceuticalExport;
        if (hasDestination ? (post is < 0 or > 3) : post != 0)
            return false;
        var maximumForPost = hasDestination
            ? claim.PostTargets[post]
            : claim.Units;
        if (claim.DeliveredByPost[post] + verifiedCrates > maximumForPost)
            return false;

        var finalDelivery = claim.Delivered + verifiedCrates == claim.Units;
        claim.DeliveredByPost[post] += verifiedCrates;
        claim.VerifiedQualityTotal += verifiedCrates * qualityPercent;
        claim.ProcessedCargoReferences.Add(cargoReference);
        _acceptedCargoProofs.Add(cargoReference);
        claim.Completed = finalDelivery;
        if (finalDelivery)
        {
            // The physical completion and economic settlement are independent:
            // a daily sector limit must never erase delivered cargo.
            claim.SettlementPending = !TryPayVerifiedQuota(org, key.Slot, key.Category, code, claim.Units,
                Math.Clamp(claim.VerifiedQualityTotal / claim.Units, 50, 100));
            NotifyQuotaAssignee(claim, claim.SettlementPending
                ? $"Квоту {claim.Code} виконано! Виплата очікує розрахунку."
                : $"Квоту {claim.Code} виконано! Винагороду зараховано.",
                !claim.SettlementPending);
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Cargo quota proof: {code}, reference={cargoReference}, post={post}, count={verifiedCrates}");
        return true;
    }

    private static int QuotaEffortPercent(OrganizationQuotaWorkflow workflow) => workflow switch
    {
        OrganizationQuotaWorkflow.ResearchExport => 200,
        OrganizationQuotaWorkflow.MiningExport => 160,
        OrganizationQuotaWorkflow.TradeExport => 120,
        OrganizationQuotaWorkflow.MedicalTreatment => 180,
        OrganizationQuotaWorkflow.PharmaceuticalExport => 260,
        OrganizationQuotaWorkflow.AgriculturalExport => 140,
        OrganizationQuotaWorkflow.IndustrialExport => 130,
        OrganizationQuotaWorkflow.CargoDelivery => 100,
        _ => 110
    };

    // A read-only estimate: don't consume random incident rolls while drawing the UI.
    // Actual settlement includes variable operating costs, sector demand and daily cap.
    private int EstimateQuotaNetReward(Organization org, int slot, int units,
        int pharmaceuticalVariant = 0)
    {
        if (org.Kind != OrganizationKind.Company)
            return 300 + units * 65;
        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        var effort = QuotaEffortPercent(workflow);
        var premium = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport
            ? 100 + OrganizationQuotaRules.PharmaceuticalPremiumPercent(pharmaceuticalVariant, slot) : 100;
        var baseGross = (long)DeliveryPrice(org) * units * (100 + slot * 20) * effort / 10_000
            * premium / 100;
        var remaining = Math.Max(0, Math.Min(AvailableSectorDemand(),
            DailyCompanyRevenueLimit - org.DailyGrossRevenue));
        // 150 credits approximate a normal dispatch cost; never treat this as guaranteed pay.
        return (int)Math.Max(0, Math.Min(baseGross, remaining) - 150);
    }

    private bool TryPayVerifiedQuota(Organization org, int slot, int category, string code, int units,
        int qualityPercent = 100)
    {
        var workflow = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry);
        var effortPercent = QuotaEffortPercent(workflow);
        var variant = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport
            ? (_sectorQuotaClaims.Values.FirstOrDefault(c => c.Organization == org.Id &&
                c.Code == code)?.PharmaceuticalVariant ?? 0)
            : 0;
        var premium = workflow == OrganizationQuotaWorkflow.PharmaceuticalExport
            ? 100 + OrganizationQuotaRules.PharmaceuticalPremiumPercent(variant, slot) : 100;
        var gross = org.Kind == OrganizationKind.Company
            ? (int)Math.Min((long)DeliveryPrice(org) * units * (100 + slot * 20) * effortPercent /
                10_000 * premium / 100,
                Math.Max(0, Math.Min(AvailableSectorDemand(), DailyCompanyRevenueLimit - org.DailyGrossRevenue)))
            : 300 + units * 65;
        // Incomplete pharmaceutical kits discount THIS contract rather than
        // charging an unidentified player. Missing contents cannot mint a full payout.
        var pharma = OrganizationQuotaRules.WorkflowFor(org.Kind, org.Industry) ==
            OrganizationQuotaWorkflow.PharmaceuticalExport;
        if (pharma)
            gross = gross * Math.Clamp(qualityPercent, 0, 100) / 100;
        var costs = org.Kind == OrganizationKind.Company ? DeliveryExpenses(org) : 0;
        var net = gross - costs;
        if (net <= 0 || org.Treasury > MaxTreasury - net)
            return false;
        var today = DateTime.UtcNow.Date;
        if (!Commit(() =>
        {
            if (org.Kind == OrganizationKind.Company)
            {
                _demandDateUtc = today;
                _demandRemaining -= gross;
                org.DailyGrossRevenue += gross;
                org.SalesDateUtc = today;
                org.OperatingCosts += costs;
            }
            org.GrossRevenue += gross;
            org.Treasury += net;
            if (org.Kind == OrganizationKind.Company)
            {
                var quotaBonus = OrganizationQuotaRules.IncomeBonusPercent(slot);
                if (pharma)
                    quotaBonus = quotaBonus * Math.Clamp(qualityPercent, 0, 100) / 100;
                org.QuotaIncomeBonusPercent = Math.Min(OrganizationQuotaRules.MaximumIncomeBonusPercent,
                    org.QuotaIncomeBonusPercent + quotaBonus);
                org.QuotaBonusCycles = OrganizationQuotaRules.BonusCycles;
                if (org.CompanyLevel < MaxCompanyLevel)
                {
                    org.CompanyExperience += 3 + slot * 2;
                    while (org.CompanyLevel < MaxCompanyLevel &&
                           org.CompanyExperience >= ExperienceNeeded(org.CompanyLevel))
                    {
                        org.CompanyExperience -= ExperienceNeeded(org.CompanyLevel);
                        org.CompanyLevel++;
                    }
                }
            }
            var free = Math.Max(0, MarketCapacityBp -
                _organizations.Values.Sum(o => o.SectorInfluenceBp));
            org.SectorInfluenceBp += Math.Min(free,
                OrganizationQuotaRules.InfluenceBasisPoints(slot));
        }))
            return false;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Verified quota payout: {code} org={org.Id} category={category} net={net}");
        return true;
    }
    // Physical popup above the assigned character, including deliveries completed
    // by cargo while the assignee is using a PDA or a different console.
    private void NotifyQuotaAssignee(SectorQuotaClaim claim, string text, bool paid)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } actor ||
                !TryGetCharacter(actor, out var key, out _) || key != claim.Assignee)
                continue;
            _quotaPopup.PopupEntity(text, actor, actor);
            if (paid)
                _audio.PlayEntity(MoneyConfirmSound, actor, actor);
            break;
        }
    }

}
