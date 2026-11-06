using Robust.Shared.Serialization;

namespace Content.Shared._Aspis.Organizations;

[Serializable, NetSerializable]
public enum OrganizationConsoleUiKey : byte
{
    Key
}

[Serializable, NetSerializable]
public enum OrganizationKind : byte
{
    Faction,
    Company
}

[Serializable, NetSerializable]
public enum OrganizationFactionFocus : byte
{
    None,
    Civic,
    Cultural,
    Religious,
    Political,
    Scientific,
    Humanitarian,
    Defense,
    Other,
    // Stable numeric IDs: existing saves from v17 keep their original primary focus.
    // One faction can choose up to three values from this fixed list.
    Technological = 9,
    Educational = 10,
    Environmental = 11,
    Diplomatic = 12,
    Exploration = 13,
    Colonial = 14,
    Commercial = 15,
    Industrial = 16,
    Medical = 17,
    Ideological = 18,
    Revolutionary = 19,
    Traditionalist = 20,
    Pacifist = 21,
    Militarist = 22,
    Labor = 23,
    Security = 24,
    Criminal = 25,
    Piracy = 26,
    Spiritual = 27,
    Agricultural = 28,
    Artistic = 29,
    Media = 30
}

[Serializable, NetSerializable]
public enum OrganizationIndustry : byte
{
    None,
    Delivery,
    Trade,
    Security,
    Services,
    Manufacturing,
    Research,
    Construction,
    Medicine,
    Mining,
    Agriculture
}

[Serializable, NetSerializable]
public enum OrganizationMemberRole : byte
{
    Member,
    Officer,
    Owner
}

[Serializable, NetSerializable]
public enum OrganizationPermissionTier : byte
{
    Owner,
    Deputy,
    Officer,
    Member
}

[Flags, Serializable, NetSerializable]
public enum OrganizationPermission : byte
{
    None = 0,
    TreasuryWithdraw = 1,
    Personnel = 2,
    Applications = 4,
    Settings = 8,
    Budget = 16,
    Strategy = 32,
    Investments = 64,
    All = TreasuryWithdraw | Personnel | Applications | Settings | Budget | Strategy | Investments
}

[Flags, Serializable, NetSerializable]
public enum OrganizationTdfServiceRight : byte
{
    None = 0,
    Audit = 1,
    FleetBudget = 2,
    Personnel = 4,
    All = Audit | FleetBudget | Personnel
}

[Serializable, NetSerializable]
public sealed class OrganizationTdfRightsMessage(Guid member, OrganizationTdfServiceRight rights)
    : BoundUserInterfaceMessage
{
    public Guid Member = member;
    public OrganizationTdfServiceRight Rights = rights;
}

[Serializable, NetSerializable]
public enum OrganizationOperatingPolicy : byte
{
    // Keep numeric values of existing policies: they are persisted in organization saves.
    Cautious,
    Balanced,
    Expansion,
    Optimization,
    Innovation
}

/// <summary>
/// Shared strategy coefficients for the authoritative economy. Every policy is
/// a trade-off: neither a guaranteed profit increase nor an unconditional bonus.
/// </summary>
public static class OrganizationPolicyRules
{
    public static double DemandMultiplier(OrganizationOperatingPolicy policy) => policy switch
    {
        OrganizationOperatingPolicy.Cautious => 0.88,
        OrganizationOperatingPolicy.Expansion => 1.13,
        OrganizationOperatingPolicy.Optimization => 0.93,
        OrganizationOperatingPolicy.Innovation => 0.97,
        _ => 1.0
    };

    public static double CostRateAdjustment(OrganizationOperatingPolicy policy) => policy switch
    {
        OrganizationOperatingPolicy.Cautious => -0.045,
        OrganizationOperatingPolicy.Expansion => 0.085,
        OrganizationOperatingPolicy.Optimization => -0.065,
        OrganizationOperatingPolicy.Innovation => 0.018,
        _ => 0.0
    };

    public static double IncidentMultiplier(OrganizationOperatingPolicy policy) => policy switch
    {
        OrganizationOperatingPolicy.Cautious => 0.60,
        OrganizationOperatingPolicy.Expansion => 1.65,
        OrganizationOperatingPolicy.Optimization => 1.12,
        OrganizationOperatingPolicy.Innovation => 1.08,
        _ => 1.0
    };

    public static int ExperienceBonus(OrganizationOperatingPolicy policy, int researchLevel) =>
        policy == OrganizationOperatingPolicy.Innovation
            ? Math.Min(7, 2 + Math.Max(0, researchLevel) / 2)
            : 0;
}

[Serializable, NetSerializable]
public enum OrganizationInsolvencyState : byte
{
    Active,
    UnderAdministration,
    Liquidation
}

[Serializable, NetSerializable]
public sealed class OrganizationLiquidationEntry(Guid id, string name, DateTime closedUtc, string reason)
{
    public Guid Id = id;
    public string Name = name;
    public DateTime ClosedUtc = closedUtc;
    public string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class OrganizationEntry(
    Guid id, string name, string owner, OrganizationKind kind, OrganizationIndustry industry,
    bool isPrivate, int entryFee, string color, int members, bool pending, float influencePercent,
    float industryPercent, long capitalSpent, bool crisisActive)
{
    public Guid Id = id;
    public string Name = name;
    public string Owner = owner;
    public OrganizationKind Kind = kind;
    public OrganizationIndustry Industry = industry;
    public OrganizationFactionFocus FactionFocus;
    public OrganizationFactionFocus[] FactionFoci = [];
    public bool IsPrivate = isPrivate;
    public bool RosterPublic = true;
    public int EntryFee = entryFee;
    public string Color = color;
    public int Members = members;
    public bool Pending = pending;
    public float InfluencePercent = influencePercent;
    public float IndustryPercent = industryPercent;
    public long CapitalSpent = capitalSpent;
    public bool CrisisActive = crisisActive;
    public OrganizationInsolvencyState InsolvencyState;
    public int MissedUpkeepCycles;
    public string Description = string.Empty;
    public string DiscordLink = string.Empty;
    // Either a legacy monochrome packed mark or a true-color PNG logo, normalized to 32x32.
    public byte[] Banner = [];
}

[Serializable, NetSerializable]
public sealed class OrganizationApplicationEntry(Guid id, string name, int fee)
{
    public Guid Id = id;
    public string Name = name;
    public int Fee = fee;
}

[Serializable, NetSerializable]
public sealed class OrganizationMemberEntry(Guid id, string name, OrganizationMemberRole role,
    bool isPresent, bool isDeputy, string title, byte distinction)
{
    public Guid Id = id;
    public string Name = name;
    public OrganizationMemberRole Role = role;
    public bool IsPresent = isPresent;
    public bool IsDeputy = isDeputy;
    public string Title = title;
    public byte Distinction = distinction;
    public Guid CustomRoleId;
    public OrganizationTdfServiceRight TdfRights;

}

[Serializable, NetSerializable]
public enum OrganizationVoteTopic : byte { Dividends, Strategy }

[Serializable, NetSerializable]
public enum OrganizationVoteState : byte { Open, Approved, Expired }

[Serializable, NetSerializable]
public sealed class OrganizationVoteEntry(Guid id, Guid company, OrganizationVoteTopic topic,
    int value, OrganizationVoteState state, DateTime expiresUtc, int yesShares,
    int noShares, int totalShares, bool canVote, bool voted)
{
    public Guid Id = id;
    public Guid Company = company;
    public OrganizationVoteTopic Topic = topic;
    public int Value = value;
    public OrganizationVoteState State = state;
    public DateTime ExpiresUtc = expiresUtc;
    public int YesShares = yesShares;
    public int NoShares = noShares;
    public int TotalShares = totalShares;
    public bool CanVote = canVote;
    public bool Voted = voted;
}

[Serializable, NetSerializable]
public sealed class OrganizationProposeVoteMessage(Guid organization, OrganizationVoteTopic topic, int value)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationVoteTopic Topic = topic;
    public int Value = value;
}

