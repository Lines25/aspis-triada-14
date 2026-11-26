using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Mono.Planets;
using Content.Server.Shuttles.Components;
using Content.Shared.Mind.Components;
using Content.Shared.Popups;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Verbs;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// One-dose agricultural expedition on the same atmospheric and surface maps
/// as the planetary NAV system. A verified physical sprayer drives a bounded
/// green aerosol effect; corporate rewards are settled by OrganizationSystem.
/// </summary>
public sealed class AspisAgriculturalFlightSystem : EntitySystem
{
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private OrganizationSystem _organizations = default!;
    [Dependency] private AspisAgroAftermathSystem _aftermath = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    private static readonly SoundSpecifier SpraySound =
        new SoundPathSpecifier("/Audio/_Aspis/Agro/sprayer_hiss.ogg");

    // Only visually loaded/nearby world regions get smoke entities. Never
    // instantiate one entity per wheat tile or blanket an infinite map.
    private const float SprayVisualSeconds = 9f;
    private const float MistInterval = 0.65f;
    private int _mistPulseIndex;

    private sealed class Flight
    {
        public EntityUid Ship;
        public EntityUid Sprayer;
        public EntityUid SurfaceMap;
        public EntityUid AirMap;
        public float SprayVisualRemaining;
        public float SprayMistTimer;
        public float SpraySoundTimer;
    }

