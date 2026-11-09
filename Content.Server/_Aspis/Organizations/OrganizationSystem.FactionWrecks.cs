using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Events;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Mobs.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Tools.Systems;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Small, per-assignment exploration maps. They are *not* general sector POIs
/// and are not permanent territory. Server-owned map/grid and a real recorder
/// prove that the player actually visited the object. No player-supplied paths.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const string FactionWreckRecorderEntity = "AspisFactionWreckRecorder";
    private const string FactionWreckInspectionEntity = "AspisFactionWreckInspectionTerminal";
    private const string FactionWreckSecurityPanelEntity = "AspisFactionWreckSecurityPanel";
    private const string FactionWreckScientificSensorEntity = "AspisFactionWreckScientificSensor";
    private const string FactionWreckEmergencyControllerEntity = "AspisFactionWreckEmergencyController";
    [Dependency] private SharedToolSystem _factionInspectionTools = default!;
    private const int FactionWreckScienceGlass = 2;
    private const int FactionWreckAidSteel = 3;
    private const string FactionCoordinatesDiskProto = "CoordinatesDisk";
    private static readonly TimeSpan FactionDiskPrintCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FactionWreckLaunchDelay = TimeSpan.FromSeconds(90);
    // With ~11 players, do not build one huge abandoned station per offer. This
    // is a total-per-round limit, including completed/expired sites: keeping
    // maps to the round end avoids deleting a shuttle or stranded character.
    private const int MaxFactionWreckMapsPerRound = 3;
    // More demanding expeditions pay for a verified objective, NOT for kills.
    private const int FactionWreckAuthorityBaseReward = 6;
    private const int MaxFactionWreckHostilesPerSite = 10;
    private const int MinimumHostilesForSite = 3;
    private const int MaxFactionFindings = 256;
    private int _factionWreckMapsCreatedThisRound;
    private enum FactionWreckActivityKind : byte
    {
        Survey,
        Patrol,
        Scientific,
        SectorAid
    }

    // A faction's declared interests change the *actual* site objective, not
    // just the payout or title. The choice is frozen when it accepts the task.
    private static FactionWreckActivityKind FactionWreckActivityFor(OrganizationFactionFocus focus) =>
        focus switch
        {
            OrganizationFactionFocus.Security or OrganizationFactionFocus.Defense or
                OrganizationFactionFocus.Militarist => FactionWreckActivityKind.Patrol,
            OrganizationFactionFocus.Humanitarian or OrganizationFactionFocus.Medical or
                OrganizationFactionFocus.Civic => FactionWreckActivityKind.SectorAid,
            OrganizationFactionFocus.Scientific or OrganizationFactionFocus.Technological or
                OrganizationFactionFocus.Exploration => FactionWreckActivityKind.Scientific,
            _ => FactionWreckActivityKind.Survey
        };

    // A sector dispatch is a *situation*, not the name of a generic activity.
    // The variant is chosen once per quota offer and frozen in the claim.
    private static string FactionWreckTitle(OrganizationFactionFocus focus,
        SectorQuotaClaim? claim, int scenarioVariant = 0)
    {
        var kind = claim?.FactionWreckActivity ?? FactionWreckActivityFor(focus);
        var variant = Math.Abs(claim?.FactionScenarioVariant ?? scenarioVariant) % 6;
        return kind switch
        {
            FactionWreckActivityKind.Patrol => new[]
            {
                "Перевірка аварійного сигналу", "Огляд охоронного периметра",
                "Відновлення журналу патруля", "Перевірка аварійного відсіку",
                "Технічна інспекція комплексу", "Оцінка автономної загрози"
            }[variant],
            FactionWreckActivityKind.Scientific => new[]
            {
                "Відновлення вимірювань", "Аналіз невідомого сигналу",
                "Перевірка наукового експерименту", "Відновлення дослідницького протоколу",
                "Аналіз архіву вимірювань", "Польова перевірка сенсорів"
            }[variant],
            FactionWreckActivityKind.SectorAid => new[]
            {
                "Перевірка енергозабезпечення", "Відновлення технічного зв'язку",
                "Діагностика резервної системи", "Інспекція віддаленого аванпосту",
                "Виконання заявки на ремонт", "Відновлення систем постачання"
            }[variant],
            _ => new[]
            {
                "Пошук бортового журналу", "Евакуація аварійних записів",
                "Ідентифікація сигнального маяка", "Обстеження невідомого уламка",
                "Розслідування зниклого рейсу", "Встановлення джерела телеметрії"
            }[variant]
        };
    }

    private static string FactionWreckTaskTitle(FactionWreckActivityKind kind) => kind switch
    {
        FactionWreckActivityKind.Patrol => "Патрулювання аварійного об'єкта",
        FactionWreckActivityKind.Scientific => "Дослідження покинутого комплексу",
        FactionWreckActivityKind.SectorAid => "Перевірка аварійного забезпечення",
        _ => "Пошук аварійного реєстратора"
    };

    private static string FactionWreckWorkflow(FactionWreckActivityKind kind) => kind switch
    {
        FactionWreckActivityKind.Patrol => "Секторне патрулювання",
        FactionWreckActivityKind.Scientific => "Наукова експедиція",
        FactionWreckActivityKind.SectorAid => "Допомога сектору",
        _ => "Пошукова експедиція"
    };

    private static string FactionWreckTerminalName(FactionWreckActivityKind kind) => kind switch
    {
        FactionWreckActivityKind.Patrol => "Пульт аварійної сигналізації",
        FactionWreckActivityKind.Scientific => "Сенсор дослідницького комплексу",
        FactionWreckActivityKind.SectorAid => "Термінал аварійного життєзабезпечення",
        _ => "Контрольний маяк об'єкта"
    };

    private static string FactionWreckTerminalAction(FactionWreckActivityKind kind) => kind switch
    {
        FactionWreckActivityKind.Patrol => "Перевірте аварійний пульт викруткою (8 секунд)",
        FactionWreckActivityKind.Scientific =>
            "Замініть оптичний блок 2 листами скла, потім відкалібруйте сенсор мультитулом (10 секунд)",
        FactionWreckActivityKind.SectorAid =>
            "Відновіть корпус 3 листами сталі, потім перезапустіть контролер гайковим ключем (10 секунд)",
        _ => "Звірте дані контрольного маяка вручну"
    };

    private static string FactionWreckInspectionPrototype(FactionWreckActivityKind kind) => kind switch
    {
        FactionWreckActivityKind.Patrol => FactionWreckSecurityPanelEntity,
        FactionWreckActivityKind.Scientific => FactionWreckScientificSensorEntity,
        FactionWreckActivityKind.SectorAid => FactionWreckEmergencyControllerEntity,
        _ => FactionWreckInspectionEntity
    };

    private static string FactionWreckInspectionStage(SectorQuotaClaim claim)
    {
        if (claim.FactionWreckInspectionComplete)
            return "Огляд завершено. Знайдіть аварійний реєстратор";
        if (!claim.FactionWreckMaterialsPrepared)
            return claim.FactionWreckActivity switch
            {
                FactionWreckActivityKind.Scientific => "Сенсор пошкоджено: потрібні 2 листи скла",
                FactionWreckActivityKind.SectorAid => "Контролер пошкоджено: потрібні 3 листи сталі",
                _ => "Огляньте контрольне обладнання"
            };
        return claim.FactionWreckActivity switch
        {
            FactionWreckActivityKind.Patrol => "Перевірте пульт викруткою: 8 секунд",
            FactionWreckActivityKind.Scientific => "Оптичний блок замінено. Калібруйте мультитулом: 10 секунд",
            FactionWreckActivityKind.SectorAid => "Корпус відновлено. Перезапустіть гайковим ключем: 10 секунд",
            _ => "Звірте дані маяка вручну"
        };
    }

    private static string FactionWreckStageName(SectorQuotaClaim? claim) => claim switch
    {
        null => "Прийміть завдання для друку диска координат",
        { FactionWreckReturned: true, Completed: false } => "Маршрут закрито до завершення перевірки",
        { Completed: true } => "Матеріали перевірки успішно заархівовано",
        { FactionWreckMap: null } => "Експедиційний об'єкт не створено",
        { FactionWreckInspectionComplete: false } => FactionWreckInspectionStage(claim!),
        _ => "Знайдіть аварійний реєстратор і зчитайте його журнал"
    };

    private static string FactionWreckLocation(SectorQuotaClaim claim) =>
        claim.FactionWreckMap != null
            ? $"FTL: {claim.FactionWreckName} · окрема мапа поточного раунду"
            : "Об'єкт ще не створено";

    private static string FactionWreckDescription(OrganizationFactionFocus focus,
        SectorQuotaClaim? claim, int scenarioVariant = 0)
    {
        var kind = claim?.FactionWreckActivity ?? FactionWreckActivityFor(focus);
        var variant = Math.Abs(claim?.FactionScenarioVariant ?? scenarioVariant) % 6;
        var context = kind switch
        {
            FactionWreckActivityKind.Patrol => new[]
            {
                "Віддалений об'єкт кілька разів передав сигнал тривоги, після чого замовк. Перевірте системи безпеки та збережіть журнал подій.",
                "Секторна автоматика помітила несанкціоноване відключення охоронних контурів. Необхідний огляд джерела тривоги.",
                "Патрульна мережа втратила зв'язок із черговим постом. З'ясуйте стан його обладнання та причину мовчання.",
                "З аварійного відсіку надходять несумісні показники датчиків. Перевірте панель безпеки і реєстратор.",
                "Черговий комплекс перестав передавати дані про власну справність. Потрібна незалежна перевірка.",
                "До сектора надійшло попередження про невстановлені автономні загрози. Огляньте аварійний вузол.",
            }[variant],
            FactionWreckActivityKind.Scientific => new[]
            {
                "Покинутий дослідницький об'єкт зберіг незавершені вимірювання. Відновіть сенсор і отримайте вихідні дані.",
                "Зафіксовано незвичну частоту від віддаленого комплексу. Вченим потрібен справний датчик і польовий журнал.",
                "Автоматичний дослід обірвався без підсумкового звіту. Перевірте обладнання й архівуйте результати.",
                "У старому науковому реєстрі знайдено посилання на втрачену серію вимірювань. Спробуйте її відновити.",
                "Віддалена станція припинила передавати дані про зовнішнє середовище. Необхідне повторне калібрування.",
                "Секторний аналіз виявив невідповідність між показниками старих сенсорів. Перевірте першоджерело.",
            }[variant],
            FactionWreckActivityKind.SectorAid => new[]
            {
                "Аварійний комплекс залишився без підтвердженого енергозабезпечення. Відновіть контролер і збережіть дані про несправність.",
                "До системи підтримки надійшла неповна заявка про відмову життєзабезпечення. Потрібна польова перевірка.",
                "Віддалений пункт повідомив про падіння резервної потужності. Перевірте можливість відновлення обладнання.",
                "Зі старого аванпосту давно не надходять технічні звіти. Відновіть аварійний контролер.",
                "У секторному журналі залишився незакритий запит на допомогу. Знайдіть причину несправності.",
                "Локальна мережа постачання втратила зв'язок із одним із вузлів. Потрібна технічна розвідка.",
            }[variant],
            _ => new[]
            {
                "Останній екіпаж залишив уривчастий сигнал із невідомого об'єкта. Знайдіть і збережіть його бортовий журнал.",
                "Секторний архів містить координатний слід без відомостей про судно. З'ясуйте його походження.",
                "Пошукова мережа повторно прийняла сигнал старого маяка. Необхідно встановити джерело.",
                "Невідомий уламок з'явився в останньому навігаційному зведенні. Перевірте його реєстратор.",
                "Втрачений рейс залишив неповні телеметричні записи. Експедиції доручено отримати первинні дані.",
                "Із віддаленого району надходять переривчасті пакети даних. Знайдіть носій первинного запису.",
            }[variant]
        };
        return claim == null ? context : context +
            $" Місце: {claim.FactionWreckName}. " +
            $"Відомі ризики: {claim.FactionWreckHazard}.";
    }

    private bool TryCreateFactionWreckSite(SectorQuotaClaim claim)
    {
        claim.FactionCreationError = string.Empty;
        if (_factionWreckMapsCreatedThisRound >= MaxFactionWreckMapsPerRound)
        {
            claim.FactionCreationError = "Ліміт: у цьому раунді вже створено всі три великі космічні експедиційні мапи.";
            return false;
        }
        if (!_prototypes.HasIndex<EntityPrototype>(FactionWreckRecorderEntity) ||
            !_prototypes.HasIndex<EntityPrototype>(FactionWreckInspectionEntity) ||
            !_prototypes.HasIndex<EntityPrototype>(FactionWreckSecurityPanelEntity) ||
            !_prototypes.HasIndex<EntityPrototype>(FactionWreckScientificSensorEntity) ||
            !_prototypes.HasIndex<EntityPrototype>(FactionWreckEmergencyControllerEntity))
        {
            claim.FactionCreationError = "Не завантажено один із прототипів експедиційного обладнання.";
            return false;
        }

        var sites = _prototypes.EnumeratePrototypes<AspisFactionExpeditionSitePrototype>()
            .OrderBy(s => s.ID, StringComparer.Ordinal).ToArray();
        if (sites.Length == 0)
        {
            claim.FactionCreationError = "Каталог експедиційних об'єктів порожній або не завантажений.";
            return false;
        }

        // A failed template is excluded for this attempt. The next entry may
        // still work if an author removed one of the configured YAML maps.
        var first = _random.Next(0, sites.Length);
        var lastFailure = "немає придатних експедиційних шаблонів";
        for (var attempt = 0; attempt < sites.Length; attempt++)
        {
            var selected = sites[(first + attempt) % sites.Length];
            EntityUid? createdMap = null;
            var succeeded = false;
            try
            {
                createdMap = _organizationMapSystem.CreateMap(out var mapId);
                if (!_organizationGridLoader.TryLoadGrid(mapId, selected.GridPath,
                        out var result) || result is null)
                {
                    lastFailure = $"MapLoader відхилив {selected.GridPath}";
                    _log.GetSawmill("organizations.faction-sites").Warning(lastFailure);
                    continue;
                }

                var gridUid = result.Value.Owner;
                if (Transform(gridUid).MapUid != createdMap ||
                    !TryComp<MapGridComponent>(gridUid, out var grid))
                {
                    lastFailure = $"Завантажений грід {selected.ID} має неправильну мапу або не має MapGridComponent";
                    _log.GetSawmill("organizations.faction-sites").Warning(lastFailure);
                    continue;
                }

                // Select a substantial floor-connected component and place the
                // objectives far apart by tile traversal (not 6m apart).
                // A site with a tiny navigable floor or a collapsed island is
                // not suitable for a long expedition; try the next template.
                // This checks connected floor tiles, not doors/atmos: these are
                // features of the authored YAML map and must be play-tested.
                // One obsolete NPC prototype must not invalidate an otherwise
                // usable expedition: use the available palette for this build.
                var availableHostiles = selected.HostilePrototypes
                    .Where(proto => _prototypes.HasIndex<EntityPrototype>(proto)).ToArray();
                if (selected.HostileCount is < MinimumHostilesForSite or > MaxFactionWreckHostilesPerSite)
                {
                    lastFailure = $"{selected.ID}: недопустима кількість ворогів {selected.HostileCount}";
                    _log.GetSawmill("organizations.faction-sites").Warning(lastFailure);
                    continue;
                }
                if (availableHostiles.Length == 0)
                {
                    lastFailure = $"{selected.ID}: відсутні прототипи ворогів із каталогу";
                    _log.GetSawmill("organizations.faction-sites").Warning(lastFailure);
                    continue;
                }
                if (!TryPlanFactionWreckLayout(gridUid, grid,
                        Math.Max(100, selected.MinimumConnectedFloor),
                        // Difficult and critical tasks must traverse farther
                        // and face more enemies. Map generator still validates
                        // viable positions; never spawn enemies outside floor.
                        Math.Max(16 + 8 * Math.Max(0, claim.DifficultySlot - 1),
                            selected.MinimumTraversalTiles),
                        Math.Min(MaxFactionWreckHostilesPerSite,
                            selected.HostileCount + 2 * Math.Max(0, claim.DifficultySlot - 1)),
                        out var floorPos, out var inspectionPos, out var hostilePositions,
                        out var layoutFailure))
                {
                    lastFailure = $"{selected.ID}: {layoutFailure}";
                    _log.GetSawmill("organizations.faction-sites").Warning(lastFailure);
                    continue;
                }


                var recorder = Spawn(FactionWreckRecorderEntity,
                    new EntityCoordinates(gridUid, floorPos));
                if (!recorder.Valid || Deleted(recorder) ||
                    Transform(recorder).GridUid != gridUid)
                {
                    if (recorder.Valid && !Deleted(recorder))
                        QueueDel(recorder);
                    continue;
                }

                var comp = EnsureComp<AspisFactionWreckRecorderComponent>(recorder);
                comp.Organization = claim.Organization;
                comp.Assignment = claim.FactionAssignmentId;
                comp.Code = claim.Code;

                var terminal = Spawn(FactionWreckInspectionPrototype(claim.FactionWreckActivity),
                    new EntityCoordinates(gridUid, inspectionPos));
                if (!terminal.Valid || Deleted(terminal) ||
                    Transform(terminal).GridUid != gridUid)
                {
                    if (terminal.Valid && !Deleted(terminal))
                        QueueDel(terminal);
                    continue;
                }
                var terminalMarker = EnsureComp<AspisFactionWreckInspectionComponent>(terminal);
                terminalMarker.Organization = claim.Organization;
                terminalMarker.Assignment = claim.FactionAssignmentId;
                terminalMarker.Code = claim.Code;
                _organizationMetadata.SetEntityName(terminal,
                    FactionWreckTerminalName(claim.FactionWreckActivity));
                _organizationMetadata.SetEntityDescription(terminal,
                    FactionWreckTerminalAction(claim.FactionWreckActivity) +
                    ". Спочатку перевірте цей об'єкт, потім знайдіть аварійний реєстратор.");
                var name = $"{selected.Name} {claim.FactionSiteCode}";
                _organizationMetadata.SetEntityName(createdMap.Value, name);
                _organizationMetadata.SetEntityName(gridUid, name);
                _organizationMetadata.SetEntityName(recorder, $"Аварійний реєстратор {claim.FactionSiteCode}");
                // Private expedition: the shared shuttle FTL gate checks the
                // physical disk; Aspis additionally checks its assignment and
                // that departure originates near Trade Mall.
                if (!_organizationShuttle.TryAddFTLDestination(mapId, true, true,
                        false, out _))
                {
                    lastFailure = $"Не вдалося зареєструвати FTL для {selected.ID}";
                    _log.GetSawmill("organizations.faction-sites").Warning(lastFailure);
                    continue;
                }
                var route = EnsureComp<AspisFactionExpeditionDestinationComponent>(createdMap.Value);
                route.Organization = claim.Organization;
                route.Assignment = claim.FactionAssignmentId;
                route.LaunchReadyAt = _gameTiming.CurTime + FactionWreckLaunchDelay;
                claim.FactionWreckLaunchReadyAt = route.LaunchReadyAt;
                claim.FactionWreckMap = createdMap;
                claim.FactionWreckGrid = gridUid;
                claim.FactionWreckRecorder = recorder;
                claim.FactionWreckRecorderPosition = floorPos;
                claim.FactionWreckInspectionTerminal = terminal;
                claim.FactionWreckInspectionPosition = inspectionPos;
                claim.FactionWreckMaterialsPrepared = claim.FactionWreckActivity is
                    FactionWreckActivityKind.Patrol or FactionWreckActivityKind.Survey;
                claim.FactionWreckName = name;
                claim.FactionWreckHazard = selected.HazardDescription;
                claim.FactionWreckMinimumRouteTiles = Math.Max(16, selected.MinimumTraversalTiles);
                for (var hostileIndex = 0; hostileIndex < hostilePositions.Count; hostileIndex++)
                {
                    // Rotating the enemy palette adds variety while keeping the
                    // encounter budget fixed by the chosen site, not player count.
                    var proto = availableHostiles[
                        (hostileIndex + _random.Next(availableHostiles.Length)) %
                        availableHostiles.Length];
                    claim.FactionWreckHostilePlan.Add((proto, hostilePositions[hostileIndex]));
                }
                claim.FactionStage = 1;
                _factionWreckMapsCreatedThisRound++;
                succeeded = true;
                _adminLog.Add(LogType.Action, LogImpact.Low,
                    $"Faction survey grid created: {claim.Code}, map={selected.GridPath}, org={claim.Organization}");
                return true;
            }
            catch (Exception ex)
            {
                lastFailure = $"{selected.ID}: {ex.GetType().Name}";
                _log.GetSawmill("organizations.faction-sites").Error(
                    $"Unable to load survey site {selected.ID}: {ex}");
            }
            finally
            {
                if (!succeeded && createdMap is { } uid && !TerminatingOrDeleted(uid))
                    QueueDel(uid);
            }
        }
        claim.FactionCreationError =
            "Не вдалося підготувати жоден великий експедиційний об'єкт. " +
            $"Остання причина: {lastFailure}. Деталі в журналі organizations.faction-sites.";
        return false;
    }

    /// <summary>
    /// Validate the person, organization, task, original grid and fixed position
    /// before either consuming materials or finishing a delayed tool interaction.
    /// This is also called AGAIN after a do-after has completed.
    /// </summary>
    private bool TryResolveFactionWreckTerminal(EntityUid terminal,
        AspisFactionWreckInspectionComponent marker, EntityUid user,
        out SectorQuotaClaim claim, out Organization org)
    {
        claim = null!;
        org = null!;
        if (!IsOriginalController(user) ||
            !TryGetCharacter(user, out var character, out _) ||
            FindMembership(character) is not { Kind: OrganizationKind.Faction } faction ||
            faction.Id != marker.Organization ||
            faction.InsolvencyState != OrganizationInsolvencyState.Active || !_store.Available)
            return false;

        var found = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.FactionAssignmentId == marker.Assignment && c.Code == marker.Code &&
            c.Organization == faction.Id && c.FactionWreckInspectionTerminal == terminal &&
            !c.Completed && !c.FactionWreckReturned);
        if (found == null || _gameTiming.CurTime >= found.Deadline ||
            found.FactionWreckGrid is not { } grid ||
            found.FactionWreckMap is not { } map ||
            Deleted(grid) || Deleted(map) || Deleted(terminal) || Deleted(user) ||
            Transform(terminal).GridUid != grid || Transform(terminal).MapUid != map ||
            Vector2.DistanceSquared(Transform(terminal).LocalPosition,
                found.FactionWreckInspectionPosition) > 0.25f ||
            Transform(user).GridUid != grid || Transform(user).MapUid != map)
            return false;

        claim = found;
        org = faction;
        return true;
    }

    private void FinishFactionWreckInspection(EntityUid terminal, EntityUid user,
        SectorQuotaClaim claim, Organization org)
    {
        if (claim.FactionWreckInspectionComplete)
            return;
        claim.FactionWreckInspectionComplete = true;
        claim.FactionStage = 2;
        claim.DeliveredByPost[0] = 2;
        _organizationMetadata.SetEntityDescription(terminal,
            "Обладнання перевірено. Дані огляду закріплено за завданням " + claim.Code + ".");
        _quotaPopup.PopupEntity($"{FactionWreckTerminalName(claim.FactionWreckActivity)}: " +
            "перевірку завершено. Знайдіть аварійний реєстратор.", terminal, user);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Faction field work completed: {claim.Code}, kind={claim.FactionWreckActivity}, org={org.Id}");
        RefreshFactionOperationConsoles();
    }

    // Empty-hand inspections only apply to the simple search objective.
    // Technical, research and safety activities need their *own* physical work.
    private void OnFactionInspectWreckTerminal(EntityUid terminal,
        AspisFactionWreckInspectionComponent marker, InteractHandEvent args)
    {
        if (args.Handled || !TryResolveFactionWreckTerminal(terminal, marker, args.User,
                out var claim, out var org))
            return;
        args.Handled = true;
        if (claim.FactionWreckInspectionComplete)
        {
            _quotaPopup.PopupEntity("Перевірку вже завершено. Знайдіть аварійний реєстратор.",
                terminal, args.User);
            return;
        }
        if (claim.FactionWreckActivity != FactionWreckActivityKind.Survey)
        {
            _quotaPopup.PopupEntity(FactionWreckInspectionStage(claim), terminal, args.User);
            return;
        }
        FinishFactionWreckInspection(terminal, args.User, claim, org);
    }

    // Physical supplies are verified and removed by the server only ONCE.
    // A timed tool interaction is required after the supply stage (if any).
    private void OnFactionServiceWreckTerminal(EntityUid terminal,
        AspisFactionWreckInspectionComponent marker, InteractUsingEvent args)
    {
        if (args.Handled || !TryResolveFactionWreckTerminal(terminal, marker, args.User,
                out var claim, out _))
            return;
        args.Handled = true;
        if (claim.FactionWreckInspectionComplete)
        {
            _quotaPopup.PopupEntity("Обладнання вже перевірено.", terminal, args.User);
            return;
        }
        if (claim.FactionWreckActivity == FactionWreckActivityKind.Survey)
        {
            _quotaPopup.PopupEntity("Звірте маяк порожньою рукою.", terminal, args.User);
            return;
        }
        if (!claim.FactionWreckMaterialsPrepared)
        {
            var requiredKind = claim.FactionWreckActivity == FactionWreckActivityKind.Scientific
                ? "Glass" : "Steel";
            var requiredCount = claim.FactionWreckActivity == FactionWreckActivityKind.Scientific
                ? FactionWreckScienceGlass : FactionWreckAidSteel;
            if (!TryComp<StackComponent>(args.Used, out var stack) ||
                stack.StackTypeId.Id != requiredKind || stack.Count < requiredCount)
            {
                _quotaPopup.PopupEntity(FactionWreckInspectionStage(claim), terminal, args.User);
                return;
            }
            _stacks.SetCount(args.Used, stack.Count - requiredCount, stack);
            claim.FactionWreckMaterialsPrepared = true;
            _organizationMetadata.SetEntityDescription(terminal, FactionWreckInspectionStage(claim));
            _quotaPopup.PopupEntity(FactionWreckInspectionStage(claim), terminal, args.User);
            RefreshFactionOperationConsoles();
            return;
        }

        var started = claim.FactionWreckActivity switch
        {
            FactionWreckActivityKind.Patrol => _factionInspectionTools.UseTool(args.Used,
                args.User, terminal, 8f, "Screwing", new AspisFactionWreckToolCompletedEvent()),
            FactionWreckActivityKind.Scientific => _factionInspectionTools.UseTool(args.Used,
                args.User, terminal, 10f, "Pulsing", new AspisFactionWreckToolCompletedEvent()),
            FactionWreckActivityKind.SectorAid => _factionInspectionTools.UseTool(args.Used,
                args.User, terminal, 10f, "Anchoring", new AspisFactionWreckToolCompletedEvent()),
            _ => false
        };
        if (!started)
            _quotaPopup.PopupEntity(FactionWreckInspectionStage(claim), terminal, args.User);
    }

    private void OnFactionWreckToolCompleted(EntityUid terminal,
        AspisFactionWreckInspectionComponent marker, AspisFactionWreckToolCompletedEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target != terminal ||
            args.Used is not { } tool ||
            !TryResolveFactionWreckTerminal(terminal, marker, args.User,
                out var claim, out var org) ||
            claim.FactionWreckInspectionComplete || !claim.FactionWreckMaterialsPrepared)
            return;

        var validTool = claim.FactionWreckActivity switch
        {
            FactionWreckActivityKind.Patrol => _factionInspectionTools.HasQuality(tool, "Screwing"),
            FactionWreckActivityKind.Scientific => _factionInspectionTools.HasQuality(tool, "Pulsing"),
            FactionWreckActivityKind.SectorAid => _factionInspectionTools.HasQuality(tool, "Anchoring"),
            _ => false
        };
        if (!validTool)
            return;
        args.Handled = true;
        FinishFactionWreckInspection(terminal, args.User, claim, org);
    }

    private void OnFactionReadWreckRecorder(EntityUid recorder,
        AspisFactionWreckRecorderComponent marker, InteractHandEvent args)
    {
        if (args.Handled || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var character, out _) ||
            FindMembership(character) is not { Kind: OrganizationKind.Faction } org ||
            org.Id != marker.Organization || org.InsolvencyState != OrganizationInsolvencyState.Active ||
            !_store.Available)
            return;

        // The recorder, grid, map and assignment must all match. Reading a
        // duplicate, another organization's recorder, or remote UI cannot pay.
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.FactionAssignmentId == marker.Assignment && c.Code == marker.Code &&
            c.Organization == org.Id && c.FactionWreckRecorder == recorder &&
            !c.Completed && !c.FactionWreckReturned);
        if (claim != null && !claim.FactionWreckInspectionComplete &&
            claim.FactionWreckGrid is { } activeGrid &&
            claim.FactionWreckMap is { } activeMap &&
            !Deleted(activeGrid) && !Deleted(activeMap) &&
            Transform(args.User).GridUid == activeGrid &&
            Transform(recorder).GridUid == activeGrid)
        {
            args.Handled = true;
            _quotaPopup.PopupEntity("Спочатку перевірте контрольний термінал цього об'єкта.",
                recorder, args.User);
            return;
        }
        if (claim == null || _gameTiming.CurTime >= claim.Deadline ||
            claim.FactionWreckGrid is not { } grid ||
            claim.FactionWreckMap is not { } map ||
            !claim.FactionWreckInspectionComplete ||
            claim.FactionWreckInspectionTerminal is not { } terminal ||
            Deleted(grid) || Deleted(map) || Deleted(recorder) || Deleted(terminal) ||
            Transform(terminal).GridUid != grid || Transform(terminal).MapUid != map ||
            Vector2.DistanceSquared(Transform(terminal).LocalPosition,
                claim.FactionWreckInspectionPosition) > 0.25f ||
            Transform(recorder).GridUid != grid || Transform(recorder).MapUid != map ||
            Vector2.DistanceSquared(Transform(recorder).LocalPosition,
                claim.FactionWreckRecorderPosition) > 0.25f ||
            Transform(args.User).GridUid != grid || Transform(args.User).MapUid != map)
            return;

        args.Handled = true;
        var authorityReward = FactionWreckAuthorityBaseReward +
            claim.FactionWreckHostilePlan.Count / 2 +
            4 * Math.Max(0, claim.DifficultySlot - 1);
        if (org.FactionAuthorityPoints > long.MaxValue - authorityReward ||
            org.FactionOperationsCompleted == int.MaxValue ||
            org.FactionFindings.Count >= MaxFactionFindings ||
            org.FactionFindings.Any(f => f.Id == claim.FactionAssignmentId) ||
            !Commit(() =>
            {
                org.FactionAuthorityPoints += authorityReward;
                org.FactionOperationsCompleted++;
                // Verified salvage information contributes a very small, bounded
                // amount of sector presence, never an instant percentage monopoly.
                // Survey results reward sustained activity, not a 100%
                // takeover. Grade II gives 0.16%, III 0.22%, IV 0.28%.
                org.SectorInfluenceBp += Math.Min(16 +
                    6 * Math.Max(0, claim.DifficultySlot - 1), Math.Max(0,
                    MarketCapacityBp - _organizations.Values.Sum(o => o.SectorInfluenceBp)));
                org.FactionFindings.Add(new OrganizationFactionFindingRecord
                {
                    Id = claim.FactionAssignmentId,
                    SiteCode = claim.FactionSiteCode,
                    SiteName = $"{FactionWreckWorkflow(claim.FactionWreckActivity)}: " +
                        claim.FactionWreckName,
                    RecordedUtc = DateTime.UtcNow
                });
            }))
        {
            _quotaPopup.PopupEntity("Не вдалося заархівувати дані у фракційному реєстрі.",
                recorder, args.User);
            return;
        }
        claim.Completed = true;
        NotifyFactionTaskCompleted(args.User);
        ScheduleNextFactionTask(org);
        claim.FactionStage = 3;
        claim.DeliveredByPost[0] = 3;
        _quotaPopup.PopupEntity($"Записи {claim.FactionSiteCode} зчитано. " +
            $"Завдання виконано, авторитет: +{authorityReward}.", recorder, args.User);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Faction wreck recorder scanned: {claim.Code}, org={org.Id}, grid={grid}");
        RefreshFactionOperationConsoles();
    }
    /// <summary>Called on acceptance and on an authorized PDA reprint request.</summary>
    private void PrintFactionCoordinatesDisk(EntityUid actor, CharacterKey character,
        Organization org, SectorQuotaClaim claim, bool initial)
    {
        if (org.Kind != OrganizationKind.Faction || claim.Organization != org.Id ||
            claim.Assignee != character || FactionDestinationMap(claim) is not { } site ||
            Deleted(site) || claim.FactionWreckReturned ||
            !_prototypes.HasIndex<EntityPrototype>(FactionCoordinatesDiskProto))
            return;
        if (!initial && (FactionTravelVisited(claim) ||
            _gameTiming.CurTime - claim.FactionWreckDiskPrintedAt < FactionDiskPrintCooldown))
        {
            _quotaPopup.PopupEntity("Повторний друк поки недоступний або експедиція вже розпочалася.", actor, actor);
            return;
        }
        if (claim.FactionWreckDisk is { } old && !Deleted(old))
        {
            if (initial)
                return;
            // Old disk becomes unusable. Prevent two valid disks for one route.
            QueueDel(old);
        }
        var disk = Spawn(FactionCoordinatesDiskProto, Transform(actor).Coordinates);
        var coordinates = EnsureComp<ShuttleDestinationCoordinatesComponent>(disk);
        coordinates.Destination = site;
        Dirty(disk, coordinates);
        var seal = EnsureComp<AspisFactionCoordinatesDiskComponent>(disk);
        seal.Organization = org.Id;
        seal.Assignment = claim.FactionAssignmentId;
        _organizationMetadata.SetEntityName(disk, $"Експедиційний диск {claim.FactionSiteCode}");
        claim.FactionWreckDisk = disk;
        claim.FactionWreckDiskPrintedAt = _gameTiming.CurTime;
        _hands.TryPickupAnyHand(actor, disk);
        // The initial dispatch includes a mission-bound locator. Do not create
        // another device for every coordinate-disk reprint.
        if (initial && claim.FactionWreckGrid is not null &&
            _prototypes.HasIndex<EntityPrototype>(FactionLocatorPrototype))
        {
            var scanner = Spawn(FactionLocatorPrototype, Transform(actor).Coordinates);
            var scannerSeal = EnsureComp<AspisFactionLocatorComponent>(scanner);
            scannerSeal.Organization = org.Id;
            scannerSeal.Assignment = claim.FactionAssignmentId;
            _hands.TryPickupAnyHand(actor, scanner);
        }
        _audio.PlayPvs(QuotaManifestPrinterSound, actor);
        _quotaPopup.PopupEntity($"КПК надрукував диск {claim.FactionSiteCode} для " +
            (claim.FactionPlanetMap != null ? "планети" : "космічного об'єкта") +
            ". Вставте його в FTL-консоль. Виліт — із району Trade Mall.", actor, actor);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Faction coordinates issued: {claim.Code}, org={org.Id}");
    }

    private void OnFactionCoordinatesPrintRequested(MsgOrganizationFactionCoordinatesPrintRequest request)
    {
        if (request.Code.Length is < 6 or > 48 || !_store.Available)
            return;
        var session = _players.Sessions.FirstOrDefault(s => s.UserId == request.MsgChannel.UserId);
        if (session?.AttachedEntity is not { Valid: true } actor ||
            !TryGetCharacter(actor, out var character, out _) || !IsOriginalController(actor) ||
            FindMembership(character) is not { Kind: OrganizationKind.Faction } org ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == org.Id && c.Code == request.Code && c.Assignee == character &&
            FactionDestinationMap(c) != null && !c.FactionWreckReturned);
        if (claim is null || _gameTiming.CurTime >= claim.Deadline)
            return;
        PrintFactionCoordinatesDisk(actor, character, org, claim, false);
    }

    /// <summary>
    /// Called only from the FTL gate. Do not use the publicly replicated
    /// affiliation component, which is deliberately absent for private factions.
    /// </summary>
    public bool HasAuthorizedFactionTraveler(EntityUid shuttle, Guid organization)
    {
        if (!_store.Available || !_organizations.TryGetValue(organization, out var faction) ||
            faction.Kind != OrganizationKind.Faction ||
            faction.InsolvencyState != OrganizationInsolvencyState.Active)
            return false;
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { Valid: true } actor ||
                Deleted(actor) || Transform(actor).GridUid != shuttle ||
                !IsOriginalController(actor) || !TryGetCharacter(actor, out var key, out _))
                continue;
            if (FindMembership(key) is { Kind: OrganizationKind.Faction } member &&
                member.Id == organization)
                return true;
        }
        return false;
    }

    private void OnFactionWreckFTLStarted(ref FTLStartedEvent args)
    {
        var targetMap = EntityManager.System<SharedTransformSystem>().ToMapCoordinates(args.TargetCoordinates).MapId;
        var targetUid = _organizationMapSystem.GetMapOrInvalid(targetMap);
        foreach (var claim in _sectorQuotaClaims.Values)
        {
            if (FactionDestinationMap(claim) != targetUid || claim.FactionWreckReturned ||
                _gameTiming.CurTime >= claim.Deadline)
                continue;
            claim.FactionWreckShuttle ??= args.Entity;
            if (claim.FactionWreckMap != null)
                DeployFactionWreckHostiles(claim);
            if (TryComp<AspisFactionExpeditionDestinationComponent>(targetUid, out var route))
                route.AssignedShuttle ??= args.Entity;
            break;
        }
    }

    private void OnFactionWreckFTLCompleted(ref FTLCompletedEvent args)
    {
        foreach (var claim in _sectorQuotaClaims.Values)
        {
            if (claim.FactionWreckShuttle != args.Entity || claim.FactionWreckReturned ||
                FactionDestinationMap(claim) is not { } map)
                continue;
            if (args.MapUid == map)
            {
                if (claim.FactionPlanetMap != null)
                    claim.FactionPlanetVisited = true;
                else
                    claim.FactionWreckVisited = true;
                return;
            }
            if (!FactionTravelVisited(claim))
                continue;
            CloseFactionWreckRoute(claim);
            return;
        }
    }

    private void CloseFactionWreckRoute(SectorQuotaClaim claim)
    {
        if (claim.FactionWreckReturned)
            return;
        claim.FactionWreckReturned = true;
        if (FactionDestinationMap(claim) is { } activeMap && !Deleted(activeMap) &&
            TryComp<AspisFactionExpeditionDestinationComponent>(activeMap, out var route))
            route.RouteClosed = true;
        if (claim.FactionWreckDisk is { } disk && !Deleted(disk))
            QueueDel(disk);
        claim.FactionWreckDisk = null;
        if (FactionDestinationMap(claim) is { } map && !Deleted(map) &&
            TryComp<FTLDestinationComponent>(map, out var destination))
        {
            destination.Enabled = false;
            Dirty(map, destination);
            EntityManager.System<Content.Server.Shuttles.Systems.ShuttleConsoleSystem>()
                .RefreshShuttleConsoles();
        }
        // Leave the map itself until round cleanup. Never delete a map that
        // may still contain an undocked shuttle or a stranded character.
    }

}
