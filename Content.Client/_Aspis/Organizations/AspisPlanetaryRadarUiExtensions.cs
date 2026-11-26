using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared._Aspis.Organizations;
using Robust.Client.Graphics;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;

namespace Content.Client.Shuttles.UI;

/// <summary>
/// Planet flight UI is injected as a dedicated NAV panel at the bottom LEFT
/// of the radar, not right-click alternative verbs. The game-side BUI event is
/// wired in one upstream glue line in Aspis_PlanetaryConsoleBUI_Buttons.patch.
/// </summary>
public sealed partial class NavScreen
{
    public event Action<bool>? AspisTransferRequested;
    private BoxContainer? _aspisFlightPanel;
    private Button? _aspisLand;
    private Button? _aspisTakeOff;
    private Label? _aspisFlightVelocity;
    private Label? _aspisFlightHeading;

    public void AspisInitializeControls() => EnsureAspisFlightPanel();

    private void EnsureAspisFlightPanel()
    {
        if (_aspisFlightPanel != null)
            return;
        if (NavRadar.Parent is not LayoutContainer radarHost)
            return;

        // A SHORT, persistent strip within the RADAR bounds. Previously the
        // header/status text expanded the panel below its parent and clipped
        // BOTH actionable buttons on smaller shuttle windows.
        var panel = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 5,
            MinWidth = 244,
            HorizontalExpand = false,
            VerticalExpand = false,
            Visible = false,
        };
        _aspisFlightPanel = panel;
        radarHost.AddChild(panel);
        LayoutContainer.SetAnchorAndMarginPreset(panel,
            LayoutContainer.LayoutPreset.BottomLeft, margin: 12);

        _aspisLand = new Button
        {
            Text = "▼ ПОСАДКА",
            MinWidth = 116,
            MinHeight = 30,
            HorizontalExpand = true,
        };
        _aspisTakeOff = new Button
        {
            Text = "▲ ЗЛІТ",
            MinWidth = 116,
            MinHeight = 30,
            HorizontalExpand = true,
        };
        _aspisLand.ToolTip = "Перехід із атмосфери на поверхню через штатну процедуру посадки.";
        _aspisTakeOff.ToolTip = "Повернення з поверхні до повітряного простору.";
        _aspisLand.OnPressed += _ => AspisTransferRequested?.Invoke(false);
        _aspisTakeOff.OnPressed += _ => AspisTransferRequested?.Invoke(true);
        panel.AddChild(_aspisLand);
        panel.AddChild(_aspisTakeOff);
    }

    private void UpdateAspisPlanetaryReadouts(TransformComponent ship, PhysicsComponent body)
    {
        EnsureAspisFlightPanel();
        var isPlanet = false;
        var surface = false;
        if (ship.MapUid is { } map &&
            _entManager.TryGetComponent<AspisPlanetaryRadarComponent>(map, out var marker))
        {
            isPlanet = true;
            surface = marker.Surface;
        }
        if (_aspisFlightPanel != null) _aspisFlightPanel.Visible = isPlanet;
        if (_aspisLand != null) _aspisLand.Disabled = !isPlanet || surface;
        if (_aspisTakeOff != null) _aspisTakeOff.Disabled = !isPlanet || !surface;

        var mode = !isPlanet ? "КОСМОС" : surface ? "ПОВЕРХНЯ" : "АТМОСФЕРА";
        var speed = body.LinearVelocity.Length();
        var condition = !isPlanet ? "НАВІГАЦІЯ" : surface ? "ПОСАДКА" :
            speed >= 20f ? "ШВИДКІСТЬ НОРМАЛЬНА" : "ЗАГРОЗА ЗРИВУ <20";
        if (AspisFlightMode.Text != mode) AspisFlightMode.Text = mode;
        if (AspisFlightCondition.Text != condition) AspisFlightCondition.Text = condition;
        if (_aspisFlightVelocity != null)
        {
            var status = $"ШВИДКІСТЬ · {speed:0.0} м/с";
            if (_aspisFlightVelocity.Text != status) _aspisFlightVelocity.Text = status;
        }
        if (_aspisFlightHeading != null && _aspisFlightHeading.Text != condition)
            _aspisFlightHeading.Text = condition;
    }
}

/// <summary>
/// Geographically anchored procedural relief. It is a deterministic synthetic
/// height model, NOT a measurement of real terrain (planet biomes have no DEM
/// yet). Unlike the previous screen-space sine waves, this uses stable world
/// coordinates, three octave value noise, crater depressions and contour
/// interpolation. Cached per radar map cell to avoid frame allocation spikes.
/// </summary>
public partial class ShuttleNavControl
{
    // World coordinates in metres, not 18x18 screen pixels.
    private readonly List<(Vector2 A, Vector2 B, int Level)> _aspisReliefLines = new();
    private Vector2 _aspisReliefCenter = new(float.NaN, float.NaN);
    private float _aspisReliefStep;
    private EntityUid? _aspisReliefMap;

