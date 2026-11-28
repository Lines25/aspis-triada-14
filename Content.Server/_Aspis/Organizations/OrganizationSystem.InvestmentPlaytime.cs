using Content.Server.Players.PlayTimeTracking;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.Enums;
using Robust.Shared.Player;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    [Dependency] private PlayTimeTrackingManager _investmentPlaytime = default!;

    public bool TryGetInvestmentPlaytimeTracker(ICommonSession session, out string tracker)
    {
        tracker = string.Empty;
        if (session.Status != SessionStatus.InGame ||
            session.AttachedEntity is not { } actor ||
            !TryGetCharacter(actor, out var key, out _) ||
            !IsOriginalController(actor))
            return false;

        tracker = OrganizationInvestmentPlaytimeRules.Tracker(key.Slot);
        return true;
    }

    private bool RequireInvestmentPlaytime(EntityUid console, EntityUid actor)
    {
        if (!_players.TryGetSessionByEntity(actor, out var session) ||
            !TryGetInvestmentPlaytimeTracker(session, out var tracker) ||
            !_investmentPlaytime.TryGetInvestmentCharacterTime(session, tracker, out var played))
        {
            Refresh(console, actor, "Інвестиції тимчасово недоступні: немає підтвердженого ігрового часу персонажа.");
            return false;
        }

        if (OrganizationInvestmentPlaytimeRules.Eligible(played))
            return true;

        Refresh(console, actor, $"Інвестиції доступні після 90 хвилин гри цим персонажем. Залишилося {OrganizationInvestmentPlaytimeRules.RemainingMinutes(played)} хв.");
        return false;
    }
}
