using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Cargo.Systems;
using Robust.Shared.Containers;
using Content.Server.Hands.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Weather;
using Content.Shared.Atmos.Components;
using Content.Shared.Body.Components;
using Content.Shared.Cargo.Components;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared._Aspis.Organizations;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Content.Shared.Light.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Content.Shared.Weather;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Shared.Parallax.Biomes;
using Content.Shared.GameTicking;

namespace Content.Server._Aspis.Organizations;

[RegisterComponent]
public sealed partial class AspisAgroQuarantineNodeComponent : Component
{
    public string ClaimCode = string.Empty;
    public EntityUid SurfaceMap;
    public string SamplePrototype = "AspisAgroQuarantineSample01";
}

[RegisterComponent]
public sealed partial class AspisAgroQuarantineSampleComponent : Component
{
    public string ClaimCode = string.Empty;
}

/// <summary>
/// Delayed environmental consequence of ONE verified agricultural spraying.
/// Existing engine rain and light-cycle replication make the whole streamed
/// map look contaminated without spawning hundreds of physics entities.
/// Poison is applied only outdoors; a bio-suit AND functioning mask/internals
/// protect against the combined rain and inhalation hazard.
/// A bounded group of quarantined samples can be recovered for Cargo sales and a one-time corporate grant.
/// </summary>
public sealed class AspisAgroAftermathSystem : EntitySystem
{
    [Dependency] private WeatherSystem _weather = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private SharedDoAfterSystem _floraDoAfter = default!;
    [Dependency] private OrganizationSystem _organizations = default!;
    [Dependency] private AspisAgroStationSystem _station = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;

    private static readonly TimeSpan StormDuration = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan SampleDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FloraHarvestDelay = TimeSpan.FromSeconds(2);
    private const int SamplesPerSpraying = 12;
    private const double HarvestedFloraPrice = 25_000;
    private const float MinFloraSpacing = 5f;
    private const float ToxicTickSeconds = 1f;
    private const int MaxPlayersPerTick = 64;
    private readonly Dictionary<EntityUid, Storm> _storms = new();
    private float _toxicTimer;
    private float _floraTimer;
    private readonly Dictionary<string, PendingFlora> _pendingFlora = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scheduledFlora = new(StringComparer.Ordinal);
    private sealed record PendingFlora(EntityUid Map, TimeSpan ReadyAt, int Spawned = 0);
    private readonly Dictionary<EntityUid, HashSet<Vector2i>> _floraCells = new();
    private static readonly string[] FloraNodePrototypes =
    {
        "AspisAgroQuarantineNode01", "AspisAgroQuarantineNode02", "AspisAgroQuarantineNode03",
        "AspisAgroQuarantineNode04", "AspisAgroQuarantineNode05", "AspisAgroQuarantineNode06"
    };
    private static readonly string[] FloraSamplePrototypes =
    {
        "AspisAgroQuarantineSample01", "AspisAgroQuarantineSample02", "AspisAgroQuarantineSample03",
        "AspisAgroQuarantineSample04", "AspisAgroQuarantineSample05", "AspisAgroQuarantineSample06"
    };

    private sealed class Storm
    {
        public EntityUid AirMap;
        public EntityUid SurfaceMap;
        public string ClaimCode = string.Empty;
        public TimeSpan Ends;
        public readonly Dictionary<EntityUid, Lighting> OriginalLight = new();
    }

