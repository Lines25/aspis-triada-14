using Content.Shared._Aspis.Organizations;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

[UsedImplicitly]
public sealed class OrganizationFrontierReceivingBui(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private FrontierReceivingWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<FrontierReceivingWindow>();
        _window.Selected += category => SendMessage(new OrganizationFrontierSelectPostMessage(category));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is OrganizationFrontierReceivingState selected)
            _window?.Refresh(selected);
    }
}

public sealed class FrontierReceivingWindow : DefaultWindow
{
    public event Action<int>? Selected;
    private readonly Label _title = new();
    private readonly Label _instructions = new();
    private readonly Button[] _buttons = new Button[4];

    public FrontierReceivingWindow()
    {
        Title = "Далекий рубіж: вантажний термінал";
        MinWidth = 475;
        MinHeight = 510;
        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 12
        };
        Contents.AddChild(root);
        _title.FontColorOverride = Color.FromHex("#A7D5E7");
        root.AddChild(_title);
        _instructions.Text = "Виберіть категорію. Приймання відбувається біля цього термінала, " +
                             "тільки для ящиків відповідної категорії.";
        root.AddChild(_instructions);
        for (var row = 0; row < 2; row++)
        {
            var strip = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                SeparationOverride = 12,
                HorizontalExpand = true
            };
            root.AddChild(strip);
            for (var col = 0; col < 2; col++)
            {
                var post = row * 2 + col;
                var button = new Button { MinWidth = 195, MaxWidth = 195, MinHeight = 180, MaxHeight = 180 };
                button.OnPressed += _ => Selected?.Invoke(post);
                _buttons[post] = button;
                strip.AddChild(button);
            }
        }
    }

    public void Refresh(OrganizationFrontierReceivingState state)
    {
        _title.Text = $"Контракт {state.Code}. Приймання: сектор {(char)('A' + state.Selected)}";
        for (var post = 0; post < 4; post++)
        {
            var count = post < state.Received.Length ? state.Received[post] : 0;
            var total = post < state.Targets.Length ? state.Targets[post] : 0;
            var button = _buttons[post];
            button.Text = $"{(char)('A' + post)}\n{count} / {total} ящиків";
            button.Modulate = state.Selected == post
                ? Color.FromHex("#83CBB5") : Color.FromHex("#D8E6ED");
        }
    }
}
