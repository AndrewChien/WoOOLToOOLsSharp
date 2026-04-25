using System;
using ImGuiNET;

namespace WoOOLToOOLsSharp.MapEditor.App;

[Flags]
public enum KeyModFlags
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
}

public struct KeyBinding
{
    public ImGuiKey Key { get; set; }
    public KeyModFlags Mods { get; set; }

    public KeyBinding(ImGuiKey key, KeyModFlags mods)
    {
        Key = key;
        Mods = mods;
    }
}

public sealed class MapEditorKeyBindings
{
    public KeyBinding ToolBlockedEditor { get; set; } = new(ImGuiKey.B, KeyModFlags.None);
    public KeyBinding ToolSelection { get; set; } = new(ImGuiKey.None, KeyModFlags.None);
    public KeyBinding ToolErase { get; set; } = new(ImGuiKey.E, KeyModFlags.None);
    public KeyBinding ToolStamp { get; set; } = new(ImGuiKey.T, KeyModFlags.None);
    public KeyBinding ToolTilePaint { get; set; } = new(ImGuiKey.P, KeyModFlags.None);
    public KeyBinding ToolCancel { get; set; } = new(ImGuiKey.Escape, KeyModFlags.None);
    public KeyBinding DeleteSelection { get; set; } = new(ImGuiKey.Delete, KeyModFlags.None);
    public KeyBinding Undo { get; set; } = new(ImGuiKey.Z, KeyModFlags.Ctrl);
    public KeyBinding Redo { get; set; } = new(ImGuiKey.Y, KeyModFlags.Ctrl);
    public KeyBinding Save { get; set; } = new(ImGuiKey.S, KeyModFlags.Ctrl);
    public KeyBinding ZoomIn { get; set; } = new(ImGuiKey.Equal, KeyModFlags.None);
    public KeyBinding ZoomOut { get; set; } = new(ImGuiKey.Minus, KeyModFlags.None);
    public KeyBinding ResetView { get; set; } = new(ImGuiKey.R, KeyModFlags.None);

    public void ResetToDefaults()
    {
        ToolBlockedEditor = new KeyBinding(ImGuiKey.B, KeyModFlags.None);
        ToolSelection = new KeyBinding(ImGuiKey.None, KeyModFlags.None);
        ToolErase = new KeyBinding(ImGuiKey.E, KeyModFlags.None);
        ToolStamp = new KeyBinding(ImGuiKey.T, KeyModFlags.None);
        ToolTilePaint = new KeyBinding(ImGuiKey.P, KeyModFlags.None);
        ToolCancel = new KeyBinding(ImGuiKey.Escape, KeyModFlags.None);
        DeleteSelection = new KeyBinding(ImGuiKey.Delete, KeyModFlags.None);
        Undo = new KeyBinding(ImGuiKey.Z, KeyModFlags.Ctrl);
        Redo = new KeyBinding(ImGuiKey.Y, KeyModFlags.Ctrl);
        Save = new KeyBinding(ImGuiKey.S, KeyModFlags.Ctrl);
        ZoomIn = new KeyBinding(ImGuiKey.Equal, KeyModFlags.None);
        ZoomOut = new KeyBinding(ImGuiKey.Minus, KeyModFlags.None);
        ResetView = new KeyBinding(ImGuiKey.R, KeyModFlags.None);
    }
}

