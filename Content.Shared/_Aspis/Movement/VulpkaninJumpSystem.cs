using System.Linq;
using System.Numerics;
using Content.Shared.Actions;
using Content.Shared.Climbing.Systems;
using Content.Shared.Damage.Systems;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Placeable;
using Content.Shared.Physics;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Aspis.Movement;

public sealed class VulpkaninJumpSystem : EntitySystem
{
    private static readonly Vector2[] WallDirections = { Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY };
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly ClimbSystem _climbing = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly ThrownItemSystem _thrownItems = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly TurfSystem _turfs = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<VulpkaninJumpComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<VulpkaninJumpComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<VulpkaninJumpComponent, VulpkaninJumpActionEvent>(OnJump);
        SubscribeLocalEvent<VulpkaninJumpComponent, ThrowDoHitEvent>(OnJumpHit);
        SubscribeLocalEvent<VulpkaninJumpComponent, StopThrowEvent>(OnStopThrow);
        SubscribeLocalEvent<VulpkaninJumpComponent, PreventCollideEvent>(OnPreventCollide);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<VulpkaninJumpComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var endState = comp.PendingEnd;
            if (endState == VulpkaninJumpEndState.None)
                continue;

            comp.PendingEnd = VulpkaninJumpEndState.None;
            Dirty(uid, comp);

            if (endState == VulpkaninJumpEndState.Wall)
                _stun.TryKnockdown(uid, TimeSpan.FromSeconds(5), true);

