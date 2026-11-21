using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Shared.Audio;
using Content.Server._NF.Shuttles.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Aspis.Organizations;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Mind.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Parallax;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Two physical, distance-verified legs on a private map. The route's physical
/// length is fixed ONCE at departure from the shuttle's configured cruise-speed
/// reference (40 minutes at that speed). There is no elapsed-time gate, no
/// artificial 2 m/s limit, and no proximity to the receiver immediately after
/// starting. The original crewed ship must actually cross the corridor.
///
/// Receipt still requires the authenticated cargo at the physical remote post;
/// a return leg must end at its own physical sector-side endpoint.
/// </summary>
public sealed partial class OrganizationSystem
{
    [Dependency] private ThrusterSystem _frontierThrusters = default!;
    [Dependency] private OrganizationFrontierAutopilotSystem _frontierAutopilot = default!;
    [Dependency] private SharedDoAfterSystem _frontierDoAfter = default!;

    private const string FrontierScenarioId = "company.delivery.frontier";
    private const string FrontierRouteNavigatorPrototype = "AspisFrontierRouteNavigator";
    private const int FrontierCargoCrateCount = OrganizationFrontierCargoRules.Default;
    // Per-contract influence is calculated by OrganizationFrontierCargoRules, 0.35%-0.95%.
    private const int MaximumFrontierRoutesPerRound = 2;
    // Reference distance, not an eligibility timer: ship-rated cruise speed * 40 min.
    private const float FrontierReferenceCruiseSeconds = 40f * 60f;
    // Bounds keep world coordinates and physics stable while allowing the requested
    // 70 / 100 / 400 m/s (168 / 240 / 960 km) classes of ships.
    private const float FrontierMinCruiseSpeed = 15f;
    private const float FrontierMaxCruiseSpeed = 500f;
    private const float FrontierLaneHalfWidth = 55f;
    private const float FrontierArrivalTolerance = 60f;
    // Physical map change is delayed while the existing shuttle warms up.
    private static readonly TimeSpan FrontierLaunchPreparation = TimeSpan.FromSeconds(2);
    // Visible, cancellable binding phase using the game's normal DoAfter bar.
    private static readonly TimeSpan FrontierModuleBinding = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FrontierControlWarningInterval = TimeSpan.FromSeconds(3);
    // A non-FTL safety clearance must complete before the ordinary 2-second ignition.
    private static readonly TimeSpan FrontierSecurityClearance = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan FrontierCombatCooldown = TimeSpan.FromSeconds(120);
    private const float FrontierHostileRadius = 30f;
    private const float FrontierRecentDamageRadius = 48f;
    private static readonly string[] FrontierCorridorParallaxes =
    {
        "AspisFrontierAsteroids",
        "AspisFrontierPlanet",
        "AspisFrontierGasGiant",
        "AspisFrontierCorePlanet"
    };
    private readonly List<(MapId Map, Vector2 Position, TimeSpan When)> _frontierRecentCombat = new();
    private static readonly TimeSpan FrontierArrivalTransition = TimeSpan.FromSeconds(2);
    private static readonly SoundSpecifier FrontierLaunchSound =
        new SoundPathSpecifier("/Audio/Effects/Shuttle/hyperspace_begin.ogg")
        {
            Params = AudioParams.Default.WithVolume(-5f),
        };
    private static readonly SoundSpecifier FrontierArrivalSound =
        new SoundPathSpecifier("/Audio/Effects/Shuttle/hyperspace_end.ogg");
    private static readonly SoundSpecifier FrontierNavigationCue =
        new SoundPathSpecifier("/Audio/Effects/Shuttle/radar_ping.ogg");
    // The same denial sound used when trying to unseal a locked MODsuit.
    private static readonly SoundSpecifier FrontierControlDeniedSound =
        new SoundPathSpecifier("/Audio/Machines/scanbuzz.ogg");
    private static readonly TimeSpan FrontierTickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FrontierEmptyMapCleanup = TimeSpan.FromSeconds(90);
    // When a navigation module is destroyed or an offer expires, the crew can
    // recover the protected route from the normal flight lane without a GM.
    private static readonly TimeSpan FrontierRecoveryGrace = TimeSpan.FromSeconds(90);
    private static readonly ResPath FrontierOutpostBlueprint =
        new("/Maps/Shuttles/trading_outpost.yml");


    private int _frontierRoutesAllocatedThisRound;
    private TimeSpan _nextFrontierTick;
    private readonly Dictionary<Guid, FrontierRouteRecord> _frontierRoutes = new();
    private readonly Dictionary<EntityUid, TimeSpan> _frontierControlDenials = new();
    private sealed record FrontierPendingDeparture(Guid Assignment, Guid Organization,
        string Code, EntityUid Ship, EntityUid Operator, TimeSpan NotifyAt, TimeSpan ReadyAt)
    {
        public bool NotificationSent;
        public bool IgnitionPlayed;
    }
    private readonly Dictionary<Guid, FrontierPendingDeparture> _frontierPendingDepartures = new();

    private enum FrontierPhase : byte
    {
        OutboundAuto,
        OutboundManual,
        Destination,
        InboundAuto,
        InboundManual,
        ReturningToSector,
        Returned,
        Aborted
    }

    private sealed class FrontierRouteRecord
    {
        public Guid Assignment;
        public Guid Organization;
        public string Code = string.Empty;
        public EntityUid Ship;
        public EntityUid RouteMap;
        public MapId RouteMapId;
        public EntityUid Outpost;
        public EntityUid OriginMap;
        public Vector2 OriginPosition;
        public Angle OriginRotation;
        public FrontierPhase Phase;
        // Values are frozen at departure, so damage/reconfiguration cannot
        // shorten an already accepted company's route.
        public float CorridorLength;
        public float CruiseSpeed;
        public float CruiseAcceleration;
        // Informational only; NEVER used as an arrival condition.
        public TimeSpan AutoElapsed;
        public TimeSpan LastUpdated;
        public TimeSpan? IdleSince;
        public TimeSpan? RecoverySince;
        public TimeSpan SectorArrivalReadyAt;
        public int LastRouteMilestone;
        public EntityUid? Navigator;
        public EntityUid? Terminal;
        public EntityUid? SecondTerminal;
        public int CargoCount;
        public HashSet<string> LoadedReferences = new(StringComparer.Ordinal);
        public HashSet<string> ReceivedReferences = new(StringComparer.Ordinal);
    }

    /// <summary>Issued to the assigned company member, not a permanent FTL disk.</summary>
    private void IssueFrontierNavigator(EntityUid actor, SectorQuotaClaim claim)
    {
        if (claim.Scenario.Id != FrontierScenarioId || claim.Completed ||
            _gameTiming.CurTime >= claim.Deadline ||
            claim.FrontierNavigator is { } old && !Deleted(old) ||
            !_prototypes.HasIndex<EntityPrototype>(FrontierRouteNavigatorPrototype))
            return;

        var item = Spawn(FrontierRouteNavigatorPrototype, Transform(actor).Coordinates);
        var key = EnsureComp<OrganizationFrontierNavigatorComponent>(item);
        key.Organization = claim.Organization;
        key.Code = claim.Code;
        claim.FrontierNavigator = item;
        _organizationMetadata.SetEntityName(item, $"Маршрут: {claim.Code}");
        _hands.TryPickupAnyHand(actor, item);
        _quotaPopup.PopupEntity($"Видано маршрутний модуль. Замовте {claim.Units} ящиків " +
            "партіями до 20 через Cargo Requests. Завантажте все на один шатл. " +
            "Після отримання допуску запускайте маршрут із кабіни.", actor, actor);
    }

    private void CleanupFrontierNavigator(SectorQuotaClaim claim)
    {
        if (claim.FrontierNavigator is { } item && !Deleted(item))
            QueueDel(item);
        claim.FrontierNavigator = null;
    }

