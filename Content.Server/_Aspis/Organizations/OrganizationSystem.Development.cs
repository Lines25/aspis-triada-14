using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;

namespace Content.Server._Aspis.Organizations;

/// <summary>Persistent, capped sector development and owner-created job roles.</summary>
public sealed partial class OrganizationSystem
{
    private const int MaxCustomRoles = 12;
    private const int MaxDevelopmentLevel = OrganizationDevelopmentRules.MaxLevel;

    private static int DevelopmentLevel(Organization org, OrganizationDevelopmentBranch branch, int tier) =>
        (branch, tier) switch
        {
            (OrganizationDevelopmentBranch.Logistics, 1) => org.LogisticsLevel,
            (OrganizationDevelopmentBranch.Research, 1) => org.ResearchFacilityLevel,
            (OrganizationDevelopmentBranch.Infrastructure, 1) => org.InfrastructureLevel,
            (OrganizationDevelopmentBranch.ServiceNetwork, 1) => org.ServiceNetworkLevel,
            (OrganizationDevelopmentBranch.RiskManagement, 1) => org.RiskManagementLevel,
            (OrganizationDevelopmentBranch.Logistics, 2) => org.LogisticsTier2Level,
            (OrganizationDevelopmentBranch.Research, 2) => org.ResearchTier2Level,
            (OrganizationDevelopmentBranch.Infrastructure, 2) => org.InfrastructureTier2Level,
            (OrganizationDevelopmentBranch.ServiceNetwork, 2) => org.ServiceNetworkTier2Level,
            (OrganizationDevelopmentBranch.RiskManagement, 2) => org.RiskManagementTier2Level,
            _ => 0
        };

    private static bool AdvancedDevelopmentUnlocked(Organization org) =>
        OrganizationDevelopmentRules.AdvancedUnlocked(org.CompanyLevel, new[]
        {
            org.LogisticsLevel, org.ResearchFacilityLevel, org.InfrastructureLevel,
            org.ServiceNetworkLevel, org.RiskManagementLevel
        }.Count(level => level >= MaxDevelopmentLevel));

    // Faction programs use the already-persisted development levels, but
    // deliberately do NOT use company mastery or automatic market production.
    // A program requires actual field experience and a matching specialization.
    private static bool FactionProgramMatches(Organization org, OrganizationDevelopmentBranch branch)
    {
        var foci = FactionFocusList(org);
        return foci.Any(focus => branch switch
        {
            OrganizationDevelopmentBranch.Logistics => focus is
                OrganizationFactionFocus.Exploration or OrganizationFactionFocus.Colonial or
                OrganizationFactionFocus.Commercial or OrganizationFactionFocus.Humanitarian or
                OrganizationFactionFocus.Piracy or OrganizationFactionFocus.Agricultural or
                OrganizationFactionFocus.Other,
            OrganizationDevelopmentBranch.Research => focus is
                OrganizationFactionFocus.Scientific or OrganizationFactionFocus.Technological or
                OrganizationFactionFocus.Medical or OrganizationFactionFocus.Educational or
                OrganizationFactionFocus.Environmental,
            OrganizationDevelopmentBranch.Infrastructure => focus is
                OrganizationFactionFocus.Industrial or OrganizationFactionFocus.Colonial or
                OrganizationFactionFocus.Defense or OrganizationFactionFocus.Technological or
                OrganizationFactionFocus.Agricultural,
            OrganizationDevelopmentBranch.ServiceNetwork => focus is
                OrganizationFactionFocus.Civic or OrganizationFactionFocus.Humanitarian or
                OrganizationFactionFocus.Cultural or OrganizationFactionFocus.Religious or
                OrganizationFactionFocus.Spiritual or OrganizationFactionFocus.Political or
                OrganizationFactionFocus.Media or OrganizationFactionFocus.Diplomatic or
                OrganizationFactionFocus.Labor or OrganizationFactionFocus.Artistic or
                OrganizationFactionFocus.Traditionalist or OrganizationFactionFocus.Pacifist,
            OrganizationDevelopmentBranch.RiskManagement => focus is
                OrganizationFactionFocus.Security or OrganizationFactionFocus.Defense or
                OrganizationFactionFocus.Militarist or OrganizationFactionFocus.Criminal or
                OrganizationFactionFocus.Piracy or OrganizationFactionFocus.Revolutionary or
                OrganizationFactionFocus.Ideological,
            _ => false
        });
    }

