using System.Text.Json;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;
using Robust.Shared.Utility;
using Content.Shared._Aspis.Organizations;

namespace Content.Server._Aspis.Organizations;

public sealed class OrganizationMemberRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public OrganizationMemberRole Role { get; set; }
    public string CustomTitle { get; set; } = string.Empty;
    public byte Distinction { get; set; }
    public Guid CustomRoleId { get; set; }
}

public sealed class OrganizationApplicationRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public int QuotedFee { get; set; }
}

public sealed class OrganizationShareholderRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Shares { get; set; }
}

// Funds are transferred out of the company treasury at accrual time, not at claim time.
// Pending marks a durable transfer intent; ambiguous bank outcomes require manual review.
// Pay is reserved from the treasury at accrual. Bank settlement follows the
// same two-phase, no-ambiguous-retry design as dividend accounts.
public sealed class OrganizationCustomRoleRecord
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public OrganizationPermission Permissions { get; set; }
}

public sealed class OrganizationPayrollAccountRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Available { get; set; }
    public int Pending { get; set; }
    public Guid PendingTransferId { get; set; }
    public long Paid { get; set; }
}

public sealed class OrganizationDividendAccountRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Available { get; set; }
    public int Pending { get; set; }
    public Guid PendingTransferId { get; set; }
    public long Paid { get; set; }
}