    private void OnFrontierQuotaExpired(SectorQuotaClaim claim)
    {
        if (claim.Scenario.Id != FrontierScenarioId)
            return;
        // A ship with players in a transit corridor must NEVER be stranded
        // because the financial contract's deadline expired. Keep the physical
        // navigation key until the ship returns, but disable any payout.
        if (!claim.FrontierDeparted)
            CleanupFrontierNavigator(claim);
    }

    private static float FrontierDistanceRemaining(FrontierRouteRecord route, Vector2 position,
        bool? inbound = null)
    {
        var toSector = inbound ?? (route.Phase is FrontierPhase.InboundAuto or FrontierPhase.InboundManual);
        return MathF.Max(0f, toSector ? position.X : route.CorridorLength - position.X);
    }

    private static int FrontierProgressPercent(FrontierRouteRecord route, Vector2 position)
    {
        if (route.CorridorLength <= 0f)
            return 0;
        var distance = FrontierDistanceRemaining(route, position);
        return Math.Clamp((int)MathF.Round(100f * (1f - distance / route.CorridorLength)), 0, 100);
    }

    private string DescribeFrontierRoute(SectorQuotaClaim claim)
    {
        if (!_frontierRoutes.TryGetValue(claim.FactionAssignmentId, out var route))
            return $"Підготовка: {claim.CargoRequestedCrates}/{claim.Units} ящиків замовлено. " +
                $"Завантажте всі {claim.Units} ящиків A–D на один шатл та застосуйте маршрутний модуль.";
        if (TerminatingOrDeleted(route.Ship))
            return "Зв'язок із кораблем втрачено.";
        var pos = Transform(route.Ship).WorldPosition;
        var remainingKm = FrontierDistanceRemaining(route, pos) / 1000f;
        var remainingMinutes = (int)MathF.Ceiling(
            FrontierDistanceRemaining(route, pos) / MathF.Max(route.CruiseSpeed, 1f) / 60f);
        var percent = FrontierProgressPercent(route, pos);
        return route.Phase switch
        {
            FrontierPhase.OutboundAuto => $"Дальній маршрут: {percent}%. " +
                $"Залишилося {remainingKm:0.0} км, орієнтовно {remainingMinutes} хв. " +
                $"Крейсерська швидкість {route.CruiseSpeed:0} м/с.",
            FrontierPhase.OutboundManual => "Ручне керування: маршрут тимчасово не утримується. " +
                FrontierManualGuidance(route),
            FrontierPhase.Destination => $"Віддалений пост: прийнято {route.ReceivedReferences.Count}/{route.CargoCount} ящиків. " +
                $"Пункт приймання: X={route.CorridorLength + 170f:0}, Y=0. " +
                $"Для зворотного курсу поверніться до X={route.CorridorLength:0}, Y=0; " +
                "використайте маршрутний модуль.",
            FrontierPhase.InboundAuto => $"Зворотний маршрут: {percent}%. " +
                $"Залишилося {remainingKm:0.0} км, орієнтовно {remainingMinutes} хв.",
            FrontierPhase.InboundManual => "Зворотний маршрут: ручне керування. " +
                FrontierManualGuidance(route),
            FrontierPhase.ReturningToSector => "Секторний пункт досягнуто. Виконується вихід із маршруту.",
            FrontierPhase.Returned => "Доставку й повернення завершено. Очікуйте розрахунку винагороди.",
            _ => "Маршрут перервано. Для евакуації потрібна допомога адміністрації."
        };
    }

    private bool IsFrontierCargoAtSelectedReceiver(FrontierRouteRecord route, int post, Vector2 worldPosition)
    {
        if (post is < 0 or > 3)
            return false;
        foreach (var terminal in new[] { route.Terminal, route.SecondTerminal })
        {
            if (terminal is not { } uid || TerminatingOrDeleted(uid) ||
                !TryComp<OrganizationFrontierReceivingComponent>(uid, out var receiver) ||
                receiver.SelectedPost != post)
                continue;
            if (Vector2.DistanceSquared(worldPosition, Transform(uid).WorldPosition) <=
                FrontierReceiverRadius * FrontierReceiverRadius)
                return true;
        }
        return false;
    }

    private void WarnFrontierLockedPilots(FrontierRouteRecord route, TimeSpan now)
    {
        var pilots = EntityQueryEnumerator<PilotComponent>();
        while (pilots.MoveNext(out var user, out var pilot))
        {
            if (pilot.HeldButtons == ShuttleButtons.None ||
                pilot.Console is not { } console || TerminatingOrDeleted(console) ||
                Transform(console).GridUid != route.Ship ||
                _frontierControlDenials.TryGetValue(user, out var lastWarning) &&
                now - lastWarning < FrontierControlWarningInterval)
                continue;

            _frontierControlDenials[user] = now;
            _quotaPopup.PopupEntity(
                "Ручне керування недоступне. Курс утримується навігаційним модулем.",
                user, user, PopupType.MediumCaution);
            _audio.PlayPvs(FrontierControlDeniedSound, user);
        }
    }

    private string FrontierManualGuidance(FrontierRouteRecord route)
    {
        if (TerminatingOrDeleted(route.Ship))
            return "Корабель недоступний. Зверніться до адміністрації.";
        var pos = Transform(route.Ship).WorldPosition;
        return $"Смуга коридору: X=0..{route.CorridorLength:0}, " +
            $"Y від {-FrontierLaneHalfWidth:0} до {FrontierLaneHalfWidth:0}. " +
            $"Поточні координати: X={pos.X:0}, Y={pos.Y:0}.";
    }

    // Do not let normal FTL replace the actual, time- and cargo-verified flight.
    private void OnFrontierFTLAttempt(ref Content.Server.Shuttles.Events.ConsoleFTLAttemptEvent args)
    {
        if (args.Cancelled)
            return;
        var shipUid = args.Uid;
        if (_frontierPendingDepartures.Values.Any(p => p.Ship == shipUid))
        {
            args.Cancelled = true;
            args.Reason = "Навігаційна послідовність уже виконується. Повторний FTL недоступний.";
            return;
        }
        foreach (var route in _frontierRoutes.Values)
        {
            if (route.Ship != shipUid ||
                route.Phase is FrontierPhase.Returned or FrontierPhase.Aborted)
                continue;
            args.Cancelled = true;
            args.Reason = "Корабель перебуває під керуванням маршрутного модуля. " +
                "Для повернення використовуйте захищений коридор.";
            return;
        }
    }

    // The vanilla trade-outpost blueprint includes a bear spawner. This
    // isolated receiver must not create a hostile encounter during a protected
    // supply mission. Remove only these specific entities, not other content.
    private void SanitizeFrontierReceivingPost(EntityUid dock)
    {
        var children = Transform(dock).ChildEnumerator;
        try
        {
            while (children.MoveNext(out var child))
            {
                if (TerminatingOrDeleted(child))
                    continue;
                var prototypeId = MetaData(child).EntityPrototype?.ID;
                if (prototypeId is "SpawnMobBear" or "MobBear")
                    QueueDel(child);
            }
        }
        finally
        {
            children.Dispose();
        }
    }

    private bool FrontierPlayerLeftOutsideShip(FrontierRouteRecord route)
    {
        // Mind-owned physical bodies count even while their player is
        // disconnected. Pure NPCs and spectator ghosts do not obstruct the
        // departure, but a living or dead crew member left at the post does.
        var people = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (people.MoveNext(out _, out var mind, out _, out var xform))
        {
            if (mind.HasMind && xform.MapUid == route.RouteMap && xform.GridUid != route.Ship)
                return true;
        }
        return false;
    }