    private void DevelopFactionProgram(EntityUid uid, EntityUid actor,
        Organization org, OrganizationDevelopmentMessage args)
    {
        if (args.Tier != 1 || !Enum.IsDefined(args.Branch) ||
            !FactionProgramMatches(org, args.Branch))
        {
            Refresh(uid, actor, "Програма не відповідає спрямуванням фракції.");
            return;
        }
        var level = DevelopmentLevel(org, args.Branch, 1);
        if (level >= MaxDevelopmentLevel)
        {
            Refresh(uid, actor, "Програму вже розвинено до максимального рівня.");
            return;
        }
        var authorityRequired = 4L + 6L * level;
        if (org.FactionAuthorityPoints < authorityRequired ||
            org.FactionOperationsCompleted < level + 1)
        {
            Refresh(uid, actor, $"Потрібні авторитет {authorityRequired} та {level + 1} виконаних завдань.");
            return;
        }
        var cost = 4_000L * (level + 1) * (level + 2);
        if (!_store.Available || org.Treasury < cost || org.CapitalSpent > long.MaxValue - cost)
        {
            Refresh(uid, actor, "У скарбниці недостатньо коштів для програми.");
            return;
        }
        var remaining = Math.Max(0, MarketCapacityBp -
            _organizations.Values.Sum(o => o.SectorInfluenceBp));
        // Measured, gradual presence; never replace this with network-share %.
        var influence = Math.Min(remaining, 5 + (level + 1) * 3);
        if (!Commit(() =>
        {
            org.Treasury -= cost;
            org.CapitalSpent += cost;
            switch (args.Branch)
            {
                case OrganizationDevelopmentBranch.Logistics: org.LogisticsLevel++; break;
                case OrganizationDevelopmentBranch.Research: org.ResearchFacilityLevel++; break;
                case OrganizationDevelopmentBranch.Infrastructure: org.InfrastructureLevel++; break;
                case OrganizationDevelopmentBranch.ServiceNetwork: org.ServiceNetworkLevel++; break;
                case OrganizationDevelopmentBranch.RiskManagement: org.RiskManagementLevel++; break;
            }
            org.SectorInfluenceBp += influence;
        }))
        {
            Refresh(uid, actor, "Не вдалося зберегти програму розвитку.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Faction program upgraded: {org.Id}, {args.Branch}, level {level + 1}, price {cost}, sector +{influence}bp");
        Refresh(uid, actor, $"Програму покращено до рівня {level + 1}. Секторна присутність: +{influence / 100f:0.00}%.");
    }

    private void OnDevelopment(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationDevelopmentMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Investments,
                out var actor, out var org))
            return;
        if (org.Kind == OrganizationKind.Faction)
        {
            DevelopFactionProgram(uid, actor, org, args);
            return;
        }
        if (org.Kind != OrganizationKind.Company || !Enum.IsDefined(args.Branch) ||
            args.Tier is < 1 or > 2 ||
            org.InsolvencyState != OrganizationInsolvencyState.Active || IsTradingSuspended(org))
            return;

