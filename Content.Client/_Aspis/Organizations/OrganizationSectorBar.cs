using System;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

/// <summary>Ranking: draws only the earned colored fraction, never an empty dark track.</summary>
public sealed class OrganizationSectorBar(float percent, Color color) : Control
{
    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var width = PixelSize.X * Math.Clamp(percent, 0f, 100f) / 100f;
        if (width <= 0 || PixelSize.Y <= 0)
            return;
        handle.DrawRect(new UIBox2(0, 0, width, PixelSize.Y), color);
    }
}