[Serializable, NetSerializable]
public sealed class OrganizationCastVoteMessage(Guid organization, Guid vote, bool approve)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Vote = vote;
    public bool Approve = approve;
}

[Serializable, NetSerializable]
public enum OrganizationStockOrder : byte { Buy, Sell, Issue }

[Serializable, NetSerializable]
public sealed class OrganizationStockEntry(Guid id, string name, string color, int quote,
    int issued, int available, int owned, float influence, int[] history)
{
    public Guid Id = id;
    public string Name = name;
    public string Color = color;
    public int Quote = quote;
    public int Issued = issued;
    public int Available = available;
    public int Owned = owned;
    // Shares not locked by the player's resting sell orders.
    public int UnreservedOwned;
    public float Influence = influence;
    public int[] History = history;
    public bool Listed;
    public bool TradingSuspended;
}

[Serializable, NetSerializable]
public sealed class OrganizationShareholderEntry(string name, int shares)
{
    public string Name = name;
    public int Shares = shares;
}

[Serializable, NetSerializable]
public sealed class OrganizationTradeAlertEntry(DateTime utc, string company, string actor, string description, bool flagged)
{
    public DateTime Utc = utc;
    public string Company = company;
    public string Actor = actor;
    public string Description = description;
    public bool Flagged = flagged;
    // Stable server-supplied key for flagged evidence only; never trust it as an authorization token.
    public string SourceKey = string.Empty;
}

[Serializable, NetSerializable]
public enum OrganizationTdfCaseStatus : byte { Open, Cleared, Referred }

[Serializable, NetSerializable]
public sealed class OrganizationTdfCaseEntry
{
    public Guid Id;
    public string SourceKey = string.Empty;
    public Guid Organization;
    public string Company = string.Empty;
    public string Subject = string.Empty;
    public string Reason = string.Empty;
    public DateTime CreatedUtc;
    public OrganizationTdfCaseStatus Status;
    public string LastOfficer = string.Empty;
    public string DecisionNote = string.Empty;
    public string[] History = [];
}

[Serializable, NetSerializable]
public sealed class OrganizationTdfOpenCaseMessage(string sourceKey) : BoundUserInterfaceMessage
{
    public string SourceKey = sourceKey;
}

[Serializable, NetSerializable]
public sealed class OrganizationTdfResolveCaseMessage(Guid caseId, OrganizationTdfCaseStatus status, string note)
    : BoundUserInterfaceMessage
{
    public Guid CaseId = caseId;
    public OrganizationTdfCaseStatus Status = status;
    public string Note = note;
}

[Serializable, NetSerializable]
public enum OrganizationTdfFundAction : byte
{
    Deposit,
    AllocateForShuttles,
    ReturnFromShuttles
}

[Serializable, NetSerializable]
public sealed class OrganizationTdfFundMessage(OrganizationTdfFundAction action, int amount)
    : BoundUserInterfaceMessage
{
    public OrganizationTdfFundAction Action = action;
    public int Amount = amount;
}

[Serializable, NetSerializable]
public enum OrganizationLimitOrderSide : byte { Buy, Sell }

[Serializable, NetSerializable]
public sealed class OrganizationLimitOrderEntry(Guid id, Guid organization, OrganizationLimitOrderSide side,
    int price, int quantity)
{
    public Guid Id = id;
    public Guid Organization = organization;
    public OrganizationLimitOrderSide Side = side;
    public int Price = price;
    public int Quantity = quantity;
    // Only the in-character display name is public. Never serialize UserId/Slot.
    public string TraderName = string.Empty;
    // Populated per recipient by the server; not a client-supplied authorization.
    public bool IsMine;
}

[Serializable, NetSerializable]
public sealed class OrganizationPlaceLimitOrderMessage(Guid organization, OrganizationLimitOrderSide side,
    int quantity, int price) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationLimitOrderSide Side = side;
    public int Quantity = quantity;
    public int Price = price;
}

[Serializable, NetSerializable]
public sealed class OrganizationCancelLimitOrderMessage(Guid organization, Guid order)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Order = order;
}

[Serializable, NetSerializable]
public sealed class OrganizationClaimMarketCashMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

[Serializable, NetSerializable]
public sealed class OrganizationStockMessage(Guid organization, OrganizationStockOrder order, int quantity)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationStockOrder Order = order;
    public int Quantity = quantity;
}

[Serializable, NetSerializable]
public sealed class OrganizationDividendPolicyMessage(Guid organization, int percent) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Percent = percent;
}

[Serializable, NetSerializable]
public sealed class OrganizationDividendClaimMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

[Serializable, NetSerializable]
public sealed class OrganizationStockListingMessage(Guid organization, int quantity) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Quantity = quantity;
}

/// <summary>
/// Private, recipient-addressed UI snapshot. The standard BUI component state is
/// shared by every viewer of the same terminal; never place personal balances,
/// TDF permissions, shareholder holdings or application lists in that state.
/// </summary>
[Serializable, NetSerializable]
public sealed class OrganizationPrivateStateMessage(OrganizationConsoleState state)
    : BoundUserInterfaceMessage
{
    public OrganizationConsoleState State = state;
}

