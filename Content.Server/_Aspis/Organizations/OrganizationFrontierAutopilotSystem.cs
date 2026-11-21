using System;
using System.Numerics;
using Content.Server.Physics.Controllers;
using Content.Server.Shuttles.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// A real source of Mono's GetShuttleInputsEvent, just like a human pilot.
/// Mono's MoverController then applies the ship's actual thruster force, gyro torque,
/// speed falloff, brake force and engine visual states on EVERY physics tick.
/// No direct velocity assignment is made by this autopilot.
/// </summary>
public sealed class OrganizationFrontierAutopilotSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<OrganizationFrontierAutopilotComponent, GetShuttleInputsEvent>(OnGetInputs);
    }

    public void Engage(EntityUid shuttle, bool inbound, float endX, float cruiseSpeed, float tolerance)
    {
        var pilot = EnsureComp<OrganizationFrontierAutopilotComponent>(shuttle);
        pilot.Inbound = inbound;
        pilot.EndX = endX;
        pilot.CruiseSpeed = cruiseSpeed;
        pilot.Tolerance = tolerance;

        // MoverController's InputSources is a HashSet. A repeated registration
        // is harmless, but losing the source would stall an actual crewed ship.
        EnsureComp<PilotedShuttleComponent>(shuttle).InputSources.Add(shuttle);
    }

    public void Disengage(EntityUid shuttle)
    {
        if (TerminatingOrDeleted(shuttle))
            return;
        if (TryComp<PilotedShuttleComponent>(shuttle, out var pilots))
            pilots.InputSources.Remove(shuttle);
        RemComp<OrganizationFrontierAutopilotComponent>(shuttle);
    }

    private void OnGetInputs(Entity<OrganizationFrontierAutopilotComponent> ent,
        ref GetShuttleInputsEvent args)
    {
        if (args.ShuttleUid != ent.Owner ||
            !TryComp<ShuttleComponent>(ent, out var shuttle) ||
            !TryComp<PhysicsComponent>(ent, out var body) ||
            !shuttle.Enabled)
            return;

        args.GotInput = true;
        args.AngularMul = 1f;
        args.AccelMul = 1f;

        var direction = ent.Comp.Inbound ? -1f : 1f;
        var position = Transform(ent.Owner).WorldPosition;
        var remaining = MathF.Max(0f, ent.Comp.Inbound ? position.X : ent.Comp.EndX - position.X);
        var signedRemaining = ent.Comp.Inbound ? position.X : ent.Comp.EndX - position.X;
        var xform = Transform(ent.Owner);
        var worldForward = xform.WorldRotation.RotateVec(Vector2.UnitY);

        // Aim the shuttle's real LOCAL NORTH (its bow) at the route, with a small
        // gradual lane correction. This works regardless of the ship's initial yaw.
        var lateral = Math.Clamp(-position.Y / MathF.Max(350f, remaining * 0.3f), -0.18f, 0.18f);
        var aim = Vector2.Normalize(new Vector2(direction, lateral));
        var cross = worldForward.X * aim.Y - worldForward.Y * aim.X;
        var dot = Math.Clamp(Vector2.Dot(worldForward, aim), -1f, 1f);
        var angleError = MathF.Atan2(cross, dot);

        // The normal Mono controller applies -Rotation as gyro torque.
        // PD control anticipates angular braking instead of snapping rotation.
        var angularAccel = MathF.Max(0.02f, shuttle.AngularThrust * body.InvI);
        var targetAngularSpeed = MathF.Sign(angleError) * MathF.Min(0.8f,
            MathF.Sqrt(2f * angularAccel * MathF.Abs(angleError)) * 0.8f);
        var rotationInput = Math.Clamp((body.AngularVelocity - targetAngularSpeed) * 2.8f,
            -1f, 1f);

        // No free acceleration. Mono will only apply force if a powered, anchored
        // and otherwise functional thruster contributes to LinearThrust[].
        var forwardThrust = shuttle.LinearThrust[2]; // North, per Mono MoverController
        var reverseThrust = shuttle.LinearThrust[0]; // South, per Mono MoverController
        var forwardSpeed = Vector2.Dot(body.LinearVelocity, new Vector2(direction, 0f));
        var speed = Math.Clamp(shuttle.BaseMaxLinearVelocity, 0f, ent.Comp.CruiseSpeed);
        if (!float.IsFinite(speed) || speed <= 0f ||
            !float.IsFinite(body.Mass) || body.Mass <= 0f)
        {
            args.Input = new ShuttleInput(Vector2.Zero, rotationInput, 0f);
            return;
        }

        var deceleration = MathF.Max(0.01f,
            reverseThrust * ShuttleComponent.BrakeCoefficient / body.Mass *
            MathF.Max(0.01f, shuttle.AccelerationMultiplier));
        // Conservative braking margin accounts for physics tick latency and the
        // non-linear thrust falloff used by ordinary Mono shuttles.
        var brakingDistance = MathF.Max(0f, signedRemaining - ent.Comp.Tolerance - 35f);
        var safeSpeed = MathF.Sqrt(2f * deceleration * brakingDistance * 0.72f);
        var desiredSpeed = MathF.Min(speed, safeSpeed);
        var aligned = MathF.Abs(angleError) < 0.11f &&
            MathF.Abs(body.AngularVelocity) < 0.18f;

        var strafe = Vector2.Zero;
        var brakes = 0f;
        if (forwardSpeed > desiredSpeed + 1.3f)
            brakes = 1f;
        else if (signedRemaining < -ent.Comp.Tolerance && forwardSpeed < 2f)
            // Overshot the target: reverse using the ship's genuine aft engines.
            strafe = new Vector2(0f, -0.65f);
        else if (aligned && forwardThrust > 0f &&
                 signedRemaining > ent.Comp.Tolerance &&
                 forwardSpeed < desiredSpeed - 0.8f)
            strafe = Vector2.UnitY;

        // Keep the radar visible; only the inputs for physical motion are owned
        // by this server-side virtual pilot while PreventPilot blocks humans.
        args.Input = new ShuttleInput(strafe, rotationInput, brakes);
        args.SetMaxVelocity = speed;
    }
}

/// <summary>
/// Server-only active pilot source. Never networked or handed to a client.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationFrontierAutopilotComponent : Component
{
    public bool Inbound;
    public float EndX;
    public float CruiseSpeed;
    public float Tolerance;
}
