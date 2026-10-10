using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server._NF.Bank;
using Content.Server.Preferences.Managers;
using Content.Shared.Preferences;
using Robust.Shared.Player;
using System;

namespace Content.Server.Respawn;

public sealed class RespawnConfirmSystem : EntitySystem
{
    [Dependency] private readonly EuiManager _euiManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly BankSystem _bankSystem = default!;
    [Dependency] private readonly IServerPreferencesManager _prefsManager = default!;

    public void RequestRespawn(ICommonSession player)
    {
        if (!_bankSystem.TryGetBalance(player, out var balance))
            balance = 0;
        
        int penalty = (int)Math.Round(balance * 0.20);

        var eui = new RespawnConfirmEui(this, penalty);
        _euiManager.OpenEui(eui, player);
    }

    public void OnRespawnConfirmed(ICommonSession player, int penalty)
    {
        if (penalty > 0)
        {
            if (_prefsManager.TryGetCachedPreferences(player.UserId, out var prefs) &&
                prefs.SelectedCharacter is HumanoidCharacterProfile profile)
            {
                _bankSystem.TryBankWithdraw(player, prefs, profile, penalty, out _);
            }
        }

        _gameTicker.Respawn(player);
    }
}
