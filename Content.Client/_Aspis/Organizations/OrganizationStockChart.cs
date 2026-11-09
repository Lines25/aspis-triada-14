using System;
using System.Linq;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

/// <summary>Real quote history, with a numerical right-hand axis shared by all series.</summary>
public sealed class OrganizationStockChart : Control
{
    private readonly (Guid Id, float[] History, Color Ink, Color HoverInk)[] _series;
    private Guid? _hoveredCompany;

    public void SetHovered(Guid? company) => _hoveredCompany = company;
    private readonly bool _relative;
    private readonly Font _axisFont;

    public OrganizationStockChart(OrganizationStockEntry[] stocks, bool relative = false)
    {
        _relative = relative;
        _series = stocks.Where(s => s.History.Length > 0)
            .Select(s => (s.Id, Normalize(s.History, relative), Color.FromHex(s.Color),
                s.Color.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) ||
                s.Color.Equals("#FFF", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromHex("#FFE6A0") : Color.White)).ToArray();
        _axisFont = LoadAxisFont();
        HorizontalExpand = true;
        MinHeight = 305;
    }

    public OrganizationStockChart(int[] points)
    {
        _series = [(Guid.Empty, Normalize(points.Length == 0 ? new[] { 0 } : points, false),
            Color.FromHex("#76BFA0"), Color.White)];
        _axisFont = LoadAxisFont();
        HorizontalExpand = true;
        MinHeight = 180;
    }

    private static Font LoadAxisFont() => new VectorFont(
        IoCManager.Resolve<IResourceCache>().GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 11);

    private static float[] Normalize(int[] values, bool relative)
    {
        if (!relative)
            return values.Select(x => (float)x).ToArray();

        var basis = Math.Max(1, values[0]);
        return values.Select(value => (value - basis) * 100f / basis).ToArray();
    }

    private static float NiceStep(float raw)
    {
        var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(MathF.Max(0.0001f, raw))));
        var scaled = raw / magnitude;
        var unit = scaled <= 1f ? 1f : scaled <= 2f ? 2f : scaled <= 5f ? 5f : 10f;
        return unit * magnitude;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var w = PixelSize.X;
        var h = PixelSize.Y;
        if (w < 150 || h < 85)
            return;

        const float left = 15f;
        const float rightAxis = 79f;
        const float top = 17f;
        const float bottomMargin = 18f;
        var right = w - rightAxis;
        var bottom = h - bottomMargin;
        var grid = Color.FromHex("#303944");

        if (_series.Length == 0)
            return;

        var min = _series.Min(s => s.History.Min());
        var max = _series.Max(s => s.History.Max());
        if (_relative)
        {
            min = MathF.Min(min, 0f);
            max = MathF.Max(max, 0f);
        }
        if (max - min < 0.001f)
        {
            min -= _relative ? 1f : MathF.Max(1f, MathF.Abs(max) * 0.05f);
            max += _relative ? 1f : MathF.Max(1f, MathF.Abs(max) * 0.05f);
        }
        var step = NiceStep((max - min) / 4f);
        var tickMin = MathF.Floor(min / step) * step;
        var tickMax = MathF.Ceiling(max / step) * step;
        if (tickMax - tickMin < step)
            tickMax = tickMin + step;
        var range = tickMax - tickMin;

        // Grid levels are the labels shown on the right (%, or actual share prices).
        var tickCount = (int)MathF.Round(range / step);
        for (var tick = 0; tick <= tickCount; tick++)
        {
            var value = tickMin + tick * step;
            var y = bottom - (bottom - top) * (value - tickMin) / range;
            handle.DrawLine(new Vector2(left, y), new Vector2(right, y), grid);
            var number = MathF.Abs(value) < step * 0.001f ? 0f : value;
            var fmt = step < 1f ? "0.##" : "0";
            var text = number.ToString(fmt) + (_relative ? "%" : " $");
            handle.DrawString(_axisFont, new Vector2(right + 9, y - 7), text,
                Color.FromHex("#A8B8C7"));
        }
        for (var column = 0; column <= 10; column++)
        {
            var x = left + (right - left) * column / 10f;
            handle.DrawLine(new Vector2(x, top), new Vector2(x, bottom), grid);
        }
        if (_relative && tickMin <= 0 && tickMax >= 0)
        {
            var zeroY = bottom - (bottom - top) * (-tickMin) / range;
            handle.DrawLine(new Vector2(left, zeroY), new Vector2(right, zeroY),
                Color.FromHex("#6D7886"));
        }

        var maxSamples = Math.Max(2, _series.Max(s => s.History.Length));
        foreach (var (id, points, baseInk, hoverInk) in _series)
        {
            var hovered = id != Guid.Empty && _hoveredCompany == id;
            var ink = hovered ? hoverInk : baseInk;
            Vector2 Point(int index) => new(
                left + (right - left) * (maxSamples - points.Length + index) / (maxSamples - 1f),
                bottom - (bottom - top) * (points[index] - tickMin) / range);
            for (var i = 1; i < points.Length; i++)
            {
                var previous = Point(i - 1);
                var next = Point(i);
                handle.DrawLine(previous, next, ink);
                if (hovered)
                {
                    // Width without engine-version-specific DrawLine overloads.
                    handle.DrawLine(previous + new Vector2(0, -1), next + new Vector2(0, -1), ink);
                    handle.DrawLine(previous + new Vector2(0, 1), next + new Vector2(0, 1), ink);
                }
            }
            handle.DrawCircle(Point(points.Length - 1), hovered ? 4f : 2.5f, ink, true);
        }
    }
}
