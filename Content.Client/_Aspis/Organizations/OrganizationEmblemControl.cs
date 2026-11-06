using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._Aspis.Organizations;

public sealed class OrganizationEmblemControl : Control
{
    public OrganizationEmblemControl()
    {
        MinWidth = 94;
        MinHeight = 92;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        var center = new Vector2(PixelSize.X * 0.5f, PixelSize.Y * 0.5f);
        var background = Color.FromHex("#324B3D");
        var highlight = Color.FromHex("#8CD7A1");
        handle.DrawRect(UIBox2.FromDimensions(center - new Vector2(33, 33), new Vector2(66, 66)), background);
        handle.DrawRect(UIBox2.FromDimensions(center - new Vector2(8, 26), new Vector2(16, 52)), highlight);
        handle.DrawRect(UIBox2.FromDimensions(center - new Vector2(26, 8), new Vector2(52, 16)), highlight);
    }
}
