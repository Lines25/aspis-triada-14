using System.Linq;
using Content.Shared.Mobs;
using Content.Shared.Ghost;
using Content.Server._NF.Bank;
using Content.Server.Mind;
using Content.Server.Roles.Jobs;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Radio;
using Robust.Shared.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Content.Shared.Hands.EntitySystems;
using Content.Shared._NF.Bank.Components;
using Content.Server.Administration.Logs;
using Content.Server.Preferences.Managers;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Database;
using Content.Shared.Preferences;
using Content.Shared.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Aspis.Organizations;

public sealed partial class OrganizationSystem : EntitySystem
{
    private const int MaxOrganizations = 128;
    private const int MaxMembers = 64;
    private const int MaxApplications = 128;
    private const int MaxEntryFee = 1_000_000;
    private const int MaxTreasuryTransfer = 100_000_000; // Separate from registration/entry fees.
    private const int MaxInitialInvestment = 1_000_000;
    // Registry fee is a one-time sink, not operating revenue or company capital.
    private const int ExitFeeCap = 10_000;
    private static readonly TimeSpan PostExitWait = TimeSpan.FromMinutes(15);
    private readonly Dictionary<CharacterKey, DateTime> _lastDepartureUtc = new();
    private const int RegistrationFee = 500_000;
    private const int PrivateRegistrationSurcharge = 75_000;
    private const int FactionRegistrationSurcharge = 100_000;

    private static int FoundingCost(OrganizationKind kind, bool isPrivate) =>
        RegistrationFee + (isPrivate ? PrivateRegistrationSurcharge : 0) +
        (kind == OrganizationKind.Faction ? FactionRegistrationSurcharge : 0);

