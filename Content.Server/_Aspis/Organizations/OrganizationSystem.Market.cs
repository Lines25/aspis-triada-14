using System.Linq;
using Content.Server.Administration.Logs;
using Content.Shared._NF.Bank.Components;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Shared.Radio;
using Robust.Shared.Prototypes;

namespace Content.Server._Aspis.Organizations;

// Stage 16: orders exchange reserved shares against reserved funds entirely inside
// one OrganizationStore commit. Bank IO is always separated by a durable intent.
public sealed partial class OrganizationSystem
{
    private const int MaxLimitOrders = 64;
    private const int MaxMarketCashAccounts = 256;
    private const int MaxMarketWithdrawal = 1_000_000;

    private static bool SameMarketOwner(OrganizationLimitOrderRecord order, CharacterKey key) =>
        order.UserId == key.UserId && order.Slot == key.Slot &&
        order.CharacterName == key.CharacterName;

    private static int ReservedAskShares(Organization company, CharacterKey key) =>
        company.MarketOrders.Where(o => o.Side == OrganizationLimitOrderSide.Sell &&
            SameMarketOwner(o, key)).Sum(o => o.Remaining);

    private static OrganizationMarketCashRecord? FindMarketWallet(Organization company, CharacterKey key) =>
        company.MarketCash.FirstOrDefault(w => w.UserId == key.UserId && w.Slot == key.Slot);

    private static OrganizationMarketCashRecord EnsureMarketWallet(Organization company, CharacterKey key, string name)
    {
        var wallet = FindMarketWallet(company, key);
        if (wallet != null)
            return wallet;
        wallet = new OrganizationMarketCashRecord { UserId = key.UserId, Slot = key.Slot, Name = name };
        company.MarketCash.Add(wallet);
        return wallet;
    }

    private static bool RestoreMarket(Organization company, OrganizationRecord record)
    {
        if (record.MarketOrders is null || record.MarketCash is null ||
            record.MarketOrders.Count > MaxLimitOrders || record.MarketCash.Count > MaxMarketCashAccounts ||
            record.MarketOrders.Any(o => o is null || o.Id == Guid.Empty ||
                !ValidIdentity(o.UserId, o.Slot, o.CharacterName) ||
                string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 128 ||
                !Enum.IsDefined(o.Side) || !Enum.IsDefined(o.State) ||
                o.CreatedUtc == default || o.Price is < 1 or > MaxQuote ||
                o.Remaining is < 1 or > MaxTradeQuantity ||
                (o.Side == OrganizationLimitOrderSide.Sell &&
                    (o.State != OrganizationLimitOrderState.Open || o.ReservedFunds != 0)) ||
                (o.Side == OrganizationLimitOrderSide.Buy &&
                    o.ReservedFunds != (long)o.Price * o.Remaining)) ||
            record.MarketOrders.Select(o => o.Id).Distinct().Count() != record.MarketOrders.Count ||
            record.MarketCash.Any(w => w is null || !Guid.TryParse(w.UserId, out _) ||
                w.Slot is < 0 or >= 256 || w.Available is < 0 or > MaxTreasury ||
                w.Pending is < 0 or > MaxMarketWithdrawal || w.Paid < 0 ||
                (w.Pending == 0) != (w.PendingTransferId == Guid.Empty)) ||
            record.MarketCash.Select(w => (w.UserId, w.Slot)).Distinct().Count() != record.MarketCash.Count)
            return false;

        // An ask locks shares without stripping beneficial ownership. The holder
        // still receives dividends until execution actually transfers the shares.
        if (record.MarketOrders.Where(o => o.Side == OrganizationLimitOrderSide.Sell)
            .GroupBy(o => (o.UserId, o.Slot))
            .Any(group => group.Sum(o => (long)o.Remaining) >
                company.Shareholders.Where(h => h.UserId == group.Key.UserId &&
                    h.Slot == group.Key.Slot).Sum(h => (long)h.Shares)))
            return false;

        // Every order has an escrow owner; future executions never need to create
        // a wallet mid-match, even when the owner is offline.
        if (record.MarketOrders.Any(o => !record.MarketCash.Any(w =>
                w.UserId == o.UserId && w.Slot == o.Slot)))
            return false;

        company.MarketOrders.AddRange(record.MarketOrders.Select(o => new OrganizationLimitOrderRecord
        {
            Id = o.Id, UserId = o.UserId, Slot = o.Slot, CharacterName = o.CharacterName,
            Name = o.Name, Side = o.Side, State = o.State,
            CreatedUtc = o.CreatedUtc, Price = o.Price,
            Remaining = o.Remaining, ReservedFunds = o.ReservedFunds
        }));
        company.MarketCash.AddRange(record.MarketCash.Select(w => new OrganizationMarketCashRecord
        {
            UserId = w.UserId, Slot = w.Slot, Name = w.Name, Available = w.Available,
            Pending = w.Pending, PendingTransferId = w.PendingTransferId, Paid = w.Paid
        }));
        return true;
    }