            _standing.Down(uid, dropHeldItems: false);
        }
    }

    private void OnStartup(Entity<VulpkaninJumpComponent> ent, ref ComponentStartup args) =>
        _actions.AddAction(ent, ref ent.Comp.JumpActionEntity, ent.Comp.Action);

    private void OnShutdown(Entity<VulpkaninJumpComponent> ent, ref ComponentShutdown args)
    {
        var wasJumping = ent.Comp.Jumping;
        EndJump(ent);
        if (wasJumping && TryComp<ThrownItemComponent>(ent, out var thrown))
            _thrownItems.StopThrow(ent, thrown);

        _actions.RemoveAction(ent.Owner, ent.Comp.JumpActionEntity);
    }

    private void OnJump(Entity<VulpkaninJumpComponent> ent, ref VulpkaninJumpActionEvent args)
    {
        if (args.Handled || ent.Comp.Jumping || ent.Comp.PendingEnd != VulpkaninJumpEndState.None
            || HasComp<ThrownItemComponent>(ent))
            return;

        if (!TryComp<PhysicsComponent>(ent, out var physics)
            || physics == null
            || (physics.BodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0)
            return;

        var weightless = _gravity.IsWeightless(ent);
        if (weightless && !IsNextToWall(Transform(ent).Coordinates))
            return;

        var target = ClampTarget(ent, args.Target, ent.Comp.JumpRange);
        var origin = _transform.GetMapCoordinates(ent);
        var destination = _transform.ToMapCoordinates(target);
        var direction = destination.Position - origin.Position;
        if (destination.MapId != origin.MapId
            || ent.Comp.JumpSpeed <= 0
            || direction.LengthSquared() < 0.001f)
            return;

        if (!_stamina.TryTakeStamina(ent, ent.Comp.StaminaCost))
            return;

        _climbing.StopClimbing(ent);

        ent.Comp.Jumping = true;
        Dirty(ent);
        _physics.RegenerateContacts((ent.Owner, physics));
        _throwing.TryThrow(ent, target, ent.Comp.JumpSpeed, ent.Owner,
            pushbackRatio: 0, recoil: false, playSound: false, doSpin: false);
        if (!HasComp<ThrownItemComponent>(ent))
        {
            EndJump(ent);
            return;
        }

        _audio.PlayPredicted(ent.Comp.JumpSound, ent, ent);
        args.Handled = true;
    }

    private EntityCoordinates ClampTarget(EntityUid uid, EntityCoordinates target, float maxRange)
    {
        var origin = _transform.GetMapCoordinates(uid);
        var destination = _transform.ToMapCoordinates(target);
        if (origin.MapId != destination.MapId)
            return target;

        var offset = destination.Position - origin.Position;
        if (offset.LengthSquared() <= maxRange * maxRange)
            return target;

        var clamped = origin.Position + Vector2.Normalize(offset) * maxRange;
        return _transform.ToCoordinates(new MapCoordinates(clamped, origin.MapId));
    }

    private bool IsNextToWall(EntityCoordinates coordinates)
    {
        foreach (var direction in WallDirections)
        {
            if (_turfs.TryGetTileRef(coordinates.Offset(direction), out var tile)
                && _turfs.IsTileBlocked(tile.Value, CollisionGroup.Impassable))
                return true;
        }

        return false;
    }

    private void OnPreventCollide(Entity<VulpkaninJumpComponent> ent, ref PreventCollideEvent args)
    {
        if (!ent.Comp.Jumping)
            return;

        if (HasComp<PlaceableSurfaceComponent>(args.OtherEntity)
            && (args.OtherFixture.CollisionLayer & (int) CollisionGroup.TableLayer) != 0)
            args.Cancelled = true;
    }

    private void OnJumpHit(Entity<VulpkaninJumpComponent> ent, ref ThrowDoHitEvent args)
    {
        if (!ent.Comp.Jumping || !IsWall(args.Target))
            return;

        ent.Comp.PendingEnd = VulpkaninJumpEndState.Wall;
        Dirty(ent);
        args.Component.LandTime = _timing.CurTime;
        Dirty(ent.Owner, args.Component);
    }

    private bool IsWall(EntityUid uid)
    {
        if (!TryComp<FixturesComponent>(uid, out var fixtures))
            return false;

        const int wallCollisionLayers = (int) (CollisionGroup.Opaque | CollisionGroup.Impassable);
        return fixtures.Fixtures.Values.Any(fixture =>
            (fixture.CollisionLayer & wallCollisionLayers) == wallCollisionLayers);
    }

    private void OnStopThrow(Entity<VulpkaninJumpComponent> ent, ref StopThrowEvent args)
    {
        if (!ent.Comp.Jumping)
            return;

        var endState = ent.Comp.PendingEnd;
        if (endState == VulpkaninJumpEndState.None && IsOnTable(ent))
            endState = VulpkaninJumpEndState.Table;

        ent.Comp.Jumping = false;
        ent.Comp.PendingEnd = endState;
        Dirty(ent);
    }

    private bool IsOnTable(EntityUid uid)
    {
        if (!_turfs.TryGetTileRef(Transform(uid).Coordinates, out var tile))
            return false;

        foreach (var entity in _lookup.GetEntitiesInRange(uid, 0.8f, LookupFlags.Dynamic | LookupFlags.Static | LookupFlags.Sundries))
        {
            if (!HasComp<PlaceableSurfaceComponent>(entity)
                || !TryComp<FixturesComponent>(entity, out var fixtures))
                continue;

            if (!_turfs.TryGetTileRef(Transform(entity).Coordinates, out var tableTile)
                || tile.Value.GridUid != tableTile.Value.GridUid
                || tile.Value.GridIndices != tableTile.Value.GridIndices)
                continue;

            if (fixtures.Fixtures.Values.Any(fixture =>
                    (fixture.CollisionLayer & (int) CollisionGroup.TableLayer) != 0))
                return true;
        }

        return false;
    }

    private void EndJump(Entity<VulpkaninJumpComponent> ent)
    {
        if (!ent.Comp.Jumping && ent.Comp.PendingEnd == VulpkaninJumpEndState.None)
            return;

        ent.Comp.Jumping = false;
        ent.Comp.PendingEnd = VulpkaninJumpEndState.None;
        Dirty(ent);
    }
}
