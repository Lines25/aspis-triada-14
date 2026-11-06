using System.Linq;
using Content.Shared.Database;

namespace Content.Server._Aspis.Organizations;

/// <summary>
/// Read-only financial reconciliation surveillance. An uncertain bank result must
/// never be replayed automatically: an administrator must compare the transfer
/// reference with durable bank records before releasing reserved balances.
/// </summary>
public sealed partial class OrganizationSystem
{
    private static readonly TimeSpan LedgerAuditInterval = TimeSpan.FromMinutes(2);
    private TimeSpan _nextLedgerAudit;
    private readonly HashSet<string> _knownUnsettledTransfers = new();

    private void AuditUnsettledFinancialTransfers()
    {
        if (!_store.Available)
            return;

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var company in _organizations.Values.Where(org => org.Kind ==
                     Content.Shared._Aspis.Organizations.OrganizationKind.Company))
        {
            foreach (var payment in company.DividendAccounts.Where(item => item.Pending > 0))
            {
                var reference = payment.PendingTransferId == Guid.Empty
                    ? $"MISSING-REFERENCE-{company.Id}-{payment.UserId}-{payment.Slot}"
                    : payment.PendingTransferId.ToString("N");
                var fingerprint = $"dividend:{company.Id}:{reference}";
                current.Add(fingerprint);
                if (_knownUnsettledTransfers.Add(fingerprint))
                    _adminLog.Add(LogType.Action, LogImpact.High,
                        $"ORGANIZATIONS LEDGER HOLD: dividend company={company.Id} transfer={reference} player={payment.UserId} slot={payment.Slot} amount={payment.Pending}. Check bank journal; do not retry automatically.");
            }

            foreach (var payment in company.MarketCash.Where(item => item.Pending > 0))
            {
                var reference = payment.PendingTransferId == Guid.Empty
                    ? $"MISSING-REFERENCE-{company.Id}-{payment.UserId}-{payment.Slot}"
                    : payment.PendingTransferId.ToString("N");
                var fingerprint = $"market-payout:{company.Id}:{reference}";
                current.Add(fingerprint);
                if (_knownUnsettledTransfers.Add(fingerprint))
                    _adminLog.Add(LogType.Action, LogImpact.High,
                        $"ORGANIZATIONS LEDGER HOLD: market payout company={company.Id} transfer={reference} player={payment.UserId} slot={payment.Slot} amount={payment.Pending}. Check bank journal; do not retry automatically.");
            }

            foreach (var order in company.MarketOrders.Where(item => item.State == OrganizationLimitOrderState.FundingPending))
            {
                var fingerprint = $"order-funding:{company.Id}:{order.Id:N}";
                current.Add(fingerprint);
                if (_knownUnsettledTransfers.Add(fingerprint))
                    _adminLog.Add(LogType.Action, LogImpact.High,
                        $"ORGANIZATIONS LEDGER HOLD: order funding company={company.Id} order={order.Id} player={order.UserId} slot={order.Slot} reserved={order.ReservedFunds}. Verify whether bank debit completed before changing order state.");
            }
        }

        // A resolved hold may reappear after a rollback or future restore. Report it again.
        _knownUnsettledTransfers.RemoveWhere(fingerprint => !current.Contains(fingerprint));
    }
}
