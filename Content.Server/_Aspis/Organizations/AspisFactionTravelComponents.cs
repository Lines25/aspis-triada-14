using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>Only faction-assignment maps use this restrictive shuttle launch rule.</summary>
[RegisterComponent]
public sealed partial class AspisFactionExpeditionDestinationComponent : Component
{
    public Guid Organization;
    public Guid Assignment;
    public TimeSpan LaunchReadyAt;
    public EntityUid? AssignedShuttle;
    public bool RouteClosed;
}

/// <summary>Server-issued disk identity. A copied disk name cannot authorize travel.</summary>
[RegisterComponent]
public sealed partial class AspisFactionCoordinatesDiskComponent : Component
{
    public Guid Organization;
    public Guid Assignment;
}
