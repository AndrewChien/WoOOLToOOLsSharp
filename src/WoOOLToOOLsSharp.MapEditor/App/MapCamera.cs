using System;
using System.Numerics;

namespace WoOOLToOOLsSharp.MapEditor.App;

public sealed class MapCamera
{
    public float PanX { get; set; }
    public float PanY { get; set; }
    public float Zoom { get; set; } = 1.0f;

    public float MinZoom { get; set; } = 0.5f;
    public float MaxZoom { get; set; } = 8.0f;

    public void ClampZoom()
    {
        Zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
    }

    public Vector2 ScreenToWorld(Vector2 screen, Vector2 canvasScreenPos)
    {
        return screen - canvasScreenPos - new Vector2(PanX, PanY);
    }

    public Vector2 WorldToScreen(Vector2 world, Vector2 canvasScreenPos)
    {
        return canvasScreenPos + new Vector2(PanX, PanY) + world;
    }
}
