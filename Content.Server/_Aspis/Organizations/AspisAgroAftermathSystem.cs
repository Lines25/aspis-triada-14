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
using Content.Shared.Damage;
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
/// A single quarantined sample can be recovered for a secondary reward.
/// </summary>
public sealed class AspisAgroAftermathSystem : EntitySystem
{
    [Dependency] private WeatherSystem _weather = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private OrganizationSystem _organizations = default!;
    [Dependency] private AspisAgroStationSystem _station = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    private static readonly TimeSpan StormDuration = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan SampleDelay = TimeSpan.FromSeconds(40);
    private const float ToxicTickSeconds = 1f;
    private const int MaxPlayersPerTick = 64;
    private readonly Dictionary<EntityUid, Storm> _storms = new();
    private float _toxicTimer;
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
        public TimeSpan Started;
        public TimeSpan Ends;
        public EntityUid SampleNode;
        public bool SampleCreated;
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
        SubscribeLocalEvent<EntitySoldEvent>(OnCargoSold);
    }

    public void Activate(EntityUid surfaceMap, EntityUid airMap, string claimCode)
    {
        if (Deleted(surfaceMap) || Deleted(airMap) || string.IsNullOrWhiteSpace(claimCode))
            return;
        if (_storms.TryGetValue(surfaceMap, out var old))
        {
            // Reusing the same shared map must never duplicate weather systems.
            // A new contract can nevertheless create a NEW salvage sample.
            if (!string.Equals(old.ClaimCode, claimCode, StringComparison.Ordinal))
            {
                if (old.SampleNode.Valid && !Deleted(old.SampleNode))
                    QueueDel(old.SampleNode);
                old.ClaimCode = claimCode;
                old.SampleCreated = false;
                old.SampleNode = EntityUid.Invalid;
                old.Started = _timing.CurTime;
            }
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
            Started = now,
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
            if (!storm.SampleCreated && now - storm.Started >= SampleDelay)
            {
                storm.SampleCreated = true;
                // One specimen per completed spray, hidden in the planetary
                // wilderness instead of in a predictable fixed location.
                // The biome streams in only around explorers; this entity stays
                // on the canonical surface map awaiting discovery.
                var origin = _station.TryGetStationPosition(map, out var station)
                    ? station : new Vector2(210f, 88f);
                var radians = Random.Shared.NextDouble() * Math.Tau;
                var distance = 180f + (float)Random.Shared.NextDouble() * 240f;
                var position = origin + new Vector2(
                    (float)Math.Cos(radians), (float)Math.Sin(radians)) * distance;
                var variant = Random.Shared.Next(0, FloraNodePrototypes.Length);
                var node = Spawn(FloraNodePrototypes[variant], new EntityCoordinates(map, position));
                var component = EnsureComp<AspisAgroQuarantineNodeComponent>(node);
                component.ClaimCode = storm.ClaimCode;
                component.SurfaceMap = map;
                component.SamplePrototype = FloraSamplePrototypes[variant];
                storm.SampleNode = node;
                Logger.Info($"Aspis Agro: rare flora node spawned on {map} at {position} for {storm.ClaimCode}.");
            }
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
            Text = "Вилучити кислотну біокультуру",
            Act = () => Collect(uid, node, args.User)
        });
    }

    private void Collect(EntityUid uid, AspisAgroQuarantineNodeComponent node, EntityUid actor)
    {
        if (Deleted(uid) || Deleted(actor) ||
            string.IsNullOrWhiteSpace(node.ClaimCode) ||
            Transform(actor).MapUid != node.SurfaceMap)
            return;
        // Exposed crews can make a risky grab; PPE dramatically reduces injury.
        if (!HasBioSuit(actor) || !HasSuppliedMask(actor))
        {
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Poison", FixedPoint2.New(7));
            _damage.TryChangeDamage(actor, damage);
            _popup.PopupEntity("Відкритий контакт: гостре отруєння!", actor, actor);
        }
        var sample = Spawn(string.IsNullOrWhiteSpace(node.SamplePrototype) ? "AspisAgroQuarantineSample01" : node.SamplePrototype, Transform(uid).Coordinates);
        EnsureComp<AspisAgroQuarantineSampleComponent>(sample).ClaimCode = node.ClaimCode;
        _hands.TryPickupAnyHand(actor, sample);
        QueueDel(uid);
        if (_storms.TryGetValue(node.SurfaceMap, out var storm) && storm.SampleNode == uid)
            storm.SampleNode = EntityUid.Invalid;
        _popup.PopupEntity("Диворослину зібрано! Продайте її на вантажній платформі Cargo, щоб компанія отримала грант і вплив.",
            actor, actor);
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
