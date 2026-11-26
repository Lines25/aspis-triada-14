using System;
using System.Linq;
using System.Numerics;
using Content.Server.GameTicking.Events;
using Content.Shared.GameTicking;
using Content.Server.Shuttles.Components;
using Content.Shared.Mobs.Components;
using Robust.Shared.IoC;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private static readonly TimeSpan ConstructionEmptyYardTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ConstructionDepartureDuration = TimeSpan.FromSeconds(20);
    [Dependency] private SharedPhysicsSystem _constructionDeparturePhysics = default!;

    /// <summary>
    /// Tick on the server independently of PDA/BUI refreshes and economic sweeps.
    /// A ship is NEVER deleted with mobs aboard; an unattended work platform
    /// is released after 90s only when no active job or visiting ship needs it.
    /// </summary>
    internal void TickConstructionYardLifecycle()
    {
        var now = _gameTiming.CurTime;
        foreach (var yard in _constructionYards.Values.ToArray())
        {
            if (TerminatingOrDeleted(yard.WorkPlatform))
                continue;
            if (!yard.Closed && yard.QuotaCode.StartsWith("TEST-YARD-", StringComparison.Ordinal))
            {
                // Admin-only probes are not quota reservations. They can idle
                // out after 90 seconds if no character ever attends them.
                if (ConstructionGridHasCrew(yard.WorkPlatform) ||
                    ConstructionGridHasCrew(yard.ContractHull))
                    yard.CreatedAt = now;
                else if (now - yard.CreatedAt >= ConstructionEmptyYardTimeout)
                    yard.Closed = true;
            }
            if (!yard.Closed && !yard.QuotaCode.StartsWith("TEST-YARD-", StringComparison.Ordinal) &&
                !_sectorQuotaClaims.Values.Any(claim =>
                    claim.FactionAssignmentId == yard.AssignmentId &&
                    claim.Organization == yard.OrganizationId && !claim.Completed))
            {
                // Cancelled/expired real job: never leave its abandoned hull
                // docking forever, even though the claim has been removed.
                yard.Closed = true;
            }
            if (yard.Closed)
                TryDepartConstructionContractHull(yard, now);
        }

        var platforms = _constructionYards.Values.Select(r => r.WorkPlatform).Distinct().ToArray();
        foreach (var platform in platforms)
        {
            if (TerminatingOrDeleted(platform))
            {
                _constructionYardIdleSince.Remove(platform);
                continue;
            }
            var records = _constructionYards.Values.Where(r => r.WorkPlatform == platform).ToArray();
            var active = records.Any(r => !r.Closed);
            var hullPending = records.Any(r => !TerminatingOrDeleted(r.ContractHull));
            var visitingGridDocked = _constructionDocking.GetDocks(platform).Any(dock => dock.Comp.Docked);
            if (active || hullPending || visitingGridDocked || ConstructionGridHasCrew(platform))
            {
                _constructionYardIdleSince.Remove(platform);
                continue;
            }

            if (!_constructionYardIdleSince.TryGetValue(platform, out var since))
            {
                _constructionYardIdleSince[platform] = now;
                continue;
            }
            if (now - since < ConstructionEmptyYardTimeout)
                continue;

            // Remove only our work platform. Other sector stations, nearby
            // players' ships and other companies' yards are untouched.
            var representative = records[0];
            _constructionYardReservations.RemoveAll(r => r.MapId == representative.MapId &&
                Vector2.DistanceSquared(r.WorldPosition, representative.WorldPosition) < 4f);
            foreach (var record in records)
                _constructionYards.Remove(record.AssignmentId);
            _constructionYardIdleSince.Remove(platform);
            _constructionYardsCreatedThisRound = Math.Max(0, _constructionYardsCreatedThisRound - 1);
            QueueDel(platform);
            _log.GetSawmill("organizations.construction-yards")
                .Info($"Removed vacant company yard {platform} after 90s, no crew or docks.");
        }
    }

    private bool ConstructionGridHasCrew(EntityUid gridUid)
    {
        if (TerminatingOrDeleted(gridUid))
            return false;
        // Includes disconnected and unconscious characters, not just players
        // with an active network session. Ghosts aren't part of a physical grid.
        var people = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (people.MoveNext(out _, out _, out var transform))
        {
            if (transform.GridUid == gridUid)
                return true;
        }
        return false;
    }

    private void TryDepartConstructionContractHull(ConstructionYardRecord yard, TimeSpan now)
    {
        var ship = yard.ContractHull;
        if (TerminatingOrDeleted(ship))
            return;
        if (ConstructionGridHasCrew(ship))
        {
            // A passenger boarded a departing ship: immediately halt. Never
            // run a delayed QueueDel timer when somebody is on board.
            if (yard.Departing && TryComp<PhysicsComponent>(ship, out var physics))
                _constructionDeparturePhysics.SetLinearVelocity(ship, Vector2.Zero, body: physics);
            yard.Departing = false;
            yard.DepartureStarted = null;
            return;
        }

        if (!yard.Departing)
        {
            // Release the berth first so the post can accept its next ship.
            _constructionDocking.UndockDocks(ship);
            if (TryComp<ShuttleComponent>(ship, out var shuttle))
            {
                _organizationShuttle.Enable(ship, force: true);
                shuttle.Enabled = true;
            }
            yard.Departing = true;
            yard.DepartureStarted = now;
            yard.DepartureOrigin = Transform(ship).WorldPosition;
            _organizationMetadata.SetEntityName(ship, "Готовий корабель: " + yard.DesignName);
        }

        var outward = Transform(ship).WorldPosition - Transform(yard.WorkPlatform).WorldPosition;
        var direction = outward.LengthSquared() > 0.01f ? Vector2.Normalize(outward) : Vector2.UnitY;
        if (TryComp<PhysicsComponent>(ship, out var body))
            _constructionDeparturePhysics.SetLinearVelocity(ship, direction * 11f, body: body);
        else
            _organizationOutpostTransforms.SetWorldPosition(ship,
                Transform(ship).WorldPosition + direction * 11f);

        if (yard.DepartureStarted is not { } started ||
            now - started < ConstructionDepartureDuration)
            return;
        // Recheck occupancy at the very last instant; no deletion of players.
        if (!ConstructionGridHasCrew(ship))
            QueueDel(ship);
    }

    internal void ResetConstructionYardLifecycleRound() => ClearConstructionYardRoundState();
}

/// <summary>
/// Dedicated lifecycle system so an unoccupied yard is cleaned up even if
/// nobody opens the organization's console. It has no client-triggered actions.
/// </summary>
public sealed class OrganizationConstructionYardLifecycleSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    private TimeSpan _nextTick;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextTick)
            return;
        _nextTick = _timing.CurTime + TimeSpan.FromSeconds(1);
        EntityManager.System<OrganizationSystem>().TickConstructionYardLifecycle();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        EntityManager.System<OrganizationSystem>().ResetConstructionYardLifecycleRound();
        _nextTick = TimeSpan.Zero;
    }
}
