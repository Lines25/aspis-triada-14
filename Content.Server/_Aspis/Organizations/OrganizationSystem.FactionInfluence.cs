using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// v49: Faction network strength comes from real commissioned nodes. Research
/// findings are archived separately and never count as territorial ownership.
/// Authority is experience, not a fictitious global market share; it unlocks a
/// shorter recovery period after a completed assignment.
/// </summary>
public sealed partial class OrganizationSystem
{
    private void NotifyFactionTaskCompleted(EntityUid actor)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity != actor)
                continue;
            _organizationProfileNet.ServerSendMessage(new MsgOrganizationFactionTaskCompleted(), session.Channel);
            break;
        }
    }

    private readonly Dictionary<Guid, TimeSpan> _factionNextTaskAt = new();

    private static int FactionNodeWeight(OrganizationFactionNodeKind kind) => kind switch
    {
        OrganizationFactionNodeKind.ScientificRelay => 4,
        OrganizationFactionNodeKind.DefenseRelay => 4,
        OrganizationFactionNodeKind.ResearchDefenseRelay => 5,
        _ => 3
    };

    private static int FactionStrength(Organization org) =>
        org.Kind == OrganizationKind.Faction
            ? org.FactionNodes.Sum(node => FactionNodeWeight(node.Kind)) : 0;

    private float FactionNetworkSharePercent(Organization org)
    {
        if (org.Kind != OrganizationKind.Faction)
            return 0f;
        var total = _organizations.Values.Sum(FactionStrength);
        return total > 0 ? 100f * FactionStrength(org) / total : 0f;
    }

    private static TimeSpan FactionTaskCooldown(Organization org) =>
        TimeSpan.FromSeconds(Math.Max(60, (org.FactionAuthorityPoints >= 50 ? 120 :
            org.FactionAuthorityPoints >= 20 ? 180 : 240) -
            12 * org.LogisticsLevel - 6 * org.ServiceNetworkLevel));

    private int FactionNextTaskSeconds(Organization org) =>
        _factionNextTaskAt.TryGetValue(org.Id, out var next)
            ? Math.Max(0, (int)Math.Ceiling((next - _gameTiming.CurTime).TotalSeconds)) : 0;

    private void ScheduleNextFactionTask(Organization org)
    {
        if (org.Kind == OrganizationKind.Faction)
            _factionNextTaskAt[org.Id] = _gameTiming.CurTime + FactionTaskCooldown(org);
    }
}