[Serializable, NetSerializable]
public sealed class OrganizationConsoleState(
    OrganizationEntry[] organizations, Guid? membership, Guid? ownership,
    OrganizationMemberRole? role, OrganizationMemberEntry[] members,
    OrganizationApplicationEntry[] applications, string notice, long treasury, int personalBalance,
    int initialInvestment, int[] budgetShares) : BoundUserInterfaceState
{
    public OrganizationEntry[] Organizations = organizations;
    public OrganizationLiquidationEntry[] LiquidationArchive = [];
    public Guid? Membership = membership;
    public Guid? Ownership = ownership;
    public OrganizationMemberRole? Role = role;
    public OrganizationMemberEntry[] Members = members;
    public OrganizationApplicationEntry[] Applications = applications;
    public string Notice = notice;
    public long Treasury = treasury;
    public int PersonalBalance = personalBalance;
    public int InitialInvestment = initialInvestment;
    public int[] BudgetShares = budgetShares;
    public long GrossRevenue;
    public long OperatingCosts;
    public int DailyGrossRevenue;
    public int DailyDemandRemaining;
    public int DailyCompanyLimit;
    public bool CrisisActive;
    public int CrisisTicks;
    public OrganizationInsolvencyState InsolvencyState;
    public int MissedUpkeepCycles;
    public Guid? DeputyMemberId;
    public string OwnerTitle = "Власник";
    public string DeputyTitle = "Заступник";
    public string OfficerTitle = "Офіцер";
    public string MemberTitle = "Учасник";
    public int CompanyLevel = 1;
    // Separate from commercial SectorInfluenceBp and market capacity.
    public long FactionAuthorityPoints;
    public int FactionOperationsCompleted;
    public int FactionNodeCount;
    public int FactionFindingCount;
    public OrganizationFactionFindingEntry[] FactionFindings = [];
    public int FactionSignalSeconds = 40;
    // The competitive share of registered faction nodes, not a claim on every POI.
    public float FactionNetworkSharePercent;
    // Remaining time before another faction job may be accepted after completion.
    public int FactionNextTaskSeconds;
    public OrganizationFactionNodeEntry[] FactionNodes = [];
    public int CompanyExperience;
    public int NextLevelExperience;
    public bool CanManagePersonnel;
    public bool CanClaimLeadership;
    public int OnlineMembers;
    public OrganizationOperatingPolicy OperatingPolicy;
    public OrganizationPermission UserPermissions;
    public OrganizationPermission DeputyPermissions;
    public OrganizationPermission OfficerPermissions;
    public OrganizationPermission MemberPermissions;
    public int LastCycleGross;
    public int LastCycleCosts;
    public int LastCycleNet;
    public int LastCycleDemand;
    public int LastCycleOutcome;
    public int CompletedEconomyCycles;
    public bool EconomyRunning;
    public int NextEconomySeconds;
    public DateTime LastEconomyUtc;
    public int ActiveDemandCycles;
    public int MaxActiveDemandCycles;
    public OrganizationVoteEntry[] CorporateVotes = [];
    public OrganizationStockEntry[] Stocks = [];
    public OrganizationLimitOrderEntry[] LimitOrders = [];
    // Company share register is disclosed to its staff, not to arbitrary passers-by.
    public OrganizationShareholderEntry[] Shareholders = [];
    public bool TdfAuthorized;
    public OrganizationTradeAlertEntry[] TdfAlerts = [];
    public OrganizationTdfCaseEntry[] TdfCases = [];
    public bool TdfCanResolveCases;
    public bool ViewingAnotherCharacter;
    public string ViewedBody = string.Empty;
    public bool TdfOnDuty;
    public string TdfRank = string.Empty;
    public Guid? AssociatedOrganization;
    // Regulator information exists only on the dedicated TDF console.
    public bool TdfTerminal;
    public bool TdfCanManagePersonnel;
    public int TdfPersonnelInSector;
    public int TdfPersonnelConnected;
    public int TdfPersonnelInRoster;
    public long TdfTreasury;
    public long TdfShuttleReserve;
    public float TdfOperationalPercent;
    public float TdfLongTermPercent;
    public bool TdfCanAllocateBudget;
    public bool TdfCanAssignRights;
    public OrganizationTdfServiceRight MyTdfRights;
    public int DividendRatePercent;
    public long DividendsDistributed;
    public bool RosterPublic = true;
    public int PayrollPerMember;
    public bool PayrollAutomatic;
    public int PayrollProfitPercent = 30;
    public long PayrollAccrued;
    public OrganizationCustomRoleEntry[] CustomRoles = [];
    public int LogisticsLevel;
    public int ResearchFacilityLevel;
    public int InfrastructureLevel;
    public int ServiceNetworkLevel;
    public int RiskManagementLevel;
    public int LogisticsTier2Level;
    public int ResearchTier2Level;
    public int InfrastructureTier2Level;
    public int ServiceNetworkTier2Level;
    public int RiskManagementTier2Level;
    // A verified blueprint is a draft, NOT a paid or spawned outpost.
    public string OutpostBlueprintName = string.Empty;
    public DateTime OutpostBlueprintUtc;
    public bool OutpostBlueprintReady;
    public bool OutpostBlueprintIsActive;
    public bool OutpostOwned;
    public string OutpostPublicName = string.Empty;
    public string OutpostPublicDescription = string.Empty;
    public bool OutpostAllowVisitors;
    public string OutpostActiveName = string.Empty;
    public bool OutpostDeployed;
    public string OutpostDeploymentError = string.Empty;
    public string OutpostReplacementName = string.Empty;
    public DateTime OutpostReplacementReadyUtc;
    public long MyPayrollAvailable;
    public long MyPayrollPaid;
    public OrganizationQuotaEntry[] SectorQuotas = [];
    public int QuotaIncomeBonusPercent;
    public int QuotaBonusCycles;
    // A verified fleet blueprint is separate from the permanent outpost site.
    public string FleetBlueprintName = string.Empty;
    public string FleetListingName = string.Empty;
    public string FleetListingDescription = string.Empty;
    public int FleetRentalPrice = OrganizationFleetRules.DefaultRentalPrice;
    public string FleetRenterName = string.Empty;
    public bool FleetRenterIsMe;
    public bool FleetShuttleReturned;
    public bool FleetBlueprintReady;
    public bool FleetShuttleIssued;
    public bool FleetShuttlePresent;
    public string FleetIssuedName = string.Empty;
    public string FleetError = string.Empty;
    // Not sent to clients: individual dividend account balances are kept server-side.



}

