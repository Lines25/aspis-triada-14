using Content.Shared.Actions;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Movement;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class VulpkaninJumpComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntProtoId Action = "ActionJumpVulpkanin";

    [DataField, AutoNetworkedField]
    public SoundSpecifier? JumpSound;

    [DataField, AutoNetworkedField]
    public float JumpSpeed = 7f;

    [DataField, AutoNetworkedField]
    public float JumpRange = 2.5f;

    [DataField, AutoNetworkedField]
    public float StaminaCost = 50f;

    [DataField, AutoNetworkedField]
    public float PushOffImpulseMultiplier = 1f;

    [ViewVariables]
    public EntityUid? JumpActionEntity;

    [AutoNetworkedField]
    public bool Jumping;

    [AutoNetworkedField]
    public bool IgnoreWalls;

    [AutoNetworkedField]
    public VulpkaninJumpEndState PendingEnd;
}

public sealed partial class VulpkaninJumpActionEvent : WorldTargetActionEvent;

[Serializable, NetSerializable]
public enum VulpkaninJumpEndState : byte
{
    None,
    Table,
    Wall,
}