    private readonly Dictionary<EntityUid, Flight> _flights = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AspisAgriculturalSprayerComponent,
            GetVerbsEvent<AlternativeVerb>>(OnSprayerVerbs);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var (ship, flight) in _flights.ToArray())
        {
            if (Deleted(ship) || Deleted(flight.AirMap) ||
                Transform(ship).MapUid != flight.AirMap)
            {
                // FTL can leave the atmospheric map without NAV landing.
                // Never retain a stale round-local flight registration.
                if (!Deleted(flight.Sprayer) &&
                    TryComp<AspisAgriculturalSprayerComponent>(flight.Sprayer, out var old))
                    old.Spraying = false;
                _flights.Remove(ship);
                continue;
            }
            if (!TryComp<AspisAgriculturalSprayerComponent>(flight.Sprayer, out var sprayer))
            {
                _flights.Remove(ship);
                continue;
            }
            if (!sprayer.Spraying || !sprayer.Spent)
                continue;

            // A validated, one-dose release remains visibly active for nine
            // seconds even though the chemical tank has already been spent.
            // This is visual smoke, not gas chemistry and cannot hurt players.
            var elapsed = MathF.Min(frameTime, 0.5f);
            flight.SprayVisualRemaining -= elapsed;
            flight.SprayMistTimer += elapsed;
            flight.SpraySoundTimer += elapsed;
            if (flight.SprayMistTimer >= MistInterval)
            {
                flight.SprayMistTimer -= MistInterval;
                SpawnAirMist(flight);
            }
            if (flight.SpraySoundTimer >= 3f)
            {
                flight.SpraySoundTimer -= 3f;
                _audio.PlayPvs(SpraySound, flight.Sprayer);
            }
            if (flight.SprayVisualRemaining <= 0f)
            {
                sprayer.Spraying = false;
                flight.SprayVisualRemaining = 0f;
            }
        }

        }

    private void SpawnAirMist(Flight flight)
    {
        if (Deleted(flight.Ship) || Deleted(flight.AirMap)) return;
        var rotation = Transform(flight.Ship).WorldRotation;
        var forward = rotation.RotateVec(new Vector2(0f, 1f));
        var lateral = new Vector2(-forward.Y, forward.X);
        var location = Transform(flight.Ship).WorldPosition - forward * 3f;
        var variation = (_mistPulseIndex++ % 5 - 2) * 2.7f;
        SpawnMist(flight.AirMap, location + lateral * variation);
        SpawnMist(flight.AirMap, location - forward * 6f - lateral * variation);
    }

    private void SpawnMist(EntityUid map, Vector2 location)
    {
        if (Deleted(map)) return;
        Spawn("AspisAgroGreenMist", new EntityCoordinates(map, location));
    }

    private void OnSprayerVerbs(EntityUid uid,
        AspisAgriculturalSprayerComponent sprayer,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || Deleted(uid))
            return;
        var ship = Transform(uid).GridUid;
        if (ship is not { } grid ||
            !TryComp<ShuttleComponent>(grid, out _) ||
            !HasComp<MapGridComponent>(grid) || !Transform(uid).Anchored ||
            Transform(args.User).GridUid != grid)
            return;

        if (!_flights.TryGetValue(grid, out var flight))
        {
            if (Transform(grid).MapUid is not { } map ||
                !HasComp<PlanetMapComponent>(map))
                return;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = "Порядок польових робіт",
                Act = () => Tell(args.User,
                    "Установіть розпилювач, вставте диск у FTL і скористайтеся кнопкою ЗЛІТ у NAV. "+
                    "Завантажте одну колбу, злетіть і виконайте одне розпилення; потім поверніться через NAV.")
            });
            return;
        }

        if (flight.Sprayer != uid)
            return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = sprayer.Spent ? "Розпилювач відпрацював" :
                sprayer.HerbicideLoaded ? "РОЗПИЛИТИ ГЕРБІЦИД (1 ДОЗА)" : "Резервуар порожній",
            Act = () => ToggleSpraying(grid, uid, args.User)
        });
        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Стан обробки",
            Act = () => ShowStatus(grid, args.User)
        });
        args.Verbs.Add(new AlternativeVerb
        {
            Text = "Повернення на посадку",
            Act = () => Tell(args.User,"Завершіть завдання кнопкою ПОСАДКА в планетарній NAV-консолі.")
        });
    }

    /// <summary>
    /// The planetary console owns map transfers. This hook only tracks the
    /// physical sprayer on that same map and verifies the actual field flight.
    /// No duplicate atmospheric map is created for a corporate quota.
    /// </summary>
    public void NotifyPlanetaryTransition(EntityUid ship, EntityUid surface, EntityUid air,
        bool toAir, EntityUid pilot)
    {
        if (toAir)
        {
            if (_flights.ContainsKey(ship)) return;
            var units=EntityQueryEnumerator<AspisAgriculturalSprayerComponent, TransformComponent>();
            while (units.MoveNext(out var uid,out _,out var xform))
            {
                if (xform.GridUid!=ship || !xform.Anchored ||
                    HasComp<AspisAgroReceiverComponent>(uid))continue;
                _flights[ship]=new Flight
                {
                    Ship=ship, Sprayer=uid, SurfaceMap=surface, AirMap=air
                };
                Logger.Info($"Aspis Agro: same-map flight armed for ship {ship}.");
                return;
            }
            return;
        }
        if (!_flights.Remove(ship,out var flight) || flight.AirMap!=air ||
            flight.SurfaceMap!=surface)return;
        if (!TryComp<AspisAgriculturalSprayerComponent>(flight.Sprayer,out var sprayer))return;
        sprayer.Spraying=false;
        Tell(pilot, sprayer.Spent
            ? "Атмосферний рейс завершено. Дозу використано, зелений туман ще видно на планеті."
            : "Атмосферний рейс завершено. Для виконання квоти злетіть і активуйте заправлений розпилювач.");
    }

    private void ToggleSpraying(EntityUid ship, EntityUid sprayerUid, EntityUid user)
    {
        if (!_flights.TryGetValue(ship, out var flight) || flight.Sprayer != sprayerUid ||
            !TryComp<AspisAgriculturalSprayerComponent>(sprayerUid, out var sprayer) ||
            Transform(user).GridUid != ship || !Transform(sprayerUid).Anchored ||
            Transform(ship).MapUid != flight.AirMap)
            return;
        if (sprayer.Spent || !sprayer.HerbicideLoaded || sprayer.Tank <= 0)
        {
            Tell(user, "Спочатку заберіть одну колбу з агростанції та заправте модуль.");
            return;
        }
        // Required: accepted planetary quota, real installed sprayer, a
        // physical flask previously loaded by the server and the shared air map.
        if (!_organizations.TryGetActiveAspisAgroQuotaCode(user, out var claimCode) ||
            !TryComp<AspisAgroQuotaItemComponent>(sprayerUid, out var tag) ||
            !string.Equals(tag.ClaimCode, claimCode, StringComparison.Ordinal))
        {
            Tell(user, "Цей розпилювач не прив'язаний до вашої активної планетарної квоти.");
            return;
        }
        if (!_organizations.TryCompleteAspisAgriculturalFieldQuota(user, true))
        {
            Tell(user, "Не вдалося завершити активну планетарну квоту. Доза збережена.");
            return;
        }
        // On completed work, the spent machine remains in place but has no
        // resale value and cannot receive a second flask. An abandoned claim,
        // in contrast, still deletes its tagged machine during cleanup.
        RemComp<AspisAgroQuotaItemComponent>(sprayerUid);
        sprayer.Tank = 0;
        sprayer.HerbicideLoaded = false;
        sprayer.Spent = true;
        sprayer.Spraying = true;
        flight.SprayVisualRemaining = SprayVisualSeconds;
        flight.SprayMistTimer = MistInterval;
        flight.SpraySoundTimer = 0f;
        _appearance.SetData(sprayerUid, AspisAgroSprayerVisuals.Loaded, false);
        _audio.PlayPvs(SpraySound, sprayerUid);
        _aftermath.Activate(flight.SurfaceMap, flight.AirMap, claimCode);
        Tell(user, "Розпилення розпочато! Зелений туман поширюється в атмосфері та над поверхнею. " +
            "Планетарну квоту зараховано; модуль одноразовий.");
    }

    private void ShowStatus(EntityUid ship, EntityUid user)
    {
        if (!_flights.TryGetValue(ship, out var flight) || Transform(user).GridUid != ship)
            return;
        var tank = TryComp<AspisAgriculturalSprayerComponent>(flight.Sprayer, out var machine)
            ? $"{machine.Tank}/{machine.TankCapacity}" : "модуль пошкоджено";
        var status = machine is { Spent: true }
            ? $"розпилено, залишок ефекту {MathF.Ceiling(flight.SprayVisualRemaining)} с"
            : "очікує одну колбу і натискання на модулі";
        Tell(user, $"Аграрна квота: {status}. Доза: {tank}.");
    }

    private void Tell(EntityUid player, string message) =>
        _popup.PopupEntity(message, player, player);
}