    private readonly record struct Lighting(
        Color MinLevel, Color MaxLevel, Color ClipLevel,
        float MinLightLevel, float MaxLightLevel, float ClipLight);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AspisAgroQuarantineNodeComponent,
            GetVerbsEvent<AlternativeVerb>>(OnNodeVerbs);
        SubscribeLocalEvent<AspisAgroQuarantineNodeComponent, InteractHandEvent>(OnNodeHand);
        SubscribeLocalEvent<AspisAgroQuarantineNodeComponent, AspisAgroCollectDoAfterEvent>(OnNodeHarvested);
        SubscribeLocalEvent<EntitySoldEvent>(OnCargoSold);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _storms.Clear();
        _pendingFlora.Clear();
        _scheduledFlora.Clear();
        _floraCells.Clear();
        _toxicTimer = 0f;
        _floraTimer = 0f;
    }

    public void Activate(EntityUid surfaceMap, EntityUid airMap, string claimCode)
    {
        if (Deleted(surfaceMap) || Deleted(airMap) || string.IsNullOrWhiteSpace(claimCode))
            return;
        // One bounded batch per completed spraying, independent of later storms.
        if (_scheduledFlora.Add(claimCode))
            _pendingFlora[claimCode] = new PendingFlora(surfaceMap, _timing.CurTime + SampleDelay);
        if (_storms.TryGetValue(surfaceMap, out var old))
        {
            old.ClaimCode = claimCode;
            old.Ends = _timing.CurTime + StormDuration;
            if (_prototypes.TryIndex<WeatherPrototype>("AspisAgroAcidRain", out var renewed))
                _weather.SetWeather(Transform(surfaceMap).MapID, renewed, old.Ends);
            return;
        }

        var now = _timing.CurTime;
        var storm = new Storm
        {
            SurfaceMap = surfaceMap,
            AirMap = airMap,
            ClaimCode = claimCode,
            Ends = now + StormDuration
        };
        _storms.Add(surfaceMap, storm);
        SetToxicLight(storm, surfaceMap);
        SetToxicLight(storm, airMap);
        if (_prototypes.TryIndex<WeatherPrototype>("AspisAgroAcidRain", out var weather))
            _weather.SetWeather(Transform(surfaceMap).MapID, weather, storm.Ends);
        Logger.Info($"Aspis Agro: acid storm enabled for {surfaceMap}, claim {claimCode}.");
    }

    public bool IsActive(EntityUid surfaceMap) =>
        _storms.TryGetValue(surfaceMap, out var storm) && _timing.CurTime < storm.Ends;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        foreach (var (map, storm) in _storms.ToArray())
        {
            if (Deleted(map) || now >= storm.Ends)
            {
                Finish(storm);
                _storms.Remove(map);
                continue;
            }
        }

        _floraTimer += frameTime;
        if (_floraTimer >= 2f)
        {
            _floraTimer = 0f;
            TrySpawnPendingFlora(now);
        }

        if (_storms.Count == 0)
            return;
        _toxicTimer += frameTime;
        if (_toxicTimer < ToxicTickSeconds)
            return;
        _toxicTimer = 0f;

        // Work O(online living players), not O(all planet tiles/entities).
        var people = EntityQueryEnumerator<MindContainerComponent, DamageableComponent, TransformComponent>();
        var counted = 0;
        while (people.MoveNext(out var uid, out var mind, out _, out var xform))
        {
            if (!mind.HasMind || xform.MapUid is not { } map || !_storms.ContainsKey(map) ||
                // Do not poison people inside a real station, shuttle or room grid.
                (xform.GridUid is { } indoor && indoor != map))
                continue;
            if (++counted > MaxPlayersPerTick)
                break;
            var protectedSuit = HasBioSuit(uid);
            var protectedBreathing = HasSuppliedMask(uid);
            if (protectedSuit && protectedBreathing)
                continue;
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Poison", FixedPoint2.New(protectedBreathing ? 1 : 3));
            if (!protectedSuit)
                damage.DamageDict.Add("Caustic", FixedPoint2.New(2));
            _damage.TryChangeDamage(uid, damage, ignoreResistances: false);
        }
    }

    private void TrySpawnPendingFlora(TimeSpan now)
    {
        if (_pendingFlora.Count == 0)
            return;

        // Only search already streamed wilderness around people on the surface.
        // Never place an entity into an unloaded tile and never force-load a planet.
        var explorers = new Dictionary<EntityUid, List<Vector2>>();
        var people = EntityQueryEnumerator<MindContainerComponent, TransformComponent>();
        while (people.MoveNext(out _, out var mind, out var xform))
        {
            if (!mind.HasMind || xform.MapUid is not { } map ||
                (xform.GridUid is { } indoor && indoor != map))
                continue;
            if (!explorers.TryGetValue(map, out var positions))
                explorers[map] = positions = new List<Vector2>();
            positions.Add(xform.WorldPosition);
        }

        foreach (var (code, pending) in _pendingFlora.ToArray())
        {
            var map = pending.Map;
            if (Deleted(map))
            {
                _pendingFlora.Remove(code);
                continue;
            }
            if (now < pending.ReadyAt || !explorers.TryGetValue(map, out var positions) ||
                !TryComp<MapGridComponent>(map, out var grid) || !TryComp<BiomeComponent>(map, out var biome))
                continue;
            var station = _station.TryGetStationPosition(map, out var basePosition) ? basePosition : new Vector2(210, 88);
            var wheat = _tiles["FloorAspisWheat"].TileId;
            if (!_floraCells.TryGetValue(map, out var usedCells))
                _floraCells[map] = usedCells = new HashSet<Vector2i>();
            Vector2i? chosen = null;
            foreach (var origin in positions)
            {
                // Keep specimens outside the base and within a short exploration walk.
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    var angle = Random.Shared.NextDouble() * Math.Tau;
                    var radius = 5f + (float)Random.Shared.NextDouble() * 17f;
                    var point = origin + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius;
                    if (Vector2.DistanceSquared(point, station) < 20f * 20f)
                        continue;
                    var index = new Vector2i((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y));
                    if (!_maps.TryGetTileRef(map, grid, index, out var tile) || tile.Tile.IsEmpty ||
                        tile.Tile.TypeId != wheat)
                        continue;
                    if (usedCells.Any(previous =>
                    {
                        var dx = previous.X - index.X;
                        var dy = previous.Y - index.Y;
                        return dx * dx + dy * dy < MinFloraSpacing * MinFloraSpacing;
                    }))
                        continue;
                    chosen = index;
                    break;
                }
                if (chosen != null)
                    break;
            }
            if (chosen is not { } cell)
                continue;
            var variant = Random.Shared.Next(FloraNodePrototypes.Length);
            if (!_prototypes.HasIndex<EntityPrototype>(FloraNodePrototypes[variant]) ||
                !_prototypes.HasIndex<EntityPrototype>(FloraSamplePrototypes[variant]))
                continue;
            // Retain each supporting tile when the rest of the biome unloads.
            var chunk = SharedMapSystem.GetChunkIndices(cell, 8) * 8;
            biome.ModifiedTiles.GetOrNew(chunk).Add(cell);
            var position = new Vector2(cell.X + 0.5f, cell.Y + 0.5f);
            var node = Spawn(FloraNodePrototypes[variant], new EntityCoordinates(map, position));
            var component = EnsureComp<AspisAgroQuarantineNodeComponent>(node);
            component.ClaimCode = code;
            component.SurfaceMap = map;
            component.SamplePrototype = FloraSamplePrototypes[variant];
            usedCells.Add(cell);
            var spawned = pending.Spawned + 1;
            if (spawned >= SamplesPerSpraying)
                _pendingFlora.Remove(code);
            else
                _pendingFlora[code] = pending with { Spawned = spawned };
            Logger.Info($"Aspis Agro: flora {spawned}/{SamplesPerSpraying} spawned on loaded wheat at {position}, map={map}, claim={code}.");
        }
    }

    private bool HasBioSuit(EntityUid actor)
    {
        if (!_inventory.TryGetSlotEntity(actor, "outerClothing", out var outer) ||
            outer is not { } suit || Deleted(suit))
            return false;
        // Accept the standard biohazard suit and its named variants only.
        var id = MetaData(suit).EntityPrototype?.ID ?? string.Empty;
        return id.StartsWith("ClothingOuterBio", StringComparison.Ordinal);
    }

    private bool HasSuppliedMask(EntityUid actor)
    {
        if (!_inventory.TryGetSlotEntity(actor, "mask", out var slot) ||
            slot is not { } mask || !TryComp<BreathToolComponent>(mask, out var tool) ||
            !tool.IsFunctional ||
            !TryComp<InternalsComponent>(actor, out var internals) ||
            internals.GasTankEntity is not { } tank ||
            !TryComp<GasTankComponent>(tank, out var air))
            return false;
        return air.Air.Pressure > 15f;
    }

    private void SetToxicLight(Storm storm, EntityUid map)
    {
        if (!TryComp<LightCycleComponent>(map, out var cycle))
            return;
        storm.OriginalLight[map] = new Lighting(
            cycle.MinLevel, cycle.MaxLevel, cycle.ClipLevel,
            cycle.MinLightLevel, cycle.MaxLightLevel, cycle.ClipLight);
        cycle.MinLevel = new Color(0.06f, 0.30f, 0.08f);
        cycle.MaxLevel = new Color(0.26f, 0.85f, 0.24f);
        cycle.ClipLevel = new Color(0.32f, 0.90f, 0.35f);
        cycle.MinLightLevel = 0.10f;
        cycle.MaxLightLevel = 0.65f;
        cycle.ClipLight = 0.65f;
        Dirty(map, cycle);
    }

    private void Finish(Storm storm)
    {
        if (!Deleted(storm.SurfaceMap))
            _weather.SetWeather(Transform(storm.SurfaceMap).MapID, null, null);
        foreach (var (map, saved) in storm.OriginalLight)
        {
            if (Deleted(map) || !TryComp<LightCycleComponent>(map, out var cycle))
                continue;
            cycle.MinLevel = saved.MinLevel;
            cycle.MaxLevel = saved.MaxLevel;
            cycle.ClipLevel = saved.ClipLevel;
            cycle.MinLightLevel = saved.MinLightLevel;
            cycle.MaxLightLevel = saved.MaxLightLevel;
            cycle.ClipLight = saved.ClipLight;
            Dirty(map, cycle);
        }
        // The flora survives the rain and remains discoverable after the storm.
        // Its one-time sale is bound to the original completed quota.
    }

    private void OnNodeVerbs(EntityUid uid, AspisAgroQuarantineNodeComponent node,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || Deleted(uid))
            return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Вирвати диворослину",
            Act = () => BeginHarvest(uid, node, args.User)
        });
    }

    private void OnNodeHand(EntityUid uid, AspisAgroQuarantineNodeComponent node, InteractHandEvent args)
    {
        if (args.Handled)
            return;
        args.Handled = true;
        BeginHarvest(uid, node, args.User);
    }

    private void BeginHarvest(EntityUid uid, AspisAgroQuarantineNodeComponent node, EntityUid actor)
    {
        if (Deleted(uid) || Deleted(actor) || string.IsNullOrWhiteSpace(node.ClaimCode) ||
            Transform(actor).MapUid != node.SurfaceMap)
            return;

        var harvest = new DoAfterArgs(EntityManager, actor, FloraHarvestDelay,
            new AspisAgroCollectDoAfterEvent(), uid, target: uid)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnWeightlessMove = true,
            // Toxic rain ticks must not make collecting impossible without PPE.
            BreakOnDamage = false,
            MovementThreshold = 0.15f,
            DistanceThreshold = 1.5f,
            BlockDuplicate = true,
            CancelDuplicate = false,
            MultiplyDelay = false
        };
        _floraDoAfter.TryStartDoAfter(harvest);
    }

    private void OnNodeHarvested(EntityUid uid, AspisAgroQuarantineNodeComponent node,
        AspisAgroCollectDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target != uid)
            return;
        args.Handled = true;
        Collect(uid, node, args.User);
    }

    private void Collect(EntityUid uid, AspisAgroQuarantineNodeComponent node, EntityUid actor)
    {
        if (Deleted(uid) || Deleted(actor) ||
            string.IsNullOrWhiteSpace(node.ClaimCode) ||
            Transform(actor).MapUid != node.SurfaceMap)
            return;
        // Invalidate the physical node before spawning or queuing deletion; two clicks cannot duplicate it.
        var claim = node.ClaimCode;
        node.ClaimCode = string.Empty;
        // Exposed crews can make a risky grab; PPE dramatically reduces injury.
        if (!HasBioSuit(actor) || !HasSuppliedMask(actor))
        {
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Poison", FixedPoint2.New(7));
            _damage.TryChangeDamage(actor, damage);
            _popup.PopupEntity("Відкритий контакт: гостре отруєння!", actor, actor);
        }
        var sample = Spawn(string.IsNullOrWhiteSpace(node.SamplePrototype) ? "AspisAgroQuarantineSample01" : node.SamplePrototype, Transform(uid).Coordinates);
        EnsureComp<AspisAgroQuarantineSampleComponent>(sample).ClaimCode = claim;
        // Set the real sale price on authenticated harvested items. Do not
        // overwrite user-edited names/descriptions in the plant YAML prototypes.
        EnsureComp<StaticPriceComponent>(sample).Price = HarvestedFloraPrice;
        ClearHarvestedFloraTile(uid, node.SurfaceMap);
        var pickedUp = _hands.TryPickupAnyHand(actor, sample);
        QueueDel(uid);
        _popup.PopupEntity(pickedUp
                ? "Диворослину зібрано! Продайте зразок через Cargo."
                : "Диворослину вирвано. Звільніть руку, щоб підібрати зразок.",
            actor, actor);
    }

    private void ClearHarvestedFloraTile(EntityUid node, EntityUid map)
    {
        if (!TryComp<MapGridComponent>(map, out var grid) || !TryComp<BiomeComponent>(map, out var biome))
            return;
        var position = Transform(node).WorldPosition;
        var cell = new Vector2i((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y));
        if (!_floraCells.TryGetValue(map, out var usedCells))
            _floraCells[map] = usedCells = new HashSet<Vector2i>();
        usedCells.Add(cell);
        // Persist the bare tile across normal biome chunk unload/reload, just
        // like the existing permanent shuttle landing clearance.
        if (!_maps.TryGetTileRef(map, grid, cell, out var tile) ||
            tile.Tile.TypeId != _tiles["FloorAspisWheat"].TileId)
            return;
        var chunk = SharedMapSystem.GetChunkIndices(cell, 8) * 8;
        biome.ModifiedTiles.GetOrNew(chunk).Add(cell);
        _maps.SetTiles(map, grid, new List<(Vector2i, Tile)> { (cell, new Tile(_tiles["FloorDesert"].TileId)) });
    }

    /// <summary>
    /// A real cargo-pallet sale fires before deleting the sold items.
    /// The credit is paid once to the company owning the completed contract,
    /// never by using the item on a shuttle or a PDA.
    /// </summary>
    private void OnCargoSold(ref EntitySoldEvent args)
    {
        var visited = new HashSet<EntityUid>();
        var pending = new Stack<EntityUid>(args.Sold);
        while (pending.Count > 0 && visited.Count < 512)
        {
            var entity = pending.Pop();
            if (!visited.Add(entity) || Deleted(entity))
                continue;
            if (TryComp<AspisAgroQuarantineSampleComponent>(entity, out var plant))
            {
                if (_organizations.TryAwardAspisQuarantineSampleCargo(plant.ClaimCode))
                    Logger.Info($"Aspis Agro: Cargo sold miracle plant for quota {plant.ClaimCode}; corporate reward applied.");
                continue;
            }
            if (!TryComp<ContainerManagerComponent>(entity, out var containers))
                continue;
            foreach (var container in containers.Containers.Values)
                foreach (var child in container.ContainedEntities)
                    pending.Push(child);
        }
    }
}
