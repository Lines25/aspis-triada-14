using System;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

/// <summary>One physical receiver at the only remote outpost; A-D are cargo lanes, not maps.</summary>
[RegisterComponent]
public sealed partial class OrganizationFrontierReceivingComponent : Component
{
    public Guid Assignment;
    public int SelectedPost;
}

[Serializable, NetSerializable]
public enum OrganizationFrontierReceivingUiKey : byte { Key }

[Serializable, NetSerializable]
public sealed class OrganizationFrontierSelectPostMessage(int post) : BoundUserInterfaceMessage
{
    public int Post = post;
}

[Serializable, NetSerializable]
public sealed class OrganizationFrontierReceivingState(int selected, int[] targets, int[] received, string code)
    : BoundUserInterfaceState
{
    public int Selected = selected;
    public int[] Targets = targets;
    public int[] Received = received;
    public string Code = code;
}
