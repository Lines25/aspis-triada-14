using System;
using System.Linq;
using Robust.Client.Graphics;
using Content.Shared._Aspis.Organizations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Aspis.Organizations;

public sealed partial class OrganizationConsoleWindow
{
    private enum TdfReviewFilter { Pending, Open, Archive, All }
    private TdfReviewFilter _tdfReviewFilter;
    private Guid? _tdfSelectedCase;
    private string? _tdfSelectedAlert;
    private Guid? _tdfDraftCase;
    private string _tdfDecisionDraft = string.Empty;
    private readonly BoxContainer _tdfReviewList = new()
    {
        Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 5
    };
    private readonly BoxContainer _tdfReviewDetail = new()
    {
        Orientation = LayoutOrientation.Vertical, HorizontalExpand = true, SeparationOverride = 8
    };

    private void ResetTdfReviewSelection()
    {
        _tdfReviewFilter = TdfReviewFilter.Pending;
        _tdfSelectedCase = null;
        _tdfSelectedAlert = null;
        _tdfDraftCase = null;
        _tdfDecisionDraft = string.Empty;
        _tdfReviewList.Parent?.RemoveChild(_tdfReviewList);
        _tdfReviewDetail.Parent?.RemoveChild(_tdfReviewDetail);
        Clear(_tdfReviewList);
        Clear(_tdfReviewDetail);
        Clear(_tdfBody);
    }

    private static Color TdfCaseColor(OrganizationTdfCaseStatus status) => Color.FromHex(status switch
    {
        OrganizationTdfCaseStatus.Open => "#E5BF75",
        OrganizationTdfCaseStatus.Cleared => "#91CBA2",
        _ => "#E49C91"
    });

    private static string TdfCaseStatus(OrganizationTdfCaseStatus status) => status switch
    {
        OrganizationTdfCaseStatus.Open => "На перевірці",
        OrganizationTdfCaseStatus.Cleared => "Порушень не встановлено",
        _ => "Передано на службовий розгляд"
    };

    private static int TdfSeverity(string reason) =>
        reason.StartsWith("III —", StringComparison.Ordinal) ? 3 :
        reason.StartsWith("II —", StringComparison.Ordinal) ? 2 :
        reason.StartsWith("I —", StringComparison.Ordinal) ? 1 : 0;

    private static Color TdfSignalColor(string reason) => Color.FromHex(TdfSeverity(reason) switch
    {
        3 => "#EC918B",
        2 => "#E7B779",
        _ => "#D7C791"
    });

    private static string TdfSeverityLabel(string reason) => TdfSeverity(reason) switch
    {
        3 => "III / Високий пріоритет",
        2 => "II / Потрібна перевірка",
        1 => "I / Спостереження",
        _ => "Потрібна перевірка"
    };

    private static string TdfReason(string reason)
    {
        // Existing case records retain their original text and evidence keys.
        // Display the same observation in shorter, neutral language.
        var text = reason;
        var level = TdfSeverity(reason);
        if (level > 0)
            text = reason[(reason.IndexOf('—') + 1)..].Trim();
        return text switch
        {
            "Різкий розворот торгів (перевірка можливого штучного розгону ціни)" =>
                "Учасник змінив напрям торгів за короткий час. Перевірте попередні купівлі та продажі.",
            "Значне розмивання часток (випуск акцій)" =>
                "Випущено значний пакет нових акцій. Перевірте зміну часток власників.",
            "Зовнішнє фінансування пов'язаного учасника торгів" =>
                "Інвестор також нещодавно торгував акціями цієї компанії.",
            "Зворотні угоди між тими самими рахунками (перевірка узгоджених торгів)" =>
                "Зафіксовано зворотні угоди між тими самими рахунками. Перевірте зв'язок між учасниками.",
            "Ціна угоди істотно відхиляється від котирування" =>
                "Ціна угоди помітно відрізняється від біржового котирування.",
            _ => text
        };
    }

    private static string TdfAlertKey(OrganizationTradeAlertEntry alert) =>
        !string.IsNullOrEmpty(alert.SourceKey) ? alert.SourceKey :
            $"{alert.Utc.Ticks}:{alert.Company}:{alert.Actor}:{alert.Description}";

    private static string TdfWrap(string value, int width = 32) =>
        string.IsNullOrWhiteSpace(value) ? "—" : WrapStockName(value.Replace("\r", " ").Replace("\n", " "), width);

