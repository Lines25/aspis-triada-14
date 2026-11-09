using System.Linq;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

// Financial administration never writes to player banks. Reserved bids are
// returned to internal settlement wallets, and company deletion waits for claims.
public sealed partial class OrganizationSystem
{
    private const int MaxLiquidationArchive = 128;
    private const int AdministrationThreshold = 3;
    private const int LiquidationThreshold = 6;

    private static bool IsTradingSuspended(Organization company) =>
        company.InsolvencyState != OrganizationInsolvencyState.Active;

    private void OnUpkeepMissed(Organization company)
    {
        if (company.Kind != OrganizationKind.Company)
            return;

        company.MissedUpkeepCycles = Math.Min(1000, company.MissedUpkeepCycles + 1);
        company.RecoveryCycles = 0;
        if (company.MissedUpkeepCycles >= LiquidationThreshold)
        {
            company.InsolvencyState = OrganizationInsolvencyState.Liquidation;
            CloseOpenMarketOrders(company);
        }
        else if (company.MissedUpkeepCycles >= AdministrationThreshold &&
                 company.InsolvencyState == OrganizationInsolvencyState.Active)
        {
            company.InsolvencyState = OrganizationInsolvencyState.UnderAdministration;
            CloseOpenMarketOrders(company);
        }
    }

    private static void OnUpkeepPaid(Organization company)
    {
        if (company.Kind != OrganizationKind.Company)
            return;

        if (company.InsolvencyState == OrganizationInsolvencyState.UnderAdministration)
        {
            company.RecoveryCycles = Math.Min(2, company.RecoveryCycles + 1);
            if (company.RecoveryCycles >= 2 && company.Treasury >= MinWorkingCapital)
            {
                company.InsolvencyState = OrganizationInsolvencyState.Active;
                company.MissedUpkeepCycles = 0;
                company.RecoveryCycles = 0;
            }
        }
        else if (company.InsolvencyState == OrganizationInsolvencyState.Active)
        {
            company.MissedUpkeepCycles = 0;
            company.RecoveryCycles = 0;
        }
    }

    private static void CloseOpenMarketOrders(Organization company)
    {
        foreach (var order in company.MarketOrders.ToArray())
        {
            // A FundingPending bid can have an ambiguous bank outcome; never
            // clear it automatically or a crash could duplicate/misplace money.
            if (order.State != OrganizationLimitOrderState.Open)
                continue;

            if (order.Side == OrganizationLimitOrderSide.Buy)
            {
                var wallet = company.MarketCash.FirstOrDefault(w =>
                    w.UserId == order.UserId && w.Slot == order.Slot);
                if (wallet == null || wallet.Available > MaxTreasury - order.ReservedFunds)
                    continue;
                wallet.Available += order.ReservedFunds;
            }
            company.MarketOrders.Remove(order);
        }
    }

    private void TryFinalizeLiquidation(Organization company)
    {
        CloseOpenMarketOrders(company);
        if (company.Treasury != 0 || company.MarketOrders.Count != 0 ||
            company.DividendAccounts.Any(d => d.Available != 0 || d.Pending != 0) ||
            company.PayrollAccounts.Any(p => p.Available != 0 || p.Pending != 0) ||
            company.MarketCash.Any(w => w.Available != 0 || w.Pending != 0) ||
            company.Votes.Any(v => v.State == OrganizationVoteState.Open && v.ExpiresUtc > DateTime.UtcNow))
            return;

        // Shares are residual ownership, not a bank liability. With no assets and
        // all creditors settled, equity is worthless; keep a public tombstone.
        _liquidationArchive.Add(new OrganizationLiquidationRecord
        {
            Id = company.Id,
            Name = company.Name,
            ClosedUtc = DateTime.UtcNow,
            Reason = "Ліквідована через тривалу неплатоспроможність",
            CancelledShares = company.SharesIssued
        });
        while (_liquidationArchive.Count > MaxLiquidationArchive)
            _liquidationArchive.RemoveAt(0);
        _organizations.Remove(company.Id);
    }
}