public sealed class OrganizationConcentrationRecord
{
    public DateTime Utc { get; set; }
    public Guid Organization { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class OrganizationTdfCaseActionRecord
{
    public DateTime Utc { get; set; }
    public string Officer { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class OrganizationTdfCaseRecord
{
    public Guid Id { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public Guid Organization { get; set; }
    public string Company { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public OrganizationTdfCaseStatus Status { get; set; }
    public string LastOfficer { get; set; } = string.Empty;
    public string DecisionNote { get; set; } = string.Empty;
    public List<OrganizationTdfCaseActionRecord> History { get; set; } = new();
}

public sealed class OrganizationTradeRecord
{
    public DateTime Utc { get; set; }
    public Guid Organization { get; set; }
    public string Company { get; set; } = string.Empty;
    public string TraderUserId { get; set; } = string.Empty;
    public int TraderSlot { get; set; }
    public string TraderName { get; set; } = string.Empty;
    public string CounterpartyUserId { get; set; } = string.Empty;
    public int CounterpartySlot { get; set; }
    public string CounterpartyName { get; set; } = string.Empty;
    public OrganizationStockOrder Order { get; set; }
    public int Quantity { get; set; }
    public int Price { get; set; }
    public long Total { get; set; }
    public bool Flagged { get; set; }
    public string Reason { get; set; } = string.Empty;
}

// A bid is funded before it becomes executable. In an ambiguous crash window it
// stays FundingPending until an administrator reconciles the bank transaction.
public enum OrganizationLimitOrderState : byte { FundingPending, Open }

public sealed class OrganizationLimitOrderRecord
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public OrganizationLimitOrderSide Side { get; set; }
    public OrganizationLimitOrderState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int Price { get; set; }
    public int Remaining { get; set; }
    // For bids, Remaining * Price must equal ReservedFunds at every saved state.
    public long ReservedFunds { get; set; }
}

// Company treasury, dividend reserves and player-to-player trading escrow are
// separate ledgers. Do not aggregate market proceeds into operating profit.
public sealed class OrganizationMarketCashRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Available { get; set; }
    public int Pending { get; set; }
    public Guid PendingTransferId { get; set; }
    public long Paid { get; set; }
}

// Voting rights are frozen at the time a proposal opens; trading shares cannot
// duplicate a ballot or revoke a vote already cast on that proposal.
public sealed class OrganizationVoteEligibleRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public int Shares { get; set; }
}

public sealed class OrganizationVoteBallotRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public bool Approve { get; set; }
}

public sealed class OrganizationVoteRecord
{
    public Guid Id { get; set; }
    public OrganizationVoteTopic Topic { get; set; }
    public int Value { get; set; }
    public OrganizationVoteState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public List<OrganizationVoteEligibleRecord> Eligible { get; set; } = new();
    public List<OrganizationVoteBallotRecord> Ballots { get; set; } = new();
}

// A tombstone is kept after all outstanding obligations have been settled.
// No client-visible account identifiers or private player details are stored here.
public sealed class OrganizationLiquidationRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime ClosedUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public int CancelledShares { get; set; }
}

// Permanent right registered after a verified field activity. The physical antenna
// remains round-local and is deliberately never serialized here.
public sealed class OrganizationFactionNodeRecord
{
    public Guid Id { get; set; }
    public string AssignmentCode { get; set; } = string.Empty;
    // Historical registration identifier; not the coordinates of a restored map.
    public string SiteCode { get; set; } = string.Empty;
    public OrganizationFactionNodeKind Kind { get; set; }
    public DateTime EstablishedUtc { get; set; }
}

// Durable survey result: no map UID, grid entity or coordinates survive a round.
public sealed class OrganizationFactionFindingRecord
{
    public Guid Id { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public DateTime RecordedUtc { get; set; }
}

public sealed class OrganizationRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public OrganizationKind Kind { get; set; }
    public OrganizationIndustry Industry { get; set; }
    public OrganizationFactionFocus FactionFocus { get; set; }
    // Optional in older saves. Primary focus remains available for migration.
    public OrganizationFactionFocus[] FactionFoci { get; set; } = [];
    public long FactionAuthorityPoints { get; set; }
    // Legacy JSON name retained so the v36 completion count still loads.
    public int FactionOperationsCompleted { get; set; }
    public List<OrganizationFactionNodeRecord> FactionNodes { get; set; } = new();
    public List<OrganizationFactionFindingRecord> FactionFindings { get; set; } = new();
    public bool IsPrivate { get; set; }
    public bool RosterPublic { get; set; } = true;
    public int PayrollPerMember { get; set; }
    public bool PayrollAutomatic { get; set; }
    public int PayrollProfitPercent { get; set; } = 30;
    public int PayrollLastCycle { get; set; }
    public int QuotaIncomeBonusPercent { get; set; }
    public int QuotaBonusCycles { get; set; }
    public List<OrganizationCustomRoleRecord> CustomRoles { get; set; } = new();
    public int LogisticsLevel { get; set; }
    public int ResearchFacilityLevel { get; set; }
    public int InfrastructureLevel { get; set; }
    public int ServiceNetworkLevel { get; set; }
    public int RiskManagementLevel { get; set; }
    public int LogisticsTier2Level { get; set; }
    public int ResearchTier2Level { get; set; }
    public int InfrastructureTier2Level { get; set; }
    public int ServiceNetworkTier2Level { get; set; }
    public int RiskManagementTier2Level { get; set; }
    public long PayrollAccrued { get; set; }
    public List<OrganizationPayrollAccountRecord> PayrollAccounts { get; set; } = new();
    public int EntryFee { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public string Description { get; set; } = string.Empty;
    public string DiscordLink { get; set; } = string.Empty;
    public byte[] Banner { get; set; } = [];
    public long Treasury { get; set; }
    public int InitialInvestment { get; set; }
    public int SectorInfluenceBp { get; set; }
    public int IndustryInfluenceBp { get; set; }
    public long CapitalSpent { get; set; }
    public long GrossRevenue { get; set; }
    public long OperatingCosts { get; set; }
    public DateTime SalesDateUtc { get; set; }
    public int DailyGrossRevenue { get; set; }
    public int[] BudgetShares { get; set; } = [25, 25, 25, 25];
    public OrganizationPermission DeputyPermissions { get; set; } = OrganizationPermission.Personnel |
        OrganizationPermission.Applications | OrganizationPermission.Budget;
    public OrganizationPermission OfficerPermissions { get; set; } = OrganizationPermission.Personnel | OrganizationPermission.Applications;
    public OrganizationPermission MemberPermissions { get; set; } = OrganizationPermission.None;
    public bool CrisisActive { get; set; }
    public int CrisisTicks { get; set; }
    public OrganizationInsolvencyState InsolvencyState { get; set; }
    public int MissedUpkeepCycles { get; set; }
    public int RecoveryCycles { get; set; }
    public int CrisisSectorBudgetBp { get; set; }
    public int CrisisIndustryBudgetBp { get; set; }
    public Guid OwnerMemberId { get; set; }
    public Guid DeputyMemberId { get; set; }
    public string OwnerTitle { get; set; } = "Власник";
    public string DeputyTitle { get; set; } = "Заступник";
    public string OfficerTitle { get; set; } = "Офіцер";
    public string MemberTitle { get; set; } = "Учасник";
    // Stored file is content-addressed outside registry JSON, avoiding a multi-MB registry.
    // A saved blueprint is not proof that a physical outpost exists this round.
    public string OutpostBlueprintName { get; set; } = string.Empty;
    public string OutpostBlueprintHash { get; set; } = string.Empty;
    public DateTime OutpostBlueprintUtc { get; set; }
    // Independently registered work-shuttle design. Round rentals themselves are transient.
    public string FleetBlueprintName { get; set; } = string.Empty;
    public string FleetBlueprintHash { get; set; } = string.Empty;
    public string FleetListingName { get; set; } = string.Empty;
    public string FleetListingDescription { get; set; } = string.Empty;
    public int FleetRentalPrice { get; set; } = OrganizationFleetRules.DefaultRentalPrice;
    // Purchased site references its own immutable, server-signed ship envelope.
    // Staging another envelope never changes the active outpost.
    public string OutpostPublicName { get; set; } = string.Empty;
    public string OutpostPublicDescription { get; set; } = string.Empty;
    public bool OutpostAllowVisitors { get; set; }
    public string OutpostActiveName { get; set; } = string.Empty;
    public string OutpostActiveHash { get; set; } = string.Empty;
    public DateTime OutpostPurchasedUtc { get; set; }
    public string OutpostReplacementName { get; set; } = string.Empty;
    public string OutpostReplacementHash { get; set; } = string.Empty;
    public DateTime OutpostReplacementRequestedUtc { get; set; }
    public int CompanyLevel { get; set; } = 1;
    public int CompanyExperience { get; set; }
    public OrganizationOperatingPolicy OperatingPolicy { get; set; } = OrganizationOperatingPolicy.Balanced;
    public int LastCycleGross { get; set; }
    public int LastCycleCosts { get; set; }
    public int LastCycleNet { get; set; }
    public int LastCycleDemand { get; set; }
    public int LastCycleOutcome { get; set; }
    public int CompletedEconomyCycles { get; set; }
    public DateTime LastEconomyUtc { get; set; }

    public List<OrganizationMemberRecord> Members { get; set; } = new();
    public List<OrganizationApplicationRecord> Applications { get; set; } = new();
    // Zero is the migration sentinel for companies created before the stock market.
    public bool SharesListed { get; set; }
    public int SharesIssued { get; set; }
    public int TreasuryShares { get; set; }
    public int Quote { get; set; }
    public List<int> PriceHistory { get; set; } = new();
    public List<OrganizationShareholderRecord> Shareholders { get; set; } = new();
    // Existing registries deserialize to the conservative default rate.
    public int DividendRatePercent { get; set; } = 20;
    public long DividendsDistributed { get; set; }
    public List<OrganizationDividendAccountRecord> DividendAccounts { get; set; } = new();
    public List<OrganizationLimitOrderRecord> MarketOrders { get; set; } = new();
    public List<OrganizationMarketCashRecord> MarketCash { get; set; } = new();
    public List<OrganizationVoteRecord> Votes { get; set; } = new();
}

public sealed class OrganizationTdfRightsRecord
{
    public string UserId { get; set; } = string.Empty;
    public int Slot { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public OrganizationTdfServiceRight Rights { get; set; }
}

public sealed class OrganizationDocument
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<OrganizationRecord> Organizations { get; set; } = new();
    public List<OrganizationLiquidationRecord> Liquidations { get; set; } = new();
    public DateTime DemandDateUtc { get; set; }
    public int DemandRemaining { get; set; }
    public int ActiveDemandCycles { get; set; } = -1;
    public long TdfTreasury { get; set; }
    public long TdfShuttleReserve { get; set; }
    public int TdfOperationalBp { get; set; }
    public int TdfLongTermBp { get; set; }
    public List<OrganizationTdfRightsRecord> TdfAssignedRights { get; set; } = new();