    private void OnPlaceLimitOrder(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationPlaceLimitOrderMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out var name) ||
            !_store.Available || !_organizations.TryGetValue(args.Organization, out var company) ||
            company.Kind != OrganizationKind.Company || !company.SharesListed ||
            IsTradingSuspended(company) ||
            !Enum.IsDefined(args.Side) || args.Quantity is < 1 or > MaxTradeQuantity ||
            args.Price is < 1 or > MaxQuote)
            return;

        var amount = (long)args.Quantity * args.Price;
        if (amount > int.MaxValue || company.MarketOrders.Count >= MaxLimitOrders ||
            (FindMarketWallet(company, key) == null && company.MarketCash.Count >= MaxMarketCashAccounts) ||
            company.MarketOrders.Any(o => o.State == OrganizationLimitOrderState.FundingPending &&
                SameMarketOwner(o, key)))
        {
            Refresh(uid, actor, "Ліміт заявок або розрахункова сума перевищені.");
            return;
        }
        var cooldownKey = (key.UserId, key.Slot);
        if (_stockCooldowns.TryGetValue(cooldownKey, out var last) &&
            _gameTiming.CurTime < last + StockCooldown)
        {
            Refresh(uid, actor, "Наступна заявка буде доступна через кілька секунд.");
            return;
        }

        var order = new OrganizationLimitOrderRecord
        {
            Id = Guid.NewGuid(), UserId = key.UserId, Slot = key.Slot,
            CharacterName = key.CharacterName, Name = name, Side = args.Side,
            State = args.Side == OrganizationLimitOrderSide.Buy
                ? OrganizationLimitOrderState.FundingPending : OrganizationLimitOrderState.Open,
            CreatedUtc = DateTime.UtcNow, Price = args.Price,
            Remaining = args.Quantity,
            ReservedFunds = args.Side == OrganizationLimitOrderSide.Buy ? amount : 0
        };

        if (args.Side == OrganizationLimitOrderSide.Sell)
        {
            var holder = company.Shareholders.FirstOrDefault(h =>
                h.UserId == key.UserId && h.Slot == key.Slot);
            if (holder == null || holder.Shares - ReservedAskShares(company, key) < args.Quantity)
            {
                Refresh(uid, actor, "Недостатньо вільних акцій для розміщення.");
                return;
            }
            var flagged = false;
            if (!Commit(() =>
                {
                    EnsureMarketWallet(company, key, name);
                    company.MarketOrders.Add(order);
                    flagged = MatchLimitOrders(company);
                }))
            {
                Refresh(uid, actor, "Заявку не збережено.");
                return;
            }
            PostMarketOrder(uid, actor, company, flagged, key);
            return;
        }

        if (!TryComp<BankAccountComponent>(actor, out var bank) || bank.Balance < amount)
        {
            Refresh(uid, actor, "Недостатньо коштів для забезпечення заявки на купівлю.");
            return;
        }

        // Persist intent before bank withdrawal: an ambiguous crash locks the bid
        // for reconciliation rather than creating unfunded shares or retrying a debit.
        if (!Commit(() =>
            {
                EnsureMarketWallet(company, key, name);
                company.MarketOrders.Add(order);
            }))
        {
            Refresh(uid, actor, "Не вдалося зареєструвати заявку.");
            return;
        }
        if (!_bank.TryBankWithdraw(actor, (int) amount))
        {
            if (!Commit(() => company.MarketOrders.Remove(order)))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Market funding intent needs reconciliation: order={order.Id}, company={company.Id}, actor={ToPrettyString(actor):player}");
            Refresh(uid, actor, "Банк відхилив резервування коштів.");
            return;
        }
        var matchingFlagged = false;
        if (!Commit(() =>
            {
                order.State = OrganizationLimitOrderState.Open;
                matchingFlagged = MatchLimitOrders(company);
            }))
        {
            // NEVER automatically retry this debit. The persisted intent must be
            // reconciled against the banking records by an administrator.
            _adminLog.Add(LogType.Action, LogImpact.High,
                $"Market escrow requires reconciliation: order={order.Id}, company={company.Id}, player={key.UserId}, amount={amount}");
            Refresh(uid, actor, "Кошти зарезервовано, але підтвердження заявки потребує адміністративної звірки.");
            return;
        }
        PostMarketOrder(uid, actor, company, matchingFlagged, key);
    }

    private void PostMarketOrder(EntityUid console, EntityUid actor, Organization company, bool flagged,
        CharacterKey key)
    {
        _stockCooldowns[(key.UserId, key.Slot)] = _gameTiming.CurTime;
        _audio.PlayEntity(MoneyConfirmSound, actor, console);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Limit order posted: org={company.Id}, actor={ToPrettyString(actor):player}");
        if (flagged && (!_recentTdfWarnings.TryGetValue(company.Id, out var time) ||
            _gameTiming.CurTime >= time + TimeSpan.FromMinutes(2)))
        {
            if (_prototypes.TryIndex<RadioChannelPrototype>("Nfsd", out var channel))
            {
                _radio.SendRadioMessage(console,
                    $"TDF: великий обсяг біржових угод у компанії {company.Name}. Перевірте журнал операцій.",
                    channel, console);
                _recentTdfWarnings[company.Id] = _gameTiming.CurTime;
            }
        }
        Refresh(console, actor, "Заявку зареєстровано.");
    }

    // Executed ONLY inside Commit. Counterparty cash and shares change together,
    // with no BankSystem call during settlement and no effect on company revenue.
    private bool MatchLimitOrders(Organization company)
    {
        var flagged = false;
        for (var i = 0; i < MaxLimitOrders; i++)
        {
            var bids = company.MarketOrders.Where(o => o.State == OrganizationLimitOrderState.Open &&
                o.Side == OrganizationLimitOrderSide.Buy)
                .OrderByDescending(o => o.Price).ThenBy(o => o.CreatedUtc).ToArray();
            var asks = company.MarketOrders.Where(o => o.State == OrganizationLimitOrderState.Open &&
                o.Side == OrganizationLimitOrderSide.Sell)
                .OrderBy(o => o.Price).ThenBy(o => o.CreatedUtc).ToArray();
            OrganizationLimitOrderRecord? bid = null;
            OrganizationLimitOrderRecord? ask = null;
            foreach (var candidate in bids)
            {
                var opposite = asks.FirstOrDefault(a => a.Price <= candidate.Price &&
                    a.UserId != candidate.UserId);
                if (opposite == null)
                    continue;
                bid = candidate;
                ask = opposite;
                break;
            }
            if (bid == null || ask == null)
                break;

            var quantity = Math.Min(bid.Remaining, ask.Remaining);
            var price = bid.CreatedUtc <= ask.CreatedUtc ? bid.Price : ask.Price;
            var proceeds = (long)price * quantity;
            var refund = (long)(bid.Price - price) * quantity;
            var sellerCash = company.MarketCash.First(w => w.UserId == ask.UserId && w.Slot == ask.Slot);
            var buyerCash = company.MarketCash.First(w => w.UserId == bid.UserId && w.Slot == bid.Slot);
            if (sellerCash.Available > MaxTreasury - proceeds ||
                buyerCash.Available > MaxTreasury - refund)
                break; // Protect wallet caps without destroying resting orders.

            sellerCash.Available += proceeds;
            buyerCash.Available += refund;
            bid.ReservedFunds -= (long)bid.Price * quantity;
            bid.Remaining -= quantity;
            ask.Remaining -= quantity;
            var sellerShares = company.Shareholders.First(h =>
                h.UserId == ask.UserId && h.Slot == ask.Slot);
            sellerShares.Shares -= quantity;
            if (sellerShares.Shares == 0)
                company.Shareholders.Remove(sellerShares);
            var shareholder = company.Shareholders.FirstOrDefault(h =>
                h.UserId == bid.UserId && h.Slot == bid.Slot);
            if (shareholder == null)
                company.Shareholders.Add(new OrganizationShareholderRecord
                {
                    UserId = bid.UserId, Slot = bid.Slot, Name = bid.Name, Shares = quantity
                });
            else
                shareholder.Shares += quantity;
            if (bid.Remaining == 0)
                company.MarketOrders.Remove(bid);
            if (ask.Remaining == 0)
                company.MarketOrders.Remove(ask);

            // A trade is a transfer of ownership, not a corporate sale or dividend.
            // One thinly traded package must not reset the entire price curve.
            var step = Math.Max(1, company.Quote / 20);
            company.Quote = Math.Clamp(price,
                Math.Max(1, company.Quote - step), Math.Min(MaxQuote, company.Quote + step));
            company.PriceHistory.Add(company.Quote);
            if (company.PriceHistory.Count > MaxPriceHistory)
                company.PriceHistory.RemoveAt(0);
            var alert = quantity * 100L >= company.SharesIssued * 5L || proceeds >= 5_000;
            flagged |= alert;
            _tradeLedger.Add(new OrganizationTradeRecord
            {
                Utc = DateTime.UtcNow, Organization = company.Id, Company = company.Name,
                TraderUserId = ask.UserId, TraderSlot = ask.Slot, TraderName = ask.Name,
                CounterpartyUserId = bid.UserId, CounterpartySlot = bid.Slot,
                CounterpartyName = bid.Name,
                Order = OrganizationStockOrder.Sell, Quantity = quantity, Price = price, Total = proceeds,
                Flagged = alert, Reason = alert ? "Велика угода між акціонерами" : string.Empty
            });
            if (_tradeLedger.Count > MaxTradeLedger)
            {
                var routine = _tradeLedger.FindIndex(t => !t.Flagged);
                _tradeLedger.RemoveAt(routine >= 0 ? routine : 0);
            }
        }
        return flagged;
    }

    private void OnCancelLimitOrder(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationCancelLimitOrderMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var company) || !_store.Available)
            return;
        var order = company.MarketOrders.FirstOrDefault(o => o.Id == args.Order);
        if (order == null || !SameMarketOwner(order, key) || order.State != OrganizationLimitOrderState.Open)
        {
            Refresh(uid, actor, "Цю заявку скасувати неможливо.");
            return;
        }
        var wallet = FindMarketWallet(company, key);
        if (wallet == null || (order.Side == OrganizationLimitOrderSide.Buy &&
                wallet.Available > MaxTreasury - order.ReservedFunds))
            return;
        if (!Commit(() =>
            {
                company.MarketOrders.Remove(order);
                if (order.Side == OrganizationLimitOrderSide.Buy)
                    wallet.Available += order.ReservedFunds;

            }))
        {
            Refresh(uid, actor, "Не вдалося скасувати заявку.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Limit order cancelled: org={company.Id}, order={order.Id}, player={key.UserId}");
        Refresh(uid, actor, order.Side == OrganizationLimitOrderSide.Buy
            ? "Заявку скасовано. Резерв доступний для отримання з біржі."
            : "Заявку скасовано. Акції знову доступні для торгів.");
    }

    private void OnClaimMarketCash(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationClaimMarketCashMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var company) || !_store.Available)
            return;
        var cash = FindMarketWallet(company, key);
        if (cash == null || cash.Pending != 0 || cash.Available <= 0)
        {
            Refresh(uid, actor, "Вільної виручки немає або попередня виплата потребує звірки.");
            return;
        }
        var amount = (int)Math.Min(cash.Available, MaxMarketWithdrawal);
        if (!TryComp<BankAccountComponent>(actor, out var bank) || bank.Balance > int.MaxValue - amount)
        {
            Refresh(uid, actor, "Рахунок не готовий до отримання коштів.");
            return;
        }
        var transferId = Guid.NewGuid();
        if (!Commit(() =>
            {
                cash.Available -= amount;
                cash.Pending = amount;
                cash.PendingTransferId = transferId;
            }))
        {
            Refresh(uid, actor, "Не вдалося зареєструвати виплату.");
            return;
        }
        if (!_bank.TryBankDeposit(actor, amount))
        {
            if (!Commit(() =>
                {
                    cash.Available += amount;
                    cash.Pending = 0;
                    cash.PendingTransferId = Guid.Empty;
                }))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Market payout rejected, intent stuck: org={company.Id}, transfer={transferId}, player={key.UserId}");
            Refresh(uid, actor, "Банк відхилив виплату. Якщо резерв заблоковано, потрібна звірка.");
            return;
        }
        if (!Commit(() =>
            {
                cash.Pending = 0;
                cash.PendingTransferId = Guid.Empty;
                cash.Paid += amount;
            }))
        {
            _adminLog.Add(LogType.Action, LogImpact.High,
                $"Market payout reconciliation required: org={company.Id}, transfer={transferId}, player={key.UserId}, amount={amount}");
            Refresh(uid, actor, "Гроші надіслано. Запис виплати потребує звірки.");
            return;
        }
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Market payout completed: org={company.Id}, player={key.UserId}, amount={amount}");
        Refresh(uid, actor, $"З біржі зараховано {amount:N0} $.");
    }
}
