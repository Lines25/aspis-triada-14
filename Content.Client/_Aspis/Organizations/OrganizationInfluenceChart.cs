using System.Linq;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Aspis.Organizations;

public sealed class OrganizationInfluenceChart : BoxContainer
{
    private readonly RingControl _ring = new();
    private readonly BoxContainer _legend = new()
    {
        Orientation = LayoutOrientation.Vertical,
        HorizontalExpand = true,
        SeparationOverride = 4
    };

    public OrganizationInfluenceChart()
    {
        Orientation = LayoutOrientation.Horizontal;
        HorizontalExpand = true;
        SeparationOverride = 12;
        AddChild(_ring);
        AddChild(_legend);
    }

    public void SetData(OrganizationEntry[] entries, OrganizationIndustry? industry = null)
    {
        var ranked = entries
            .Where(o => industry == null || o.Kind == OrganizationKind.Company && o.Industry == industry.Value)
            .Select(o => (Name: o.Name, Percent: industry == null ? o.InfluencePercent : o.IndustryPercent,
                Color: Color.FromHex(o.Color)))
            .Where(o => o.Percent > 0f)
            .OrderByDescending(o => o.Percent)
            .ToArray();

        var series = ranked.Take(6).ToList();
        if (ranked.Length > 6)
            series.Add(("Інші організації", ranked.Skip(6).Sum(o => o.Percent), Color.FromHex("#88939C")));

        var occupied = ranked.Sum(o => o.Percent);
        series.Add(("Вільний вплив", MathF.Max(0f, 100f - occupied), Color.FromHex("#3B444D")));
        _ring.Segments = series.Where(x => x.Percent > 0f).Select(x => (x.Percent, x.Color)).ToArray();

        _legend.RemoveAllChildren();
        foreach (var entry in series.Where(x => x.Percent > 0.0001f))
        {
            var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 5 };
            row.AddChild(new PanelContainer
            {
                MinWidth = 11,
                MinHeight = 11,
                PanelOverride = new StyleBoxFlat { BackgroundColor = entry.Color }
            });
            row.AddChild(new Label { Text = entry.Name, HorizontalExpand = true });
            row.AddChild(new Label { Text = $"{entry.Percent:0.00}%" });
            _legend.AddChild(row);
        }
    }

    private sealed class RingControl : Control
    {
        public (float Percent, Color Color)[] Segments = [];

        public RingControl()
        {
            MinWidth = 225;
            MinHeight = 225;
            MaxWidth = 250;
            MaxHeight = 250;
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            base.Draw(handle);
            var center = new Vector2(PixelSize.X * 0.5f, PixelSize.Y * 0.5f);
            var radius = MathF.Min(PixelSize.X, PixelSize.Y) * 0.37f;
            var start = 0f;

            foreach (var (percent, color) in Segments)
            {
                var end = MathF.Min(360f, start + percent * 3.6f);
                var steps = Math.Max(1, (int) MathF.Ceiling((end - start) / 2.5f));
                for (var depth = 0; depth < 20; depth++)
                {
                    var r = radius - depth * 1.15f;
                    var previous = Point(center, r, start);
                    for (var i = 1; i <= steps; i++)
                    {
                        var current = Point(center, r, start + (end - start) * i / steps);
                        handle.DrawLine(previous, current, color);
                        previous = current;
                    }
                }
                start = end;
            }

            handle.DrawCircle(center, radius - 26, Color.FromHex("#1A1D21"), true);
            handle.DrawCircle(center, radius - 26, Color.FromHex("#57636D"), false);
        }

        private static Vector2 Point(Vector2 center, float radius, float angle)
        {
            var radians = angle * MathF.PI / 180f;
            return center + new Vector2(MathF.Sin(radians) * radius, -MathF.Cos(radians) * radius);
        }
    }
}
