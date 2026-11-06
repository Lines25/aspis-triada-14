using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Utility;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Client.UserInterface.CustomControls;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Aspis.Organizations;

public sealed class OrganizationConsoleWindow : DefaultWindow
{
    public event Action<string, OrganizationKind, OrganizationIndustry, bool, int, string, int, int, int, int, int>? CreateRequested;
    public event Action<Guid>? JoinRequested;
    public event Action<Guid, bool>? ReviewRequested;
    public event Action<Guid, bool, int, string, bool, string, byte[], bool>? ConfigureRequested;
    public event Action<Guid, int, bool, int>? PayrollPolicyRequested;
    public event Action<Guid, Guid, string, OrganizationPermission>? SaveCustomRoleRequested;
    public event Action<Guid, Guid, Guid>? AssignCustomRoleRequested;
    public event Action<Guid, OrganizationDevelopmentBranch, int>? DevelopmentRequested;
    public event Action? LeaveRequested;
    public event Action<Guid, Guid, OrganizationMemberRole>? SetRoleRequested;
    public event Action<Guid, Guid>? TransferOwnershipRequested;
    public event Action<Guid, Guid>? RemoveMemberRequested;
    public event Action? RefreshRequested;
    public event Action<Guid, int, bool>? TransferRequested;
    public event Action<Guid, OrganizationMarketScope, OrganizationInvestmentFocus, int, Guid?>? InvestRequested;
    public event Action<Guid>? FulfillOrderRequested;
    public event Action<Guid, int, int>? QuotaClaimRequested;
    public event Action<Guid, int, int>? QuotaCompleteRequested;
    public event Action<Guid, int, int>? QuotaReleaseRequested;
    public event Action<Guid, string>? FleetBlueprintRequested;
    public event Action<Guid>? FleetRentRequested;
    public event Action<Guid, string, string, int>? FleetListingRequested;
    public event Action<Guid>? FleetReturnRequested;
    private string _fleetLocalNotice = string.Empty;
    private bool _fleetRentConfirm;
    public void SetFleetLocalNotice(string message)
    {
        _fleetLocalNotice = message;
        if (_activeTab == ConsoleTab.Services)
            RefreshServices();
        else if (_activeTab == ConsoleTab.Management &&
                 _managementSection == ManagementSection.Permissions)
            RefreshPermissions();
    }
    public event Action<Guid>? ClaimLeadershipRequested;
    public event Action<Guid, Guid>? SetDeputyRequested;
    public event Action<Guid, OrganizationTitleSlot, string>? SetTitleRequested;
    public event Action<Guid, Guid, string>? SetMemberTitleRequested;
    public event Action<Guid, int, int, int, int>? ConfigureBudgetRequested;
    public event Action<Guid, OrganizationOperatingPolicy>? SetOperatingPolicyRequested;
    public event Action<Guid, OrganizationPermissionTier, OrganizationPermission>? SetPermissionsRequested;
    public event Action<Guid, Guid, byte>? SetDistinctionRequested;
    public event Action<Guid, OrganizationStockOrder, int>? StockOrderRequested;
    public event Action<Guid, int>? StockListingRequested;
    public event Action<OrganizationTdfFundAction, int>? TdfFundRequested;
    public event Action<Guid, OrganizationTdfServiceRight>? TdfRightsRequested;
    public event Action<string>? TdfOpenCaseRequested;
    public event Action<Guid, OrganizationTdfCaseStatus, string>? TdfResolveCaseRequested;
    public event Action<Guid, int>? DividendPolicyRequested;
    public event Action<Guid>? DividendClaimRequested;
    public event Action<Guid, OrganizationLimitOrderSide, int, int>? LimitOrderRequested;
    public event Action<Guid, Guid>? LimitCancelRequested;
    public event Action<Guid>? MarketClaimRequested;
    public event Action? BannerChooseRequested;
    public event Action<Guid, OrganizationVoteTopic, int>? ProposeVoteRequested;
    public event Action<Guid, Guid, bool>? CastVoteRequested;
    // Only local saved-ship paths appear in the selector; the server gets file contents, never paths.
    public Func<string[]>? GetOutpostBlueprintFiles;
    public event Action<Guid, string>? OutpostBlueprintRequested;
    public event Action<Guid>? OutpostPurchaseRequested;
    public event Action<Guid>? OutpostReplacementRequested;
    public event Action<Guid>? OutpostCancelReplacementRequested;
    public event Action<Guid>? OutpostRedeployRequested;
    public event Action<Guid, OrganizationOutpostAccessTier, bool>? OutpostToolRequested;
    public event Action<Guid, string, string, bool>? OutpostListingRequested;
    private bool _outpostPurchaseConfirm;
    private bool _outpostReplaceConfirm;
    private string _outpostLocalNotice = string.Empty;
    public void SetOutpostLocalNotice(string text)
    {
        _outpostLocalNotice = text;
        if (_activeTab == ConsoleTab.Management && _managementSection == ManagementSection.Overview)
            RefreshManagement();
    }


    private readonly HashSet<string> _expandedFactionStepLists = new();
    private readonly HashSet<string> _expandedFactionStepDetails = new();

    private enum ConsoleTab
    {
        Registry,
        Analytics,
        Management,
        Creation,
        Services,
        Tdf
    }

    private enum ManagementSection { Overview, Personnel, Finance, Permissions, Shares }

    private enum SortMode
    {
        InfluenceDesc,
        InfluenceAsc,
        NameAsc,
        NameDesc,
        MembersDesc,
        MembersAsc
    }

