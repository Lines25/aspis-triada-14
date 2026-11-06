using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Aspis.Organizations;

public sealed class OrganizationBudgetChart : BoxContainer
{
    private static readonly Color[] SegmentColors =
    [
        Color.FromHex("#76BFA0"),
        Color.FromHex("#6E9DCE"),
        Color.FromHex("#E4B66A"),
        Color.FromHex("#BD86BE")
    ];

    private readonly BudgetRing _ring;
    private readonly Label _budgetTotal = new();
    private static readonly string[] CompanyHints =
    [
        "Маркетинг: реклама, впізнаваність компанії та залучення нових замовлень.",
        "Операції: логістика, постачання та поточна діяльність компанії.",
        "Безпека: захист активів, страхування ризиків і стабільність роботи.",
        "Дослідження: нові розробки, модернізація та підвищення ефективності."
    ];
    private readonly Label[] _descriptions = new Label[4];
    private readonly Label[] _amounts = new Label[4];
    private readonly int[] _shares = [25, 25, 25, 25];
    private string[] _labels = ["Маркетинг", "Операції", "Безпека", "Дослідження"];
    private int _investment;

    public int[] Allocation => (int[]) _shares.Clone();

    public OrganizationBudgetChart()
    {
        Orientation = LayoutOrientation.Vertical;
        HorizontalExpand = true;
        SeparationOverride = 8;

        _ring = new BudgetRing(_shares, SegmentColors);
        _ring.SharesChanged += UpdateLegend;
        AddChild(_ring);
        AddChild(_budgetTotal);

        var legend = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            SeparationOverride = 4
        };
        AddChild(legend);

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var row = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 5,
                ToolTip = CompanyHints[i]
            };
            row.AddChild(new PanelContainer
            {
                MinWidth = 12,
                MinHeight = 12,
                PanelOverride = new StyleBoxFlat { BackgroundColor = SegmentColors[index] }
            });

            var label = new Label { HorizontalExpand = true, ToolTip = CompanyHints[i], MouseFilter = MouseFilterMode.Stop };
            row.AddChild(label);
            _descriptions[i] = label;

            var info = new Button
            {
                Text = "?",
                ToolTip = CompanyHints[i],
                MinWidth = 24,
                MinHeight = 24
            };
            row.AddChild(info);

            var amount = new Label { MinWidth = 74, HorizontalAlignment = HAlignment.Right };
            _amounts[i] = amount;
            row.AddChild(amount);

            var minus = new Button { Text = "−", MinWidth = 24, ToolTip = $"Зменшити: {_labels[index]}" };
            var plus = new Button { Text = "+", MinWidth = 24, ToolTip = $"Збільшити: {_labels[index]}" };
            minus.OnPressed += _ => Shift(index, -1);
            plus.OnPressed += _ => Shift(index, 1);
            row.AddChild(minus);
            row.AddChild(plus);
            legend.AddChild(row);
        }

        UpdateLegend();
    }

    public void SetLabels(string[] labels)
    {
        if (labels.Length != 4)
            return;
        _labels = labels;
        UpdateLegend();
    }

    public void SetInvestment(int investment)
    {
        _investment = Math.Max(0, investment);
        UpdateLegend();
    }

    public void SetAllocation(int[] shares)
    {
        if (shares.Length != 4 || shares.Any(value => value is < 5 or > 85) || shares.Sum() != 100)
            return;
        Array.Copy(shares, _shares, 4);
        UpdateLegend();
    }

    private void Shift(int index, int change)
    {
        var next = (index + 1) % 4;
        if (_shares[index] + change < 5 || _shares[next] - change < 5)
            return;

        _shares[index] += change;
        _shares[next] -= change;
        UpdateLegend();
    }

    private void UpdateLegend()
    {
        _budgetTotal.Text = $"Розподілюваний бюджет: {_investment:N0} $";
        for (var i = 0; i < 4; i++)
        {
            _descriptions[i].Text = $"{_labels[i]} — {_shares[i]}%";
            _amounts[i].Text = $"{(long) _investment * _shares[i] / 100:N0} $";
        }
    }

    private sealed class BudgetRing : Control
    {
        private readonly int[] _shares;
        private readonly Color[] _colors;
        private bool _dragging;
        private int _boundary = -1;
        private Vector2 _pointer;

        public event Action? SharesChanged;

        public BudgetRing(int[] shares, Color[] colors)
        {
            _shares = shares;
            _colors = colors;
            MinWidth = 180;
            MinHeight = 185;
            HorizontalAlignment = HAlignment.Center;
            MouseFilter = MouseFilterMode.Stop;
            ToolTip = "Перетягніть межу секторів або використовуйте кнопки + / − для точного налаштування.";
            OnMouseExited += _ =>
            {
                _dragging = false;
                _boundary = -1;
            };
        }

        private Vector2 Center => new(PixelSize.X * 0.5f, PixelSize.Y * 0.5f);
        private float Radius => MathF.Min(PixelSize.X, PixelSize.Y) * 0.38f;

        private float AngleFromPoint(Vector2 point)
        {
            var relative = point - Center;
            return (MathF.Atan2(relative.X, -relative.Y) * 180f / MathF.PI + 360f) % 360f;
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);
            if (args.Function != EngineKeyFunctions.UIClick)
                return;

            _pointer = args.RelativePixelPosition;
            var distance = Vector2.Distance(_pointer, Center);
            if (distance < Radius - 24 || distance > Radius + 24)
                return;

            var angle = AngleFromPoint(_pointer);
            var boundaryAngle = 0f;
            for (var i = 0; i < 3; i++)
            {
                boundaryAngle += _shares[i] * 3.6f;
                var diff = MathF.Abs(angle - boundaryAngle);
                if (MathF.Min(diff, 360f - diff) > 12f)
                    continue;

                _boundary = i;
                _dragging = true;
                args.Handle();
                return;
            }
        }

        protected override void KeyBindUp(GUIBoundKeyEventArgs args)
        {
            base.KeyBindUp(args);
            if (args.Function != EngineKeyFunctions.UIClick)
                return;
            _dragging = false;
            _boundary = -1;
        }

        protected override void MouseMove(GUIMouseMoveEventArgs args)
        {
            base.MouseMove(args);
            if (!_dragging || _boundary < 0)
                return;

            _pointer += args.Relative;
            var angle = AngleFromPoint(_pointer);
            var preceding = 0;
            for (var i = 0; i < _boundary; i++)
                preceding += _shares[i];

            var pairTotal = _shares[_boundary] + _shares[_boundary + 1];
            var left = Math.Clamp((int) MathF.Round(angle / 3.6f) - preceding, 5, pairTotal - 5);
            if (left == _shares[_boundary])
                return;

            _shares[_boundary] = left;
            _shares[_boundary + 1] = pairTotal - left;
            SharesChanged?.Invoke();
        }

        // Cache complete horizontal spans, not radial strokes: no hairlines,
        // holes between angular samples or cross-hatch artefacts after scaling.
        private readonly List<(int X, int Y, int Width, int Segment)> _spans = new();
        private readonly int[] _renderedShares = [-1, -1, -1, -1];
        private int _renderedWidth = -1;
        private int _renderedHeight = -1;

        private void RebuildSpans(int width, int height)
        {
            _spans.Clear();
            _renderedWidth = width;
            _renderedHeight = height;
            Array.Copy(_shares, _renderedShares, 4);

            var center = new Vector2(width * 0.5f, height * 0.5f);
            var outer = MathF.Min(width, height) * 0.38f;
            var inner = MathF.Max(0, outer - 25f);
            var innerSquared = inner * inner;
            var outerSquared = outer * outer;
            var boundA = _shares[0] * 3.6f;
            var boundB = boundA + _shares[1] * 3.6f;
            var boundC = boundB + _shares[2] * 3.6f;

            for (var y = 0; y < height; y++)
            {
                var previousSegment = -1;
                var runStart = 0;
                for (var x = 0; x <= width; x++)
                {
                    var segment = -1;
                    if (x < width)
                    {
                        var dx = x + 0.5f - center.X;
                        var dy = y + 0.5f - center.Y;
                        var squared = dx * dx + dy * dy;
                        if (squared >= innerSquared && squared <= outerSquared)
                        {
                            var angle = (MathF.Atan2(dx, -dy) * 180f / MathF.PI + 360f) % 360f;
                            segment = angle < boundA ? 0 : angle < boundB ? 1 : angle < boundC ? 2 : 3;
                        }
                    }
                    if (segment == previousSegment)
                        continue;
                    if (previousSegment >= 0)
                        _spans.Add((runStart, y, x - runStart, previousSegment));
                    runStart = x;
                    previousSegment = segment;
                }
            }
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);
            var width = Math.Max(0, (int)MathF.Ceiling(PixelSize.X));
            var height = Math.Max(0, (int)MathF.Ceiling(PixelSize.Y));
            if (width < 20 || height < 20)
                return;

            if (width != _renderedWidth || height != _renderedHeight ||
                Enumerable.Range(0, 4).Any(i => _renderedShares[i] != _shares[i]))
                RebuildSpans(width, height);

            foreach (var (x, y, runWidth, segment) in _spans)
                handle.DrawRect(new Robust.Shared.Maths.UIBox2(x, y, x + runWidth + 0.2f, y + 1.05f), _colors[segment]);

            // Three visible grips mark the movable borders between the four allocations.
            // The fourth edge is fixed, since shares always sum to 100%.
            var angle = 0f;
            for (var i = 0; i < 3; i++)
            {
                angle += _shares[i] * MathF.PI / 50f;
                var direction = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
                var start = Center + direction * (Radius - 11f);
                var end = Center + direction * (Radius + 16f);
                var gripColor = _dragging && _boundary == i
                    ? Color.FromHex("#FFF2C8") : Color.FromHex("#E4EAF1");
                handle.DrawLine(start, end, gripColor);
                handle.DrawCircle(end, 4.5f, gripColor, true);
                handle.DrawCircle(end, 2f, Color.FromHex("#3F4B58"), true);
            }
        }


    }
}
