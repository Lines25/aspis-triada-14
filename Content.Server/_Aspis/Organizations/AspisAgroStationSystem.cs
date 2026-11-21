using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Server.Parallax;
using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Interaction;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Server.Hands.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Atmos;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Content.Shared.Verbs;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using Robust.Shared.Containers;

namespace Content.Server._Aspis.Organizations;

[RegisterComponent]
public sealed partial class AspisAgroCargoComponent : Component { }

[RegisterComponent]
public sealed partial class AspisAgroSupplyCrateComponent : Component
{
    [DataField] public bool Dispensed; // legacy map field, no longer global mission lock
    public readonly HashSet<string> DispensedClaims = new(StringComparer.Ordinal);
}

[RegisterComponent]
public sealed partial class AspisAgroReceiverComponent : Component
{
    public EntityUid SurfaceMap;
    public int Delivered;
}

/// <summary>
/// Installs the player's authored agrostation on the shared planetary surface.
/// This is a real grid, not a separate instance / FTL destination. First slice:
/// retrieve eight sample canisters and insert them into the shuttle's receiver.
/// There is deliberately no automatic quota money transfer yet.
/// </summary>
public sealed class AspisAgroStationSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private BiomeSystem _biomes = default!;
    [Dependency] private ShuttleSystem _shuttle = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private HandsSystem _hands = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private OrganizationSystem _organizations = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    private static readonly ResPath StationPath = new("/Maps/_Aspis/Agro_Station.yml");
    private const int TargetTotal = 1;
    private static readonly SoundSpecifier DispenseSound = new SoundPathSpecifier("/Audio/_Aspis/Agro/dispense.ogg");
    private static readonly SoundSpecifier InsertSound = new SoundPathSpecifier("/Audio/_Aspis/Agro/sprayer_insert.ogg");
    private readonly Dictionary<EntityUid, EntityUid> _stationGrids = new();
    private readonly Dictionary<EntityUid, EntityUid> _receivers = new();
    private readonly List<(Vector2i Index, Tile Tile)> _reserved = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AspisAgroCargoComponent, AfterInteractEvent>(OnCanisterUsed);
        SubscribeLocalEvent<AspisAgroSupplyCrateComponent, InteractHandEvent>(OnSupplyUsed);
        SubscribeLocalEvent<AspisAgroReceiverComponent, GetVerbsEvent<AlternativeVerb>>(OnReceiverVerbs);
    }

    public bool TryDeployStation(EntityUid surfaceMap)
    {
        if (_stationGrids.TryGetValue(surfaceMap, out var existing) && !Deleted(existing))
            return true;
        if (!TryComp<MapComponent>(surfaceMap, out var map))
            return false;

        // A deliberate expedition walk: 190+ metres from the landing pad.
        // Fixed position, so PDA bearings and map bounds remain authoritative.
        var position = new Vector2(210, 88);
        // Reserve before map load so the biome cannot grow wheat through the base.
        _reserved.Clear();
        _biomes.ReserveTiles(surfaceMap,
            new Box2(position - new Vector2(23, 21), position + new Vector2(23, 21)), _reserved);

        if (!_loader.TryLoadGrid(map.MapId, StationPath, out var loaded, offset: position) || loaded is null)
        {
            Logger.Error("Aspis Agro: cannot load /Maps/_Aspis/Agro_Station.yml");
            return false;
        }
        var grid = loaded.Value.Owner;
        _metadata.SetEntityName(grid, "Агростанція");
        // Keep the authored facility stationary, not flyable as another shuttle.
        EnsureComp<ShuttleComponent>(grid);
        _shuttle.Disable(grid, force: true);
        Comp<ShuttleComponent>(grid).Enabled = false;
        EnsureComp<NoFTLComponent>(grid);
        _stationGrids[surfaceMap] = grid;
        if (FillStationAir(grid) == 0)
            _airFillAttempts[grid] = 0;

        // The authored CryoPod slot is now a one-shot dispenser with the
        // user's colb.png artwork. Do not spawn a free flask at map load.
        var sources = EntityQueryEnumerator<AspisAgroSupplyCrateComponent, TransformComponent>();
        var found = false;
        while (sources.MoveNext(out _, out _, out var sourceXform))
            if (sourceXform.GridUid == grid) { found = true; break; }
        if (!found)
        {
            Logger.Error("Aspis Agro: one-shot source missing on the authored station grid.");
            return false;
        }
        Logger.Info($"Aspis Agro: station={grid}, surface={surfaceMap}; dispenser is one-shot; offset={position}");
        return true;
    }

    private readonly Dictionary<EntityUid, int> _airFillAttempts = new();
    private float _airFillTimer;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_airFillAttempts.Count == 0)
            return;
        _airFillTimer += frameTime;
        if (_airFillTimer < 2f)
            return;
        _airFillTimer = 0f;
        foreach (var grid in new List<EntityUid>(_airFillAttempts.Keys))
        {
            if (Deleted(grid))
            {
                _airFillAttempts.Remove(grid);
                continue;
            }
            var attempts = _airFillAttempts[grid] + 1;
            _airFillAttempts[grid] = attempts;
            var cells = FillStationAir(grid);
            if (cells > 0 || attempts >= 12)
            {
                _airFillAttempts.Remove(grid);
                if (cells == 0)
                    Logger.Warning($"Aspis Agro: no initialized station air cells on grid {grid} after {attempts} attempts.");
            }
        }
    }

    private int FillStationAir(EntityUid stationGrid)
    {
        // The saved map includes a GridAtmosphere component, but its sealed
        // rooms can be initialized with vacuum. Fill ONLY already-instantiated
        // tile mixtures; no new map chunks or atmos entities are created.
        var count = 0;
        var checkedCells = 0;
        foreach (var mixture in _atmos.GetAllMixtures(stationGrid))
        {
            checkedCells++;
            if (mixture.Pressure > 50f)
                continue;
            mixture.SetMoles(Gas.Oxygen, 21.8f);
            mixture.SetMoles(Gas.Nitrogen, 82.1f);
            mixture.Temperature = Atmospherics.T20C;
            count++;
        }
        if (count > 0)
            Logger.Info($"Aspis Agro: filled {count}/{checkedCells} station tile mixtures with breathable air.");
        return checkedCells;
    }

    public void EnsureShipReceiver(EntityUid ship, EntityUid surfaceMap)
    {
        // Q2.5F: a flatpacked sprayer IS the receiving machine. The old extra
        // receiver was the second, redundant machine on every landed ship.
        var receivers = EntityQueryEnumerator<AspisAgroReceiverComponent, TransformComponent>();
        while (receivers.MoveNext(out var uid, out _, out var xform))
            if (xform.GridUid == ship && !Deleted(uid))
                QueueDel(uid);
    }

    private void OnSupplyUsed(EntityUid uid, AspisAgroSupplyCrateComponent source,
        InteractHandEvent args)
    {
        if (args.Handled)
            return;
        args.Handled = true;
        if (!_organizations.TryGetActiveAspisAgroQuotaCode(args.User, out var claimCode))
        {
            _popup.PopupEntity("Спочатку прийміть активну планетарну аграрну квоту.",
                args.User, args.User);
            return;
        }
        if (!source.DispensedClaims.Add(claimCode))
        {
            _popup.PopupEntity("Для цього контракту колбу вже видано.", args.User, args.User);
            return;
        }
        // Every new contract gets one flask; old abandoned flasks keep their
        // previous claim code and are deleted by the quota-item cleanup.
        var flask = Spawn("AspisAgroHerbicideFlask", Transform(uid).Coordinates);
        EnsureComp<AspisAgroQuotaItemComponent>(flask).ClaimCode = claimCode;
        _hands.TryPickupAnyHand(args.User, flask);
        _audio.PlayPvs(DispenseSound, uid);
        _organizations.MarkAspisAgroProgress(args.User, 2);
        _popup.PopupEntity("Колбу гербіциду видано (1/1). Доставте її до розпилювача на шатлі.",
            args.User, args.User);
    }

    public bool TryGetStationPosition(EntityUid surfaceMap, out Vector2 position)
    {
        position = Vector2.Zero;
        if (!_stationGrids.TryGetValue(surfaceMap, out var station) || Deleted(station))
            return false;
        position = Transform(station).WorldPosition;
        return true;
    }

    public string Bearing(EntityUid ship, EntityUid surfaceMap)
    {
        if (!_stationGrids.TryGetValue(surfaceMap, out var station) || Deleted(station))
            return "сигнал агростанції не знайдено";
        var delta = Transform(station).WorldPosition - Transform(ship).WorldPosition;
        var direction = MathF.Abs(delta.X) > MathF.Abs(delta.Y)
            ? delta.X >= 0 ? "схід" : "захід"
            : delta.Y >= 0 ? "північ" : "південь";
        return $"Агростанція: {direction}; відстань ≈{MathF.Round(delta.Length())} м.";
    }

    private void OnCanisterUsed(EntityUid uid, AspisAgroCargoComponent cargo, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || Deleted(uid) ||
            args.Target is not { } target ||
            !TryComp<AspisAgriculturalSprayerComponent>(target, out var sprayer))
            return;
        var ship = Transform(target).GridUid;
        if (ship is not { } shuttle || Transform(args.User).GridUid != shuttle ||
            !Transform(target).Anchored || !HasComp<ShuttleComponent>(shuttle))
            return;
        if (!_organizations.TryGetActiveAspisAgroQuotaCode(args.User, out var claimCode) ||
            !TryComp<AspisAgroQuotaItemComponent>(uid, out var flaskTag) ||
            !string.Equals(flaskTag.ClaimCode, claimCode, StringComparison.Ordinal) ||
            (TryComp<AspisAgroQuotaItemComponent>(target, out var machineTag) &&
             !string.Equals(machineTag.ClaimCode, claimCode, StringComparison.Ordinal)))
        {
            _popup.PopupEntity("Колба не належить до поточної планетарної квоти.",
                args.User, args.User);
            return;
        }
        if (sprayer.Spent)
        {
            _popup.PopupEntity("Цей розпилювач уже використаний у завершеній квоті.", args.User, args.User);
            return;
        }
        if (sprayer.HerbicideLoaded || sprayer.Tank > 0)
        {
            _popup.PopupEntity("Розпилювач уже заправлено; друга колба не потрібна.",
                args.User, args.User);
            return;
        }
        args.Handled = true;
        EnsureComp<AspisAgroQuotaItemComponent>(target).ClaimCode = claimCode;
        sprayer.Tank = sprayer.TankCapacity;
        sprayer.HerbicideLoaded = true;
        _appearance.SetData(target, AspisAgroSprayerVisuals.Loaded, true);
        _audio.PlayPvs(InsertSound, target);
        QueueDel(uid);
        _organizations.MarkAspisAgroProgress(args.User, 3);
        _popup.PopupEntity("Гербіцид заправлено. Злетіть в атмосферу та активуйте розпилення на модулі.",
            args.User, args.User);
    }

    private void OnReceiverVerbs(EntityUid uid, AspisAgroReceiverComponent receiver,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Стан агрономічного вантажу",
            Act = () => _popup.PopupEntity($"Завантажено {receiver.Delivered}/{TargetTotal} контейнерів.",
                args.User, args.User)
        });
    }
}