    private SectorQuotaClaim? FindActiveFrontierClaim(FrontierRouteRecord route)
    {
        var now = _gameTiming.CurTime;
        return _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.FactionAssignmentId == route.Assignment &&
            c.Organization == route.Organization && c.Code == route.Code &&
            c.Scenario.Id == FrontierScenarioId && !c.Completed && now < c.Deadline);
    }

    private bool IsFrontierNavigatorUnavailable(FrontierRouteRecord route)
    {
        return route.Navigator is not { } navigator || TerminatingOrDeleted(navigator) ||
            FindActiveFrontierClaim(route) == null;
    }

    // Rescue uses the same physical return distance. Expiry does NOT grant
    // cargo proof or any financial payout, and cannot strand the crew.
    private bool TryStartFrontierReturn(FrontierRouteRecord route, out string failure)
    {
        failure = "Не вдалося розпочати зворотний рейс.";
        var claim = FindActiveFrontierClaim(route);
        if (claim != null && route.ReceivedReferences.Count < route.CargoCount)
        {
            failure = $"Віддалений пост прийняв {route.ReceivedReferences.Count}/{route.CargoCount} ящиків. " +
                "Для чинного контракту потрібно завершити розвантаження.";
            return false;
        }
        if (FrontierPlayerLeftOutsideShip(route))
        {
            failure = "На віддаленій платформі залишилися гравці. " +
                "Зберіть екіпаж на кораблі перед зворотним рейсом.";
            return false;
        }
        if (TerminatingOrDeleted(route.Ship) || !TryComp<PhysicsComponent>(route.Ship, out var body))
        {
            failure = "Корабель не знайдено на маршруті.";
            return false;
        }
        var position = Transform(route.Ship).WorldPosition;
        if (MathF.Abs(position.X - route.CorridorLength) > 65f ||
            MathF.Abs(position.Y) > FrontierLaneHalfWidth ||
            _constructionDocking.GetDocks(route.Ship).Any(d => d.Comp.Docked))
        {
            failure = "Відстикуйте корабель і поверніться до початку зворотного коридору.";
            return false;
        }
        route.Phase = FrontierPhase.InboundAuto;
        EnsureComp<PreventPilotComponent>(route.Ship);
        SetFrontierTransitDockBolts(route.Ship, true);
        route.AutoElapsed = TimeSpan.Zero;
        route.LastRouteMilestone = 0;
        _audio.PlayPvs(FrontierLaunchSound, route.Ship);
        BroadcastFrontierShortband(route.Ship,
            "Зворотний рейс розпочато.");
        route.RecoverySince = null;
        route.LastUpdated = _gameTiming.CurTime;
        UpdateFrontierAutopilot(route, body);
        if (claim == null)
            _log.GetSawmill("organizations.frontier-routes").Info(
                $"Rescue return started after deadline or loss of claim: {route.Code}");
        return true;
    }

    /// <summary>
    /// Failsafe for a destroyed navigator or an expired company contract.
    /// Requires physically reaching the original safe lane; it never teleports
    /// a ship and does not bypass the physical return leg.
    /// </summary>
    private void TryRecoverFrontierNavigation(FrontierRouteRecord route, TimeSpan now)
    {
        if (!IsFrontierNavigatorUnavailable(route) || TerminatingOrDeleted(route.Ship))
        {
            route.RecoverySince = null;
            return;
        }
        if (route.Phase == FrontierPhase.Destination)
        {
            var position = Transform(route.Ship).WorldPosition;
            if (MathF.Abs(position.X - route.CorridorLength) > 65f ||
                MathF.Abs(position.Y) > FrontierLaneHalfWidth ||
                _constructionDocking.GetDocks(route.Ship).Any(d => d.Comp.Docked) ||
                FrontierPlayerLeftOutsideShip(route))
            {
                route.RecoverySince = null;
                return;
            }
            // An active contract cannot skip unloading just by destroying
            // the navigator. After expiry, rescue is possible but unpaid.
            if (FindActiveFrontierClaim(route) != null &&
                route.ReceivedReferences.Count < route.CargoCount)
            {
                route.RecoverySince = null;
                return;
            }
            route.RecoverySince ??= now;
            if (now - route.RecoverySince < FrontierRecoveryGrace)
                return;
            if (TryStartFrontierReturn(route, out _))
                _log.GetSawmill("organizations.frontier-routes")
                    .Info($"Automatic navigator recovery: inbound {route.Code}");
            return;
        }
        if (route.Phase is not (FrontierPhase.OutboundManual or FrontierPhase.InboundManual))
        {
            route.RecoverySince = null;
            return;
        }
        var inbound = route.Phase == FrontierPhase.InboundManual;
        var actual = Transform(route.Ship).WorldPosition;
        if (MathF.Abs(actual.Y) > FrontierLaneHalfWidth ||
            actual.X < -FrontierLaneHalfWidth ||
            actual.X > route.CorridorLength + FrontierLaneHalfWidth ||
            !TryComp<PhysicsComponent>(route.Ship, out var physics))
        {
            route.RecoverySince = null;
            return;
        }
        route.RecoverySince ??= now;
        if (now - route.RecoverySince < FrontierRecoveryGrace)
            return;
        route.Phase = inbound ? FrontierPhase.InboundAuto : FrontierPhase.OutboundAuto;
        route.LastUpdated = now;
        route.RecoverySince = null;
        UpdateFrontierAutopilot(route, physics);
        _log.GetSawmill("organizations.frontier-routes")
            .Info($"Automatic navigator recovery: physical flight {route.Code}");
    }

    private void OnFrontierNavigatorUsed(EntityUid item, OrganizationFrontierNavigatorComponent marker,
        AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target ||
            !HasComp<ShuttleConsoleComponent>(target))
            return;
        args.Handled = true;

        if (!_store.Available || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var character, out _) ||
            FindMembership(character) is not { Kind: OrganizationKind.Company } org ||
            org.Id != marker.Organization || org.Industry != OrganizationIndustry.Delivery ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            _quotaPopup.PopupEntity("Маршрутний модуль призначений працівникам компанії-перевізника.",
                args.User, args.User);
            return;
        }

        var shipUid = Transform(target).GridUid;
        if (shipUid is not { } ship || !HasComp<ShuttleComponent>(ship) ||
            !HasComp<MapGridComponent>(ship) || !HasComp<PhysicsComponent>(ship))
        {
            _quotaPopup.PopupEntity("Використайте модуль на консолі справжнього шатла.", args.User, args.User);
            return;
        }

        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == marker.Organization && c.Code == marker.Code &&
            c.Scenario.Id == FrontierScenarioId);
        var route = _frontierRoutes.Values.FirstOrDefault(r =>
            r.Organization == marker.Organization && r.Code == marker.Code);
        if (route == null && (claim == null || claim.Completed || _gameTiming.CurTime >= claim.Deadline))
        {
            _quotaPopup.PopupEntity("У цього документа немає чинного рейсу.", args.User, args.User);
            return;
        }
        if (route != null && route.Ship != ship)
        {
            _quotaPopup.PopupEntity("Для цього рейсу зареєстровано інший корабель.", args.User, args.User);
            return;
        }

        if (route == null)
        {
            if (claim == null)
                return;
            if (_frontierPendingDepartures.ContainsKey(claim.FactionAssignmentId))
            {
                _quotaPopup.PopupEntity("Підготовка до рейсу вже виконується.", args.User, args.User);
                return;
            }
            // The route-binding phase is an actual, cancellable DoAfter, not
            // a hidden timestamp. Its progress is shown over the operator.
            var binding = new DoAfterArgs(EntityManager, args.User, FrontierModuleBinding,
                new OrganizationFrontierBindingDoAfterEvent(), item, target: target, used: item)
            {
                NeedHand = true,
                BreakOnMove = true,
                BreakOnWeightlessMove = true,
                BreakOnDamage = true,
                MovementThreshold = 0.10f,
                DistanceThreshold = 2.5f,
                BlockDuplicate = true,
                CancelDuplicate = false
            };
            if (!_frontierDoAfter.TryStartDoAfter(binding))
                _quotaPopup.PopupEntity("Не вдалося підключити маршрутний модуль.", args.User, args.User);
            return;
        }

        if (route.Phase is FrontierPhase.OutboundAuto or FrontierPhase.InboundAuto)
        {
            // The same navigator which authorized departure keeps the ship
            // under autopilot during both transit legs. The pilot may open
            // the console radar, but cannot suspend or override the route.
            _quotaPopup.PopupEntity("Маршрут утримується навігаційним модулем.",
                args.User, args.User, PopupType.MediumCaution);
            _audio.PlayPvs(FrontierControlDeniedSound, args.User);
        }
        else if (route.Phase is FrontierPhase.OutboundManual or FrontierPhase.InboundManual)
        {
            var current = Transform(ship).WorldPosition;
            if (MathF.Abs(current.Y) > FrontierLaneHalfWidth ||
                current.X < -FrontierLaneHalfWidth ||
                current.X > route.CorridorLength + FrontierLaneHalfWidth)
            {
                _quotaPopup.PopupEntity("Шатл поза безпечним маршрутом. Поверніться до позначеної лінії "+
                    "і повторіть активацію модуля.", args.User, args.User);
                return;
            }
            route.Phase = route.Phase == FrontierPhase.OutboundManual
                ? FrontierPhase.OutboundAuto : FrontierPhase.InboundAuto;
            SetFrontierTransitDockBolts(ship, true);
            route.RecoverySince = null;
            route.LastUpdated = _gameTiming.CurTime;
            if (TryComp<PhysicsComponent>(ship, out var body))
                UpdateFrontierAutopilot(route, body);
            _quotaPopup.PopupEntity("Автопілот відновлено. Захищений маршрут активний.",
                args.User, args.User);
        }
        else if (route.Phase == FrontierPhase.Destination)
        {
            if (!TryStartFrontierReturn(route, out var failure))
            {
                _quotaPopup.PopupEntity(failure, args.User, args.User);
                return;
            }
            _quotaPopup.PopupEntity(FindActiveFrontierClaim(route) == null
                ? $"Аварійне повернення: {route.CorridorLength / 1000f:0} км до сектора. Прострочений контракт не оплачується."
                : $"Зворотний курс активний: {route.CorridorLength / 1000f:0} км до сектора.",
                args.User, args.User);
        }
        else
        {
            _quotaPopup.PopupEntity("Цей рейс уже завершено або скасовано.", args.User, args.User);
        }
    }

    private void OnFrontierBindingComplete(EntityUid item, OrganizationFrontierNavigatorComponent marker,
        OrganizationFrontierBindingDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Used != item ||
            args.Target is not { } target || TerminatingOrDeleted(target) ||
            !HasComp<ShuttleConsoleComponent>(target) ||
            TerminatingOrDeleted(args.User) || !IsOriginalController(args.User) ||
            !TryGetCharacter(args.User, out var character, out _) ||
            FindMembership(character) is not { Kind: OrganizationKind.Company } org ||
            org.Id != marker.Organization || org.Industry != OrganizationIndustry.Delivery ||
            org.InsolvencyState != OrganizationInsolvencyState.Active || !_store.Available)
            return;

        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == marker.Organization && c.Code == marker.Code &&
            c.Scenario.Id == FrontierScenarioId && !c.Completed &&
            _gameTiming.CurTime < c.Deadline && c.FrontierNavigator == item);
        if (claim == null || _frontierPendingDepartures.ContainsKey(claim.FactionAssignmentId) ||
            _frontierRoutes.Values.Any(r => r.Organization == marker.Organization && r.Code == marker.Code))
            return;

        var ship = Transform(target).GridUid;
        if (ship is not { } shuttle || TerminatingOrDeleted(shuttle) ||
            !HasComp<ShuttleComponent>(shuttle) ||
            !HasComp<MapGridComponent>(shuttle) || !HasComp<PhysicsComponent>(shuttle))
            return;

        args.Handled = true;
        if (!TryQueueFrontierDeparture(shuttle, claim, args.User, out var failure))
            _quotaPopup.PopupEntity(failure, args.User, args.User);
    }

    private bool TryCollectFrontierCargo(SectorQuotaClaim claim, EntityUid ship,
        out HashSet<string> references)
    {
        references = new HashSet<string>(StringComparer.Ordinal);
        if (!OrganizationFrontierCargoRules.Valid(claim.Units) ||
            claim.CargoRequestedCrates != claim.Units || claim.Delivered != 0)
            return false;
        var byPost = new int[4];
        var crates = EntityQueryEnumerator<OrganizationQuotaCrateComponent, TransformComponent>();
        while (crates.MoveNext(out var uid, out var manifest, out var xform))
        {
            if (xform.GridUid != ship || manifest.Organization != claim.Organization ||
                manifest.Code != claim.Code || manifest.DeliveryAccepted || manifest.Post is < 0 or > 3 ||
                string.IsNullOrWhiteSpace(manifest.Reference))
                continue;
            if (references.Add(manifest.Reference))
                byPost[manifest.Post]++;
        }
        return references.Count == claim.Units && byPost.SequenceEqual(claim.PostTargets);
    }

    /// <summary>
    /// All positive damage around a potential departure is recorded BEFORE the
    /// operator requests launch. A module cannot instantly open an escape map in combat.
    /// The list is bounded; no expensive ray casts or hidden player identities.
    /// </summary>
    private void OnFrontierCombatDamage(EntityUid uid, DamageableComponent component, DamageChangedEvent args)
    {
        if (args.DamageDelta == null || args.DamageDelta.GetTotal() <= 0 ||
            !_sectorQuotaClaims.Values.Any(c => c.Scenario.Id == FrontierScenarioId && !c.Completed) ||
            TerminatingOrDeleted(uid))
            return;
        var xform = Transform(uid);
        if (xform.MapUid == null)
            return;
        var now = _gameTiming.CurTime;
        _frontierRecentCombat.RemoveAll(e => now - e.When >= FrontierCombatCooldown);
        if (_frontierRecentCombat.Count > 256)
            _frontierRecentCombat.RemoveRange(0, _frontierRecentCombat.Count - 256);
        _frontierRecentCombat.Add((xform.MapID, xform.WorldPosition, now));
    }

    /// <summary>Rechecked through the entire clearance, not only on first click.</summary>
    private bool FrontierLaunchIsSafe(EntityUid ship, Guid organization, out string reason)
    {
        reason = string.Empty;
        if (TerminatingOrDeleted(ship) ||
            !TryComp<PhysicsComponent>(ship, out var body) ||
            body.LinearVelocity.LengthSquared() > 0.3f * 0.3f ||
            MathF.Abs(body.AngularVelocity) > 0.08f)
        {
            reason = "Недостатня стабілізація навігаційного рішення: зафіксовано інерційне відхилення.";
            return false;
        }
        var shipXform = Transform(ship);
        var pos = shipXform.WorldPosition;
        var map = shipXform.MapID;
        // The secure corridor is entered ONLY from the commercial dispatch area.
        // An armed ship cannot press the route key in the middle of a sector chase.
        var nearDispatch = false;
        var sectorGrids = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (sectorGrids.MoveNext(out var gridUid, out _, out var gridTransform))
        {
            if (gridTransform.MapID != map ||
                !MetaData(gridUid).EntityName.Contains("Trade Mall", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Vector2.DistanceSquared(pos, gridTransform.WorldPosition) > 200f * 200f)
                continue;
            nearDispatch = true;
            break;
        }
        if (!nearDispatch)
        {
            reason = "Захищений маршрут запускається тільки біля вантажного термінала Trade Mall.";
            return false;
        }
        var now = _gameTiming.CurTime;
        _frontierRecentCombat.RemoveAll(e => now - e.When >= FrontierCombatCooldown);
        if (_frontierRecentCombat.Any(e => e.Map == map &&
            Vector2.DistanceSquared(e.Position, pos) <= FrontierRecentDamageRadius * FrontierRecentDamageRadius))
        {
            reason = "Не завершено інтервал перевірки зовнішніх перешкод. Повторіть розрахунок пізніше.";
            return false;
        }
        // A non-company player in or right beside the ship can fight or be held
        // hostage. Never move that confrontation into a protected private map.
        var people = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (people.MoveNext(out var uid, out var mind, out _, out var xform))
        {
            if (!mind.HasMind || xform.MapID != map ||
                (xform.GridUid != ship &&
                 Vector2.DistanceSquared(xform.WorldPosition, pos) > FrontierHostileRadius * FrontierHostileRadius))
                continue;
            if (!TryGetCharacter(uid, out var character, out _) ||
                FindMembership(character) is not { } member || member.Id != organization)
            {
                reason = "У зоні прокладання маршруту виявлено сторонні біосигнали. Допуск не видано.";
                return false;
            }
        }
        return true;
    }

    private string PickFrontierCorridorParallaxId() =>
        FrontierCorridorParallaxes[_random.Next(FrontierCorridorParallaxes.Length)];

    private void ConfigureFrontierRouteMapAppearance(EntityUid mapUid)
    {
        var parallaxId = PickFrontierCorridorParallaxId();
        var ftlMap = EnsureComp<FTLMapComponent>(mapUid);
        ftlMap.Parallax = parallaxId;
        var parallax = EnsureComp<ParallaxComponent>(mapUid);
        parallax.Parallax = parallaxId;
    }

    private void SetFrontierTransitDockBolts(EntityUid ship, bool enabled)
    {
        if (TerminatingOrDeleted(ship))
            return;

        _constructionDocking.SetDockBolts(ship, enabled);
    }

    private void SpawnFrontierCorridorVisuals(FrontierRouteRecord route)
    {
        // Route beacons caused wall/embed issues on the receiving platform and
        // are no longer spawned. The corridor now relies on the background
        // parallax plus the destination structure itself for visual identity.
    }

    /// <summary>
    /// A visible 2-second engine/route preparation in the sector. Players keep
    /// control and remain on their original shuttle until the countdown ends.
    /// All cargo/ship checks are repeated by TryLaunchFrontierRoute after warm-up.
    /// </summary>
    private bool TryQueueFrontierDeparture(EntityUid ship, SectorQuotaClaim claim,
        EntityUid operatorUid, out string failure)
    {
        failure = "Відправлення недоступне.";
        if (claim.Scenario.Id != FrontierScenarioId || claim.Completed ||
            _gameTiming.CurTime >= claim.Deadline ||
            _frontierPendingDepartures.ContainsKey(claim.FactionAssignmentId) ||
            _frontierPendingDepartures.Values.Any(p => p.Organization == claim.Organization) ||
            _frontierRoutesAllocatedThisRound >= MaximumFrontierRoutesPerRound ||
            _frontierRoutes.Values.Any(r => r.Organization == claim.Organization &&
                r.Phase is not (FrontierPhase.Returned or FrontierPhase.Aborted)) ||
            HasComp<OrganizationConstructionYardProtectedGridComponent>(ship) ||
            !TryComp<MapGridComponent>(ship, out var previewGrid) ||
            previewGrid.LocalAABB.MaxDimension > 170f ||
            !TryComp<PhysicsComponent>(ship, out var body) ||
            (body.BodyType & BodyType.Dynamic) == 0 ||
            HasComp<PreventPilotComponent>(ship) ||
            HasComp<FTLComponent>(ship) ||
            _constructionDocking.GetDocks(ship).Any(d => d.Comp.Docked) ||
            !TryGetConstructionSector(out var sectorMap, out _, out _) ||
            Transform(ship).MapUid != sectorMap)
            return false;
        if (!TryCollectFrontierCargo(claim, ship, out _))
        {
            failure = $"Завантажте всі {claim.Units} зареєстрованих ящиків A–D " +
                "на один шатл перед підготовкою.";
            return false;
        }
        if (!FrontierLaunchIsSafe(ship, claim.Organization, out failure))
            return false;
        var now = _gameTiming.CurTime;
        _frontierPendingDepartures.Add(claim.FactionAssignmentId,
            new FrontierPendingDeparture(claim.FactionAssignmentId, claim.Organization,
                claim.Code, ship, operatorUid, now,
                now + FrontierSecurityClearance + FrontierLaunchPreparation));
        return true;
    }

    private void TickFrontierDepartures(TimeSpan now)
    {
        foreach (var request in _frontierPendingDepartures.Values.ToArray())
        {
            var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
                c.FactionAssignmentId == request.Assignment &&
                c.Organization == request.Organization && c.Code == request.Code &&
                !c.Completed && now < c.Deadline);
            if (claim == null || TerminatingOrDeleted(request.Ship))
            {
                StopFrontierIgnition(request);
                _frontierPendingDepartures.Remove(request.Assignment);
                continue;
            }
            // Combat, intrusion or movement immediately cancels even a nearly
            // complete countdown; no safe-map teleport while in a firefight.
            if (!FrontierLaunchIsSafe(request.Ship, request.Organization, out var unsafeReason) ||
                !TryCollectFrontierCargo(claim, request.Ship, out _))
            {
                StopFrontierIgnition(request);
                _frontierPendingDepartures.Remove(request.Assignment);
                NotifyQuotaAssignee(claim, "Дозвіл на рейс скасовано: " +
                    (string.IsNullOrWhiteSpace(unsafeReason) ? "вантаж або обстановка змінилися" : unsafeReason), false);
                continue;
            }
            // The visible DoAfter already held the operator for two seconds.
            // The message now marks the beginning of route calculation.
            if (!request.NotificationSent && now >= request.NotifyAt)
            {
                request.NotificationSent = true;
                if (!TerminatingOrDeleted(request.Operator))
                    _quotaPopup.PopupEntity(
                        "Навігаційний модуль підключено. Триває розрахунок коридору: 18 с.",
                        request.Operator, request.Operator);
                _audio.PlayPvs(FrontierNavigationCue, request.Ship);
            }
            if (now < request.ReadyAt)
            {
                if (request.NotificationSent && !request.IgnitionPlayed &&
                    request.ReadyAt - now <= FrontierLaunchPreparation)
                {
                    request.IgnitionPlayed = true;
                    // The SAME propulsion visuals used by stock FTL startup.
                    // This is only a warmup; physical thrust starts via Mono's
                    // MoverController once the ship enters its private corridor.
                    if (TryComp<ShuttleComponent>(request.Ship, out var ignitionShuttle))
                    {
                        _frontierThrusters.DisableLinearThrusters(ignitionShuttle);
                        _frontierThrusters.EnableLinearThrustDirection(ignitionShuttle, DirectionFlag.North);
                        _frontierThrusters.SetAngularThrust(ignitionShuttle, false);
                    }
                    var startupAudio = _audio.PlayPvs(FrontierLaunchSound, request.Ship);
                    _audio.SetGridAudio(startupAudio);
                    Spawn("EffectFlashBluespace", Transform(request.Ship).Coordinates);
                }
                continue;
            }
            _frontierPendingDepartures.Remove(request.Assignment);
            StopFrontierIgnition(request);
            if (!TryLaunchFrontierRoute(request.Ship, claim, out var failure))
            {
                NotifyQuotaAssignee(claim, "Підготовку рейсу перервано: " + failure, false);
                continue;
            }
            Spawn("EffectFlashBluespace", Transform(request.Ship).Coordinates);
            if (_frontierRoutes.TryGetValue(request.Assignment, out var route))
            {
                BroadcastFrontierShortband(request.Ship,
                    "Дальній рейс розпочато.");
                NotifyQuotaAssignee(claim,
                    $"Дальній курс відкрито: {route.CorridorLength / 1000f:0} км до пункту приймання.", false);
            }
        }
    }

    private bool TryLaunchFrontierRoute(EntityUid ship, SectorQuotaClaim claim, out string failure)
    {
        failure = "Рейс не запущено.";
        if (_frontierRoutesAllocatedThisRound >= MaximumFrontierRoutesPerRound ||
            _frontierRoutes.Values.Any(r => r.Organization == claim.Organization &&
                r.Phase is not (FrontierPhase.Returned or FrontierPhase.Aborted)) ||
            claim.FrontierDeparted || _gameTiming.CurTime >= claim.Deadline ||
            !_organizations.TryGetValue(claim.Organization, out var org) ||
            org.InsolvencyState != OrganizationInsolvencyState.Active ||
            !_store.Available)
        {
            failure = "Рейс недоступний або ліміт віддалених маршрутів вичерпано.";
            return false;
        }
        // Only a detached, physics-enabled civilian craft can enter the
        // corridor. NEVER relocate a static station, Trade Mall or a yard.
        if (!TryComp<PhysicsComponent>(ship, out var sourceBody) ||
            (sourceBody.BodyType & BodyType.Dynamic) == 0 ||
            HasComp<OrganizationConstructionYardProtectedGridComponent>(ship) ||
            !TryComp<MapGridComponent>(ship, out var sourceGrid) ||
            sourceGrid.LocalAABB.MaxDimension > 170f ||
            !TryGetConstructionSector(out var sectorMap, out _, out _) ||
            Transform(ship).MapUid != sectorMap ||
            _constructionDocking.GetDocks(ship).Any(d => d.Comp.Docked) ||
            HasComp<FTLComponent>(ship))
        {
            failure = "Перед стартом відстикуйте шатл у звичайному секторному космосі та вимкніть FTL.";
            return false;
        }
        if (!TryCollectFrontierCargo(claim, ship, out var references))
        {
            failure = $"Потрібно {claim.Units} ящиків A–D саме цього контракту " +
                "з серверними накладними на одному шатлі. Перевірте партії Cargo Requests.";
            return false;
        }

        // Derive the distance from this ship's actual configuration, never from
        // a universal 35-minute timer. BaseMaxLinearVelocity is the Mono
        // shuttle's rated slowdown threshold, not an exact physical speed cap;
        // actual arrival still requires crossing the entire real corridor.
        if (!TryComp<ShuttleComponent>(ship, out var capability) ||
            !float.IsFinite(capability.BaseMaxLinearVelocity) ||
            capability.BaseMaxLinearVelocity <= 0f ||
            !float.IsFinite(sourceBody.Mass) || sourceBody.Mass <= 0f)
        {
            failure = "Неможливо визначити параметри руху цього шатла.";
            return false;
        }
        // The real autopilot must be able to steer its nose into the lane,
        // accelerate FORWARD, and brake with actual aft thrusters.
        var peakThrust = capability.LinearThrust[2];
        if (!float.IsFinite(peakThrust) || peakThrust <= 0f ||
            !float.IsFinite(capability.LinearThrust[0]) || capability.LinearThrust[0] <= 0f ||
            !float.IsFinite(capability.AngularThrust) || capability.AngularThrust <= 0f)
        {
            failure = "Для дальнього маршруту потрібні справні маршові, гальмівні двигуни та гіроскоп.";
            return false;
        }
        var cruiseSpeed = Math.Clamp(capability.BaseMaxLinearVelocity,
            FrontierMinCruiseSpeed, FrontierMaxCruiseSpeed);
        var cruiseAcceleration = Math.Clamp(peakThrust /
            MathF.Max(sourceBody.Mass, 1f) * capability.AccelerationMultiplier,
            0.25f, 20f);
        var corridorLength = cruiseSpeed * FrontierReferenceCruiseSeconds;

        // An independent clean space map is not a normal sector/expedition POI:
        // ambient drone/debris generators do not target the registered sector here.
        EntityUid? createdMap = null;
        var moved = false;
        var success = false;
        var originalPosition = Transform(ship).WorldPosition;
        var originalRotation = _organizationOutpostTransforms.GetWorldRotation(ship);
        try
        {
            createdMap = _organizationMapSystem.CreateMap(out var mapId);
            ConfigureFrontierRouteMapAppearance(createdMap.Value);
            if (!_organizationGridLoader.TryLoadGrid(mapId, FrontierOutpostBlueprint,
                    out var loaded) || loaded is null)
            {
                failure = "Не вдалося створити віддалений торговий пост із фізичної карти.";
                return false;
            }
            var dock = loaded.Value.Owner;
            if (!TryComp<MapGridComponent>(dock, out _) ||
                !_constructionDocking.GetDocks(dock).Any())
            {
                failure = "Віддалений пост не має робочого стикувального порту.";
                return false;
            }
            // The source trade-outpost includes a hostile bear spawner.
            // Clear it before placing the player shuttle on this map.
            SanitizeFrontierReceivingPost(dock);
            _organizationShuttle.Disable(dock, force: true);
            if (TryComp<ShuttleComponent>(dock, out var staticShuttle))
                staticShuttle.Enabled = false;
            EnsureComp<NoFTLComponent>(dock);
            EnsureComp<PreventGridAnchorChangesComponent>(dock);
            // Use the same tile/fixture protection as the authored sector yards.
            // Only station infrastructure is protected; people and cargo remain physical.
            if (!_constructionProtection.Protect(dock))
            {
                failure = "Не вдалося захистити конструкції віддаленого поста.";
                return false;
            }
            _organizationOutpostTransforms.SetWorldPosition(dock,
                new Vector2(corridorLength + 170f, 0f));
            _organizationMetadata.SetEntityName(dock, $"Далекий рубіж: пункт приймання {claim.Code}");
            _organizationMetadata.SetEntityName(createdMap.Value, "Захищений логістичний коридор");

            _constructionDeparturePhysics.SetLinearVelocity(ship, Vector2.Zero, body: Comp<PhysicsComponent>(ship));
            _constructionDeparturePhysics.SetAngularVelocity(ship, 0f, body: Comp<PhysicsComponent>(ship));
            _organizationOutpostTransforms.SetParent(ship, Transform(ship), createdMap.Value);
            moved = true;
            _organizationOutpostTransforms.SetWorldPosition(ship, Vector2.Zero);
            // Start with the bow (local north) facing the route, not sideways.
            // Mono's input-driven gyroscopes maintain/correct heading from here.
            _organizationOutpostTransforms.SetWorldRotation(ship, -MathF.PI / 2f);

            var route = new FrontierRouteRecord
            {
                Assignment = claim.FactionAssignmentId,
                Organization = claim.Organization,
                Code = claim.Code,
                Ship = ship,
                RouteMap = createdMap.Value,
                RouteMapId = mapId,
                Outpost = dock,
                OriginMap = sectorMap,
                OriginPosition = originalPosition,
                OriginRotation = originalRotation,
                Phase = FrontierPhase.OutboundAuto,
                LastUpdated = _gameTiming.CurTime,
                Navigator = claim.FrontierNavigator,
                CargoCount = claim.Units,
                CorridorLength = corridorLength,
                CruiseSpeed = cruiseSpeed,
                CruiseAcceleration = cruiseAcceleration,
                LoadedReferences = references
            };
            if (!TryCreateFrontierReceiver(route, out failure))
                return false;
            SpawnFrontierCorridorVisuals(route);
            EnsureComp<PreventPilotComponent>(ship);
            SetFrontierTransitDockBolts(ship, true);
            UpdateFrontierAutopilot(route, Comp<PhysicsComponent>(ship));
            _frontierRoutes.Add(route.Assignment, route);
            claim.FrontierDeparted = true;
            _frontierRoutesAllocatedThisRound++;
            _adminLog.Add(Content.Shared.Database.LogType.Action, Content.Shared.Database.LogImpact.Medium,
                $"Long-haul route START: code={claim.Code} ship={ship} cargo={references.Count} map={mapId}");
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            failure = "Не вдалося відкрити фізичний маршрут. Подробиці в журналі organizations.frontier-routes.";
            _log.GetSawmill("organizations.frontier-routes").Error($"Route start failed: {ex}");
            return false;
        }
        finally
        {
            if (!success)
            {
                if (moved && !TerminatingOrDeleted(ship))
                {
                    _frontierAutopilot.Disengage(ship);
                    RemComp<PreventPilotComponent>(ship);
                    SetFrontierTransitDockBolts(ship, false);
                    _organizationOutpostTransforms.SetParent(ship, Transform(ship), sectorMap);
                    _organizationOutpostTransforms.SetWorldPosition(ship, originalPosition);
                    _organizationOutpostTransforms.SetWorldRotation(ship, originalRotation);
                }
                if (createdMap is { } map && !TerminatingOrDeleted(map))
                    QueueDel(map);
            }
        }
    }

    /// <summary>
    /// Keep the ship enrolled as a normal Mono shuttle-input source. The server's
    /// MoverController evaluates actual physical thrust/gyros at every physics
    /// step, including engine disablement, mass and speed falloff. NEVER set
    /// cruise velocity directly; that used to fake engine force and animation.
    /// </summary>
    private void UpdateFrontierAutopilot(FrontierRouteRecord route, PhysicsComponent body,
        float deltaSeconds = 1f)
    {
        if ((body.BodyType & BodyType.Dynamic) == 0 ||
            !TryComp<ShuttleComponent>(route.Ship, out var shuttle) ||
            !float.IsFinite(shuttle.BaseMaxLinearVelocity) ||
            shuttle.BaseMaxLinearVelocity <= 0f)
            return;
        _frontierAutopilot.Engage(route.Ship,
            route.Phase == FrontierPhase.InboundAuto,
            route.CorridorLength,
            Math.Clamp(shuttle.BaseMaxLinearVelocity, FrontierMinCruiseSpeed, FrontierMaxCruiseSpeed),
            FrontierArrivalTolerance);
    }

    /// <summary>Undo the native FTL-style startup engine visuals if launch is canceled.</summary>
    private void StopFrontierIgnition(FrontierPendingDeparture request)
    {
        if (!request.IgnitionPlayed || TerminatingOrDeleted(request.Ship))
            return;
        if (TryComp<ShuttleComponent>(request.Ship, out var shuttle))
            _frontierThrusters.DisableLinearThrusters(shuttle);
    }

    private void BroadcastFrontierShortband(EntityUid ship, string message)
    {
        // 'Traffic' is Triada's actual shortband channel. Our isolated route
        // intentionally contains no sector telecom server, so exempt ONLY this
        // one console transmission rather than making all ship radios global.
        if (!_prototypes.TryIndex<RadioChannelPrototype>("Traffic", out var shortband))
            return;
        var source = ship;
        var consoles = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (consoles.MoveNext(out var console, out _, out var xform))
        {
            if (xform.GridUid != ship)
                continue;
            source = console;
            break;
        }
        var wasExempt = HasComp<TelecomExemptComponent>(source);
        if (!wasExempt)
            EnsureComp<TelecomExemptComponent>(source);
        try
        {
            _radio.SendRadioMessage(source, message, shortband, source);
        }
        finally
        {
            if (!wasExempt && !TerminatingOrDeleted(source))
                RemComp<TelecomExemptComponent>(source);
        }
    }

    private void TickFrontierRoutes()
    {
        var now = _gameTiming.CurTime;
        if (now < _nextFrontierTick)
            return;
        _nextFrontierTick = now + FrontierTickInterval;
        TickFrontierDepartures(now);

        foreach (var route in _frontierRoutes.Values.ToArray())
        {
            if (route.Phase is FrontierPhase.Returned or FrontierPhase.Aborted)
            {
                _frontierAutopilot.Disengage(route.Ship);
                TryCleanupFrontierMap(route, now);
                continue;
            }
            if (TerminatingOrDeleted(route.Ship) || TerminatingOrDeleted(route.RouteMap) ||
                Transform(route.Ship).MapUid != route.RouteMap)
            {
                route.Phase = FrontierPhase.Aborted;
                _frontierAutopilot.Disengage(route.Ship);
                _log.GetSawmill("organizations.frontier-routes").Warning(
                    $"Ship lost its protected route: {route.Code}, ship={route.Ship}");
                continue;
            }
            // This grid-level component is enforced by the server's
            // MoverController.CanPilot, not merely by client-side UI buttons.
            // Radar/view access continues through the ordinary console.
            if (route.Phase is FrontierPhase.OutboundAuto or FrontierPhase.InboundAuto or
                FrontierPhase.ReturningToSector)
            {
                EnsureComp<PreventPilotComponent>(route.Ship);
                WarnFrontierLockedPilots(route, now);
            }
            else if (route.Phase == FrontierPhase.Destination)
                RemComp<PreventPilotComponent>(route.Ship);
            if (route.Phase == FrontierPhase.ReturningToSector)
            {
                if (now >= route.SectorArrivalReadyAt)
                    FinishFrontierReturn(route);
                continue;
            }
            if (route.Phase == FrontierPhase.Destination)
            {
                ReceiveFrontierCrates(route);
                TryRecoverFrontierNavigation(route, now);
                route.LastUpdated = now;
                continue;
            }
            if (route.Phase is FrontierPhase.OutboundManual or FrontierPhase.InboundManual)
            {
                TryRecoverFrontierNavigation(route, now);
                route.LastUpdated = now;
                continue;
            }

            if (!TryComp<PhysicsComponent>(route.Ship, out var physics))
            {
                route.Phase = FrontierPhase.Aborted;
                SetFrontierTransitDockBolts(route.Ship, false);
                RemComp<PreventPilotComponent>(route.Ship);
                continue;
            }
            var inbound = route.Phase == FrontierPhase.InboundAuto;
            var actual = Transform(route.Ship).WorldPosition;
            var endpointError = MathF.Abs((inbound ? 0f : route.CorridorLength) - actual.X);
            var elapsed = now - route.LastUpdated;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;
            if (elapsed > TimeSpan.FromSeconds(5))
                elapsed = TimeSpan.FromSeconds(5);
            route.LastUpdated = now;
            // This duration is retained for diagnostics only. It can never
            // complete a trip by itself or substitute for actual distance.
            route.AutoElapsed += elapsed;
            var distanceProgress = FrontierProgressPercent(route, actual);
            var milestone = distanceProgress / 10;
            if (milestone > route.LastRouteMilestone && milestone < 10)
            {
                route.LastRouteMilestone = milestone;
                _audio.PlayPvs(FrontierNavigationCue, route.Ship);
            }
            if (endpointError > FrontierArrivalTolerance ||
                MathF.Abs(actual.Y) > FrontierArrivalTolerance ||
                physics.LinearVelocity.LengthSquared() > 4f * 4f)
            {
                UpdateFrontierAutopilot(route, physics, (float)elapsed.TotalSeconds);
                continue;
            }
            // Arrival requires actual position AND a physical stop by the
            // shuttle's own aft thrusters, not an instant SetLinearVelocity.
            _frontierAutopilot.Disengage(route.Ship);
            _frontierThrusters.DisableLinearThrusters(Comp<ShuttleComponent>(route.Ship));
            route.Phase = inbound ? FrontierPhase.ReturningToSector : FrontierPhase.Destination;
            // Docking at the remote station is a distinct phase; release the
            // console only AFTER the entire physical outbound leg completes.
            if (route.Phase == FrontierPhase.Destination)
            {
                RemComp<PreventPilotComponent>(route.Ship);
                SetFrontierTransitDockBolts(route.Ship, false);
            }
            _audio.PlayPvs(FrontierNavigationCue, route.Ship);
            if (route.Phase == FrontierPhase.Destination)
            {
                _audio.PlayPvs(FrontierArrivalSound, route.Ship);
                _adminLog.Add(Content.Shared.Database.LogType.Action, Content.Shared.Database.LogImpact.Medium,
                    $"Long-haul OUTBOUND DONE: {route.Code}, distance={route.CorridorLength:0}m " +
                    $"elapsed={route.AutoElapsed.TotalMinutes:0.0}min, ship={route.Ship}");
                continue;
            }
            // Give the crew a visible, audible arrival interval rather than
            // instantly swapping their shuttle back to the sector map.
            route.SectorArrivalReadyAt = now + FrontierArrivalTransition;
            _audio.PlayPvs(FrontierLaunchSound, route.Ship);
            Spawn("EffectFlashBluespace", Transform(route.Ship).Coordinates);
        }
    }

    private void ReceiveFrontierCrates(FrontierRouteRecord route)
    {
        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == route.Organization && c.Code == route.Code &&
            c.Scenario.Id == FrontierScenarioId && !c.Completed &&
            _gameTiming.CurTime < c.Deadline);
        if (claim == null || TerminatingOrDeleted(route.Outpost))
            return;

        var accepted = 0;
        var crates = EntityQueryEnumerator<OrganizationQuotaCrateComponent, TransformComponent>();
        while (crates.MoveNext(out var uid, out var cargo, out var xform))
        {
            // The two door-side consoles share the same physical station,
            // but each can select A-D independently. A crate only counts if
            // its category matches a receiver within arm's reach.
            if (xform.GridUid != route.Outpost ||
                !IsFrontierCargoAtSelectedReceiver(route, cargo.Post, xform.WorldPosition) ||
                cargo.Organization != route.Organization || cargo.Code != route.Code ||
                cargo.DeliveryAccepted || !route.LoadedReferences.Contains(cargo.Reference) ||
                route.ReceivedReferences.Contains(cargo.Reference) ||
                _acceptedCargoProofs.Contains(cargo.Reference) ||
                string.IsNullOrWhiteSpace(cargo.Reference) ||
                claim.DeliveredByPost[cargo.Post] >= claim.PostTargets[cargo.Post])
                continue;
            route.ReceivedReferences.Add(cargo.Reference);
            _acceptedCargoProofs.Add(cargo.Reference);
            claim.ProcessedCargoReferences.Add(cargo.Reference);
            claim.DeliveredByPost[cargo.Post]++;
            claim.VerifiedQualityTotal += 100;
            cargo.DeliveryAccepted = true;
            accepted++;
            QueueDel(uid);
        }
        if (accepted > 0)
        {
            RefreshFrontierReceiver(route);
            if (route.ReceivedReferences.Count == route.CargoCount)
                NotifyQuotaAssignee(claim,
                    $"{route.CargoCount} ящиків A–D прийнято на віддаленому посту. " +
                    "Зберіть екіпаж, відстикуйте шатл і виконайте зворотний рейс.", false);
        }
    }

    private bool TryFindFrontierReturnSpace(MapId mapId, Vector2 around, out Vector2 point)
    {
        point = default;
        var nearby = new List<Entity<MapGridComponent>>();
        for (var ring = 1; ring <= 12; ring++)
        {
            var radius = ring * 125f;
            for (var spoke = 0; spoke < 12; spoke++)
            {
                var angle = spoke * MathF.PI * 2f / 12f;
                var candidate = around + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                nearby.Clear();
                _organizationMapSystem.FindGridsIntersecting(mapId,
                    Box2.CenteredAround(candidate, new Vector2(180f, 180f)),
                    ref nearby, false, false);
                if (nearby.Count != 0)
                    continue;
                point = candidate;
                return true;
            }
        }
        return false;
    }

    private void FinishFrontierReturn(FrontierRouteRecord route)
    {
        // Never delete or teleport the whole route map while somebody may be
        // physically aboard the remote station. Only the crew's same ship moves.
        if (TerminatingOrDeleted(route.Ship) || TerminatingOrDeleted(route.OriginMap))
        {
            route.Phase = FrontierPhase.Aborted;
            return;
        }
        // A player may have left the vessel during the long return flight.
        // Keep the original ship in the corridor until every player boards.
        if (FrontierPlayerLeftOutsideShip(route))
        {
            route.Phase = FrontierPhase.InboundManual;
            // Actual position determines the completed distance; no timer gate.
            route.LastUpdated = _gameTiming.CurTime;
            return;
        }
        var originalMap = Transform(route.OriginMap).MapID;
        if (!TryFindFrontierReturnSpace(originalMap, route.OriginPosition, out var point))
        {
            // Keep the crew intact near the sector-end of the corridor;
            // another tick can try finding empty space.
            route.Phase = FrontierPhase.InboundManual;
            // Actual position determines the completed distance; no timer gate.
            return;
        }
        _organizationOutpostTransforms.SetParent(route.Ship, Transform(route.Ship), route.OriginMap);
        _organizationOutpostTransforms.SetWorldPosition(route.Ship, point);
        _organizationOutpostTransforms.SetWorldRotation(route.Ship, route.OriginRotation);
        if (TryComp<PhysicsComponent>(route.Ship, out var physics))
        {
            _constructionDeparturePhysics.SetLinearVelocity(route.Ship, Vector2.Zero, body: physics);
            _constructionDeparturePhysics.SetAngularVelocity(route.Ship, 0f, body: physics);
        }
        route.Phase = FrontierPhase.Returned;
        _frontierAutopilot.Disengage(route.Ship);
        RemComp<PreventPilotComponent>(route.Ship);
        SetFrontierTransitDockBolts(route.Ship, false);
        _audio.PlayPvs(FrontierArrivalSound, route.Ship);
        Spawn("EffectFlashBluespace", Transform(route.Ship).Coordinates);
        route.IdleSince = _gameTiming.CurTime;
        // Also clean up navigation keys when the financial claim was expired
        // and already purged from the shared quota board.
        if (route.Navigator is { } navigator && !TerminatingOrDeleted(navigator))
            QueueDel(navigator);
        route.Navigator = null;

        var claim = _sectorQuotaClaims.Values.FirstOrDefault(c =>
            c.Organization == route.Organization && c.Code == route.Code &&
            c.Scenario.Id == FrontierScenarioId && !c.Completed);
        if (claim == null || route.ReceivedReferences.Count != route.CargoCount ||
            _gameTiming.CurTime >= claim.Deadline || !_store.Available ||
            !_organizations.TryGetValue(route.Organization, out var org) ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            _log.GetSawmill("organizations.frontier-routes")
                .Warning($"Route physically returned without financial completion: {route.Code}");
            return;
        }

        claim.FrontierReturned = true;
        claim.Completed = true;
        CleanupQuotaInspectionScanner(claim);
        CleanupFrontierNavigator(claim);
        claim.SettlementPending = !TryPayVerifiedQuota(org, claim.DifficultySlot,
            (int)org.Industry, claim.Code, claim.Units, 100);
        NotifyQuotaAssignee(claim, claim.SettlementPending
            ? $"Далекий рубіж {claim.Code} виконано. Винагорода очікує виплати."
            : $"Далекий рубіж {claim.Code} виконано. Кошти й секторний вплив зараховано.",
            !claim.SettlementPending);
        _adminLog.Add(Content.Shared.Database.LogType.Action, Content.Shared.Database.LogImpact.Medium,
            $"Long-haul DONE: {route.Code}, exactCargo={route.ReceivedReferences.Count}, " +
            $"routeDistance={route.CorridorLength:0}m, ratedSpeed={route.CruiseSpeed:0}mps, " +
            $"ship={route.Ship}, pending={claim.SettlementPending}");
    }

    private void TryCleanupFrontierMap(FrontierRouteRecord route, TimeSpan now)
    {
        if (TerminatingOrDeleted(route.RouteMap))
        {
            if (route.Navigator is { } navigator && !TerminatingOrDeleted(navigator))
                QueueDel(navigator);
            _frontierRoutes.Remove(route.Assignment);
            return;
        }
        // Player-owned characters (including disconnected minds) keep the
        // map alive. Native outpost NPCs must not leak a new route map forever.
        var people = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (people.MoveNext(out _, out var mind, out _, out var xform))
        {
            if (mind.HasMind && xform.MapUid == route.RouteMap)
            {
                route.IdleSince = null;
                return;
            }
        }
        var grids = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (grids.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapUid == route.RouteMap && uid != route.Outpost)
            {
                route.IdleSince = null;
                return;
            }
        }
        if (route.IdleSince == null)
        {
            route.IdleSince = now;
            return;
        }
        if (now - route.IdleSince < FrontierEmptyMapCleanup)
            return;
        QueueDel(route.RouteMap);
        _frontierRoutes.Remove(route.Assignment);
    }

    private void ResetFrontierRoutesForRound()
    {
        foreach (var route in _frontierRoutes.Values)
            if (!TerminatingOrDeleted(route.Ship))
            {
                _frontierAutopilot.Disengage(route.Ship);
                SetFrontierTransitDockBolts(route.Ship, false);
            }
        foreach (var pending in _frontierPendingDepartures.Values)
            StopFrontierIgnition(pending);
        _frontierRoutes.Clear();
        _frontierPendingDepartures.Clear();
        _frontierRoutesAllocatedThisRound = 0;
        _nextFrontierTick = TimeSpan.Zero;
        _frontierRecentCombat.Clear();
        _frontierControlDenials.Clear();
    }
}
