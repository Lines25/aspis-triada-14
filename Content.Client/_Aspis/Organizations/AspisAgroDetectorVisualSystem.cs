using System;
using System.Collections.Generic;
using Content.Shared._Aspis.Organizations;
using Robust.Client.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// Audible, strictly local PDA signal. The original PDA sprite is never tinted.
/// The server provides a bounded period based on distance to the objective.
/// Only a detector contained by the local player's inventory may beep.
/// </summary>
public sealed class AspisAgroDetectorVisualSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private IGameTiming _timing = default!;
    private static readonly SoundPathSpecifier Beep = new("/Audio/_Aspis/Agro/detector_ping.ogg");
    private readonly Dictionary<EntityUid, TimeSpan> _nextBeep = new();
    private float _accumulator;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _accumulator += frameTime;
        if (_accumulator < 0.06f)
            return;
        _accumulator = 0f;
        var local = _players.LocalEntity;
        if (local is not { } player)
        {
            _nextBeep.Clear();
            return;
        }
        var active = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<AspisAgroDetectorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var detector, out _))
        {
            if (!detector.Active || !InLocalInventory(uid, player))
                continue;
            active.Add(uid);
            if (_nextBeep.TryGetValue(uid, out var next) && _timing.CurTime < next)
                continue;
            // Do not render/blink/tint the PDA. Audio is client-local only.
            _audio.PlayGlobal(Beep, Filter.Local(), false,
                AudioParams.Default.WithVolume(-6f));
            _nextBeep[uid] = _timing.CurTime + TimeSpan.FromSeconds(
                Math.Clamp(detector.Period, 0.17f, 1.4f));
        }
        foreach (var uid in new List<EntityUid>(_nextBeep.Keys))
            if (!active.Contains(uid)) _nextBeep.Remove(uid);
    }

    private bool InLocalInventory(EntityUid item, EntityUid player)
    {
        var current = item;
        for (var i = 0; i < 12 && current.Valid; i++)
        {
            if (current == player)
                return true;
            if (!TryComp<TransformComponent>(current, out var transform))
                return false;
            current = transform.ParentUid;
        }
        return false;
    }
}
