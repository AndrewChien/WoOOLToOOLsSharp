using System.Numerics;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.MapEditor.App;

public enum MapEditTool
{
    Select = 0,
    Pencil = 1,
    RectFill = 2,
    Stamp = 3,
    BlockedEditor = 4,
    Erase = 5,
}

public enum StampAnchorMode
{
    Center,
    TopLeft,
}

public enum StampSourceKind
{
    Path,
    Clipboard,
    MoveSelection,
}

public enum MapEditorSettingsSection
{
    Defaults = 0,
    Application,
    MapPaths,
    DataPaths,
    Assets,
    ClientParity,
    KeyBindings,
    Luminance,
}

public enum TextureSourceMode
{
    /// <summary>Load WPF TEX first, then fall back to SGL for missing packages (default).</summary>
    WpfSglFallback = 0,
    /// <summary>Load only WPF TEX archives.</summary>
    WpfOnly = 1,
    /// <summary>Load only SGL archives (including WPF-embedded SGL entries).</summary>
    SglOnly = 2,
}

public enum PrefabBrowserViewMode
{
    Thumbnails = 0,
    Details = 1,
}

public sealed class NamedPathEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class MapEditorState
{
    public bool RequestExit { get; set; }

    /// <summary>Whether MapEditor should restore previously opened documents on startup.</summary>
    public bool RestoreState { get; set; } = true;

    /// <summary>Previously opened document paths loaded from preferences (also used as a save snapshot).</summary>
    public List<string> RestoreOpenMapPaths { get; } = new();

    /// <summary>Active document index to restore on startup (loaded from preferences).</summary>
    public int RestoreActiveMapIndex { get; set; } = -1;

    /// <summary>If true, unload inactive clean tabs to reduce RAM usage (old: unload_inactive_tabs).</summary>
    public bool UnloadInactiveTabs { get; set; }

    public MapDocument? Map { get; set; }
    public string MapPath { get; set; } = string.Empty;
    public string MapLoadError { get; set; } = string.Empty;

    public bool ShowFileBrowserPanel { get; set; } = true;
    public bool ShowPrefabBrowserPanel { get; set; }
    public bool ShowInformationPanel { get; set; } = true;
    public bool ShowCellInspectorPanel { get; set; } = true;
    public string MapBrowserRootDirectory { get; set; } = string.Empty;
    public bool MapBrowserRecursive { get; set; } = true;
    public bool MapBrowserIncludePrefabs { get; set; } = true;
    public string MapBrowserFilter { get; set; } = string.Empty;
    public List<NamedPathEntry> MapPathEntries { get; } = new();
    public int SelectedMapPathEntryIndex { get; set; } = -1;

    public float PrefabThumbnailSize { get; set; } = 96.0f;
    public PrefabBrowserViewMode PrefabBrowserViewMode { get; set; } = PrefabBrowserViewMode.Thumbnails;

    public bool ShowObjectListPanel { get; set; } = true;
    public string ObjectListFilter { get; set; } = string.Empty;

    public bool ShowSceneTreePanel { get; set; } = true;
    public string SceneTreeFilter { get; set; } = string.Empty;

    public bool ShowConsolePanel { get; set; } = true;
    public bool ConsoleAutoScroll { get; set; } = true;

    public bool ShowSettingsWindow { get; set; }
    public MapEditorSettingsSection CurrentSettingsSection { get; set; } = MapEditorSettingsSection.Defaults;

    // --- Key Bindings (old settings.cfg: keybind.*) ---
    public MapEditorKeyBindings KeyBindings { get; } = new();

    /// <summary>Which key binding row is listening for a new key (-1 = none).</summary>
    public int KeyBindListeningIndex { get; set; } = -1;

    // --- Luminance-to-alpha settings (old settings.cfg: luminance_* / skip_luminance) ---
    public LuminanceSettings LuminanceSettings { get; set; } = new();
    public LuminanceSettings PendingLuminanceSettings { get; set; } = new();
    public bool SkipLuminanceToAlpha { get; set; }

    public MapCamera Camera { get; set; } = new();
    public bool CameraNeedsFit { get; set; } = true;
    public float ZoomStep { get; set; } = 0.1f;

    public MapLayer ViewLayer { get; set; } = MapLayer.Back;

    // --- Layer visibility (old: show*Layer) ---
    public bool ShowBackLayer { get; set; } = true;
    public bool ShowMiddleLayer { get; set; } = true;
    public bool ShowFloorLayer { get; set; } = true;
    public bool ShowUnderFrontLayer { get; set; } = true;
    public bool ShowFrontLayer { get; set; } = true;
    public bool ShowOverFrontLayer { get; set; } = true;
    public bool ShowDynamicSceneLayer { get; set; } = true;
    public bool ShowAttachedEffectsLayer { get; set; } = true;
    public bool ShowGrid { get; set; }
    public bool ShowTileFill { get; set; }
    public bool ShowBlockedOverlay { get; set; }

