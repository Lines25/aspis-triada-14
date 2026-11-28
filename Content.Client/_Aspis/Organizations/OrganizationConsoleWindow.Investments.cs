using System;
using System.Linq;
using Content.Shared._Aspis.Organizations;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Aspis.Organizations;

public sealed partial class OrganizationConsoleWindow
{
    public event Action<Guid, OrganizationMarketProgram>? MarketProgramRequested;
    public event Action<Guid, bool>? InvestorPrivacyRequested;
    private bool _investmentOverviewExpanded;
    private string _investorSearch = string.Empty;
    private int _investorSort;
    private int _investorPage;

    private void ResetInvestmentOverview()
    {
        _investmentOverviewExpanded = false;
        _investorSearch = string.Empty;
        _investorSort = 0;
        _investorPage = 0;
    }

    private Control BuildInvestmentOverview(OrganizationEntry org)
    {
        var panel = BuildFoldout("Інвестиції", out var body, _investmentOverviewExpanded,
            expanded => _investmentOverviewExpanded = expanded);
        var stats = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true, SeparationOverride = 6 };
        var capital = BuildTitledPanel("Залучений капітал", out var capitalBody);
        capitalBody.AddChild(new Label { Text = $"{org.ExternalInvestmentTotal:N0} $",
            FontColorOverride = Color.FromHex("#91CBA2") });
        var investors = BuildTitledPanel("Інвесторів", out var investorsBody);
        investorsBody.AddChild(new Label { Text = $"{org.ExternalInvestorCount:N0}" });
        stats.AddChild(capital);
        stats.AddChild(investors);
        body.AddChild(stats);
        if (org.Kind == OrganizationKind.Company)
            AddMetric(body, "Підтримка котирування", $"+{org.InvestmentDemandPercent}%", Color.FromHex("#91CBA2"));
        AddMetric(body, "Публічний реєстр", org.InvestorsPublic ? "Відкритий" : "Прихований",
            Color.FromHex(org.InvestorsPublic ? "#91CBA2" : "#D9B77A"));
        if (_state?.Ownership == org.Id && !_state.ViewingAnotherCharacter)
        {
            var privacy = new Button { Text = org.InvestorsPublic ? "Приховати імена" : "Відкрити імена",
                MinHeight = 30, HorizontalAlignment = HAlignment.Right,
                ToolTip = "Змінює видимість списку в публічному реєстрі. Службовий доступ TDF зберігається." };
            privacy.OnPressed += _ =>
            {
                privacy.Disabled = true;
                InvestorPrivacyRequested?.Invoke(org.Id, !org.InvestorsPublic);
            };
            body.AddChild(privacy);
        }

        if (!org.InvestorListVisible)
        {
            body.AddChild(new Label { Text = WrapText("Імена доступні власнику та учасникам із дозволом на інвестиції.", 45) });
            return panel;
        }
        if (org.ExternalInvestors.Length == 0)
        {
            body.AddChild(new Label { Text = "Зовнішніх внесків ще немає.", FontColorOverride = Color.FromHex("#91A2B0") });
            return panel;
        }
        var average = org.ExternalInvestmentTotal / Math.Max(1, org.ExternalInvestorCount);
        AddMetric(body, "Середній внесок на інвестора", $"{average:N0} $");
        var top = org.ExternalInvestors.Max(i => i.Amount);
        AddMetric(body, "Частка найбільшого інвестора",
            $"{100d * top / Math.Max(1L, org.ExternalInvestmentTotal):0.0}%");
        var search = new LineEdit { Text = _investorSearch, PlaceHolder = "Пошук інвестора",
            HorizontalExpand = true, MinHeight = 31 };
        var sort = new OptionButton { HorizontalExpand = true, MinHeight = 30 };
        sort.AddItem("За сумою: більше", 0);
        sort.AddItem("За сумою: менше", 1);
        sort.AddItem("За іменем", 2);
        sort.SelectId(_investorSort);
        body.AddChild(search);
        body.AddChild(sort);
        var rows = new BoxContainer { Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true, SeparationOverride = 4 };
        body.AddChild(rows);