    private void RefreshTdf()
    {
        // Detach reusable columns before disposing the previous layout.
        _tdfReviewList.Parent?.RemoveChild(_tdfReviewList);
        _tdfReviewDetail.Parent?.RemoveChild(_tdfReviewDetail);
        Clear(_tdfBody);
        Clear(_tdfReviewList);
        Clear(_tdfReviewDetail);
        if (_state == null || !_state.TdfAuthorized)
        {
            ResetTdfReviewSelection();
            _tdfBody.AddChild(new Label { Text = "Потрібен допуск до фінансового нагляду." });
            return;
        }

        var cases = _state.TdfCases;
        var registered = cases.Select(c => c.SourceKey).ToHashSet(StringComparer.Ordinal);
        var pending = _state.TdfAlerts.Count(a => a.Flagged && !string.IsNullOrEmpty(a.SourceKey) &&
            !registered.Contains(a.SourceKey));
        var active = cases.Count(c => c.Status == OrganizationTdfCaseStatus.Open);
        var archive = cases.Length - active;
        var counters = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true, SeparationOverride = 6 };
        AddTdfCounter(counters, "Нові сигнали", pending, "#E6B777", TdfReviewFilter.Pending);
        AddTdfCounter(counters, "Відкриті справи", active, "#D9C48B", TdfReviewFilter.Open);
        AddTdfCounter(counters, "Архів справ", archive, "#91CBA2", TdfReviewFilter.Archive);
        var overview = BuildTitledPanel("Огляд нагляду", out var overviewBody);
        overview.PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#1A1D21") };
        overviewBody.AddChild(counters);
        _tdfBody.AddChild(overview);

