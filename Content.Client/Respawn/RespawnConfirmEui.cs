using Content.Client.Eui;
using Content.Shared.Eui;
using Content.Shared.Respawn;
using JetBrains.Annotations;

namespace Content.Client.Respawn;

[UsedImplicitly]
public sealed class RespawnConfirmEui : BaseEui
{
    private readonly RespawnConfirmWindow _window;

    public RespawnConfirmEui()
    {
        _window = new RespawnConfirmWindow();

        _window.OnConfirm += () =>
        {
            SendMessage(new RespawnConfirmChoiceMessage(true));
            _window.Close();
        };

        _window.OnCancel += () =>
        {
            SendMessage(new RespawnConfirmChoiceMessage(false));
            _window.Close();
        };

        _window.OnClose += () =>
        {
            SendMessage(new RespawnConfirmChoiceMessage(false));
        };
    }

    public override void HandleState(EuiStateBase state)
    {
        base.HandleState(state);

        if (state is not RespawnConfirmEuiState confirmState)
            return;

        _window.SetPenalty(confirmState.PenaltyAmount);
    }

    public override void Opened()
    {
        base.Opened();
        _window.OpenCentered();
    }

    public override void Closed()
    {
        base.Closed();
        _window.Close();
    }
}