[Serializable, NetSerializable]
public sealed class OrganizationCreateMessage(
    string name, OrganizationKind kind, OrganizationIndustry industry, bool isPrivate, int entryFee, string color,
    int investment, int marketing, int logistics, int security, int development) : BoundUserInterfaceMessage
{
    public string Name = name;
    public OrganizationKind Kind = kind;
    public OrganizationIndustry Industry = industry;
    public OrganizationFactionFocus FactionFocus;
    public OrganizationFactionFocus[] FactionFoci = [];
    public bool IsPrivate = isPrivate;
    public int EntryFee = entryFee;
    public string Color = color;
    public int Investment = investment;
    public bool RosterPublic = true;
    public int Marketing = marketing;
    public int Logistics = logistics;
    public int Security = security;
    public int Development = development;
    public string Description = string.Empty;
    public string DiscordLink = string.Empty;
    public byte[] Banner = [];
}

[Serializable, NetSerializable]
public sealed class OrganizationJoinMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

[Serializable, NetSerializable]
public sealed class OrganizationReviewMessage(Guid application, bool accept) : BoundUserInterfaceMessage
{
    public Guid Application = application;
    public bool Accept = accept;
}

[Serializable, NetSerializable]
public sealed class OrganizationConfigureMessage(Guid organization, bool isPrivate, int entryFee, string color) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public bool IsPrivate = isPrivate;
    public int EntryFee = entryFee;
    public string Color = color;
    public bool RosterPublic = true;
    // Optional owner-editor changes. Empty name / false UpdateBanner preserve existing values.
    public string Name = string.Empty;
    public bool UpdateBanner;
    public byte[] Banner = [];
}

[Serializable, NetSerializable]
public sealed class OrganizationPayrollPolicyMessage(Guid organization, int perMember) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int PerMember = perMember;
    public bool Automatic;
    public int ProfitPercent = 30;
}

[Serializable, NetSerializable]
public sealed class OrganizationSetRoleMessage(Guid organization, Guid member, OrganizationMemberRole role) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
    public OrganizationMemberRole Role = role;
}

[Serializable, NetSerializable]
public sealed class OrganizationTransferOwnershipMessage(Guid organization, Guid member) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
}

[Serializable, NetSerializable]
public sealed class OrganizationRemoveMemberMessage(Guid organization, Guid member) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
}

[Serializable, NetSerializable]
public sealed class OrganizationLeaveMessage() : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class OrganizationRefreshMessage() : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class OrganizationTreasuryMessage(Guid organization, int amount, bool deposit) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Amount = amount;
    public bool Deposit = deposit;
}

[Serializable, NetSerializable]
public enum OrganizationMarketScope : byte
{
    Sector,
    Industry
}

[Serializable, NetSerializable]
public enum OrganizationInvestmentFocus : byte
{
    Marketing,
    Logistics,
    Security,
    Development
}

[Serializable, NetSerializable]
public sealed class OrganizationInvestMessage(
    Guid organization, OrganizationMarketScope scope, OrganizationInvestmentFocus focus, int amount, Guid? target)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationMarketScope Scope = scope;
    public OrganizationInvestmentFocus Focus = focus;
    public int Amount = amount;
    public Guid? Target = target;
}


// All quota progress is round-local and backed by server cargo proofs.
// No player BUI button can complete a quota or mint the reward.
[Serializable, NetSerializable]
public enum OrganizationQuotaWorkflow : byte
{
    CargoDelivery,
    ResearchExport,
    AgriculturalExport,
    MiningExport,
    IndustrialExport,
    SectorService,
    // Real treatment verified after HealingSystem completes a medical procedure.
    MedicalTreatment,
    // Authenticated physical cargo-pallet sales of actual commercial products.
    TradeExport,
    // Keep previous workflow byte IDs stable for older versions and saved data.
    PharmaceuticalExport
}

// A faction's registered sector infrastructure (not a live spawned grid).
[Serializable, NetSerializable]
public enum OrganizationFactionNodeKind : byte
{
    PlanetaryRelay,
    ScientificRelay,
    DefenseRelay,
    ResearchDefenseRelay
}

public static class OrganizationFactionNodeRules
{
    public static string Name(OrganizationFactionNodeKind kind) => kind switch
    {
        OrganizationFactionNodeKind.ScientificRelay => "Науковий ретранслятор",
        OrganizationFactionNodeKind.DefenseRelay => "Оборонний ретранслятор",
        OrganizationFactionNodeKind.ResearchDefenseRelay => "Науково-оборонний ретранслятор",
        _ => "Планетарний вузол зв'язку"
    };
}

[Serializable, NetSerializable]
public sealed class OrganizationFactionNodeEntry(Guid id, string assignmentCode,
    OrganizationFactionNodeKind kind, DateTime establishedUtc)
{
    public Guid Id = id;
    public string AssignmentCode = assignmentCode;
    public OrganizationFactionNodeKind Kind = kind;
    public DateTime EstablishedUtc = establishedUtc;
    public string SiteCode = string.Empty;
    public bool ActiveThisRound;
    public string RoundLocation = string.Empty;
}

[Serializable, NetSerializable]
public sealed class OrganizationFactionFindingEntry(Guid id, string siteCode,
    string siteName, DateTime recordedUtc)
{
    public Guid Id = id;
    public string SiteCode = siteCode;
    public string SiteName = siteName;
    public DateTime RecordedUtc = recordedUtc;
}

[Serializable, NetSerializable]
public sealed class OrganizationFactionStepEntry
{
    public string Title = string.Empty;
    public string Details = string.Empty;
    public bool Completed;
}

[Serializable, NetSerializable]
public sealed class OrganizationQuotaEntry(int category, int slot, string title,
    string description, string material, int units, int incomeBonusPercent,
    int sectorInfluenceBp, bool reservedByUs, bool completed)
{
    public int Category = category;
    public int Slot = slot;
    public string Title = title;
    public string Description = description;
    public string Material = material;
    public int Units = units;
    public int IncomeBonusPercent = incomeBonusPercent;
    public int SectorInfluenceBp = sectorInfluenceBp;
    public bool ReservedByUs = reservedByUs;
    public bool Completed = completed;
    public bool SettlementPending;
    public string Code = string.Empty;
    public string AssignedTo = string.Empty;
    public bool AssignedToMe;
    // The same round-local offer (volume, deadline and post quotas) is shared by all competitors.
    public int SecondsRemaining;
    public int TimeLimitMinutes;
    public int EstimatedReward;
    public int Delivered;
    // Updated only by physical, server-verified pharmaceutical cargo sales.
    public string LastInspectionReport = string.Empty;
    public int LastInspectionQuality;
    // Rejected physical shipments are counted once by authenticated cargo reference.
    public int RejectedShipments;
    public int[] DeliveredByPost = [0, 0, 0, 0];
    public int[] PostTargets = [0, 0, 0, 0];
    public OrganizationQuotaWorkflow Workflow;
    // Legacy BUI field name retained for code compatibility; displayed as a faction task.
    public bool IsFactionOperation;
    public string FactionStageName = string.Empty;
    public OrganizationFactionStepEntry[] FactionSteps = [];
    public string FactionSiteLabel = string.Empty;
    public long FactionAuthorityPoints;
}

