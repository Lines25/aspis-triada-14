using System;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Shared.Radio;
using Robust.Shared.Prototypes;
using Content.Server.Administration.Logs;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private bool CanViewInvestmentList(Organization org, EntityUid actor, EntityUid console,
        CharacterKey key, bool canOperate) =>
        org.InvestorsPublic || (canOperate && (org.Owner == key ||
            (org.Members.TryGetValue(key, out var member) &&
             HasPermission(org, member, OrganizationPermission.Investments)) ||
            CanInspectExternalInvestors(actor, console, key)));

    private void OnInvestorPrivacy(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationInvestorPrivacyMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        if (org.InvestorsPublic == args.Visible)
            return;
        if (!Commit(() => org.InvestorsPublic = args.Visible))
        {
            Refresh(uid, actor, "Не вдалося зберегти налаштування реєстру.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Investor registry visibility: {org.Id} public={args.Visible}");
        Refresh(uid, actor, args.Visible ? "Реєстр інвесторів відкрито." : "Реєстр інвесторів приховано.");
    }

    private void OnMarketProgram(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationMarketProgramMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Strategy,
                out var actor, out var org) || org.Kind != OrganizationKind.Company ||
            args.Program is not (OrganizationMarketProgram.PriceCampaign or OrganizationMarketProgram.PriorityContracts))
            return;
        if (org.MarketProgramCycles > 0 || org.InsolvencyState != OrganizationInsolvencyState.Active ||
            org.Treasury < MinWorkingCapital)
        {
            Refresh(uid, actor, "Програма недоступна: перевірте скарбницю та поточну кампанію.");
            return;
        }
        if (!Commit(() =>
        {
            org.MarketProgram = args.Program;
            org.MarketProgramCycles = OrganizationMarketProgramRules.Duration;
        }))
        {
            Refresh(uid, actor, "Не вдалося розпочати ринкову програму.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Market program: {org.Id} program={args.Program} actor={ToPrettyString(actor):player}");
        Refresh(uid, actor, "Ринкова програма розпочата. Діятиме три цикли з виручкою.");
    }

    // Called inside the same durable commit as the actual economy settlement.
    private void RecordPassiveMarketReview(Organization org, Organization[] companies,
        int[] amounts, int index, int gross, int costs, int assignedDemand, DateTime now)
    {
        long industryGross = gross;
        var rivals = 0;
        for (var i = 0; i < companies.Length; i++)
        {
            if (i == index || companies[i].Industry != org.Industry || amounts[i] <= 0)
                continue;
            rivals++;
            industryGross += amounts[i];
        }

        var level = OrganizationMarketProgramRules.ReviewLevel(org.MarketProgram,
            org.MarketProgramCycles, gross, costs, industryGross, rivals);
        if (level > 0)
        {
            var share = 100L * gross / industryGross;
            var category = org.MarketProgram == OrganizationMarketProgram.PriceCampaign
                ? "Цінова конкуренція" : "Пріоритетні контракти";
            var reason = org.MarketProgram == OrganizationMarketProgram.PriceCampaign
                ? "Цінова кампанія при низькій рентабельності та великій частці виручки."
                : "Значна частка галузевої виручки під час програми пріоритетних контрактів.";
            AddPassiveMarketAlert(org, category, gross, now,
                $"{(level == 3 ? "III" : "II")} — {reason} Частка: {share}%. Виручка: {gross:N0} $, витрати: {costs:N0} $. Компаній-конкурентів: {rivals}.");
        }

        // Independent review of unusually concentrated passive demand, even
        // when no player selected a market program.
        if (rivals > 0 && gross >= 250 && org.CompletedEconomyCycles > 0 && org.LastCycleDemand > 0 &&
            gross * 100L >= assignedDemand * 65L &&
            org.LastCycleGross * 100L >= org.LastCycleDemand * 35L &&
            gross * 100L >= industryGross * 70L)
        {
            AddPassiveMarketAlert(org, "Концентрація пасивного попиту", gross, now,
                $"I — Висока частка пасивної виручки у двох циклах. Поточна частка галузевої виручки: {100L * gross / industryGross}%. Перевірити умови конкуренції.");
        }
    }

    private void NotifyPassiveMarketReviews(DateTime now)
    {
        var alerts = _concentrationAlerts.Where(a => a.Utc == now && !string.IsNullOrEmpty(a.Operation)).ToArray();
        if (alerts.Length == 0)
            return;
        foreach (var alert in alerts)
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"TDF passive market review: {alert.Organization} {alert.Operation}: {alert.Reason}");
        if (!_prototypes.TryIndex<RadioChannelPrototype>("Nfsd", out var channel))
            return;
        var query = EntityQueryEnumerator<OrganizationConsoleComponent>();
        if (query.MoveNext(out var terminal, out _))
            _radio.SendRadioMessage(terminal,
                $"TDF: нові сигнали автономної економіки — {alerts.Length}. Перевірте журнал нагляду.",
                channel, terminal);
    }

    private void AddPassiveMarketAlert(Organization org, string category, int gross, DateTime now, string reason)
    {
        if (_concentrationAlerts.Any(a => a.Organization == org.Id && a.Operation == category &&
            a.Utc > now - TimeSpan.FromMinutes(30)))
            return;
        _concentrationAlerts.Add(new OrganizationConcentrationRecord
        {
            Utc = now, Organization = org.Id, Company = org.Name, Operation = category,
            Gross = gross, Reason = reason
        });
        if (_concentrationAlerts.Count > 250)
            _concentrationAlerts.RemoveAt(0);
    }
}
