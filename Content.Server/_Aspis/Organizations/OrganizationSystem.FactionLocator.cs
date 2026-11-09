using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Inventory;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Item.ItemToggle.Components;
using Robust.Shared.Audio;
using Robust.Shared.Enums;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Local, mission-bound proximity feedback. No coordinates are sent to clients;
/// the server only varies the pulse interval for the assigned expedition.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const string FactionLocatorPrototype = "AspisFactionObjectiveLocator";
    private readonly Dictionary<EntityUid, TimeSpan> _factionLocatorNextPing = new();
    private TimeSpan _factionLocatorNextSweep;
    private static readonly SoundSpecifier FactionLocatorPulse =
        new SoundPathSpecifier("/Audio/Machines/sonar-ping.ogg");

    private void UpdateFactionLocators()
    {
        var now = _gameTiming.CurTime;
        if (now < _factionLocatorNextSweep)
            return;
        _factionLocatorNextSweep = now + TimeSpan.FromSeconds(0.25);
        var inventory = EntityManager.System<InventorySystem>();
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame ||
                session.AttachedEntity is not { Valid: true } actor ||
                !TryGetCharacter(actor, out var character, out _))
                continue;
            var org = FindMembership(character);
            if (org == null || org.Kind != OrganizationKind.Faction)
                continue;

            foreach (var item in inventory.GetHandOrInventoryEntities(actor))
            {
                if (!TryComp<AspisFactionLocatorComponent>(item, out var locator) ||
                    locator.Organization != org.Id ||
                    !TryComp<ItemToggleComponent>(item, out var toggle) || !toggle.Activated)
                    continue;
                var claim = _sectorQuotaClaims.Values.FirstOrDefault(entry =>
                    entry.Organization == org.Id && !entry.Completed &&
                    !entry.FactionWreckReturned &&
                    entry.FactionAssignmentId == locator.Assignment &&
                    entry.FactionWreckGrid != null);
                if (claim == null || claim.FactionWreckGrid is not { } grid ||
                    Deleted(grid) || Transform(actor).GridUid != grid)
                    continue;
                EntityUid? destination = claim.FactionWreckInspectionComplete
                    ? claim.FactionWreckRecorder : claim.FactionWreckInspectionTerminal;
                if (destination is not { } target || Deleted(target) ||
                    Transform(target).GridUid != grid)
                    continue;

                var distance = Vector2.Distance(Transform(actor).WorldPosition,
                    Transform(target).WorldPosition);
                var interval = distance switch
                {
                    < 5f => 0.5,
                    < 12f => 1.0,
                    < 25f => 2.0,
                    < 60f => 3.5,
                    _ => 5.5
                };
                if (_factionLocatorNextPing.TryGetValue(item, out var next) && now < next)
                    continue;
                _factionLocatorNextPing[item] = now + TimeSpan.FromSeconds(interval);
                _audio.PlayPvs(FactionLocatorPulse, item);
            }
        }
    }
}