public static class OrganizationQuotaRules
{
    public const int SlotsPerCategory = 4;
    public const int BonusCycles = 3;
    public const int MaximumIncomeBonusPercent = 15;
    public static int Difficulty(int slot) => Math.Clamp(slot + 1, 1, 4);
    public static string DifficultyName(int slot) => Difficulty(slot) switch
    {
        1 => "Звичайне", 2 => "Підвищене", 3 => "Складне", _ => "Критичне"
    };
    // Round-local offers use these difficulty bands. A critical order may require 40 crates.
    public static int MinimumUnits(int slot) => slot switch { 0 => 3, 1 => 7, 2 => 12, _ => 20 };
    public static int MaximumUnits(int slot) => slot switch { 0 => 6, 1 => 12, 2 => 22, _ => 40 };
    // Compatibility for old UI/catalog callers. Server-generated offers use actual randomized Units.
    public static int Units(int slot) => slot switch { 0 => 4, 1 => 9, 2 => 16, _ => 30 };
    public static int TimeLimitForUnits(int slot, int units, int variation = 0) =>
        Math.Clamp(20 + units * 2 + Difficulty(slot) * 6 + Math.Clamp(variation, 0, 3) * 3, 30, 150);
    // Minimum qualifying CONTENT per physical export crate (not the number of
    // cargo crates). Also used by the server when validating nested inventory.
    public static int ExportItemsPerCrate(OrganizationQuotaWorkflow workflow, int slot) => workflow switch
    {
        OrganizationQuotaWorkflow.ResearchExport => 1 + slot / 2,
        OrganizationQuotaWorkflow.AgriculturalExport => 5 + slot * 3,
        OrganizationQuotaWorkflow.MiningExport => 15 + slot * 10,
        OrganizationQuotaWorkflow.IndustrialExport => 12 + slot * 8,
        OrganizationQuotaWorkflow.TradeExport => 8 + slot * 5,
        OrganizationQuotaWorkflow.PharmaceuticalExport => slot switch { 0 => 4, 1 => 6, 2 => 8, _ => 4 },
        _ => 0
    };
    public static int IncomeBonusPercent(int slot) => 3 + slot * 2;
    public static int InfluenceBasisPoints(int slot) => 2 + slot * 2;
    public static int TimeLimitMinutes(int slot) => TimeLimitForUnits(slot, Units(slot));
    public static int PostTarget(int slot, int post) => PostTargetForUnits(Units(slot), post);
    public static int PostTargetForUnits(int units, int post) => post is >= 0 and < 4
        ? units / 4 + (post < units % 4 ? 1 : 0) : 0;
    public static OrganizationQuotaWorkflow WorkflowFor(OrganizationKind kind, OrganizationIndustry industry)
    {
        if (kind == OrganizationKind.Faction)
            return OrganizationQuotaWorkflow.SectorService;
        return industry switch
        {
            OrganizationIndustry.Delivery => OrganizationQuotaWorkflow.CargoDelivery,
            OrganizationIndustry.Trade => OrganizationQuotaWorkflow.TradeExport,
            // Keep the persisted Medicine enum ID for existing companies.
            OrganizationIndustry.Medicine => OrganizationQuotaWorkflow.PharmaceuticalExport,
            OrganizationIndustry.Research => OrganizationQuotaWorkflow.ResearchExport,
            OrganizationIndustry.Agriculture => OrganizationQuotaWorkflow.AgriculturalExport,
            OrganizationIndustry.Mining => OrganizationQuotaWorkflow.MiningExport,
            OrganizationIndustry.Construction or OrganizationIndustry.Manufacturing => OrganizationQuotaWorkflow.IndustrialExport,
            _ => OrganizationQuotaWorkflow.SectorService
        };
    }
    // Curated candidate formulations. A name here does NOT mean it exists in a
    // particular fork: the server resolves each candidate against its loaded
    // ReagentPrototype catalog before an offer may use it. Pairs are consecutive
    // entries (index XOR 1); both must exist for the specialist-kit contract.
    // Indices 0 and 1 keep v33-v34's original Bicaridine/Dylovene identity.
    // Never accept reagent IDs from a PDA/BUI client.
    private static readonly (string Reagent, string Name, int Complexity)[] PharmacyFormulary =
    [
        ("Bicaridine", "Бікаридин", 0), ("Dylovene", "Диловен", 0),
        ("Tricordrazine", "Трикордразин", 1), ("Dexalin", "Дексалін", 1),
        ("Bruizine", "Бруїзин", 2), ("Lacerinol", "Лацеринол", 2),
        ("Dermaline", "Дермалін", 2), ("Arithrazine", "Аритразин", 2),
        ("Hyronalin", "Гіроналін", 2), ("PotassiumIodide", "Йодид калію", 2),
        ("Omnizine", "Омнізин", 3), ("Saline", "Фізіологічний розчин", 1),
        ("Cryoxadone", "Кріоксадон", 3), ("Aloxadone", "Алоксадон", 3),
        ("Puncturase", "Пунктураза", 2), ("Sigynate", "Сігінат", 2),
        ("Epinephrine", "Епінефрин", 2), ("Leporazine", "Лепоразин", 2),
        ("DexalinPlus", "Дексалін плюс", 3), ("Inaprovaline", "Інапровалін", 2)
    ];
    public static int PharmaceuticalFormulaCount => PharmacyFormulary.Length;
    public static int PharmaceuticalSecondaryVariant(int variant) =>
        Math.Clamp(variant, 0, PharmacyFormulary.Length - 1) ^ 1;
    public static string PharmaceuticalReagent(int variant) =>
        PharmacyFormulary[Math.Clamp(variant, 0, PharmacyFormulary.Length - 1)].Reagent;
    public static string PharmaceuticalMedicineName(int variant) =>
        PharmacyFormulary[Math.Clamp(variant, 0, PharmacyFormulary.Length - 1)].Name;
    public static int PharmaceuticalComplexity(int variant) =>
        PharmacyFormulary[Math.Clamp(variant, 0, PharmacyFormulary.Length - 1)].Complexity;
    // Extra fabrication time and pay are only awarded for advanced recipes,
    // with the real payout still subject to company/sector revenue caps.
    public static int PharmaceuticalExtraMinutes(int variant, int slot) =>
        slot == 0 ? 0 : PharmaceuticalComplexity(variant) * 8;
    public static int PharmaceuticalPremiumPercent(int variant, int slot) =>
        slot == 0 ? 0 : PharmaceuticalComplexity(variant) * 14;

