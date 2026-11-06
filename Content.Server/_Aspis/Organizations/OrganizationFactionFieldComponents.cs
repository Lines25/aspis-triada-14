using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>Server-only marker: a field kit is bound to one accepted faction operation.</summary>
[RegisterComponent]
public sealed partial class OrganizationFactionFieldKitComponent : Component
{
    public Guid Organization;
    public string OperationCode = string.Empty;
}

/// <summary>
/// Round-only survey marker attached to the uncommissioned transmitter. The
/// same physical entity becomes the mission relay after the verified field-kit
/// interaction. Assignment identity is checked on the server; this is not a
/// persistent sector-map UID.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationFactionSurveyBeaconComponent : Component
{
    public Guid Organization;
    public Guid Assignment;
    public string Code = string.Empty;
}

/// <summary>Server-only marker on the physically placed planetary relay.</summary>
[RegisterComponent]
public sealed partial class OrganizationFactionAntennaComponent : Component
{
    public Guid Organization;
    public string OperationCode = string.Empty;
    // Runtime identity; the durable registry stores the Guid, never EntityUid.
    public Guid SiteId;
}

/// <summary>Round-only record of a mission-specific physical recorder.</summary>
[RegisterComponent]
public sealed partial class AspisFactionWreckRecorderComponent : Component
{
    public Guid Organization;
    public Guid Assignment;
    public string Code = string.Empty;
}

/// <summary>
/// Second, distinct piece of expedition equipment. Its server-side identity
/// prevents any unrelated switch, console or recreated item from advancing a task.
/// </summary>
[RegisterComponent]
public sealed partial class AspisFactionWreckInspectionComponent : Component
{
    public Guid Organization;
    public Guid Assignment;
    public string Code = string.Empty;
}

/// <summary>Mission-locked scanner issued with the initial coordinate disk.</summary>
[RegisterComponent]
public sealed partial class AspisFactionLocatorComponent : Component
{
    public Guid Organization;
    public Guid Assignment;
}
