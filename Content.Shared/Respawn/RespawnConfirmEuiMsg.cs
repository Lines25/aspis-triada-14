using System;
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.Respawn;

[Serializable, NetSerializable]
public sealed class RespawnConfirmEuiState : EuiStateBase
{
    public int PenaltyAmount { get; }

    public RespawnConfirmEuiState(int penaltyAmount)
    {
        PenaltyAmount = penaltyAmount;
    }
}

[Serializable, NetSerializable]
public sealed class RespawnConfirmChoiceMessage : EuiMessageBase
{
    public bool Confirmed { get; }

    public RespawnConfirmChoiceMessage(bool confirmed)
    {
        Confirmed = confirmed;
    }
}