        var level = DevelopmentLevel(org, args.Branch, args.Tier);
        if (level >= MaxDevelopmentLevel)
        {
            Refresh(uid, actor, "Цей напрям уже повністю розвинений.");
            return;
        }
        if ((args.Tier == 2 && (!AdvancedDevelopmentUnlocked(org) ||
            DevelopmentLevel(org, args.Branch, 1) < MaxDevelopmentLevel)) ||
            org.CompanyLevel < OrganizationDevelopmentRules.MasteryRequired(level, args.Tier) ||
            org.SectorInfluenceBp < OrganizationDevelopmentRules.SectorRequiredBp(level, args.Tier))
        {
            Refresh(uid, actor, "Для покращення потрібні вища майстерність, присутність у секторі та попередній щабель.");
            return;
        }
        var price = OrganizationDevelopmentRules.Price(args.Branch, level, args.Tier);
        if (!_store.Available || org.Treasury < price || org.CapitalSpent > long.MaxValue - price)
        {
            Refresh(uid, actor, "У скарбниці недостатньо коштів для розвитку.");
            return;
        }
        if (!Commit(() =>
        {
            org.Treasury -= price;
            org.CapitalSpent += price;
            switch (args.Branch, args.Tier)
            {
                case (OrganizationDevelopmentBranch.Logistics, 1): org.LogisticsLevel++; break;
                case (OrganizationDevelopmentBranch.Research, 1): org.ResearchFacilityLevel++; break;
                case (OrganizationDevelopmentBranch.Infrastructure, 1): org.InfrastructureLevel++; break;
                case (OrganizationDevelopmentBranch.ServiceNetwork, 1): org.ServiceNetworkLevel++; break;
                case (OrganizationDevelopmentBranch.RiskManagement, 1): org.RiskManagementLevel++; break;
                case (OrganizationDevelopmentBranch.Logistics, 2): org.LogisticsTier2Level++; break;
                case (OrganizationDevelopmentBranch.Research, 2): org.ResearchTier2Level++; break;
                case (OrganizationDevelopmentBranch.Infrastructure, 2): org.InfrastructureTier2Level++; break;
                case (OrganizationDevelopmentBranch.ServiceNetwork, 2): org.ServiceNetworkTier2Level++; break;
                case (OrganizationDevelopmentBranch.RiskManagement, 2): org.RiskManagementTier2Level++; break;
            }
            // Gentle passive footprint, never takes share away from rivals.
            var sectorFree = Math.Max(0, MarketCapacityBp -
                _organizations.Values.Sum(o => o.SectorInfluenceBp));
            org.SectorInfluenceBp += Math.Min(sectorFree, args.Tier == 2 ? 6 : 4);
            var industryFree = Math.Max(0, MarketCapacityBp -
                _organizations.Values.Where(o => o.Kind == OrganizationKind.Company && o.Industry == org.Industry)
                    .Sum(o => o.IndustryInfluenceBp));
            org.IndustryInfluenceBp += Math.Min(industryFree, args.Tier == 2 ? 12 : 8);
        }))
        {
            Refresh(uid, actor, "Не вдалося зберегти розвиток.");
            return;
        }
        CheckConcentration(org, uid);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Organization facility upgrade: org={org.Id}, branch={args.Branch}, tier={args.Tier}, level={level + 1}, price={price}");
        Refresh(uid, actor, "Розвиток відділу збережено.");
    }

    // Organic market gains follow actual successful production, bounded by available capacity.
    private void EarnOrganicInfluence(Organization org, int net)
    {
        if (org.Kind != OrganizationKind.Company || net <= 0 ||
            org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;
        var industryFree = Math.Max(0, MarketCapacityBp - _organizations.Values
            .Where(o => o.Kind == OrganizationKind.Company && o.Industry == org.Industry)
            .Sum(o => o.IndustryInfluenceBp));
        var industryGain = Math.Min(industryFree, Math.Clamp(net / 500, 1, 4));
        org.IndustryInfluenceBp += industryGain;

        if (org.IndustryInfluenceBp < 25) return;
        var sectorFree = Math.Max(0, MarketCapacityBp - _organizations.Values.Sum(o => o.SectorInfluenceBp));
        org.SectorInfluenceBp += Math.Min(sectorFree, Math.Clamp(net / 1_000, 1, 2));
    }

    private void OnSaveCustomRole(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationSaveCustomRoleMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            !ValidPermissions(args.Permissions))
            return;
        var title = string.Join(" ", (args.Title ?? string.Empty).Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!ValidTitle(title) ||
            new[] { org.OwnerTitle, org.DeputyTitle, org.MemberTitle, org.OfficerTitle }
                .Any(baseName => string.Equals(baseName, title, StringComparison.OrdinalIgnoreCase)))
        {
            Refresh(uid, actor, "Назва посади має бути унікальною (2–32 символи).");
            return;
        }
        var role = org.CustomRoles.FirstOrDefault(r => r.Id == args.Role);
        if ((role == null && (args.Role != Guid.Empty || org.CustomRoles.Count >= MaxCustomRoles)) ||
            org.CustomRoles.Any(r => r.Id != args.Role &&
                string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase)))
        {
            Refresh(uid, actor, "Посада вже існує або ліміт посад досягнуто.");
            return;
        }
        var newRoleId = Guid.NewGuid();
        var saved = Commit(() =>
        {
            if (role != null)
            {
                role.Title = title;
                role.Permissions = args.Permissions;
            }
            else
                org.CustomRoles.Add(new OrganizationCustomRoleRecord
                { Id = newRoleId, Title = title, Permissions = args.Permissions });
        });
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Organization role {(role == null ? "created" : "updated")}: org={org.Id}, role={args.Role}, actor={ToPrettyString(actor):player}");
        Refresh(uid, actor, saved ? "Посаду збережено." : "Не вдалося зберегти посаду.");
    }

    private void OnAssignCustomRole(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationAssignCustomRoleMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        if (member == null || member.Role == OrganizationMemberRole.Owner ||
            member.Key == org.Owner || member.Id == org.DeputyMemberId ||
            (args.Role != Guid.Empty && org.CustomRoles.All(r => r.Id != args.Role)))
            return;
        if (!Commit(() =>
        {
            member.Role = OrganizationMemberRole.Member;
            member.CustomTitle = string.Empty;
            member.CustomRoleId = args.Role;
        }))
        {
            Refresh(uid, actor, "Не вдалося призначити посаду.");
            return;
        }
        SyncActive(member.Key);
        Refresh(uid, actor, "Посаду працівника змінено.");
    }
}