    public static string PharmaceuticalTitle(int slot, int variant) => slot switch
    {
        0 => "Комплекти першої допомоги",
        1 => $"Шприци: {PharmaceuticalMedicineName(variant)}",
        2 => $"Пігулки: {PharmaceuticalMedicineName(variant)}",
        _ => $"Спеціалізовані аптечки: {PharmaceuticalMedicineName(variant)} + {PharmaceuticalMedicineName(PharmaceuticalSecondaryVariant(variant))}"
    };
    public static string PharmaceuticalDescription(int slot, int crates, int variant) => slot switch
    {
        0 => $"Поставте {crates} ящиків. У кожному 4 окремі аптечки з 4 бинтами всередині. Прикріпіть накладну до готового ящика.",
        1 => $"Поставте {crates} ящиків. У кожному 6 шприців із {PharmaceuticalMedicineName(variant)}: мінімум 5 одиниць препарату, чистота 90%. Пломбуйте після пакування.",
        2 => $"Поставте {crates} ящиків. У кожному коробка з 8 пігулками {PharmaceuticalMedicineName(variant)}: мінімум 5 одиниць на пігулку, чистота 95%. Пломбуйте після пакування.",
        _ => $"Поставте {crates} ящиків: по 4 аптечки, у кожній 4 бинти, шприц із {PharmaceuticalMedicineName(variant)} та 2 пігулки {PharmaceuticalMedicineName(PharmaceuticalSecondaryVariant(variant))}. Чистота 95%, якість від 75%. Прикріпіть накладну."
    };

    public static string WorkflowName(OrganizationQuotaWorkflow workflow) => workflow switch
    {
        OrganizationQuotaWorkflow.CargoDelivery => "Доставка через Cargo Requests та пости A-D",
        OrganizationQuotaWorkflow.ResearchExport => "Експорт технологій, маркованих ящиків",
        OrganizationQuotaWorkflow.AgriculturalExport => "Експорт аграрної продукції в ящиках",
        OrganizationQuotaWorkflow.MiningExport => "Експорт руди й ресурсів в ящиках",
        OrganizationQuotaWorkflow.IndustrialExport => "Постачання будівельних партій",
        OrganizationQuotaWorkflow.TradeExport => "Експорт торговельних товарів через Cargo",
        OrganizationQuotaWorkflow.MedicalTreatment => "Медична допомога: підтверджене лікування пацієнтів",
        OrganizationQuotaWorkflow.PharmaceuticalExport => "Експорт фармацевтичної продукції через Cargo",
        _ => "Планетарна експедиція фракції"
    };
    // Legacy helper remains for older UI/catalog paths; it is NOT proof of delivery.
    public static string Material(OrganizationKind kind, OrganizationIndustry industry, int focus)
    {
        if (kind == OrganizationKind.Faction)
            return (focus % 4) switch { 0 => "Cardboard", 1 => "Cloth", 2 => "Glass", _ => "Steel" };
        return industry switch
        {
            OrganizationIndustry.Delivery or OrganizationIndustry.Agriculture => "Cardboard",
            OrganizationIndustry.Trade or OrganizationIndustry.Research => "Glass",
            OrganizationIndustry.Security or OrganizationIndustry.Manufacturing or
                OrganizationIndustry.Construction or OrganizationIndustry.Mining => "Steel",
            OrganizationIndustry.Services or OrganizationIndustry.Medicine => "Cloth",
            _ => "Cardboard"
        };
    }
    public static string MaterialName(string material) => material switch
    {
        "Cardboard" => "картону", "Cloth" => "тканини", "Glass" => "скла",
        "Steel" => "сталі", _ => "матеріалів"
    };

    public static string FocusTitle(OrganizationFactionFocus focus) => focus switch
    {
        OrganizationFactionFocus.Civic => "Громадська ініціатива",
        OrganizationFactionFocus.Cultural => "Культурна програма",
        OrganizationFactionFocus.Religious => "Релігійна громада",
        OrganizationFactionFocus.Political => "Політична діяльність",
        OrganizationFactionFocus.Scientific => "Наукова програма",
        OrganizationFactionFocus.Humanitarian => "Гуманітарна місія",
        OrganizationFactionFocus.Defense => "Оборонна ініціатива",
        OrganizationFactionFocus.Technological => "Технологічний проєкт",
        OrganizationFactionFocus.Educational => "Освітня програма",
        OrganizationFactionFocus.Environmental => "Екологічна програма",
        OrganizationFactionFocus.Diplomatic => "Дипломатична місія",
        OrganizationFactionFocus.Exploration => "Дослідницька експедиція",
        OrganizationFactionFocus.Colonial => "Колонізаційний проєкт",
        OrganizationFactionFocus.Commercial => "Торговельна місія",
        OrganizationFactionFocus.Industrial => "Промислова програма",
        OrganizationFactionFocus.Medical => "Медична місія",
        OrganizationFactionFocus.Ideological => "Ідейна програма",
        OrganizationFactionFocus.Revolutionary => "Рух змін",
        OrganizationFactionFocus.Traditionalist => "Збереження традицій",
        OrganizationFactionFocus.Pacifist => "Миротворча місія",
        OrganizationFactionFocus.Militarist => "Військова програма",
        OrganizationFactionFocus.Labor => "Трудова спілка",
        OrganizationFactionFocus.Security => "Охорона сектору",
        OrganizationFactionFocus.Criminal => "Тіньова мережа",
        OrganizationFactionFocus.Piracy => "Вільні рейдери",
        OrganizationFactionFocus.Spiritual => "Духовна місія",
        OrganizationFactionFocus.Agricultural => "Аграрна ініціатива",
        OrganizationFactionFocus.Artistic => "Мистецька програма",
        OrganizationFactionFocus.Media => "Інформаційна мережа",
        _ => "Спільна ініціатива"
    };


    public static string Title(OrganizationKind kind, OrganizationIndustry industry, int category, int slot)
    {
        if (kind == OrganizationKind.Faction)
            return $"{FocusTitle((OrganizationFactionFocus)category)}: " +
                new[] { "Польове забезпечення", "Галузева експедиція", "Секторний проєкт", "Велика експедиція" }[slot];
        string[] names = industry switch
        {
            OrganizationIndustry.Delivery => ["Рейс постачання", "Маршрут торгових постів", "Міжстанційна логістика", "Великий конвой"],
            OrganizationIndustry.Research => ["Архів досліджень", "Передача технологій", "Патентний контракт", "Великий науковий експорт"],
            OrganizationIndustry.Agriculture => ["Партія врожаю", "Фермерський контракт", "Ботанічне постачання", "Аграрний конвой"],
            OrganizationIndustry.Mining => ["Поставка руди", "Ресурсний контракт", "Гірничий експорт", "Велика партія видобутку"],
            OrganizationIndustry.Construction => ["Будівельна поставка", "Ремонтна кампанія", "Інфраструктурний проєкт", "Контракт розбудови"],
            OrganizationIndustry.Trade => ["Поставка товарів", "Комерційний експорт", "Торговельна партія", "Великий торговельний контракт"],
            OrganizationIndustry.Medicine => ["Комплекти першої допомоги", "Фармацевтичні шприци", "Фасування пігулок", "Спеціалізовані аптечки"],
            _ => ["Секторне замовлення", "Галузевий контракт", "Пріоритетна робота", "Велике замовлення"]
        };
        return names[slot];
    }
    public static string Description(OrganizationKind kind, OrganizationIndustry industry, int category, int slot) =>
        DescriptionForUnits(kind, industry, category, slot, Units(slot));