        void Draw()
        {
            Clear(rows);
            var filtered = org.ExternalInvestors.Where(i =>
                i.Name.Contains(_investorSearch.Trim(), StringComparison.OrdinalIgnoreCase));
            var ordered = (_investorSort switch
            {
                1 => filtered.OrderBy(i => i.Amount).ThenBy(i => i.Name),
                2 => filtered.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(i => i.Amount),
                _ => filtered.OrderByDescending(i => i.Amount).ThenBy(i => i.Name)
            }).ToArray();
            const int pageSize = 8;
            var pages = Math.Max(1, (ordered.Length + pageSize - 1) / pageSize);
            _investorPage = Math.Clamp(_investorPage, 0, pages - 1);
            foreach (var investor in ordered.Skip(_investorPage * pageSize).Take(pageSize))
            {
                var item = new PanelContainer { HorizontalExpand = true,
                    PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#2A323A") } };
                var content = new BoxContainer { Orientation = LayoutOrientation.Vertical,
                    HorizontalExpand = true, Margin = new Thickness(7), SeparationOverride = 3 };
                var line = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                    HorizontalExpand = true, SeparationOverride = 5 };
                line.AddChild(new Label { Text = WrapStockName(investor.Name, 22), HorizontalExpand = true });
                line.AddChild(new Label { Text = $"{investor.Amount:N0} $", FontColorOverride = Color.FromHex("#91CBA2") });
                content.AddChild(line);
                var share = (float)(100d * investor.Amount / Math.Max(1L, org.ExternalInvestmentTotal));
                content.AddChild(new ProgressBar { HorizontalExpand = true, MinHeight = 8,
                    MinValue = 0, MaxValue = 100, Value = Math.Clamp(share, 0f, 100f) });
                content.AddChild(new Label { Text = $"{share:0.0}% усіх зовнішніх внесків",
                    FontColorOverride = Color.FromHex("#91A2B0") });
                item.AddChild(content);
                rows.AddChild(item);
            }
            if (ordered.Length == 0)
                rows.AddChild(new Label { Text = "За пошуком нічого не знайдено." });
            var nav = new BoxContainer { Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true, SeparationOverride = 5 };
            var prev = new Button { Text = "←", Disabled = _investorPage == 0, MinWidth = 32 };
            var next = new Button { Text = "→", Disabled = _investorPage + 1 >= pages, MinWidth = 32 };
            prev.OnPressed += _ => { _investorPage--; Draw(); };
            next.OnPressed += _ => { _investorPage++; Draw(); };
            nav.AddChild(prev);
            nav.AddChild(new Label { Text = $"{_investorPage + 1} / {pages} · {ordered.Length} записів",
                HorizontalExpand = true, HorizontalAlignment = HAlignment.Center });
            nav.AddChild(next);
            rows.AddChild(nav);
        }
        search.OnTextChanged += _ => { _investorSearch = search.Text; _investorPage = 0; Draw(); };
        sort.OnItemSelected += e => { _investorSort = e.Id; sort.SelectId(e.Id); _investorPage = 0; Draw(); };
        Draw();
        return panel;
    }

    private void AddPassiveMarketProgramControls(OrganizationEntry org, BoxContainer body)
    {
        if (_state == null)
            return;
        var program = BuildTitledPanel("Ринкові програми", out var programBody);
        var current = _state.MarketProgram;
        var remaining = _state.MarketProgramCycles;
        AddMetric(programBody, "Поточна програма", current switch
        {
            OrganizationMarketProgram.PriceCampaign => "Цінова кампанія",
            OrganizationMarketProgram.PriorityContracts => "Пріоритетні контракти",
            _ => "Не обрано"
        });
        if (remaining > 0)
            AddMetric(programBody, "Циклів залишилося", remaining.ToString());
        var locked = remaining > 0 || !HasRight(OrganizationPermission.Strategy) ||
            _state.ViewingAnotherCharacter || org.InsolvencyState != OrganizationInsolvencyState.Active;
        foreach (var option in new[] { OrganizationMarketProgram.PriceCampaign, OrganizationMarketProgram.PriorityContracts })
        {
            var price = option == OrganizationMarketProgram.PriceCampaign;
            var label = price ? "Цінова кампанія" : "Пріоритетні контракти";
            var choice = BuildTitledPanel(label, out var choiceBody);
            AddMetric(choiceBody, "Вага попиту", price ? "+35%" : "+50%", Color.FromHex("#91CBA2"));
            AddMetric(choiceBody, "Додаткові витрати", price ? "12% виручки" : "20% виручки", Color.FromHex("#E5A39C"));
            choiceBody.AddChild(new Label { Text = WrapText(price ?
                "Залучення замовлень за рахунок цінової кампанії." :
                "Залучення замовлень через пріоритетні угоди.", 43) });
            var button = new Button { Text = "Розпочати на 3 цикли", Disabled = locked,
                HorizontalExpand = true, MinHeight = 32 };
            var confirmed = false;
            button.OnPressed += _ =>
            {
                if (!confirmed)
                {
                    confirmed = true;
                    button.Text = "Підтвердити програму";
                    return;
                }
                button.Disabled = true;
                MarketProgramRequested?.Invoke(org.Id, option);
            };
            choiceBody.AddChild(button);
            programBody.AddChild(choice);
        }
        programBody.AddChild(new Label { Text = WrapText("Програма змінює розподіл пасивного попиту та витрати. Прибуток не гарантовано.", 43),
            FontColorOverride = Color.FromHex("#91A2B0") });
        body.AddChild(program);
    }
}
