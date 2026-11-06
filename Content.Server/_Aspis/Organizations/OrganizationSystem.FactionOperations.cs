using System.Linq;
using Content.Server.Salvage.Expeditions;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Interaction;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Stacks;
using Content.Shared.Database;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// First physical faction expedition. No PDA action completes it: a unique server
/// field kit must be deployed on an expedition planet, steel physically repairs
/// the relay, and the player must activate and collect its signal afterwards.
/// The permanent points are independent of commercial market shares.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const string FactionFieldKitEntity = "TriadFactionFieldKit";
    private const string FactionSurveyEntity = "TriadFactionSurveyBeacon";
    private const string FactionAntennaEntity = "TriadFactionPlanetaryRelay";
    private const int FactionAntennaRepairSteel = 5;
    private const int FactionAntennaBaseSignalSeconds = 40;
    private const int FactionAntennaAuthorityReward = 9;
    // Faction coverage is an actual *round-local physical network*: only
    // commissioned relays still standing on the SAME expedition map and within
    // range of this site contribute. Historical registry records provide no
    // global bonus and do not respawn physical equipment by themselves.
    private const float FactionRelayLinkRangeSquared = 120f * 120f;
    private TimeSpan _nextFactionSignalValidation;

    private int FactionLocalLinks(Organization org, SectorQuotaClaim? site)
    {
        if (site is not { FactionSiteBound: true })
            return 0;
        var links = 0;
        foreach (var claim in _sectorQuotaClaims.Values)
        {
            if (!claim.Completed || claim.Organization != org.Id ||
                claim.FactionAssignmentId == site.FactionAssignmentId ||
                claim.FactionSiteMapUid != site.FactionSiteMapUid ||
                claim.FactionAntenna is not { } relay ||
                !FactionSiteEntityMatches(claim, relay) ||
                !org.FactionNodes.Any(node => node.Id == claim.FactionAssignmentId))
                continue;
            var dx = claim.FactionSiteX - site.FactionSiteX;
            var dy = claim.FactionSiteY - site.FactionSiteY;
            if (dx * dx + dy * dy <= FactionRelayLinkRangeSquared)
                links++;
        }
        return links;
    }

    private int FactionSignalSeconds(Organization org, SectorQuotaClaim? site = null) =>
        FactionAntennaBaseSignalSeconds - Math.Min(4, FactionLocalLinks(org, site)) * 5;

    // One cheap server-side check per second for all stabilizing field relays.
    // A signal cannot finish by moving the antenna away and returning it only
    // for the final hand interaction; a destroyed relay requires a new kit.
    private void ValidateFactionSignals()
    {
        foreach (var claim in _sectorQuotaClaims.Values)
        {
            if (claim.Completed || claim.FactionStage != 4)
                continue;
            if (claim.FactionAntenna is not { } antenna || Deleted(antenna))
            {
                claim.FactionAntenna = null;
                claim.FactionStage = 0;
                claim.FactionFieldKit = null;
                claim.FactionSurveyBeacon = null;
                claim.FactionMilitaryPrepared = false;
                claim.FactionSciencePrepared = false;
                claim.FactionSignalReadyAt = TimeSpan.Zero;
                NotifyQuotaAssignee(claim,
                    "Сигнал перервано: ретранслятор втрачено. Отримайте новий польовий комплект.", false);
                continue;
            }
            if (FactionSiteEntityMatches(claim, antenna))
                continue;
            claim.FactionStage = 3;
            claim.FactionSignalReadyAt = TimeSpan.Zero;
            NotifyQuotaAssignee(claim,
                "Сигнал перервано: ретранслятор переміщено. Поверніть його на ділянку та активуйте повторно.", false);
        }
    }
    // Bounded persistent registry until the full sector-site and diplomacy modules ship.
    private const int MaxFactionNetworkNodes = 512;
    // Prevent several assignments from creating a cluster of identical relays.
    private const float FactionSiteExclusionRadiusSquared = 20f * 20f;
    // A relocated relay does not count as a verified surveyed site.
    private const float FactionSiteDriftToleranceSquared = 2f * 2f;

    // Coordinates are deliberately round-local. Do not reveal another mission's
    // site or use coordinates supplied by a BUI message to advance its progress.
    private string FactionSiteDescription(SectorQuotaClaim claim, CharacterKey? viewer = null)
    {
        if (!claim.FactionSiteBound)
            return claim.FactionPlanetMap != null
                ? $"{claim.FactionPlanetName} · FTL-диск {claim.FactionSiteCode} · " +
                  "оберіть місце для ретранслятора після висадки"
                : $"Ділянка {claim.FactionSiteCode}: маршрут ще не підготовлено";

        var description = $"Ділянка {claim.FactionSiteCode} · {claim.FactionSiteMapLabel} · " +
            $"X {claim.FactionSiteX:0.0}, Y {claim.FactionSiteY:0.0} (лише цей раунд)";
        if (_organizations.TryGetValue(claim.Organization, out var siteOrg) && siteOrg != null)
        {
            var links = FactionLocalLinks(siteOrg, claim);
            description += links > 0
                ? $" · локальна мережа: {links} вуз. у радіусі 120 м"
                : " · поблизу немає активних вузлів вашої мережі";
        }

        if (claim.FactionStage == 1)
            description += claim.FactionSurveyBeacon is { } beacon && !Deleted(beacon)
                ? " · польовий орієнтир встановлено"
                : " · польовий орієнтир потребує відновлення";

        if (viewer is not { } key)
            return description;

        // The console and PDA already render FactionSiteLabel, so useful live
        // navigation can be provided without adding a second UI protocol.
        foreach (var pair in _identityByBody)
        {
            if (pair.Value != key || !pair.Key.Valid || Deleted(pair.Key))
                continue;
            var actorXform = Transform(pair.Key);
            if (actorXform.MapUid != claim.FactionSiteMapUid)
                return description + " · ви на іншій мапі";

            var current = actorXform.MapPosition.Position;
            var dx = claim.FactionSiteX - current.X;
            var dy = claim.FactionSiteY - current.Y;
            var meters = Math.Sqrt(dx * dx + dy * dy);
            if (meters <= 2)
                return description + " · ви на місці";
            // Map-space Y points north, X points east. This is a simple local
            // bearing, not a radar marker or FTL navigation coordinate.
            var directions = new[] { "Пн", "ПнСх", "Сх", "ПдСх", "Пд", "ПдЗх", "Зх", "ПнЗх" };
            var sector = ((int)Math.Round(Math.Atan2(dx, dy) * 4 / Math.PI) + 8) % 8;
            return description + $" · від вас {meters:0} м, напрямок {directions[sector]}";
        }
        return description;
    }

    // Only the exact server-bound planet/site may advance the task. Coordinates
    // are in map space, never relative to a shuttle or a movable object.
    private static bool FactionSitePositionMatches(SectorQuotaClaim claim, EntityUid? mapUid,
        float x, float y)
    {
        if (!claim.FactionSiteBound || mapUid != claim.FactionSiteMapUid)
            return false;
        var dx = x - claim.FactionSiteX;
        var dy = y - claim.FactionSiteY;
        return dx * dx + dy * dy <= FactionSiteDriftToleranceSquared;
    }

    private bool FactionSurveyBeaconMatches(SectorQuotaClaim claim, EntityUid beacon)
    {
        if (Deleted(beacon) || !TryComp<OrganizationFactionSurveyBeaconComponent>(beacon,
                out var marker) || marker.Organization != claim.Organization ||
            marker.Assignment != claim.FactionAssignmentId || marker.Code != claim.Code)
            return false;
        var pos = Transform(beacon);
        var coordinates = pos.MapPosition.Position;
        return FactionSitePositionMatches(claim, pos.MapUid, coordinates.X, coordinates.Y);
    }

    private bool FactionSiteEntityMatches(SectorQuotaClaim claim, EntityUid antenna)
    {
        if (Deleted(antenna))
            return false;
        var xform = Transform(antenna);
        var pos = xform.MapPosition.Position;
        return FactionSitePositionMatches(claim, xform.MapUid, pos.X, pos.Y);
    }

    // All assignments share the physical exclusion zone, regardless of faction.
    // Completed relays still reserve their ground position during this round.
    private bool FactionSiteAvailable(SectorQuotaClaim requester, EntityUid? mapUid,
        float x, float y)
    {
        foreach (var active in _sectorQuotaClaims.Values)
        {
            if (active == requester || !active.FactionSiteBound ||
                active.FactionSiteMapUid != mapUid)
                continue;
            var dx = x - active.FactionSiteX;
            var dy = y - active.FactionSiteY;
            if (dx * dx + dy * dy < FactionSiteExclusionRadiusSquared)
                return false;
        }
        return true;
    }

    private bool FactionNodeOnlineThisRound(Guid nodeId)
    {
        return _sectorQuotaClaims.Values.Any(c => c.FactionAssignmentId == nodeId &&
            c.Completed && c.FactionAntenna is { } relay && FactionSiteEntityMatches(c, relay));
    }

    private string FactionNodeRoundLocation(Guid nodeId)
    {
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.FactionAssignmentId == nodeId && c.Completed);
        return claim is { FactionSiteBound: true } &&
            claim.FactionAntenna is { } relay && FactionSiteEntityMatches(claim, relay)
            ? FactionSiteDescription(claim) : string.Empty;
    }

    private static bool IsFactionAntennaFocus(OrganizationFactionFocus focus) => focus is
        OrganizationFactionFocus.Colonial or OrganizationFactionFocus.Exploration or
        OrganizationFactionFocus.Scientific or OrganizationFactionFocus.Technological or
        OrganizationFactionFocus.Militarist or OrganizationFactionFocus.Defense;

    private static OrganizationFactionFocus[] FactionFocusList(Organization org) =>
        org.FactionFoci.Length > 0 ? org.FactionFoci : [org.FactionFocus];

    private static bool FactionMilitaryObjective(Organization org) =>
        FactionFocusList(org).Contains(OrganizationFactionFocus.Militarist) ||
        FactionFocusList(org).Contains(OrganizationFactionFocus.Defense);

    private static bool FactionScienceObjective(Organization org) =>
        FactionFocusList(org).Contains(OrganizationFactionFocus.Scientific) ||
        FactionFocusList(org).Contains(OrganizationFactionFocus.Technological);

    private static OrganizationFactionNodeKind FactionNodeKindFor(Organization org) =>
        FactionMilitaryObjective(org) && FactionScienceObjective(org)
            ? OrganizationFactionNodeKind.ResearchDefenseRelay :
        FactionMilitaryObjective(org) ? OrganizationFactionNodeKind.DefenseRelay :
        FactionScienceObjective(org) ? OrganizationFactionNodeKind.ScientificRelay :
        OrganizationFactionNodeKind.PlanetaryRelay;

    private static string FactionAntennaTitle(Organization org, int scenarioVariant = 0)
    {
        var kind = FactionMilitaryObjective(org) && FactionScienceObjective(org)
            ? "науково-оборонний вузол" : FactionMilitaryObjective(org)
            ? "оборонний ретранслятор" : FactionScienceObjective(org)
            ? "науковий ретранслятор" : "планетарний передавач";
        var name = new[]
        {
            "Відновлення лінії зв'язку №1", "Зовнішній ретрансляційний пункт",
            "Планетарний канал зв'язку", "Резервний секторний передавач",
            "Технічний вузол експедиції", "Розширення мережі ретрансляторів"
        }[Math.Abs(scenarioVariant) % 6];
        return $"\"{name}\" — {kind}";
    }

    private string FactionStageName(SectorQuotaClaim? claim, Organization org) => claim?.FactionStage switch
    {
        null or 0 => "Надрукуйте накладну в КПК, отримайте польовий комплект через Trade Mall",
        1 when claim is { FactionSurveyBeacon: { } beacon } &&
            FactionSurveyBeaconMatches(claim, beacon) =>
            "Використайте польовий комплект на встановленому орієнтирі",
        1 when claim?.FactionSiteBound == true =>
            "Поновіть орієнтир на раніше обраній ділянці",
        1 => "Оберіть ділянку на планеті експедиції та встановіть орієнтир",
        2 => "Відремонтуйте антену 5 листами сталі",
        3 when FactionMilitaryObjective(org) && claim?.FactionMilitaryPrepared != true =>
            "Додайте 3 листи пласталі для зміцнення захисту",
        3 when FactionScienceObjective(org) && claim?.FactionSciencePrepared != true =>
            "Додайте 3 листи скла для наукового калібрування",
        3 => "Активуйте відремонтовану антену вручну",
        4 => _gameTiming.CurTime >= claim!.FactionSignalReadyAt
            ? "Сигнал стабільний — підтвердьте його біля антени"
            : $"Стабілізація сигналу: {Math.Max(1, (int)Math.Ceiling((claim!.FactionSignalReadyAt - _gameTiming.CurTime).TotalSeconds))} с",
        _ => "Передавання підтверджено"
    };

    private string FactionAntennaDescription(Organization org, SectorQuotaClaim? claim,
        int scenarioVariant = 0)
    {
        var variant = Math.Abs(claim?.FactionScenarioVariant ?? scenarioVariant) % 6;
        var context = new[]
        {
            "Колонізаційні групи втратили стабільний канал зв'язку з віддаленим районом. Потрібен новий наземний ретранслятор.",
            "Нова експедиційна зона залишається поза мережею секторних передавачів. Підготуйте опорний вузол зв'язку.",
            "Старий маршрут передавання сигналів більше не відповідає потребам експедицій. Сформуйте альтернативний канал.",
            "Секторна мережа потребує незалежної точки зв'язку на незаселеній планеті. Забезпечте її введення в роботу.",
            "Далека ділянка не має надійного ретрансляційного покриття. Розгорніть обладнання для майбутніх досліджень.",
            "Нестача проміжних передавачів ускладнює експедиційні маршрути. Створіть новий перевірений вузол.",
        }[variant];
        return context + (claim == null ? "" :
            " Призначена локація: " + claim.FactionPlanetName + ".");
    }

    private bool TryFactionOperationActor(EntityUid actor, Guid orgId, string code,
        out Organization org, out SectorQuotaClaim claim)
    {
        org = default!;
        claim = default!;
        if (!IsOriginalController(actor) || !TryGetCharacter(actor, out var key, out _) ||
            FindMembership(key) is not { Kind: OrganizationKind.Faction } membership ||
            membership.Id != orgId || !_store.Available ||
            membership.InsolvencyState != OrganizationInsolvencyState.Active)
            return false;
        var active = _sectorQuotaClaims.Values.FirstOrDefault(c => c.Organization == orgId &&
            c.Code == code && !c.Completed &&
            _gameTiming.CurTime < c.Deadline);
        if (active == null)
            return false;
        org = membership;
        claim = active;
        return true;
    }

    private void OnFactionDeployAntenna(EntityUid kit, OrganizationFactionFieldKitComponent marker,
        AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;
        // Cargo crates hold an unbound field kit. Its first valid, reachable
        // use binds it to the requesting faction's ONE approved assignment.
        if (marker.Organization == Guid.Empty && marker.OperationCode.Length == 0)
        {
            if (!IsOriginalController(args.User) ||
                !TryGetCharacter(args.User, out var userKey, out _) ||
                FindMembership(userKey) is not { Kind: OrganizationKind.Faction } faction)
                return;
            var awaiting = _sectorQuotaClaims.Values.FirstOrDefault(c =>
                c.Organization == faction.Id && !c.Completed && c.FactionKitOrdered &&
                c.FactionWreckMap == null && c.FactionStage == 0 &&
                c.FactionFieldKit == null && _gameTiming.CurTime < c.Deadline);
            if (awaiting is null)
                return;
            marker.Organization = faction.Id;
            marker.OperationCode = awaiting.Code;
            awaiting.FactionFieldKit = kit;
            awaiting.FactionStage = 1;
        }
        if (!TryFactionOperationActor(args.User, marker.Organization, marker.OperationCode,
                out _, out var claim) ||
            claim.FactionStage != 1 || claim.FactionFieldKit != kit)
            return;

        var actorMap = Transform(args.User).MapUid;
        if (actorMap is not { } mapUid ||
            claim.FactionPlanetMap != mapUid ||
            !HasComp<BiomeComponent>(mapUid))
        {
            args.Handled = true;
            _quotaPopup.PopupEntity("Розгорніть обладнання на планеті, координати якої видано для цього завдання.",
                args.User, args.User);
            return;
        }
        if (!_prototypes.HasIndex<EntityPrototype>(FactionSurveyEntity) ||
            !_prototypes.HasIndex<EntityPrototype>(FactionAntennaEntity))
        {
            args.Handled = true;
            _quotaPopup.PopupEntity("Прототипи польового обладнання не завантажено.",
                args.User, args.User);
            return;
        }

        // Phase 1: the server registers an actual location and creates a
        // physical, task-bound survey unit. The initial map/position comes from
        // a normal reachable in-world interaction, never a UI packet.
        if (args.Target == null)
        {
            args.Handled = true;
            if (claim.FactionSurveyBeacon is { } old && !Deleted(old))
            {
                if (FactionSurveyBeaconMatches(claim, old))
                {
                    _quotaPopup.PopupEntity("Орієнтир уже встановлено. Використайте комплект безпосередньо на ньому.",
                        args.User, args.User);
                    return;
                }
                // An invalid/moved survey unit must not be turned into a relay
                // by dragging it out of the reserved site.
                QueueDel(old);
                claim.FactionSurveyBeacon = null;
            }

            var surveyUnit = Spawn(FactionSurveyEntity, args.ClickLocation);
            if (!surveyUnit.Valid || Deleted(surveyUnit))
                return;
            var transform = Transform(surveyUnit);
            var position = transform.MapPosition.Position;
            if (transform.MapUid != actorMap ||
                (claim.FactionSiteBound && !FactionSitePositionMatches(claim,
                    transform.MapUid, position.X, position.Y)) ||
                (!claim.FactionSiteBound && !FactionSiteAvailable(claim,
                    transform.MapUid, position.X, position.Y)))
            {
                QueueDel(surveyUnit);
                _quotaPopup.PopupEntity(claim.FactionSiteBound
                    ? "Відновити орієнтир можна лише на раніше закріпленій ділянці."
                    : "Майданчик зайнятий. Оберіть місце щонайменше за 20 м від іншої ділянки.",
                    args.User, args.User);
                return;
            }

            // The coordinates are saved only after the spawned entity has
            // passed map checks. Site ownership is never granted at this step.
            if (!claim.FactionSiteBound)
            {
                claim.FactionSiteBound = true;
                claim.FactionSiteMapUid = transform.MapUid;
                claim.FactionSiteMapLabel = $"Мапа {transform.MapID}";
                claim.FactionSiteX = position.X;
                claim.FactionSiteY = position.Y;
            }
            var survey = EnsureComp<OrganizationFactionSurveyBeaconComponent>(surveyUnit);
            survey.Organization = marker.Organization;
            survey.Code = marker.OperationCode;
            survey.Assignment = claim.FactionAssignmentId;
            _organizationMetadata.SetEntityName(surveyUnit,
                $"Польовий орієнтир {claim.FactionSiteCode}");
            claim.FactionSurveyBeacon = surveyUnit;
            _quotaPopup.PopupEntity($"Ділянку {claim.FactionSiteCode} обрано. " +
                "Тепер застосуйте комплект на орієнтирі, щоб підключити ретранслятор.",
                surveyUnit, args.User);
            RefreshFactionOperationConsoles();
            return;
        }

        // Phase 2: only the exact unmodified physical unit can be activated.
        // Clicking another transmitter or a client-provided coordinate cannot
        // consume the kit, switch maps or finish the assignment.
        if (args.Target is not { } beacon || beacon != claim.FactionSurveyBeacon)
            return;
        if (!FactionSurveyBeaconMatches(claim, beacon))
        {
            args.Handled = true;
            _quotaPopup.PopupEntity("Орієнтир переміщено або пошкоджено. " +
                "Поновіть обладнання на координатах закріпленої ділянки.",
                args.User, args.User);
            return;
        }

        args.Handled = true;
        // Only now create the real telecom machine. A survey marker must not
        // silently function as a powered telecom server before commissioning.
        var antenna = Spawn(FactionAntennaEntity, Transform(beacon).Coordinates);
        if (!antenna.Valid || Deleted(antenna))
        {
            _quotaPopup.PopupEntity("Не вдалося встановити ретранслятор. Орієнтир збережено.",
                args.User, args.User);
            return;
        }
        if (!FactionSiteEntityMatches(claim, antenna))
        {
            QueueDel(antenna);
            _quotaPopup.PopupEntity("Обладнання опинилося за межами ділянки. Спробуйте знову.",
                args.User, args.User);
            return;
        }
        var relay = EnsureComp<OrganizationFactionAntennaComponent>(antenna);
        relay.Organization = marker.Organization;
        relay.OperationCode = marker.OperationCode;
        relay.SiteId = claim.FactionAssignmentId;
        _organizationMetadata.SetEntityName(antenna,
            $"Польовий ретранслятор {claim.FactionSiteCode}");
        claim.FactionSurveyBeacon = null;
        claim.FactionAntenna = antenna;
        claim.FactionFieldKit = null;
        claim.FactionStage = 2;
        QueueDel(beacon);
        QueueDel(kit);
        _quotaPopup.PopupEntity($"Обладнання {claim.FactionSiteCode} підключено. " +
            "Потрібно 5 листів сталі для ремонту антени.", antenna, args.User);
        RefreshFactionOperationConsoles();
    }

    private void OnFactionInspectSurveyBeacon(EntityUid beacon,
        OrganizationFactionSurveyBeaconComponent marker, InteractHandEvent args)
    {
        if (args.Handled || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var character, out _) ||
            FindMembership(character) is not { Kind: OrganizationKind.Faction } org ||
            org.Id != marker.Organization)
            return;

        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.FactionAssignmentId == marker.Assignment &&
            c.Organization == marker.Organization && c.Code == marker.Code &&
            !c.Completed && c.FactionSurveyBeacon == beacon);
        if (claim == null)
            return;

        args.Handled = true;
        _quotaPopup.PopupEntity(FactionSurveyBeaconMatches(claim, beacon)
            ? $"Орієнтир {claim.FactionSiteCode}: ділянку закріплено. " +
              "Для підключення використайте польовий комплект на орієнтирі."
            : "Орієнтир переміщено. Потрібно відновити його на закріпленій ділянці.",
            beacon, args.User);
    }

    private void OnFactionRepairAntenna(EntityUid antenna, OrganizationFactionAntennaComponent marker,
        InteractUsingEvent args)
    {
        if (args.Handled || !TryFactionOperationActor(args.User, marker.Organization,
                marker.OperationCode, out var org, out var claim) ||
            claim.FactionStage is not (2 or 3) || claim.FactionAntenna != antenna ||
            marker.SiteId != claim.FactionAssignmentId)
            return;
        args.Handled = true;
        if (!FactionSiteEntityMatches(claim, antenna))
        {
            _quotaPopup.PopupEntity("Антена поза закріпленою ділянкою. Поверніть її на місце.",
                antenna, args.User);
            return;
        }
        if (!TryComp<StackComponent>(args.Used, out var stack))
        {
            _quotaPopup.PopupEntity("Цей етап потребує матеріалів.", antenna, args.User);
            return;
        }
        if (claim.FactionStage == 2)
        {
            if (stack.StackTypeId.Id != "Steel" || stack.Count < FactionAntennaRepairSteel)
            {
                _quotaPopup.PopupEntity("Потрібно 5 листів сталі в одному стосі.", antenna, args.User);
                return;
            }
            _stacks.SetCount(args.Used, stack.Count - FactionAntennaRepairSteel, stack);
            claim.FactionStage = 3;
        }
        else if (FactionMilitaryObjective(org) && !claim.FactionMilitaryPrepared)
        {
            if (stack.StackTypeId.Id != "Plasteel" || stack.Count < 3)
            {
                _quotaPopup.PopupEntity("Для оборонного контуру потрібно 3 листи пласталі.", antenna, args.User);
                return;
            }
            _stacks.SetCount(args.Used, stack.Count - 3, stack);
            claim.FactionMilitaryPrepared = true;
        }
        else if (FactionScienceObjective(org) && !claim.FactionSciencePrepared)
        {
            if (stack.StackTypeId.Id != "Glass" || stack.Count < 3)
            {
                _quotaPopup.PopupEntity("Для наукового контуру потрібно 3 листи скла.", antenna, args.User);
                return;
            }
            _stacks.SetCount(args.Used, stack.Count - 3, stack);
            claim.FactionSciencePrepared = true;
        }
        else
        {
            _quotaPopup.PopupEntity("Матеріали вже встановлені. Активуйте антену порожньою рукою.",
                antenna, args.User);
            return;
        }
        _quotaPopup.PopupEntity(FactionStageName(claim, org), antenna, args.User);
        RefreshFactionOperationConsoles();
    }

    private void OnFactionActivateAntenna(EntityUid antenna, OrganizationFactionAntennaComponent marker,
        InteractHandEvent args)
    {
        if (args.Handled)
            return;

        // Completed structures are inspectable by faction members even after
        // their task has left the list of active assignments.
        if (IsOriginalController(args.User) &&
            TryGetCharacter(args.User, out var character, out _) &&
            FindMembership(character) is { Kind: OrganizationKind.Faction } membership &&
            membership.Id == marker.Organization)
        {
            var completed = _sectorQuotaClaims.Values.FirstOrDefault(c =>
                c.FactionAssignmentId == marker.SiteId && c.Organization == marker.Organization &&
                c.Code == marker.OperationCode && c.Completed && c.FactionAntenna == antenna);
            if (completed != null)
            {
                args.Handled = true;
                _quotaPopup.PopupEntity(FactionSiteEntityMatches(completed, antenna)
                    ? $"Вузол {completed.FactionSiteCode} зареєстровано. " +
                      "Ретранслятор присутній у цьому раунді."
                    : "Зареєстрований ретранслятор змінив місце. Поточний сигнал не підтверджено.",
                    antenna, args.User);
                return;
            }
        }
        if (!TryFactionOperationActor(args.User, marker.Organization,
                marker.OperationCode, out var org, out var claim) ||
            claim.FactionAntenna != antenna || claim.FactionStage is < 3 or > 4 ||
            marker.SiteId != claim.FactionAssignmentId)
            return;
        if (Transform(args.User).MapUid != Transform(antenna).MapUid)
            return;
        args.Handled = true;
        if (!FactionSiteEntityMatches(claim, antenna))
        {
            _quotaPopup.PopupEntity("Передавання можливе лише із закріпленої ділянки.",
                antenna, args.User);
            return;
        }
        if (claim.FactionStage == 3 &&
            ((FactionMilitaryObjective(org) && !claim.FactionMilitaryPrepared) ||
             (FactionScienceObjective(org) && !claim.FactionSciencePrepared)))
        {
            _quotaPopup.PopupEntity(FactionStageName(claim, org), antenna, args.User);
            return;
        }
        if (claim.FactionStage == 3)
        {
            claim.FactionStage = 4;
            var signalSeconds = FactionSignalSeconds(org, claim);
            claim.FactionSignalReadyAt = _gameTiming.CurTime + TimeSpan.FromSeconds(signalSeconds);
            _quotaPopup.PopupEntity($"Сигнал запущено. Час стабілізації: {signalSeconds} с.",
                antenna, args.User);
            RefreshFactionOperationConsoles();
            return;
        }
        if (_gameTiming.CurTime < claim.FactionSignalReadyAt)
        {
            var remaining = (int)Math.Ceiling((claim.FactionSignalReadyAt -
                _gameTiming.CurTime).TotalSeconds);
            _quotaPopup.PopupEntity($"Стабілізація сигналу: ще {remaining} с.", antenna, args.User);
            return;
        }
        // Never consume economic market capacity for faction work.
        var points = FactionAntennaAuthorityReward + Math.Min(3,
            FactionFocusList(org).Count(IsFactionAntennaFocus));
        if (!FactionSiteEntityMatches(claim, antenna))
        {
            claim.FactionStage = 3;
            claim.FactionSignalReadyAt = TimeSpan.Zero;
            _quotaPopup.PopupEntity("Сигнал втрачено. Поверніть антену на ділянку та повторіть передавання.",
                antenna, args.User);
            return;
        }
        if (org.FactionNodes.Count >= MaxFactionNetworkNodes)
        {
            _quotaPopup.PopupEntity("Реєстр вузлів заповнений. Потрібне розширення місткості в наступній версії.",
                antenna, args.User);
            return;
        }
        if (org.FactionAuthorityPoints > long.MaxValue - points ||
            org.FactionOperationsCompleted == int.MaxValue ||
            org.FactionNodes.Any(n => n.Id == claim.FactionAssignmentId) ||
            !Commit(() =>
            {
                org.FactionAuthorityPoints += points;
                org.FactionOperationsCompleted++;
                org.SectorInfluenceBp += Math.Min(24, Math.Max(0,
                    MarketCapacityBp - _organizations.Values.Sum(o => o.SectorInfluenceBp)));
                org.FactionNodes.Add(new OrganizationFactionNodeRecord
                {
                    Id = claim.FactionAssignmentId,
                    AssignmentCode = claim.Code,
                    SiteCode = claim.FactionSiteCode,
                    Kind = FactionNodeKindFor(org),
                    EstablishedUtc = DateTime.UtcNow
                });
            }))
        {
            _quotaPopup.PopupEntity("Підтвердження очікує доступності реєстру фракцій.", antenna, args.User);
            return;
        }
        claim.FactionStage = 5;
        claim.Completed = true;
        ScheduleNextFactionTask(org);
        claim.DeliveredByPost[0] = 4;
        _quotaPopup.PopupEntity($"Завдання {claim.Code} виконано! " +
            $"Вузол {claim.FactionSiteCode} зареєстровано. Авторитет: +{points}.",
            args.User, args.User);
        NotifyFactionTaskCompleted(args.User);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Faction relay established: {claim.Code}, org={org.Id}, node={claim.FactionAssignmentId}, points={points}");
        RefreshFactionOperationConsoles();
    }

    private void RefreshFactionOperationConsoles()
    {
        var consoles = EntityQueryEnumerator<OrganizationConsoleComponent, UserInterfaceComponent>();
        while (consoles.MoveNext(out var console, out _, out var interfaceComp))
        {
            foreach (var session in _players.Sessions)
            {
                if (session.Status != SessionStatus.InGame ||
                    session.AttachedEntity is not { Valid: true } actor ||
                    !_ui.IsUiOpen((console, interfaceComp), OrganizationConsoleUiKey.Key, actor))
                    continue;
                Refresh(console, actor);
            }
        }
    }

    private void RemoveFactionOperationObjects(SectorQuotaClaim claim)
    {
        if (claim.FactionFieldKit is { } kit && !Deleted(kit))
            QueueDel(kit);
        if (claim.FactionSurveyBeacon is { } survey && !Deleted(survey))
            QueueDel(survey);
        claim.FactionSurveyBeacon = null;
        // Registered nodes are durable records, not persisted entity/grid UIDs.
        // Do not delete completed structures in a running expedition. Their
        // parent map is retired by the expedition system itself.
        if (!claim.Completed && claim.FactionAntenna is { } antenna && !Deleted(antenna))
            QueueDel(antenna);
    }

    private void ResetFactionOperationsForRound()
    {
        foreach (var claim in _sectorQuotaClaims.Values)
            if (claim.FactionStage != 0)
                RemoveFactionOperationObjects(claim);
    }
}
