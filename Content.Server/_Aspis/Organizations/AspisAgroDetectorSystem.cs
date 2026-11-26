using System;
using System.Collections.Generic;
using Content.Shared._Aspis.Organizations;
using Content.Shared.PDA;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Marker-based right-click PDA interaction. Never subscribes directly to
/// PdaComponent, which might already have upstream GetVerbsEvent handlers.
/// </summary>
public sealed class AspisAgroDetectorSystem : EntitySystem
{
    [Dependency] private AspisAgroStationSystem _agro = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    private float _markerTimer;
    private float _signalTimer;
    private readonly List<EntityUid> _unmarked = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AspisAgroDetectorComponent,
            GetVerbsEvent<AlternativeVerb>>(OnPdaVerbs);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _markerTimer += frameTime;
        _signalTimer += frameTime;
        if (_markerTimer >= 3f)
        {
            _markerTimer = 0f;
            _unmarked.Clear();
            var pdas = EntityQueryEnumerator<PdaComponent>();
            while (pdas.MoveNext(out var uid, out _))
                if (!HasComp<AspisAgroDetectorComponent>(uid))
                    _unmarked.Add(uid);
            foreach (var uid in _unmarked)
                if (!Deleted(uid)) EnsureComp<AspisAgroDetectorComponent>(uid);
        }
        if (_signalTimer < 0.3f)
            return;
        _signalTimer = 0f;
        var sensors = EntityQueryEnumerator<AspisAgroDetectorComponent, TransformComponent>();
        while (sensors.MoveNext(out var uid, out var detector, out var xform))
        {
            if (!detector.Active)
                continue;
            if (xform.MapUid is not { } map ||
                !_agro.TryGetStationPosition(map, out var station))
            {
                detector.Active = false;
                Dirty(uid, detector);
                continue;
            }
            var distance = System.Numerics.Vector2.Distance(xform.WorldPosition, station);
            var period = Math.Clamp(0.17f + distance / 75f, 0.17f, 1.4f);
            if (MathF.Abs(detector.Period - period) < 0.07f)
                continue;
            detector.Period = period;
            Dirty(uid, detector);
        }
    }

    private void OnPdaVerbs(EntityUid uid, AspisAgroDetectorComponent detector,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        if (Transform(args.User).MapUid is not { } map ||
            !_agro.TryGetStationPosition(map, out _))
            return;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = detector.Active ? "Вимкнути пошук агростанції" : "Увімкнути пошук агростанції",
            Act = () => Toggle(uid, args.User)
        });
    }

    private void Toggle(EntityUid uid, EntityUid user)
    {
        if (!TryComp<AspisAgroDetectorComponent>(uid, out var detector) ||
            Transform(user).MapUid is not { } map ||
            !_agro.TryGetStationPosition(map, out var station))
            return;
        detector.Active = !detector.Active;
        Dirty(uid, detector);
        _popup.PopupEntity(detector.Active
            ? "Звуковий детектор увімкнено. Частота сигналу зростає поблизу цілі."
            : "Детектор вимкнено.", user, user);
    }
}