    // Durable, cross-round regulator ledger; separate from round entities.
    public List<OrganizationTradeRecord> TradeLedger { get; set; } = new();
    public List<OrganizationTdfCaseRecord> TdfCases { get; set; } = new();
    public List<OrganizationConcentrationRecord> ConcentrationAlerts { get; set; } = new();
}

public sealed class OrganizationStore(IResourceManager resources, ILogManager logs)
{
    private static readonly ResPath Directory = new("/Organizations");
    private static readonly ResPath First = Directory / "registry-a.json";
    private static readonly ResPath Second = Directory / "registry-b.json";
    private readonly ISawmill _log = logs.GetSawmill("organizations");
    private long _revision;
    public bool Available { get; private set; } = true;

    public OrganizationDocument Load()
    {
        var first = Read(First);
        var second = Read(Second);
        var latest = first != null && (second == null || first.Revision >= second.Revision) ? first : second;
        if (latest == null && (resources.UserData.Exists(First) || resources.UserData.Exists(Second)))
            Disable("Stored organization data is unreadable");
        _revision = latest?.Revision ?? 0;
        return latest ?? new OrganizationDocument();
    }

    private OrganizationDocument? Read(ResPath path)
    {
        try
        {
            // A new server has no registry files yet. Save() creates the directory on first write.
            if (!resources.UserData.Exists(path))
                return null;

            if (!resources.UserData.TryReadAllText(path, out var contents))
                return null;
            var document = JsonSerializer.Deserialize<OrganizationDocument>(contents);
            return document is { Version: 1, Revision: >= 0, Organizations: not null } ? document : null;
        }
        catch (Exception e)
        {
            _log.Error($"Unable to read organization data from {path}: {e}");
            return null;
        }
    }

    public void Disable(string reason)
    {
        Available = false;
        _log.Error($"Organization registry is read-only: {reason}");
    }

    public bool Save(OrganizationDocument document)
    {
        if (!Available)
            return false;

        try
        {
            resources.UserData.CreateDir(Directory);
            var next = _revision + 1;
            document.Revision = next;
            var json = JsonSerializer.Serialize(document);
            using (var writer = resources.UserData.OpenWriteText(next % 2 == 0 ? First : Second))
                writer.Write(json);
            _revision = next;
            return true;
        }
        catch (Exception e)
        {
            Available = false;
            _log.Error($"Unable to save organization data; registry is now read-only: {e}");
            return false;
        }
    }
}