        var layout = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true, VerticalExpand = true, SeparationOverride = 10 };
        var left = BuildTitledPanel("Журнал", out var leftBody, true);
        left.HorizontalExpand = false;
        left.MinWidth = 300;
        left.PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#1A1D21") };
        var filterRow = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 8 };
        filterRow.AddChild(new Label { Text = "Розділ журналу", FontColorOverride = Color.FromHex("#91A2B0") });
        var filter = new OptionButton { HorizontalExpand = true, MinHeight = 32 };
        filter.AddItem("Нові сигнали", (int)TdfReviewFilter.Pending);
        filter.AddItem("Відкриті справи", (int)TdfReviewFilter.Open);
        filter.AddItem("Архів справ", (int)TdfReviewFilter.Archive);
        filter.AddItem("Усі записи біржі", (int)TdfReviewFilter.All);
        filter.SelectId((int)_tdfReviewFilter);
        filter.OnItemSelected += e => ChangeTdfFilter((TdfReviewFilter)e.Id);
        filterRow.AddChild(filter);
        var refresh = new Button { Text = "Оновити", MinHeight = 32 };
        refresh.OnPressed += _ => RefreshRequested?.Invoke();
        filterRow.AddChild(refresh);
        overviewBody.AddChild(filterRow);
        var listScroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        listScroll.AddChild(_tdfReviewList);
        leftBody.AddChild(listScroll);
        layout.AddChild(left);

        var right = BuildTitledPanel("Матеріали перевірки", out var rightBody, true);
        right.HorizontalExpand = true;
        right.MinWidth = 420;
        right.PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#1A1D21") };
        var detailScroll = new ScrollContainer { HScrollEnabled = false, HorizontalExpand = true, VerticalExpand = true };
        detailScroll.AddChild(_tdfReviewDetail);
        rightBody.AddChild(detailScroll);
        layout.AddChild(right);
        _tdfBody.AddChild(layout);

        // When an alert becomes a case, keep its materials selected.
        if (_tdfReviewFilter == TdfReviewFilter.Pending && _tdfSelectedCase == null && _tdfSelectedAlert != null)
        {
            var opened = cases.FirstOrDefault(c => c.SourceKey == _tdfSelectedAlert);
            if (opened != null)
            {
                _tdfSelectedCase = opened.Id;
                _tdfSelectedAlert = null;
                ChangeTdfFilter(opened.Status == OrganizationTdfCaseStatus.Open ?
                    TdfReviewFilter.Open : TdfReviewFilter.Archive, keepSelection: true);
                return;
            }
        }
        if (_tdfReviewFilter == TdfReviewFilter.Open && _tdfSelectedCase is { } selected &&
            cases.Any(c => c.Id == selected && c.Status != OrganizationTdfCaseStatus.Open))
        {
            _tdfDraftCase = null;
            _tdfDecisionDraft = string.Empty;
            ChangeTdfFilter(TdfReviewFilter.Archive, keepSelection: true);
            return;
        }
        if (_tdfReviewFilter == TdfReviewFilter.Archive && _tdfSelectedCase is { } reopened &&
            cases.Any(c => c.Id == reopened && c.Status == OrganizationTdfCaseStatus.Open))
        {
            _tdfDraftCase = null;
            _tdfDecisionDraft = string.Empty;
            ChangeTdfFilter(TdfReviewFilter.Open, keepSelection: true);
            return;
        }
        RefreshTdfReviewRows();
    }

    private void ChangeTdfFilter(TdfReviewFilter filter, bool keepSelection = false)
    {
        if (_tdfReviewFilter == filter)
            return;
        _tdfReviewFilter = filter;
        if (!keepSelection)
        {
            _tdfSelectedCase = null;
            _tdfSelectedAlert = null;
        }
        RefreshTdf();
    }

    private void AddTdfCounter(BoxContainer parent, string title, int value, string color, TdfReviewFilter filter)
    {
        var panel = BuildTitledPanel(title, out var body);
        var selected = _tdfReviewFilter == filter;
        panel.PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#262E35"),
            BorderColor = Color.FromHex(selected ? color : "#38434C"), BorderThickness = new Thickness(1) };
        var button = new Button { Text = value.ToString(), MinHeight = 32,
            Modulate = Color.FromHex(color), HorizontalExpand = true };
        button.OnPressed += _ => ChangeTdfFilter(filter);
        body.AddChild(button);
        parent.AddChild(panel);
    }

    private void RefreshTdfReviewRows()
    {
        Clear(_tdfReviewList);
        Clear(_tdfReviewDetail);
        if (_state == null || !_state.TdfAuthorized)
            return;

        var registered = _state.TdfCases.Select(c => c.SourceKey).ToHashSet(StringComparer.Ordinal);
        var cases = _state.TdfCases.Where(c => _tdfReviewFilter switch
        {
            TdfReviewFilter.Open => c.Status == OrganizationTdfCaseStatus.Open,
            TdfReviewFilter.Archive => c.Status != OrganizationTdfCaseStatus.Open,
            _ => false
        }).OrderByDescending(c => c.CreatedUtc).ToArray();
        var alerts = _state.TdfAlerts.Where(a => _tdfReviewFilter == TdfReviewFilter.All ||
            (_tdfReviewFilter == TdfReviewFilter.Pending && a.Flagged &&
                !string.IsNullOrEmpty(a.SourceKey) && !registered.Contains(a.SourceKey)))
            .OrderByDescending(a => a.Utc).ToArray();

        if (_tdfSelectedCase.HasValue && !cases.Any(c => c.Id == _tdfSelectedCase.Value))
            _tdfSelectedCase = null;
        if (_tdfSelectedAlert != null && !alerts.Any(a => TdfAlertKey(a) == _tdfSelectedAlert))
            _tdfSelectedAlert = null;
        if (_tdfSelectedCase == null && _tdfSelectedAlert == null)
        {
            if (cases.Length > 0)
                _tdfSelectedCase = cases[0].Id;
            else if (alerts.Length > 0)
                _tdfSelectedAlert = TdfAlertKey(alerts[0]);
        }

        foreach (var docket in cases)
        {
            var selected = _tdfSelectedCase == docket.Id;
            AddTdfReviewRow(docket.Company, TdfCaseStatus(docket.Status), docket.CreatedUtc,
                TdfCaseColor(docket.Status), selected, () =>
                {
                    _tdfSelectedCase = docket.Id;
                    _tdfSelectedAlert = null;
                    RefreshTdfReviewRows();
                });
            if (selected)
                ShowTdfCase(docket);
        }
        foreach (var alert in alerts)
        {
            var key = TdfAlertKey(alert);
            var selected = _tdfSelectedAlert == key;
            var hasCase = alert.Flagged && registered.Contains(alert.SourceKey);
            var reason = string.IsNullOrEmpty(alert.Reason) ? alert.Description : alert.Reason;
            var color = alert.Flagged ? TdfSignalColor(reason) : Color.FromHex("#9DC9AB");
            var label = hasCase ? "Є справа" : alert.Flagged ? TdfSeverityLabel(reason) :
                string.IsNullOrEmpty(alert.Operation) ? "Угода" : alert.Operation;
            AddTdfReviewRow(alert.Company, label, alert.Utc, color, selected, () =>
            {
                _tdfSelectedAlert = key;
                _tdfSelectedCase = null;
                RefreshTdfReviewRows();
            });
            if (selected)
                ShowTdfAlert(alert);
        }
        if (cases.Length == 0 && alerts.Length == 0)
        {
            _tdfReviewList.AddChild(new Label { Text = "Записів немає.", FontColorOverride = Color.FromHex("#91A2B0") });
            _tdfReviewDetail.AddChild(new Label { Text = "У цьому розділі немає матеріалів." });
        }
    }

    private void AddTdfReviewRow(string company, string status, DateTime utc, Color color, bool selected, Action choose)
    {
        var panel = new PanelContainer { HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex(selected ? "#34424E" : "#252D35"),
                BorderColor = Color.FromHex(selected ? "#7894A8" : "#39444D"), BorderThickness = new Thickness(1) } };
        var root = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 6 };
        root.AddChild(new PanelContainer { MinWidth = 4, VerticalExpand = true,
            PanelOverride = new StyleBoxFlat { BackgroundColor = color } });
        var body = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
            Margin = new Thickness(4, 4, 5, 4), SeparationOverride = 3 };
        var pick = new Button { Text = TdfWrap(company, 24), ToolTip = company, MinHeight = 31,
            HorizontalExpand = true };
        pick.OnPressed += _ => choose();
        body.AddChild(pick);
        body.AddChild(new Label { Text = TdfWrap(status, 28), FontColorOverride = color });
        body.AddChild(new Label { Text = $"{utc:dd.MM.yyyy HH:mm} UTC", FontColorOverride = Color.FromHex("#91A2B0") });
        root.AddChild(body);
        panel.AddChild(root);
        _tdfReviewList.AddChild(panel);
    }

    private static void AddTdfField(BoxContainer body, string title, string value, Color? color = null, int width = 25)
    {
        body.AddChild(new Label { Text = title, FontColorOverride = Color.FromHex("#91A2B0") });
        body.AddChild(new Label { Text = TdfWrap(value, width),
            FontColorOverride = color ?? Color.FromHex("#E2E8ED") });
    }

    private static BoxContainer TdfColumn(BoxContainer parent)
    {
        var column = new BoxContainer { Orientation = LayoutOrientation.Vertical, HorizontalExpand = true,
            SeparationOverride = 5 };
        parent.AddChild(column);
        return column;
    }

    private void AddTdfHeading(string company, string status, Color color)
    {
        var panel = BuildTitledPanel("Організація", out var header);
        header.AddChild(new Label { Text = TdfWrap(company, 42), FontColorOverride = Color.FromHex("#E2E8ED") });
        header.AddChild(new Label { Text = TdfWrap(status, 42), FontColorOverride = color });
        _tdfReviewDetail.AddChild(panel);
    }

    private void ShowTdfAlert(OrganizationTradeAlertEntry alert)
    {
        var reason = string.IsNullOrEmpty(alert.Reason) && alert.Flagged ? alert.Description : alert.Reason;
        var color = alert.Flagged ? TdfSignalColor(reason) : Color.FromHex("#91CBA2");
        AddTdfHeading(alert.Company, alert.Flagged ? TdfSeverityLabel(reason) : "Зареєстровано", color);
        var card = BuildTitledPanel("Відомості про запис", out var body);
        var columns = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 12 };
        var left = TdfColumn(columns);
        var right = TdfColumn(columns);
        AddTdfField(left, "Дата і час (UTC)", $"{alert.Utc:dd.MM.yyyy HH:mm}");
        AddTdfField(left, "Учасник", alert.Actor);
        if (!string.IsNullOrWhiteSpace(alert.Counterparty))
            AddTdfField(left, "Контрагент", alert.Counterparty);
        AddTdfField(right, "Вид запису", alert.Operation);
        if (alert.Amount > 0)
            AddTdfField(right, alert.Actor == "Автономна економіка" ? "Виручка циклу" : "Сума",
                $"{alert.Amount:N0} $", Color.FromHex("#91CBA2"));
        if (alert.Quantity > 0)
            AddTdfField(right, "Акції", $"{alert.Quantity:N0} шт.");
        body.AddChild(columns);
        _tdfReviewDetail.AddChild(card);

        if (alert.Flagged)
        {
            var reasonPanel = BuildTitledPanel("Підстава для перевірки", out var reasonBody);
            reasonBody.AddChild(new Label { Text = TdfWrap(TdfReason(reason), 46), FontColorOverride = color });
            reasonBody.AddChild(new Label { Text = TdfWrap("Сигнал не є встановленим порушенням.", 46),
                FontColorOverride = Color.FromHex("#91A2B0") });
            _tdfReviewDetail.AddChild(reasonPanel);
            var docket = _state?.TdfCases.FirstOrDefault(c => c.SourceKey == alert.SourceKey);
            if (docket != null)
            {
                var view = new Button { Text = "Перейти до справи", MinHeight = 35, HorizontalExpand = true };
                view.OnPressed += _ =>
                {
                    _tdfSelectedCase = docket.Id;
                    _tdfSelectedAlert = null;
                    ChangeTdfFilter(docket.Status == OrganizationTdfCaseStatus.Open ?
                        TdfReviewFilter.Open : TdfReviewFilter.Archive, keepSelection: true);
                };
                _tdfReviewDetail.AddChild(view);
            }
            else if (!string.IsNullOrEmpty(alert.SourceKey))
            {
                var open = new Button { Text = "Відкрити справу", MinHeight = 35, HorizontalExpand = true,
                    Modulate = Color.FromHex("#E7B779") };
                open.OnPressed += _ =>
                {
                    open.Disabled = true;
                    TdfOpenCaseRequested?.Invoke(alert.SourceKey);
                };
                _tdfReviewDetail.AddChild(open);
            }
        }
        else if (string.IsNullOrEmpty(alert.Operation))
            _tdfReviewDetail.AddChild(new Label { Text = TdfWrap(alert.Description, 46) });
    }

    private void ShowTdfCase(OrganizationTdfCaseEntry docket)
    {
        AddTdfHeading(docket.Company, TdfCaseStatus(docket.Status), TdfCaseColor(docket.Status));
        var summary = BuildTitledPanel($"Справа № {docket.Id.ToString("N")[..8].ToUpperInvariant()}", out var body);
        var columns = new BoxContainer { Orientation = LayoutOrientation.Horizontal, HorizontalExpand = true, SeparationOverride = 12 };
        var left = TdfColumn(columns);
        var right = TdfColumn(columns);
        AddTdfField(left, "Відкрито (UTC)", $"{docket.CreatedUtc:dd.MM.yyyy HH:mm}");
        AddTdfField(left, "Учасник", docket.Subject);
        AddTdfField(right, "Стан", TdfCaseStatus(docket.Status), TdfCaseColor(docket.Status));
        AddTdfField(right, "Співробітник", docket.LastOfficer);
        body.AddChild(columns);
        _tdfReviewDetail.AddChild(summary);

        var evidence = BuildTitledPanel("Підстава", out var evidenceBody);
        evidenceBody.AddChild(new Label { Text = TdfWrap(TdfReason(docket.Reason), 46),
            FontColorOverride = TdfSignalColor(docket.Reason) });
        var alert = _state?.TdfAlerts.FirstOrDefault(a => a.SourceKey == docket.SourceKey);
        if (alert != null)
        {
            if (!string.IsNullOrWhiteSpace(alert.Counterparty))
                AddTdfField(evidenceBody, "Контрагент", alert.Counterparty, width: 42);
            if (alert.Amount > 0)
                AddTdfField(evidenceBody, alert.Actor == "Автономна економіка" ? "Виручка циклу" : "Сума запису",
                    $"{alert.Amount:N0} $", width: 42);
            if (alert.Quantity > 0)
                AddTdfField(evidenceBody, "Акції", $"{alert.Quantity:N0} шт.", width: 42);
        }
        _tdfReviewDetail.AddChild(evidence);

        if (!string.IsNullOrWhiteSpace(docket.DecisionNote))
        {
            var result = BuildTitledPanel(docket.Status == OrganizationTdfCaseStatus.Open ?
                "Підстава поновлення" : "Рішення", out var resultBody);
            resultBody.AddChild(new Label { Text = TdfWrap(docket.DecisionNote, 46),
                FontColorOverride = TdfCaseColor(docket.Status) });
            _tdfReviewDetail.AddChild(result);
        }

        if (_state?.TdfCanResolveCases == true && docket.Status == OrganizationTdfCaseStatus.Open)
        {
            if (_tdfDraftCase != docket.Id)
            {
                _tdfDraftCase = docket.Id;
                _tdfDecisionDraft = string.Empty;
            }
            var decision = BuildTitledPanel("Рішення за справою", out var decisionBody);
            var note = new LineEdit { PlaceHolder = "Підстава рішення (5–220 символів)",
                Text = _tdfDecisionDraft, HorizontalExpand = true, MinHeight = 32 };
            var hint = new Label { Text = TdfWrap("Коротко зазначте, що встановлено під час перевірки.", 46),
                FontColorOverride = Color.FromHex("#91A2B0") };
            decisionBody.AddChild(note);
            decisionBody.AddChild(hint);
            var clear = new Button { Text = "Закрити справу", MinHeight = 34, HorizontalExpand = true,
                Modulate = Color.FromHex("#91CBA2"), ToolTip = "Порушення не встановлено" };
            var refer = new Button { Text = "Передати на розгляд", MinHeight = 34, HorizontalExpand = true,
                Modulate = Color.FromHex("#E49C91"), ToolTip = "Передати матеріали без автоматичних санкцій" };
            void Validate()
            {
                _tdfDecisionDraft = note.Text;
                var valid = note.Text.Trim().Length is >= 5 and <= 220 && !note.Text.Any(char.IsControl);
                clear.Disabled = refer.Disabled = !valid;
            }
            note.OnTextChanged += _ => Validate();
            Validate();
            void Submit(OrganizationTdfCaseStatus status)
            {
                clear.Disabled = refer.Disabled = true;
                TdfResolveCaseRequested?.Invoke(docket.Id, status, note.Text.Trim());
            }
            clear.OnPressed += _ => Submit(OrganizationTdfCaseStatus.Cleared);
            refer.OnPressed += _ => Submit(OrganizationTdfCaseStatus.Referred);
            var actions = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true, SeparationOverride = 6 };
            actions.AddChild(clear);
            actions.AddChild(refer);
            decisionBody.AddChild(actions);
            decisionBody.AddChild(new Label { Text = TdfWrap("Передача справи фіксує матеріали для подальшого службового розгляду. Санкції застосовуються окремо.", 46),
                FontColorOverride = Color.FromHex("#91A2B0") });
            _tdfReviewDetail.AddChild(decision);
        }
        else if (docket.Status == OrganizationTdfCaseStatus.Open)
            _tdfReviewDetail.AddChild(new Label { Text = TdfWrap("Для рішення потрібен старший співробітник.", 46) });
        else if (_state?.TdfCanResolveCases == true)
        {
            if (_tdfDraftCase != docket.Id)
            {
                _tdfDraftCase = docket.Id;
                _tdfDecisionDraft = string.Empty;
            }
            var reopenPanel = BuildTitledPanel("Поновлення справи", out var reopenBody);
            reopenBody.AddChild(new Label { Text = TdfWrap("З'явилися нові матеріали? Зазначте підставу для повторної перевірки.", 46) });
            var note = new LineEdit { Text = _tdfDecisionDraft, PlaceHolder = "Нові відомості (5–220 символів)",
                HorizontalExpand = true, MinHeight = 32 };
            var reopen = new Button { Text = "Поновити справу", HorizontalExpand = true, MinHeight = 34,
                Modulate = Color.FromHex("#E5BF75") };
            void ValidateReopen()
            {
                _tdfDecisionDraft = note.Text;
                reopen.Disabled = note.Text.Trim().Length is < 5 or > 220 || note.Text.Any(char.IsControl);
            }
            note.OnTextChanged += _ => ValidateReopen();
            ValidateReopen();
            reopen.OnPressed += _ =>
            {
                reopen.Disabled = true;
                TdfResolveCaseRequested?.Invoke(docket.Id, OrganizationTdfCaseStatus.Open, note.Text.Trim());
            };
            reopenBody.AddChild(note);
            reopenBody.AddChild(reopen);
            _tdfReviewDetail.AddChild(reopenPanel);
        }

        if (docket.History.Length > 0)
        {
            var history = BuildFoldout("Журнал рішень", out var historyBody);
            foreach (var line in docket.History)
                historyBody.AddChild(new Label { Text = TdfWrap(line, 44) });
            _tdfReviewDetail.AddChild(history);
        }
    }
}