    // --- Grid & diagnostics overlays (old MapEditor: Grid / Blocked Overlay / Cell Highlights) ---
    public Vector4 GridColor { get; set; } = new(0.235f, 0.255f, 0.275f, 0.235f);
    public int GridThickness { get; set; } = 1; // 1-5 px
    public Vector4 BlockedOverlayColor { get; set; } = new(1.0f, 0.0f, 0.0f, 0.235f);

    public bool HighlightBackCells { get; set; }
    public bool HighlightMiddleCells { get; set; }
    public bool HighlightFrontCells { get; set; }
    public bool HighlightFloorCells { get; set; }
    public bool HighlightUnderFrontCells { get; set; }
    public bool HighlightOverFrontCells { get; set; }
    public bool HighlightCoastMaskCells { get; set; }
    public bool HighlightMissingTextureCells { get; set; }

    public Vector4 HighlightBackColor { get; set; } = new(0.0f, 0.5f, 1.0f, 0.25f);
    public Vector4 HighlightMiddleColor { get; set; } = new(0.0f, 1.0f, 0.0f, 0.25f);
    public Vector4 HighlightFrontColor { get; set; } = new(1.0f, 0.5f, 0.0f, 0.25f);
    public Vector4 HighlightFloorColor { get; set; } = new(0.0f, 1.0f, 1.0f, 0.25f);
    public Vector4 HighlightUnderFrontColor { get; set; } = new(1.0f, 1.0f, 0.0f, 0.25f);
    public Vector4 HighlightOverFrontColor { get; set; } = new(1.0f, 0.0f, 1.0f, 0.25f);
    public Vector4 HighlightCoastMaskColor { get; set; } = new(1.0f, 0.2f, 0.45f, 0.30f);
    public Vector4 HighlightMissingTextureColor { get; set; } = new(0.75f, 0.25f, 1.0f, 0.33f);

    // --- Minimap overlay (old parity: show_minimap / minimap_opacity) ---
    public bool ShowMinimapOverlay { get; set; } = true;

    /// <summary>Background opacity of the minimap overlay panel (0-1).</summary>
    public float MinimapOpacity { get; set; } = 0.85f;

    public UndoRedoStack History { get; set; } = new();

    public MapEditTool Tool { get; set; } = MapEditTool.Select;
    public MapLayer EditLayer { get; set; } = MapLayer.Back;
    public int PaintLibrary { get; set; } = 3001;
    public int PaintImage { get; set; } = 1;

    public string StampPath { get; set; } = string.Empty;
    public StampSourceKind StampSource { get; set; } = StampSourceKind.Path;
    public StampAnchorMode StampAnchor { get; set; } = StampAnchorMode.TopLeft;
    public bool StampOverwriteEmpty { get; set; }
    public bool StampApplyBack { get; set; } = true;
    public bool StampApplyMiddle { get; set; } = true;
    public bool StampApplyFront { get; set; } = true;
    public bool StampApplyUnderObject { get; set; } = true;
    public bool StampApplyOverObject { get; set; } = true;
    public bool StampApplyNearGround { get; set; } = true;
    public bool StampApplyBlocked { get; set; } = true;

    public bool MoveApplyBack { get; set; } = true;
    public bool MoveApplyMiddle { get; set; } = true;
    public bool MoveApplyFront { get; set; } = true;
    public bool MoveApplyUnderObject { get; set; } = true;
    public bool MoveApplyOverObject { get; set; } = true;
    public bool MoveApplyNearGround { get; set; } = true;
    public bool MoveApplyBlocked { get; set; } = true;

    public bool EraseApplyBack { get; set; } = true;
    public bool EraseApplyMiddle { get; set; } = true;
    public bool EraseApplyFront { get; set; } = true;
    public bool EraseApplyUnderObject { get; set; } = true;
    public bool EraseApplyOverObject { get; set; } = true;
    public bool EraseApplyNearGround { get; set; } = true;
    public bool EraseApplyBlocked { get; set; }