    private static float AspisLerp(float a, float b, float t) => a + (b-a)*t;
    private static float AspisSmooth(float x) => x*x*(3f-2f*x);
    private static float AspisHash(int x,int y)
    {
        unchecked
        {
            uint n = (uint)(x*374761393) + (uint)(y*668265263) + 0x5F356495u;
            n = (n ^ (n >> 13))*1274126177u;
            return (n ^ (n >> 16)) / (float)uint.MaxValue;
        }
    }
    private static float AspisNoise(float x,float y)
    {
        var ix=(int)MathF.Floor(x); var iy=(int)MathF.Floor(y);
        var fx=AspisSmooth(x-ix); var fy=AspisSmooth(y-iy);
        return AspisLerp(AspisLerp(AspisHash(ix,iy),AspisHash(ix+1,iy),fx),
                    AspisLerp(AspisHash(ix,iy+1),AspisHash(ix+1,iy+1),fx),fy);
    }
    private static float AspisHeight(Vector2 p)
    {
        var h=0.52f*AspisNoise(p.X/190f,p.Y/190f)
            +0.31f*AspisNoise(p.X/57f,p.Y/57f)
            +0.17f*AspisNoise(p.X/20f,p.Y/20f);
        var ridge=MathF.Abs(p.Y-49f-23f*MathF.Sin(p.X/83f));
        h+=0.14f*MathF.Exp(-ridge*ridge/360f);
        var basin=Vector2.DistanceSquared(p,new Vector2(125f,-55f));
        h-=0.18f*MathF.Exp(-basin/(135f*135f));
        return Math.Clamp(h,0f,1f);
    }
    private static Vector2 AspisCross(Vector2 a, Vector2 b, float va, float vb, float level)
    {
        var delta=vb-va;
        var t=MathF.Abs(delta)<0.000001f ? 0.5f : Math.Clamp((level-va)/delta,0f,1f);
        return Vector2.Lerp(a,b,t);
    }

    private void RefreshAspisRelief(Vector2 center, EntityUid map, float step)
    {
        const int cells=30;
        var snapped=new Vector2(MathF.Floor(center.X/step)*step,MathF.Floor(center.Y/step)*step);
        if (_aspisReliefMap==map && _aspisReliefCenter==snapped &&
            MathF.Abs(_aspisReliefStep-step)<0.001f)
            return;
        _aspisReliefMap=map;
        _aspisReliefCenter=snapped;
        _aspisReliefStep=step;
        _aspisReliefLines.Clear();
        var origin=snapped-new Vector2(cells*step*0.5f);
        var heights=new float[cells+1,cells+1];
        for(var y=0;y<=cells;y++)
        for(var x=0;x<=cells;x++)
            heights[x,y]=AspisHeight(origin+new Vector2(x*step,y*step));
        // Bounded contour count and fixed array, IL-verifier friendly.
        var edges=new Vector2[4];
        for(var levelIndex=0;levelIndex<7;levelIndex++)
        {
            var level=0.16f+levelIndex*0.105f;
            for(var y=0;y<cells;y++)
            for(var x=0;x<cells;x++)
            {
                var h0=heights[x,y];var h1=heights[x+1,y];
                var h2=heights[x+1,y+1];var h3=heights[x,y+1];
                var a=origin+new Vector2(x*step,y*step);
                var b=a+new Vector2(step,0);
                var c=a+new Vector2(step,step);
                var d=a+new Vector2(0,step);
                var count=0;
                if((h0>=level)!=(h1>=level)) edges[count++]=AspisCross(a,b,h0,h1,level);
                if((h1>=level)!=(h2>=level)) edges[count++]=AspisCross(b,c,h1,h2,level);
                if((h2>=level)!=(h3>=level)) edges[count++]=AspisCross(c,d,h2,h3,level);
                if((h3>=level)!=(h0>=level)) edges[count++]=AspisCross(d,a,h3,h0,level);
                for(var i=0;i+1<count;i+=2)
                    _aspisReliefLines.Add((edges[i],edges[i+1],levelIndex));
            }
        }
    }

