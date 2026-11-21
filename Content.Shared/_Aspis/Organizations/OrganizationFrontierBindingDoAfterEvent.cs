using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// The actual two-second interaction between a route navigator and a shuttle console.
/// A server-side eligibility check is repeated after the visible progress finishes.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class OrganizationFrontierBindingDoAfterEvent : SimpleDoAfterEvent;
