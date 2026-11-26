using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Hands.Systems;
using Content.Server.GameTicking;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Radio;
using Content.Server.Explosion.EntitySystems;
using Content.Shared._Aspis.Organizations;
using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Events;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Content.Shared.Chasm;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Content.Server.Parallax;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Gravity;
using Content.Shared.Inventory;
using Content.Shared.Mind.Components;
using Content.Shared.Light.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Parallax;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Utility;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Shared foundation for future planetary activities, not an agricultural quota.
/// Space -> atmospheric map uses the normal CoordinatesDisk + vanilla shuttle FTL.
/// Atmospheric map <-> planet surface uses a deliberate console transition.
/// No special touchdown-speed, height gauge or landing microphysics.
/// </summary>
public sealed class AspisPlanetaryAirspaceSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private ThrusterSystem _thrusters = default!;
    [Dependency] private ShuttleConsoleSystem _console = default!;
    [Dependency] private AspisWheatFieldSystem _wheat = default!;
    [Dependency] private AspisAgroStationSystem _agro = default!;
    [Dependency] private AspisAgriculturalFlightSystem _agricultural = default!;
    [Dependency] private AspisAgroAftermathSystem _aftermath = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private BiomeSystem _biomes = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private ExplosionSystem _explosions = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;

    private const string TestingBiome = "AspisWheatlands";
    private const string DiskPrototype = "CoordinatesDisk";
    private const string AirspaceParallax = "AspisWheatlandsSky";
    private const float MinimumAtmosphericSpeed = 20f;
    private const float MaximumAtmosphericTurnRate = 0.20f;
    // Preserve SS14's ChasmFalling animation during the atmospheric drop.
    // Surface descent and its dedicated landing state are handled separately.
    private static readonly TimeSpan FallingTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ParachuteSurfaceTime = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan FreefallSurfaceTime = TimeSpan.FromSeconds(0.8);
    private const float SurfaceDropDistance = 12f;
    private const float EngineOutDeceleration = 5f;
    private const float StallCrashDelay = 4.5f;
    private const float ArrivalGraceSeconds = 10f;
    private const int MaxTestSitesPerRound = 2;
    private static readonly TimeSpan TransferStartup = TimeSpan.FromSeconds(7);
    private readonly Dictionary<EntityUid, (EntityUid Pilot, Site TargetSite, bool ToAir, TimeSpan Deadline)> _pendingTransfers = new();
    private readonly SoundSpecifier _spool = new SoundPathSpecifier("/Audio/Effects/Shuttle/hyperspace_begin.ogg");
    private readonly SoundSpecifier _arrival = new SoundPathSpecifier("/Audio/Effects/Shuttle/hyperspace_end.ogg");
    private readonly SoundSpecifier _descentWind = new SoundPathSpecifier("/Audio/Effects/falling.ogg");
    private readonly SoundSpecifier _deployChute = new SoundPathSpecifier("/Audio/Effects/pop.ogg");
    private readonly SoundSpecifier _softTouchdown = new SoundPathSpecifier("/Audio/Effects/bodyfall1.ogg");
    private readonly SoundSpecifier _hardTouchdown = new SoundPathSpecifier("/Audio/Effects/bodyfall4.ogg");

    private sealed class Site
    {
        public EntityUid AirMap;
        public EntityUid SurfaceMap;
        public EntityUid Beacon;
        public readonly Dictionary<EntityUid, bool> InitiallyNoFtl = new();
        public readonly Dictionary<EntityUid, (float BodyModifier, float AngularMultiplier)> FlightDefaults = new();
        public readonly Dictionary<EntityUid, float> StallSeconds = new();
        public readonly HashSet<EntityUid> CrashResolved = new();
        public readonly HashSet<EntityUid> LaunchThrust = new();
        public readonly Dictionary<EntityUid, TimeSpan> AirEnteredAt = new();
    }

    private readonly List<Site> _sites = new();
    private sealed class Fall
    {
        public required Site Site;
        public bool OnSurface;
        public bool HasParachute;
        public Vector2 AirPosition;
        public TimeSpan Deadline;
        public EntityUid Canopy;
        public EntityUid Chute;
        public Vector2 SurfaceStart;
        public Vector2 SurfaceTarget;
        public TimeSpan SurfaceStarted;
    }
    private readonly Dictionary<EntityUid, Fall> _falls = new();
    private TimeSpan _nextPhysicsUpdate;
    private TimeSpan _nextConsoleMarkerRefresh;
    private readonly List<EntityUid> _pendingConsoleMarkers = new();

    public override void Initialize()
    {
        base.Initialize();
        // The vanilla server (or another Aspis extension) may already subscribe
        // ShuttleConsoleComponent + GetVerbsEvent. Robust rejects a second
        // subscription for the same component/event pair at server startup.
        // A dedicated marker keeps our planetary actions isolated.
        SubscribeLocalEvent<AspisPlanetaryConsoleVerbsComponent, GetVerbsEvent<AlternativeVerb>>(OnConsoleVerbs);
        SubscribeLocalEvent<ShuttleConsoleComponent, AspisPlanetaryTransferMessage>(OnPlanetaryNavMessage);
        SubscribeLocalEvent<AspisPlanetaryLandingComponent, UpdateCanMoveEvent>(OnLandingCanMove);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        foreach (var person in _falls.Keys.ToArray())
            CancelFall(person);
        _falls.Clear();
        foreach (var site in _sites)
            foreach (var (ship, defaults) in site.FlightDefaults)
                RestoreFlightDefaults(ship, defaults);
        _sites.Clear();
        _pendingTransfers.Clear();
        _nextPhysicsUpdate = TimeSpan.Zero;
        _nextConsoleMarkerRefresh = TimeSpan.Zero;
        _pendingConsoleMarkers.Clear();
    }

    /// <summary>
    /// Admin-only test entry. In production, an organization quota can call this
    /// method and issue a disk through its normal authorization workflow.
    /// </summary>
    public bool TryIssueTestRoute(EntityUid player, out string result)
        => TryIssueTestRoute(player, out result, out _);

    public bool TryIssueTestRoute(EntityUid player, out string result, out EntityUid issuedDisk)
    {
        issuedDisk = EntityUid.Invalid;
        result = "Не вдалося створити планетарний маршрут.";
        if (Deleted(player) ||
            !_prototypes.TryIndex<BiomeTemplatePrototype>(TestingBiome, out var biome) ||
            !_prototypes.HasIndex<EntityPrototype>(DiskPrototype))
        {
            result = "Відсутній біом AspisWheatlands або штатний координатний диск.";
            return false;
        }

        Site site;
        if (_sites.Count > 0 && !Deleted(_sites[^1].AirMap) && !Deleted(_sites[^1].SurfaceMap))
        {
            site = _sites[^1];
        }
        else
        {
            if (_sites.Count >= MaxTestSitesPerRound)
            {
                result = "Ліміт тестових планетарних маршрутів у раунді вичерпано.";
                return false;
            }

            EntityUid? air = null;
            EntityUid? ground = null;
            var created = false;
            try
            {
                air = _maps.CreateMap(out var airId);
                ground = _maps.CreateMap(out _);
                _biomes.EnsurePlanet(ground.Value, biome);
                _wheat.RegisterSurface(ground.Value);
                ApplyBreathableAtmosphere(ground.Value);
                ApplyBreathableAtmosphere(air.Value);
                ConfigureSynchronizedDaylight(ground.Value, air.Value);
                EnableInherentGravity(ground.Value);
                EnableInherentGravity(air.Value);

                var background = EnsureComp<ParallaxComponent>(air.Value);
                background.Parallax = AirspaceParallax;
                Dirty(air.Value, background);
                var radarBackground = EnsureComp<ShuttleMapParallaxComponent>(air.Value);
                radarBackground.TexturePath = new ResPath("/Textures/_Aspis/Parallaxes/wheatlands_land.png");
                Dirty(air.Value, radarBackground);

                _metadata.SetEntityName(air.Value, "Атмосфера: дослідна планета");
                _metadata.SetEntityName(ground.Value, "Поверхня: дослідна планета");
                // Networked map markers drive the planetary NAV UI without
                // injecting another handler into the stock shuttle console.
                var airRadar = EnsureComp<AspisPlanetaryRadarComponent>(air.Value);
                airRadar.Surface = false;
                Dirty(air.Value, airRadar);
                var groundRadar = EnsureComp<AspisPlanetaryRadarComponent>(ground.Value);
                groundRadar.Surface = true;
                Dirty(ground.Value, groundRadar);

                // Restrict by disk, but permit choosing either the beacon or a free point.
                if (!_shuttle.TryAddFTLDestination(airId, true, true, false, out _))
                {
                    result = "Не вдалося зареєструвати атмосферу в штатній системі FTL.";
                    return false;
                }

                // ShuttleConsoleSystem.FTL expects a beacon with the MAP as its
                // Transform parent. The map entity itself has no map-parented
                // coordinates, and shows an empty destination category in FTL UI.
                site = new Site { AirMap = air.Value, SurfaceMap = ground.Value };
                EnsureRouteBeacon(site);
                _sites.Add(site);
                // Failure of a custom expedition grid must not invalidate an FTL route.
                try { _agro.TryDeployStation(ground.Value); }
                catch (Exception ex) { Logger.Error($"Aspis Agro: station deployment failed: {ex}"); }
                created = true;
            }
            catch (Exception e)
            {
                Logger.Error($"Aspis planetary route setup failed: {e}");
                result = "Помилка під час створення тестової планети. Подробиці в серверному журналі.";
                return false;
            }
            finally
            {
                if (!created)
                {
                    if (air is { } sky && !Deleted(sky)) QueueDel(sky);
                    if (ground is { } surface && !Deleted(surface)) QueueDel(surface);
                }
            }
        }

        // A route can be re-issued during the round: repair a deleted marker.
        if (!site.Beacon.Valid || Deleted(site.Beacon))
            EnsureRouteBeacon(site);

        var disk = Spawn(DiskPrototype, Transform(player).Coordinates);
        issuedDisk = disk;
        var destination = EnsureComp<ShuttleDestinationCoordinatesComponent>(disk);
        destination.Destination = site.AirMap;
        Dirty(disk, destination);
        _metadata.SetEntityName(disk, "Координати атмосферного коридору");
        _hands.TryPickupAnyHand(player, disk);
        // First refresh ran before any disk existed; send the new beacon and
        // destination to already-open helms as soon as this route is issued.
        EnsureConsoleVerbMarkers();
        _console.RefreshShuttleConsoles();
        result = "Координатний диск видано. Вставте його у слот FTL-консолі, оберіть атмосферний маяк та здійсніть стрибок. " +
                 "Для посадки й зльоту скористайтеся діями шатлової консолі.";
        return true;
    }

    /// <summary>
    /// A real navigational marker must be a separate map child; putting the
    /// FTLBeacon on MapComponent's entity yields invalid/empty target coords.
    /// The beacon has no collision and creates no additional navigable grid.
    /// </summary>
    private void EnsureRouteBeacon(Site site)
    {
        if (site.Beacon.Valid && !Deleted(site.Beacon) &&
            HasComp<FTLBeaconComponent>(site.Beacon) &&
            Transform(site.Beacon).MapUid == site.AirMap)
            return;

        var beacon = Spawn(null, new EntityCoordinates(site.AirMap, Vector2.Zero));
        _metadata.SetEntityName(beacon, "Атмосферний навігаційний маяк");
        EnsureComp<FTLBeaconComponent>(beacon);
        site.Beacon = beacon;
        _console.RefreshShuttleConsoles();
        Logger.Info($"Aspis FTL: atmospheric route registered. map={site.AirMap}, beacon={beacon}");
    }

    private void ApplyBreathableAtmosphere(EntityUid surface)
    {
        // 101 kPa near 20 C for a 2500-L atmospheric tile.
        var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
        air.SetMoles(Gas.Oxygen, 21.8f);
        air.SetMoles(Gas.Nitrogen, 82.1f);
        _atmos.SetMapAtmosphere(surface, false, air);
    }

    private void ConfigureSynchronizedDaylight(EntityUid surface, EntityUid air)
    {
        // Both maps represent one planet. Do not give the atmospheric map a
        // separate, randomly phased light cycle. Start with a morning sky.
        // LightCycleSystem measures phase from round start, not map creation.
        // Offset against the current round elapsed time so a late accepted
        // mission ALSO begins its first planetary visit in the morning.
        var morning = TimeSpan.FromMinutes(7.5) -
            (_timing.CurTime - _ticker.RoundStartTimeSpan);
        var groundCycle = EnsureComp<LightCycleComponent>(surface);
        var skyCycle = EnsureComp<LightCycleComponent>(air);
        var skyLight = EnsureComp<MapLightComponent>(air);
        var dawnColor = Color.FromHex("#D8B059");
        skyLight.AmbientLightColor = dawnColor;
        Dirty(air, skyLight);
        groundCycle.Duration = TimeSpan.FromMinutes(30);
        groundCycle.InitialOffset = false;
        groundCycle.Offset = morning;
        skyCycle.Duration = groundCycle.Duration;
        skyCycle.Offset = groundCycle.Offset;
        skyCycle.InitialOffset = false;
        skyCycle.OriginalColor = dawnColor;
        skyCycle.MinLevel = groundCycle.MinLevel;
        skyCycle.MaxLevel = groundCycle.MaxLevel;
        skyCycle.ClipLevel = groundCycle.ClipLevel;
        skyCycle.MinLightLevel = groundCycle.MinLightLevel;
        skyCycle.MaxLightLevel = groundCycle.MaxLightLevel;
        skyCycle.ClipLight = groundCycle.ClipLight;
        Dirty(surface, groundCycle);
        Dirty(air, skyCycle);
    }

    private void EnableInherentGravity(EntityUid map)
    {
        var gravity = EnsureComp<GravityComponent>(map);
        gravity.Inherent = true;
        gravity.EnabledVV = true;
        Dirty(map, gravity);
    }

    private Site? FindSite(EntityUid? map)
    {
        if (map is not { } uid)
            return null;
        foreach (var site in _sites)
            if (!Deleted(site.AirMap) && !Deleted(site.SurfaceMap) &&
                (site.AirMap == uid || site.SurfaceMap == uid))
                return site;
        return null;
    }

    /// <summary>
    /// The planetary map is created at runtime. Existing and newly spawned
    /// shuttle consoles need our isolated verbs component to display
    /// landing / takeoff actions without conflicting with upstream events.
    /// </summary>
    private void EnsureConsoleVerbMarkers()
    {
        if (_sites.Count == 0)
            return;

        // Snapshot UIDs first: do not change the component set while traversing
        // the ShuttleConsoleComponent query.
        _pendingConsoleMarkers.Clear();
        var consoles = EntityQueryEnumerator<ShuttleConsoleComponent>();
        while (consoles.MoveNext(out var console, out _))
        {
            if (!HasComp<AspisPlanetaryConsoleVerbsComponent>(console))
                _pendingConsoleMarkers.Add(console);
        }

        foreach (var console in _pendingConsoleMarkers)
        {
            if (!Deleted(console) && HasComp<ShuttleConsoleComponent>(console))
                EnsureComp<AspisPlanetaryConsoleVerbsComponent>(console);
        }
        _pendingConsoleMarkers.Clear();
    }

    private void OnPlanetaryNavMessage(EntityUid uid, ShuttleConsoleComponent comp,
        AspisPlanetaryTransferMessage args)
    {
        // BUI authorization: client only supplies the requested direction. The
        // actor, ship, grid, site, docking and cooldown are all resolved here.
        var pilot = args.Actor;
        if (Deleted(pilot) || Transform(uid).GridUid is not { } ship ||
            Transform(pilot).GridUid != ship || !HasComp<ShuttleComponent>(ship)) return;
        var site = FindSite(Transform(ship).MapUid);
        if (site == null) return;
        var expected = args.ToAir ? site.SurfaceMap : site.AirMap;
        if (Transform(ship).MapUid != expected) return;
        QueueTransfer(ship, pilot, site, args.ToAir);
    }

    private void OnConsoleVerbs(EntityUid uid, AspisPlanetaryConsoleVerbsComponent component,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!HasComp<ShuttleConsoleComponent>(uid) ||
            !args.CanAccess || !args.CanInteract ||
            Transform(uid).GridUid is not { } ship ||
            Transform(args.User).GridUid != ship ||
            !HasComp<ShuttleComponent>(ship))
            return;

        var site = FindSite(Transform(ship).MapUid);
        if (site == null)
            return;

        if (Transform(ship).MapUid == site.AirMap)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Посадка на планету",
                Act = () => QueueTransfer(ship, args.User, site, false)
            });
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Атмосферні параметри польоту",
                Act = () => ReportAtmosphericStatus(ship, args.User, site)
            });
        }
        else
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Зліт в атмосферу",
                Act = () => QueueTransfer(ship, args.User, site, true)
            });
        }
    }

    private void ReportAtmosphericStatus(EntityUid ship, EntityUid pilot, Site site)
    {
        if (Deleted(ship) || Deleted(pilot) || Transform(pilot).GridUid != ship ||
            !TryComp<ShuttleComponent>(ship, out var shuttle) ||
            !TryComp<PhysicsComponent>(ship, out var body)) return;
        var hasEngines = shuttle.Enabled && shuttle.LinearThrusters.Any(t => t.Count > 0);
        var speed = body.LinearVelocity.Length();
        var stall = site.StallSeconds.GetValueOrDefault(ship);
        var description = $"АТМОСФЕРА | швидкість {speed:0.0} м/с; " +
                          $"двигуни {(hasEngines ? "активні" : "недоступні")}; " +
                          $"межа зриву 20 м/с; час зриву {stall:0.0}/{StallCrashDelay:0.0} с.";
        _popup.PopupEntity(description, pilot, pilot);
    }

    private void QueueTransfer(EntityUid ship, EntityUid pilot, Site site, bool toAir)
    {
        if (_pendingTransfers.ContainsKey(ship) || Deleted(ship) || Deleted(pilot) ||
            Transform(pilot).GridUid != ship ||
            _docking.GetDocks(ship).Any(d => d.Comp.Docked))
        {
            if (!Deleted(pilot))
                _popup.PopupEntity("Посадку/зліт відхилено: перевірте стикування й стан консолі.", pilot, pilot);
            return;
        }
        var expected = toAir ? site.SurfaceMap : site.AirMap;
        if (Transform(ship).MapUid != expected)
            return;
        _pendingTransfers.Add(ship, (pilot, site, toAir, _timing.CurTime + TransferStartup));
        if (TryComp<ShuttleComponent>(ship,out var engines) && engines.Enabled)
            _thrusters.EnableLinearThrustDirection(engines,
                toAir ? DirectionFlag.North : DirectionFlag.South);
        _audio.PlayPvs(_spool, ship);
        _popup.PopupEntity(toAir ? "Запуск двигунів: вихід в атмосферу..."
                                     : "Почато процедуру планетарної посадки...", pilot, pilot);
    }

    private void TransferShip(EntityUid ship, EntityUid pilot, Site site, bool toAir)
    {
        if (Deleted(ship) || Deleted(pilot))
            return;
        if (Transform(pilot).GridUid != ship ||
            !HasComp<MapGridComponent>(ship) ||
            !HasComp<ShuttleComponent>(ship) ||
            HasComp<FTLComponent>(ship) ||
            _docking.GetDocks(ship).Any(d => d.Comp.Docked) ||
            !TryComp<PhysicsComponent>(ship, out var body))
        {
            _popup.PopupEntity("Посадка неможлива: FTL зайнятий, шатл пристикований або втрачено керування.",
                pilot, pilot, PopupType.MediumCaution);
            Logger.Warning($"Aspis transfer rejected: ship={ship}, FTL={HasComp<FTLComponent>(ship)}, " +
                $"docked={_docking.GetDocks(ship).Any(d => d.Comp.Docked)}.");
            return;
        }

        var expected = toAir ? site.SurfaceMap : site.AirMap;
        var target = toAir ? site.AirMap : site.SurfaceMap;
        if (Transform(ship).MapUid != expected || Deleted(target))
        {
            Logger.Warning($"Aspis transfer aborted: incorrect source map for shuttle {ship}.");
            _popup.PopupEntity("Посадку скасовано: неправильна карта відправлення.", pilot, pilot);
            return;
        }

        var originalRotation = Transform(ship).WorldRotation;
        // Only first atmospheric landing records whether some other system
        // had already applied a NoFTL component to the shuttle.
        if (!toAir && !site.InitiallyNoFtl.ContainsKey(ship))
            site.InitiallyNoFtl[ship] = HasComp<NoFTLComponent>(ship);

        if (TryComp<ShuttleComponent>(ship,out var transitionEngine))
        {
            _thrusters.DisableLinearThrustDirection(transitionEngine,DirectionFlag.North);
            _thrusters.DisableLinearThrustDirection(transitionEngine,DirectionFlag.South);
        }
        _physics.SetLinearVelocity(ship, Vector2.Zero, body: body);
        _physics.SetAngularVelocity(ship, 0f, body: body);
        _transforms.SetParent(ship, Transform(ship), target);
        _transforms.SetWorldPosition(ship, toAir ? Vector2.Zero : new Vector2(24f, 24f));
        // Touchdown always aligns hull to the planet grid. Airborne heading is unaffected.
        _transforms.SetWorldRotation(ship, toAir ? originalRotation : Angle.Zero);

        if (!toAir)
        {
            _agricultural.NotifyPlanetaryTransition(ship, site.SurfaceMap, site.AirMap, false, pilot);
            _wheat.ClearShipLanding(site.SurfaceMap, ship);
            _agro.EnsureShipReceiver(ship, site.SurfaceMap);
            var agroBearing = _agro.Bearing(ship, site.SurfaceMap);
            _popup.PopupEntity(agroBearing, pilot, pilot);
            if (_aftermath.IsActive(site.SurfaceMap))
                _popup.PopupEntity(
                    "КАРАНТИН! Після гербіциду триває кислотний дощ. Для виходу потрібні захисний костюм, маска й активний балон. Нестабільні біокультури мають високу дослідницьку цінність.",
                    pilot, pilot, PopupType.MediumCaution);
            // Handheld is the stock portable-radio channel (1330 kHz). Sender
            // is a living pilot for correct speech/language handling; ship is
            // the broadcast origin. A popup is retained as a radio fallback.
            try
            {
                if (_prototypes.TryIndex<RadioChannelPrototype>("Handheld", out var channel))
                    _radio.SendRadioMessage(pilot,
                        $"[Планетарна служба] Посадку завершено. {agroBearing}",
                        channel, ship);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Aspis planetary bearing radio skipped: {ex.Message}");
            }
            if (site.FlightDefaults.Remove(ship, out var defaults))
                RestoreFlightDefaults(ship, defaults);
            site.StallSeconds.Remove(ship);
            site.AirEnteredAt.Remove(ship);
            site.LaunchThrust.Remove(ship);
        }
        _audio.PlayPvs(_arrival, ship);

        if (toAir)
        {
            if (site.InitiallyNoFtl.TryGetValue(ship, out var hadNoFtl) && !hadNoFtl)
                RemComp<NoFTLComponent>(ship);
            PrepareAtmosphericFlight(ship, site);
            _agricultural.NotifyPlanetaryTransition(ship, site.SurfaceMap, site.AirMap, true, pilot);
            if (TryComp<ShuttleComponent>(ship, out var poweredShip) &&
                poweredShip.Enabled && poweredShip.LinearThrusters.Any(t => t.Count > 0))
            {
                // Native ThrusterSystem drives REAL thrust and engine visuals.
                // A brief takeoff run automatically holds the forward thrusters.
                _thrusters.EnableLinearThrustDirection(poweredShip, DirectionFlag.North);
                site.LaunchThrust.Add(ship);
                _popup.PopupEntity("Двигуни активні: розгін до 20 м/с, далі керування пілотом.", pilot, pilot);
            }
            else
                _popup.PopupEntity("Увага: немає працюючих маршових двигунів!", pilot, pilot,
                    PopupType.MediumCaution);
        }
        else
        {
            EnsureComp<NoFTLComponent>(ship);
            _popup.PopupEntity("Посадку завершено.", pilot, pilot);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        foreach (var (ship, pending) in _pendingTransfers.ToArray())
        {
            if (Deleted(ship) || Deleted(pending.Pilot) ||
                Transform(pending.Pilot).GridUid != ship ||
                Transform(ship).MapUid != (pending.ToAir ? pending.TargetSite.SurfaceMap : pending.TargetSite.AirMap))
            {
                _pendingTransfers.Remove(ship);
                Logger.Warning($"Aspis transfer aborted: ship={ship}, invalid pilot/map during spool-up.");
                continue;
            }
            // FTL keeps its component during arrival/cooldown. The original
            // implementation silently dropped the landing action in that window.
            // Wait for it to finish instead of repeatedly resetting shuttle speed.
            if (HasComp<FTLComponent>(ship))
            {
                if (now > pending.Deadline + TimeSpan.FromSeconds(30))
                {
                    _pendingTransfers.Remove(ship);
                    _popup.PopupEntity("Планетарний перехід скасовано: FTL ще зайнятий.",
                        pending.Pilot, pending.Pilot);
                }
                continue;
            }
            if (now < pending.Deadline)
                continue;
            _pendingTransfers.Remove(ship);
            TransferShip(ship, pending.Pilot, pending.TargetSite, pending.ToAir);
        }
        if (_sites.Count > 0 && now >= _nextConsoleMarkerRefresh)
        {
            _nextConsoleMarkerRefresh = now + TimeSpan.FromSeconds(2);
            EnsureConsoleVerbMarkers();
        }

        // Ships may leave the atmosphere through vanilla FTL rather than our
        // surface transition. Restore spaceflight parameters when that FTL has
        // fully completed, or their zero-damping mode would leak into space.
        foreach (var site in _sites)
        foreach (var (ship, defaults) in site.FlightDefaults.ToArray())
        {
            if (!Deleted(ship) && (HasComp<FTLComponent>(ship) ||
                Transform(ship).MapUid == site.AirMap ||
                Transform(ship).MapUid == site.SurfaceMap))
                continue;
            RestoreFlightDefaults(ship, defaults);
            site.FlightDefaults.Remove(ship);
            site.StallSeconds.Remove(ship);
            site.AirEnteredAt.Remove(ship);
        }

        // Actor descent needs per-tick motion; throttling this with shuttle
        // aerodynamics produced 10 Hz hops even on a high-refresh client.
        UpdateFallingPeople(now);
        if (now < _nextPhysicsUpdate)
            return;
        _nextPhysicsUpdate = now + TimeSpan.FromSeconds(0.1);

        var shuttles = EntityQueryEnumerator<ShuttleComponent, PhysicsComponent, TransformComponent>();
        while (shuttles.MoveNext(out var ship, out var shuttle, out var body, out var xform))
        {
            var site = FindSite(xform.MapUid);
            if (site == null || xform.MapUid != site.AirMap || _pendingTransfers.ContainsKey(ship) ||
                (body.BodyType & BodyType.Dynamic) == 0 || HasComp<FTLComponent>(ship))
                continue;

            PrepareAtmosphericFlight(ship, site);
            var speed=body.LinearVelocity.Length();
            var northIndex = (int)Math.Log2((int)DirectionFlag.North);
            var actualForwardThrust = shuttle.Enabled &&
                shuttle.LinearThrusters[northIndex].Count > 0
                ? MathF.Max(0f, shuttle.LinearThrust[northIndex]) : 0f;
            var powered=actualForwardThrust > 0.01f;

            // Auto-launch and FTL arrival spool: only real, currently powered
            // north-facing thrusters may create velocity. The visible engine
            // flames are driven by ThrusterSystem; this impulse uses the
            // registered force and physical ship mass (not SetLinearVelocity).
            if (site.LaunchThrust.Contains(ship))
            {
                if (speed>=MinimumAtmosphericSpeed ||
                    now-site.AirEnteredAt.GetValueOrDefault(ship)>TimeSpan.FromSeconds(32))
                {
                    site.LaunchThrust.Remove(ship);
                    _thrusters.DisableLinearThrustDirection(shuttle, DirectionFlag.North);
                }
                else if (powered)
                {
                    _thrusters.EnableLinearThrustDirection(shuttle, DirectionFlag.North);
                    var mass=MathF.Max(1f,body.FixturesMass);
                    var deltaV=Math.Clamp(actualForwardThrust/mass*0.1f*6f,0f,1.2f);
                    if(deltaV>0f)
                    {
                        var heading=xform.WorldRotation.RotateVec(new Vector2(0f,1f));
                        _physics.ApplyLinearImpulse(ship,heading*(deltaV*mass),body:body);
                    }
                }
                else
                    _thrusters.DisableLinearThrustDirection(shuttle,DirectionFlag.North);
            }
            // Inertial travel is preserved with working engines. However a
            // vessel below minimum aerodynamic speed STILL stalls after grace
            // time, even if it has working engines but the pilot stops flying.
            // A ship leaving FTL gets time to accelerate normally first.
            shuttle.BodyModifier=powered?0f:0.25f;
            shuttle.DampingModifier=powered?0f:0.25f;
            var flightAge=now-site.AirEnteredAt.GetValueOrDefault(ship);
            if (flightAge.TotalSeconds>=ArrivalGraceSeconds && speed<MinimumAtmosphericSpeed &&
                !site.CrashResolved.Contains(ship))
            {
                var stalled=site.StallSeconds.GetValueOrDefault(ship)+0.1f;
                site.StallSeconds[ship]=stalled;
                if(stalled>=StallCrashDelay)
                {
                    site.StallSeconds.Remove(ship);
                    site.LaunchThrust.Remove(ship);
                    _thrusters.DisableLinearThrustDirection(shuttle,DirectionFlag.North);
                    CrashLandShip(ship,site);
                    continue;
                }
            }
            else if (speed>=MinimumAtmosphericSpeed)
                site.StallSeconds.Remove(ship);
            if (MathF.Abs(body.AngularVelocity) > MaximumAtmosphericTurnRate)
                _physics.SetAngularVelocity(ship,
                    MathF.Sign(body.AngularVelocity) * MaximumAtmosphericTurnRate, body: body);
        }

    }

    private void PrepareAtmosphericFlight(EntityUid ship, Site site)
    {
        if (!TryComp<ShuttleComponent>(ship, out var shuttle)) return;
        if (!site.FlightDefaults.ContainsKey(ship))
        {
            site.FlightDefaults[ship] = (shuttle.BodyModifier, shuttle.AngularMultiplier);
            site.AirEnteredAt[ship] = _timing.CurTime;
            site.CrashResolved.Remove(ship); // a new, separate atmospheric flight
            site.LaunchThrust.Add(ship); // also FTL -> air, not only surface -> air
            // Direct FTL arrival must register the same physical installed
            // sprayer as a normal NAV takeoff, never create another air map.
            _agricultural.NotifyPlanetaryTransition(ship, site.SurfaceMap,
                site.AirMap, true, EntityUid.Invalid);
        }
        // FTL and normal shuttle logic restore DampingModifier from BodyModifier.
        // Change both so it stays inertial during the atmospheric flight.
        shuttle.BodyModifier = 0f;
        shuttle.DampingModifier = 0f;
        shuttle.AngularMultiplier = MathF.Min(shuttle.AngularMultiplier, 0.35f);
    }

    private void RestoreFlightDefaults(EntityUid ship, (float BodyModifier, float AngularMultiplier) defaults)
    {
        if (Deleted(ship) || !TryComp<ShuttleComponent>(ship, out var shuttle)) return;
        shuttle.BodyModifier = defaults.BodyModifier;
        shuttle.DampingModifier = defaults.BodyModifier;
        shuttle.AngularMultiplier = defaults.AngularMultiplier;
    }

    private void CrashLandShip(EntityUid ship, Site site)
    {
        if (Deleted(ship) || Deleted(site.SurfaceMap) ||
            Transform(ship).MapUid != site.AirMap ||
            !TryComp<MapGridComponent>(ship, out var grid) ||
            !site.CrashResolved.Add(ship)) return;

        // A crash isn't an explosion in midair. First finish the forced descent,
        // THEN apply one bounded set of impacts on the surface grid.
        EntityUid pilot = EntityUid.Invalid;
        var passengers = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (passengers.MoveNext(out var uid, out var mind, out _, out var xform))
            if (mind.HasMind && xform.GridUid == ship) { pilot = uid; break; }

        if (!pilot.Valid)
        {
            Logger.Warning($"Aspis: engine-out ship {ship} has no crew. Crash postponed, no explosions queued.");
            site.CrashResolved.Remove(ship);
            return;
        }
        _popup.PopupEntity("КРИТИЧНА ВІДМОВА: двигуни недоступні. Аварійне зниження!",
            pilot, pilot, PopupType.MediumCaution);
        TransferShip(ship, pilot, site, false);
        if (Deleted(ship) || Transform(ship).MapUid != site.SurfaceMap)
        {
            site.CrashResolved.Remove(ship);
            return;
        }
        var rect = grid.LocalAABB;
        var marks = new[]
        {
            new Vector2(rect.Left * 0.55f, rect.Bottom * 0.50f),
            new Vector2(rect.Right * 0.55f, rect.Top * 0.50f),
            new Vector2(rect.Left * 0.20f + rect.Right * 0.25f, rect.Top * 0.25f),
        };
        foreach (var hit in marks)
            _explosions.QueueExplosion(new EntityCoordinates(ship, hit), "Default",
                24f, 8f, 8f, ship, canCreateVacuum: false, gridOnly: true);
        Logger.Warning($"Aspis: one-shot confirmed surface crash for shuttle {ship}.");
    }

    private void UpdateFallingPeople(TimeSpan now)
    {
        var people = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (people.MoveNext(out var person, out var mind, out _, out var xform))
        {
            if (!mind.HasMind || xform.GridUid != null ||
                FindSite(xform.MapUid) is not { } site || xform.MapUid != site.AirMap ||
                _falls.ContainsKey(person))
                continue;

            var parachute = FindParachute(person) != null;
            var fall = new Fall
            {
                Site = site, HasParachute = parachute,
                AirPosition = xform.WorldPosition,
                Deadline = now + FallingTime
            };
            _falls.Add(person, fall);
            // Keep the original ChasmFalling visual requested by players.
            // Its default deletion deadline is zero, so ALWAYS configure the
            // authoritative lifetime before the ChasmSystem next Update().
            var chasm = EnsureComp<ChasmFallingComponent>(person);
            chasm.AnimationTime = FallingTime;
            chasm.NextDeletionTime = now + FallingTime + TimeSpan.FromSeconds(5);
            var landing = EnsureComp<AspisPlanetaryLandingComponent>(person);
            landing.AnimationTime = FallingTime;
            landing.StartAt = now;
            landing.StartScale = 1f;
            Dirty(person, landing);
            _blocker.UpdateCanMove(person);
            _audio.PlayPvs(_descentWind, person);
            _popup.PopupEntity("Падіння крізь атмосферу!", person, person,
                PopupType.MediumCaution);
        }

        foreach (var (person, fall) in _falls.ToArray())
        {
            if (Deleted(person)) { CancelFall(person); continue; }
            var xform = Transform(person);
            if (!fall.OnSurface)
            {
                if (xform.MapUid != fall.Site.AirMap || xform.GridUid != null)
                { CancelFall(person); continue; }
                if (now < fall.Deadline) continue;
                BeginSurfaceDescent(person, fall, now);
            }
            else
            {
                if (xform.MapUid != fall.Site.SurfaceMap ||
                    (xform.GridUid is { } foreignGrid && foreignGrid != fall.Site.SurfaceMap))
                { CancelFall(person); continue; }
                // Move the networked ENTIRE ACTOR, not only its texture.
                // This is also what the controlled player/camera sees.
                AdvanceSurfaceDescent(person, fall, now);
                if (now >= fall.Deadline)
                    CompleteSurfaceDescent(person, fall);
            }
        }
    }

    private void OnLandingCanMove(EntityUid uid, AspisPlanetaryLandingComponent comp, UpdateCanMoveEvent args)
        => args.Cancel();

    private void BeginSurfaceDescent(EntityUid person, Fall fall, TimeSpan now)
    {
        if (Deleted(fall.Site.SurfaceMap)) { CancelFall(person); return; }
        // First change map (camera follows), then begin a NEW visual phase here.
        // Parachutists do NOT always land on the same pad. Sample a bounded
        // surface position; avoid placing them inside the agrostation grid.
        var landingPoint = new Vector2(24f, 24f);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var bearing = (float)(Random.Shared.NextDouble() * Math.Tau);
            var range = 65f + (float)Random.Shared.NextDouble() * 95f;
            var candidate = landingPoint + new Vector2(MathF.Cos(bearing), MathF.Sin(bearing)) * range;
            if (_agro.TryGetStationPosition(fall.Site.SurfaceMap, out var station) &&
                Vector2.Distance(candidate, station) < 50f)
                continue;
            landingPoint = candidate;
            break;
        }
        // The SS14 chasm phase has ended; restore character scale before
        // showing the real seven-second surface descent.
        if (HasComp<ChasmFallingComponent>(person)) RemComp<ChasmFallingComponent>(person);
        fall.Chute = FindParachute(person) ?? EntityUid.Invalid;
        fall.HasParachute = fall.Chute.Valid;
        var duration = fall.HasParachute ? ParachuteSurfaceTime : FreefallSurfaceTime;
        fall.SurfaceTarget = landingPoint;
        fall.SurfaceStart = landingPoint + new Vector2(0f, SurfaceDropDistance);
        fall.SurfaceStarted = now;
        fall.Deadline = now + duration;
        fall.OnSurface = true;

        // Reparent to the real destination map at the START of the descent,
        // not at the final landing pad. Camera follows the physical entity;
        // all clients receive the same falling coordinates.
        _transforms.SetParent(person, Transform(person), fall.Site.SurfaceMap);
        _transforms.SetWorldPosition(person, fall.SurfaceStart);
        var landing = EnsureComp<AspisPlanetaryLandingComponent>(person);
        landing.AnimationTime = duration;
        landing.StartAt = now;
        landing.StartScale = 1f; // character is NEVER scaled down.
        Dirty(person, landing);
        _blocker.UpdateCanMove(person);

        if (fall.HasParachute)
        {
            // Do NOT parent the canopy to the turning actor. A local-space
            // offset rotates around them in combat mode, making the Fulton
            // orbit. Instead follow their world position on the surface map.
            fall.Canopy = Spawn("AspisAtmosphericFultonCanopy",
                new EntityCoordinates(fall.Site.SurfaceMap, fall.SurfaceStart + new Vector2(0f, 0.9f)));
            _audio.PlayPvs(_deployChute, person);
            _popup.PopupEntity("Парашут розкрито. Ви знижуєтеся до поверхні планети.",
                person, person);
        }
        else
            _popup.PopupEntity("Стрімке падіння до поверхні!", person, person,
                PopupType.MediumCaution);
    }

    private void AdvanceSurfaceDescent(EntityUid person, Fall fall, TimeSpan now)
    {
        var duration = Math.Max(0.05, (fall.Deadline - fall.SurfaceStarted).TotalSeconds);
        var elapsed = (now - fall.SurfaceStarted).TotalSeconds;
        var progress = (float)Math.Clamp(elapsed / duration, 0d, 1d);
        // Real server-side position: camera, local player and other witnesses
        // see the SAME seven-second descent, without fake sprite offsets.
        var position = Vector2.Lerp(fall.SurfaceStart, fall.SurfaceTarget, progress);
        _transforms.SetWorldPosition(person, position);
        if (fall.Canopy.Valid && !Deleted(fall.Canopy))
            _transforms.SetWorldPosition(fall.Canopy, position + new Vector2(0f, 0.9f));
    }

    private void CompleteSurfaceDescent(EntityUid person, Fall fall)
    {
        _transforms.SetWorldPosition(person, fall.SurfaceTarget);
        if (fall.HasParachute && fall.Chute.Valid && !Deleted(fall.Chute))
            QueueDel(fall.Chute);
        if (!fall.HasParachute && HasComp<DamageableComponent>(person))
        {
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Blunt", FixedPoint2.New(140));
            _damage.TryChangeDamage(person, damage, ignoreResistances: true);
        }
        _audio.PlayPvs(fall.HasParachute ? _softTouchdown : _hardTouchdown, person);
        _popup.PopupEntity(fall.HasParachute ? "М'яке приземлення. Парашут згорнуто."
            : "Сильний удар об ґрунт!", person, person,
            fall.HasParachute ? PopupType.Small : PopupType.MediumCaution);
        CancelFall(person);
    }

    private EntityUid? FindParachute(EntityUid person)
    {
        // RMC-like descents require an EQUIPPED backpack, not a parachute
        // carried in a pocket or held in a hand.
        var inventory = EntityManager.System<InventorySystem>();
        if (inventory.TryGetSlotEntity(person, "back", out var back) &&
            back is { } backpack && HasComp<AspisPlanetaryParachuteComponent>(backpack))
            return backpack;
        return null;
    }

    private void CancelFall(EntityUid person)
    {
        if (_falls.Remove(person, out var fall) && fall.Canopy.Valid && !Deleted(fall.Canopy))
            QueueDel(fall.Canopy);
        if (Deleted(person)) return;
        if (HasComp<ChasmFallingComponent>(person))
            RemComp<ChasmFallingComponent>(person);
        if (HasComp<AspisPlanetaryLandingComponent>(person))
            RemComp<AspisPlanetaryLandingComponent>(person);
        _blocker.UpdateCanMove(person);
    }

}

[RegisterComponent]
public sealed partial class AspisPlanetaryParachuteComponent : Component
{
}

/// <summary>
/// Local verb hook for the planetary landing system. Do not register
/// GetVerbsEvent directly on ShuttleConsoleComponent: it may be owned by
/// another EntitySystem, which aborts Robust initialization.
/// </summary>
[RegisterComponent]
public sealed partial class AspisPlanetaryConsoleVerbsComponent : Component
{
}
