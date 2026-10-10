using Content.Server.EUI;
using Content.Shared.Eui;
using Content.Shared.Respawn;

namespace Content.Server.Respawn;

public sealed class RespawnConfirmEui : BaseEui
{
    private readonly RespawnConfirmSystem _respawnSystem;
    private readonly int _penalty;

    public RespawnConfirmEui(RespawnConfirmSystem respawnSystem, int penalty)
    {
        _respawnSystem = respawnSystem;
        _penalty = penalty;
    }

    public override EuiStateBase GetNewState()
    {
        return new RespawnConfirmEuiState(_penalty);
    }

    public override void Opened()
    {
        base.Opened();
        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is not RespawnConfirmChoiceMessage choice)
            return;

        Close();

        if (choice.Confirmed)
        {
            _respawnSystem.OnRespawnConfirmed(Player, _penalty);
        }
    }
}
