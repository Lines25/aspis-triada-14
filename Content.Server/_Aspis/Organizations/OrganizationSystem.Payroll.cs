using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Database;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Salary policy and durable payroll: salaries are allocated from real organization
/// treasury funds, never minted in the bank. Offline allocations wait in escrow.
/// </summary>
public sealed partial class OrganizationSystem
{
    private const int MaxPayrollPerMember = 2_000;
    private const int MaxPayrollAccounts = 256;
    private const int MaxPayrollClaim = 25_000;
    private const int PayrollPaymentsPerSweep = 4;
    private TimeSpan _nextAutomaticPayrollPayout;
    private readonly Dictionary<Guid, TimeSpan> _recentPayrollPolicyChanges = new();

    private void OnPayrollPolicy(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationPayrollPolicyMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Budget,
                out var actor, out var org))
            return;
        if (args.PerMember is < 0 or > MaxPayrollPerMember ||
            (args.Automatic && org.Kind != OrganizationKind.Company) ||
            args.ProfitPercent is < 0 or > 60 || args.ProfitPercent % 5 != 0 ||
            (_recentPayrollPolicyChanges.TryGetValue(org.Id, out var last) &&
             _gameTiming.CurTime < last + TimeSpan.FromMinutes(10)))
        {
            Refresh(uid, actor, "Значення зарплати має бути 0–2000 $, пауза зміни — 10 хвилин.");
            return;
        }
        if (!Commit(() =>
            {
                org.PayrollPerMember = args.PerMember;
                if (args.Automatic && !org.PayrollAutomatic)
                    org.PayrollLastCycle = org.CompletedEconomyCycles;
                org.PayrollAutomatic = args.Automatic;
                org.PayrollProfitPercent = args.ProfitPercent;
            }))
        {
            Refresh(uid, actor, "Не вдалося зберегти зарплатну політику.");
            return;
        }
        _recentPayrollPolicyChanges[org.Id] = _gameTiming.CurTime;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Organization payroll policy: org={org.Id}, amount={args.PerMember}, actor={ToPrettyString(actor):player}");
        Refresh(uid, actor, "Зарплатну політику збережено.");
    }

    // Relative salary weights are tied to organizational responsibility. Custom roles
    // receive a slightly higher weight for delegated permissions; distinctions add up
    // to 15%. This does not grant any gameplay permission through payroll.
    private static int PayrollWeight(Organization org, Member member)
    {
        var weight = member.Id == org.DeputyMemberId ? 150 :
            member.Role == OrganizationMemberRole.Officer ? 130 : 100;
        if (member.Role == OrganizationMemberRole.Member && member.CustomRoleId != Guid.Empty)
        {
            var custom = org.CustomRoles.FirstOrDefault(r => r.Id == member.CustomRoleId);
            if (custom != null)
            {
                var rights = custom.Permissions;
                weight = 105 +
                    ((rights & OrganizationPermission.Personnel) != 0 ? 10 : 0) +
                    ((rights & OrganizationPermission.Budget) != 0 ? 10 : 0) +
                    ((rights & OrganizationPermission.Investments) != 0 ? 10 : 0) +
                    ((rights & OrganizationPermission.Settings) != 0 ? 5 : 0) +
                    ((rights & OrganizationPermission.Strategy) != 0 ? 5 : 0);
            }
        }
        return weight * (100 + Math.Min(3, (int) member.Distinction) * 5) / 100;
    }

    // The owner's share rises moderately as the company develops. All payments,
    // including the owner's, come from the SAME profit-funded payroll allocation.
    private static int OwnerPayrollPoolPercent(Organization org) =>
        Math.Clamp(18 + 2 * (org.CompanyLevel - 1), 18, 32);

    // Runs within the owning economy/upkeep Commit transaction. Never pays the
    // same cycle twice and never spends more than treasury / allocated payroll.
    private void AccruePayroll(Organization org)
    {
        if (org.InsolvencyState != OrganizationInsolvencyState.Active)
            return;
        var workers = org.Members.Values.Where(m => m.Key != org.Owner)
            .OrderBy(m => m.Id).ToArray();
        var owner = org.Kind == OrganizationKind.Company &&
            org.Members.TryGetValue(org.Owner, out var memberOwner) ? memberOwner : null;
        if (workers.Length == 0 && owner == null)
            return;

        long budget;
        if (org.PayrollAutomatic)
        {
            if (org.PayrollLastCycle >= org.CompletedEconomyCycles)
                return;
            org.PayrollLastCycle = org.CompletedEconomyCycles;
            budget = Math.Max(0L, (long)Math.Max(0, org.LastCycleNet) * org.PayrollProfitPercent / 100);
        }
        else
        {
            budget = 0;
            foreach (var worker in workers)
                budget += Math.Min(MaxPayrollPerMember,
                    (long)org.PayrollPerMember * PayrollWeight(org, worker) / 100);
            if (owner != null)
                budget += Math.Min(3_000L,
                    (long)org.PayrollPerMember * (125 + 5 * Math.Clamp(org.CompanyLevel, 1, 8)) / 100);
        }
        if (budget <= 0) return;
        if (!org.PayrollAutomatic && budget > org.Treasury) return;
        budget = Math.Min(budget, org.Treasury);
        if (budget <= 0) return;

        var recipients = new List<(Member Worker, long Amount)>();
        long remaining = budget;
        if (owner != null)
        {
            var amount = org.PayrollAutomatic
                ? Math.Min(3_000L, budget * OwnerPayrollPoolPercent(org) / 100)
                : Math.Min(3_000L, (long)org.PayrollPerMember *
                    (125 + 5 * Math.Clamp(org.CompanyLevel, 1, 8)) / 100);
            if (amount > 0)
            {
                recipients.Add((owner, amount));
                remaining -= amount;
            }
        }
        var weights = workers.Select(worker => PayrollWeight(org, worker)).ToArray();
        var weightSum = weights.Sum(weight => (long)weight);
        for (var i = 0; i < workers.Length; i++)
        {
            var pay = org.PayrollAutomatic
                ? (weightSum == 0 ? 0 : Math.Min(MaxPayrollPerMember,
                    remaining * weights[i] / weightSum))
                : Math.Min(MaxPayrollPerMember,
                    (long)org.PayrollPerMember * weights[i] / 100);
            if (pay > 0)
                recipients.Add((workers[i], pay));
        }
        var total = recipients.Sum(entry => entry.Amount);
        if (total <= 0 || total > org.Treasury || total > budget ||
            org.PayrollAccrued > long.MaxValue - total ||
            org.OperatingCosts > long.MaxValue - total)
            return;

        var newAccounts = recipients.Count(entry => !org.PayrollAccounts.Any(p =>
            p.UserId == entry.Worker.Key.UserId && p.Slot == entry.Worker.Key.Slot));
        if (org.PayrollAccounts.Count + newAccounts > MaxPayrollAccounts ||
            recipients.Any(entry => org.PayrollAccounts.Any(p =>
                p.UserId == entry.Worker.Key.UserId && p.Slot == entry.Worker.Key.Slot &&
                (p.Name != entry.Worker.Name || p.Available > MaxTreasury - entry.Amount))))
            return;

        foreach (var (worker, pay) in recipients)
        {
            var account = org.PayrollAccounts.FirstOrDefault(p =>
                p.UserId == worker.Key.UserId && p.Slot == worker.Key.Slot);
            if (account == null)
            {
                account = new OrganizationPayrollAccountRecord
                    { UserId = worker.Key.UserId, Slot = worker.Key.Slot, Name = worker.Name };
                org.PayrollAccounts.Add(account);
            }
            account.Available += pay;
        }
        org.Treasury -= total;
        org.OperatingCosts += total;
        org.PayrollAccrued += total;
    }

    private void SettleAutomaticPayroll()
    {
        if (!_store.Available)
            return;
        var paid = 0;
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame ||
                session.AttachedEntity is not { } actor ||
                !TryGetCharacter(actor, out var key, out _) ||
                !IsOriginalController(actor) || IsTdfOfficer(actor))
                continue;

            foreach (var orgId in _organizations.Keys.ToArray())
            {
                if (!_organizations.TryGetValue(orgId, out var org))
                    continue;
                var account = org.PayrollAccounts.FirstOrDefault(p =>
                    p.UserId == key.UserId && p.Slot == key.Slot &&
                    p.Name == key.CharacterName && p.Available > 0 && p.Pending == 0);
                if (account == null)
                    continue;
                if (!TryComp<BankAccountComponent>(actor, out var bank))
                    break;
                var amount = (int)Math.Min(account.Available, MaxPayrollClaim);
                if (bank.Balance > int.MaxValue - amount)
                    break;
                var transfer = Guid.NewGuid();
                if (!Commit(() =>
                    {
                        account.Available -= amount;
                        account.Pending = amount;
                        account.PendingTransferId = transfer;
                    }))
                    return;

                if (!_bank.TryBankDeposit(actor, amount))
                {
                    if (!Commit(() =>
                        {
                            account.Available += amount;
                            account.Pending = 0;
                            account.PendingTransferId = Guid.Empty;
                        }))
                        _adminLog.Add(LogType.Action, LogImpact.High,
                            $"Payroll FAILED bank rollback: org={orgId}, transfer={transfer}, amount={amount}");
                    break;
                }

                if (!Commit(() =>
                    {
                        account.Pending = 0;
                        account.PendingTransferId = Guid.Empty;
                        account.Paid += amount;
                    }))
                {
                    _adminLog.Add(LogType.Action, LogImpact.High,
                        $"Payroll UNCONFIRMED deposit: org={orgId}, transfer={transfer}, " +
                        $"player={key.UserId}, amount={amount}; DO NOT repeat deposit.");
                    return;
                }
                _adminLog.Add(LogType.Action, LogImpact.Medium,
                    $"Payroll paid: org={orgId}, player={key.UserId}, amount={amount}");
                paid++;
                break;
            }
            if (paid >= PayrollPaymentsPerSweep)
                return;
        }
    }
}