    private readonly Label _notice = new();
    private readonly Label _personalBalance = new();
    private readonly Label _creationError = new();
    private readonly OrganizationBudgetChart _budgetChart = new();
    private readonly LineEdit _investment = new() { Text = "0", PlaceHolder = "Початковий капітал" };
    private OrganizationKind? _creationKind;
    private OrganizationKind? _renderedCreationKind;
    private bool? _renderedCreationHasMembership;
    private readonly LineEdit _search = new() { PlaceHolder = "Пошук організації" };
    private readonly OptionButton _typeFilter = new();
    private readonly OptionButton _sortFilter = new();
    private readonly BoxContainer _registryList = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 4 };
    private readonly OptionButton _marketSelector = new();
    private readonly BoxContainer _analyticsBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly BoxContainer _sharesBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly BoxContainer _tdfBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly BoxContainer _servicesBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8 };
    private readonly BoxContainer _quotaShuttlesBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8 };
    private readonly BoxContainer _quotaActionsBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 8 };
    private bool _outpostOverviewExpanded = true;
    private bool _assetsFleetExpanded = true;
    private bool _assetsBranchesInfo;
    private readonly OptionButton _accessRoleSelector = new();
    private readonly OptionButton _stockSelector = new();
    private Guid? _selectedStock;
    private bool _chartRelativeMode = true;
    // UI-only market filters. No extra game state or server handlers.
    private Guid? _marketOrderCompany;
    private int? _preparedOrderQuantity;
    private int? _preparedOrderPrice;
    // Persist the trade-side selector across analytics refreshes. UI-only setting.
    private bool _tradeSellMode;
    private readonly HashSet<Guid> _hiddenStocks = new();
    private Guid? _previousMembership;
    private bool _previousTdfDuty;
    private string _previousViewedBody = string.Empty;
    private bool _stateInitialized;
    private readonly List<Guid> _stockIds = new();
    private readonly BoxContainer _managementBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly BoxContainer _personnelBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    // Keeps a single personnel action group expanded across state refreshes.
    private readonly Dictionary<Guid, int> _personnelOpenPanels = new();
    private readonly BoxContainer _rankingBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 7 };
    private readonly BoxContainer _financeBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly BoxContainer _permissionsBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly BoxContainer _personnelRightsBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 5 };
    private readonly BoxContainer _budgetSide = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly Dictionary<ManagementSection, Control> _managementSections = new();
    private readonly Dictionary<ManagementSection, Button> _managementButtons = new();
    private ManagementSection _managementSection = ManagementSection.Overview;
    private readonly BoxContainer _creationBody = new() { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
    private readonly Dictionary<ConsoleTab, Control> _tabViews = new();
    private readonly Dictionary<ConsoleTab, Button> _tabButtons = new();

    private readonly LineEdit _name = new() { PlaceHolder = "Назва (3-32 символи)" };
    private readonly CheckBox _entryFeeEnabled = new() { Text = "Вимагати вступний внесок від нових учасників" };
    private readonly LineEdit _fee = new() { Text = "0", PlaceHolder = "Сума вступного внеску", Visible = false };
    private readonly LineEdit _color = new() { Text = "#5599DD", PlaceHolder = "Колір (#RRGGBB)" };
    private readonly PanelContainer _colorPreview = new() { MinWidth = 28, MinHeight = 28 };
    private readonly CheckBox _private = new() { Text = "Приватна організація (вступ лише через схвалення)" };
    private readonly CheckBox _rosterPublic = new() { Text = "Показувати склад організації стороннім", Pressed = true };
    public bool DraftRosterPublic => _rosterPublic.Pressed;
    private readonly OptionButton _industry = new();
    // Ordered selections, with at most three tags, survive UI redraws and PNG selection.
    private readonly List<OrganizationFactionFocus> _factionFoci = new();
    public OrganizationFactionFocus DraftFactionFocus =>
        _creationKind == OrganizationKind.Faction && _factionFoci.Count > 0
            ? _factionFoci[0] : OrganizationFactionFocus.None;
    public OrganizationFactionFocus[] DraftFactionFoci =>
        _creationKind == OrganizationKind.Faction ? _factionFoci.ToArray() : [];
    private TextureRect? _selectedIndustryIcon;
    private Label? _selectedIndustryText;
    private readonly TextEdit _description = new() { MinHeight = 118, HorizontalExpand = true };
    private readonly LineEdit _discordLink = new() { PlaceHolder = "https://...", MinHeight = 34 };
    private byte[] _draftBanner = [];
    private readonly Label _bannerStatus = new() { Text = "Емблему не обрано" };
    private readonly Label _foundingTotal = new();
    private Button? _registerButton;
    private bool _leaveConfirmation;
    private bool _personalizationBannerPicker;
    private BoxContainer? _personalizationBannerPreview;
    private bool _developmentExpanded;
    private bool _personalizationExpanded;
    // Keep help rows and the economy section open across server-state UI refreshes.
    private bool _economyExpanded;
    private readonly HashSet<string> _expandedEconomyHelpRows = new();
    private int _developmentPage = 1;
    private byte[] _newOrganizationBanner = [];
    private Label? _personalizationBannerStatus;
    private string _personalizationBannerStatusText = "Емблема без змін";

    public string DraftDescription => Rope.Collapse(_description.TextRope).Trim();
    public string DraftDiscordLink => _discordLink.Text.Trim();
    public byte[] DraftBanner => (byte[])_draftBanner.Clone();

    public void SetDraftBanner(byte[] banner)
    {
        if (banner.Length == 0)
            return;
        if (_personalizationBannerPicker)
        {
            _newOrganizationBanner = (byte[])banner.Clone();
            _personalizationBannerStatusText = "Нова емблема готова до збереження";
            if (_personalizationBannerStatus != null)
                _personalizationBannerStatus.Text = _personalizationBannerStatusText;
            if (_personalizationBannerPreview != null)
            {
                Clear(_personalizationBannerPreview);
                _personalizationBannerPreview.AddChild(new OrganizationBannerControl(_newOrganizationBanner, 76));
            }
            _personalizationBannerPicker = false;
            return;
        }
        _draftBanner = (byte[]) banner.Clone();
        _bannerStatus.Text = "Логотип готовий";
        _renderedCreationKind = null;
        RefreshCreation();
    }

    public void SetBannerError(string message)
    {
        if (_personalizationBannerPicker)
        {
            _personalizationBannerStatusText = message;
            if (_personalizationBannerStatus != null)
                _personalizationBannerStatus.Text = message;
            _personalizationBannerPicker = false;
        }
        else
            _bannerStatus.Text = WrapText(message, 27);
    }

    private static int FoundingPrice(OrganizationKind kind, bool isPrivate) =>
        500_000 + (isPrivate ? 75_000 : 0) + (kind == OrganizationKind.Faction ? 100_000 : 0);


    private OrganizationConsoleState? _state;
    private Guid? _selectedOrganization;
    private ConsoleTab _activeTab = ConsoleTab.Registry;

    public OrganizationConsoleWindow()
    {
        Title = "Реєстр організацій";
        MinSize = new Vector2i(900, 620);
        SetSize = new Vector2(1080, 760);

        _typeFilter.AddItem("Усі", 0);
        _typeFilter.AddItem("Фракції", 1);
        _typeFilter.AddItem("Компанії", 2);
        _typeFilter.SelectId(0);

        _sortFilter.AddItem("За впливом ↓", (int) SortMode.InfluenceDesc);
        _sortFilter.AddItem("За впливом ↑", (int) SortMode.InfluenceAsc);
        _sortFilter.AddItem("За назвою А-Я", (int) SortMode.NameAsc);
        _sortFilter.AddItem("За назвою Я-А", (int) SortMode.NameDesc);
        _sortFilter.AddItem("За учасниками ↓", (int) SortMode.MembersDesc);
        _sortFilter.AddItem("За учасниками ↑", (int) SortMode.MembersAsc);
        _sortFilter.SelectId((int) SortMode.InfluenceDesc);

        for (var i = 0; i <= (int) OrganizationIndustry.Agriculture; i++)
            _industry.AddItem(IndustryLabel((OrganizationIndustry) i), i);
        _industry.SelectId(0);
        _industry.OnItemSelected += args =>
        {
            _industry.SelectId(args.Id);
            if (_selectedIndustryIcon != null)
                _selectedIndustryIcon.Texture = BuildIndustryIcon((OrganizationIndustry)args.Id, 21).Texture;
            if (_selectedIndustryText != null)
                _selectedIndustryText.Text = IndustryLabel((OrganizationIndustry)args.Id);
        };
        for (var i = (int) OrganizationIndustry.Delivery; i <= (int) OrganizationIndustry.Agriculture; i++)
            _marketSelector.AddItem(IndustryLabel((OrganizationIndustry) i), i);
        _marketSelector.SelectId((int) OrganizationIndustry.Delivery);
        _marketSelector.OnItemSelected += args =>
        {
            _marketSelector.SelectId(args.Id);
            RefreshAnalytics();
        };
        _investment.OnTextChanged += _ =>
        {
            _budgetChart.SetInvestment(ParseAmount(_investment));
            RefreshFoundingPrice();
        };
        _private.OnToggled += _ => RefreshFoundingPrice();
        _entryFeeEnabled.OnToggled += _ =>
        {
            _fee.Visible = _entryFeeEnabled.Pressed;
            if (!_entryFeeEnabled.Pressed)
                _fee.Text = "0";
        };
        _color.OnTextChanged += _ => UpdateColorPreview();
        UpdateColorPreview();
        _accessRoleSelector.AddItem("Власник: усі права", (int) OrganizationPermissionTier.Owner);
        _accessRoleSelector.AddItem("Заступник", (int) OrganizationPermissionTier.Deputy);
        _accessRoleSelector.AddItem("Керівництво", (int) OrganizationPermissionTier.Officer);
        _accessRoleSelector.AddItem("Працівники", (int) OrganizationPermissionTier.Member);
        _accessRoleSelector.SelectId((int) OrganizationPermissionTier.Deputy);
        _accessRoleSelector.OnItemSelected += args =>
        {
            _accessRoleSelector.SelectId(args.Id);
            RefreshPermissions();
        };
        _stockSelector.OnItemSelected += args =>
        {
            _stockSelector.SelectId(args.Id);
            if (args.Id >= 0 && args.Id < _stockIds.Count)
                _selectedStock = _stockIds[args.Id];
            RefreshAnalytics();
        };

        _search.OnTextChanged += _ => RefreshViews();
        _typeFilter.OnItemSelected += args =>
        {
            _typeFilter.SelectId(args.Id);
            RefreshViews();
        };
        _sortFilter.OnItemSelected += args =>
        {
            _sortFilter.SelectId(args.Id);
            RefreshViews();
        };

        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 6
        };
        Contents.AddChild(root);

        var tabsRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            SeparationOverride = 4,
            Margin = new Thickness(6, 5, 6, 0)
        };
        root.AddChild(tabsRow);
        AddTabButton(tabsRow, ConsoleTab.Registry, "Реєстр");
        AddTabButton(tabsRow, ConsoleTab.Analytics, "Аналітика");
        AddTabButton(tabsRow, ConsoleTab.Management, "Моя організація");
        AddTabButton(tabsRow, ConsoleTab.Creation, "Створення");
        AddTabButton(tabsRow, ConsoleTab.Services, "Квота");
        _tabButtons[ConsoleTab.Services].Visible = false;
        AddTabButton(tabsRow, ConsoleTab.Tdf, "TDF: нагляд");
        _tabButtons[ConsoleTab.Tdf].Visible = false;
        tabsRow.AddChild(new Control { HorizontalExpand = true });

        var refresh = new Button { Text = "Оновити" };
        refresh.OnPressed += _ => RefreshRequested?.Invoke();
        tabsRow.AddChild(refresh);
        _personalBalance.Text = "Баланс: —";
        _personalBalance.FontColorOverride = Color.FromHex("#A7DDA9");
        // Reserve the full balance width on the FIRST layout pass, not only after a drag/resize.
        _personalBalance.MinWidth = 224;
        _personalBalance.HorizontalAlignment = HAlignment.Right;
        _personalBalance.Margin = new Thickness(8, 0, 5, 0);
        tabsRow.AddChild(_personalBalance);

        _notice.FontColorOverride = Color.FromHex("#D6E8FF");
        _notice.Margin = new Thickness(8, 0, 8, 0);
        root.AddChild(_notice);

        var viewHost = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new Thickness(6, 0, 6, 6)
        };
        root.AddChild(viewHost);

        var registryView = BuildRegistryView();
        var analyticsView = BuildAnalyticsView();
        var managementView = BuildManagementView();
        var creationView = BuildCreationView();
        var servicesView = BuildServicesView();
        var tdfView = BuildTdfView();
        _tabViews[ConsoleTab.Registry] = registryView;
        _tabViews[ConsoleTab.Analytics] = analyticsView;
        _tabViews[ConsoleTab.Management] = managementView;
        _tabViews[ConsoleTab.Creation] = creationView;
        _tabViews[ConsoleTab.Services] = servicesView;
        _tabViews[ConsoleTab.Tdf] = tdfView;

        viewHost.AddChild(registryView);
        viewHost.AddChild(analyticsView);
        viewHost.AddChild(managementView);
        viewHost.AddChild(creationView);
        viewHost.AddChild(servicesView);
        viewHost.AddChild(tdfView);

        SwitchTab(ConsoleTab.Registry);
    }

    private void AddTabButton(BoxContainer parent, ConsoleTab tab, string text)
    {
        var button = new Button { Text = text, MinWidth = tab == ConsoleTab.Management ? 155 : 110 };
        button.OnPressed += _ => SwitchTab(tab);
        _tabButtons[tab] = button;
        parent.AddChild(button);
    }

    private Control BuildRegistryView()
    {
        var view = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 6
        };

        var filterRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            SeparationOverride = 6
        };
        _search.HorizontalExpand = true;
        filterRow.AddChild(_search);
        filterRow.AddChild(_typeFilter);
        filterRow.AddChild(_sortFilter);
        view.AddChild(filterRow);

        var body = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 8
        };
        view.AddChild(body);

        var listPanel = BuildTitledPanel("Реєстр: натисніть на організацію", out var listContent, true);
        var listScroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        listScroll.AddChild(_registryList);
        listContent.AddChild(listScroll);
        body.AddChild(listPanel);

        var ranking = BuildTitledPanel("Рейтинг сектора", out var rankingContent, true);
        ranking.MinWidth = 275;
        var rankingScroll = new ScrollContainer { HScrollEnabled = false, VerticalExpand = true, HorizontalExpand = true };
        rankingScroll.AddChild(_rankingBody);
        rankingContent.AddChild(rankingScroll);
        body.AddChild(ranking);

        return view;
    }

    private Control BuildAnalyticsView()
    {
        // Keep the analytics root transparent, just like Registry. The graph/legend
        // and both trading columns are independent dark panels, not panels drawn
        // on an identically colored giant parent panel.
        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 6
        };
        root.AddChild(new Label
        {
            Text = "Секторна аналітика",
            FontColorOverride = Color.FromHex("#C8D7E8"),
            Margin = new Thickness(3, 3, 0, 0)
        });
        var scroll = new ScrollContainer
        {
            HScrollEnabled = false,
            HorizontalExpand = true,
            VerticalExpand = true
        };
        scroll.AddChild(_analyticsBody);
        root.AddChild(scroll);
        return root;
    }

    private Control BuildManagementView()
    {
        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 8
        };

        var left = BuildTitledPanel("Моя організація", out var leftBody, true);
        root.AddChild(left);
        AddManagementPage(leftBody, ManagementSection.Overview, _managementBody);
        AddManagementPage(leftBody, ManagementSection.Personnel, _personnelBody);
        AddManagementPage(leftBody, ManagementSection.Finance, _financeBody);
        AddManagementPage(leftBody, ManagementSection.Permissions, _permissionsBody);
        AddManagementPage(leftBody, ManagementSection.Shares, _sharesBody);

        var side = BuildTitledPanel("Розділи й бюджет", out var sideBody, true);
        side.MinWidth = 310;
        root.AddChild(side);
        var firstRow = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
        var secondRow = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
        AddManagementButton(firstRow, ManagementSection.Overview, "Огляд");
        AddManagementButton(firstRow, ManagementSection.Personnel, "Кадри");
        AddManagementButton(secondRow, ManagementSection.Finance, "Фінанси");
        AddManagementButton(secondRow, ManagementSection.Permissions, "Активи");
        var thirdRow = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
        AddManagementButton(thirdRow, ManagementSection.Shares, "Акції та власники");
        sideBody.AddChild(firstRow);
        sideBody.AddChild(secondRow);
        sideBody.AddChild(thirdRow);
        sideBody.AddChild(new PanelContainer
        {
            MinHeight = 1,
            Margin = new Thickness(3, 12, 3, 10),
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#77808A")
            }
        });
        var budgetScroll = new ScrollContainer { HScrollEnabled = false, VerticalExpand = true, HorizontalExpand = true };
        budgetScroll.AddChild(_budgetSide);
        sideBody.AddChild(budgetScroll);
        SelectManagementSection(ManagementSection.Overview);
        return root;
    }

    private void AddManagementPage(BoxContainer left, ManagementSection section, BoxContainer body)
    {
        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        scroll.AddChild(body);
        _managementSections[section] = scroll;
        left.AddChild(scroll);
    }

    private void AddManagementButton(BoxContainer parent, ManagementSection section, string title)
    {
        var button = new Button { Text = title, HorizontalExpand = true, MinHeight = 32 };
        button.OnPressed += _ => SelectManagementSection(section);
        parent.AddChild(button);
        _managementButtons[section] = button;
    }

    private void SelectManagementSection(ManagementSection section)
    {
        _managementSection = section;
        foreach (var (name, view) in _managementSections)
            view.Visible = name == section;
        foreach (var (name, button) in _managementButtons)
            button.Modulate = name == section ? Color.FromHex("#A7DDA9") : Color.White;
    }

    private Control BuildTdfView()
    {
        var panel = BuildTitledPanel("TDF: Ринковий нагляд", out var content, true);
        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        scroll.AddChild(_tdfBody);
        content.AddChild(scroll);
        return panel;
    }

    // Collapsible sections keep the overview readable without removing details.
    private static PanelContainer BuildFoldout(string title, out BoxContainer body, bool expanded = false, Action<bool>? onToggle = null)
    {
        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#222830") }
        };
        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
            Margin = new Thickness(7), SeparationOverride = 5
        };
        var button = new Button { Text = (expanded ? "▾  " : "▸  ") + title, HorizontalExpand = true, MinHeight = 33 };
        root.AddChild(button);
        body = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
            SeparationOverride = 5, Margin = new Thickness(6, 3, 6, 6), Visible = expanded };
        var content = body;
        button.OnPressed += _ =>
        {
            content.Visible = !content.Visible;
            button.Text = (content.Visible ? "▾  " : "▸  ") + title;
            onToggle?.Invoke(content.Visible);
        };
        root.AddChild(body);
        panel.AddChild(root);
        return panel;
    }

    private static string WrapText(string value, int width = 43)
    {
        var result = new List<string>();
        foreach (var paragraph in value.Split('\n'))
        {
            var line = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > width)
                {
                    result.Add(line);
                    line = word;
                }
                else
                    line = line.Length == 0 ? word : line + " " + word;
            }
            result.Add(line);
        }
        return string.Join("\n", result);
    }

    // A real interactive help control, not a Label pretending to be a button.
    // The state is kept on the window so new UI trees do not reset the help rows.
    private void AddEconomyRow(BoxContainer parent, string name, string value,
        string tooltip, Color? color = null)
    {
        var row = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6,
            MinHeight = 27
        };
        row.AddChild(new Label
        {
            Text = name,
            MinWidth = 135,
            FontColorOverride = Color.FromHex("#A7B7C6")
        });

        var expanded = _expandedEconomyHelpRows.Contains(name);
        var details = new Label
        {
            Text = WrapText(tooltip, 48),
            Visible = expanded,
            HorizontalExpand = true,
            Margin = new Thickness(9, 1, 9, 7),
            FontColorOverride = Color.FromHex("#AEBFCE")
        };
        var help = new Button
        {
            Text = "?",
            MinWidth = 25,
            MinHeight = 23,
            ToolTip = tooltip,
            Modulate = expanded ? Color.FromHex("#B9D6F0") : Color.FromHex("#A0AEBB")
        };
        help.OnPressed += _ =>
        {
            if (_expandedEconomyHelpRows.Remove(name))
                details.Visible = false;
            else
            {
                _expandedEconomyHelpRows.Add(name);
                details.Visible = true;
            }
            help.Modulate = details.Visible ? Color.FromHex("#B9D6F0") : Color.FromHex("#A0AEBB");
        };
        row.AddChild(help);
        row.AddChild(new Control { HorizontalExpand = true });
        row.AddChild(new Label
        {
            Text = value,
            HorizontalAlignment = HAlignment.Right,
            FontColorOverride = color ?? Color.FromHex("#E3EAF0")
        });
        parent.AddChild(row);
        parent.AddChild(details);
    }

    private static void AddMetric(BoxContainer body, string title, string value, Color? color = null)
    {
        var longLabel = title.Length > 26 || value.Length > 32;
        var row = new BoxContainer { Orientation = longLabel ? LayoutOrientation.Vertical : LayoutOrientation.Horizontal,
            HorizontalExpand = true, SeparationOverride = longLabel ? 2 : 8 };
        row.AddChild(new Label { Text = WrapText(title, 31), HorizontalExpand = true,
            FontColorOverride = Color.FromHex("#9DAFBC") });
        row.AddChild(new Label { Text = WrapText(value, 34), FontColorOverride = color ?? Color.FromHex("#E5EAF0") });
        body.AddChild(row);
    }

    private Control BuildServicesView()
    {
        // Three zones: one large task list and two stacked right-hand panels.
        var layout = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 8
        };
        var jobs = BuildTitledPanel("Квота: спільні завдання сектора", out var jobBody, true);
        var jobScroll = new ScrollContainer { HScrollEnabled = false,
            HorizontalExpand = true, VerticalExpand = true };
        jobScroll.AddChild(_servicesBody);
        jobBody.AddChild(jobScroll);
        layout.AddChild(jobs);

        var right = new BoxContainer { Orientation = LayoutOrientation.Vertical,
            VerticalExpand = true, MinWidth = 290, SeparationOverride = 8 };
        // Active assignments belong above shuttle rentals: they are consulted
        // more often and need space for the expandable objective checklist.
        var accepted = BuildTitledPanel("Прийняті завдання", out var acceptedContent, true);
        accepted.MinHeight = 340;
        var acceptedScroll = new ScrollContainer { HScrollEnabled = false,
            HorizontalExpand = true, VerticalExpand = true };
        acceptedScroll.AddChild(_quotaActionsBody);
        acceptedContent.AddChild(acceptedScroll);
        right.AddChild(accepted);
        var ships = BuildTitledPanel("Доступні шатли", out var shipsContent, true);
        ships.MinHeight = 180;
        var shipsScroll = new ScrollContainer { HScrollEnabled = false,
            HorizontalExpand = true, VerticalExpand = true };
        shipsScroll.AddChild(_quotaShuttlesBody);
        shipsContent.AddChild(shipsScroll);
        right.AddChild(ships);
        layout.AddChild(right);
        return layout;
    }

    private void RefreshServices()
    {
        Clear(_servicesBody);
        Clear(_quotaShuttlesBody);
        Clear(_quotaActionsBody);
        if (_state?.Membership is not { } membership)
            return;
        var org = _state.Organizations.FirstOrDefault(o => o.Id == membership);
        if (org == null)
            return;

        _servicesBody.AddChild(new Label
        {
            Text = org.Kind == OrganizationKind.Faction
                ? "Завдання за спрямуваннями фракції" : "Галузеві замовлення",
            FontColorOverride = Color.FromHex("#C7D6E6")
        });
        if (org.Kind == OrganizationKind.Company)
            _servicesBody.AddChild(new Label
            {
                Text = "Вільні замовлення спільні для всіх організацій галузі. Одне завдання на організацію.",
                FontColorOverride = Color.FromHex("#9CABB9")
            });
        else
            AddMetric(_servicesBody, "Прийняття нового завдання",
                _state.FactionNextTaskSeconds > 0
                    ? $"через {_state.FactionNextTaskSeconds / 60} хв {_state.FactionNextTaskSeconds % 60} с"
                    : "доступне, якщо є вільна локація");
        var available = _state.SectorQuotas.Where(q => !q.ReservedByUs).ToArray();
        foreach (var quota in available)
        {
            var panel = BuildQuotaCard(quota, out var body);
            if (quota.IsFactionOperation)
            {
                body.AddChild(new Label { Text = WrapText(quota.Description, 53),
                    FontColorOverride = Color.FromHex("#BDCBD7") });
                OrganizationFactionStepUi.AddTo(body, quota.FactionSteps,
                    $"offer:{quota.Category}:{quota.Slot}",
                    _expandedFactionStepLists, _expandedFactionStepDetails, false);
            }
            else
                body.AddChild(new Label { Text = WrapText(quota.Description, 54),
                    FontColorOverride = Color.FromHex("#BECBD7") });
            if (!quota.IsFactionOperation)
            {
                AddMetric(body, "Обсяг", quota.Workflow == OrganizationQuotaWorkflow.MedicalTreatment
                    ? $"{quota.Units} пацієнтів" : $"{quota.Units} ящиків");
                if (quota.Workflow != OrganizationQuotaWorkflow.CargoDelivery)
                    AddMetric(body, "Виконання", quota.Workflow switch
                    {
                        OrganizationQuotaWorkflow.SectorService => "Очікує профільної системи",
                        OrganizationQuotaWorkflow.MedicalTreatment => "Реальна допомога пацієнтам",
                        _ => "Вантажні ящики з накладною"
                    });
            }
            AddMetric(body, "Термін", $"{quota.TimeLimitMinutes} хв");
            if (quota.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
            {
                var targetPost = Array.FindIndex(quota.PostTargets, count => count > 0);
                if (targetPost >= 0)
                    AddMetric(body, "Пункт здачі", $"Торговий пост {(char)('A' + targetPost)}");
            }
            if (quota.IsFactionOperation)
            {
                AddMetric(body, "Результат", quota.Slot == 1
                    ? "Архівні дані та розвиток фракції"
                    : "Вузол зв'язку та розвиток фракції",
                    Color.FromHex("#A1D8BB"));
            }
            else
            {
                AddMetric(body, "Орієнтовна оплата", $"≈ {quota.EstimatedReward:N0} $",
                    Color.FromHex("#A1D8BB"));
                AddMetric(body, "Вплив у секторі", $"до +{quota.SectorInfluenceBp / 100f:0.00}%",
                    Color.FromHex("#A1D8BB"));
            }
            if (org.Kind == OrganizationKind.Company)
                AddMetric(body, "Бонус до прибутку", $"+{quota.IncomeBonusPercent}% на {OrganizationQuotaRules.BonusCycles} цикли",
                    Color.FromHex("#A1D8BB"));
            else if (!quota.IsFactionOperation)
                AddMetric(body, "Винагорода", "Постачання у скарбницю та вплив");
            var accept = new Button
            {
                Text = quota.Workflow == OrganizationQuotaWorkflow.SectorService && !quota.IsFactionOperation
                    ? "Очікує профільної інтеграції" : "Прийняти завдання",
                Disabled = quota.Workflow == OrganizationQuotaWorkflow.SectorService && !quota.IsFactionOperation,
                MinHeight = 32
            };
            accept.OnPressed += _ => QuotaClaimRequested?.Invoke(org.Id, quota.Category, quota.Slot);
            body.AddChild(accept);
            _servicesBody.AddChild(panel);
        }
        if (available.Length == 0)
            _servicesBody.AddChild(new Label { Text = org.Kind == OrganizationKind.Faction
                ? "Доступних завдань наразі немає. Перевірте згодом."
                : "Вільних квот зараз немає. Перевірте після оновлення.",
                FontColorOverride = Color.FromHex("#AFC0CE") });

        var active = _state.SectorQuotas.Where(q => q.ReservedByUs && !q.Completed).ToArray();
        foreach (var quota in active)
        {
            var panel = BuildQuotaCard(quota, out var body);
            if (quota.IsFactionOperation)
            {
                body.AddChild(new Label { Text = WrapText(quota.Description, 37),
                    FontColorOverride = Color.FromHex("#BDCBD7") });
                OrganizationFactionStepUi.AddTo(body, quota.FactionSteps, quota.Code,
                    _expandedFactionStepLists, _expandedFactionStepDetails, false);
            }
            else
                body.AddChild(new Label { Text = WrapText(quota.Description, 38),
                    FontColorOverride = Color.FromHex("#B8C7D5") });
            AddMetric(body, quota.IsFactionOperation ? "Код завдання" : "Код замовлення", quota.Code);
            AddMetric(body, "Прийняв", quota.AssignedTo + (quota.AssignedToMe ? " (ви)" : ""));
            AddMetric(body, "Прогрес", quota.IsFactionOperation
                ? $"{quota.FactionSteps.Count(step => step.Completed)} / {quota.FactionSteps.Length} етапів"
                : quota.Workflow == OrganizationQuotaWorkflow.MedicalTreatment
                ? $"{quota.Delivered} / {quota.Units} пацієнтів"
                : $"{quota.Delivered} / {quota.Units} ящиків");
            if (quota.IsFactionOperation)
            {
                if (!string.IsNullOrEmpty(quota.FactionSiteLabel))
                    AddMetric(body, quota.Slot == 1 ? "FTL-напрямок" : "Планета / ділянка",
                        quota.FactionSiteLabel, Color.FromHex("#A4D4B2"));
            }
            AddMetric(body, "Термін", $"{quota.TimeLimitMinutes} хв");
            if (quota.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport)
            {
                var targetPost = Array.FindIndex(quota.PostTargets, count => count > 0);
                if (targetPost >= 0)
                    AddMetric(body, "Пункт здачі", $"Торговий пост {(char)('A' + targetPost)}");
            }
            if (quota.IsFactionOperation)
                AddMetric(body, "Результат", quota.Slot == 1
                    ? "Архівування даних та авторитет фракції"
                    : "Реєстрація вузла мережі фракції", Color.FromHex("#A1D8BB"));
            else
                AddMetric(body, "Орієнтовна оплата", $"≈ {quota.EstimatedReward:N0} $",
                    Color.FromHex("#A1D8BB"));
            AddMetric(body, "Залишилося", $"{quota.SecondsRemaining / 60} хв {quota.SecondsRemaining % 60} с");
            if (quota.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport &&
                quota.RejectedShipments > 0)
                AddMetric(body, "Відхилено партій", quota.RejectedShipments.ToString(),
                    Color.FromHex("#E3A488"));
            if (quota.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport &&
                !string.IsNullOrWhiteSpace(quota.LastInspectionReport))
            {
                AddMetric(body, "Остання перевірка", quota.LastInspectionReport,
                    quota.LastInspectionQuality < 100 ? Color.FromHex("#E3BC83") : Color.FromHex("#A1D8BB"));
                if (quota.Delivered > 0)
                    AddMetric(body, "Середня якість", $"{quota.LastInspectionQuality}%");
            }
            if (quota.Workflow == OrganizationQuotaWorkflow.CargoDelivery)
            {
                for (var post = 0; post < 4; post++)
                {
                    if (quota.PostTargets[post] == 0)
                        continue;
                    AddMetric(body, $"Пост {(char)('A' + post)}",
                        $"{quota.DeliveredByPost[post]} / {quota.PostTargets[post]}");
                }
            }
            if (quota.Workflow == OrganizationQuotaWorkflow.CargoDelivery)
                body.AddChild(new Label { Text = "Отримання вантажу: Trade Mall",
                    FontColorOverride = Color.FromHex("#9DB7C8") });
            var release = new Button { Text = "Відмовитися від завдання", MinHeight = 31,
                Disabled = quota.IsFactionOperation && quota.Delivered > 0 };
            release.OnPressed += _ => QuotaReleaseRequested?.Invoke(org.Id, quota.Category, quota.Slot);
            body.AddChild(release);
            _quotaActionsBody.AddChild(panel);
        }
        if (active.Length == 0)
            _quotaActionsBody.AddChild(new Label { Text = "Немає активного завдання.",
                FontColorOverride = Color.FromHex("#B6C4D0") });
        var pendingSettlement = _state.SectorQuotas.Count(q => q.ReservedByUs && q.SettlementPending);
        if (pendingSettlement > 0)
            AddMetric(_quotaActionsBody, "Очікує виплати", pendingSettlement.ToString(),
                Color.FromHex("#E6BD76"));
        var completed = _state.SectorQuotas.Count(q => q.ReservedByUs && q.Completed && !q.SettlementPending);
        if (completed > 0)
            AddMetric(_quotaActionsBody, "Виконано цього раунду", completed.ToString(),
                Color.FromHex("#A4D4B2"));
        foreach (var finished in _state.SectorQuotas.Where(q => q.ReservedByUs && q.Completed &&
                     q.Workflow == OrganizationQuotaWorkflow.PharmaceuticalExport &&
                     !string.IsNullOrWhiteSpace(q.LastInspectionReport)))
        {
            var report = BuildQuotaCard(finished, out var details);
            AddMetric(details, "Завершена квота", finished.Code);
            AddMetric(details, "Якість поставки", $"{finished.LastInspectionQuality}%",
                Color.FromHex("#A4D4B2"));
            if (finished.RejectedShipments > 0)
                AddMetric(details, "Відхилено партій", finished.RejectedShipments.ToString(),
                    Color.FromHex("#E3A488"));
            details.AddChild(new Label { Text = WrapText(finished.LastInspectionReport, 38),
                FontColorOverride = Color.FromHex("#BED1DD") });
            _quotaActionsBody.AddChild(report);
        }
        if (org.Kind == OrganizationKind.Company && _state.QuotaBonusCycles > 0)
            AddMetric(_quotaActionsBody, "Бонус прибутку", $"+{_state.QuotaIncomeBonusPercent}% ({_state.QuotaBonusCycles} цикли)",
                Color.FromHex("#A4D4B2"));
        if (org.Kind == OrganizationKind.Company)
        {
            AddMetric(_quotaActionsBody, "Доступний попит", $"{_state.DailyDemandRemaining:N0} $");
            AddMetric(_quotaActionsBody, "Ліміт компанії", $"{_state.DailyGrossRevenue:N0} / {_state.DailyCompanyLimit:N0} $");
        }

        var fleet = BuildTitledPanel("Транспорт для квоти", out var fleetBody);
        AddMetric(fleetBody, "Назва", string.IsNullOrEmpty(_state.FleetListingName)
            ? "Шатл ще не підготовлений" : _state.FleetListingName);
        if (!string.IsNullOrEmpty(_state.FleetListingDescription))
            fleetBody.AddChild(new Label { Text = WrapText(_state.FleetListingDescription, 38),
                FontColorOverride = Color.FromHex("#BBCAD7") });
        AddMetric(fleetBody, "Ціна на раунд", $"{_state.FleetRentalPrice:N0} $");
        AddMetric(fleetBody, "Статус", _state.FleetShuttleIssued
            ? (_state.FleetShuttlePresent ? "Зараз орендований" :
                _state.FleetShuttleReturned ? "Повернуто в цьому раунді" : "Втрачений у цьому раунді")
            : _state.FleetBlueprintReady ? "Доступний для оренди" : "Ще не виставлений");
        if (!string.IsNullOrEmpty(_state.FleetError))
            fleetBody.AddChild(new Label { Text = WrapText(_state.FleetError, 35),
                FontColorOverride = Color.FromHex("#E6A5A5") });
        fleetBody.AddChild(new Label { Text = "Креслення та умови оренди змінюються в розділі «Активи».",
            FontColorOverride = Color.FromHex("#A2B3C1") });
        _quotaShuttlesBody.AddChild(fleet);

        if (_state.FleetShuttleIssued)
        {
            AddMetric(_quotaActionsBody, "Робочий шатл",
                _state.FleetShuttlePresent ? $"{_state.FleetIssuedName}, до кінця раунду" :
                    _state.FleetShuttleReturned ? "Повернуто. Наступна оренда в новому раунді." :
                    "Втрачено в цьому раунді. Повторна оренда недоступна.",
                Color.FromHex(_state.FleetShuttlePresent ? "#A5D0B0" : "#E5BAA5"));
            if (_state.FleetShuttlePresent && _state.FleetRenterIsMe)
            {
                var returnButton = new Button { Text = "Повернути шатл", MinHeight = 33,
                    ToolTip = "Повернення доступне лише коли на мапі шатла не залишилося гравців" };
                var confirmReturn = false;
                returnButton.OnPressed += _ =>
                {
                    if (!confirmReturn)
                    {
                        confirmReturn = true;
                        returnButton.Text = "Підтвердити повернення";
                        returnButton.Modulate = Color.FromHex("#E6AC80");
                        return;
                    }
                    returnButton.Disabled = true;
                    FleetReturnRequested?.Invoke(org.Id);
                };
                _quotaActionsBody.AddChild(returnButton);
            }
        }
        else if (_state.FleetBlueprintReady)
        {
            var rent = new Button {
                Text = _fleetRentConfirm
                    ? $"Підтвердити оплату: {_state.FleetRentalPrice:N0} $"
                    : $"Орендувати на раунд: {_state.FleetRentalPrice:N0} $",
                MinHeight = 36,
                Disabled = active.Length == 0 || _state.ViewingAnotherCharacter ||
                    _state.Role == OrganizationMemberRole.Owner,
                Modulate = _fleetRentConfirm ? Color.FromHex("#E67575") : Color.White
            };
            rent.OnPressed += _ =>
            {
                if (!_fleetRentConfirm)
                {
                    _fleetRentConfirm = true;
                    rent.Text = $"Підтвердити оплату: {_state.FleetRentalPrice:N0} $";
                    rent.Modulate = Color.FromHex("#E67575");
                    return;
                }
                _fleetRentConfirm = false;
                FleetRentRequested?.Invoke(org.Id);
            };
            _quotaActionsBody.AddChild(rent);
            _quotaActionsBody.AddChild(new Label { Text =
                "Орендар сплачує з особистого рахунку. Власник не орендує власний шатл.",
                FontColorOverride = Color.FromHex("#A7B7C6") });
        }
        else
            _quotaActionsBody.AddChild(new Label {
                Text = "Для оренди власник має виставити шатл у підвкладці «Активи».",
                FontColorOverride = Color.FromHex("#A7B7C6") });
    }

    private static PanelContainer BuildQuotaCard(OrganizationQuotaEntry quota, out BoxContainer body)
    {
        var difficulty = OrganizationQuotaRules.Difficulty(quota.Slot);
        var ribbonColor = difficulty switch
        {
            1 => Color.FromHex("#75BD88"),
            2 => Color.FromHex("#E5CB69"),
            3 => Color.FromHex("#E6A169"),
            _ => Color.FromHex("#DD7474")
        };
        var card = new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#252C34"),
                BorderColor = Color.FromHex("#4C5865"),
                BorderThickness = new Thickness(1)
            }
        };
        var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true,
            SeparationOverride = 9, Margin = new Thickness(3, 5, 8, 7) };
        row.AddChild(new PanelContainer { MinWidth = 5, VerticalExpand = true,
            PanelOverride = new StyleBoxFlat
        { BackgroundColor = ribbonColor } });
        body = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
            SeparationOverride = 5 };
        var header = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true,
            SeparationOverride = 7 };
        header.AddChild(new Label { Text = WrapText(quota.Title, 38), HorizontalExpand = true,
            FontColorOverride = Color.FromHex("#E4ECF4") });
        header.AddChild(new Label { Text = new string('★', difficulty),
            ToolTip = OrganizationQuotaRules.DifficultyName(quota.Slot), FontColorOverride = ribbonColor });
        body.AddChild(header);
        row.AddChild(body);
        card.AddChild(row);
        return card;
    }

    // The five saved company-development fields are reused for faction programs.
    // Server validation is authoritative: this is only a presentation of eligibility.
    private Control BuildFactionProgressPanel(OrganizationEntry org)
    {
        var panel = BuildFoldout("Розвиток фракції", out var body, false);
        body.AddChild(new Label { Text = WrapText(
            "Програми формуються за вашими спрямуваннями. Авторитет відкриває рівні, а кошти зі скарбниці забезпечують роботу. Присутність у секторі зростає поступово.", 48),
            FontColorOverride = Color.FromHex("#ADC3D2") });
        var programs = new[]
        {
            (OrganizationDevelopmentBranch.Logistics, "Координація діяльності", _state!.LogisticsLevel,
                "Підготовка нових завдань; коротша затримка між ними"),
            (OrganizationDevelopmentBranch.Research, "Методи дослідження", _state!.ResearchFacilityLevel,
                "Розвиток профільних знань і дослідницьких можливостей"),
            (OrganizationDevelopmentBranch.Infrastructure, "Матеріальна база", _state!.InfrastructureLevel,
                "Підтримка створеної фракцією інфраструктури"),
            (OrganizationDevelopmentBranch.ServiceNetwork, "Організаційна мережа", _state!.ServiceNetworkLevel,
                "Координація учасників та осередків діяльності"),
            (OrganizationDevelopmentBranch.RiskManagement, "Захист і стійкість", _state!.RiskManagementLevel,
                "Забезпечення діяльності в складних умовах")
        };
        // Compatibility with factions created before the multi-focus selector.
        var selectedFoci = org.FactionFoci.Length > 0 ? org.FactionFoci :
            org.FactionFocus == OrganizationFactionFocus.None ? [] : [org.FactionFocus];
        foreach (var (branch, label, level, description) in programs)
        {
            if (!FactionBranchMatches(selectedFoci, branch))
                continue;
            var cost = 4_000L * (level + 1) * (level + 2);
            var neededAuthority = 4L + 6L * level;
            var fulfilled = _state.FactionOperationsCompleted >= level + 1 &&
                _state.FactionAuthorityPoints >= neededAuthority;
            var card = BuildTitledPanel(label, out var detail);
            AddMetric(detail, "Рівень програми", $"{level}/5");
            detail.AddChild(new Label { Text = description,
                FontColorOverride = Color.FromHex("#A9BBCA") });
            var squares = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
            for (var i = 0; i < 5; i++)
                squares.AddChild(new PanelContainer { MinHeight = 11, MinWidth = 17,
                    PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(i < level ? "#83B8A0" : "#3C4752") } });
            detail.AddChild(squares);
            if (level < 5)
            {
                AddMetric(detail, "Умови", $"{neededAuthority} авторитету · {level + 1} завдань · {cost:N0} $");
                var chosen = branch;
                var upgrade = new Button { Text = $"Розвинути програму · {cost:N0} $", MinHeight = 34,
                    Disabled = !fulfilled || _state.Treasury < cost ||
                        !HasRight(OrganizationPermission.Investments) };
                upgrade.OnPressed += _ => DevelopmentRequested?.Invoke(org.Id, chosen, 1);
                detail.AddChild(upgrade);
            }
            body.AddChild(card);
        }
        return panel;
    }

    private static bool FactionBranchMatches(OrganizationFactionFocus[] foci,
        OrganizationDevelopmentBranch branch) => foci.Any(focus => branch switch
    {
        OrganizationDevelopmentBranch.Logistics => focus is OrganizationFactionFocus.Exploration or
            OrganizationFactionFocus.Colonial or OrganizationFactionFocus.Commercial or
            OrganizationFactionFocus.Humanitarian or OrganizationFactionFocus.Piracy or
            OrganizationFactionFocus.Agricultural or OrganizationFactionFocus.Other,
        OrganizationDevelopmentBranch.Research => focus is OrganizationFactionFocus.Scientific or
            OrganizationFactionFocus.Technological or OrganizationFactionFocus.Medical or
            OrganizationFactionFocus.Educational or OrganizationFactionFocus.Environmental,
        OrganizationDevelopmentBranch.Infrastructure => focus is OrganizationFactionFocus.Industrial or
            OrganizationFactionFocus.Colonial or OrganizationFactionFocus.Defense or
            OrganizationFactionFocus.Technological or OrganizationFactionFocus.Agricultural,
        OrganizationDevelopmentBranch.ServiceNetwork => focus is OrganizationFactionFocus.Civic or
            OrganizationFactionFocus.Humanitarian or OrganizationFactionFocus.Cultural or
            OrganizationFactionFocus.Religious or OrganizationFactionFocus.Spiritual or
            OrganizationFactionFocus.Political or OrganizationFactionFocus.Media or
            OrganizationFactionFocus.Diplomatic or OrganizationFactionFocus.Labor or
            OrganizationFactionFocus.Artistic or OrganizationFactionFocus.Traditionalist or
            OrganizationFactionFocus.Pacifist,
        OrganizationDevelopmentBranch.RiskManagement => focus is OrganizationFactionFocus.Security or
            OrganizationFactionFocus.Defense or OrganizationFactionFocus.Militarist or
            OrganizationFactionFocus.Criminal or OrganizationFactionFocus.Piracy or
            OrganizationFactionFocus.Revolutionary or OrganizationFactionFocus.Ideological,
        _ => false
    });

    private Control BuildOutpostOverviewPanel()
    {
        if (_state?.Membership == null)
            return new Control();
        var org = _state.Organizations.FirstOrDefault(o => o.Id == _state.Membership.Value);
        if (org == null)
            return new Control();
        var outpost = BuildFoldout(org.Kind == OrganizationKind.Faction
            ? "Фракційний аванпост" : "Корпоративний аванпост", out var outpostBody,
            _outpostOverviewExpanded, expanded => _outpostOverviewExpanded = expanded);
        outpostBody.AddChild(new Label { Text = "Створення та обладнання станції",
            FontColorOverride = Color.FromHex("#C8D7E4") });
        outpostBody.AddChild(new Label { Text = WrapText(
            "Станція створюється з підписаного креслення, реєструється в секторі та обладнується точками появи й корпоративними доступами.", 43),
            FontColorOverride = Color.FromHex("#9EB1C3") });
        var outpostSteps = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true, SeparationOverride = 5 };
        foreach (var step in new[]
        {
            ("01 · Креслення", _state.OutpostBlueprintReady),
            ("02 · Станція", _state.OutpostOwned),
            ("03 · Розгортання", _state.OutpostDeployed)
        })
            outpostSteps.AddChild(new PanelContainer { HorizontalExpand = true,
                PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(
                    step.Item2 ? "#315346" : "#2E343D"),
                    BorderColor = Color.FromHex(step.Item2 ? "#75AF90" : "#4D5967"),
                    BorderThickness = new Thickness(1) },
                Children = { new Label { Text = step.Item1, Margin = new Thickness(5, 6),
                    FontColorOverride = Color.FromHex(step.Item2 ? "#C5E5D5" : "#A8B5C5") } }
            });
        outpostBody.AddChild(outpostSteps);
        AddMetric(outpostBody, "Місце в секторі",
            _state.OutpostOwned ? "Придбано" : $"{OrganizationOutpostRules.SitePrice:N0} $");
        if (_state.OutpostOwned)
        {
            AddMetric(outpostBody, "Конструкція", _state.OutpostActiveName);
            AddMetric(outpostBody, "Поточний раунд", _state.OutpostDeployed
                ? "Аванпост зареєстровано як станцію сектора"
                : "Аванпост не розгорнуто, перевірте журнал сервера",
                Color.FromHex(_state.OutpostDeployed ? "#A5CBAF" : "#E4BC96"));
            if (!_state.OutpostDeployed && !string.IsNullOrEmpty(_state.OutpostDeploymentError))
                outpostBody.AddChild(new Label
                {
                    Text = WrapText(_state.OutpostDeploymentError, 54),
                    FontColorOverride = Color.FromHex("#E8AE97")
                });
            if (!_state.OutpostDeployed && _state.Ownership == org.Id && !_state.ViewingAnotherCharacter)
            {
                var retry = new Button { Text = "Повторити розгортання без оплати", MinHeight = 32 };
                retry.OnPressed += _ => OutpostRedeployRequested?.Invoke(org.Id);
                outpostBody.AddChild(retry);
            }
            if (!string.IsNullOrEmpty(_state.OutpostReplacementName))
            {
                AddMetric(outpostBody, "Запланована заміна", _state.OutpostReplacementName);
                AddMetric(outpostBody, "Після демонтажу", _state.OutpostReplacementReadyUtc.ToLocalTime()
                    .ToString("dd.MM.yyyy HH:mm"));
                outpostBody.AddChild(new Label
                {
                    Text = "Заміна відбудеться на початку наступного відповідного раунду. Стара конструкція працює до його завершення.",
                    FontColorOverride = Color.FromHex("#BAC7D1")
                });
                if (_state.Ownership == org.Id && !_state.ViewingAnotherCharacter)
                {
                    var cancel = new Button { Text = "Скасувати заплановану заміну", MinHeight = 31 };
                    cancel.OnPressed += _ => OutpostCancelReplacementRequested?.Invoke(org.Id);
                    outpostBody.AddChild(cancel);
                }
            }
        }
        else
        {
            AddMetric(outpostBody, "Розгортання", "Зареєструйте креслення та придбайте місце");
        }

        if (_state.OutpostOwned)
        {
            var listing = BuildFoldout("Назва, опис і доступність станції", out var listingBody, false);
            AddMetric(listingBody, "Поточна назва", string.IsNullOrWhiteSpace(_state.OutpostPublicName)
                ? org.Name + " | аванпост" : _state.OutpostPublicName);
            if (!string.IsNullOrWhiteSpace(_state.OutpostPublicDescription))
                listingBody.AddChild(new Label { Text = WrapText(_state.OutpostPublicDescription, 47) });
            if (_state.Ownership == org.Id && !_state.ViewingAnotherCharacter)
            {
                var display = new LineEdit { Text = string.IsNullOrWhiteSpace(_state.OutpostPublicName)
                    ? org.Name + " | аванпост" : _state.OutpostPublicName,
                    PlaceHolder = "Назва станції · 3–52 знаки", MinHeight = 31 };
                var description = new LineEdit { Text = _state.OutpostPublicDescription,
                    PlaceHolder = "Короткий опис для Join · до 200 знаків", MinHeight = 31 };
                var visitors = new CheckBox { Text = "Дозволити появу сторонніх відвідувачів",
                    Pressed = _state.OutpostAllowVisitors,
                    ToolTip = "Дозвіл діє лише якщо встановлено безпечний публічний маяк з відкритим маршрутом." };
                listingBody.AddChild(display);
                listingBody.AddChild(description);
                listingBody.AddChild(visitors);
                var save = new Button { Text = "Зберегти профіль станції", MinHeight = 33 };
                save.OnPressed += _ => OutpostListingRequested?.Invoke(org.Id,
                    display.Text, description.Text, visitors.Pressed);
                listingBody.AddChild(save);
            }
            outpostBody.AddChild(listing);
        }
        if (_state.OutpostBlueprintReady)
        {
            AddMetric(outpostBody, "Підготовлене креслення", _state.OutpostBlueprintName);
            AddMetric(outpostBody, "Збережено", _state.OutpostBlueprintUtc.ToString("dd.MM.yyyy HH:mm"));
        }
        else if (!string.IsNullOrEmpty(_state.OutpostBlueprintName))
        {
            outpostBody.AddChild(new Label { Text = "Креслення недоступне або пошкоджене. Завантажте нову копію.",
                FontColorOverride = Color.FromHex("#E4BC96") });
        }
        else
        {
            outpostBody.AddChild(new Label { Text = "Спершу підготуйте підписаний файл із збереженого шатла.",
                FontColorOverride = Color.FromHex("#ABB8C5") });
        }

        if (_state.Ownership == org.Id && !_state.ViewingAnotherCharacter)
        {
            var files = GetOutpostBlueprintFiles?.Invoke() ?? [];
            if (files.Length == 0)
            {
                outpostBody.AddChild(new Label { Text = "Збережених шатлів немає. Спочатку збережіть шатл на верфі.",
                    FontColorOverride = Color.FromHex("#ABB8C5") });
            }
            else
            {
                var selector = new OptionButton { HorizontalExpand = true, MinHeight = 31 };
                for (var i = 0; i < files.Length; i++)
                {
                    var filename = files[i];
                    var slash = filename.LastIndexOf('/');
                    selector.AddItem(filename[(slash + 1)..], i);
                }
                selector.SelectId(0);
                var chosenIndex = 0;
                selector.OnItemSelected += args =>
                {
                    chosenIndex = args.Id;
                    selector.SelectId(args.Id);
                };
                outpostBody.AddChild(selector);
                var stage = new Button { Text = "Перевірити та зберегти креслення", MinHeight = 34 };
                stage.OnPressed += _ =>
                {
                    if (chosenIndex >= 0 && chosenIndex < files.Length)
                    {
                        _outpostPurchaseConfirm = false;
                        _outpostReplaceConfirm = false;
                        _outpostLocalNotice = "Перевірка збереженого шатла...";
                        OutpostBlueprintRequested?.Invoke(org.Id, files[chosenIndex]);
                    }
                };
                outpostBody.AddChild(stage);
            }

            if (!_state.OutpostOwned && _state.OutpostBlueprintReady)
            {
                var purchase = new Button { Text = $"Придбати місце за {OrganizationOutpostRules.SitePrice:N0} $",
                    MinHeight = 36 };
                purchase.Modulate = _outpostPurchaseConfirm ? Color.FromHex("#E67575") : Color.White;
                if (_outpostPurchaseConfirm)
                    purchase.Text = $"Підтвердити оплату {OrganizationOutpostRules.SitePrice:N0} $";
                purchase.OnPressed += _ =>
                {
                    if (!_outpostPurchaseConfirm)
                    {
                        _outpostPurchaseConfirm = true;
                        purchase.Text = $"Підтвердити оплату {OrganizationOutpostRules.SitePrice:N0} $";
                        purchase.Modulate = Color.FromHex("#E67575");
                        return;
                    }
                    _outpostPurchaseConfirm = false;
                    purchase.Disabled = true;
                    OutpostPurchaseRequested?.Invoke(org.Id);
                };
                outpostBody.AddChild(purchase);
            }
            else if (_state.OutpostOwned && _state.OutpostBlueprintReady &&
                     !_state.OutpostBlueprintIsActive &&
                     string.IsNullOrEmpty(_state.OutpostReplacementName))
            {
                // Server compares hashes as well. Different display names alone
                // cannot accidentally authorize replacing the same blueprint.
                var replace = new Button
                {
                    Text = $"Запланувати заміну за {OrganizationOutpostRules.ReplacementPrice:N0} $",
                    MinHeight = 36
                };
                replace.Modulate = _outpostReplaceConfirm ? Color.FromHex("#E67575") : Color.White;
                if (_outpostReplaceConfirm)
                    replace.Text = "Підтвердити заміну після демонтажу";
                replace.OnPressed += _ =>
                {
                    if (!_outpostReplaceConfirm)
                    {
                        _outpostReplaceConfirm = true;
                        replace.Text = "Підтвердити заміну після демонтажу";
                        replace.Modulate = Color.FromHex("#E67575");
                        return;
                    }
                    _outpostReplaceConfirm = false;
                    replace.Disabled = true;
                    OutpostReplacementRequested?.Invoke(org.Id);
                };
                outpostBody.AddChild(replace);
            }
        }
        if (org.Kind == OrganizationKind.Company && _state.OutpostOwned &&
            _state.Ownership == org.Id && !_state.ViewingAnotherCharacter)
        {
            outpostBody.AddChild(new Label { Text = "Корпоративні доступи та маяки появи",
                FontColorOverride = Color.FromHex("#C5D6E8") });
            outpostBody.AddChild(new Label { Text =
                "Для появи після перезапуску маяки мають бути в підписаному кресленні. Поки доступний лише вхід учасників компанії; публічний вхід відвідувачів потребує перевірки відкритого маршруту.",
                FontColorOverride = Color.FromHex("#ABB8C5") });
            var keyButton = new Button { Text = "Видати спеціальну карту доступів · 30 000 $", MinHeight = 35,
                ToolTip = "Карта працює тільки з корпоративним конфігуратором дверей. Це не посвідчення і не ключ до шатла." };
            keyButton.OnPressed += _ => OutpostToolRequested?.Invoke(org.Id,
                OrganizationOutpostAccessTier.Member, true);
            outpostBody.AddChild(keyButton);
            foreach (var (tier, label) in new[]
                     {
                         (OrganizationOutpostAccessTier.Visitor, "Відвідувач"),
                         (OrganizationOutpostAccessTier.Member, "Учасник"),
                         (OrganizationOutpostAccessTier.Deputy, "Заступник"),
                         (OrganizationOutpostAccessTier.Owner, "Голова")
                     })
            {
                var beaconButton = new Button { Text = $"Маяк появи: {label} — 2 000 $", MinHeight = 30 };
                beaconButton.OnPressed += _ => OutpostToolRequested?.Invoke(org.Id, tier, false);
                outpostBody.AddChild(beaconButton);
            }
        }
        if (!string.IsNullOrEmpty(_outpostLocalNotice))
            outpostBody.AddChild(new Label { Text = _outpostLocalNotice, FontColorOverride = Color.FromHex("#C2D0E2") });
        return outpost;
    }

    private static string IndustryContractGoods(OrganizationIndustry industry) => industry switch
    {
        OrganizationIndustry.Delivery => "картону",
        OrganizationIndustry.Trade => "скла",
        OrganizationIndustry.Security => "сталі",
        OrganizationIndustry.Services => "тканини",
        OrganizationIndustry.Manufacturing => "сталі",
        OrganizationIndustry.Research => "скла",
        OrganizationIndustry.Construction => "сталі",
        OrganizationIndustry.Medicine => "тканини",
        OrganizationIndustry.Mining => "сталі",
        OrganizationIndustry.Agriculture => "картону",
        _ => "матеріалів"
    };

    private Control BuildCreationView()
    {
        var panel = BuildTitledPanel("Створення нової організації", out var content, true);
        var scroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        scroll.AddChild(_creationBody);
        content.AddChild(scroll);
        return panel;
    }

    private static PanelContainer BuildTitledPanel(string title, out BoxContainer content, bool expand = false)
    {
        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            VerticalExpand = expand,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#1A1D21")
            }
        };

        content = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = expand,
            Margin = new Thickness(8),
            SeparationOverride = 6
        };
        content.AddChild(new Label
        {
            Text = title,
            FontColorOverride = Color.FromHex("#C8D7E8")
        });
        panel.AddChild(content);
        return panel;
    }

    private void SwitchTab(ConsoleTab tab)
    {
        if (tab != ConsoleTab.Management)
            _leaveConfirmation = false;
        _activeTab = tab;
        foreach (var (tabId, control) in _tabViews)
            control.Visible = tabId == tab;
        foreach (var (tabId, button) in _tabButtons)
            button.Modulate = tabId == tab ? Color.FromHex("#A7DDA9") : Color.White;
        RefreshViews();
    }

    private static int ParseAmount(LineEdit edit) => int.TryParse(edit.Text.Trim(), out var amount) ? amount : -1;

    private void UpdateColorPreview()
    {
        var color = Color.FromHex("#5599DD");
        var hex = _color.Text.Trim();
        if (hex.Length == 7 && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit))
            color = Color.FromHex(hex);
        _colorPreview.PanelOverride = new StyleBoxFlat { BackgroundColor = color };
    }

    private void RefreshFoundingPrice()
    {
        if (_creationKind is not { } kind) return;
        var fee = FoundingPrice(kind, _private.Pressed);
        var capital = Math.Max(0, ParseAmount(_investment));
        var total = fee + capital;
        _foundingTotal.Text = $"Збір: {fee:N0} $\nКапітал: {capital:N0} $\nРазом: {total:N0} $";
        if (_registerButton != null)
            _registerButton.Text = $"Зареєструвати за {total:N0} $";
    }

    private void SubmitCreate()
    {
        if (_creationKind is not { } kind)
            return;

        _creationError.Text = string.Empty;
        var industry = kind == OrganizationKind.Faction
            ? OrganizationIndustry.None
            : (OrganizationIndustry) _industry.SelectedId;

        if (kind == OrganizationKind.Faction && _factionFoci.Count == 0)
        {
            _creationError.Text = "Оберіть від одного до трьох спрямувань фракції.";
            return;
        }

        if (kind == OrganizationKind.Company && industry == OrganizationIndustry.None)
        {
            _creationError.Text = "Оберіть галузь діяльності компанії.";
            return;
        }

        if (DraftDescription.Length > 500 || _discordLink.Text.Length > 240)
        {
            _creationError.Text = "Опис або посилання надто довгі.";
            return;
        }
        var proposedName = _name.Text.Trim();
        if (proposedName.Length is < 3 or > 32 ||
            !proposedName.All(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '\''))
        {
            _creationError.Text = WrapText("Назва: 3–32 символи, лише літери, цифри, пробіли, дефіс або апостроф.");
            return;
        }

        var proposedColor = _color.Text.Trim();
        if (proposedColor.Length != 7 || proposedColor[0] != '#' ||
            !proposedColor.Skip(1).All(Uri.IsHexDigit))
        {
            _creationError.Text = "Колір повинен мати формат #RRGGBB.";
            return;
        }

        if (_entryFeeEnabled.Pressed && ParseAmount(_fee) is < 0 or > 1_000_000)
        {
            _creationError.Text = "Вступний внесок має бути від 0 до 1 000 000.";
            return;
        }

        var investment = ParseAmount(_investment);
        if (investment < 0 || investment > 1_000_000 ||
            _state == null || (long)investment + FoundingPrice(kind, _private.Pressed) > _state.PersonalBalance)
        {
            _creationError.Text = "Недостатньо коштів для реєстрації та початкового капіталу.";
            return;
        }

        var shares = _budgetChart.Allocation;
        var entryFee = _entryFeeEnabled.Pressed ? ParseAmount(_fee) : 0;
        CreateRequested?.Invoke(_name.Text, kind, industry, _private.Pressed, entryFee,
            _color.Text, investment, shares[0], shares[1], shares[2], shares[3]);
    }

    private static void Clear(BoxContainer container)
    {
        foreach (var child in container.Children.ToArray())
            container.RemoveChild(child);
    }

    private IEnumerable<OrganizationEntry> FilteredOrganizations()
    {
        var state = _state;
        if (state == null)
            return [];

        IEnumerable<OrganizationEntry> result = state.Organizations;
        var text = _search.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            result = result.Where(o => o.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

        result = _typeFilter.SelectedId switch
        {
            1 => result.Where(o => o.Kind == OrganizationKind.Faction),
            2 => result.Where(o => o.Kind == OrganizationKind.Company),
            _ => result
        };

        return (SortMode) _sortFilter.SelectedId switch
        {
            SortMode.InfluenceAsc => result.OrderBy(o => o.InfluencePercent).ThenBy(o => o.Name),
            SortMode.NameAsc => result.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.NameDesc => result.OrderByDescending(o => o.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.MembersDesc => result.OrderByDescending(o => o.Members).ThenBy(o => o.Name),
            SortMode.MembersAsc => result.OrderBy(o => o.Members).ThenBy(o => o.Name),
            _ => result.OrderByDescending(o => o.InfluencePercent).ThenBy(o => o.Name)
        };
    }

    public void UpdateState(OrganizationConsoleState state)
    {
        var changedIdentity = _stateInitialized &&
            (state.Membership != _previousMembership || state.TdfOnDuty != _previousTdfDuty ||
             state.ViewedBody != _previousViewedBody);
        if (_state != null && (_state.FleetRentalPrice != state.FleetRentalPrice ||
            _state.FleetListingName != state.FleetListingName ||
            _state.FleetBlueprintReady != state.FleetBlueprintReady))
            _fleetRentConfirm = false;
        _state = state;
        if (state.Notice.Contains("креслен", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.Contains("аванпост", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.StartsWith("Відхилено", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.Contains("файл шатла", StringComparison.OrdinalIgnoreCase))
            _outpostLocalNotice = state.Notice;
        if (changedIdentity)
            _outpostLocalNotice = string.Empty;
        _tabButtons[ConsoleTab.Tdf].Visible = state.TdfAuthorized;
        _tabButtons[ConsoleTab.Creation].Visible = state.Membership == null;
        _tabButtons[ConsoleTab.Services].Visible = state.Membership != null;
        if (_activeTab == ConsoleTab.Creation && state.Membership != null)
            SwitchTab(ConsoleTab.Registry);
        if (_activeTab == ConsoleTab.Services && state.Membership == null)
            SwitchTab(ConsoleTab.Registry);
        if (_activeTab == ConsoleTab.Tdf && !state.TdfAuthorized)
            SwitchTab(ConsoleTab.Registry);
        _stateInitialized = true;
        _previousMembership = state.Membership;
        _previousTdfDuty = state.TdfOnDuty;
        _previousViewedBody = state.ViewedBody;
        if (changedIdentity)
        {
            _leaveConfirmation = false;
            _outpostPurchaseConfirm = false;
            _outpostReplaceConfirm = false;
            _fleetRentConfirm = false;
            _fleetLocalNotice = string.Empty;
            _hiddenStocks.Clear();
            _selectedStock = null;
            _selectedOrganization = null;
            _personalizationExpanded = false;
            _outpostOverviewExpanded = true;
            _developmentExpanded = false;
            _economyExpanded = false;
            _expandedEconomyHelpRows.Clear();
            _developmentPage = 1;
            _personalizationBannerPicker = false;
            _newOrganizationBanner = [];
            _personalizationBannerStatusText = "Емблема без змін";
            SelectManagementSection(ManagementSection.Overview);
        }
        // Successful transactions play a sound; do not flood the top of the window with receipts.
        var error = state.Notice.StartsWith("Не ", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.StartsWith("Недостатньо", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.StartsWith("Некорект", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.StartsWith("Потрібно", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.StartsWith("Біржа:", StringComparison.OrdinalIgnoreCase) ||
            state.Notice.StartsWith("Акції недоступні", StringComparison.OrdinalIgnoreCase);
        _notice.Visible = error;
        _notice.Text = error ? state.Notice : string.Empty;
        _personalBalance.Text = state.ViewingAnotherCharacter
            ? "Перегляд персонажа"
            : $"Баланс: {state.PersonalBalance:N0} $";
        if (_selectedOrganization != null && state.Organizations.All(o => o.Id != _selectedOrganization.Value))
            _selectedOrganization = null;
        RefreshViews();
    }

    private void RefreshViews()
    {
        switch (_activeTab)
        {
            case ConsoleTab.Registry:
                RefreshRegistry();
                break;
            case ConsoleTab.Analytics:
                RefreshAnalytics();
                break;
            case ConsoleTab.Management:
                RefreshManagement();
                RefreshPersonnel();
                RefreshPermissions();
                RefreshSideBudget();
                RefreshShares();
                break;
            case ConsoleTab.Creation:
                RefreshCreation();
                break;
            case ConsoleTab.Services:
                RefreshServices();
                break;
            case ConsoleTab.Tdf:
                RefreshTdf();
                break;
        }
    }

    private void RefreshRegistry()
    {
        Clear(_registryList);
        Clear(_rankingBody);
        if (_state == null)
            return;
        var organizations = FilteredOrganizations().ToArray();
        if (_selectedOrganization != null && organizations.All(o => o.Id != _selectedOrganization.Value))
            _selectedOrganization = null;
        if (organizations.Length == 0)
            _registryList.AddChild(new Label { Text = "Діючих організацій не знайдено." });
        foreach (var org in organizations)
            _registryList.AddChild(BuildRegistryRow(org));
        if (_state.LiquidationArchive.Length > 0)
        {
            var archive = BuildFoldout("Архів ліквідованих компаній", out var archiveBody);
            foreach (var closed in _state.LiquidationArchive.Take(20))
                archiveBody.AddChild(new Label
                {
                    Text = WrapText($"{closed.Name}: {closed.ClosedUtc:yyyy-MM-dd}: {closed.Reason}", 54),
                    FontColorOverride = Color.FromHex("#9FAAB6")
                });
            _registryList.AddChild(archive);
        }
        var rank = 0;
        foreach (var org in _state.Organizations.OrderByDescending(o => o.InfluencePercent).ThenBy(o => o.Name).Take(12))
        {
            rank++;
            var color = Color.FromHex(org.Color);
            var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true,
                SeparationOverride = 7 };
            var banner = BuildOrganizationBanner(org, 35);
            banner.ToolTip = $"{rank}. {org.Name}";
            row.AddChild(banner);
            // Draw only the colored amount. No dark progress-bar track.
            row.AddChild(new OrganizationSectorBar(org.InfluencePercent, Color.FromHex(org.Color))
            {
                HorizontalExpand = true,
                MinHeight = 15,
                ToolTip = org.Name
            });
            row.AddChild(new Label { Text = $"{org.InfluencePercent:0.0}%", MinWidth = 46 });
            _rankingBody.AddChild(row);
        }
    }

    private Control BuildRegistryRow(OrganizationEntry org)
    {
        // The influence fill belongs ONLY to the header, never the expanded details.
        var column = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 0
        };

        var header = new OrganizationInfluenceFill(org.InfluencePercent)
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true
        };
        var open = new Button
        {
            HorizontalExpand = true,
            MinHeight = 68,
            ToolTip = org.Name,
            StyleBoxOverride = new StyleBoxFlat { BackgroundColor = Color.Transparent }
        };
        var contents = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 10,
            Margin = new Thickness(14, 8, 14, 8),
            MouseFilter = MouseFilterMode.Ignore
        };
        open.AddChild(contents);
        var labels = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4,
            MouseFilter = MouseFilterMode.Ignore
        };
        labels.AddChild(new Label { Text = org.Name, FontColorOverride = Color.FromHex(org.Color) });
        labels.AddChild(new Label
        {
            Text = $"{KindLabel(org.Kind)}  {(org.Kind == OrganizationKind.Company ? IndustryLabel(org.Industry) : FactionFociShortLabel(org))}  {org.Members} учасн.",
            FontColorOverride = Color.FromHex("#C0C6CD")
        });
        contents.AddChild(labels);
        contents.AddChild(new Label
        {
            Text = $"{org.InfluencePercent:0.0}%",
            MinWidth = 58,
            VerticalAlignment = VAlignment.Center,
            HorizontalAlignment = HAlignment.Right,
            FontColorOverride = Color.FromHex("#ECF1F4")
        });
        open.OnPressed += _ =>
        {
            _selectedOrganization = _selectedOrganization == org.Id ? null : org.Id;
            RefreshRegistry();
        };
        header.AddChild(open);
        column.AddChild(header);

        if (_selectedOrganization == org.Id)
        {
            var detailsBackground = new PanelContainer
            {
                HorizontalExpand = true,
                PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#262E35") }
            };
            var details = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true,
                Margin = new Thickness(14, 12, 14, 14),
                SeparationOverride = 6
            };
            BuildDetails(org, details);
            detailsBackground.AddChild(details);
            column.AddChild(detailsBackground);
        }
        return column;
    }

    private void BuildDetails(OrganizationEntry org, BoxContainer details)
    {
        // Banner and organization details are in a separate surface; the name remains
        // in the clickable header and is not printed a second time.
        var summary = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 12
        };
        summary.AddChild(BuildOrganizationBanner(org, 76));
        var facts = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4
        };
        facts.AddChild(new Label { Text = $"Тип: {KindLabel(org.Kind)}" });
        if (org.Kind == OrganizationKind.Company)
            facts.AddChild(new Label { Text = $"Галузь: {IndustryLabel(org.Industry)}" });
        else if (org.FactionFocus != OrganizationFactionFocus.None || org.FactionFoci.Length > 0)
            facts.AddChild(new Label { Text = $"Спрямування: {FactionFociLabel(org)}" });
        if (!string.IsNullOrWhiteSpace(org.Owner))
            facts.AddChild(new Label { Text = $"Власник: {org.Owner}" });
        facts.AddChild(new Label { Text = $"Учасників: {org.Members}" });
        summary.AddChild(facts);
        details.AddChild(summary);

        if (!string.IsNullOrWhiteSpace(org.Description))
            details.AddChild(new Label { Text = WrapText(org.Description, 60) });
        if (!string.IsNullOrEmpty(org.DiscordLink))
        {
            var lore = new Button { Text = "Матеріали організації", ToolTip = org.DiscordLink };
            lore.OnPressed += _ => IoCManager.Resolve<IUriOpener>().OpenUri(org.DiscordLink);
            details.AddChild(lore);
        }
        if (org.InsolvencyState != OrganizationInsolvencyState.Active)
            details.AddChild(new Label
            {
                Text = org.InsolvencyState == OrganizationInsolvencyState.Liquidation
                    ? "Ліквідація: операції призупинено"
                    : $"Фінансовий нагляд: {org.MissedUpkeepCycles}/6 несплачених періодів",
                FontColorOverride = Color.FromHex("#E3AE89")
            });
        if (org.CrisisActive)
            details.AddChild(new Label { Text = "Криза керівництва", FontColorOverride = Color.FromHex("#E8A876") });
        details.AddChild(new Label { Text = $"Вступ: {(org.IsPrivate ? "за схваленням" : "відкритий")}" });
        if (org.EntryFee > 0)
            details.AddChild(new Label { Text = $"Вступний внесок: {org.EntryFee:N0} $" });
        details.AddChild(new Label { Text = $"Вплив у секторі: {org.InfluencePercent:0.00}%" });
        if (org.Kind == OrganizationKind.Company)
            details.AddChild(new Label { Text = $"Частка галузевого ринку: {org.IndustryPercent:0.00}%" });
        details.AddChild(new Label
        {
            Text = $"Інвестиції: {org.CapitalSpent:N0} $",
            FontColorOverride = Color.FromHex("#98A0AA")
        });

        var isTdf = org.Name.StartsWith("TDF", StringComparison.OrdinalIgnoreCase);
        // TDF staffing is driven by service assignment, not public registration.
        // Only a member of this organization receives its own roster from the server.
        if (isTdf && _state?.Membership == org.Id)
        {
            foreach (var member in _state.Members)
                details.AddChild(new Label { Text = member.Name });
        }
        if (isTdf)
            return;

        if (_state?.Membership == null)
        {
            var join = new Button { Text = org.Pending ? "Заявку подано" : org.IsPrivate ? "Подати заявку" : "Вступити" };
            join.Disabled = org.Pending || org.InsolvencyState == OrganizationInsolvencyState.Liquidation;
            join.OnPressed += _ => JoinRequested?.Invoke(org.Id);
            details.AddChild(join);
        }
        else if (_state.Membership == org.Id)
            details.AddChild(new Label { Text = _state.Ownership == org.Id ? "Ви власник організації" : "Ви учасник організації" });
    }

    // Split long company names at natural boundaries, and long single words into chunks.
    // Unlike the old 12-character legend limit, no characters are discarded.
    private static string WrapStockName(string name, int width)
    {
        var parts = new List<string>();
        var line = string.Empty;
        foreach (var token in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remainder = token;
            while (remainder.Length > width)
            {
                if (line.Length > 0)
                {
                    parts.Add(line);
                    line = string.Empty;
                }
                parts.Add(remainder[..width]);
                remainder = remainder[width..];
            }
            if (remainder.Length == 0)
                continue;
            if (line.Length > 0 && line.Length + remainder.Length + 1 > width)
            {
                parts.Add(line);
                line = remainder;
            }
            else
                line = line.Length == 0 ? remainder : line + " " + remainder;
        }
        if (line.Length > 0)
            parts.Add(line);
        return string.Join("\n", parts);
    }

    private void RefreshAnalytics()
    {
        Clear(_analyticsBody);
        if (_state == null)
            return;

        var stocks = _state.Stocks.OrderBy(s => s.Name).ToArray();
        if (stocks.Length == 0)
        {
            _analyticsBody.AddChild(new Label { Text = "Компаній на біржі ще немає." });
            return;
        }

        // TOP: full-name toggle list on the LEFT, wide graph on the RIGHT.
        // These are two visibly separate dark panels, matching the Registry tab.
        var comparison = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8
        };
        _analyticsBody.AddChild(comparison);

        var legendPanel = BuildTitledPanel("Компанії / ваш пакет", out var legend, true);
        legendPanel.HorizontalExpand = false;
        legendPanel.MinWidth = 285;
        legendPanel.MaxWidth = 310;
        comparison.AddChild(legendPanel);
        var legendScroll = new ScrollContainer
        {
            HScrollEnabled = false,
            HorizontalExpand = true,
            VerticalExpand = true,
            MinHeight = 320
        };
        var legendRows = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4
        };
        legendScroll.AddChild(legendRows);
        legend.AddChild(legendScroll);
        OrganizationStockChart? graphControl = null;
        foreach (var stock in stocks)
        {
            var hidden = _hiddenStocks.Contains(stock.Id);
            var percent = stock.Issued > 0 ? 100d * stock.Owned / stock.Issued : 0d;
            var row = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 5
            };
            // The entire company name is ONE button. No separate visibility icon.
            var toggle = new Button
            {
                HorizontalExpand = true,
                MinHeight = 32,
                ToolTip = $"{stock.Name}: {(hidden ? "показати" : "приховати")} графік",
                Modulate = hidden ? Color.FromHex("#838992") : Color.FromHex(stock.Color)
            };
            var caption = WrapStockName(stock.Name, 26);
            var companyText = new Label
            {
                Text = hidden ? StruckText(caption) : caption,
                HorizontalExpand = true,
                MouseFilter = MouseFilterMode.Ignore,
                FontColorOverride = Color.White
            };
            toggle.AddChild(companyText);
            var originalColor = hidden ? Color.FromHex("#838992") : Color.FromHex(stock.Color);
            var isWhite = stock.Color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) ||
                stock.Color.Equals("#FFF", StringComparison.OrdinalIgnoreCase);
            var hoverColor = isWhite ? Color.FromHex("#FFE6A0") : Color.White;
            toggle.OnMouseEntered += _ =>
            {
                toggle.Modulate = hoverColor;
                companyText.FontColorOverride = Color.White;
                graphControl?.SetHovered(stock.Id);
            };
            toggle.OnMouseExited += _ =>
            {
                toggle.Modulate = originalColor;
                graphControl?.SetHovered(null);
            };
            toggle.OnPressed += _ =>
            {
                if (!_hiddenStocks.Add(stock.Id))
                    _hiddenStocks.Remove(stock.Id);
                RefreshAnalytics();
            };
            row.AddChild(toggle);
            row.AddChild(new Label
            {
                Text = $"{percent:0.0}%",
                MinWidth = 57,
                HorizontalAlignment = HAlignment.Right,
                VerticalAlignment = VAlignment.Center,
                ToolTip = $"Ваш пакет: {stock.Owned:N0} / {stock.Issued:N0} акцій"
            });
            legendRows.AddChild(row);
        }

        var graphPanel = BuildTitledPanel("Динаміка котирувань", out var graph, true);
        graphPanel.MinWidth = 475;
        comparison.AddChild(graphPanel);
        var chartModes = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 5
        };
        var relativeMode = new Button
        {
            Text = "Зміна %",
            MinHeight = 28,
            Modulate = _chartRelativeMode ? Color.FromHex("#94CAA8") : Color.FromHex("#83909D")
        };
        var absoluteMode = new Button
        {
            Text = "Ціна $",
            MinHeight = 28,
            Modulate = !_chartRelativeMode ? Color.FromHex("#94CAA8") : Color.FromHex("#83909D")
        };
        relativeMode.OnPressed += _ => { _chartRelativeMode = true; RefreshAnalytics(); };
        absoluteMode.OnPressed += _ => { _chartRelativeMode = false; RefreshAnalytics(); };
        chartModes.AddChild(relativeMode);
        chartModes.AddChild(absoluteMode);
        chartModes.AddChild(new Control { HorizontalExpand = true });
        graph.AddChild(chartModes);
        var visible = stocks.Where(s => !_hiddenStocks.Contains(s.Id) && s.History.Length > 0).ToArray();
        graphControl = new OrganizationStockChart(visible, _chartRelativeMode);
        graph.AddChild(graphControl);

        // LOWER: a market-wide order table on the left and company-specific trading on the right.
        _stockIds.Clear();
        _stockSelector.Clear();
        foreach (var stock in stocks)
        {
            _stockIds.Add(stock.Id);
            _stockSelector.AddItem(stock.Name, _stockIds.Count - 1);
        }
        var index = Array.FindIndex(stocks, stock => stock.Id == _selectedStock);
        if (index < 0)
            index = 0;
        _selectedStock = stocks[index].Id;
        _stockSelector.SelectId(index);
        if (_stockSelector.Parent is BoxContainer previous)
            previous.RemoveChild(_stockSelector);
        var selected = stocks[index];

        // BOTTOM: two independent, visibly outlined panels (like Registry),
        // never two text columns painted on the same large background.
        var lowerColumns = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _analyticsBody.AddChild(lowerColumns);

        var bookPanel = BuildTitledPanel("Біржові пропозиції", out var book);
        // Slightly narrower than the original wide marketplace, but roomier than v4.
        // MaxWidth keeps the trading panel usefully sized on larger windows.
        bookPanel.MinWidth = 430;
        bookPanel.HorizontalExpand = true;
        bookPanel.MaxWidth = 545;
        lowerColumns.AddChild(bookPanel);

        var tradePanel = BuildTitledPanel("Торгівля акціями", out var trade, true);
        tradePanel.MinWidth = 390;
        lowerColumns.AddChild(tradePanel);

        // Market filter is independent from the stock currently selected for trading.
        if (_marketOrderCompany.HasValue && stocks.All(s => s.Id != _marketOrderCompany.Value))
            _marketOrderCompany = null;
        var filterRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 5
        };
        var companyFilter = new OptionButton { HorizontalExpand = true, MinHeight = 30, MaxWidth = 220 };
        companyFilter.AddItem("Усі компанії", 0);
        for (var i = 0; i < stocks.Length; i++)
            companyFilter.AddItem(stocks[i].Name, i + 1);
        var companyFilterIndex = _marketOrderCompany.HasValue
            ? Array.FindIndex(stocks, s => s.Id == _marketOrderCompany.Value) + 1
            : 0;
        companyFilter.SelectId(companyFilterIndex);
        companyFilter.OnItemSelected += args =>
        {
            _marketOrderCompany = args.Id > 0 && args.Id <= stocks.Length ? stocks[args.Id - 1].Id : null;
            RefreshAnalytics();
        };
        filterRow.AddChild(companyFilter);
        filterRow.AddChild(new Control { HorizontalExpand = true });
        var refreshOffers = new Button
        {
            Text = "Оновити",
            MinHeight = 30,
            MinWidth = 84,
            ToolTip = "Оновити біржові пропозиції"
        };
        refreshOffers.OnPressed += _ => RefreshRequested?.Invoke();
        filterRow.AddChild(refreshOffers);
        book.AddChild(filterRow);

        // Public market lists SELL offers only. Company reserve offerings appear below players.
        var sellOrders = _state.LimitOrders
            .Where(o => o.Side == OrganizationLimitOrderSide.Sell &&
                (!_marketOrderCompany.HasValue || o.Organization == _marketOrderCompany.Value))
            .OrderBy(o => o.Price).ThenByDescending(o => o.Quantity).ThenBy(o => o.Id).ToArray();

        // Choosing an order prepares price and quantity, it never executes a trade by itself.
        var quantity = new LineEdit
        {
            Text = (_preparedOrderQuantity ?? 1).ToString(),
            PlaceHolder = "Кількість",
            MinHeight = 32,
            HorizontalExpand = true
        };
        var limitPrice = new LineEdit
        {
            Text = (_preparedOrderPrice ?? selected.Quote).ToString(),
            PlaceHolder = "Ціна за акцію",
            MinHeight = 32,
            HorizontalExpand = true
        };
        _preparedOrderQuantity = null;
        _preparedOrderPrice = null;
        var estimate = new Label { FontColorOverride = Color.FromHex("#ADB9C7") };
        void UpdateEstimate()
        {
            var count = ParseAmount(quantity);
            var price = ParseAmount(limitPrice);
            if (count is < 1 or > 100 || price is < 1 or > 25_000)
            {
                estimate.Text = "Ліміт: 1–100 акцій, ціна 1–25 000 $ за акцію.";
                return;
            }
            var corporateBuy = ((long)selected.Quote * 105 + 99) / 100 * count;
            var corporateSell = Math.Max(1L, (long)selected.Quote * 95 / 100) * count;
            estimate.Text = $"Лімітна заявка: {(long)count * price:N0} $\n" +
                (_tradeSellMode ? $"Продаж компанії: {corporateSell:N0} $"
                                : $"Купівля з резерву: {corporateBuy:N0} $");
        }
        quantity.OnTextChanged += _ => UpdateEstimate();
        limitPrice.OnTextChanged += _ => UpdateEstimate();
        UpdateEstimate();

        void AddMarketHeader(BoxContainer section)
        {
            var header = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 5
            };
            header.AddChild(new Label { Text = "Компанія", HorizontalExpand = true });
            header.AddChild(new Label { Text = "Акцій", MinWidth = 53 });
            header.AddChild(new Label { Text = "Ціна", MinWidth = 74 });
            header.AddChild(new Label { Text = "Частка", MinWidth = 60 });
            section.AddChild(header);
        }

        var ordersSection = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 3
        };
        AddMarketHeader(ordersSection);
        if (sellOrders.Length == 0)
            ordersSection.AddChild(new Label { Text = "Пропозицій немає." });
        foreach (var order in sellOrders.Take(24))
        {
            var found = stocks.FirstOrDefault(s => s.Id == order.Organization);
            if (found == null)
                continue;
            var orderRow = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 4
            };
            var pick = new Button
            {
                HorizontalExpand = true,
                MinHeight = 29,
                MaxHeight = 32,
                ToolTip = $"{found.Name} · {order.TraderName}"
            };
            var columns = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 5,
                MouseFilter = MouseFilterMode.Ignore
            };
            columns.AddChild(new Label
            {
                Text = found.Name.Length > 22 ? found.Name[..21] + "…" : found.Name,
                HorizontalExpand = true,
                FontColorOverride = Color.FromHex("#E0E7F0")
            });
            columns.AddChild(new Label { Text = $"{order.Quantity:N0}", MinWidth = 53 });
            columns.AddChild(new Label { Text = $"{order.Price:N0} $", MinWidth = 74 });
            columns.AddChild(new Label { Text = found.Issued > 0
                ? $"{100d * order.Quantity / found.Issued:0.##}%" : "—", MinWidth = 60 });
            pick.AddChild(columns);
            pick.OnPressed += _ =>
            {
                if (_selectedStock != order.Organization)
                {
                    _selectedStock = order.Organization;
                    _preparedOrderQuantity = Math.Clamp(order.Quantity, 1, 100);
                    _preparedOrderPrice = order.Price;
                    RefreshAnalytics();
                }
                else
                {
                    quantity.Text = Math.Clamp(order.Quantity, 1, 100).ToString();
                    limitPrice.Text = order.Price.ToString();
                    UpdateEstimate();
                }
            };
            orderRow.AddChild(pick);
            if (order.IsMine)
            {
                var cancel = new Button
                {
                    Text = "×",
                    MinWidth = 28,
                    Disabled = _state.TdfOnDuty || _state.ViewingAnotherCharacter,
                    ToolTip = "Скасувати заявку"
                };
                cancel.OnPressed += _ => LimitCancelRequested?.Invoke(order.Organization, order.Id);
                orderRow.AddChild(cancel);
            }
            ordersSection.AddChild(orderRow);
        }
        book.AddChild(ordersSection);

        // Company-issued stock is deliberately after all player sell orders.
        var issued = stocks.Where(stock => stock.Listed && !stock.TradingSuspended && stock.Available > 0 &&
            (!_marketOrderCompany.HasValue || stock.Id == _marketOrderCompany.Value))
            .OrderBy(stock => stock.Quote).ThenBy(stock => stock.Name).ToArray();
        if (issued.Length > 0)
        {
            book.AddChild(new Label
            {
                Text = "Резерв компаній",
                FontColorOverride = Color.FromHex("#A4D5B2"),
                Margin = new Thickness(0, 8, 0, 0)
            });
            var companySection = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true,
                SeparationOverride = 3
            };
            AddMarketHeader(companySection);
            foreach (var stock in issued)
            {
                // The same 5% corporate reserve spread as the existing trade handler.
                var offerPrice = ((long)stock.Quote * 105 + 99) / 100;
                var corporate = new Button { HorizontalExpand = true, MinHeight = 29, MaxHeight = 32, ToolTip = stock.Name };
                var columns = new BoxContainer
                {
                    Orientation = LayoutOrientation.Horizontal,
                    HorizontalExpand = true,
                    SeparationOverride = 5,
                    MouseFilter = MouseFilterMode.Ignore
                };
                columns.AddChild(new Label { Text = stock.Name.Length > 22 ? stock.Name[..21] + "…" : stock.Name, HorizontalExpand = true });
                columns.AddChild(new Label { Text = $"{stock.Available:N0}", MinWidth = 53 });
                columns.AddChild(new Label { Text = $"{offerPrice:N0} $", MinWidth = 74 });
                columns.AddChild(new Label { Text = stock.Issued > 0
                    ? $"{100d * stock.Available / stock.Issued:0.##}%" : "—", MinWidth = 60 });
                corporate.AddChild(columns);
                corporate.OnPressed += _ =>
                {
                    _selectedStock = stock.Id;
                    _preparedOrderQuantity = 1;
                    _preparedOrderPrice = stock.Quote;
                    RefreshAnalytics();
                };
                companySection.AddChild(corporate);
            }
            book.AddChild(companySection);
        }

        // Right trading panel: the company dropdown no longer occupies a whole row.
        var selectionRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };
        selectionRow.AddChild(new Label { Text = "Компанія", HorizontalExpand = true });
        _stockSelector.MinWidth = 175;
        _stockSelector.MaxWidth = 200;
        _stockSelector.HorizontalExpand = false;
        selectionRow.AddChild(_stockSelector);
        trade.AddChild(selectionRow);

        // Same dropdown style as company selection. The choice affects the visible
        // actions only, without changing the existing stock-market event handlers.
        var operationRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };
        operationRow.AddChild(new Label { Text = "Операція", HorizontalExpand = true });
        var tradeMode = new OptionButton
        {
            MinWidth = 175,
            MaxWidth = 200,
            HorizontalExpand = false,
            MinHeight = 30
        };
        tradeMode.AddItem("Купівля", 0);
        tradeMode.AddItem("Продаж", 1);
        tradeMode.SelectId(_tradeSellMode ? 1 : 0);
        operationRow.AddChild(tradeMode);
        trade.AddChild(operationRow);

        var ownedPercent = selected.Issued > 0 ? 100d * selected.Owned / selected.Issued : 0d;
        var previousQuote = selected.History.Length > 1 ? selected.History[^2] : selected.Quote;
        var delta = selected.Quote - previousQuote;
        var deltaPercent = previousQuote > 0 ? 100d * delta / previousQuote : 0d;
        AddMetric(trade, "Ціна за акцію", $"{selected.Quote:N0} $");
        AddMetric(trade, "Зміна останнього кроку", $"{delta:+#;-#;0} $ ({deltaPercent:+0.0;-0.0;0.0}%)",
            delta >= 0 ? Color.FromHex("#9DCCAA") : Color.FromHex("#D6A0A0"));
        AddMetric(trade, "Ваш пакет", $"{selected.Owned:N0} / {selected.Issued:N0} ({ownedPercent:0.0}%)");
        AddMetric(trade, "Вартість пакета за котируванням", $"{(long)selected.Owned * selected.Quote:N0} $");
        AddMetric(trade, "Вільні для продажу", $"{selected.UnreservedOwned:N0}");
        AddMetric(trade, "Резерв компанії", $"{selected.Available:N0}");
        var bestAsk = _state.LimitOrders.Where(o => o.Organization == selected.Id &&
                o.Side == OrganizationLimitOrderSide.Sell)
            .Select(o => (int?)o.Price).OrderBy(p => p).FirstOrDefault();
        var bestBid = _state.LimitOrders.Where(o => o.Organization == selected.Id &&
                o.Side == OrganizationLimitOrderSide.Buy)
            .Select(o => (int?)o.Price).OrderByDescending(p => p).FirstOrDefault();
        AddMetric(trade, "Найкраща ціна продажу", bestAsk.HasValue ? $"{bestAsk.Value:N0} $" : "—");
        AddMetric(trade, "Найкраща ставка купівлі", bestBid.HasValue ? $"{bestBid.Value:N0} $" : "—");
        if (bestAsk.HasValue && bestBid.HasValue)
            AddMetric(trade, "Спред", $"{bestAsk.Value - bestBid.Value:N0} $");

        var canTrade = !_state.TdfOnDuty && !_state.ViewingAnotherCharacter &&
            selected.Listed && !selected.TradingSuspended;
        if (!selected.Listed || selected.TradingSuspended)
            trade.AddChild(new Label
            {
                Text = selected.TradingSuspended
                    ? "Біржову торгівлю тимчасово зупинено через фінансовий нагляд."
                    : "Компанія поки не відкрила торгівлю акціями.",
                FontColorOverride = Color.FromHex("#E7BE84")
            });
        var inputRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 5
        };
        inputRow.AddChild(quantity);
        inputRow.AddChild(limitPrice);
        trade.AddChild(inputRow);
        trade.AddChild(estimate);
        var instant = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };
        var buy = new Button
        {
            Text = "Купити в компанії",
            HorizontalExpand = true,
            Modulate = Color.FromHex("#91CCAA"),
            Disabled = !canTrade || selected.Available < 1,
            ToolTip = "Купівля з корпоративного резерву за поточним котируванням"
        };
        var sell = new Button
        {
            Text = "Продати компанії",
            HorizontalExpand = true,
            Modulate = Color.FromHex("#D9A2A2"),
            Disabled = !canTrade || selected.UnreservedOwned < 1,
            ToolTip = "Продаж вільних акцій до резерву компанії"
        };
        buy.OnPressed += _ => StockOrderRequested?.Invoke(selected.Id, OrganizationStockOrder.Buy, ParseAmount(quantity));
        sell.OnPressed += _ => StockOrderRequested?.Invoke(selected.Id, OrganizationStockOrder.Sell, ParseAmount(quantity));
        instant.AddChild(buy);
        instant.AddChild(sell);
        trade.AddChild(instant);

        trade.AddChild(new Label { Text = "Лімітні заявки", FontColorOverride = Color.FromHex("#C7D5E2") });
        var makeOrders = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };
        var bid = new Button
        {
            Text = "Заявка на купівлю",
            HorizontalExpand = true,
            Disabled = !canTrade,
            Modulate = Color.FromHex("#9BC6A4")
        };
        var ask = new Button
        {
            Text = "Заявка на продаж",
            HorizontalExpand = true,
            Disabled = !canTrade || selected.UnreservedOwned < 1,
            Modulate = Color.FromHex("#D2B19B")
        };
        bid.OnPressed += _ => LimitOrderRequested?.Invoke(selected.Id,
            OrganizationLimitOrderSide.Buy, ParseAmount(quantity), ParseAmount(limitPrice));
        ask.OnPressed += _ => LimitOrderRequested?.Invoke(selected.Id,
            OrganizationLimitOrderSide.Sell, ParseAmount(quantity), ParseAmount(limitPrice));
        makeOrders.AddChild(bid);
        makeOrders.AddChild(ask);
        trade.AddChild(makeOrders);

        void UpdateTradeMode()
        {
            buy.Visible = !_tradeSellMode;
            sell.Visible = _tradeSellMode;
            bid.Visible = !_tradeSellMode;
            ask.Visible = _tradeSellMode;
            UpdateEstimate();
        }
        tradeMode.OnItemSelected += args =>
        {
            tradeMode.SelectId(args.Id);
            _tradeSellMode = args.Id == 1;
            UpdateTradeMode();
        };
        UpdateTradeMode();

        var receipts = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 5
        };
        var dividends = new Button
        {
            Text = "Дивіденди",
            HorizontalExpand = true,
            Disabled = _state.TdfOnDuty || _state.ViewingAnotherCharacter,
            ToolTip = "Перевірити доступні дивіденди"
        };
        dividends.OnPressed += _ => DividendClaimRequested?.Invoke(selected.Id);
        var proceeds = new Button
        {
            Text = "Виручка з біржі",
            HorizontalExpand = true,
            Disabled = _state.TdfOnDuty || _state.ViewingAnotherCharacter,
            ToolTip = "Отримати кошти за виконані заявки"
        };
        proceeds.OnPressed += _ => MarketClaimRequested?.Invoke(selected.Id);
        receipts.AddChild(dividends);
        receipts.AddChild(proceeds);
        trade.AddChild(receipts);
        if (_state.TdfOnDuty)
            trade.AddChild(new Label
            {
                Text = "Торгівля недоступна під час служби TDF.",
                FontColorOverride = Color.FromHex("#DDBCA1")
            });

        var rankingPanel = BuildFoldout("Котирування: таблиця", out var rankingBody);
        foreach (var stock in stocks.OrderByDescending(s => s.Quote).Take(16))
            AddMetric(rankingBody, stock.Name, $"{stock.Quote:N0} $", Color.FromHex(stock.Color));
        book.AddChild(rankingPanel);


    }

    private static string StruckText(string text) =>
        string.Concat(text.Select(c => c is ' ' or '\n' ? c.ToString() : $"{c}\u0336"));

    // These are direct factors used by the server economy, not promised profits.
    private static string DevelopmentBenefit(OrganizationDevelopmentBranch branch, int totalLevel) => branch switch
    {
        OrganizationDevelopmentBranch.Logistics => $"охоплення +{totalLevel * 3.8:0.#}%",
        OrganizationDevelopmentBranch.Research =>
            $"досвід +{totalLevel}/цикл, витрати −{totalLevel * 0.9:0.#} п.п.",
        OrganizationDevelopmentBranch.Infrastructure =>
            $"охоплення +{totalLevel * 2.4:0.#}%, витрати −{totalLevel * 1.6:0.#} п.п.",
        OrganizationDevelopmentBranch.ServiceNetwork => $"охоплення +{totalLevel * 2.6:0.#}%",
        OrganizationDevelopmentBranch.RiskManagement =>
            $"витрати −{totalLevel * 0.9:0.#} п.п., ризик −до {Math.Min(60.0, totalLevel * 7.5):0.#}%",
        _ => string.Empty
    };

    private void RefreshManagement()
    {
        Clear(_managementBody);
        Clear(_financeBody);
        if (_state == null)
            return;

        if (_state.Membership == null)
        {
            _managementBody.AddChild(new Label { Text = "Ви не перебуваєте в жодній організації." });
            _managementBody.AddChild(new Label { Text = WrapText("Після вступу тут з’явиться керування складом, заявками та скарбницею.") });
            return;
        }

        var org = _state.Organizations.FirstOrDefault(o => o.Id == _state.Membership.Value);
        if (org == null)
        {
            _managementBody.AddChild(new Label { Text = "Не вдалося знайти поточну організацію." });
            return;
        }

        if (_state.TdfOnDuty)
        {
            _managementBody.AddChild(new Label { Text = "TDF: Служба контролю сектора",
                FontColorOverride = Color.FromHex("#D6AD67") });
            var service = BuildTitledPanel("Ваше призначення", out var serviceBody);
            AddMetric(serviceBody, "Звання", _state.TdfRank);
            AddMetric(serviceBody, "Базовий мандат", "35%");
            AddMetric(serviceBody, "Поточна діяльність", $"+{_state.TdfOperationalPercent:0.00}%");
            AddMetric(serviceBody, "Результати попередніх змін", $"+{_state.TdfLongTermPercent:0.00}%");
            AddMetric(serviceBody, "У секторі", $"{_state.OnlineMembers} службовців");
            _managementBody.AddChild(service);
            AddMetric(_financeBody, "Загальний фонд", $"{_state.TdfTreasury:N0} $");
            AddMetric(_financeBody, "Фонд забезпечення флоту", $"{_state.TdfShuttleReserve:N0} $");
            if (_state.TdfTerminal)
            {
                var amount = new LineEdit { Text = "100", MinHeight = 32, PlaceHolder = "Сума" };
                _financeBody.AddChild(amount);
                var donate = new Button { Text = "Поповнити фонд з особистого рахунку", MinHeight = 32 };
                donate.OnPressed += _ => TdfFundRequested?.Invoke(OrganizationTdfFundAction.Deposit, ParseAmount(amount));
                _financeBody.AddChild(donate);
                if (_state.TdfCanAllocateBudget)
                {
                    var allocate = new Button { Text = "Виділити на придбання шатлів", MinHeight = 32 };
                    allocate.OnPressed += _ => TdfFundRequested?.Invoke(OrganizationTdfFundAction.AllocateForShuttles, ParseAmount(amount));
                    _financeBody.AddChild(allocate);
                    var recall = new Button { Text = "Повернути до основного фонду", MinHeight = 32 };
                    recall.OnPressed += _ => TdfFundRequested?.Invoke(OrganizationTdfFundAction.ReturnFromShuttles, ParseAmount(amount));
                    _financeBody.AddChild(recall);
                }
            }
            if (_state.AssociatedOrganization is { } associatedId)
            {
                var association = _state.Organizations.FirstOrDefault(o => o.Id == associatedId);
                if (association != null)
                {
                    var assocPanel = BuildTitledPanel("Збережена асоціація", out var assocBody);
                    AddMetric(assocBody, "Організація", association.Name);
                    _managementBody.AddChild(assocPanel);
                }
            }
            return;
        }
        _managementBody.AddChild(new Label { Text = org.Name, FontColorOverride = Color.FromHex(org.Color) });
        var summary = BuildTitledPanel("Ключові показники", out var summaryBody);
        AddMetric(summaryBody, "Ваша роль", RoleLabel(_state.Role));
        AddMetric(summaryBody, "Скарбниця", $"{_state.Treasury:N0} $");
        AddMetric(summaryBody, "Початковий капітал", $"{_state.InitialInvestment:N0} $");
        _managementBody.AddChild(summary);
        if (org.Kind == OrganizationKind.Faction)
        {
            var network = BuildFoldout("Підсумки діяльності та архів", out var networkBody, false);
            AddMetric(networkBody, "Зареєстровано вузлів", _state.FactionNodeCount.ToString(),
                Color.FromHex("#A4D4B2"));
            AddMetric(networkBody, "Зареєстровано досліджень", _state.FactionFindingCount.ToString(),
                Color.FromHex("#A4D4B2"));
            AddMetric(networkBody, "Авторитет фракції", _state.FactionAuthorityPoints.ToString());
            AddMetric(networkBody, "Вплив у секторі", $"{org.InfluencePercent:0.00}%", Color.FromHex("#A4D4B2"));
            AddMetric(networkBody, "Частка зареєстрованої мережі",
                $"{_state.FactionNetworkSharePercent:0.0}%", Color.FromHex("#A4D4B2"));
            AddMetric(networkBody, "Затримка між завданнями",
                _state.FactionAuthorityPoints >= 50 ? "2 хв" :
                _state.FactionAuthorityPoints >= 20 ? "3 хв" : "4 хв");
            AddMetric(networkBody, "Завершено завдань", _state.FactionOperationsCompleted.ToString());
            AddMetric(networkBody, "Стабілізація на ділянці",
                $"{_state.FactionSignalSeconds} с (основа — 40 с)", Color.FromHex("#A4D4B2"));
            foreach (var node in _state.FactionNodes)
            {
                var site = string.IsNullOrWhiteSpace(node.SiteCode)
                    ? "історичний запис" : node.SiteCode;
                AddMetric(networkBody, OrganizationFactionNodeRules.Name(node.Kind),
                    $"{site} · {node.EstablishedUtc:dd.MM.yyyy} · " +
                    (node.ActiveThisRound ? "антена присутня у цій зміні" : "архів / неактивний"),
                    node.ActiveThisRound ? Color.FromHex("#A4D4B2") : Color.FromHex("#B8C7D5"));
                if (node.ActiveThisRound && !string.IsNullOrEmpty(node.RoundLocation))
                    AddMetric(networkBody, "Поточне розташування", node.RoundLocation);
            }
            foreach (var finding in _state.FactionFindings)
            {
                AddMetric(networkBody, "Архів · " + finding.SiteCode,
                    $"{finding.SiteName} · {finding.RecordedUtc:dd.MM.yyyy}",
                    Color.FromHex("#AAC7D6"));
            }
            networkBody.AddChild(new Label
            {
                Text = "Секторна присутність зростає від фактично завершених завдань і програм розвитку. Частка ретрансляторів відображається окремо. Авторитет відкриває нові рівні програм і скорочує відновлення між завданнями.",
                FontColorOverride = Color.FromHex("#9DB7C8")
            });
            _managementBody.AddChild(network);
        }
        if (org.Kind == OrganizationKind.Faction)
            _managementBody.AddChild(BuildFactionProgressPanel(org));
        else if (org.Kind == OrganizationKind.Company)
            _managementBody.AddChild(BuildOutpostOverviewPanel());
        // Corporate stations and rental listings live under "Активи".
        if (HasRight(OrganizationPermission.Settings))
        {
            var personal = BuildFoldout("Персоналізація", out var personalBody,
                _personalizationExpanded, expanded => _personalizationExpanded = expanded);
            var name = new LineEdit { Text = org.Name, PlaceHolder = "Назва організації", HorizontalExpand = true };
            var color = new LineEdit { Text = org.Color, PlaceHolder = "#RRGGBB", HorizontalExpand = true };
            var fee = new LineEdit { Text = org.EntryFee.ToString(), PlaceHolder = "Вступний внесок" };
            var isPrivate = new CheckBox { Text = "Приватний вступ", Pressed = org.IsPrivate };
            var publicRoster = new CheckBox { Text = "Відкритий список учасників", Pressed = org.RosterPublic };
            personalBody.AddChild(new Label { Text = "Назва" });
            personalBody.AddChild(name);
            personalBody.AddChild(new Label { Text = "Колір" });
            personalBody.AddChild(color);
            personalBody.AddChild(new Label { Text = "Вступний внесок" });
            personalBody.AddChild(fee);
            personalBody.AddChild(isPrivate);
            personalBody.AddChild(publicRoster);
            var bannerLine = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                SeparationOverride = 10, HorizontalExpand = true };
            _personalizationBannerPreview = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                MinWidth = 76, MinHeight = 76
            };
            if (_newOrganizationBanner.Length > 0)
                _personalizationBannerPreview.AddChild(new OrganizationBannerControl(_newOrganizationBanner, 76));
            else
                _personalizationBannerPreview.AddChild(BuildOrganizationBanner(org, 76));
            bannerLine.AddChild(_personalizationBannerPreview);
            var logo = new Button { Text = "Змінити емблему (PNG)", MinHeight = 32 };
            logo.OnPressed += _ =>
            {
                _personalizationBannerPicker = true;
                BannerChooseRequested?.Invoke();
            };
            bannerLine.AddChild(logo);
            personalBody.AddChild(bannerLine);
            _personalizationBannerStatus = new Label { Text = _personalizationBannerStatusText };
            personalBody.AddChild(_personalizationBannerStatus);
            var savePersonal = new Button { Text = "Зберегти персоналізацію", MinHeight = 34 };
            savePersonal.OnPressed += _ => ConfigureRequested?.Invoke(org.Id, isPrivate.Pressed,
                ParseAmount(fee), color.Text, publicRoster.Pressed, name.Text,
                _newOrganizationBanner, _newOrganizationBanner.Length > 0);
            personalBody.AddChild(savePersonal);
            _managementBody.AddChild(personal);
        }
        if (org.InsolvencyState != OrganizationInsolvencyState.Active)
        {
            var administration = BuildTitledPanel("Фінансовий стан", out var administrationBody);
            administrationBody.AddChild(new Label
            {
                Text = org.InsolvencyState == OrganizationInsolvencyState.Liquidation
                    ? "ЛІКВІДАЦІЯ: компанія завершує розрахунки з акціонерами та учасниками ринку."
                    : "ФІНАНСОВИЙ НАГЛЯД: нові інвестиції й біржові операції призупинені.",
                FontColorOverride = Color.FromHex("#DAB38E")
            });
            administrationBody.AddChild(new Label
            {
                Text = org.InsolvencyState == OrganizationInsolvencyState.Liquidation
                    ? "Біржові резерви й належні гравцям виплати залишаються захищеними."
                    : $"Несплачених періодів: {org.MissedUpkeepCycles}. Для відновлення потрібен капітал і два успішні періоди утримання."
            });
            _managementBody.AddChild(administration);
        }
        var leadership = BuildFoldout("Керівництво й склад", out var leadershipBody);
        leadershipBody.AddChild(new Label { Text = $"Керівник: {org.Owner}" });
        var deputy = _state.Members.FirstOrDefault(m => m.IsDeputy);
        if (deputy != null)
            leadershipBody.AddChild(new Label { Text = $"Заступник: {deputy.Name}" });
        leadershipBody.AddChild(new Label
        {
            Text = $"Учасників: {org.Members}: присутні: {_state.OnlineMembers}"
        });
        leadershipBody.AddChild(new Label { Text = "Повний список і відзнаки: у розділі «Кадри»." });
        _managementBody.AddChild(leadership);
        if (org.Kind == OrganizationKind.Company)
        {
            var status = BuildFoldout("Стан керівництва", out var statusBody, org.CrisisActive);
            if (org.CrisisActive)
            {
                statusBody.AddChild(new Label
                {
                    Text = $"КРИЗА КЕРІВНИЦТВА: періодів утримання минуло: {_state.CrisisTicks}",
                    FontColorOverride = Color.FromHex("#E8A876")
                });
                statusBody.AddChild(new Label
                {
                    Text = "Перші 15 хв: без додаткової втрати впливу.\nДалі вплив поступово зменшується до відновлення керівництва."
                });
                if (_state.Role == OrganizationMemberRole.Officer)
                {
                    var takeOver = new Button { Text = "Перебрати керівництво", MinHeight = 34 };
                    takeOver.Disabled = _state.CrisisTicks < 1 || !_state.CanClaimLeadership;
                    takeOver.OnPressed += _ => ClaimLeadershipRequested?.Invoke(org.Id);
                    statusBody.AddChild(takeOver);
                }
            }
            else
                statusBody.AddChild(new Label { Text = "Керівництво працює у звичайному режимі." });
            _managementBody.AddChild(status);


        }
        if (org.Kind == OrganizationKind.Company)
        {
            var economyPanel = BuildFoldout("Автономна економіка", out var economyBody,
                _economyExpanded, expanded => _economyExpanded = expanded);
            var economyStatus = _state.EconomyRunning ? "Активна" : "Призупинена";
            AddEconomyRow(economyBody, "Стан", economyStatus,
                "Економіка працює за наявності активних гравців і доступного сховища",
                _state.EconomyRunning ? Color.FromHex("#9CCBA3") : Color.FromHex("#D8BC8B"));
            AddEconomyRow(economyBody, "Наступний цикл",
                _state.EconomyRunning ? $"{_state.NextEconomySeconds / 60} хв {_state.NextEconomySeconds % 60} с" : "-",
                "Час до наступного розрахунку автономної економіки");
            AddEconomyRow(economyBody, "Виконано циклів", $"{_state.CompletedEconomyCycles:N0}",
                "Загальна кількість завершених економічних розрахунків");
            AddEconomyRow(economyBody, "Активне вікно",
                $"{_state.ActiveDemandCycles} / {_state.MaxActiveDemandCycles}",
                "Кількість циклів у поточному вікні попиту");
            AddEconomyRow(economyBody, "Виручка за цикл", $"{_state.LastCycleGross:N0} $",
                "Гроші, отримані компанією за останній цикл");
            AddEconomyRow(economyBody, "Витрати за цикл", $"{_state.LastCycleCosts:N0} $",
                "Операційні витрати за останній цикл");
            AddEconomyRow(economyBody, "Результат", $"{_state.LastCycleNet:+# ##0;-# ##0;0} $",
                "Виручка мінус витрати за останній цикл",
                _state.LastCycleNet >= 0 ? Color.FromHex("#9CCBA3") : Color.FromHex("#E5A39C"));
            var eventDescription = _state.CompletedEconomyCycles == 0 ? "Ще немає даних" :
                _state.LastCycleOutcome switch
                {
                    1 => "Сприятливий",
                    2 => "Додаткові витрати",
                    _ => "Звичайний"
                };
            AddEconomyRow(economyBody, "Подія циклу", eventDescription,
                "Випадкова подія, яка змінила витрати останнього циклу");
            AddEconomyRow(economyBody, "Попит сектора", $"{_state.DailyDemandRemaining:N0} $",
                "Доступний попит у поточному ринковому вікні");
            AddEconomyRow(economyBody, "Ліміт виручки",
                $"{_state.DailyGrossRevenue:N0} / {_state.DailyCompanyLimit:N0} $",
                "Поточна виручка та дозволений ліміт компанії");
            AddEconomyRow(economyBody, "Загальна виручка", $"{_state.GrossRevenue:N0} $",
                "Виручка компанії за весь час");
            AddEconomyRow(economyBody, "Загальні витрати", $"{_state.OperatingCosts:N0} $",
                "Витрати компанії за весь час");
            _managementBody.AddChild(economyPanel);

            var progression = BuildFoldout("Прогрес компанії", out var levelBody,
                _developmentExpanded, expanded => _developmentExpanded = expanded);
            levelBody.AddChild(new Label
            {
                Text = "Стратегія компанії",
                FontColorOverride = Color.FromHex("#C6D8EA")
            });
            // Two rows avoid cramped labels in the left management column.
            var strategyRows = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
                SeparationOverride = 5
            };
            var strategyRow = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true,
                SeparationOverride = 6
            };
            strategyRows.AddChild(strategyRow);
            var strategyIndex = 0;
            foreach (var policy in Enum.GetValues<OrganizationOperatingPolicy>())
            {
                if (strategyIndex++ == 3)
                {
                    strategyRow = new BoxContainer
                    {
                        Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true,
                        SeparationOverride = 6
                    };
                    strategyRows.AddChild(strategyRow);
                }
                var selectedPolicy = policy;
                var policyTitle = policy switch
                {
                    OrganizationOperatingPolicy.Cautious => "Стабільність",
                    OrganizationOperatingPolicy.Expansion => "Експансія",
                    OrganizationOperatingPolicy.Optimization => "Оптимізація",
                    OrganizationOperatingPolicy.Innovation => "Інновації",
                    _ => "Баланс"
                };
                var policyHint = policy switch
                {
                    OrganizationOperatingPolicy.Cautious => "Менший ризик і витрати, менший попит",
                    OrganizationOperatingPolicy.Expansion => "Більше замовлень, значно вищі витрати і ризик",
                    OrganizationOperatingPolicy.Optimization => "Менша собівартість ціною нижчого попиту та дещо більшого ризику",
                    OrganizationOperatingPolicy.Innovation => "Прискорений досвід, особливо з дослідницькими відділами; дорожчі операції",
                    _ => "Помірний попит, витрати та ризик"
                };
                var selected = _state.OperatingPolicy == policy;
                var strategy = new Button
                {
                    Text = (selected ? "✓ " : "") + policyTitle,
                    HorizontalExpand = true, MinHeight = 30,
                    Disabled = selected || !HasRight(OrganizationPermission.Strategy),
                    ToolTip = policyHint + " · Зміна раз на 10 хвилин",
                    Modulate = selected ? Color.FromHex("#9CCBA3") : Color.White
                };
                strategy.OnPressed += _ => SetOperatingPolicyRequested?.Invoke(org.Id, selectedPolicy);
                strategyRow.AddChild(strategy);
            }
            levelBody.AddChild(strategyRows);
            var currentPolicyHint = _state.OperatingPolicy switch
            {
                OrganizationOperatingPolicy.Cautious => "Знижений ризик і попит",
                OrganizationOperatingPolicy.Expansion => "Зростання попиту з підвищеним ризиком",
                OrganizationOperatingPolicy.Optimization => "Пріоритет економії операційних витрат",
                OrganizationOperatingPolicy.Innovation => "Пріоритет галузевого досвіду",
                _ => "Збалансована робота"
            };
            levelBody.AddChild(new Label { Text = currentPolicyHint,
                FontColorOverride = Color.FromHex("#98AAB8") });
            levelBody.AddChild(new Label { Text = $"Галузева майстерність: {_state.CompanyLevel} / {OrganizationDevelopmentRules.MaxMastery}" });
            levelBody.AddChild(new ProgressBar
            {
                HorizontalExpand = true,
                MinValue = 0,
                MaxValue = Math.Max(1, _state.NextLevelExperience),
                Value = _state.CompanyLevel >= 100 ? Math.Max(1, _state.NextLevelExperience) :
                    Math.Min(_state.CompanyExperience, Math.Max(1, _state.NextLevelExperience))
            });
            levelBody.AddChild(new Label
            {
                Text = _state.CompanyLevel >= 100 ? "Максимальна майстерність" :
                    $"Досвід: {_state.CompanyExperience:N0} / {_state.NextLevelExperience:N0}",
                FontColorOverride = Color.FromHex("#AAB8C5")
            });
            var facilities = new[]
            {
                (OrganizationDevelopmentBranch.Logistics, "Логістичний центр", _state.LogisticsLevel,
                    _state.LogisticsTier2Level, "Охоплення замовлень і частка ринку"),
                (OrganizationDevelopmentBranch.Research, "Дослідницький відділ", _state.ResearchFacilityLevel,
                    _state.ResearchTier2Level, "Швидше набуття досвіду, нижча собівартість"),
                (OrganizationDevelopmentBranch.Infrastructure, "Інфраструктура", _state.InfrastructureLevel,
                    _state.InfrastructureTier2Level, "Продуктивність і обслуговування ринку"),
                (OrganizationDevelopmentBranch.ServiceNetwork, "Сервісна мережа", _state.ServiceNetworkLevel,
                    _state.ServiceNetworkTier2Level, "Постійні клієнти і більший попит"),
                (OrganizationDevelopmentBranch.RiskManagement, "Фінансовий контроль", _state.RiskManagementLevel,
                    _state.RiskManagementTier2Level, "Зменшення непередбачених операційних втрат")
            };
            var fullBranches = facilities.Count(facility =>
                facility.Item3 >= OrganizationDevelopmentRules.MaxLevel);
            var advancedAvailable = OrganizationDevelopmentRules.AdvancedUnlocked(
                _state.CompanyLevel, fullBranches);
            if (!advancedAvailable && _developmentPage == 2)
                _developmentPage = 1;
            levelBody.AddChild(new Label
            {
                Text = _developmentPage == 1 ? "I рівень · Базові відділи" :
                    "II рівень · Розширені відділи",
                FontColorOverride = Color.FromHex("#C6D8EA")
            });
            if (!advancedAvailable)
                levelBody.AddChild(new Label
                {
                    Text = $"II рівень: завершені відділи {fullBranches}/" +
                        $"{OrganizationDevelopmentRules.CompletedBranchesForAdvanced} · " +
                        $"майстерність {_state.CompanyLevel}/{OrganizationDevelopmentRules.AdvancedMasteryUnlock}",
                    FontColorOverride = Color.FromHex("#9AAAB8")
                });
            var sectorBp = (int)Math.Round(org.InfluencePercent * 100.0);
            foreach (var (branch, headingText, firstLevel, secondLevel, description) in facilities)
            {
                var tier = _developmentPage;
                var facilityLevel = tier == 1 ? firstLevel : secondLevel;
                var price = OrganizationDevelopmentRules.Price(branch, facilityLevel, tier);
                var masteryRequired = OrganizationDevelopmentRules.MasteryRequired(facilityLevel, tier);
                var sectorRequired = OrganizationDevelopmentRules.SectorRequiredBp(facilityLevel, tier);
                var unlocked = tier == 1 || (advancedAvailable && firstLevel >= OrganizationDevelopmentRules.MaxLevel);
                var maxed = facilityLevel >= OrganizationDevelopmentRules.MaxLevel;
                var eligible = unlocked && _state.CompanyLevel >= masteryRequired &&
                    sectorBp >= sectorRequired && _state.Treasury >= price;
                var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                    SeparationOverride = 9, HorizontalExpand = true };
                var plus = new Button
                {
                    Text = maxed ? "✓" : "+", MinWidth = 38, MinHeight = 36,
                    Disabled = maxed || !eligible || !HasRight(OrganizationPermission.Investments) ||
                        _state.InsolvencyState != OrganizationInsolvencyState.Active,
                    ToolTip = maxed ? "Відділ повністю розвинено" :
                        $"Потрібно: {price:N0} $, майстерність {masteryRequired}, вплив {sectorRequired / 100.0:0.00}%" +
                        (tier == 2 && firstLevel < OrganizationDevelopmentRules.MaxLevel ? ", завершити I рівень" : "")
                };
                var chosenBranch = branch;
                var chosenTier = tier;
                plus.OnPressed += _ => DevelopmentRequested?.Invoke(org.Id, chosenBranch, chosenTier);
                row.AddChild(plus);
                var details = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
                    SeparationOverride = 2 };
                var heading = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                    HorizontalExpand = true, SeparationOverride = 6 };
                heading.AddChild(new Label { Text = headingText, HorizontalExpand = true });
                var squares = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 3 };
                for (var stage = 0; stage < OrganizationDevelopmentRules.MaxLevel; stage++)
                    squares.AddChild(new PanelContainer
                    {
                        MinWidth = 12, MinHeight = 12,
                        PanelOverride = new StyleBoxFlat
                        {
                            BackgroundColor = Color.FromHex(stage < facilityLevel ? "#8CCBA5" : "#343C45")
                        }
                    });
                heading.AddChild(squares);
                details.AddChild(heading);
                details.AddChild(new Label
                {
                    Text = maxed ? "Завершено · 5/5" :
                        $"{facilityLevel}/5 · {price:N0} $ · майстерність {masteryRequired} · вплив {sectorRequired / 100.0:0.00}%"
                });
                details.AddChild(new Label
                {
                    Text = description + " · " + DevelopmentBenefit(branch, firstLevel + secondLevel),
                    FontColorOverride = Color.FromHex("#9AAAB8")
                });
                row.AddChild(details);
                levelBody.AddChild(row);
            }
            var navigation = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true, SeparationOverride = 6 };
            navigation.AddChild(new Control { HorizontalExpand = true });
            if (_developmentPage == 2)
            {
                var previous = new Button { Text = "← I рівень", MinHeight = 32 };
                previous.OnPressed += _ => { _developmentPage = 1; RefreshManagement(); };
                navigation.AddChild(previous);
                navigation.AddChild(new Button { Text = "II рівень", MinHeight = 32, Disabled = true });
            }
            if (_developmentPage == 1)
            {
                var next = new Button { Text = "II рівень →", MinHeight = 32,
                    Disabled = !advancedAvailable,
                    ToolTip = $"Необхідно: {OrganizationDevelopmentRules.CompletedBranchesForAdvanced} відділи 5/5 " +
                        $"та майстерність {OrganizationDevelopmentRules.AdvancedMasteryUnlock}" };
                next.OnPressed += _ => { _developmentPage = 2; RefreshManagement(); };
                navigation.AddChild(next);
            }
            levelBody.AddChild(navigation);
            _managementBody.AddChild(progression);
        }

        if (_state.Role != OrganizationMemberRole.Owner)
        {
            var leave = new Button { Text = _leaveConfirmation ? "Підтвердити вихід" : "Залишити організацію",
                Modulate = Color.FromHex(_leaveConfirmation ? "#FF7474" : "#DC9999"),
                ToolTip = "Після виходу можливі штрафи та обмеження повторного вступу." };
            leave.OnPressed += _ =>
            {
                if (!_leaveConfirmation)
                {
                    _leaveConfirmation = true;
                    RefreshManagement();
                    return;
                }
                _leaveConfirmation = false;
                LeaveRequested?.Invoke();
            };
            _managementBody.AddChild(leave);
        }

        var treasuryPanel = BuildTitledPanel("Скарбниця", out var treasuryBody);
        treasuryBody.AddChild(new Label
        {
            Text = $"На балансі: {_state.Treasury:N0} $: ваш рахунок: {_state.PersonalBalance:N0} $"
        });
        var transferAmount = new LineEdit
        {
            Text = "500", PlaceHolder = "Сума в $едитах", MinWidth = 180, MinHeight = 34,
            HorizontalExpand = true
        };
        treasuryBody.AddChild(new Label { Text = "Сума переказу" });
        treasuryBody.AddChild(transferAmount);
        var transferActions = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 6 };
        var deposit = new Button { Text = "Внести", HorizontalExpand = true, MinHeight = 32 };
        var withdraw = new Button { Text = "Вивести", HorizontalExpand = true, MinHeight = 32 };
        withdraw.Disabled = !HasRight(OrganizationPermission.TreasuryWithdraw);
        deposit.OnPressed += _ => TransferRequested?.Invoke(org.Id, ParseAmount(transferAmount), true);
        withdraw.OnPressed += _ => TransferRequested?.Invoke(org.Id, ParseAmount(transferAmount), false);
        transferActions.AddChild(deposit);
        transferActions.AddChild(withdraw);
        treasuryBody.AddChild(transferActions);
        treasuryBody.AddChild(new Label { Text = "Виведення доступне тільки уповноваженим. Пауза 10 секунд." });
        _financeBody.AddChild(treasuryPanel);
        AddFinanceSeparator(_financeBody);

        // Real server-managed payroll, separate from UI-only salary estimates.
        var payroll = BuildTitledPanel("Зарплати працівникам", out var payrollBody);
        AddMetric(payrollBody, _state.PayrollAutomatic ? "Автоматичний фонд" : "На одного працівника за цикл",
            _state.PayrollAutomatic ? $"{_state.PayrollProfitPercent}% прибутку" : $"{_state.PayrollPerMember:N0} $");
        if (org.Kind == OrganizationKind.Company)
            AddMetric(payrollBody, "Частка власника з автоматичного фонду",
                $"{Math.Clamp(18 + 2 * (_state.CompanyLevel - 1), 18, 32)}% фонду");
        AddMetric(payrollBody, "Усього нараховано", $"{_state.PayrollAccrued:N0} $");
        AddMetric(payrollBody, "Очікує на вашому рахунку", $"{_state.MyPayrollAvailable:N0} $");
        AddMetric(payrollBody, "Вам виплачено", $"{_state.MyPayrollPaid:N0} $");
        if (HasRight(OrganizationPermission.Budget))
        {
            var salaryValue = new LineEdit
            {
                Text = _state.PayrollPerMember.ToString(),
                PlaceHolder = "Зарплата (0–2000)", MinHeight = 32
            };
            var mode = new OptionButton { MinWidth = 230, MinHeight = 32 };
            mode.AddItem("Фіксована зарплата", 0);
            if (org.Kind == OrganizationKind.Company)
                mode.AddItem("Автоматично з прибутку", 1);
            mode.SelectId(_state.PayrollAutomatic && org.Kind == OrganizationKind.Company ? 1 : 0);
            var autoRate = new OptionButton { MinWidth = 165, MinHeight = 32 };
            for (var percent = 5; percent <= 60; percent += 5)
                autoRate.AddItem($"{percent}% прибутку", percent);
            autoRate.SelectId(_state.PayrollProfitPercent);
            salaryValue.Visible = !_state.PayrollAutomatic;
            autoRate.Visible = _state.PayrollAutomatic;
            mode.OnItemSelected += args =>
            {
                mode.SelectId(args.Id);
                salaryValue.Visible = args.Id == 0;
                autoRate.Visible = args.Id == 1;
            };
            var salarySave = new Button { Text = "Зберегти", MinHeight = 32 };
            salarySave.OnPressed += _ => PayrollPolicyRequested?.Invoke(org.Id,
                ParseAmount(salaryValue), mode.SelectedId == 1, autoRate.SelectedId);
            payrollBody.AddChild(mode);
            payrollBody.AddChild(salaryValue);
            payrollBody.AddChild(autoRate);
            payrollBody.AddChild(salarySave);
        }
        _financeBody.AddChild(payroll);
        AddFinanceSeparator(_financeBody);

        if (HasRight(OrganizationPermission.Investments) && org.Kind == OrganizationKind.Company)
        {
            var investments = BuildTitledPanel("Інновації та інвестиційні програми", out var investmentsBody);
            investmentsBody.AddChild(new Label { Text = $"Сектор: {org.InfluencePercent:0.00}%: вкладено {org.CapitalSpent:N0} $" });
            if (org.Kind == OrganizationKind.Company)
                investmentsBody.AddChild(new Label { Text = $"{IndustryLabel(org.Industry)}: {org.IndustryPercent:0.00}%" });

            var scope = new OptionButton();
            scope.AddItem("Секторний вплив", (int) OrganizationMarketScope.Sector);
            if (org.Kind == OrganizationKind.Company)
                scope.AddItem($"Галузевий ринок: {IndustryLabel(org.Industry)}", (int) OrganizationMarketScope.Industry);
            scope.SelectId(0);
            scope.OnItemSelected += args => scope.SelectId(args.Id);
            investmentsBody.AddChild(new Label { Text = "Ринок" });
            if (org.Kind == OrganizationKind.Company && org.IndustryPercent < 0.25f)
                investmentsBody.AddChild(new Label { Text = WrapText("Для секторних інвестицій спершу досягніть 0,25% впливу у своїй галузі.") });
            investmentsBody.AddChild(scope);

            var focus = new OptionButton();
            var focusLabels = new[] { "Маркетингові дослідження", "Виробничі процеси", "Системи безпеки", "Технологічні розробки" };
            for (var i = 0; i < focusLabels.Length; i++)
                focus.AddItem(focusLabels[i], i);
            focus.SelectId(0);
            focus.OnItemSelected += args => focus.SelectId(args.Id);
            investmentsBody.AddChild(new Label { Text = "Програма, яку фінансуємо" });
            investmentsBody.AddChild(new Label
            {
                Text = "Кошти спрямовуються на обраний напрям розвитку ринку. Для постійних покращень відділів відкрийте «Огляд → Прогрес компанії».",
                FontColorOverride = Color.FromHex("#98ADBE")
            });
            investmentsBody.AddChild(focus);

            var money = new LineEdit { Text = "5000", PlaceHolder = "Бюджет інвестиції, $" };
            investmentsBody.AddChild(money);
            var invest = new Button { Text = "Інвестувати зі скарбниці", MinHeight = 34 };
            invest.OnPressed += _ =>
                InvestRequested?.Invoke(org.Id, (OrganizationMarketScope)scope.SelectedId,
                    (OrganizationInvestmentFocus)focus.SelectedId, ParseAmount(money), null);
            investmentsBody.AddChild(invest);
            _financeBody.AddChild(investments);
        }
        else if (org.Kind == OrganizationKind.Faction)
        {
            var programs = BuildTitledPanel("Фінансування програм фракції", out var programsBody);
            programsBody.AddChild(new Label { Text = WrapText(
                "Оберіть програму, що відповідає спрямуванням фракції, у розділі «Моя організація → Огляд → Розвиток фракції». Кожне покращення вимагає авторитету, виконаних завдань та оплати зі скарбниці.", 46) });
            _financeBody.AddChild(programs);
        }

    }

    private static void AddFinanceSeparator(BoxContainer body)
    {
        body.AddChild(new PanelContainer
        {
            HorizontalExpand = true,
            MinHeight = 1,
            Margin = new Thickness(4, 5, 4, 5),
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#697784") }
        });
    }

    private void RefreshPersonnel()
    {
        Clear(_personnelBody);
        if (_state == null || _state.Membership is not { } membership)
        {
            _personnelBody.AddChild(new Label { Text = "Ви не належите до організації." });
            return;
        }
        var org = _state.Organizations.FirstOrDefault(o => o.Id == membership);
        if (org == null)
            return;

        if (_state.TdfOnDuty)
        {
            _personnelBody.AddChild(new Label { Text = "Штат TDF",
                FontColorOverride = Color.FromHex("#D6AD67") });
            AddMetric(_personnelBody, "Заступали на службу", _state.TdfPersonnelInRoster.ToString());
            AddMetric(_personnelBody, "Перебувають у секторі", _state.TdfPersonnelInSector.ToString());
            AddMetric(_personnelBody, "Зараз під керуванням гравців", _state.TdfPersonnelConnected.ToString());
            var tdfRosterPanel = BuildTitledPanel("Особовий склад", out var tdfRoster);
            foreach (var member in _state.Members)
            {
                var name = member.IsPresent ? $"● {member.Name}" : $"○ {member.Name}";
                AddMetric(tdfRoster, name, member.Title,
                    member.IsPresent ? Color.FromHex("#80C39A") : Color.FromHex("#A5AAB5"));
            }
            _personnelBody.AddChild(tdfRosterPanel);
            return;
        }
        _personnelBody.AddChild(new Label { Text = org.Name, FontColorOverride = Color.FromHex(org.Color) });
        _personnelBody.AddChild(new Label { Text = $"Учасників: {org.Members}: у секторі зараз: {_state.OnlineMembers}" });
        var rosterPanel = BuildTitledPanel("Штатний розпис", out var roster);
        // One compact roster card per employee. The editor buttons expose actions on demand.
        foreach (var member in _state.Members)
        {
            var personnelCard = new PanelContainer
            {
                HorizontalExpand = true,
                PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#242A30") }
            };
            var cardBody = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true,
                Margin = new Thickness(7),
                SeparationOverride = 4
            };
            personnelCard.AddChild(cardBody);
            var header = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 6
            };
            header.AddChild(new Label
            {
                Text = member.IsPresent ? "●" : "○",
                ToolTip = member.IsPresent ? "У секторі" : "Не в секторі",
                FontColorOverride = member.IsPresent ? Color.FromHex("#79BD85") : Color.FromHex("#9399A7")
            });
            var identity = new BoxContainer { Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true, SeparationOverride = 1 };
            identity.AddChild(new Label { Text = WrapText(member.Name, 30), HorizontalExpand = true });
            identity.AddChild(new Label
            {
                Text = WrapText(member.Role == OrganizationMemberRole.Owner ? _state.OwnerTitle : member.Title, 32),
                FontColorOverride = Color.FromHex("#A9B8C5")
            });
            header.AddChild(identity);
            if (member.Distinction > 0)
                header.AddChild(new Label
                {
                    Text = new string('★', member.Distinction),
                    ToolTip = $"Відзнака: +{member.Distinction * 5}% до ваги зарплати",
                    FontColorOverride = Color.FromHex("#E6C276")
                });

            var canManage = _state.Role == OrganizationMemberRole.Owner;
            var isOwner = member.Role == OrganizationMemberRole.Owner;
            var memberId = member.Id;
            var roleDetails = new BoxContainer { Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true, SeparationOverride = 5 };
            var rightsDetails = new BoxContainer { Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true, SeparationOverride = 5 };
            var actionsDetails = new BoxContainer { Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true, SeparationOverride = 5 };

            if (canManage && !isOwner)
            {
                var selectRole = new OptionButton { HorizontalExpand = true, MinHeight = 32 };
                selectRole.AddItem(_state.MemberTitle, 0);
                for (var j = 0; j < _state.CustomRoles.Length; j++)
                    selectRole.AddItem(_state.CustomRoles[j].Title, j + 1);
                var chosen = Array.FindIndex(_state.CustomRoles, c => c.Id == member.CustomRoleId);
                selectRole.SelectId(chosen + 1);
                if (!member.IsDeputy)
                {
                    selectRole.OnItemSelected += args =>
                    {
                        selectRole.SelectId(args.Id);
                        var assigned = args.Id == 0 ? Guid.Empty : _state.CustomRoles[args.Id - 1].Id;
                        AssignCustomRoleRequested?.Invoke(org.Id, memberId, assigned);
                    };
                    roleDetails.AddChild(selectRole);
                }
                else
                    roleDetails.AddChild(new Label { Text = "Посада заступника змінюється в кадрових рішеннях." });
                var titleRow = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                    HorizontalExpand = true, SeparationOverride = 5 };
                var titleEdit = new LineEdit { Text = member.Title, HorizontalExpand = true,
                    PlaceHolder = "Індивідуальна назва посади" };
                var saveTitle = new Button { Text = "✓", MinWidth = 36,
                    ToolTip = "Зберегти особисту назву посади" };
                saveTitle.OnPressed += _ => SetMemberTitleRequested?.Invoke(org.Id, memberId, titleEdit.Text);
                titleRow.AddChild(titleEdit);
                titleRow.AddChild(saveTitle);
                roleDetails.AddChild(titleRow);

                var awards = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                    SeparationOverride = 5, HorizontalExpand = true };
                for (byte level = 0; level <= 3; level++)
                {
                    var selected = level;
                    var award = new Button
                    {
                        Text = level == 0 ? "—" : new string('★', level),
                        ToolTip = level == 0 ? "Прибрати відзнаку" : $"Відзнака {level}: +{level * 5}% до ваги зарплати",
                        MinHeight = 28
                    };
                    award.Modulate = member.Distinction == level ? Color.FromHex("#E6C276") : Color.White;
                    award.OnPressed += _ => SetDistinctionRequested?.Invoke(org.Id, memberId, selected);
                    awards.AddChild(award);
                }
                roleDetails.AddChild(awards);

                var deputyButton = new Button
                {
                    Text = member.IsDeputy ? "Зняти заступника" : "Призначити заступника",
                    MinHeight = 30
                };
                deputyButton.OnPressed += _ => SetDeputyRequested?.Invoke(org.Id,
                    member.IsDeputy ? Guid.Empty : memberId);
                actionsDetails.AddChild(deputyButton);
                var transfer = new Button { Text = "Передати керівництво", MinHeight = 30 };
                var confirmed = false;
                transfer.OnPressed += _ =>
                {
                    if (!confirmed)
                    {
                        confirmed = true;
                        transfer.Text = "Підтвердити передачу власності";
                        transfer.Modulate = Color.FromHex("#E4A2A2");
                    }
                    else
                        TransferOwnershipRequested?.Invoke(org.Id, memberId);
                };
                actionsDetails.AddChild(transfer);
            }

            if (!isOwner && HasRight(OrganizationPermission.Personnel) &&
                (_state.Role == OrganizationMemberRole.Owner ||
                 (_state.Role is { } callerRole && member.Role < callerRole)))
            {
                var remove = new Button { Text = "Звільнити", MinHeight = 30 };
                var removalConfirmed = false;
                remove.OnPressed += _ =>
                {
                    if (!removalConfirmed)
                    {
                        removalConfirmed = true;
                        remove.Text = "Підтвердити звільнення";
                    }
                    else
                        RemoveMemberRequested?.Invoke(org.Id, memberId);
                };
                actionsDetails.AddChild(remove);
            }

            // Rights belong to the ROLE, not to the individual person.
            if (!isOwner)
            {
                var custom = _state.CustomRoles.FirstOrDefault(r => r.Id == member.CustomRoleId);
                if (custom != null)
                    AddRoleEditor(rightsDetails, org.Id, OrganizationPermissionTier.Member,
                        custom.Id, custom.Title, custom.Permissions, canManage, expanded: true);
                else
                {
                    var tier = member.IsDeputy ? OrganizationPermissionTier.Deputy :
                        member.Role == OrganizationMemberRole.Officer ? OrganizationPermissionTier.Officer :
                        OrganizationPermissionTier.Member;
                    var rights = tier == OrganizationPermissionTier.Deputy ? _state.DeputyPermissions :
                        tier == OrganizationPermissionTier.Officer ? _state.OfficerPermissions : _state.MemberPermissions;
                    var title = tier == OrganizationPermissionTier.Deputy ? _state.DeputyTitle :
                        tier == OrganizationPermissionTier.Officer ? _state.OfficerTitle : _state.MemberTitle;
                    AddRoleEditor(rightsDetails, org.Id, tier, Guid.Empty, title, rights, canManage, expanded: true);
                }
            }

            var selectedPanel = _personnelOpenPanels.TryGetValue(memberId, out var opened) ? opened : 0;
            void Toggle(int panel)
            {
                selectedPanel = selectedPanel == panel ? 0 : panel;
                _personnelOpenPanels[memberId] = selectedPanel;
                roleDetails.Visible = selectedPanel == 1;
                rightsDetails.Visible = selectedPanel == 2;
                actionsDetails.Visible = selectedPanel == 3;
            }
            roleDetails.Visible = selectedPanel == 1;
            rightsDetails.Visible = selectedPanel == 2;
            actionsDetails.Visible = selectedPanel == 3;
            if (canManage && !isOwner)
            {
                var roleButton = new Button { Text = "✎", MinWidth = 32, MinHeight = 30,
                    ToolTip = "Посада, назва й відзнаки" };
                roleButton.OnPressed += _ => Toggle(1);
                header.AddChild(roleButton);
            }
            if (!isOwner)
            {
                var rightsButton = new Button { Text = "⚙", MinWidth = 32, MinHeight = 30,
                    ToolTip = "Доступи посади" };
                rightsButton.OnPressed += _ => Toggle(2);
                header.AddChild(rightsButton);
            }
            if (actionsDetails.ChildCount > 0)
            {
                var actionsButton = new Button { Text = "⋯", MinWidth = 32, MinHeight = 30,
                    ToolTip = "Кадрові дії" };
                actionsButton.OnPressed += _ => Toggle(3);
                header.AddChild(actionsButton);
            }
            cardBody.AddChild(header);
            if (roleDetails.ChildCount > 0) cardBody.AddChild(roleDetails);
            if (rightsDetails.ChildCount > 0) cardBody.AddChild(rightsDetails);
            if (actionsDetails.ChildCount > 0) cardBody.AddChild(actionsDetails);
            roster.AddChild(personnelCard);
        }
        if (_state.Members.Length == 0)
            roster.AddChild(new Label { Text = "Особовий склад порожній." });
        _personnelBody.AddChild(rosterPanel);

        if (_state.Role == OrganizationMemberRole.Owner)
        {
            var titlesPanel = BuildFoldout("Назви базових посад", out var titles);
            var slots = new[]
            {
                (OrganizationTitleSlot.Owner, _state.OwnerTitle),
                (OrganizationTitleSlot.Deputy, _state.DeputyTitle),
                (OrganizationTitleSlot.Member, _state.MemberTitle)
            };
            foreach (var (slot, current) in slots)
            {
                var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 5 };
                var edit = new LineEdit { Text = current, HorizontalExpand = true };
                row.AddChild(new Label { Text = TitleSlotLabel(slot), MinWidth = 100 });
                row.AddChild(edit);
                var save = new Button { Text = "Зберегти" };
                save.OnPressed += _ => SetTitleRequested?.Invoke(org.Id, slot, edit.Text);
                row.AddChild(save);
                titles.AddChild(row);
            }
            titles.AddChild(new Label { Text = "" });
            _personnelBody.AddChild(titlesPanel);
        }

        // The full role directory is useful, but should not fill the roster by default.
        var roleDirectory = BuildFoldout("Посади та доступи", out var directoryBody);
        // RefreshPersonnel recreates this foldout on every network state update.
        // Reparent the persistent rights container before attaching it again.
        // Otherwise Robust UI throws "This component is still parented" and
        // aborts the entire console refresh (including fleet/outpost controls).
        if (_personnelRightsBody.Parent is { } previousRightsParent)
            previousRightsParent.RemoveChild(_personnelRightsBody);
        directoryBody.AddChild(_personnelRightsBody);
        _personnelBody.AddChild(roleDirectory);
        RefreshPermissions();

        if (!HasRight(OrganizationPermission.Applications))
            return;
        var applicationsPanel = BuildTitledPanel("Кандидати на вступ", out var apps);
        foreach (var application in _state.Applications)
        {
            var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 5 };
            row.AddChild(new Label { Text = $"{application.Name}: внесок {application.Fee}", HorizontalExpand = true });
            var id = application.Id;
            var accept = new Button { Text = "Прийняти" };
            var decline = new Button { Text = "Відмовити" };
            accept.OnPressed += _ => ReviewRequested?.Invoke(id, true);
            decline.OnPressed += _ => ReviewRequested?.Invoke(id, false);
            row.AddChild(accept);
            row.AddChild(decline);
            apps.AddChild(row);
        }
        if (_state.Applications.Length == 0)
            apps.AddChild(new Label { Text = "Немає заявок на вступ." });
        _personnelBody.AddChild(applicationsPanel);
    }

    private bool HasRight(OrganizationPermission permission) =>
        _state != null && (_state.UserPermissions & permission) == permission;

    private void RefreshPermissions()
    {
        Clear(_permissionsBody);
        Clear(_personnelRightsBody);
        if (_state?.Membership == null)
            return;
        if (_state.TdfOnDuty)
        {
            _permissionsBody.AddChild(new Label { Text = WrapText(
                "Звання визначається посадою та не підлягає перейменуванню. Керівник TDF може делегувати окремі службові повноваження без підвищення звання.") });
            if (!_state.TdfTerminal)
            {
                _permissionsBody.AddChild(new Label { Text = "Кадрові дозволи відкриваються на терміналі TDF." });
                return;
            }
            foreach (var member in _state.Members)
            {
                var tdfRightsPanel = BuildFoldout(member.Name + ": " + member.Title, out var rights);
                rights.AddChild(new Label { Text = "Додаткові дозволи: " + member.TdfRights });
                if (_state.TdfCanAssignRights && member.Title != "Головний енфорсер")
                {
                    foreach (var setting in new[]
                    {
                        (OrganizationTdfServiceRight.Audit, "Ринковий нагляд"),
                        (OrganizationTdfServiceRight.FleetBudget, "Фінансування флоту"),
                        (OrganizationTdfServiceRight.Personnel, "Кадровий облік")
                    })
                    {
                        var right = setting.Item1;
                        var active = (member.TdfRights & right) != 0;
                        var button = new Button { Text = (active ? "✓ " : "+ ") + setting.Item2, MinHeight = 30 };
                        button.OnPressed += _ => TdfRightsRequested?.Invoke(member.Id,
                            active ? member.TdfRights & ~right : member.TdfRights | right);
                        rights.AddChild(button);
                    }
                }
                _permissionsBody.AddChild(tdfRightsPanel);
            }
            return;
        }

        RefreshAssets(_state.Membership.Value);
        Clear(_personnelRightsBody);
        var panel = BuildTitledPanel("Посади та доступи", out var roles);
        var canEdit = _state.Role == OrganizationMemberRole.Owner;
        foreach (var (slot, title, rights) in new[]
        {
            (OrganizationPermissionTier.Owner, _state.OwnerTitle, OrganizationPermission.All),
            (OrganizationPermissionTier.Deputy, _state.DeputyTitle, _state.DeputyPermissions),
            (OrganizationPermissionTier.Member, _state.MemberTitle, _state.MemberPermissions)
        })
            AddRoleEditor(roles, _state.Membership.Value, slot, Guid.Empty, title, rights, canEdit);
        foreach (var custom in _state.CustomRoles)
            AddRoleEditor(roles, _state.Membership.Value, OrganizationPermissionTier.Member,
                custom.Id, custom.Title, custom.Permissions, canEdit);
        if (canEdit && _state.CustomRoles.Length < 12)
            AddRoleEditor(roles, _state.Membership.Value, OrganizationPermissionTier.Member,
                Guid.Empty, "Нова посада", OrganizationPermission.None, true, true);
        _personnelRightsBody.AddChild(panel);
    }

    private void RefreshAssets(Guid membership)
    {
        if (_state == null)
            return;
        var owner = _state.Ownership == membership && !_state.ViewingAnotherCharacter;
        var fleetPanel = BuildFoldout("Шатл для оренди", out var fleetBody,
            _assetsFleetExpanded, expanded => _assetsFleetExpanded = expanded);
        fleetBody.AddChild(new Label { Text = WrapText(
            "Оренда корпоративного транспорту: завантажте підписане креслення, задайте назву, короткий опис і вартість. Перед публікацією перевірте великий грід на верфі.", 45),
            FontColorOverride = Color.FromHex("#9EB6C9") });
        var fleetStatus = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true, SeparationOverride = 5 };
        foreach (var step in new[]
        {
            ("01 · Креслення", _state.FleetBlueprintReady),
            ("02 · Оголошення", !string.IsNullOrWhiteSpace(_state.FleetListingName)),
            ("03 · Оренда", _state.FleetShuttleIssued)
        })
            fleetStatus.AddChild(new PanelContainer { HorizontalExpand = true,
                PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(
                    step.Item2 ? "#315346" : "#2E343D"),
                    BorderColor = Color.FromHex(step.Item2 ? "#75AF90" : "#4D5967"),
                    BorderThickness = new Thickness(1) },
                Children = { new Label { Text = step.Item1, Margin = new Thickness(5, 6),
                    FontColorOverride = Color.FromHex(step.Item2 ? "#C5E5D5" : "#A8B5C5") } }
            });
        fleetBody.AddChild(fleetStatus);
        AddMetric(fleetBody, "Креслення", string.IsNullOrEmpty(_state.FleetBlueprintName)
            ? "Не зареєстроване" : _state.FleetBlueprintName);
        AddMetric(fleetBody, "Назва для оренди", string.IsNullOrEmpty(_state.FleetListingName)
            ? "Немає" : _state.FleetListingName);
        AddMetric(fleetBody, "Ціна на раунд", $"{_state.FleetRentalPrice:N0} $");
        AddMetric(fleetBody, "Стан", _state.FleetShuttleIssued
            ? _state.FleetShuttlePresent ? "Виданий" : "Закритий на цей раунд"
            : _state.FleetBlueprintReady ? "Можна взяти в оренду" : "Потрібне креслення");
        if (!string.IsNullOrEmpty(_state.FleetListingDescription))
            fleetBody.AddChild(new Label { Text = WrapText(_state.FleetListingDescription, 42),
                FontColorOverride = Color.FromHex("#BBCBDA") });
        if (owner)
        {
            if (!_state.FleetShuttleIssued)
            {
                var fleetFiles = GetOutpostBlueprintFiles?.Invoke() ?? [];
                if (fleetFiles.Length > 0)
                {
                    var selector = new OptionButton { HorizontalExpand = true, MinHeight = 33 };
                    for (var i = 0; i < fleetFiles.Length; i++)
                    {
                        var name = fleetFiles[i];
                        selector.AddItem(name[(name.LastIndexOf('/') + 1)..], i);
                    }
                    selector.SelectId(0);
                    fleetBody.AddChild(selector);
                    var register = new Button { Text = "Поставити креслення шатла на оренду", MinHeight = 33 };
                    register.OnPressed += _ =>
                    {
                        var selected = selector.SelectedId;
                        if (selected >= 0 && selected < fleetFiles.Length)
                            FleetBlueprintRequested?.Invoke(membership, fleetFiles[selected]);
                    };
                    fleetBody.AddChild(register);
                }
                var publicName = new LineEdit { Text = _state.FleetListingName,
                    PlaceHolder = "Назва шатла для орендарів", MinHeight = 33 };
                var description = new LineEdit { Text = _state.FleetListingDescription,
                    PlaceHolder = "Короткий опис, до 160 знаків", MinHeight = 33 };
                var price = new LineEdit { Text = _state.FleetRentalPrice.ToString(),
                    PlaceHolder = "Ціна на раунд, 1 000-20 000", MinHeight = 33 };
                fleetBody.AddChild(publicName);
                fleetBody.AddChild(description);
                fleetBody.AddChild(price);
                var save = new Button { Text = "Затвердити умови оренди", MinHeight = 34 };
                save.OnPressed += _ =>
                {
                    if (int.TryParse(price.Text.Trim(), out var amount) &&
                        amount is >= OrganizationFleetRules.MinimumRentalPrice
                            and <= OrganizationFleetRules.MaximumRentalPrice)
                        FleetListingRequested?.Invoke(membership, publicName.Text, description.Text, amount);
                };
                fleetBody.AddChild(save);
            }
            else
                fleetBody.AddChild(new Label { Text = "Змінити умови можна з наступного раунду." });
        }
        _permissionsBody.AddChild(fleetPanel);

        var branches = new PanelContainer { HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#222830") } };
        var branchBody = new BoxContainer { Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true, SeparationOverride = 5, Margin = new Thickness(8) };
        var locked = new Button { Text = "Філії", MinHeight = 34,
            HorizontalExpand = true,
            ToolTip = "Філії з'являться в наступних технічних оновленнях цієї організаційної консолі." };
        locked.OnPressed += _ =>
        {
            _assetsBranchesInfo = !_assetsBranchesInfo;
            RefreshAssetsInPlace(membership);
        };
        var lockedRow = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true, SeparationOverride = 6 };
        var resourceCache = IoCManager.Resolve<IResourceCache>();
        if (resourceCache.TryGetResource<TextureResource>(
            new ResPath("/Textures/Interface/Nano/lock.svg.192dpi.png"), out var lockTexture))
            lockedRow.AddChild(new TextureRect
            {
                Texture = lockTexture.Texture, MinWidth = 23, MinHeight = 23,
                Stretch = TextureRect.StretchMode.KeepAspectCentered,
                VerticalAlignment = VAlignment.Center,
                MouseFilter = MouseFilterMode.Ignore
            });
        lockedRow.AddChild(locked);
        branchBody.AddChild(lockedRow);
        if (_assetsBranchesInfo)
            branchBody.AddChild(new Label { Text = WrapText(
                "Функція філій стане доступною в наступних технічних оновленнях консолі, з якою ви зараз працюєте.", 46),
                FontColorOverride = Color.FromHex("#AFC0D0") });
        branches.AddChild(branchBody);
        _permissionsBody.AddChild(branches);

        _permissionsBody.AddChild(BuildOutpostOverviewPanel());
    }

    private void RefreshAssetsInPlace(Guid membership)
    {
        Clear(_permissionsBody);
        RefreshAssets(membership);
    }

    private void AddRoleEditor(BoxContainer container, Guid org, OrganizationPermissionTier tier,
        Guid customId, string title, OrganizationPermission current, bool canEdit, bool newRole = false, bool expanded = false)
    {
        var header = BuildFoldout(title, out var body, expanded: expanded);
        container.AddChild(header);
        var selected = current;
        var isFixed = customId == Guid.Empty && !newRole;
        var readOnly = !canEdit || (isFixed && tier == OrganizationPermissionTier.Owner);
        LineEdit? customName = null;
        if (!isFixed)
        {
            customName = new LineEdit { Text = newRole ? "" : title,
                PlaceHolder = "Назва посади (2–32)", HorizontalExpand = true };
            body.AddChild(customName);
        }
        var rights = new (OrganizationPermission Flag, string Name, string Description)[]
        {
            (OrganizationPermission.TreasuryWithdraw, "Виведення коштів", "Дозволяє переказувати гроші зі скарбниці"),
            (OrganizationPermission.Personnel, "Кадрові рішення", "Прийняття та звільнення працівників у межах повноважень"),
            (OrganizationPermission.Applications, "Розгляд заявок", "Схвалення або відхилення вступу"),
            (OrganizationPermission.Settings, "Налаштування", "Умови вступу, приватність та оформлення організації"),
            (OrganizationPermission.Budget, "Фінанси", "Розподіл бюджету та зарплатна політика"),
            (OrganizationPermission.Strategy, "Стратегія", "Зміна господарської стратегії"),
            (OrganizationPermission.Investments, "Інвестиції", "Розвиток відділів та ринкова експансія")
        };
        foreach (var (flag, name, description) in rights)
        {
            var granted = flag;
            var check = new CheckBox { Text = name, ToolTip = description,
                Pressed = (current & granted) != 0, Disabled = readOnly };
            check.OnToggled += args =>
            {
                if (args.Pressed) selected |= granted;
                else selected &= ~granted;
            };
            body.AddChild(check);
        }
        if (!readOnly)
        {
            var save = new Button { Text = newRole ? "Створити посаду" : "Зберегти", MinHeight = 32 };
            save.OnPressed += _ =>
            {
                if (isFixed)
                    SetPermissionsRequested?.Invoke(org, tier, selected);
                else
                    SaveCustomRoleRequested?.Invoke(org, customId, customName?.Text ?? "", selected);
            };
            body.AddChild(save);
        }
    }

    private void RefreshShares()
    {
        Clear(_sharesBody);
        if (_state?.Membership is not { } membership)
        {
            _sharesBody.AddChild(new Label { Text = "Вступіть до організації, щоб переглянути її корпоративні активи." });
            return;
        }
        var org = _state.Organizations.FirstOrDefault(o => o.Id == membership);
        if (_state.TdfOnDuty)
        {
            if (!_state.TdfAuthorized)
            {
                _sharesBody.AddChild(new Label { Text = "Ринковий нагляд відкривається лише на службовому терміналі TDF." });
                return;
            }
            var market = BuildFoldout("Котирування компаній", out var marketBody, true);
            foreach (var listing in _state.Stocks)
                AddMetric(marketBody, listing.Name, $"{listing.Quote:N0} $", Color.FromHex(listing.Color));
            _sharesBody.AddChild(market);
            var oversight = BuildFoldout("Журнал фінансового нагляду", out var logBody, true);
            foreach (var alert in _state.TdfAlerts)
            {
                var item = BuildFoldout((alert.Flagged ? "Потребує перевірки" : "Угода") + ": " + alert.Company,
                    out var detail);
                AddMetric(detail, "Час UTC", alert.Utc.ToString("yyyy-MM-dd HH:mm"));
                AddMetric(detail, "Учасник", alert.Actor);
                detail.AddChild(new Label { Text = WrapText(alert.Description) });
                logBody.AddChild(item);
            }
            if (_state.TdfAlerts.Length == 0)
                logBody.AddChild(new Label { Text = "Записів поки немає." });
            _sharesBody.AddChild(oversight);
            return;
        }
        if (org?.Kind != OrganizationKind.Company)
        {
            _sharesBody.AddChild(new Label { Text = "Акції доступні лише комерційним компаніям." });
            return;
        }
        var stock = _state.Stocks.FirstOrDefault(s => s.Id == membership);
        if (stock == null)
            return;
        var summary = BuildTitledPanel("Корпоративний капітал", out var summaryBody);
        AddMetric(summaryBody, "Публічний продаж", stock.Listed ? "Відкритий" : "Закритий: рішення власника");
        AddMetric(summaryBody, "Всього акцій", $"{stock.Issued:N0}");
        AddMetric(summaryBody, "Акції для продажу", $"{stock.Available:N0}");
        AddMetric(summaryBody, "Ваші акції", $"{stock.Owned:N0}");
        AddMetric(summaryBody, "Ціна акції", $"{stock.Quote:N0} $");
        summaryBody.AddChild(new OrganizationStockChart(stock.History));
        _sharesBody.AddChild(summary);
        if (_state.Role == OrganizationMemberRole.Owner && !stock.Listed)
        {
            var listing = BuildTitledPanel("Перше розміщення акцій", out var listingBody);
            listingBody.AddChild(new Label { Text = WrapText("Зараз усі акції приватні. Оберіть частину власного пакета для продажу на біржі.") });
            var amount = new LineEdit { Text = "20", MinHeight = 32, PlaceHolder = "Кількість для розміщення (1–100)" };
            listingBody.AddChild(amount);
            var publish = new Button { Text = "Відкрити торгівлю акціями", MinHeight = 34 };
            publish.OnPressed += _ => StockListingRequested?.Invoke(membership, ParseAmount(amount));
            listingBody.AddChild(publish);
            _sharesBody.AddChild(listing);
        }
        if (_state.Role == OrganizationMemberRole.Owner && stock.Listed)
        {
            var issuePanel = BuildTitledPanel("Додатковий випуск", out var issueBody);
            var amount = new LineEdit { Text = "20", MinHeight = 32, PlaceHolder = "Скільки нових акцій" };
            issueBody.AddChild(amount);
            var issue = new Button { Text = "Випустити додаткові акції" };
            var pendingIssue = 0;
            amount.OnTextChanged += _ =>
            {
                pendingIssue = 0;
                issue.Text = "Випустити додаткові акції";
                issue.Modulate = Color.White;
            };
            issue.OnPressed += _ =>
            {
                var requested = ParseAmount(amount);
                if (requested is < 1 or > 100) return;
                if (pendingIssue != requested)
                {
                    pendingIssue = requested;
                    issue.Text = $"Підтвердити випуск {requested} акцій";
                    issue.Modulate = Color.FromHex("#E67575");
                    return;
                }
                pendingIssue = 0;
                issue.Modulate = Color.White;
                issue.Text = "Випустити додаткові акції";
                StockOrderRequested?.Invoke(membership, OrganizationStockOrder.Issue, requested);
            };
            issueBody.AddChild(issue);
            _sharesBody.AddChild(issuePanel);
        }
        var dividendPanel = BuildTitledPanel("Дивіденди", out var dividendBody);
        AddMetric(dividendBody, "Частка прибутку акціонерам", $"{_state.DividendRatePercent}%");
        AddMetric(dividendBody, "Нараховано акціонерам", $"{_state.DividendsDistributed:N0} $");
        dividendBody.AddChild(new Label
        {
            Text = "Виплати автоматично надходять на банківський рахунок активного персонажа. " +
                "Нарахування офлайн-акціонерів зберігаються до їхнього повернення.",
            FontColorOverride = Color.FromHex("#9EB3C6")
        });
        // Personal payout eligibility is checked by the server, against the body's own account.
        var claimDividends = new Button
        {
            Text = "Перевірити виплату дивідендів",
            ToolTip = "Ручна виплата, якщо автоматична ще не відбулася.",
            MinHeight = 32,
            Disabled = _state.ViewingAnotherCharacter || _state.TdfOnDuty
        };
        claimDividends.OnPressed += _ => DividendClaimRequested?.Invoke(membership);
        dividendBody.AddChild(claimDividends);
        if (_state.Role == OrganizationMemberRole.Owner && !_state.ViewingAnotherCharacter)
        {
            var rate = new OptionButton { MinWidth = 145 };
            foreach (var percent in new[] { 0, 5, 10, 15, 20, 25, 30 })
                rate.AddItem($"{percent}%", percent);
            rate.SelectId(_state.DividendRatePercent);
            rate.OnItemSelected += args => rate.SelectId(args.Id);
            dividendBody.AddChild(rate);
            var saveRate = new Button { Text = "Зберегти частку", MinHeight = 32 };
            saveRate.OnPressed += _ => DividendPolicyRequested?.Invoke(membership, rate.SelectedId);
            dividendBody.AddChild(saveRate);
        }
        _financeBody.AddChild(dividendPanel);
        var shareholders = BuildFoldout("Акціонери компанії", out var holders, true);
        foreach (var holder in _state.Shareholders)
        {
            var percent = stock.Issued == 0 ? 0 : 100f * holder.Shares / stock.Issued;
            AddMetric(holders, holder.Name, $"{holder.Shares:N0}: {percent:0.0}%");
        }
        _sharesBody.AddChild(shareholders);
    }

    private void RefreshTdf()
    {
        Clear(_tdfBody);
        if (_state == null || !_state.TdfAuthorized)
        {
            _tdfBody.AddChild(new Label { Text = "Ринковий нагляд доступний тільки уповноваженим службовцям TDF на службовому терміналі." });
            return;
        }

        _tdfBody.AddChild(new Label
        {
            Text = WrapText("Позначка біржової системи не доводить провину. Тут фіксується розгляд фінансових сигналів. " +
                "Направлення справи не оголошує персонажа в розшук й не блокує його рахунки."),
            FontColorOverride = Color.FromHex("#B6C6CF")
        });
        var active = BuildFoldout("Справи ринкового нагляду", out var caseBody, true);
        _tdfBody.AddChild(active);
        if (_state.TdfCases.Length == 0)
            caseBody.AddChild(new Label { Text = "Справ поки немає." });

        foreach (var docket in _state.TdfCases)
        {
            var status = docket.Status switch
            {
                OrganizationTdfCaseStatus.Open => "Відкрита",
                OrganizationTdfCaseStatus.Cleared => "Закрита: порушення не підтверджено",
                _ => "Передана на розгляд"
            };
            var foldout = BuildFoldout(status + ": " + docket.Company, out var details);
            AddMetric(details, "Відкрито", docket.CreatedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC");
            AddMetric(details, "Учасник / джерело", docket.Subject);
            details.AddChild(new Label { Text = WrapText(docket.Reason, 54) });
            if (!string.IsNullOrWhiteSpace(docket.LastOfficer))
                AddMetric(details, "Останнє рішення ухвалив", docket.LastOfficer);
            if (!string.IsNullOrWhiteSpace(docket.DecisionNote))
                details.AddChild(new Label { Text = WrapText(docket.DecisionNote, 54) });
            if (docket.History.Length > 0)
            {
                var historyPanel = BuildFoldout("Історія рішень", out var historyBody);
                foreach (var historyLine in docket.History)
                    historyBody.AddChild(new Label { Text = WrapText(historyLine, 54) });
                details.AddChild(historyPanel);
            }

            if (_state.TdfCanResolveCases && docket.Status == OrganizationTdfCaseStatus.Open)
            {
                var note = new LineEdit { PlaceHolder = "Обґрунтування рішення (5–220 символів)", HorizontalExpand = true };
                details.AddChild(note);
                var actions = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 5 };
                var clear = new Button { Text = "Закрити без порушення", HorizontalExpand = true,
                    ToolTip = "Зняти підозру за підсумками перевірки" };
                var refer = new Button { Text = "Передати на розгляд", HorizontalExpand = true,
                    ToolTip = "Документально направити матеріали, не накладаючи покарання" };
                clear.OnPressed += _ => TdfResolveCaseRequested?.Invoke(docket.Id, OrganizationTdfCaseStatus.Cleared, note.Text.Trim());
                refer.OnPressed += _ => TdfResolveCaseRequested?.Invoke(docket.Id, OrganizationTdfCaseStatus.Referred, note.Text.Trim());
                actions.AddChild(clear);
                actions.AddChild(refer);
                details.AddChild(actions);
            }
            caseBody.AddChild(foldout);
        }

        var ledger = BuildFoldout("Сигнали й біржові угоди", out var ledgerBody, true);
        _tdfBody.AddChild(ledger);
        foreach (var alert in _state.TdfAlerts)
        {
            var entry = BuildFoldout((alert.Flagged ? "⚠ Сигнал" : "Угода") + ": " + alert.Company, out var body);
            AddMetric(body, "Дата UTC", alert.Utc.ToString("yyyy-MM-dd HH:mm"));
            AddMetric(body, "Учасник", alert.Actor);
            body.AddChild(new Label { Text = WrapText(alert.Description, 54) });
            if (alert.Flagged && !string.IsNullOrEmpty(alert.SourceKey))
            {
                var exists = _state.TdfCases.Any(c => c.Id != Guid.Empty &&
                    c.SourceKey == alert.SourceKey);
                // Duplicate case prevention is enforced by the server, including older evidence.
                if (!exists)
                {
                    var open = new Button { Text = "Відкрити перевірку", MinHeight = 30 };
                    open.OnPressed += _ => TdfOpenCaseRequested?.Invoke(alert.SourceKey);
                    body.AddChild(open);
                }
                else body.AddChild(new Label { Text = "Перевірку вже зареєстровано." });
            }
            ledgerBody.AddChild(entry);
        }
        if (_state.TdfAlerts.Length == 0)
            ledgerBody.AddChild(new Label { Text = "Операцій ще немає." });
    }

    private void RefreshSideBudget()
    {
        Clear(_budgetSide);
        if (_state?.Membership is not { } membership || _state.BudgetShares.Length != 4)
        {
            _budgetSide.AddChild(new Label { Text = "Розподіл з’явиться після вступу." });
            return;
        }
        var org = _state.Organizations.FirstOrDefault(o => o.Id == membership);
        if (org == null)
            return;
        if (_state.TdfOnDuty)
        {
            _budgetSide.AddChild(new Label { Text = "TDF: державний контроль, не торговельний бюджет." });
            _budgetSide.AddChild(new Label { Text = $"Контроль: {35 + _state.TdfOperationalPercent + _state.TdfLongTermPercent:0.00}%" });
            return;
        }
        var labels = org.Kind == OrganizationKind.Company
            ? new[] { "Маркетинг", "Операції", "Безпека", "Дослідження" }
            : new[] { "Набір", "Постачання", "Оборона", "Розвідка" };
        _budgetSide.AddChild(new Label { Text = "Розподіл коштів" });
        var chart = new OrganizationBudgetChart();
        chart.SetLabels(labels);
        chart.SetAllocation(_state.BudgetShares);
        chart.SetInvestment((int) Math.Min(_state.Treasury, int.MaxValue));
        _budgetSide.AddChild(chart);
        if (HasRight(OrganizationPermission.Budget))
        {
            var save = new Button { Text = "Застосувати розподіл", MinHeight = 33 };
            save.OnPressed += _ =>
            {
                var values = chart.Allocation;
                ConfigureBudgetRequested?.Invoke(org.Id, values[0], values[1], values[2], values[3]);
            };
            _budgetSide.AddChild(save);
        }
        _budgetSide.AddChild(new Label { Text = "Зміна бюджету платна. Пауза: 10 хв." });
    }

    private void RefreshCreation()
    {
        var hasMembership = _state?.Membership != null;
        if (_creationBody.ChildCount > 0 &&
            _renderedCreationKind == _creationKind &&
            _renderedCreationHasMembership == hasMembership)
            return;

        _renderedCreationKind = _creationKind;
        _renderedCreationHasMembership = hasMembership;
        _registerButton = null;
        _selectedIndustryIcon = null;
        _selectedIndustryText = null;
        DetachCreationFields();
        Clear(_creationBody);
        if (hasMembership)
        {
            _creationBody.AddChild(new Label
            {
                Text = "Ви вже належите до організації. Спершу залиште поточну.",
                FontColorOverride = Color.FromHex("#C7CEDE")
            });
            return;
        }

        if (_creationKind is not { } kind)
        {
            _creationBody.AddChild(new Label { Text = "Оберіть тип організації" });
            var choices = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                VerticalExpand = true,
                SeparationOverride = 12,
                Margin = new Thickness(10, 16, 10, 16)
            };
            choices.AddChild(BuildCreationChoice(OrganizationKind.Company, "СТВОРИТИ КОМПАНІЮ",
                "Ринок, послуги, розвиток"));
            choices.AddChild(BuildCreationChoice(OrganizationKind.Faction, "СТВОРИТИ ФРАКЦІЮ",
                "Мета, спільнота, вплив"));
            _creationBody.AddChild(choices);
            return;
        }

        var top = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 6
        };
        var back = new Button { Text = "← Назад", MinHeight = 30 };
        back.OnPressed += _ =>
        {
            _creationKind = null;
            _creationError.Text = string.Empty;
            RefreshCreation();
        };
        top.AddChild(back);
        top.AddChild(new Control { HorizontalExpand = true });
        top.AddChild(new Label
        {
            Text = kind == OrganizationKind.Company ? "НОВА КОМПАНІЯ" : "НОВА ФРАКЦІЯ",
            FontColorOverride = Color.FromHex("#B6D8B6")
        });
        _creationBody.AddChild(top);

        // Three independent surfaces: a wide left form, a tall upper-right budget
        // and a compact lower-right registration and privacy summary.
        var columns = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 10
        };
        _creationBody.AddChild(columns);

        var left = BuildTitledPanel("Основні відомості", out var inputs, true);
        var creationSurface = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#222830"),
            BorderColor = Color.FromHex("#48545F"),
            BorderThickness = new Thickness(1)
        };
        left.PanelOverride = creationSurface;
        left.MinWidth = 455;
        inputs.SeparationOverride = 6;

        // Banner goes first. Enlarge the preview frame, not the PNG file format limit.
        inputs.AddChild(new Label { Text = "Емблема організації" });
        var bannerFrame = new PanelContainer
        {
            MinWidth = 155,
            MinHeight = 150,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#1A222A"),
                BorderColor = Color.FromHex("#536575"),
                BorderThickness = new Thickness(1)
            }
        };
        var bannerRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 12
        };
        var bannerPreview = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            Margin = new Thickness(12),
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center
        };
        bannerPreview.AddChild(new OrganizationBannerControl(_draftBanner, 112, showDraftPlaceholder: true));
        bannerFrame.AddChild(bannerPreview);
        bannerRow.AddChild(bannerFrame);
        var bannerDetails = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalAlignment = VAlignment.Center,
            SeparationOverride = 6
        };
        var chooseBanner = new Button { Text = "Обрати PNG", MinHeight = 34 };
        chooseBanner.OnPressed += _ => BannerChooseRequested?.Invoke();
        bannerDetails.AddChild(chooseBanner);
        bannerDetails.AddChild(new Label
        {
            Text = "PNG до 32×32",
            ToolTip = "Збереження підтримує прозорість та повноколірні PNG.",
            FontColorOverride = Color.FromHex("#AFBBC7")
        });
        bannerDetails.AddChild(_bannerStatus);
        bannerRow.AddChild(bannerDetails);
        inputs.AddChild(bannerRow);

        inputs.AddChild(new Label { Text = "Назва" });
        _name.MinHeight = 34;
        _name.HorizontalExpand = true;
        inputs.AddChild(_name);

        if (kind == OrganizationKind.Company)
        {
            inputs.AddChild(new Label { Text = "Галузь діяльності" });
            _industry.Visible = true;
            _industry.HorizontalExpand = true;
            _industry.MinHeight = 32;
            inputs.AddChild(_industry);
        }
        else
        {
            inputs.AddChild(new Label { Text = "Спрямування фракції" });
            var selectionCount = new Label
            {
                Text = $"Обрано {_factionFoci.Count} із 3",
                FontColorOverride = Color.FromHex("#A9C3D7")
            };
            inputs.AddChild(selectionCount);
            var selectorScroll = new ScrollContainer
            {
                HScrollEnabled = false,
                HorizontalExpand = true,
                MinHeight = 185,
                MaxHeight = 185
            };
            var focusGrid = new GridContainer { Columns = 2, HorizontalExpand = true };
            foreach (var focus in Enum.GetValues<OrganizationFactionFocus>())
            {
                if (focus is OrganizationFactionFocus.None or OrganizationFactionFocus.Other)
                    continue;
                var focusValue = focus;
                var checkbox = new CheckBox
                {
                    Text = FactionFocusLabel(focusValue),
                    Pressed = _factionFoci.Contains(focusValue),
                    MinHeight = 26,
                    ToolTip = "Можна обрати не більше трьох спрямувань"
                };
                checkbox.OnToggled += args =>
                {
                    if (args.Pressed)
                    {
                        if (_factionFoci.Count >= 3)
                            checkbox.Pressed = false;
                        else if (!_factionFoci.Contains(focusValue))
                            _factionFoci.Add(focusValue);
                    }
                    else
                    {
                        _factionFoci.Remove(focusValue);
                    }
                    selectionCount.Text = $"Обрано {_factionFoci.Count} із 3";
                };
                focusGrid.AddChild(checkbox);
            }
            selectorScroll.AddChild(focusGrid);
            inputs.AddChild(selectorScroll);
        }

        inputs.AddChild(new Label
        {
            Text = kind == OrganizationKind.Company ? "Опис діяльності" : "Мета, принципи та переконання",
            FontColorOverride = Color.FromHex("#B8C8D6")
        });
        _description.MinHeight = 92;
        _description.HorizontalExpand = true;
        _description.Placeholder = new Rope.Leaf(kind == OrganizationKind.Company
            ? "Чим займається ваша компанія?"
            : "Опишіть ідеологію, релігію, мету або правила фракції");
        inputs.AddChild(_description);

        inputs.AddChild(new Label { Text = "Колір (#RRGGBB)" });
        var colorRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8
        };
        _color.MinWidth = 145;
        _color.MinHeight = 34;
        _color.HorizontalExpand = true;
        _colorPreview.MinWidth = 30;
        _colorPreview.MinHeight = 30;
        colorRow.AddChild(_color);
        colorRow.AddChild(_colorPreview);
        inputs.AddChild(colorRow);

        inputs.AddChild(new Label { Text = "Посилання на матеріали" });
        _discordLink.HorizontalExpand = true;
        inputs.AddChild(_discordLink);
        columns.AddChild(left);

        var rightStack = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 10
        };
        columns.AddChild(rightStack);

        var budgetPanel = BuildTitledPanel("Початковий бюджет", out var budget);
        budgetPanel.PanelOverride = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#222830"),
            BorderColor = Color.FromHex("#48545F"),
            BorderThickness = new Thickness(1)
        };
        budget.SeparationOverride = 4;
        _budgetChart.SetLabels(kind == OrganizationKind.Company
            ? ["Маркетинг", "Операції", "Безпека", "Дослідження"]
            : ["Набір", "Постачання", "Оборона", "Розвідка"]);
        budget.AddChild(_budgetChart);
        rightStack.AddChild(budgetPanel);

        // The bottom-right area provides actual creation controls, rather than a placeholder.
        var rulesPanel = BuildTitledPanel("Умови вступу та реєстрація", out var rules);
        rulesPanel.PanelOverride = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#222830"),
            BorderColor = Color.FromHex("#48545F"),
            BorderThickness = new Thickness(1)
        };
        rules.SeparationOverride = 5;
        rules.AddChild(_private);
        rules.AddChild(_rosterPublic);
        rules.AddChild(_entryFeeEnabled);
        _fee.MinHeight = 30;
        rules.AddChild(_fee);
        rules.AddChild(new Label { Text = "Початковий капітал" });
        _investment.MinHeight = 32;
        _investment.HorizontalExpand = true;
        rules.AddChild(_investment);
        rules.AddChild(_foundingTotal);
        RefreshFoundingPrice();
        _creationError.FontColorOverride = Color.FromHex("#E7A5A5");
        rules.AddChild(_creationError);
        var register = new Button
        {
            Text = kind == OrganizationKind.Company ? "Створити компанію" : "Створити фракцію",
            MinHeight = 38,
            Modulate = Color.FromHex("#92D5A2")
        };
        _registerButton = register;
        RefreshFoundingPrice();
        register.OnPressed += _ => SubmitCreate();
        rules.AddChild(register);
        rightStack.AddChild(rulesPanel);
    }

    private void DetachCreationFields()
    {
        if (_name.Parent is BoxContainer nameParent)
            nameParent.RemoveChild(_name);
        if (_description.Parent is BoxContainer descriptionParent)
            descriptionParent.RemoveChild(_description);
        if (_discordLink.Parent is BoxContainer discordParent)
            discordParent.RemoveChild(_discordLink);
        if (_bannerStatus.Parent is BoxContainer bannerParent)
            bannerParent.RemoveChild(_bannerStatus);
        if (_foundingTotal.Parent is BoxContainer foundingParent)
            foundingParent.RemoveChild(_foundingTotal);
        if (_industry.Parent is BoxContainer industryParent)
            industryParent.RemoveChild(_industry);
        if (_entryFeeEnabled.Parent is BoxContainer feeToggleParent)
            feeToggleParent.RemoveChild(_entryFeeEnabled);
        if (_fee.Parent is BoxContainer feeParent)
            feeParent.RemoveChild(_fee);
        if (_color.Parent is BoxContainer colorParent)
            colorParent.RemoveChild(_color);
        if (_colorPreview.Parent is BoxContainer colorPreviewParent)
            colorPreviewParent.RemoveChild(_colorPreview);
        if (_private.Parent is BoxContainer privateParent)
            privateParent.RemoveChild(_private);
        if (_rosterPublic.Parent is BoxContainer rosterParent)
            rosterParent.RemoveChild(_rosterPublic);
        if (_investment.Parent is BoxContainer investmentParent)
            investmentParent.RemoveChild(_investment);
        if (_creationError.Parent is BoxContainer errorParent)
            errorParent.RemoveChild(_creationError);
        if (_budgetChart.Parent is BoxContainer budgetParent)
            budgetParent.RemoveChild(_budgetChart);
    }

    private Control BuildCreationChoice(OrganizationKind kind, string title, string subtitle)
    {
        var card = new ContainerButton { HorizontalExpand = true, VerticalExpand = true };
        card.OnPressed += _ => SelectCreationKind(kind);
        var panel = new PanelContainer
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#222B28") }
        };
        var stack = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 12,
            Margin = new Thickness(18)
        };
        panel.AddChild(stack);
        card.AddChild(panel);
        stack.AddChild(new Control { VerticalExpand = true });

        stack.AddChild(new OrganizationEmblemControl { HorizontalAlignment = HAlignment.Center });
        stack.AddChild(new PanelContainer
        {
            MinHeight = 3,
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#4E735A") }
        });
        stack.AddChild(new Label
        {
            Text = title,
            HorizontalAlignment = HAlignment.Center,
            FontColorOverride = Color.FromHex("#E4F0E6")
        });
        stack.AddChild(new Label
        {
            Text = subtitle,
            HorizontalAlignment = HAlignment.Center,
            FontColorOverride = Color.FromHex("#9FB7A5")
        });
        stack.AddChild(new Label
        {
            Text = "НАТИСНІТЬ, ЩОБ ОБРАТИ",
            HorizontalAlignment = HAlignment.Center,
            FontColorOverride = Color.FromHex("#B9DCC1")
        });
        stack.AddChild(new Control { VerticalExpand = true });
        return card;
    }

    private void SelectCreationKind(OrganizationKind kind)
    {
        _creationKind = kind;
        if (kind == OrganizationKind.Company && _industry.SelectedId == 0)
            _industry.SelectId((int) OrganizationIndustry.Delivery);
        _creationError.Text = string.Empty;
        RefreshCreation();
    }

    private static string TitleSlotLabel(OrganizationTitleSlot slot) => slot switch
    {
        OrganizationTitleSlot.Owner => "Керівник",
        OrganizationTitleSlot.Deputy => "Заступник",
        OrganizationTitleSlot.Officer => "Офіцер",
        _ => "Учасник"
    };

    private static string KindLabel(OrganizationKind kind) => kind == OrganizationKind.Company ? "Компанія" : "Фракція";

    private static string RoleLabel(OrganizationMemberRole? role) => role switch
    {
        OrganizationMemberRole.Owner => "власник",
        OrganizationMemberRole.Officer => "офіцер",
        OrganizationMemberRole.Member => "учасник",
        _ => "немає"
    };

    private static Control BuildOrganizationBanner(OrganizationEntry org, int size)
    {
        if (org.Banner.Length > 0)
            return new OrganizationBannerControl(org.Banner, size);

        if (org.Name.StartsWith("TDF —", StringComparison.Ordinal))
            return BuildSpriteBanner(size,
                ("/Textures/_Triad/Structure/Decoration/banner.rsi", "tdf-banner"));

        return BuildSpriteBanner(size,
            ("/Textures/Objects/Fun/toys.rsi", "plushie_hampter"),
            ("/Textures/Objects/Fun/plushies.rsi", "plushie_hampter"));
    }

    private static Control BuildSpriteBanner(int size, params (string Path, string State)[] candidates)
    {
        var resources = IoCManager.Resolve<IResourceCache>();
        var frame = default(Texture);
        foreach (var (path, state) in candidates)
        {
            if (!resources.TryGetResource<RSIResource>(new ResPath(path), out var resource) ||
                !resource.RSI.TryGetState(state, out var sprite))
                continue;
            frame = sprite.Frame0;
            break;
        }

        if (frame == null)
            return new OrganizationBannerControl([], size, showDraftPlaceholder: true);

        var texture = new TextureRect
        {
            Texture = frame,
            Stretch = TextureRect.StretchMode.KeepAspectCentered,
            MinWidth = size,
            MinHeight = size,
            HorizontalExpand = true,
            VerticalExpand = true,
            MouseFilter = MouseFilterMode.Ignore
        };

        var border = new PanelContainer
        {
            MinWidth = size,
            MinHeight = size,
            SetSize = new Vector2(size, size),
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = new Color(1f, 1f, 1f, 0.02f),
                BorderColor = Color.FromHex("#74837A"),
                BorderThickness = new Thickness(1)
            }
        };
        border.AddChild(texture);
        return border;
    }

    private static TextureRect BuildIndustryIcon(OrganizationIndustry industry, int size = 20)
    {
        // Use the same sprites as the job icons already shipped with the game.
        // Source of the paths/states: Prototypes/StatusIcon/job.yml and
        // Prototypes/_NF/StatusIcon/job.yml. No custom industry PNGs are needed.
        var (spritePath, spriteState) = industry switch
        {
            OrganizationIndustry.Delivery => ("/Textures/Interface/Misc/job_icons.rsi", "CargoTechnician"),
            OrganizationIndustry.Trade => ("/Textures/Interface/Misc/job_icons.rsi", "QuarterMaster"),
            OrganizationIndustry.Security => ("/Textures/Interface/Misc/job_icons.rsi", "SecurityOfficer"),
            OrganizationIndustry.Services => ("/Textures/Interface/Misc/job_icons.rsi", "ServiceWorker"),
            OrganizationIndustry.Manufacturing => ("/Textures/_NF/Interface/Misc/job_icons.rsi", "planttechnician"),
            OrganizationIndustry.Research => ("/Textures/Interface/Misc/job_icons.rsi", "Scientist"),
            OrganizationIndustry.Construction => ("/Textures/Interface/Misc/job_icons.rsi", "StationEngineer"),
            OrganizationIndustry.Medicine => ("/Textures/Interface/Misc/job_icons.rsi", "Chemist"),
            OrganizationIndustry.Mining => ("/Textures/Interface/Misc/job_icons.rsi", "ShaftMiner"),
            OrganizationIndustry.Agriculture => ("/Textures/Interface/Misc/job_icons.rsi", "Botanist"),
            _ => ("/Textures/Interface/Misc/job_icons.rsi", "ServiceWorker")
        };

        var icon = new TextureRect
        {
            SetSize = new Vector2(size, size),
            VerticalAlignment = VAlignment.Center,
            MouseFilter = MouseFilterMode.Ignore
        };

        var resources = IoCManager.Resolve<IResourceCache>();
        if (resources.TryGetResource<RSIResource>(new ResPath(spritePath), out var resource) &&
            resource.RSI.TryGetState(spriteState, out var sprite))
        {
            icon.Texture = sprite.Frame0;
        }

        return icon;
    }

    private static string FactionFociShortLabel(OrganizationEntry org) =>
        org.FactionFoci.Length > 1
            ? $"{org.FactionFoci.Length} спрямування"
            : FactionFociLabel(org);

    private static string FactionFociLabel(OrganizationEntry org) =>
        org.FactionFoci.Length > 0
            ? string.Join(", ", org.FactionFoci.Select(FactionFocusLabel))
            : FactionFocusLabel(org.FactionFocus);

    private static string FactionFocusLabel(OrganizationFactionFocus focus) => focus switch
    {
        OrganizationFactionFocus.Civic => "Громадська",
        OrganizationFactionFocus.Cultural => "Культурна",
        OrganizationFactionFocus.Religious => "Релігійна",
        OrganizationFactionFocus.Political => "Політична",
        OrganizationFactionFocus.Scientific => "Наукова",
        OrganizationFactionFocus.Humanitarian => "Гуманітарна",
        OrganizationFactionFocus.Defense => "Оборонна",
        OrganizationFactionFocus.Technological => "Технологічна",
        OrganizationFactionFocus.Educational => "Освітня",
        OrganizationFactionFocus.Environmental => "Екологічна",
        OrganizationFactionFocus.Diplomatic => "Дипломатична",
        OrganizationFactionFocus.Exploration => "Експедиційна",
        OrganizationFactionFocus.Colonial => "Колонізаційна",
        OrganizationFactionFocus.Commercial => "Комерційна",
        OrganizationFactionFocus.Industrial => "Промислова",
        OrganizationFactionFocus.Medical => "Медична",
        OrganizationFactionFocus.Ideological => "Ідеологічна",
        OrganizationFactionFocus.Revolutionary => "Революційна",
        OrganizationFactionFocus.Traditionalist => "Традиціоналістська",
        OrganizationFactionFocus.Pacifist => "Пацифістська",
        OrganizationFactionFocus.Militarist => "Мілітаристська",
        OrganizationFactionFocus.Labor => "Робітнича",
        OrganizationFactionFocus.Security => "Безпекова",
        OrganizationFactionFocus.Criminal => "Злочинна",
        OrganizationFactionFocus.Piracy => "Піратська",
        OrganizationFactionFocus.Spiritual => "Духовна",
        OrganizationFactionFocus.Agricultural => "Аграрна",
        OrganizationFactionFocus.Artistic => "Мистецька",
        OrganizationFactionFocus.Media => "Інформаційна",
        _ => "Без спрямування"
    };

    private static string IndustryLabel(OrganizationIndustry industry) => industry switch
    {
        OrganizationIndustry.Delivery => "Доставка",
        OrganizationIndustry.Trade => "Торгівля",
        OrganizationIndustry.Security => "Охорона",
        OrganizationIndustry.Services => "Обслуговування",
        OrganizationIndustry.Manufacturing => "Виробництво",
        OrganizationIndustry.Research => "Дослідження",
        OrganizationIndustry.Construction => "Будівництво",
        OrganizationIndustry.Medicine => "Фармацевтика",
        OrganizationIndustry.Mining => "Видобуток",
        OrganizationIndustry.Agriculture => "Аграрна справа",
        _ => "Немає"
    };
}
