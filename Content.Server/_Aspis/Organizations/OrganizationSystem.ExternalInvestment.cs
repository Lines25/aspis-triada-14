using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem
{
    private const int MaxExternalInvestors = 256;
    private const long MaxRecordedInvestorCapital = 100_000_000_000L;

    private static bool ValidExternalInvestors(List<OrganizationExternalInvestorRecord>? records)
    {
        if (records == null)
            return true;
        if (records.Count > MaxExternalInvestors)
            return false;
        var keys = new HashSet<(string, int)>();
        return records.All(i => i != null && !string.IsNullOrWhiteSpace(i.UserId) &&
            i.UserId.Length <= 64 && i.Slot >= 0 && !string.IsNullOrWhiteSpace(i.Name) &&
            i.Name.Length <= 128 && i.Amount is > 0 and <= MaxRecordedInvestorCapital &&
            keys.Add((i.UserId, i.Slot)));
    }

    private static long TotalExternalInvestment(Organization org) =>
        org.ExternalInvestors.Sum(i => i.Amount);

    private static int InvestmentDemandPercent(Organization org) =>
        org.Kind == OrganizationKind.Company
            ? (int)Math.Clamp(Math.Min(org.Treasury, TotalExternalInvestment(org)) / 100_000L, 0, 10)
            : 0;

    private bool CanInspectExternalInvestors(EntityUid actor, EntityUid console, CharacterKey key) =>
        TryGetTdfRank(actor, out _, out var grade) && IsTdfTerminal(console) &&
        (grade >= 3 || (TdfRights(key) & OrganizationTdfServiceRight.Audit) != 0);

    private void OnExternalInvestment(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationExternalInvestmentMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out var name) ||
            !_store.Available || !_organizations.TryGetValue(args.Organization, out var org) ||
            org.InsolvencyState != OrganizationInsolvencyState.Active ||
            args.Amount is <= 0 or > MaxTreasuryTransfer || org.Treasury > MaxTreasury - args.Amount)
        {
            if (actor.Valid)
                Refresh(uid, actor, "Внесок недоступний: перевірте суму та стан організації.");
            return;
        }
        if (!RequireInvestmentPlaytime(uid, actor))
            return;

        var existing = org.ExternalInvestors.FirstOrDefault(i => i.UserId == key.UserId && i.Slot == key.Slot);
        if ((existing == null && org.ExternalInvestors.Count >= MaxExternalInvestors) ||
            (existing != null && existing.Amount > MaxRecordedInvestorCapital - args.Amount))
        {
            Refresh(uid, actor, "Ліміт реєстру інвесторів досягнуто.");
            return;
        }
        if (!_bank.TryBankWithdraw(actor, args.Amount))
        {
            Refresh(uid, actor, "Банк відхилив внесок.");
            return;
        }
        var recent = _tradeLedger.Where(t => t.Organization == org.Id &&
            t.TraderUserId == key.UserId && t.Utc > DateTime.UtcNow - TimeSpan.FromMinutes(10)).ToArray();
        var reason = recent.Any(t => t.Order is OrganizationStockOrder.Buy or OrganizationStockOrder.Sell)
            ? "II — Зовнішнє фінансування пов'язаного учасника торгів"
            : args.Amount + recent.Where(t => t.Order == OrganizationStockOrder.ExternalInvestment).Sum(t => t.Total) >= 100_000
                ? "I — Значний зовнішній приплив капіталу" : string.Empty;
        if (!Commit(() =>
        {
            org.Treasury += args.Amount;
            if (existing == null)
                org.ExternalInvestors.Add(new OrganizationExternalInvestorRecord
                    { UserId = key.UserId, Slot = key.Slot, Name = name, Amount = args.Amount });
            else
            {
                existing.Amount += args.Amount;
                existing.Name = name;
            }
            _tradeLedger.Add(new OrganizationTradeRecord
            {
                Utc = DateTime.UtcNow, Organization = org.Id, Company = org.Name,
                TraderUserId = key.UserId, TraderSlot = key.Slot, TraderName = name,
                Order = OrganizationStockOrder.ExternalInvestment, Total = args.Amount,
                Flagged = reason.Length > 0, Reason = reason
            });
            if (_tradeLedger.Count > MaxTradeLedger)
            {
                var routine = _tradeLedger.FindIndex(t => !t.Flagged);
                _tradeLedger.RemoveAt(routine >= 0 ? routine : 0);
            }
        }))
        {
            if (!_bank.TryBankDeposit(actor, args.Amount))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"External investment refund failed: {key.UserId} {org.Id} {args.Amount}");
            Refresh(uid, actor, "Не вдалося зберегти внесок; перевірте баланс.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"External investment: {key.UserId} {org.Id} {args.Amount}");
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor, $"До скарбниці переказано {args.Amount:N0} $.");
    }

    // Signals are evidence for a human review, not automatic accusations.
    private string ClassifyMatchedTrade(Organization company, OrganizationLimitOrderRecord ask,
        OrganizationLimitOrderRecord bid, int quantity, int price, long proceeds, int previousQuote)
    {
        var recent = _tradeLedger.Where(t => t.Organization == company.Id &&
            t.Utc > DateTime.UtcNow - TimeSpan.FromMinutes(10)).ToArray();
        if (recent.Any(t => t.TraderUserId == bid.UserId && t.CounterpartyUserId == ask.UserId))
            return "III — Зворотні угоди між тими самими рахунками (перевірка узгоджених торгів)";
        if (Math.Abs((long)price - previousQuote) * 100 >= Math.Max(1, previousQuote) * 25L)
            return "II — Ціна угоди істотно відхиляється від котирування";
        if (recent.Count(t => t.TraderUserId == ask.UserId && t.CounterpartyUserId == bid.UserId) >= 3)
            return "II — Повторювані угоди між одними контрагентами";
        if (quantity * 100L >= company.SharesIssued * 5L || proceeds >= 5_000)
            return "I — Велика угода між акціонерами";
        return string.Empty;
    }
}
