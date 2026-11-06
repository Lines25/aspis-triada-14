using System.Linq;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Database;
using Content.Shared._Aspis.Organizations;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Automatic dividend transfers only for currently authenticated, active
/// characters. Offline claims stay in the durable corporate ledger until that
/// character returns. The two-phase protocol never retries ambiguous deposits.
/// </summary>
public sealed partial class OrganizationSystem
{
    private static readonly TimeSpan AutoDividendPayoutInterval = TimeSpan.FromMinutes(1);
    private TimeSpan _nextAutomaticDividendPayout;
    private const int AutoDividendPayoutsPerSweep = 4;

    private enum DividendPaymentResult : byte
    {
        Empty,
        Held,
        BankUnavailable,
        NotSaved,
        BankRejected,
        NeedsReconciliation,
        Paid
    }

    private DividendPaymentResult SettleDividendPayment(Organization org,
        OrganizationDividendAccountRecord account, CharacterKey key, EntityUid actor,
        bool automatic)
    {
        if (!_store.Available)
            return DividendPaymentResult.NotSaved;
        if (account.Pending != 0)
            return DividendPaymentResult.Held;

        var amount = (int)Math.Min(account.Available, MaxDividendClaim);
        if (amount <= 0)
            return DividendPaymentResult.Empty;
        if (!TryComp<BankAccountComponent>(actor, out var bank) ||
            bank.Balance > int.MaxValue - amount)
            return DividendPaymentResult.BankUnavailable;

        var transferId = Guid.NewGuid();
        if (!Commit(() =>
            {
                account.Available -= amount;
                account.Pending = amount;
                account.PendingTransferId = transferId;
            }))
            return DividendPaymentResult.NotSaved;

        if (!_bank.TryBankDeposit(actor, amount))
        {
            if (!Commit(() =>
                {
                    account.Available += amount;
                    account.Pending = 0;
                    account.PendingTransferId = Guid.Empty;
                }))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Dividend bank rejection unresolved: org={org.Id}, transfer={transferId}, " +
                    $"player={key.UserId}, amount={amount}; administrator reconciliation required.");
            return DividendPaymentResult.BankRejected;
        }

        if (!Commit(() =>
            {
                account.Pending = 0;
                account.PendingTransferId = Guid.Empty;
                account.Paid += amount;
            }))
        {
            _adminLog.Add(LogType.Action, LogImpact.High,
                $"Dividend bank deposit UNCONFIRMED: org={org.Id}, transfer={transferId}, " +
                $"player={key.UserId}, amount={amount}; DO NOT repeat the deposit.");
            return DividendPaymentResult.NeedsReconciliation;
        }

        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Dividend paid: org={org.Id}, transfer={transferId}, player={key.UserId}, " +
            $"amount={amount}, automatic={automatic}");
        return DividendPaymentResult.Paid;
    }

    private void SettleAutomaticDividends()
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

            // Snapshot the company enumeration because an unsuccessful persistent
            // commit may restore the whole organization dictionary.
            foreach (var companyId in _organizations.Keys.ToArray())
            {
                if (!_organizations.TryGetValue(companyId, out var company) ||
                    company.Kind != Content.Shared._Aspis.Organizations.OrganizationKind.Company)
                    continue;
                var account = company.DividendAccounts.FirstOrDefault(a =>
                    a.UserId == key.UserId && a.Slot == key.Slot &&
                    a.Available > 0 && a.Pending == 0);
                if (account == null)
                    continue;

                var outcome = SettleDividendPayment(company, account, key, actor, automatic: true);
                if (outcome == DividendPaymentResult.Paid)
                {
                    paid++;
                    // At most one payout per character in a sweep. Avoid a burst
                    // of persistent bank writes after a long offline period.
                    break;
                }
                if (outcome is DividendPaymentResult.NotSaved or DividendPaymentResult.NeedsReconciliation)
                    return;
            }

            if (paid >= AutoDividendPayoutsPerSweep)
                return;
        }
    }
}
