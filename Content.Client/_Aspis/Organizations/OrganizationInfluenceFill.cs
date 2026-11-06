using System;
using System.Collections.Generic;
using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

/// <summary>
/// A single bevelled registry tile, with a neutral fill across the entire shape.
/// No inner progress bar or faction-colored track is drawn.
/// </summary>
public sealed class OrganizationInfluenceFill(float percent) : BoxContainer
{
    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var w = PixelSize.X;
        var h = PixelSize.Y;
        if (w <= 1 || h <= 1)
            return;

        var cut = MathF.Min(13f, MathF.Min(w, h) * 0.18f);
        var shape = new[]
        {
            new Vector2(cut, 0),
            new Vector2(w - cut, 0),
            new Vector2(w, cut),
            new Vector2(w, h - cut),
            new Vector2(w - cut, h),
            new Vector2(cut, h),
            new Vector2(0, h - cut),
            new Vector2(0, cut)
        };
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, shape, Color.FromHex("#30343A"));

        // Clip the SAME bevelled shape to the current influence amount.
        // The neutral pearl-grey tint stays readable regardless of organization color.
        var fillX = w * Math.Clamp(percent, 0f, 100f) / 100f;
        if (fillX <= 0)
            return;

        var clipped = new List<Vector2>(12);
        for (var i = 0; i < shape.Length; i++)
        {
            var a = shape[i];
            var b = shape[(i + 1) % shape.Length];
            var aInside = a.X <= fillX;
            var bInside = b.X <= fillX;
            if (aInside)
                clipped.Add(a);
            if (aInside == bInside)
                continue;
            var ratio = (fillX - a.X) / (b.X - a.X);
            clipped.Add(a + (b - a) * ratio);
        }
        if (clipped.Count >= 3)
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, clipped.ToArray(),
                Color.FromHex("#637478").WithAlpha(0.64f));
    }
}
