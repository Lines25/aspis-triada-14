using Robust.Shared.GameObjects;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// A door may authorize any one of several purchased corporate access slots.
/// The original RequiredSlot remains as a compatible fallback for older maps.
/// </summary>
[RegisterComponent]
public sealed partial class OrganizationDoorAccessMaskComponent : Component
{
    [DataField]
    public int AllowedMask;
}