    // Called by the existing Q2.5B NAV hook. Uses exactly the same world ->
    // shuttle -> screen matrix as the stock radar's Draw() implementation.
    private void DrawAspisPlanetaryContours(DrawingHandleScreen handle,
        Vector2 mapPosition, EntityUid? map)
    {
        if (map is not { } uid ||
            !EntManager.TryGetComponent<AspisPlanetaryRadarComponent>(uid,out var radar))
        {
            _aspisReliefMap=null;
            return;
        }
        var worldRot=_rotation??Angle.Zero;
        var worldToShuttle=Matrix3Helpers.CreateTranslation(-mapPosition)*
            Matrix3Helpers.CreateRotation(-worldRot);
        var shuttleToView=Matrix3x2.CreateScale(new Vector2(MinimapScale,-MinimapScale))*
            Matrix3x2.CreateTranslation(MidPointVector);
        var worldToView=worldToShuttle*shuttleToView;
        // The cache resolution follows radar zoom. No fixed on-screen square,
        // no backward-moving screen-space overlay, no coordinate flip.
        var step=Math.Clamp(WorldRange*3f/30f,2f,4096f);
        RefreshAspisRelief(mapPosition,uid,step);
        var contour=radar.Surface?Color.FromHex("#87B78C"):Color.FromHex("#E4B879");
        var terrainTitle=radar.Surface?"SURFACE / TOPOGRAPHIC NAV":"ATMOSPHERE / TERRAIN FOLLOW";

        foreach(var (a,b,level) in _aspisReliefLines)
        {
            var p=Vector2.Transform(a,worldToView);
            var q=Vector2.Transform(b,worldToView);
            if ((p.X<0f&&q.X<0f)||(p.Y<0f&&q.Y<0f)||
                (p.X>PixelWidth&&q.X>PixelWidth)||(p.Y>PixelHeight&&q.Y>PixelHeight))
                continue;
            handle.DrawLine(p,q,contour.WithAlpha(level%3==0?0.65f:0.34f));
        }
        // A few fixed world-aligned survey crosshairs are meaningful only
        // in geographic space; they naturally pan/rotate/zoom with the shuttle.
        var gridStep=step*5f;
        var gx=(int)MathF.Floor(mapPosition.X/gridStep);
        var gy=(int)MathF.Floor(mapPosition.Y/gridStep);
        for(var x=-3;x<=3;x++)
        for(var y=-3;y<=3;y++)
        {
            var survey=new Vector2((gx+x)*gridStep,(gy+y)*gridStep);
            var p=Vector2.Transform(survey,worldToView);
            if(p.X<10||p.Y<10||p.X>PixelWidth-10||p.Y>PixelHeight-10)continue;
            var mark=contour.WithAlpha(0.15f);
            handle.DrawLine(p-new Vector2(3,0),p+new Vector2(3,0),mark);
            handle.DrawLine(p-new Vector2(0,3),p+new Vector2(0,3),mark);
        }
        // HUD text is fixed on the instrument glass, but the TERRAIN is not.
        handle.DrawString(Font,new Vector2(22f,17f),terrainTitle,contour);
        handle.DrawString(Font,new Vector2(22f,PixelHeight-26f),
            $"X {mapPosition.X:0}   Y {mapPosition.Y:0}   RANGE {WorldRange:0}m",contour.WithAlpha(0.85f));
    }

    // This is called at the very end of the upstream radar Draw(). Terrain
    // stays BEHIND normal IFF and blips; this unmistakable ship icon is in FRONT.
    private void DrawAspisShipHighlight(DrawingHandleScreen handle, Vector2 mapPosition, EntityUid? map)
    {
        if (map is not { } uid ||
            !EntManager.TryGetComponent<AspisPlanetaryRadarComponent>(uid, out _))
            return;
        var worldRot=_rotation??Angle.Zero;
        var transform=Matrix3Helpers.CreateTranslation(-mapPosition)*
            Matrix3Helpers.CreateRotation(-worldRot)*
            Matrix3x2.CreateScale(new Vector2(MinimapScale,-MinimapScale))*
            Matrix3x2.CreateTranslation(MidPointVector);
        if (_consoleEntity is not { } console ||
            !EntManager.TryGetComponent<TransformComponent>(console,out var consoleXform) ||
            consoleXform.GridUid is not { } ship ||
            !EntManager.TryGetComponent<TransformComponent>(ship,out var shuttleXform))
            return;
        var center=Vector2.Transform(_transform.GetWorldPosition(shuttleXform),transform);
        if(center.X<0||center.Y<0||center.X>PixelWidth||center.Y>PixelHeight)return;
        var heading=_transform.GetWorldRotation(ship).RotateVec(new Vector2(0,1));
        var tipVector=Vector2.TransformNormal(heading,transform);
        if(tipVector.LengthSquared()>0.001f)tipVector=Vector2.Normalize(tipVector);
        else tipVector=new Vector2(0,-1);
        var side=new Vector2(-tipVector.Y,tipVector.X);
        var gold=Color.FromHex("#F6CE64");
        handle.DrawCircle(center,13f,Color.FromHex("#FFE6A8").WithAlpha(0.88f),false);
        handle.DrawCircle(center,9f,gold.WithAlpha(0.45f),false);
        handle.DrawLine(center+tipVector*19f,center-side*9f,gold);
        handle.DrawLine(center+tipVector*19f,center+side*9f,gold);
        handle.DrawLine(center-side*9f,center+side*9f,gold);
        handle.DrawCircle(center,3f,Color.White,true);
    }
}

public sealed partial class ShuttleConsoleWindow
{
    public NavScreen AspisNav
    {
        get
        {
            NavContainer.AspisInitializeControls();
            return NavContainer;
        }
    }
}
