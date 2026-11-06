using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>
/// Raised by SharedToolSystem only after a cancellable screwdriver, multitool
/// or wrench interaction. The task, tool, user and grid are revalidated at the
/// end of the action; this event carries no user-provided progress data.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class AspisFactionWreckToolCompletedEvent : SimpleDoAfterEvent;