    private static bool ValidDiscordLink(string link)
    {
        if (link.Length == 0) return true;
        return link.Length <= 240 && Uri.TryCreate(link, UriKind.Absolute, out var url) &&
            url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0 && url.IsDefaultPort &&
            ((url.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) &&
              url.AbsolutePath.StartsWith("/channels/", StringComparison.OrdinalIgnoreCase)) ||
             url.Host.Equals("discord.gg", StringComparison.OrdinalIgnoreCase));
    }

    // The client can submit arbitrary BUI bytes. Validate PNG signature and bounds on
    // the SERVER as well; retain legacy monochrome banners from older stages.
    private static bool ValidBanner(byte[]? banner)
    {
        if (banner == null)
            return false;
        if (banner.Length == 0)
            return true; // Rendered with the built-in Hampter fallback.

        var png = banner.Length >= 24 && banner[0] == 137 && banner[1] == 80 &&
            banner[2] == 78 && banner[3] == 71 && banner[4] == 13 &&
            banner[5] == 10 && banner[6] == 26 && banner[7] == 10;
        if (!png)
            return banner.Length is 128 or 512; // Previous 1-bit logos.
        if (banner.Length > 16_384 || banner[12] != 73 || banner[13] != 72 ||
            banner[14] != 68 || banner[15] != 82)
            return false;

        var width = (uint)banner[16] << 24 | (uint)banner[17] << 16 |
            (uint)banner[18] << 8 | banner[19];
        var height = (uint)banner[20] << 24 | (uint)banner[21] << 16 |
            (uint)banner[22] << 8 | banner[23];
        return width is >= 8 and <= 32 && height is >= 8 and <= 32;
    }

    private const long MaxTreasury = 1_000_000_000;
    private const int MarketCapacityBp = 10_000;
    private const int PeacefulExpansionBp = 1_800;
    private const int MaxInvestment = 100_000;
    private const int DailySectorDemand = 160_000;
    private const int DailyCompanyRevenueLimit = 24_000;
    private const int MinWorkingCapital = 200;
    private const int BaseCycleDemand = 6_500;
    private static readonly TimeSpan EconomyInterval = TimeSpan.FromMinutes(10);
    private const int DeliveryUnits = 5;
    private const int CrisisLossLimitPercent = 20;
    private static readonly TimeSpan UpkeepInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DeliveryCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan InvestmentCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BudgetChangeCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AdvertisementCooldown = TimeSpan.FromMinutes(2);
    private const int MaxCompanyLevel = OrganizationDevelopmentRules.MaxMastery;
    private const int AdvertisementCost = 10;
    private const int InitialShares = 1_000;
    private const int InitialTreasuryShares = 300; // Legacy Stage 11 reserve; private until owner lists.
    // TDF is an institutional authority, not a participant in the finite commercial influence pool.
    private const float TdfSectorMandatePercent = 35f;
    private const int MaxIssuedShares = 50_000;
    private const int MaxTradeQuantity = 100;
    private const int MaxQuote = 25_000;
    private const int MaxTradeLedger = 1_000;
    private const int MaxDividendPolicy = 30;
    private const int MaxDividendAccounts = 256;
    private const int MaxDividendClaim = 1_000_000;
    private const int MaxPriceHistory = 48;
    private static readonly TimeSpan StockCooldown = TimeSpan.FromSeconds(5);
    // Canonical sector authority is NOT an owner-controlled company.
    // Service roster follows the spawned bodies, not the currently attached network sessions.
    // The roster is reset with the round and never grants a permanent corporate role.
    private sealed class TdfDutyRecord(CharacterKey key, string name, EntityUid body, string rank, int grade)
    {
        public CharacterKey Key = key;
        public string Name = name;
        public EntityUid Body = body;
        public string Rank = rank;
        public int Grade = grade;
        public Guid Id = Guid.NewGuid();
    }

    private readonly Dictionary<CharacterKey, TdfDutyRecord> _tdfDutyRoster = new();
    // A character belongs to its SPAWNED BODY. The controller can ghost, possess and change
    // profiles without transferring corporate identity, ownership or TDF affiliation.
    // This dictionary is per shift; permanent ownership is still kept in OrganizationStore.
    private readonly Dictionary<EntityUid, CharacterKey> _identityByBody = new();
    private readonly Dictionary<EntityUid, string> _displayNameByBody = new();
    private readonly Dictionary<EntityUid, (string Rank, int Grade)> _tdfRankByBody = new();
    private readonly Dictionary<CharacterKey, OrganizationTdfServiceRight> _tdfAssignedRights = new();
    private long _tdfTreasury;
    private long _tdfShuttleReserve;
    // Separate from the finite 100% commercial market pool; never blocks other companies.
    private int _tdfOperationalBp;
    private int _tdfLongTermBp;
    private const int MaxTdfOperationalBp = 1500;
    private const int MaxTdfLongTermBp = 500;

    private static readonly Guid TdfAuthorityId = new("d4dd21c8-8d9c-4bf5-af71-cb1a3f8b3d99");
    private static readonly SoundSpecifier MoneyConfirmSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IServerNetManager _organizationProfileNet = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private BankSystem _bank = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private MindSystem _minds = default!;
    [Dependency] private JobSystem _jobs = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private ILogManager _log = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private Robust.Shared.Random.IRobustRandom _random = default!;
    [Dependency] private Content.Server.Chat.Systems.ChatSystem _chat = default!;

    private OrganizationStore _store = default!;
    private readonly Dictionary<Guid, Organization> _organizations = new();
    private readonly List<OrganizationLiquidationRecord> _liquidationArchive = new();
    private readonly Dictionary<Guid, TimeSpan> _recentInvestments = new();
    private readonly Dictionary<Guid, TimeSpan> _recentDeliveries = new();
    private readonly Dictionary<Guid, TimeSpan> _recentBudgetChanges = new();
    private TimeSpan _lastAdvertisement;
    private TimeSpan _nextAffiliationCheck;
    private TimeSpan _nextUpkeep;
    private TimeSpan _nextEconomyCycle;
    private readonly Dictionary<Guid, TimeSpan> _recentPolicies = new();
    private readonly Dictionary<Guid, TimeSpan> _recentWithdrawals = new();
    private readonly Dictionary<(string UserId, int Slot), TimeSpan> _stockCooldowns = new();
    private readonly List<OrganizationConcentrationRecord> _concentrationAlerts = new();
    private readonly List<OrganizationTradeRecord> _tradeLedger = new();
    private readonly List<OrganizationTdfCaseRecord> _tdfCases = new();
    private readonly Dictionary<Guid, TimeSpan> _recentTdfWarnings = new();
    private static readonly TimeSpan TreasuryWithdrawalCooldown = TimeSpan.FromSeconds(10);
    private DateTime _demandDateUtc;
    private int _demandRemaining;
    private int _activeDemandCycles;
    private const int MaxActiveDemandCycles = 12;

    private readonly record struct CharacterKey(string UserId, int Slot, string CharacterName);

    private sealed class Member(CharacterKey key, string name, OrganizationMemberRole role, Guid id)
    {
        public CharacterKey Key = key;
        public string Name = name;
        public OrganizationMemberRole Role = role;
        public Guid Id = id;
        public string CustomTitle = string.Empty;
        public byte Distinction;
        public Guid CustomRoleId;
    }

    private sealed class Application(Guid id, CharacterKey key, string name, int quotedFee)
    {
        public Guid Id = id;
        public CharacterKey Key = key;
        public string Name = name;
        public int QuotedFee = quotedFee;
    }

    private sealed class Organization(Guid id, string name, OrganizationKind kind, OrganizationIndustry industry,
        Member owner, bool isPrivate, int fee, string color)
    {
        public Guid Id = id;
        public string Name = name;
        public OrganizationKind Kind = kind;
        public OrganizationIndustry Industry = industry;
        public OrganizationFactionFocus FactionFocus;
        public OrganizationFactionFocus[] FactionFoci = [];
        public long FactionAuthorityPoints;
        // Kept under its v36 field name for backwards-compatible persistence.
        public int FactionOperationsCompleted;
        public readonly List<OrganizationFactionNodeRecord> FactionNodes = new();
        public readonly List<OrganizationFactionFindingRecord> FactionFindings = new();
        public CharacterKey Owner = owner.Key;
        public bool IsPrivate = isPrivate;
        public bool RosterPublic = true;
        public int PayrollPerMember;
        public bool PayrollAutomatic;
        public int PayrollProfitPercent = 30;
        public int PayrollLastCycle;
        public int QuotaIncomeBonusPercent;
        public int QuotaBonusCycles;
        public readonly List<OrganizationCustomRoleRecord> CustomRoles = new();
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
        public long PayrollAccrued;
        public readonly List<OrganizationPayrollAccountRecord> PayrollAccounts = new();
        public int EntryFee = fee;
        public string Color = color;
        public string Description = string.Empty;
        public string DiscordLink = string.Empty;
        public byte[] Banner = [];
        public long Treasury;
        public int InitialInvestment;
        public int SectorInfluenceBp;
        public int IndustryInfluenceBp;
        public long CapitalSpent;
        public long GrossRevenue;
        public long OperatingCosts;
        public DateTime SalesDateUtc;
        public int DailyGrossRevenue;
        public int[] BudgetShares = [25, 25, 25, 25];
        public OrganizationPermission DeputyPermissions = OrganizationPermission.Personnel |
            OrganizationPermission.Applications | OrganizationPermission.Budget;
        public OrganizationPermission OfficerPermissions = OrganizationPermission.Personnel | OrganizationPermission.Applications;
        public OrganizationPermission MemberPermissions = OrganizationPermission.None;
        public Guid DeputyMemberId;
        public string OwnerTitle = "Власник";
        public string DeputyTitle = "Заступник";
        public string OfficerTitle = "Офіцер";
        public string MemberTitle = "Учасник";
        public string OutpostBlueprintName = string.Empty;
        public string OutpostBlueprintHash = string.Empty;
        public DateTime OutpostBlueprintUtc;
        public string FleetBlueprintName = string.Empty;
        public string FleetBlueprintHash = string.Empty;
        public string FleetListingName = string.Empty;
        public string FleetListingDescription = string.Empty;
        public int FleetRentalPrice = OrganizationFleetRules.DefaultRentalPrice;
        public string OutpostPublicName = string.Empty;
        public string OutpostPublicDescription = string.Empty;
        public bool OutpostAllowVisitors;
        public string OutpostActiveName = string.Empty;
        public string OutpostActiveHash = string.Empty;
        public DateTime OutpostPurchasedUtc;
        public string OutpostReplacementName = string.Empty;
        public string OutpostReplacementHash = string.Empty;
        public DateTime OutpostReplacementRequestedUtc;
        public int CompanyLevel = 1;
        public int CompanyExperience;
        public OrganizationOperatingPolicy OperatingPolicy = OrganizationOperatingPolicy.Balanced;
        public int LastCycleGross;
        public int LastCycleCosts;
        public int LastCycleNet;
        public int LastCycleDemand;
        public int LastCycleOutcome;
        public int CompletedEconomyCycles;
        public DateTime LastEconomyUtc;
        public bool SharesListed;
        public int SharesIssued;
        public int TreasuryShares;
        public int Quote;
        public readonly List<int> PriceHistory = new();
        public readonly List<OrganizationShareholderRecord> Shareholders = new();
        public int DividendRatePercent = 20;
        public long DividendsDistributed;
        public readonly List<OrganizationDividendAccountRecord> DividendAccounts = new();
        public readonly List<OrganizationLimitOrderRecord> MarketOrders = new();
        public readonly List<OrganizationMarketCashRecord> MarketCash = new();
        public readonly List<OrganizationVoteRecord> Votes = new();
        public bool CrisisActive;
        public int CrisisTicks;
        public OrganizationInsolvencyState InsolvencyState;
        public int MissedUpkeepCycles;
        public int RecoveryCycles;
        public int CrisisSectorBudgetBp;
        public int CrisisIndustryBudgetBp;
        public readonly Dictionary<CharacterKey, Member> Members = new() { [owner.Key] = owner };
        public readonly Dictionary<Guid, Application> Applications = new();
    }

    public override void Initialize()
    {
        base.Initialize();
        _store = new OrganizationStore(_resources, _log);
        var document = _store.Load();
        Restore(document);
        // Character Setup is available in the lobby, before an in-world console exists.
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationProfileRequest>(OnOrganizationProfileRequest);
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationProfileResponse>();
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationOutpostLobbyRequest>(OnOrganizationOutpostLobbyRequest);
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationOutpostLobbyResponse>();
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationOutpostDirectoryRequest>(OnOrganizationOutpostDirectoryRequest);
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationOutpostDirectoryResponse>();
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationQuotaPdaRequest>(OnOrganizationQuotaPdaRequest);
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationQuotaPdaResponse>();
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationFactionTaskCompleted>();
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationQuotaChipPrintRequest>(OnQuotaChipPrintRequested);
        _organizationProfileNet.RegisterNetMessage<MsgOrganizationFactionCoordinatesPrintRequest>(OnFactionCoordinatesPrintRequested);
        _nextUpkeep = _gameTiming.CurTime + UpkeepInterval;
        _nextEconomyCycle = _gameTiming.CurTime + EconomyInterval;
        if (_organizations.Count != document.Organizations.Count)
            _store.Disable("One or more organization records failed validation");

        SubscribeLocalEvent<Content.Shared.Cargo.Components.CargoOrderConsoleComponent,
            Content.Shared.Interaction.InteractUsingEvent>(OnInsertQuotaChip);
        SubscribeLocalEvent<Content.Server.Cargo.Systems.EntitySoldEvent>(OnQuotaCratesSold);
        SubscribeLocalEvent<Content.Server.Shuttles.Events.FTLStartedEvent>(OnFactionWreckFTLStarted);
        SubscribeLocalEvent<Content.Server.Shuttles.Events.FTLCompletedEvent>(OnFactionWreckFTLCompleted);
        SubscribeLocalEvent<OrganizationQuotaCrateComponent, ComponentShutdown>(OnQuotaCrateRemoved);
        SubscribeLocalEvent<Content.Server.Storage.Components.EntityStorageComponent,
            Content.Shared.Interaction.InteractUsingEvent>(OnAttachQuotaExportInvoice);
        SubscribeLocalEvent<OrganizationConsoleComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationCreateMessage>(OnCreate);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationStageOutpostBlueprintMessage>(OnStageOutpostBlueprint);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationStageFleetBlueprintMessage>(OnStageFleetBlueprint);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationBlueprintUploadChunkMessage>(OnBlueprintUploadChunk);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationRentFleetShuttleMessage>(OnRentFleetShuttle);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationConfigureFleetListingMessage>(OnConfigureFleetListing);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationReturnFleetShuttleMessage>(OnReturnFleetShuttle);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationPurchaseOutpostMessage>(OnPurchaseOutpost);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationScheduleOutpostReplacementMessage>(OnScheduleOutpostReplacement);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationCancelOutpostReplacementMessage>(OnCancelOutpostReplacement);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationRedeployOutpostMessage>(OnRedeployOutpost);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationPrintOutpostToolMessage>(OnPrintOutpostTool);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationConfigureOutpostListingMessage>(OnConfigureOutpostListing);
        SubscribeLocalEvent<OrganizationOutpostDoorComponent, OrganizationOutpostAccessCheckEvent>(OnOrganizationReaderCheck);
        SubscribeLocalEvent<OrganizationOutpostConfiguratorComponent,
            Content.Shared.Interaction.InteractHandEvent>(OnOutpostConfiguratorCycle);
        SubscribeLocalEvent<OrganizationOutpostSpawnBeaconComponent,
            Content.Shared.Interaction.InteractHandEvent>(OnOutpostBeaconActivate);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationJoinMessage>(OnJoin);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationReviewMessage>(OnReview);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationConfigureMessage>(OnConfigure);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationLeaveMessage>(OnLeave);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationTreasuryMessage>(OnTreasury);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationInvestMessage>(OnInvest);
        // Legacy open-ended delivery disabled: all manual contract deliveries use sector quotas.
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationClaimQuotaMessage>(OnClaimQuota);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationCompleteQuotaMessage>(OnCompleteQuota);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationReleaseQuotaMessage>(OnReleaseQuota);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationClaimLeadershipMessage>(OnClaimLeadership);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetDeputyMessage>(OnSetDeputy);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetTitleMessage>(OnSetTitle);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetMemberTitleMessage>(OnSetMemberTitle);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationConfigureBudgetMessage>(OnConfigureBudget);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationPayrollPolicyMessage>(OnPayrollPolicy);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSaveCustomRoleMessage>(OnSaveCustomRole);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationAssignCustomRoleMessage>(OnAssignCustomRole);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationDevelopmentMessage>(OnDevelopment);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetOperatingPolicyMessage>(OnSetOperatingPolicy);
        SubscribeLocalEvent<Content.Server.Advertise.Components.AdvertiseComponent, Content.Server.Advertise.EntitySystems.AttemptAdvertiseEvent>(OnAttemptAdvertisement);
        SubscribeLocalEvent<OrganizationOwnerBindingComponent, MobStateChangedEvent>(OnOwnerMobState);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetPermissionsMessage>(OnSetPermissions);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetDistinctionMessage>(OnSetDistinction);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationStockMessage>(OnStockOrder);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationTdfFundMessage>(OnTdfFund);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationTdfRightsMessage>(OnTdfRights);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationTdfOpenCaseMessage>(OnTdfOpenCase);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationTdfResolveCaseMessage>(OnTdfResolveCase);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationStockListingMessage>(OnStockListing);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationDividendPolicyMessage>(OnDividendPolicy);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationDividendClaimMessage>(OnDividendClaim);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationPlaceLimitOrderMessage>(OnPlaceLimitOrder);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationCancelLimitOrderMessage>(OnCancelLimitOrder);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationClaimMarketCashMessage>(OnClaimMarketCash);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationProposeVoteMessage>(OnProposeShareholderVote);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationCastVoteMessage>(OnCastShareholderVote);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationSetRoleMessage>(OnSetRole);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationTransferOwnershipMessage>(OnTransferOwnership);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationRemoveMemberMessage>(OnRemoveMember);
        SubscribeLocalEvent<OrganizationConsoleComponent, OrganizationRefreshMessage>(OnRefresh);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnSpawn);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnAttached);
        SubscribeLocalEvent<OrganizationFactionFieldKitComponent,
            Content.Shared.Interaction.AfterInteractEvent>(OnFactionDeployAntenna);
        SubscribeLocalEvent<OrganizationFactionAntennaComponent,
            Content.Shared.Interaction.InteractUsingEvent>(OnFactionRepairAntenna);
        SubscribeLocalEvent<OrganizationFactionAntennaComponent,
            Content.Shared.Interaction.InteractHandEvent>(OnFactionActivateAntenna);
        SubscribeLocalEvent<OrganizationFactionSurveyBeaconComponent,
            Content.Shared.Interaction.InteractHandEvent>(OnFactionInspectSurveyBeacon);
        SubscribeLocalEvent<AspisFactionWreckRecorderComponent,
            Content.Shared.Interaction.InteractHandEvent>(OnFactionReadWreckRecorder);
        SubscribeLocalEvent<AspisFactionWreckInspectionComponent,
            Content.Shared.Interaction.InteractHandEvent>(OnFactionInspectWreckTerminal);
        SubscribeLocalEvent<AspisFactionWreckInspectionComponent,
            Content.Shared.Interaction.InteractUsingEvent>(OnFactionServiceWreckTerminal);
        SubscribeLocalEvent<AspisFactionWreckInspectionComponent,
            AspisFactionWreckToolCompletedEvent>(OnFactionWreckToolCompleted);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<RoundStartingEvent>(OnOutpostRoundStarting);

        // Alert administrators after registry restore, before any player can
        // attempt further withdrawals. Auditing never changes bank balances.
        _nextGovernanceSweep = _gameTiming.CurTime + TimeSpan.FromMinutes(1);
        _nextLedgerAudit = _gameTiming.CurTime + LedgerAuditInterval;
        _nextAutomaticDividendPayout = _gameTiming.CurTime + AutoDividendPayoutInterval;
        AuditUnsettledFinancialTransfers();
    }

    private TimeSpan _nextGovernanceSweep;

    private static CharacterKey Key(string userId, int slot, string name) =>
        new(userId, slot, name.Trim().ToUpperInvariant());

    private bool TryGetCharacter(EntityUid actor, out CharacterKey key, out string name)
    {
        key = default;
        name = string.Empty;
        if (!actor.Valid || Deleted(actor) || HasComp<GhostComponent>(actor) ||
            !_identityByBody.TryGetValue(actor, out key))
            return false;

        name = _displayNameByBody.TryGetValue(actor, out var originalName)
            ? originalName : key.CharacterName;
        return true;
    }

    // A visitor can VIEW the organization of the body they control, but bank debits,
    // share orders and management changes must never silently use the visitor's
    // *different* account or character preferences.
    private bool IsOriginalController(EntityUid actor)
    {
        if (!_identityByBody.TryGetValue(actor, out var identity) ||
            !_players.TryGetSessionByEntity(actor, out var session) ||
            !_preferences.TryGetCachedPreferences(session.UserId, out var prefs) ||
            prefs.SelectedCharacter is not HumanoidCharacterProfile profile)
            return false;
        return identity.UserId == session.UserId.ToString() &&
               identity.Slot == prefs.SelectedCharacterIndex &&
               identity.CharacterName == Key(identity.UserId, identity.Slot, profile.Name).CharacterName;
    }

    private Organization? FindMembership(CharacterKey key)
    {
        foreach (var organization in _organizations.Values)
            if (organization.Members.ContainsKey(key))
                return organization;
        return null;
    }

    private static bool TryNormalizeColor(string? value, out string color)
    {
        color = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
    }

    private static bool TryNormalizeName(string? value, out string name)
    {
        name = string.Join(" ", (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length is >= 3 and <= 32 && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'');
    }

    private static bool ValidShares(int[]? shares) =>
        shares is { Length: 4 } && shares.All(value => value is >= 5 and <= 85) && shares.Sum() == 100;

    // Never trust checkbox limits from the client. Enforce them when founding and restoring.
    // "Other" is kept only to read old saves; it cannot be selected for new factions.
    private static bool ValidFactionFoci(OrganizationFactionFocus[]? foci) =>
        foci is { Length: >= 1 and <= 3 } &&
        foci.All(f => f != OrganizationFactionFocus.None &&
                      f != OrganizationFactionFocus.Other && Enum.IsDefined(f)) &&
        foci.Distinct().Count() == foci.Length;

    private static OrganizationFactionFocus[] RestoreFactionFoci(OrganizationFactionFocus[]? saved,
        OrganizationFactionFocus legacy)
    {
        if (ValidFactionFoci(saved))
            return (OrganizationFactionFocus[])saved!.Clone();
        // Old factions only had one named focus. Do not reject existing v17 saves.
        if (legacy != OrganizationFactionFocus.None && Enum.IsDefined(legacy))
            return [legacy];
        return [];
    }

    private static bool ValidKind(OrganizationKind kind, OrganizationIndustry industry) =>
        Enum.IsDefined(kind) && Enum.IsDefined(industry) &&
        (kind == OrganizationKind.Faction && industry == OrganizationIndustry.None ||
         kind == OrganizationKind.Company && industry != OrganizationIndustry.None);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _gameTiming.CurTime;
        UpdateFactionLocators();
        if (now >= _nextQuotaSweep)
        {
            _nextQuotaSweep = now + TimeSpan.FromSeconds(30);
            ExpireSectorQuotas();
        }
        if (now >= _nextFactionSignalValidation)
        {
            _nextFactionSignalValidation = now + TimeSpan.FromSeconds(1);
            ValidateFactionSignals();
        }
        if (now >= _nextAutomaticPayrollPayout)
        {
            _nextAutomaticPayrollPayout = now + TimeSpan.FromMinutes(1);
            SettleAutomaticPayroll();
        }
        if (now >= _nextAutomaticDividendPayout)
        {
            _nextAutomaticDividendPayout = now + AutoDividendPayoutInterval;
            SettleAutomaticDividends();
        }
        if (now >= _nextGovernanceSweep)
        {
            _nextGovernanceSweep = now + TimeSpan.FromMinutes(1);
            ExpireOpenShareholderVotes();
        }
        if (now >= _nextLedgerAudit)
        {
            _nextLedgerAudit = now + LedgerAuditInterval;
            AuditUnsettledFinancialTransfers();
        }
        if (now >= _nextAffiliationCheck)
        {
            _nextAffiliationCheck = now + TimeSpan.FromSeconds(5);
            // Keep purchased and leased shuttle IFF in sync when membership,
            // organization color, or the controlled character changes.
            RefreshOrganizationShipIFF();
            foreach (var session in _players.Sessions)
            {
                if (session.Status != SessionStatus.InGame || session.AttachedEntity is not { } actor ||
                    !TryGetCharacter(actor, out var key, out _))
                    continue;
                var onDuty = IsTdfOfficer(actor);
                var markedTdf = TryComp<OrganizationAffiliationComponent>(actor, out var affiliation) &&
                    affiliation.OrganizationId == TdfAuthorityId.ToString("N");
                if (onDuty)
                    TrackTdfDuty(actor, key);
                if (onDuty != markedTdf)
                    UpdateAffiliation(actor, key);
            }
        }
        if (now < _nextEconomyCycle && now < _nextUpkeep)
            return;

        var activePlayers = _players.Sessions.Count(session =>
            session.Status == SessionStatus.InGame && session.AttachedEntity is { Valid: true });
        if (activePlayers < 1 || !_store.Available)
        {
            _nextEconomyCycle = now + EconomyInterval;
            _nextUpkeep = now + UpkeepInterval;
            return;
        }

        if (now >= _nextEconomyCycle)
        {
            _nextEconomyCycle = now + EconomyInterval;
            RunEconomyCycle(activePlayers);
            if (_tdfOperationalBp > 0)
                Commit(() => _tdfOperationalBp = Math.Max(0, _tdfOperationalBp - 8));
        }

        if (now >= _nextUpkeep)
        {
            _nextUpkeep = now + UpkeepInterval;
            RunUpkeep();
            CleanupOrphanedOrganizationOutposts();
        }
    }

    private void RunEconomyCycle(int activePlayers)
    {
        if (_activeDemandCycles >= MaxActiveDemandCycles)
        {
            if (!Commit(() =>
            {
                _activeDemandCycles = 0;
                _demandRemaining = DailySectorDemand;
                foreach (var org in _organizations.Values)
                    org.DailyGrossRevenue = 0;
            }))
                return;
        }

        var companies = _organizations.Values
            .Where(org => org.Kind == OrganizationKind.Company &&
                org.InsolvencyState != OrganizationInsolvencyState.Liquidation &&
                org.Treasury >= MinWorkingCapital)
            .OrderBy(org => org.Id)
            .ToArray();
        if (companies.Length == 0)
        {
            Commit(() => _activeDemandCycles++);
            return;
        }

        var today = DateTime.UtcNow.Date;
        var available = AvailableSectorDemand();
        if (available <= 0)
        {
            Commit(() => _activeDemandCycles++);
            return;
        }

        var volume = Math.Min(available, BaseCycleDemand + 100 * Math.Min(activePlayers, 12));
        var outsideWeight = 5.0 + 0.25 * Math.Max(0, 8 - activePlayers);
        var weights = new double[companies.Length];
        var weightTotal = outsideWeight;
        var marketWaves = new Dictionary<OrganizationIndustry, double>();
        foreach (var industry in Enum.GetValues<OrganizationIndustry>())
            marketWaves[industry] = _random.Next(85, 116) / 100.0;

        for (var i = 0; i < companies.Length; i++)
        {
            var org = companies[i];
            var operations = org.BudgetShares[1] / 100.0;
            var marketing = org.BudgetShares[0] / 100.0;
            var saturation = 1.0 - Math.Exp(-4.0 * operations);
            var reach = 1.0 + 0.26 * (1.0 - Math.Exp(-4.0 * marketing));
            var competition = 1.0 + _organizations.Values
                .Where(other => other.Id != org.Id && other.Industry == org.Industry)
                .Sum(other => other.IndustryInfluenceBp) / 4_000.0;
            var policyReach = OrganizationPolicyRules.DemandMultiplier(org.OperatingPolicy);
            var crisis = org.CrisisActive ? 0.80 : 1.0;
            weights[i] = Math.Max(0.05,
                (0.55 + org.IndustryInfluenceBp / 1_200.0 + org.SectorInfluenceBp / 2_000.0) *
                reach * (0.88 + 0.20 * saturation) *
                (1.0 + 0.038 * (org.LogisticsLevel + org.LogisticsTier2Level) +
                    0.024 * (org.InfrastructureLevel + org.InfrastructureTier2Level) +
                    0.026 * (org.ServiceNetworkLevel + org.ServiceNetworkTier2Level)) *
                (1.0 + 0.025 * (org.CompanyLevel - 1)) * policyReach * crisis /
                competition * marketWaves[org.Industry]);
            weightTotal += weights[i];
        }

        var grossAmounts = new int[companies.Length];
        var costAmounts = new int[companies.Length];
        var events = new int[companies.Length];
        var assignedDemand = 0;
        var earnedExperience = new int[companies.Length];
        for (var i = 0; i < companies.Length; i++)
        {
            var org = companies[i];
            var remainingCap = DailyCompanyRevenueLimit - CompanyRevenueInWindow(org);
            if (remainingCap <= 0)
                continue;

            var planned = (int) Math.Floor(volume * weights[i] / weightTotal);
            var gross = Math.Max(0, Math.Min(planned, Math.Min(remainingCap, volume - assignedDemand)));
            if (gross <= 0)
                continue;

            var operations = org.BudgetShares[1] / 100.0;
            var security = org.BudgetShares[2] / 100.0;
            var research = org.BudgetShares[3] / 100.0;
            var efficiency = 1.0 - Math.Exp(-3.8 * operations);
            var cooperation = Math.Sqrt(operations * research);
            var baseCostRate = 0.76 - 0.16 * efficiency - 0.04 * cooperation;
            baseCostRate -= 0.016 * (org.InfrastructureLevel + org.InfrastructureTier2Level) +
                0.009 * (org.ResearchFacilityLevel + org.ResearchTier2Level) +
                0.009 * (org.RiskManagementLevel + org.RiskManagementTier2Level);
            baseCostRate += OrganizationPolicyRules.CostRateAdjustment(org.OperatingPolicy);
            var costs = (int) Math.Ceiling(gross * Math.Clamp(baseCostRate, 0.38, 0.94)) + 25;
            var risk = 0.15 * Math.Exp(-3.5 * security);
            risk *= Math.Max(0.40, 1.0 - 0.075 * (org.RiskManagementLevel + org.RiskManagementTier2Level));
            risk *= OrganizationPolicyRules.IncidentMultiplier(org.OperatingPolicy);
            var outcome = 0;
            if (_random.Next(0, 10_000) < risk * 10_000)
            {
                costs += _random.Next(90, 401);
                outcome = 2;
            }
            else if (_random.Next(0, 10_000) < 800)
            {
                costs = Math.Max(0, costs - _random.Next(20, 111));
                outcome = 1;
            }

            // Contract quota bonuses increase REAL net profit, not only the UI.
            // The additional income is bounded by saved operating costs and the sector revenue cap.
            if (gross > costs && org.QuotaBonusCycles > 0 && org.QuotaIncomeBonusPercent > 0)
                costs = Math.Max(0, costs - Math.Min(costs,
                    (gross - costs) * org.QuotaIncomeBonusPercent / 100));
            var net = gross - costs;
            if (net > 0 && org.Treasury > MaxTreasury - net)
            {
                gross -= (int) Math.Min(net, org.Treasury + net - MaxTreasury);
                net = gross - costs;
            }
            if (net < 0 && -net > org.Treasury)
            {
                costs = gross + (int) org.Treasury;
                net = -(int) org.Treasury;
            }
            if (gross < 0 || costs < 0)
                continue;

            grossAmounts[i] = gross;
            costAmounts[i] = costs;
            events[i] = outcome;
            earnedExperience[i] = gross > 0
                ? Math.Min(8, Math.Max(1, gross / 500)) + org.BudgetShares[3] / 35 +
                  org.ResearchFacilityLevel + org.ResearchTier2Level +
                  OrganizationPolicyRules.ExperienceBonus(org.OperatingPolicy,
                      org.ResearchFacilityLevel + org.ResearchTier2Level)
                : 0;
            assignedDemand += gross;
        }
        if (assignedDemand <= 0)
        {
            Commit(() => _activeDemandCycles++);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (!Commit(() =>
        {
            _demandDateUtc = today;
            _demandRemaining = available - assignedDemand;
            _activeDemandCycles++;
            for (var i = 0; i < companies.Length; i++)
            {
                var gross = grossAmounts[i];
                if (gross <= 0)
                    continue;
                var org = companies[i];
                var cost = costAmounts[i];
                var previousRevenue = CompanyRevenueInWindow(org);
                org.SalesDateUtc = today;
                org.DailyGrossRevenue = previousRevenue + gross;
                org.GrossRevenue += gross;
                org.OperatingCosts += cost;
                org.Treasury += gross - cost;
                if (org.InsolvencyState == OrganizationInsolvencyState.Active)
                    AllocateDividends(org, gross - cost);
                org.LastCycleGross = gross;
                org.LastCycleCosts = cost;
                org.LastCycleNet = gross - cost;
                org.LastCycleDemand = volume;
                org.LastCycleOutcome = events[i];
                org.LastEconomyUtc = nowUtc;
                org.CompletedEconomyCycles = Math.Min(1_000_000, org.CompletedEconomyCycles + 1);
                if (org.QuotaBonusCycles > 0)
                {
                    org.QuotaBonusCycles--;
                    if (org.QuotaBonusCycles == 0)
                        org.QuotaIncomeBonusPercent = 0;
                }
                // Profit-linked wages belong to the same durable economy transaction.
                if (org.PayrollAutomatic)
                    AccruePayroll(org);
                EarnOrganicInfluence(org, gross - cost);
                UpdateQuote(org);
                if (org.CompanyLevel >= MaxCompanyLevel)
                    continue;
                org.CompanyExperience += earnedExperience[i];
                while (org.CompanyLevel < MaxCompanyLevel &&
                       org.CompanyExperience >= ExperienceNeeded(org.CompanyLevel))
                {
                    org.CompanyExperience -= ExperienceNeeded(org.CompanyLevel);
                    org.CompanyLevel++;
                }
            }
        }))
            return;

        foreach (var company in _organizations.Values.Where(o => o.Kind == OrganizationKind.Company))
            CheckConcentration(company);

        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Corporate sector cycle: {companies.Length} companies, demand volume={volume}, gross={assignedDemand}, players={activePlayers}");
    }

    private void OnSetOperatingPolicy(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationSetOperatingPolicyMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Strategy,
                out var actor, out var org) ||
            org.Kind != OrganizationKind.Company || !Enum.IsDefined(args.Policy))
            return;
        if (_recentPolicies.TryGetValue(org.Id, out var last) &&
            _gameTiming.CurTime < last + TimeSpan.FromMinutes(10))
        {
            Refresh(uid, actor, "Зміна стратегії буде доступна після завершення паузи.");
            return;
        }
        if (org.OperatingPolicy == args.Policy)
        {
            Refresh(uid, actor, "Цю стратегію вже обрано.");
            return;
        }
        if (!Commit(() => org.OperatingPolicy = args.Policy))
        {
            Refresh(uid, actor, "Не вдалося зберегти господарську стратегію.");
            return;
        }
        _recentPolicies[org.Id] = _gameTiming.CurTime;
        Refresh(uid, actor, "Господарську стратегію змінено.");
    }

    private void RunUpkeep()
    {
        if (!_organizations.Values.Any(o => o.Kind == OrganizationKind.Company ||
            o.SectorInfluenceBp > 0 || o.IndustryInfluenceBp > 0 || o.CrisisActive ||
            o.PayrollPerMember > 0))
            return;
        var previousArchiveIds = _liquidationArchive.Select(entry => entry.Id).ToHashSet();
        var previousStates = _organizations.Values.ToDictionary(org => org.Id, org => org.InsolvencyState);
        if (!Commit(() =>
        {
            foreach (var org in _organizations.Values.ToArray())
            {
                if (org.InsolvencyState == OrganizationInsolvencyState.Liquidation)
                {
                    TryFinalizeLiquidation(org);
                    continue;
                }
                var cost = OrganizationUpkeep(org);
                if (cost > 0)
                {
                    if (org.Treasury >= cost)
                    {
                        org.Treasury -= cost;
                        org.OperatingCosts += cost;
                        OnUpkeepPaid(org);
                    }
                    else
                    {
                        org.OperatingCosts += org.Treasury;
                        org.Treasury = 0;
                        OnUpkeepMissed(org);
                        org.SectorInfluenceBp = Math.Max(0, org.SectorInfluenceBp - Math.Max(1, org.SectorInfluenceBp / 75));
                        org.IndustryInfluenceBp = Math.Max(0, org.IndustryInfluenceBp - Math.Max(1, org.IndustryInfluenceBp / 75));
                    }
                }

                // Fixed payroll remains attached to the 15-minute upkeep cycle.
                // Automatic company payroll has already been reserved from its
                // actual profit within each 10-minute economy transaction.
                if (!org.PayrollAutomatic)
                    AccruePayroll(org);

                // A new living owner body resolves the leadership emergency.
                if (org.CrisisActive && IsMemberPresent(org.Owner))
                    EndCrisis(org);

                if (org.CrisisActive)
                {
                    org.CrisisTicks = Math.Min(org.CrisisTicks + 1, 1000);
                    if (org.CrisisTicks > 1)
                    {
                        var sectorLoss = Math.Min(org.CrisisSectorBudgetBp, Math.Min(org.SectorInfluenceBp,
                            Math.Max(1, org.SectorInfluenceBp / 100)));
                        var industryLoss = Math.Min(org.CrisisIndustryBudgetBp, Math.Min(org.IndustryInfluenceBp,
                            Math.Max(1, org.IndustryInfluenceBp / 100)));
                        org.SectorInfluenceBp -= sectorLoss;
                        org.IndustryInfluenceBp -= industryLoss;
                        org.CrisisSectorBudgetBp -= sectorLoss;
                        org.CrisisIndustryBudgetBp -= industryLoss;
                    }
                }
            }
        }))
            return;
        foreach (var company in _organizations.Values)
        {
            if (previousStates.TryGetValue(company.Id, out var previous) && previous != company.InsolvencyState)
                _adminLog.Add(LogType.Action, LogImpact.Medium,
                    $"Company insolvency transition: {company.Name} ({company.Id}), {previous} -> {company.InsolvencyState}");
        }
        foreach (var closed in _liquidationArchive.Where(entry => !previousArchiveIds.Contains(entry.Id)))
            _adminLog.Add(LogType.Action, LogImpact.High,
                $"Organization liquidated after insolvency: {closed.Name} ({closed.Id}), shares written off={closed.CancelledShares}");
    }

    private static void EndCrisis(Organization org)
    {
        org.CrisisActive = false;
        org.CrisisTicks = 0;
        org.CrisisSectorBudgetBp = 0;
        org.CrisisIndustryBudgetBp = 0;
    }

    private int OrganizationUpkeep(Organization org)
    {
        if (org.InsolvencyState == OrganizationInsolvencyState.Liquidation)
            return 0;
        var influence = org.SectorInfluenceBp + org.IndustryInfluenceBp;
        if (influence == 0)
            return org.Kind == OrganizationKind.Company ? 12 + org.Members.Count * 2 : 0;

        var upkeep = 6 + influence / 24 + org.Members.Count * 2;
        if (org.Kind != OrganizationKind.Company)
            return upkeep;

        upkeep += (org.CompanyLevel - 1) * 5;
        var protection = org.BudgetShares[2] / 100.0;
        var incidentChance = 0.18 * Math.Exp(-3.2 * protection);
        if (_random.Next(0, 10_000) < incidentChance * 10_000)
            upkeep += _random.Next(40, 320);
        return upkeep;
    }

    private static string DeliveryMaterial(OrganizationIndustry industry) => industry switch
    {
        OrganizationIndustry.Delivery => "Cardboard",
        OrganizationIndustry.Trade => "Glass",
        OrganizationIndustry.Security => "Steel",
        OrganizationIndustry.Services => "Cloth",
        OrganizationIndustry.Manufacturing => "Steel",
        OrganizationIndustry.Research => "Glass",
        OrganizationIndustry.Construction => "Steel",
        OrganizationIndustry.Medicine => "Cloth",
        OrganizationIndustry.Mining => "Steel",
        OrganizationIndustry.Agriculture => "Cardboard",
        _ => string.Empty
    };

    private static int DeliveryPrice(Organization org)
    {
        var baseRate = org.Industry switch
        {
            OrganizationIndustry.Delivery => 175,
            OrganizationIndustry.Trade => 200,
            OrganizationIndustry.Security => 275,
            OrganizationIndustry.Services => 190,
            OrganizationIndustry.Manufacturing => 280,
            OrganizationIndustry.Research => 235,
            OrganizationIndustry.Construction => 250,
            OrganizationIndustry.Medicine => 220,
            OrganizationIndustry.Mining => 260,
            OrganizationIndustry.Agriculture => 190,
            _ => 0
        };
        var operations = org.BudgetShares[1] / 100.0;
        var research = org.BudgetShares[3] / 100.0;
        var operationalGain = 1.0 + 0.32 * (1.0 - Math.Exp(-3.8 * operations));
        var cooperation = 1.0 + 0.10 * Math.Sqrt(operations * research);
        var levelBonus = 1.0 + 0.035 * (org.CompanyLevel - 1);
        var marketRate = baseRate + org.IndustryInfluenceBp / 5 + org.SectorInfluenceBp / 10;
        var facilityBonus = OrganizationDevelopmentRules.ContractRevenueBonusPercent(
            org.LogisticsLevel + org.LogisticsTier2Level,
            org.ServiceNetworkLevel + org.ServiceNetworkTier2Level);
        return Math.Clamp((int) Math.Round(marketRate * operationalGain * cooperation * levelBonus *
            (1.0 + facilityBonus / 100.0)), 0, 1_800);
    }

    private int DeliveryExpenses(Organization org)
    {
        var security = org.BudgetShares[2] / 100.0;
        var operations = org.BudgetShares[1] / 100.0;
        var baseCost = 25 + org.IndustryInfluenceBp / 30 + org.SectorInfluenceBp / 40;
        var overhead = (int) Math.Round(35.0 * operations * operations);
        var riskLevel = org.RiskManagementLevel + org.RiskManagementTier2Level;
        var operatingDiscount = OrganizationDevelopmentRules.ContractCostDiscountPercent(
            org.InfrastructureLevel + org.InfrastructureTier2Level, riskLevel);
        var operatingCost = Math.Max(15, (int) Math.Round((baseCost + overhead) *
            (1.0 - operatingDiscount / 100.0)));
        var incidentChance = Math.Clamp((0.20 * Math.Exp(-4.0 * security) +
            org.IndustryInfluenceBp / 100_000.0) * Math.Max(0.60, 1.0 - 0.04 * riskLevel),
            0.02, 0.25);
        var incident = _random.Next(0, 10_000) < incidentChance * 10_000
            ? _random.Next(40, 320)
            : 0;
        return operatingCost + incident;
    }

    private int AvailableSectorDemand() => _demandRemaining;

    private static int CompanyRevenueInWindow(Organization organization) =>
        organization.DailyGrossRevenue;

    private void OnFulfillOrder(EntityUid uid, OrganizationConsoleComponent comp, OrganizationFulfillOrderMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            !org.Members.ContainsKey(key) || org.Kind != OrganizationKind.Company)
            return;

        if (!_store.Available || org.IndustryInfluenceBp < 25)
        {
            Refresh(uid, actor, "Потрібно щонайменше 0,25% галузевого впливу та доступне сховище.");
            return;
        }

        if (_recentDeliveries.TryGetValue(org.Id, out var previous) &&
            _gameTiming.CurTime < previous + DeliveryCooldown)
        {
            Refresh(uid, actor, "Замовлення цієї компанії ще обробляється.");
            return;
        }

        if (!_hands.TryGetActiveItem((actor, null), out var item) ||
            !TryComp<StackComponent>(item.Value, out var stack) || stack.Unlimited ||
            stack.Count < DeliveryUnits || stack.StackTypeId.ToString() != DeliveryMaterial(org.Industry))
        {
            Refresh(uid, actor, $"Для замовлення потрібно тримати в руці {DeliveryUnits} од. матеріалу {DeliveryMaterial(org.Industry)}.");
            return;
        }

        _recentDeliveries[org.Id] = _gameTiming.CurTime;
        var today = DateTime.UtcNow.Date;
        var demand = AvailableSectorDemand();
        var sold = CompanyRevenueInWindow(org);
        var crisisPenalty = org.CrisisActive ? 85 : 100;
        var marketFluctuation = _random.Next(80, 121);
        var gross = Math.Min(DeliveryPrice(org) * crisisPenalty * marketFluctuation / 10_000,
            Math.Min(demand, DailyCompanyRevenueLimit - sold));
        var costs = DeliveryExpenses(org);
        if (gross <= costs || org.Treasury > MaxTreasury - (gross - costs))
        {
            Refresh(uid, actor, "Замовлення недоступне: ліміт попиту, витрати або місткість скарбниці.");
            return;
        }

        if (!Commit(() =>
        {
            _demandDateUtc = today;
            _demandRemaining = demand - gross;
            org.SalesDateUtc = today;
            org.DailyGrossRevenue = sold + gross;
            org.GrossRevenue += gross;
            org.OperatingCosts += costs;
            org.Treasury += gross - costs;
        }))
        {
            Refresh(uid, actor, "Не вдалося зафіксувати замовлення. Матеріали не списано.");
            return;
        }

        if (!_stacks.Use(item.Value, DeliveryUnits, stack))
        {
            if (!Commit(() =>
            {
                _demandRemaining += gross;
                org.DailyGrossRevenue -= gross;
                org.GrossRevenue -= gross;
                org.OperatingCosts -= costs;
                org.Treasury -= gross - costs;
            }))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Organization {org.Id} supply compensation could not be persisted");
            Refresh(uid, actor, "Не вдалося списати матеріали. Операцію скасовано.");
            return;
        }

        if (org.CompanyLevel < MaxCompanyLevel)
        {
            var earned = 1 + org.BudgetShares[3] / 35;
            Commit(() =>
            {
                org.CompanyExperience += earned;
                if (org.CompanyExperience >= ExperienceNeeded(org.CompanyLevel))
                {
                    org.CompanyExperience -= ExperienceNeeded(org.CompanyLevel);
                    org.CompanyLevel++;
                }
            });
        }
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(actor):player} supplied {DeliveryUnits} {DeliveryMaterial(org.Industry)} to {org.Name}, gross={gross}, costs={costs}");
        Refresh(uid, actor, $"Постачання виконано: виручка {gross:N0} $, витрати {costs:N0} $, чистий дохід {gross - costs:N0} $.");
    }

    private OrganizationDocument Snapshot()
    {
        var document = new OrganizationDocument
        {
            DemandDateUtc = _demandDateUtc,
            DemandRemaining = _demandRemaining,
            ActiveDemandCycles = _activeDemandCycles,
            TdfTreasury = _tdfTreasury,
            TdfShuttleReserve = _tdfShuttleReserve,
            TdfOperationalBp = _tdfOperationalBp,
            TdfLongTermBp = _tdfLongTermBp,
            Liquidations = _liquidationArchive.Select(entry => new OrganizationLiquidationRecord
            {
                Id = entry.Id, Name = entry.Name, ClosedUtc = entry.ClosedUtc,
                Reason = entry.Reason, CancelledShares = entry.CancelledShares
            }).ToList(),
            TdfAssignedRights = _tdfAssignedRights.Select(pair => new OrganizationTdfRightsRecord
            {
                UserId = pair.Key.UserId, Slot = pair.Key.Slot, CharacterName = pair.Key.CharacterName, Rights = pair.Value
            }).ToList(),
            ConcentrationAlerts = _concentrationAlerts.Select(a => new OrganizationConcentrationRecord
            {
                Utc = a.Utc, Organization = a.Organization, Company = a.Company, Reason = a.Reason
            }).ToList(),
            TdfCases = _tdfCases.Select(c => new OrganizationTdfCaseRecord
            {
                Id = c.Id, SourceKey = c.SourceKey, Organization = c.Organization,
                Company = c.Company, Subject = c.Subject, Reason = c.Reason,
                CreatedUtc = c.CreatedUtc, Status = c.Status,
                LastOfficer = c.LastOfficer, DecisionNote = c.DecisionNote,
                History = c.History.Select(a => new OrganizationTdfCaseActionRecord
                {
                    Utc = a.Utc, Officer = a.Officer, Description = a.Description
                }).ToList()
            }).ToList(),
            TradeLedger = _tradeLedger.Select(t => new OrganizationTradeRecord
            {
                Utc = t.Utc, Organization = t.Organization, Company = t.Company,
                TraderUserId = t.TraderUserId, TraderSlot = t.TraderSlot, TraderName = t.TraderName,
                CounterpartyUserId = t.CounterpartyUserId, CounterpartySlot = t.CounterpartySlot,
                CounterpartyName = t.CounterpartyName,
                Order = t.Order, Quantity = t.Quantity, Price = t.Price, Total = t.Total,
                Flagged = t.Flagged, Reason = t.Reason
            }).ToList()
        };
        foreach (var org in _organizations.Values.OrderBy(o => o.Id))
        {
            var record = new OrganizationRecord
            {
                Id = org.Id,
                Name = org.Name,
                Kind = org.Kind,
                Industry = org.Industry,
                FactionFocus = org.FactionFocus,
                FactionFoci = (OrganizationFactionFocus[])org.FactionFoci.Clone(),
                FactionAuthorityPoints = org.FactionAuthorityPoints,
                FactionOperationsCompleted = org.FactionOperationsCompleted,
                FactionNodes = org.FactionNodes.Select(n => new OrganizationFactionNodeRecord
                {
                    Id = n.Id, AssignmentCode = n.AssignmentCode, SiteCode = n.SiteCode,
                    Kind = n.Kind, EstablishedUtc = n.EstablishedUtc
                }).ToList(),
                FactionFindings = org.FactionFindings.Select(f => new OrganizationFactionFindingRecord
                {
                    Id = f.Id, SiteCode = f.SiteCode,
                    SiteName = f.SiteName, RecordedUtc = f.RecordedUtc
                }).ToList(),
                IsPrivate = org.IsPrivate,
                RosterPublic = org.RosterPublic,
                PayrollPerMember = org.PayrollPerMember,
                PayrollAutomatic = org.PayrollAutomatic,
                PayrollProfitPercent = org.PayrollProfitPercent,
                PayrollLastCycle = org.PayrollLastCycle,
                QuotaIncomeBonusPercent = org.QuotaIncomeBonusPercent,
                QuotaBonusCycles = org.QuotaBonusCycles,
                CustomRoles = org.CustomRoles.Select(c => new OrganizationCustomRoleRecord
                { Id = c.Id, Title = c.Title, Permissions = c.Permissions }).ToList(),
                LogisticsLevel = org.LogisticsLevel,
                ResearchFacilityLevel = org.ResearchFacilityLevel,
                InfrastructureLevel = org.InfrastructureLevel,
                ServiceNetworkLevel = org.ServiceNetworkLevel,
                RiskManagementLevel = org.RiskManagementLevel,
                LogisticsTier2Level = org.LogisticsTier2Level,
                ResearchTier2Level = org.ResearchTier2Level,
                InfrastructureTier2Level = org.InfrastructureTier2Level,
                ServiceNetworkTier2Level = org.ServiceNetworkTier2Level,
                RiskManagementTier2Level = org.RiskManagementTier2Level,
                PayrollAccrued = org.PayrollAccrued,
                PayrollAccounts = org.PayrollAccounts.Select(p => new OrganizationPayrollAccountRecord
                {
                    UserId = p.UserId, Slot = p.Slot, Name = p.Name,
                    Available = p.Available, Pending = p.Pending,
                    PendingTransferId = p.PendingTransferId, Paid = p.Paid
                }).ToList(),
                EntryFee = org.EntryFee,
                Color = org.Color,
                Description = org.Description,
                DiscordLink = org.DiscordLink,
                Banner = (byte[])org.Banner.Clone(),
                Treasury = org.Treasury,
                InitialInvestment = org.InitialInvestment,
                SectorInfluenceBp = org.SectorInfluenceBp,
                IndustryInfluenceBp = org.IndustryInfluenceBp,
                CapitalSpent = org.CapitalSpent,
                GrossRevenue = org.GrossRevenue,
                OperatingCosts = org.OperatingCosts,
                SalesDateUtc = org.SalesDateUtc,
                DailyGrossRevenue = org.DailyGrossRevenue,
                BudgetShares = (int[]) org.BudgetShares.Clone(),
                DeputyPermissions = org.DeputyPermissions,
                OfficerPermissions = org.OfficerPermissions,
                MemberPermissions = org.MemberPermissions,
                DeputyMemberId = org.DeputyMemberId,
                OwnerTitle = org.OwnerTitle,
                DeputyTitle = org.DeputyTitle,
                OfficerTitle = org.OfficerTitle,
                MemberTitle = org.MemberTitle,
                OutpostBlueprintName = org.OutpostBlueprintName,
                OutpostBlueprintHash = org.OutpostBlueprintHash,
                OutpostBlueprintUtc = org.OutpostBlueprintUtc,
                FleetBlueprintName = org.FleetBlueprintName,
                FleetBlueprintHash = org.FleetBlueprintHash,
                FleetListingName = org.FleetListingName,
                FleetListingDescription = org.FleetListingDescription,
                FleetRentalPrice = org.FleetRentalPrice,
                OutpostPublicName = org.OutpostPublicName,
                OutpostPublicDescription = org.OutpostPublicDescription,
                OutpostAllowVisitors = org.OutpostAllowVisitors,
                OutpostActiveName = org.OutpostActiveName,
                OutpostActiveHash = org.OutpostActiveHash,
                OutpostPurchasedUtc = org.OutpostPurchasedUtc,
                OutpostReplacementName = org.OutpostReplacementName,
                OutpostReplacementHash = org.OutpostReplacementHash,
                OutpostReplacementRequestedUtc = org.OutpostReplacementRequestedUtc,
                CompanyLevel = org.CompanyLevel,
                CompanyExperience = org.CompanyExperience,
                OperatingPolicy = org.OperatingPolicy,
                LastCycleGross = org.LastCycleGross,
                LastCycleCosts = org.LastCycleCosts,
                LastCycleNet = org.LastCycleNet,
                LastCycleDemand = org.LastCycleDemand,
                LastCycleOutcome = org.LastCycleOutcome,
                CompletedEconomyCycles = org.CompletedEconomyCycles,
                LastEconomyUtc = org.LastEconomyUtc,
                SharesListed = org.SharesListed,
                SharesIssued = org.SharesIssued,
                TreasuryShares = org.TreasuryShares,
                Quote = org.Quote,
                PriceHistory = org.PriceHistory.ToList(),
                Shareholders = org.Shareholders.Select(h => new OrganizationShareholderRecord
                {
                    UserId = h.UserId, Slot = h.Slot, Name = h.Name, Shares = h.Shares
                }).ToList(),
                DividendRatePercent = org.DividendRatePercent,
                DividendsDistributed = org.DividendsDistributed,
                DividendAccounts = org.DividendAccounts.Select(d => new OrganizationDividendAccountRecord
                {
                    UserId = d.UserId, Slot = d.Slot, Name = d.Name,
                    Available = d.Available, Pending = d.Pending,
                    PendingTransferId = d.PendingTransferId, Paid = d.Paid
                }).ToList(),
                MarketOrders = org.MarketOrders.Select(o => new OrganizationLimitOrderRecord
                {
                    Id = o.Id, UserId = o.UserId, Slot = o.Slot, CharacterName = o.CharacterName,
                    Name = o.Name, Side = o.Side, State = o.State, CreatedUtc = o.CreatedUtc,
                    Price = o.Price, Remaining = o.Remaining, ReservedFunds = o.ReservedFunds
                }).ToList(),
                MarketCash = org.MarketCash.Select(w => new OrganizationMarketCashRecord
                {
                    UserId = w.UserId, Slot = w.Slot, Name = w.Name, Available = w.Available,
                    Pending = w.Pending, PendingTransferId = w.PendingTransferId, Paid = w.Paid
                }).ToList(),
                Votes = org.Votes.Select(CloneVote).ToList(),
                CrisisActive = org.CrisisActive,
                CrisisTicks = org.CrisisTicks,
                InsolvencyState = org.InsolvencyState,
                MissedUpkeepCycles = org.MissedUpkeepCycles,
                RecoveryCycles = org.RecoveryCycles,
                CrisisSectorBudgetBp = org.CrisisSectorBudgetBp,
                CrisisIndustryBudgetBp = org.CrisisIndustryBudgetBp,
                OwnerMemberId = org.Members[org.Owner].Id
            };
            foreach (var member in org.Members.Values.OrderBy(m => m.Id))
                record.Members.Add(new OrganizationMemberRecord
                {
                    Id = member.Id, UserId = member.Key.UserId, Slot = member.Key.Slot,
                    CharacterName = member.Name, Role = member.Role, CustomTitle = member.CustomTitle,
                    Distinction = member.Distinction, CustomRoleId = member.CustomRoleId
                });
            foreach (var application in org.Applications.Values.OrderBy(a => a.Id))
                record.Applications.Add(new OrganizationApplicationRecord
                {
                    Id = application.Id, UserId = application.Key.UserId, Slot = application.Key.Slot,
                    CharacterName = application.Name, QuotedFee = application.QuotedFee
                });
            document.Organizations.Add(record);
        }
        return document;
    }

    private void Restore(OrganizationDocument document)
    {
        _tdfTreasury = Math.Clamp(document.TdfTreasury, 0, MaxTreasury);
        _tdfShuttleReserve = Math.Clamp(document.TdfShuttleReserve, 0, MaxTreasury);
        _tdfOperationalBp = Math.Clamp(document.TdfOperationalBp, 0, MaxTdfOperationalBp);
        _tdfLongTermBp = Math.Clamp(document.TdfLongTermBp, 0, MaxTdfLongTermBp);
        _tdfAssignedRights.Clear();
        if (document.TdfAssignedRights is not null)
        {
            foreach (var entry in document.TdfAssignedRights)
            {
                if (entry is null || !Guid.TryParse(entry.UserId, out _) ||
                    entry.Slot is < 0 or >= 256 || string.IsNullOrWhiteSpace(entry.CharacterName) ||
                    (entry.Rights & ~OrganizationTdfServiceRight.All) != 0)
                    continue;
                _tdfAssignedRights[Key(entry.UserId, entry.Slot, entry.CharacterName)] = entry.Rights;
            }
        }

        _organizations.Clear();
        _liquidationArchive.Clear();
        if (document.Liquidations is null || document.Liquidations.Count > MaxLiquidationArchive ||
            document.Liquidations.Any(entry => entry is null || entry.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 32 ||
                entry.Reason is null || entry.Reason.Length > 150 ||
                entry.ClosedUtc == default || entry.CancelledShares < 0) ||
            document.Liquidations.Select(entry => entry.Id).Distinct().Count() != document.Liquidations.Count)
        {
            _store.Disable("Invalid company liquidation history");
            return;
        }
        _liquidationArchive.AddRange(document.Liquidations.Select(entry => new OrganizationLiquidationRecord
        {
            Id = entry.Id, Name = entry.Name, ClosedUtc = entry.ClosedUtc,
            Reason = entry.Reason, CancelledShares = entry.CancelledShares
        }));
        _concentrationAlerts.Clear();
        if (document.ConcentrationAlerts is not null)
            _concentrationAlerts.AddRange(document.ConcentrationAlerts.Where(a => a is not null)
                .TakeLast(250));
        _tradeLedger.Clear();
        if (document.TradeLedger is not null)
            _tradeLedger.AddRange(document.TradeLedger.Where(t => t is not null)
                .TakeLast(MaxTradeLedger));
        if (!RestoreTdfCases(document.TdfCases))
        {
            _store.Disable("Invalid TDF audit case archive");
            return;
        }
        _demandDateUtc = document.DemandDateUtc;
        _activeDemandCycles = document.ActiveDemandCycles < 0 ? 0 : document.ActiveDemandCycles;
        _demandRemaining = document.ActiveDemandCycles < 0 ? DailySectorDemand : document.DemandRemaining;
        var occupied = new HashSet<CharacterKey>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var data in document.Organizations.Take(MaxOrganizations))
        {
            if (data.Id == Guid.Empty || _organizations.ContainsKey(data.Id) ||
                !TryNormalizeName(data.Name, out var title) || title != data.Name ||
                !TryNormalizeColor(data.Color, out var color) ||
                !ValidKind(data.Kind, data.Industry) ||
                data.SectorInfluenceBp is < 0 or > MarketCapacityBp ||
                data.IndustryInfluenceBp is < 0 or > MarketCapacityBp ||
                (data.Kind == OrganizationKind.Faction && data.IndustryInfluenceBp != 0) ||
                data.CapitalSpent < 0 || data.GrossRevenue < 0 || data.OperatingCosts < 0 ||
                data.DailyGrossRevenue is < 0 or > DailyCompanyRevenueLimit ||
                data.EntryFee is < 0 or > MaxEntryFee || data.Treasury is < 0 or > MaxTreasury ||
                data.InitialInvestment is < 0 or > MaxInitialInvestment || !ValidShares(data.BudgetShares) ||
                data.PayrollPerMember is < 0 or > MaxPayrollPerMember ||
                data.PayrollProfitPercent is < 0 or > 60 ||
                data.PayrollLastCycle < 0 || data.PayrollLastCycle > data.CompletedEconomyCycles ||
                data.CustomRoles is null || data.CustomRoles.Count > 12 ||
                data.CustomRoles.Any(c => c is null || c.Id == Guid.Empty ||
                    !ValidTitle(c.Title) || !ValidPermissions(c.Permissions)) ||
                data.CustomRoles.Select(c => c.Id).Distinct().Count() != data.CustomRoles.Count ||
                data.LogisticsLevel is < 0 or > 5 || data.ResearchFacilityLevel is < 0 or > 5 ||
                data.InfrastructureLevel is < 0 or > 5 ||
                data.ServiceNetworkLevel is < 0 or > 5 || data.RiskManagementLevel is < 0 or > 5 ||
                data.LogisticsTier2Level is < 0 or > 5 ||
                data.ResearchTier2Level is < 0 or > 5 ||
                data.InfrastructureTier2Level is < 0 or > 5 ||
                data.ServiceNetworkTier2Level is < 0 or > 5 ||
                data.RiskManagementTier2Level is < 0 or > 5 ||
                data.PayrollAccrued < 0 || data.PayrollAccounts is null ||
                data.PayrollAccounts.Count > MaxPayrollAccounts ||
                data.PayrollAccounts.Any(p => p is null || !Guid.TryParse(p.UserId, out _) ||
                    p.Slot is < 0 or >= 256 || p.Name is null || p.Name.Length > 128 ||
                    p.Available is < 0 or > MaxTreasury || p.Pending is < 0 or > MaxPayrollClaim ||
                    p.Paid < 0 || (p.Pending == 0) != (p.PendingTransferId == Guid.Empty)) ||
                data.PayrollAccounts.Select(p => (p.UserId, p.Slot)).Distinct().Count() != data.PayrollAccounts.Count ||
                !Enum.IsDefined(data.OperatingPolicy) ||
                !ValidPermissions(data.DeputyPermissions) || !ValidPermissions(data.OfficerPermissions) || !ValidPermissions(data.MemberPermissions) ||
                data.LastCycleGross is < 0 or > DailyCompanyRevenueLimit ||
                data.LastCycleCosts is < 0 or > 20_000 ||
                data.LastCycleDemand is < 0 or > 20_000 ||
                data.LastCycleOutcome is < 0 or > 2 ||
                data.CompletedEconomyCycles is < 0 or > 1_000_000 ||
                data.CrisisTicks is < 0 or > 1000 ||
                !Enum.IsDefined(data.InsolvencyState) ||
                data.MissedUpkeepCycles is < 0 or > 1000 ||
                data.RecoveryCycles is < 0 or > 2 ||
                (data.Kind != OrganizationKind.Company && data.InsolvencyState != OrganizationInsolvencyState.Active) ||
                data.CrisisSectorBudgetBp is < 0 or > MarketCapacityBp ||
                data.CrisisIndustryBudgetBp is < 0 or > MarketCapacityBp ||
                (data.CrisisActive && data.Kind != OrganizationKind.Company) ||
                data.Members is not { Count: >= 1 and <= MaxMembers } ||
                data.Applications is not { Count: <= MaxApplications } || names.Contains(title))
                continue;

            var members = new List<Member>();
            var memberIds = new HashSet<Guid>();
            foreach (var record in data.Members)
            {
                if (record is null || !ValidIdentity(record.UserId, record.Slot, record.CharacterName) ||
                    record.Id == Guid.Empty || !memberIds.Add(record.Id) ||
                    !Enum.IsDefined(record.Role) || record.Distinction > 3)
                    break;
                members.Add(new Member(Key(record.UserId, record.Slot, record.CharacterName),
                    record.CharacterName, record.Role, record.Id)
                {
                    CustomTitle = ValidTitle(record.CustomTitle) ? record.CustomTitle : string.Empty,
                    Distinction = record.Distinction, CustomRoleId = record.CustomRoleId
                });
            }
            if (members.Count != data.Members.Count ||
                members.Select(m => m.Key).Distinct().Count() != members.Count ||
                members.Any(m => occupied.Contains(m.Key)))
                continue;

            var owner = members.FirstOrDefault(m => m.Id == data.OwnerMemberId);
            if (owner == null || owner.Role != OrganizationMemberRole.Owner ||
                members.Count(m => m.Role == OrganizationMemberRole.Owner) != 1)
                continue;

            if (data.Description is null || data.Description.Length > 500 ||
                data.DiscordLink is null || !ValidDiscordLink(data.DiscordLink) ||
                !ValidBanner(data.Banner))
                continue;
            var org = new Organization(data.Id, title, data.Kind, data.Industry, owner,
                data.IsPrivate, data.EntryFee, color)
            {
                RosterPublic = data.RosterPublic,
                FactionFocus = data.Kind == OrganizationKind.Faction && Enum.IsDefined(data.FactionFocus)
                    ? data.FactionFocus : OrganizationFactionFocus.None,
                FactionFoci = data.Kind == OrganizationKind.Faction
                    ? RestoreFactionFoci(data.FactionFoci, data.FactionFocus) : [],
                FactionAuthorityPoints = data.Kind == OrganizationKind.Faction
                    ? Math.Max(0, data.FactionAuthorityPoints) : 0,
                FactionOperationsCompleted = data.Kind == OrganizationKind.Faction
                    ? Math.Max(0, data.FactionOperationsCompleted) : 0,
                PayrollPerMember = data.PayrollPerMember,
                PayrollAutomatic = data.PayrollAutomatic,
                PayrollProfitPercent = data.PayrollProfitPercent,
                PayrollLastCycle = data.PayrollLastCycle,
                QuotaIncomeBonusPercent = Math.Clamp(data.QuotaIncomeBonusPercent, 0, OrganizationQuotaRules.MaximumIncomeBonusPercent),
                QuotaBonusCycles = Math.Clamp(data.QuotaBonusCycles, 0, OrganizationQuotaRules.BonusCycles),
                LogisticsLevel = data.LogisticsLevel,
                ResearchFacilityLevel = data.ResearchFacilityLevel,
                InfrastructureLevel = Math.Clamp(data.InfrastructureLevel, 0, 5),
                ServiceNetworkLevel = Math.Clamp(data.ServiceNetworkLevel, 0, OrganizationDevelopmentRules.MaxLevel),
                RiskManagementLevel = Math.Clamp(data.RiskManagementLevel, 0, 5),
                LogisticsTier2Level = Math.Clamp(data.LogisticsTier2Level, 0, 5),
                ResearchTier2Level = Math.Clamp(data.ResearchTier2Level, 0, 5),
                InfrastructureTier2Level = Math.Clamp(data.InfrastructureTier2Level, 0, 5),
                ServiceNetworkTier2Level = Math.Clamp(data.ServiceNetworkTier2Level, 0, 5),
                RiskManagementTier2Level = Math.Clamp(data.RiskManagementTier2Level, 0, 5),
                PayrollAccrued = data.PayrollAccrued,
                Description = data.Description ?? string.Empty,
                DiscordLink = data.DiscordLink ?? string.Empty,
                Banner = data.Banner ?? [],
                Treasury = data.Treasury,
                InitialInvestment = data.InitialInvestment,
                SectorInfluenceBp = data.SectorInfluenceBp,
                IndustryInfluenceBp = data.IndustryInfluenceBp,
                CapitalSpent = data.CapitalSpent,
                GrossRevenue = data.GrossRevenue,
                OperatingCosts = data.OperatingCosts,
                SalesDateUtc = data.SalesDateUtc,
                DailyGrossRevenue = document.ActiveDemandCycles < 0 ? 0 : data.DailyGrossRevenue,
                BudgetShares = (int[]) data.BudgetShares.Clone(),
                DeputyPermissions = data.DeputyPermissions,
                OfficerPermissions = data.OfficerPermissions,
                MemberPermissions = data.MemberPermissions,
                DeputyMemberId = data.DeputyMemberId,
                OwnerTitle = ValidTitle(data.OwnerTitle) ? data.OwnerTitle : "Власник",
                DeputyTitle = ValidTitle(data.DeputyTitle) ? data.DeputyTitle : "Заступник",
                OfficerTitle = ValidTitle(data.OfficerTitle) ? data.OfficerTitle : "Офіцер",
                MemberTitle = ValidTitle(data.MemberTitle) ? data.MemberTitle : "Учасник",
                OutpostBlueprintName = data.OutpostBlueprintName is { Length: > 0 and <= 64 }
                    ? data.OutpostBlueprintName : string.Empty,
                OutpostBlueprintHash = IsValidOutpostHash(data.OutpostBlueprintHash)
                    ? data.OutpostBlueprintHash : string.Empty,
                OutpostBlueprintUtc = data.OutpostBlueprintUtc,
                FleetBlueprintName = data.FleetBlueprintName is { Length: > 0 and <= 64 }
                    ? data.FleetBlueprintName : string.Empty,
                FleetBlueprintHash = IsValidOutpostHash(data.FleetBlueprintHash)
                    ? data.FleetBlueprintHash : string.Empty,
                FleetListingName = data.FleetListingName is { Length: > 0 and <= 48 }
                    ? data.FleetListingName : string.Empty,
                FleetListingDescription = data.FleetListingDescription is { Length: <= 160 }
                    ? data.FleetListingDescription : string.Empty,
                FleetRentalPrice = data.FleetRentalPrice is >= OrganizationFleetRules.MinimumRentalPrice
                    and <= OrganizationFleetRules.MaximumRentalPrice
                    ? data.FleetRentalPrice : OrganizationFleetRules.DefaultRentalPrice,
                OutpostPublicName = data.OutpostPublicName is { Length: <= 52 } ? data.OutpostPublicName : string.Empty,
                OutpostPublicDescription = data.OutpostPublicDescription is { Length: <= 200 } ? data.OutpostPublicDescription : string.Empty,
                OutpostAllowVisitors = data.OutpostAllowVisitors,
                OutpostActiveName = IsValidOutpostHash(data.OutpostActiveHash) &&
                    data.OutpostActiveName is { Length: > 0 and <= 64 }
                    ? data.OutpostActiveName : string.Empty,
                OutpostActiveHash = IsValidOutpostHash(data.OutpostActiveHash) &&
                    data.OutpostActiveName is { Length: > 0 and <= 64 }
                    ? data.OutpostActiveHash : string.Empty,
                OutpostPurchasedUtc = data.OutpostPurchasedUtc,
                OutpostReplacementName = IsValidOutpostHash(data.OutpostReplacementHash) &&
                    data.OutpostReplacementName is { Length: > 0 and <= 64 }
                    ? data.OutpostReplacementName : string.Empty,
                OutpostReplacementHash = IsValidOutpostHash(data.OutpostReplacementHash) &&
                    data.OutpostReplacementName is { Length: > 0 and <= 64 }
                    ? data.OutpostReplacementHash : string.Empty,
                OutpostReplacementRequestedUtc = data.OutpostReplacementRequestedUtc,
                CompanyLevel = Math.Clamp(data.CompanyLevel, 1, MaxCompanyLevel),
                CompanyExperience = Math.Clamp(data.CompanyExperience, 0, 1_000_000),
                OperatingPolicy = data.OperatingPolicy,
                LastCycleGross = data.LastCycleGross,
                LastCycleCosts = data.LastCycleCosts,
                LastCycleNet = data.LastCycleNet,
                LastCycleDemand = data.LastCycleDemand,
                LastCycleOutcome = data.LastCycleOutcome,
                CompletedEconomyCycles = data.CompletedEconomyCycles,
                LastEconomyUtc = data.LastEconomyUtc,
                SharesListed = data.SharesListed ||
                    (data.Shareholders?.Any(h => h.UserId != owner.Key.UserId || h.Slot != owner.Key.Slot) == true),
                SharesIssued = data.SharesIssued,
                TreasuryShares = data.TreasuryShares,
                DividendRatePercent = data.DividendRatePercent,
                DividendsDistributed = data.DividendsDistributed,
                Quote = data.Quote,
                // Leadership crises are round-local; old saves must not revive death penalties.
                CrisisActive = false,
                CrisisTicks = 0,
                InsolvencyState = data.InsolvencyState,
                MissedUpkeepCycles = data.MissedUpkeepCycles,
                RecoveryCycles = data.RecoveryCycles,
                CrisisSectorBudgetBp = 0,
                CrisisIndustryBudgetBp = 0
            };
            if (org.Kind == OrganizationKind.Faction && data.FactionNodes is { } savedNodes)
            {
                var uniqueNodes = new HashSet<Guid>();
                foreach (var node in savedNodes.Take(MaxFactionNetworkNodes))
                {
                    if (node is null || node.Id == Guid.Empty ||
                        !Enum.IsDefined(node.Kind) ||
                        node.AssignmentCode is not { Length: > 0 and <= 48 } ||
                        node.EstablishedUtc == default || !uniqueNodes.Add(node.Id))
                        continue;
                    org.FactionNodes.Add(new OrganizationFactionNodeRecord
                    {
                        Id = node.Id, AssignmentCode = node.AssignmentCode,
                        SiteCode = node.SiteCode is { Length: <= 20 } ? node.SiteCode : string.Empty,
                        Kind = node.Kind, EstablishedUtc = node.EstablishedUtc
                    });
                }
            }
            // Old saves predate FactionFindings. Accept only bounded, unique
            // trustworthy data and avoid accidentally persisting runtime map IDs.
            if (org.Kind == OrganizationKind.Faction && data.FactionFindings is { } findings)
            {
                var uniqueFindings = new HashSet<Guid>();
                foreach (var finding in findings.Take(MaxFactionFindings))
                {
                    if (finding is null || finding.Id == Guid.Empty ||
                        !uniqueFindings.Add(finding.Id) ||
                        finding.SiteCode is not { Length: >= 3 and <= 20 } ||
                        finding.SiteName is not { Length: >= 2 and <= 100 } ||
                        finding.RecordedUtc == default)
                        continue;
                    org.FactionFindings.Add(new OrganizationFactionFindingRecord
                    {
                        Id = finding.Id, SiteCode = finding.SiteCode,
                        SiteName = finding.SiteName, RecordedUtc = finding.RecordedUtc
                    });
                }
            }
            org.CustomRoles.AddRange(data.CustomRoles.Select(c => new OrganizationCustomRoleRecord
            { Id = c.Id, Title = c.Title, Permissions = c.Permissions }));
            foreach (var member in members)
                if (member.Role == OrganizationMemberRole.Owner || member.Id == org.DeputyMemberId ||
                    org.CustomRoles.All(c => c.Id != member.CustomRoleId))
                    member.CustomRoleId = Guid.Empty;
            org.PayrollAccounts.AddRange(data.PayrollAccounts.Select(p => new OrganizationPayrollAccountRecord
            {
                UserId = p.UserId, Slot = p.Slot, Name = p.Name,
                Available = p.Available, Pending = p.Pending,
                PendingTransferId = p.PendingTransferId, Paid = p.Paid
            }));
            foreach (var member in members)
                org.Members[member.Key] = member;
            if (org.Kind == OrganizationKind.Company)
            {
                // Backwards-compatible migration: existing companies receive founder equity.
                if (org.SharesIssued == 0)
                {
                    org.SharesIssued = InitialShares;
                    org.TreasuryShares = 0;
                    org.Quote = InitialQuote(org);
                    org.Shareholders.Add(new OrganizationShareholderRecord
                    {
                        UserId = owner.Key.UserId, Slot = owner.Key.Slot,
                        Name = owner.Name, Shares = InitialShares
                    });
                    org.PriceHistory.Add(org.Quote);
                }
                else
                {
                    if (data.Shareholders is not null)
                        org.Shareholders.AddRange(data.Shareholders.Select(h => new OrganizationShareholderRecord
                        {
                            UserId = h.UserId, Slot = h.Slot, Name = h.Name, Shares = h.Shares
                        }));
                    if (data.PriceHistory is not null)
                        org.PriceHistory.AddRange(data.PriceHistory.TakeLast(MaxPriceHistory));
                    if (org.PriceHistory.Count == 0)
                        org.PriceHistory.Add(org.Quote);
                }
                if ((data.DividendRatePercent is < 0 or > MaxDividendPolicy || data.DividendRatePercent % 5 != 0) ||
                    data.DividendsDistributed < 0 || data.DividendAccounts is null ||
                    data.DividendAccounts.Count > MaxDividendAccounts ||
                    data.DividendAccounts.Any(d => d is null || !Guid.TryParse(d.UserId, out _) ||
                        d.Slot is < 0 or >= 256 || d.Available is < 0 or > MaxTreasury ||
                        d.Pending is < 0 or > MaxDividendClaim || d.Paid < 0 ||
                        (d.Pending == 0) != (d.PendingTransferId == Guid.Empty)) ||
                    data.DividendAccounts.Select(d => (d.UserId, d.Slot)).Distinct().Count() != data.DividendAccounts.Count)
                {
                    _store.Disable($"Invalid dividend ledger for company {org.Id}");
                    return;
                }
                org.DividendAccounts.AddRange(data.DividendAccounts.Select(d => new OrganizationDividendAccountRecord
                {
                    UserId = d.UserId, Slot = d.Slot, Name = d.Name, Available = d.Available,
                    Pending = d.Pending, PendingTransferId = d.PendingTransferId, Paid = d.Paid
                }));
                if (!RestoreMarket(org, data))
                {
                    _store.Disable($"Invalid limit order or trading escrow for company {org.Id}");
                    return;
                }
                if (!ValidateGovernance(org, data))
                {
                    _store.Disable($"Invalid shareholder votes for company {org.Id}");
                    return;
                }
                if (org.SharesIssued is < InitialShares or > MaxIssuedShares ||
                    org.TreasuryShares < 0 || org.Quote is < 1 or > MaxQuote ||
                    org.Shareholders.Any(h => !Guid.TryParse(h.UserId, out _) || h.Slot is < 0 or >= 256 || h.Shares <= 0) ||
                    org.Shareholders.Select(h => (h.UserId, h.Slot)).Distinct().Count() != org.Shareholders.Count ||
                    org.PriceHistory.Any(quote => quote is < 1 or > MaxQuote) ||
                    org.Shareholders.Sum(h => (long) h.Shares) + org.TreasuryShares != org.SharesIssued)
                {
                    _store.Disable($"Invalid share register for company {org.Id}");
                    return;
                }
            }
            if (org.DeputyMemberId != Guid.Empty &&
                !org.Members.Values.Any(m => m.Id == org.DeputyMemberId && m.Role == OrganizationMemberRole.Officer))
                org.DeputyMemberId = Guid.Empty;

            foreach (var record in data.Applications)
            {
                if (record is null || !ValidIdentity(record.UserId, record.Slot, record.CharacterName) ||
                    record.Id == Guid.Empty || record.QuotedFee is < 0 or > MaxEntryFee ||
                    org.Applications.ContainsKey(record.Id))
                    continue;
                var key = Key(record.UserId, record.Slot, record.CharacterName);
                if (org.Members.ContainsKey(key) || org.Applications.Values.Any(a => a.Key == key))
                    continue;
                org.Applications.Add(record.Id, new Application(record.Id, key, record.CharacterName, record.QuotedFee));
            }
            _organizations.Add(org.Id, org);
            names.Add(title);
            foreach (var member in members)
                occupied.Add(member.Key);
        }

        if (_activeDemandCycles is < 0 or > MaxActiveDemandCycles ||
            _demandRemaining is < 0 or > DailySectorDemand)
        {
            _organizations.Clear();
            _store.Disable("Organization market demand is out of bounds");
            return;
        }

        if (_organizations.Values.Sum(o => o.SectorInfluenceBp) > MarketCapacityBp ||
            Enum.GetValues<OrganizationIndustry>().Where(i => i != OrganizationIndustry.None)
                .Any(i => _organizations.Values.Where(o => o.Industry == i)
                    .Sum(o => o.IndustryInfluenceBp) > MarketCapacityBp))
        {
            _organizations.Clear();
            _store.Disable("Organization influence exceeds market capacity");
        }
    }

    private static bool ValidIdentity(string? userId, int slot, string? name) =>
        Guid.TryParse(userId, out _) && slot is >= 0 and < 256 &&
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128;

    private bool Commit(Action apply)
    {
        if (!_store.Available)
            return false;
        var before = Snapshot();
        apply();
        if (_store.Save(Snapshot()))
            return true;
        Restore(before);
        return false;
    }

    private bool AuthorizedBusiness(EntityUid console, EntityUid actor) =>
        Authorized(console, actor) && !IsTdfOfficer(actor) && IsOriginalController(actor);

    private bool Authorized(EntityUid console, EntityUid actor) =>
        actor.Valid &&
        TryComp<UserInterfaceComponent>(console, out var ui) &&
        _ui.IsUiOpen((console, ui), OrganizationConsoleUiKey.Key, actor);

    private void OnOpened(EntityUid uid, OrganizationConsoleComponent comp, BoundUIOpenedEvent args) => Refresh(uid, args.Actor);

    private void OnRefresh(EntityUid uid, OrganizationConsoleComponent comp, OrganizationRefreshMessage args)
    {
        if (Authorized(uid, args.Actor))
            Refresh(uid, args.Actor);
    }

    private void OnCreate(EntityUid uid, OrganizationConsoleComponent comp, OrganizationCreateMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) ||
            !TryGetCharacter(actor, out var key, out var name))
            return;

        if (!_store.Available)
        {
            Refresh(uid, actor, "Сховище організацій недоступне. Перевірте журнал сервера.");
            return;
        }

        if (_organizations.Count >= MaxOrganizations || FindMembership(key) != null)
        {
            Refresh(uid, actor, "Ліміт організацій досягнуто або персонаж уже має організацію.");
            return;
        }

        if (!TryNormalizeName(args.Name, out var title))
        {
            Refresh(uid, actor, "Некоректна назва. Використовуйте 3–32 символи: літери, цифри, пробіли, дефіс або апостроф.");
            return;
        }

        if (title.StartsWith("TDF", StringComparison.OrdinalIgnoreCase) ||
            _organizations.Values.Any(o => string.Equals(o.Name, title, StringComparison.OrdinalIgnoreCase)))
        {
            Refresh(uid, actor, "Організація з такою назвою вже існує.");
            return;
        }

        if (!TryNormalizeColor(args.Color, out var color) ||
            !ValidKind(args.Kind, args.Industry) || args.EntryFee is < 0 or > MaxEntryFee ||
            args.Investment is < 0 or > MaxInitialInvestment ||
            !ValidShares([args.Marketing, args.Logistics, args.Security, args.Development]) ||
            args.Description is null || args.Description.Length > 500 ||
            args.DiscordLink is null || !ValidDiscordLink(args.DiscordLink) ||
            !ValidBanner(args.Banner) ||
            (args.Kind == OrganizationKind.Faction && !ValidFactionFoci(args.FactionFoci)) ||
            (args.Kind == OrganizationKind.Company && args.FactionFoci is not { Length: 0 }))
        {
            Refresh(uid, actor, "Перевірте тип, галузь, колір, внесок, логотип і розподіл бюджету.");
            return;
        }

        var shares = new[] { args.Marketing, args.Logistics, args.Security, args.Development };
        var owner = new Member(key, name, OrganizationMemberRole.Owner, Guid.NewGuid());
        var org = new Organization(Guid.NewGuid(), title, args.Kind, args.Industry, owner,
            args.IsPrivate, args.EntryFee, color)
        {
            InitialInvestment = args.Investment,
            Treasury = args.Investment,
            BudgetShares = shares,
            RosterPublic = args.RosterPublic,
            FactionFocus = args.Kind == OrganizationKind.Faction ? args.FactionFoci[0] : OrganizationFactionFocus.None,
            FactionFoci = args.Kind == OrganizationKind.Faction
                ? (OrganizationFactionFocus[])args.FactionFoci.Clone() : [],
            Description = args.Description.Trim(),
            DiscordLink = args.DiscordLink.Trim(),
            Banner = (byte[])args.Banner.Clone()
        };
        if (org.Kind == OrganizationKind.Company)
        {
            org.SharesIssued = InitialShares;
            org.TreasuryShares = 0;
            org.Quote = InitialQuote(org);
            org.PriceHistory.Add(org.Quote);
            org.Shareholders.Add(new OrganizationShareholderRecord
            {
                UserId = key.UserId, Slot = key.Slot, Name = name,
                Shares = InitialShares
            });
        }

        var totalDebit = args.Investment + FoundingCost(args.Kind, args.IsPrivate);
        if (!_bank.TryBankWithdraw(actor, totalDebit))
        {
            Refresh(uid, actor, "Недостатньо коштів для реєстрації та початкового капіталу.");
            return;
        }

        if (!Commit(() => _organizations.Add(org.Id, org)))
        {
            if (!_bank.TryBankDeposit(actor, totalDebit))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Organization founding refund failed for {ToPrettyString(actor):player}: {totalDebit}");
            Refresh(uid, actor, "Не вдалося зберегти організацію. Платіж повернуто, якщо банк доступний.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(actor):player} registered {org.Name} ({org.Id}), registry fee={FoundingCost(args.Kind, args.IsPrivate)}");
        UpdateAffiliation(actor, key);
        Refresh(uid, actor, $"Організацію \"{title}\" зареєстровано.");
    }

    private void OnJoin(EntityUid uid, OrganizationConsoleComponent comp, OrganizationJoinMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) ||
            !TryGetCharacter(actor, out var key, out var name))
            return;

        if (_lastDepartureUtc.TryGetValue(key, out var departure) &&
            DateTime.UtcNow - departure < PostExitWait)
        {
            Refresh(uid, actor, "Повторний вступ доступний через 15 хвилин після виходу.");
            return;
        }

        if (args.Organization == TdfAuthorityId ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            FindMembership(key) != null || org.Members.Count >= MaxMembers)
        {
            Refresh(uid, actor, "Вступ зараз недоступний.");
            return;
        }

        if (org.InsolvencyState == OrganizationInsolvencyState.Liquidation)
        {
            Refresh(uid, actor, "Організація припиняє діяльність, вступ закритий.");
            return;
        }
        if (org.IsPrivate)
        {
            if (org.Applications.Values.Any(a => a.Key == key) || org.Applications.Count >= MaxApplications)
            {
                Refresh(uid, actor, "Заявка вже подана або черга переповнена.");
                return;
            }
            var application = new Application(Guid.NewGuid(), key, name, org.EntryFee);
            Refresh(uid, actor, Commit(() => org.Applications.Add(application.Id, application))
                ? "Заявку подано. Внесок буде списано після схвалення."
                : "Не вдалося зберегти заявку.");
            return;
        }

        Refresh(uid, actor, TryAdmit(org, actor, key, name, org.EntryFee)
            ? $"Ви вступили до організації {org.Name}." : "Не вдалося вступити або сплатити внесок.");
    }

    private bool TryAdmit(Organization org, EntityUid actor, CharacterKey key, string name, int fee)
    {
        if (FindMembership(key) != null || org.Members.Count >= MaxMembers ||
            org.InsolvencyState == OrganizationInsolvencyState.Liquidation ||
            org.Treasury > MaxTreasury - fee || !_store.Available)
            return false;
        if (fee > 0 && !_bank.TryBankWithdraw(actor, fee))
            return false;

        var member = new Member(key, name, OrganizationMemberRole.Member, Guid.NewGuid());
        var accepted = Commit(() =>
        {
            org.Members.Add(key, member);
            org.Treasury += fee;
            foreach (var id in org.Applications.Values.Where(a => a.Key == key).Select(a => a.Id).ToArray())
                org.Applications.Remove(id);
        });
        if (!accepted)
        {
            if (fee > 0 && !_bank.TryBankDeposit(actor, fee))
                _adminLog.Add(LogType.Action, LogImpact.High, $"Refund failed for {ToPrettyString(actor):player}, amount={fee}");
            return false;
        }

        UpdateAffiliation(actor, key);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(actor):player} joined {org.Name} ({org.Id}), fee={fee}");
        return true;
    }

    private void OnReview(EntityUid uid, OrganizationConsoleComponent comp, OrganizationReviewMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) ||
            !TryGetCharacter(actor, out var key, out _))
            return;

        var org = _organizations.Values.FirstOrDefault(o => o.Members.TryGetValue(key, out var member) &&
            HasPermission(o, member, OrganizationPermission.Applications) && o.Applications.ContainsKey(args.Application));
        if (org == null)
            return;

        var application = org.Applications[args.Application];
        if (!args.Accept)
        {
            Refresh(uid, actor, Commit(() => org.Applications.Remove(args.Application))
                ? "Заявку відхилено." : "Не вдалося зберегти рішення.");
            return;
        }

        if (!Guid.TryParse(application.Key.UserId, out var userId) ||
            !_players.TryGetSessionById(new NetUserId(userId), out var session) ||
            session.AttachedEntity is not { } applicant ||
            !TryGetCharacter(applicant, out var actualKey, out var actualName) ||
            actualKey != application.Key)
        {
            Refresh(uid, actor, "Заявник повинен бути в мережі саме цим персонажем, щоб сплатити внесок.");
            return;
        }

        Refresh(uid, actor, TryAdmit(org, applicant, actualKey, actualName, application.QuotedFee)
            ? "Заявку схвалено." : "Заявник не може вступити або сплатити внесок.");
    }

    private void OnConfigure(EntityUid uid, OrganizationConsoleComponent comp, OrganizationConfigureMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Settings,
                out var actor, out var org))
            return;

        if (args.EntryFee is < 0 or > MaxEntryFee || !TryNormalizeColor(args.Color, out var color))
        {
            Refresh(uid, actor, "Некоректний внесок або колір. Використовуйте формат #RRGGBB.");
            return;
        }
        var newName = org.Name;
        if (!string.IsNullOrWhiteSpace(args.Name))
        {
            if (!TryNormalizeName(args.Name, out newName) ||
                _organizations.Values.Any(other => other.Id != org.Id &&
                    string.Equals(other.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                Refresh(uid, actor, "Некоректна або вже зайнята назва організації.");
                return;
            }
        }
        if (args.UpdateBanner && !ValidBanner(args.Banner))
        {
            Refresh(uid, actor, "Емблема має бути PNG, не більш ніж 16 КБ.");
            return;
        }
        if (!Commit(() =>
        {
            org.Name = newName;
            org.EntryFee = args.EntryFee;
            org.IsPrivate = args.IsPrivate;
            org.RosterPublic = args.RosterPublic;
            org.Color = color;
            if (args.UpdateBanner)
                org.Banner = (byte[])args.Banner.Clone();
        }))
        {
            Refresh(uid, actor, "Не вдалося зберегти параметри організації.");
            return;
        }
        foreach (var member in org.Members.Keys)
            SyncActive(member);
        Refresh(uid, actor, "Параметри організації оновлено.");
    }

    private static bool ValidPermissions(OrganizationPermission permissions) =>
        (permissions & ~OrganizationPermission.All) == OrganizationPermission.None;

    private static OrganizationPermission EffectivePermissions(Organization org, Member member)
    {
        if (member.Role == OrganizationMemberRole.Member && member.CustomRoleId != Guid.Empty)
        {
            var custom = org.CustomRoles.FirstOrDefault(c => c.Id == member.CustomRoleId);
            if (custom != null) return custom.Permissions;
        }
        return BasePermissions(org, member);
    }

    private static OrganizationPermission BasePermissions(Organization org, Member member) => member.Role switch
    {
        OrganizationMemberRole.Owner => OrganizationPermission.All,
        OrganizationMemberRole.Officer when member.Id == org.DeputyMemberId => org.DeputyPermissions,
        OrganizationMemberRole.Officer => org.OfficerPermissions,
        _ => org.MemberPermissions
    };

    private static bool HasPermission(Organization org, Member member, OrganizationPermission permission) =>
        (EffectivePermissions(org, member) & permission) == permission;

    private void OnSetPermissions(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetPermissionsMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            args.Tier == OrganizationPermissionTier.Owner || !Enum.IsDefined(args.Tier) ||
            !ValidPermissions(args.Permissions))
            return;
        var saved = Commit(() =>
        {
            switch (args.Tier)
            {
                case OrganizationPermissionTier.Deputy: org.DeputyPermissions = args.Permissions; break;
                case OrganizationPermissionTier.Officer: org.OfficerPermissions = args.Permissions; break;
                case OrganizationPermissionTier.Member: org.MemberPermissions = args.Permissions; break;
            }
        });
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(actor):player} {(saved ? "updated" : "failed to update")} {args.Tier} permissions in {org.Name} ({org.Id})");
        Refresh(uid, actor, saved ? "Права ролі оновлено." : "Не вдалося зберегти права.");
    }

    private void OnSetDistinction(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetDistinctionMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) || args.Distinction > 3)
            return;
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        if (member == null || member.Role == OrganizationMemberRole.Owner || member.Key == org.Owner)
            return;
        Refresh(uid, actor, Commit(() => member.Distinction = args.Distinction)
            ? "Відзнаку співробітника оновлено." : "Не вдалося зберегти відзнаку.");
    }

    private void OnTreasury(EntityUid uid, OrganizationConsoleComponent comp, OrganizationTreasuryMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) ||
            !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            !org.Members.TryGetValue(key, out var member) ||
            (!args.Deposit && (!HasPermission(org, member, OrganizationPermission.TreasuryWithdraw) ||
                IsTradingSuspended(org))))
            return;

        if (args.Deposit && org.InsolvencyState == OrganizationInsolvencyState.Liquidation)
        {
            Refresh(uid, actor, "Під час ліквідації прийом нових внесків закритий.");
            return;
        }
        if (!args.Deposit && _recentWithdrawals.TryGetValue(org.Id, out var lastWithdrawal) &&
            _gameTiming.CurTime < lastWithdrawal + TreasuryWithdrawalCooldown)
        {
            Refresh(uid, actor, "Наступне виведення буде доступне за 10 секунд.");
            return;
        }

        if (args.Amount is <= 0 or > MaxTreasuryTransfer || !_store.Available)
        {
            Refresh(uid, actor, "Некоректна сума або сховище організації недоступне.");
            return;
        }

        if (args.Deposit)
        {
            if (org.Treasury > MaxTreasury - args.Amount || !_bank.TryBankWithdraw(actor, args.Amount))
            {
                Refresh(uid, actor, "Не вдалося внести кошти.");
                return;
            }
            if (!Commit(() => org.Treasury += args.Amount))
            {
                if (!_bank.TryBankDeposit(actor, args.Amount))
                    _adminLog.Add(LogType.Action, LogImpact.High, $"Deposit refund failed for {ToPrettyString(actor):player}: {args.Amount}");
                Refresh(uid, actor, "Не вдалося зберегти внесення коштів.");
                return;
            }
        }
        else
        {
            if (org.Treasury < args.Amount || !_bank.TryBankDeposit(actor, args.Amount))
            {
                Refresh(uid, actor, "Не вдалося вивести кошти.");
                return;
            }
            if (!Commit(() => org.Treasury -= args.Amount))
            {
                if (!_bank.TryBankWithdraw(actor, args.Amount))
                    _adminLog.Add(LogType.Action, LogImpact.High, $"Treasury withdrawal rollback failed for {ToPrettyString(actor):player}: {args.Amount}");
                Refresh(uid, actor, "Не вдалося зберегти виведення коштів.");
                return;
            }
        }

        if (!args.Deposit)
            _recentWithdrawals[org.Id] = _gameTiming.CurTime;
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        _adminLog.Add(LogType.Action, args.Deposit ? LogImpact.Medium : LogImpact.High,
            $"{ToPrettyString(actor):player} {(args.Deposit ? "deposited" : "withdrew")} {args.Amount} for {org.Name} ({org.Id})");
        Refresh(uid, actor, args.Deposit ? "Кошти внесено до скарбниці." : "Кошти виведено зі скарбниці.");
    }

    private void OnLeave(EntityUid uid, OrganizationConsoleComponent comp, OrganizationLeaveMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) ||
            !TryGetCharacter(actor, out var key, out _))
            return;
        var org = FindMembership(key);
        if (org == null)
            return;
        if (org.Owner == key)
        {
            Refresh(uid, actor, "Перед виходом спершу передайте володіння іншому учаснику.");
            return;
        }
        // Exit fee goes to the former organization. Never trap an insolvent member:
        // the fee is capped by the actual account balance.
        var exitFee = TryComp<BankAccountComponent>(actor, out var account)
            ? Math.Min(ExitFeeCap, Math.Max(0, account.Balance)) : 0;
        if (exitFee > 0 && !_bank.TryBankWithdraw(actor, exitFee))
        {
            Refresh(uid, actor, "Не вдалося списати збір за розірвання контракту.");
            return;
        }
        if (!Commit(() => { org.Members.Remove(key); org.Treasury += exitFee; }))
        {
            if (exitFee > 0 && !_bank.TryBankDeposit(actor, exitFee))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Organization departure refund requires reconciliation: actor={ToPrettyString(actor):player}, amount={exitFee}");
            Refresh(uid, actor, "Не вдалося зберегти вихід з організації.");
            return;
        }
        _lastDepartureUtc[key] = DateTime.UtcNow;
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"Organization departure: actor={ToPrettyString(actor):player}, organization={org.Id}, fee={exitFee}");
        UpdateAffiliation(actor, key);
        Refresh(uid, actor, "Контракт розірвано. Повторний вступ можливий через 15 хвилин.");
    }

    private void OnSetRole(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetRoleMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            args.Role == OrganizationMemberRole.Owner || !Enum.IsDefined(args.Role))
            return;
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        if (member == null || member.Role == OrganizationMemberRole.Owner || member.Key == org.Owner)
            return;
        if (!Commit(() =>
            {
                member.Role = args.Role;
                member.CustomRoleId = Guid.Empty;
                if (org.DeputyMemberId == member.Id && args.Role != OrganizationMemberRole.Officer)
                    org.DeputyMemberId = Guid.Empty;
            }))
        {
            Refresh(uid, actor, "Не вдалося змінити роль учасника.");
            return;
        }
        SyncActive(member.Key);
        Refresh(uid, actor, "Роль учасника оновлено.");
    }

    private void OnTransferOwnership(EntityUid uid, OrganizationConsoleComponent comp, OrganizationTransferOwnershipMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        if (member == null || member.Role == OrganizationMemberRole.Owner || member.Key == org.Owner)
            return;
        var oldOwner = org.Members[org.Owner];
        if (!Commit(() =>
            {
                oldOwner.Role = OrganizationMemberRole.Officer;
                oldOwner.CustomRoleId = Guid.Empty;
                oldOwner.CustomTitle = string.Empty;
                member.Role = OrganizationMemberRole.Owner;
                member.CustomRoleId = Guid.Empty;
                member.CustomTitle = string.Empty;
                org.Owner = member.Key;
                if (org.DeputyMemberId == member.Id)
                    org.DeputyMemberId = Guid.Empty;
                EndCrisis(org);
            }))
        {
            Refresh(uid, actor, "Не вдалося передати володіння організацією.");
            return;
        }
        SyncActive(member.Key);
        SyncActive(oldOwner.Key);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(actor):player} transferred ownership of {org.Name} ({org.Id}) to {member.Name}");
        Refresh(uid, actor, "Володіння організацією передано.");
    }

    private void OnRemoveMember(EntityUid uid, OrganizationConsoleComponent comp, OrganizationRemoveMemberMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) ||
            !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            !org.Members.TryGetValue(key, out var caller) ||
            !HasPermission(org, caller, OrganizationPermission.Personnel))
            return;
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        if (member == null || member.Id == caller.Id || member.Role == OrganizationMemberRole.Owner ||
            member.Role >= caller.Role || member.Key == org.Owner)
            return;
        if (!Commit(() =>
            {
                org.Members.Remove(member.Key);
                if (org.DeputyMemberId == member.Id)
                    org.DeputyMemberId = Guid.Empty;
            }))
        {
            Refresh(uid, actor, "Не вдалося виключити учасника.");
            return;
        }
        SyncActive(member.Key);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(actor):player} removed {member.Name} from {org.Name} ({org.Id})");
        Refresh(uid, actor, "Учасника виключено.");
    }

    private static bool ValidTitle(string? text) =>
        text is { Length: >= 2 and <= 32 } &&
        text.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '\'' or '’');

    private static string MemberTitle(Organization org, Member member) =>
        // The owner identity is authoritative even for older saves with an overridden title.
        member.Key == org.Owner ? org.OwnerTitle :
        !string.IsNullOrEmpty(member.CustomTitle) ? member.CustomTitle :
        member.Role == OrganizationMemberRole.Member && member.CustomRoleId != Guid.Empty &&
        org.CustomRoles.FirstOrDefault(c => c.Id == member.CustomRoleId) is { } custom ? custom.Title :
        member.Role == OrganizationMemberRole.Owner ? org.OwnerTitle :
        member.Id == org.DeputyMemberId ? org.DeputyTitle :
        member.Role == OrganizationMemberRole.Officer ? org.OfficerTitle : org.MemberTitle;

    private bool IsMemberPresent(CharacterKey key)
    {
        // Do not confuse a currently connected SESSION with an in-world character.
        // A spawned body remains physically present even after the player ghosts
        // or temporarily switches to another character.
        return _identityByBody.Any(entry => entry.Value == key && entry.Key.Valid &&
            !Deleted(entry.Key) &&
            TryComp<Content.Shared.Mobs.Components.MobStateComponent>(entry.Key, out var mobState) &&
            mobState.CurrentState != MobState.Dead);
    }

    private void OnSetTitle(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetTitleMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            !Enum.IsDefined(args.Slot))
            return;
        var title = string.Join(" ", (args.Title ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!ValidTitle(title))
        {
            Refresh(uid, actor, "Посада повинна мати 2–32 символи: літери, цифри, пробіли або дефіси.");
            return;
        }
        if (!Commit(() =>
            {
                switch (args.Slot)
                {
                    case OrganizationTitleSlot.Owner: org.OwnerTitle = title; break;
                    case OrganizationTitleSlot.Deputy: org.DeputyTitle = title; break;
                    case OrganizationTitleSlot.Officer: org.OfficerTitle = title; break;
                    case OrganizationTitleSlot.Member: org.MemberTitle = title; break;
                }
            }))
        {
            Refresh(uid, actor, "Не вдалося оновити назву посади.");
            return;
        }
        Refresh(uid, actor, "Назву посади оновлено.");
    }

    private void OnSetMemberTitle(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetMemberTitleMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        // An individual nickname must never mask the actual owner title.
        if (member == null || member.Key == org.Owner || member.Role == OrganizationMemberRole.Owner)
            return;
        var title = string.Join(" ", (args.Title ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (title.Length > 0 && !ValidTitle(title))
        {
            Refresh(uid, actor, "Некоректна особиста посада: від 2 до 32 символів.");
            return;
        }
        Refresh(uid, actor, Commit(() => member.CustomTitle = title)
            ? "Особисту посаду оновлено." : "Не вдалося змінити посаду.");
    }

    private void OnSetDeputy(EntityUid uid, OrganizationConsoleComponent comp, OrganizationSetDeputyMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org))
            return;
        if (args.Member == Guid.Empty)
        {
            Refresh(uid, actor, Commit(() => org.DeputyMemberId = Guid.Empty)
                ? "Заступника звільнено від повноважень." : "Не вдалося оновити повноваження.");
            return;
        }
        var member = org.Members.Values.FirstOrDefault(m => m.Id == args.Member);
        if (member == null || member.Role == OrganizationMemberRole.Owner || member.Key == org.Owner)
            return;
        if (!Commit(() =>
            {
                member.Role = OrganizationMemberRole.Officer;
                org.DeputyMemberId = member.Id;
            }))
        {
            Refresh(uid, actor, "Не вдалося призначити заступника.");
            return;
        }
        SyncActive(member.Key);
        Refresh(uid, actor, $"{member.Name} призначено заступником.");
    }

    private void OnConfigureBudget(EntityUid uid, OrganizationConsoleComponent comp, OrganizationConfigureBudgetMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Budget,
                out var actor, out var org))
            return;
        var shares = new[] { args.Marketing, args.Operations, args.Security, args.Research };
        var fee = Math.Clamp(org.InitialInvestment / 100, 250, 5_000);
        if (!ValidShares(shares) || org.Treasury < fee ||
            (_recentBudgetChanges.TryGetValue(org.Id, out var last) &&
                _gameTiming.CurTime < last + BudgetChangeCooldown))
        {
            Refresh(uid, actor, "Розподіл має становити 100%, мінімум 5% на напрям. Перевірте кошти та паузу в 10 хв.");
            return;
        }
        if (!Commit(() => { org.BudgetShares = shares; org.Treasury -= fee; org.OperatingCosts += fee; }))
        {
            Refresh(uid, actor, "Не вдалося змінити бюджет.");
            return;
        }
        _recentBudgetChanges[org.Id] = _gameTiming.CurTime;
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor, $"Бюджет змінено, організаційні витрати {fee:N0} $.");
    }

    // Slow but reachable progression through level 100, without int overflow.
    private static int ExperienceNeeded(int level) =>
        OrganizationDevelopmentRules.ExperienceNeeded(level);

    private bool TryWithPermission(EntityUid uid, EntityUid actorUid, Guid orgId,
        OrganizationPermission permission, out EntityUid actor, out Organization org)
    {
        actor = default;
        org = default!;
        if (!AuthorizedBusiness(uid, actorUid) || !TryGetCharacter(actorUid, out var key, out _) ||
            !_organizations.TryGetValue(orgId, out var organization) ||
            !organization.Members.TryGetValue(key, out var member) ||
            !HasPermission(organization, member, permission))
            return false;
        actor = actorUid;
        org = organization;
        return true;
    }

    private bool TryOwner(EntityUid uid, EntityUid actorUid, Guid orgId, out EntityUid actor, out Organization org)
    {
        actor = default;
        org = default!;
        if (!AuthorizedBusiness(uid, actorUid) ||
            !TryGetCharacter(actorUid, out var key, out _) ||
            !_organizations.TryGetValue(orgId, out var organization) || organization.Owner != key)
            return false;
        actor = actorUid;
        org = organization;
        return true;
    }

    private void OnSpawn(PlayerSpawnCompleteEvent args)
    {
        // PlayerSpawnCompleteEvent supplies the BODY'S original profile and job.
        // Never derive a visited body's organization from the current controller.
        if (!_preferences.TryGetCachedPreferences(args.Player.UserId, out var prefs))
            return;
        var identity = Key(args.Player.UserId.ToString(), prefs.SelectedCharacterIndex, args.Profile.Name);
        _identityByBody[args.Mob] = identity;
        _displayNameByBody[args.Mob] = args.Profile.Name;
        if (TryTdfRankFromJob(args.JobId, out var rank, out var grade))
            _tdfRankByBody[args.Mob] = (rank, grade);
        TrackTdfDuty(args.Mob, identity);
        UpdateAffiliation(args.Mob, identity);
        ResolveCrisisForOwner(identity);
    }

    private void OnAttached(PlayerAttachedEvent args)
    {
        if (TryGetCharacter(args.Entity, out var key, out _))
        {
            TrackTdfDuty(args.Entity, key);
            UpdateAffiliation(args.Entity, key);
            ResolveCrisisForOwner(key);
        }
    }

    private void ResolveCrisisForOwner(CharacterKey key)
    {
        var org = FindMembership(key);
        if (org == null || org.Kind != OrganizationKind.Company || org.Owner != key ||
            !org.CrisisActive || !IsMemberPresent(key))
            return;

        if (Commit(() => EndCrisis(org)))
            _adminLog.Add(LogType.Action, LogImpact.Low,
                $"Management crisis ended when the living owner returned: {org.Id}");
    }

    private void SyncActive(CharacterKey key)
    {
        if (!Guid.TryParse(key.UserId, out var userId) ||
            !_players.TryGetSessionById(new NetUserId(userId), out var session) ||
            session.AttachedEntity is not { } actor ||
            !TryGetCharacter(actor, out var actualKey, out _) || actualKey != key)
            return;
        UpdateAffiliation(actor, key);
    }

    private void UpdateAffiliation(EntityUid actor, CharacterKey key)
    {
        TrackTdfDuty(actor, key);
        if (TryGetTdfRank(actor, out _, out _))
        {
            RemComp<OrganizationOwnerBindingComponent>(actor);
            var service = EnsureComp<OrganizationAffiliationComponent>(actor);
            service.OrganizationId = TdfAuthorityId.ToString("N");
            service.OrganizationName = "TDF — служба контролю сектора";
            service.Color = "#D6AD67";
            service.IsOwner = false;
            service.Role = OrganizationMemberRole.Member;
            Dirty(actor, service);
            return;
        }
        var org = FindMembership(key);
        if (org == null)
        {
            RemComp<OrganizationAffiliationComponent>(actor);
            RemComp<OrganizationOwnerBindingComponent>(actor);
            return;
        }

        if (org.Owner == key)
        {
            var binding = EnsureComp<OrganizationOwnerBindingComponent>(actor);
            binding.OrganizationId = org.Id;
            binding.MemberId = org.Members[key].Id;
        }
        else
            RemComp<OrganizationOwnerBindingComponent>(actor);

        // Affiliation is a globally networked component. Removing it for a
        // hidden roster prevents other clients from reading membership, role,
        // ownership or the organization's GUID from the entity state.
        if (!org.RosterPublic)
        {
            RemComp<OrganizationAffiliationComponent>(actor);
            return;
        }
        var component = EnsureComp<OrganizationAffiliationComponent>(actor);
        component.OrganizationId = org.Id.ToString("N");
        component.OrganizationName = org.Name;
        component.Color = org.Color;
        component.IsOwner = org.Owner == key;
        component.Role = org.Members[key].Role;
        Dirty(actor, component);
    }

    private void OnOwnerMobState(EntityUid uid, OrganizationOwnerBindingComponent binding, MobStateChangedEvent args)
    {
        if (!_organizations.TryGetValue(binding.OrganizationId, out var org) ||
            org.Kind != OrganizationKind.Company ||
            org.Members[org.Owner].Id != binding.MemberId ||
            args.OldMobState == args.NewMobState)
            return;

        if (args.NewMobState == MobState.Dead && !org.CrisisActive)
        {
            if (!Commit(() =>
                {
                    org.CrisisActive = true;
                    org.CrisisTicks = 0;
                    org.CrisisSectorBudgetBp = org.SectorInfluenceBp * CrisisLossLimitPercent / 100;
                    org.CrisisIndustryBudgetBp = org.IndustryInfluenceBp * CrisisLossLimitPercent / 100;
                }))
                return;
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"Management crisis started for organization {org.Id} after owner death");
        }
        else if (args.OldMobState == MobState.Dead && args.NewMobState != MobState.Dead && org.CrisisActive)
        {
            if (Commit(() => EndCrisis(org)))
                _adminLog.Add(LogType.Action, LogImpact.Low, $"Management crisis ended after owner revival: {org.Id}");
        }
    }

    private void OnClaimLeadership(EntityUid uid, OrganizationConsoleComponent comp, OrganizationClaimLeadershipMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            org.Kind != OrganizationKind.Company || !org.CrisisActive || org.CrisisTicks < 1 ||
            !org.Members.TryGetValue(key, out var claimant) || claimant.Role != OrganizationMemberRole.Officer ||
            (org.DeputyMemberId != Guid.Empty && claimant.Id != org.DeputyMemberId && org.CrisisTicks < 3))
            return;

        var formerOwner = org.Members[org.Owner];
        if (!Commit(() =>
            {
                formerOwner.Role = OrganizationMemberRole.Officer;
                claimant.Role = OrganizationMemberRole.Owner;
                org.Owner = claimant.Key;
                org.DeputyMemberId = Guid.Empty;
                EndCrisis(org);
            }))
        {
            Refresh(uid, actor, "Не вдалося завершити передачу керівництва.");
            return;
        }

        SyncActive(formerOwner.Key);
        SyncActive(claimant.Key);
        _adminLog.Add(LogType.Action, LogImpact.High,
            $"{ToPrettyString(actor):player} assumed emergency leadership of {org.Name} ({org.Id})");
        Refresh(uid, actor, "Кризу завершено. Ви стали новим керівником компанії.");
    }

    private void OnAttemptAdvertisement(EntityUid uid,
        Content.Server.Advertise.Components.AdvertiseComponent component,
        ref Content.Server.Advertise.EntitySystems.AttemptAdvertiseEvent args)
    {
        if (args.Cancelled || _gameTiming.CurTime < _lastAdvertisement + AdvertisementCooldown ||
            !TryComp<Content.Shared.VendingMachines.VendingMachineComponent>(uid, out var vending) || vending.Broken ||
            (TryComp<Content.Server.Power.Components.ApcPowerReceiverComponent>(uid, out var receiver) && !receiver.Powered))
            return;

        var candidates = _organizations.Values
            .Where(o => o.Kind == OrganizationKind.Company && o.IndustryInfluenceBp >= 25 &&
                o.Treasury >= AdvertisementCost && CompanyRevenueInWindow(o) > 0)
            .OrderBy(o => o.Id).ToArray();
        if (candidates.Length == 0)
            return;

        var weights = candidates.Select(o => Math.Clamp(2 + o.BudgetShares[0] / 5 +
            o.IndustryInfluenceBp / 500, 1, 45)).ToArray();
        var total = weights.Sum();
        if (_random.Next(0, 100) >= Math.Min(30, 5 + total / 10))
            return;

        var roll = _random.Next(0, total);
        var selected = candidates[0];
        for (var i = 0; i < candidates.Length; i++)
        {
            roll -= weights[i];
            if (roll >= 0)
                continue;
            selected = candidates[i];
            break;
        }

        if (!Commit(() =>
            {
                selected.Treasury -= AdvertisementCost;
                selected.OperatingCosts += AdvertisementCost;
            }))
            return;

        _lastAdvertisement = _gameTiming.CurTime;
        args.Cancelled = true;
        _chat.TrySendInGameICMessage(uid,
            $"Реклама: {selected.Name} — послуги в секторі. Дізнайтеся більше через реєстр організацій.",
            Content.Shared.Chat.InGameICChatType.Speak, hideChat: true);
    }

    private IEnumerable<Organization> MarketMembers(Organization target, OrganizationMarketScope scope)
    {
        return scope == OrganizationMarketScope.Sector
            ? _organizations.Values
            : _organizations.Values.Where(o => o.Kind == OrganizationKind.Company && o.Industry == target.Industry);
    }

    private static int InfluenceBp(Organization target, OrganizationMarketScope scope) =>
        scope == OrganizationMarketScope.Sector ? target.SectorInfluenceBp : target.IndustryInfluenceBp;

    private static void ChangeInfluence(Organization target, OrganizationMarketScope scope, int delta)
    {
        if (scope == OrganizationMarketScope.Sector)
            target.SectorInfluenceBp += delta;
        else
            target.IndustryInfluenceBp += delta;
    }

    private void OnInvest(EntityUid uid, OrganizationConsoleComponent comp, OrganizationInvestMessage args)
    {
        if (!TryWithPermission(uid, args.Actor, args.Organization, OrganizationPermission.Investments,
                out var actor, out var organization))
            return;

        if (organization.Kind == OrganizationKind.Company && IsTradingSuspended(organization))
        {
            Refresh(uid, actor, "Інвестиції призупинено до завершення фінансового нагляду.");
            return;
        }
        if (!Enum.IsDefined(args.Scope) || !Enum.IsDefined(args.Focus) ||
            args.Amount is < 100 or > MaxInvestment ||
            (args.Scope == OrganizationMarketScope.Industry && organization.Kind != OrganizationKind.Company))
        {
            Refresh(uid, actor, "Некоректні параметри інвестиції.");
            return;
        }

        if (!_store.Available || organization.Treasury < args.Amount)
        {
            Refresh(uid, actor, "Недостатньо коштів у скарбниці або реєстр недоступний.");
            return;
        }

        if (args.Scope == OrganizationMarketScope.Sector &&
            organization.Kind == OrganizationKind.Company && organization.IndustryInfluenceBp < 25)
        {
            Refresh(uid, actor, "Для виходу на секторний ринок компанії потрібно щонайменше 0,25% впливу у своїй галузі.");
            return;
        }

        if (_recentInvestments.TryGetValue(organization.Id, out var previous) &&
            _gameTiming.CurTime < previous + InvestmentCooldown)
        {
            Refresh(uid, actor, "Наступна інвестиція буде доступна через кілька секунд.");
            return;
        }

        var market = MarketMembers(organization, args.Scope).ToArray();
        var owned = InfluenceBp(organization, args.Scope);
        var occupied = market.Sum(o => InfluenceBp(o, args.Scope));
        var free = Math.Max(0, MarketCapacityBp - occupied);

        var cost = (int) Math.Min(10_000L, 100L + owned / 16 + (long) owned * owned / 40_000);
        var budgetBonus = 75 + organization.BudgetShares[(int) args.Focus];
        var creditPerPoint = Math.Max(1, (cost * 100 + budgetBonus - 1) / budgetBonus);
        var creditPerOpenPoint = creditPerPoint * 2;
        // Competition is represented through demand, costs and free market capacity.
        // A player's investment must never directly subtract another organization's share.
        if (args.Target != null)
        {
            Refresh(uid, actor, "Пряме витіснення конкурентів більше не підтримується.");
            return;
        }
        var maxPoints = Math.Min(80, args.Amount / creditPerPoint);
        var peaceful = Math.Min(Math.Min(maxPoints, free), Math.Max(0, PeacefulExpansionBp - owned));
        var creditsLeft = args.Amount - peaceful * creditPerPoint;
        var open = owned + peaceful >= PeacefulExpansionBp
            ? Math.Min(Math.Min(maxPoints - peaceful, Math.Max(0, free - peaceful)),
                creditsLeft / creditPerOpenPoint)
            : 0;
        var gained = peaceful + open;
        if (gained <= 0)
        {
            Refresh(uid, actor, "Вільної частки ринку немає. Розвивайте виробництво або інвестуйте в інший напрям.");
            return;
        }
        var spent = peaceful * creditPerPoint + open * creditPerOpenPoint;
        if (!Commit(() =>
        {
            organization.Treasury -= spent;
            organization.CapitalSpent += spent;
            ChangeInfluence(organization, args.Scope, gained);
        }))
        {
            Refresh(uid, actor, "Інвестицію не вдалося зберегти, кошти не списано.");
            return;
        }
        CheckConcentration(organization, uid);
        _recentInvestments[organization.Id] = _gameTiming.CurTime;
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"{ToPrettyString(actor):player} invested {spent} in {organization.Name} {args.Scope}, gained {gained} bp from free capacity");
        Refresh(uid, actor, $"Інвестиція: {spent:N0} $, зростання частки ринку +{gained / 100f:0.00}%.");
    }

    public string GetPdaRoleTitle(EntityUid actor)
    {
        if (!TryGetCharacter(actor, out var key, out _) || FindMembership(key) is not { } org)
            return string.Empty;
        return MemberTitle(org, org.Members[key]);
    }

    public bool TryGetPdaDetails(EntityUid actor, out string name, out Color color,
        out OrganizationMemberRole role, out string[] members)
    {
        name = string.Empty;
        color = Color.White;
        role = OrganizationMemberRole.Member;
        members = [];
        if (!TryGetCharacter(actor, out var key, out _))
            return false;
        var org = FindMembership(key);
        if (org == null)
            return false;
        name = org.Name;
        color = Color.FromHex(org.Color);
        role = org.Members[key].Role;
        if (role == OrganizationMemberRole.Owner)
            members = org.Members.Values.OrderBy(m => m.Role).ThenBy(m => m.Name)
                .Select(m => $"{m.Name} ({MemberTitle(org, m)})").ToArray();
        return true;
    }

    // The canonical TDF regulator is a sector authority, not a player-owned organization.
    private void CheckConcentration(Organization org, EntityUid? source = null)
    {
        if (org.Kind != OrganizationKind.Company || !_store.Available ||
            (org.SectorInfluenceBp < 3500 && org.IndustryInfluenceBp < 5000) ||
            _concentrationAlerts.Any(a => a.Organization == org.Id &&
                a.Utc > DateTime.UtcNow - TimeSpan.FromHours(24)))
            return;
        var reason = $"Концентрація ринку: сектор {org.SectorInfluenceBp / 100f:0.0}%, " +
            $"галузь {org.IndustryInfluenceBp / 100f:0.0}%. Перевірити конкурентні умови.";
        var alert = new OrganizationConcentrationRecord
        {
            Utc = DateTime.UtcNow, Organization = org.Id, Company = org.Name, Reason = reason
        };
        if (!Commit(() =>
        {
            _concentrationAlerts.Add(alert);
            if (_concentrationAlerts.Count > 250)
                _concentrationAlerts.RemoveAt(0);
        }))
            return;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"TDF concentration review: {org.Name} ({org.Id}) {reason}");
        // Notify the existing TDF radio channel; no punishment without human review.
        if (_prototypes.TryIndex<RadioChannelPrototype>("Nfsd", out var channel))
        {
            if (source is { } console && console.Valid)
                _radio.SendRadioMessage(console,
                    $"TDF — АНТИМОНОПОЛЬНИЙ НАГЛЯД: {org.Name}. {reason}", channel, console);
            else
            {
                var query = EntityQueryEnumerator<OrganizationConsoleComponent>();
                if (query.MoveNext(out var terminal, out _))
                    _radio.SendRadioMessage(terminal,
                        $"TDF — АНТИМОНОПОЛЬНИЙ НАГЛЯД: {org.Name}. {reason}", channel, terminal);
            }
        }
    }

    // No commercial influence is subtracted when TDF enforcement capacity changes.
    public int CreditTdfContrabandTurnIn(int grossReward)
    {
        if (grossReward < 5 || !_store.Available)
            return 0;
        // 60% of the existing reward is paid into the TDF fund; no money is minted.
        var contribution = Math.Min((int)((long)grossReward * 3 / 5), 100_000);
        if (_tdfTreasury > MaxTreasury - contribution)
            return 0;
        var gainBp = Math.Clamp(grossReward / 200, 1, 90);
        if (!Commit(() =>
            {
                _tdfTreasury += contribution;
                _tdfOperationalBp = Math.Min(MaxTdfOperationalBp, _tdfOperationalBp + gainBp);
            }))
            return 0;
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"TDF contraband restitution: {contribution} transferred from {grossReward} reward, operational +{gainBp}bp");
        return contribution;
    }

    // Called only by the Security shipyard console after the vessel and access were validated.
    public bool TrySpendTdfShuttleBudget(EntityUid buyer, int price)
    {
        if (price <= 0 || !_store.Available || _tdfShuttleReserve < price ||
            !TryGetTdfRank(buyer, out _, out var grade) || grade < 3)
            return false;
        if (!Commit(() => _tdfShuttleReserve -= price))
            return false;
        _adminLog.Add(LogType.Action, LogImpact.High,
            $"TDF shipyard reserve funded purchase {price} by {ToPrettyString(buyer):player}");
        return true;
    }

    private void OnTdfFund(EntityUid uid, OrganizationConsoleComponent comp, OrganizationTdfFundMessage args)
    {
        var actor = args.Actor;
        if (!Authorized(uid, actor) || !IsOriginalController(actor) || !IsTdfTerminal(uid) ||
            !TryGetTdfRank(actor, out _, out var grade) ||
            args.Amount is < 1 or > 1_000_000 || !Enum.IsDefined(args.Action) || !_store.Available)
            return;
        var amount = args.Amount;
        if (args.Action == OrganizationTdfFundAction.Deposit)
        {
            if (_tdfTreasury > MaxTreasury - amount || !_bank.TryBankWithdraw(actor, amount))
            {
                Refresh(uid, actor, "Не вдалося внести кошти до фонду TDF.");
                return;
            }
            if (!Commit(() => _tdfTreasury += amount))
            {
                if (!_bank.TryBankDeposit(actor, amount))
                    _adminLog.Add(LogType.Action, LogImpact.High, $"TDF deposit rollback FAILED for {ToPrettyString(actor):player}: {amount}");
                Refresh(uid, actor, "Не вдалося зберегти платіж TDF.");
                return;
            }
        }
        else
        {
            if (grade < 4 && (!TryGetCharacter(actor, out var dutyKey, out _) ||
                    (TdfRights(dutyKey) & OrganizationTdfServiceRight.FleetBudget) == 0))
                return;
            if (args.Action == OrganizationTdfFundAction.AllocateForShuttles)
            {
                if (_tdfTreasury < amount || _tdfShuttleReserve > MaxTreasury - amount ||
                    !Commit(() => { _tdfTreasury -= amount; _tdfShuttleReserve += amount; }))
                    return;
            }
            else if (_tdfShuttleReserve < amount || _tdfTreasury > MaxTreasury - amount ||
                     !Commit(() => { _tdfShuttleReserve -= amount; _tdfTreasury += amount; }))
                return;
        }
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"TDF funds: {args.Action} amount={amount} by {ToPrettyString(actor):player}");
        Refresh(uid, actor);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        // Per round only up to +1% of earned enforcement capacity is retained.
        // Baseline remains 35% and never consumes companies' market influence.
        Commit(() =>
        {
            _tdfLongTermBp = Math.Min(MaxTdfLongTermBp,
                _tdfLongTermBp + Math.Min(100, _tdfOperationalBp));
            _tdfOperationalBp = 0;
            foreach (var org in _organizations.Values)
                if (org.CrisisActive)
                    EndCrisis(org);
        });
        // Validate stored blueprints again in the next round; cached success is not an outpost spawn.
        _verifiedOutpostBlueprints.Clear();
        ClearRoundOutpostReferences();
        ClearRoundFleetReferences();
        ResetFactionOperationsForRound();
        _factionWreckMapsCreatedThisRound = 0;
        _factionPlanetMapsCreatedThisRound = 0;
        _sectorQuotaClaims.Clear();
        _sectorQuotaOffers.Clear();
        _factionNextTaskAt.Clear();
        _acceptedCargoProofs.Clear();
        _quotaManifestPrintCooldowns.Clear();
        _roundQuotaSerial = 0;
        _tdfDutyRoster.Clear();
        _identityByBody.Clear();
        _displayNameByBody.Clear();
        _tdfRankByBody.Clear();
    }

    private bool IsTdfOfficer(EntityUid actor) => TryGetTdfRank(actor, out _, out _);

    // Job prototypes are canon: rank labels are NOT editable organization titles.
    private bool TryGetTdfRank(EntityUid actor, out string rank, out int grade)
    {
        rank = string.Empty;
        grade = 0;
        // Ghosting does not revoke the mind's old job. The current BODY must be a living
        // non-ghost humanoid; otherwise TDF tools can leak to spectator mode.
        if (!actor.Valid || Deleted(actor) || HasComp<GhostComponent>(actor) ||
            !HasComp<Content.Shared.Mobs.Components.MobStateComponent>(actor))
            return false;
        // Already-spawned bodies keep their own service role even when another
        // mind visits them. A civilian body cannot inherit the visitor's TDF rank.
        if (_identityByBody.ContainsKey(actor))
        {
            if (!_tdfRankByBody.TryGetValue(actor, out var bodyRank))
                return false;
            rank = bodyRank.Rank;
            grade = bodyRank.Grade;
            return true;
        }
        // Fallback used only before the spawn-completed event has stamped the body.
        if (!_minds.TryGetMind(actor, out var mindId, out _) ||
            !_jobs.MindTryGetJob(mindId, out var job) || job is null)
            return false;
        return TryTdfRankFromJob(job.ID.ToString(), out rank, out grade);
    }

    private static bool TryTdfRankFromJob(string? jobId, out string rank, out int grade)
    {
        rank = string.Empty;
        grade = 0;
        switch (jobId)
        {
            case "TdfCadet": rank = "Молодший енфорсер"; grade = 1; break;
            case "TdfEnforcer": rank = "Енфорсер"; grade = 2; break;
            case "TdfMedic": rank = "Медик TDF, енфорсер"; grade = 2; break;
            case "TdfPatrolTeamLeader": rank = "Старший енфорсер"; grade = 3; break;
            case "TdfLieutenant": rank = "Лейтенант"; grade = 4; break;
            case "TdfChiefEnforcer": rank = "Головний енфорсер"; grade = 5; break;
            default: return false;
        }
        return true;
    }

    private OrganizationTdfServiceRight TdfRights(CharacterKey key) =>
        _tdfAssignedRights.GetValueOrDefault(key);

    private void OnTdfRights(EntityUid uid, OrganizationConsoleComponent comp, OrganizationTdfRightsMessage args)
    {
        if (!Authorized(uid, args.Actor) || !IsOriginalController(args.Actor) || !IsTdfTerminal(uid) ||
            !TryGetTdfRank(args.Actor, out _, out var grade) || grade < 5 ||
            (args.Rights & ~OrganizationTdfServiceRight.All) != 0 || !_store.Available)
            return;
        var target = _tdfDutyRoster.Values.FirstOrDefault(member => member.Id == args.Member);
        if (target == null || target.Grade >= grade)
            return;
        if (!Commit(() => _tdfAssignedRights[target.Key] = args.Rights))
            return;
        _adminLog.Add(LogType.Action, LogImpact.High,
            $"TDF authorization changed: {target.Name}, rights={args.Rights} by {ToPrettyString(args.Actor):player}");
        Refresh(uid, args.Actor);
    }

    private void TrackTdfDuty(EntityUid actor, CharacterKey key)
    {
        if (!TryGetTdfRank(actor, out var rank, out var grade))
            return;
        // The same character slot can respawn; the latest body represents that slot.
        var name = Name(actor);
        if (_tdfDutyRoster.TryGetValue(key, out var existing))
        {
            existing.Name = name;
            existing.Body = actor;
            existing.Rank = rank;
            existing.Grade = grade;
        }
        else
            _tdfDutyRoster[key] = new TdfDutyRecord(key, name, actor, rank, grade);
    }

    private bool TdfBodyInSector(TdfDutyRecord record)
    {
        if (Deleted(record.Body))
            return false;
        if (TryComp<Content.Shared.Mobs.Components.MobStateComponent>(record.Body, out var mob) &&
            mob.CurrentState == MobState.Dead)
            return false;
        var xform = Transform(record.Body);
        return xform.MapID != Robust.Shared.Map.MapId.Nullspace;
    }

    private bool TdfBodyControlled(TdfDutyRecord record) =>
        _players.Sessions.Any(session => session.Status == SessionStatus.InGame &&
            session.AttachedEntity == record.Body);

    private OrganizationMemberEntry[] ActiveTdfMembers()
    {
        // Includes TDF people still present in the world after the player takes another role.
        // Historical personnel remain in the roster marked as absent, not as online players.
        return _tdfDutyRoster.Values.OrderByDescending(record => record.Grade)
            .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .Select(record => new OrganizationMemberEntry(record.Id, record.Name,
                record.Grade >= 4 ? OrganizationMemberRole.Officer : OrganizationMemberRole.Member,
                TdfBodyInSector(record), false, record.Rank, 0)
                { TdfRights = TdfRights(record.Key) }).ToArray();
    }

    private bool IsTdfTerminal(EntityUid uid)
    {
        var prototypeId = Prototype(uid)?.ID.ToString();
        return prototypeId is "ComputerTdfMarketOversight" or "ComputerTdfOrganizationConsole";
    }

    private static int InitialQuote(Organization org) => Math.Clamp(
        75 + org.CompanyLevel * 20 + org.SectorInfluenceBp / 25 + org.IndustryInfluenceBp / 25,
        40, MaxQuote);

    // One economic cycle creates a single durable allocation. Equity held by the
    // corporate reserve earns nothing; rounding residuals remain in the treasury.
    private static void AllocateDividends(Organization org, int net)
    {
        if (net <= 0 || org.DividendRatePercent <= 0 || org.SharesIssued <= 0 ||
            org.Shareholders.Count == 0)
            return;

        var pool = Math.Min((long) net * org.DividendRatePercent / 100,
            Math.Max(0, org.Treasury - MinWorkingCapital));
        if (pool <= 0)
            return;

        // Never create a partial distribution if the persistent ledger is full.
        var fresh = org.Shareholders.Count(h => !org.DividendAccounts.Any(a =>
            a.UserId == h.UserId && a.Slot == h.Slot));
        if (org.DividendAccounts.Count + fresh > MaxDividendAccounts)
            return;

        long allocated = 0;
        foreach (var holder in org.Shareholders)
        {
            var amount = pool * holder.Shares / org.SharesIssued;
            if (amount <= 0)
                continue;
            var account = org.DividendAccounts.FirstOrDefault(a =>
                a.UserId == holder.UserId && a.Slot == holder.Slot);
            if (account == null)
            {
                account = new OrganizationDividendAccountRecord
                {
                    UserId = holder.UserId, Slot = holder.Slot, Name = holder.Name
                };
                org.DividendAccounts.Add(account);
            }
            if (account.Available > MaxTreasury - amount)
                continue;
            account.Available += amount;
            allocated += amount;
        }
        org.Treasury -= allocated;
        org.DividendsDistributed += allocated;
    }

    private void OnDividendPolicy(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationDividendPolicyMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            org.Kind != OrganizationKind.Company || IsTradingSuspended(org) ||
            args.Percent is < 0 or > MaxDividendPolicy ||
            args.Percent % 5 != 0)
            return;
        if (!Commit(() => org.DividendRatePercent = args.Percent))
        {
            Refresh(uid, actor, "Не вдалося зберегти дивідендну політику.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.Medium,
            $"Dividend policy changed: {org.Id}, rate={args.Percent}%, owner={ToPrettyString(actor):player}");
        Refresh(uid, actor);
    }

    private void OnDividendClaim(EntityUid uid, OrganizationConsoleComponent comp,
        OrganizationDividendClaimMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out _) ||
            !_store.Available || !_organizations.TryGetValue(args.Organization, out var org) ||
            org.Kind != OrganizationKind.Company)
            return;

        var account = org.DividendAccounts.FirstOrDefault(d =>
            d.UserId == key.UserId && d.Slot == key.Slot);
        if (account == null)
        {
            Refresh(uid, actor, "Для цього персонажа немає нарахованих дивідендів.");
            return;
        }

        var result = SettleDividendPayment(org, account, key, actor, automatic: false);
        var notice = result switch
        {
            DividendPaymentResult.Paid => "Дивіденди зараховано на особистий рахунок.",
            DividendPaymentResult.Held => "Попередня виплата очікує банківської звірки.",
            DividendPaymentResult.Empty => "Доступних до виплати дивідендів немає.",
            DividendPaymentResult.BankUnavailable => "Банківський рахунок не готовий до виплати.",
            DividendPaymentResult.NotSaved => "Не вдалося зареєструвати виплату.",
            DividendPaymentResult.BankRejected => "Банк відхилив переказ. Якщо сума заблокована, зверніться до адміністрації.",
            _ => "Кошти надіслано, але запис виплати потребує перевірки адміністрації."
        };
        if (result == DividendPaymentResult.Paid)
            _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor, notice);
    }

    private static void UpdateQuote(Organization org)
    {
        if (org.Kind != OrganizationKind.Company || org.SharesIssued <= 0)
            return;
        // Price reacts to measured company fundamentals, with a bounded step each economy cycle.
        var target = InitialQuote(org);
        var change = Math.Clamp(target - org.Quote, -Math.Max(2, org.Quote / 10), Math.Max(2, org.Quote / 10));
        org.Quote = Math.Clamp(org.Quote + change, 1, MaxQuote);
        org.PriceHistory.Add(org.Quote);
        if (org.PriceHistory.Count > MaxPriceHistory)
            org.PriceHistory.RemoveAt(0);
    }

    private void OnStockListing(EntityUid uid, OrganizationConsoleComponent comp, OrganizationStockListingMessage args)
    {
        if (!TryOwner(uid, args.Actor, args.Organization, out var actor, out var org) ||
            org.Kind != OrganizationKind.Company || org.SharesListed ||
            IsTradingSuspended(org) ||
            args.Quantity is < 1 or > MaxTradeQuantity || !_store.Available ||
            !TryGetCharacter(actor, out var key, out _))
            return;
        var founder = org.Shareholders.FirstOrDefault(h => h.UserId == key.UserId && h.Slot == key.Slot);
        if (founder == null || founder.Shares < args.Quantity)
        {
            Refresh(uid, actor, "Недостатньо особистих акцій для розміщення на біржі.");
            return;
        }
        if (!Commit(() =>
            {
                founder.Shares -= args.Quantity;
                if (founder.Shares == 0)
                    org.Shareholders.Remove(founder);
                org.TreasuryShares += args.Quantity;
                org.SharesListed = true;
            }))
        {
            Refresh(uid, actor, "Не вдалося опублікувати акції.");
            return;
        }
        _adminLog.Add(LogType.Action, LogImpact.High,
            $"Stock listing opened: org={org.Id}, founder={key.UserId}, listed={args.Quantity}");
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        Refresh(uid, actor);
    }

    private void OnStockOrder(EntityUid uid, OrganizationConsoleComponent comp, OrganizationStockMessage args)
    {
        var actor = args.Actor;
        if (!AuthorizedBusiness(uid, actor) || !TryGetCharacter(actor, out var key, out var name) ||
            !_organizations.TryGetValue(args.Organization, out var org) ||
            org.Kind != OrganizationKind.Company || !_store.Available ||
            IsTradingSuspended(org) || !Enum.IsDefined(args.Order) || args.Quantity is < 1 or > MaxTradeQuantity)
            return;

        var holderKey = (key.UserId, key.Slot);
        if (_stockCooldowns.TryGetValue(holderKey, out var last) &&
            _gameTiming.CurTime < last + StockCooldown)
        {
            Refresh(uid, actor, "Біржа: зачекайте 5 секунд перед наступною операцією.");
            return;
        }
        if ((args.Order is OrganizationStockOrder.Buy or OrganizationStockOrder.Sell) && !org.SharesListed)
        {
            Refresh(uid, actor, "Акції компанії ще не розміщені на відкритій біржі.");
            return;
        }
        var isOwner = org.Owner == key;
        var existing = org.Shareholders.FirstOrDefault(h => h.UserId == key.UserId && h.Slot == key.Slot);
        var quantity = args.Quantity;
        var oldQuote = org.Quote;
        var oldIssued = org.SharesIssued;
        long total = 0;
        if (args.Order == OrganizationStockOrder.Issue)
        {
            if (!isOwner || !org.SharesListed || org.SharesIssued > MaxIssuedShares - quantity)
            {
                Refresh(uid, actor, "Лише власник може випускати нові акції. Ліміт випуску досягнуто.");
                return;
            }
        }
        else
        {
            // Bid/ask spread (5%) prevents instant buy->sell loops.
            var perShare = args.Order == OrganizationStockOrder.Buy
                ? (org.Quote * 105L + 99) / 100
                : Math.Max(1, org.Quote * 95L / 100);
            total = perShare * quantity;
            if (total <= 0 || total > int.MaxValue)
                return;
            if (args.Order == OrganizationStockOrder.Buy)
            {
                if (org.TreasuryShares < quantity || org.Treasury > MaxTreasury - total)
                {
                    Refresh(uid, actor, "Акції недоступні або ліміт скарбниці досягнуто.");
                    return;
                }
            }
            else if (existing == null || existing.Shares - ReservedAskShares(org, key) < quantity || org.Treasury < total)
            {
                Refresh(uid, actor, "Недостатньо акцій або ліквідності компанії для викупу.");
                return;
            }
        }

        // Aggregate successive small orders and reversals, not just one huge sale.
        var recentTrades = _tradeLedger.Where(t => t.Organization == org.Id &&
            t.TraderUserId == key.UserId && t.TraderSlot == key.Slot &&
            t.Utc > DateTime.UtcNow - TimeSpan.FromMinutes(10)).ToArray();
        var combinedQuantity = quantity + recentTrades.Sum(t => (long) t.Quantity);
        var combinedValue = total + recentTrades.Sum(t => t.Total);
        var reversal = args.Order is OrganizationStockOrder.Buy or OrganizationStockOrder.Sell &&
            recentTrades.Any(t => t.Order != args.Order && t.Order != OrganizationStockOrder.Issue);
        // A market alert indicates *review required*, never automatic guilt.
        var flagged = quantity * 100L >= oldIssued * 5L || total >= 5_000 ||
            combinedQuantity * 100L >= oldIssued * 10L || combinedValue >= 10_000 || reversal;
        var reason = flagged ? reversal ? "Швидка зміна напрямку торгів" :
            "Значний разовий або сукупний обсяг за 10 хвилин" : string.Empty;
        var entry = new OrganizationTradeRecord
        {
            Utc = DateTime.UtcNow, Organization = org.Id, Company = org.Name,
            TraderUserId = key.UserId, TraderSlot = key.Slot, TraderName = name,
            Order = args.Order, Quantity = quantity, Price = oldQuote, Total = total,
            Flagged = flagged, Reason = reason
        };

        // Bank debit precedes persistent commit; on error the debit is refunded.
        if (args.Order == OrganizationStockOrder.Buy && !_bank.TryBankWithdraw(actor, (int) total))
        {
            Refresh(uid, actor, "Недостатньо особистих коштів для купівлі акцій.");
            return;
        }
        // During buyback, credit first, then roll back credit if persistence fails.
        if (args.Order == OrganizationStockOrder.Sell && !_bank.TryBankDeposit(actor, (int) total))
        {
            Refresh(uid, actor, "Банк не прийняв виплату за акції.");
            return;
        }

        var success = Commit(() =>
        {
            if (args.Order == OrganizationStockOrder.Issue)
            {
                org.SharesIssued += quantity;
                org.TreasuryShares += quantity;
                // Dilution is real; issuance does not create money from nowhere.
                org.Quote = Math.Max(1, (int) ((long) org.Quote * oldIssued / org.SharesIssued));
            }
            else if (args.Order == OrganizationStockOrder.Buy)
            {
                org.TreasuryShares -= quantity;
                org.Treasury += total;
                if (existing != null)
                {
                    existing.Shares += quantity;
                    existing.Name = name;
                }
                else
                    org.Shareholders.Add(new OrganizationShareholderRecord
                    {
                        UserId = key.UserId, Slot = key.Slot, Name = name, Shares = quantity
                    });
            }
            else
            {
                existing!.Shares -= quantity;
                if (existing.Shares == 0)
                    org.Shareholders.Remove(existing);
                org.TreasuryShares += quantity;
                org.Treasury -= total;
            }
            // Volume impact is capped; company fundamentals dominate the long-term quote.
            if (args.Order != OrganizationStockOrder.Issue)
            {
                var direction = args.Order == OrganizationStockOrder.Buy ? 1 : -1;
                org.Quote = Math.Clamp(org.Quote + direction * Math.Max(1, org.Quote * quantity / Math.Max(250, oldIssued * 4)), 1, MaxQuote);
            }
            org.PriceHistory.Add(org.Quote);
            if (org.PriceHistory.Count > MaxPriceHistory)
                org.PriceHistory.RemoveAt(0);
            _tradeLedger.Add(entry);
            if (_tradeLedger.Count > MaxTradeLedger)
            {
                // Prefer evicting routine trades; retained red flags survive later rounds.
                var firstRoutine = _tradeLedger.FindIndex(t => !t.Flagged);
                _tradeLedger.RemoveAt(firstRoutine >= 0 ? firstRoutine : 0);
            }
        });
        if (!success)
        {
            if (args.Order == OrganizationStockOrder.Buy && !_bank.TryBankDeposit(actor, (int) total) ||
                args.Order == OrganizationStockOrder.Sell && !_bank.TryBankWithdraw(actor, (int) total))
                _adminLog.Add(LogType.Action, LogImpact.High,
                    $"Stock transaction bank compensation FAILED: org={org.Id}, amount={total}, player={key.UserId}");
            Refresh(uid, actor, "Операцію не збережено. Перевірте журнал банку при невдалому поверненні.");
            return;
        }
        _stockCooldowns[holderKey] = _gameTiming.CurTime;
        _audio.PlayEntity(MoneyConfirmSound, actor, uid);
        _adminLog.Add(LogType.Action, flagged ? LogImpact.High : LogImpact.Medium,
            $"Stock {args.Order}: {ToPrettyString(actor):player}, {org.Id}, quantity={quantity}, total={total}, price={oldQuote}, flag={flagged}");
        if (flagged && (!_recentTdfWarnings.TryGetValue(org.Id, out var warned) ||
                _gameTiming.CurTime >= warned + TimeSpan.FromMinutes(2)))
        {
            if (_prototypes.TryIndex<RadioChannelPrototype>("Nfsd", out var channel))
            {
                _radio.SendRadioMessage(uid,
                    $"TDF — РИНКОВИЙ НАГЛЯД: {org.Name}, {args.Order}, {quantity} акцій. Відкрийте журнал перевірок у реєстрі.",
                    channel, uid);
                _recentTdfWarnings[org.Id] = _gameTiming.CurTime;
            }
        }
        Refresh(uid, actor);
    }

    private void Refresh(EntityUid console, EntityUid actor, string notice = "")
    {
        // Opening-event order differs across engine versions; do not require IsUiOpen
        // here. Incoming mutation messages are authenticated by their own handlers.
        if (!actor.Valid)
            return;
        if (!TryGetCharacter(actor, out var key, out _))
        {
            _ui.ServerSendUiMessage(console, OrganizationConsoleUiKey.Key,
                new OrganizationPrivateStateMessage(
                    new OrganizationConsoleState([], null, null, null, [], [], string.Empty, 0, 0, 0,
                        [25, 25, 25, 25])), actor);
            return;
        }
        UpdateAffiliation(actor, key);
        var onTdfDuty = TryGetTdfRank(actor, out var tdfRank, out var tdfGrade);
        var associated = onTdfDuty ? FindMembership(key) : null;
        var membership = onTdfDuty ? null : FindMembership(key);
        Member? caller = membership != null ? membership.Members[key] : null;
        var owned = membership?.Owner == key ? membership : null;
        var management = caller != null ? membership : null;
        var managerPermissions = caller != null && membership != null
            ? EffectivePermissions(membership, caller) : OrganizationPermission.None;
        var canOperate = IsOriginalController(actor);
        var organizations = _organizations.Values.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new OrganizationEntry(o.Id, o.Name,
                o.RosterPublic || (canOperate && o.Members.ContainsKey(key)) ? o.Members[o.Owner].Name : "Приховано",
                o.Kind, o.Industry, o.IsPrivate, o.EntryFee, o.Color, o.Members.Count,
                o.Applications.Values.Any(a => a.Key == key),
                o.SectorInfluenceBp / 100f,
                // Faction network ownership is presented only in the separate
                // network statistic, never in the sector influence registry.
                o.IndustryInfluenceBp / 100f, o.CapitalSpent, o.CrisisActive)
            {
                RosterPublic = o.RosterPublic,
                FactionFocus = o.FactionFocus,
                FactionFoci = (OrganizationFactionFocus[])o.FactionFoci.Clone(),
                InsolvencyState = o.InsolvencyState,
                MissedUpkeepCycles = o.MissedUpkeepCycles,
                Description = o.Description,
                DiscordLink = o.DiscordLink,
                Banner = (byte[])o.Banner.Clone()
            }).ToArray();
        var tdfMembers = ActiveTdfMembers();
        var tdfInSector = _tdfDutyRoster.Values.Count(TdfBodyInSector);
        var tdfConnected = _tdfDutyRoster.Values.Count(TdfBodyControlled);
        var tdfTerminal = IsTdfTerminal(console);
        var myTdfRights = onTdfDuty ? TdfRights(key) : OrganizationTdfServiceRight.None;
        var tdfCanAudit = tdfGrade >= 3 || (myTdfRights & OrganizationTdfServiceRight.Audit) != 0;
        var tdfCanFleet = tdfGrade >= 4 || (myTdfRights & OrganizationTdfServiceRight.FleetBudget) != 0;
        var tdfCanPersonnel = tdfGrade >= 4 || (myTdfRights & OrganizationTdfServiceRight.Personnel) != 0;
        var tdfAccess = onTdfDuty && tdfTerminal && canOperate && tdfCanAudit;

        organizations = [.. organizations, new OrganizationEntry(
            TdfAuthorityId, "TDF — служба контролю сектора", "Сектор", OrganizationKind.Faction,
            OrganizationIndustry.None, true, 0, "#D6AD67", tdfMembers.Length, false,
            TdfSectorMandatePercent + (_tdfOperationalBp + _tdfLongTermBp) / 100f,
            0f, 0, false)];
        var members = (canOperate ? management : null)?.Members.Values.OrderByDescending(m => m.Role).ThenBy(m => m.Name)
            .Select(m => new OrganizationMemberEntry(m.Id, m.Name, m.Role,
                IsMemberPresent(m.Key), management!.DeputyMemberId == m.Id, MemberTitle(management!, m), m.Distinction)
                { CustomRoleId = m.CustomRoleId }).ToArray()
            ?? (onTdfDuty ? tdfMembers : []);
        var applications = management != null &&
            (managerPermissions & OrganizationPermission.Applications) != 0
            ? management.Applications.Values.OrderBy(a => a.Name)
                .Select(a => new OrganizationApplicationEntry(a.Id, a.Name, a.QuotedFee)).ToArray()
            : Array.Empty<OrganizationApplicationEntry>();
        var personalBalance = canOperate && TryComp<BankAccountComponent>(actor, out var bankAccount)
            ? bankAccount.Balance
            : 0;
        var state = new OrganizationConsoleState(organizations,
            onTdfDuty ? TdfAuthorityId : membership?.Id, owned?.Id,
            onTdfDuty ? (tdfGrade >= 4 ? OrganizationMemberRole.Officer : OrganizationMemberRole.Member) : caller?.Role,
            members, applications, notice, membership?.Treasury ?? 0, personalBalance,
            membership?.InitialInvestment ?? 0,
            membership is null ? [25, 25, 25, 25] : (int[]) membership.BudgetShares.Clone())
        {
            GrossRevenue = membership?.GrossRevenue ?? 0,
            OperatingCosts = membership?.OperatingCosts ?? 0,
            DailyGrossRevenue = membership == null ? 0 : CompanyRevenueInWindow(membership),
            DailyDemandRemaining = AvailableSectorDemand(),
            DailyCompanyLimit = DailyCompanyRevenueLimit,
            OutpostDeploymentError = membership != null && _outpostDeploymentErrors.TryGetValue(membership.Id, out var deploymentError)
                ? deploymentError : string.Empty,
            SectorQuotas = membership == null || !canOperate || onTdfDuty ? [] : BuildSectorQuotaBoard(membership, key),
            FactionAuthorityPoints = membership?.FactionAuthorityPoints ?? 0,
            FactionNetworkSharePercent = membership is { Kind: OrganizationKind.Faction } shareOrg
                ? FactionNetworkSharePercent(shareOrg) : 0f,
            FactionNextTaskSeconds = membership is { Kind: OrganizationKind.Faction } cooldownOrg
                ? FactionNextTaskSeconds(cooldownOrg) : 0,
            FactionOperationsCompleted = membership?.FactionOperationsCompleted ?? 0,
            FactionNodeCount = membership?.FactionNodes.Count ?? 0,
            FactionFindingCount = membership?.FactionFindings.Count ?? 0,
            FactionFindings = membership is { Kind: OrganizationKind.Faction }
                ? membership.FactionFindings.OrderByDescending(f => f.RecordedUtc).Take(8)
                    .Select(f => new OrganizationFactionFindingEntry(f.Id, f.SiteCode,
                        f.SiteName, f.RecordedUtc)).ToArray()
                : [],
            FactionSignalSeconds = membership is { Kind: OrganizationKind.Faction }
                ? FactionSignalSeconds(membership, _sectorQuotaClaims.Values.FirstOrDefault(c =>
                    c.Organization == membership.Id && !c.Completed && c.FactionSiteBound))
                : FactionAntennaBaseSignalSeconds,
            FactionNodes = membership is { Kind: OrganizationKind.Faction }
                ? membership.FactionNodes.OrderByDescending(n => n.EstablishedUtc).Take(12)
                    .Select(n => new OrganizationFactionNodeEntry(n.Id, n.AssignmentCode,
                        n.Kind, n.EstablishedUtc)
                    {
                        SiteCode = n.SiteCode,
                        ActiveThisRound = FactionNodeOnlineThisRound(n.Id),
                        RoundLocation = FactionNodeRoundLocation(n.Id)
                    }).ToArray() : [],
            QuotaIncomeBonusPercent = membership?.QuotaIncomeBonusPercent ?? 0,
            QuotaBonusCycles = membership?.QuotaBonusCycles ?? 0,
            FleetBlueprintName = membership?.FleetBlueprintName ?? string.Empty,
            FleetListingName = membership != null && !string.IsNullOrWhiteSpace(membership.FleetListingName)
                ? membership.FleetListingName : membership?.FleetBlueprintName ?? string.Empty,
            FleetListingDescription = membership?.FleetListingDescription ?? string.Empty,
            FleetRentalPrice = membership?.FleetRentalPrice ?? OrganizationFleetRules.DefaultRentalPrice,
            FleetRenterName = membership != null && _fleetRenterNames.TryGetValue(membership.Id, out var renterName)
                ? renterName : string.Empty,
            FleetRenterIsMe = membership != null &&
                _fleetIssuedRenters.TryGetValue(membership.Id, out var rentingCharacter) &&
                key == rentingCharacter,
            FleetShuttleReturned = membership != null && _fleetReturnedThisRound.Contains(membership.Id),
            FleetBlueprintReady = membership != null && IsFleetBlueprintReady(membership),
            FleetShuttleIssued = membership != null && _fleetUsedThisRound.Contains(membership.Id),
            FleetShuttlePresent = membership != null && IsFleetShuttlePresent(membership.Id),
            FleetIssuedName = membership != null && _fleetIssuedNames.TryGetValue(membership.Id, out var shipName)
                ? shipName : string.Empty,
            FleetError = membership != null && _fleetErrors.TryGetValue(membership.Id, out var fleetError)
                ? fleetError : string.Empty,
            CrisisActive = membership?.CrisisActive ?? false,
            CrisisTicks = membership?.CrisisTicks ?? 0,
            InsolvencyState = membership?.InsolvencyState ?? OrganizationInsolvencyState.Active,
            MissedUpkeepCycles = membership?.MissedUpkeepCycles ?? 0,
            LiquidationArchive = _liquidationArchive.OrderByDescending(entry => entry.ClosedUtc)
                .Select(entry => new OrganizationLiquidationEntry(entry.Id, entry.Name,
                    entry.ClosedUtc, entry.Reason)).ToArray(),
            DeputyMemberId = membership?.DeputyMemberId == Guid.Empty ? null : membership?.DeputyMemberId,
            OwnerTitle = membership?.OwnerTitle ?? "Власник",
            DeputyTitle = membership?.DeputyTitle ?? "Заступник",
            OfficerTitle = membership?.OfficerTitle ?? "Офіцер",
            MemberTitle = membership?.MemberTitle ?? "Учасник",
            CompanyLevel = membership?.CompanyLevel ?? 1,
            CompanyExperience = membership?.CompanyExperience ?? 0,
            NextLevelExperience = membership == null ? 0 : ExperienceNeeded(membership.CompanyLevel),
            CanManagePersonnel = (canOperate && (managerPermissions & OrganizationPermission.Personnel) != 0) ||
                (onTdfDuty && canOperate && tdfCanPersonnel && tdfTerminal),
            UserPermissions = canOperate ? managerPermissions : OrganizationPermission.None,
            DeputyPermissions = membership?.DeputyPermissions ?? OrganizationPermission.None,
            OfficerPermissions = membership?.OfficerPermissions ?? OrganizationPermission.None,
            MemberPermissions = membership?.MemberPermissions ?? OrganizationPermission.None,
            CanClaimLeadership = caller is { Role: OrganizationMemberRole.Officer } && membership != null &&
                (membership.DeputyMemberId == Guid.Empty || caller!.Id == membership.DeputyMemberId ||
                    membership.CrisisTicks >= 3),
            OnlineMembers = onTdfDuty ? tdfInSector :
                management?.Members.Values.Count(m => IsMemberPresent(m.Key)) ?? 0,
            OperatingPolicy = membership?.OperatingPolicy ?? OrganizationOperatingPolicy.Balanced,
            LastCycleGross = membership?.LastCycleGross ?? 0,
            LastCycleCosts = membership?.LastCycleCosts ?? 0,
            LastCycleNet = membership?.LastCycleNet ?? 0,
            LastCycleDemand = membership?.LastCycleDemand ?? 0,
            LastCycleOutcome = membership?.LastCycleOutcome ?? 0,
            CompletedEconomyCycles = membership?.CompletedEconomyCycles ?? 0,
            LastEconomyUtc = membership?.LastEconomyUtc ?? default,
            EconomyRunning = _store.Available && _players.Sessions.Any(session =>
                session.Status == SessionStatus.InGame && session.AttachedEntity is { Valid: true }),
            NextEconomySeconds = Math.Max(0, (int) Math.Ceiling((_nextEconomyCycle - _gameTiming.CurTime).TotalSeconds)),
            ActiveDemandCycles = _activeDemandCycles,
            MaxActiveDemandCycles = MaxActiveDemandCycles,
            LimitOrders = _organizations.Values.Where(o => o.Kind == OrganizationKind.Company)
                .SelectMany(o => o.MarketOrders.Where(order => order.State == OrganizationLimitOrderState.Open)
                    .OrderBy(order => order.CreatedUtc).Take(32)
                    .Select(order => new OrganizationLimitOrderEntry(order.Id, o.Id, order.Side,
                        order.Price, order.Remaining)
                    {
                        TraderName = order.Name,
                        IsMine = canOperate && !onTdfDuty &&
                            order.UserId == key.UserId && order.Slot == key.Slot &&
                            order.CharacterName == key.CharacterName
                    }))
                .ToArray(),
            Stocks = _organizations.Values.Where(org => org.Kind == OrganizationKind.Company)
                .OrderBy(org => org.Name)
                .Select(org => new OrganizationStockEntry(org.Id, org.Name, org.Color, org.Quote,
                    org.SharesIssued, org.SharesListed ? org.TreasuryShares : 0,
                    org.Shareholders.Where(h => h.UserId == key.UserId && h.Slot == key.Slot).Sum(h => h.Shares),
                    (org.SectorInfluenceBp + org.IndustryInfluenceBp) / 100f,
                    org.PriceHistory.ToArray())
                    {
                      Listed = org.SharesListed,
                      TradingSuspended = org.InsolvencyState != OrganizationInsolvencyState.Active,
                      UnreservedOwned = Math.Max(0,
                          org.Shareholders.Where(h => h.UserId == key.UserId && h.Slot == key.Slot)
                              .Sum(h => h.Shares) - ReservedAskShares(org, key))
                    })
                .ToArray(),
            TdfOnDuty = onTdfDuty,
            TdfRank = onTdfDuty ? tdfRank : string.Empty,
            ViewingAnotherCharacter = !canOperate,
            ViewedBody = actor.ToString(),
            AssociatedOrganization = associated?.Id,
            RosterPublic = membership?.RosterPublic ?? true,
            PayrollPerMember = membership?.PayrollPerMember ?? 0,
            PayrollAutomatic = membership?.PayrollAutomatic ?? false,
            PayrollProfitPercent = membership?.PayrollProfitPercent ?? 30,
            CustomRoles = membership?.CustomRoles.Select(c => new OrganizationCustomRoleEntry(c.Id, c.Title, c.Permissions)).ToArray() ?? [],
            LogisticsLevel = membership?.LogisticsLevel ?? 0,
            ResearchFacilityLevel = membership?.ResearchFacilityLevel ?? 0,
            InfrastructureLevel = membership?.InfrastructureLevel ?? 0,
            ServiceNetworkLevel = membership?.ServiceNetworkLevel ?? 0,
            RiskManagementLevel = membership?.RiskManagementLevel ?? 0,
            LogisticsTier2Level = membership?.LogisticsTier2Level ?? 0,
            ResearchTier2Level = membership?.ResearchTier2Level ?? 0,
            InfrastructureTier2Level = membership?.InfrastructureTier2Level ?? 0,
            ServiceNetworkTier2Level = membership?.ServiceNetworkTier2Level ?? 0,
            RiskManagementTier2Level = membership?.RiskManagementTier2Level ?? 0,
            OutpostBlueprintName = membership?.OutpostBlueprintName ?? string.Empty,
            OutpostBlueprintUtc = membership?.OutpostBlueprintUtc ?? default,
            OutpostBlueprintReady = membership != null && IsOutpostBlueprintReady(membership),
            OutpostBlueprintIsActive = membership != null &&
                IsValidOutpostHash(membership.OutpostActiveHash) &&
                membership.OutpostBlueprintHash == membership.OutpostActiveHash,
            OutpostOwned = membership != null && IsValidOutpostHash(membership.OutpostActiveHash),
            OutpostPublicName = membership?.OutpostPublicName ?? string.Empty,
            OutpostPublicDescription = membership?.OutpostPublicDescription ?? string.Empty,
            OutpostAllowVisitors = membership?.OutpostAllowVisitors ?? false,
            OutpostActiveName = membership?.OutpostActiveName ?? string.Empty,
            OutpostDeployed = membership != null && IsOutpostPhysicallyPresent(membership.Id),
            OutpostReplacementName = membership?.OutpostReplacementName ?? string.Empty,
            OutpostReplacementReadyUtc = membership != null &&
                IsValidOutpostHash(membership.OutpostReplacementHash)
                ? membership.OutpostReplacementRequestedUtc + OutpostReplacementDelay : default,
            PayrollAccrued = membership?.PayrollAccrued ?? 0,
            MyPayrollAvailable = membership?.PayrollAccounts.FirstOrDefault(p =>
                p.UserId == key.UserId && p.Slot == key.Slot)?.Available ?? 0,
            MyPayrollPaid = membership?.PayrollAccounts.FirstOrDefault(p =>
                p.UserId == key.UserId && p.Slot == key.Slot)?.Paid ?? 0,
            DividendRatePercent = membership?.DividendRatePercent ?? 0,
            DividendsDistributed = membership?.DividendsDistributed ?? 0,
            Shareholders = membership?.Kind == OrganizationKind.Company
                ? membership.Shareholders.OrderByDescending(h => h.Shares)
                    .Select(h => new OrganizationShareholderEntry(h.Name, h.Shares)).ToArray()
                : Array.Empty<OrganizationShareholderEntry>(),
            CorporateVotes = _organizations.Values.Where(o => o.Kind == OrganizationKind.Company)
                .SelectMany(o => o.Votes.OrderByDescending(v => v.CreatedUtc).Take(6)
                    .Select(v => new OrganizationVoteEntry(v.Id, o.Id, v.Topic, v.Value,
                        v.State == OrganizationVoteState.Open && v.ExpiresUtc <= DateTime.UtcNow
                            ? OrganizationVoteState.Expired : v.State,
                        v.ExpiresUtc, BallotWeight(v, true), BallotWeight(v, false),
                        EligibleVotes(v), v.State == OrganizationVoteState.Open &&
                            v.ExpiresUtc > DateTime.UtcNow && canOperate && !onTdfDuty &&
                            v.Eligible.Any(e => e.UserId == key.UserId && e.Slot == key.Slot) &&
                            !v.Ballots.Any(b => b.UserId == key.UserId && b.Slot == key.Slot),
                        v.Ballots.Any(b => b.UserId == key.UserId && b.Slot == key.Slot))))
                .ToArray(),
            TdfAuthorized = tdfAccess,
            TdfCanResolveCases = tdfAccess && (tdfGrade >= 4 ||
                (myTdfRights & OrganizationTdfServiceRight.Audit) != 0),
            TdfCases = tdfAccess ? _tdfCases.OrderByDescending(c => c.CreatedUtc)
                .Take(100).Select(c => new OrganizationTdfCaseEntry
                {
                    Id = c.Id, SourceKey = c.SourceKey, Organization = c.Organization, Company = c.Company,
                    Subject = c.Subject, Reason = c.Reason, CreatedUtc = c.CreatedUtc,
                    Status = c.Status,
                    LastOfficer = c.LastOfficer, DecisionNote = c.DecisionNote,
                    History = c.History.TakeLast(12)
                        .Select(a => $"{a.Utc:yyyy-MM-dd HH:mm} UTC — {a.Officer}: {a.Description}")
                        .ToArray()
                }).ToArray() : [],
            TdfTerminal = tdfTerminal,
            TdfCanManagePersonnel = onTdfDuty && tdfTerminal && canOperate && tdfCanPersonnel,
            TdfCanAssignRights = onTdfDuty && tdfTerminal && canOperate && tdfGrade >= 5,
            MyTdfRights = myTdfRights,
            TdfPersonnelInRoster = tdfMembers.Length,
            TdfPersonnelInSector = tdfInSector,
            TdfPersonnelConnected = tdfConnected,
            TdfTreasury = onTdfDuty && tdfTerminal && canOperate ? _tdfTreasury : 0,
            TdfShuttleReserve = onTdfDuty && tdfTerminal && canOperate ? _tdfShuttleReserve : 0,
            TdfOperationalPercent = _tdfOperationalBp / 100f,
            TdfLongTermPercent = _tdfLongTermBp / 100f,
            TdfCanAllocateBudget = onTdfDuty && tdfTerminal && canOperate && tdfCanFleet,
            TdfAlerts = tdfAccess
                ? _tradeLedger.TakeLast(100).Select(t =>
                    new OrganizationTradeAlertEntry(t.Utc, t.Company, t.TraderName,
                        $"{t.Order}: {t.Quantity} акцій, {t.Total:N0} $" +
                        (t.Flagged ? $" — ПЕРЕВІРКА: {t.Reason}" : string.Empty), t.Flagged)
                        { SourceKey = t.Flagged ? TdfTradeSourceKey(t) : string.Empty })
                    .Concat(_concentrationAlerts.TakeLast(100).Select(a =>
                        new OrganizationTradeAlertEntry(a.Utc, a.Company, "Секторна антимонопольна служба",
                            a.Reason, true) { SourceKey = TdfConcentrationSourceKey(a) }))
                    .OrderByDescending(a => a.Utc).Take(100).ToArray()
                : Array.Empty<OrganizationTradeAlertEntry>()
        };
        // Do NOT use SetUiState here: that publishes the last caller's private
        // data to every player subscribed to the same organization console.
        _ui.ServerSendUiMessage(console, OrganizationConsoleUiKey.Key,
            new OrganizationPrivateStateMessage(state), actor);
    }
}
