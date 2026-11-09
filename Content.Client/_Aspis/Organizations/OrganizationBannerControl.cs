using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.Utility;
using Robust.Shared.Maths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Robust.Shared.Maths.Color;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// Draws either a legacy monochrome 32x32/64x64 mask or a true-color 32x32 PNG logo.
/// When no image is supplied it can render the dashed draft placeholder used in creation.
/// </summary>
public sealed class OrganizationBannerControl : Control
{
    private readonly List<(int X, int Y, int Length, Color Color)> _runs = new();
    private readonly bool _showDraftPlaceholder;
    private int _width;
    private int _height;

    public OrganizationBannerControl(byte[]? banner, int size = 48, bool showDraftPlaceholder = false)
    {
        _showDraftPlaceholder = showDraftPlaceholder;
        if (!TryBuildLegacyRuns(banner) && !TryBuildPngRuns(banner))
        {
            _width = 0;
            _height = 0;
        }

        MinWidth = size;
        MinHeight = size;
        SetSize = new Vector2(size, size);
        MouseFilter = MouseFilterMode.Ignore;
    }

    private bool TryBuildLegacyRuns(byte[]? banner)
    {
        if (banner is not { Length: 128 or 512 })
            return false;

        // A real PNG can coincidentally occupy exactly 128 or 512 bytes.
        // Never interpret its encoded bytes as a legacy bit mask.
        if (banner.Length >= 8 && banner[0] == 137 && banner[1] == 80 &&
            banner[2] == 78 && banner[3] == 71 && banner[4] == 13 &&
            banner[5] == 10 && banner[6] == 26 && banner[7] == 10)
            return false;

        var resolution = banner.Length == 512 ? 64 : 32;
        _width = resolution;
        _height = resolution;
        for (var y = 0; y < resolution; y++)
        {
            var start = -1;
            for (var x = 0; x <= resolution; x++)
            {
                var black = x < resolution &&
                    (banner[(y * resolution + x) / 8] & (1 << (7 - (x % 8)))) != 0;
                if (black && start < 0)
                    start = x;
                if (!black && start >= 0)
                {
                    _runs.Add((start, y, x - start, Color.Black));
                    start = -1;
                }
            }
        }

        return _runs.Count > 0;
    }

    private bool TryBuildPngRuns(byte[]? banner)
    {
        if (banner is not { Length: > 0 })
            return false;

        try
        {
            using var stream = new MemoryStream(banner, writable: false);
            using var image = Image.Load<Rgba32>(stream);
            if (image.Width is < 8 or > 32 || image.Height is < 8 or > 32)
                return false;

            _width = image.Width;
            _height = image.Height;
            var pixels = image.GetPixelSpan();

            for (var y = 0; y < image.Height; y++)
            {
                var start = -1;
                var runColor = Color.Transparent;
                for (var x = 0; x <= image.Width; x++)
                {
                    var visible = false;
                    var currentColor = Color.Transparent;
                    if (x < image.Width)
                    {
                        var pixel = pixels[y * image.Width + x];
                        visible = pixel.A > 4;
                        currentColor = new Color(pixel.R / 255f, pixel.G / 255f, pixel.B / 255f, pixel.A / 255f);
                    }

                    if (visible && start < 0)
                    {
                        start = x;
                        runColor = currentColor;
                        continue;
                    }

                    if (visible && start >= 0 && ColorsEqual(runColor, currentColor))
                        continue;

                    if (start >= 0)
                    {
                        _runs.Add((start, y, x - start, runColor));
                        start = -1;
                    }

                    if (visible)
                    {
                        start = x;
                        runColor = currentColor;
                    }
                }
            }

            return _runs.Count > 0;
        }
        catch
        {
            _runs.Clear();
            _width = 0;
            _height = 0;
            return false;
        }
    }

    private static bool ColorsEqual(Color a, Color b)
    {
        const float epsilon = 0.001f;
        return MathF.Abs(a.R - b.R) < epsilon &&
               MathF.Abs(a.G - b.G) < epsilon &&
               MathF.Abs(a.B - b.B) < epsilon &&
               MathF.Abs(a.A - b.A) < epsilon;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var size = MathF.Min(PixelSize.X, PixelSize.Y);
        var border = Color.FromHex("#74837A");
        if (_runs.Count == 0)
        {
            if (_showDraftPlaceholder)
                DrawDraftPlaceholder(handle, size);
            return;
        }

        var origin = new Vector2((PixelSize.X - size) * 0.5f, (PixelSize.Y - size) * 0.5f);
        handle.DrawRect(new UIBox2(origin.X, origin.Y, origin.X + size, origin.Y + size), new Color(1f, 1f, 1f, 0.02f));
        handle.DrawRect(new UIBox2(origin.X, origin.Y, origin.X + size, origin.Y + 1.5f), border);
        handle.DrawRect(new UIBox2(origin.X, origin.Y + size - 1.5f, origin.X + size, origin.Y + size), border);
        handle.DrawRect(new UIBox2(origin.X, origin.Y, origin.X + 1.5f, origin.Y + size), border);
        handle.DrawRect(new UIBox2(origin.X + size - 1.5f, origin.Y, origin.X + size, origin.Y + size), border);

        var unitX = size / Math.Max(1, _width);
        var unitY = size / Math.Max(1, _height);
        foreach (var (x, y, length, color) in _runs)
            handle.DrawRect(new UIBox2(origin.X + x * unitX, origin.Y + y * unitY,
                origin.X + (x + length) * unitX, origin.Y + (y + 1) * unitY), color);
    }

    private static void DrawDraftPlaceholder(DrawingHandleScreen handle, float size)
    {
        var inset = 2f;
        var dash = MathF.Max(7f, size / 7.5f);
        var gap = MathF.Max(4f, dash * 0.4f);
        var thickness = MathF.Max(2f, size * 0.028f);
        var ink = Color.FromHex("#8B968E");
        void DashRect(float x1, float y1, float x2, float y2)
            => handle.DrawRect(new UIBox2(x1, y1, x2, y2), ink);

        for (var x = inset; x < size - inset; x += dash + gap)
        {
            DashRect(x, inset, MathF.Min(x + dash, size - inset), inset + thickness);
            DashRect(x, size - inset - thickness, MathF.Min(x + dash, size - inset), size - inset);
        }
        for (var y = inset; y < size - inset; y += dash + gap)
        {
            DashRect(inset, y, inset + thickness, MathF.Min(y + dash, size - inset));
            DashRect(size - inset - thickness, y, size - inset, MathF.Min(y + dash, size - inset));
        }

        var center = size * 0.5f;
        var arm = size * 0.16f;
        var thick = MathF.Max(3.25f, size * 0.055f);
        var green = Color.FromHex("#66D36E");
        handle.DrawRect(new UIBox2(center - thick * 0.5f, center - arm, center + thick * 0.5f, center + arm), green);
        handle.DrawRect(new UIBox2(center - arm, center - thick * 0.5f, center + arm, center + thick * 0.5f), green);
    }
}