    public bool ShowMinimapExportPopup { get; set; }
    public string MinimapExportPath { get; set; } = string.Empty;
    public int MinimapScale { get; set; } = 4;
    public bool MinimapIncludeBack { get; set; } = true;
    public bool MinimapIncludeMiddle { get; set; } = true;
    public bool MinimapIncludeFloor { get; set; } = true;
    public bool MinimapIncludeUnderFront { get; set; } = true;
    public bool MinimapIncludeFront { get; set; } = true;
    public bool MinimapIncludeOverFront { get; set; } = true;
    public bool MinimapIncludeDynamicScene { get; set; }
    public bool MinimapIncludeAttachedEffects { get; set; }
    public string MinimapOverlayMapIdOverride { get; set; } = string.Empty;
    public string MinimapAttachedEffectsMapIdOverride { get; set; } = string.Empty;
    public string MinimapOverlayLayoutPath { get; set; } = string.Empty;
    public string MinimapAttachedEffectsLayoutPath { get; set; } = string.Empty;
    public long MinimapOverlayMaxDecompressedBytes { get; set; } = DynamicOverlayCodec.DefaultMaxDecompressedBytes;
    public bool MinimapSeparateLayerFiles { get; set; }
    public bool MinimapUseTextures { get; set; }
    public bool MinimapSuppressBorderCells { get; set; }
    public bool MinimapApplyCellTints { get; set; }
    public float MinimapTintStrength { get; set; } = 0.35f;
    public bool MinimapApplyCellHeightFlag { get; set; }
    public float MinimapCellHeightFlagOffset { get; set; } = 0.0f;
    public bool MinimapApplyObjectHeight { get; set; }
    public float MinimapObjectHeightScale { get; set; } = 0.0f;
    public bool MinimapApplyLuminanceToAlpha { get; set; } = true;
    public bool MinimapApplyLightingOverlay { get; set; } = true;
    public MapLightingSettings MinimapLighting { get; } = new();
    public int MinimapLightingOverlayMaxAlpha { get; set; } = 120;
    public bool MinimapIncludeLightSprites { get; set; } = true;

    public MinimapCropMode MinimapCropMode { get; set; } = MinimapCropMode.None;
    public int MinimapCropCellX { get; set; }
    public int MinimapCropCellY { get; set; }
    public int MinimapCropCellWidth { get; set; } = 1;
    public int MinimapCropCellHeight { get; set; } = 1;
    public int MinimapCropPixelX { get; set; }
    public int MinimapCropPixelY { get; set; }
    public int MinimapCropPixelWidth { get; set; }
    public int MinimapCropPixelHeight { get; set; }
    public int MinimapAutoCropPaddingCells { get; set; }

    public bool ShowMinimapBatchExportPopup { get; set; }
    public string MinimapBatchInputDirectory { get; set; } = string.Empty;
    public string MinimapBatchOutputDirectory { get; set; } = string.Empty;
    public bool MinimapBatchRecursive { get; set; } = true;
    public bool MinimapBatchOverwrite { get; set; }
    public bool MinimapBatchIncludeScaleTag { get; set; } = true;

    public string TextureRootDirectory { get; set; } = string.Empty;
    public TextureSourceMode TextureSourceMode { get; set; } = TextureSourceMode.WpfSglFallback;
    public bool CoastMaskPreferTex { get; set; } = true;
    public bool TextureScanRecursive { get; set; } = true;
    public List<NamedPathEntry> DataPathEntries { get; } = new();
    public int SelectedDataPathEntryIndex { get; set; } = -1;
    // 旧版 MapEditor 默认使用真实贴图渲染；无贴图库时会自动回退为占位色块/网格。
    public bool RenderUseTextures { get; set; } = true;
    public bool RenderAnimateTextures { get; set; }
    public float TextureAnimationFps { get; set; } = 8.0f;
    public bool TextureAnimationPerCellOffset { get; set; }
    public bool RenderApplyCellTints { get; set; }
    public float RenderTintStrength { get; set; } = 0.35f;
    public bool RenderWarnOnUnsupportedParityData { get; set; }
    public bool RenderSuppressBorderCells { get; set; }
    public bool RenderApplyCellHeightFlag { get; set; }
    public float RenderCellHeightFlagOffset { get; set; } = 0.0f;
    public bool RenderApplyObjectHeight { get; set; }
    public float RenderObjectHeightScale { get; set; } = 0.0f;
    public bool RenderApplyLightingOverlay { get; set; } = true;
    public MapLightingSettings RenderLighting { get; } = new();
    public int RenderLightingOverlayMaxAlpha { get; set; } = 120;
    public bool RenderIncludeLightSprites { get; set; } = true;
    // 旧 C++ TileCache 默认可容纳 32768 张纹理，且没有把整图视野压到几百张缓存。
    // 这里保持同量级容量，避免整图缩放时大量格子长期停留在占位色。
    public int TextureMaxCacheItems { get; set; } = 32768;
    public int TextureSubmitBudgetPerFrame { get; set; } = 2048;
    public int TextureCreateBudgetPerFrame { get; set; } = 256;
    public string TextureLastError { get; set; } = string.Empty;

    public int HoverCellX { get; set; } = -1;
    public int HoverCellY { get; set; } = -1;
    public int SelectedCellX { get; set; } = -1;
    public int SelectedCellY { get; set; } = -1;

    public bool HasSelection { get; set; }
    public int SelectionX0 { get; set; }
    public int SelectionY0 { get; set; }
    public int SelectionX1 { get; set; }
    public int SelectionY1 { get; set; }

    public string StatusMessage { get; set; } = "就绪";
}
