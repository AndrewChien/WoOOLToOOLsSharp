using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ImGuiNET;
using WoOOLToOOLsSharp.Rendering.Vulkan;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.EditorBridge;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;

namespace WoOOLToOOLsSharp.MapEditor.App;

public sealed class MapEditorApp : IVulkanApp
{
    private const float BaseCellWidth = 64.0f;
    private const float BaseCellHeight = 32.0f;

    private const string DragDropPayloadPrefabPath = "WOOOL_PREFAB_PATH";

    private static readonly int[] PrefabVersions = { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 };
    private const string PrefabVersionComboItems = "V1\0V2\0V3\0V5\0V6\0V7\0V8\0V9\0V10\0V11\0V12\0";

    private const byte FlagSmTiles = 0x02;
    private const byte FlagTiles = 0x04;
    private const byte FlagObject = 0x08;

    private const byte NmpFlagBlocked = 0x01;
    private const byte NmpFlagBorderCell = 0x04;
    private const byte NmpFlagUnderObject = 0x20;
    private const byte NmpFlagNearGround = 0x40;

    private const ushort DefaultSmTilesLibrary = 3001;
    private const ushort DefaultTilesLibrary = 3051;
    private const byte DefaultObjectLibrary = 5;

    private const ushort NmpExAttrOverObject = 0x0001;

    private const double PreferencesAutosaveCheckIntervalSeconds = 0.25;
    private const double PreferencesAutosaveDebounceSeconds = 0.35;
    private const double PreferencesAutosaveMinIntervalSeconds = 0.75;

    private readonly MapEditorState _state = new();
    private readonly List<MapEditorDocument> _documents = new();
    private int _activeDocumentIndex = -1;

    private VulkanRenderer? _renderer;
    private readonly MapTextureIndex _textureIndex = new();
    private AsyncTextureLoader? _textureLoader;
    private AsyncPrefabThumbnailLoader? _prefabThumbnailLoader;
    private Task<TextureScanResult>? _textureScanTask;
    private readonly Dictionary<string, TextureScanResult> _textureIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _isRestoringDocuments;
    private bool _queuedTextureRootSwitchQueued;
    private string _queuedTextureRootSwitchDataRoot = string.Empty;
    private string _queuedTextureRootSwitchReason = string.Empty;

    private Task<MapBrowserScanResult>? _mapBrowserScanTask;
    private MapBrowserScanResult? _mapBrowserScanResult;

    private bool _objectListDirty = true;
    private readonly List<ObjectListEntry> _objectListEntries = new();
    private ObjectListKey? _selectedObjectListKey;

    private bool _sceneTreeDirty = true;
    private MapDocument? _sceneTreeMap;
    private readonly List<SceneTreeObjectNode> _sceneTreeObjectNodes = new();
    private readonly List<SceneTreeGroupNode> _sceneTreeGroupNodes = new();
    private int _sceneTreeMaxInstancesPerRef = 200;

    private bool _requestCenterOnCell;
    private int _requestCenterCellX;
    private int _requestCenterCellY;

    private string _selectedBrowserPath = string.Empty;

    private string _selectedPrefabBrowserPath = string.Empty;
    private string _prefabBrowserFilter = string.Empty;
    private float _lastPrefabBrowserThumbSize = -1.0f;
    private readonly Dictionary<string, PrefabInfoCacheEntry> _prefabInfoCache = new(StringComparer.OrdinalIgnoreCase);

    private string _loadedStampPath = string.Empty;
    private MapDocument? _stampMap;
    private string _stampLoadError = string.Empty;

    private bool _requestResetDockLayout;
    private uint _dockspaceId;

    private bool _requestOpenByPathPopup;
    private string _openByPathText = string.Empty;
    private string _pendingOpenPath = string.Empty;

    private int _newInMemoryDocumentCounter;
    private bool _requestNewMapPopup;
    private int _newMapWidth = 200;
    private int _newMapHeight = 200;
    private int _newMapVersion = 12;

    private bool _requestNewPrefabPopup;
    private int _newPrefabWidth = 20;
    private int _newPrefabHeight = 20;
    private int _newPrefabVersion = 12;

    private enum FolderBrowseTarget
    {
        None = 0,
        MapBrowserRoot = 1,
        TextureRoot = 2,
        MapPathEntry = 3,
        DataPathEntry = 4,
    }

    private enum PendingFileDialogAction
    {
        None = 0,
        OpenMapByPath = 1,
        OpenAssetInContentEditor = 2,
        SaveAsMap = 3,
    }

    private enum TextureDrawStatus : byte
    {
        None = 0,
        TextureUnavailable = 1,
        Culled = 2,
        Drawn = 3,
    }

    private readonly LocalEditorBridge _editorBridge = new(EditorBridgeApp.MapEditor);
    private readonly MapEditorConsoleBackend _console = new();
    private string _consoleSearchFilter = string.Empty;
    private bool _consoleShowInfo = true;
    private bool _consoleShowWarnings = true;
    private bool _consoleShowErrors = true;
    private string _pendingTextureSettingsMigrationLog = string.Empty;
    private double _lastVisibleTextureMissLogTime;
    private string _lastVisibleTextureMissSignature = string.Empty;
    private readonly SimpleFileDialog _folderDialog = new();
    private readonly SimpleFileDialog _fileDialog = new();
    private FolderBrowseTarget _folderBrowseTarget;
    private int _folderBrowseEntryIndex = -1;
    private PendingFileDialogAction _pendingFileDialogAction;
    private bool _requestOpenInContentEditorPopup;
    private string _openInContentEditorPathText = string.Empty;
    private int _openInContentEditorImageIndex = -1;
    private bool _bridgeTextureReloadQueued;
    private string _bridgeTextureReloadDataPath = string.Empty;

    private bool _requestSaveAsPopup;
    private string _saveAsPathText = string.Empty;
    private string _pendingSaveAsPath = string.Empty;
    private bool _saveAsOverwriteConfirm;

    private bool _requestSaveSelectionAsPrefabPopup;
    private bool _saveSelectionAsPrefabOverwriteConfirm;
    private int _saveSelectionAsPrefabVersion = 12;
    private string _saveSelectionAsPrefabNameText = string.Empty;
    private string _saveSelectionAsPrefabError = string.Empty;

    private bool _requestCopyPrefabPopup;
    private bool _copyPrefabOverwriteConfirm;
    private string _copyPrefabSourcePath = string.Empty;
    private string _copyPrefabNameText = string.Empty;
    private string _copyPrefabError = string.Empty;

    private bool _requestSavePrefabAsPopup;
    private bool _savePrefabAsOverwriteConfirm;
    private string _savePrefabAsNameText = string.Empty;
    private string _savePrefabAsError = string.Empty;

    private MapDocument? _cellInspectorDraftMap;
    private int _cellInspectorDraftIndex = -1;
    private CellInspectorDraft _cellInspectorDraft;

    private PaintDragSession? _paintDrag;
    private RectFillDragSession? _rectFillDrag;
    private SelectionDragSession? _selectionDrag;
    private BlockedDragSession? _blockedDrag;

    private bool _mapCanvasMiddleDragPastThreshold;

    private MapDocument? _clipboardStamp;
    private MoveSelectionSession? _moveSelection;

    private Task<MinimapBatchExportResult>? _minimapBatchExportTask;
    private CancellationTokenSource? _minimapBatchExportCts;
    private readonly MinimapBatchExportProgress _minimapBatchExportProgress = new();

    private Task<MapResourceValidationReport>? _resourceValidationTask;
    private CancellationTokenSource? _resourceValidationCts;
    private MapResourceValidationReport? _resourceValidationReport;
    private string _resourceValidationError = string.Empty;
    private bool _resourceValidationValidateCoastComposite = true;
    private int _resourceValidationMaxSamplesPerIssue = 8;
    private int _resourceValidationMaxDisplayItems = 200;
    private string _resourceValidationFilter = string.Empty;
    private string _resourceValidationExportPath = string.Empty;

    private bool _loadedPreferences;
    private bool _restoredDocumentsFromPreferences;
    private string _lastSavedPreferencesText = string.Empty;
    private string _lastObservedPreferencesText = string.Empty;
    private double _preferencesLastChangeTime;
    private double _preferencesLastAutosaveTime;
    private double _preferencesLastAutosaveCheckTime;
    private string _preferencesAutosaveLastError = string.Empty;
    private double _preferencesAutosaveLastErrorTime;

    public bool RequestExit => _state.RequestExit;

    public void ConfigureImGui(VulkanRenderer renderer, ImGuiController controller)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        if (controller is null) throw new ArgumentNullException(nameof(controller));

        _dockspaceId = controller.DockspaceId;

        controller.BuildMenuBar = BuildMenuBar;
        controller.BuildDockedUi = BuildDockedUi;
        controller.BuildStatusBar = BuildStatusBar;

        _textureLoader ??= new AsyncTextureLoader(_renderer, _textureIndex);
        _prefabThumbnailLoader ??= new AsyncPrefabThumbnailLoader(_renderer, _textureIndex);

        LoadPreferencesOnce();

        if (!_console.Initialize(Environment.CurrentDirectory, out string consoleError) && !string.IsNullOrWhiteSpace(consoleError))
        {
            _state.StatusMessage = consoleError;
        }
        else
        {
            _console.Append(MapEditorConsoleLogLevel.Info, "Console", $"Session: {_console.CurrentSessionPath}");
            _textureLoader!.LogSink = (level, message) => _console.Append(level, "Texture", message);
            if (!string.IsNullOrWhiteSpace(_pendingTextureSettingsMigrationLog))
            {
                _console.Append(MapEditorConsoleLogLevel.Warning, "Preferences", _pendingTextureSettingsMigrationLog);
                _pendingTextureSettingsMigrationLog = string.Empty;
            }

            _console.Append(MapEditorConsoleLogLevel.Info, "Texture",
                $"纹理缓存设置：maxCache={_state.TextureMaxCacheItems} submit={_state.TextureSubmitBudgetPerFrame} create={_state.TextureCreateBudgetPerFrame}");
        }

        if (!_editorBridge.Initialize(out string bridgeError) && !string.IsNullOrWhiteSpace(bridgeError))
        {
            _state.StatusMessage = bridgeError;
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", bridgeError);
        }

        RestoreDocumentsFromPreferencesOnce();
    }

    public void Tick(GlfwInput input, float deltaSeconds)
    {
        _ = input;
        _ = deltaSeconds;

        ProcessTextureScanTask();
        ProcessMapBrowserScanTask();
        ProcessMinimapBatchExportTask();
        ProcessResourceValidationTask();

        if (_textureLoader is not null)
        {
            _textureLoader.MaxCacheItems = _state.TextureMaxCacheItems;
            _textureLoader.SubmitBudgetPerFrame = _state.TextureSubmitBudgetPerFrame;
            _textureLoader.CreateBudgetPerFrame = _state.TextureCreateBudgetPerFrame;
            _textureLoader.TickFrame();
        }

        _prefabThumbnailLoader?.TickFrame();

        _editorBridge.Tick();
        DrainEditorBridgeRequests();

        if (!string.IsNullOrWhiteSpace(_pendingOpenPath))
        {
            string path = _pendingOpenPath;
            _pendingOpenPath = string.Empty;
            TryLoadMap(path);
        }

        if (!string.IsNullOrWhiteSpace(_pendingSaveAsPath))
        {
            string path = _pendingSaveAsPath;
            _pendingSaveAsPath = string.Empty;
            TrySaveActiveDocumentToPath(path, updateDocumentIdentity: true);
        }
    }

    private void DrainEditorBridgeRequests()
    {
        if (!_editorBridge.Initialized)
        {
            return;
        }

        List<EditorBridgeRequest> requests = _editorBridge.DrainRequests();
        if (requests.Count == 0)
        {
            return;
        }

        for (int i = 0; i < requests.Count; i++)
        {
            EditorBridgeRequest req = requests[i];
            if (req is null)
            {
                continue;
            }

            switch (req.Kind)
            {
                case EditorBridgeRequestKind.OpenMap:
                    if (!string.IsNullOrWhiteSpace(req.Path))
                    {
                        _pendingOpenPath = req.Path;
                        _state.StatusMessage = "桥接：收到打开地图请求。";
                        _console.Append(MapEditorConsoleLogLevel.Info, "Bridge", $"收到打开地图请求：{req.Path}");
                    }
                    break;

                case EditorBridgeRequestKind.ReloadDataFolder:
                    _console.Append(MapEditorConsoleLogLevel.Info, "Bridge", string.IsNullOrWhiteSpace(req.Path)
                        ? "收到刷新贴图库请求（dataPath 为空）。"
                        : $"收到刷新贴图库请求：{req.Path}");
                    if (!string.IsNullOrWhiteSpace(req.ExtraPath))
                    {
                        string detail = req.ExtraPath.Trim();
                        detail = detail.Replace("\r\n", "\n", StringComparison.Ordinal);
                        foreach (string line in detail.Split('\n'))
                        {
                            string l = line?.TrimEnd() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(l))
                            {
                                continue;
                            }

                            _console.Append(MapEditorConsoleLogLevel.Info, "Downloader", l);
                        }
                    }
                    HandleReloadDataFolderBridgeRequest(req.Path);
                    break;

                default:
                    break;
            }
        }
    }

    private static string NormalizeBridgeDataFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string p = path.Trim();
        try
        {
            p = Path.GetFullPath(p);
        }
        catch
        {
            // ignore
        }

        p = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.ToLowerInvariant();
    }

    private void HandleReloadDataFolderBridgeRequest(string requestedDataPath)
    {
        string normalizedRequest = NormalizeBridgeDataFolderPath(requestedDataPath);
        string normalizedCurrent = NormalizeBridgeDataFolderPath(_state.TextureRootDirectory);

        if (!string.IsNullOrWhiteSpace(normalizedRequest)
            && !string.IsNullOrWhiteSpace(normalizedCurrent)
            && !string.Equals(normalizedRequest, normalizedCurrent, StringComparison.Ordinal))
        {
            _state.StatusMessage = $"桥接：收到刷新贴图库请求，但当前 Data Path 不匹配（请求={requestedDataPath}）。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", $"刷新贴图库请求被忽略：当前 Data Path 不匹配（请求={requestedDataPath} 当前={_state.TextureRootDirectory}）。");
            return;
        }

        if (_textureScanTask is not null && !_textureScanTask.IsCompleted)
        {
            _bridgeTextureReloadQueued = true;
            _bridgeTextureReloadDataPath = requestedDataPath ?? string.Empty;
            _state.StatusMessage = "桥接：收到刷新贴图库请求，等待当前扫描结束…";
            _console.Append(MapEditorConsoleLogLevel.Info, "Bridge", "刷新贴图库请求已排队（等待当前扫描结束）。");
            return;
        }

        string root = _state.TextureRootDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            _state.StatusMessage = "桥接：收到刷新贴图库请求，但当前 Data Path 为空（请先在“贴图库目录”中设置）。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", "刷新贴图库失败：当前 Data Path 为空。");
            return;
        }

        if (!Directory.Exists(root))
        {
            _state.StatusMessage = $"桥接：收到刷新贴图库请求，但目录不存在：{root}";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", $"刷新贴图库失败：目录不存在：{root}");
            return;
        }

        _state.TextureLastError = string.Empty;
        _textureScanTask = Task.Run(() => MapTextureIndex.ScanSglDirectory(root, _state.TextureScanRecursive));
        _state.StatusMessage = "桥接：开始重新扫描贴图库（SGL/WPF）…";
        _console.Append(MapEditorConsoleLogLevel.Info, "Bridge", $"开始重新扫描贴图库（SGL/WPF）：{root}");
    }

    public void Dispose()
    {
        TrySavePreferences();

        if (_renderer is not null)
        {
            for (int i = 0; i < _documents.Count; i++)
            {
                MapEditorDocument doc = _documents[i];
                if (doc.RuntimeMinimapTextureId != nint.Zero)
                {
                    _renderer.DestroyImGuiTexture(doc.RuntimeMinimapTextureId);
                    doc.RuntimeMinimapTextureId = nint.Zero;
                }

                doc.RuntimeMinimapBuildTask = null;
            }
        }

        if (_minimapBatchExportCts is not null)
        {
            try
            {
                _minimapBatchExportCts.Cancel();
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            _minimapBatchExportTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _minimapBatchExportCts?.Dispose();
        _minimapBatchExportCts = null;
        _minimapBatchExportTask = null;

        if (_resourceValidationCts is not null)
        {
            try
            {
                _resourceValidationCts.Cancel();
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            _resourceValidationTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _resourceValidationCts?.Dispose();
        _resourceValidationCts = null;
        _resourceValidationTask = null;

        _textureLoader?.Dispose();
        _prefabThumbnailLoader?.Dispose();
        _textureIndex.Dispose();
        _console.Dispose();
    }

    private static string GetPreferencesPath()
    {
        string currentPath = Path.Combine(Environment.CurrentDirectory, "map_editor_prefs.cfg");
        string executableDir = GetExecutableDirectory();
        string executablePath = Path.Combine(executableDir, "map_editor_prefs.cfg");

        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        if (!PathsEqual(currentPath, executablePath) && File.Exists(executablePath))
        {
            return executablePath;
        }

        string currentLegacy = Path.Combine(Environment.CurrentDirectory, "settings.cfg");
        string executableLegacy = Path.Combine(executableDir, "settings.cfg");
        if (!PathsEqual(currentPath, executablePath)
            && !File.Exists(currentPath)
            && !File.Exists(currentLegacy)
            && File.Exists(executableLegacy))
        {
            return executablePath;
        }

        // Default to the executable directory, matching OldProj's settings.cfg behavior.
        return executablePath;
    }

    private void LoadPreferencesOnce()
    {
        if (_loadedPreferences)
        {
            return;
        }

        _loadedPreferences = true;
        string prefsPath = GetPreferencesPath();

        try
        {
            if (File.Exists(prefsPath))
            {
                if (!MapEditorPreferences.TryLoad(prefsPath, _state, out string error) && !string.IsNullOrWhiteSpace(error))
                {
                    _state.StatusMessage = error;
                }

                UpgradeLegacyTextureLoaderDefaultsIfNeeded(prefsPath);
                UpgradeLegacyViewportOverlayDefaultsIfNeeded(prefsPath);

                return;
            }

            foreach (string legacyPath in GetLegacyPreferencesPaths(prefsPath))
            {
                if (!File.Exists(legacyPath))
                {
                    continue;
                }

                if (!MapEditorPreferences.TryLoadLegacy(legacyPath, _state, out string legacyError, out string legacyNote))
                {
                    if (!string.IsNullOrWhiteSpace(legacyError))
                    {
                        _state.StatusMessage = legacyError;
                    }

                    return;
                }

                if (!MapEditorPreferences.TrySave(prefsPath, _state, out string saveError))
                {
                    _state.StatusMessage = string.IsNullOrWhiteSpace(saveError)
                        ? legacyNote
                        : $"{legacyNote} 但写入新偏好失败：{saveError}";
                    return;
                }

                _state.StatusMessage = $"{legacyNote} 已自动写入 {prefsPath}。";
                return;
            }
        }
        finally
        {
            ApplyTextureIndexSettingsFromState(invalidateTextures: false);
            InitializePreferencesAutosaveSnapshot();
        }
    }

    private void ApplyTextureIndexSettingsFromState(bool invalidateTextures)
    {
        _textureIndex.TextureSourceMode = _state.TextureSourceMode;
        _textureIndex.CoastMaskPreferTex = _state.CoastMaskPreferTex;
        _textureIndex.SkipLuminanceToAlpha = _state.SkipLuminanceToAlpha;
        _textureIndex.LuminanceSettings = _state.LuminanceSettings;

        if (!invalidateTextures)
        {
            return;
        }

        _textureIndex.ClearRuntimeCaches();
        _textureLoader?.InvalidateAll();
        _prefabThumbnailLoader?.InvalidateAll();
        InvalidateAllRuntimeMinimaps();
    }

    private void UpgradeLegacyTextureLoaderDefaultsIfNeeded(string prefsPath)
    {
        if (_state.TextureMaxCacheItems != 256
            || _state.TextureSubmitBudgetPerFrame != 64
            || _state.TextureCreateBudgetPerFrame != 8)
        {
            return;
        }

        _state.TextureMaxCacheItems = 32768;
        _state.TextureSubmitBudgetPerFrame = 2048;
        _state.TextureCreateBudgetPerFrame = 256;

        string note = $"已自动升级旧版纹理缓存设置：{prefsPath}（256/64/8 -> 32768/2048/256）";
        _state.StatusMessage = note;
        _pendingTextureSettingsMigrationLog = note;

        if (!MapEditorPreferences.TrySave(prefsPath, _state, out string saveError) && !string.IsNullOrWhiteSpace(saveError))
        {
            _pendingTextureSettingsMigrationLog = $"{note}；写回失败：{saveError}";
        }
    }

    private void UpgradeLegacyViewportOverlayDefaultsIfNeeded(string prefsPath)
    {
        if (!_state.ShowGrid || !_state.ShowTileFill)
        {
            return;
        }

        if (_state.GridThickness != 1)
        {
            return;
        }

        Vector4 legacyGridColor = new(0.235f, 0.255f, 0.275f, 0.235f);
        if (!VectorNearlyEquals(_state.GridColor, legacyGridColor))
        {
            return;
        }

        _state.ShowGrid = false;
        _state.ShowTileFill = false;

        string note = $"已自动纠正旧版视图默认值：{prefsPath}（show_grid/show_tile_fill: 1 -> 0）";
        _state.StatusMessage = note;

        if (_console.Initialized)
        {
            _console.Append(MapEditorConsoleLogLevel.Warning, "Preferences", note);
        }

        if (!MapEditorPreferences.TrySave(prefsPath, _state, out string saveError) && !string.IsNullOrWhiteSpace(saveError))
        {
            string saveNote = $"{note}；写回失败：{saveError}";
            _state.StatusMessage = saveNote;
            if (_console.Initialized)
            {
                _console.Append(MapEditorConsoleLogLevel.Warning, "Preferences", saveNote);
            }
        }
    }

    private static bool VectorNearlyEquals(Vector4 a, Vector4 b)
    {
        const float Epsilon = 0.0001f;
        return Math.Abs(a.X - b.X) <= Epsilon
            && Math.Abs(a.Y - b.Y) <= Epsilon
            && Math.Abs(a.Z - b.Z) <= Epsilon
            && Math.Abs(a.W - b.W) <= Epsilon;
    }

    private void TrySavePreferences()
    {
        SnapshotRestoreStateForSave();

        string prefsPath = GetPreferencesPath();
        if (!MapEditorPreferences.TrySave(prefsPath, _state, out string error) && !string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine(error);
        }
    }

    private void InitializePreferencesAutosaveSnapshot()
    {
        _lastSavedPreferencesText = string.Empty;
        _lastObservedPreferencesText = string.Empty;
        _preferencesLastChangeTime = 0.0;
        _preferencesLastAutosaveTime = 0.0;
        _preferencesLastAutosaveCheckTime = 0.0;
        _preferencesAutosaveLastError = string.Empty;
        _preferencesAutosaveLastErrorTime = 0.0;

        if (MapEditorPreferences.TryBuildSaveText(_state, out string text, out _))
        {
            _lastSavedPreferencesText = text;
            _lastObservedPreferencesText = text;
        }
    }

    private void MaybeAutosavePreferences()
    {
        if (!_loadedPreferences)
        {
            return;
        }

        double now = ImGui.GetTime();
        if (_preferencesLastAutosaveCheckTime > 0.0
            && (now - _preferencesLastAutosaveCheckTime) < PreferencesAutosaveCheckIntervalSeconds)
        {
            return;
        }
        _preferencesLastAutosaveCheckTime = now;

        SnapshotRestoreStateForSave();

        if (!MapEditorPreferences.TryBuildSaveText(_state, out string text, out string buildError))
        {
            ReportPreferencesAutosaveError(buildError, now);
            return;
        }

        if (!string.Equals(text, _lastObservedPreferencesText, StringComparison.Ordinal))
        {
            _lastObservedPreferencesText = text;
            _preferencesLastChangeTime = now;
        }

        if (string.Equals(_lastObservedPreferencesText, _lastSavedPreferencesText, StringComparison.Ordinal))
        {
            return;
        }

        if ((now - _preferencesLastChangeTime) < PreferencesAutosaveDebounceSeconds)
        {
            return;
        }

        if (_preferencesLastAutosaveTime > 0.0
            && (now - _preferencesLastAutosaveTime) < PreferencesAutosaveMinIntervalSeconds)
        {
            return;
        }

        string prefsPath = GetPreferencesPath();
        if (!MapEditorPreferences.TrySave(prefsPath, _state, out string saveError))
        {
            ReportPreferencesAutosaveError(saveError, now);
            return;
        }

        _lastSavedPreferencesText = _lastObservedPreferencesText;
        _preferencesLastAutosaveTime = now;
        _preferencesAutosaveLastError = string.Empty;
    }

    private void ReportPreferencesAutosaveError(string error, double nowSeconds)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return;
        }

        string normalized = error.Trim();
        if (string.Equals(normalized, _preferencesAutosaveLastError, StringComparison.Ordinal)
            && (nowSeconds - _preferencesAutosaveLastErrorTime) < 5.0)
        {
            return;
        }

        _preferencesAutosaveLastError = normalized;
        _preferencesAutosaveLastErrorTime = nowSeconds;

        string msg = $"自动保存偏好设置失败：{normalized}";
        _state.StatusMessage = msg;
        if (_console.Initialized)
        {
            _console.Append(MapEditorConsoleLogLevel.Warning, "Preferences", msg);
        }
    }

    private void SnapshotRestoreStateForSave()
    {
        _state.RestoreOpenMapPaths.Clear();
        for (int i = 0; i < _documents.Count; i++)
        {
            string path = _documents[i].Path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            _state.RestoreOpenMapPaths.Add(path);
        }

        _state.RestoreActiveMapIndex = _activeDocumentIndex;
    }

    private void RestoreDocumentsFromPreferencesOnce()
    {
        if (_restoredDocumentsFromPreferences)
        {
            return;
        }

        _restoredDocumentsFromPreferences = true;

        if (!_state.RestoreState)
        {
            return;
        }

        if (_state.RestoreOpenMapPaths.Count == 0)
        {
            return;
        }

        if (_console.Initialized)
        {
            _console.Append(MapEditorConsoleLogLevel.Info, "Preferences", $"恢复上次打开的文档：{_state.RestoreOpenMapPaths.Count} 个");
        }

        _isRestoringDocuments = true;
        try
        {
            for (int i = 0; i < _state.RestoreOpenMapPaths.Count; i++)
            {
                string path = _state.RestoreOpenMapPaths[i]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                OpenOrActivateDocument(path);
            }
        }
        finally
        {
            _isRestoringDocuments = false;
        }

        if (_documents.Count > 0 && _state.RestoreActiveMapIndex >= 0)
        {
            int idx = Math.Clamp(_state.RestoreActiveMapIndex, 0, _documents.Count - 1);
            ActivateDocument(idx);
        }
    }

    private void TryLoadMap(string path)
    {
        OpenOrActivateDocument(path);
    }

    private static IEnumerable<string> GetLegacyPreferencesPaths(string prefsPath)
    {
        string currentLegacy = Path.Combine(Environment.CurrentDirectory, "settings.cfg");
        string executableLegacy = Path.Combine(GetExecutableDirectory(), "settings.cfg");
        string prefsFullPath = GetFullPathSafe(prefsPath);

        string currentLegacyFullPath = GetFullPathSafe(currentLegacy);
        if (!PathsEqual(prefsFullPath, currentLegacyFullPath))
        {
            yield return currentLegacy;
        }

        string executableLegacyFullPath = GetFullPathSafe(executableLegacy);
        if (!PathsEqual(prefsFullPath, executableLegacyFullPath)
            && !PathsEqual(currentLegacyFullPath, executableLegacyFullPath))
        {
            yield return executableLegacy;
        }
    }

    private static string GetExecutableDirectory()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(processPath));
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            return Path.GetFullPath(AppContext.BaseDirectory);
        }

        return Environment.CurrentDirectory;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(GetFullPathSafe(left), GetFullPathSafe(right), StringComparison.OrdinalIgnoreCase);

    private static string GetFullPathSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string GetMapTypeLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "文件";
        }

        if (NmpFileName.IsMapFile(path))
        {
            return "地图";
        }

        if (NmpFileName.IsPrefabFile(path))
        {
            return "预制体";
        }

        return "文件";
    }

    private static bool IsPrefabPath(string path)
    {
        return NmpFileName.IsPrefabFile(path);
    }

    private static bool TryInferDataRootFromMapPath(string mapPath, out string dataRoot)
    {
        dataRoot = string.Empty;

        if (string.IsNullOrWhiteSpace(mapPath))
        {
            return false;
        }

        string fullPath = mapPath.Trim();
        try
        {
            fullPath = Path.GetFullPath(fullPath);
        }
        catch
        {
            // ignore
        }

        string dir;
        try
        {
            dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        }
        catch
        {
            dir = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            return false;
        }

        try
        {
            DirectoryInfo? cur = new DirectoryInfo(dir);
            DirectoryInfo? mapDir = null;

            int guard = 0;
            while (cur is not null && guard++ < 24)
            {
                // Most game layouts: <DataRoot>\\map\\... -> DataRoot
                if (string.Equals(cur.Name, "data", StringComparison.OrdinalIgnoreCase))
                {
                    dataRoot = cur.FullName;
                    return true;
                }

                if (mapDir is null && string.Equals(cur.Name, "map", StringComparison.OrdinalIgnoreCase))
                {
                    mapDir = cur;
                }

                cur = cur.Parent;
            }

            if (mapDir?.Parent is not null)
            {
                dataRoot = mapDir.Parent.FullName;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private void MaybeAutoAddInitialDataPathForDocument(MapEditorDocument doc, string logReason)
    {
        if (doc is null)
        {
            return;
        }

        // Old editor users often expect: open map -> textures immediately work.
        // If the user hasn't configured any Data Paths yet, try to infer a reasonable data root from the map path.
        if (_state.DataPathEntries.Count > 0)
        {
            return;
        }

        string path = doc.Path?.Trim() ?? string.Empty;
        if (!IsFileSystemPath(path))
        {
            return;
        }

        if (!TryInferDataRootFromMapPath(path, out string dataRoot))
        {
            return;
        }

        dataRoot = dataRoot?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
        {
            return;
        }

        string displayName = (doc.PreferredDataPathEntryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = SuggestNamedPathDisplayName(dataRoot, "Data Path", ordinal: 1);
        }

        _state.DataPathEntries.Add(new NamedPathEntry
        {
            DisplayName = displayName,
            Path = dataRoot,
        });
        _state.SelectedDataPathEntryIndex = 0;
        _state.TextureRootDirectory = dataRoot;
        doc.PreferredDataPathEntryName = displayName;

        // Align with old default behavior: prefer textured rendering when available.
        _state.RenderUseTextures = true;

        _state.StatusMessage = $"已自动设置 Data Path：{displayName} -> {dataRoot}";
        if (_console.Initialized)
        {
            _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"已自动从地图路径推断 Data Path（{logReason}）：{displayName} -> {dataRoot}");
        }
    }

    private bool IsActiveDocumentPrefab()
    {
        MapEditorDocument? doc = GetActiveDocument();
        return doc is not null && doc.IsPrefabDocument;
    }

    private bool IsActiveDocumentReadOnly()
    {
        MapEditorDocument? doc = GetActiveDocument();
        return doc is not null && doc.IsReadOnly;
    }

    private string GetActiveDocumentTypeLabel()
    {
        MapEditorDocument? doc = GetActiveDocument();
        if (doc is null)
        {
            return "文件";
        }

        return doc.IsPrefabDocument ? "预制体" : "地图";
    }

    private static string NormalizeDocumentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        if (LocalEditorBridge.ParseEditorBridgeWpfPath(trimmed, out string archivePath, out string entryPath))
        {
            string resolvedArchivePath = archivePath;
            try
            {
                resolvedArchivePath = Path.GetFullPath(archivePath);
            }
            catch
            {
                // ignore
            }

            return LocalEditorBridge.MakeEditorBridgeWpfPath(resolvedArchivePath, entryPath);
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string GetDefaultDisplayNameForPath(string normalizedPath, bool isPrefabDocument)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return isPrefabDocument ? "New Prefab" : "New Map";
        }

        if (MapDocument.TryParseWpfSyntheticPath(normalizedPath, out _, out string entryPath))
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(entryPath);
                return string.IsNullOrWhiteSpace(name) ? entryPath : name;
            }
            catch
            {
                return entryPath;
            }
        }

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(normalizedPath);
            return string.IsNullOrWhiteSpace(fileName) ? normalizedPath : fileName;
        }
        catch
        {
            return normalizedPath;
        }
    }

    private void LoadFolderMetaForDocument(MapEditorDocument doc)
    {
        if (doc is null)
        {
            return;
        }

        string normalizedPath = doc.Path?.Trim() ?? string.Empty;
        doc.DisplayName = GetDefaultDisplayNameForPath(normalizedPath, doc.IsPrefabDocument);
        doc.PreferredDataPathEntryName = string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (MapDocument.TryParseWpfSyntheticPath(normalizedPath, out _, out _))
        {
            return;
        }

        if (FolderMetaCodec.TryGetEntryForFilePath(normalizedPath, out FolderMetaEntry entry, out _))
        {
            if (!string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                doc.DisplayName = entry.DisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(entry.DataFolderName))
            {
                doc.PreferredDataPathEntryName = entry.DataFolderName.Trim();
            }
        }
    }

    private static bool IsFileSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (MapDocument.TryParseWpfSyntheticPath(path, out _, out _))
        {
            return false;
        }

        if (path.StartsWith("mem:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void PersistFolderMetaForDocument(MapEditorDocument doc, string logReason)
    {
        if (doc is null)
        {
            return;
        }

        string path = doc.Path?.Trim() ?? string.Empty;
        if (!IsFileSystemPath(path))
        {
            return;
        }

        string displayName = doc.DisplayName?.Trim() ?? string.Empty;
        string dataFolderName = doc.PreferredDataPathEntryName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = GetDefaultDisplayNameForPath(path, doc.IsPrefabDocument);
        }

        if (!FolderMetaCodec.TryUpsertFileEntry(path, displayName, dataFolderName, out string error))
        {
            if (_console.Initialized)
            {
                string msg = string.IsNullOrWhiteSpace(error)
                    ? $"写入 .meta 失败（{logReason}）：{path}"
                    : $"写入 .meta 失败（{logReason}）：{path} | {error}";
                _console.Append(MapEditorConsoleLogLevel.Warning, "Meta", msg);
            }
        }
    }

    private void PersistFolderMetaForFile(string filePath, string displayName, string dataFolderName, string logReason)
    {
        string path = filePath?.Trim() ?? string.Empty;
        if (!IsFileSystemPath(path))
        {
            return;
        }

        string dn = displayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dn))
        {
            dn = GetDefaultDisplayNameForPath(path, isPrefabDocument: NmpFileName.IsPrefabFile(path));
        }

        string df = dataFolderName?.Trim() ?? string.Empty;

        if (!FolderMetaCodec.TryUpsertFileEntry(path, dn, df, out string error))
        {
            if (_console.Initialized)
            {
                string msg = string.IsNullOrWhiteSpace(error)
                    ? $"写入 .meta 失败（{logReason}）：{path}"
                    : $"写入 .meta 失败（{logReason}）：{path} | {error}";
                _console.Append(MapEditorConsoleLogLevel.Warning, "Meta", msg);
            }
        }
    }

    private int FindDataPathEntryIndexByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return -1;
        }

        string target = displayName.Trim();
        for (int i = 0; i < _state.DataPathEntries.Count; i++)
        {
            string name = (_state.DataPathEntries[i].DisplayName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void ApplyDocumentPreferredDataPath(MapEditorDocument doc, bool startScan, string reason)
    {
        if (doc is null)
        {
            return;
        }

        string preferredName = (doc.PreferredDataPathEntryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(preferredName))
        {
            string currentName = GetSelectedDataPathDisplayName();
            if (!string.IsNullOrWhiteSpace(currentName))
            {
                doc.PreferredDataPathEntryName = currentName;
            }
            return;
        }

        int idx = FindDataPathEntryIndexByDisplayName(preferredName);
        if (idx < 0 || idx >= _state.DataPathEntries.Count)
        {
            return;
        }

        NamedPathEntry entry = _state.DataPathEntries[idx];
        string root = entry.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        string desiredKey = NormalizeBridgeDataFolderPath(root);
        string currentKey = NormalizeBridgeDataFolderPath(_state.TextureRootDirectory);
        bool keyChanged = !string.IsNullOrWhiteSpace(desiredKey)
            && !string.Equals(desiredKey, currentKey, StringComparison.Ordinal);

        if (idx != _state.SelectedDataPathEntryIndex)
        {
            _state.SelectedDataPathEntryIndex = idx;
        }

        if (keyChanged)
        {
            _state.TextureRootDirectory = root;
        }

        if ((keyChanged || idx != _state.SelectedDataPathEntryIndex) && _console.Initialized)
        {
            _console.Append(MapEditorConsoleLogLevel.Info, "Meta", $"Data Path 切换（{reason}）：{entry.DisplayName} -> {root}");
        }

        if (!startScan)
        {
            return;
        }

        RequestTextureRootSwitch(root, $"Data Path（{reason}）");
    }

    private void RequestTextureRootSwitch(string rootDirectory, string reason)
    {
        string root = rootDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        string desiredKey = NormalizeBridgeDataFolderPath(root);
        string currentKey = NormalizeBridgeDataFolderPath(_textureIndex.RootDirectory);
        if (!string.IsNullOrWhiteSpace(desiredKey)
            && string.Equals(desiredKey, currentKey, StringComparison.Ordinal)
            && _textureIndex.IsReady)
        {
            return;
        }

        if (_textureScanTask is not null && !_textureScanTask.IsCompleted)
        {
            _queuedTextureRootSwitchQueued = true;
            _queuedTextureRootSwitchDataRoot = root;
            _queuedTextureRootSwitchReason = reason ?? string.Empty;
            _state.StatusMessage = $"贴图库切换已排队（{reason}，等待当前扫描结束…）";
            return;
        }

        if (!string.IsNullOrWhiteSpace(desiredKey)
            && _textureIndexCache.TryGetValue(desiredKey, out TextureScanResult? cached)
            && cached is not null
            && cached.Ok)
        {
            ApplyTextureScanResult(cached, source: $"cache:{reason}");
            return;
        }

        if (!Directory.Exists(root))
        {
            _state.TextureLastError = $"贴图库目录不存在：{root}";
            _state.StatusMessage = _state.TextureLastError;
            _console.Append(MapEditorConsoleLogLevel.Warning, "Texture", _state.TextureLastError);
            return;
        }

        _state.TextureLastError = string.Empty;
        _textureScanTask = Task.Run(() => MapTextureIndex.ScanSglDirectory(root, _state.TextureScanRecursive));
        _state.StatusMessage = $"开始扫描贴图库（{reason}）…";
        _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"开始扫描贴图库（{reason}）：{root}");
    }

    private void ApplyTextureScanResult(TextureScanResult result, string source)
    {
        if (result is null || !result.Ok)
        {
            return;
        }

        string cacheKey = NormalizeBridgeDataFolderPath(result.RootDirectory);
        if (!string.IsNullOrWhiteSpace(cacheKey))
        {
            _textureIndexCache[cacheKey] = result;
        }

        _textureIndex.ApplyIndex(
            result.RootDirectory,
            result.PackageToStandaloneSglPath,
            result.PackageToWpfSglSource,
            result.PackageToWpfTex);
        _textureLoader?.InvalidateAll();
        _prefabThumbnailLoader?.InvalidateAll();
        InvalidateAllRuntimeMinimaps();

        // 旧版 C++ 编辑器在贴图库可用后会直接进入纹理渲染状态，没有独立的持久化“只看占位格子”模式。
        // 这里恢复同样的用户体验，避免历史偏好或“重置贴图库”后的 false 状态让地图长期只显示占位块。
        _state.RenderUseTextures = true;
        _state.TextureLastError = string.Empty;
        _state.StatusMessage = $"贴图库就绪：{_textureIndex.PackageCount} 个 package";
        _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"贴图库就绪：packages={_textureIndex.PackageCount} root={result.RootDirectory} source={source}");
    }

    private int FindDocumentIndexByNormalizedPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return -1;
        }

        for (int i = 0; i < _documents.Count; i++)
        {
            if (string.Equals(_documents[i].NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private MapEditorDocument? GetActiveDocument()
    {
        if (_activeDocumentIndex < 0 || _activeDocumentIndex >= _documents.Count)
        {
            return null;
        }

        return _documents[_activeDocumentIndex];
    }

    private string GetSelectedDataPathDisplayName()
    {
        int idx = _state.SelectedDataPathEntryIndex;
        if (idx < 0 || idx >= _state.DataPathEntries.Count)
        {
            return string.Empty;
        }

        return (_state.DataPathEntries[idx].DisplayName ?? string.Empty).Trim();
    }

    private string GetPreferredDataFolderNameForFile(string filePath)
    {
        string path = filePath?.Trim() ?? string.Empty;
        if (!IsFileSystemPath(path))
        {
            return GetSelectedDataPathDisplayName();
        }

        if (FolderMetaCodec.TryGetEntryForFilePath(path, out FolderMetaEntry entry, out _)
            && !string.IsNullOrWhiteSpace(entry.DataFolderName))
        {
            return entry.DataFolderName.Trim();
        }

        return GetSelectedDataPathDisplayName();
    }

    private void SaveActiveDocumentState()
    {
        MapEditorDocument? doc = GetActiveDocument();
        if (doc is null)
        {
            return;
        }

        doc.Map = _state.Map;
        doc.Path = _state.MapPath ?? string.Empty;
        doc.MapLoadError = _state.MapLoadError ?? string.Empty;
        doc.PreferredDataPathEntryName = GetSelectedDataPathDisplayName();
        doc.CameraNeedsFit = _state.CameraNeedsFit;
        doc.HoverCellX = _state.HoverCellX;
        doc.HoverCellY = _state.HoverCellY;
        doc.SelectedCellX = _state.SelectedCellX;
        doc.SelectedCellY = _state.SelectedCellY;
        doc.HasSelection = _state.HasSelection;
        doc.SelectionX0 = _state.SelectionX0;
        doc.SelectionY0 = _state.SelectionY0;
        doc.SelectionX1 = _state.SelectionX1;
        doc.SelectionY1 = _state.SelectionY1;
    }

    private void ActivateDocument(int index)
    {
        if (index < 0 || index >= _documents.Count)
        {
            return;
        }

        if (_activeDocumentIndex == index)
        {
            return;
        }

        ClearMoveSelectionSession(restorePreviousStampSource: true, restoreTool: false, updateStatus: false, statusMessage: null);
        SaveActiveDocumentState();

        _activeDocumentIndex = index;
        MapEditorDocument doc = _documents[index];

        _state.Map = doc.Map;
        _state.MapPath = doc.Path ?? string.Empty;
        _state.MapLoadError = doc.MapLoadError ?? string.Empty;
        _state.History = doc.History;
        _state.Camera = doc.Camera;
        _state.CameraNeedsFit = doc.CameraNeedsFit;

        _state.HoverCellX = doc.HoverCellX;
        _state.HoverCellY = doc.HoverCellY;
        _state.SelectedCellX = doc.SelectedCellX;
        _state.SelectedCellY = doc.SelectedCellY;
        _state.HasSelection = doc.HasSelection;
        _state.SelectionX0 = doc.SelectionX0;
        _state.SelectionY0 = doc.SelectionY0;
        _state.SelectionX1 = doc.SelectionX1;
        _state.SelectionY1 = doc.SelectionY1;

        _paintDrag = null;
        _rectFillDrag = null;
        _selectionDrag = null;
        _objectListDirty = true;
        _sceneTreeDirty = true;
        _objectListEntries.Clear();
        _selectedObjectListKey = null;
        _requestCenterOnCell = false;

        if (_console.Initialized)
        {
            string label = string.IsNullOrWhiteSpace(doc.Path) ? doc.NormalizedPath : doc.Path;
            _console.Append(MapEditorConsoleLogLevel.Info, "Map",
                $"切换标签页：{label} cache={_textureLoader?.CachedCount ?? 0} pending={_textureLoader?.PendingCount ?? 0} errors={_textureLoader?.ErrorCount ?? 0}");
        }

        EnsureActiveDocumentLoaded();
        ApplyDocumentPreferredDataPath(doc, startScan: !_isRestoringDocuments, reason: "切换标签页");
    }

    private void EnsureActiveDocumentLoaded()
    {
        MapEditorDocument? doc = GetActiveDocument();
        if (doc is null)
        {
            return;
        }

        if (doc.Map is not null)
        {
            return;
        }

        // Only auto-reload tabs that were unloaded for memory reasons.
        if (!string.IsNullOrWhiteSpace(doc.MapLoadError))
        {
            return;
        }

        string path = doc.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            doc.MapLoadError = "无法重新加载：文档没有保存路径。";
            _state.Map = null;
            _state.MapLoadError = doc.MapLoadError;
            return;
        }

        _state.StatusMessage = $"正在重新加载{GetMapTypeLabel(path)}...";
        _console.Append(MapEditorConsoleLogLevel.Info, "Memory", $"正在重新加载标签页数据：{path}");

        if (MapDocument.TryLoad(path, out MapDocument? map, out string error))
        {
            doc.Map = map;
            doc.MapLoadError = string.Empty;
            doc.History.Clear();
            doc.History.MarkSaved();

            // Selection state depends on cell coordinates; reset.
            doc.HoverCellX = -1;
            doc.HoverCellY = -1;
            doc.SelectedCellX = -1;
            doc.SelectedCellY = -1;
            doc.HasSelection = false;
            doc.SelectionX0 = 0;
            doc.SelectionY0 = 0;
            doc.SelectionX1 = 0;
            doc.SelectionY1 = 0;

            doc.RuntimeMinimapHasSettingsSnapshot = false;
            doc.RuntimeMinimapLastSettingsHash = 0;
            InvalidateRuntimeMinimap(doc);

            _state.Map = doc.Map;
            _state.MapLoadError = string.Empty;
            _objectListDirty = true;
            _sceneTreeDirty = true;

            if (map is not null)
            {
                _state.StatusMessage = $"已重新加载{GetMapTypeLabel(path)}：{path}";
                _console.Append(MapEditorConsoleLogLevel.Info, "Memory", $"已重新加载：{path}（{map.Width}x{map.Height}）");
            }
        }
        else
        {
            doc.Map = null;
            doc.MapLoadError = error;
            _state.Map = null;
            _state.MapLoadError = error;
            _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? $"{GetMapTypeLabel(path)}加载失败" : $"{GetMapTypeLabel(path)}加载失败：{error}";
            _console.Append(MapEditorConsoleLogLevel.Error, "Map", string.IsNullOrWhiteSpace(error)
                ? $"重新加载失败：{path}"
                : $"重新加载失败：{path} | {error}");
        }
    }

    private static bool CanUnloadDocumentContents(MapEditorDocument doc)
    {
        if (doc is null || doc.Map is null)
        {
            return false;
        }

        if (!doc.History.IsAtSavePoint)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(doc.MapLoadError))
        {
            return false;
        }

        string path = doc.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return true;
    }

    private void UnloadDocumentContents(MapEditorDocument doc)
    {
        if (!CanUnloadDocumentContents(doc))
        {
            return;
        }

        if (_renderer is not null && doc.RuntimeMinimapTextureId != nint.Zero)
        {
            _renderer.DestroyImGuiTexture(doc.RuntimeMinimapTextureId);
            doc.RuntimeMinimapTextureId = nint.Zero;
        }

        doc.RuntimeMinimapBuildTask = null;
        doc.RuntimeMinimapHasSettingsSnapshot = false;
        doc.RuntimeMinimapLastSettingsHash = 0;
        doc.IsDraggingMinimap = false;

        doc.Map = null;
        doc.MapLoadError = string.Empty;

        doc.History.Clear();
        doc.HoverCellX = -1;
        doc.HoverCellY = -1;
        doc.SelectedCellX = -1;
        doc.SelectedCellY = -1;
        doc.HasSelection = false;
        doc.SelectionX0 = 0;
        doc.SelectionY0 = 0;
        doc.SelectionX1 = 0;
        doc.SelectionY1 = 0;

        InvalidateRuntimeMinimap(doc);

        string label = string.IsNullOrWhiteSpace(doc.Path) ? doc.NormalizedPath : doc.Path;
        _console.Append(MapEditorConsoleLogLevel.Info, "Memory", $"已卸载未激活标签页：{label}");
    }

    private void UnloadInactiveTabsIfEnabled()
    {
        if (!_state.UnloadInactiveTabs)
        {
            return;
        }

        if (_documents.Count <= 0)
        {
            return;
        }

        if (_activeDocumentIndex < 0 || _activeDocumentIndex >= _documents.Count)
        {
            return;
        }

        for (int i = 0; i < _documents.Count; i++)
        {
            if (i == _activeDocumentIndex)
            {
                continue;
            }

            UnloadDocumentContents(_documents[i]);
        }
    }

    private void CloseDocument(int index)
    {
        if (index < 0 || index >= _documents.Count)
        {
            return;
        }

        bool wasActive = index == _activeDocumentIndex;
        if (wasActive)
        {
            ClearMoveSelectionSession(restorePreviousStampSource: true, restoreTool: false, updateStatus: false, statusMessage: null);
        }

        if (wasActive)
        {
            SaveActiveDocumentState();
        }

        MapEditorDocument closing = _documents[index];
        if (_renderer is not null && closing.RuntimeMinimapTextureId != nint.Zero)
        {
            _renderer.DestroyImGuiTexture(closing.RuntimeMinimapTextureId);
            closing.RuntimeMinimapTextureId = nint.Zero;
        }
        closing.RuntimeMinimapBuildTask = null;

        _documents.RemoveAt(index);

        if (_documents.Count <= 0)
        {
            _activeDocumentIndex = -1;
            ClearMoveSelectionSession(restorePreviousStampSource: true, restoreTool: false, updateStatus: false, statusMessage: null);

            _state.Map = null;
            _state.MapPath = string.Empty;
            _state.MapLoadError = string.Empty;
            _state.History = new UndoRedoStack();
            _state.Camera = new MapCamera();
            _state.CameraNeedsFit = true;

            _state.HoverCellX = -1;
            _state.HoverCellY = -1;
            _state.SelectedCellX = -1;
            _state.SelectedCellY = -1;
            _state.HasSelection = false;
            _state.SelectionX0 = 0;
            _state.SelectionY0 = 0;
            _state.SelectionX1 = 0;
            _state.SelectionY1 = 0;

            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _objectListDirty = true;
            _sceneTreeDirty = true;
            _objectListEntries.Clear();
            _selectedObjectListKey = null;
            _requestCenterOnCell = false;
            return;
        }

        if (index < _activeDocumentIndex)
        {
            _activeDocumentIndex--;
        }

        if (wasActive)
        {
            _activeDocumentIndex = Math.Clamp(_activeDocumentIndex, 0, _documents.Count - 1);
            MapEditorDocument doc = _documents[_activeDocumentIndex];

            _state.Map = doc.Map;
            _state.MapPath = doc.Path ?? string.Empty;
            _state.MapLoadError = doc.MapLoadError ?? string.Empty;
            _state.History = doc.History;
            _state.Camera = doc.Camera;
            _state.CameraNeedsFit = doc.CameraNeedsFit;

            _state.HoverCellX = doc.HoverCellX;
            _state.HoverCellY = doc.HoverCellY;
            _state.SelectedCellX = doc.SelectedCellX;
            _state.SelectedCellY = doc.SelectedCellY;
            _state.HasSelection = doc.HasSelection;
            _state.SelectionX0 = doc.SelectionX0;
            _state.SelectionY0 = doc.SelectionY0;
            _state.SelectionX1 = doc.SelectionX1;
            _state.SelectionY1 = doc.SelectionY1;

            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _objectListDirty = true;
            _sceneTreeDirty = true;
            _objectListEntries.Clear();
            _selectedObjectListKey = null;
            _requestCenterOnCell = false;
        }
    }

    private void OpenOrActivateDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _state.StatusMessage = "打开失败：路径为空。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Map", "打开失败：路径为空。");
            return;
        }

        string normalized = NormalizeDocumentPath(path);
        int existing = FindDocumentIndexByNormalizedPath(normalized);
        if (existing >= 0)
        {
            ActivateDocument(existing);
            _state.StatusMessage = $"已切换{GetMapTypeLabel(_state.MapPath)}：{_state.MapPath}";
            _console.Append(MapEditorConsoleLogLevel.Info, "Map", $"已切换已打开文档：{normalized}");
            return;
        }

        _state.MapLoadError = string.Empty;
        _state.StatusMessage = $"正在加载{GetMapTypeLabel(path)}...";
        _console.Append(MapEditorConsoleLogLevel.Info, "Map", $"正在加载{GetMapTypeLabel(path)}：{normalized}");

        var doc = new MapEditorDocument(normalized)
        {
            Path = normalized,
            IsPrefabDocument = NmpFileName.IsPrefabFile(path),
            IsReadOnly = MapDocument.TryParseWpfSyntheticPath(normalized, out _, out _),
            CameraNeedsFit = true,
            HoverCellX = -1,
            HoverCellY = -1,
            SelectedCellX = -1,
            SelectedCellY = -1,
            HasSelection = false,
            SelectionX0 = 0,
            SelectionY0 = 0,
            SelectionX1 = 0,
            SelectionY1 = 0,
        };

        LoadFolderMetaForDocument(doc);
        if (string.IsNullOrWhiteSpace(doc.PreferredDataPathEntryName))
        {
            doc.PreferredDataPathEntryName = GetSelectedDataPathDisplayName();
        }

        MaybeAutoAddInitialDataPathForDocument(doc, logReason: "打开文档");

        if (MapDocument.TryLoad(path, out MapDocument? map, out string error))
        {
            doc.Map = map;
            doc.MapLoadError = string.Empty;
        }
        else
        {
            doc.Map = null;
            doc.MapLoadError = error;
        }

        _documents.Add(doc);
        ActivateDocument(_documents.Count - 1);

        _state.StatusMessage = doc.Map is not null
            ? $"已加载{GetMapTypeLabel(doc.Path)}：{doc.Path}"
            : $"{GetMapTypeLabel(path)}加载失败";

        if (doc.Map is not null)
        {
            _console.Append(MapEditorConsoleLogLevel.Info, "Map", $"已加载{GetMapTypeLabel(doc.Path)}：{doc.Path}（{doc.Map.Width}x{doc.Map.Height}）");
        }
        else
        {
            _console.Append(MapEditorConsoleLogLevel.Error, "Map", string.IsNullOrWhiteSpace(doc.MapLoadError)
                ? $"加载失败：{doc.Path}"
                : $"加载失败：{doc.Path} | {doc.MapLoadError}");
        }
    }

    private void TrySaveActiveDocument()
    {
        if (_state.Map is null)
        {
            _state.StatusMessage = "保存失败：未加载地图。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Map", "保存失败：未加载地图。");
            return;
        }

        string path = _state.MapPath?.Trim() ?? string.Empty;
        if (IsActiveDocumentReadOnly() || string.IsNullOrWhiteSpace(path))
        {
            if (IsActiveDocumentPrefab())
            {
                BeginSavePrefabAsPopup();
            }
            else
            {
                _requestSaveAsPopup = true;
            }
            return;
        }

        TrySaveActiveDocumentToPath(path, updateDocumentIdentity: false);
    }

    private bool TrySaveActiveDocumentToPath(string path, bool updateDocumentIdentity)
    {
        if (_state.Map is null)
        {
            _state.StatusMessage = "保存失败：未加载地图。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Map", "保存失败：未加载地图。");
            return false;
        }

        path = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _state.StatusMessage = "保存失败：路径为空。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Map", "保存失败：路径为空。");
            return false;
        }

        string normalized = NormalizeDocumentPath(path);
        if (updateDocumentIdentity)
        {
            int existing = FindDocumentIndexByNormalizedPath(normalized);
            if (existing >= 0 && existing != _activeDocumentIndex)
            {
                _state.StatusMessage = $"另存为失败：目标路径已在其它标签页打开：{normalized}";
                _console.Append(MapEditorConsoleLogLevel.Warning, "Map", $"另存为失败：目标路径已在其它标签页打开：{normalized}");
                return false;
            }
        }

        MapDocument map = _state.Map;
        NmpMapInfo info = map.Info;
        var infoForWrite = new NmpMapInfo
        {
            Path = normalized,
            HeaderSize = info.HeaderSize,
            Version = info.Version,
            Width = info.Width,
            Height = info.Height,
            DataOffset = info.DataOffset,
        };

        if (!NmpCodec.WriteNmpMapData(normalized, infoForWrite, map.Cells, out string error))
        {
            _state.StatusMessage = $"保存失败：{error}";
            _console.Append(MapEditorConsoleLogLevel.Error, "Map", string.IsNullOrWhiteSpace(error)
                ? $"保存失败：{normalized}"
                : $"保存失败：{normalized} | {error}");
            return false;
        }

        _state.MapPath = normalized;
        MapEditorDocument? doc = GetActiveDocument();
        if (doc is not null)
        {
            doc.Path = normalized;
            doc.IsPrefabDocument = NmpFileName.IsPrefabFile(normalized);
            doc.IsReadOnly = MapDocument.TryParseWpfSyntheticPath(normalized, out _, out _);
            doc.PreferredDataPathEntryName = GetSelectedDataPathDisplayName();
            if (string.IsNullOrWhiteSpace(doc.DisplayName))
            {
                doc.DisplayName = GetDefaultDisplayNameForPath(normalized, doc.IsPrefabDocument);
            }

            if (updateDocumentIdentity)
            {
                doc.NormalizedPath = normalized;
            }

            PersistFolderMetaForDocument(doc, updateDocumentIdentity ? "另存为" : "保存");
        }

        _state.History.MarkSaved();
        _state.StatusMessage = $"已保存：{normalized}";
        _console.Append(MapEditorConsoleLogLevel.Info, "Map", $"已保存：{normalized}");
        return true;
    }

    private void BeginSaveSelectionAsPrefabPopup(int defaultVersion)
    {
        int version = defaultVersion;
        if (Array.IndexOf(PrefabVersions, version) < 0)
        {
            version = PrefabVersions[^1];
        }

        _saveSelectionAsPrefabVersion = version;
        _saveSelectionAsPrefabOverwriteConfirm = false;
        _saveSelectionAsPrefabError = string.Empty;
        _requestSaveSelectionAsPrefabPopup = true;
    }

    private void BeginCopyPrefabPopup(string sourcePath)
    {
        _copyPrefabSourcePath = sourcePath ?? string.Empty;
        _copyPrefabOverwriteConfirm = false;
        _copyPrefabError = string.Empty;

        string stem = string.Empty;
        if (FolderMetaCodec.TryGetEntryForFilePath(_copyPrefabSourcePath, out FolderMetaEntry meta, out _)
            && !string.IsNullOrWhiteSpace(meta.DisplayName))
        {
            stem = meta.DisplayName.Trim();
        }
        else
        {
            try
            {
                stem = Path.GetFileNameWithoutExtension(_copyPrefabSourcePath);
            }
            catch
            {
                stem = string.Empty;
            }
        }

        _copyPrefabNameText = string.IsNullOrWhiteSpace(stem) ? "prefab_copy" : $"{stem}_copy";
        _requestCopyPrefabPopup = true;
    }

    private void BeginSavePrefabAsPopup()
    {
        _savePrefabAsOverwriteConfirm = false;
        _savePrefabAsError = string.Empty;

        string stem = GetActiveDocument()?.DisplayName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stem))
        {
            try
            {
                stem = Path.GetFileNameWithoutExtension(_state.MapPath ?? string.Empty);
            }
            catch
            {
                stem = string.Empty;
            }
        }

        _savePrefabAsNameText = string.IsNullOrWhiteSpace(stem) ? "prefab" : stem;
        _requestSavePrefabAsPopup = true;
    }

    private static string GetPrefabsDirectory()
        => Path.Combine(Environment.CurrentDirectory, "prefabs");

    private static bool TryEnsureDirectory(string directoryPath, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            error = "目录路径为空。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directoryPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string BuildNmpoExtension(int version)
        => $".nmpo{version}";

    private static int GetPrefabVersionIndex(int version)
    {
        int idx = Array.IndexOf(PrefabVersions, version);
        return idx >= 0 ? idx : (PrefabVersions.Length - 1);
    }

    private static string SanitizePrefabFileStem(string rawStem)
    {
        string stem = rawStem?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stem))
        {
            return string.Empty;
        }

        try
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }
        catch
        {
            // ignore
        }

        stem = stem.Trim();
        if (stem.Length == 0)
        {
            return string.Empty;
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(c, '_');
        }

        stem = stem.Replace(Path.DirectorySeparatorChar, '_');
        stem = stem.Replace(Path.AltDirectorySeparatorChar, '_');
        stem = stem.Trim().TrimEnd('.');

        if (stem == "." || stem == "..")
        {
            return string.Empty;
        }

        return stem;
    }

    private static string SanitizeMapFileStem(string rawStem)
    {
        string stem = rawStem?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stem))
        {
            return string.Empty;
        }

        try
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }
        catch
        {
            // ignore
        }

        stem = stem.Trim();
        if (stem.Length == 0)
        {
            return string.Empty;
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(c, '_');
        }

        stem = stem.Replace(Path.DirectorySeparatorChar, '_');
        stem = stem.Replace(Path.AltDirectorySeparatorChar, '_');
        stem = stem.Trim().TrimEnd('.');

        if (stem == "." || stem == "..")
        {
            return string.Empty;
        }

        return stem;
    }

    private string GetSuggestedMapSaveAsPath()
    {
        string current = _state.MapPath?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current))
        {
            if (!MapDocument.TryParseWpfSyntheticPath(current, out _, out _))
            {
                return current;
            }
        }

        string baseName = "New Map";
        if (MapDocument.TryParseWpfSyntheticPath(current, out _, out string entryPath))
        {
            try
            {
                baseName = Path.GetFileNameWithoutExtension(entryPath);
            }
            catch
            {
                baseName = entryPath;
            }
        }
        else if (_state.Map is not null && !string.IsNullOrWhiteSpace(_state.Map.Path))
        {
            baseName = _state.Map.Path;
        }

        baseName = SanitizeMapFileStem(baseName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "New Map";
        }

        string folder = string.Empty;
        for (int i = 0; i < _state.MapPathEntries.Count; i++)
        {
            string candidate = _state.MapPathEntries[i]?.Path?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                folder = candidate;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Environment.CurrentDirectory;
        }

        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch
        {
            // ignore
        }

        return Path.Combine(folder, baseName + ".nmp");
    }

    private static string AppendDefaultNmpExtensionIfMissing(string path)
    {
        string value = path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            string ext = Path.GetExtension(value);
            if (string.IsNullOrWhiteSpace(ext) || ext == ".")
            {
                return value + ".nmp";
            }
        }
        catch
        {
            // ignore
        }

        return value;
    }

    private static bool FileExistsSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private bool TryWritePrefabFromSelection(string outputPath, int version, out string error)
    {
        error = string.Empty;

        if (_state.Map is null)
        {
            error = "未加载地图。";
            return false;
        }

        if (!_state.HasSelection)
        {
            error = "未选择区域。";
            return false;
        }

        if (Array.IndexOf(PrefabVersions, version) < 0)
        {
            error = $"不支持的 Prefab 版本：{version}";
            return false;
        }

        MapDocument map = _state.Map;

        int x0 = Math.Min(_state.SelectionX0, _state.SelectionX1);
        int y0 = Math.Min(_state.SelectionY0, _state.SelectionY1);
        int x1 = Math.Max(_state.SelectionX0, _state.SelectionX1);
        int y1 = Math.Max(_state.SelectionY0, _state.SelectionY1);

        x0 = Math.Clamp(x0, 0, map.Width - 1);
        y0 = Math.Clamp(y0, 0, map.Height - 1);
        x1 = Math.Clamp(x1, 0, map.Width - 1);
        y1 = Math.Clamp(y1, 0, map.Height - 1);

        if (x1 < x0 || y1 < y0)
        {
            error = "选择区域无效。";
            return false;
        }

        int selW = x1 - x0 + 1;
        int selH = y1 - y0 + 1;

        long cellCountLong = (long)selW * selH;
        if (cellCountLong is <= 0 or > 1_000_000)
        {
            error = $"选择区域过大（{selW} x {selH} = {cellCountLong} 格），暂不支持保存为 Prefab。";
            return false;
        }

        var cells = new NmpCellData[(int)cellCountLong];
        for (int y = y0; y <= y1; y++)
        {
            int row = (y - y0) * selW;
            for (int x = x0; x <= x1; x++)
            {
                int srcIndex = map.GetIndex(x, y);
                if ((uint)srcIndex >= (uint)map.Cells.Length)
                {
                    continue;
                }

                cells[row + (x - x0)] = map.Cells[srcIndex];
            }
        }

        var infoForWrite = new NmpMapInfo
        {
            Path = outputPath,
            HeaderSize = 20,
            Version = (uint)version,
            Width = selW,
            Height = selH,
            DataOffset = 20,
        };

        if (!NmpCodec.WriteNmpMapData(outputPath, infoForWrite, cells, out error))
        {
            return false;
        }

        return true;
    }

    private bool TryCopyPrefabFile(string sourcePath, string outputPath, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            error = "源 Prefab 路径为空。";
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            error = $"源 Prefab 不存在：{sourcePath}";
            return false;
        }

        if (!MapDocument.TryLoad(sourcePath, out MapDocument? prefab, out error) || prefab is null)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                error = "读取 Prefab 失败。";
            }
            return false;
        }

        NmpMapInfo info = prefab.Info;
        var infoForWrite = new NmpMapInfo
        {
            Path = outputPath,
            HeaderSize = info.HeaderSize,
            Version = info.Version,
            Width = info.Width,
            Height = info.Height,
            DataOffset = info.DataOffset,
        };

        if (!NmpCodec.WriteNmpMapData(outputPath, infoForWrite, prefab.Cells, out error))
        {
            return false;
        }

        return true;
    }

    private static void SetDragDropPayloadUtf8String(string payloadType, string value)
    {
        if (string.IsNullOrWhiteSpace(payloadType))
        {
            return;
        }

        value ??= string.Empty;

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] payload = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, payload, 0, bytes.Length);
        payload[^1] = 0;

        GCHandle handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            ImGui.SetDragDropPayload(payloadType, handle.AddrOfPinnedObject(), (uint)payload.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    private void ClearStamp()
    {
        _loadedStampPath = string.Empty;
        _stampMap = null;
        _stampLoadError = string.Empty;
    }

    private bool EnsureStampLoaded(bool forceReload, out string error)
    {
        error = string.Empty;

        if (_state.StampSource == StampSourceKind.Clipboard)
        {
            _loadedStampPath = "<clipboard>";
            _stampLoadError = string.Empty;

            if (_clipboardStamp is null)
            {
                _stampMap = null;
                _stampLoadError = "剪贴板印章为空（请先用“选择”工具框选并复制）。";
                error = _stampLoadError;
                return false;
            }

            _stampMap = _clipboardStamp;
            return true;
        }

        if (_state.StampSource == StampSourceKind.MoveSelection)
        {
            _loadedStampPath = "<moving>";
            _stampLoadError = string.Empty;

            if (_moveSelection is null)
            {
                _stampMap = null;
                _stampLoadError = "移动选区印章为空（请先在“选择”中执行“移动选区”）。";
                error = _stampLoadError;
                return false;
            }

            _stampMap = _moveSelection.Snippet;
            return true;
        }

        string path = _state.StampPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            ClearStamp();
            return false;
        }

        if (!forceReload && string.Equals(path, _loadedStampPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_stampMap is not null)
            {
                return true;
            }

            error = _stampLoadError;
            return false;
        }

        _loadedStampPath = path;
        _stampLoadError = string.Empty;
        _stampMap = null;

        if (MapDocument.TryLoad(path, out MapDocument? stamp, out string loadError))
        {
            _stampMap = stamp;
            return stamp is not null;
        }

        _stampLoadError = loadError;
        error = loadError;
        return false;
    }

    private void ProcessTextureScanTask()
    {
        if (_textureScanTask is null)
        {
            return;
        }

        if (!_textureScanTask.IsCompleted)
        {
            return;
        }

        Task<TextureScanResult> task = _textureScanTask;
        _textureScanTask = null;

        TextureScanResult result;
        try
        {
            result = task.Result;
        }
        catch (Exception ex)
        {
            _state.TextureLastError = ex.Message;
            _state.StatusMessage = "贴图库扫描失败。";
            _console.Append(MapEditorConsoleLogLevel.Error, "Texture", $"贴图库扫描失败：{ex.Message}");
            return;
        }

        if (!result.Ok)
        {
            _state.TextureLastError = result.Error;
            _state.StatusMessage = "贴图库扫描失败。";
            _console.Append(MapEditorConsoleLogLevel.Error, "Texture", string.IsNullOrWhiteSpace(result.Error) ? "贴图库扫描失败。" : $"贴图库扫描失败：{result.Error}");
            return;
        }

        ApplyTextureScanResult(result, source: "scan");

        if (_queuedTextureRootSwitchQueued)
        {
            string queuedRoot = _queuedTextureRootSwitchDataRoot;
            string queuedReason = _queuedTextureRootSwitchReason;
            _queuedTextureRootSwitchQueued = false;
            _queuedTextureRootSwitchDataRoot = string.Empty;
            _queuedTextureRootSwitchReason = string.Empty;

            if (!string.IsNullOrWhiteSpace(queuedRoot))
            {
                RequestTextureRootSwitch(queuedRoot, queuedReason);
            }
        }

        if (_bridgeTextureReloadQueued)
        {
            string queued = _bridgeTextureReloadDataPath;
            _bridgeTextureReloadQueued = false;
            _bridgeTextureReloadDataPath = string.Empty;
            HandleReloadDataFolderBridgeRequest(queued);
        }
    }

    private void ProcessResourceValidationTask()
    {
        if (_resourceValidationTask is null)
        {
            return;
        }

        if (!_resourceValidationTask.IsCompleted)
        {
            return;
        }

        Task<MapResourceValidationReport> task = _resourceValidationTask;
        _resourceValidationTask = null;

        try
        {
            MapResourceValidationReport report = task.GetAwaiter().GetResult();
            _resourceValidationReport = report;
            _resourceValidationError = string.Empty;
            _state.StatusMessage = $"资源验证完成：{report.Issues.Count} 条问题（UniqueImages={report.UniqueImageRefs}，Coast={report.UniqueCoastCompositeRefs}）";
            _console.Append(MapEditorConsoleLogLevel.Info, "Validate", $"资源验证完成：issues={report.Issues.Count} uniqueImages={report.UniqueImageRefs} coast={report.UniqueCoastCompositeRefs} path={report.DocumentPath}");
        }
        catch (OperationCanceledException)
        {
            _resourceValidationReport = null;
            _resourceValidationError = "资源验证已取消。";
            _state.StatusMessage = _resourceValidationError;
            _console.Append(MapEditorConsoleLogLevel.Warning, "Validate", "资源验证已取消。");
        }
        catch (Exception ex)
        {
            _resourceValidationReport = null;
            _resourceValidationError = ex.Message;
            _state.StatusMessage = "资源验证失败。";
            _console.Append(MapEditorConsoleLogLevel.Error, "Validate", $"资源验证失败：{ex.Message}");
        }
        finally
        {
            _resourceValidationCts?.Dispose();
            _resourceValidationCts = null;
        }
    }

    private void ProcessMapBrowserScanTask()
    {
        if (_mapBrowserScanTask is null)
        {
            return;
        }

        if (!_mapBrowserScanTask.IsCompleted)
        {
            return;
        }

        Task<MapBrowserScanResult> task = _mapBrowserScanTask;
        _mapBrowserScanTask = null;

        MapBrowserScanResult result;
        try
        {
            result = task.Result;
        }
        catch (Exception ex)
        {
            _mapBrowserScanResult = null;
            _state.StatusMessage = "地图目录扫描失败。";
            Console.Error.WriteLine(ex.Message);
            return;
        }

        _mapBrowserScanResult = result;
        if (!result.Ok)
        {
            _state.StatusMessage = "地图目录扫描失败。";
            return;
        }

        _state.StatusMessage = $"地图目录扫描完成：{result.FileCount} 个文件";
    }

    private void ProcessMinimapBatchExportTask()
    {
        if (_minimapBatchExportTask is null)
        {
            return;
        }

        if (!_minimapBatchExportTask.IsCompleted)
        {
            return;
        }

        Task<MinimapBatchExportResult> task = _minimapBatchExportTask;
        _minimapBatchExportTask = null;
        _minimapBatchExportCts?.Dispose();
        _minimapBatchExportCts = null;

        try
        {
            MinimapBatchExportResult result = task.Result;
            int warnings = _minimapBatchExportProgress.GetSnapshot().Warnings.Length;
            if (result.Canceled)
            {
                _state.StatusMessage = $"批量导出已取消：ok={result.Ok} warn={warnings} failed={result.Failed} skipped={result.Skipped}（输出目录：{result.OutputDirectory}）";
                _console.Append(MapEditorConsoleLogLevel.Warning, "Export", $"批量导出已取消：ok={result.Ok} warn={warnings} failed={result.Failed} skipped={result.Skipped} out={result.OutputDirectory}");
            }
            else
            {
                _state.StatusMessage = $"批量导出完成：total={result.Total} ok={result.Ok} warn={warnings} failed={result.Failed} skipped={result.Skipped}（输出目录：{result.OutputDirectory}）";
                _console.Append(MapEditorConsoleLogLevel.Info, "Export", $"批量导出完成：total={result.Total} ok={result.Ok} warn={warnings} failed={result.Failed} skipped={result.Skipped} out={result.OutputDirectory}");
            }
        }
        catch (Exception ex)
        {
            _minimapBatchExportProgress.FailAll($"批量导出任务异常：{ex.Message}");
            _state.StatusMessage = $"批量导出失败：{ex.Message}";
            _console.Append(MapEditorConsoleLogLevel.Error, "Export", $"批量导出失败：{ex.Message}");
        }
    }

    private void DrawMapPathEntriesSection()
    {
        List<NamedPathEntry> entries = _state.MapPathEntries;
        _state.SelectedMapPathEntryIndex = ClampNamedPathIndex(entries, _state.SelectedMapPathEntryIndex);

        ImGui.TextUnformatted($"Map Paths（{entries.Count}）");
        ImGui.TextDisabled("支持命名、多条路径、顺序调整、浏览和持久化。");

        if (ImGui.Button("新增空项##map_paths_new"))
        {
            entries.Add(new NamedPathEntry
            {
                DisplayName = SuggestNamedPathDisplayName(string.Empty, "Map Path", entries.Count + 1),
                Path = string.Empty,
            });
            _state.SelectedMapPathEntryIndex = entries.Count - 1;
            _state.StatusMessage = "已新增 Map Path（请填写路径或点击“浏览...”）。";
        }

        ImGui.SameLine();
        if (ImGui.Button("加入当前根目录##map_paths_add"))
        {
            if (TryAddNamedPathEntry(entries, _state.MapBrowserRootDirectory, "Map Path", out int selectedIndex, out string message))
            {
                _state.SelectedMapPathEntryIndex = selectedIndex;
            }

            _state.StatusMessage = message;
        }

        bool hasSelected = _state.SelectedMapPathEntryIndex >= 0 && _state.SelectedMapPathEntryIndex < entries.Count;
        if (!hasSelected)
        {
            ImGui.BeginDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("套用选中##map_paths_apply"))
        {
            NamedPathEntry entry = entries[_state.SelectedMapPathEntryIndex];
            _state.MapBrowserRootDirectory = entry.Path;
            _state.StatusMessage = $"已切换地图根目录：{entry.DisplayName}";
        }

        ImGui.SameLine();
        if (ImGui.Button("用当前根目录覆盖选中##map_paths_update"))
        {
            UpdateNamedPathEntryFromCurrentPath(entries, _state.SelectedMapPathEntryIndex, _state.MapBrowserRootDirectory, "Map Path");
            _state.StatusMessage = $"已更新 Map Path：{entries[_state.SelectedMapPathEntryIndex].DisplayName}";
        }

        ImGui.SameLine();
        if (ImGui.Button("上移##map_paths_up"))
        {
            int selectedIndex = _state.SelectedMapPathEntryIndex;
            MoveNamedPathEntry(entries, ref selectedIndex, -1);
            _state.SelectedMapPathEntryIndex = selectedIndex;
        }

        ImGui.SameLine();
        if (ImGui.Button("下移##map_paths_down"))
        {
            int selectedIndex = _state.SelectedMapPathEntryIndex;
            MoveNamedPathEntry(entries, ref selectedIndex, 1);
            _state.SelectedMapPathEntryIndex = selectedIndex;
        }

        ImGui.SameLine();
        if (ImGui.Button("移除##map_paths_remove"))
        {
            int selectedIndex = _state.SelectedMapPathEntryIndex;
            RemoveNamedPathEntry(entries, ref selectedIndex);
            _state.SelectedMapPathEntryIndex = selectedIndex;
            _state.StatusMessage = "已移除选中的 Map Path。";
        }

        if (!hasSelected)
        {
            ImGui.EndDisabled();
        }

        Vector2 listSize = new(0.0f, 110.0f);
        if (ImGui.BeginChild("##map_paths_list", listSize, ImGuiChildFlags.Borders))
        {
            if (entries.Count == 0)
            {
                ImGui.TextDisabled("尚未保存 Map Path。");
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    NamedPathEntry entry = entries[i];
                    ImGui.PushID(i);
                    bool selected = i == _state.SelectedMapPathEntryIndex;
                    string displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? "(未命名)" : entry.DisplayName;
                    if (ImGui.Selectable(displayName, selected))
                    {
                        _state.SelectedMapPathEntryIndex = i;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Path);
                    }

                    ImGui.TextDisabled(entry.Path);
                    ImGui.PopID();
                }
            }
        }

        ImGui.EndChild();

        if (_state.SelectedMapPathEntryIndex >= 0 && _state.SelectedMapPathEntryIndex < entries.Count)
        {
            NamedPathEntry entry = entries[_state.SelectedMapPathEntryIndex];

            string displayName = entry.DisplayName ?? string.Empty;
            if (ImGui.InputText("名称##map_path_name", ref displayName, 256))
            {
                entry.DisplayName = displayName;
            }

            string path = entry.Path ?? string.Empty;
            float browseButtonWidth = 80.0f;
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(-browseButtonWidth - spacing);
            if (ImGui.InputText("路径##map_path_value", ref path, 4096))
            {
                entry.Path = path;
            }

            ImGui.SameLine();
            if (ImGui.Button("浏览...##map_path_browse"))
            {
                string startDir = entry.Path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(startDir))
                {
                    startDir = _state.MapBrowserRootDirectory;
                }

                StartFolderBrowse(FolderBrowseTarget.MapPathEntry, _state.SelectedMapPathEntryIndex, "选择 Map Path 目录", startDir);
            }
        }
    }

    private void DrawDataPathEntriesSection()
    {
        List<NamedPathEntry> entries = _state.DataPathEntries;
        _state.SelectedDataPathEntryIndex = ClampNamedPathIndex(entries, _state.SelectedDataPathEntryIndex);

        ImGui.TextUnformatted($"Data Paths（{entries.Count}）");
        ImGui.TextDisabled("支持命名、多条路径、顺序调整、浏览和持久化。");

        if (ImGui.Button("新增空项##data_paths_new"))
        {
            entries.Add(new NamedPathEntry
            {
                DisplayName = SuggestNamedPathDisplayName(string.Empty, "Data Path", entries.Count + 1),
                Path = string.Empty,
            });
            _state.SelectedDataPathEntryIndex = entries.Count - 1;
            _state.StatusMessage = "已新增 Data Path（请填写路径或点击“浏览...”）。";
        }

        ImGui.SameLine();
        if (ImGui.Button("加入当前贴图库目录##data_paths_add"))
        {
            if (TryAddNamedPathEntry(entries, _state.TextureRootDirectory, "Data Path", out int selectedIndex, out string message))
            {
                _state.SelectedDataPathEntryIndex = selectedIndex;
            }

            _state.StatusMessage = message;
        }

        bool hasSelected = _state.SelectedDataPathEntryIndex >= 0 && _state.SelectedDataPathEntryIndex < entries.Count;
        if (!hasSelected)
        {
            ImGui.BeginDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("套用选中##data_paths_apply"))
        {
            NamedPathEntry entry = entries[_state.SelectedDataPathEntryIndex];
            _state.TextureRootDirectory = entry.Path;
            _state.StatusMessage = $"已切换贴图库目录：{entry.DisplayName}";
        }

        ImGui.SameLine();
        if (ImGui.Button("用当前贴图库目录覆盖选中##data_paths_update"))
        {
            UpdateNamedPathEntryFromCurrentPath(entries, _state.SelectedDataPathEntryIndex, _state.TextureRootDirectory, "Data Path");
            _state.StatusMessage = $"已更新 Data Path：{entries[_state.SelectedDataPathEntryIndex].DisplayName}";
        }

        ImGui.SameLine();
        if (ImGui.Button("上移##data_paths_up"))
        {
            int selectedIndex = _state.SelectedDataPathEntryIndex;
            MoveNamedPathEntry(entries, ref selectedIndex, -1);
            _state.SelectedDataPathEntryIndex = selectedIndex;
        }

        ImGui.SameLine();
        if (ImGui.Button("下移##data_paths_down"))
        {
            int selectedIndex = _state.SelectedDataPathEntryIndex;
            MoveNamedPathEntry(entries, ref selectedIndex, 1);
            _state.SelectedDataPathEntryIndex = selectedIndex;
        }

        ImGui.SameLine();
        if (ImGui.Button("移除##data_paths_remove"))
        {
            int selectedIndex = _state.SelectedDataPathEntryIndex;
            RemoveNamedPathEntry(entries, ref selectedIndex);
            _state.SelectedDataPathEntryIndex = selectedIndex;
            _state.StatusMessage = "已移除选中的 Data Path。";
        }

        if (!hasSelected)
        {
            ImGui.EndDisabled();
        }

        Vector2 listSize = new(0.0f, 110.0f);
        if (ImGui.BeginChild("##data_paths_list", listSize, ImGuiChildFlags.Borders))
        {
            if (entries.Count == 0)
            {
                ImGui.TextDisabled("尚未保存 Data Path。");
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    NamedPathEntry entry = entries[i];
                    ImGui.PushID(i);
                    bool selected = i == _state.SelectedDataPathEntryIndex;
                    string displayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? "(未命名)" : entry.DisplayName;
                    if (ImGui.Selectable(displayName, selected))
                    {
                        _state.SelectedDataPathEntryIndex = i;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Path);
                    }

                    ImGui.TextDisabled(entry.Path);
                    ImGui.PopID();
                }
            }
        }

        ImGui.EndChild();

        if (_state.SelectedDataPathEntryIndex >= 0 && _state.SelectedDataPathEntryIndex < entries.Count)
        {
            NamedPathEntry entry = entries[_state.SelectedDataPathEntryIndex];

            string displayName = entry.DisplayName ?? string.Empty;
            if (ImGui.InputText("名称##data_path_name", ref displayName, 256))
            {
                entry.DisplayName = displayName;
            }

            string path = entry.Path ?? string.Empty;
            float browseButtonWidth = 80.0f;
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(-browseButtonWidth - spacing);
            if (ImGui.InputText("路径##data_path_value", ref path, 4096))
            {
                entry.Path = path;
            }

            ImGui.SameLine();
            if (ImGui.Button("浏览...##data_path_browse"))
            {
                string startDir = entry.Path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(startDir))
                {
                    startDir = _state.TextureRootDirectory;
                }

                StartFolderBrowse(FolderBrowseTarget.DataPathEntry, _state.SelectedDataPathEntryIndex, "选择 Data Path 目录", startDir);
            }
        }
    }

    private static int ClampNamedPathIndex(List<NamedPathEntry> entries, int selectedIndex)
    {
        if (entries.Count == 0)
        {
            return -1;
        }

        return selectedIndex < 0 || selectedIndex >= entries.Count
            ? 0
            : selectedIndex;
    }

    private static bool TryAddNamedPathEntry(List<NamedPathEntry> entries, string currentPath, string fallbackPrefix, out int selectedIndex, out string message)
    {
        selectedIndex = ClampNamedPathIndex(entries, -1);
        string normalizedPath = NormalizeNamedPath(currentPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            message = "当前路径为空，无法加入列表。";
            return false;
        }

        int existingIndex = FindNamedPathEntry(entries, normalizedPath);
        if (existingIndex >= 0)
        {
            selectedIndex = existingIndex;
            message = $"路径已在列表中：{entries[existingIndex].DisplayName}";
            return true;
        }

        entries.Add(new NamedPathEntry
        {
            DisplayName = SuggestNamedPathDisplayName(normalizedPath, fallbackPrefix, entries.Count + 1),
            Path = normalizedPath,
        });

        selectedIndex = entries.Count - 1;
        message = $"已加入路径：{normalizedPath}";
        return true;
    }

    private static void UpdateNamedPathEntryFromCurrentPath(List<NamedPathEntry> entries, int selectedIndex, string currentPath, string fallbackPrefix)
    {
        if (selectedIndex < 0 || selectedIndex >= entries.Count)
        {
            return;
        }

        string normalizedPath = NormalizeNamedPath(currentPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        NamedPathEntry entry = entries[selectedIndex];
        entry.Path = normalizedPath;
        if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            entry.DisplayName = SuggestNamedPathDisplayName(normalizedPath, fallbackPrefix, selectedIndex + 1);
        }
    }

    private static void MoveNamedPathEntry(List<NamedPathEntry> entries, ref int selectedIndex, int delta)
    {
        if (entries.Count == 0 || selectedIndex < 0 || selectedIndex >= entries.Count || delta == 0)
        {
            return;
        }

        int targetIndex = Math.Clamp(selectedIndex + delta, 0, entries.Count - 1);
        if (targetIndex == selectedIndex)
        {
            return;
        }

        NamedPathEntry entry = entries[selectedIndex];
        entries.RemoveAt(selectedIndex);
        entries.Insert(targetIndex, entry);
        selectedIndex = targetIndex;
    }

    private static void RemoveNamedPathEntry(List<NamedPathEntry> entries, ref int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= entries.Count)
        {
            return;
        }

        entries.RemoveAt(selectedIndex);
        if (entries.Count == 0)
        {
            selectedIndex = -1;
            return;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, entries.Count - 1);
    }

    private static int FindNamedPathEntry(List<NamedPathEntry> entries, string path)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(NormalizeNamedPath(entries[i].Path), path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string SuggestNamedPathDisplayName(string path, string fallbackPrefix, int ordinal)
    {
        string normalizedPath = NormalizeNamedPath(path);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            try
            {
                string trimmed = normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch
            {
                // ignored
            }
        }

        return $"{fallbackPrefix} {Math.Max(1, ordinal)}";
    }

    private static string NormalizeNamedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim();
    }

    private bool TrySendAssetToContentEditor(string assetPath, int selectedImageIndex, string description)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            _state.StatusMessage = "桥接：资产路径为空。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", "发送到 ContentEditor 失败：资产路径为空。");
            return false;
        }

        if (!_editorBridge.SendOpenAsset(assetPath, selectedImageIndex, out string error))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? "桥接：发送到 ContentEditor 失败。" : error;
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", string.IsNullOrWhiteSpace(error)
                ? $"发送到 ContentEditor 失败：{description}"
                : $"发送到 ContentEditor 失败：{description} | {error}");
            return false;
        }

        bool running = _editorBridge.IsAppRunning(EditorBridgeApp.ContentEditor);
        string status = running ? "已发送到 ContentEditor" : "已加入队列（ContentEditor 未运行）";
        _state.StatusMessage = string.IsNullOrWhiteSpace(description)
            ? $"桥接：{status}。"
            : $"桥接：{status}：{description}";
        _console.Append(MapEditorConsoleLogLevel.Info, "Bridge", $"{status}：{description}");
        return true;
    }

    private bool TryGetFetchTexturesRequestInfo(out string mapPath, out string dataPath, out string disabledReason)
    {
        mapPath = string.Empty;
        dataPath = string.Empty;
        disabledReason = string.Empty;

        if (_state.Map is null)
        {
            disabledReason = "未加载地图。";
            return false;
        }

        mapPath = _state.MapPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mapPath))
        {
            disabledReason = "当前地图没有磁盘路径（请先保存到磁盘）。";
            return false;
        }

        if (MapDocument.TryParseWpfSyntheticPath(mapPath, out _, out _))
        {
            disabledReason = "当前地图来自 WPF（只读），请先另存为到磁盘。";
            return false;
        }

        try
        {
            string ext = (Path.GetExtension(mapPath) ?? string.Empty).ToLowerInvariant();
            if (ext is not ".nmp" and not ".mmp")
            {
                disabledReason = "仅支持 .nmp / .mmp。";
                return false;
            }
        }
        catch
        {
            disabledReason = "地图路径无效。";
            return false;
        }

        if (mapPath.Contains("::", StringComparison.Ordinal))
        {
            disabledReason = "当前地图路径不是普通磁盘路径（疑似桥接/WPF）。";
            return false;
        }

        if (!File.Exists(mapPath))
        {
            disabledReason = $"地图文件不存在：{mapPath}";
            return false;
        }

        dataPath = _state.TextureRootDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            disabledReason = "请先在“贴图库目录 / Data Paths”中选择 Data Path。";
            return false;
        }

        try
        {
            // Allow non-existing folders (Downloader will create it).
            _ = Path.GetFullPath(dataPath);
        }
        catch (Exception ex)
        {
            disabledReason = $"Data Path 无效：{ex.Message}";
            return false;
        }

        return true;
    }

    private void TrySendFetchTexturesRequest()
    {
        if (!TryGetFetchTexturesRequestInfo(out string mapPath, out string dataPath, out string reason))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(reason) ? "无法发送抓取纹理请求。" : reason;
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", string.IsNullOrWhiteSpace(reason)
                ? "无法发送抓取纹理请求。"
                : $"无法发送抓取纹理请求：{reason}");
            return;
        }

        if (!_editorBridge.SendPatchNmp(mapPath, dataPath, out string error))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? "桥接：发送抓取纹理请求失败。" : error;
            _console.Append(MapEditorConsoleLogLevel.Warning, "Bridge", string.IsNullOrWhiteSpace(error)
                ? $"发送抓取纹理请求失败：{mapPath} | data={dataPath}"
                : $"发送抓取纹理请求失败：{mapPath} | data={dataPath} | {error}");
            return;
        }

        bool running = _editorBridge.IsAppRunning(EditorBridgeApp.Downloader);
        string status = running ? "已发送到 Downloader" : "已加入队列（Downloader 未运行）";
        _state.StatusMessage = $"桥接：{status}：{Path.GetFileName(mapPath)} | data={dataPath}";
        _console.Append(MapEditorConsoleLogLevel.Info, "Bridge", $"{status}：{mapPath} | data={dataPath}");
    }

    private static bool TryResolveTextureRef(ObjectListEntryKind kind, int library, int image, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (image <= 0)
        {
            return false;
        }

        imageIndex = image;
        packageId = library;

        if (packageId == 0)
        {
            packageId = kind switch
            {
                ObjectListEntryKind.BackTile => DefaultSmTilesLibrary,
                ObjectListEntryKind.MiddleTile => DefaultTilesLibrary,
                _ => DefaultObjectLibrary,
            };
        }

        return packageId > 0;
    }

    private bool TryOpenTextureRefInContentEditor(string label, int packageId, int imageIndex)
    {
        if (!_textureIndex.IsReady)
        {
            _state.StatusMessage = "桥接：贴图库未就绪（请先扫描贴图库：SGL/WPF）。";
            return false;
        }

        if (!_textureIndex.TryGetImageBridgeTarget(packageId, imageIndex, out string assetPath, out int selectedImageIndex, out string error))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? "桥接：无法解析贴图来源。" : error;
            return false;
        }

        string displayPath = assetPath;
        try
        {
            string name = Path.GetFileName(assetPath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                displayPath = name;
            }
        }
        catch
        {
            // ignore
        }

        string desc = $"{label} -> {displayPath}（包={packageId} 图={imageIndex}）";
        return TrySendAssetToContentEditor(assetPath, selectedImageIndex, desc);
    }

    private void BuildMenuBar()
    {
        HandleGlobalShortcuts();

        if (ImGui.BeginMenu("MAP EDITOR"))
        {
            if (ImGui.MenuItem("偏好设置(Preferences)"))
            {
                _state.ShowSettingsWindow = true;
            }

            if (ImGui.MenuItem("重置布局(Reset Layout)"))
            {
                _requestResetDockLayout = true;
            }

            ImGui.Separator();

            if (ImGui.MenuItem("退出"))
            {
                _state.RequestExit = true;
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("文件"))
        {
            if (ImGui.MenuItem("新建地图(New Map)"))
            {
                _requestNewMapPopup = true;
            }

            if (ImGui.MenuItem("新建 Prefab(New Prefab)"))
            {
                _requestNewPrefabPopup = true;
            }

            ImGui.Separator();

            if (ImGui.MenuItem("打开路径..."))
            {
                _requestOpenByPathPopup = true;
            }

            bool canSave = _state.Map is not null && !_state.History.IsAtSavePoint;
            if (ImGui.MenuItem("保存", "Ctrl+S", selected: false, enabled: canSave))
            {
                TrySaveActiveDocument();
            }
            if (!canSave
                && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)
                && _state.Map is not null)
            {
                ImGui.SetTooltip("当前文档没有未保存改动。");
            }

            if (ImGui.MenuItem("另存为...", "Ctrl+Shift+S", selected: false, enabled: _state.Map is not null))
            {
                if (IsActiveDocumentPrefab())
                {
                    BeginSavePrefabAsPopup();
                }
                else
                {
                    _requestSaveAsPopup = true;
                }
            }

            bool canSavePrefabAs = _state.Map is not null && IsActiveDocumentPrefab();
            if (ImGui.MenuItem("另存为 Prefab...", null, selected: false, enabled: canSavePrefabAs))
            {
                BeginSavePrefabAsPopup();
            }
            if (!canSavePrefabAs
                && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)
                && _state.Map is not null)
            {
                ImGui.SetTooltip("仅对已打开的 Prefab 文档（.nmpo/.nmpoN）可用。");
            }

            ImGui.Separator();

            if (ImGui.MenuItem("导出小地图 PNG...", null, selected: false, enabled: _state.Map is not null))
            {
                _state.ShowMinimapExportPopup = true;
            }

            if (ImGui.MenuItem("批量导出小地图 PNG..."))
            {
                _state.ShowMinimapBatchExportPopup = true;
            }

            ImGui.Separator();

            bool canClose = _documents.Count > 0 && _activeDocumentIndex >= 0 && _activeDocumentIndex < _documents.Count;
            if (ImGui.MenuItem("关闭(Close)", null, selected: false, enabled: canClose))
            {
                CloseDocument(_activeDocumentIndex);
            }

            if (ImGui.MenuItem("退出"))
            {
                _state.RequestExit = true;
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("编辑"))
        {
            bool canUndo = _state.Map is not null && _state.History.UndoCount > 0;
            bool canRedo = _state.Map is not null && _state.History.RedoCount > 0;

            string undoLabel = string.IsNullOrWhiteSpace(_state.History.PeekUndoName)
                ? "撤销"
                : $"撤销：{_state.History.PeekUndoName}";

            string redoLabel = string.IsNullOrWhiteSpace(_state.History.PeekRedoName)
                ? "重做"
                : $"重做：{_state.History.PeekRedoName}";

            if (ImGui.MenuItem(undoLabel, "Ctrl+Z", selected: false, enabled: canUndo))
            {
                TryUndo();
            }

            if (ImGui.MenuItem(redoLabel, "Ctrl+Y", selected: false, enabled: canRedo))
            {
                TryRedo();
            }

            ImGui.Separator();
            if (ImGui.MenuItem("清空历史", null, selected: false, enabled: _state.History.UndoCount > 0 || _state.History.RedoCount > 0))
            {
                _state.History.Clear();
                _state.StatusMessage = "已清空 Undo/Redo 历史。";
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("图层(Layers)"))
        {
            DrawLayersMenuItems("##main");

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("光照(Lighting)"))
        {
            DrawLightingMenuItems("##main");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("视图"))
        {
            bool showBrowser = _state.ShowFileBrowserPanel;
            if (ImGui.MenuItem("文件浏览器", null, selected: showBrowser))
            {
                _state.ShowFileBrowserPanel = !showBrowser;
            }

            bool showPrefabs = _state.ShowPrefabBrowserPanel;
            if (ImGui.MenuItem("Prefabs", null, selected: showPrefabs))
            {
                _state.ShowPrefabBrowserPanel = !showPrefabs;
            }

            bool showObjects = _state.ShowObjectListPanel;
            if (ImGui.MenuItem("对象列表", null, selected: showObjects))
            {
                _state.ShowObjectListPanel = !showObjects;
            }

            bool showSceneTree = _state.ShowSceneTreePanel;
            if (ImGui.MenuItem("场景树", null, selected: showSceneTree))
            {
                _state.ShowSceneTreePanel = !showSceneTree;
            }

            bool showInfo = _state.ShowInformationPanel;
            if (ImGui.MenuItem("地图信息(Information)", null, selected: showInfo))
            {
                _state.ShowInformationPanel = !showInfo;
            }

            bool showInspector = _state.ShowCellInspectorPanel;
            if (ImGui.MenuItem("格子检查器(Cell Inspector)", null, selected: showInspector))
            {
                _state.ShowCellInspectorPanel = !showInspector;
            }

            bool showConsole = _state.ShowConsolePanel;
            if (ImGui.MenuItem("Console", null, selected: showConsole))
            {
                _state.ShowConsolePanel = !showConsole;
            }

            bool showSettings = _state.ShowSettingsWindow;
            if (ImGui.MenuItem("偏好设置(Preferences)", null, selected: showSettings))
            {
                _state.ShowSettingsWindow = !showSettings;
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("覆盖(Overlays)"))
        {
            bool showGrid = _state.ShowGrid;
            if (ImGui.MenuItem("显示网格(Show Grid)", null, selected: showGrid))
            {
                _state.ShowGrid = !showGrid;
            }

            bool showFill = _state.ShowTileFill;
            if (ImGui.MenuItem("显示填充(Show Tile Fill)", null, selected: showFill))
            {
                _state.ShowTileFill = !showFill;
            }

            bool showBlocked = _state.ShowBlockedOverlay;
            if (ImGui.MenuItem("显示阻挡(Show Blocked)", null, selected: showBlocked))
            {
                _state.ShowBlockedOverlay = !showBlocked;
            }

            bool showMinimap = _state.ShowMinimapOverlay;
            if (ImGui.MenuItem("显示小地图(Show Minimap)", null, selected: showMinimap))
            {
                _state.ShowMinimapOverlay = !showMinimap;
                if (_state.ShowMinimapOverlay)
                {
                    InvalidateRuntimeMinimapActiveDocument();
                }
            }

            ImGui.Separator();
            DrawHighlightOverlayMenuItems();

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("网格(Grid)"))
        {
            DrawGridSettingsMenuItems("##main");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("阻挡(Blocked Overlay)"))
        {
            DrawBlockedOverlaySettingsMenuItems("##main");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("格子高亮(Cell Highlights)"))
        {
            DrawCellHighlightSettingsMenuItems("##main");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("动画(Animation)"))
        {
            DrawAnimationSettingsMenuItems("##main");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("桥接"))
        {
            bool contentRunning = _editorBridge.IsAppRunning(EditorBridgeApp.ContentEditor);
            bool downloaderRunning = _editorBridge.IsAppRunning(EditorBridgeApp.Downloader);
            ImGui.TextDisabled($"ContentEditor：{(contentRunning ? "运行中" : "未检测到")}");
            ImGui.TextDisabled($"Downloader：{(downloaderRunning ? "运行中" : "未检测到")}");

            ImGui.Separator();

            if (TryGetFetchTexturesRequestInfo(out _, out _, out string fetchReason))
            {
                if (ImGui.MenuItem("抓取纹理(Fetch Textures)：发送到 Downloader"))
                {
                    TrySendFetchTexturesRequest();
                }
            }
            else
            {
                ImGui.BeginDisabled();
                ImGui.MenuItem("抓取纹理(Fetch Textures)：发送到 Downloader");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(fetchReason))
                {
                    ImGui.SetTooltip(fetchReason);
                }
            }

            if (ImGui.MenuItem("在 ContentEditor 中打开资源..."))
            {
                _requestOpenInContentEditorPopup = true;
                _openInContentEditorPathText = string.Empty;
                _openInContentEditorImageIndex = -1;
            }

            ImGui.Separator();
            ImGui.TextWrapped("提示：");
            ImGui.TextWrapped("  - 格子检查器：每个层右侧可直接“在CE打开”对应贴图源。");
            ImGui.TextWrapped("  - 对象列表/场景树：右键条目可“在CE打开”。");
            ImGui.TextWrapped("  - 贴图库区域：可发送“抓取纹理(Fetch Textures)”到 Downloader。");

            ImGui.EndMenu();
        }
    }

    private const ImGuiColorEditFlags OverlayColorEditFlags =
        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf;

    private void DrawHighlightOverlayMenuItems()
    {
        bool highlightBack = _state.HighlightBackCells;
        if (ImGui.MenuItem("高亮后景格(Highlight Back Cells)", null, selected: highlightBack))
        {
            _state.HighlightBackCells = !highlightBack;
        }

        bool highlightMiddle = _state.HighlightMiddleCells;
        if (ImGui.MenuItem("高亮中景格(Highlight Middle Cells)", null, selected: highlightMiddle))
        {
            _state.HighlightMiddleCells = !highlightMiddle;
        }

        bool highlightFront = _state.HighlightFrontCells;
        if (ImGui.MenuItem("高亮前景格(Highlight Front Cells)", null, selected: highlightFront))
        {
            _state.HighlightFrontCells = !highlightFront;
        }

        bool highlightFloor = _state.HighlightFloorCells;
        if (ImGui.MenuItem("高亮近景地面格(Highlight Floor Cells)", null, selected: highlightFloor))
        {
            _state.HighlightFloorCells = !highlightFloor;
        }

        bool highlightUnder = _state.HighlightUnderFrontCells;
        if (ImGui.MenuItem("高亮下层物件格(Highlight UnderFront Cells)", null, selected: highlightUnder))
        {
            _state.HighlightUnderFrontCells = !highlightUnder;
        }

        bool highlightOver = _state.HighlightOverFrontCells;
        if (ImGui.MenuItem("高亮上层物件格(Highlight OverFront Cells)", null, selected: highlightOver))
        {
            _state.HighlightOverFrontCells = !highlightOver;
        }

        bool highlightCoastMask = _state.HighlightCoastMaskCells;
        if (ImGui.MenuItem("高亮海岸遮罩格(Highlight Coast Mask Cells)", null, selected: highlightCoastMask))
        {
            _state.HighlightCoastMaskCells = !highlightCoastMask;
        }

        ImGui.Separator();

        bool highlightMissingTex = _state.HighlightMissingTextureCells;
        if (ImGui.MenuItem("高亮缺失贴图格(Highlight Missing Texture Cells)", null, selected: highlightMissingTex))
        {
            _state.HighlightMissingTextureCells = !highlightMissingTex;
        }
    }

    private void DrawGridSettingsMenuItems(string idSuffix)
    {
        Vector4 gridColor = _state.GridColor;
        if (ImGui.ColorEdit4($"颜色##GridColor{idSuffix}", ref gridColor, OverlayColorEditFlags))
        {
            _state.GridColor = gridColor;
        }

        int thickness = _state.GridThickness;
        if (ImGui.SliderInt($"粗细##GridThickness{idSuffix}", ref thickness, 1, 5, "%d px"))
        {
            _state.GridThickness = Math.Clamp(thickness, 1, 5);
        }
    }

    private void DrawBlockedOverlaySettingsMenuItems(string idSuffix)
    {
        Vector4 blockedColor = _state.BlockedOverlayColor;
        if (ImGui.ColorEdit4($"颜色##BlockedOverlayColor{idSuffix}", ref blockedColor, OverlayColorEditFlags))
        {
            _state.BlockedOverlayColor = blockedColor;
        }
    }

    private void DrawCellHighlightSettingsMenuItems(string idSuffix)
    {
        Vector4 back = _state.HighlightBackColor;
        if (ImGui.ColorEdit4($"后景(Back)##HighlightBack{idSuffix}", ref back, OverlayColorEditFlags))
        {
            _state.HighlightBackColor = back;
        }

        Vector4 middle = _state.HighlightMiddleColor;
        if (ImGui.ColorEdit4($"中景(Middle)##HighlightMiddle{idSuffix}", ref middle, OverlayColorEditFlags))
        {
            _state.HighlightMiddleColor = middle;
        }

        Vector4 front = _state.HighlightFrontColor;
        if (ImGui.ColorEdit4($"前景(Front)##HighlightFront{idSuffix}", ref front, OverlayColorEditFlags))
        {
            _state.HighlightFrontColor = front;
        }

        Vector4 floor = _state.HighlightFloorColor;
        if (ImGui.ColorEdit4($"近景地面(Floor)##HighlightFloor{idSuffix}", ref floor, OverlayColorEditFlags))
        {
            _state.HighlightFloorColor = floor;
        }

        Vector4 under = _state.HighlightUnderFrontColor;
        if (ImGui.ColorEdit4($"下层物件(UnderFront)##HighlightUnderFront{idSuffix}", ref under, OverlayColorEditFlags))
        {
            _state.HighlightUnderFrontColor = under;
        }

        Vector4 over = _state.HighlightOverFrontColor;
        if (ImGui.ColorEdit4($"上层物件(OverFront)##HighlightOverFront{idSuffix}", ref over, OverlayColorEditFlags))
        {
            _state.HighlightOverFrontColor = over;
        }

        Vector4 coastMask = _state.HighlightCoastMaskColor;
        if (ImGui.ColorEdit4($"海岸遮罩(CoastMask)##HighlightCoastMask{idSuffix}", ref coastMask, OverlayColorEditFlags))
        {
            _state.HighlightCoastMaskColor = coastMask;
        }

        Vector4 missing = _state.HighlightMissingTextureColor;
        if (ImGui.ColorEdit4($"缺失贴图(Missing)##HighlightMissing{idSuffix}", ref missing, OverlayColorEditFlags))
        {
            _state.HighlightMissingTextureColor = missing;
        }
    }

    private void DrawAnimationSettingsMenuItems(string idSuffix)
    {
        bool animate = _state.RenderAnimateTextures;
        if (ImGui.MenuItem("贴图动画(Animate Textures)", null, selected: animate))
        {
            _state.RenderAnimateTextures = !animate;
        }

        int tickMs = AnimationTickMsFromFps(_state.TextureAnimationFps);
        if (ImGui.SliderInt($"Tick (ms)##AnimationTick{idSuffix}", ref tickMs, 1, 1000, "%d ms"))
        {
            _state.TextureAnimationFps = AnimationFpsFromTickMs(tickMs);
        }
    }

    private void DrawLightingMenuItems(string idSuffix)
    {
        void LightingItem(string label, MapLightingMode mode)
        {
            bool selected = _state.RenderLighting.Mode == mode;
            if (ImGui.MenuItem(label, null, selected))
            {
                _state.RenderLighting.Mode = mode;
            }
        }

        LightingItem("白天(Day)", MapLightingMode.Day);
        LightingItem("夜晚(Night)", MapLightingMode.Night);
        LightingItem("自动（系统时间）(Auto)", MapLightingMode.Auto);
        LightingItem("自定义时间(Custom Time)", MapLightingMode.CustomTime);

        if (_state.RenderLighting.Mode == MapLightingMode.CustomTime)
        {
            int hour = Math.Clamp(_state.RenderLighting.CustomHour, 0, 23);
            int minute = Math.Clamp(_state.RenderLighting.CustomMinute, 0, 59);

            ImGui.Separator();

            ImGui.SetNextItemWidth(140.0f);
            if (ImGui.SliderInt($"小时##MapLightingHour{idSuffix}", ref hour, 0, 23, "%02d"))
            {
                _state.RenderLighting.CustomHour = hour;
            }

            ImGui.SetNextItemWidth(140.0f);
            if (ImGui.SliderInt($"分钟##MapLightingMinute{idSuffix}", ref minute, 0, 59, "%02d"))
            {
                _state.RenderLighting.CustomMinute = minute;
            }
        }

        ImGui.Separator();
        ResolvedMapLighting resolved = MapLighting.Resolve(_state.RenderLighting);
        ImGui.TextDisabled($"预览：{resolved.Hour:00}:{resolved.Minute:00}  夜晚系数={resolved.NightFactor:0.00}");
    }

    private void DrawLayersMenuItems(string idSuffix)
    {
        bool showBack = _state.ShowBackLayer;
        if (ImGui.MenuItem($"后景 (SmTiles)##ShowBack{idSuffix}", null, selected: showBack))
        {
            _state.ShowBackLayer = !showBack;
        }

        bool showMiddle = _state.ShowMiddleLayer;
        if (ImGui.MenuItem($"中景 (Tiles)##ShowMiddle{idSuffix}", null, selected: showMiddle))
        {
            _state.ShowMiddleLayer = !showMiddle;
        }

        bool showFloor = _state.ShowFloorLayer;
        if (ImGui.MenuItem($"近景地面 (NearGround)##ShowFloor{idSuffix}", null, selected: showFloor))
        {
            _state.ShowFloorLayer = !showFloor;
        }

        bool showUnder = _state.ShowUnderFrontLayer;
        if (ImGui.MenuItem($"下层物件 (UnderObject)##ShowUnderFront{idSuffix}", null, selected: showUnder))
        {
            _state.ShowUnderFrontLayer = !showUnder;
        }

        bool showFront = _state.ShowFrontLayer;
        if (ImGui.MenuItem($"前景 (Object)##ShowFront{idSuffix}", null, selected: showFront))
        {
            _state.ShowFrontLayer = !showFront;
        }

        bool showOver = _state.ShowOverFrontLayer;
        if (ImGui.MenuItem($"上层物件 (OverObject)##ShowOverFront{idSuffix}", null, selected: showOver))
        {
            _state.ShowOverFrontLayer = !showOver;
        }

        ImGui.Separator();

        bool showDynamicScene = _state.ShowDynamicSceneLayer;
        if (ImGui.MenuItem($"Dynamic Scene Layer (experimental)##ShowDynScene{idSuffix}", null, selected: showDynamicScene))
        {
            _state.ShowDynamicSceneLayer = !showDynamicScene;
        }

        bool showEffects = _state.ShowAttachedEffectsLayer;
        if (ImGui.MenuItem($"Attached Effect Layer (experimental)##ShowEffects{idSuffix}", null, selected: showEffects))
        {
            _state.ShowAttachedEffectsLayer = !showEffects;
        }

        ImGui.Separator();

        if (ImGui.BeginMenu("Client Parity"))
        {
            DrawClientParityMenuItems(idSuffix);
            ImGui.Separator();
            ImGui.TextDisabled("Dynamic scene / attached effects require external client data.");
            ImGui.EndMenu();
        }
    }

    private void DrawClientParityMenuItems(string idSuffix)
    {
        bool suppressBorder = _state.RenderSuppressBorderCells;
        if (ImGui.MenuItem($"Suppress BORDER cells##SuppressBorder{idSuffix}", null, selected: suppressBorder))
        {
            _state.RenderSuppressBorderCells = !suppressBorder;
        }

        bool applyHeightFlag = _state.RenderApplyCellHeightFlag;
        if (ImGui.MenuItem($"Apply height flag (0x40)##ApplyHeightFlag{idSuffix}", null, selected: applyHeightFlag))
        {
            _state.RenderApplyCellHeightFlag = !applyHeightFlag;
        }

        if (_state.RenderApplyCellHeightFlag)
        {
            float lift = _state.RenderCellHeightFlagOffset;
            ImGui.SetNextItemWidth(140.0f);
            if (ImGui.DragFloat($"Height flag lift##HeightFlagLift{idSuffix}", ref lift, 0.25f, 0.0f, 64.0f, "%.2f px"))
            {
                _state.RenderCellHeightFlagOffset = Math.Clamp(lift, 0.0f, 64.0f);
            }
        }

        bool applyObjHeight = _state.RenderApplyObjectHeight;
        if (ImGui.MenuItem($"Apply object height##ApplyObjectHeight{idSuffix}", null, selected: applyObjHeight))
        {
            _state.RenderApplyObjectHeight = !applyObjHeight;
        }

        if (_state.RenderApplyObjectHeight)
        {
            float scale = _state.RenderObjectHeightScale;
            ImGui.SetNextItemWidth(140.0f);
            if (ImGui.DragFloat($"Object height scale##ObjectHeightScale{idSuffix}", ref scale, 0.05f, 0.0f, 8.0f, "%.2f px/unit"))
            {
                _state.RenderObjectHeightScale = Math.Clamp(scale, 0.0f, 8.0f);
            }
        }

        bool applyTints = _state.RenderApplyCellTints;
        if (ImGui.MenuItem($"Apply cell tints##ApplyCellTints{idSuffix}", null, selected: applyTints))
        {
            _state.RenderApplyCellTints = !applyTints;
        }

        if (_state.RenderApplyCellTints)
        {
            float strength = _state.RenderTintStrength;
            ImGui.SetNextItemWidth(140.0f);
            if (ImGui.DragFloat($"Tint strength##TintStrength{idSuffix}", ref strength, 0.01f, 0.0f, 1.0f, "%.2f"))
            {
                _state.RenderTintStrength = Math.Clamp(strength, 0.0f, 1.0f);
            }
        }

        bool warn = _state.RenderWarnOnUnsupportedParityData;
        if (ImGui.MenuItem($"Warn on missing parity data##WarnParity{idSuffix}", null, selected: warn))
        {
            _state.RenderWarnOnUnsupportedParityData = !warn;
        }
    }

    private static int AnimationTickMsFromFps(float fps)
    {
        if (!float.IsFinite(fps) || fps <= 0.001f)
        {
            return 100;
        }

        int tickMs = (int)MathF.Round(1000.0f / fps);
        return Math.Clamp(tickMs, 1, 1000);
    }

    private static float AnimationFpsFromTickMs(int tickMs)
    {
        tickMs = Math.Clamp(tickMs, 1, 1000);
        return 1000.0f / tickMs;
    }

    private void HandleResetDockLayout()
    {
        if (!_requestResetDockLayout)
        {
            return;
        }

        _requestResetDockLayout = false;

        try
        {
            ResetDockLayoutToDefault();
            _state.StatusMessage = "已重置 Dock 布局。";
            _console.Append(MapEditorConsoleLogLevel.Info, "UI", "已重置 Dock 布局。");
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"重置布局失败：{ex.Message}";
            _console.Append(MapEditorConsoleLogLevel.Error, "UI", $"重置布局失败：{ex}");
        }
    }

    private void ResetDockLayoutToDefault()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        uint dockspaceId = _dockspaceId;

        // DockSpaceOverViewport 在本帧更早阶段已创建 dockspace 节点，这里只清空其 layout 再按旧版比例重建。
        ImGuiDockBuilder.RemoveNodeDockedWindows(dockspaceId);
        ImGuiDockBuilder.RemoveNodeChildNodes(dockspaceId);
        ImGuiDockBuilder.SetNodeSize(dockspaceId, viewport.WorkSize);

        uint centerDockId = dockspaceId;
        ImGuiDockBuilder.SplitNode(centerDockId, ImGuiDir.Left, 0.20f, out uint leftDockId, out centerDockId);
        ImGuiDockBuilder.SplitNode(centerDockId, ImGuiDir.Right, 0.25f, out uint rightDockId, out centerDockId);
        ImGuiDockBuilder.SplitNode(centerDockId, ImGuiDir.Down, 0.25f, out uint bottomDockId, out centerDockId);
        ImGuiDockBuilder.SplitNode(rightDockId, ImGuiDir.Down, 0.55f, out uint rightBottomId, out uint rightTopId);
        ImGuiDockBuilder.SplitNode(leftDockId, ImGuiDir.Down, 0.50f, out uint leftBottomId, out uint leftTopId);

        ImGuiDockBuilder.DockWindow("文件浏览器##map_browser", leftTopId);
        ImGuiDockBuilder.DockWindow("Prefabs##prefab_browser", leftBottomId);
        ImGuiDockBuilder.DockWindow("地图信息##map_info", rightTopId);
        ImGuiDockBuilder.DockWindow("格子检查器##cell_inspector", rightBottomId);
        ImGuiDockBuilder.DockWindow("地图视图", centerDockId);
        ImGuiDockBuilder.DockWindow("Console##console", bottomDockId);

        ImGuiDockBuilder.Finish(dockspaceId);
    }

    private void BuildDockedUi()
    {
        HandleResetDockLayout();
        BuildSettingsWindow();
        BuildMapViewWindow();
        BuildMapBrowserWindow();
        BuildPrefabBrowserWindow();
        BuildInspectorWindow();
        BuildObjectListWindow();
        BuildSceneTreeWindow();
        BuildMapInfoWindow();
        BuildConsoleWindow();
        BuildNewMapPopup();
        BuildNewPrefabPopup();
        BuildOpenByPathPopup();
        BuildOpenInContentEditorPopup();
        BuildSaveAsPopup();
        BuildSaveSelectionAsPrefabPopup();
        BuildCopyPrefabPopup();
        BuildSavePrefabAsPopup();
        BuildMinimapExportPopup();
        BuildMinimapBatchExportPopup();
        BuildFolderDialog();
        HandleFileDialog();

        UnloadInactiveTabsIfEnabled();
        MaybeAutosavePreferences();
    }

    private void StartFolderBrowse(FolderBrowseTarget target, int entryIndex, string title, string startDirectory)
    {
        _folderBrowseTarget = target;
        _folderBrowseEntryIndex = entryIndex;

        string start = startDirectory ?? string.Empty;
        if (string.IsNullOrWhiteSpace(start))
        {
            start = Environment.CurrentDirectory;
        }

        _folderDialog.Open(SimpleFileDialogMode.OpenFolder, title, start);
    }

    private void BuildFolderDialog()
    {
        _folderDialog.Draw();

        if (!_folderDialog.TryConsumeResult(out SimpleFileDialogResult result, out string selectedPath))
        {
            return;
        }

        if (result != SimpleFileDialogResult.Ok || string.IsNullOrWhiteSpace(selectedPath))
        {
            _folderBrowseTarget = FolderBrowseTarget.None;
            _folderBrowseEntryIndex = -1;
            return;
        }

        string path = selectedPath.Trim();
        switch (_folderBrowseTarget)
        {
            case FolderBrowseTarget.MapBrowserRoot:
                _state.MapBrowserRootDirectory = path;
                {
                    int matchIndex = FindNamedPathEntry(_state.MapPathEntries, NormalizeNamedPath(path));
                    if (matchIndex >= 0)
                    {
                        _state.SelectedMapPathEntryIndex = matchIndex;
                    }
                }
                _state.StatusMessage = $"已选择 Map Root：{path}";
                _console.Append(MapEditorConsoleLogLevel.Info, "Settings", $"已选择 Map Root：{path}");
                break;

            case FolderBrowseTarget.TextureRoot:
                _state.TextureRootDirectory = path;
                {
                    int matchIndex = FindNamedPathEntry(_state.DataPathEntries, NormalizeNamedPath(path));
                    if (matchIndex >= 0)
                    {
                        _state.SelectedDataPathEntryIndex = matchIndex;
                    }
                }
                _state.StatusMessage = $"已选择 Data Root：{path}";
                _console.Append(MapEditorConsoleLogLevel.Info, "Settings", $"已选择 Data Root：{path}");
                break;

            case FolderBrowseTarget.MapPathEntry:
                if (_folderBrowseEntryIndex >= 0 && _folderBrowseEntryIndex < _state.MapPathEntries.Count)
                {
                    NamedPathEntry entry = _state.MapPathEntries[_folderBrowseEntryIndex];
                    entry.Path = path;
                    if (string.IsNullOrWhiteSpace(entry.DisplayName))
                    {
                        entry.DisplayName = SuggestNamedPathDisplayName(path, "Map Path", _folderBrowseEntryIndex + 1);
                    }

                    _state.StatusMessage = $"已更新 Map Path：{entry.DisplayName}";
                    _console.Append(MapEditorConsoleLogLevel.Info, "Settings", $"已更新 Map Path：{entry.DisplayName} -> {path}");
                }
                break;

            case FolderBrowseTarget.DataPathEntry:
                if (_folderBrowseEntryIndex >= 0 && _folderBrowseEntryIndex < _state.DataPathEntries.Count)
                {
                    NamedPathEntry entry = _state.DataPathEntries[_folderBrowseEntryIndex];
                    entry.Path = path;
                    if (string.IsNullOrWhiteSpace(entry.DisplayName))
                    {
                        entry.DisplayName = SuggestNamedPathDisplayName(path, "Data Path", _folderBrowseEntryIndex + 1);
                    }

                    _state.StatusMessage = $"已更新 Data Path：{entry.DisplayName}";
                    _console.Append(MapEditorConsoleLogLevel.Info, "Settings", $"已更新 Data Path：{entry.DisplayName} -> {path}");
                }
                break;

            default:
                break;
        }

        _folderBrowseTarget = FolderBrowseTarget.None;
        _folderBrowseEntryIndex = -1;
    }

    private void HandleFileDialog()
    {
        _fileDialog.Draw();

        if (!_fileDialog.TryConsumeResult(out SimpleFileDialogResult result, out string selectedPath))
        {
            return;
        }

        PendingFileDialogAction action = _pendingFileDialogAction;
        _pendingFileDialogAction = PendingFileDialogAction.None;

        if (result != SimpleFileDialogResult.Ok || string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        string path = selectedPath.Trim();
        switch (action)
        {
            case PendingFileDialogAction.OpenMapByPath:
                _openByPathText = path;
                break;

            case PendingFileDialogAction.OpenAssetInContentEditor:
                _openInContentEditorPathText = path;
                break;

            case PendingFileDialogAction.SaveAsMap:
                _saveAsPathText = path;
                _saveAsOverwriteConfirm = false;
                break;

            default:
                break;
        }
    }

    private static float CalcButtonWidth(string label)
    {
        Vector2 size = ImGui.CalcTextSize(label);
        Vector2 padding = ImGui.GetStyle().FramePadding;
        return size.X + padding.X * 2.0f;
    }

    private static string SuggestStartDirectory(string? pathText, string? fallbackPath = null)
    {
        if (TryGetExistingDirectoryFromPath(pathText, out string dir))
        {
            return dir;
        }

        if (TryGetExistingDirectoryFromPath(fallbackPath, out string fallbackDir))
        {
            return fallbackDir;
        }

        return Environment.CurrentDirectory;
    }

    private static bool TryGetExistingDirectoryFromPath(string? pathText, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(pathText))
        {
            return false;
        }

        string candidate = pathText.Trim();
        try
        {
            candidate = Path.GetFullPath(candidate);
        }
        catch
        {
            // ignore
        }

        try
        {
            if (Directory.Exists(candidate))
            {
                directory = candidate;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            string? dir = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                directory = dir;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private void BuildMapBrowserWindow()
    {
        bool open = _state.ShowFileBrowserPanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("文件浏览器##map_browser", ref open))
        {
            ImGui.End();
            _state.ShowFileBrowserPanel = open;
            return;
        }

        _state.ShowFileBrowserPanel = open;

        bool scanning = _mapBrowserScanTask is not null && !_mapBrowserScanTask.IsCompleted;
        if (scanning)
        {
            ImGui.TextDisabled("正在扫描地图目录...");
        }

        string rootDir = _state.MapBrowserRootDirectory ?? string.Empty;
        float rootBrowseButtonWidth = 80.0f;
        float rootBrowseSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetNextItemWidth(-rootBrowseButtonWidth - rootBrowseSpacing);
        if (ImGui.InputText("根目录", ref rootDir, 512))
        {
            _state.MapBrowserRootDirectory = rootDir;
            int matchIndex = FindNamedPathEntry(_state.MapPathEntries, NormalizeNamedPath(rootDir));
            if (matchIndex >= 0)
            {
                _state.SelectedMapPathEntryIndex = matchIndex;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("浏览...##map_root_browse"))
        {
            string startDir = rootDir;
            StartFolderBrowse(FolderBrowseTarget.MapBrowserRoot, -1, "选择地图根目录", startDir);
        }

        DrawMapPathEntriesSection();

        ImGui.Separator();

        bool recursive = _state.MapBrowserRecursive;
        if (ImGui.Checkbox("递归扫描", ref recursive))
        {
            _state.MapBrowserRecursive = recursive;
        }

        bool includePrefabs = _state.MapBrowserIncludePrefabs;
        if (ImGui.Checkbox("包含 Prefab（.nmpo/.nmpoN）", ref includePrefabs))
        {
            _state.MapBrowserIncludePrefabs = includePrefabs;
        }

        string filter = _state.MapBrowserFilter ?? string.Empty;
        if (ImGui.InputText("过滤", ref filter, 256))
        {
            _state.MapBrowserFilter = filter;
        }

        if (scanning)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("扫描"))
        {
            StartMapBrowserScan();
        }

        ImGui.SameLine();
        if (ImGui.Button("使用测试样本目录"))
        {
            string candidate = Path.Combine(Environment.CurrentDirectory, "tests", "fixtures", "nmp");
            _state.MapBrowserRootDirectory = candidate;
        }

        if (scanning)
        {
            ImGui.EndDisabled();
        }

        ImGui.Separator();

        MapBrowserScanResult? result = _mapBrowserScanResult;
        if (result is null)
        {
            ImGui.TextDisabled("尚未扫描。请先点击“扫描”。");
            ImGui.End();
            return;
        }

        if (!result.Ok)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "扫描失败：");
            ImGui.TextWrapped(result.Error);
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted($"已发现文件：{result.FileCount}");
        ImGui.TextDisabled("提示：双击可打开（.nmp/.mmp 地图；.nmpo/.nmpoN 预制体）；拖拽 Prefab 到地图可直接放置。");

        if (!string.IsNullOrWhiteSpace(_selectedBrowserPath))
        {
            ImGui.TextDisabled("已选择：");
            ImGui.SameLine();
            ImGui.TextWrapped(Path.GetFileName(_selectedBrowserPath));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(_selectedBrowserPath);
            }

            bool isMapPath = false;
            bool isPrefabPath = false;
            try
            {
                string ext = Path.GetExtension(_selectedBrowserPath).ToLowerInvariant();
                isMapPath = ext is ".nmp" or ".mmp";
                isPrefabPath = !isMapPath && IsPrefabPath(_selectedBrowserPath);
            }
            catch
            {
                isMapPath = false;
                isPrefabPath = false;
            }

            if (isMapPath || isPrefabPath)
            {
                if (ImGui.Button("打开"))
                {
                    _pendingOpenPath = _selectedBrowserPath;
                }

                if (isPrefabPath)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("设为印章并切换工具"))
                    {
                        _state.StampPath = _selectedBrowserPath;
                        _state.StampSource = StampSourceKind.Path;
                        _state.Tool = MapEditTool.Stamp;
                        _paintDrag = null;
                        _rectFillDrag = null;
                        _selectionDrag = null;
                        ClearStamp();
                        _state.StatusMessage = "已设置印章：点击地图可盖章。";
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("复制 Prefab..."))
                    {
                        BeginCopyPrefabPopup(_selectedBrowserPath);
                    }
                }
            }
        }

        ImGui.Separator();

        string trimmedFilter = filter.Trim();
        DrawMapBrowserDirectoryNode(result.Root, trimmedFilter);

        ImGui.End();
    }

    private void BuildPrefabBrowserWindow()
    {
        bool open = _state.ShowPrefabBrowserPanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("Prefabs##prefab_browser", ref open))
        {
            ImGui.End();
            _state.ShowPrefabBrowserPanel = open;
            return;
        }

        _state.ShowPrefabBrowserPanel = open;

        string prefabsDir = GetPrefabsDirectory();
        if (!TryEnsureDirectory(prefabsDir, out string dirError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Prefabs 目录不可用：");
            ImGui.TextWrapped(dirError);
            ImGui.End();
            return;
        }

        bool hasSelection = !string.IsNullOrWhiteSpace(_selectedPrefabBrowserPath);
        if (!hasSelection)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("Copy Prefab..."))
        {
            BeginCopyPrefabPopup(_selectedPrefabBrowserPath);
        }
        if (!hasSelection)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (hasSelection)
        {
            string displayName = Path.GetFileName(_selectedPrefabBrowserPath);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = _selectedPrefabBrowserPath;
            }
            ImGui.TextDisabled($"Selected: {displayName}");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(_selectedPrefabBrowserPath);
            }
        }
        else
        {
            ImGui.TextDisabled("Select a prefab to copy, stamp, or open.");
        }

        ImGui.Separator();

        string filter = _prefabBrowserFilter ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##PrefabBrowserFilter", "Search prefabs...", ref filter, 256))
        {
            _prefabBrowserFilter = filter;
        }

        int viewMode = (int)_state.PrefabBrowserViewMode;
        if (ImGui.RadioButton("Thumbnails", viewMode == (int)PrefabBrowserViewMode.Thumbnails))
        {
            _state.PrefabBrowserViewMode = PrefabBrowserViewMode.Thumbnails;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Details", viewMode == (int)PrefabBrowserViewMode.Details))
        {
            _state.PrefabBrowserViewMode = PrefabBrowserViewMode.Details;
        }

        bool thumbnailsView = _state.PrefabBrowserViewMode == PrefabBrowserViewMode.Thumbnails;
        float thumbSize = _state.PrefabThumbnailSize;
        if (!float.IsFinite(thumbSize))
        {
            thumbSize = 96.0f;
        }
        thumbSize = Math.Clamp(thumbSize, 32.0f, 128.0f);

        if (!thumbnailsView)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.SliderFloat("Thumbnail Size", ref thumbSize, 32.0f, 128.0f, "%.0f px"))
        {
            _state.PrefabThumbnailSize = Math.Clamp(thumbSize, 32.0f, 128.0f);
        }
        if (!thumbnailsView)
        {
            ImGui.EndDisabled();
        }

        // Keep thumbnail loader settings in sync (and refresh cached textures when size changes).
        if (_prefabThumbnailLoader is not null)
        {
            int desiredMaxDim = (int)MathF.Round(Math.Clamp(_state.PrefabThumbnailSize, 32.0f, 128.0f));
            if (_prefabThumbnailLoader.MaxThumbnailSize != desiredMaxDim)
            {
                _prefabThumbnailLoader.MaxThumbnailSize = desiredMaxDim;
            }

            if (Math.Abs(_lastPrefabBrowserThumbSize - _state.PrefabThumbnailSize) > 0.01f)
            {
                _lastPrefabBrowserThumbSize = _state.PrefabThumbnailSize;
                _prefabThumbnailLoader.InvalidateAll();
            }
        }

        ImGui.Separator();

        static bool MatchesFilter(string value, string filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string TruncateToFit(string value, float maxWidth)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string s = value;
            Vector2 size = ImGui.CalcTextSize(s);
            if (size.X <= maxWidth)
            {
                return s;
            }

            while (s.Length > 3 && ImGui.CalcTextSize(s + "...").X > maxWidth)
            {
                s = s[..^1];
            }

            return s.Length <= 3 ? "..." : s + "...";
        }

        void SelectAsStamp(string prefabPath)
        {
            if (string.IsNullOrWhiteSpace(prefabPath))
            {
                return;
            }

            _selectedPrefabBrowserPath = prefabPath;

            if (_state.Map is null)
            {
                return;
            }

            // Old behavior: only stamp onto maps, not onto prefab/object documents.
            if (IsActiveDocumentPrefab())
            {
                return;
            }

            _state.StampPath = prefabPath;
            _state.StampSource = StampSourceKind.Path;
            _state.Tool = MapEditTool.Stamp;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            ClearStamp();
            _state.StatusMessage = "已设置印章：点击地图可盖章。";
        }

        int RenderThumbnailGrid(string dirPath, ref int itemId, ref string activatedPath, string filterText)
        {
            var entries = new List<PrefabBrowserEntry>();
            CollectPrefabEntries(dirPath, entries);

            int rendered = 0;

            // Directories first.
            for (int i = 0; i < entries.Count; i++)
            {
                PrefabBrowserEntry entry = entries[i];
                if (!entry.IsDirectory)
                {
                    continue;
                }

                ImGui.PushID(itemId++);
                ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
                bool openNode = ImGui.TreeNodeEx(entry.Name, flags);
                if (openNode)
                {
                    rendered += RenderThumbnailGrid(entry.FullPath, ref itemId, ref activatedPath, filterText);
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }

            float thumbImgH = Math.Clamp(_state.PrefabThumbnailSize, 32.0f, 128.0f);
            float cardW = Math.Max(96.0f, thumbImgH + 18.0f);
            float labelH = ImGui.GetTextLineHeight() + 6.0f;
            float cardH = thumbImgH + labelH + 4.0f;
            const float padding = 4.0f;

            float availW = ImGui.GetContentRegionAvail().X;
            int cols = Math.Max(1, (int)(availW / (cardW + padding)));

            int col = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                PrefabBrowserEntry entry = entries[i];
                if (entry.IsDirectory)
                {
                    continue;
                }

                if (!MatchesFilter(entry.DisplayName, filterText)
                    && !MatchesFilter(entry.Name, filterText)
                    && !MatchesFilter(entry.FullPath, filterText)
                    && !MatchesFilter(entry.PreferredDataFolderName, filterText))
                {
                    continue;
                }

                if (col > 0)
                {
                    ImGui.SameLine(0.0f, padding);
                }

                rendered++;

                ImGui.PushID(entry.FullPath);

                bool isSelected = string.Equals(_selectedPrefabBrowserPath, entry.FullPath, StringComparison.OrdinalIgnoreCase);
                Vector2 cardPos = ImGui.GetCursorScreenPos();
                ImDrawListPtr dl = ImGui.GetWindowDrawList();

                ImGui.InvisibleButton("##card", new Vector2(cardW, cardH));
                bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
                bool hovered = ImGui.IsItemHovered();
                bool dblClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

                uint bgCol = PackColor(30, 30, 30, 255);
                if (isSelected) bgCol = PackColor(50, 70, 100, 255);
                else if (hovered) bgCol = PackColor(45, 45, 50, 255);
                dl.AddRectFilled(cardPos, cardPos + new Vector2(cardW, cardH), bgCol, 3.0f);

                uint borderCol = isSelected ? PackColor(100, 160, 255, 200) : PackColor(60, 60, 60, 180);
                dl.AddRect(cardPos, cardPos + new Vector2(cardW, cardH), borderCol, 3.0f);

                Vector2 imgMin = cardPos + new Vector2(2.0f, 2.0f);
                Vector2 imgMax = new Vector2(cardPos.X + cardW - 2.0f, cardPos.Y + thumbImgH - 2.0f);

                bool drewThumb = false;
                if (_prefabThumbnailLoader is not null && _prefabThumbnailLoader.TryGetThumbnail(entry.FullPath, out PrefabThumbnailInfo thumb))
                {
                    dl.AddImage(thumb.TextureId, imgMin, imgMax, Vector2.Zero, Vector2.One, PackColor(255, 255, 255, 255));
                    drewThumb = true;
                }
                else if (_prefabThumbnailLoader is not null && _prefabThumbnailLoader.TryGetError(entry.FullPath, out _))
                {
                    dl.AddRectFilled(imgMin, imgMax, PackColor(190, 60, 60, 255), 2.0f);
                    drewThumb = true;
                }

                if (!drewThumb)
                {
                    const string ph = "...";
                    Vector2 phSize = ImGui.CalcTextSize(ph);
                    dl.AddText(new Vector2(cardPos.X + (cardW - phSize.X) * 0.5f, cardPos.Y + (thumbImgH - phSize.Y) * 0.5f),
                        PackColor(120, 120, 120, 255), ph);
                }

                string label = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Name : entry.DisplayName;

                string clipped = TruncateToFit(label, cardW - 4.0f);
                Vector2 textSize = ImGui.CalcTextSize(clipped);
                float labelX = cardPos.X + (cardW - textSize.X) * 0.5f;
                float labelY = cardPos.Y + thumbImgH + 2.0f;
                dl.AddText(new Vector2(labelX, labelY), PackColor(220, 220, 220, 255), clipped);

                if (hovered)
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(entry.FullPath);
                    ImGui.TextDisabled("左键：设为印章并切换工具；双击：打开编辑；拖拽：放置到地图。");
                    ImGui.EndTooltip();
                }

                if (clicked)
                {
                    SelectAsStamp(entry.FullPath);
                }

                if (dblClicked)
                {
                    activatedPath = entry.FullPath;
                }

                if (ImGui.BeginDragDropSource())
                {
                    SetDragDropPayloadUtf8String(DragDropPayloadPrefabPath, entry.FullPath);
                    ImGui.TextUnformatted("拖拽到地图以放置：");
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Name : entry.DisplayName);
                    ImGui.EndDragDropSource();
                }

                ImGui.PopID();

                col++;
                if (col >= cols)
                {
                    col = 0;
                }
            }

            return rendered;
        }

        int RenderDetailsList(string dirPath, ref int itemId, ref string activatedPath, string filterText)
        {
            var entries = new List<PrefabBrowserEntry>();
            CollectPrefabEntries(dirPath, entries);

            int rendered = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                PrefabBrowserEntry entry = entries[i];

                if (entry.IsDirectory)
                {
                    ImGui.TableNextRow();
                    ImGui.PushID(itemId++);

                    ImGui.TableSetColumnIndex(0);
                    bool openNode = ImGui.TreeNodeEx(entry.Name, ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextDisabled("Folder");

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled("-");

                    if (openNode)
                    {
                        rendered += RenderDetailsList(entry.FullPath, ref itemId, ref activatedPath, filterText);
                        ImGui.TreePop();
                    }

                    ImGui.PopID();
                    continue;
                }

                if (!MatchesFilter(entry.DisplayName, filterText)
                    && !MatchesFilter(entry.Name, filterText)
                    && !MatchesFilter(entry.FullPath, filterText)
                    && !MatchesFilter(entry.PreferredDataFolderName, filterText))
                {
                    continue;
                }

                rendered++;

                ImGui.TableNextRow();
                ImGui.PushID(itemId++);

                ImGui.TableSetColumnIndex(0);
                bool isSelected = string.Equals(_selectedPrefabBrowserPath, entry.FullPath, StringComparison.OrdinalIgnoreCase);
                string label = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Name : entry.DisplayName;
                if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _selectedPrefabBrowserPath = entry.FullPath;
                    SelectAsStamp(entry.FullPath);

                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        activatedPath = entry.FullPath;
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                {
                    ImGui.SetTooltip(entry.FullPath);
                }

                if (ImGui.BeginDragDropSource())
                {
                    SetDragDropPayloadUtf8String(DragDropPayloadPrefabPath, entry.FullPath);
                    ImGui.TextUnformatted("拖拽到地图以放置：");
                    ImGui.TextUnformatted(label);
                    ImGui.EndDragDropSource();
                }

                ImGui.TableSetColumnIndex(1);
                if (TryGetPrefabInfoCached(entry.FullPath, out NmpMapInfo info, out _))
                {
                    ImGui.TextUnformatted($"{info.Width}x{info.Height}");
                }
                else
                {
                    ImGui.TextDisabled("--");
                }

                ImGui.TableSetColumnIndex(2);
                if (TryGetPrefabInfoCached(entry.FullPath, out NmpMapInfo info2, out _))
                {
                    ImGui.TextUnformatted($"V{info2.Version}");
                }
                else
                {
                    ImGui.TextDisabled("--");
                }

                ImGui.PopID();
            }

            return rendered;
        }

        string activated = string.Empty;
        int renderedItemCount = 0;

        string trimmedFilter = filter.Trim();
        ImGui.BeginChild("PrefabBrowserList", new Vector2(0.0f, 0.0f), (ImGuiChildFlags)0, ImGuiWindowFlags.None);
        if (_state.PrefabBrowserViewMode == PrefabBrowserViewMode.Thumbnails)
        {
            int itemId = 0;
            renderedItemCount = RenderThumbnailGrid(prefabsDir, ref itemId, ref activated, trimmedFilter);
        }
        else if (ImGui.BeginTable("PrefabsDetailsTable", 3,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80.0f);
            ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 80.0f);
            ImGui.TableHeadersRow();

            int itemId = 0;
            renderedItemCount = RenderDetailsList(prefabsDir, ref itemId, ref activated, trimmedFilter);
            ImGui.EndTable();
        }
        ImGui.EndChild();

        if (renderedItemCount == 0)
        {
            if (!string.IsNullOrWhiteSpace(trimmedFilter))
            {
                ImGui.TextDisabled("No prefabs match the current filter.");
            }
            else
            {
                ImGui.TextDisabled("No .nmpo files found.");
            }
        }

        if (!string.IsNullOrWhiteSpace(activated))
        {
            _pendingOpenPath = activated;
        }

        ImGui.End();
    }

    private void StartMapBrowserScan()
    {
        if (_mapBrowserScanTask is not null)
        {
            return;
        }

        string root = _state.MapBrowserRootDirectory?.Trim() ?? string.Empty;
        bool recursive = _state.MapBrowserRecursive;
        bool includePrefabs = _state.MapBrowserIncludePrefabs;

        _mapBrowserScanResult = null;
        _state.StatusMessage = "正在扫描地图目录...";

        _mapBrowserScanTask = Task.Run(() =>
        {
            if (MapBrowserIndex.TryScan(root, recursive, includePrefabs, out MapBrowserDirectoryNode rootNode, out int fileCount, out string error))
            {
                return MapBrowserScanResult.Success(rootNode.FullPath, rootNode, fileCount);
            }

            return MapBrowserScanResult.Failed(root, error);
        });
    }

    private void DrawMapBrowserDirectoryNode(MapBrowserDirectoryNode node, string filter)
    {
        if (node is null)
        {
            return;
        }

        if (!HasAnyMapBrowserMatch(node, filter))
        {
            return;
        }

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        bool open = ImGui.TreeNodeEx(node.Name, flags);
        if (!open)
        {
            return;
        }

        for (int i = 0; i < node.Directories.Count; i++)
        {
            DrawMapBrowserDirectoryNode(node.Directories[i], filter);
        }

        for (int i = 0; i < node.Files.Count; i++)
        {
            MapBrowserFileEntry file = node.Files[i];
            if (!MapBrowserFileMatches(file, filter))
            {
                continue;
            }

            bool isMap = IsMapBrowserMapFile(file);
            bool isPrefab = !isMap && IsMapBrowserPrefabFile(file);
            bool canOpen = isMap || isPrefab;

            string label = string.IsNullOrWhiteSpace(file.DisplayName) ? file.Name : file.DisplayName;
            if (isPrefab)
            {
                label = $"[预制体] {label}";
            }

            if (!canOpen)
            {
                ImGui.BeginDisabled();
            }

            const float prefabThumbSize = 24.0f;

            ImGui.PushID(file.FullPath);
            if (isPrefab)
            {
                bool drawn = false;
                if (_prefabThumbnailLoader is not null
                    && _prefabThumbnailLoader.TryGetThumbnail(file.FullPath, out PrefabThumbnailInfo thumb))
                {
                    ImGui.Image(thumb.TextureId, new Vector2(prefabThumbSize, prefabThumbSize));
                    drawn = true;

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(file.FullPath);
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            _selectedBrowserPath = file.FullPath;
                            _openByPathText = file.FullPath;
                        }
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            _pendingOpenPath = file.FullPath;
                        }
                    }
                }
                else if (_prefabThumbnailLoader is not null
                    && _prefabThumbnailLoader.TryGetError(file.FullPath, out string thumbError))
                {
                    ImGui.ColorButton("##thumb_error", new Vector4(0.9f, 0.25f, 0.25f, 1.0f),
                        ImGuiColorEditFlags.NoTooltip, new Vector2(prefabThumbSize, prefabThumbSize));
                    drawn = true;

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(string.IsNullOrWhiteSpace(thumbError) ? "缩略图失败。" : $"缩略图失败：{thumbError}");
                    }
                }

                if (!drawn)
                {
                    ImGui.Dummy(new Vector2(prefabThumbSize, prefabThumbSize));
                }

                ImGui.SameLine();
            }

            bool selected = IsCurrentlyLoadedMap(file.FullPath);
            Vector2 selectableSize = isPrefab ? new Vector2(0, prefabThumbSize) : default;
            if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, selectableSize))
            {
                _openByPathText = file.FullPath;
                _selectedBrowserPath = file.FullPath;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(file.FullPath);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _pendingOpenPath = file.FullPath;
            }

            if (isPrefab && ImGui.BeginDragDropSource())
            {
                SetDragDropPayloadUtf8String(DragDropPayloadPrefabPath, file.FullPath);
                ImGui.TextUnformatted("拖拽到地图以放置：");
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(file.DisplayName) ? file.Name : file.DisplayName);
                ImGui.EndDragDropSource();
            }

            ImGui.PopID();

            if (!canOpen)
            {
                ImGui.EndDisabled();
            }
        }

        ImGui.TreePop();
    }

    private bool IsCurrentlyLoadedMap(string filePath)
    {
        if (_activeDocumentIndex < 0 || _activeDocumentIndex >= _documents.Count)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string a = _documents[_activeDocumentIndex].NormalizedPath;
        string b = NormalizeDocumentPath(filePath);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyMapBrowserMatch(MapBrowserDirectoryNode node, string filter)
    {
        if (node is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (MapBrowserDirectoryMatches(node, filter))
        {
            return true;
        }

        for (int i = 0; i < node.Files.Count; i++)
        {
            if (MapBrowserFileMatches(node.Files[i], filter))
            {
                return true;
            }
        }

        for (int i = 0; i < node.Directories.Count; i++)
        {
            if (HasAnyMapBrowserMatch(node.Directories[i], filter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MapBrowserDirectoryMatches(MapBrowserDirectoryNode node, string filter)
    {
        if (node is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(node.Name)
            && node.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(node.FullPath)
            && node.FullPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private static bool MapBrowserFileMatches(MapBrowserFileEntry file, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(file.DisplayName)
            && file.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(file.Name)
            && file.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(file.RelativePath)
            && file.RelativePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(file.FullPath)
            && file.FullPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(file.PreferredDataFolderName)
            && file.PreferredDataFolderName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private static bool IsMapBrowserMapFile(MapBrowserFileEntry file)
    {
        string ext;
        try
        {
            ext = Path.GetExtension(file.Name);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        return NmpFileName.IsMapExtension(ext);
    }

    private static bool IsMapBrowserPrefabFile(MapBrowserFileEntry file)
    {
        string ext;
        try
        {
            ext = Path.GetExtension(file.Name);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        return NmpFileName.IsPrefabExtension(ext);
    }

    private readonly record struct PrefabBrowserEntry(
        string Name,
        string DisplayName,
        string FullPath,
        bool IsDirectory,
        string PreferredDataFolderName);

    private sealed class PrefabInfoCacheEntry
    {
        public long LastWriteUtcTicks { get; init; }
        public bool Ok { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public uint Version { get; init; }
        public string Error { get; init; } = string.Empty;
    }

    private static void CollectPrefabEntries(string dirPath, List<PrefabBrowserEntry> entries)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        entries.Clear();

        if (string.IsNullOrWhiteSpace(dirPath))
        {
            return;
        }

        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(dirPath);
        }
        catch
        {
            fullDir = dirPath;
        }

        if (!Directory.Exists(fullDir))
        {
            return;
        }

        Dictionary<string, FolderMetaEntry> metaEntries = new(StringComparer.OrdinalIgnoreCase);
        FolderMetaCodec.TryReadDirectory(fullDir, out metaEntries, out _);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        try
        {
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(fullDir, "*", options))
            {
                if (string.IsNullOrWhiteSpace(entryPath))
                {
                    continue;
                }

                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(entryPath);
                }
                catch
                {
                    continue;
                }

                bool isDir = (attrs & FileAttributes.Directory) != 0;

                string name;
                try
                {
                    name = Path.GetFileName(entryPath);
                }
                catch
                {
                    name = entryPath;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = entryPath;
                }

                if (isDir)
                {
                    entries.Add(new PrefabBrowserEntry(name, name, entryPath, IsDirectory: true, PreferredDataFolderName: string.Empty));
                    continue;
                }

                if (NmpFileName.IsPrefabFile(entryPath))
                {
                    string displayName = name;
                    try
                    {
                        displayName = Path.GetFileNameWithoutExtension(name);
                    }
                    catch
                    {
                        displayName = name;
                    }

                    string preferredDataFolderName = string.Empty;
                    if (metaEntries.TryGetValue(name, out FolderMetaEntry meta))
                    {
                        if (!string.IsNullOrWhiteSpace(meta.DisplayName))
                        {
                            displayName = meta.DisplayName.Trim();
                        }

                        preferredDataFolderName = meta.DataFolderName ?? string.Empty;
                    }

                    entries.Add(new PrefabBrowserEntry(name, displayName, entryPath, IsDirectory: false, PreferredDataFolderName: preferredDataFolderName));
                }
            }
        }
        catch
        {
            // Ignore scan errors in the UI; it is best-effort.
        }

        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        });
    }

    private bool TryGetPrefabInfoCached(string prefabPath, out NmpMapInfo info, out string error)
    {
        info = new NmpMapInfo();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            error = "prefab 路径为空。";
            return false;
        }

        long lastWriteTicks = 0;
        try
        {
            if (!File.Exists(prefabPath))
            {
                error = "prefab 文件不存在。";
                _prefabInfoCache.Remove(prefabPath);
                return false;
            }

            lastWriteTicks = File.GetLastWriteTimeUtc(prefabPath).Ticks;
        }
        catch
        {
            lastWriteTicks = 0;
        }

        if (_prefabInfoCache.TryGetValue(prefabPath, out PrefabInfoCacheEntry? cached)
            && cached is not null
            && cached.LastWriteUtcTicks == lastWriteTicks)
        {
            if (!cached.Ok)
            {
                error = cached.Error;
                return false;
            }

            info = new NmpMapInfo
            {
                Path = prefabPath,
                HeaderSize = 0,
                Version = cached.Version,
                Width = cached.Width,
                Height = cached.Height,
                DataOffset = 0,
            };
            return true;
        }

        if (NmpCodec.TryReadMapInfo(prefabPath, out NmpMapInfo parsed, out error))
        {
            _prefabInfoCache[prefabPath] = new PrefabInfoCacheEntry
            {
                LastWriteUtcTicks = lastWriteTicks,
                Ok = true,
                Width = parsed.Width,
                Height = parsed.Height,
                Version = parsed.Version,
                Error = string.Empty,
            };
            info = parsed;
            return true;
        }

        _prefabInfoCache[prefabPath] = new PrefabInfoCacheEntry
        {
            LastWriteUtcTicks = lastWriteTicks,
            Ok = false,
            Width = 0,
            Height = 0,
            Version = 0,
            Error = error ?? string.Empty,
        };

        return false;
    }

    private void BuildObjectListWindow()
    {
        bool open = _state.ShowObjectListPanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("对象列表##object_list", ref open))
        {
            ImGui.End();
            _state.ShowObjectListPanel = open;
            return;
        }

        _state.ShowObjectListPanel = open;

        if (_state.Map is null)
        {
            ImGui.TextDisabled("未加载地图。");
            ImGui.End();
            return;
        }

        MapDocument map = _state.Map;
        if (_objectListDirty)
        {
            RebuildObjectList(map);
        }

        ImGui.TextUnformatted($"条目：{_objectListEntries.Count}");
        ImGui.TextDisabled("提示：双击条目可定位到样本格子；Back/Middle/Front 会同时设置为当前画笔。");

        string filter = _state.ObjectListFilter ?? string.Empty;
        if (ImGui.InputText("过滤", ref filter, 256))
        {
            _state.ObjectListFilter = filter;
        }

        if (ImGui.Button("刷新"))
        {
            _objectListDirty = true;
            _sceneTreeDirty = true;
        }

        ImGui.Separator();

        string trimmedFilter = filter.Trim();
        ImGui.BeginChild("##object_list_scroll", new Vector2(0, 0), (ImGuiChildFlags)0, ImGuiWindowFlags.None);
        for (int i = 0; i < _objectListEntries.Count; i++)
        {
            ObjectListEntry entry = _objectListEntries[i];
            string label = entry.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(trimmedFilter)
                && label.IndexOf(trimmedFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            bool selected = _selectedObjectListKey.HasValue && _selectedObjectListKey.Value == entry.Key;
            if (ImGui.Selectable(label, selected))
            {
                _selectedObjectListKey = entry.Key;
                _state.SelectedCellX = entry.SampleX;
                _state.SelectedCellY = entry.SampleY;
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                ApplyObjectListEntry(entry, centerCamera: true);
            }

            if (ImGui.BeginPopupContextItem($"##object_list_ctx_{(int)entry.Kind}_{entry.Library}_{entry.Image}_{entry.SampleX}_{entry.SampleY}"))
            {
                if (ImGui.MenuItem("定位"))
                {
                    ApplyObjectListEntry(entry, centerCamera: true);
                }

                bool canOpen = false;
                string openTip = string.Empty;
                int pkg = 0;
                int idx = 0;

                if (!TryResolveTextureRef(entry.Kind, entry.Library, entry.Image, out pkg, out idx))
                {
                    openTip = "空引用（img=0）。";
                }
                else if (!_textureIndex.IsReady)
                {
                    openTip = "贴图库未就绪：请先扫描贴图库（SGL/WPF）。";
                }
                else
                {
                    canOpen = _textureIndex.TryGetImageBridgeTarget(pkg, idx, out _, out _, out string bridgeError);
                    if (!canOpen)
                    {
                        openTip = string.IsNullOrWhiteSpace(bridgeError) ? "无法解析贴图来源。" : bridgeError;
                    }
                }

                if (ImGui.MenuItem("在 ContentEditor 中打开", null, selected: false, enabled: canOpen))
                {
                    TryOpenTextureRefInContentEditor(entry.KindLabel, pkg, idx);
                }

                if (!canOpen && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(openTip))
                {
                    ImGui.SetTooltip(openTip);
                }

                ImGui.EndPopup();
            }
        }
        ImGui.EndChild();

        ImGui.End();
    }

    private void BuildSceneTreeWindow()
    {
        bool open = _state.ShowSceneTreePanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("场景树##scene_tree", ref open))
        {
            ImGui.End();
            _state.ShowSceneTreePanel = open;
            return;
        }

        _state.ShowSceneTreePanel = open;

        if (_state.Map is null)
        {
            ImGui.TextDisabled("未加载地图。");
            ImGui.End();
            return;
        }

        MapDocument map = _state.Map;
        if (_sceneTreeDirty || !ReferenceEquals(_sceneTreeMap, map))
        {
            RebuildSceneTree(map);
        }

        ImGui.TextUnformatted($"对象条目：{_sceneTreeObjectNodes.Count}  预制体/组：{_sceneTreeGroupNodes.Count}");
        ImGui.TextDisabled("提示：点击实例可选中并定位；右键菜单可删除/复制为印章。");

        string filter = _state.SceneTreeFilter ?? string.Empty;
        if (ImGui.InputText("过滤", ref filter, 256))
        {
            _state.SceneTreeFilter = filter;
        }

        int max = _sceneTreeMaxInstancesPerRef;
        if (ImGui.SliderInt("每项最多显示", ref max, 20, 2000))
        {
            _sceneTreeMaxInstancesPerRef = Math.Clamp(max, 20, 2000);
        }

        ImGui.Separator();

        string trimmedFilter = filter.Trim();
        if (ImGui.BeginTabBar("##scene_tree_tabs"))
        {
            if (ImGui.BeginTabItem("对象"))
            {
                DrawSceneTreeObjects(map, trimmedFilter);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("预制体/组"))
            {
                DrawSceneTreeGroups(map, trimmedFilter);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void ApplyObjectListEntry(ObjectListEntry entry, bool centerCamera)
    {
        _state.SelectedCellX = entry.SampleX;
        _state.SelectedCellY = entry.SampleY;

        switch (entry.Kind)
        {
            case ObjectListEntryKind.BackTile:
                _state.EditLayer = MapLayer.Back;
                _state.PaintLibrary = Math.Clamp(entry.Library, 0, 65535);
                _state.PaintImage = Math.Clamp(entry.Image, 0, 65535);
                _state.StatusMessage = $"已选择后景：库={entry.Library} 图={entry.Image}";
                break;
            case ObjectListEntryKind.MiddleTile:
                _state.EditLayer = MapLayer.Middle;
                _state.PaintLibrary = Math.Clamp(entry.Library, 0, 65535);
                _state.PaintImage = Math.Clamp(entry.Image, 0, 65535);
                _state.StatusMessage = $"已选择中景：库={entry.Library} 图={entry.Image}";
                break;
            case ObjectListEntryKind.NearGround:
            {
                _state.EditLayer = MapLayer.Floor;
                int lib = Math.Clamp(entry.Library, 0, 255);
                int img = Math.Clamp(entry.Image, 0, 65535);
                if (img != 0 && lib == 0)
                {
                    lib = DefaultObjectLibrary;
                }
                _state.PaintLibrary = lib;
                _state.PaintImage = img;
                _state.StatusMessage = $"已选择近景地面：库={lib} 图={img}";
                break;
            }
            case ObjectListEntryKind.UnderObject:
            {
                _state.EditLayer = MapLayer.UnderFront;
                int lib = Math.Clamp(entry.Library, 0, 255);
                int img = Math.Clamp(entry.Image, 0, 65535);
                if (img != 0 && lib == 0)
                {
                    lib = DefaultObjectLibrary;
                }
                _state.PaintLibrary = lib;
                _state.PaintImage = img;
                _state.StatusMessage = $"已选择下层物件：库={lib} 图={img}";
                break;
            }
            case ObjectListEntryKind.FrontObject:
                _state.EditLayer = MapLayer.Front;
                _state.PaintLibrary = Math.Clamp(entry.Library, 0, 255);
                _state.PaintImage = Math.Clamp(entry.Image, 0, 65535);
                _state.StatusMessage = $"已选择前景：库={entry.Library} 图={entry.Image}";
                break;
            case ObjectListEntryKind.OverObject:
            {
                _state.EditLayer = MapLayer.OverFront;
                int lib = Math.Clamp(entry.Library, 0, 255);
                int img = Math.Clamp(entry.Image, 0, 65535);
                if (img != 0 && lib == 0)
                {
                    lib = DefaultObjectLibrary;
                }
                _state.PaintLibrary = lib;
                _state.PaintImage = img;
                _state.StatusMessage = $"已选择上层物件：库={lib} 图={img}";
                break;
            }
            default:
                _state.StatusMessage = $"已定位：{entry.KindLabel} 库={entry.Library} 图={entry.Image}";
                break;
        }

        if (centerCamera)
        {
            _requestCenterOnCell = true;
            _requestCenterCellX = entry.SampleX;
            _requestCenterCellY = entry.SampleY;
        }
    }

    private struct ObjectListAccumulator
    {
        public int Count;
        public int SampleX;
        public int SampleY;
    }

    private sealed class SceneTreeObjectNode
    {
        public ObjectListKey Key { get; }
        public List<int> CellIndices { get; } = new();

        public SceneTreeObjectNode(ObjectListKey key)
        {
            Key = key;
        }
    }

    private sealed class SceneTreeGroupNode
    {
        public uint GroupId { get; }
        public int MinX { get; private set; }
        public int MinY { get; private set; }
        public int MaxX { get; private set; }
        public int MaxY { get; private set; }
        public List<int> CellIndices { get; } = new();

        public SceneTreeGroupNode(uint groupId, int x, int y, int cellIndex)
        {
            GroupId = groupId;
            MinX = x;
            MinY = y;
            MaxX = x;
            MaxY = y;
            CellIndices.Add(cellIndex);
        }

        public void Add(int x, int y, int cellIndex)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            CellIndices.Add(cellIndex);
        }
    }

    private void RebuildSceneTree(MapDocument map)
    {
        _sceneTreeDirty = false;
        _sceneTreeMap = map;
        _sceneTreeObjectNodes.Clear();
        _sceneTreeGroupNodes.Clear();

        if (map.Width <= 0 || map.Height <= 0 || map.Cells.Length <= 0)
        {
            return;
        }

        var objectDict = new Dictionary<ObjectListKey, SceneTreeObjectNode>(capacity: 2048);
        var groupDict = new Dictionary<uint, SceneTreeGroupNode>(capacity: 256);

        for (int cellIndex = 0; cellIndex < map.Cells.Length; cellIndex++)
        {
            int x = map.Width > 0 ? cellIndex % map.Width : 0;
            int y = map.Width > 0 ? cellIndex / map.Width : 0;
            NmpCellData cell = map.Cells[cellIndex];

            AccumulateSceneTreeObject(objectDict, ObjectListEntryKind.BackTile, cell.BackLibrary, cell.BackImage, cellIndex);
            AccumulateSceneTreeObject(objectDict, ObjectListEntryKind.MiddleTile, cell.MiddleLibrary, cell.MiddleImage, cellIndex);

            uint front = cell.FrontImage & 0x00FFFFFFu;
            if (front != 0)
            {
                AccumulateSceneTreeObject(objectDict, ObjectListEntryKind.FrontObject, (int)((front >> 16) & 0xFF), (int)(front & 0xFFFF), cellIndex);
            }

            uint under = cell.UnderObject & 0x00FFFFFFu;
            if (under != 0)
            {
                AccumulateSceneTreeObject(objectDict, ObjectListEntryKind.UnderObject, (int)((under >> 16) & 0xFF), (int)(under & 0xFFFF), cellIndex);
            }

            uint over = cell.OverObject & 0x00FFFFFFu;
            if (over != 0)
            {
                AccumulateSceneTreeObject(objectDict, ObjectListEntryKind.OverObject, (int)((over >> 16) & 0xFF), (int)(over & 0xFFFF), cellIndex);
            }

            uint near = cell.NearGround & 0x00FFFFFFu;
            if (near != 0)
            {
                AccumulateSceneTreeObject(objectDict, ObjectListEntryKind.NearGround, (int)((near >> 16) & 0xFF), (int)(near & 0xFFFF), cellIndex);
            }

            if (cell.Group != 0)
            {
                if (groupDict.TryGetValue(cell.Group, out SceneTreeGroupNode? node))
                {
                    node.Add(x, y, cellIndex);
                }
                else
                {
                    groupDict[cell.Group] = new SceneTreeGroupNode(cell.Group, x, y, cellIndex);
                }
            }
        }

        _sceneTreeObjectNodes.AddRange(objectDict.Values);
        _sceneTreeObjectNodes.Sort(static (a, b) =>
        {
            ObjectListKey ak = a.Key;
            ObjectListKey bk = b.Key;
            int kind = ((int)ak.Kind).CompareTo((int)bk.Kind);
            if (kind != 0) return kind;
            int lib = ak.Library.CompareTo(bk.Library);
            if (lib != 0) return lib;
            return ak.Image.CompareTo(bk.Image);
        });

        _sceneTreeGroupNodes.AddRange(groupDict.Values);
        _sceneTreeGroupNodes.Sort(static (a, b) => a.GroupId.CompareTo(b.GroupId));
    }

    private static void AccumulateSceneTreeObject(
        Dictionary<ObjectListKey, SceneTreeObjectNode> dict,
        ObjectListEntryKind kind,
        int library,
        int image,
        int cellIndex)
    {
        if (dict is null)
        {
            return;
        }

        if (image == 0)
        {
            return;
        }

        library = Math.Clamp(library, 0, 65535);
        image = Math.Clamp(image, 0, 65535);

        ObjectListKey key = new(kind, library, image);
        if (!dict.TryGetValue(key, out SceneTreeObjectNode? node))
        {
            node = new SceneTreeObjectNode(key);
            dict[key] = node;
        }

        node.CellIndices.Add(cellIndex);
    }

    private static string GetSceneTreeKindLabel(ObjectListEntryKind kind)
        => kind switch
        {
            ObjectListEntryKind.BackTile => "后景(Back)",
            ObjectListEntryKind.MiddleTile => "中景(Middle)",
            ObjectListEntryKind.FrontObject => "前景(Front)",
            ObjectListEntryKind.UnderObject => "底层对象(UnderObject)",
            ObjectListEntryKind.OverObject => "上层对象(OverObject)",
            ObjectListEntryKind.NearGround => "近地(NearGround)",
            _ => kind.ToString(),
        };

    private static uint PackSceneTreeRef24(int library, int image)
        => (uint)(((library & 0xFF) << 16) | (image & 0xFFFF));

    private void DrawSceneTreeObjects(MapDocument map, string trimmedFilter)
    {
        if (map is null)
        {
            return;
        }

        ObjectListEntryKind[] kindOrder =
        {
            ObjectListEntryKind.BackTile,
            ObjectListEntryKind.MiddleTile,
            ObjectListEntryKind.FrontObject,
            ObjectListEntryKind.UnderObject,
            ObjectListEntryKind.OverObject,
            ObjectListEntryKind.NearGround,
        };

        ImGui.BeginChild("##scene_tree_objects", new Vector2(0, 0), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);

        for (int k = 0; k < kindOrder.Length; k++)
        {
            ObjectListEntryKind kind = kindOrder[k];
            string kindLabel = GetSceneTreeKindLabel(kind);

            int refCount = 0;
            long instanceCount = 0;
            for (int i = 0; i < _sceneTreeObjectNodes.Count; i++)
            {
                SceneTreeObjectNode node = _sceneTreeObjectNodes[i];
                if (node.Key.Kind != kind)
                {
                    continue;
                }

                refCount++;
                instanceCount += node.CellIndices.Count;
            }

            if (refCount <= 0)
            {
                continue;
            }

            if (!ImGui.TreeNodeEx($"{kindLabel}  引用={refCount}  实例={instanceCount}##scene_kind_{(int)kind}"))
            {
                continue;
            }

            for (int i = 0; i < _sceneTreeObjectNodes.Count; i++)
            {
                SceneTreeObjectNode node = _sceneTreeObjectNodes[i];
                ObjectListKey key = node.Key;
                if (key.Kind != kind)
                {
                    continue;
                }

                uint packed24 = PackSceneTreeRef24(key.Library, key.Image);
                string refLabel = $"库={key.Library} 图={key.Image}  数量={node.CellIndices.Count}  (0x{packed24:X6})##scene_ref_{(int)kind}_{key.Library}_{key.Image}";
                if (!string.IsNullOrWhiteSpace(trimmedFilter)
                    && refLabel.IndexOf(trimmedFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (!ImGui.TreeNode(refLabel))
                {
                    continue;
                }

                int shown = 0;
                for (int j = 0; j < node.CellIndices.Count; j++)
                {
                    int cellIndex = node.CellIndices[j];
                    if ((uint)cellIndex >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    if (shown >= _sceneTreeMaxInstancesPerRef)
                    {
                        ImGui.TextDisabled($"仅显示前 {_sceneTreeMaxInstancesPerRef} 个实例（共 {node.CellIndices.Count}）。");
                        break;
                    }

                    int x = map.Width > 0 ? cellIndex % map.Width : 0;
                    int y = map.Width > 0 ? cellIndex / map.Width : 0;
                    bool selected = _state.SelectedCellX == x && _state.SelectedCellY == y;

                    string instLabel = $"({x},{y})##scene_inst_{(int)kind}_{key.Library}_{key.Image}_{cellIndex}";
                    if (ImGui.Selectable(instLabel, selected))
                    {
                        SelectAndCenterOnCell(x, y);
                    }

                    if (ImGui.BeginPopupContextItem($"##scene_inst_ctx_{(int)kind}_{key.Library}_{key.Image}_{cellIndex}"))
                    {
                        if (ImGui.MenuItem("定位"))
                        {
                            SelectAndCenterOnCell(x, y);
                        }

                        bool canOpen = false;
                        string openTip = string.Empty;
                        int pkg = 0;
                        int idx = 0;

                        if (!TryResolveTextureRef(kind, key.Library, key.Image, out pkg, out idx))
                        {
                            openTip = "空引用（img=0）。";
                        }
                        else if (!_textureIndex.IsReady)
                        {
                            openTip = "贴图库未就绪：请先扫描贴图库（SGL/WPF）。";
                        }
                        else
                        {
                            canOpen = _textureIndex.TryGetImageBridgeTarget(pkg, idx, out _, out _, out string bridgeError);
                            if (!canOpen)
                            {
                                openTip = string.IsNullOrWhiteSpace(bridgeError) ? "无法解析贴图来源。" : bridgeError;
                            }
                        }

                        if (ImGui.MenuItem("在 ContentEditor 中打开", null, selected: false, enabled: canOpen))
                        {
                            TryOpenTextureRefInContentEditor(GetSceneTreeKindLabel(kind), pkg, idx);
                        }

                        if (!canOpen && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(openTip))
                        {
                            ImGui.SetTooltip(openTip);
                        }

                        if (ImGui.MenuItem("复制为印章"))
                        {
                            CopyCellToClipboardStampAndFocus(map, x, y, kind);
                        }

                        if (ImGui.MenuItem("删除"))
                        {
                            DeleteSceneTreeInstance(map, kind, cellIndex);
                        }

                        ImGui.EndPopup();
                    }

                    shown++;
                }

                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        ImGui.EndChild();
    }

    private void DrawSceneTreeGroups(MapDocument map, string trimmedFilter)
    {
        if (map is null)
        {
            return;
        }

        if (_sceneTreeGroupNodes.Count <= 0)
        {
            ImGui.TextDisabled("当前地图未发现 Group 标记（NMP cell.Group）。");
            ImGui.TextDisabled("说明：旧工程可能用它来表达 prefab/组链接，这里先按 GroupId 聚合展示。");
            return;
        }

        ImGui.BeginChild("##scene_tree_groups", new Vector2(0, 0), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
        for (int i = 0; i < _sceneTreeGroupNodes.Count; i++)
        {
            SceneTreeGroupNode node = _sceneTreeGroupNodes[i];
            string label = $"0x{node.GroupId:X8}  格子={node.CellIndices.Count}  区域=({node.MinX},{node.MinY})-({node.MaxX},{node.MaxY})##scene_grp_{node.GroupId}";
            if (!string.IsNullOrWhiteSpace(trimmedFilter)
                && label.IndexOf(trimmedFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            bool open = ImGui.TreeNode(label);
            if (ImGui.BeginPopupContextItem($"##scene_grp_ctx_{node.GroupId}"))
            {
                if (ImGui.MenuItem("定位样本"))
                {
                    int sampleIndex = node.CellIndices.Count > 0 ? node.CellIndices[0] : -1;
                    int x = map.Width > 0 ? sampleIndex % map.Width : 0;
                    int y = map.Width > 0 ? sampleIndex / map.Width : 0;
                    SelectAndCenterOnCell(x, y);
                }

                if (ImGui.MenuItem("框选区域"))
                {
                    _state.SelectionX0 = node.MinX;
                    _state.SelectionY0 = node.MinY;
                    _state.SelectionX1 = node.MaxX;
                    _state.SelectionY1 = node.MaxY;
                    _state.HasSelection = true;
                    _state.StatusMessage = $"已框选 Group=0x{node.GroupId:X8}：({node.MinX},{node.MinY})-({node.MaxX},{node.MaxY})";
                }

                if (ImGui.MenuItem("复制为印章（按区域框选）"))
                {
                    _state.SelectionX0 = node.MinX;
                    _state.SelectionY0 = node.MinY;
                    _state.SelectionX1 = node.MaxX;
                    _state.SelectionY1 = node.MaxY;
                    _state.HasSelection = true;
                    CopySelectionToClipboardStamp();
                }

                ImGui.EndPopup();
            }

            if (!open)
            {
                continue;
            }

            ImGui.TextDisabled("右键可定位/框选/复制。展开后仅列出部分格子坐标。");

            int shown = 0;
            for (int j = 0; j < node.CellIndices.Count; j++)
            {
                int cellIndex = node.CellIndices[j];
                if ((uint)cellIndex >= (uint)map.Cells.Length)
                {
                    continue;
                }

                if (shown >= _sceneTreeMaxInstancesPerRef)
                {
                    ImGui.TextDisabled($"仅显示前 {_sceneTreeMaxInstancesPerRef} 个实例（共 {node.CellIndices.Count}）。");
                    break;
                }

                int x = map.Width > 0 ? cellIndex % map.Width : 0;
                int y = map.Width > 0 ? cellIndex / map.Width : 0;
                bool selected = _state.SelectedCellX == x && _state.SelectedCellY == y;

                string instLabel = $"({x},{y})##scene_grp_inst_{node.GroupId}_{cellIndex}";
                if (ImGui.Selectable(instLabel, selected))
                {
                    SelectAndCenterOnCell(x, y);
                }

                shown++;
            }

            ImGui.TreePop();
        }
        ImGui.EndChild();
    }

    private void SelectAndCenterOnCell(int x, int y)
    {
        _state.SelectedCellX = x;
        _state.SelectedCellY = y;
        _requestCenterOnCell = true;
        _requestCenterCellX = x;
        _requestCenterCellY = y;
    }

    private void CopyCellToClipboardStampAndFocus(MapDocument map, int x, int y, ObjectListEntryKind kind)
    {
        if (map is null)
        {
            return;
        }

        bool prevHasSelection = _state.HasSelection;
        int prevX0 = _state.SelectionX0;
        int prevY0 = _state.SelectionY0;
        int prevX1 = _state.SelectionX1;
        int prevY1 = _state.SelectionY1;

        _state.SelectionX0 = x;
        _state.SelectionY0 = y;
        _state.SelectionX1 = x;
        _state.SelectionY1 = y;
        _state.HasSelection = true;

        CopySelectionToClipboardStamp();

        _state.HasSelection = prevHasSelection;
        _state.SelectionX0 = prevX0;
        _state.SelectionY0 = prevY0;
        _state.SelectionX1 = prevX1;
        _state.SelectionY1 = prevY1;

        _state.StampApplyBack = kind == ObjectListEntryKind.BackTile;
        _state.StampApplyMiddle = kind == ObjectListEntryKind.MiddleTile;
        _state.StampApplyFront = kind == ObjectListEntryKind.FrontObject;
        _state.StampApplyUnderObject = kind == ObjectListEntryKind.UnderObject;
        _state.StampApplyOverObject = kind == ObjectListEntryKind.OverObject;
        _state.StampApplyNearGround = kind == ObjectListEntryKind.NearGround;
    }

    private void DeleteSceneTreeInstance(MapDocument map, ObjectListEntryKind kind, int cellIndex)
    {
        if (map is null)
        {
            return;
        }

        if ((uint)cellIndex >= (uint)map.Cells.Length)
        {
            return;
        }

        int x = map.Width > 0 ? cellIndex % map.Width : 0;
        int y = map.Width > 0 ? cellIndex / map.Width : 0;

        NmpCellData before = map.Cells[cellIndex];
        NmpCellData after = before;

        bool changed = kind switch
        {
            ObjectListEntryKind.BackTile => TryApplyLayerEdit(ref after, MapLayer.Back, 0, 0, clear: true),
            ObjectListEntryKind.MiddleTile => TryApplyLayerEdit(ref after, MapLayer.Middle, 0, 0, clear: true),
            ObjectListEntryKind.FrontObject => TryApplyLayerEdit(ref after, MapLayer.Front, 0, 0, clear: true),
            ObjectListEntryKind.UnderObject => TryClearUnderObject(ref after),
            ObjectListEntryKind.OverObject => TryClearOverObject(ref after),
            ObjectListEntryKind.NearGround => TryClearNearGround(ref after),
            _ => false,
        };

        if (!changed)
        {
            _state.StatusMessage = "未发生变化。";
            return;
        }

        map.Cells[cellIndex] = after;

        string kindLabel = GetSceneTreeKindLabel(kind);
        _state.History.Push(new MultiCellEditAction($"删除 {kindLabel} ({x},{y})", map,
            indices: new[] { cellIndex },
            before: new[] { before },
            after: new[] { after }));

        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = $"已删除：{kindLabel} ({x},{y})";
        InvalidateRuntimeMinimapActiveDocument();
    }

    private void RebuildObjectList(MapDocument map)
    {
        _objectListDirty = false;
        _objectListEntries.Clear();

        if (map.Width <= 0 || map.Height <= 0 || map.Cells.Length <= 0)
        {
            return;
        }

        var dict = new Dictionary<ObjectListKey, ObjectListAccumulator>(capacity: 2048);

        for (int index = 0; index < map.Cells.Length; index++)
        {
            int x = map.Width > 0 ? index % map.Width : 0;
            int y = map.Width > 0 ? index / map.Width : 0;
            NmpCellData cell = map.Cells[index];

            AccumulateObjectEntry(dict, ObjectListEntryKind.BackTile, cell.BackLibrary, cell.BackImage, x, y);
            AccumulateObjectEntry(dict, ObjectListEntryKind.MiddleTile, cell.MiddleLibrary, cell.MiddleImage, x, y);

            uint front = cell.FrontImage & 0x00FFFFFFu;
            if (front != 0)
            {
                AccumulateObjectEntry(dict, ObjectListEntryKind.FrontObject, (int)((front >> 16) & 0xFF), (int)(front & 0xFFFF), x, y);
            }

            uint under = cell.UnderObject & 0x00FFFFFFu;
            if (under != 0)
            {
                AccumulateObjectEntry(dict, ObjectListEntryKind.UnderObject, (int)((under >> 16) & 0xFF), (int)(under & 0xFFFF), x, y);
            }

            uint over = cell.OverObject & 0x00FFFFFFu;
            if (over != 0)
            {
                AccumulateObjectEntry(dict, ObjectListEntryKind.OverObject, (int)((over >> 16) & 0xFF), (int)(over & 0xFFFF), x, y);
            }

            uint near = cell.NearGround & 0x00FFFFFFu;
            if (near != 0)
            {
                AccumulateObjectEntry(dict, ObjectListEntryKind.NearGround, (int)((near >> 16) & 0xFF), (int)(near & 0xFFFF), x, y);
            }
        }

        foreach (KeyValuePair<ObjectListKey, ObjectListAccumulator> pair in dict)
        {
            ObjectListKey key = pair.Key;
            ObjectListAccumulator acc = pair.Value;
            _objectListEntries.Add(new ObjectListEntry(key.Kind, key.Library, key.Image, acc.Count, acc.SampleX, acc.SampleY));
        }

        _objectListEntries.Sort(static (a, b) =>
        {
            int kind = ((int)a.Kind).CompareTo((int)b.Kind);
            if (kind != 0) return kind;
            int lib = a.Library.CompareTo(b.Library);
            if (lib != 0) return lib;
            return a.Image.CompareTo(b.Image);
        });
    }

    private static void AccumulateObjectEntry(
        Dictionary<ObjectListKey, ObjectListAccumulator> dict,
        ObjectListEntryKind kind,
        int library,
        int image,
        int x,
        int y)
    {
        if (dict is null)
        {
            return;
        }

        if (image == 0)
        {
            return;
        }

        library = Math.Clamp(library, 0, 65535);
        image = Math.Clamp(image, 0, 65535);

        ObjectListKey key = new(kind, library, image);
        if (dict.TryGetValue(key, out ObjectListAccumulator acc))
        {
            acc.Count++;
            dict[key] = acc;
            return;
        }

        dict[key] = new ObjectListAccumulator { Count = 1, SampleX = x, SampleY = y };
    }

    private void HandleGlobalShortcuts()
    {
        if (_state.Map is null)
        {
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput)
        {
            return;
        }

        // When rebinding keys, don't fire global shortcuts.
        if (_state.KeyBindListeningIndex >= 0)
        {
            return;
        }

        MapEditorKeyBindings kb = _state.KeyBindings;

        if (_moveSelection is not null && IsBindingPressed(kb.ToolCancel, io, repeat: false))
        {
            ClearMoveSelectionSession(
                restorePreviousStampSource: true,
                restoreTool: true,
                updateStatus: true,
                statusMessage: "已取消移动选区（可 Ctrl+Z 撤销剪切）。");
            return;
        }

        if (IsBindingPressed(kb.ZoomIn, io, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd, repeat: false))
        {
            _state.Camera.Zoom = Math.Min(_state.Camera.MaxZoom, _state.Camera.Zoom * 1.25f);
        }

        if (IsBindingPressed(kb.ZoomOut, io, repeat: false) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract, repeat: false))
        {
            _state.Camera.Zoom = Math.Max(_state.Camera.MinZoom, _state.Camera.Zoom / 1.25f);
        }

        if (IsBindingPressed(kb.ResetView, io, repeat: false))
        {
            _state.Camera.Zoom = Math.Clamp(1.0f, _state.Camera.MinZoom, _state.Camera.MaxZoom);
            _state.CameraNeedsFit = true;
        }

        if (IsBindingPressed(kb.ToolBlockedEditor, io, repeat: false))
        {
            _state.Tool = MapEditTool.BlockedEditor;
            _state.ShowBlockedOverlay = true;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _blockedDrag = null;
        }

        if (IsBindingPressed(kb.ToolSelection, io, repeat: false))
        {
            _state.Tool = MapEditTool.Select;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _blockedDrag = null;
        }

        if (IsBindingPressed(kb.ToolErase, io, repeat: false))
        {
            _state.Tool = MapEditTool.Erase;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _blockedDrag = null;
        }

        if (IsBindingPressed(kb.ToolStamp, io, repeat: false))
        {
            _state.Tool = MapEditTool.Stamp;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _blockedDrag = null;
        }

        if (IsBindingPressed(kb.ToolTilePaint, io, repeat: false))
        {
            _state.Tool = MapEditTool.Pencil;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _blockedDrag = null;
        }

        if (IsBindingPressed(kb.ToolCancel, io, repeat: false))
        {
            CancelActiveEditsAndDeselect();
        }

        if (_moveSelection is null
            && IsBindingPressed(kb.DeleteSelection, io, repeat: false)
            && _state.HasSelection
            && _state.Tool is MapEditTool.Select or MapEditTool.Erase)
        {
            EraseSelectionRegion();
        }

        if (IsBindingPressed(kb.Undo, io, repeat: false))
        {
            TryUndo();
        }

        bool redoFallback = io.KeyCtrl
            && io.KeyShift
            && kb.Undo.Key != ImGuiKey.None
            && ImGui.IsKeyPressed(kb.Undo.Key, repeat: false);
        if (IsBindingPressed(kb.Redo, io, repeat: false) || redoFallback)
        {
            TryRedo();
        }

        bool saveAsReserved = kb.Save.Key == ImGuiKey.S && kb.Save.Mods == (KeyModFlags.Ctrl | KeyModFlags.Shift);
        if (!saveAsReserved
            && _state.Map is not null
            && io.KeyCtrl
            && io.KeyShift
            && ImGui.IsKeyPressed(ImGuiKey.S, repeat: false))
        {
            if (IsActiveDocumentPrefab())
            {
                BeginSavePrefabAsPopup();
            }
            else
            {
                _requestSaveAsPopup = true;
            }
        }

        if (_state.Map is not null && !_state.History.IsAtSavePoint && IsBindingPressed(kb.Save, io, repeat: false))
        {
            TrySaveActiveDocument();
        }

        bool ctrl = io.KeyCtrl;
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C, repeat: false))
        {
            if (_moveSelection is null && _state.HasSelection)
            {
                CopySelectionToClipboardStamp();
            }
        }

        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.X, repeat: false))
        {
            if (_moveSelection is null && _state.HasSelection)
            {
                MoveSelectionToMoveStamp();
            }
        }

        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V, repeat: false))
        {
            if (_moveSelection is null && _clipboardStamp is not null)
            {
                _state.StampSource = StampSourceKind.Clipboard;
                _state.Tool = MapEditTool.Stamp;
                _paintDrag = null;
                _rectFillDrag = null;
                _selectionDrag = null;
                _blockedDrag = null;
                ClearStamp();
                _state.StatusMessage = "已切换为剪贴板印章：点击地图可粘贴。";
            }
        }
    }

    private static bool IsBindingPressed(KeyBinding binding, ImGuiIOPtr io, bool repeat)
    {
        if (binding.Key == ImGuiKey.None)
        {
            return false;
        }

        if (!ImGui.IsKeyPressed(binding.Key, repeat))
        {
            return false;
        }

        bool needsCtrl = (binding.Mods & KeyModFlags.Ctrl) != 0;
        bool needsShift = (binding.Mods & KeyModFlags.Shift) != 0;
        bool needsAlt = (binding.Mods & KeyModFlags.Alt) != 0;

        if (needsCtrl && !io.KeyCtrl)
        {
            return false;
        }
        if (needsShift && !io.KeyShift)
        {
            return false;
        }
        if (needsAlt && !io.KeyAlt)
        {
            return false;
        }

        // Match old behavior: modifiers not required must not be held (prevents Ctrl+Z triggering plain Z).
        if (!needsCtrl && io.KeyCtrl)
        {
            return false;
        }
        if (!needsShift && io.KeyShift)
        {
            return false;
        }
        if (!needsAlt && io.KeyAlt)
        {
            return false;
        }

        return true;
    }

    private void CancelActiveEditsAndDeselect()
    {
        MapDocument? map = _state.Map;

        if (_paintDrag is not null)
        {
            if (map is not null && ReferenceEquals(_paintDrag.Map, map))
            {
                foreach ((int index, NmpCellData before) in _paintDrag.BeforeCells)
                {
                    if ((uint)index < (uint)map.Cells.Length)
                    {
                        map.Cells[index] = before;
                    }
                }
            }

            _paintDrag = null;
        }

        if (_blockedDrag is not null)
        {
            if (map is not null && ReferenceEquals(_blockedDrag.Map, map))
            {
                foreach ((int index, NmpCellData before) in _blockedDrag.BeforeCells)
                {
                    if ((uint)index < (uint)map.Cells.Length)
                    {
                        map.Cells[index] = before;
                    }
                }
            }

            _blockedDrag = null;
        }

        _rectFillDrag = null;
        _selectionDrag = null;

        ClearMoveSelectionSession(restorePreviousStampSource: true, restoreTool: false, updateStatus: false, statusMessage: null);

        _state.Tool = MapEditTool.Select;
        _state.HasSelection = false;

        _state.StampSource = StampSourceKind.Path;
        _state.StampPath = string.Empty;
        ClearStamp();

        _objectListDirty = true;
        _sceneTreeDirty = true;
        InvalidateRuntimeMinimapActiveDocument();

        _state.StatusMessage = "已取消当前操作。";
    }

    private void TryUndo()
    {
        if (_state.Map is null)
        {
            return;
        }

        if (_state.History.TryUndo(out string actionName, out string error))
        {
            _objectListDirty = true;
            _sceneTreeDirty = true;
            _state.StatusMessage = string.IsNullOrWhiteSpace(actionName)
                ? "已撤销。"
                : $"已撤销：{actionName}";
            InvalidateRuntimeMinimapActiveDocument();
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            _state.StatusMessage = error;
        }
    }

    private void TryRedo()
    {
        if (_state.Map is null)
        {
            return;
        }

        if (_state.History.TryRedo(out string actionName, out string error))
        {
            _objectListDirty = true;
            _sceneTreeDirty = true;
            _state.StatusMessage = string.IsNullOrWhiteSpace(actionName)
                ? "已重做。"
                : $"已重做：{actionName}";
            InvalidateRuntimeMinimapActiveDocument();
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            _state.StatusMessage = error;
        }
    }

    private void ApplyEditToSelectedCell(bool clear)
    {
        if (_state.Map is null)
        {
            _state.StatusMessage = "未加载地图。";
            return;
        }

        MapDocument map = _state.Map;
        int x = _state.SelectedCellX;
        int y = _state.SelectedCellY;
        if (!map.IsInBounds(x, y))
        {
            _state.StatusMessage = "未选择格子（请先在地图视图中左键点击选择）。";
            return;
        }

        int index = map.GetIndex(x, y);
        if ((uint)index >= (uint)map.Cells.Length)
        {
            _state.StatusMessage = "选中格子索引越界。";
            return;
        }

        NmpCellData before = map.Cells[index];
        NmpCellData after = before;

        if (!TryApplyLayerEdit(ref after, _state.EditLayer, _state.PaintLibrary, _state.PaintImage, clear))
        {
            _state.StatusMessage = "未发生变化。";
            return;
        }

        map.Cells[index] = after;

        string actionName = clear ? $"清空 {_state.EditLayer} ({x},{y})" : $"编辑 {_state.EditLayer} ({x},{y})";
        _state.History.Push(new MultiCellEditAction(actionName, map,
            indices: new[] { index },
            before: new[] { before },
            after: new[] { after }));

        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = clear ? "已清空选中格。" : "已应用到选中格。";
        InvalidateRuntimeMinimapActiveDocument();
    }

    private static bool TryApplyLayerEdit(ref NmpCellData cell, MapLayer layer, int library, int image, bool clear)
    {
        library = Math.Clamp(library, 0, 65535);
        image = Math.Clamp(image, 0, 65535);

        switch (layer)
        {
            case MapLayer.Back:
            {
                ushort desiredImage = clear ? (ushort)0 : (ushort)image;
                ushort desiredLibrary = desiredImage == 0 ? (ushort)0 : (ushort)library;
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultSmTilesLibrary;
                }

                if (cell.BackImage == desiredImage && cell.BackLibrary == desiredLibrary)
                {
                    return false;
                }

                cell.BackImage = desiredImage;
                cell.BackLibrary = desiredLibrary;
                cell.Flags = desiredImage == 0 ? (byte)(cell.Flags & ~FlagSmTiles) : (byte)(cell.Flags | FlagSmTiles);
                return true;
            }
            case MapLayer.Middle:
            {
                ushort desiredImage = clear ? (ushort)0 : (ushort)image;
                ushort desiredLibrary = desiredImage == 0 ? (ushort)0 : (ushort)library;
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultTilesLibrary;
                }

                if (cell.MiddleImage == desiredImage && cell.MiddleLibrary == desiredLibrary)
                {
                    return false;
                }

                cell.MiddleImage = desiredImage;
                cell.MiddleLibrary = desiredLibrary;
                cell.Flags = desiredImage == 0 ? (byte)(cell.Flags & ~FlagTiles) : (byte)(cell.Flags | FlagTiles);
                return true;
            }
            case MapLayer.Floor:
            {
                ushort desiredImage = clear ? (ushort)0 : (ushort)image;
                byte desiredLibrary = desiredImage == 0 ? (byte)0 : (byte)Math.Clamp(library, 0, 255);
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultObjectLibrary;
                }

                uint desiredPacked = desiredImage == 0 ? 0u : (((uint)desiredLibrary << 16) | desiredImage);
                uint beforePacked = cell.NearGround & 0x00FFFFFFu;
                byte beforeFlags = cell.Flags;
                byte desiredFlags = desiredPacked == 0
                    ? (byte)(beforeFlags & ~NmpFlagNearGround)
                    : (byte)(beforeFlags | NmpFlagNearGround);

                if (beforePacked == desiredPacked && beforeFlags == desiredFlags)
                {
                    return false;
                }

                cell.NearGround = (cell.NearGround & 0xFF000000u) | desiredPacked;
                cell.Flags = desiredFlags;
                return true;
            }
            case MapLayer.UnderFront:
            {
                ushort desiredImage = clear ? (ushort)0 : (ushort)image;
                byte desiredLibrary = desiredImage == 0 ? (byte)0 : (byte)Math.Clamp(library, 0, 255);
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultObjectLibrary;
                }

                uint desiredPacked = desiredImage == 0 ? 0u : (((uint)desiredLibrary << 16) | desiredImage);
                uint beforePacked = cell.UnderObject & 0x00FFFFFFu;
                byte beforeFlags = cell.Flags;
                byte desiredFlags = desiredPacked == 0
                    ? (byte)(beforeFlags & ~NmpFlagUnderObject)
                    : (byte)(beforeFlags | NmpFlagUnderObject);

                if (beforePacked == desiredPacked && beforeFlags == desiredFlags)
                {
                    return false;
                }

                cell.UnderObject = (cell.UnderObject & 0xFF000000u) | desiredPacked;
                cell.Flags = desiredFlags;
                return true;
            }
            case MapLayer.Front:
            {
                ushort desiredImage = clear ? (ushort)0 : (ushort)image;
                byte desiredLibrary = desiredImage == 0 ? (byte)0 : (byte)Math.Clamp(library, 0, 255);
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultObjectLibrary;
                }

                uint desiredPacked = desiredImage == 0 ? 0u : (((uint)desiredLibrary << 16) | desiredImage);
                if (cell.FrontLibrary == desiredLibrary && (cell.FrontImage & 0x00FFFFFFu) == desiredPacked)
                {
                    return false;
                }

                cell.FrontLibrary = desiredLibrary;
                cell.FrontImage = desiredPacked;
                cell.Flags = desiredImage == 0 ? (byte)(cell.Flags & ~FlagObject) : (byte)(cell.Flags | FlagObject);
                return true;
            }
            case MapLayer.OverFront:
            {
                ushort desiredImage = clear ? (ushort)0 : (ushort)image;
                byte desiredLibrary = desiredImage == 0 ? (byte)0 : (byte)Math.Clamp(library, 0, 255);
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultObjectLibrary;
                }

                uint desiredPacked = desiredImage == 0 ? 0u : (((uint)desiredLibrary << 16) | desiredImage);
                uint beforePacked = cell.OverObject & 0x00FFFFFFu;
                ushort beforeExAttr = cell.ExtendedAttributes;
                ushort desiredExAttr = desiredPacked == 0
                    ? (ushort)(beforeExAttr & ~NmpExAttrOverObject)
                    : (ushort)(beforeExAttr | NmpExAttrOverObject);
                bool clearColor = desiredPacked == 0 && cell.ColorOverObj != 0;

                if (beforePacked == desiredPacked && beforeExAttr == desiredExAttr && !clearColor)
                {
                    return false;
                }

                cell.OverObject = (cell.OverObject & 0xFF000000u) | desiredPacked;
                cell.ExtendedAttributes = desiredExAttr;
                if (desiredPacked == 0)
                {
                    cell.ColorOverObj = 0;
                }
                return true;
            }
            default:
                return false;
        }
    }

    private void BuildMapViewWindow()
    {
        if (!ImGui.Begin("地图视图"))
        {
            ImGui.End();
            return;
        }

        if (_documents.Count <= 0)
        {
            ImGui.TextUnformatted("未打开地图。");
            ImGui.Separator();
            if (ImGui.Button("打开路径..."))
            {
                _requestOpenByPathPopup = true;
            }

            ImGui.End();
            return;
        }

        if (_activeDocumentIndex < 0 || _activeDocumentIndex >= _documents.Count)
        {
            ActivateDocument(0);
        }

        List<int> closeIndices = new();
        if (ImGui.BeginTabBar("##map_docs"))
        {
            for (int i = 0; i < _documents.Count; i++)
            {
                MapEditorDocument doc = _documents[i];

                string name = doc.DisplayName?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    string displayPath = doc.Path;
                    if (string.IsNullOrWhiteSpace(displayPath) && doc.Map is not null)
                    {
                        displayPath = doc.Map.Path;
                    }

                    name = Path.GetFileName(displayPath);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = displayPath;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = doc.IsPrefabDocument ? "New Prefab" : "New Map";
                    }
                }

                if (doc.Map is not null && !doc.History.IsAtSavePoint)
                {
                    name = $"{name}*";
                }

                if (doc.Map is null && !string.IsNullOrWhiteSpace(doc.MapLoadError))
                {
                    name = $"{name}（加载失败）";
                }

                string label = $"{name}##{doc.NormalizedPath}";
                bool open = true;
                if (ImGui.BeginTabItem(label, ref open))
                {
                    if (_activeDocumentIndex != i)
                    {
                        ActivateDocument(i);
                    }

                    if (ImGui.Button("打开路径..."))
                    {
                        _requestOpenByPathPopup = true;
                    }

                    if (!string.IsNullOrWhiteSpace(_state.MapPath))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(_state.MapPath);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(_state.MapPath);
                        }
                    }

                    ImGui.Separator();

                    if (_state.Map is null)
                    {
                        ImGui.TextUnformatted("未打开地图。");

                        if (!string.IsNullOrWhiteSpace(_state.MapLoadError))
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "错误：");
                            ImGui.TextWrapped(_state.MapLoadError);
                        }
                    }
                    else
                    {
                        MapDocument map = _state.Map;
                        string mapName = doc.DisplayName?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(mapName))
                        {
                            mapName = Path.GetFileName(_state.MapPath);
                            if (string.IsNullOrWhiteSpace(mapName))
                            {
                                mapName = Path.GetFileName(map.Path);
                            }
                        }

                        ImGui.TextUnformatted($"{mapName}  {map.Width} x {map.Height}  v{map.Version}");
                        ImGui.Separator();
                        DrawMapCanvas(map);
                    }

                    ImGui.EndTabItem();
                }

                if (!open)
                {
                    closeIndices.Add(i);
                }
            }

            ImGui.EndTabBar();
        }

        for (int i = closeIndices.Count - 1; i >= 0; i--)
        {
            CloseDocument(closeIndices[i]);
        }

        ImGui.End();
    }

    private void DrawMapCanvas(MapDocument map)
    {
        Vector2 canvasScreenPos = ImGui.GetCursorScreenPos();
        Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 32.0f || canvasSize.Y < 32.0f)
        {
            ImGui.TextDisabled("视图区域太小。");
            return;
        }

        ImGui.InvisibleButton("##map_canvas", canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);

        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        HandleCanvasDragDrop(map, canvasScreenPos);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        Vector2 canvasMin = canvasScreenPos;
        Vector2 canvasMax = canvasScreenPos + canvasSize;
        drawList.AddRectFilled(canvasMin, canvasMax, PackColor(10, 10, 12, 255));
        drawList.PushClipRect(canvasMin, canvasMax, true);

        MapCamera camera = _state.Camera;
        camera.ClampZoom();

        if (_state.CameraNeedsFit)
        {
            FitMapToCanvas(map, canvasSize);
            _state.CameraNeedsFit = false;
        }

        if (_requestCenterOnCell)
        {
            CenterCameraOnCell(canvasSize, _requestCenterCellX, _requestCenterCellY);
            _requestCenterOnCell = false;
        }

        MapEditorDocument? doc = GetActiveDocument();
        if (doc is not null)
        {
            PumpRuntimeMinimapBuild(doc, map, nowSeconds: ImGui.GetTime());
        }

        bool minimapConsumed = false;
        MinimapOverlayLayout minimapLayout = default;
        bool hasMinimap = doc is not null && TryComputeMinimapOverlayLayout(map, canvasMin, canvasMax, out minimapLayout);
        if (hasMinimap)
        {
            minimapConsumed = HandleMinimapOverlayInput(doc!, minimapLayout, canvasSize);
        }

        if (!minimapConsumed)
        {
            HandleCanvasInput(map, hovered, active, canvasScreenPos, canvasSize);
            UpdateHoverCell(map, hovered, canvasScreenPos);
        }

        DrawMapTilesAndGrid(map, drawList, canvasScreenPos, canvasSize);
        DrawSelectionOverlay(map, drawList, canvasScreenPos);
        DrawStampPreviewOverlay(map, drawList, canvasScreenPos);

        if (hasMinimap)
        {
            DrawMinimapOverlay(doc!, map, drawList, canvasSize, minimapLayout);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            _mapCanvasMiddleDragPastThreshold = false;
        }

        if ((hovered || active) && ImGui.IsMouseDown(ImGuiMouseButton.Middle) && !_mapCanvasMiddleDragPastThreshold)
        {
            Vector2 delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Middle, 0.0f);
            const float threshold = 6.0f;
            if ((delta.X * delta.X + delta.Y * delta.Y) > (threshold * threshold))
            {
                _mapCanvasMiddleDragPastThreshold = true;
            }
        }

        Vector2 rightDragDelta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right, 0.0f);
        const float rightClickThreshold = 6.0f;
        bool rightDragPastThreshold = (rightDragDelta.X * rightDragDelta.X + rightDragDelta.Y * rightDragDelta.Y)
            > (rightClickThreshold * rightClickThreshold);

        bool openContextMenu = (hovered || active)
            && !minimapConsumed
            && ImGui.IsMouseReleased(ImGuiMouseButton.Right)
            && !rightDragPastThreshold;

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
        {
            _mapCanvasMiddleDragPastThreshold = false;
        }

        drawList.PopClipRect();

        if (openContextMenu)
        {
            ImGui.OpenPopup("MapCanvasContextMenu");
        }

        if (ImGui.BeginPopup("MapCanvasContextMenu"))
        {
            bool canUndo = _state.Map is not null && _state.History.UndoCount > 0;
            bool canRedo = _state.Map is not null && _state.History.RedoCount > 0;

            if (ImGui.BeginMenu("编辑工具(Editing Tools)"))
            {
                static void ClearDrags(MapEditorApp app)
                {
                    app._paintDrag = null;
                    app._rectFillDrag = null;
                    app._selectionDrag = null;
                    app._blockedDrag = null;
                }

                if (ImGui.MenuItem("选择/Inspect", null, selected: _state.Tool == MapEditTool.Select))
                {
                    _state.Tool = MapEditTool.Select;
                    ClearDrags(this);
                }

                if (ImGui.MenuItem("画笔(Pencil)", null, selected: _state.Tool == MapEditTool.Pencil))
                {
                    _state.Tool = MapEditTool.Pencil;
                    ClearDrags(this);
                }

                if (ImGui.MenuItem("矩形填充(Rect)", null, selected: _state.Tool == MapEditTool.RectFill))
                {
                    _state.Tool = MapEditTool.RectFill;
                    ClearDrags(this);
                }

                if (ImGui.MenuItem("印章(Stamp)", null, selected: _state.Tool == MapEditTool.Stamp))
                {
                    _state.Tool = MapEditTool.Stamp;
                    ClearDrags(this);
                }

                if (ImGui.MenuItem("阻挡编辑(Blocked)", null, selected: _state.Tool == MapEditTool.BlockedEditor))
                {
                    _state.Tool = MapEditTool.BlockedEditor;
                    _state.ShowBlockedOverlay = true;
                    ClearDrags(this);
                }

                if (ImGui.MenuItem("擦除(Erase)", null, selected: _state.Tool == MapEditTool.Erase))
                {
                    _state.Tool = MapEditTool.Erase;
                    ClearDrags(this);
                }

                ImGui.Separator();

                if (ImGui.MenuItem("取消操作/取消选择(Deselect)", null, selected: false, enabled: _state.Map is not null))
                {
                    CancelActiveEditsAndDeselect();
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("编辑(Edit)"))
            {
                string undoLabel = string.IsNullOrWhiteSpace(_state.History.PeekUndoName)
                    ? "撤销"
                    : $"撤销：{_state.History.PeekUndoName}";

                string redoLabel = string.IsNullOrWhiteSpace(_state.History.PeekRedoName)
                    ? "重做"
                    : $"重做：{_state.History.PeekRedoName}";

                if (ImGui.MenuItem(undoLabel, "Ctrl+Z", selected: false, enabled: canUndo))
                {
                    TryUndo();
                }

                if (ImGui.MenuItem(redoLabel, "Ctrl+Y", selected: false, enabled: canRedo))
                {
                    TryRedo();
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("图层(Layers)"))
            {
                DrawLayersMenuItems("##ctx");

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("光照(Lighting)"))
            {
                DrawLightingMenuItems("##ctx");
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("覆盖(Overlays)"))
            {
                bool showGrid = _state.ShowGrid;
                if (ImGui.MenuItem("显示网格(Show Grid)", null, selected: showGrid))
                {
                    _state.ShowGrid = !showGrid;
                }

                bool showFill = _state.ShowTileFill;
                if (ImGui.MenuItem("显示填充(Show Tile Fill)", null, selected: showFill))
                {
                    _state.ShowTileFill = !showFill;
                }

                bool showBlocked = _state.ShowBlockedOverlay;
                if (ImGui.MenuItem("显示阻挡(Show Blocked)", null, selected: showBlocked))
                {
                    _state.ShowBlockedOverlay = !showBlocked;
                }

                bool showMinimap = _state.ShowMinimapOverlay;
                if (ImGui.MenuItem("显示小地图(Show Minimap)", null, selected: showMinimap))
                {
                    _state.ShowMinimapOverlay = !showMinimap;
                    if (_state.ShowMinimapOverlay)
                    {
                        InvalidateRuntimeMinimapActiveDocument();
                    }
                }

                ImGui.Separator();
                DrawHighlightOverlayMenuItems();

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("网格(Grid)"))
            {
                DrawGridSettingsMenuItems("##ctx");
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("阻挡(Blocked Overlay)"))
            {
                DrawBlockedOverlaySettingsMenuItems("##ctx");
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("格子高亮(Cell Highlights)"))
            {
                DrawCellHighlightSettingsMenuItems("##ctx");
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("动画(Animation)"))
            {
                DrawAnimationSettingsMenuItems("##ctx");
                ImGui.EndMenu();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("适配视图(Fit)"))
            {
                _state.CameraNeedsFit = true;
            }

            ImGui.EndPopup();
        }
    }

    private const double RuntimeMinimapRebuildDelaySeconds = 0.20;

    private readonly record struct MinimapOverlayLayout(Vector2 Min, Vector2 Max, float CellPxW, float CellPxH);

    private readonly record struct RuntimeMinimapBuildResult(int Revision, int Width, int Height, byte[] Rgba8, MinimapExportDiagnostics Diagnostics, string Error);

    private void InvalidateRuntimeMinimapActiveDocument()
    {
        MapEditorDocument? doc = GetActiveDocument();
        if (doc is null)
        {
            return;
        }

        InvalidateRuntimeMinimap(doc);
    }

    private void InvalidateAllRuntimeMinimaps()
    {
        for (int i = 0; i < _documents.Count; i++)
        {
            InvalidateRuntimeMinimap(_documents[i]);
        }
    }

    private static void InvalidateRuntimeMinimap(MapEditorDocument doc)
    {
        if (doc is null)
        {
            return;
        }

        unchecked
        {
            doc.RuntimeMinimapRevision++;
        }
        doc.RuntimeMinimapDirty = true;
        doc.RuntimeMinimapDirtyStampSet = false;
    }

    private ulong ComputeRuntimeMinimapSettingsHash()
    {
        // This hash is used only within the current process to detect changes and rebuild the overlay.
        const ulong FnvOffset = 14695981039346656037ul;
        const ulong FnvPrime = 1099511628211ul;

        static void Add(ref ulong h, ulong v)
        {
            h ^= v;
            h *= FnvPrime;
        }

        static void AddBool(ref ulong h, bool v) => Add(ref h, v ? 1ul : 0ul);
        static void AddInt(ref ulong h, int v) => Add(ref h, unchecked((uint)v));
        static void AddFloat(ref ulong h, float v) => AddInt(ref h, BitConverter.SingleToInt32Bits(v));

        static void AddStringIgnoreCase(ref ulong h, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Add(ref h, 0ul);
                return;
            }

            string s = value.Trim();
            AddInt(ref h, s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                AddInt(ref h, char.ToLowerInvariant(s[i]));
            }
        }

        ulong hash = FnvOffset;

        AddStringIgnoreCase(ref hash, _textureIndex.RootDirectory);
        AddInt(ref hash, (int)_textureIndex.TextureSourceMode);
        AddBool(ref hash, _textureIndex.CoastMaskPreferTex);
        AddBool(ref hash, _textureIndex.IsReady);

        AddBool(ref hash, _state.ShowBackLayer);
        AddBool(ref hash, _state.ShowMiddleLayer);
        AddBool(ref hash, _state.ShowFloorLayer);
        AddBool(ref hash, _state.ShowUnderFrontLayer);
        AddBool(ref hash, _state.ShowFrontLayer);
        AddBool(ref hash, _state.ShowOverFrontLayer);
        AddBool(ref hash, _state.ShowDynamicSceneLayer);
        AddBool(ref hash, _state.ShowAttachedEffectsLayer);

        AddBool(ref hash, _state.RenderApplyCellTints);
        AddFloat(ref hash, _state.RenderTintStrength);
        AddBool(ref hash, _state.RenderSuppressBorderCells);
        AddBool(ref hash, _state.RenderApplyCellHeightFlag);
        AddFloat(ref hash, _state.RenderCellHeightFlagOffset);
        AddBool(ref hash, _state.RenderApplyObjectHeight);
        AddFloat(ref hash, _state.RenderObjectHeightScale);

        AddBool(ref hash, _state.RenderApplyLightingOverlay);
        AddInt(ref hash, _state.RenderLightingOverlayMaxAlpha);
        AddBool(ref hash, _state.RenderIncludeLightSprites);

        AddInt(ref hash, (int)_state.RenderLighting.Mode);
        AddInt(ref hash, _state.RenderLighting.CustomHour);
        AddInt(ref hash, _state.RenderLighting.CustomMinute);
        AddFloat(ref hash, _state.RenderLighting.ManualNightFactor);

        return hash;
    }

    private MinimapExportOptions BuildRuntimeMinimapOptions(MapDocument map)
    {
        _ = map;

        float tintStrength = _state.RenderTintStrength;
        if (!float.IsFinite(tintStrength))
        {
            tintStrength = 0.35f;
        }
        tintStrength = Math.Clamp(tintStrength, 0.0f, 1.0f);

        float cellHeightLift = _state.RenderCellHeightFlagOffset;
        if (!float.IsFinite(cellHeightLift))
        {
            cellHeightLift = 0.0f;
        }

        float objHeightScale = _state.RenderObjectHeightScale;
        if (!float.IsFinite(objHeightScale))
        {
            objHeightScale = 0.0f;
        }

        float nightFactor = MapLighting.Resolve(_state.RenderLighting).NightFactor;

        return new MinimapExportOptions
        {
            ScaleDivisor = 16,
            IncludeBack = _state.ShowBackLayer,
            IncludeMiddle = _state.ShowMiddleLayer,
            IncludeFloor = _state.ShowFloorLayer,
            IncludeUnderFront = _state.ShowUnderFrontLayer,
            IncludeFront = _state.ShowFrontLayer,
            IncludeOverFront = _state.ShowOverFrontLayer,
            IncludeDynamicScene = _state.ShowDynamicSceneLayer,
            IncludeAttachedEffects = _state.ShowAttachedEffectsLayer,
            SeparateLayerFiles = false,

            SuppressBorderCells = _state.RenderSuppressBorderCells,
            ApplyCellTints = _state.RenderApplyCellTints,
            TintStrength = tintStrength,
            ApplyCellHeightFlag = _state.RenderApplyCellHeightFlag,
            CellHeightFlagOffset = Math.Clamp(cellHeightLift, 0.0f, 64.0f),
            ApplyObjectHeight = _state.RenderApplyObjectHeight,
            ObjectHeightScale = Math.Clamp(objHeightScale, 0.0f, 8.0f),

            ApplyLuminanceToAlpha = true,
            LuminanceSettings = new LuminanceSettings(),

            ApplyLightingOverlay = _state.RenderApplyLightingOverlay,
            NightFactor = nightFactor,
            LightingOverlayMaxAlpha = Math.Clamp(_state.RenderLightingOverlayMaxAlpha, 0, 255),
            IncludeLightSprites = _state.RenderIncludeLightSprites,

            // Runtime overlay should be lightweight; large maps can still be expensive.
            MaxUncompressedBytes = 256L * 1024L * 1024L,
        };
    }

    private static string BuildRuntimeMinimapParityWarning(MinimapExportDiagnostics diag)
    {
        // Keep runtime overlay warnings focused: only DynScene/AttachedEffects parity data.
        var parts = new List<string>(capacity: 2);
        if (diag.DynamicSceneRequested && !string.IsNullOrWhiteSpace(diag.DynamicSceneWarning))
        {
            parts.Add(diag.DynamicSceneWarning);
        }

        if (diag.AttachedEffectsRequested && !string.IsNullOrWhiteSpace(diag.AttachedEffectsWarning))
        {
            parts.Add(diag.AttachedEffectsWarning);
        }

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }

    private void PumpRuntimeMinimapBuild(MapEditorDocument doc, MapDocument map, double nowSeconds)
    {
        if (doc is null || map is null)
        {
            return;
        }

        ulong settingsHash = ComputeRuntimeMinimapSettingsHash();
        if (!doc.RuntimeMinimapHasSettingsSnapshot || doc.RuntimeMinimapLastSettingsHash != settingsHash)
        {
            doc.RuntimeMinimapHasSettingsSnapshot = true;
            doc.RuntimeMinimapLastSettingsHash = settingsHash;
            InvalidateRuntimeMinimap(doc);
        }

        if (doc.RuntimeMinimapBuildTask is not null)
        {
            if (!doc.RuntimeMinimapBuildTask.IsCompleted)
            {
                return;
            }

            Task<RuntimeMinimapBuildResult> task = doc.RuntimeMinimapBuildTask;
            doc.RuntimeMinimapBuildTask = null;

            RuntimeMinimapBuildResult result;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _console.Append(MapEditorConsoleLogLevel.Warning, "Minimap", $"Runtime minimap rebuild failed: {ex.Message}");
                doc.RuntimeMinimapDirty = true;
                doc.RuntimeMinimapDirtyStampSet = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                _console.Append(MapEditorConsoleLogLevel.Warning, "Minimap", $"Runtime minimap rebuild failed: {result.Error}");
                doc.RuntimeMinimapDirty = true;
                doc.RuntimeMinimapDirtyStampSet = false;
                return;
            }

            if (result.Revision != doc.RuntimeMinimapRevision)
            {
                // Inputs changed while we were rendering; keep latest request pending.
                doc.RuntimeMinimapDirty = true;
                doc.RuntimeMinimapDirtyStampSet = false;
                return;
            }

            if (result.Width <= 0 || result.Height <= 0 || result.Rgba8.Length <= 0)
            {
                doc.RuntimeMinimapDirty = true;
                doc.RuntimeMinimapDirtyStampSet = false;
                return;
            }

            if (_state.RenderWarnOnUnsupportedParityData)
            {
                string warn = BuildRuntimeMinimapParityWarning(result.Diagnostics);
                if (string.IsNullOrWhiteSpace(warn))
                {
                    doc.RuntimeMinimapLastParityWarning = string.Empty;
                }
                else if (!warn.Equals(doc.RuntimeMinimapLastParityWarning, StringComparison.Ordinal))
                {
                    doc.RuntimeMinimapLastParityWarning = warn;
                    _console.Append(MapEditorConsoleLogLevel.Warning, "Parity", warn);
                }
            }

            if (_renderer is null)
            {
                return;
            }

            if (doc.RuntimeMinimapTextureId != nint.Zero)
            {
                _renderer.DestroyImGuiTexture(doc.RuntimeMinimapTextureId);
                doc.RuntimeMinimapTextureId = nint.Zero;
            }

            if (!_renderer.TryCreateImGuiTextureRgba8(result.Rgba8, result.Width, result.Height, out nint textureId, out string createError))
            {
                _console.Append(MapEditorConsoleLogLevel.Warning, "Minimap", $"Runtime minimap upload failed: {createError}");
                doc.RuntimeMinimapDirty = true;
                doc.RuntimeMinimapDirtyStampSet = false;
                return;
            }

            doc.RuntimeMinimapTextureId = textureId;
            doc.RuntimeMinimapTextureWidth = result.Width;
            doc.RuntimeMinimapTextureHeight = result.Height;
            doc.RuntimeMinimapBuiltRevision = result.Revision;
        }

        if (!_state.ShowMinimapOverlay)
        {
            return;
        }

        if (doc.RuntimeMinimapBuildTask is not null)
        {
            return;
        }

        if (!doc.RuntimeMinimapDirty)
        {
            return;
        }

        if (!doc.RuntimeMinimapDirtyStampSet)
        {
            doc.RuntimeMinimapDirtyStampSet = true;
            doc.RuntimeMinimapDirtySince = nowSeconds;
            return;
        }

        if ((nowSeconds - doc.RuntimeMinimapDirtySince) < RuntimeMinimapRebuildDelaySeconds)
        {
            return;
        }

        if (map.Width <= 0 || map.Height <= 0 || map.Cells.Length <= 0)
        {
            return;
        }

        // Snapshot cells to avoid data races while editing.
        var cells = new NmpCellData[map.Cells.Length];
        Array.Copy(map.Cells, cells, map.Cells.Length);

        string label = string.IsNullOrWhiteSpace(_state.MapPath) ? map.Path : _state.MapPath;
        int revision = doc.RuntimeMinimapRevision;
        bool tryTextures = _textureIndex.IsReady;
        MinimapExportOptions opts = BuildRuntimeMinimapOptions(map);

        doc.RuntimeMinimapDirty = false;
        doc.RuntimeMinimapDirtyStampSet = false;

        doc.RuntimeMinimapBuildTask = Task.Run(() => RenderRuntimeMinimap(revision, label, map.Width, map.Height, map.Version, cells, tryTextures, opts));
    }

    private RuntimeMinimapBuildResult RenderRuntimeMinimap(
        int revision,
        string label,
        int width,
        int height,
        uint version,
        NmpCellData[] cells,
        bool tryTextures,
        MinimapExportOptions opts)
    {
        try
        {
            MapDocument snapshot = MapDocument.CreateInMemory(label, width, height, version, cells);

            if (tryTextures && _textureIndex.IsReady)
            {
                if (MinimapExporter.TryRenderTexturedRgba8(
                    snapshot,
                    _textureIndex,
                    opts,
                    transparentBackground: false,
                    out byte[] rgba8,
                    out int outW,
                    out int outH,
                    out MinimapExportDiagnostics diagnostics,
                    out string error))
                {
                    return new RuntimeMinimapBuildResult(revision, outW, outH, rgba8, diagnostics, string.Empty);
                }
            }

            if (MinimapExporter.TryRenderPlaceholderRgba8(
                snapshot,
                opts,
                transparentBackground: false,
                out byte[] placeholder,
                out int outW2,
                out int outH2,
                out string placeholderError))
            {
                return new RuntimeMinimapBuildResult(revision, outW2, outH2, placeholder, default, string.Empty);
            }

            return new RuntimeMinimapBuildResult(revision, 0, 0, Array.Empty<byte>(), default, placeholderError);
        }
        catch (Exception ex)
        {
            return new RuntimeMinimapBuildResult(revision, 0, 0, Array.Empty<byte>(), default, ex.Message);
        }
    }

    private bool TryComputeMinimapOverlayLayout(MapDocument map, Vector2 canvasMin, Vector2 canvasMax, out MinimapOverlayLayout layout)
    {
        layout = default;

        if (!_state.ShowMinimapOverlay)
        {
            return false;
        }

        int mapW = map.Width;
        int mapH = map.Height;
        if (mapW <= 0 || mapH <= 0 || map.Cells.Length <= 0)
        {
            return false;
        }

        const float minimapScale = 1.0f / 16.0f;
        const float margin = 10.0f;
        const float maxDim = 320.0f;

        float mmW = mapW * BaseCellWidth * minimapScale;
        float mmH = mapH * BaseCellHeight * minimapScale;
        if (mmW <= 1.0f || mmH <= 1.0f)
        {
            return false;
        }

        if (mmW > maxDim || mmH > maxDim)
        {
            float factor = MathF.Min(maxDim / mmW, maxDim / mmH);
            if (factor > 0.0f && float.IsFinite(factor))
            {
                mmW *= factor;
                mmH *= factor;
            }
        }

        Vector2 mmMin = new(canvasMax.X - mmW - margin, canvasMin.Y + margin);
        Vector2 mmMax = new(mmMin.X + mmW, mmMin.Y + mmH);

        float cellPxW = mmW / mapW;
        float cellPxH = mmH / mapH;
        if (!float.IsFinite(cellPxW) || !float.IsFinite(cellPxH) || cellPxW <= 0.001f || cellPxH <= 0.001f)
        {
            return false;
        }

        layout = new MinimapOverlayLayout(mmMin, mmMax, cellPxW, cellPxH);
        return true;
    }

    private bool HandleMinimapOverlayInput(MapEditorDocument doc, MinimapOverlayLayout layout, Vector2 canvasSize)
    {
        if (doc is null)
        {
            return false;
        }

        if (!_state.ShowMinimapOverlay)
        {
            doc.IsDraggingMinimap = false;
            return false;
        }

        float zoom = _state.Camera.Zoom;
        zoom = Math.Clamp(zoom, _state.Camera.MinZoom, _state.Camera.MaxZoom);

        float cellW = BaseCellWidth * zoom;
        float cellH = BaseCellHeight * zoom;
        if (cellW <= 0.001f || cellH <= 0.001f)
        {
            return false;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        Vector2 mouse = io.MousePos;

        bool mouseInMinimap = mouse.X >= layout.Min.X && mouse.X <= layout.Max.X
            && mouse.Y >= layout.Min.Y && mouse.Y <= layout.Max.Y;

        bool consumed = false;

        if (doc.IsDraggingMinimap)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                float worldCellX = (mouse.X - layout.Min.X) / layout.CellPxW;
                float worldCellY = (mouse.Y - layout.Min.Y) / layout.CellPxH;

                float vpCellsW = canvasSize.X / cellW;
                float vpCellsH = canvasSize.Y / cellH;

                float newWorldX0 = worldCellX - vpCellsW * 0.5f;
                float newWorldY0 = worldCellY - vpCellsH * 0.5f;

                _state.Camera.PanX = -newWorldX0 * cellW;
                _state.Camera.PanY = -newWorldY0 * cellH;
                consumed = true;
            }
            else
            {
                doc.IsDraggingMinimap = false;
            }
        }
        else if (mouseInMinimap && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            doc.IsDraggingMinimap = true;

            float worldCellX = (mouse.X - layout.Min.X) / layout.CellPxW;
            float worldCellY = (mouse.Y - layout.Min.Y) / layout.CellPxH;

            float vpCellsW = canvasSize.X / cellW;
            float vpCellsH = canvasSize.Y / cellH;

            float newWorldX0 = worldCellX - vpCellsW * 0.5f;
            float newWorldY0 = worldCellY - vpCellsH * 0.5f;

            _state.Camera.PanX = -newWorldX0 * cellW;
            _state.Camera.PanY = -newWorldY0 * cellH;
            consumed = true;
        }

        return consumed;
    }

    private void DrawMinimapOverlay(MapEditorDocument doc, MapDocument map, ImDrawListPtr drawList, Vector2 canvasSize, MinimapOverlayLayout layout)
    {
        if (doc is null || map is null)
        {
            return;
        }

        float opacity = _state.MinimapOpacity;
        if (!float.IsFinite(opacity))
        {
            opacity = 0.85f;
        }
        opacity = Math.Clamp(opacity, 0.0f, 1.0f);
        byte bgA = (byte)Math.Clamp((int)Math.Round(opacity * 255.0f, MidpointRounding.AwayFromZero), 0, 255);
        uint bgCol = PackColor(20, 23, 26, bgA);
        drawList.AddRectFilled(layout.Min, layout.Max, bgCol);

        if (doc.RuntimeMinimapTextureId != nint.Zero && doc.RuntimeMinimapTextureWidth > 0 && doc.RuntimeMinimapTextureHeight > 0)
        {
            drawList.AddImage(doc.RuntimeMinimapTextureId,
                layout.Min,
                layout.Max,
                Vector2.Zero,
                Vector2.One,
                PackColor(255, 255, 255, 255));
        }

        float zoom = _state.Camera.Zoom;
        zoom = Math.Clamp(zoom, _state.Camera.MinZoom, _state.Camera.MaxZoom);

        float cellW = BaseCellWidth * zoom;
        float cellH = BaseCellHeight * zoom;
        if (cellW <= 0.001f || cellH <= 0.001f)
        {
            return;
        }

        float vpWorldX0 = (-_state.Camera.PanX) / cellW;
        float vpWorldY0 = (-_state.Camera.PanY) / cellH;
        float vpWorldX1 = (canvasSize.X - _state.Camera.PanX) / cellW;
        float vpWorldY1 = (canvasSize.Y - _state.Camera.PanY) / cellH;

        vpWorldX0 = Math.Clamp(vpWorldX0, 0.0f, map.Width);
        vpWorldY0 = Math.Clamp(vpWorldY0, 0.0f, map.Height);
        vpWorldX1 = Math.Clamp(vpWorldX1, 0.0f, map.Width);
        vpWorldY1 = Math.Clamp(vpWorldY1, 0.0f, map.Height);

        Vector2 vpMin = new(layout.Min.X + vpWorldX0 * layout.CellPxW, layout.Min.Y + vpWorldY0 * layout.CellPxH);
        Vector2 vpMax = new(layout.Min.X + vpWorldX1 * layout.CellPxW, layout.Min.Y + vpWorldY1 * layout.CellPxH);

        drawList.AddRectFilled(vpMin, vpMax, PackColor(255, 255, 255, 25));
        drawList.AddRect(vpMin, vpMax, PackColor(255, 255, 255, 200), 0.0f, ImDrawFlags.None, 1.5f);

        drawList.AddRect(layout.Min, layout.Max, PackColor(180, 180, 180, 200), 0.0f, ImDrawFlags.None, 1.0f);
    }

    private void FitMapToCanvas(MapDocument map, Vector2 canvasSize)
    {
        float zoom = _state.Camera.Zoom;
        zoom = Math.Clamp(zoom, _state.Camera.MinZoom, _state.Camera.MaxZoom);

        float mapPixelW = map.Width * BaseCellWidth * zoom;
        float mapPixelH = map.Height * BaseCellHeight * zoom;

        _state.Camera.PanX = (canvasSize.X - mapPixelW) * 0.5f;
        _state.Camera.PanY = (canvasSize.Y - mapPixelH) * 0.5f;
    }

    private void CenterCameraOnCell(Vector2 canvasSize, int cellX, int cellY)
    {
        if (_state.Map is null)
        {
            return;
        }

        MapDocument map = _state.Map;
        if (!map.IsInBounds(cellX, cellY))
        {
            return;
        }

        float zoom = _state.Camera.Zoom;
        zoom = Math.Clamp(zoom, _state.Camera.MinZoom, _state.Camera.MaxZoom);

        float cellW = BaseCellWidth * zoom;
        float cellH = BaseCellHeight * zoom;

        float targetX = (cellX + 0.5f) * cellW;
        float targetY = (cellY + 0.5f) * cellH;

        _state.Camera.PanX = canvasSize.X * 0.5f - targetX;
        _state.Camera.PanY = canvasSize.Y * 0.5f - targetY;
    }

    private void HandleCanvasInput(MapDocument map, bool hovered, bool active, Vector2 canvasScreenPos, Vector2 canvasSize)
    {
        _ = canvasSize;

        if (!hovered && !active)
        {
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        Vector2 mouse = io.MousePos;

        if (hovered && io.MouseWheel != 0.0f)
        {
            float previousZoom = _state.Camera.Zoom;
            const float wheelFactor = 1.25f;
            float targetZoom = previousZoom * MathF.Pow(wheelFactor, io.MouseWheel);
            float step = _state.ZoomStep;
            if (step > 0.0f && float.IsFinite(step))
            {
                targetZoom = MathF.Round(targetZoom / step, MidpointRounding.AwayFromZero) * step;
            }
            targetZoom = Math.Clamp(targetZoom, _state.Camera.MinZoom, _state.Camera.MaxZoom);

            float prevCellW = BaseCellWidth * previousZoom;
            float prevCellH = BaseCellHeight * previousZoom;
            float newCellW = BaseCellWidth * targetZoom;
            float newCellH = BaseCellHeight * targetZoom;

            float worldX = (mouse.X - canvasScreenPos.X - _state.Camera.PanX) / prevCellW;
            float worldY = (mouse.Y - canvasScreenPos.Y - _state.Camera.PanY) / prevCellH;

            _state.Camera.Zoom = targetZoom;
            _state.Camera.PanX = mouse.X - canvasScreenPos.X - worldX * newCellW;
            _state.Camera.PanY = mouse.Y - canvasScreenPos.Y - worldY * newCellH;
        }

        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0.0f))
        {
            Vector2 delta = io.MouseDelta;
            _state.Camera.PanX += delta.X;
            _state.Camera.PanY += delta.Y;
        }

        if (hovered && !io.WantTextInput)
        {
            // Match old behavior: WASD/arrow keys pan the map view while hovered.
            float panSpeed = 600.0f * io.DeltaTime;
            if (io.KeyShift)
            {
                panSpeed *= 2.0f;
            }

            if (ImGui.IsKeyDown(ImGuiKey.A) || ImGui.IsKeyDown(ImGuiKey.LeftArrow)) _state.Camera.PanX += panSpeed;
            if (ImGui.IsKeyDown(ImGuiKey.D) || ImGui.IsKeyDown(ImGuiKey.RightArrow)) _state.Camera.PanX -= panSpeed;
            if (ImGui.IsKeyDown(ImGuiKey.W) || ImGui.IsKeyDown(ImGuiKey.UpArrow)) _state.Camera.PanY += panSpeed;
            if (ImGui.IsKeyDown(ImGuiKey.S) || ImGui.IsKeyDown(ImGuiKey.DownArrow)) _state.Camera.PanY -= panSpeed;
        }

        bool canvasHasMouse = hovered || active;

        if (_state.Tool is MapEditTool.Select or MapEditTool.Erase)
        {
            if (_selectionDrag is null && canvasHasMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                if (map.IsInBounds(x, y))
                {
                    _selectionDrag = new SelectionDragSession(map, x, y);
                    _state.SelectedCellX = x;
                    _state.SelectedCellY = y;
                    _state.HasSelection = false;
                }
            }

            if (_selectionDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (!ReferenceEquals(_selectionDrag.Map, map))
                {
                    _selectionDrag = null;
                }
                else if (canvasHasMouse)
                {
                    (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                    if (map.IsInBounds(x, y))
                    {
                        _selectionDrag.CurrentX = x;
                        _selectionDrag.CurrentY = y;
                        _state.SelectedCellX = x;
                        _state.SelectedCellY = y;
                    }
                }
            }

            if (_selectionDrag is not null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                EndSelectionDrag();
            }
        }
        else if (_state.Tool == MapEditTool.Pencil)
        {
            if (_paintDrag is null && canvasHasMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _paintDrag = new PaintDragSession(map, _state.EditLayer, _state.PaintLibrary, _state.PaintImage);
            }

            if (_paintDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (canvasHasMouse)
                {
                    (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                    if (map.IsInBounds(x, y))
                    {
                        PaintDragCell(_paintDrag, x, y);
                        _state.SelectedCellX = x;
                        _state.SelectedCellY = y;
                    }
                }
            }

            if (_paintDrag is not null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                EndPaintDrag();
            }
        }
        else if (_state.Tool == MapEditTool.RectFill)
        {
            if (_rectFillDrag is null && canvasHasMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                if (map.IsInBounds(x, y))
                {
                    _rectFillDrag = new RectFillDragSession(map, _state.EditLayer, _state.PaintLibrary, _state.PaintImage, x, y);
                    _state.SelectedCellX = x;
                    _state.SelectedCellY = y;
                }
            }

            if (_rectFillDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (canvasHasMouse)
                {
                    (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                    if (map.IsInBounds(x, y))
                    {
                        _rectFillDrag.CurrentX = x;
                        _rectFillDrag.CurrentY = y;
                        _state.SelectedCellX = x;
                        _state.SelectedCellY = y;
                    }
                }
            }

            if (_rectFillDrag is not null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                EndRectFillDrag();
            }
        }
        else if (_state.Tool == MapEditTool.BlockedEditor)
        {
            bool setBlocked = !io.KeyShift;

            if (_blockedDrag is null && canvasHasMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _blockedDrag = new BlockedDragSession(map, setBlocked);
            }

            if (_blockedDrag is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (!ReferenceEquals(_blockedDrag.Map, map))
                {
                    _blockedDrag = null;
                }
                else if (canvasHasMouse)
                {
                    (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                    if (map.IsInBounds(x, y))
                    {
                        ApplyBlockedDragCell(_blockedDrag, x, y);
                        _state.SelectedCellX = x;
                        _state.SelectedCellY = y;
                    }
                }
            }

            if (_blockedDrag is not null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                EndBlockedDrag();
            }
        }
        else if (_state.Tool == MapEditTool.Stamp)
        {
            if (canvasHasMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
                if (map.IsInBounds(x, y))
                {
                    _state.SelectedCellX = x;
                    _state.SelectedCellY = y;
                    ApplyStampAt(map, x, y);
                }
            }
        }
        else if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
            if (map.IsInBounds(x, y))
            {
                _state.SelectedCellX = x;
                _state.SelectedCellY = y;
            }
        }
    }

    private void PaintDragCell(PaintDragSession session, int x, int y)
    {
        if (_state.Map is null)
        {
            return;
        }

        if (!ReferenceEquals(session.Map, _state.Map))
        {
            return;
        }

        if (!session.Map.IsInBounds(x, y))
        {
            return;
        }

        int index = session.Map.GetIndex(x, y);
        if ((uint)index >= (uint)session.Map.Cells.Length)
        {
            return;
        }

        NmpCellData before = session.Map.Cells[index];
        NmpCellData after = before;
        if (!TryApplyLayerEdit(ref after, session.Layer, session.Library, session.Image, clear: false))
        {
            return;
        }

        if (!session.BeforeCells.ContainsKey(index))
        {
            session.BeforeCells.Add(index, before);
        }

        session.Map.Cells[index] = after;
    }

    private void EndPaintDrag()
    {
        if (_paintDrag is null)
        {
            return;
        }

        PaintDragSession session = _paintDrag;
        _paintDrag = null;

        if (_state.Map is null || !ReferenceEquals(session.Map, _state.Map))
        {
            return;
        }

        if (session.BeforeCells.Count <= 0)
        {
            return;
        }

        int count = session.BeforeCells.Count;
        int[] indices = new int[count];
        NmpCellData[] before = new NmpCellData[count];
        NmpCellData[] after = new NmpCellData[count];

        session.BeforeCells.Keys.CopyTo(indices, 0);
        Array.Sort(indices);

        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            before[i] = session.BeforeCells[index];
            after[i] = session.Map.Cells[index];
        }

        string actionName = $"绘制 {session.Layer}（{indices.Length} 格）";
        _state.History.Push(new MultiCellEditAction(actionName, session.Map, indices, before, after));
        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = actionName;
        InvalidateRuntimeMinimapActiveDocument();
    }

    private void ApplyBlockedDragCell(BlockedDragSession session, int x, int y)
    {
        if (_state.Map is null)
        {
            return;
        }

        if (!ReferenceEquals(session.Map, _state.Map))
        {
            return;
        }

        if (!session.Map.IsInBounds(x, y))
        {
            return;
        }

        int index = session.Map.GetIndex(x, y);
        if ((uint)index >= (uint)session.Map.Cells.Length)
        {
            return;
        }

        NmpCellData before = session.Map.Cells[index];
        NmpCellData after = before;

        if (session.SetBlocked)
        {
            if ((after.Flags & NmpFlagBlocked) != 0)
            {
                return;
            }

            after.Flags = (byte)(after.Flags | NmpFlagBlocked);
        }
        else
        {
            if ((after.Flags & NmpFlagBlocked) == 0)
            {
                return;
            }

            after.Flags = (byte)(after.Flags & ~NmpFlagBlocked);
        }

        if (!session.BeforeCells.ContainsKey(index))
        {
            session.BeforeCells.Add(index, before);
        }

        session.Map.Cells[index] = after;
    }

    private void EndBlockedDrag()
    {
        if (_blockedDrag is null)
        {
            return;
        }

        BlockedDragSession session = _blockedDrag;
        _blockedDrag = null;

        if (_state.Map is null || !ReferenceEquals(session.Map, _state.Map))
        {
            return;
        }

        if (session.BeforeCells.Count <= 0)
        {
            return;
        }

        int count = session.BeforeCells.Count;
        int[] indices = new int[count];
        NmpCellData[] before = new NmpCellData[count];
        NmpCellData[] after = new NmpCellData[count];

        session.BeforeCells.Keys.CopyTo(indices, 0);
        Array.Sort(indices);

        for (int i = 0; i < indices.Length; i++)
        {
            int index = indices[i];
            before[i] = session.BeforeCells[index];
            after[i] = session.Map.Cells[index];
        }

        string actionName = session.SetBlocked
            ? $"绘制阻挡（{indices.Length} 格）"
            : $"清除阻挡（{indices.Length} 格）";
        _state.History.Push(new MultiCellEditAction(actionName, session.Map, indices, before, after));
        _state.StatusMessage = actionName;
        InvalidateRuntimeMinimapActiveDocument();
    }

    private void EndRectFillDrag()
    {
        if (_rectFillDrag is null)
        {
            return;
        }

        RectFillDragSession session = _rectFillDrag;
        _rectFillDrag = null;

        if (_state.Map is null || !ReferenceEquals(session.Map, _state.Map))
        {
            return;
        }

        MapDocument map = session.Map;

        int minX = Math.Min(session.StartX, session.CurrentX);
        int maxX = Math.Max(session.StartX, session.CurrentX);
        int minY = Math.Min(session.StartY, session.CurrentY);
        int maxY = Math.Max(session.StartY, session.CurrentY);

        int estimated = (maxX - minX + 1) * (maxY - minY + 1);
        estimated = Math.Clamp(estimated, 0, 1_000_000);

        List<int> indices = new(capacity: Math.Min(estimated, 4096));
        List<NmpCellData> before = new(capacity: Math.Min(estimated, 4096));
        List<NmpCellData> after = new(capacity: Math.Min(estimated, 4096));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!map.IsInBounds(x, y))
                {
                    continue;
                }

                int index = map.GetIndex(x, y);
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cellBefore = map.Cells[index];
                NmpCellData cellAfter = cellBefore;
                if (!TryApplyLayerEdit(ref cellAfter, session.Layer, session.Library, session.Image, clear: false))
                {
                    continue;
                }

                map.Cells[index] = cellAfter;
                indices.Add(index);
                before.Add(cellBefore);
                after.Add(cellAfter);
            }
        }

        if (indices.Count <= 0)
        {
            _state.StatusMessage = "矩形填充：未发生变化。";
            return;
        }

        string actionName = $"矩形填充 {session.Layer}（({minX},{minY})-({maxX},{maxY})，{indices.Count} 格）";
        _state.History.Push(new MultiCellEditAction(actionName, map, indices.ToArray(), before.ToArray(), after.ToArray()));
        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = actionName;
        InvalidateRuntimeMinimapActiveDocument();
    }

    private void EndSelectionDrag()
    {
        if (_selectionDrag is null)
        {
            return;
        }

        SelectionDragSession session = _selectionDrag;
        _selectionDrag = null;

        if (_state.Map is null || !ReferenceEquals(_state.Map, session.Map))
        {
            return;
        }

        MapDocument map = session.Map;
        int x0 = Math.Min(session.StartX, session.CurrentX);
        int y0 = Math.Min(session.StartY, session.CurrentY);
        int x1 = Math.Max(session.StartX, session.CurrentX);
        int y1 = Math.Max(session.StartY, session.CurrentY);

        x0 = Math.Clamp(x0, 0, map.Width - 1);
        y0 = Math.Clamp(y0, 0, map.Height - 1);
        x1 = Math.Clamp(x1, 0, map.Width - 1);
        y1 = Math.Clamp(y1, 0, map.Height - 1);

        _state.SelectionX0 = x0;
        _state.SelectionY0 = y0;
        _state.SelectionX1 = x1;
        _state.SelectionY1 = y1;
        _state.HasSelection = true;

        _state.StatusMessage = $"已选择区域：({x0},{y0})-({x1},{y1})";
    }

    private void CopySelectionToClipboardStamp()
    {
        if (_state.Map is null)
        {
            _state.StatusMessage = "未加载地图。";
            return;
        }

        if (!_state.HasSelection)
        {
            _state.StatusMessage = "未选择区域。";
            return;
        }

        MapDocument map = _state.Map;

        int x0 = Math.Min(_state.SelectionX0, _state.SelectionX1);
        int y0 = Math.Min(_state.SelectionY0, _state.SelectionY1);
        int x1 = Math.Max(_state.SelectionX0, _state.SelectionX1);
        int y1 = Math.Max(_state.SelectionY0, _state.SelectionY1);

        x0 = Math.Clamp(x0, 0, map.Width - 1);
        y0 = Math.Clamp(y0, 0, map.Height - 1);
        x1 = Math.Clamp(x1, 0, map.Width - 1);
        y1 = Math.Clamp(y1, 0, map.Height - 1);

        if (x1 < x0 || y1 < y0)
        {
            _state.StatusMessage = "选择区域无效。";
            return;
        }

        int selW = x1 - x0 + 1;
        int selH = y1 - y0 + 1;

        long cellCountLong = (long)selW * selH;
        if (cellCountLong is <= 0 or > 1_000_000)
        {
            _state.StatusMessage = $"选择区域过大（{selW} x {selH} = {cellCountLong} 格），暂不支持复制到剪贴板印章。";
            return;
        }

        var cells = new NmpCellData[(int)cellCountLong];
        for (int y = y0; y <= y1; y++)
        {
            int row = (y - y0) * selW;
            for (int x = x0; x <= x1; x++)
            {
                int srcIndex = map.GetIndex(x, y);
                if ((uint)srcIndex >= (uint)map.Cells.Length)
                {
                    continue;
                }

                cells[row + (x - x0)] = map.Cells[srcIndex];
            }
        }

        _clipboardStamp = MapDocument.CreateInMemory("(clipboard)", selW, selH, map.Version, cells);
        _state.StampSource = StampSourceKind.Clipboard;
        _state.StampAnchor = StampAnchorMode.TopLeft;
        _state.Tool = MapEditTool.Stamp;
        _paintDrag = null;
        _rectFillDrag = null;
        _selectionDrag = null;
        ClearStamp();
        _state.StatusMessage = $"已复制选区为剪贴板印章：{selW} x {selH}（Ctrl+V 可切换粘贴）";
    }

    private void EraseSelectionRegion()
    {
        if (_state.Map is null)
        {
            _state.StatusMessage = "未加载地图。";
            return;
        }

        if (!_state.HasSelection)
        {
            _state.StatusMessage = "未选择区域。";
            return;
        }

        bool eraseBack = _state.EraseApplyBack;
        bool eraseMiddle = _state.EraseApplyMiddle;
        bool eraseFront = _state.EraseApplyFront;
        bool eraseUnderObject = _state.EraseApplyUnderObject;
        bool eraseOverObject = _state.EraseApplyOverObject;
        bool eraseNearGround = _state.EraseApplyNearGround;
        bool eraseBlocked = _state.EraseApplyBlocked;

        if (!eraseBack && !eraseMiddle && !eraseFront && !eraseUnderObject && !eraseOverObject && !eraseNearGround && !eraseBlocked)
        {
            _state.StatusMessage = "删除选区：未选择要删除的层。";
            return;
        }

        MapDocument map = _state.Map;

        int x0 = Math.Min(_state.SelectionX0, _state.SelectionX1);
        int y0 = Math.Min(_state.SelectionY0, _state.SelectionY1);
        int x1 = Math.Max(_state.SelectionX0, _state.SelectionX1);
        int y1 = Math.Max(_state.SelectionY0, _state.SelectionY1);

        x0 = Math.Clamp(x0, 0, map.Width - 1);
        y0 = Math.Clamp(y0, 0, map.Height - 1);
        x1 = Math.Clamp(x1, 0, map.Width - 1);
        y1 = Math.Clamp(y1, 0, map.Height - 1);

        if (x1 < x0 || y1 < y0)
        {
            _state.StatusMessage = "选择区域无效。";
            return;
        }

        int selW = x1 - x0 + 1;
        int selH = y1 - y0 + 1;

        long cellCountLong = (long)selW * selH;
        if (cellCountLong is <= 0 or > 1_000_000)
        {
            _state.StatusMessage = $"删除选区：选择区域过大（{selW} x {selH} = {cellCountLong} 格），暂不支持。";
            return;
        }

        int estimated = (int)cellCountLong;
        List<int> indices = new(capacity: Math.Min(estimated, 4096));
        List<NmpCellData> before = new(capacity: indices.Capacity);
        List<NmpCellData> after = new(capacity: indices.Capacity);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int index = map.GetIndex(x, y);
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cellBefore = map.Cells[index];
                NmpCellData cellAfter = cellBefore;
                bool changed = false;

                if (eraseBack) changed |= TryClearBack(ref cellAfter);
                if (eraseMiddle) changed |= TryClearMiddle(ref cellAfter);
                if (eraseFront) changed |= TryClearFront(ref cellAfter);
                if (eraseUnderObject) changed |= TryClearUnderObject(ref cellAfter);
                if (eraseOverObject) changed |= TryClearOverObject(ref cellAfter);
                if (eraseNearGround) changed |= TryClearNearGround(ref cellAfter);
                if (eraseBlocked) changed |= TryClearBlockedFlag(ref cellAfter);

                if (!changed)
                {
                    continue;
                }

                map.Cells[index] = cellAfter;
                indices.Add(index);
                before.Add(cellBefore);
                after.Add(cellAfter);
            }
        }

        if (indices.Count <= 0)
        {
            _state.StatusMessage = "删除选区：未发生变化。";
            return;
        }

        string actionName = $"删除选区（({x0},{y0})-({x1},{y1})，{indices.Count} 格）";
        _state.History.Push(new MultiCellEditAction(actionName, map, indices.ToArray(), before.ToArray(), after.ToArray()));
        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = actionName;
        InvalidateRuntimeMinimapActiveDocument();
    }

    private void MoveSelectionToMoveStamp()
    {
        if (_state.Map is null)
        {
            _state.StatusMessage = "未加载地图。";
            return;
        }

        if (!_state.HasSelection)
        {
            _state.StatusMessage = "未选择区域。";
            return;
        }

        MapDocument map = _state.Map;

        int x0 = Math.Min(_state.SelectionX0, _state.SelectionX1);
        int y0 = Math.Min(_state.SelectionY0, _state.SelectionY1);
        int x1 = Math.Max(_state.SelectionX0, _state.SelectionX1);
        int y1 = Math.Max(_state.SelectionY0, _state.SelectionY1);

        x0 = Math.Clamp(x0, 0, map.Width - 1);
        y0 = Math.Clamp(y0, 0, map.Height - 1);
        x1 = Math.Clamp(x1, 0, map.Width - 1);
        y1 = Math.Clamp(y1, 0, map.Height - 1);

        if (x1 < x0 || y1 < y0)
        {
            _state.StatusMessage = "选择区域无效。";
            return;
        }

        bool moveBack = _state.MoveApplyBack;
        bool moveMiddle = _state.MoveApplyMiddle;
        bool moveFront = _state.MoveApplyFront;
        bool moveUnderObject = _state.MoveApplyUnderObject;
        bool moveOverObject = _state.MoveApplyOverObject;
        bool moveNearGround = _state.MoveApplyNearGround;
        bool moveBlocked = _state.MoveApplyBlocked;

        if (!moveBack && !moveMiddle && !moveFront && !moveUnderObject && !moveOverObject && !moveNearGround && !moveBlocked)
        {
            _state.StatusMessage = "移动选区：未选择要移动的层。";
            return;
        }

        int selW = x1 - x0 + 1;
        int selH = y1 - y0 + 1;

        long cellCountLong = (long)selW * selH;
        if (cellCountLong is <= 0 or > 1_000_000)
        {
            _state.StatusMessage = $"移动选区：选择区域过大（{selW} x {selH} = {cellCountLong} 格），暂不支持。";
            return;
        }

        var snippetCells = new NmpCellData[(int)cellCountLong];

        int estimated = (int)cellCountLong;
        List<int> indices = new(capacity: Math.Min(estimated, 4096));
        List<NmpCellData> before = new(capacity: indices.Capacity);
        List<NmpCellData> after = new(capacity: indices.Capacity);

        for (int y = y0; y <= y1; y++)
        {
            int row = (y - y0) * selW;
            for (int x = x0; x <= x1; x++)
            {
                int srcIndex = map.GetIndex(x, y);
                if ((uint)srcIndex >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData srcCell = map.Cells[srcIndex];

                NmpCellData snippetCell = srcCell;
                FilterMoveSnippetCell(ref snippetCell, moveBack, moveMiddle, moveFront, moveUnderObject, moveOverObject, moveNearGround, moveBlocked);
                snippetCells[row + (x - x0)] = snippetCell;

                NmpCellData cutCell = srcCell;
                if (!TryCutMoveSourceCell(ref cutCell, moveBack, moveMiddle, moveFront, moveUnderObject, moveOverObject, moveNearGround, moveBlocked))
                {
                    continue;
                }

                map.Cells[srcIndex] = cutCell;
                indices.Add(srcIndex);
                before.Add(srcCell);
                after.Add(cutCell);
            }
        }

        if (indices.Count <= 0)
        {
            _state.StatusMessage = "移动选区：选区内没有可移动内容。";
            return;
        }

        string cutActionName = $"移动选区：剪切源区域（{indices.Count} 格）";
        _state.History.Push(new MultiCellEditAction(cutActionName, map, indices.ToArray(), before.ToArray(), after.ToArray()));
        _objectListDirty = true;
        _sceneTreeDirty = true;

        MapDocument snippet = MapDocument.CreateInMemory("(moving)", selW, selH, map.Version, snippetCells);

        if (_moveSelection is not null)
        {
            ClearMoveSelectionSession(restorePreviousStampSource: true, restoreTool: false, updateStatus: false, statusMessage: null);
        }

        _moveSelection = new MoveSelectionSession(
            map,
            snippet,
            _state.StampSource,
            moveBack,
            moveMiddle,
            moveFront,
            moveUnderObject,
            moveOverObject,
            moveNearGround,
            moveBlocked);

        _state.StampSource = StampSourceKind.MoveSelection;
        _state.Tool = MapEditTool.Stamp;
        _paintDrag = null;
        _rectFillDrag = null;
        _selectionDrag = null;
        ClearStamp();
        _state.HasSelection = false;
        _state.StatusMessage = "移动选区：已剪切源区域，点击地图放置（一次性；Esc 取消，可 Ctrl+Z 撤销剪切）。";
        InvalidateRuntimeMinimapActiveDocument();
    }

    private static void FilterMoveSnippetCell(
        ref NmpCellData cell,
        bool moveBack,
        bool moveMiddle,
        bool moveFront,
        bool moveUnderObject,
        bool moveOverObject,
        bool moveNearGround,
        bool moveBlocked)
    {
        if (!moveBack) ClearBackCell(ref cell);
        if (!moveMiddle) ClearMiddleCell(ref cell);
        if (!moveFront) ClearFrontCell(ref cell);
        if (!moveUnderObject) ClearUnderObjectCell(ref cell);
        if (!moveOverObject) ClearOverObjectCell(ref cell);
        if (!moveNearGround) ClearNearGroundCell(ref cell);
        if (!moveBlocked) cell.Flags = (byte)(cell.Flags & ~NmpFlagBlocked);
    }

    private static bool TryCutMoveSourceCell(
        ref NmpCellData cell,
        bool moveBack,
        bool moveMiddle,
        bool moveFront,
        bool moveUnderObject,
        bool moveOverObject,
        bool moveNearGround,
        bool moveBlocked)
    {
        bool changed = false;
        if (moveBack) changed |= TryClearBack(ref cell);
        if (moveMiddle) changed |= TryClearMiddle(ref cell);
        if (moveFront) changed |= TryClearFront(ref cell);
        if (moveUnderObject) changed |= TryClearUnderObject(ref cell);
        if (moveOverObject) changed |= TryClearOverObject(ref cell);
        if (moveNearGround) changed |= TryClearNearGround(ref cell);
        if (moveBlocked) changed |= TryClearBlockedFlag(ref cell);
        return changed;
    }

    private static bool TryClearBack(ref NmpCellData cell)
    {
        if (cell.BackImage == 0 && cell.BackLibrary == 0 && cell.BackAnimTick == 0 && (cell.Flags & FlagSmTiles) == 0)
        {
            return false;
        }

        ClearBackCell(ref cell);
        return true;
    }

    private static bool TryClearMiddle(ref NmpCellData cell)
    {
        if (cell.MiddleImage == 0
            && cell.MiddleLibrary == 0
            && cell.MiddleImage2 == 0
            && cell.MiddleLibrary2 == 0
            && cell.MiddleAlphaMask == 0
            && cell.MiddleAnimTick == 0
            && (cell.Flags & FlagTiles) == 0)
        {
            return false;
        }

        ClearMiddleCell(ref cell);
        return true;
    }

    private static bool TryClearFront(ref NmpCellData cell)
    {
        if (cell.FrontImage == 0
            && cell.FrontLibrary == 0
            && cell.FrontAnimFrame == 0
            && cell.FrontAnimTick == 0
            && cell.ObjectHeight == 0
            && (cell.Flags & FlagObject) == 0)
        {
            return false;
        }

        ClearFrontCell(ref cell);
        return true;
    }

    private static bool TryClearUnderObject(ref NmpCellData cell)
    {
        if (cell.UnderObject == 0 && (cell.Flags & NmpFlagUnderObject) == 0)
        {
            return false;
        }

        ClearUnderObjectCell(ref cell);
        return true;
    }

    private static bool TryClearOverObject(ref NmpCellData cell)
    {
        if (cell.OverObject == 0 && (cell.ExtendedAttributes & NmpExAttrOverObject) == 0 && cell.ColorOverObj == 0)
        {
            return false;
        }

        ClearOverObjectCell(ref cell);
        return true;
    }

    private static bool TryClearNearGround(ref NmpCellData cell)
    {
        if (cell.NearGround == 0 && (cell.Flags & NmpFlagNearGround) == 0)
        {
            return false;
        }

        ClearNearGroundCell(ref cell);
        return true;
    }

    private static bool TryClearBlockedFlag(ref NmpCellData cell)
    {
        if ((cell.Flags & NmpFlagBlocked) == 0)
        {
            return false;
        }

        cell.Flags = (byte)(cell.Flags & ~NmpFlagBlocked);
        return true;
    }

    private static void ClearBackCell(ref NmpCellData cell)
    {
        cell.BackImage = 0;
        cell.BackLibrary = 0;
        cell.BackAnimTick = 0;
        cell.Flags = (byte)(cell.Flags & ~FlagSmTiles);
    }

    private static void ClearMiddleCell(ref NmpCellData cell)
    {
        cell.MiddleImage = 0;
        cell.MiddleLibrary = 0;
        cell.MiddleImage2 = 0;
        cell.MiddleLibrary2 = 0;
        cell.MiddleAlphaMask = 0;
        cell.MiddleAnimTick = 0;
        cell.Flags = (byte)(cell.Flags & ~FlagTiles);
    }

    private static void ClearFrontCell(ref NmpCellData cell)
    {
        cell.FrontImage = 0;
        cell.FrontLibrary = 0;
        cell.FrontAnimFrame = 0;
        cell.FrontAnimTick = 0;
        cell.ObjectHeight = 0;
        cell.Flags = (byte)(cell.Flags & ~FlagObject);
    }

    private static void ClearUnderObjectCell(ref NmpCellData cell)
    {
        cell.UnderObject = 0;
        cell.Flags = (byte)(cell.Flags & ~NmpFlagUnderObject);
    }

    private static void ClearOverObjectCell(ref NmpCellData cell)
    {
        cell.OverObject = 0;
        cell.ColorOverObj = 0;
        cell.ExtendedAttributes = (ushort)(cell.ExtendedAttributes & ~NmpExAttrOverObject);
    }

    private static void ClearNearGroundCell(ref NmpCellData cell)
    {
        cell.NearGround = 0;
        cell.Flags = (byte)(cell.Flags & ~NmpFlagNearGround);
    }

    private void ClearMoveSelectionSession(
        bool restorePreviousStampSource,
        bool restoreTool,
        bool updateStatus,
        string? statusMessage)
    {
        if (_moveSelection is null)
        {
            return;
        }

        if (restorePreviousStampSource)
        {
            _state.StampSource = _moveSelection.PreviousStampSource;
        }

        _moveSelection = null;
        ClearStamp();

        if (restoreTool)
        {
            _state.Tool = MapEditTool.Select;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
        }

        if (updateStatus && !string.IsNullOrWhiteSpace(statusMessage))
        {
            _state.StatusMessage = statusMessage;
        }
    }

    private void ApplyStampAt(MapDocument map, int anchorCellX, int anchorCellY)
    {
        if (_state.Map is null || !ReferenceEquals(_state.Map, map))
        {
            _state.StatusMessage = "未加载地图。";
            return;
        }

        if (!map.IsInBounds(anchorCellX, anchorCellY))
        {
            _state.StatusMessage = "盖章位置越界。";
            return;
        }

        if (!EnsureStampLoaded(forceReload: false, out string error))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? "未设置印章或载入失败。" : error;
            return;
        }

        if (_stampMap is null)
        {
            _state.StatusMessage = "印章载入失败（内部状态为空）。";
            return;
        }

        MapDocument stamp = _stampMap;
        if (stamp.Width <= 0 || stamp.Height <= 0 || stamp.Cells.Length <= 0)
        {
            _state.StatusMessage = "印章内容为空。";
            return;
        }

        bool isMovePlacement = _state.StampSource == StampSourceKind.MoveSelection && _moveSelection is not null;

        StampAnchorMode anchorMode = _state.StampAnchor;
        bool overwriteEmpty = _state.StampOverwriteEmpty;
        bool applyBack = _state.StampApplyBack;
        bool applyMiddle = _state.StampApplyMiddle;
        bool applyFront = _state.StampApplyFront;
        bool applyUnderObject = _state.StampApplyUnderObject;
        bool applyOverObject = _state.StampApplyOverObject;
        bool applyNearGround = _state.StampApplyNearGround;
        bool applyBlocked = _state.StampApplyBlocked;

        if (isMovePlacement)
        {
            MoveSelectionSession move = _moveSelection!;
            anchorMode = StampAnchorMode.TopLeft;
            overwriteEmpty = false;
            applyBack = move.ApplyBack;
            applyMiddle = move.ApplyMiddle;
            applyFront = move.ApplyFront;
            applyUnderObject = move.ApplyUnderObject;
            applyOverObject = move.ApplyOverObject;
            applyNearGround = move.ApplyNearGround;
            applyBlocked = move.ApplyBlocked;
        }

        int stampAnchorX = 0;
        int stampAnchorY = 0;
        if (anchorMode == StampAnchorMode.Center)
        {
            stampAnchorX = stamp.Width / 2;
            stampAnchorY = stamp.Height / 2;
        }

        int startX = anchorCellX - stampAnchorX;
        int startY = anchorCellY - stampAnchorY;

        int estimated = stamp.Width * stamp.Height;
        estimated = Math.Clamp(estimated, 0, 1_000_000);

        List<int> indices = new(capacity: Math.Min(estimated, 4096));
        List<NmpCellData> before = new(capacity: Math.Min(estimated, 4096));
        List<NmpCellData> after = new(capacity: Math.Min(estimated, 4096));

        for (int sy = 0; sy < stamp.Height; sy++)
        {
            int destY = startY + sy;
            if (destY < 0 || destY >= map.Height)
            {
                continue;
            }

            int rowBase = checked(sy * stamp.Width);
            for (int sx = 0; sx < stamp.Width; sx++)
            {
                int destX = startX + sx;
                if (destX < 0 || destX >= map.Width)
                {
                    continue;
                }

                int destIndex = map.GetIndex(destX, destY);
                if ((uint)destIndex >= (uint)map.Cells.Length)
                {
                    continue;
                }

                int srcIndex = rowBase + sx;
                if ((uint)srcIndex >= (uint)stamp.Cells.Length)
                {
                    continue;
                }

                NmpCellData srcCell = stamp.Cells[srcIndex];
                NmpCellData destBefore = map.Cells[destIndex];
                NmpCellData destAfter = destBefore;

                if (!TryApplyStampCell(ref destAfter, srcCell,
                        overwriteEmpty: overwriteEmpty,
                        applyBack: applyBack,
                        applyMiddle: applyMiddle,
                        applyFront: applyFront,
                        applyUnderObject: applyUnderObject,
                        applyOverObject: applyOverObject,
                        applyNearGround: applyNearGround,
                        applyBlocked: applyBlocked))
                {
                    continue;
                }

                map.Cells[destIndex] = destAfter;
                indices.Add(destIndex);
                before.Add(destBefore);
                after.Add(destAfter);
            }
        }

        if (indices.Count <= 0)
        {
            _state.StatusMessage = isMovePlacement ? "移动选区：放置未发生变化。" : "盖章：未发生变化。";
            return;
        }

        string actionName = isMovePlacement ? $"移动选区：放置（{indices.Count} 格）" : $"盖章（{indices.Count} 格）";
        _state.History.Push(new MultiCellEditAction(actionName, map, indices.ToArray(), before.ToArray(), after.ToArray()));
        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = actionName;

        if (isMovePlacement)
        {
            ClearMoveSelectionSession(restorePreviousStampSource: true, restoreTool: true, updateStatus: false, statusMessage: null);
            _state.StatusMessage = $"{actionName}（移动完成）";
        }

        InvalidateRuntimeMinimapActiveDocument();
    }

    private static bool TryApplyStampCell(
        ref NmpCellData dest,
        in NmpCellData src,
        bool overwriteEmpty,
        bool applyBack,
        bool applyMiddle,
        bool applyFront,
        bool applyUnderObject,
        bool applyOverObject,
        bool applyNearGround,
        bool applyBlocked)
    {
        bool changed = false;

        if (applyBack)
        {
            bool srcEmpty = src.BackImage == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                ushort desiredImage = src.BackImage;
                ushort desiredLibrary = desiredImage == 0 ? (ushort)0 : src.BackLibrary;
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultSmTilesLibrary;
                }

                if (dest.BackImage != desiredImage || dest.BackLibrary != desiredLibrary)
                {
                    dest.BackImage = desiredImage;
                    dest.BackLibrary = desiredLibrary;
                    changed = true;
                }

                if (dest.BackAnimTick != src.BackAnimTick)
                {
                    dest.BackAnimTick = src.BackAnimTick;
                    changed = true;
                }

                byte desiredFlags = desiredImage == 0 ? (byte)(dest.Flags & ~FlagSmTiles) : (byte)(dest.Flags | FlagSmTiles);
                if (dest.Flags != desiredFlags)
                {
                    dest.Flags = desiredFlags;
                    changed = true;
                }
            }
        }

        if (applyMiddle)
        {
            bool srcEmpty = src.MiddleImage == 0 && src.MiddleImage2 == 0 && src.MiddleAlphaMask == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                ushort desiredImage = src.MiddleImage;
                ushort desiredLibrary = desiredImage == 0 ? (ushort)0 : src.MiddleLibrary;
                if (desiredImage != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultTilesLibrary;
                }

                if (dest.MiddleImage != desiredImage || dest.MiddleLibrary != desiredLibrary)
                {
                    dest.MiddleImage = desiredImage;
                    dest.MiddleLibrary = desiredLibrary;
                    changed = true;
                }

                if (dest.MiddleImage2 != src.MiddleImage2 || dest.MiddleLibrary2 != src.MiddleLibrary2 || dest.MiddleAlphaMask != src.MiddleAlphaMask)
                {
                    dest.MiddleImage2 = src.MiddleImage2;
                    dest.MiddleLibrary2 = src.MiddleLibrary2;
                    dest.MiddleAlphaMask = src.MiddleAlphaMask;
                    changed = true;
                }

                if (dest.MiddleAnimTick != src.MiddleAnimTick)
                {
                    dest.MiddleAnimTick = src.MiddleAnimTick;
                    changed = true;
                }

                byte desiredFlags = desiredImage == 0 ? (byte)(dest.Flags & ~FlagTiles) : (byte)(dest.Flags | FlagTiles);
                if (dest.Flags != desiredFlags)
                {
                    dest.Flags = desiredFlags;
                    changed = true;
                }
            }
        }

        if (applyFront)
        {
            bool srcEmpty = (src.FrontImage & 0x00FFFFFFu) == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                uint desiredPacked = srcEmpty ? 0u : (src.FrontImage & 0x00FFFFFFu);
                byte desiredLibrary = srcEmpty ? (byte)0 : src.FrontLibrary;
                if (desiredPacked != 0 && desiredLibrary == 0)
                {
                    desiredLibrary = DefaultObjectLibrary;
                }

                if (dest.FrontImage != desiredPacked || dest.FrontLibrary != desiredLibrary)
                {
                    dest.FrontImage = desiredPacked;
                    dest.FrontLibrary = desiredLibrary;
                    changed = true;
                }

                if (dest.FrontAnimFrame != src.FrontAnimFrame || dest.FrontAnimTick != src.FrontAnimTick)
                {
                    dest.FrontAnimFrame = src.FrontAnimFrame;
                    dest.FrontAnimTick = src.FrontAnimTick;
                    changed = true;
                }

                if (dest.ObjectHeight != src.ObjectHeight)
                {
                    dest.ObjectHeight = src.ObjectHeight;
                    changed = true;
                }

                byte desiredFlags = desiredPacked == 0 ? (byte)(dest.Flags & ~FlagObject) : (byte)(dest.Flags | FlagObject);
                if (dest.Flags != desiredFlags)
                {
                    dest.Flags = desiredFlags;
                    changed = true;
                }
            }
        }

        if (applyUnderObject)
        {
            bool srcEmpty = (src.UnderObject & 0x00FFFFFFu) == 0 && (src.Flags & NmpFlagUnderObject) == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                if (dest.UnderObject != src.UnderObject)
                {
                    dest.UnderObject = src.UnderObject;
                    changed = true;
                }

                byte desiredFlags = (byte)((dest.Flags & ~NmpFlagUnderObject) | (src.Flags & NmpFlagUnderObject));
                if (dest.Flags != desiredFlags)
                {
                    dest.Flags = desiredFlags;
                    changed = true;
                }
            }
        }

        if (applyOverObject)
        {
            bool srcEmpty = (src.OverObject & 0x00FFFFFFu) == 0 && (src.ExtendedAttributes & NmpExAttrOverObject) == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                if (dest.OverObject != src.OverObject)
                {
                    dest.OverObject = src.OverObject;
                    changed = true;
                }

                ushort desiredExAttr = (ushort)((dest.ExtendedAttributes & ~NmpExAttrOverObject) | (src.ExtendedAttributes & NmpExAttrOverObject));
                if (dest.ExtendedAttributes != desiredExAttr)
                {
                    dest.ExtendedAttributes = desiredExAttr;
                    changed = true;
                }
            }
        }

        if (applyNearGround)
        {
            bool srcEmpty = (src.NearGround & 0x00FFFFFFu) == 0 && (src.Flags & NmpFlagNearGround) == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                if (dest.NearGround != src.NearGround)
                {
                    dest.NearGround = src.NearGround;
                    changed = true;
                }

                byte desiredFlags = (byte)((dest.Flags & ~NmpFlagNearGround) | (src.Flags & NmpFlagNearGround));
                if (dest.Flags != desiredFlags)
                {
                    dest.Flags = desiredFlags;
                    changed = true;
                }
            }
        }

        if (applyBlocked)
        {
            bool srcEmpty = (src.Flags & NmpFlagBlocked) == 0;
            if (!srcEmpty || overwriteEmpty)
            {
                byte desiredFlags = srcEmpty ? (byte)(dest.Flags & ~NmpFlagBlocked) : (byte)(dest.Flags | NmpFlagBlocked);
                if (dest.Flags != desiredFlags)
                {
                    dest.Flags = desiredFlags;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void DrawStampPreviewOverlay(MapDocument map, ImDrawListPtr drawList, Vector2 canvasScreenPos)
    {
        if (_state.Tool != MapEditTool.Stamp)
        {
            return;
        }

        if (_state.HoverCellX < 0 || _state.HoverCellY < 0)
        {
            return;
        }

        if (!EnsureStampLoaded(forceReload: false, out _))
        {
            return;
        }

        if (_stampMap is null)
        {
            return;
        }

        MapDocument stamp = _stampMap;
        if (stamp.Width <= 0 || stamp.Height <= 0)
        {
            return;
        }

        StampAnchorMode anchorMode = _state.StampAnchor;
        if (_state.StampSource == StampSourceKind.MoveSelection && _moveSelection is not null)
        {
            anchorMode = StampAnchorMode.TopLeft;
        }

        int stampAnchorX = 0;
        int stampAnchorY = 0;
        if (anchorMode == StampAnchorMode.Center)
        {
            stampAnchorX = stamp.Width / 2;
            stampAnchorY = stamp.Height / 2;
        }

        int startX = _state.HoverCellX - stampAnchorX;
        int startY = _state.HoverCellY - stampAnchorY;

        float zoom = _state.Camera.Zoom;
        zoom = Math.Clamp(zoom, _state.Camera.MinZoom, _state.Camera.MaxZoom);
        float cellW = BaseCellWidth * zoom;
        float cellH = BaseCellHeight * zoom;

        float sx = canvasScreenPos.X + _state.Camera.PanX + startX * cellW;
        float sy = canvasScreenPos.Y + _state.Camera.PanY + startY * cellH;

        Vector2 min = new(sx, sy);
        Vector2 max = new(sx + stamp.Width * cellW, sy + stamp.Height * cellH);

        drawList.AddRectFilled(min, max, PackColor(90, 190, 255, 26));
        drawList.AddRect(min, max, PackColor(90, 190, 255, 220), 0.0f, ImDrawFlags.None, 2.0f);
    }

    private void HandleCanvasDragDrop(MapDocument map, Vector2 canvasScreenPos)
    {
        if (_state.Map is null || !ReferenceEquals(_state.Map, map))
        {
            return;
        }

        if (!ImGui.BeginDragDropTarget())
        {
            return;
        }

        try
        {
            ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragDropPayloadPrefabPath);
            if (payload.Data == nint.Zero || payload.DataSize <= 0)
            {
                return;
            }

            if (!TryGetPayloadUtf8String(payload, out string droppedPath))
            {
                _state.StatusMessage = "拖拽放置 Prefab 失败：无法读取拖拽数据。";
                return;
            }

            if (_moveSelection is not null)
            {
                _state.StatusMessage = "拖拽放置 Prefab：当前正在移动选区，请先按 Esc 取消移动。";
                return;
            }

            if (!IsPrefabPath(droppedPath))
            {
                _state.StatusMessage = $"拖拽放置 Prefab：不是有效的 prefab 路径: {droppedPath}";
                return;
            }

            Vector2 mouse = ImGui.GetIO().MousePos;
            (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
            if (!map.IsInBounds(x, y))
            {
                return;
            }

            _state.StampPath = droppedPath;
            _state.StampSource = StampSourceKind.Path;
            _state.Tool = MapEditTool.Stamp;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            ClearStamp();

            _state.SelectedCellX = x;
            _state.SelectedCellY = y;
            ApplyStampAt(map, x, y);
            if (!string.IsNullOrWhiteSpace(_state.StatusMessage))
            {
                _state.StatusMessage = $"{_state.StatusMessage}（Prefab：{Path.GetFileName(droppedPath)}）";
            }
        }
        finally
        {
            ImGui.EndDragDropTarget();
        }
    }

    private static bool TryGetPayloadUtf8String(ImGuiPayloadPtr payload, out string value)
    {
        value = string.Empty;

        nint data = payload.Data;
        int size = payload.DataSize;
        if (data == nint.Zero || size <= 0)
        {
            return false;
        }

        byte[] bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);

        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0)
        {
            len = size;
        }

        value = Encoding.UTF8.GetString(bytes, 0, len);
        return !string.IsNullOrWhiteSpace(value);
    }

    private void UpdateHoverCell(MapDocument map, bool hovered, Vector2 canvasScreenPos)
    {
        if (!hovered)
        {
            _state.HoverCellX = -1;
            _state.HoverCellY = -1;
            return;
        }

        Vector2 mouse = ImGui.GetIO().MousePos;
        (int x, int y) = GetCellAtMouse(map, mouse, canvasScreenPos);
        if (!map.IsInBounds(x, y))
        {
            _state.HoverCellX = -1;
            _state.HoverCellY = -1;
            return;
        }

        _state.HoverCellX = x;
        _state.HoverCellY = y;
    }

    private (int x, int y) GetCellAtMouse(MapDocument map, Vector2 mouse, Vector2 canvasScreenPos)
    {
        _ = map;
        float cellW = BaseCellWidth * _state.Camera.Zoom;
        float cellH = BaseCellHeight * _state.Camera.Zoom;
        if (cellW <= 0.001f || cellH <= 0.001f)
        {
            return (-1, -1);
        }

        float localX = mouse.X - canvasScreenPos.X - _state.Camera.PanX;
        float localY = mouse.Y - canvasScreenPos.Y - _state.Camera.PanY;

        int x = (int)MathF.Floor(localX / cellW);
        int y = (int)MathF.Floor(localY / cellH);
        return (x, y);
    }

    private void DrawMapTilesAndGrid(MapDocument map, ImDrawListPtr drawList, Vector2 canvasScreenPos, Vector2 canvasSize)
    {
        if (map.Width <= 0 || map.Height <= 0)
        {
            return;
        }

        float zoom = _state.Camera.Zoom;
        float cellW = BaseCellWidth * zoom;
        float cellH = BaseCellHeight * zoom;
        if (cellW <= 1.0f || cellH <= 1.0f)
        {
            return;
        }

        float worldMinX = (-_state.Camera.PanX) / cellW;
        float worldMinY = (-_state.Camera.PanY) / cellH;
        float worldMaxX = (canvasSize.X - _state.Camera.PanX) / cellW;
        float worldMaxY = (canvasSize.Y - _state.Camera.PanY) / cellH;

        // 旧版编辑器会额外扩展可见区域，以确保“格子在屏幕外但精灵伸进屏幕内”的对象也能被绘制。
        // 这对一些偏移较大的物件/特效很关键（否则会出现“边缘缺东西”的错觉）。
        const int margin = 20;
        int startX = Math.Max(0, (int)MathF.Floor(worldMinX) - margin);
        int startY = Math.Max(0, (int)MathF.Floor(worldMinY) - margin);
        int endX = Math.Min(map.Width - 1, (int)MathF.Ceiling(worldMaxX) + margin);
        int endY = Math.Min(map.Height - 1, (int)MathF.Ceiling(worldMaxY) + margin);

        int visibleW = endX - startX + 1;
        int visibleH = endY - startY + 1;
        if (visibleW <= 0 || visibleH <= 0)
        {
            return;
        }

        long visibleCells = (long)visibleW * visibleH;

        bool drawFill = _state.ShowTileFill;
        bool drawGrid = _state.ShowGrid;
        bool drawBlocked = _state.ShowBlockedOverlay;

        bool showBackLayer = _state.ShowBackLayer;
        bool showMiddleLayer = _state.ShowMiddleLayer;
        bool showFloorLayer = _state.ShowFloorLayer;
        bool showUnderFrontLayer = _state.ShowUnderFrontLayer;
        bool showFrontLayer = _state.ShowFrontLayer;
        bool showOverFrontLayer = _state.ShowOverFrontLayer;

        // 旧版编辑器不做“跳格渲染(stride)”以保证不同版本地图在任意格子上的数据都能正确显示。
        bool canDrawTextures = _state.RenderUseTextures
            && _textureLoader is not null
            && _textureIndex.IsReady;
        bool suppressBorder = _state.RenderSuppressBorderCells;
        float renderNightFactor = MapLighting.Resolve(_state.RenderLighting).NightFactor;

        uint lightingOverlayTint = 0;
        if (canDrawTextures && _state.RenderApplyLightingOverlay)
        {
            int overlayAlpha = ComputeLightingOverlayAlpha(renderNightFactor, _state.RenderLightingOverlayMaxAlpha);
            if (overlayAlpha > 0)
            {
                lightingOverlayTint = PackColor(24, 34, 58, (byte)overlayAlpha);
            }
        }

        uint lightSpriteTint = 0;
        bool drawLightSprites = false;
        if (canDrawTextures && _state.RenderIncludeLightSprites && showOverFrontLayer)
        {
            byte alpha = ComputeLightSpriteAlpha(renderNightFactor);
            if (alpha > 0)
            {
                lightSpriteTint = PackColor(255, 232, 186, alpha);
                drawLightSprites = true;
            }
        }

        uint gridColor = PackColor(_state.GridColor);
        float gridThickness = Math.Clamp(_state.GridThickness, 1.0f, 5.0f);

        uint blockedOverlayColor = PackColor(_state.BlockedOverlayColor);
        uint layerEmptyColor = PackColor(26, 26, 30, 90);

        float stepW = cellW;
        float stepH = cellH;

        Vector2 mapMin = new(canvasScreenPos.X + _state.Camera.PanX, canvasScreenPos.Y + _state.Camera.PanY);
        Vector2 mapMax = new(mapMin.X + map.Width * cellW, mapMin.Y + map.Height * cellH);

        if (drawFill)
        {
            // Base fill to keep empty cells visible (useful when textures are disabled/unavailable).
            drawList.AddRectFilled(mapMin, mapMax, layerEmptyColor);
        }

        Vector2 clipMin = canvasScreenPos;
        Vector2 clipMax = canvasScreenPos + canvasSize;

        bool animateTextures = canDrawTextures && _state.RenderAnimateTextures;
        long tick = 0;
        if (animateTextures)
        {
            float fps = Math.Clamp(_state.TextureAnimationFps, 1.0f, 1000.0f);
            tick = (long)Math.Floor(ImGui.GetTime() * fps);
        }

        int GetAnimFrame(int packageId, int imageIndex, int cellX, int cellY)
        {
            if (!animateTextures)
            {
                return 0;
            }

            if (!_textureIndex.TryGetCachedFrameCount(packageId, imageIndex, out int frameCount) || frameCount <= 1)
            {
                return 0;
            }

            int offset = _state.TextureAnimationPerCellOffset ? ComputeCellAnimationOffset(cellX, cellY) : 0;
            return (int)((tick + offset) % frameCount);
        }

        TextureDrawStatus TryDrawObjectTexture(
            LoadedTextureInfo info,
            uint tintColor,
            float cellScreenX,
            float cellScreenY,
            float cellHeight,
            float cellHeightLiftPx,
            float objectHeightLiftPx)
        {
            if (info.TextureId == nint.Zero || info.Width <= 0 || info.Height <= 0)
            {
                return TextureDrawStatus.TextureUnavailable;
            }

            float tileW = info.Width * zoom;
            float tileH = info.Height * zoom;
            if (tileW <= 0.5f || tileH <= 0.5f)
            {
                return TextureDrawStatus.TextureUnavailable;
            }

            float baseY = cellScreenY + cellHeight - tileH;
            float drawX = cellScreenX - info.CenterX * zoom + info.OffsetX * zoom;
            float drawY = baseY - info.CenterY * zoom + info.OffsetY * zoom - (cellHeightLiftPx + objectHeightLiftPx) * zoom;

            if (drawX + tileW < clipMin.X || drawX > clipMax.X ||
                drawY + tileH < clipMin.Y || drawY > clipMax.Y)
            {
                return TextureDrawStatus.Culled;
            }

            drawList.AddImage(info.TextureId,
                new Vector2(drawX, drawY),
                new Vector2(drawX + tileW, drawY + tileH),
                Vector2.Zero,
                Vector2.One,
                tintColor);
            return TextureDrawStatus.Drawn;
        }

        void DrawLayerFill(uint layerKey, float sx, float sy)
        {
            if (layerKey == 0)
            {
                return;
            }

            Vector2 min = new(sx, sy);
            Vector2 max = new(sx + stepW, sy + stepH);
            drawList.AddRectFilled(min, max, HashToColor(layerKey, alpha: 140));
        }

        int visibleTextureUnavailable = 0;
        int visibleTextureCulled = 0;
        List<string>? visibleTextureUnavailableSamples = canDrawTextures ? new List<string>(capacity: 8) : null;
        List<string>? visibleTextureCulledSamples = canDrawTextures ? new List<string>(capacity: 8) : null;

        void RecordVisibleTextureOutcome(MapLayer layer, uint layerKey, int cellX, int cellY, TextureDrawStatus drawStatus)
        {
            if (!canDrawTextures || layerKey == 0 || drawStatus is TextureDrawStatus.None or TextureDrawStatus.Drawn)
            {
                return;
            }

            int pkg = (int)((layerKey >> 16) & 0xFFFFu);
            int img = (int)(layerKey & 0xFFFFu);
            string sample = $"{layer}@({cellX},{cellY}) pkg={pkg} img={img}";

            if (drawStatus == TextureDrawStatus.TextureUnavailable)
            {
                visibleTextureUnavailable++;
                if (visibleTextureUnavailableSamples is not null && visibleTextureUnavailableSamples.Count < 8)
                {
                    visibleTextureUnavailableSamples.Add(sample);
                }
            }
            else if (drawStatus == TextureDrawStatus.Culled)
            {
                visibleTextureCulled++;
                if (visibleTextureCulledSamples is not null && visibleTextureCulledSamples.Count < 8)
                {
                    visibleTextureCulledSamples.Add(sample);
                }
            }
        }

        // --- Pass 1: Middle layer (Tiles) ---
        if (showMiddleLayer)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (suppressBorder && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    uint layerKey = GetLayerKey(cell, MapLayer.Middle);
                    if (layerKey == 0)
                    {
                        continue;
                    }

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;
                    TextureDrawStatus drawStatus = TextureDrawStatus.None;
                    if (canDrawTextures)
                    {
                        drawStatus = TryDrawCellTexture(MapLayer.Middle, cell, x, y, sx, sy, cellH, zoom, canvasScreenPos, canvasSize, drawList);
                    }
                    bool drawnTexture = drawStatus == TextureDrawStatus.Drawn;

                    if (canDrawTextures)
                    {
                        RecordVisibleTextureOutcome(MapLayer.Middle, layerKey, x, y, drawStatus);
                    }

                    if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                    {
                        DrawLayerFill(layerKey, sx, sy);
                    }
                }
            }
        }

        // --- Pass 2: Back layer (SmTiles) ---
        if (showBackLayer)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (suppressBorder && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    uint layerKey = GetLayerKey(cell, MapLayer.Back);
                    if (layerKey == 0)
                    {
                        continue;
                    }

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;

                    TextureDrawStatus drawStatus = TextureDrawStatus.None;
                    if (canDrawTextures)
                    {
                        drawStatus = TryDrawCellTexture(MapLayer.Back, cell, x, y, sx, sy, cellH, zoom, canvasScreenPos, canvasSize, drawList);
                    }
                    bool drawnTexture = drawStatus == TextureDrawStatus.Drawn;

                    if (canDrawTextures)
                    {
                        RecordVisibleTextureOutcome(MapLayer.Back, layerKey, x, y, drawStatus);
                    }

                    if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                    {
                        DrawLayerFill(layerKey, sx, sy);
                    }
                }
            }
        }

        // --- Pass 3: Floor layer (NearGround) ---
        if (showFloorLayer)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (suppressBorder && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    uint layerKey = GetLayerKey(cell, MapLayer.Floor);
                    if (layerKey == 0)
                    {
                        continue;
                    }

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;

                    TextureDrawStatus drawStatus = TextureDrawStatus.None;
                    if (canDrawTextures)
                    {
                        drawStatus = TryDrawCellTexture(MapLayer.Floor, cell, x, y, sx, sy, cellH, zoom, canvasScreenPos, canvasSize, drawList);
                    }
                    bool drawnTexture = drawStatus == TextureDrawStatus.Drawn;

                    if (canDrawTextures)
                    {
                        RecordVisibleTextureOutcome(MapLayer.Floor, layerKey, x, y, drawStatus);
                    }

                    if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                    {
                        DrawLayerFill(layerKey, sx, sy);
                    }
                }
            }
        }

        // --- Pass 4: Object layers (short-front -> underfront -> front -> overfront) ---
        if (showFrontLayer || showUnderFrontLayer || showOverFrontLayer)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (suppressBorder && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;

                    float cellHeightLiftPx = ComputeCellHeightLiftPx(cell);
                    float objectHeightLiftPx = ComputeObjectHeightLiftPx(cell);

                    uint frontKey = showFrontLayer ? GetLayerKey(cell, MapLayer.Front) : 0u;
                    int frontPackageId = 0;
                    int frontImageIndex = 0;
                    int frontFrame = 0;
                    bool frontInfoOk = false;
                    LoadedTextureInfo frontInfo = default;
                    bool frontIsShort = false;

                    if (frontKey != 0)
                    {
                        frontPackageId = (int)((frontKey >> 16) & 0xFFFFu);
                        frontImageIndex = (int)(frontKey & 0xFFFFu);

                        if (canDrawTextures)
                        {
                            frontFrame = GetAnimFrame(frontPackageId, frontImageIndex, x, y);
                            frontInfoOk = _textureLoader!.TryGetTexture(frontPackageId, frontImageIndex, frontFrame, out frontInfo);
                            if (frontInfoOk)
                            {
                                frontIsShort = frontInfo.Height <= 32;
                            }
                        }
                    }

                    bool frontNearGroundFlag = (cell.Flags & NmpFlagNearGround) != 0;
                    bool drawFrontInShortPass = frontKey != 0 && (frontNearGroundFlag || frontIsShort);

                    // 4a. Short front objects
                    if (frontKey != 0 && drawFrontInShortPass)
                    {
                        TextureDrawStatus drawStatus = TextureDrawStatus.None;
                        if (canDrawTextures)
                        {
                            if (frontInfoOk)
                            {
                                uint tint = BuildRuntimeTintColor(cell.ColorAdjObject);
                                drawStatus = TryDrawObjectTexture(frontInfo, tint, sx, sy, cellH, cellHeightLiftPx, objectHeightLiftPx);
                            }
                            else
                            {
                                drawStatus = TextureDrawStatus.TextureUnavailable;
                            }
                        }
                        bool drawn = drawStatus == TextureDrawStatus.Drawn;

                        if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                        {
                            DrawLayerFill(frontKey, sx, sy);
                        }

                        if (canDrawTextures)
                        {
                            RecordVisibleTextureOutcome(MapLayer.Front, frontKey, x, y, drawStatus);
                        }
                    }

                    // 4b. UnderFront objects
                    if (showUnderFrontLayer)
                    {
                        uint underKey = GetLayerKey(cell, MapLayer.UnderFront);
                        if (underKey != 0)
                        {
                            TextureDrawStatus drawStatus = TextureDrawStatus.None;
                            if (canDrawTextures)
                            {
                                drawStatus = TryDrawCellTexture(MapLayer.UnderFront, cell, x, y, sx, sy, cellH, zoom, canvasScreenPos, canvasSize, drawList);
                            }
                            bool drawn = drawStatus == TextureDrawStatus.Drawn;

                            if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                            {
                                DrawLayerFill(underKey, sx, sy);
                            }

                            if (canDrawTextures)
                            {
                                RecordVisibleTextureOutcome(MapLayer.UnderFront, underKey, x, y, drawStatus);
                            }
                        }
                    }

                    // 4c. Tall front objects
                    if (frontKey != 0 && !drawFrontInShortPass)
                    {
                        TextureDrawStatus drawStatus = TextureDrawStatus.None;
                        if (canDrawTextures)
                        {
                            if (!frontInfoOk)
                            {
                                frontFrame = GetAnimFrame(frontPackageId, frontImageIndex, x, y);
                                frontInfoOk = _textureLoader!.TryGetTexture(frontPackageId, frontImageIndex, frontFrame, out frontInfo);
                            }

                            if (frontInfoOk)
                            {
                                uint tint = BuildRuntimeTintColor(cell.ColorAdjObject);
                                drawStatus = TryDrawObjectTexture(frontInfo, tint, sx, sy, cellH, cellHeightLiftPx, objectHeightLiftPx);
                            }
                            else
                            {
                                drawStatus = TextureDrawStatus.TextureUnavailable;
                            }
                        }
                        bool drawn = drawStatus == TextureDrawStatus.Drawn;

                        if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                        {
                            DrawLayerFill(frontKey, sx, sy);
                        }

                        if (canDrawTextures)
                        {
                            RecordVisibleTextureOutcome(MapLayer.Front, frontKey, x, y, drawStatus);
                        }
                    }

                    // 4d. OverFront objects (light images are drawn in the lighting sprite pass)
                    if (showOverFrontLayer)
                    {
                        uint overKey = GetLayerKey(cell, MapLayer.OverFront);
                        if (overKey != 0)
                        {
                            int overPkg = (int)((overKey >> 16) & 0xFFFFu);
                            int overImg = (int)(overKey & 0xFFFFu);
                            if (!IsLightImage(overPkg, overImg))
                            {
                                TextureDrawStatus drawStatus = TextureDrawStatus.None;
                                if (canDrawTextures)
                                {
                                    drawStatus = TryDrawCellTexture(MapLayer.OverFront, cell, x, y, sx, sy, cellH, zoom, canvasScreenPos, canvasSize, drawList);
                                }
                                bool drawn = drawStatus == TextureDrawStatus.Drawn;

                                if (drawFill && (!canDrawTextures || drawStatus == TextureDrawStatus.TextureUnavailable))
                                {
                                    DrawLayerFill(overKey, sx, sy);
                                }

                                if (canDrawTextures)
                                {
                                    RecordVisibleTextureOutcome(MapLayer.OverFront, overKey, x, y, drawStatus);
                                }
                            }
                        }
                    }
                }
            }
        }

        MaybeLogVisibleTextureStates(
            map,
            visibleTextureUnavailable,
            visibleTextureUnavailableSamples,
            visibleTextureCulled,
            visibleTextureCulledSamples);

        if (lightingOverlayTint != 0)
        {
            Vector2 canvasMin = canvasScreenPos;
            Vector2 canvasMax = canvasScreenPos + canvasSize;
            drawList.AddRectFilled(canvasMin, canvasMax, lightingOverlayTint);
        }

        if (drawLightSprites)
        {
            DrawRuntimeLightSprites(map, startX, startY, endX, endY, cellW, cellH, zoom, canvasScreenPos, canvasSize, drawList, lightSpriteTint);
        }

        // Grid overlay
        if (drawGrid && cellW >= 8.0f && cellH >= 8.0f && visibleCells <= 50_000)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;
                    Vector2 min = new(sx, sy);
                    Vector2 max = new(sx + stepW, sy + stepH);
                    drawList.AddRect(min, max, gridColor, 0.0f, ImDrawFlags.None, gridThickness);
                }
            }
        }

        // Blocked cells overlay
        if (drawBlocked)
        {
            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if ((cell.Flags & NmpFlagBlocked) == 0)
                    {
                        continue;
                    }

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;
                    Vector2 min = new(sx, sy);
                    Vector2 max = new(sx + stepW, sy + stepH);
                    drawList.AddRectFilled(min, max, blockedOverlayColor);
                }
            }
        }

        // Cell-highlight overlays — highlight cells that have data on each layer (old MapEditor parity).
        bool highlightAnyLayerCells = _state.HighlightBackCells
            || _state.HighlightMiddleCells
            || _state.HighlightFrontCells
            || _state.HighlightFloorCells
            || _state.HighlightUnderFrontCells
            || _state.HighlightOverFrontCells
            || _state.HighlightCoastMaskCells;

        if (highlightAnyLayerCells)
        {
            uint backCol = _state.HighlightBackCells ? PackColor(_state.HighlightBackColor) : 0;
            uint middleCol = _state.HighlightMiddleCells ? PackColor(_state.HighlightMiddleColor) : 0;
            uint frontCol = _state.HighlightFrontCells ? PackColor(_state.HighlightFrontColor) : 0;
            uint floorCol = _state.HighlightFloorCells ? PackColor(_state.HighlightFloorColor) : 0;
            uint underCol = _state.HighlightUnderFrontCells ? PackColor(_state.HighlightUnderFrontColor) : 0;
            uint overCol = _state.HighlightOverFrontCells ? PackColor(_state.HighlightOverFrontColor) : 0;
            uint coastMaskCol = _state.HighlightCoastMaskCells ? PackColor(_state.HighlightCoastMaskColor) : 0;

            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;
                    Vector2 min = new(sx, sy);
                    Vector2 max = new(sx + stepW, sy + stepH);

                    if (_state.HighlightBackCells && cell.BackImage != 0)
                    {
                        drawList.AddRectFilled(min, max, backCol);
                    }
                    if (_state.HighlightMiddleCells && cell.MiddleImage != 0)
                    {
                        drawList.AddRectFilled(min, max, middleCol);
                    }
                    if (_state.HighlightFrontCells && (cell.FrontImage & 0x00FFFFFFu) != 0)
                    {
                        drawList.AddRectFilled(min, max, frontCol);
                    }
                    if (_state.HighlightFloorCells && (cell.NearGround & 0x00FFFFFFu) != 0)
                    {
                        drawList.AddRectFilled(min, max, floorCol);
                    }
                    if (_state.HighlightUnderFrontCells && (cell.UnderObject & 0x00FFFFFFu) != 0)
                    {
                        drawList.AddRectFilled(min, max, underCol);
                    }
                    if (_state.HighlightOverFrontCells && (cell.OverObject & 0x00FFFFFFu) != 0)
                    {
                        drawList.AddRectFilled(min, max, overCol);
                    }

                    bool hasCoastMask = cell.MiddleImage2 != 0 && cell.MiddleImage != 0 && cell.MiddleAlphaMask != 0;
                    if (_state.HighlightCoastMaskCells && hasCoastMask)
                    {
                        drawList.AddRectFilled(min, max, coastMaskCol);
                    }
                }
            }
        }

        // Missing-texture overlay — highlight cells whose referenced texture cannot be resolved.
        if (canDrawTextures && _state.HighlightMissingTextureCells && visibleCells <= 30_000)
        {
            uint missingCol = PackColor(_state.HighlightMissingTextureColor);
            var existsCache = new Dictionary<long, bool>();

            bool HasImageCached(int pkg, int idx)
            {
                long key = ((long)pkg << 32) | (uint)idx;
                if (existsCache.TryGetValue(key, out bool cached))
                {
                    return cached;
                }

                bool exists = _textureIndex.HasImage(pkg, idx);
                existsCache[key] = exists;
                return exists;
            }

            static bool TryResolveBackTileRef(NmpCellData cell, out int pkg, out int idx)
            {
                idx = cell.BackImage;
                if (idx <= 0)
                {
                    pkg = 0;
                    return false;
                }

                pkg = cell.BackLibrary != 0 ? cell.BackLibrary : 3001;
                return pkg > 0;
            }

            static bool TryResolveMiddleTileRef(NmpCellData cell, out int pkg, out int idx)
            {
                idx = cell.MiddleImage;
                if (idx <= 0)
                {
                    pkg = 0;
                    return false;
                }

                pkg = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : 3051;
                return pkg > 0;
            }

            static bool TryResolveFrontObjectTileRef(NmpCellData cell, out int pkg, out int idx)
            {
                uint masked = cell.FrontImage & 0x00FFFFFFu;
                idx = (int)(masked & 0xFFFFu);
                if (idx <= 0)
                {
                    pkg = 0;
                    return false;
                }

                pkg = (int)((masked >> 16) & 0xFFu);
                if (pkg == 0)
                {
                    pkg = cell.FrontLibrary;
                }
                if (pkg == 0)
                {
                    pkg = DefaultObjectLibrary;
                }

                return true;
            }

            for (int y = startY; y <= endY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    bool missing = false;

                    if (TryResolveBackTileRef(cell, out int backPkg, out int backIdx)
                        && !HasImageCached(backPkg, backIdx))
                    {
                        missing = true;
                    }
                    if (!missing && TryResolveMiddleTileRef(cell, out int midPkg, out int midIdx)
                        && !HasImageCached(midPkg, midIdx))
                    {
                        missing = true;
                    }
                    if (!missing && TryResolveFrontObjectTileRef(cell, out int frontPkg, out int frontIdx)
                        && !HasImageCached(frontPkg, frontIdx))
                    {
                        missing = true;
                    }

                    if (!missing)
                    {
                        continue;
                    }

                    float sx = mapMin.X + x * cellW;
                    float sy = mapMin.Y + y * cellH;
                    Vector2 min = new(sx, sy);
                    Vector2 max = new(sx + stepW, sy + stepH);
                    drawList.AddRectFilled(min, max, missingCol);
                }
            }
        }

        drawList.AddRect(mapMin, mapMax, PackColor(220, 220, 240, 180), 0.0f, ImDrawFlags.None, 2.0f);
    }

    private void MaybeLogVisibleTextureStates(
        MapDocument map,
        int unavailableCount,
        List<string>? unavailableSamples,
        int culledCount,
        List<string>? culledSamples)
    {
        if (!_console.Initialized
            || _textureLoader is null
            || map is null
            || (unavailableCount <= 0 && culledCount <= 0))
        {
            return;
        }

        double now = ImGui.GetTime();
        string unavailableText = unavailableSamples is not null && unavailableSamples.Count > 0
            ? string.Join("; ", unavailableSamples)
            : string.Empty;
        string culledText = culledSamples is not null && culledSamples.Count > 0
            ? string.Join("; ", culledSamples)
            : string.Empty;
        string signature = $"{unavailableCount}|{culledCount}|{_textureLoader.PendingCount}|{_textureLoader.ErrorCount}|{unavailableText}|{culledText}";
        if ((now - _lastVisibleTextureMissLogTime) < 0.75
            && string.Equals(signature, _lastVisibleTextureMissSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastVisibleTextureMissLogTime = now;
        _lastVisibleTextureMissSignature = signature;

        string mapPath = string.IsNullOrWhiteSpace(_state.MapPath) ? (map.Path ?? string.Empty) : _state.MapPath;
        string unavailableSuffix = string.IsNullOrWhiteSpace(unavailableText) ? string.Empty : $" unavailableSamples={unavailableText}";
        string culledSuffix = string.IsNullOrWhiteSpace(culledText) ? string.Empty : $" culledSamples={culledText}";
        MapEditorConsoleLogLevel level = unavailableCount > 0 ? MapEditorConsoleLogLevel.Warning : MapEditorConsoleLogLevel.Info;
        _console.Append(level, "Texture",
            $"当前视口贴图状态：map={mapPath} unavailable={unavailableCount} culled={culledCount} pending={_textureLoader.PendingCount} errors={_textureLoader.ErrorCount}{unavailableSuffix}{culledSuffix}");
    }

    private TextureDrawStatus TryDrawCellTexture(
        MapLayer layer,
        NmpCellData cell,
        int cellX,
        int cellY,
        float cellScreenX,
        float cellScreenY,
        float cellHeight,
        float zoom,
        Vector2 canvasScreenPos,
        Vector2 canvasSize,
        ImDrawListPtr drawList)
    {
        if (_textureLoader is null)
        {
            return TextureDrawStatus.None;
        }

        if (layer == MapLayer.Middle && cell.MiddleImage2 != 0)
        {
            return TryDrawCoastCellTextures(cell, cellX, cellY, cellScreenX, cellScreenY, zoom, canvasScreenPos, canvasSize, drawList);
        }

        int packageId;
        int imageIndex;
        bool isObject = layer == MapLayer.Front
            || layer == MapLayer.Floor
            || layer == MapLayer.UnderFront
            || layer == MapLayer.OverFront;
        float cellHeightLiftPx = ComputeCellHeightLiftPx(cell);
        float objectHeightLiftPx = layer == MapLayer.Front ? ComputeObjectHeightLiftPx(cell) : 0.0f;

        if (layer == MapLayer.Back)
        {
            imageIndex = cell.BackImage;
            if (imageIndex == 0)
            {
                return TextureDrawStatus.None;
            }

            packageId = cell.BackLibrary != 0 ? cell.BackLibrary : 3001;
        }
        else if (layer == MapLayer.Middle)
        {
            imageIndex = cell.MiddleImage;
            if (imageIndex == 0)
            {
                return TextureDrawStatus.None;
            }

            packageId = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : 3051;
        }
        else if (layer == MapLayer.Floor)
        {
            uint packed = cell.NearGround & 0x00FFFFFFu;
            imageIndex = (int)(packed & 0xFFFFu);
            if (imageIndex == 0)
            {
                return TextureDrawStatus.None;
            }

            packageId = (int)((packed >> 16) & 0xFFu);
            if (packageId == 0)
            {
                packageId = DefaultObjectLibrary;
            }
        }
        else if (layer == MapLayer.UnderFront)
        {
            uint packed = cell.UnderObject & 0x00FFFFFFu;
            imageIndex = (int)(packed & 0xFFFFu);
            if (imageIndex == 0)
            {
                return TextureDrawStatus.None;
            }

            packageId = (int)((packed >> 16) & 0xFFu);
            if (packageId == 0)
            {
                packageId = DefaultObjectLibrary;
            }
        }
        else if (layer == MapLayer.Front)
        {
            imageIndex = (int)(cell.FrontImage & 0xFFFF);
            if (imageIndex == 0)
            {
                return TextureDrawStatus.None;
            }

            packageId = (int)((cell.FrontImage >> 16) & 0xFF);
            if (packageId == 0)
            {
                packageId = cell.FrontLibrary;
            }

            if (packageId == 0)
            {
                packageId = 5;
            }
        }
        else if (layer == MapLayer.OverFront)
        {
            uint packed = cell.OverObject & 0x00FFFFFFu;
            imageIndex = (int)(packed & 0xFFFFu);
            if (imageIndex == 0)
            {
                return TextureDrawStatus.None;
            }

            packageId = (int)((packed >> 16) & 0xFFu);
            if (packageId == 0)
            {
                packageId = DefaultObjectLibrary;
            }

            // OverFront layer: light images are rendered as light sprites, not regular objects.
            if (IsLightImage(packageId, imageIndex))
            {
                return TextureDrawStatus.None;
            }
        }
        else
        {
            return TextureDrawStatus.None;
        }

        int frame = 0;
        if (_state.RenderAnimateTextures
            && _textureIndex.TryGetCachedFrameCount(packageId, imageIndex, out int frameCount)
            && frameCount > 1)
        {
            float fps = Math.Clamp(_state.TextureAnimationFps, 1.0f, 1000.0f);
            long tick = (long)Math.Floor(ImGui.GetTime() * fps);
            int offset = _state.TextureAnimationPerCellOffset ? ComputeCellAnimationOffset(cellX, cellY) : 0;
            frame = (int)((tick + offset) % frameCount);
        }

        if (!_textureLoader.TryGetTexture(packageId, imageIndex, frame, out LoadedTextureInfo info))
        {
            return TextureDrawStatus.TextureUnavailable;
        }

        uint rawTint = layer switch
        {
            MapLayer.Back => cell.ColorAdjSmTile,
            MapLayer.Middle => cell.ColorAdjTile,
            MapLayer.Floor => cell.ColorAdjFloor,
            MapLayer.UnderFront => cell.ColorAdjEffect != 0 ? cell.ColorAdjEffect : cell.ColorAdjObject,
            MapLayer.Front => cell.ColorAdjObject,
            MapLayer.OverFront => cell.ColorOverObj != 0 ? cell.ColorOverObj : cell.ColorAdjEffect,
            _ => 0u,
        };
        uint tintColor = BuildRuntimeTintColor(rawTint);

        if (!isObject)
        {
            return TryDrawTileTexture(info, tintColor, cellScreenX, cellScreenY, zoom, cellHeightLiftPx, canvasScreenPos, canvasSize, drawList);
        }

        Vector2 clipMin = canvasScreenPos;
        Vector2 clipMax = canvasScreenPos + canvasSize;
        return TryDrawObjectTexture(info, tintColor, cellScreenX, cellScreenY, cellHeight, cellHeightLiftPx, objectHeightLiftPx, zoom, clipMin, clipMax, drawList);
    }

    private TextureDrawStatus TryDrawCoastCellTextures(
        NmpCellData cell,
        int cellX,
        int cellY,
        float cellScreenX,
        float cellScreenY,
        float zoom,
        Vector2 canvasScreenPos,
        Vector2 canvasSize,
        ImDrawListPtr drawList)
    {
        if (_textureLoader is null)
        {
            return TextureDrawStatus.None;
        }

        // Coast cell: draw ground tile (MiddleImage2) then coast tile masked by MiddleAlphaMask.
        bool drawnAny = false;
        bool hadUnavailable = false;
        bool hadCulled = false;
        float cellHeightLiftPx = ComputeCellHeightLiftPx(cell);

        float fps = Math.Clamp(_state.TextureAnimationFps, 1.0f, 1000.0f);
        long tick = (long)Math.Floor(ImGui.GetTime() * fps);
        int offset = _state.TextureAnimationPerCellOffset ? ComputeCellAnimationOffset(cellX, cellY) : 0;
        long animTick = tick + offset;

        int groundIdx = cell.MiddleImage2;
        if (groundIdx > 0)
        {
            int groundPkg = cell.MiddleLibrary2 != 0 ? cell.MiddleLibrary2 : 3051;
            uint groundTint = BuildRuntimeTintColor(cell.ColorAdjTile);
            int groundFrame = 0;
            if (_state.RenderAnimateTextures
                && _textureIndex.TryGetCachedFrameCount(groundPkg, groundIdx, out int groundFrames)
                && groundFrames > 1)
            {
                groundFrame = (int)(animTick % groundFrames);
            }

            if (_textureLoader.TryGetTexture(groundPkg, groundIdx, groundFrame, out LoadedTextureInfo ground))
            {
                TextureDrawStatus groundStatus = TryDrawTileTexture(ground, groundTint, cellScreenX, cellScreenY, zoom, cellHeightLiftPx, canvasScreenPos, canvasSize, drawList);
                drawnAny |= groundStatus == TextureDrawStatus.Drawn;
                hadUnavailable |= groundStatus == TextureDrawStatus.TextureUnavailable;
                hadCulled |= groundStatus == TextureDrawStatus.Culled;
            }
            else
            {
                hadUnavailable = true;
            }
        }

        int coastIdx = cell.MiddleImage;
        int maskIdx = cell.MiddleAlphaMask;
        if (coastIdx > 0 && maskIdx > 0)
        {
            int coastPkg = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : 49;
            uint coastTintRaw = cell.ColorAdjEffect != 0 ? cell.ColorAdjEffect : cell.ColorAdjTile;
            uint coastTint = BuildRuntimeTintColor(coastTintRaw);
            int coastFrame = 0;
            if (_state.RenderAnimateTextures
                && _textureIndex.TryGetCachedFrameCount(coastPkg, coastIdx, out int coastFrames)
                && coastFrames > 1)
            {
                coastFrame = (int)(animTick % coastFrames);
            }

            if (_textureLoader.TryGetCoastCompositeTexture(coastPkg, coastIdx, maskIdx, coastFrame, out LoadedTextureInfo coast))
            {
                TextureDrawStatus coastStatus = TryDrawTileTexture(coast, coastTint, cellScreenX, cellScreenY, zoom, cellHeightLiftPx, canvasScreenPos, canvasSize, drawList);
                drawnAny |= coastStatus == TextureDrawStatus.Drawn;
                hadUnavailable |= coastStatus == TextureDrawStatus.TextureUnavailable;
                hadCulled |= coastStatus == TextureDrawStatus.Culled;
            }
            else
            {
                hadUnavailable = true;
            }
        }

        if (drawnAny)
        {
            return TextureDrawStatus.Drawn;
        }

        if (hadUnavailable)
        {
            return TextureDrawStatus.TextureUnavailable;
        }

        if (hadCulled)
        {
            return TextureDrawStatus.Culled;
        }

        return TextureDrawStatus.None;
    }

    private static TextureDrawStatus TryDrawTileTexture(
        LoadedTextureInfo info,
        uint tintColor,
        float cellScreenX,
        float cellScreenY,
        float zoom,
        float liftPx,
        Vector2 canvasScreenPos,
        Vector2 canvasSize,
        ImDrawListPtr drawList)
    {
        if (info.TextureId == nint.Zero || info.Width <= 0 || info.Height <= 0)
        {
            return TextureDrawStatus.TextureUnavailable;
        }

        float tileW = info.Width * zoom;
        float tileH = info.Height * zoom;
        if (tileW <= 0.5f || tileH <= 0.5f)
        {
            return TextureDrawStatus.TextureUnavailable;
        }

        float drawX = cellScreenX + info.OffsetX * zoom;
        float drawY = cellScreenY + info.OffsetY * zoom - liftPx * zoom;

        Vector2 clipMin = canvasScreenPos;
        Vector2 clipMax = canvasScreenPos + canvasSize;

        if (drawX + tileW < clipMin.X || drawX > clipMax.X ||
            drawY + tileH < clipMin.Y || drawY > clipMax.Y)
        {
            return TextureDrawStatus.Culled;
        }

        drawList.AddImage(info.TextureId,
            new Vector2(drawX, drawY),
            new Vector2(drawX + tileW, drawY + tileH),
            Vector2.Zero,
            Vector2.One,
            tintColor);
        return TextureDrawStatus.Drawn;
    }

    private static TextureDrawStatus TryDrawObjectTexture(
        LoadedTextureInfo info,
        uint tintColor,
        float cellScreenX,
        float cellScreenY,
        float cellHeight,
        float cellHeightLiftPx,
        float objectHeightLiftPx,
        float zoom,
        Vector2 clipMin,
        Vector2 clipMax,
        ImDrawListPtr drawList)
    {
        if (info.TextureId == nint.Zero || info.Width <= 0 || info.Height <= 0)
        {
            return TextureDrawStatus.TextureUnavailable;
        }

        float tileW = info.Width * zoom;
        float tileH = info.Height * zoom;
        if (tileW <= 0.5f || tileH <= 0.5f)
        {
            return TextureDrawStatus.TextureUnavailable;
        }

        float baseY = cellScreenY + cellHeight - tileH;
        float drawX = cellScreenX - info.CenterX * zoom + info.OffsetX * zoom;
        float drawY = baseY - info.CenterY * zoom + info.OffsetY * zoom - (cellHeightLiftPx + objectHeightLiftPx) * zoom;

        if (drawX + tileW < clipMin.X || drawX > clipMax.X ||
            drawY + tileH < clipMin.Y || drawY > clipMax.Y)
        {
            return TextureDrawStatus.Culled;
        }

        drawList.AddImage(info.TextureId,
            new Vector2(drawX, drawY),
            new Vector2(drawX + tileW, drawY + tileH),
            Vector2.Zero,
            Vector2.One,
            tintColor);
        return TextureDrawStatus.Drawn;
    }

    private uint BuildRuntimeTintColor(uint rawTint)
    {
        if (!_state.RenderApplyCellTints)
        {
            return PackColor(255, 255, 255, 255);
        }

        uint rgb = rawTint & 0x00FFFFFFu;
        if (rgb == 0u || rgb == 0x00FFFFFFu)
        {
            return PackColor(255, 255, 255, 255);
        }

        float strength = Math.Clamp(_state.RenderTintStrength, 0.0f, 1.0f);
        if (strength <= 0.0f)
        {
            return PackColor(255, 255, 255, 255);
        }

        float alphaScale = ((rawTint >> 24) & 0xFFu) != 0u
            ? ((rawTint >> 24) & 0xFFu) / 255.0f
            : 1.0f;
        float blend = Math.Clamp(strength * alphaScale, 0.0f, 1.0f);
        if (blend <= 0.0f)
        {
            return PackColor(255, 255, 255, 255);
        }

        byte r = MixTintChannel((byte)((rgb >> 16) & 0xFFu), blend);
        byte g = MixTintChannel((byte)((rgb >> 8) & 0xFFu), blend);
        byte b = MixTintChannel((byte)(rgb & 0xFFu), blend);
        return PackColor(r, g, b, 255);
    }

    private static byte MixTintChannel(byte src, float blend)
    {
        float value = 255.0f + (src - 255.0f) * blend;
        int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < 0) rounded = 0;
        if (rounded > 255) rounded = 255;
        return (byte)rounded;
    }

    private float ComputeCellHeightLiftPx(NmpCellData cell)
    {
        if (!_state.RenderApplyCellHeightFlag)
        {
            return 0.0f;
        }

        if ((cell.Flags & NmpFlagNearGround) == 0)
        {
            return 0.0f;
        }

        float px = _state.RenderCellHeightFlagOffset;
        if (!float.IsFinite(px) || px <= 0.0f)
        {
            return 0.0f;
        }

        return Math.Clamp(px, 0.0f, 64.0f);
    }

    private float ComputeObjectHeightLiftPx(NmpCellData cell)
    {
        if (!_state.RenderApplyObjectHeight || cell.ObjectHeight == 0)
        {
            return 0.0f;
        }

        float scale = _state.RenderObjectHeightScale;
        if (!float.IsFinite(scale) || scale <= 0.0f)
        {
            return 0.0f;
        }

        return cell.ObjectHeight * scale;
    }

    private static int ComputeLightingOverlayAlpha(float nightFactor, int maxAlpha)
    {
        if (!float.IsFinite(nightFactor))
        {
            return 0;
        }

        nightFactor = Math.Clamp(nightFactor, 0.0f, 1.0f);
        maxAlpha = Math.Clamp(maxAlpha, 0, 255);
        if (nightFactor <= 0.0f || maxAlpha == 0)
        {
            return 0;
        }

        int alpha = (int)Math.Round(nightFactor * maxAlpha, MidpointRounding.AwayFromZero);
        return Math.Clamp(alpha, 0, 255);
    }

    private static byte ComputeLightSpriteAlpha(float nightFactor)
    {
        if (!float.IsFinite(nightFactor))
        {
            return 0;
        }

        float factor = Math.Clamp(nightFactor, 0.0f, 1.0f);
        float strength = Math.Clamp((factor - 0.08f) / 0.92f, 0.0f, 1.0f);
        if (strength <= 0.0f)
        {
            return 0;
        }

        int alpha = (int)Math.Round(48.0f + strength * 160.0f, MidpointRounding.AwayFromZero);
        alpha = Math.Clamp(alpha, 0, 255);
        return (byte)alpha;
    }

    private static void DrawLightingSettingsEditor(string idSuffix, MapLightingSettings settings)
    {
        if (settings is null)
        {
            ImGui.TextDisabled("光照设置为空。");
            return;
        }

        int modeIndex = (int)settings.Mode;
        modeIndex = Math.Clamp(modeIndex, 0, 4);

        if (ImGui.Combo($"光照模式##{idSuffix}", ref modeIndex, "白天\0夜晚\0自动（系统时间）\0自定义时间\0手动夜晚系数\0"))
        {
            settings.Mode = (MapLightingMode)modeIndex;
        }

        if (settings.Mode == MapLightingMode.CustomTime)
        {
            int hour = settings.CustomHour;
            int minute = settings.CustomMinute;

            ImGui.InputInt($"小时##{idSuffix}_hour", ref hour);
            ImGui.InputInt($"分钟##{idSuffix}_minute", ref minute);

            settings.CustomHour = Math.Clamp(hour, 0, 23);
            settings.CustomMinute = Math.Clamp(minute, 0, 59);
        }

        const string nightFactorFormat = "%.2f";
        if (settings.Mode == MapLightingMode.Manual)
        {
            float manual = settings.ManualNightFactor;
            if (!float.IsFinite(manual))
            {
                manual = 0.0f;
            }

            if (ImGui.SliderFloat($"夜晚系数（0=白天，1=夜晚）##{idSuffix}_factor", ref manual, 0.0f, 1.0f, nightFactorFormat))
            {
                settings.ManualNightFactor = Math.Clamp(manual, 0.0f, 1.0f);
            }
        }
        else
        {
            ResolvedMapLighting preview = MapLighting.Resolve(settings);
            float factor = preview.NightFactor;
            ImGui.BeginDisabled();
            ImGui.SliderFloat($"夜晚系数（0=白天，1=夜晚）##{idSuffix}_factor", ref factor, 0.0f, 1.0f, nightFactorFormat);
            ImGui.EndDisabled();
        }

        ResolvedMapLighting resolved = MapLighting.Resolve(settings);
        ImGui.TextDisabled($"当前：{resolved.Hour:00}:{resolved.Minute:00}:{resolved.Second:00}  夜晚系数={resolved.NightFactor:0.00}");
    }

    private static bool TryResolveExtraObject(uint raw, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        uint masked = raw & 0x00FFFFFFu;
        if (masked == 0)
        {
            return false;
        }

        imageIndex = (int)(masked & 0xFFFFu);
        if (imageIndex == 0)
        {
            return false;
        }

        packageId = (int)((masked >> 16) & 0xFFu);
        if (packageId == 0)
        {
            packageId = DefaultObjectLibrary;
        }

        return true;
    }

    private static bool IsLightImage(int packageId, int imageIndex)
    {
        if (packageId != 38)
        {
            return false;
        }

        return (imageIndex >= 9249 && imageIndex <= 9271) || (imageIndex >= 9499 && imageIndex <= 9518);
    }

    private void DrawRuntimeLightSprites(
        MapDocument map,
        int startX,
        int startY,
        int endX,
        int endY,
        float cellW,
        float cellH,
        float zoom,
        Vector2 canvasScreenPos,
        Vector2 canvasSize,
        ImDrawListPtr drawList,
        uint tintColor)
    {
        if (_textureLoader is null)
        {
            return;
        }

        if (((tintColor >> 24) & 0xFFu) == 0u)
        {
            return;
        }

        bool suppressBorder = _state.RenderSuppressBorderCells;
        bool animate = _state.RenderAnimateTextures;
        long tick = 0;
        if (animate)
        {
            float fps = Math.Clamp(_state.TextureAnimationFps, 1.0f, 1000.0f);
            tick = (long)Math.Floor(ImGui.GetTime() * fps);
        }

        Vector2 clipMin = canvasScreenPos;
        Vector2 clipMax = canvasScreenPos + canvasSize;

        foreach (MapChunk chunk in map.EnumerateChunksCoveringRange(startX, startY, endX, endY))
        {
            int chunkEndX = chunk.StartX + chunk.Width - 1;
            int chunkEndY = chunk.StartY + chunk.Height - 1;

            int chunkStartX = Math.Max(startX, chunk.StartX);
            int chunkStartY = Math.Max(startY, chunk.StartY);
            chunkEndX = Math.Min(endX, chunkEndX);
            chunkEndY = Math.Min(endY, chunkEndY);

            for (int y = chunkStartY; y <= chunkEndY; y++)
            {
                for (int x = chunkStartX; x <= chunkEndX; x++)
                {
                    int index = map.GetIndex(x, y);
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (suppressBorder && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    if (!TryResolveExtraObject(cell.OverObject, out int packageId, out int imageIndex))
                    {
                        continue;
                    }

                    if (!IsLightImage(packageId, imageIndex))
                    {
                        continue;
                    }

                    int frame = 0;
                    if (animate
                        && _textureIndex.TryGetCachedFrameCount(packageId, imageIndex, out int frameCount)
                        && frameCount > 1)
                    {
                        int offset = _state.TextureAnimationPerCellOffset ? ComputeCellAnimationOffset(x, y) : 0;
                        frame = (int)((tick + offset) % frameCount);
                    }

                    if (!_textureLoader.TryGetTexture(packageId, imageIndex, frame, out LoadedTextureInfo info))
                    {
                        continue;
                    }

                    float tileW = info.Width * zoom;
                    float tileH = info.Height * zoom;
                    if (tileW <= 0.5f || tileH <= 0.5f)
                    {
                        continue;
                    }

                    float sx = canvasScreenPos.X + _state.Camera.PanX + x * cellW;
                    float sy = canvasScreenPos.Y + _state.Camera.PanY + y * cellH;

                    float cellHeightLiftPx = ComputeCellHeightLiftPx(cell);
                    float baseY = sy + cellH - tileH;
                    float drawX = sx - info.CenterX * zoom + info.OffsetX * zoom;
                    float drawY = baseY - info.CenterY * zoom + info.OffsetY * zoom - cellHeightLiftPx * zoom;

                    if (drawX + tileW < clipMin.X || drawX > clipMax.X ||
                        drawY + tileH < clipMin.Y || drawY > clipMax.Y)
                    {
                        continue;
                    }

                    drawList.AddImage(info.TextureId,
                        new Vector2(drawX, drawY),
                        new Vector2(drawX + tileW, drawY + tileH),
                        Vector2.Zero,
                        Vector2.One,
                        tintColor);
                }
            }
        }
    }

    private static int ComputeCellAnimationOffset(int x, int y)
    {
        unchecked
        {
            uint hx = (uint)(x * 73856093);
            uint hy = (uint)(y * 19349663);
            return (int)((hx ^ hy) & 0x7FFFFFFF);
        }
    }

    private void DrawSelectionOverlay(MapDocument map, ImDrawListPtr drawList, Vector2 canvasScreenPos)
    {
        float zoom = _state.Camera.Zoom;
        float cellW = BaseCellWidth * zoom;
        float cellH = BaseCellHeight * zoom;
        if (cellW <= 0.001f || cellH <= 0.001f)
        {
            return;
        }

        if (_rectFillDrag is not null && ReferenceEquals(_rectFillDrag.Map, map))
        {
            int minX = Math.Min(_rectFillDrag.StartX, _rectFillDrag.CurrentX);
            int maxX = Math.Max(_rectFillDrag.StartX, _rectFillDrag.CurrentX);
            int minY = Math.Min(_rectFillDrag.StartY, _rectFillDrag.CurrentY);
            int maxY = Math.Max(_rectFillDrag.StartY, _rectFillDrag.CurrentY);

            Vector2 min = new(
                canvasScreenPos.X + _state.Camera.PanX + minX * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + minY * cellH);
            Vector2 max = new(
                canvasScreenPos.X + _state.Camera.PanX + (maxX + 1) * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + (maxY + 1) * cellH);

            drawList.AddRectFilled(min, max, PackColor(90, 190, 255, 28));
            drawList.AddRect(min, max, PackColor(90, 190, 255, 220), 0.0f, ImDrawFlags.None, 2.0f);
        }

        if (_selectionDrag is not null && ReferenceEquals(_selectionDrag.Map, map))
        {
            int minX = Math.Min(_selectionDrag.StartX, _selectionDrag.CurrentX);
            int maxX = Math.Max(_selectionDrag.StartX, _selectionDrag.CurrentX);
            int minY = Math.Min(_selectionDrag.StartY, _selectionDrag.CurrentY);
            int maxY = Math.Max(_selectionDrag.StartY, _selectionDrag.CurrentY);

            Vector2 min = new(
                canvasScreenPos.X + _state.Camera.PanX + minX * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + minY * cellH);
            Vector2 max = new(
                canvasScreenPos.X + _state.Camera.PanX + (maxX + 1) * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + (maxY + 1) * cellH);

            drawList.AddRectFilled(min, max, PackColor(120, 255, 180, 22));
            drawList.AddRect(min, max, PackColor(120, 255, 180, 220), 0.0f, ImDrawFlags.None, 2.0f);
        }

        if (_state.HasSelection)
        {
            int minX = Math.Min(_state.SelectionX0, _state.SelectionX1);
            int maxX = Math.Max(_state.SelectionX0, _state.SelectionX1);
            int minY = Math.Min(_state.SelectionY0, _state.SelectionY1);
            int maxY = Math.Max(_state.SelectionY0, _state.SelectionY1);

            Vector2 min = new(
                canvasScreenPos.X + _state.Camera.PanX + minX * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + minY * cellH);
            Vector2 max = new(
                canvasScreenPos.X + _state.Camera.PanX + (maxX + 1) * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + (maxY + 1) * cellH);

            drawList.AddRectFilled(min, max, PackColor(120, 255, 180, 16));
            drawList.AddRect(min, max, PackColor(120, 255, 180, 180), 0.0f, ImDrawFlags.None, 2.0f);
        }

        if (map.IsInBounds(_state.HoverCellX, _state.HoverCellY))
        {
            Vector2 min = new(
                canvasScreenPos.X + _state.Camera.PanX + _state.HoverCellX * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + _state.HoverCellY * cellH);
            Vector2 max = new(min.X + cellW, min.Y + cellH);
            drawList.AddRect(min, max, PackColor(255, 220, 120, 220), 0.0f, ImDrawFlags.None, 2.0f);
        }

        if (map.IsInBounds(_state.SelectedCellX, _state.SelectedCellY))
        {
            Vector2 min = new(
                canvasScreenPos.X + _state.Camera.PanX + _state.SelectedCellX * cellW,
                canvasScreenPos.Y + _state.Camera.PanY + _state.SelectedCellY * cellH);
            Vector2 max = new(min.X + cellW, min.Y + cellH);
            drawList.AddRect(min, max, PackColor(120, 255, 180, 240), 0.0f, ImDrawFlags.None, 3.0f);
        }
    }

    private void ResetCellInspectorDraft(MapDocument map, int cellIndex, in NmpCellData cell)
    {
        _cellInspectorDraftMap = map;
        _cellInspectorDraftIndex = cellIndex;

        _cellInspectorDraft.BackLibrary = cell.BackLibrary;
        _cellInspectorDraft.BackImage = cell.BackImage;
        _cellInspectorDraft.MiddleLibrary = cell.MiddleLibrary;
        _cellInspectorDraft.MiddleImage = cell.MiddleImage;

        uint frontPacked = cell.FrontImage & 0x00FFFFFFu;
        _cellInspectorDraft.FrontLibrary = cell.FrontLibrary;
        _cellInspectorDraft.FrontImage = (int)(frontPacked & 0xFFFF);

        uint underPacked = cell.UnderObject & 0x00FFFFFFu;
        _cellInspectorDraft.UnderLibrary = (int)((underPacked >> 16) & 0xFF);
        _cellInspectorDraft.UnderImage = (int)(underPacked & 0xFFFF);

        uint overPacked = cell.OverObject & 0x00FFFFFFu;
        _cellInspectorDraft.OverLibrary = (int)((overPacked >> 16) & 0xFF);
        _cellInspectorDraft.OverImage = (int)(overPacked & 0xFFFF);

        uint nearPacked = cell.NearGround & 0x00FFFFFFu;
        _cellInspectorDraft.NearLibrary = (int)((nearPacked >> 16) & 0xFF);
        _cellInspectorDraft.NearImage = (int)(nearPacked & 0xFFFF);

        _cellInspectorDraft.Flags = cell.Flags;
        _cellInspectorDraft.ExtendedAttributes = cell.ExtendedAttributes;
        _cellInspectorDraft.ObjectHeight = cell.ObjectHeight;
        _cellInspectorDraft.DoorIndex = cell.DoorIndex;
        _cellInspectorDraft.DoorOffset = cell.DoorOffset;
        _cellInspectorDraft.Light = cell.Light;
        _cellInspectorDraft.Sound = cell.Sound;
    }

    private void ApplyCellInspectorDraft(MapDocument map, int x, int y, int cellIndex, in NmpCellData before)
    {
        if ((uint)cellIndex >= (uint)map.Cells.Length)
        {
            _state.StatusMessage = "应用失败：格子索引越界。";
            return;
        }

        NmpCellData after = before;

        byte flags = (byte)Math.Clamp(_cellInspectorDraft.Flags, 0, 255);
        ushort exAttr = (ushort)Math.Clamp(_cellInspectorDraft.ExtendedAttributes, 0, 65535);

        ushort backImage = (ushort)Math.Clamp(_cellInspectorDraft.BackImage, 0, 65535);
        ushort backLibrary = (ushort)Math.Clamp(_cellInspectorDraft.BackLibrary, 0, 65535);
        if (backImage == 0)
        {
            backLibrary = 0;
            flags = (byte)(flags & ~FlagSmTiles);
        }
        else
        {
            if (backLibrary == 0) backLibrary = DefaultSmTilesLibrary;
            flags = (byte)(flags | FlagSmTiles);
        }

        after.BackImage = backImage;
        after.BackLibrary = backLibrary;

        ushort middleImage = (ushort)Math.Clamp(_cellInspectorDraft.MiddleImage, 0, 65535);
        ushort middleLibrary = (ushort)Math.Clamp(_cellInspectorDraft.MiddleLibrary, 0, 65535);
        if (middleImage == 0)
        {
            middleLibrary = 0;
            flags = (byte)(flags & ~FlagTiles);
        }
        else
        {
            if (middleLibrary == 0) middleLibrary = DefaultTilesLibrary;
            flags = (byte)(flags | FlagTiles);
        }

        after.MiddleImage = middleImage;
        after.MiddleLibrary = middleLibrary;

        ushort frontImage = (ushort)Math.Clamp(_cellInspectorDraft.FrontImage, 0, 65535);
        byte frontLibrary = (byte)Math.Clamp(_cellInspectorDraft.FrontLibrary, 0, 255);
        uint frontPacked = frontImage == 0 ? 0u : (((uint)frontLibrary << 16) | frontImage);
        if (frontPacked == 0)
        {
            frontLibrary = 0;
            flags = (byte)(flags & ~FlagObject);
        }
        else
        {
            if (frontLibrary == 0) frontLibrary = DefaultObjectLibrary;
            frontPacked = (((uint)frontLibrary << 16) | frontImage);
            flags = (byte)(flags | FlagObject);
        }

        after.FrontLibrary = frontLibrary;
        after.FrontImage = (after.FrontImage & 0xFF000000u) | frontPacked;

        ushort underImage = (ushort)Math.Clamp(_cellInspectorDraft.UnderImage, 0, 65535);
        byte underLibrary = (byte)Math.Clamp(_cellInspectorDraft.UnderLibrary, 0, 255);
        uint underPacked2 = underImage == 0 ? 0u : (((uint)underLibrary << 16) | underImage);
        if (underPacked2 == 0)
        {
            flags = (byte)(flags & ~NmpFlagUnderObject);
        }
        else
        {
            if (underLibrary == 0) underLibrary = DefaultObjectLibrary;
            underPacked2 = (((uint)underLibrary << 16) | underImage);
            flags = (byte)(flags | NmpFlagUnderObject);
        }

        after.UnderObject = (after.UnderObject & 0xFF000000u) | underPacked2;

        ushort overImage = (ushort)Math.Clamp(_cellInspectorDraft.OverImage, 0, 65535);
        byte overLibrary = (byte)Math.Clamp(_cellInspectorDraft.OverLibrary, 0, 255);
        uint overPacked2 = overImage == 0 ? 0u : (((uint)overLibrary << 16) | overImage);
        if (overPacked2 == 0)
        {
            exAttr = (ushort)(exAttr & ~NmpExAttrOverObject);
            after.ColorOverObj = 0;
        }
        else
        {
            if (overLibrary == 0) overLibrary = DefaultObjectLibrary;
            overPacked2 = (((uint)overLibrary << 16) | overImage);
            exAttr = (ushort)(exAttr | NmpExAttrOverObject);
        }

        after.OverObject = (after.OverObject & 0xFF000000u) | overPacked2;

        ushort nearImage = (ushort)Math.Clamp(_cellInspectorDraft.NearImage, 0, 65535);
        byte nearLibrary = (byte)Math.Clamp(_cellInspectorDraft.NearLibrary, 0, 255);
        uint nearPacked2 = nearImage == 0 ? 0u : (((uint)nearLibrary << 16) | nearImage);
        if (nearPacked2 == 0)
        {
            flags = (byte)(flags & ~NmpFlagNearGround);
        }
        else
        {
            if (nearLibrary == 0) nearLibrary = DefaultObjectLibrary;
            nearPacked2 = (((uint)nearLibrary << 16) | nearImage);
            flags = (byte)(flags | NmpFlagNearGround);
        }

        after.NearGround = (after.NearGround & 0xFF000000u) | nearPacked2;

        after.ObjectHeight = (ushort)Math.Clamp(_cellInspectorDraft.ObjectHeight, 0, 65535);
        after.DoorIndex = (byte)Math.Clamp(_cellInspectorDraft.DoorIndex, 0, 255);
        after.DoorOffset = (byte)Math.Clamp(_cellInspectorDraft.DoorOffset, 0, 255);
        after.Light = (byte)Math.Clamp(_cellInspectorDraft.Light, 0, 255);
        after.Sound = (byte)Math.Clamp(_cellInspectorDraft.Sound, 0, 255);
        after.Flags = flags;
        after.ExtendedAttributes = exAttr;

        if (after.Equals(before))
        {
            _state.StatusMessage = "未发生变化。";
            return;
        }

        map.Cells[cellIndex] = after;
        _state.History.Push(new MultiCellEditAction($"编辑属性 ({x},{y})", map,
            indices: new[] { cellIndex },
            before: new[] { before },
            after: new[] { after }));

        _objectListDirty = true;
        _sceneTreeDirty = true;
        _state.StatusMessage = "已应用属性修改。";
        ResetCellInspectorDraft(map, cellIndex, in after);
    }

    private void BuildInspectorWindow()
    {
        bool open = _state.ShowCellInspectorPanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("格子检查器##cell_inspector", ref open))
        {
            ImGui.End();
            _state.ShowCellInspectorPanel = open;
            return;
        }

        _state.ShowCellInspectorPanel = open;

        if (_state.Map is null)
        {
            ImGui.TextDisabled("未加载地图。");
            ImGui.End();
            return;
        }

        MapDocument map = _state.Map;

        int x = _state.SelectedCellX;
        int y = _state.SelectedCellY;
        if (!map.IsInBounds(x, y))
        {
            ImGui.TextDisabled("未选择格子（左键点击地图选择）。");
            ImGui.End();
            return;
        }

        if (!map.TryGetCell(x, y, out NmpCellData cell))
        {
            ImGui.TextDisabled("读取格子失败。");
            ImGui.End();
            return;
        }

        int cellIndex = map.GetIndex(x, y);
        if (!ReferenceEquals(_cellInspectorDraftMap, map) || _cellInspectorDraftIndex != cellIndex)
        {
            ResetCellInspectorDraft(map, cellIndex, in cell);
        }

        ImGui.TextUnformatted($"格子：({x}, {y})  索引={cellIndex}");
        ImGui.Separator();

        if (ImGui.Button("应用修改"))
        {
            ApplyCellInspectorDraft(map, x, y, cellIndex, in cell);
        }

        ImGui.SameLine();
        if (ImGui.Button("重置"))
        {
            ResetCellInspectorDraft(map, cellIndex, in cell);
        }

        ImGui.TextDisabled("提示：将 img 设为 0 视为清空；lib=0 时会自动用默认库。");
        ImGui.Separator();

        void DrawLibImgRow(string title, string id, ObjectListEntryKind kind, ref int lib, int libMax, ref int img, int imgMax)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(title);
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##{id}_lib", ref lib))
            {
                lib = Math.Clamp(lib, 0, libMax);
            }
            ImGui.TableSetColumnIndex(2);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##{id}_img", ref img))
            {
                img = Math.Clamp(img, 0, imgMax);
            }

            ImGui.TableSetColumnIndex(3);
            string bridgeTooltip = string.Empty;
            bool canOpen = false;
            if (TryResolveTextureRef(kind, lib, img, out int pkg, out int idx))
            {
                if (!_textureIndex.IsReady)
                {
                    bridgeTooltip = "贴图库未就绪：请先扫描贴图库（SGL/WPF）。";
                }
                else
                {
                    canOpen = _textureIndex.TryGetImageBridgeTarget(pkg, idx, out _, out _, out string bridgeError);
                    if (!canOpen)
                    {
                        bridgeTooltip = string.IsNullOrWhiteSpace(bridgeError) ? "无法解析贴图来源。" : bridgeError;
                    }
                }

                if (!canOpen)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.SmallButton($"在CE打开##{id}_open_ce"))
                {
                    TryOpenTextureRefInContentEditor(title, pkg, idx);
                }

                if (!canOpen)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(bridgeTooltip))
                    {
                        ImGui.SetTooltip(bridgeTooltip);
                    }
                }
            }
            else
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton($"在CE打开##{id}_open_ce_empty");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("空引用（img=0）。");
                }
            }
        }

        ImGui.TextUnformatted("图层");
        if (ImGui.BeginTable("##cell_layers", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("层");
            ImGui.TableSetupColumn("库(lib)");
            ImGui.TableSetupColumn("图(img)");
            ImGui.TableSetupColumn("桥接");
            ImGui.TableHeadersRow();

            DrawLibImgRow("后景 (Back)", "back", ObjectListEntryKind.BackTile, ref _cellInspectorDraft.BackLibrary, 65535, ref _cellInspectorDraft.BackImage, 65535);
            DrawLibImgRow("中景 (Middle)", "middle", ObjectListEntryKind.MiddleTile, ref _cellInspectorDraft.MiddleLibrary, 65535, ref _cellInspectorDraft.MiddleImage, 65535);
            DrawLibImgRow("前景 (Front)", "front", ObjectListEntryKind.FrontObject, ref _cellInspectorDraft.FrontLibrary, 255, ref _cellInspectorDraft.FrontImage, 65535);

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("对象层");
        if (ImGui.BeginTable("##cell_objects", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("层");
            ImGui.TableSetupColumn("库(lib)");
            ImGui.TableSetupColumn("图(img)");
            ImGui.TableSetupColumn("桥接");
            ImGui.TableHeadersRow();

            DrawLibImgRow("下层物件 (UnderObject)", "under", ObjectListEntryKind.UnderObject, ref _cellInspectorDraft.UnderLibrary, 255, ref _cellInspectorDraft.UnderImage, 65535);
            DrawLibImgRow("上层物件 (OverObject)", "over", ObjectListEntryKind.OverObject, ref _cellInspectorDraft.OverLibrary, 255, ref _cellInspectorDraft.OverImage, 65535);
            DrawLibImgRow("近景地面 (NearGround)", "near", ObjectListEntryKind.NearGround, ref _cellInspectorDraft.NearLibrary, 255, ref _cellInspectorDraft.NearImage, 65535);

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("属性");

        ImGui.TextUnformatted($"标志(Flags)：0x{Math.Clamp(_cellInspectorDraft.Flags, 0, 255):X2}");
        if (ImGui.InputInt("标志(Flags)##flags", ref _cellInspectorDraft.Flags))
        {
            _cellInspectorDraft.Flags = Math.Clamp(_cellInspectorDraft.Flags, 0, 255);
        }

        ImGui.TextUnformatted($"扩展属性：0x{Math.Clamp(_cellInspectorDraft.ExtendedAttributes, 0, 65535):X4}");
        if (ImGui.InputInt("扩展属性(ExAttr)##exattr", ref _cellInspectorDraft.ExtendedAttributes))
        {
            _cellInspectorDraft.ExtendedAttributes = Math.Clamp(_cellInspectorDraft.ExtendedAttributes, 0, 65535);
        }

        if (ImGui.InputInt("物件高度(ObjectHeight)##objh", ref _cellInspectorDraft.ObjectHeight))
        {
            _cellInspectorDraft.ObjectHeight = Math.Clamp(_cellInspectorDraft.ObjectHeight, 0, 65535);
        }

        if (ImGui.InputInt("音效(Sound)##sound", ref _cellInspectorDraft.Sound))
        {
            _cellInspectorDraft.Sound = Math.Clamp(_cellInspectorDraft.Sound, 0, 255);
        }

        if (ImGui.InputInt("门索引(DoorIndex)##door_i", ref _cellInspectorDraft.DoorIndex))
        {
            _cellInspectorDraft.DoorIndex = Math.Clamp(_cellInspectorDraft.DoorIndex, 0, 255);
        }

        if (ImGui.InputInt("门偏移(DoorOffset)##door_o", ref _cellInspectorDraft.DoorOffset))
        {
            _cellInspectorDraft.DoorOffset = Math.Clamp(_cellInspectorDraft.DoorOffset, 0, 255);
        }

        if (ImGui.InputInt("光照(Light)##light", ref _cellInspectorDraft.Light))
        {
            _cellInspectorDraft.Light = Math.Clamp(_cellInspectorDraft.Light, 0, 255);
        }

        ImGui.End();
    }

    private static bool IsModifierKey(ImGuiKey key)
    {
        if (key is ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl
            or ImGuiKey.LeftShift or ImGuiKey.RightShift
            or ImGuiKey.LeftAlt or ImGuiKey.RightAlt
            or ImGuiKey.LeftSuper or ImGuiKey.RightSuper)
        {
            return true;
        }

        // Some ImGui builds expose "ModCtrl/ModShift/..." virtual keys; keep them out of rebind list too.
        string name = key.ToString();
        return name.StartsWith("Mod", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Shift", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Alt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Super", StringComparison.OrdinalIgnoreCase);
    }

    private static ImGuiKey DetectKeyPress()
    {
        foreach (ImGuiKey key in Enum.GetValues(typeof(ImGuiKey)))
        {
            if (key == ImGuiKey.None)
            {
                continue;
            }

            if (IsModifierKey(key))
            {
                continue;
            }

            if (ImGui.IsKeyPressed(key, repeat: false))
            {
                return key;
            }
        }

        return ImGuiKey.None;
    }

    private void BuildKeyBindingsSettingsContent()
    {
        ImGui.TextUnformatted("Key Bindings");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("点击 Key 按钮可重绑；监听中按 Esc 取消。修饰键勾选表示必须同时按下（且未勾选的修饰键不能被按下，以避免 Ctrl+Z 触发普通 Z）。");
        ImGui.Spacing();

        MapEditorKeyBindings kb = _state.KeyBindings;

        static string LabelForRow(int i) => i switch
        {
            0 => "Blocked Editor",
            1 => "Selection",
            2 => "Erase",
            3 => "Stamp",
            4 => "Tile Paint",
            5 => "Cancel / Deselect",
            6 => "Delete Selection",
            7 => "Undo",
            8 => "Redo",
            9 => "Save",
            10 => "Zoom In",
            11 => "Zoom Out",
            12 => "Reset View",
            _ => "?",
        };

        static KeyBinding GetBinding(MapEditorKeyBindings bindings, int i) => i switch
        {
            0 => bindings.ToolBlockedEditor,
            1 => bindings.ToolSelection,
            2 => bindings.ToolErase,
            3 => bindings.ToolStamp,
            4 => bindings.ToolTilePaint,
            5 => bindings.ToolCancel,
            6 => bindings.DeleteSelection,
            7 => bindings.Undo,
            8 => bindings.Redo,
            9 => bindings.Save,
            10 => bindings.ZoomIn,
            11 => bindings.ZoomOut,
            12 => bindings.ResetView,
            _ => default,
        };

        static void SetBinding(MapEditorKeyBindings bindings, int i, KeyBinding binding)
        {
            switch (i)
            {
                case 0:
                    bindings.ToolBlockedEditor = binding;
                    break;
                case 1:
                    bindings.ToolSelection = binding;
                    break;
                case 2:
                    bindings.ToolErase = binding;
                    break;
                case 3:
                    bindings.ToolStamp = binding;
                    break;
                case 4:
                    bindings.ToolTilePaint = binding;
                    break;
                case 5:
                    bindings.ToolCancel = binding;
                    break;
                case 6:
                    bindings.DeleteSelection = binding;
                    break;
                case 7:
                    bindings.Undo = binding;
                    break;
                case 8:
                    bindings.Redo = binding;
                    break;
                case 9:
                    bindings.Save = binding;
                    break;
                case 10:
                    bindings.ZoomIn = binding;
                    break;
                case 11:
                    bindings.ZoomOut = binding;
                    break;
                case 12:
                    bindings.ResetView = binding;
                    break;
                default:
                    break;
            }
        }

        const int rowCount = 13;
        if (ImGui.BeginTable("KeyBindingsTable", 5, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthFixed, 140.0f);
            ImGui.TableSetupColumn("Ctrl", ImGuiTableColumnFlags.WidthFixed, 40.0f);
            ImGui.TableSetupColumn("Shift", ImGuiTableColumnFlags.WidthFixed, 45.0f);
            ImGui.TableSetupColumn("Alt", ImGuiTableColumnFlags.WidthFixed, 40.0f);
            ImGui.TableHeadersRow();

            for (int i = 0; i < rowCount; i++)
            {
                ImGui.TableNextRow();
                ImGui.PushID(i);

                KeyBinding b = GetBinding(kb, i);
                bool changed = false;

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(LabelForRow(i));

                ImGui.TableSetColumnIndex(1);
                bool isListening = _state.KeyBindListeningIndex == i;
                if (isListening)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.3f, 0.1f, 1.0f));
                    ImGui.Button("Press a key...", new Vector2(-1.0f, 0.0f));
                    ImGui.PopStyleColor();

                    ImGuiKey pressed = DetectKeyPress();
                    if (pressed == ImGuiKey.Escape)
                    {
                        _state.KeyBindListeningIndex = -1;
                    }
                    else if (pressed != ImGuiKey.None)
                    {
                        b.Key = pressed;
                        changed = true;
                        _state.KeyBindListeningIndex = -1;
                    }
                }
                else
                {
                    string keyName = b.Key == ImGuiKey.None ? "(None)" : b.Key.ToString();
                    if (ImGui.Button(keyName, new Vector2(-1.0f, 0.0f)))
                    {
                        _state.KeyBindListeningIndex = i;
                    }
                }

                ImGui.TableSetColumnIndex(2);
                bool ctrl = (b.Mods & KeyModFlags.Ctrl) != 0;
                if (ImGui.Checkbox("##Ctrl", ref ctrl))
                {
                    b.Mods = ctrl ? (b.Mods | KeyModFlags.Ctrl) : (b.Mods & ~KeyModFlags.Ctrl);
                    changed = true;
                }

                ImGui.TableSetColumnIndex(3);
                bool shift = (b.Mods & KeyModFlags.Shift) != 0;
                if (ImGui.Checkbox("##Shift", ref shift))
                {
                    b.Mods = shift ? (b.Mods | KeyModFlags.Shift) : (b.Mods & ~KeyModFlags.Shift);
                    changed = true;
                }

                ImGui.TableSetColumnIndex(4);
                bool alt = (b.Mods & KeyModFlags.Alt) != 0;
                if (ImGui.Checkbox("##Alt", ref alt))
                {
                    b.Mods = alt ? (b.Mods | KeyModFlags.Alt) : (b.Mods & ~KeyModFlags.Alt);
                    changed = true;
                }

                if (changed)
                {
                    SetBinding(kb, i, b);
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            kb.ResetToDefaults();
            _state.KeyBindListeningIndex = -1;
            _state.StatusMessage = "已重置快捷键为默认值。";
        }
    }

    private static string GetLuminanceModeName(LuminanceMode mode)
        => mode switch
        {
            LuminanceMode.Rec709 => "Rec709",
            LuminanceMode.Hsl => "HSL",
            LuminanceMode.Hsv => "HSV",
            LuminanceMode.Average => "Average",
            LuminanceMode.RedChannel => "Red Channel",
            LuminanceMode.GreenChannel => "Green Channel",
            LuminanceMode.BlueChannel => "Blue Channel",
            _ => "Rec709",
        };

    private static string GetAlphaBlendModeName(AlphaBlendMode mode)
        => mode switch
        {
            AlphaBlendMode.Replace => "Replace",
            AlphaBlendMode.Multiply => "Multiply",
            AlphaBlendMode.Screen => "Screen",
            AlphaBlendMode.Overlay => "Overlay",
            _ => "Replace",
        };

    private static LuminanceSettings WithLuminanceSettings(
        LuminanceSettings current,
        LuminanceMode? mode = null,
        AlphaBlendMode? blendMode = null,
        float? gamma = null,
        float? contrast = null,
        byte? threshold = null,
        bool? inverted = null)
    {
        return new LuminanceSettings
        {
            Mode = mode ?? current.Mode,
            BlendMode = blendMode ?? current.BlendMode,
            Gamma = gamma ?? current.Gamma,
            Contrast = contrast ?? current.Contrast,
            Threshold = threshold ?? current.Threshold,
            Inverted = inverted ?? current.Inverted,
        };
    }

    private void BuildLuminanceSettingsContent()
    {
        ImGui.TextUnformatted("Luminance to Alpha");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("控制 packages 46/47（WoooL_objects18/19）的渲染方式：从 RGB 计算亮度并写入 alpha，使亮区不透明、暗区透明。调整后点击 Apply 使其在视口与小地图中生效。");
        ImGui.Spacing();

        bool skip = _state.SkipLuminanceToAlpha;
        if (ImGui.Checkbox("Skip Luminance to Alpha", ref skip))
        {
            _state.SkipLuminanceToAlpha = skip;
            ApplyTextureIndexSettingsFromState(invalidateTextures: true);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(bypass L-to-A processing)");
        ImGui.Spacing();

        if (_state.SkipLuminanceToAlpha)
        {
            ImGui.BeginDisabled();
        }

        LuminanceSettings s = _state.PendingLuminanceSettings;

        ImGui.TextUnformatted("Luminance Mode");
        ImGui.SetNextItemWidth(220.0f);
        if (ImGui.BeginCombo("##LumMode", GetLuminanceModeName(s.Mode)))
        {
            LuminanceMode[] modes =
            {
                LuminanceMode.Rec709,
                LuminanceMode.Hsl,
                LuminanceMode.Hsv,
                LuminanceMode.Average,
                LuminanceMode.RedChannel,
                LuminanceMode.GreenChannel,
                LuminanceMode.BlueChannel,
            };

            for (int i = 0; i < modes.Length; i++)
            {
                LuminanceMode m = modes[i];
                bool selected = s.Mode == m;
                if (ImGui.Selectable(GetLuminanceModeName(m), selected))
                {
                    s = WithLuminanceSettings(s, mode: m);
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        ImGui.TextUnformatted("Alpha Blend Mode");
        ImGui.SetNextItemWidth(220.0f);
        if (ImGui.BeginCombo("##BlendMode", GetAlphaBlendModeName(s.BlendMode)))
        {
            AlphaBlendMode[] modes =
            {
                AlphaBlendMode.Replace,
                AlphaBlendMode.Multiply,
                AlphaBlendMode.Screen,
                AlphaBlendMode.Overlay,
            };

            for (int i = 0; i < modes.Length; i++)
            {
                AlphaBlendMode m = modes[i];
                bool selected = s.BlendMode == m;
                if (ImGui.Selectable(GetAlphaBlendModeName(m), selected))
                {
                    s = WithLuminanceSettings(s, blendMode: m);
                }
                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Gamma");
        ImGui.SameLine();
        ImGui.TextDisabled("(< 1 brightens, > 1 darkens)");
        float gamma = float.IsFinite(s.Gamma) ? s.Gamma : 1.0f;
        gamma = Math.Clamp(gamma, 0.1f, 4.0f);
        ImGui.SetNextItemWidth(220.0f);
        if (ImGui.SliderFloat("##Gamma", ref gamma, 0.1f, 4.0f, "%.2f"))
        {
            s = WithLuminanceSettings(s, gamma: Math.Clamp(gamma, 0.1f, 4.0f));
        }

        ImGui.Spacing();

        ImGui.TextUnformatted("Contrast");
        float contrast = float.IsFinite(s.Contrast) ? s.Contrast : 0.0f;
        contrast = Math.Clamp(contrast, -1.0f, 1.0f);
        ImGui.SetNextItemWidth(220.0f);
        if (ImGui.SliderFloat("##Contrast", ref contrast, -1.0f, 1.0f, "%.2f"))
        {
            s = WithLuminanceSettings(s, contrast: Math.Clamp(contrast, -1.0f, 1.0f));
        }

        ImGui.Spacing();

        ImGui.TextUnformatted("Threshold");
        ImGui.SameLine();
        ImGui.TextDisabled("(pixels below this luminance become transparent)");
        int threshold = s.Threshold;
        ImGui.SetNextItemWidth(220.0f);
        if (ImGui.SliderInt("##Threshold", ref threshold, 0, 255))
        {
            threshold = Math.Clamp(threshold, 0, 255);
            s = WithLuminanceSettings(s, threshold: (byte)threshold);
        }

        ImGui.Spacing();

        bool inverted = s.Inverted;
        if (ImGui.Checkbox("Invert", ref inverted))
        {
            s = WithLuminanceSettings(s, inverted: inverted);
        }

        _state.PendingLuminanceSettings = s;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults"))
        {
            _state.PendingLuminanceSettings = new LuminanceSettings();
        }
        ImGui.SameLine();

        bool dirty = !_state.PendingLuminanceSettings.Equals(_state.LuminanceSettings);
        if (!dirty)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Apply"))
        {
            _state.LuminanceSettings = _state.PendingLuminanceSettings;
            ApplyTextureIndexSettingsFromState(invalidateTextures: true);
        }

        if (!dirty)
        {
            ImGui.EndDisabled();
        }

        if (dirty)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(unapplied changes)");
        }

        if (_state.SkipLuminanceToAlpha)
        {
            ImGui.EndDisabled();
        }
    }

    private void BuildSettingsWindow()
    {
        bool open = _state.ShowSettingsWindow;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("Preferences##settings", ref open))
        {
            ImGui.End();
            _state.ShowSettingsWindow = open;
            return;
        }

        _state.ShowSettingsWindow = open;

        const float leftPanelWidth = 160.0f;

        ImGui.BeginChild("##settings_sections", new Vector2(leftPanelWidth, 0.0f), ImGuiChildFlags.Borders);

        void SectionSelectable(string label, MapEditorSettingsSection section)
        {
            if (ImGui.Selectable(label, _state.CurrentSettingsSection == section))
            {
                _state.CurrentSettingsSection = section;
            }
        }

        SectionSelectable("Defaults", MapEditorSettingsSection.Defaults);
        SectionSelectable("Application", MapEditorSettingsSection.Application);
        SectionSelectable("Map Paths", MapEditorSettingsSection.MapPaths);
        SectionSelectable("Data Paths", MapEditorSettingsSection.DataPaths);
        SectionSelectable("Assets", MapEditorSettingsSection.Assets);
        SectionSelectable("Client Parity", MapEditorSettingsSection.ClientParity);
        SectionSelectable("Key Bindings", MapEditorSettingsSection.KeyBindings);
        SectionSelectable("Luminance", MapEditorSettingsSection.Luminance);

        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##settings_content", new Vector2(0.0f, 0.0f), ImGuiChildFlags.Borders);

        switch (_state.CurrentSettingsSection)
        {
            case MapEditorSettingsSection.Defaults:
            {
                ImGui.TextUnformatted("Defaults");
                ImGui.Separator();
                ImGui.TextDisabled("说明：这里的设置主要影响默认视图与新打开地图的初始体验。");

                bool showGrid = _state.ShowGrid;
                if (ImGui.Checkbox("Show Grid", ref showGrid))
                {
                    _state.ShowGrid = showGrid;
                }

                bool showTileFill = _state.ShowTileFill;
                if (ImGui.Checkbox("Show Tile Fill", ref showTileFill))
                {
                    _state.ShowTileFill = showTileFill;
                }

                bool showMinimap = _state.ShowMinimapOverlay;
                if (ImGui.Checkbox("Show Minimap", ref showMinimap))
                {
                    _state.ShowMinimapOverlay = showMinimap;
                    if (showMinimap)
                    {
                        InvalidateRuntimeMinimapActiveDocument();
                    }
                }

                float minimapOpacity = _state.MinimapOpacity;
                if (!float.IsFinite(minimapOpacity))
                {
                    minimapOpacity = 0.85f;
                }
                minimapOpacity = Math.Clamp(minimapOpacity, 0.0f, 1.0f);
                if (ImGui.SliderFloat("Minimap Opacity", ref minimapOpacity, 0.0f, 1.0f, "%.2f"))
                {
                    _state.MinimapOpacity = Math.Clamp(minimapOpacity, 0.0f, 1.0f);
                }

                ImGui.TextDisabled("提示：右上角小地图支持左键拖动，用于快速定位视图。");

                break;
            }

            case MapEditorSettingsSection.Application:
            {
                ImGui.TextUnformatted("Application");
                ImGui.Separator();

                ImGui.TextUnformatted("Panels");
                bool showBrowser = _state.ShowFileBrowserPanel;
                if (ImGui.Checkbox("File Browser", ref showBrowser))
                {
                    _state.ShowFileBrowserPanel = showBrowser;
                }

                bool showPrefabs = _state.ShowPrefabBrowserPanel;
                if (ImGui.Checkbox("Prefabs", ref showPrefabs))
                {
                    _state.ShowPrefabBrowserPanel = showPrefabs;
                }

                bool showObjects = _state.ShowObjectListPanel;
                if (ImGui.Checkbox("Object List", ref showObjects))
                {
                    _state.ShowObjectListPanel = showObjects;
                }

                bool showSceneTree = _state.ShowSceneTreePanel;
                if (ImGui.Checkbox("Scene Tree", ref showSceneTree))
                {
                    _state.ShowSceneTreePanel = showSceneTree;
                }

                bool showInfo = _state.ShowInformationPanel;
                if (ImGui.Checkbox("Information", ref showInfo))
                {
                    _state.ShowInformationPanel = showInfo;
                }

                bool showInspector = _state.ShowCellInspectorPanel;
                if (ImGui.Checkbox("Cell Inspector", ref showInspector))
                {
                    _state.ShowCellInspectorPanel = showInspector;
                }

                bool showConsole = _state.ShowConsolePanel;
                if (ImGui.Checkbox("Console", ref showConsole))
                {
                    _state.ShowConsolePanel = showConsole;
                }

                ImGui.Separator();
                ImGui.TextUnformatted("Zoom");

                float zoomMin = _state.Camera.MinZoom;
                float zoomMax = _state.Camera.MaxZoom;
                float zoomStep = _state.ZoomStep;

                if (ImGui.DragFloat("Min Zoom", ref zoomMin, 0.01f, 0.1f, 4.0f, "%.2f"))
                {
                    zoomMin = Math.Clamp(zoomMin, 0.1f, 4.0f);
                    if (zoomMin > zoomMax) zoomMin = zoomMax;
                    _state.Camera.MinZoom = zoomMin;
                    _state.Camera.ClampZoom();
                }

                if (ImGui.DragFloat("Max Zoom", ref zoomMax, 0.1f, 1.0f, 32.0f, "%.1f"))
                {
                    zoomMax = Math.Clamp(zoomMax, 1.0f, 32.0f);
                    if (zoomMax < zoomMin) zoomMax = zoomMin;
                    _state.Camera.MaxZoom = zoomMax;
                    _state.Camera.ClampZoom();
                }

                if (ImGui.DragFloat("Zoom Step", ref zoomStep, 0.01f, 0.01f, 1.0f, "%.2f"))
                {
                    _state.ZoomStep = Math.Clamp(zoomStep, 0.01f, 1.0f);
                }

                ImGui.Separator();
                ImGui.TextUnformatted("Restore");
                bool restoreState = _state.RestoreState;
                if (ImGui.Checkbox("启动时恢复上次打开的文档", ref restoreState))
                {
                    _state.RestoreState = restoreState;
                }
                ImGui.TextDisabled("说明：会保存 open_map 列表与 active_map_index 到偏好文件（退出时写入）。");

                bool unloadInactive = _state.UnloadInactiveTabs;
                if (ImGui.Checkbox("卸载未激活标签页以节省内存（unload_inactive_tabs）", ref unloadInactive))
                {
                    _state.UnloadInactiveTabs = unloadInactive;
                }
                ImGui.TextDisabled("说明：仅会卸载“已保存且无修改”的标签页；切回时会自动重新加载。");

                break;
            }

            case MapEditorSettingsSection.MapPaths:
            {
                ImGui.TextUnformatted("Map Paths");
                ImGui.Separator();
                ImGui.TextDisabled("说明：用于配置“文件浏览器”的地图根目录列表（支持命名与排序）。");

                DrawMapPathEntriesSection();

                bool recursive = _state.MapBrowserRecursive;
                if (ImGui.Checkbox("递归扫描 Map Path", ref recursive))
                {
                    _state.MapBrowserRecursive = recursive;
                }

                bool includePrefabs = _state.MapBrowserIncludePrefabs;
                if (ImGui.Checkbox("包含 Prefabs（.nmpo/.nmpoN）", ref includePrefabs))
                {
                    _state.MapBrowserIncludePrefabs = includePrefabs;
                }

                break;
            }

            case MapEditorSettingsSection.DataPaths:
            {
                ImGui.TextUnformatted("Data Paths");
                ImGui.Separator();
                ImGui.TextDisabled("说明：用于配置贴图库(Data Path)目录列表（用于扫描 SGL/WPF）。");

                DrawDataPathEntriesSection();

                bool recursive = _state.TextureScanRecursive;
                if (ImGui.Checkbox("递归扫描贴图库", ref recursive))
                {
                    _state.TextureScanRecursive = recursive;
                }

                break;
            }

            case MapEditorSettingsSection.Assets:
            {
                ImGui.TextUnformatted("Assets");
                ImGui.Separator();
                ImGui.TextDisabled("说明：这些设置会影响运行时贴图解析（WPF/TEX vs SGL），以及海岸(coast)合成时遮罩来源（TEX vs .msk）。");
                ImGui.TextDisabled("修改后会自动清空贴图缓存并重新解码。");

                int modeIndex = _state.TextureSourceMode switch
                {
                    TextureSourceMode.WpfOnly => 1,
                    TextureSourceMode.SglOnly => 2,
                    _ => 0,
                };

                if (ImGui.Combo("Texture Source Mode", ref modeIndex, "WPF + SGL fallback (默认)\0WPF only\0SGL only\0"))
                {
                    TextureSourceMode newMode = modeIndex switch
                    {
                        1 => TextureSourceMode.WpfOnly,
                        2 => TextureSourceMode.SglOnly,
                        _ => TextureSourceMode.WpfSglFallback,
                    };

                    if (_state.TextureSourceMode != newMode)
                    {
                        _state.TextureSourceMode = newMode;
                        ApplyTextureIndexSettingsFromState(invalidateTextures: true);
                        _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"Texture Source Mode={newMode}");
                        _state.StatusMessage = $"Texture Source Mode 已切换：{newMode}";
                    }
                }

                int coastIndex = _state.CoastMaskPreferTex ? 0 : 1;
                if (ImGui.Combo("Coast Mask Source", ref coastIndex, "TEX (优先)\0MSK (优先)\0"))
                {
                    bool preferTex = coastIndex == 0;
                    if (_state.CoastMaskPreferTex != preferTex)
                    {
                        _state.CoastMaskPreferTex = preferTex;
                        ApplyTextureIndexSettingsFromState(invalidateTextures: true);
                        _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"Coast Mask Source={(preferTex ? "tex" : "msk")}");
                        _state.StatusMessage = $"Coast Mask Source 已切换：{(preferTex ? "tex" : "msk")}";
                    }
                }

                ImGui.TextDisabled("提示：coast_mask_source=msk 时会优先读取 dataRoot/mask/<id/1000>/<id>.msk；否则优先读取同 package 内的 maskImageIndex。");
                break;
            }

            case MapEditorSettingsSection.ClientParity:
            {
                ImGui.TextUnformatted("Client Parity");
                ImGui.Separator();
                ImGui.TextDisabled("提示：这里的设置会影响运行时贴图渲染，以及小地图导出中的 client parity 选项。");

                bool canRenderTextures = _textureIndex.IsReady && _textureLoader is not null;
                if (!canRenderTextures)
                {
                    ImGui.TextDisabled("（贴图库未就绪：需要先扫描贴图库（SGL/WPF））");
                }

                bool renderTextures = _state.RenderUseTextures;
                if (!canRenderTextures)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Checkbox("使用真实贴图渲染", ref renderTextures))
                {
                    _state.RenderUseTextures = renderTextures;
                }

                if (!canRenderTextures)
                {
                    ImGui.EndDisabled();
                }

                bool animate = _state.RenderAnimateTextures;
                if (ImGui.Checkbox("贴图动画（实验性）", ref animate))
                {
                    _state.RenderAnimateTextures = animate;
                }

                float fps = _state.TextureAnimationFps;
                if (ImGui.SliderFloat("动画 FPS", ref fps, 1.0f, 30.0f, "%.1f"))
                {
                    _state.TextureAnimationFps = Math.Clamp(fps, 1.0f, 1000.0f);
                }

                bool perCell = _state.TextureAnimationPerCellOffset;
                if (ImGui.Checkbox("每格错峰（Per-Cell Offset）", ref perCell))
                {
                    _state.TextureAnimationPerCellOffset = perCell;
                }

                ImGui.Separator();

                bool tint = _state.RenderApplyCellTints;
                if (ImGui.Checkbox("应用格子色调（ColorAdj*）", ref tint))
                {
                    _state.RenderApplyCellTints = tint;
                }

                if (_state.RenderApplyCellTints)
                {
                    float strength = _state.RenderTintStrength;
                    if (ImGui.SliderFloat("色调强度", ref strength, 0.0f, 1.0f, "%.2f"))
                    {
                        _state.RenderTintStrength = Math.Clamp(strength, 0.0f, 1.0f);
                    }
                }

                bool suppressBorder = _state.RenderSuppressBorderCells;
                if (ImGui.Checkbox("抑制 BORDER 格（flags 0x04）", ref suppressBorder))
                {
                    _state.RenderSuppressBorderCells = suppressBorder;
                }

                bool applyHeightFlag = _state.RenderApplyCellHeightFlag;
                if (ImGui.Checkbox("应用 NEARGROUND(0x40) 高度偏移", ref applyHeightFlag))
                {
                    _state.RenderApplyCellHeightFlag = applyHeightFlag;
                }

                if (_state.RenderApplyCellHeightFlag)
                {
                    float lift = _state.RenderCellHeightFlagOffset;
                    if (ImGui.SliderFloat("高度标记提升（未缩放 px）", ref lift, 0.0f, 64.0f, "%.2f"))
                    {
                        _state.RenderCellHeightFlagOffset = Math.Clamp(lift, 0.0f, 64.0f);
                    }
                }

                bool applyObjHeight = _state.RenderApplyObjectHeight;
                if (ImGui.Checkbox("应用 objectHeight 偏移", ref applyObjHeight))
                {
                    _state.RenderApplyObjectHeight = applyObjHeight;
                }

                if (_state.RenderApplyObjectHeight)
                {
                    float scale = _state.RenderObjectHeightScale;
                    if (ImGui.SliderFloat("物件高度(objectHeight) 缩放（未缩放 px/unit）", ref scale, 0.0f, 8.0f, "%.2f"))
                    {
                        _state.RenderObjectHeightScale = Math.Clamp(scale, 0.0f, 8.0f);
                    }
                }

                bool warnParity = _state.RenderWarnOnUnsupportedParityData;
                if (ImGui.Checkbox("缺失 DynScene/effects 数据时警告", ref warnParity))
                {
                    _state.RenderWarnOnUnsupportedParityData = warnParity;
                }

                bool applyLighting = _state.RenderApplyLightingOverlay;
                if (ImGui.Checkbox("应用夜晚光照叠加", ref applyLighting))
                {
                    _state.RenderApplyLightingOverlay = applyLighting;
                }

                DrawLightingSettingsEditor("render", _state.RenderLighting);

                if (_state.RenderApplyLightingOverlay)
                {
                    int maxAlpha = _state.RenderLightingOverlayMaxAlpha;
                    if (ImGui.SliderInt("叠加最大 Alpha", ref maxAlpha, 0, 255))
                    {
                        _state.RenderLightingOverlayMaxAlpha = Math.Clamp(maxAlpha, 0, 255);
                    }
                }

                bool includeLightSprites = _state.RenderIncludeLightSprites;
                if (ImGui.Checkbox("显示灯光 sprite（Objects10 overObject）", ref includeLightSprites))
                {
                    _state.RenderIncludeLightSprites = includeLightSprites;
                }

                ImGui.Separator();
                ImGui.TextUnformatted("AsyncTextureLoader");

                int maxCache = _state.TextureMaxCacheItems;
                int submitBudget = _state.TextureSubmitBudgetPerFrame;
                int createBudget = _state.TextureCreateBudgetPerFrame;

                ImGui.InputInt("GPU 缓存上限", ref maxCache);
                ImGui.InputInt("每帧提交解码", ref submitBudget);
                ImGui.InputInt("每帧创建纹理", ref createBudget);

                _state.TextureMaxCacheItems = Math.Clamp(maxCache, 16, 32768);
                _state.TextureSubmitBudgetPerFrame = Math.Clamp(submitBudget, 1, 8192);
                _state.TextureCreateBudgetPerFrame = Math.Clamp(createBudget, 1, 1024);

                if (ImGui.Button("清空 GPU 纹理缓存"))
                {
                    _textureLoader?.CancelPendingDecodes();
                    _textureLoader?.Clear();
                    _state.StatusMessage = "已清空 GPU 纹理缓存。";
                    _console.Append(MapEditorConsoleLogLevel.Info, "Texture", "已清空 GPU 纹理缓存。");
                }

                break;
            }

            case MapEditorSettingsSection.KeyBindings:
            {
                BuildKeyBindingsSettingsContent();
                break;
            }

            case MapEditorSettingsSection.Luminance:
            {
                BuildLuminanceSettingsContent();
                break;
            }

            default:
                break;
        }

        ImGui.EndChild();

        ImGui.End();
    }

    private static Vector4 ConsoleLevelColor(MapEditorConsoleLogLevel level)
    {
        return level switch
        {
            MapEditorConsoleLogLevel.Warning => new Vector4(1.0f, 0.78f, 0.18f, 1.0f),
            MapEditorConsoleLogLevel.Error => new Vector4(1.0f, 0.38f, 0.38f, 1.0f),
            _ => new Vector4(0.82f, 0.84f, 0.88f, 1.0f),
        };
    }

    private static MapEditorConsoleLogLevel ParseConsoleLevelFromLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return MapEditorConsoleLogLevel.Info;
        }

        int first = line.IndexOf("] [", StringComparison.Ordinal);
        if (first < 0)
        {
            return MapEditorConsoleLogLevel.Info;
        }

        int levelStart = first + 3;
        int levelEnd = line.IndexOf(']', levelStart);
        if (levelEnd <= levelStart)
        {
            return MapEditorConsoleLogLevel.Info;
        }

        string token = line.Substring(levelStart, levelEnd - levelStart);
        if (token.Equals("WARN", StringComparison.OrdinalIgnoreCase) || token.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return MapEditorConsoleLogLevel.Warning;
        }

        if (token.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return MapEditorConsoleLogLevel.Error;
        }

        return MapEditorConsoleLogLevel.Info;
    }

    private void BuildConsoleWindow()
    {
        bool open = _state.ShowConsolePanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("Console##console", ref open))
        {
            ImGui.End();
            _state.ShowConsolePanel = open;
            return;
        }

        _state.ShowConsolePanel = open;

        if (!_console.Initialized)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Console backend 未初始化（无法写入/读取 console_logs）。");
            ImGui.End();
            return;
        }

        if (ImGui.Button("清空 Live"))
        {
            _console.ClearLive();
        }

        ImGui.SameLine();
        if (ImGui.Button("刷新会话"))
        {
            _console.RefreshSessions();
        }

        ImGui.SameLine();
        bool autoScroll = _state.ConsoleAutoScroll;
        if (ImGui.Checkbox("自动滚动", ref autoScroll))
        {
            _state.ConsoleAutoScroll = autoScroll;
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(260.0f);
        ImGui.InputTextWithHint("##ConsoleSearch", "搜索可见日志...", ref _consoleSearchFilter, 256);
        ImGui.SameLine();
        ImGui.Checkbox("Info", ref _consoleShowInfo);
        ImGui.SameLine();
        ImGui.Checkbox("Warnings", ref _consoleShowWarnings);
        ImGui.SameLine();
        ImGui.Checkbox("Errors", ref _consoleShowErrors);

        IReadOnlyList<MapEditorConsoleSessionFile> sessions = _console.Sessions;
        string selectedPath = _console.SelectedSessionPath;
        bool selectedIsCurrent = _console.IsSelectedCurrentSession(out selectedPath);

        if (sessions.Count > 0)
        {
            int selectedIndex = 0;
            for (int i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(sessions[i].Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    break;
                }
            }

            string comboLabel = sessions[selectedIndex].DisplayName;
            if (ImGui.BeginCombo("会话(Session)", comboLabel))
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    MapEditorConsoleSessionFile session = sessions[i];
                    bool isSelected = i == selectedIndex;
                    if (ImGui.Selectable(session.DisplayName, isSelected))
                    {
                        _console.SelectedSessionPath = session.Path;
                        selectedPath = session.Path;
                        selectedIsCurrent = string.Equals(session.Path, _console.CurrentSessionPath, StringComparison.OrdinalIgnoreCase);
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }
        }
        else
        {
            ImGui.TextDisabled("未发现 console_logs/*.log 会话文件。");
            selectedIsCurrent = false;
        }

        if (!selectedIsCurrent)
        {
            _console.FollowLive = false;
        }

        if (selectedIsCurrent)
        {
            ImGui.SameLine();
            bool followLive = _console.FollowLive;
            if (ImGui.Checkbox("跟随 Live", ref followLive))
            {
                _console.FollowLive = followLive;
            }
        }

        bool AcceptLevel(MapEditorConsoleLogLevel level)
        {
            if (level == MapEditorConsoleLogLevel.Info) return _consoleShowInfo;
            if (level == MapEditorConsoleLogLevel.Warning) return _consoleShowWarnings;
            return _consoleShowErrors;
        }

        var visibleLines = new List<string>(capacity: 256);
        var visibleLevels = new List<MapEditorConsoleLogLevel>(capacity: 256);

        bool filterOn = !string.IsNullOrWhiteSpace(_consoleSearchFilter);

        if (selectedIsCurrent && _console.FollowLive)
        {
            IReadOnlyList<MapEditorConsoleEntry> entries = _console.LiveEntries;
            for (int i = 0; i < entries.Count; i++)
            {
                MapEditorConsoleEntry entry = entries[i];
                if (!AcceptLevel(entry.Level))
                {
                    continue;
                }

                string line = MapEditorConsoleBackend.FormatConsoleLine(entry);
                if (filterOn && line.IndexOf(_consoleSearchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                visibleLines.Add(line);
                visibleLevels.Add(entry.Level);
            }
        }
        else if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            const int pageSize = 100;
            int totalLines = _console.GetHistoryTotalLines(selectedPath);
            int maxStart = totalLines > pageSize ? (totalLines - pageSize) : 0;
            int historyStart = Math.Min(_console.HistoryStartLine, maxStart);
            _console.HistoryStartLine = historyStart;

            IReadOnlyList<string> pageLines = _console.GetHistoryPageLines(selectedPath, historyStart);
            for (int i = 0; i < pageLines.Count; i++)
            {
                string line = pageLines[i];
                MapEditorConsoleLogLevel level = ParseConsoleLevelFromLine(line);
                if (!AcceptLevel(level))
                {
                    continue;
                }

                if (filterOn && line.IndexOf(_consoleSearchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                visibleLines.Add(line);
                visibleLevels.Add(level);
            }

            if (totalLines > 0)
            {
                ImGui.TextDisabled($"Showing lines {historyStart + 1}-{historyStart + pageLines.Count} of {totalLines}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("复制可见(Copy Visible)"))
        {
            var sb = new StringBuilder(capacity: 8192);
            for (int i = 0; i < visibleLines.Count; i++)
            {
                sb.AppendLine(visibleLines[i]);
            }
            ImGui.SetClipboardText(sb.ToString());
        }

        ImGui.Separator();

        bool childOpen = ImGui.BeginChild("##ConsoleLogRegion", new Vector2(0.0f, 0.0f), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
        if (childOpen)
        {
            bool wasAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY();

            for (int i = 0; i < visibleLines.Count; i++)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ConsoleLevelColor(visibleLevels[i]));
                ImGui.Selectable(visibleLines[i], selected: false, ImGuiSelectableFlags.AllowDoubleClick);
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    ImGui.SetClipboardText(visibleLines[i]);
                }
                ImGui.PopStyleColor();
            }

            if (selectedIsCurrent && _console.FollowLive && _state.ConsoleAutoScroll && wasAtBottom)
            {
                ImGui.SetScrollHereY(1.0f);
            }

            if ((!selectedIsCurrent || !_console.FollowLive) && ImGui.IsWindowHovered())
            {
                ImGuiIOPtr io = ImGui.GetIO();
                if (io.MouseWheel > 0.0f)
                {
                    _console.HistoryStartLine = Math.Max(0, _console.HistoryStartLine - 20);
                }
                else if (io.MouseWheel < 0.0f)
                {
                    _console.HistoryStartLine += 20;
                }
            }

        }

        ImGui.EndChild();
        ImGui.End();
    }

    private void BuildMapInfoWindow()
    {
        bool open = _state.ShowInformationPanel;
        if (!open)
        {
            return;
        }

        if (!ImGui.Begin("地图信息##map_info", ref open))
        {
            ImGui.End();
            _state.ShowInformationPanel = open;
            return;
        }

        _state.ShowInformationPanel = open;

        if (_state.Map is not null)
        {
            MapDocument map = _state.Map;
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(_state.MapPath) ? map.Path : _state.MapPath);
            ImGui.TextUnformatted($"尺寸：{map.Width} x {map.Height}");
            ImGui.TextUnformatted($"版本：{map.Version}");
            ImGui.TextUnformatted($"分块：{map.ChunkCountX} x {map.ChunkCountY}（块大小={map.ChunkSize}）");
        }
        else if (!string.IsNullOrWhiteSpace(_state.MapPath))
        {
            ImGui.TextUnformatted(_state.MapPath);
        }

        if (!string.IsNullOrWhiteSpace(_state.MapLoadError))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "错误：");
            ImGui.TextWrapped(_state.MapLoadError);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Data Path（按文档记忆）");

        MapEditorDocument? doc = GetActiveDocument();
        if (doc is null)
        {
            ImGui.TextDisabled("未打开文档。");
        }
        else if (_state.DataPathEntries.Count <= 0)
        {
            ImGui.TextDisabled("尚未配置 Data Paths（请在 偏好设置 -> Data Paths 中添加）。");
        }
        else
        {
            int selected = FindDataPathEntryIndexByDisplayName(doc.PreferredDataPathEntryName);
            if (selected < 0)
            {
                selected = _state.SelectedDataPathEntryIndex;
            }

            selected = Math.Clamp(selected, 0, _state.DataPathEntries.Count - 1);
            NamedPathEntry currentEntry = _state.DataPathEntries[selected];
            string currentLabel = string.IsNullOrWhiteSpace(currentEntry.DisplayName) ? "(未命名)" : currentEntry.DisplayName;

            if (ImGui.BeginCombo("选择", currentLabel))
            {
                for (int i = 0; i < _state.DataPathEntries.Count; i++)
                {
                    NamedPathEntry entry = _state.DataPathEntries[i];
                    string label = string.IsNullOrWhiteSpace(entry.DisplayName) ? "(未命名)" : entry.DisplayName;
                    bool isSelected = i == selected;
                    if (ImGui.Selectable(label, isSelected))
                    {
                        string name = entry.DisplayName ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = SuggestNamedPathDisplayName(entry.Path ?? string.Empty, "Data Path", i + 1);
                            entry.DisplayName = name;
                        }

                        doc.PreferredDataPathEntryName = name;
                        PersistFolderMetaForDocument(doc, "切换 Data Path");
                        ApplyDocumentPreferredDataPath(doc, startScan: !_isRestoringDocuments, reason: "信息面板");
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Path);
                    }
                }

                ImGui.EndCombo();
            }

            if (!string.IsNullOrWhiteSpace(currentEntry.Path))
            {
                ImGui.TextDisabled(currentEntry.Path);
            }

            bool textureScanBusy = _textureScanTask is not null && !_textureScanTask.IsCompleted;
            if (textureScanBusy)
            {
                ImGui.TextDisabled("扫描中...");
            }
            else if (ImGui.Button("扫描贴图库（SGL/WPF）##map_info_scan"))
            {
                string root = _state.TextureRootDirectory?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    _state.TextureLastError = string.IsNullOrWhiteSpace(root) ? "贴图库目录为空。" : $"贴图库目录不存在：{root}";
                    _state.StatusMessage = _state.TextureLastError;
                }
                else
                {
                    _state.TextureLastError = string.Empty;
                    _textureScanTask = Task.Run(() => MapTextureIndex.ScanSglDirectory(root, _state.TextureScanRecursive));
                    _state.StatusMessage = "开始扫描贴图库...";
                    _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"开始扫描贴图库（SGL/WPF）：{root}（递归={_state.TextureScanRecursive}）");
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"包数：{_textureIndex.PackageCount}");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("图层显示");

        bool showBackLayer = _state.ShowBackLayer;
        if (ImGui.Checkbox("后景 (SmTiles)", ref showBackLayer))
        {
            _state.ShowBackLayer = showBackLayer;
        }

        bool showMiddleLayer = _state.ShowMiddleLayer;
        if (ImGui.Checkbox("中景 (Tiles)", ref showMiddleLayer))
        {
            _state.ShowMiddleLayer = showMiddleLayer;
        }

        bool showFloorLayer = _state.ShowFloorLayer;
        if (ImGui.Checkbox("近景地面 (NearGround)", ref showFloorLayer))
        {
            _state.ShowFloorLayer = showFloorLayer;
        }

        bool showUnderLayer = _state.ShowUnderFrontLayer;
        if (ImGui.Checkbox("下层物件 (UnderObject)", ref showUnderLayer))
        {
            _state.ShowUnderFrontLayer = showUnderLayer;
        }

        bool showFrontLayer = _state.ShowFrontLayer;
        if (ImGui.Checkbox("前景 (Object)", ref showFrontLayer))
        {
            _state.ShowFrontLayer = showFrontLayer;
        }

        bool showOverLayer = _state.ShowOverFrontLayer;
        if (ImGui.Checkbox("上层物件 (OverObject)", ref showOverLayer))
        {
            _state.ShowOverFrontLayer = showOverLayer;
        }

        bool showTileFill = _state.ShowTileFill;
        if (ImGui.Checkbox("显示填充", ref showTileFill))
        {
            _state.ShowTileFill = showTileFill;
        }

        bool showGrid = _state.ShowGrid;
        if (ImGui.Checkbox("显示网格", ref showGrid))
        {
            _state.ShowGrid = showGrid;
        }

        float zoom = _state.Camera.Zoom;
        if (ImGui.SliderFloat("缩放", ref zoom, _state.Camera.MinZoom, _state.Camera.MaxZoom, "%.2f"))
        {
            _state.Camera.Zoom = zoom;
        }

        if (ImGui.Button("适配视图"))
        {
            _state.CameraNeedsFit = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("撤销/重做");

        bool canUndo = _state.Map is not null && _state.History.UndoCount > 0;
        bool canRedo = _state.Map is not null && _state.History.RedoCount > 0;

        if (ImGui.Button("撤销") && canUndo)
        {
            TryUndo();
        }

        ImGui.SameLine();
        if (ImGui.Button("重做") && canRedo)
        {
            TryRedo();
        }

        ImGui.TextUnformatted($"撤销：{_state.History.UndoCount}  重做：{_state.History.RedoCount}");
        if (!string.IsNullOrWhiteSpace(_state.History.PeekUndoName))
        {
            ImGui.TextUnformatted($"下一个撤销：{_state.History.PeekUndoName}");
        }

        if (!string.IsNullOrWhiteSpace(_state.History.PeekRedoName))
        {
            ImGui.TextUnformatted($"下一个重做：{_state.History.PeekRedoName}");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("选择");

        if (_state.Map is null)
        {
            ImGui.TextDisabled("未加载地图。");
        }
        else if (_moveSelection is not null)
        {
            MapDocument snippet = _moveSelection.Snippet;
            ImGui.TextUnformatted($"正在移动选区：{snippet.Width} x {snippet.Height}（一次性放置）");
            ImGui.TextDisabled("提示：点击地图放置；按 Esc 取消（可 Ctrl+Z 撤销剪切）。");

            if (ImGui.Button("取消移动 (Esc)"))
            {
                ClearMoveSelectionSession(
                    restorePreviousStampSource: true,
                    restoreTool: true,
                    updateStatus: true,
                    statusMessage: "已取消移动选区（可 Ctrl+Z 撤销剪切）。");
            }
        }
        else if (!_state.HasSelection)
        {
            ImGui.TextDisabled("未选择区域。提示：切换工具为“选择/Inspect”，在地图上左键拖拽框选。");
        }
        else
        {
            MapDocument map = _state.Map;
            int x0 = Math.Min(_state.SelectionX0, _state.SelectionX1);
            int y0 = Math.Min(_state.SelectionY0, _state.SelectionY1);
            int x1 = Math.Max(_state.SelectionX0, _state.SelectionX1);
            int y1 = Math.Max(_state.SelectionY0, _state.SelectionY1);

            x0 = Math.Clamp(x0, 0, map.Width - 1);
            y0 = Math.Clamp(y0, 0, map.Height - 1);
            x1 = Math.Clamp(x1, 0, map.Width - 1);
            y1 = Math.Clamp(y1, 0, map.Height - 1);

            int selW = x1 >= x0 ? (x1 - x0 + 1) : 0;
            int selH = y1 >= y0 ? (y1 - y0 + 1) : 0;

            ImGui.TextUnformatted($"已选择：({x0},{y0})-({x1},{y1})  {selW} x {selH}");

            if (ImGui.Button("复制为印章 (Ctrl+C)"))
            {
                CopySelectionToClipboardStamp();
            }

            ImGui.SameLine();
            if (ImGui.Button("移动选区 (Ctrl+X)"))
            {
                MoveSelectionToMoveStamp();
            }

            ImGui.SameLine();
            long selectionCellCountLong = (long)selW * selH;
            bool isPrefabDoc = IsActiveDocumentPrefab();
            bool canSavePrefab = !isPrefabDoc && selectionCellCountLong is > 0 and <= 1_000_000;
            if (!canSavePrefab)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.Button("保存为 Prefab..."))
            {
                BeginSaveSelectionAsPrefabPopup((int)map.Version);
            }
            if (!canSavePrefab)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(isPrefabDoc
                        ? "仅对地图文档可用。"
                        : $"选择区域过大（{selW} x {selH} = {selectionCellCountLong} 格）。");
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("清除选择"))
            {
                _state.HasSelection = false;
            }

            ImGui.TextUnformatted("移动层：");

            bool moveBack = _state.MoveApplyBack;
            bool moveMiddle = _state.MoveApplyMiddle;
            bool moveFront = _state.MoveApplyFront;
            bool moveUnderObject = _state.MoveApplyUnderObject;
            bool moveOverObject = _state.MoveApplyOverObject;
            bool moveNearGround = _state.MoveApplyNearGround;
            bool moveBlocked = _state.MoveApplyBlocked;

            if (ImGui.Checkbox("后景##MoveBack", ref moveBack)) _state.MoveApplyBack = moveBack;
            ImGui.SameLine();
            if (ImGui.Checkbox("中景##MoveMiddle", ref moveMiddle)) _state.MoveApplyMiddle = moveMiddle;
            ImGui.SameLine();
            if (ImGui.Checkbox("前景##MoveFront", ref moveFront)) _state.MoveApplyFront = moveFront;

            if (ImGui.Checkbox("下层物件##MoveUnder", ref moveUnderObject)) _state.MoveApplyUnderObject = moveUnderObject;
            ImGui.SameLine();
            if (ImGui.Checkbox("上层物件##MoveOver", ref moveOverObject)) _state.MoveApplyOverObject = moveOverObject;
            ImGui.SameLine();
            if (ImGui.Checkbox("近景地面##MoveNear", ref moveNearGround)) _state.MoveApplyNearGround = moveNearGround;
            if (ImGui.Checkbox("阻挡(Blocked,0x01)##MoveBlocked", ref moveBlocked)) _state.MoveApplyBlocked = moveBlocked;

            ImGui.Separator();
            ImGui.TextUnformatted("删除选区（Del，可撤销）：");

            if (ImGui.Button("删除选区 (Del)"))
            {
                EraseSelectionRegion();
            }

            ImGui.TextUnformatted("删除层：");

            bool eraseBack = _state.EraseApplyBack;
            bool eraseMiddle = _state.EraseApplyMiddle;
            bool eraseFront = _state.EraseApplyFront;
            bool eraseUnderObject = _state.EraseApplyUnderObject;
            bool eraseOverObject = _state.EraseApplyOverObject;
            bool eraseNearGround = _state.EraseApplyNearGround;
            bool eraseBlocked = _state.EraseApplyBlocked;

            if (ImGui.Checkbox("后景##EraseBack", ref eraseBack)) _state.EraseApplyBack = eraseBack;
            ImGui.SameLine();
            if (ImGui.Checkbox("中景##EraseMiddle", ref eraseMiddle)) _state.EraseApplyMiddle = eraseMiddle;
            ImGui.SameLine();
            if (ImGui.Checkbox("前景##EraseFront", ref eraseFront)) _state.EraseApplyFront = eraseFront;

            if (ImGui.Checkbox("下层物件##EraseUnder", ref eraseUnderObject)) _state.EraseApplyUnderObject = eraseUnderObject;
            ImGui.SameLine();
            if (ImGui.Checkbox("上层物件##EraseOver", ref eraseOverObject)) _state.EraseApplyOverObject = eraseOverObject;
            ImGui.SameLine();
            if (ImGui.Checkbox("近景地面##EraseNear", ref eraseNearGround)) _state.EraseApplyNearGround = eraseNearGround;

            if (ImGui.Checkbox("阻挡(Blocked,0x01)##EraseBlocked", ref eraseBlocked)) _state.EraseApplyBlocked = eraseBlocked;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("编辑");

        int toolIndex = (int)_state.Tool;
        if (ImGui.Combo("工具", ref toolIndex, "选择/Inspect\0画笔 (Pencil)\0矩形填充 (Rect)\0印章 (Stamp)\0阻挡编辑 (Blocked)\0擦除 (Erase)\0"))
        {
            MapEditTool newTool = (MapEditTool)Math.Clamp(toolIndex, 0, 5);
            _state.Tool = newTool;
            if (newTool == MapEditTool.BlockedEditor)
            {
                _state.ShowBlockedOverlay = true;
            }
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
            _blockedDrag = null;
        }

        int editLayerIndex = (int)_state.EditLayer;
        if (ImGui.Combo("编辑层", ref editLayerIndex,
                "后景 (SmTiles)\0中景 (Tiles)\0近景地面 (NearGround)\0下层物件 (UnderObject)\0前景 (Object)\0上层物件 (OverObject)\0"))
        {
            _state.EditLayer = (MapLayer)editLayerIndex;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
        }

        int paintLibrary = _state.PaintLibrary;
        int paintImage = _state.PaintImage;
        ImGui.InputInt("库编号(Library)##paint_lib", ref paintLibrary);
        ImGui.InputInt("图片编号(Image)##paint_img", ref paintImage);

        paintLibrary = Math.Clamp(paintLibrary, 0, 65535);
        paintImage = Math.Clamp(paintImage, 0, 65535);

        _state.PaintLibrary = paintLibrary;
        _state.PaintImage = paintImage;

        if (ImGui.Button("应用到选中格"))
        {
            ApplyEditToSelectedCell(clear: false);
        }

        ImGui.SameLine();
        if (ImGui.Button("清空选中格"))
        {
            ApplyEditToSelectedCell(clear: true);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("印章 (Stamp)");

        bool hasMoveSelection = _moveSelection is not null;
        string stampSourceOptions = hasMoveSelection ? "文件路径\0剪贴板\0移动选区(临时)\0" : "文件路径\0剪贴板\0";

        StampSourceKind currentStampSource = _state.StampSource;
        if (!hasMoveSelection && currentStampSource == StampSourceKind.MoveSelection)
        {
            currentStampSource = StampSourceKind.Path;
            _state.StampSource = currentStampSource;
            ClearStamp();
        }

        int stampSourceIndex = currentStampSource switch
        {
            StampSourceKind.Clipboard => 1,
            StampSourceKind.MoveSelection => 2,
            _ => 0,
        };

        if (ImGui.Combo("来源", ref stampSourceIndex, stampSourceOptions))
        {
            StampSourceKind desired = stampSourceIndex switch
            {
                1 => StampSourceKind.Clipboard,
                2 => StampSourceKind.MoveSelection,
                _ => StampSourceKind.Path,
            };

            if (_state.StampSource == StampSourceKind.MoveSelection && desired != StampSourceKind.MoveSelection)
            {
                ClearMoveSelectionSession(restorePreviousStampSource: false, restoreTool: false, updateStatus: false, statusMessage: null);
            }

            _state.StampSource = desired;
            ClearStamp();
        }

        bool stampSourceIsPath = _state.StampSource == StampSourceKind.Path;

        string stampPath = _state.StampPath ?? string.Empty;
        if (!stampSourceIsPath)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.InputText("印章路径（.nmpo/.nmpoN）", ref stampPath, 4096))
        {
            _state.StampPath = stampPath;
            _state.StampSource = StampSourceKind.Path;
            ClearStamp();
        }
        if (!stampSourceIsPath)
        {
            ImGui.EndDisabled();
        }

        bool canUseSelectedPrefab = !string.IsNullOrWhiteSpace(_selectedBrowserPath) && IsPrefabPath(_selectedBrowserPath);
        if (!canUseSelectedPrefab)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("使用文件浏览器选中 Prefab"))
        {
            _state.StampPath = _selectedBrowserPath;
            _state.StampSource = StampSourceKind.Path;
            ClearStamp();
        }

        if (!canUseSelectedPrefab)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("切换到印章工具"))
        {
            _state.Tool = MapEditTool.Stamp;
            _paintDrag = null;
            _rectFillDrag = null;
            _selectionDrag = null;
        }

        ImGui.SameLine();
        if (!stampSourceIsPath)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("重新载入"))
        {
            _ = EnsureStampLoaded(forceReload: true, out _);
        }
        if (!stampSourceIsPath)
        {
            ImGui.EndDisabled();
        }

        int anchorUiIndex = _state.StampAnchor == StampAnchorMode.TopLeft ? 0 : 1;
        if (ImGui.Combo("锚点", ref anchorUiIndex, "左上\0中心\0"))
        {
            _state.StampAnchor = anchorUiIndex == 0 ? StampAnchorMode.TopLeft : StampAnchorMode.Center;
        }

        bool overwriteEmpty = _state.StampOverwriteEmpty;
        if (ImGui.Checkbox("覆盖空值（允许清空目标层）", ref overwriteEmpty))
        {
            _state.StampOverwriteEmpty = overwriteEmpty;
        }

        bool applyBack = _state.StampApplyBack;
        bool applyMiddle = _state.StampApplyMiddle;
        bool applyFront = _state.StampApplyFront;
        bool applyUnderObject = _state.StampApplyUnderObject;
        bool applyOverObject = _state.StampApplyOverObject;
        bool applyNearGround = _state.StampApplyNearGround;
        bool applyBlocked = _state.StampApplyBlocked;

        if (ImGui.Checkbox("应用 Back", ref applyBack)) _state.StampApplyBack = applyBack;
        ImGui.SameLine();
        if (ImGui.Checkbox("应用 Middle", ref applyMiddle)) _state.StampApplyMiddle = applyMiddle;
        ImGui.SameLine();
        if (ImGui.Checkbox("应用 Front", ref applyFront)) _state.StampApplyFront = applyFront;

        if (ImGui.Checkbox("应用 UnderObject", ref applyUnderObject)) _state.StampApplyUnderObject = applyUnderObject;
        ImGui.SameLine();
        if (ImGui.Checkbox("应用 OverObject", ref applyOverObject)) _state.StampApplyOverObject = applyOverObject;
        ImGui.SameLine();
        if (ImGui.Checkbox("应用 NearGround", ref applyNearGround)) _state.StampApplyNearGround = applyNearGround;
        if (ImGui.Checkbox("应用 Blocked(0x01)", ref applyBlocked)) _state.StampApplyBlocked = applyBlocked;

        bool shouldShowStampStatus = _state.StampSource != StampSourceKind.Path || !string.IsNullOrWhiteSpace(_state.StampPath);
        if (shouldShowStampStatus)
        {
            if (EnsureStampLoaded(forceReload: false, out string stampError))
            {
                if (_stampMap is not null)
                {
                    string label = _state.StampSource switch
                    {
                        StampSourceKind.Path => Path.GetFileName(_loadedStampPath),
                        StampSourceKind.Clipboard => "剪贴板",
                        _ => "移动选区",
                    };
                    ImGui.TextDisabled($"已加载：{label}（{_stampMap.Width} x {_stampMap.Height}）");
                }
            }
            else if (!string.IsNullOrWhiteSpace(stampError))
            {
                ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "印章载入失败：");
                ImGui.TextWrapped(stampError);
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("贴图库（SGL/WPF） / AsyncTextureLoader");

        string textureRoot = _state.TextureRootDirectory ?? string.Empty;
        float dataRootBrowseButtonWidth = 80.0f;
        float dataRootBrowseSpacing = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetNextItemWidth(-dataRootBrowseButtonWidth - dataRootBrowseSpacing);
        if (ImGui.InputText("贴图库目录", ref textureRoot, 4096))
        {
            _state.TextureRootDirectory = textureRoot;
            int matchIndex = FindNamedPathEntry(_state.DataPathEntries, NormalizeNamedPath(textureRoot));
            if (matchIndex >= 0)
            {
                _state.SelectedDataPathEntryIndex = matchIndex;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("浏览...##data_root_browse"))
        {
            string startDir = textureRoot;
            StartFolderBrowse(FolderBrowseTarget.TextureRoot, -1, "选择贴图库目录(Data Root)", startDir);
        }

        DrawDataPathEntriesSection();

        {
            bool downloaderRunning = _editorBridge.IsAppRunning(EditorBridgeApp.Downloader);
            bool canFetch = TryGetFetchTexturesRequestInfo(out _, out _, out string fetchReason);

            if (!canFetch)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("抓取纹理(Fetch Textures)"))
            {
                TrySendFetchTexturesRequest();
            }

            if (!canFetch)
            {
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(fetchReason))
                {
                    ImGui.SetTooltip(fetchReason);
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"Downloader：{(downloaderRunning ? "运行中" : "未检测到")}");
        }

        ImGui.Separator();

        if (ImGui.Button("从地图目录推断"))
        {
            try
            {
                string sourcePath = _state.Map?.Path ?? _state.MapPath;
                string dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    _state.TextureRootDirectory = dir;
                }
            }
            catch
            {
                _state.TextureRootDirectory = string.Empty;
            }
        }

        ImGui.SameLine();
        bool recursive = _state.TextureScanRecursive;
        if (ImGui.Checkbox("递归扫描", ref recursive))
        {
            _state.TextureScanRecursive = recursive;
        }

        bool scanBusy = _textureScanTask is not null && !_textureScanTask.IsCompleted;
        if (scanBusy)
        {
            ImGui.TextDisabled("扫描中...");
        }
        else
        {
            if (ImGui.Button("扫描贴图库（SGL/WPF）"))
            {
                _state.TextureLastError = string.Empty;
                string root = _state.TextureRootDirectory ?? string.Empty;
                _textureScanTask = Task.Run(() => MapTextureIndex.ScanSglDirectory(root, _state.TextureScanRecursive));
                _state.StatusMessage = "开始扫描贴图库...";
                _console.Append(MapEditorConsoleLogLevel.Info, "Texture", $"开始扫描贴图库（SGL/WPF）：{_state.TextureRootDirectory}（递归={_state.TextureScanRecursive}）");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("重置贴图库"))
        {
            _textureIndex.Reset();
            _textureLoader?.InvalidateAll();
            _prefabThumbnailLoader?.InvalidateAll();
            _state.RenderUseTextures = false;
            _state.RenderAnimateTextures = false;
            _state.StatusMessage = "已重置贴图库。";
        }

        if (!string.IsNullOrWhiteSpace(_state.TextureLastError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "贴图库错误：");
            ImGui.TextWrapped(_state.TextureLastError);
        }

        ImGui.TextUnformatted($"包数：{_textureIndex.PackageCount}  缓存：{_textureLoader?.CachedCount ?? 0}  待处理：{_textureLoader?.PendingCount ?? 0}  错误：{_textureLoader?.ErrorCount ?? 0}");
        ImGui.TextUnformatted($"提交：{_textureLoader?.SubmittedTotal ?? 0}  创建：{_textureLoader?.CreatedTotal ?? 0}  失败：{_textureLoader?.FailedTotal ?? 0}  取消：{_textureLoader?.CanceledTotal ?? 0}");

        ImGui.Separator();
        ImGui.TextUnformatted("资源引用验证（真实资源）");

        bool validateBusy = _resourceValidationTask is not null && !_resourceValidationTask.IsCompleted;
        bool canValidate = _state.Map is not null && _textureIndex.IsReady && !scanBusy;
        if (!canValidate)
        {
            ImGui.TextDisabled("提示：需要先加载地图并完成“扫描贴图库（SGL/WPF）”。");
        }

        bool validateCoast = _resourceValidationValidateCoastComposite;
        if (ImGui.Checkbox("包含海岸合成（MiddleImage2 + AlphaMask）", ref validateCoast))
        {
            _resourceValidationValidateCoastComposite = validateCoast;
        }

        int maxSamples = _resourceValidationMaxSamplesPerIssue;
        if (ImGui.InputInt("每项最多坐标样本", ref maxSamples))
        {
            _resourceValidationMaxSamplesPerIssue = Math.Clamp(maxSamples, 0, 64);
        }

        if (!canValidate || validateBusy)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("开始验证"))
        {
            StartResourceValidation();
        }

        if (!canValidate || validateBusy)
        {
            ImGui.EndDisabled();
        }

        if (validateBusy)
        {
            ImGui.SameLine();
            if (ImGui.Button("取消验证"))
            {
                try
                {
                    _resourceValidationCts?.Cancel();
                }
                catch
                {
                    // ignored
                }
            }

            ImGui.TextDisabled("验证中...");
        }

        if (!string.IsNullOrWhiteSpace(_resourceValidationError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "资源验证错误：");
            ImGui.TextWrapped(_resourceValidationError);
        }

        if (_resourceValidationReport is not null)
        {
            MapResourceValidationReport report = _resourceValidationReport;
            ImGui.TextUnformatted($"报告：{report.DocumentPath}（{report.Width} x {report.Height}）");
            ImGui.TextUnformatted($"生成：{report.GeneratedAt:yyyy-MM-dd HH:mm:ss}  UniqueImages={report.UniqueImageRefs}  Coast={report.UniqueCoastCompositeRefs}  Issues={report.Issues.Count}");

            string exportPath = _resourceValidationExportPath;
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                exportPath = Path.Combine(Environment.CurrentDirectory, "artifacts", "reports", "resource_validation_report.json");
                _resourceValidationExportPath = exportPath;
            }

            if (ImGui.InputText("导出 JSON", ref exportPath, 4096))
            {
                _resourceValidationExportPath = exportPath;
            }

            if (ImGui.Button("写入 JSON 报告"))
            {
                TryExportResourceValidationReport(report, exportPath);
            }

            ImGui.SameLine();
            if (ImGui.Button("复制摘要"))
            {
                string summary = $"资源验证：{report.DocumentPath} | UniqueImages={report.UniqueImageRefs} | Coast={report.UniqueCoastCompositeRefs} | Issues={report.Issues.Count}";
                ImGui.SetClipboardText(summary);
                _state.StatusMessage = "已复制资源验证摘要到剪贴板。";
            }

            string filterText = _resourceValidationFilter;
            if (ImGui.InputText("过滤", ref filterText, 256))
            {
                _resourceValidationFilter = filterText;
            }

            int maxDisplay = _resourceValidationMaxDisplayItems;
            if (ImGui.InputInt("最多显示", ref maxDisplay))
            {
                _resourceValidationMaxDisplayItems = Math.Clamp(maxDisplay, 10, 5000);
            }

            if (report.Issues.Count <= 0)
            {
                ImGui.TextDisabled("未发现问题。");
            }
            else
            {
                bool childOpen = ImGui.BeginChild("##resource_validation_issues", new Vector2(0, 220), (ImGuiChildFlags)0, ImGuiWindowFlags.None);
                if (childOpen)
                {
                    int shown = 0;
                    string filter = _resourceValidationFilter ?? string.Empty;

                    foreach (MapResourceValidationIssue issue in report.Issues)
                    {
                        if (shown >= _resourceValidationMaxDisplayItems)
                        {
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(filter))
                        {
                            string hay = $"{issue.Kind} {issue.PackageId} {issue.ImageIndex} {issue.MaskImageIndex} {issue.Layers} {issue.Error}";
                            if (hay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }
                        }

                        string keyLabel = issue.MaskImageIndex > 0
                            ? $"{issue.Kind}: 包={issue.PackageId} 图={issue.ImageIndex} 掩码={issue.MaskImageIndex}"
                            : $"{issue.Kind}: 包={issue.PackageId} 图={issue.ImageIndex}";

                        ImGui.TextUnformatted(keyLabel);
                        if (ImGui.BeginPopupContextItem($"##resource_validation_ctx_{issue.Kind}_{issue.PackageId}_{issue.ImageIndex}_{issue.MaskImageIndex}"))
                        {
                            bool hasSample = issue.Samples is not null && issue.Samples.Count > 0;
                            if (ImGui.MenuItem("定位到第一个样本格子", null, selected: false, enabled: hasSample))
                            {
                                MapResourceValidationSample sample = issue.Samples![0];
                                SelectAndCenterOnCell(sample.X, sample.Y);
                            }

                            bool canOpen = false;
                            string openTip = string.Empty;
                            string cePath = string.Empty;
                            int ceSel = -1;

                            if (issue.PackageId <= 0 || issue.ImageIndex <= 0)
                            {
                                openTip = "引用无效（packageId/imageIndex <= 0）。";
                            }
                            else if (!_textureIndex.IsReady)
                            {
                                openTip = "贴图库未就绪：请先扫描贴图库（SGL/WPF）。";
                            }
                            else
                            {
                                canOpen = _textureIndex.TryGetImageBridgeTarget(issue.PackageId, issue.ImageIndex, out cePath, out ceSel, out string bridgeError);
                                if (!canOpen)
                                {
                                    openTip = string.IsNullOrWhiteSpace(bridgeError) ? "无法解析贴图来源。" : bridgeError;
                                }
                            }

                            if (ImGui.MenuItem("在 ContentEditor 中打开对应资源", null, selected: false, enabled: canOpen))
                            {
                                string desc = $"资源验证：包={issue.PackageId} 图={issue.ImageIndex}";
                                TrySendAssetToContentEditor(cePath, ceSel, desc);
                            }

                            if (!canOpen && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(openTip))
                            {
                                ImGui.SetTooltip(openTip);
                            }

                            ImGui.EndPopup();
                        }
                        ImGui.SameLine();
                        ImGui.TextDisabled($"x{issue.Occurrences}");

                        if (!string.IsNullOrWhiteSpace(issue.Layers))
                        {
                            ImGui.SameLine();
                            ImGui.TextDisabled(issue.Layers);
                        }

                        if (issue.Samples is not null && issue.Samples.Count > 0)
                        {
                            string sampleText = string.Join(", ", issue.Samples.Select(static s => $"({s.X},{s.Y})"));
                            ImGui.TextDisabled($"样本：{sampleText}");
                        }

                        if (!string.IsNullOrWhiteSpace(issue.Error))
                        {
                            ImGui.TextWrapped(issue.Error);
                        }

                        ImGui.Separator();
                        shown++;
                    }

                    if (shown <= 0)
                    {
                        ImGui.TextDisabled("无匹配项。");
                    }

                    if (shown >= _resourceValidationMaxDisplayItems && report.Issues.Count > shown)
                    {
                        ImGui.TextDisabled($"已达到显示上限：{_resourceValidationMaxDisplayItems}（总计={report.Issues.Count}）。");
                    }
                }

                // BeginChild/EndChild 必须成对调用：即便 BeginChild 返回 false（不可见/被裁剪）也需要 EndChild，
                // 否则会触发 ImGui 断言（Missing EndChild）并导致 native cimgui.dll 弹窗。
                ImGui.EndChild();
            }
        }

        bool canRenderTextures = _textureIndex.IsReady && _textureLoader is not null;
        bool renderTextures = _state.RenderUseTextures;
        if (!canRenderTextures)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("使用真实贴图渲染（按可见图层叠加）", ref renderTextures))
        {
            _state.RenderUseTextures = renderTextures;
            if (!renderTextures)
            {
                _state.RenderAnimateTextures = false;
            }
        }

        if (!canRenderTextures)
        {
            ImGui.EndDisabled();
        }

        bool canAnimateTextures = canRenderTextures && renderTextures;
        bool animateTextures = _state.RenderAnimateTextures;
        float animationFps = _state.TextureAnimationFps;
        bool animationOffsetPerCell = _state.TextureAnimationPerCellOffset;

        if (!canAnimateTextures)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("动画预览（按 TEX 帧）", ref animateTextures))
        {
            _state.RenderAnimateTextures = animateTextures;
        }

        if (animateTextures)
        {
            if (ImGui.SliderFloat("动画 FPS", ref animationFps, 1.0f, 30.0f, "%.1f"))
            {
                _state.TextureAnimationFps = Math.Clamp(animationFps, 1.0f, 1000.0f);
            }

            if (ImGui.Checkbox("每格错开起始帧", ref animationOffsetPerCell))
            {
                _state.TextureAnimationPerCellOffset = animationOffsetPerCell;
            }
        }

        if (!canAnimateTextures)
        {
            ImGui.EndDisabled();
        }

        bool canTintTextures = canRenderTextures && renderTextures;
        bool applyTints = _state.RenderApplyCellTints;
        float tintStrength = _state.RenderTintStrength;

        if (!canTintTextures)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("应用格子色调(ColorAdj)", ref applyTints))
        {
            _state.RenderApplyCellTints = applyTints;
        }

        if (applyTints)
        {
            if (ImGui.SliderFloat("染色强度(Tint)", ref tintStrength, 0.0f, 1.0f, "%.2f"))
            {
                _state.RenderTintStrength = Math.Clamp(tintStrength, 0.0f, 1.0f);
            }
        }

        if (!canTintTextures)
        {
            ImGui.EndDisabled();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("运行时渲染（对齐旧工程/导出语义）");

        bool suppressBorderCells = _state.RenderSuppressBorderCells;
        if (ImGui.Checkbox("抑制 BORDER 格（flags 0x04）", ref suppressBorderCells))
        {
            _state.RenderSuppressBorderCells = suppressBorderCells;
        }

        bool canParityOptions = canRenderTextures && renderTextures;
        if (!canParityOptions)
        {
            ImGui.BeginDisabled();
        }

        bool applyHeightFlag = _state.RenderApplyCellHeightFlag;
        if (ImGui.Checkbox("应用 NEARGROUND(0x40) 高度偏移", ref applyHeightFlag))
        {
            _state.RenderApplyCellHeightFlag = applyHeightFlag;
        }

        if (_state.RenderApplyCellHeightFlag)
        {
            float lift = _state.RenderCellHeightFlagOffset;
            if (ImGui.SliderFloat("高度标记提升（未缩放 px）", ref lift, 0.0f, 64.0f, "%.2f"))
            {
                _state.RenderCellHeightFlagOffset = Math.Clamp(lift, 0.0f, 64.0f);
            }
        }

        bool applyObjectHeight = _state.RenderApplyObjectHeight;
        if (ImGui.Checkbox("应用 objectHeight 偏移", ref applyObjectHeight))
        {
            _state.RenderApplyObjectHeight = applyObjectHeight;
        }

        if (_state.RenderApplyObjectHeight)
        {
            float scale = _state.RenderObjectHeightScale;
            if (ImGui.SliderFloat("物件高度(objectHeight) 缩放（未缩放 px/unit）", ref scale, 0.0f, 8.0f, "%.2f"))
            {
                _state.RenderObjectHeightScale = Math.Clamp(scale, 0.0f, 8.0f);
            }
        }

        bool applyLightingOverlay = _state.RenderApplyLightingOverlay;
        if (ImGui.Checkbox("应用夜晚光照叠加", ref applyLightingOverlay))
        {
            _state.RenderApplyLightingOverlay = applyLightingOverlay;
        }

        DrawLightingSettingsEditor("render", _state.RenderLighting);

        if (_state.RenderApplyLightingOverlay)
        {
            int maxAlpha = _state.RenderLightingOverlayMaxAlpha;
            if (ImGui.SliderInt("叠加最大 Alpha", ref maxAlpha, 0, 255))
            {
                _state.RenderLightingOverlayMaxAlpha = Math.Clamp(maxAlpha, 0, 255);
            }
        }

        bool includeLightSprites = _state.RenderIncludeLightSprites;
        if (ImGui.Checkbox("显示灯光 sprite（Objects10 overObject）", ref includeLightSprites))
        {
            _state.RenderIncludeLightSprites = includeLightSprites;
        }

        if (!canParityOptions)
        {
            ImGui.EndDisabled();
        }

        int maxCache = _state.TextureMaxCacheItems;
        int submitBudget = _state.TextureSubmitBudgetPerFrame;
        int createBudget = _state.TextureCreateBudgetPerFrame;

        ImGui.InputInt("GPU 缓存上限", ref maxCache);
        ImGui.InputInt("每帧提交解码", ref submitBudget);
        ImGui.InputInt("每帧创建纹理", ref createBudget);

        _state.TextureMaxCacheItems = Math.Clamp(maxCache, 16, 32768);
        _state.TextureSubmitBudgetPerFrame = Math.Clamp(submitBudget, 1, 8192);
        _state.TextureCreateBudgetPerFrame = Math.Clamp(createBudget, 1, 1024);

        if (ImGui.Button("清空 GPU 纹理缓存"))
        {
            _textureLoader?.CancelPendingDecodes();
            _textureLoader?.Clear();
            _state.StatusMessage = "已清空 GPU 纹理缓存。";
        }

        if (_textureLoader is not null)
        {
            int errorCount = _textureLoader.ErrorCount;
            TextureErrorInfo[] errors = _textureLoader.GetRecentErrors(32);

            if (errorCount > 0 || errors.Length > 0)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), $"纹理错误（最近 {errors.Length} / 当前 {errorCount}）:");
                ImGui.BeginChild("##texture_loader_errors", new Vector2(0, 140), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
                for (int i = 0; i < errors.Length; i++)
                {
                    TextureErrorInfo e = errors[i];
                    ImGui.TextUnformatted($"包={e.PackageId} 图={e.ImageIndex} 帧={e.Frame}: {e.Error}");
                }
                ImGui.EndChild();

                if (ImGui.Button("清空纹理错误列表"))
                {
                    _textureLoader.ClearErrors();
                    _state.StatusMessage = "已清空纹理错误列表。";
                }

                ImGui.SameLine();
                if (ImGui.Button("取消挂起解码"))
                {
                    _textureLoader.CancelPendingDecodes();
                    _state.StatusMessage = "已取消挂起解码。";
                }
            }
        }

        ImGui.End();
    }

    private void StartResourceValidation()
    {
        if (_state.Map is null)
        {
            _resourceValidationError = "未加载地图。";
            _state.StatusMessage = _resourceValidationError;
            return;
        }

        if (!_textureIndex.IsReady)
        {
            _resourceValidationError = "贴图库未就绪：请先扫描贴图库（SGL/WPF）。";
            _state.StatusMessage = _resourceValidationError;
            return;
        }

        if (_resourceValidationTask is not null && !_resourceValidationTask.IsCompleted)
        {
            _resourceValidationError = "资源验证正在进行中。";
            _state.StatusMessage = _resourceValidationError;
            return;
        }

        _resourceValidationError = string.Empty;
        _resourceValidationReport = null;

        _resourceValidationCts?.Dispose();
        _resourceValidationCts = new CancellationTokenSource();

        var options = new MapResourceValidationOptions
        {
            ValidateCoastComposite = _resourceValidationValidateCoastComposite,
            MaxSamplesPerIssue = Math.Clamp(_resourceValidationMaxSamplesPerIssue, 0, 64),
        };

        MapDocument map = _state.Map;
        CancellationToken token = _resourceValidationCts.Token;
        _resourceValidationTask = Task.Run(() => MapResourceValidator.Validate(map, _textureIndex, options, token), token);
        _state.StatusMessage = "开始资源引用验证...";
    }

    private void TryExportResourceValidationReport(MapResourceValidationReport report, string path)
    {
        if (report is null)
        {
            _state.StatusMessage = "导出失败：报告为空。";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _state.StatusMessage = "导出失败：路径为空。";
            return;
        }

        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            string json = JsonSerializer.Serialize(report, jsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _state.StatusMessage = $"已导出资源验证报告：{path}";
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    private void CreateNewInMemoryDocument(string title, bool isPrefab, int width, int height, uint version)
    {
        width = Math.Clamp(width, 1, 2000);
        height = Math.Clamp(height, 1, 2000);

        if (isPrefab)
        {
            width = Math.Clamp(width, 1, 200);
            height = Math.Clamp(height, 1, 200);
        }

        int cellCount;
        try
        {
            cellCount = checked(width * height);
        }
        catch (OverflowException)
        {
            _state.StatusMessage = "创建失败：Width*Height 溢出。";
            _console.Append(MapEditorConsoleLogLevel.Error, "New", _state.StatusMessage);
            return;
        }

        if (cellCount <= 0)
        {
            _state.StatusMessage = "创建失败：尺寸无效。";
            _console.Append(MapEditorConsoleLogLevel.Error, "New", _state.StatusMessage);
            return;
        }

        NmpCellData[] cells;
        try
        {
            cells = new NmpCellData[cellCount];
        }
        catch (OutOfMemoryException)
        {
            _state.StatusMessage = $"创建失败：内存不足（{width}x{height}）。";
            _console.Append(MapEditorConsoleLogLevel.Error, "New", _state.StatusMessage);
            return;
        }

        MapDocument map;
        try
        {
            map = MapDocument.CreateInMemory(title, width, height, version, cells);
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"创建失败：{ex.Message}";
            _console.Append(MapEditorConsoleLogLevel.Error, "New", $"创建失败：{ex}");
            return;
        }

        _newInMemoryDocumentCounter++;
        string normalized = $"mem:{(isPrefab ? "prefab" : "map")}:{_newInMemoryDocumentCounter}";

        var doc = new MapEditorDocument(normalized)
        {
            Path = string.Empty,
            IsPrefabDocument = isPrefab,
            DisplayName = title ?? string.Empty,
            PreferredDataPathEntryName = GetSelectedDataPathDisplayName(),
            Map = map,
            MapLoadError = string.Empty,
            CameraNeedsFit = true,
            HoverCellX = -1,
            HoverCellY = -1,
            SelectedCellX = -1,
            SelectedCellY = -1,
            HasSelection = false,
            SelectionX0 = 0,
            SelectionY0 = 0,
            SelectionX1 = 0,
            SelectionY1 = 0,
        };

        doc.History.Clear();
        doc.History.MarkUnsaved();

        _documents.Add(doc);
        ActivateDocument(_documents.Count - 1);

        string kind = isPrefab ? "预制体" : "地图";
        _state.StatusMessage = $"已创建{kind}：{title}（{width}x{height} v{version}）";
        _console.Append(MapEditorConsoleLogLevel.Info, "New", $"已创建{kind}：{title}（{width}x{height} v{version}）");
    }

    private void BuildNewMapPopup()
    {
        if (_requestNewMapPopup)
        {
            _requestNewMapPopup = false;
            ImGui.OpenPopup("新建地图");

            if (Array.IndexOf(PrefabVersions, _newMapVersion) < 0)
            {
                _newMapVersion = PrefabVersions[^1];
            }
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("新建地图", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        int width = _newMapWidth;
        int height = _newMapHeight;
        if (ImGui.InputInt("Width", ref width))
        {
            _newMapWidth = width;
        }
        if (ImGui.InputInt("Height", ref height))
        {
            _newMapHeight = height;
        }

        _newMapWidth = Math.Clamp(_newMapWidth, 1, 2000);
        _newMapHeight = Math.Clamp(_newMapHeight, 1, 2000);

        int versionIndex = GetPrefabVersionIndex(_newMapVersion);
        if (ImGui.Combo("Version", ref versionIndex, PrefabVersionComboItems))
        {
            _newMapVersion = PrefabVersions[Math.Clamp(versionIndex, 0, PrefabVersions.Length - 1)];
        }

        ImGui.Separator();
        if (ImGui.Button("创建", new Vector2(120, 0)))
        {
            CreateNewInMemoryDocument("New Map", false, _newMapWidth, _newMapHeight, (uint)_newMapVersion);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void BuildNewPrefabPopup()
    {
        if (_requestNewPrefabPopup)
        {
            _requestNewPrefabPopup = false;
            ImGui.OpenPopup("新建 Prefab");

            if (Array.IndexOf(PrefabVersions, _newPrefabVersion) < 0)
            {
                _newPrefabVersion = PrefabVersions[^1];
            }
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("新建 Prefab", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        int width = _newPrefabWidth;
        int height = _newPrefabHeight;
        if (ImGui.InputInt("Width", ref width))
        {
            _newPrefabWidth = width;
        }
        if (ImGui.InputInt("Height", ref height))
        {
            _newPrefabHeight = height;
        }

        _newPrefabWidth = Math.Clamp(_newPrefabWidth, 1, 200);
        _newPrefabHeight = Math.Clamp(_newPrefabHeight, 1, 200);

        int versionIndex = GetPrefabVersionIndex(_newPrefabVersion);
        if (ImGui.Combo("Version", ref versionIndex, PrefabVersionComboItems))
        {
            _newPrefabVersion = PrefabVersions[Math.Clamp(versionIndex, 0, PrefabVersions.Length - 1)];
        }

        ImGui.Separator();
        if (ImGui.Button("创建", new Vector2(120, 0)))
        {
            CreateNewInMemoryDocument("New Prefab", true, _newPrefabWidth, _newPrefabHeight, (uint)_newPrefabVersion);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void BuildOpenByPathPopup()
    {
        if (_requestOpenByPathPopup)
        {
            _requestOpenByPathPopup = false;
            ImGui.OpenPopup("打开地图路径");
            if (string.IsNullOrWhiteSpace(_openByPathText))
            {
                _openByPathText = _state.MapPath;
            }
        }

        bool open = true;
        if (ImGui.BeginPopupModal("打开地图路径", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("支持：.nmp / .mmp / .nmpo / .nmpoN");

            float pathInputWidth = Math.Clamp(ImGui.GetMainViewport().WorkSize.X * 0.55f, 240.0f, 520.0f);
            ImGui.SetNextItemWidth(pathInputWidth);
            ImGui.InputText("##open_map_path_input", ref _openByPathText, 4096);
            ImGui.SameLine();
            if (ImGui.Button("路径##open_map_path_browse"))
            {
                _pendingFileDialogAction = PendingFileDialogAction.OpenMapByPath;
                string startDir = SuggestStartDirectory(_openByPathText, _state.MapBrowserRootDirectory);
                _fileDialog.Open(SimpleFileDialogMode.OpenFile, "选择地图/Prefab 文件", startDir);
            }

            if (ImGui.Button("打开"))
            {
                _pendingOpenPath = _openByPathText.Trim();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void BuildOpenInContentEditorPopup()
    {
        if (_requestOpenInContentEditorPopup)
        {
            _requestOpenInContentEditorPopup = false;
            ImGui.OpenPopup("在 ContentEditor 中打开");
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("在 ContentEditor 中打开", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped("请输入要在 ContentEditor 中打开的资源路径（.sgl/.tex 或 wpfPath::/entry）：");

        float pathInputWidth = Math.Clamp(ImGui.GetMainViewport().WorkSize.X * 0.55f, 240.0f, 520.0f);
        ImGui.SetNextItemWidth(pathInputWidth);
        ImGui.InputText("##open_in_content_editor_path_input", ref _openInContentEditorPathText, 4096);
        ImGui.SameLine();
        if (ImGui.Button("路径##open_in_content_editor_path_browse"))
        {
            _pendingFileDialogAction = PendingFileDialogAction.OpenAssetInContentEditor;
            string startDir = SuggestStartDirectory(_openInContentEditorPathText, Environment.CurrentDirectory);
            _fileDialog.Open(SimpleFileDialogMode.OpenFile, "选择资源文件（.sgl/.tex）", startDir);
        }

        int imageIndex = _openInContentEditorImageIndex;
        ImGui.InputInt("ImageIndex（可选）", ref imageIndex);
        _openInContentEditorImageIndex = imageIndex;

        bool canSend = !string.IsNullOrWhiteSpace(_openInContentEditorPathText);
        if (!canSend)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("发送"))
        {
            string p = _openInContentEditorPathText.Trim();
            if (_editorBridge.SendOpenAsset(p, _openInContentEditorImageIndex, out string error))
            {
                _state.StatusMessage = "桥接：已发送打开资产请求。";
                ImGui.CloseCurrentPopup();
            }
            else
            {
                _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? "桥接发送失败。" : error;
            }
        }

        if (!canSend)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void BuildSaveAsPopup()
    {
        if (_requestSaveAsPopup)
        {
            _requestSaveAsPopup = false;
            ImGui.OpenPopup("另存为");
            _saveAsOverwriteConfirm = false;
            _saveAsPathText = GetSuggestedMapSaveAsPath();
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("另存为", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        if (_state.Map is null)
        {
            ImGui.TextDisabled("未加载地图。");
            if (ImGui.Button("关闭"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        if (IsActiveDocumentPrefab())
        {
            ImGui.TextDisabled("当前为 Prefab 文档，请使用 “另存为 Prefab...” 或 “另存为...” 进入 Save Prefab As。");
            if (ImGui.Button("关闭", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        string pathText = _saveAsPathText;
        float browseButtonWidth = CalcButtonWidth("路径");
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetNextItemWidth(-browseButtonWidth - spacing);
        if (ImGui.InputText("##save_as_path_input", ref pathText, 4096))
        {
            _saveAsPathText = pathText;
            _saveAsOverwriteConfirm = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("路径##save_as_path_browse"))
        {
            string currentSavePath = AppendDefaultNmpExtensionIfMissing(_saveAsPathText);
            string startDir = SuggestStartDirectory(_saveAsPathText, _state.MapPath);

            string defaultName = "map.nmp";
            try
            {
                string name = Path.GetFileName(currentSavePath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    defaultName = name;
                }
            }
            catch
            {
                // ignore
            }

            _pendingFileDialogAction = PendingFileDialogAction.SaveAsMap;
            _fileDialog.Open(SimpleFileDialogMode.SaveFile, "选择保存路径", startDir, defaultName);
        }
        ImGui.TextDisabled("若不提供扩展名，将保存为 .nmp。");

        string savePath = AppendDefaultNmpExtensionIfMissing(_saveAsPathText);

        ImGui.Separator();

        bool pathEmpty = string.IsNullOrWhiteSpace(_saveAsPathText);

        if (!_saveAsOverwriteConfirm)
        {
            if (pathEmpty)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("保存", new Vector2(120, 0)))
            {
                if (FileExistsSafe(savePath))
                {
                    _saveAsOverwriteConfirm = true;
                }
                else
                {
                    _pendingSaveAsPath = savePath;
                    ImGui.CloseCurrentPopup();
                }
            }

            if (pathEmpty)
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.TextWrapped($"地图已存在：\n{savePath}");
            ImGui.Spacing();
            ImGui.TextUnformatted("确定要覆盖吗？");
            ImGui.Separator();

            if (ImGui.Button("覆盖", new Vector2(120, 0)))
            {
                _pendingSaveAsPath = savePath;
                _saveAsOverwriteConfirm = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消##MapOverwrite", new Vector2(120, 0)))
            {
                _saveAsOverwriteConfirm = false;
            }
        }

        ImGui.EndPopup();
    }

    private void BuildSaveSelectionAsPrefabPopup()
    {
        if (_requestSaveSelectionAsPrefabPopup)
        {
            _requestSaveSelectionAsPrefabPopup = false;
            ImGui.OpenPopup("保存为 Prefab");
            _saveSelectionAsPrefabOverwriteConfirm = false;
            _saveSelectionAsPrefabError = string.Empty;

            if (Array.IndexOf(PrefabVersions, _saveSelectionAsPrefabVersion) < 0)
            {
                _saveSelectionAsPrefabVersion = PrefabVersions[^1];
            }
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("保存为 Prefab", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        if (_state.Map is null || !_state.HasSelection)
        {
            ImGui.TextDisabled("未加载地图或未选择区域。");
            if (ImGui.Button("关闭", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        MapDocument map = _state.Map;
        int x0 = Math.Min(_state.SelectionX0, _state.SelectionX1);
        int y0 = Math.Min(_state.SelectionY0, _state.SelectionY1);
        int x1 = Math.Max(_state.SelectionX0, _state.SelectionX1);
        int y1 = Math.Max(_state.SelectionY0, _state.SelectionY1);
        x0 = Math.Clamp(x0, 0, map.Width - 1);
        y0 = Math.Clamp(y0, 0, map.Height - 1);
        x1 = Math.Clamp(x1, 0, map.Width - 1);
        y1 = Math.Clamp(y1, 0, map.Height - 1);
        int selW = x1 >= x0 ? (x1 - x0 + 1) : 0;
        int selH = y1 >= y0 ? (y1 - y0 + 1) : 0;
        long cellCountLong = (long)selW * selH;
        ImGui.TextUnformatted($"选区：({x0},{y0})-({x1},{y1})  {selW} x {selH}（{cellCountLong} 格）");

        int versionIndex = GetPrefabVersionIndex(_saveSelectionAsPrefabVersion);
        if (ImGui.Combo("Version", ref versionIndex, PrefabVersionComboItems))
        {
            _saveSelectionAsPrefabVersion = PrefabVersions[Math.Clamp(versionIndex, 0, PrefabVersions.Length - 1)];
            _saveSelectionAsPrefabOverwriteConfirm = false;
            _saveSelectionAsPrefabError = string.Empty;
        }

        string nameText = _saveSelectionAsPrefabNameText;
        if (ImGui.InputText("Name", ref nameText, 256))
        {
            _saveSelectionAsPrefabNameText = nameText;
            _saveSelectionAsPrefabOverwriteConfirm = false;
            _saveSelectionAsPrefabError = string.Empty;
        }

        string sanitizedStem = SanitizePrefabFileStem(_saveSelectionAsPrefabNameText);
        string ext = BuildNmpoExtension(_saveSelectionAsPrefabVersion);
        ImGui.TextDisabled($"将保存为：prefabs/{sanitizedStem}{ext}");

        if (!string.IsNullOrWhiteSpace(_saveSelectionAsPrefabError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), _saveSelectionAsPrefabError);
        }

        bool canSave = !string.IsNullOrWhiteSpace(sanitizedStem) && cellCountLong is > 0 and <= 1_000_000;

        string prefabsDir = GetPrefabsDirectory();
        string outputPath = Path.Combine(prefabsDir, sanitizedStem + ext);

        ImGui.Separator();

        if (!_saveSelectionAsPrefabOverwriteConfirm)
        {
            if (!canSave)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("保存", new Vector2(120, 0)))
            {
                if (!TryEnsureDirectory(prefabsDir, out string dirError))
                {
                    _saveSelectionAsPrefabError = $"创建 prefabs 目录失败：{dirError}";
                }
                else if (File.Exists(outputPath))
                {
                    _saveSelectionAsPrefabOverwriteConfirm = true;
                }
                else if (TryWritePrefabFromSelection(outputPath, _saveSelectionAsPrefabVersion, out string saveError))
                {
                    PersistFolderMetaForFile(outputPath, sanitizedStem, GetSelectedDataPathDisplayName(), "保存 Prefab");
                    _state.StatusMessage = $"已保存 Prefab：{outputPath}";
                    _console.Append(MapEditorConsoleLogLevel.Info, "Prefab", $"已保存 Prefab：{outputPath}");
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _saveSelectionAsPrefabError = string.IsNullOrWhiteSpace(saveError) ? "保存 Prefab 失败。" : saveError;
                }
            }

            if (!canSave)
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.TextWrapped($"Prefab 已存在：{outputPath}");
            ImGui.TextUnformatted("是否覆盖？");
            if (ImGui.Button("覆盖", new Vector2(120, 0)))
            {
                if (!TryEnsureDirectory(prefabsDir, out string dirError))
                {
                    _saveSelectionAsPrefabError = $"创建 prefabs 目录失败：{dirError}";
                }
                else if (TryWritePrefabFromSelection(outputPath, _saveSelectionAsPrefabVersion, out string saveError))
                {
                    PersistFolderMetaForFile(outputPath, sanitizedStem, GetSelectedDataPathDisplayName(), "覆盖 Prefab");
                    _state.StatusMessage = $"已覆盖 Prefab：{outputPath}";
                    _console.Append(MapEditorConsoleLogLevel.Warning, "Prefab", $"已覆盖 Prefab：{outputPath}");
                    _saveSelectionAsPrefabOverwriteConfirm = false;
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _saveSelectionAsPrefabError = string.IsNullOrWhiteSpace(saveError) ? "保存 Prefab 失败。" : saveError;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("取消##prefab_overwrite_cancel", new Vector2(120, 0)))
            {
                _saveSelectionAsPrefabOverwriteConfirm = false;
            }
        }

        ImGui.EndPopup();
    }

    private void BuildCopyPrefabPopup()
    {
        if (_requestCopyPrefabPopup)
        {
            _requestCopyPrefabPopup = false;
            ImGui.OpenPopup("复制 Prefab");
            _copyPrefabOverwriteConfirm = false;
            _copyPrefabError = string.Empty;
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("复制 Prefab", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        string sourcePath = _copyPrefabSourcePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || !IsPrefabPath(sourcePath))
        {
            ImGui.TextDisabled("未选择有效的 Prefab 文件。请先在文件浏览器中选中一个 Prefab（.nmpo/.nmpoN）。");
            if (ImGui.Button("关闭", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        string sourceFileName = string.Empty;
        try
        {
            sourceFileName = Path.GetFileName(sourcePath);
        }
        catch
        {
            sourceFileName = sourcePath;
        }

        ImGui.TextDisabled($"源：{sourceFileName}");

        string nameText = _copyPrefabNameText;
        if (ImGui.InputText("Name", ref nameText, 256))
        {
            _copyPrefabNameText = nameText;
            _copyPrefabOverwriteConfirm = false;
            _copyPrefabError = string.Empty;
        }

        string sanitizedStem = SanitizePrefabFileStem(_copyPrefabNameText);

        string extension = string.Empty;
        try
        {
            extension = Path.GetExtension(sourcePath);
        }
        catch
        {
            extension = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            if (NmpCodec.TryReadMapInfo(sourcePath, out NmpMapInfo info, out _))
            {
                extension = BuildNmpoExtension((int)info.Version);
            }
        }

        string dir = string.Empty;
        try
        {
            dir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        }
        catch
        {
            dir = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Environment.CurrentDirectory;
        }

        string outputPath = Path.Combine(dir, sanitizedStem + extension);
        ImGui.TextDisabled($"将生成：{Path.GetFileName(outputPath)}");

        if (!string.IsNullOrWhiteSpace(_copyPrefabError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), _copyPrefabError);
        }

        bool canCopy = !string.IsNullOrWhiteSpace(sanitizedStem);

        ImGui.Separator();

        if (!_copyPrefabOverwriteConfirm)
        {
            if (!canCopy)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("复制", new Vector2(120, 0)))
            {
                if (!TryEnsureDirectory(dir, out string dirError))
                {
                    _copyPrefabError = $"创建输出目录失败：{dirError}";
                }
                else if (File.Exists(outputPath))
                {
                    _copyPrefabOverwriteConfirm = true;
                }
                else if (TryCopyPrefabFile(sourcePath, outputPath, out string copyError))
                {
                    PersistFolderMetaForFile(outputPath, sanitizedStem, GetPreferredDataFolderNameForFile(sourcePath), "复制 Prefab");
                    _state.StatusMessage = $"已复制 Prefab：{outputPath}";
                    _console.Append(MapEditorConsoleLogLevel.Info, "Prefab", $"已复制 Prefab：{sourcePath} -> {outputPath}");
                    _selectedBrowserPath = outputPath;
                    _pendingOpenPath = outputPath;
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _copyPrefabError = string.IsNullOrWhiteSpace(copyError) ? "复制 Prefab 失败。" : copyError;
                }
            }

            if (!canCopy)
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.TextWrapped($"目标已存在：{outputPath}");
            ImGui.TextUnformatted("是否覆盖？");
            if (ImGui.Button("覆盖", new Vector2(120, 0)))
            {
                if (!TryEnsureDirectory(dir, out string dirError))
                {
                    _copyPrefabError = $"创建输出目录失败：{dirError}";
                }
                else if (TryCopyPrefabFile(sourcePath, outputPath, out string copyError))
                {
                    PersistFolderMetaForFile(outputPath, sanitizedStem, GetPreferredDataFolderNameForFile(sourcePath), "覆盖 Prefab（Copy）");
                    _state.StatusMessage = $"已覆盖 Prefab：{outputPath}";
                    _console.Append(MapEditorConsoleLogLevel.Warning, "Prefab", $"已覆盖 Prefab：{sourcePath} -> {outputPath}");
                    _selectedBrowserPath = outputPath;
                    _pendingOpenPath = outputPath;
                    _copyPrefabOverwriteConfirm = false;
                    ImGui.CloseCurrentPopup();
                }
                else
                {
                    _copyPrefabError = string.IsNullOrWhiteSpace(copyError) ? "复制 Prefab 失败。" : copyError;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("取消##copy_prefab_overwrite_cancel", new Vector2(120, 0)))
            {
                _copyPrefabOverwriteConfirm = false;
            }
        }

        ImGui.EndPopup();
    }

    private void BuildSavePrefabAsPopup()
    {
        if (_requestSavePrefabAsPopup)
        {
            _requestSavePrefabAsPopup = false;
            ImGui.OpenPopup("Save Prefab As");
            _savePrefabAsOverwriteConfirm = false;
            _savePrefabAsError = string.Empty;
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("Save Prefab As", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        if (_state.Map is null || !IsActiveDocumentPrefab())
        {
            ImGui.TextDisabled("当前未打开 Prefab 文档（.nmpo/.nmpoN）。");
            if (ImGui.Button("关闭", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        int version = (int)_state.Map.Version;
        string ext = BuildNmpoExtension(version);

        string nameText = _savePrefabAsNameText;
        if (ImGui.InputText("Name", ref nameText, 256))
        {
            _savePrefabAsNameText = nameText;
            _savePrefabAsOverwriteConfirm = false;
            _savePrefabAsError = string.Empty;
        }

        string sanitizedStem = SanitizePrefabFileStem(_savePrefabAsNameText);
        string prefabsDir = GetPrefabsDirectory();
        string outputPath = Path.Combine(prefabsDir, sanitizedStem + ext);
        ImGui.TextDisabled($"将保存为：prefabs/{sanitizedStem}{ext}");

        if (!string.IsNullOrWhiteSpace(_savePrefabAsError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), _savePrefabAsError);
        }

        bool canSave = !string.IsNullOrWhiteSpace(sanitizedStem);

        ImGui.Separator();

        if (!_savePrefabAsOverwriteConfirm)
        {
            if (!canSave)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("保存", new Vector2(120, 0)))
            {
                if (!TryEnsureDirectory(prefabsDir, out string dirError))
                {
                    _savePrefabAsError = $"创建 prefabs 目录失败：{dirError}";
                }
                else if (File.Exists(outputPath))
                {
                    _savePrefabAsOverwriteConfirm = true;
                }
                else
                {
                    MapEditorDocument? activeDoc = GetActiveDocument();
                    if (activeDoc is not null)
                    {
                        activeDoc.DisplayName = sanitizedStem;
                        activeDoc.PreferredDataPathEntryName = GetSelectedDataPathDisplayName();
                    }

                    if (TrySaveActiveDocumentToPath(outputPath, updateDocumentIdentity: true))
                    {
                        ImGui.CloseCurrentPopup();
                    }
                    else
                    {
                        _savePrefabAsError = _state.StatusMessage;
                    }
                }
            }

            if (!canSave)
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        }
        else
        {
            ImGui.TextWrapped($"Prefab 已存在：{outputPath}");
            ImGui.TextUnformatted("是否覆盖？");
            if (ImGui.Button("覆盖", new Vector2(120, 0)))
            {
                if (!TryEnsureDirectory(prefabsDir, out string dirError))
                {
                    _savePrefabAsError = $"创建 prefabs 目录失败：{dirError}";
                }
                else
                {
                    MapEditorDocument? activeDoc = GetActiveDocument();
                    if (activeDoc is not null)
                    {
                        activeDoc.DisplayName = sanitizedStem;
                        activeDoc.PreferredDataPathEntryName = GetSelectedDataPathDisplayName();
                    }

                    if (TrySaveActiveDocumentToPath(outputPath, updateDocumentIdentity: true))
                    {
                        _savePrefabAsOverwriteConfirm = false;
                        ImGui.CloseCurrentPopup();
                    }
                    else
                    {
                        _savePrefabAsError = _state.StatusMessage;
                    }
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("取消##save_prefab_as_overwrite_cancel", new Vector2(120, 0)))
            {
                _savePrefabAsOverwriteConfirm = false;
            }
        }

        ImGui.EndPopup();
    }

    private void BuildMinimapExportPopup()
    {
        if (_state.ShowMinimapExportPopup)
        {
            _state.ShowMinimapExportPopup = false;
            ImGui.OpenPopup("导出小地图 PNG");

            if (_state.Map is not null && string.IsNullOrWhiteSpace(_state.MinimapExportPath))
            {
                _state.MinimapExportPath = SuggestDefaultMinimapExportPath(_state.Map, _state.MinimapScale);
            }
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("导出小地图 PNG", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        if (_state.Map is null)
        {
            ImGui.TextDisabled("未加载地图。");
            if (ImGui.Button("关闭"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted("说明：可导出“占位色”或“真实贴图”小地图。真实贴图当前仅 frame=0（不含动画）。");
        ImGui.TextDisabled("已支持：海岸遮罩合成（TEX mask + .msk 回退含 WPF）、nearGround/underObject/overObject、夜晚光照叠加/灯光 sprite、ColorAdj 色调、NEARGROUND/objectHeight 偏移、BORDER 抑制、objects18/19 亮度转 Alpha、SceneLightMaskCfg。");
        ImGui.TextDisabled("实验性：动态场景覆盖（DynScene/挂接 effects），仅在“真实贴图导出”时启用；缺失或解析失败会给出警告并忽略。");
        ImGui.Separator();

        string outputPath = _state.MinimapExportPath;
        if (ImGui.InputText("输出路径", ref outputPath, 4096))
        {
            _state.MinimapExportPath = outputPath;
        }

        if (ImGui.Button("重置为默认路径"))
        {
            _state.MinimapExportPath = SuggestDefaultMinimapExportPath(_state.Map, _state.MinimapScale);
        }

        ImGui.Separator();

        int[] scales = { 1, 2, 4, 8, 16, 32 };
        int scaleIndex = 2;
        for (int i = 0; i < scales.Length; i++)
        {
            if (scales[i] == _state.MinimapScale)
            {
                scaleIndex = i;
                break;
            }
        }

        if (ImGui.Combo("缩放除数（ScaleDivisor）", ref scaleIndex, "1\0 2\0 4\0 8\0 16\0 32\0"))
        {
            _state.MinimapScale = scales[Math.Clamp(scaleIndex, 0, scales.Length - 1)];
        }

        bool includeScaleTag = _state.MinimapBatchIncludeScaleTag;
        if (ImGui.Checkbox("文件名包含 Scale 标签（_s4）", ref includeScaleTag))
        {
            _state.MinimapBatchIncludeScaleTag = includeScaleTag;
        }

        bool includeBack = _state.MinimapIncludeBack;
        bool includeMiddle = _state.MinimapIncludeMiddle;
        bool includeFloor = _state.MinimapIncludeFloor;
        bool includeUnderFront = _state.MinimapIncludeUnderFront;
        bool includeFront = _state.MinimapIncludeFront;
        bool includeOverFront = _state.MinimapIncludeOverFront;
        bool separateFiles = _state.MinimapSeparateLayerFiles;
        bool useTextures = _state.MinimapUseTextures;
        bool canUseTextures = _textureIndex.IsReady;

        if (!canUseTextures)
        {
            useTextures = false;
            _state.MinimapUseTextures = false;
        }

        if (!canUseTextures)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("使用真实贴图（需要先扫描贴图库）", ref useTextures))
        {
            _state.MinimapUseTextures = useTextures;
        }

        if (!canUseTextures)
        {
            ImGui.EndDisabled();
            ImGui.TextDisabled("（贴图库未就绪：请先在“地图信息”窗口扫描 SGL 贴图库）");
        }

        if (ImGui.Checkbox("包含 Back (SmTiles)", ref includeBack))
        {
            _state.MinimapIncludeBack = includeBack;
        }

        if (ImGui.Checkbox("包含 Middle (Tiles)", ref includeMiddle))
        {
            _state.MinimapIncludeMiddle = includeMiddle;
        }

        if (ImGui.Checkbox("包含 Floor (nearGround)", ref includeFloor))
        {
            _state.MinimapIncludeFloor = includeFloor;
        }

        if (ImGui.Checkbox("包含 UnderFront (underObject)", ref includeUnderFront))
        {
            _state.MinimapIncludeUnderFront = includeUnderFront;
        }

        if (ImGui.Checkbox("包含 Front (Object)", ref includeFront))
        {
            _state.MinimapIncludeFront = includeFront;
        }

        if (ImGui.Checkbox("包含 OverFront (overObject)", ref includeOverFront))
        {
            _state.MinimapIncludeOverFront = includeOverFront;
        }

        if (ImGui.Checkbox("分层分别导出文件", ref separateFiles))
        {
            _state.MinimapSeparateLayerFiles = separateFiles;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("客户端对齐设置（可选）：");
        ImGui.TextDisabled("说明：以下选项主要影响“真实贴图”导出；占位色导出仅会受 BORDER 抑制影响。");

        bool suppressBorder = _state.MinimapSuppressBorderCells;
        if (ImGui.Checkbox("抑制 BORDER 格（flags 0x04）", ref suppressBorder))
        {
            _state.MinimapSuppressBorderCells = suppressBorder;
        }

        bool applyTints = _state.MinimapApplyCellTints;
        if (ImGui.Checkbox("应用格子色调（ColorAdj*）", ref applyTints))
        {
            _state.MinimapApplyCellTints = applyTints;
        }

        if (_state.MinimapApplyCellTints)
        {
            float strength = _state.MinimapTintStrength;
            if (ImGui.SliderFloat("色调强度", ref strength, 0.0f, 1.0f, "%.2f"))
            {
                _state.MinimapTintStrength = strength;
            }
        }

        bool applyHeightFlag = _state.MinimapApplyCellHeightFlag;
        if (ImGui.Checkbox("应用 NEARGROUND(0x40) 高度偏移", ref applyHeightFlag))
        {
            _state.MinimapApplyCellHeightFlag = applyHeightFlag;
        }

        if (_state.MinimapApplyCellHeightFlag)
        {
            float lift = _state.MinimapCellHeightFlagOffset;
            if (ImGui.SliderFloat("高度标记提升（未缩放 px）", ref lift, 0.0f, 64.0f, "%.2f"))
            {
                _state.MinimapCellHeightFlagOffset = lift;
            }
        }

        bool applyObjectHeight = _state.MinimapApplyObjectHeight;
        if (ImGui.Checkbox("应用 objectHeight 偏移", ref applyObjectHeight))
        {
            _state.MinimapApplyObjectHeight = applyObjectHeight;
        }

        if (_state.MinimapApplyObjectHeight)
        {
            float scale = _state.MinimapObjectHeightScale;
            if (ImGui.SliderFloat("物件高度(objectHeight) 缩放（未缩放 px/unit）", ref scale, 0.0f, 8.0f, "%.2f"))
            {
                _state.MinimapObjectHeightScale = scale;
            }
        }

        bool applyLumAlpha = _state.MinimapApplyLuminanceToAlpha;
        if (ImGui.Checkbox("objects18/19：亮度转透明度(Alpha)（包 46/47）", ref applyLumAlpha))
        {
            _state.MinimapApplyLuminanceToAlpha = applyLumAlpha;
        }

        bool applyLightingOverlay = _state.MinimapApplyLightingOverlay;
        if (ImGui.Checkbox("应用夜晚光照叠加", ref applyLightingOverlay))
        {
            _state.MinimapApplyLightingOverlay = applyLightingOverlay;
        }

        DrawLightingSettingsEditor("minimap", _state.MinimapLighting);

        if (_state.MinimapApplyLightingOverlay)
        {
            int maxAlpha = _state.MinimapLightingOverlayMaxAlpha;
            if (ImGui.SliderInt("叠加最大 Alpha", ref maxAlpha, 0, 255))
            {
                _state.MinimapLightingOverlayMaxAlpha = maxAlpha;
            }
        }

        bool includeLights = _state.MinimapIncludeLightSprites;
        if (ImGui.Checkbox("导出灯光 sprite（Objects10 overObject）", ref includeLights))
        {
            _state.MinimapIncludeLightSprites = includeLights;
        }

        bool dynamicScene = _state.MinimapIncludeDynamicScene;
        if (ImGui.Checkbox("动态覆盖（DynScene，实验性）", ref dynamicScene))
        {
            _state.MinimapIncludeDynamicScene = dynamicScene;
        }

        bool attachedEffects = _state.MinimapIncludeAttachedEffects;
        if (ImGui.Checkbox("挂接 effects（实验性）", ref attachedEffects))
        {
            _state.MinimapIncludeAttachedEffects = attachedEffects;
        }
        ImGui.TextDisabled("提示：仅在“使用真实贴图”时生效；数据文件需可被解析（文本/JSON，或二进制+sidecar layout）。缺失/解析失败会提示并忽略，不影响基础导出。");

        if (_state.MinimapIncludeDynamicScene || _state.MinimapIncludeAttachedEffects)
        {
            if (_state.MinimapIncludeDynamicScene)
            {
                string overlayMapId = _state.MinimapOverlayMapIdOverride;
                if (ImGui.InputText("DynScene MapId 覆盖（可选）", ref overlayMapId, 128))
                {
                    _state.MinimapOverlayMapIdOverride = overlayMapId;
                }
                ImGui.TextDisabled("留空则按地图文件名推导；用于验证 DynScene/<MAPID>.dat 的真实关联方式。");

                string overlayLayout = _state.MinimapOverlayLayoutPath;
                if (ImGui.InputText("DynScene layout（可选）", ref overlayLayout, 4096))
                {
                    _state.MinimapOverlayLayoutPath = overlayLayout;
                }
                ImGui.TextDisabled("留空则尝试 <path>.layout.json sidecar 或自动解析；用于二进制+layout 且不便写 sidecar 的情况。");
            }

            if (_state.MinimapIncludeAttachedEffects)
            {
                string effectsMapId = _state.MinimapAttachedEffectsMapIdOverride;
                if (ImGui.InputText("挂接 effects MapId 覆盖（可选）", ref effectsMapId, 128))
                {
                    _state.MinimapAttachedEffectsMapIdOverride = effectsMapId;
                }
                ImGui.TextDisabled("留空则按地图文件名推导；用于验证 AttachedEffects/<MAPID>.dat 的真实关联方式。");

                string effectsLayout = _state.MinimapAttachedEffectsLayoutPath;
                if (ImGui.InputText("挂接 effects layout（可选）", ref effectsLayout, 4096))
                {
                    _state.MinimapAttachedEffectsLayoutPath = effectsLayout;
                }
                ImGui.TextDisabled("留空则尝试 <path>.layout.json sidecar 或自动解析；用于二进制+layout 且不便写 sidecar 的情况。");
            }

            const long mib = 1024L * 1024L;
            long currentBytes = Math.Clamp(_state.MinimapOverlayMaxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024);
            int maxMiB = currentBytes <= 0 ? 0 : (int)Math.Clamp((currentBytes + mib - 1) / mib, 0, 2048);
            if (ImGui.InputInt("overlay 解压上限（MiB；0=不限制）", ref maxMiB))
            {
                maxMiB = Math.Clamp(maxMiB, 0, 2048);
                _state.MinimapOverlayMaxDecompressedBytes = (long)maxMiB * mib;
            }
            ImGui.TextDisabled("默认 16MiB；若样本为 gzip/zlib 且解压后较大，可适当调大。");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("裁剪范围（可选）：");

        int scaleDivisor = Math.Clamp(_state.MinimapScale, 1, 32);
        int cellPxW = 64 / scaleDivisor;
        int cellPxH = 32 / scaleDivisor;
        int fullW = _state.Map.Width * cellPxW;
        int fullH = _state.Map.Height * cellPxH;
        ImGui.TextDisabled($"未裁剪输出尺寸：{fullW}x{fullH} px（cell={cellPxW}x{cellPxH} px）");

        int cropModeIndex = _state.MinimapCropMode switch
        {
            MinimapCropMode.None => 0,
            MinimapCropMode.CellRect => 1,
            MinimapCropMode.PixelRect => 2,
            MinimapCropMode.AutoNonEmptyCells => 3,
            _ => 0,
        };

        if (ImGui.Combo("裁剪模式", ref cropModeIndex, "无\0按格子矩形\0按像素矩形\0自动（非空包围盒）\0"))
        {
            _state.MinimapCropMode = cropModeIndex switch
            {
                1 => MinimapCropMode.CellRect,
                2 => MinimapCropMode.PixelRect,
                3 => MinimapCropMode.AutoNonEmptyCells,
                _ => MinimapCropMode.None,
            };
        }

        if (_state.MinimapCropMode == MinimapCropMode.CellRect)
        {
            int cx = _state.MinimapCropCellX;
            int cy = _state.MinimapCropCellY;
            int cw = _state.MinimapCropCellWidth;
            int ch = _state.MinimapCropCellHeight;

            ImGui.InputInt("裁剪格子 X##CropCellX", ref cx);
            ImGui.InputInt("裁剪格子 Y##CropCellY", ref cy);
            ImGui.InputInt("裁剪格子 宽##CropCellWidth", ref cw);
            ImGui.InputInt("裁剪格子 高##CropCellHeight", ref ch);

            _state.MinimapCropCellX = Math.Max(0, cx);
            _state.MinimapCropCellY = Math.Max(0, cy);
            _state.MinimapCropCellWidth = Math.Max(1, cw);
            _state.MinimapCropCellHeight = Math.Max(1, ch);
            ImGui.TextDisabled("说明：按格子坐标裁剪（0-based）。超出地图范围会自动裁剪到边界。");
        }
        else if (_state.MinimapCropMode == MinimapCropMode.PixelRect)
        {
            if (_state.MinimapCropPixelWidth <= 0 || _state.MinimapCropPixelHeight <= 0)
            {
                _state.MinimapCropPixelWidth = fullW;
                _state.MinimapCropPixelHeight = fullH;
            }

            int px = _state.MinimapCropPixelX;
            int py = _state.MinimapCropPixelY;
            int pw = _state.MinimapCropPixelWidth;
            int ph = _state.MinimapCropPixelHeight;

            ImGui.InputInt("裁剪像素 X##CropPixelX", ref px);
            ImGui.InputInt("裁剪像素 Y##CropPixelY", ref py);
            ImGui.InputInt("裁剪像素 宽##CropPixelWidth", ref pw);
            ImGui.InputInt("裁剪像素 高##CropPixelHeight", ref ph);

            _state.MinimapCropPixelX = Math.Max(0, px);
            _state.MinimapCropPixelY = Math.Max(0, py);
            _state.MinimapCropPixelWidth = Math.Max(1, pw);
            _state.MinimapCropPixelHeight = Math.Max(1, ph);
            ImGui.TextDisabled("说明：按导出图像像素坐标裁剪（0-based，基于当前缩放除数/ScaleDivisor 的输出尺寸）。超出范围会自动裁剪到边界。");
        }
        else if (_state.MinimapCropMode == MinimapCropMode.AutoNonEmptyCells)
        {
            int pad = _state.MinimapAutoCropPaddingCells;
            ImGui.InputInt("自动裁剪边距(格)##AutoCropPaddingCells", ref pad);
            _state.MinimapAutoCropPaddingCells = Math.Max(0, pad);
            ImGui.TextDisabled("说明：按“非空 cell 的最小包围盒”自动裁剪（可选 padding=额外扩展的格子边距）。若未找到非空 cell，则回退为不裁剪。");
        }

        if (ImGui.Button("导出"))
        {
            _console.Append(MapEditorConsoleLogLevel.Info, "Export", $"开始导出小地图：{_state.MinimapExportPath}（useTextures={_state.MinimapUseTextures} scale={_state.MinimapScale}）");
            float nightFactor = MapLighting.Resolve(_state.MinimapLighting).NightFactor;
            var opts = new MinimapExportOptions
            {
                ScaleDivisor = _state.MinimapScale,
                IncludeBack = _state.MinimapIncludeBack,
                IncludeMiddle = _state.MinimapIncludeMiddle,
                IncludeFloor = _state.MinimapIncludeFloor,
                IncludeUnderFront = _state.MinimapIncludeUnderFront,
                IncludeFront = _state.MinimapIncludeFront,
                IncludeOverFront = _state.MinimapIncludeOverFront,
                SeparateLayerFiles = _state.MinimapSeparateLayerFiles,
                SuppressBorderCells = _state.MinimapSuppressBorderCells,
                ApplyCellTints = _state.MinimapApplyCellTints,
                TintStrength = _state.MinimapTintStrength,
                ApplyCellHeightFlag = _state.MinimapApplyCellHeightFlag,
                CellHeightFlagOffset = _state.MinimapCellHeightFlagOffset,
                ApplyObjectHeight = _state.MinimapApplyObjectHeight,
                ObjectHeightScale = _state.MinimapObjectHeightScale,
                ApplyLuminanceToAlpha = _state.MinimapApplyLuminanceToAlpha,
                ApplyLightingOverlay = _state.MinimapApplyLightingOverlay,
                NightFactor = nightFactor,
                LightingOverlayMaxAlpha = _state.MinimapLightingOverlayMaxAlpha,
                IncludeLightSprites = _state.MinimapIncludeLightSprites,
                IncludeDynamicScene = _state.MinimapIncludeDynamicScene,
                IncludeAttachedEffects = _state.MinimapIncludeAttachedEffects,
                DynamicOverlayMapIdOverride = _state.MinimapOverlayMapIdOverride,
                AttachedEffectsMapIdOverride = _state.MinimapAttachedEffectsMapIdOverride,
                DynamicOverlayLayoutPath = _state.MinimapOverlayLayoutPath,
                AttachedEffectsLayoutPath = _state.MinimapAttachedEffectsLayoutPath,
                DynamicOverlayMaxDecompressedBytes = Math.Clamp(_state.MinimapOverlayMaxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024),
                CropMode = _state.MinimapCropMode,
                CropCellX = _state.MinimapCropCellX,
                CropCellY = _state.MinimapCropCellY,
                CropCellWidth = _state.MinimapCropCellWidth,
                CropCellHeight = _state.MinimapCropCellHeight,
                CropPixelX = _state.MinimapCropPixelX,
                CropPixelY = _state.MinimapCropPixelY,
                CropPixelWidth = _state.MinimapCropPixelWidth,
                CropPixelHeight = _state.MinimapCropPixelHeight,
                AutoCropPaddingCells = _state.MinimapAutoCropPaddingCells,
            };

            bool ok;
            string exportError;
            string[] files;
            MinimapExportDiagnostics diag = default;

            if (_state.MinimapUseTextures)
            {
                ok = MinimapExporter.TryExportTexturedPng(_state.Map, _textureIndex, opts, _state.MinimapExportPath, out files, out diag, out exportError);
            }
            else
            {
                ok = MinimapExporter.TryExportPlaceholderPng(_state.Map, opts, _state.MinimapExportPath, out files, out exportError);
            }

            if (ok)
            {
                string msg = files.Length <= 1
                    ? $"小地图导出完成：{files[0]}"
                    : $"小地图导出完成：{files.Length} 个文件";

                string warn = _state.MinimapUseTextures ? diag.BuildWarningSummary() : string.Empty;
                if (!_state.MinimapUseTextures && (opts.IncludeDynamicScene || opts.IncludeAttachedEffects))
                {
                    warn = "动态覆盖/挂接 effects 仅在“真实贴图导出”时支持；占位色导出已忽略。";
                }

                if (!string.IsNullOrWhiteSpace(warn))
                {
                    msg = $"{msg}；警告：{warn}";
                }

                _state.StatusMessage = msg;
                _console.Append(!string.IsNullOrWhiteSpace(warn) ? MapEditorConsoleLogLevel.Warning : MapEditorConsoleLogLevel.Info,
                    "Export",
                    !string.IsNullOrWhiteSpace(warn)
                        ? $"小地图导出完成（含警告）：{string.Join(", ", files)} | {warn}"
                        : $"小地图导出完成：{string.Join(", ", files)}");
                ImGui.CloseCurrentPopup();
            }
            else
            {
                _state.StatusMessage = exportError;
                _console.Append(MapEditorConsoleLogLevel.Error, "Export", string.IsNullOrWhiteSpace(exportError)
                    ? "小地图导出失败。"
                    : $"小地图导出失败：{exportError}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void BuildMinimapBatchExportPopup()
    {
        if (_state.ShowMinimapBatchExportPopup)
        {
            _state.ShowMinimapBatchExportPopup = false;
            ImGui.OpenPopup("批量导出小地图 PNG");

            if (string.IsNullOrWhiteSpace(_state.MinimapBatchInputDirectory))
            {
                _state.MinimapBatchInputDirectory = SuggestDefaultMinimapBatchInputDirectory();
            }

            if (string.IsNullOrWhiteSpace(_state.MinimapBatchOutputDirectory))
            {
                _state.MinimapBatchOutputDirectory = SuggestDefaultMinimapBatchOutputDirectory(_state.MinimapBatchInputDirectory);
            }
        }

        bool open = true;
        if (!ImGui.BeginPopupModal("批量导出小地图 PNG", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        MinimapBatchExportSnapshot snapshot = _minimapBatchExportProgress.GetSnapshot();
        bool running = snapshot.Running;

        ImGui.TextUnformatted("说明：扫描输入目录下的 .nmp/.mmp，并逐个导出 PNG。");
        ImGui.TextDisabled(_state.MinimapBatchIncludeScaleTag
            ? "命名规则：<map>_minimap_s<缩放除数>.png（分层导出会追加 _back/_middle/_floor/_underfront/_front/_overfront）。"
            : "命名规则：<map>_minimap.png（分层导出会追加 _back/_middle/_floor/_underfront/_front/_overfront）。");
        ImGui.Separator();

        if (running)
        {
            ImGui.BeginDisabled();
        }

        string inputDir = _state.MinimapBatchInputDirectory;
        if (ImGui.InputText("输入目录", ref inputDir, 4096))
        {
            _state.MinimapBatchInputDirectory = inputDir;
        }

        string outputDir = _state.MinimapBatchOutputDirectory;
        if (ImGui.InputText("输出目录", ref outputDir, 4096))
        {
            _state.MinimapBatchOutputDirectory = outputDir;
        }

        if (ImGui.Button("从当前地图目录推断"))
        {
            string suggested = SuggestDefaultMinimapBatchInputDirectory();
            if (!string.IsNullOrWhiteSpace(suggested))
            {
                _state.MinimapBatchInputDirectory = suggested;

                if (string.IsNullOrWhiteSpace(_state.MinimapBatchOutputDirectory))
                {
                    _state.MinimapBatchOutputDirectory = SuggestDefaultMinimapBatchOutputDirectory(suggested);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("输出=输入/_minimap"))
        {
            string dir = _state.MinimapBatchInputDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                _state.MinimapBatchOutputDirectory = SuggestDefaultMinimapBatchOutputDirectory(dir);
            }
        }

        bool recursive = _state.MinimapBatchRecursive;
        if (ImGui.Checkbox("递归扫描子目录", ref recursive))
        {
            _state.MinimapBatchRecursive = recursive;
        }

        bool overwrite = _state.MinimapBatchOverwrite;
        if (ImGui.Checkbox("覆盖已存在文件", ref overwrite))
        {
            _state.MinimapBatchOverwrite = overwrite;
        }

        ImGui.Separator();

        int[] scales = { 1, 2, 4, 8, 16, 32 };
        int scaleIndex = 2;
        for (int i = 0; i < scales.Length; i++)
        {
            if (scales[i] == _state.MinimapScale)
            {
                scaleIndex = i;
                break;
            }
        }

        if (ImGui.Combo("缩放除数（ScaleDivisor）", ref scaleIndex, "1\0 2\0 4\0 8\0 16\0 32\0"))
        {
            _state.MinimapScale = scales[Math.Clamp(scaleIndex, 0, scales.Length - 1)];
        }

        bool includeBack = _state.MinimapIncludeBack;
        bool includeMiddle = _state.MinimapIncludeMiddle;
        bool includeFloor = _state.MinimapIncludeFloor;
        bool includeUnderFront = _state.MinimapIncludeUnderFront;
        bool includeFront = _state.MinimapIncludeFront;
        bool includeOverFront = _state.MinimapIncludeOverFront;
        bool separateFiles = _state.MinimapSeparateLayerFiles;

        bool useTextures = _state.MinimapUseTextures;
        bool canUseTextures = _textureIndex.IsReady;
        if (!canUseTextures)
        {
            useTextures = false;
            _state.MinimapUseTextures = false;
        }

        if (!canUseTextures)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("使用真实贴图（需要先扫描贴图库）", ref useTextures))
        {
            _state.MinimapUseTextures = useTextures;
        }

        if (!canUseTextures)
        {
            ImGui.EndDisabled();
            ImGui.TextDisabled("（贴图库未就绪：请先在“地图信息”窗口扫描 SGL 贴图库）");
        }

        if (ImGui.Checkbox("包含 Back (SmTiles)", ref includeBack))
        {
            _state.MinimapIncludeBack = includeBack;
        }

        if (ImGui.Checkbox("包含 Middle (Tiles)", ref includeMiddle))
        {
            _state.MinimapIncludeMiddle = includeMiddle;
        }

        if (ImGui.Checkbox("包含 Floor (nearGround)", ref includeFloor))
        {
            _state.MinimapIncludeFloor = includeFloor;
        }

        if (ImGui.Checkbox("包含 UnderFront (underObject)", ref includeUnderFront))
        {
            _state.MinimapIncludeUnderFront = includeUnderFront;
        }

        if (ImGui.Checkbox("包含 Front (Object)", ref includeFront))
        {
            _state.MinimapIncludeFront = includeFront;
        }

        if (ImGui.Checkbox("包含 OverFront (overObject)", ref includeOverFront))
        {
            _state.MinimapIncludeOverFront = includeOverFront;
        }

        if (ImGui.Checkbox("分层分别导出文件", ref separateFiles))
        {
            _state.MinimapSeparateLayerFiles = separateFiles;
        }

        ImGui.Separator();
        ImGui.TextUnformatted("客户端对齐设置（可选）：");
        ImGui.TextDisabled("说明：以下选项主要影响“真实贴图”导出；占位色导出仅会受 BORDER 抑制影响。");

        bool suppressBorder = _state.MinimapSuppressBorderCells;
        if (ImGui.Checkbox("抑制 BORDER 格（flags 0x04）", ref suppressBorder))
        {
            _state.MinimapSuppressBorderCells = suppressBorder;
        }

        bool applyTints = _state.MinimapApplyCellTints;
        if (ImGui.Checkbox("应用格子色调（ColorAdj*）", ref applyTints))
        {
            _state.MinimapApplyCellTints = applyTints;
        }

        if (_state.MinimapApplyCellTints)
        {
            float strength = _state.MinimapTintStrength;
            if (ImGui.SliderFloat("色调强度", ref strength, 0.0f, 1.0f, "%.2f"))
            {
                _state.MinimapTintStrength = strength;
            }
        }

        bool applyHeightFlag = _state.MinimapApplyCellHeightFlag;
        if (ImGui.Checkbox("应用 NEARGROUND(0x40) 高度偏移", ref applyHeightFlag))
        {
            _state.MinimapApplyCellHeightFlag = applyHeightFlag;
        }

        if (_state.MinimapApplyCellHeightFlag)
        {
            float lift = _state.MinimapCellHeightFlagOffset;
            if (ImGui.SliderFloat("高度标记提升（未缩放 px）", ref lift, 0.0f, 64.0f, "%.2f"))
            {
                _state.MinimapCellHeightFlagOffset = lift;
            }
        }

        bool applyObjectHeight = _state.MinimapApplyObjectHeight;
        if (ImGui.Checkbox("应用 objectHeight 偏移", ref applyObjectHeight))
        {
            _state.MinimapApplyObjectHeight = applyObjectHeight;
        }

        if (_state.MinimapApplyObjectHeight)
        {
            float scale = _state.MinimapObjectHeightScale;
            if (ImGui.SliderFloat("物件高度(objectHeight) 缩放（未缩放 px/unit）", ref scale, 0.0f, 8.0f, "%.2f"))
            {
                _state.MinimapObjectHeightScale = scale;
            }
        }

        bool applyLumAlpha = _state.MinimapApplyLuminanceToAlpha;
        if (ImGui.Checkbox("objects18/19：亮度转透明度(Alpha)（包 46/47）", ref applyLumAlpha))
        {
            _state.MinimapApplyLuminanceToAlpha = applyLumAlpha;
        }

        bool applyLightingOverlay = _state.MinimapApplyLightingOverlay;
        if (ImGui.Checkbox("应用夜晚光照叠加", ref applyLightingOverlay))
        {
            _state.MinimapApplyLightingOverlay = applyLightingOverlay;
        }

        DrawLightingSettingsEditor("minimap_batch", _state.MinimapLighting);

        if (_state.MinimapApplyLightingOverlay)
        {
            int maxAlpha = _state.MinimapLightingOverlayMaxAlpha;
            if (ImGui.SliderInt("叠加最大 Alpha", ref maxAlpha, 0, 255))
            {
                _state.MinimapLightingOverlayMaxAlpha = maxAlpha;
            }
        }

        bool includeLights = _state.MinimapIncludeLightSprites;
        if (ImGui.Checkbox("导出灯光 sprite（Objects10 overObject）", ref includeLights))
        {
            _state.MinimapIncludeLightSprites = includeLights;
        }

        bool dynamicScene = _state.MinimapIncludeDynamicScene;
        if (ImGui.Checkbox("动态覆盖（DynScene，实验性）", ref dynamicScene))
        {
            _state.MinimapIncludeDynamicScene = dynamicScene;
        }

        bool attachedEffects = _state.MinimapIncludeAttachedEffects;
        if (ImGui.Checkbox("挂接 effects（实验性）", ref attachedEffects))
        {
            _state.MinimapIncludeAttachedEffects = attachedEffects;
        }
        ImGui.TextDisabled("提示：仅在“使用真实贴图”时生效；数据文件需可被解析（文本/JSON，或二进制+sidecar layout）。缺失/解析失败会提示并忽略，不影响基础导出。");

        if (_state.MinimapIncludeDynamicScene || _state.MinimapIncludeAttachedEffects)
        {
            if (_state.MinimapIncludeDynamicScene)
            {
                string overlayMapId = _state.MinimapOverlayMapIdOverride;
                if (ImGui.InputText("DynScene MapId 覆盖（可选）", ref overlayMapId, 128))
                {
                    _state.MinimapOverlayMapIdOverride = overlayMapId;
                }
                ImGui.TextDisabled("留空则按地图文件名推导；用于验证 DynScene/<MAPID>.dat 的真实关联方式。");

                string overlayLayout = _state.MinimapOverlayLayoutPath;
                if (ImGui.InputText("DynScene layout（可选）", ref overlayLayout, 4096))
                {
                    _state.MinimapOverlayLayoutPath = overlayLayout;
                }
                ImGui.TextDisabled("留空则尝试 <path>.layout.json sidecar 或自动解析；用于二进制+layout 且不便写 sidecar 的情况。");
            }

            if (_state.MinimapIncludeAttachedEffects)
            {
                string effectsMapId = _state.MinimapAttachedEffectsMapIdOverride;
                if (ImGui.InputText("挂接 effects MapId 覆盖（可选）", ref effectsMapId, 128))
                {
                    _state.MinimapAttachedEffectsMapIdOverride = effectsMapId;
                }
                ImGui.TextDisabled("留空则按地图文件名推导；用于验证 AttachedEffects/<MAPID>.dat 的真实关联方式。");

                string effectsLayout = _state.MinimapAttachedEffectsLayoutPath;
                if (ImGui.InputText("挂接 effects layout（可选）", ref effectsLayout, 4096))
                {
                    _state.MinimapAttachedEffectsLayoutPath = effectsLayout;
                }
                ImGui.TextDisabled("留空则尝试 <path>.layout.json sidecar 或自动解析；用于二进制+layout 且不便写 sidecar 的情况。");
            }

            const long mib = 1024L * 1024L;
            long currentBytes = Math.Clamp(_state.MinimapOverlayMaxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024);
            int maxMiB = currentBytes <= 0 ? 0 : (int)Math.Clamp((currentBytes + mib - 1) / mib, 0, 2048);
            if (ImGui.InputInt("overlay 解压上限（MiB；0=不限制）", ref maxMiB))
            {
                maxMiB = Math.Clamp(maxMiB, 0, 2048);
                _state.MinimapOverlayMaxDecompressedBytes = (long)maxMiB * mib;
            }
            ImGui.TextDisabled("默认 16MiB；若样本为 gzip/zlib 且解压后较大，可适当调大。");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("裁剪范围（可选）：");
        ImGui.TextDisabled("说明：裁剪参数会应用到每张地图；像素坐标基于各地图在当前缩放除数/ScaleDivisor 下的输出尺寸。");

        int cropModeIndex = _state.MinimapCropMode switch
        {
            MinimapCropMode.None => 0,
            MinimapCropMode.CellRect => 1,
            MinimapCropMode.PixelRect => 2,
            MinimapCropMode.AutoNonEmptyCells => 3,
            _ => 0,
        };

        if (ImGui.Combo("裁剪模式", ref cropModeIndex, "无\0按格子矩形\0按像素矩形\0自动（非空包围盒）\0"))
        {
            _state.MinimapCropMode = cropModeIndex switch
            {
                1 => MinimapCropMode.CellRect,
                2 => MinimapCropMode.PixelRect,
                3 => MinimapCropMode.AutoNonEmptyCells,
                _ => MinimapCropMode.None,
            };
        }

        if (_state.MinimapCropMode == MinimapCropMode.CellRect)
        {
            int cx = _state.MinimapCropCellX;
            int cy = _state.MinimapCropCellY;
            int cw = _state.MinimapCropCellWidth;
            int ch = _state.MinimapCropCellHeight;

            ImGui.InputInt("裁剪格子 X##CropCellX", ref cx);
            ImGui.InputInt("裁剪格子 Y##CropCellY", ref cy);
            ImGui.InputInt("裁剪格子 宽##CropCellWidth", ref cw);
            ImGui.InputInt("裁剪格子 高##CropCellHeight", ref ch);

            _state.MinimapCropCellX = Math.Max(0, cx);
            _state.MinimapCropCellY = Math.Max(0, cy);
            _state.MinimapCropCellWidth = Math.Max(1, cw);
            _state.MinimapCropCellHeight = Math.Max(1, ch);
        }
        else if (_state.MinimapCropMode == MinimapCropMode.PixelRect)
        {
            if ((_state.MinimapCropPixelWidth <= 0 || _state.MinimapCropPixelHeight <= 0) && _state.Map is not null)
            {
                int scaleDivisor = Math.Clamp(_state.MinimapScale, 1, 32);
                int cellPxW = 64 / scaleDivisor;
                int cellPxH = 32 / scaleDivisor;
                _state.MinimapCropPixelWidth = _state.Map.Width * cellPxW;
                _state.MinimapCropPixelHeight = _state.Map.Height * cellPxH;
            }

            int px = _state.MinimapCropPixelX;
            int py = _state.MinimapCropPixelY;
            int pw = _state.MinimapCropPixelWidth;
            int ph = _state.MinimapCropPixelHeight;

            ImGui.InputInt("裁剪像素 X##CropPixelX", ref px);
            ImGui.InputInt("裁剪像素 Y##CropPixelY", ref py);
            ImGui.InputInt("裁剪像素 宽##CropPixelWidth", ref pw);
            ImGui.InputInt("裁剪像素 高##CropPixelHeight", ref ph);

            _state.MinimapCropPixelX = Math.Max(0, px);
            _state.MinimapCropPixelY = Math.Max(0, py);
            _state.MinimapCropPixelWidth = Math.Max(1, pw);
            _state.MinimapCropPixelHeight = Math.Max(1, ph);
        }
        else if (_state.MinimapCropMode == MinimapCropMode.AutoNonEmptyCells)
        {
            int pad = _state.MinimapAutoCropPaddingCells;
            ImGui.InputInt("自动裁剪边距(格)##AutoCropPaddingCells", ref pad);
            _state.MinimapAutoCropPaddingCells = Math.Max(0, pad);
        }

        if (running)
        {
            ImGui.EndDisabled();
        }

        ImGui.Separator();

        if (running)
        {
            float fraction = snapshot.Total > 0 ? snapshot.Done / (float)snapshot.Total : 0.0f;
            ImGui.ProgressBar(fraction, new Vector2(420, 0), $"{snapshot.Done}/{snapshot.Total}");
            ImGui.TextUnformatted($"成功={snapshot.Ok}  失败={snapshot.Failed}  跳过={snapshot.Skipped}");

            if (!string.IsNullOrWhiteSpace(snapshot.CurrentItem))
            {
                ImGui.TextUnformatted($"当前：{snapshot.CurrentItem}");
            }

            if (ImGui.Button("取消导出"))
            {
                try
                {
                    _minimapBatchExportCts?.Cancel();
                }
                catch
                {
                    // ignored
                }
            }
        }
        else
        {
            if (ImGui.Button("开始批量导出"))
            {
                StartMinimapBatchExport();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("关闭"))
        {
            ImGui.CloseCurrentPopup();
        }

        if (snapshot.Warnings.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.2f, 1.0f), $"警告（{snapshot.Warnings.Length}）:");
            ImGui.BeginChild("##minimap_batch_warnings", new Vector2(0, 120), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
            for (int i = 0; i < snapshot.Warnings.Length; i++)
            {
                ImGui.TextUnformatted(snapshot.Warnings[i]);
            }
            ImGui.EndChild();

            if (!running && ImGui.Button("清空警告列表"))
            {
                _minimapBatchExportProgress.ClearWarnings();
            }
        }

        if (snapshot.Errors.Length > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), $"错误（{snapshot.Errors.Length}）:");
            ImGui.BeginChild("##minimap_batch_errors", new Vector2(0, 160), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
            for (int i = 0; i < snapshot.Errors.Length; i++)
            {
                ImGui.TextUnformatted(snapshot.Errors[i]);
            }
            ImGui.EndChild();

            if (!running && ImGui.Button("清空错误列表"))
            {
                _minimapBatchExportProgress.ClearErrors();
            }
        }

        ImGui.EndPopup();
    }

    private string SuggestDefaultMinimapBatchInputDirectory()
    {
        string source = _state.Map?.Path ?? _state.MapPath;

        try
        {
            string? dir = string.IsNullOrWhiteSpace(source)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(Path.GetFullPath(source));

            return string.IsNullOrWhiteSpace(dir) ? Environment.CurrentDirectory : dir;
        }
        catch
        {
            return Environment.CurrentDirectory;
        }
    }

    private static string SuggestDefaultMinimapBatchOutputDirectory(string inputDir)
    {
        if (string.IsNullOrWhiteSpace(inputDir))
        {
            return string.Empty;
        }

        try
        {
            string full = Path.GetFullPath(inputDir);
            return Path.Combine(full, "_minimap");
        }
        catch
        {
            return string.Empty;
        }
    }

    private void StartMinimapBatchExport()
    {
        if (_minimapBatchExportTask is not null && !_minimapBatchExportTask.IsCompleted)
        {
            _state.StatusMessage = "批量导出正在进行中。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Export", "批量导出请求被忽略：已有批量导出任务在运行。");
            return;
        }

        string inputDir = _state.MinimapBatchInputDirectory.Trim();
        string outputDir = _state.MinimapBatchOutputDirectory.Trim();

        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            _state.StatusMessage = "输入目录不存在或为空。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Export", $"批量导出失败：输入目录不存在或为空：{inputDir}");
            return;
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            _state.StatusMessage = "输出目录为空。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Export", "批量导出失败：输出目录为空。");
            return;
        }

        string? listError;
        List<string> files = CollectMapFiles(inputDir, _state.MinimapBatchRecursive, out listError);
        if (!string.IsNullOrWhiteSpace(listError))
        {
            _state.StatusMessage = listError;
            _console.Append(MapEditorConsoleLogLevel.Error, "Export", $"批量导出失败：{listError}");
            return;
        }

        if (files.Count == 0)
        {
            _state.StatusMessage = $"未在目录中发现 .nmp/.mmp：{inputDir}";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Export", $"批量导出失败：未发现 .nmp/.mmp：{inputDir}");
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state.StatusMessage = $"无法创建输出目录：{ex.Message}";
            _console.Append(MapEditorConsoleLogLevel.Error, "Export", $"批量导出失败：无法创建输出目录：{outputDir} | {ex.Message}");
            return;
        }

        bool useTextures = _state.MinimapUseTextures && _textureIndex.IsReady;
        if (_state.MinimapUseTextures && !useTextures)
        {
            _state.MinimapUseTextures = false;
        }

        float nightFactor = MapLighting.Resolve(_state.MinimapLighting).NightFactor;
        var opts = new MinimapBatchExportOptions
        {
            ScaleDivisor = _state.MinimapScale,
            IncludeBack = _state.MinimapIncludeBack,
            IncludeMiddle = _state.MinimapIncludeMiddle,
            IncludeFloor = _state.MinimapIncludeFloor,
            IncludeUnderFront = _state.MinimapIncludeUnderFront,
            IncludeFront = _state.MinimapIncludeFront,
            IncludeOverFront = _state.MinimapIncludeOverFront,
            SeparateLayerFiles = _state.MinimapSeparateLayerFiles,
            UseTextures = useTextures,
            SuppressBorderCells = _state.MinimapSuppressBorderCells,
            ApplyCellTints = _state.MinimapApplyCellTints,
            TintStrength = _state.MinimapTintStrength,
            ApplyCellHeightFlag = _state.MinimapApplyCellHeightFlag,
            CellHeightFlagOffset = _state.MinimapCellHeightFlagOffset,
            ApplyObjectHeight = _state.MinimapApplyObjectHeight,
            ObjectHeightScale = _state.MinimapObjectHeightScale,
            ApplyLuminanceToAlpha = _state.MinimapApplyLuminanceToAlpha,
            ApplyLightingOverlay = _state.MinimapApplyLightingOverlay,
            NightFactor = nightFactor,
            LightingOverlayMaxAlpha = _state.MinimapLightingOverlayMaxAlpha,
            IncludeLightSprites = _state.MinimapIncludeLightSprites,
            IncludeDynamicScene = _state.MinimapIncludeDynamicScene,
            IncludeAttachedEffects = _state.MinimapIncludeAttachedEffects,
            DynamicOverlayMapIdOverride = _state.MinimapOverlayMapIdOverride,
            AttachedEffectsMapIdOverride = _state.MinimapAttachedEffectsMapIdOverride,
            DynamicOverlayLayoutPath = _state.MinimapOverlayLayoutPath,
            AttachedEffectsLayoutPath = _state.MinimapAttachedEffectsLayoutPath,
            DynamicOverlayMaxDecompressedBytes = Math.Clamp(_state.MinimapOverlayMaxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024),
            CropMode = _state.MinimapCropMode,
            CropCellX = _state.MinimapCropCellX,
            CropCellY = _state.MinimapCropCellY,
            CropCellWidth = _state.MinimapCropCellWidth,
            CropCellHeight = _state.MinimapCropCellHeight,
            CropPixelX = _state.MinimapCropPixelX,
            CropPixelY = _state.MinimapCropPixelY,
            CropPixelWidth = _state.MinimapCropPixelWidth,
            CropPixelHeight = _state.MinimapCropPixelHeight,
            AutoCropPaddingCells = _state.MinimapAutoCropPaddingCells,
            IncludeScaleTag = _state.MinimapBatchIncludeScaleTag,
            Overwrite = _state.MinimapBatchOverwrite,
        };

        if (!opts.IncludeBack && !opts.IncludeMiddle && !opts.IncludeFloor && !opts.IncludeUnderFront && !opts.IncludeFront && !opts.IncludeOverFront)
        {
            _state.StatusMessage = "未选择任何导出层。";
            _console.Append(MapEditorConsoleLogLevel.Warning, "Export", "批量导出失败：未选择任何导出层。");
            return;
        }

        _minimapBatchExportCts?.Dispose();
        _minimapBatchExportCts = new CancellationTokenSource();

        files.Sort(StringComparer.OrdinalIgnoreCase);
        _minimapBatchExportProgress.Start(files.Count, outputDir);

        CancellationToken token = _minimapBatchExportCts.Token;
        _minimapBatchExportTask = Task.Run(() => RunMinimapBatchExport(inputDir, outputDir, files, opts, token), token);
        _state.StatusMessage = $"开始批量导出：{files.Count} 个地图…";
        _console.Append(MapEditorConsoleLogLevel.Info, "Export", $"开始批量导出：total={files.Count} in={inputDir} out={outputDir} useTextures={opts.UseTextures} scale={opts.ScaleDivisor}");
    }

    private static List<string> CollectMapFiles(string inputDir, bool recursive, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(inputDir))
        {
            error = "输入目录为空。";
            return new List<string>();
        }

        if (!Directory.Exists(inputDir))
        {
            error = $"目录不存在：{inputDir}";
            return new List<string>();
        }

        var files = new List<string>();

        SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            foreach (string path in Directory.EnumerateFiles(inputDir, "*.*", option))
            {
                string ext = Path.GetExtension(path);
                if (ext.Equals(".nmp", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".mmp", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return new List<string>();
        }

        return files;
    }

    private MinimapBatchExportResult RunMinimapBatchExport(
        string inputDir,
        string outputDir,
        IReadOnlyList<string> files,
        MinimapBatchExportOptions opts,
        CancellationToken token)
    {
        int ok = 0;
        int failed = 0;
        int skipped = 0;

        bool canceled = false;
        bool sceneCfgMissingWarned = false;
        bool dynSceneWarned = false;
        bool attachedEffectsWarned = false;

        for (int i = 0; i < files.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            string file = files[i];
            string rel;
            try
            {
                rel = Path.GetRelativePath(inputDir, file);
            }
            catch
            {
                rel = Path.GetFileName(file);
            }

            if (string.IsNullOrWhiteSpace(rel))
            {
                rel = Path.GetFileName(file);
            }

            _minimapBatchExportProgress.SetCurrent(rel);

            string outFile = BuildBatchMinimapOutputPath(outputDir, rel, opts.ScaleDivisor, opts.IncludeScaleTag);
            if (!opts.Overwrite && OutputAlreadyExists(outFile, opts))
            {
                skipped++;
                _minimapBatchExportProgress.ReportSkipped($"跳过（已存在）：{rel}");
                continue;
            }

            if (!MapDocument.TryLoad(file, out MapDocument? map, out string loadError) || map is null)
            {
                failed++;
                _minimapBatchExportProgress.ReportFailed($"{rel}: 加载失败：{loadError}");
                continue;
            }

            var exportOpts = new MinimapExportOptions
            {
                ScaleDivisor = opts.ScaleDivisor,
                IncludeBack = opts.IncludeBack,
                IncludeMiddle = opts.IncludeMiddle,
                IncludeFloor = opts.IncludeFloor,
                IncludeUnderFront = opts.IncludeUnderFront,
                IncludeFront = opts.IncludeFront,
                IncludeOverFront = opts.IncludeOverFront,
                SeparateLayerFiles = opts.SeparateLayerFiles,
                SuppressBorderCells = opts.SuppressBorderCells,
                ApplyCellTints = opts.ApplyCellTints,
                TintStrength = opts.TintStrength,
                ApplyCellHeightFlag = opts.ApplyCellHeightFlag,
                CellHeightFlagOffset = opts.CellHeightFlagOffset,
                ApplyObjectHeight = opts.ApplyObjectHeight,
                ObjectHeightScale = opts.ObjectHeightScale,
                ApplyLuminanceToAlpha = opts.ApplyLuminanceToAlpha,
                ApplyLightingOverlay = opts.ApplyLightingOverlay,
                NightFactor = opts.NightFactor,
                LightingOverlayMaxAlpha = opts.LightingOverlayMaxAlpha,
                IncludeLightSprites = opts.IncludeLightSprites,
                IncludeDynamicScene = opts.IncludeDynamicScene,
                IncludeAttachedEffects = opts.IncludeAttachedEffects,
                DynamicOverlayMapIdOverride = opts.DynamicOverlayMapIdOverride,
                AttachedEffectsMapIdOverride = opts.AttachedEffectsMapIdOverride,
                DynamicOverlayLayoutPath = opts.DynamicOverlayLayoutPath,
                AttachedEffectsLayoutPath = opts.AttachedEffectsLayoutPath,
                DynamicOverlayMaxDecompressedBytes = Math.Clamp(opts.DynamicOverlayMaxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024),
                CropMode = opts.CropMode,
                CropCellX = opts.CropCellX,
                CropCellY = opts.CropCellY,
                CropCellWidth = opts.CropCellWidth,
                CropCellHeight = opts.CropCellHeight,
                CropPixelX = opts.CropPixelX,
                CropPixelY = opts.CropPixelY,
                CropPixelWidth = opts.CropPixelWidth,
                CropPixelHeight = opts.CropPixelHeight,
                AutoCropPaddingCells = opts.AutoCropPaddingCells,
            };

            bool exportOk;
            string exportError;
            MinimapExportDiagnostics diag = default;

            if (opts.UseTextures)
            {
                exportOk = MinimapExporter.TryExportTexturedPng(map, _textureIndex, exportOpts, outFile, out _, out diag, out exportError);
            }
            else
            {
                exportOk = MinimapExporter.TryExportPlaceholderPng(map, exportOpts, outFile, out _, out exportError);
            }

            if (exportOk)
            {
                ok++;
                _minimapBatchExportProgress.ReportOk();

                if (opts.UseTextures)
                {
                    if (!dynSceneWarned && !string.IsNullOrWhiteSpace(diag.DynamicSceneWarning))
                    {
                        dynSceneWarned = true;
                        _minimapBatchExportProgress.ReportWarning(diag.DynamicSceneWarning!);
                    }

                    if (!attachedEffectsWarned && !string.IsNullOrWhiteSpace(diag.AttachedEffectsWarning))
                    {
                        attachedEffectsWarned = true;
                        _minimapBatchExportProgress.ReportWarning(diag.AttachedEffectsWarning!);
                    }

                    if (false && !dynSceneWarned && diag.DynamicSceneRequested)
                    {
                        dynSceneWarned = true;
                        if (string.IsNullOrWhiteSpace(diag.DynamicSceneDataPath))
                        {
                            _minimapBatchExportProgress.ReportWarning("DynScene：未找到数据文件，且当前未实现渲染：已忽略动态覆盖选项。");
                        }
                        else
                        {
                            string name = Path.GetFileName(diag.DynamicSceneDataPath);
                            _minimapBatchExportProgress.ReportWarning($"DynScene：已发现数据文件（{name}），但当前未实现渲染：已忽略动态覆盖选项。");
                        }
                    }

                    if (false && !attachedEffectsWarned && diag.AttachedEffectsRequested)
                    {
                        attachedEffectsWarned = true;
                        if (string.IsNullOrWhiteSpace(diag.AttachedEffectsDataPath))
                        {
                            _minimapBatchExportProgress.ReportWarning("挂接 effects：未找到数据文件，且当前未实现渲染：已忽略挂接 effects 选项。");
                        }
                        else
                        {
                            string name = Path.GetFileName(diag.AttachedEffectsDataPath);
                            _minimapBatchExportProgress.ReportWarning($"挂接 effects：已发现数据文件（{name}），但当前未实现渲染：已忽略挂接 effects 选项。");
                        }
                    }

                    if (diag.SceneLightMaskConfigMissing && !sceneCfgMissingWarned)
                    {
                        sceneCfgMissingWarned = true;
                        _minimapBatchExportProgress.ReportWarning("SceneLightMaskCfg.xml 未找到：已跳过 scene light mask 叠加（不会影响基础导出/夜晚叠加）。");
                    }

                    if (diag.MissingTextures > 0)
                    {
                        _minimapBatchExportProgress.ReportWarning($"{rel}: 缺失贴图：{diag.MissingTextures} 次");
                    }
                }
            }
            else
            {
                failed++;
                _minimapBatchExportProgress.ReportFailed($"{rel}: {exportError}");
            }
        }

        _minimapBatchExportProgress.Finish(canceled);

        if (canceled)
        {
            return new MinimapBatchExportResult(files.Count, ok, failed, skipped, true, outputDir);
        }

        return new MinimapBatchExportResult(files.Count, ok, failed, skipped, false, outputDir);
    }

    private static string BuildBatchMinimapOutputPath(string outputDir, string relativeMapPath, int scaleDivisor, bool includeScaleTag)
    {
        string dir;
        try
        {
            dir = Path.GetDirectoryName(relativeMapPath) ?? string.Empty;
        }
        catch
        {
            dir = string.Empty;
        }

        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(relativeMapPath);
        }
        catch
        {
            stem = "map";
        }

        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "map";
        }

        scaleDivisor = Math.Clamp(scaleDivisor, 1, 32);
        string file = $"{stem}_minimap{(includeScaleTag ? $"_s{scaleDivisor}" : string.Empty)}.png";
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(outputDir, file)
            : Path.Combine(outputDir, dir, file);
    }

    private static bool OutputAlreadyExists(string outputPath, MinimapBatchExportOptions opts)
    {
        string normalized = outputPath;
        try
        {
            normalized = Path.GetFullPath(outputPath);
        }
        catch
        {
            // ignored
        }

        if (!opts.SeparateLayerFiles)
        {
            return File.Exists(normalized);
        }

        string basePath = Path.ChangeExtension(normalized, null) ?? normalized;
        if (opts.IncludeBack && File.Exists($"{basePath}_back.png")) return true;
        if (opts.IncludeMiddle && File.Exists($"{basePath}_middle.png")) return true;
        if (opts.IncludeFloor && File.Exists($"{basePath}_floor.png")) return true;
        if (opts.IncludeUnderFront && File.Exists($"{basePath}_underfront.png")) return true;
        if (opts.IncludeFront && File.Exists($"{basePath}_front.png")) return true;
        if (opts.IncludeOverFront && File.Exists($"{basePath}_overfront.png")) return true;
        return false;
    }

    private static string SuggestDefaultMinimapExportPath(MapDocument map, int scaleDivisor)
    {
        string fileNameStem;
        try
        {
            fileNameStem = Path.GetFileNameWithoutExtension(map.Path);
        }
        catch
        {
            fileNameStem = "map";
        }

        if (string.IsNullOrWhiteSpace(fileNameStem))
        {
            fileNameStem = "map";
        }

        string dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(map.Path)) ?? Environment.CurrentDirectory;
        }
        catch
        {
            dir = Environment.CurrentDirectory;
        }

        scaleDivisor = Math.Clamp(scaleDivisor, 1, 32);
        return Path.Combine(dir, $"{fileNameStem}_minimap.png");
    }

    private void BuildStatusBar()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float height = ImGui.GetFrameHeightWithSpacing();

        ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X, viewport.Pos.Y + viewport.Size.Y - height));
        ImGui.SetNextWindowSize(new Vector2(viewport.Size.X, height));

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
                                 | ImGuiWindowFlags.NoDocking
                                 | ImGuiWindowFlags.NoSavedSettings
                                 | ImGuiWindowFlags.NoMove
                                 | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##status_bar", flags))
        {
            ImGui.End();
            return;
        }

        string status = _state.StatusMessage;
        if (string.IsNullOrWhiteSpace(status))
        {
            status = "就绪";
        }

        ImGui.TextUnformatted(status);

        if (_state.Map is not null)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 420);
            ImGui.TextUnformatted($"缩放：{_state.Camera.Zoom:0.00}  悬停：{_state.HoverCellX},{_state.HoverCellY}  选中：{_state.SelectedCellX},{_state.SelectedCellY}");
        }

        ImGui.End();
    }

    private static uint GetLayerKey(NmpCellData cell, MapLayer layer)
    {
        static uint ResolveExtraObjectKey(uint raw, byte defaultPackageId)
        {
            uint masked = raw & 0x00FFFFFFu;
            uint img = masked & 0xFFFFu;
            if (img == 0)
            {
                return 0u;
            }

            uint pkg = (masked >> 16) & 0xFFu;
            if (pkg == 0)
            {
                pkg = defaultPackageId;
            }

            return (pkg << 16) | img;
        }

        if (layer == MapLayer.Back)
        {
            ushort img = cell.BackImage;
            if (img == 0)
            {
                return 0u;
            }

            ushort lib = cell.BackLibrary != 0 ? cell.BackLibrary : DefaultSmTilesLibrary;
            return ComposeKey(lib, img);
        }

        if (layer == MapLayer.Middle)
        {
            ushort img = cell.MiddleImage;
            if (img == 0)
            {
                return 0u;
            }

            ushort lib = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultTilesLibrary;
            return ComposeKey(lib, img);
        }

        if (layer == MapLayer.Floor)
        {
            return ResolveExtraObjectKey(cell.NearGround, DefaultObjectLibrary);
        }

        if (layer == MapLayer.UnderFront)
        {
            return ResolveExtraObjectKey(cell.UnderObject, DefaultObjectLibrary);
        }

        if (layer == MapLayer.Front)
        {
            uint masked = cell.FrontImage & 0x00FFFFFFu;
            uint img = masked & 0xFFFFu;
            if (img == 0)
            {
                return 0u;
            }

            uint pkg = (masked >> 16) & 0xFFu;
            if (pkg == 0)
            {
                pkg = cell.FrontLibrary;
            }
            if (pkg == 0)
            {
                pkg = DefaultObjectLibrary;
            }

            return (pkg << 16) | img;
        }

        if (layer == MapLayer.OverFront)
        {
            return ResolveExtraObjectKey(cell.OverObject, DefaultObjectLibrary);
        }

        return 0u;
    }

    private static uint ComposeKey(ushort lib, ushort img)
    {
        if (img == 0)
        {
            return 0u;
        }

        return ((uint)lib << 16) | img;
    }

    private static uint HashToColor(uint key, byte alpha)
    {
        if (key == 0)
        {
            return PackColor(0, 0, 0, 0);
        }

        uint x = key;
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;

        byte r = (byte)(40 + (x & 0x7F));
        byte g = (byte)(40 + ((x >> 8) & 0x7F));
        byte b = (byte)(40 + ((x >> 16) & 0x7F));
        return PackColor(r, g, b, alpha);
    }

    private static uint PackColor(byte r, byte g, byte b, byte a)
    {
        return (uint)(a << 24 | b << 16 | g << 8 | r);
    }

    private static uint PackColor(Vector4 rgba)
    {
        float r0 = float.IsFinite(rgba.X) ? Math.Clamp(rgba.X, 0.0f, 1.0f) : 0.0f;
        float g0 = float.IsFinite(rgba.Y) ? Math.Clamp(rgba.Y, 0.0f, 1.0f) : 0.0f;
        float b0 = float.IsFinite(rgba.Z) ? Math.Clamp(rgba.Z, 0.0f, 1.0f) : 0.0f;
        float a0 = float.IsFinite(rgba.W) ? Math.Clamp(rgba.W, 0.0f, 1.0f) : 0.0f;

        byte r = (byte)Math.Clamp((int)MathF.Round(r0 * 255.0f), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(g0 * 255.0f), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(b0 * 255.0f), 0, 255);
        byte a = (byte)Math.Clamp((int)MathF.Round(a0 * 255.0f), 0, 255);
        return PackColor(r, g, b, a);
    }

    private readonly record struct MinimapBatchExportSnapshot(
        int Total,
        int Done,
        int Ok,
        int Failed,
        int Skipped,
        bool Running,
        string CurrentItem,
        string[] Warnings,
        string[] Errors);

    private sealed class MinimapBatchExportProgress
    {
        private readonly object _gate = new();
        private int _total;
        private int _done;
        private int _ok;
        private int _failed;
        private int _skipped;
        private bool _running;
        private string _currentItem = string.Empty;
        private string _outputDirectory = string.Empty;
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        public void Start(int total, string outputDirectory)
        {
            lock (_gate)
            {
                _total = Math.Max(0, total);
                _done = 0;
                _ok = 0;
                _failed = 0;
                _skipped = 0;
                _running = true;
                _currentItem = string.Empty;
                _outputDirectory = outputDirectory ?? string.Empty;
                _warnings.Clear();
                _errors.Clear();
            }
        }

        public void SetCurrent(string currentItem)
        {
            lock (_gate)
            {
                _currentItem = currentItem ?? string.Empty;
            }
        }

        public void ReportOk()
        {
            lock (_gate)
            {
                _done++;
                _ok++;
            }
        }

        public void ReportSkipped(string message)
        {
            _ = message;

            lock (_gate)
            {
                _done++;
                _skipped++;
            }
        }

        public void ReportFailed(string message)
        {
            lock (_gate)
            {
                _done++;
                _failed++;
                AddError(message);
            }
        }

        public void ReportWarning(string message)
        {
            lock (_gate)
            {
                AddWarning(message);
            }
        }

        public void Finish(bool canceled)
        {
            _ = canceled;

            lock (_gate)
            {
                _running = false;
                _currentItem = string.Empty;
            }
        }

        public void FailAll(string message)
        {
            lock (_gate)
            {
                AddError(message);

                if (_done < _total)
                {
                    _failed += _total - _done;
                    _done = _total;
                }

                _running = false;
                _currentItem = string.Empty;
            }
        }

        public void ClearErrors()
        {
            lock (_gate)
            {
                _errors.Clear();
            }
        }

        public void ClearWarnings()
        {
            lock (_gate)
            {
                _warnings.Clear();
            }
        }

        public MinimapBatchExportSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new MinimapBatchExportSnapshot(
                    Total: _total,
                    Done: _done,
                    Ok: _ok,
                    Failed: _failed,
                    Skipped: _skipped,
                    Running: _running,
                    CurrentItem: _currentItem,
                    Warnings: _warnings.ToArray(),
                    Errors: _errors.ToArray());
            }
        }

        private void AddError(string message)
        {
            string text = message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            const int maxErrors = 200;
            if (_errors.Count >= maxErrors)
            {
                if (_errors.Count == maxErrors)
                {
                    _errors.Add("（错误过多，后续错误已省略…）");
                }

                return;
            }

            _errors.Add(text);
        }

        private void AddWarning(string message)
        {
            string text = message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            const int maxWarnings = 200;
            if (_warnings.Count >= maxWarnings)
            {
                if (_warnings.Count == maxWarnings)
                {
                    _warnings.Add("（警告过多，后续警告已省略…）");
                }

                return;
            }

            _warnings.Add(text);
        }
    }

    private sealed class MinimapBatchExportOptions
    {
        public int ScaleDivisor { get; init; } = 4;
        public bool IncludeBack { get; init; } = true;
        public bool IncludeMiddle { get; init; } = true;
        public bool IncludeFloor { get; init; } = true;
        public bool IncludeUnderFront { get; init; } = true;
        public bool IncludeFront { get; init; } = true;
        public bool IncludeOverFront { get; init; } = true;
        public bool IncludeDynamicScene { get; init; }
        public bool IncludeAttachedEffects { get; init; }
        public string DynamicOverlayMapIdOverride { get; init; } = string.Empty;
        public string AttachedEffectsMapIdOverride { get; init; } = string.Empty;
        public string DynamicOverlayLayoutPath { get; init; } = string.Empty;
        public string AttachedEffectsLayoutPath { get; init; } = string.Empty;
        public long DynamicOverlayMaxDecompressedBytes { get; init; } = DynamicOverlayCodec.DefaultMaxDecompressedBytes;
        public bool IncludeScaleTag { get; init; } = true;
        public bool SeparateLayerFiles { get; init; }
        public bool UseTextures { get; init; }
        public bool SuppressBorderCells { get; init; }
        public bool ApplyCellTints { get; init; }
        public float TintStrength { get; init; } = 0.35f;
        public bool ApplyCellHeightFlag { get; init; }
        public float CellHeightFlagOffset { get; init; } = 0.0f;
        public bool ApplyObjectHeight { get; init; }
        public float ObjectHeightScale { get; init; } = 0.0f;
        public bool ApplyLuminanceToAlpha { get; init; } = true;
        public bool ApplyLightingOverlay { get; init; } = true;
        public float NightFactor { get; init; } = 0.0f;
        public int LightingOverlayMaxAlpha { get; init; } = 120;
        public bool IncludeLightSprites { get; init; } = true;
        public MinimapCropMode CropMode { get; init; } = MinimapCropMode.None;
        public int CropCellX { get; init; }
        public int CropCellY { get; init; }
        public int CropCellWidth { get; init; } = 1;
        public int CropCellHeight { get; init; } = 1;
        public int CropPixelX { get; init; }
        public int CropPixelY { get; init; }
        public int CropPixelWidth { get; init; }
        public int CropPixelHeight { get; init; }
        public int AutoCropPaddingCells { get; init; }
        public bool Overwrite { get; init; }
    }

    private readonly record struct MinimapBatchExportResult(
        int Total,
        int Ok,
        int Failed,
        int Skipped,
        bool Canceled,
        string OutputDirectory);

    private struct CellInspectorDraft
    {
        public int BackLibrary;
        public int BackImage;
        public int MiddleLibrary;
        public int MiddleImage;
        public int FrontLibrary;
        public int FrontImage;

        public int UnderLibrary;
        public int UnderImage;
        public int OverLibrary;
        public int OverImage;
        public int NearLibrary;
        public int NearImage;

        public int Flags;
        public int ExtendedAttributes;
        public int ObjectHeight;
        public int DoorIndex;
        public int DoorOffset;
        public int Light;
        public int Sound;
    }

    private sealed class MapEditorDocument
    {
        public string NormalizedPath { get; set; }

        public string Path { get; set; }
        public bool IsPrefabDocument { get; set; }
        public bool IsReadOnly { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string PreferredDataPathEntryName { get; set; } = string.Empty;
        public MapDocument? Map { get; set; }
        public string MapLoadError { get; set; } = string.Empty;

        public UndoRedoStack History { get; } = new();
        public MapCamera Camera { get; } = new();
        public bool CameraNeedsFit { get; set; } = true;

        public int HoverCellX { get; set; } = -1;
        public int HoverCellY { get; set; } = -1;
        public int SelectedCellX { get; set; } = -1;
        public int SelectedCellY { get; set; } = -1;

        public bool HasSelection { get; set; }
        public int SelectionX0 { get; set; }
        public int SelectionY0 { get; set; }
        public int SelectionX1 { get; set; }
        public int SelectionY1 { get; set; }

        // --- Runtime minimap overlay (transient) ---
        public nint RuntimeMinimapTextureId { get; set; }
        public int RuntimeMinimapTextureWidth { get; set; }
        public int RuntimeMinimapTextureHeight { get; set; }
        public bool RuntimeMinimapDirty { get; set; } = true;
        public bool RuntimeMinimapDirtyStampSet { get; set; }
        public double RuntimeMinimapDirtySince { get; set; }
        public int RuntimeMinimapRevision { get; set; } = 1;
        public int RuntimeMinimapBuiltRevision { get; set; }
        public Task<RuntimeMinimapBuildResult>? RuntimeMinimapBuildTask { get; set; }
        public ulong RuntimeMinimapLastSettingsHash { get; set; }
        public bool RuntimeMinimapHasSettingsSnapshot { get; set; }
        public bool IsDraggingMinimap { get; set; }
        public string RuntimeMinimapLastParityWarning { get; set; } = string.Empty;

        public MapEditorDocument(string normalizedPath)
        {
            NormalizedPath = normalizedPath ?? string.Empty;
            Path = normalizedPath ?? string.Empty;
        }
    }

    private sealed class MoveSelectionSession
    {
        public MapDocument Map { get; }
        public MapDocument Snippet { get; }
        public StampSourceKind PreviousStampSource { get; }

        public bool ApplyBack { get; }
        public bool ApplyMiddle { get; }
        public bool ApplyFront { get; }
        public bool ApplyUnderObject { get; }
        public bool ApplyOverObject { get; }
        public bool ApplyNearGround { get; }
        public bool ApplyBlocked { get; }

        public MoveSelectionSession(
            MapDocument map,
            MapDocument snippet,
            StampSourceKind previousStampSource,
            bool applyBack,
            bool applyMiddle,
            bool applyFront,
            bool applyUnderObject,
            bool applyOverObject,
            bool applyNearGround,
            bool applyBlocked)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
            PreviousStampSource = previousStampSource;
            ApplyBack = applyBack;
            ApplyMiddle = applyMiddle;
            ApplyFront = applyFront;
            ApplyUnderObject = applyUnderObject;
            ApplyOverObject = applyOverObject;
            ApplyNearGround = applyNearGround;
            ApplyBlocked = applyBlocked;
        }
    }

    private sealed class PaintDragSession
    {
        public MapDocument Map { get; }
        public MapLayer Layer { get; }
        public int Library { get; }
        public int Image { get; }

        public Dictionary<int, NmpCellData> BeforeCells { get; } = new();

        public PaintDragSession(MapDocument map, MapLayer layer, int library, int image)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Layer = layer;
            Library = library;
            Image = image;
        }
    }

    private sealed class BlockedDragSession
    {
        public MapDocument Map { get; }
        public bool SetBlocked { get; }

        public Dictionary<int, NmpCellData> BeforeCells { get; } = new();

        public BlockedDragSession(MapDocument map, bool setBlocked)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            SetBlocked = setBlocked;
        }
    }

    private sealed class RectFillDragSession
    {
        public MapDocument Map { get; }
        public MapLayer Layer { get; }
        public int Library { get; }
        public int Image { get; }

        public int StartX { get; }
        public int StartY { get; }

        public int CurrentX { get; set; }
        public int CurrentY { get; set; }

        public RectFillDragSession(MapDocument map, MapLayer layer, int library, int image, int startX, int startY)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            Layer = layer;
            Library = library;
            Image = image;
            StartX = startX;
            StartY = startY;
            CurrentX = startX;
            CurrentY = startY;
        }
    }

    private sealed class SelectionDragSession
    {
        public MapDocument Map { get; }

        public int StartX { get; }
        public int StartY { get; }

        public int CurrentX { get; set; }
        public int CurrentY { get; set; }

        public SelectionDragSession(MapDocument map, int startX, int startY)
        {
            Map = map ?? throw new ArgumentNullException(nameof(map));
            StartX = startX;
            StartY = startY;
            CurrentX = startX;
            CurrentY = startY;
        }
    }
}