    public static string DescriptionForUnits(OrganizationKind kind, OrganizationIndustry industry,
        int category, int slot, int crates)
    {
        var workflow = WorkflowFor(kind, industry);
        return workflow switch
        {
            OrganizationQuotaWorkflow.CargoDelivery =>
                $"Постачання {crates} промаркованих вантажних ящиків. Пункти призначення та кількість для кожного поста зазначено в маршруті.",
            OrganizationQuotaWorkflow.ResearchExport =>
                $"Підготуйте {crates} ящиків, по {ExportItemsPerCrate(workflow, slot)} технологічних або дослідницьких дисків у кожному. Прикріпіть накладну й здайте через Cargo.",
            OrganizationQuotaWorkflow.AgriculturalExport =>
                $"Поставте {crates} ящиків урожаю або насіння, не менше {ExportItemsPerCrate(workflow, slot)} одиниць на ящик. Потрібна накладна й продаж через Cargo.",
            OrganizationQuotaWorkflow.MiningExport =>
                $"Здайте {crates} ящиків з рудою, від {ExportItemsPerCrate(workflow, slot)} одиниць на ящик. Сумки всередині ящика дозволені. Прикріпіть накладну.",
            OrganizationQuotaWorkflow.IndustrialExport =>
                $"Підготуйте {crates} ящиків будматеріалів, від {ExportItemsPerCrate(workflow, slot)} одиниць на ящик. Прикріпіть накладну й здайте через Cargo.",
            OrganizationQuotaWorkflow.TradeExport =>
                $"Поставте {crates} ящиків товарів, не менше {ExportItemsPerCrate(workflow, slot)} одиниць на ящик. Вміст і накладну перевіряє Cargo.",
            OrganizationQuotaWorkflow.MedicalTreatment =>
                $"Надайте дієву медичну допомогу {crates} різним пацієнтам.",
            // Legacy callers use the first formula; real shared offers pass their
            // server-assigned variant to PharmaceuticalDescription directly.
            OrganizationQuotaWorkflow.PharmaceuticalExport => PharmaceuticalDescription(slot, crates, 0),
            _ => $"Виконайте секторну роботу ({crates} партій). Потрібне підтвердження від профільної ігрової системи."
        };
    }
}

[Serializable, NetSerializable]
public sealed class OrganizationClaimQuotaMessage(Guid organization, int category, int slot) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Category = category;
    public int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class OrganizationCompleteQuotaMessage(Guid organization, int category, int slot) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Category = category;
    public int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class OrganizationReleaseQuotaMessage(Guid organization, int category, int slot) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Category = category;
    public int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class OrganizationFulfillOrderMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}


[Serializable, NetSerializable]
public sealed class OrganizationClaimLeadershipMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

[Serializable, NetSerializable]
public enum OrganizationTitleSlot : byte
{
    Owner,
    Deputy,
    Officer,
    Member
}

[Serializable, NetSerializable]
public sealed class OrganizationSetTitleMessage(Guid organization, OrganizationTitleSlot slot, string title) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationTitleSlot Slot = slot;
    public string Title = title;
}

[Serializable, NetSerializable]
public sealed class OrganizationSetDeputyMessage(Guid organization, Guid member) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
}

[Serializable, NetSerializable]
public sealed class OrganizationConfigureBudgetMessage(Guid organization, int marketing, int operations, int security, int research) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public int Marketing = marketing;
    public int Operations = operations;
    public int Security = security;
    public int Research = research;
}

[Serializable, NetSerializable]
public sealed class OrganizationSetMemberTitleMessage(Guid organization, Guid member, string title) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
    public string Title = title;
}

[Serializable, NetSerializable]
public sealed class OrganizationSetOperatingPolicyMessage(Guid organization, OrganizationOperatingPolicy policy)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationOperatingPolicy Policy = policy;
}

[Serializable, NetSerializable]
public sealed class OrganizationSetPermissionsMessage(Guid organization, OrganizationPermissionTier tier, OrganizationPermission permissions)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationPermissionTier Tier = tier;
    public OrganizationPermission Permissions = permissions;
}

[Serializable, NetSerializable]
public sealed class OrganizationSetDistinctionMessage(Guid organization, Guid member, byte distinction)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
    public byte Distinction = distinction;
}

[Serializable, NetSerializable]
public enum OrganizationDevelopmentBranch : byte
{
    Logistics,
    Research,
    Infrastructure,
    ServiceNetwork,
    RiskManagement
}

/// <summary>
/// Authoritative facility progression. Shared by server validation and client pricing:
/// changing the balance here updates both views without separate hard-coded tables.
/// All market share thresholds use basis points (100 bp = 1%).
/// </summary>
public static class OrganizationDevelopmentRules
{
    public const int MaxLevel = 5;
    public const int MaxMastery = 100;
    public const int AdvancedMasteryUnlock = 16;
    public const int CompletedBranchesForAdvanced = 2;

    private static readonly int[] BasicMastery = [1, 3, 5, 8, 12];
    private static readonly int[] AdvancedMastery = [16, 20, 25, 31, 38];
    private static readonly int[] BasicInfluenceBp = [0, 0, 1, 3, 6];
    private static readonly int[] AdvancedInfluenceBp = [10, 15, 25, 40, 60];

    public static bool AdvancedUnlocked(int mastery, int completeBasicBranches) =>
        mastery >= AdvancedMasteryUnlock &&
        completeBasicBranches >= CompletedBranchesForAdvanced;

    public static long Price(OrganizationDevelopmentBranch branch, int currentLevel, int tier)
    {
        if (tier is < 1 or > 2 || currentLevel is < 0 or >= MaxLevel)
            return long.MaxValue;
        var basePrice = branch switch
        {
            OrganizationDevelopmentBranch.Logistics => 20_000L,
            OrganizationDevelopmentBranch.Research => 25_000L,
            OrganizationDevelopmentBranch.Infrastructure => 30_000L,
            OrganizationDevelopmentBranch.ServiceNetwork => 22_000L,
            OrganizationDevelopmentBranch.RiskManagement => 28_000L,
            _ => long.MaxValue / 100
        };
        return basePrice * (currentLevel + 1) * (currentLevel + 2) / 2 * (tier == 2 ? 3 : 1);
    }

