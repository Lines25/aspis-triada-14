using System;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Content.Shared.Containers.ItemSlots;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// Company-local access list, independent from stock station access tags.
/// The server checks inserted ID + organization ownership for each click.
/// </summary>
public sealed class OrganizationOutpostConfiguratorBui(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private OrganizationOutpostConfiguratorWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<OrganizationOutpostConfiguratorWindow>();
        _window.ToggleIdCard += () => SendMessage(new ItemSlotButtonPressedEvent(
            OrganizationOutpostConfiguratorComponent.CardSlotId));
        _window.SelectSlot += slot => SendMessage(new OrganizationOutpostConfiguratorSelectSlotMessage(slot));
        if (State is OrganizationOutpostConfiguratorUiState state)
            _window.UpdateState(state);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is OrganizationOutpostConfiguratorUiState configurator)
            _window?.UpdateState(configurator);
    }
}

public sealed class OrganizationOutpostConfiguratorWindow : DefaultWindow
{
    private readonly BoxContainer _slots = new()
    {
        Orientation = BoxContainer.LayoutOrientation.Vertical,
        SeparationOverride = 7,
        HorizontalExpand = true
    };
    private readonly Label _target = new() { FontColorOverride = Color.FromHex("#DCE7F0") };
    private readonly Button _card = new() { MinHeight = 44, HorizontalExpand = true };
    private readonly Label _status = new() { FontColorOverride = Color.FromHex("#B4C7D8") };

    public event Action? ToggleIdCard;
    public event Action<int>? SelectSlot;

    public OrganizationOutpostConfiguratorWindow()
    {
        Title = "Корпоративні доступи";
        MinSize = new Vector2i(440, 380);
        SetSize = new Vector2(480, 445);
        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 10,
            Margin = new Thickness(16)
        };
        Contents.AddChild(body);
        body.AddChild(new Label { Text = "Налаштування доступу дверей аванпосту" });
        body.AddChild(_target);
        _card.OnPressed += _ => ToggleIdCard?.Invoke();
        body.AddChild(_card);
        body.AddChild(_status);
        body.AddChild(_slots);
    }

    public void UpdateState(OrganizationOutpostConfiguratorUiState state)
    {
        _target.Text = $"Двері: {state.TargetName}";
        _card.Text = $"ID-картка: {state.CardName} (вставити / вийняти)";
        _status.Text = state.Slots.Length == 0
            ? "Ще не придбано жодного корпоративного доступу."
            : "Оберіть придбаний доступ для цих дверей.";
        _slots.RemoveAllChildren();
        foreach (var entry in state.Slots)
        {
            var slot = entry.Slot;
            var b = new Button
            {
                Text = $"{(state.SelectedSlot == slot ? "✓  " : "□  ")}{slot}. {entry.Title}",
                MinHeight = 44,
                HorizontalExpand = true,
                Disabled = !entry.Available
            };
            // The server independently revalidates this card and slot on click.
            b.OnPressed += _ => SelectSlot?.Invoke(slot);
            _slots.AddChild(b);
        }
    }
}