    public static int MasteryRequired(int currentLevel, int tier) =>
        (tier == 2 ? AdvancedMastery : BasicMastery)[Math.Clamp(currentLevel, 0, MaxLevel - 1)];

    public static int SectorRequiredBp(int currentLevel, int tier) =>
        (tier == 2 ? AdvancedInfluenceBp : BasicInfluenceBp)[Math.Clamp(currentLevel, 0, MaxLevel - 1)];

    // Company facilities also make real, player-delivered contracts more viable.
    // Totals include both stages of the corresponding development branch.
    public static int ContractRevenueBonusPercent(int logisticsLevel, int serviceLevel) =>
        Math.Clamp(2 * logisticsLevel + 2 * serviceLevel, 0, 40);

    public static int ContractCostDiscountPercent(int infrastructureLevel, int riskLevel) =>
        Math.Clamp(2 * infrastructureLevel + riskLevel, 0, 30);

    public static int ExperienceNeeded(int currentMastery)
    {
        var level = Math.Clamp(currentMastery, 1, MaxMastery);
        return 12 + level * 3 + level * level / 25;
    }
}

[Serializable, NetSerializable]
public sealed class OrganizationDevelopmentMessage(Guid organization, OrganizationDevelopmentBranch branch)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationDevelopmentBranch Branch = branch;
    // 1 = basic company facilities, 2 = advanced facilities.
    public int Tier = 1;
}

[Serializable, NetSerializable]
public sealed class OrganizationCustomRoleEntry(Guid id, string title, OrganizationPermission permissions)
{
    public Guid Id = id;
    public string Title = title;
    public OrganizationPermission Permissions = permissions;
}

[Serializable, NetSerializable]
public sealed class OrganizationSaveCustomRoleMessage(Guid organization, Guid role, string title,
    OrganizationPermission permissions) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Role = role;
    public string Title = title;
    public OrganizationPermission Permissions = permissions;
}

[Serializable, NetSerializable]
public sealed class OrganizationAssignCustomRoleMessage(Guid organization, Guid member, Guid role)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public Guid Member = member;
    public Guid Role = role;
}

/// <summary>
/// Uploads a previously saved, server-signed ship blueprint to the organization registry.
/// Ownership and signature are checked again on the server. This never buys a site.
/// </summary>
[Serializable, NetSerializable]
public sealed class OrganizationStageOutpostBlueprintMessage(Guid organization, string name, string envelope)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public string Name = name;
    public string Envelope = envelope;
}

/// <summary>Purchase only after a verified grid exists and an FTL destination can be established.</summary>
[Serializable, NetSerializable]
public sealed class OrganizationPurchaseOutpostMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

/// <summary>Schedule a new verified blueprint for the next eligible round.</summary>
[Serializable, NetSerializable]
public sealed class OrganizationScheduleOutpostReplacementMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

[Serializable, NetSerializable]
public sealed class OrganizationCancelOutpostReplacementMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

/// <summary>Common prices and wait time, validated exclusively on the server.</summary>
[Serializable, NetSerializable]
public sealed class OrganizationConfigureOutpostListingMessage(Guid organization,
    string name, string description, bool allowVisitors) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public string Name = name;
    public string Description = description;
    public bool AllowVisitors = allowVisitors;
}

[Serializable, NetSerializable]
public sealed class OrganizationPrintOutpostToolMessage(Guid organization,
    OrganizationOutpostAccessTier tier, bool configurator) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public OrganizationOutpostAccessTier Tier = tier;
    public bool Configurator = configurator;
}

public static class OrganizationOutpostRules
{
    public const long SitePrice = 18_000_000;
    public const long ReplacementPrice = 5_000_000;
    public const int ReplacementWaitMinutes = 30;
}

[Serializable, NetSerializable]
public sealed class OrganizationRedeployOutpostMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

// Saved-ship envelope is uploaded directly from the client, never a client filesystem path.
/// <summary>
/// Blueprint envelopes can be several megabytes. Transfer bounded
/// plain-text fragments instead of a giant BUI message (sandbox-safe).
/// </summary>
public static class OrganizationBlueprintUploadRules
{
    public const int MaxEnvelopeCharacters = 12_000_000;
    public const int MaxTransferCharacters = MaxEnvelopeCharacters;
    public const int ChunkCharacters = 24_576;
    public const int MaxChunks = 768;
}

[Serializable, NetSerializable]
public enum OrganizationBlueprintUploadKind : byte
{
    Outpost,
    Fleet
}

[Serializable, NetSerializable]
public sealed class OrganizationBlueprintUploadChunkMessage : BoundUserInterfaceMessage
{
    public Guid Organization;
    public string Name = string.Empty;
    public Guid UploadId;
    public OrganizationBlueprintUploadKind Kind;
    public int Index;
    public int TotalChunks;
    public int OriginalLength;
    public string Data = string.Empty;

    public OrganizationBlueprintUploadChunkMessage(Guid organization, string name,
        Guid uploadId, OrganizationBlueprintUploadKind kind, int index,
        int totalChunks, int originalLength, string data)
    {
        Organization = organization;
        Name = name;
        UploadId = uploadId;
        Kind = kind;
        Index = index;
        TotalChunks = totalChunks;
        OriginalLength = originalLength;
        Data = data;
    }
}

[Serializable, NetSerializable]
public sealed class OrganizationStageFleetBlueprintMessage(Guid organization, string name, string envelope)
    : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public string Name = name;
    public string Envelope = envelope;
}

[Serializable, NetSerializable]
public sealed class OrganizationRentFleetShuttleMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

[Serializable, NetSerializable]
public sealed class OrganizationConfigureFleetListingMessage(Guid organization, string name,
    string description, int rentalPrice) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
    public string Name = name;
    public string Description = description;
    public int RentalPrice = rentalPrice;
}

[Serializable, NetSerializable]
public sealed class OrganizationReturnFleetShuttleMessage(Guid organization) : BoundUserInterfaceMessage
{
    public Guid Organization = organization;
}

public static class OrganizationFleetRules
{
    // Price paid by the renter from their own account; never minted by a damage claim.
    public const int DefaultRentalPrice = 18_000;
    public const int MinimumRentalPrice = 1_000;
    public const int MaximumRentalPrice = 20_000;
    public const int MaximumSimultaneousRentals = 8;
}
