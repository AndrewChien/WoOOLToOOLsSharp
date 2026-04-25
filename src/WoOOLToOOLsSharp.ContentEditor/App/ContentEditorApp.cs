using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using ImGuiNET;
using WoOOLToOOLsSharp.Rendering.Vulkan;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.EditorBridge;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;
using WoOOLToOOLsSharp.Shared.Formats.Sgl;
using WoOOLToOOLsSharp.Shared.Formats.Tex;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.ContentEditor.App;

public sealed class ContentEditorApp : IVulkanApp
{
    private static readonly TimeSpan WatchedChangeDebounce = ReadWatchedChangeDebounce();

    private readonly EditorState _state = new();
    private readonly FileWatcher _fileWatcher = new();
    private readonly HashSet<string> _pendingWatchedPaths = new(StringComparer.OrdinalIgnoreCase);

    private bool _loadedPreferences;
    private bool _watcherRootsDirty = true;
    private bool _watchedReloadPending;
    private bool _watchedOverflowed;
    private long _lastWatchedEventTimestamp;

    private bool _liveReloadQueued;
    private bool _liveReloadOverflowed;
    private int _liveReloadChangedPathCount;
    private readonly HashSet<string> _liveReloadChangedPaths = new(StringComparer.OrdinalIgnoreCase);

    private string _assetFilterText = string.Empty;
    private bool _requestOpenByPathPopup;
    private string _openByPathText = string.Empty;
    private int _pendingDataFolderBrowseIndex = -1;

    private bool _requestResetDockLayout;
    private uint _dockspaceId;

    private readonly SimpleFileDialog _fileDialog = new();

    private VulkanRenderer? _renderer;
    private UiTheme? _appliedTheme;

    private readonly Dictionary<string, PreviewTexture> _previewTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<PreviewDecodeResult>> _pendingPreviewDecodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _previewErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _texFrameCountCache = new(StringComparer.OrdinalIgnoreCase);

    public bool RequestExit => _state.RequestExit;

    public ContentEditorApp()
    {
        _state.EditorBridge = new LocalEditorBridge(EditorBridgeApp.ContentEditor);
        _state.StatusMessage = "就绪";

        if (!_state.EditorBridge.Initialize(out string bridgeError) && !string.IsNullOrWhiteSpace(bridgeError))
        {
            _state.StatusMessage = bridgeError;
        }
    }

    public void ConfigureImGui(VulkanRenderer renderer, ImGuiController controller)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        if (controller is null) throw new ArgumentNullException(nameof(controller));

        _dockspaceId = controller.DockspaceId;

        controller.BuildMenuBar = BuildMenuBar;
        controller.BuildDockedUi = BuildDockedUi;
        controller.BuildStatusBar = BuildStatusBar;
    }

    public void Tick(GlfwInput input, float deltaSeconds)
    {
        _ = input;
        _ = deltaSeconds;

        if (!_loadedPreferences)
        {
            LoadPreferencesOnce();
        }

        if (_state.EditorBridge is not null)
        {
            _state.EditorBridge.Tick();
            DrainEditorBridgeRequests();
            _state.MapEditorRunning = _state.EditorBridge.IsAppRunning(EditorBridgeApp.MapEditor);
        }

        _state.AssetLibrary.TickFrame();
        ProcessPreviewDecodes();
        PrunePreviewCache();

        if (_state.PreferencesDirty)
        {
            TrySavePreferences();
        }

        HandleFileWatcher();

        if (_state.DataFoldersDirty)
        {
            if (_watcherRootsDirty)
            {
                ReconfigureWatcherRoots();
                _watcherRootsDirty = false;
            }

            _state.AssetLibrary.Refresh(_state.DataFolders);
            _state.DataFoldersDirty = false;

            string indexWarnings = _state.AssetLibrary.LastError;
            if (_liveReloadQueued)
            {
                LiveReloadSummary summary = ApplyLiveReload();
                string message = BuildLiveReloadStatus(summary, _liveReloadChangedPathCount, _liveReloadOverflowed);

                if (!string.IsNullOrWhiteSpace(indexWarnings))
                {
                    string warn = SummarizeIndexWarnings(indexWarnings);
                    if (!string.IsNullOrWhiteSpace(warn))
                    {
                        message += $"；索引警告：{warn}";
                    }
                }

                _state.StatusMessage = message;
            }
            else if (!string.IsNullOrWhiteSpace(indexWarnings))
            {
                _state.StatusMessage = indexWarnings;
            }
            else
            {
                _state.StatusMessage = $"索引完成：{_state.AssetLibrary.DiscoveredFiles.Count} 个文件";
            }
        }

        PollBatchImportCompletion();
    }

    public void Dispose()
    {
        if (_state.PreferencesDirty)
        {
            TrySavePreferences();
        }

        DisposePreviewResources();
        _fileWatcher.Dispose();
    }

    private static string GetPreferencesPath()
        => Path.Combine(Environment.CurrentDirectory, "content_editor_prefs.cfg");

    private void LoadPreferencesOnce()
    {
        _loadedPreferences = true;

        string prefsPath = GetPreferencesPath();
        if (!ContentEditorPreferences.TryLoad(prefsPath, _state, out string error))
        {
            _state.StatusMessage = error;
        }

        if (_state.RestoreState && _state.PendingTabRestore.Count > 0)
        {
            RestoreOpenTabsFromPreferences();
        }

        _watcherRootsDirty = true;
        _state.DataFoldersDirty = true;
    }

    private void RestoreOpenTabsFromPreferences()
    {
        _state.Tabs.Clear();

        foreach (SavedTabInfo saved in _state.PendingTabRestore)
        {
            if (string.IsNullOrWhiteSpace(saved.SglKey))
            {
                continue;
            }

            if (IsWpfHashPath(saved.SglKey))
            {
                OpenHashComparisonTab(saved.SglKey);
                continue;
            }

            string title = saved.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                try
                {
                    title = Path.GetFileName(saved.SglKey);
                }
                catch
                {
                    title = saved.SglKey;
                }
            }

            _state.Tabs.Add(new ImageTab
            {
                Title = title,
                SglKey = saved.SglKey,
                Open = true,
                Loading = false,
            });
        }

        if (_state.PendingActiveTabIndex >= 0 && _state.PendingActiveTabIndex < _state.Tabs.Count)
        {
            _state.ActiveTabIndex = _state.PendingActiveTabIndex;
        }
        else if (_state.Tabs.Count > 0)
        {
            _state.ActiveTabIndex = 0;
        }

        _state.PendingTabRestore.Clear();
        _state.PendingActiveTabIndex = -1;
    }

    private void TrySavePreferences()
    {
        string prefsPath = GetPreferencesPath();
        if (ContentEditorPreferences.TrySave(prefsPath, _state, out string error))
        {
            _state.PreferencesDirty = false;
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            _state.StatusMessage = error;
        }
    }

    private void ReconfigureWatcherRoots()
    {
        var roots = new List<string>(_state.DataFolders.Count);
        foreach (DataFolder folder in _state.DataFolders)
        {
            if (folder is null) continue;
            if (string.IsNullOrWhiteSpace(folder.Path)) continue;
            roots.Add(folder.Path);
        }

        _fileWatcher.SetRoots(roots, out string error);
        if (!string.IsNullOrWhiteSpace(error))
        {
            _state.StatusMessage = error;
        }

        _pendingWatchedPaths.Clear();
        _watchedReloadPending = false;
        _watchedOverflowed = false;
        _lastWatchedEventTimestamp = 0;
    }

    private void HandleFileWatcher()
    {
        IReadOnlyList<FileChangeEvent> events = _fileWatcher.PollEvents();
        if (events.Count > 0)
        {
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < events.Count; i++)
            {
                FileChangeEvent ev = events[i];
                if (ev.Action == FileChangeAction.Overflow)
                {
                    _watchedReloadPending = true;
                    _watchedOverflowed = true;
                    _lastWatchedEventTimestamp = now;
                    continue;
                }

                if (ev.Action == FileChangeAction.Renamed)
                {
                    RecordRelevantWatchedPath(now, ev.OldPath);
                    RecordRelevantWatchedPath(now, ev.Path);
                    continue;
                }

                RecordRelevantWatchedPath(now, ev.Path);
            }
        }

        if (!_watchedReloadPending || _lastWatchedEventTimestamp == 0)
        {
            return;
        }

        long current = Stopwatch.GetTimestamp();
        double seconds = (current - _lastWatchedEventTimestamp) / (double)Stopwatch.Frequency;
        if (seconds < WatchedChangeDebounce.TotalSeconds)
        {
            return;
        }

        int changedPathCount = _pendingWatchedPaths.Count;
        QueueLiveReloadFromWatcher(changedPathCount);
        _pendingWatchedPaths.Clear();
        _watchedReloadPending = false;
        _watchedOverflowed = false;
        _lastWatchedEventTimestamp = 0;

        _state.DataFoldersDirty = true;
        _state.StatusMessage = changedPathCount > 0
            ? $"实时刷新：检测到 {changedPathCount} 个变更，准备重建索引并刷新已打开页签…"
            : "实时刷新：检测到变更，准备重建索引并刷新已打开页签…";
    }

    private void QueueLiveReloadFromWatcher(int changedPathCount)
    {
        _liveReloadQueued = true;
        _liveReloadOverflowed = _watchedOverflowed;
        _liveReloadChangedPathCount = changedPathCount;

        _liveReloadChangedPaths.Clear();
        _liveReloadChangedPaths.UnionWith(_pendingWatchedPaths);
    }

    private readonly record struct LiveReloadSummary(
        int ReloadedTabs,
        int ClosedTabs,
        int RefreshedHashTabs,
        int ClosedHashTabs);

    private LiveReloadSummary ApplyLiveReload()
    {
        // Clear caches that depend on file content. (旧版 LiveReload.cpp 也会清理这些。)
        _state.WpfHashCache.Clear();
        _state.BatchHashScanned = false;
        _state.BatchHashResults.Clear();

        string activeTabKey = string.Empty;
        if (_state.ActiveTabIndex >= 0 && _state.ActiveTabIndex < _state.Tabs.Count)
        {
            activeTabKey = _state.Tabs[_state.ActiveTabIndex]?.SglKey ?? string.Empty;
        }

        string activeHashPath = string.Empty;
        if (_state.ActiveHashTabIndex >= 0 && _state.ActiveHashTabIndex < _state.HashTabs.Count)
        {
            activeHashPath = _state.HashTabs[_state.ActiveHashTabIndex]?.HashFilePath ?? string.Empty;
        }

        int reloadedTabs = 0;
        int closedTabs = 0;

        for (int i = 0; i < _state.Tabs.Count; i++)
        {
            ImageTab tab = _state.Tabs[i];
            if (tab is null || !tab.Open) continue;

            if (TryReloadImageTabForLiveReload(tab))
            {
                reloadedTabs++;
            }
            else
            {
                tab.Open = false;
                tab.SelectedImageIndex = -1;
                tab.HasUnsavedChanges = false;
                closedTabs++;
            }
        }

        if (closedTabs > 0)
        {
            PruneClosedImageTabs();
            _state.PreferencesDirty = true;
        }

        _state.ActiveTabIndex = FindOpenImageTabIndexByKey(activeTabKey);
        if (_state.ActiveTabIndex < 0 && _state.Tabs.Count > 0)
        {
            _state.ActiveTabIndex = 0;
        }
        _state.PendingTabSwitch = _state.ActiveTabIndex;

        if (_state.ActiveTabIndex < 0)
        {
            _state.SelectedAssetIndex = -1;
        }

        int refreshedHashTabs = 0;
        int closedHashTabs = 0;

        for (int i = 0; i < _state.HashTabs.Count; i++)
        {
            HashComparisonTab ht = _state.HashTabs[i];
            if (ht is null || !ht.Open) continue;

            if (TryReloadHashTabForLiveReload(ht))
            {
                refreshedHashTabs++;
            }
            else
            {
                ht.Open = false;
                closedHashTabs++;
            }
        }

        if (closedHashTabs > 0)
        {
            PruneClosedHashTabs();
            _state.PreferencesDirty = true;
        }

        _state.ActiveHashTabIndex = FindOpenHashTabIndexByPath(activeHashPath);
        if (_state.ActiveHashTabIndex < 0 && _state.HashTabs.Count > 0)
        {
            _state.ActiveHashTabIndex = 0;
        }
        _state.PendingHashTabSwitch = _state.ActiveHashTabIndex;

        // Consume the queued live reload request.
        _liveReloadQueued = false;
        _liveReloadOverflowed = false;
        _liveReloadChangedPathCount = 0;
        _liveReloadChangedPaths.Clear();

        return new LiveReloadSummary(reloadedTabs, closedTabs, refreshedHashTabs, closedHashTabs);
    }

    private bool TryReloadImageTabForLiveReload(ImageTab tab)
    {
        if (tab is null)
        {
            return false;
        }

        string key = tab.SglKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (IsWpfHashPath(key))
        {
            return true;
        }

        string ext = GetExtensionSafe(key);
        if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            return TryReloadTexKeyForLiveReload(key);
        }

        if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            return TryReloadSglKeyForLiveReload(tab, key);
        }

        if (ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            return TryReloadWpfKeyForLiveReload(tab, key);
        }

        // Unknown tab types are kept as-is.
        return true;
    }

    private bool TryReloadTexKeyForLiveReload(string texKey)
    {
        if (string.IsNullOrWhiteSpace(texKey))
        {
            return false;
        }

        if (WpfKey.TryParse(texKey, out string wpfPath, out _))
        {
            if (!File.Exists(wpfPath))
            {
                return false;
            }
        }
        else if (!File.Exists(texKey))
        {
            return false;
        }

        EvictPreviewTexturesForSource(texKey);
        _texFrameCountCache.Remove(texKey);
        return true;
    }

    private bool TryReloadSglKeyForLiveReload(ImageTab tab, string sglKey)
    {
        if (tab is null || string.IsNullOrWhiteSpace(sglKey))
        {
            return false;
        }

        if (WpfKey.TryParse(sglKey, out string wpfPath, out _))
        {
            if (!File.Exists(wpfPath))
            {
                return false;
            }
        }
        else if (!File.Exists(sglKey))
        {
            return false;
        }

        // Discard in-memory edits so the tab reflects the updated source.
        _state.AssetLibrary.DiscardEditableSgl(sglKey);
        tab.HasUnsavedChanges = false;

        EvictPreviewTexturesForSource(sglKey);
        _state.AssetLibrary.RequestSglLibraryAsync(sglKey, forceReload: true);
        return true;
    }

    private bool TryReloadWpfKeyForLiveReload(ImageTab tab, string wpfPath)
    {
        if (tab is null || string.IsNullOrWhiteSpace(wpfPath))
        {
            return false;
        }

        if (!File.Exists(wpfPath))
        {
            return false;
        }

        // Force the WPF filter match cache to rebuild next draw, because the entry tree may have changed.
        tab.WpfFilterLastApplied = "__reload__";
        tab.WpfMatchedDirs.Clear();

        _state.AssetLibrary.RequestWpfArchiveAsync(wpfPath, forceReload: true);
        return true;
    }

    private bool TryReloadHashTabForLiveReload(HashComparisonTab ht)
    {
        if (ht is null)
        {
            return false;
        }

        string hashPath = ht.HashFilePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(hashPath) || !File.Exists(hashPath))
        {
            return false;
        }

        if (!WpfHashFileCodec.TryReadWpfHashFile(hashPath, out WpfHashFileData hashData, out _))
        {
            return false;
        }

        string wpfPath = hashPath;
        if (wpfPath.EndsWith(".hash", StringComparison.OrdinalIgnoreCase) && wpfPath.Length >= 5)
        {
            wpfPath = wpfPath.Substring(0, wpfPath.Length - 5);
        }

        if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> wpfEntries, out _))
        {
            return false;
        }

        WpfHashComparison comparison = WpfHashFileCodec.CompareWpfHash(hashData, wpfEntries);
        ht.WpfPath = wpfPath;
        ht.Comparison = comparison;
        ht.VisibleEntryIndicesDirty = true;
        _state.WpfHashCache[wpfPath] = comparison;
        return true;
    }

    private void PruneClosedImageTabs()
    {
        for (int i = _state.Tabs.Count - 1; i >= 0; i--)
        {
            if (_state.Tabs[i].Open)
            {
                continue;
            }

            string key = _state.Tabs[i].SglKey;
            EvictPreviewTexturesForSource(key);
            _state.Tabs.RemoveAt(i);
        }
    }

    private void PruneClosedHashTabs()
    {
        for (int i = _state.HashTabs.Count - 1; i >= 0; i--)
        {
            if (_state.HashTabs[i].Open)
            {
                continue;
            }

            _state.HashTabs.RemoveAt(i);
        }
    }

    private int FindOpenImageTabIndexByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return -1;
        }

        for (int i = 0; i < _state.Tabs.Count; i++)
        {
            ImageTab tab = _state.Tabs[i];
            if (tab is null || !tab.Open) continue;
            if (string.Equals(tab.SglKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindOpenHashTabIndexByPath(string hashPath)
    {
        if (string.IsNullOrWhiteSpace(hashPath))
        {
            return -1;
        }

        for (int i = 0; i < _state.HashTabs.Count; i++)
        {
            HashComparisonTab ht = _state.HashTabs[i];
            if (ht is null || !ht.Open) continue;
            if (string.Equals(ht.HashFilePath, hashPath, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string BuildLiveReloadStatus(LiveReloadSummary summary, int changedPathCount, bool overflowed)
    {
        var sb = new StringBuilder();
        sb.Append("实时刷新");

        if (changedPathCount > 0)
        {
            sb.Append($"：{changedPathCount} 个变更");
        }
        else if (overflowed)
        {
            sb.Append("：检测到大量变更");
        }

        sb.Append($"（刷新 {summary.ReloadedTabs} 个页签");

        if (summary.ClosedTabs > 0)
        {
            sb.Append($"，关闭 {summary.ClosedTabs} 个页签");
        }
        if (summary.RefreshedHashTabs > 0)
        {
            sb.Append($"，刷新 {summary.RefreshedHashTabs} 个 Hash 页签");
        }
        if (summary.ClosedHashTabs > 0)
        {
            sb.Append($"，关闭 {summary.ClosedHashTabs} 个 Hash 页签");
        }

        sb.Append("）");
        return sb.ToString();
    }

    private static string SummarizeIndexWarnings(string warnings)
    {
        if (string.IsNullOrWhiteSpace(warnings))
        {
            return string.Empty;
        }

        string oneLine = warnings.Replace('\r', ' ').Replace('\n', ' ');
        oneLine = oneLine.Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
        {
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        const int maxLen = 120;
        if (oneLine.Length > maxLen)
        {
            oneLine = oneLine.Substring(0, maxLen) + "...";
        }

        return oneLine;
    }

    private static bool IsRelevantWatchedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string filename = Path.GetFileName(path);
        if (IsTemporaryFilename(filename))
        {
            return false;
        }

        if (filename.EndsWith(".wpf.hash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string ext = Path.GetExtension(path);
        return ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tex", StringComparison.OrdinalIgnoreCase);
    }

    private void RecordRelevantWatchedPath(long nowTimestamp, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!IsRelevantWatchedPath(path))
        {
            return;
        }

        _watchedReloadPending = true;
        _lastWatchedEventTimestamp = nowTimestamp;
        _pendingWatchedPaths.Add(path);
    }

    private static bool IsTemporaryFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return true;
        }

        string name = filename.Trim();
        if (name.Length == 0)
        {
            return true;
        }

        // 常见临时文件（编辑器/系统）
        if (name[0] == '~')
        {
            return true;
        }

        if (name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Contains(".~", StringComparison.Ordinal))
        {
            return true;
        }

        string ext;
        try
        {
            ext = Path.GetExtension(name);
        }
        catch
        {
            return false;
        }

        return ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".bak", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".swp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".swo", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".swx", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ReadWatchedChangeDebounce()
    {
        const int defaultMs = 200;
        int ms = defaultMs;

        string? env = Environment.GetEnvironmentVariable("WOOOL_WATCH_DEBOUNCE_MS");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out int parsed))
        {
            ms = Math.Clamp(parsed, 50, 5000);
        }

        return TimeSpan.FromMilliseconds(ms);
    }

    private void BuildMenuBar()
    {
        if (ImGui.BeginMenu("CONTENT EDITOR##ContentEditor"))
        {
            if (ImGui.MenuItem("Preferences"))
            {
                _state.ShowSettingsWindow = true;
            }

            if (ImGui.MenuItem("Reset Layout"))
            {
                _requestResetDockLayout = true;
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Exit"))
            {
                _state.RequestExit = true;
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("文件"))
        {
            if (ImGui.MenuItem("打开路径..."))
            {
                _requestOpenByPathPopup = true;
                _openByPathText = string.Empty;
            }

            if (ImGui.BeginMenu("最近打开", _state.RecentFiles.Count > 0))
            {
                for (int i = 0; i < _state.RecentFiles.Count; i++)
                {
                    string path = _state.RecentFiles[i];
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    string label = path;
                    try
                    {
                        label = Path.GetFileName(path);
                        if (string.IsNullOrWhiteSpace(label))
                        {
                            label = path;
                        }
                    }
                    catch
                    {
                        label = path;
                    }

                    if (ImGui.MenuItem(label))
                    {
                        TryOpenPath(path);
                    }
                }
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("保存偏好设置", null, false, _state.PreferencesDirty))
            {
                TrySavePreferences();
            }

            ImGui.Separator();
            if (ImGui.MenuItem("退出"))
            {
                _state.RequestExit = true;
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("构建"))
        {
            if (ImGui.MenuItem("从文件夹创建 WPF..."))
            {
                _state.PendingBrowserAction = PendingFileAction.ConvertFolderToWpf;
                _fileDialog.Open(SimpleFileDialogMode.OpenFolder, "选择要打包为 WPF 的文件夹", GetDefaultBrowserStartPath());
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("将文件夹内所有文件打包为新的 .wpf（输出写在文件夹旁边）");
            }

            if (ImGui.MenuItem("从文件夹创建 SGL..."))
            {
                _state.PendingBrowserAction = PendingFileAction.CreateSglFromFolder;
                _fileDialog.Open(SimpleFileDialogMode.OpenFolder, "选择包含 .tex 的文件夹（将创建 .sgl）", GetDefaultBrowserStartPath());
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("从文件夹内的 .tex 构建 .sgl；若存在 placements.txt 则按索引写入相应 slot");
            }

            ImGui.Separator();

            if (ImGui.MenuItem("批量 Hash 校验..."))
            {
                _state.ShowBatchHashValidation = true;
                _state.BatchHashScanned = false;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("校验数据目录下的 .wpf.hash 与对应 .wpf 是否一致，并可导出缺失 hash 列表");
            }

            ImGui.EndMenu();
        }

        bool hasActiveSglTab = TryGetActiveSglTab(out ImageTab? sglTab, out string sglKey);
        bool hasSglSelection = hasActiveSglTab && sglTab!.SelectedImageIndex >= 0;
        bool allowEditMenus = !_fileDialog.IsOpen;

        if (ImGui.BeginMenu("编辑", hasActiveSglTab && allowEditMenus))
        {
            if (ImGui.BeginMenu("插入到前面", hasSglSelection))
            {
                if (ImGui.MenuItem("空单元", null, false, hasSglSelection))
                {
                    if (_state.AssetLibrary.InsertEmptyCell(sglKey, sglTab!.SelectedImageIndex))
                    {
                        sglTab.HasUnsavedChanges = true;
                        sglTab.SelectedFrame = 0;
                        EvictPreviewTexturesForSource(sglKey);
                        _state.StatusMessage = "已在选中索引前插入空单元";
                    }
                    else
                    {
                        _state.StatusMessage = _state.AssetLibrary.LastError;
                    }
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("在选中索引前插入空单元，后续索引整体上移");
                }

                if (ImGui.MenuItem("纹理 (.tex)…", null, false, hasSglSelection))
                {
                    _state.PendingBrowserAction = PendingFileAction.InsertBefore;
                    _state.PendingEditSglKey = sglKey;
                    _state.PendingEditImageIndex = sglTab!.SelectedImageIndex;

                    string startDir = GetLibraryPreferredDirectory(sglKey);
                    _fileDialog.Open(SimpleFileDialogMode.OpenFileOrFolder, "选择 .tex 文件（或包含 .tex 的文件夹）", startDir);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("选择单个 .tex 文件，或选择一个文件夹（将按文件名排序插入其中的 .tex，非递归）");
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("插入到后面", hasSglSelection))
            {
                if (ImGui.MenuItem("空单元", null, false, hasSglSelection))
                {
                    int insertIndex = sglTab!.SelectedImageIndex + 1;
                    if (_state.AssetLibrary.InsertEmptyCell(sglKey, insertIndex))
                    {
                        sglTab.HasUnsavedChanges = true;
                        sglTab.SelectedFrame = 0;
                        EvictPreviewTexturesForSource(sglKey);
                        _state.StatusMessage = "已在选中索引后插入空单元";
                    }
                    else
                    {
                        _state.StatusMessage = _state.AssetLibrary.LastError;
                    }
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("在选中索引后插入空单元，后续索引整体上移");
                }

                if (ImGui.MenuItem("纹理 (.tex)…", null, false, hasSglSelection))
                {
                    _state.PendingBrowserAction = PendingFileAction.InsertAfter;
                    _state.PendingEditSglKey = sglKey;
                    _state.PendingEditImageIndex = sglTab!.SelectedImageIndex;

                    string startDir = GetLibraryPreferredDirectory(sglKey);
                    _fileDialog.Open(SimpleFileDialogMode.OpenFileOrFolder, "选择 .tex 文件（或包含 .tex 的文件夹）", startDir);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("选择单个 .tex 文件，或选择一个文件夹（将按文件名排序插入其中的 .tex，非递归）");
                }

                ImGui.EndMenu();
            }

            ImGui.Separator();

            if (ImGui.MenuItem("替换…", null, false, hasSglSelection))
            {
                _state.PendingBrowserAction = PendingFileAction.ReplaceSelected;
                _state.PendingEditSglKey = sglKey;
                _state.PendingEditImageIndex = sglTab!.SelectedImageIndex;

                string startDir = GetLibraryPreferredDirectory(sglKey);
                _fileDialog.Open(SimpleFileDialogMode.OpenFile, "选择替换用 .tex", startDir);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("用另一个 .tex 替换当前选中的单元");
            }

            if (ImGui.MenuItem("移除", null, false, hasSglSelection))
            {
                if (_state.AssetLibrary.RemoveImage(sglKey, sglTab!.SelectedImageIndex))
                {
                    sglTab.HasUnsavedChanges = true;
                    sglTab.SelectedImageIndex = Math.Max(0, sglTab.SelectedImageIndex - 1);
                    sglTab.SelectedFrame = 0;
                    FixupSglTabSelectionAfterEdit(sglKey, sglTab);
                    EvictPreviewTexturesForSource(sglKey);
                    _state.StatusMessage = "已移除选中单元";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("删除选中单元，后续索引整体下移");
            }

            if (ImGui.MenuItem("置空", null, false, hasSglSelection))
            {
                if (_state.AssetLibrary.BlankCell(sglKey, sglTab!.SelectedImageIndex))
                {
                    sglTab.HasUnsavedChanges = true;
                    sglTab.SelectedFrame = 0;
                    EvictPreviewTexturesForSource(sglKey);
                    _state.StatusMessage = "已置空选中单元（索引保持不变）";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("清空选中单元的纹理数据与元数据，保留索引不变");
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("保存", hasActiveSglTab && allowEditMenus))
        {
            if (ImGui.MenuItem("覆盖原库"))
            {
                if (_state.AssetLibrary.SaveLibrary(sglKey))
                {
                    sglTab!.HasUnsavedChanges = false;
                    _state.StatusMessage = "已保存到原库";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("将修改写回到原始库文件（会直接修改源文件）");
            }

            if (ImGui.MenuItem("另存为新库..."))
            {
                _state.PendingBrowserAction = PendingFileAction.SaveAsCopy;
                _state.PendingEditSglKey = sglKey;
                _state.PendingEditImageIndex = sglTab!.SelectedImageIndex;

                string startDir = GetLibraryPreferredDirectory(sglKey);
                string baseName = MakeSafeFilename(sglTab!.Title);
                if (string.IsNullOrWhiteSpace(baseName))
                {
                    baseName = "library";
                }

                _fileDialog.Open(SimpleFileDialogMode.SaveFile, "另存为新库", startDir, baseName + ".sgl");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("将库保存为新的文件，原库不变（若目标已存在，将提示确认覆盖）");
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("视图"))
        {
            bool changed = false;

            bool showDataFoldersPanel = _state.ShowDataFoldersPanel;
            if (ImGui.MenuItem("数据目录", null, ref showDataFoldersPanel))
            {
                _state.ShowDataFoldersPanel = showDataFoldersPanel;
                changed = true;
            }

            bool showLibraryPanel = _state.ShowLibraryTexturesPanel;
            if (ImGui.MenuItem("文件浏览器", null, ref showLibraryPanel))
            {
                _state.ShowLibraryTexturesPanel = showLibraryPanel;
                changed = true;
            }

            bool showInfo = _state.ShowInformationPanel;
            if (ImGui.MenuItem("资源信息", null, ref showInfo))
            {
                _state.ShowInformationPanel = showInfo;
                changed = true;
            }

            if (changed)
            {
                _state.PreferencesDirty = true;
            }

            ImGui.Separator();
            if (ImGui.MenuItem("设置..."))
            {
                _state.ShowSettingsWindow = true;
            }

            if (ImGui.MenuItem("刷新索引"))
            {
                _state.DataFoldersDirty = true;
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("桥接"))
        {
            if (_state.EditorBridge is null || !_state.EditorBridge.Initialized)
            {
                ImGui.TextDisabled("EditorBridge：未初始化");
            }
            else
            {
                bool running = _state.EditorBridge.IsAppRunning(EditorBridgeApp.MapEditor);
                ImGui.TextDisabled($"MapEditor：{(running ? "运行中" : "未检测到")}");
            }

            ImGui.Separator();
            ImGui.TextWrapped("桥接打开的资源会出现在“导入资产 (Imported)”中。");
            ImGui.TextWrapped("在“文件浏览器”中双击/右键地图(.nmp/.mmp)或 Prefab(.nmpo/.nmpoN) 可发送到 MapEditor 打开。");
            ImGui.TextWrapped("在 MapEditor 中可通过右键菜单或“在CE打开”按钮把贴图源发送到 ContentEditor。");
            ImGui.TextWrapped("提示：若目标程序未运行，请求会排队，待其启动后自动处理。");
            ImGui.EndMenu();
        }
    }

    private void BuildDockedUi()
    {
        ApplyThemeIfNeeded();
        ApplyUiScale();
        HandleResetDockLayout();
        HandleFileDialog();
        DrawConfirmDialog();
        DrawOpenByPathPopup();

        if (_state.ShowSettingsWindow)
        {
            DrawSettingsWindow();
        }

        DrawBatchHashValidationWindow();

        if (_state.ShowDataFoldersPanel)
        {
            DrawDataFoldersPanel();
        }

        if (_state.ShowLibraryTexturesPanel)
        {
            DrawAssetLibraryPanel();
        }

        DrawWorkspacePanel();
        DrawHashComparisonWindow();

        if (_state.ShowInformationPanel)
        {
            DrawInformationPanel();
        }
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
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"重置布局失败：{ex.Message}";
        }
    }

    private void ResetDockLayoutToDefault()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        uint dockspaceId = _dockspaceId;

        ImGuiDockBuilder.RemoveNodeDockedWindows(dockspaceId);
        ImGuiDockBuilder.RemoveNodeChildNodes(dockspaceId);
        ImGuiDockBuilder.SetNodeSize(dockspaceId, viewport.WorkSize);

        uint center = dockspaceId;
        ImGuiDockBuilder.SplitNode(center, ImGuiDir.Left, 0.20f, out uint left, out center);
        ImGuiDockBuilder.SplitNode(center, ImGuiDir.Right, 0.25f, out uint right, out center);
        ImGuiDockBuilder.SplitNode(center, ImGuiDir.Down, 0.30f, out uint bottom, out center);

        ImGuiDockBuilder.DockWindow("数据目录", left);
        ImGuiDockBuilder.DockWindow("资源信息", right);
        ImGuiDockBuilder.DockWindow("工作区", center);
        ImGuiDockBuilder.DockWindow("Hash Comparison", center);
        ImGuiDockBuilder.DockWindow("文件浏览器", bottom);

        ImGuiDockBuilder.Finish(dockspaceId);
    }

    private void ApplyThemeIfNeeded()
    {
        UiTheme theme = _state.Theme;
        if (_appliedTheme.HasValue && _appliedTheme.Value == theme)
        {
            return;
        }

        if (theme == UiTheme.Light)
        {
            ImGui.StyleColorsLight();
        }
        else
        {
            ImGui.StyleColorsDark();
        }

        _appliedTheme = theme;
    }

    private void ApplyUiScale()
    {
        float scale = _state.UiScale;
        if (scale is < 0.5f or > 3.0f)
        {
            scale = 1.0f;
        }

        ImGui.GetIO().FontGlobalScale = scale;
    }

    private void HandleFileDialog()
    {
        _fileDialog.Draw();

        if (!_fileDialog.TryConsumeResult(out SimpleFileDialogResult result, out string selectedPath))
        {
            return;
        }

        PendingFileAction action = _state.PendingBrowserAction;
        _state.PendingBrowserAction = PendingFileAction.None;

        if (result != SimpleFileDialogResult.Ok || string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        string pendingEditSglKey = _state.PendingEditSglKey;
        int pendingEditImageIndex = _state.PendingEditImageIndex;
        _state.PendingEditSglKey = string.Empty;
        _state.PendingEditImageIndex = -1;

        switch (action)
        {
            case PendingFileAction.InsertBefore:
                if (!TryResolveSglEditContext(pendingEditSglKey, out ImageTab? editTab, out string editKey))
                {
                    _state.StatusMessage = "插入失败：未找到目标 SGL 标签页。";
                    break;
                }

                List<string> beforeTexPaths = CollectTexInputs(selectedPath);
                if (_state.AssetLibrary.InsertImagesBefore(editKey, pendingEditImageIndex, beforeTexPaths))
                {
                    editTab!.HasUnsavedChanges = true;
                    editTab.SelectedFrame = 0;
                    editTab.SelectedImageIndex = pendingEditImageIndex;
                    FixupSglTabSelectionAfterEdit(editKey, editTab);
                    EvictPreviewTexturesForSource(editKey);
                    _state.StatusMessage = "已插入到选中索引前";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
                break;

            case PendingFileAction.InsertAfter:
                if (!TryResolveSglEditContext(pendingEditSglKey, out ImageTab? afterTab, out string afterKey))
                {
                    _state.StatusMessage = "插入失败：未找到目标 SGL 标签页。";
                    break;
                }

                List<string> afterTexPaths = CollectTexInputs(selectedPath);
                if (_state.AssetLibrary.InsertImagesAfter(afterKey, pendingEditImageIndex, afterTexPaths))
                {
                    afterTab!.HasUnsavedChanges = true;
                    afterTab.SelectedFrame = 0;
                    afterTab.SelectedImageIndex = pendingEditImageIndex;
                    FixupSglTabSelectionAfterEdit(afterKey, afterTab);
                    EvictPreviewTexturesForSource(afterKey);
                    _state.StatusMessage = "已插入到选中索引后";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
                break;

            case PendingFileAction.ReplaceSelected:
                if (!TryResolveSglEditContext(pendingEditSglKey, out ImageTab? replaceTab, out string replaceKey))
                {
                    _state.StatusMessage = "替换失败：未找到目标 SGL 标签页。";
                    break;
                }

                if (_state.AssetLibrary.ReplaceImage(replaceKey, pendingEditImageIndex, selectedPath))
                {
                    replaceTab!.HasUnsavedChanges = true;
                    replaceTab.SelectedFrame = 0;
                    replaceTab.SelectedImageIndex = pendingEditImageIndex;
                    FixupSglTabSelectionAfterEdit(replaceKey, replaceTab);
                    EvictPreviewTexturesForSource(replaceKey);
                    _state.StatusMessage = "已替换选中单元";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
                break;

            case PendingFileAction.SaveAsCopy:
                if (!TryResolveSglEditContext(pendingEditSglKey, out _, out string saveKey))
                {
                    _state.StatusMessage = "另存失败：未找到目标 SGL 标签页。";
                    break;
                }

                string normalizedTarget = NormalizeCopySaveTargetPath(selectedPath);
                if (File.Exists(normalizedTarget))
                {
                    _state.ConfirmDialog.Title = "覆盖已存在文件";
                    _state.ConfirmDialog.Message = "目标文件已存在，是否覆盖？\n\n" + normalizedTarget;
                    _state.ConfirmDialog.Action = PendingFileAction.SaveAsCopy;
                    _state.ConfirmDialog.Path = normalizedTarget;
                    _state.ConfirmDialog.Key = saveKey;
                    _state.ConfirmDialog.Open = true;
                    break;
                }

                if (_state.AssetLibrary.SaveLibraryAsCopy(saveKey, normalizedTarget))
                {
                    _state.StatusMessage = $"已保存副本：{Path.GetFileName(normalizedTarget)}";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
                break;

            case PendingFileAction.ConvertFolderToWpf:
                if (_state.AssetLibrary.ConvertFolderToWpf(selectedPath, out string wpfPath))
                {
                    _state.StatusMessage = $"Created: {wpfPath}";
                    _state.DataFoldersDirty = true;
                }
                else
                {
                    _state.StatusMessage = string.IsNullOrWhiteSpace(_state.AssetLibrary.LastError)
                        ? "创建 WPF 失败。"
                        : _state.AssetLibrary.LastError;
                }
                break;

            case PendingFileAction.CreateSglFromFolder:
                if (_state.AssetLibrary.CreateSglFromFolder(selectedPath, out string sglPath))
                {
                    _state.StatusMessage = $"Created: {sglPath}";
                    _state.DataFoldersDirty = true;
                }
                else
                {
                    _state.StatusMessage = string.IsNullOrWhiteSpace(_state.AssetLibrary.LastError)
                        ? "创建 SGL 失败。"
                        : _state.AssetLibrary.LastError;
                }
                break;

            case PendingFileAction.ExportBatchMissingHashesCsv:
                if (TryExportBatchMissingHashesCsv(selectedPath, out int written, out string normalizedPath, out string error))
                {
                    _state.StatusMessage = $"Exported {written} missing hash(es) to {Path.GetFileName(normalizedPath)}";
                }
                else
                {
                    _state.StatusMessage = string.IsNullOrWhiteSpace(error) ? "导出失败。" : error;
                }
                break;

            case PendingFileAction.ExportMissingHashes:
                int htIdx = _state.PendingExportHashTabIndex;
                _state.PendingExportHashTabIndex = -1;
                if (htIdx < 0 || htIdx >= _state.HashTabs.Count)
                {
                    _state.StatusMessage = "导出失败：hash 页签索引无效。";
                    break;
                }

                HashComparisonTab ht = _state.HashTabs[htIdx];
                if (TryExportMissingHashesToTxt(ht, selectedPath, out int txtWritten, out string txtPath, out string txtError))
                {
                    _state.StatusMessage = $"Exported {txtWritten} missing hash(es) to {Path.GetFileName(txtPath)}";
                }
                else
                {
                    _state.StatusMessage = string.IsNullOrWhiteSpace(txtError) ? "导出失败。" : txtError;
                }
                break;

            case PendingFileAction.ExportWpfToFolder:
                string wpfExportPath = _state.PendingExportWpfPath;
                _state.PendingExportWpfPath = string.Empty;
                if (string.IsNullOrWhiteSpace(wpfExportPath))
                {
                    _state.StatusMessage = "导出失败：未指定 WPF。";
                    break;
                }

                if (TryExportWpfToFolder(wpfExportPath, selectedPath, out int exported, out string exportErr))
                {
                    _state.StatusMessage = $"已导出：{exported} 个文件";
                }
                else
                {
                    _state.StatusMessage = string.IsNullOrWhiteSpace(exportErr) ? "导出失败。" : exportErr;
                }
                break;

            case PendingFileAction.ImportMissingDataToWpf:
                // This modifies WPF archives on disk; confirm first.
                _state.ConfirmDialog.Title = "确认导入缺失数据";
                _state.ConfirmDialog.Message =
                    "该操作会扫描所选目录，并把其中“不存在于对应 WPF 中”的文件注入回 WPF（将直接修改 .wpf 文件）。\n" +
                    $"导入根目录：{selectedPath}\n\n" +
                    "是否继续？";
                _state.ConfirmDialog.Action = PendingFileAction.ImportMissingDataToWpf;
                _state.ConfirmDialog.Path = selectedPath;
                _state.ConfirmDialog.Open = true;
                break;

            case PendingFileAction.OpenByPath:
                _openByPathText = selectedPath.Trim();
                break;

            case PendingFileAction.PickDataFolderPath:
                int folderIndex = _pendingDataFolderBrowseIndex;
                _pendingDataFolderBrowseIndex = -1;
                if (folderIndex < 0 || folderIndex >= _state.DataFolders.Count)
                {
                    _state.StatusMessage = "选择数据目录失败：索引无效。";
                    break;
                }

                _state.DataFolders[folderIndex].Path = selectedPath.Trim();
                MarkDataFoldersChanged();
                break;
        }
    }

    private void DrawConfirmDialog()
    {
        ConfirmDialogState dialog = _state.ConfirmDialog;
        if (!dialog.Open)
        {
            return;
        }

        string title = string.IsNullOrWhiteSpace(dialog.Title) ? "确认" : dialog.Title;
        ImGui.OpenPopup(title);

        bool open = true;
        if (ImGui.BeginPopupModal(title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            string message = dialog.Message ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message))
            {
                ImGui.TextWrapped(message);
            }

            ImGui.Separator();

            if (ImGui.Button("继续"))
            {
                ExecuteConfirmedAction(dialog);
                dialog.Open = false;
                dialog.Action = PendingFileAction.None;
                dialog.Path = string.Empty;
                dialog.Key = string.Empty;
                dialog.Title = string.Empty;
                dialog.Message = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消"))
            {
                dialog.Open = false;
                dialog.Action = PendingFileAction.None;
                dialog.Path = string.Empty;
                dialog.Key = string.Empty;
                dialog.Title = string.Empty;
                dialog.Message = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (!open)
        {
            dialog.Open = false;
        }
    }

    private void ExecuteConfirmedAction(ConfirmDialogState dialog)
    {
        PendingFileAction action = dialog.Action;
        string path = dialog.Path ?? string.Empty;
        string key = dialog.Key ?? string.Empty;

        switch (action)
        {
            case PendingFileAction.ImportMissingDataToWpf:
                StartBatchImportMissingData(path);
                break;

            case PendingFileAction.SaveAsCopy:
                if (string.IsNullOrWhiteSpace(key))
                {
                    _state.StatusMessage = "另存失败：SGL key 为空。";
                    break;
                }

                if (_state.AssetLibrary.SaveLibraryAsCopy(key, path))
                {
                    _state.StatusMessage = $"已保存副本：{Path.GetFileName(path)}";
                }
                else
                {
                    _state.StatusMessage = _state.AssetLibrary.LastError;
                }
                break;
        }
    }

    private bool TryGetActiveSglTab(out ImageTab? tab, out string sglKey)
    {
        tab = null;
        sglKey = string.Empty;

        int idx = _state.ActiveTabIndex;
        if (idx < 0 || idx >= _state.Tabs.Count)
        {
            return false;
        }

        ImageTab candidate = _state.Tabs[idx];
        if (candidate is null)
        {
            return false;
        }

        string key = candidate.SglKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!GetExtensionSafe(key).Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        tab = candidate;
        sglKey = key;
        return true;
    }

    private bool TryResolveSglEditContext(string pendingSglKey, out ImageTab? tab, out string sglKey)
    {
        tab = null;
        sglKey = string.Empty;

        string desiredKey = pendingSglKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(desiredKey))
        {
            return TryGetActiveSglTab(out tab, out sglKey);
        }

        // Prefer active tab if it matches.
        if (TryGetActiveSglTab(out ImageTab? activeTab, out string activeKey)
            && string.Equals(activeKey, desiredKey, StringComparison.OrdinalIgnoreCase))
        {
            tab = activeTab;
            sglKey = activeKey;
            return true;
        }

        for (int i = 0; i < _state.Tabs.Count; i++)
        {
            ImageTab t = _state.Tabs[i];
            if (t is null || !t.Open) continue;

            if (string.Equals(t.SglKey, desiredKey, StringComparison.OrdinalIgnoreCase))
            {
                tab = t;
                sglKey = desiredKey;
                return true;
            }
        }

        return false;
    }

    private void FixupSglTabSelectionAfterEdit(string sglKey, ImageTab tab)
    {
        if (tab is null)
        {
            return;
        }

        var library = _state.AssetLibrary.GetSglLibrary(sglKey);
        if (library is null || !library.IsOpen())
        {
            return;
        }

        int count = library.GetImageCount();
        if (count <= 0)
        {
            tab.SelectedImageIndex = -1;
            tab.SelectedFrame = 0;
            return;
        }

        int idx = tab.SelectedImageIndex;
        if (idx < 0) idx = 0;
        if (idx >= count) idx = count - 1;
        tab.SelectedImageIndex = idx;

        int frameCount = library.GetFrameCount(idx);
        if (frameCount < 1) frameCount = 1;

        int frame = tab.SelectedFrame;
        if (frame < 0 || frame >= frameCount)
        {
            tab.SelectedFrame = 0;
        }
    }

    private string GetLibraryPreferredDirectory(string libraryKey)
    {
        string key = libraryKey ?? string.Empty;
        if (WpfKey.TryParse(key, out string wpfPath, out _))
        {
            try
            {
                string? dir = Path.GetDirectoryName(wpfPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    return dir;
                }
            }
            catch
            {
                // ignore
            }

            return GetDefaultBrowserStartPath();
        }

        try
        {
            string? dir = Path.GetDirectoryName(key);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }
        catch
        {
            // ignore
        }

        return GetDefaultBrowserStartPath();
    }

    private static List<string> CollectTexInputs(string pickedPath)
    {
        var outPaths = new List<string>();

        string p = pickedPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(p))
        {
            return outPaths;
        }

        try
        {
            if (File.Exists(p))
            {
                if (Path.GetExtension(p).Equals(".tex", StringComparison.OrdinalIgnoreCase))
                {
                    outPaths.Add(p);
                }

                return outPaths;
            }
        }
        catch
        {
            return outPaths;
        }

        try
        {
            if (!Directory.Exists(p))
            {
                return outPaths;
            }
        }
        catch
        {
            return outPaths;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(p, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetExtension(file).Equals(".tex", StringComparison.OrdinalIgnoreCase))
                {
                    outPaths.Add(file);
                }
            }
        }
        catch
        {
            // ignore; best-effort
        }

        outPaths.Sort(StringComparer.OrdinalIgnoreCase);
        return outPaths;
    }

    private static string NormalizeCopySaveTargetPath(string selectedPath)
    {
        string targetPath = selectedPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return string.Empty;
        }

        string ext = string.Empty;
        try
        {
            ext = Path.GetExtension(targetPath);
        }
        catch
        {
            ext = string.Empty;
        }

        if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tex", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            return targetPath;
        }

        try
        {
            return Path.ChangeExtension(targetPath, ".sgl");
        }
        catch
        {
            return targetPath;
        }
    }

    private static string MakeSafeFilename(string rawName)
    {
        string name = (rawName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    private string GetDefaultBrowserStartPath()
    {
        for (int i = 0; i < _state.DataFolders.Count; i++)
        {
            DataFolder folder = _state.DataFolders[i];
            if (folder is null) continue;
            if (string.IsNullOrWhiteSpace(folder.Path)) continue;

            string p = folder.Path.Trim();
            try
            {
                p = Path.GetFullPath(p);
            }
            catch
            {
                // ignore
            }

            try
            {
                if (Directory.Exists(p))
                {
                    return p;
                }
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            string cwd = Environment.CurrentDirectory;
            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                return cwd;
            }
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private bool TryExportWpfToFolder(string wpfPath, string outFolder, out int exportedCount, out string error)
    {
        exportedCount = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath))
        {
            error = "WPF 路径为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outFolder))
        {
            error = "输出目录为空。";
            return false;
        }

        string outRootFullPath;
        try
        {
            outRootFullPath = Path.GetFullPath(outFolder);
        }
        catch
        {
            outRootFullPath = outFolder;
        }

        string outRootPrefix = outRootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;

        try
        {
            Directory.CreateDirectory(outRootFullPath);
        }
        catch (Exception ex)
        {
            error = $"无法创建输出目录：{ex.Message}";
            return false;
        }

        WpfArchive? archive = _state.AssetLibrary.GetWpfArchive(wpfPath);
        bool tempArchive = false;
        if (archive is null || !archive.IsOpen())
        {
            tempArchive = true;
            archive = new WpfArchive();
            if (!archive.Open(wpfPath, out error))
            {
                archive.Dispose();
                return false;
            }
        }

        try
        {
            IReadOnlyList<WpfEntry> entries = archive.GetEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                WpfEntry e = entries[i];
                if (e.IsDirectory || e.ByteSize == 0) continue;
                if (string.IsNullOrWhiteSpace(e.FullPath)) continue;

                string rel = e.FullPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

                string outPath;
                try
                {
                    outPath = Path.GetFullPath(Path.Combine(outRootFullPath, rel));
                }
                catch
                {
                    continue;
                }

                // Safety: prevent path traversal out of the chosen folder.
                if (!outPath.Equals(outRootFullPath, StringComparison.OrdinalIgnoreCase)
                    && !outPath.StartsWith(outRootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? parent = null;
                try
                {
                    parent = Path.GetDirectoryName(outPath);
                }
                catch
                {
                    parent = null;
                }

                if (!string.IsNullOrWhiteSpace(parent))
                {
                    try
                    {
                        Directory.CreateDirectory(parent);
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (!archive.ExtractEntry(e, out byte[] bytes, out error))
                {
                    return false;
                }

                try
                {
                    File.WriteAllBytes(outPath, bytes);
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    error = $"写入失败：{ex.Message}";
                    return false;
                }
            }

            return true;
        }
        finally
        {
            if (tempArchive)
            {
                archive.Dispose();
            }
        }
    }

    private bool TryExportBatchMissingHashesCsv(string outPath, out int written, out string normalizedPath, out string error)
    {
        written = 0;
        normalizedPath = outPath ?? string.Empty;
        error = string.Empty;

        if (_state.BatchHashResults.Count == 0)
        {
            error = "没有可导出的校验结果。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            error = "输出路径为空。";
            return false;
        }

        try
        {
            string ext = Path.GetExtension(outPath);
            if (!ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                outPath = Path.ChangeExtension(outPath, ".csv");
            }
        }
        catch
        {
            // ignore invalid path normalization
        }

        normalizedPath = outPath;

        try
        {
            string? parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法创建输出目录：{ex.Message}";
            return false;
        }

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var writer = new StreamWriter(outPath, append: false, encoding: utf8NoBom);
            writer.NewLine = "\n";

            writer.WriteLine("File,Hash");

            foreach (BatchHashResult r in _state.BatchHashResults)
            {
                if (!string.IsNullOrWhiteSpace(r.Error))
                {
                    continue;
                }

                for (int i = 0; i < r.MissingHashes.Count; i++)
                {
                    long h = r.MissingHashes[i];
                    ulong u = unchecked((ulong)h);
                    writer.Write(r.DisplayName);
                    writer.Write(',');
                    writer.Write("0x");
                    writer.Write(u.ToString("X16"));
                    writer.WriteLine();
                    written++;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"写入失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryExportMissingHashesToTxt(
        HashComparisonTab ht,
        string outPath,
        out int written,
        out string normalizedPath,
        out string error)
    {
        written = 0;
        normalizedPath = outPath ?? string.Empty;
        error = string.Empty;

        if (ht is null)
        {
            error = "hash 页签为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            error = "输出路径为空。";
            return false;
        }

        try
        {
            string ext = Path.GetExtension(outPath);
            if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                outPath = Path.ChangeExtension(outPath, ".txt");
            }
        }
        catch
        {
            // ignore invalid path normalization
        }

        normalizedPath = outPath;

        try
        {
            string? parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法创建输出目录：{ex.Message}";
            return false;
        }

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var writer = new StreamWriter(outPath, append: false, encoding: utf8NoBom);
            writer.NewLine = "\n";

            IReadOnlyList<WpfHashComparisonEntry> entries = ht.Comparison.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                WpfHashComparisonEntry e = entries[i];
                if (e.Status != WpfHashComparisonStatus.MissingFromWpf)
                {
                    continue;
                }

                ulong h = unchecked((ulong)e.Hash);
                writer.Write("0x");
                writer.Write(h.ToString("X16"));
                writer.WriteLine();
                written++;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"写入失败：{ex.Message}";
            return false;
        }
    }

    private void DrawOpenByPathPopup()
    {
        if (_requestOpenByPathPopup)
        {
            ImGui.OpenPopup("打开路径");
            _requestOpenByPathPopup = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal("打开路径", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("请输入要打开的资源文件路径（.wpf/.sgl/.tex/.wpf.hash）：");

            float browseButtonWidth = ImGui.CalcTextSize("路径").X + ImGui.GetStyle().FramePadding.X * 2.0f;
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            float lineWidth = Math.Clamp(ImGui.GetMainViewport().WorkSize.X * 0.55f, 240.0f, 520.0f);
            float inputWidth = MathF.Max(160.0f, lineWidth - browseButtonWidth - spacing);
            ImGui.SetNextItemWidth(inputWidth);
            ImGui.InputText("##open_by_path_input", ref _openByPathText, 1024);
            ImGui.SameLine();
            if (ImGui.Button("路径##open_by_path_browse"))
            {
                _state.PendingBrowserAction = PendingFileAction.OpenByPath;

                string startDir = GetDefaultBrowserStartPath();
                if (!string.IsNullOrWhiteSpace(_openByPathText))
                {
                    try
                    {
                        string full = Path.GetFullPath(_openByPathText.Trim());
                        string? dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        {
                            startDir = dir;
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                _fileDialog.Open(SimpleFileDialogMode.OpenFile, "选择要打开的资源文件", startDir);
            }

            bool canOpen = !string.IsNullOrWhiteSpace(_openByPathText);
            if (!canOpen)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("打开"))
            {
                if (TryOpenPath(_openByPathText))
                {
                    ImGui.CloseCurrentPopup();
                }
            }

            if (!canOpen)
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
    }

    private bool TryOpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _state.StatusMessage = "打开失败：路径为空";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            fullPath = path;
        }

        if (!File.Exists(fullPath))
        {
            _state.StatusMessage = $"打开失败：文件不存在: {fullPath}";
            return false;
        }

        string ext = GetExtensionSafe(fullPath);
        bool isMap = NmpFileName.IsMapExtension(ext);
        bool isPrefab = !isMap && NmpFileName.IsPrefabExtension(ext);

        if (isMap || isPrefab)
        {
            if (_state.EditorBridge is null)
            {
                _state.StatusMessage = "打开失败：EditorBridge 未初始化。";
                return false;
            }

            if (_state.EditorBridge.SendOpenMap(fullPath, out string bridgeError))
            {
                bool running = _state.EditorBridge.IsAppRunning(EditorBridgeApp.MapEditor);
                string status = running ? "已发送到 MapEditor" : "已加入队列（MapEditor 未运行）";
                string typeLabel = isPrefab ? "预制体" : "地图";
                _state.StatusMessage = $"桥接：{status}打开{typeLabel}：{Path.GetFileName(fullPath)}";
                AddRecentFile(fullPath);
                return true;
            }

            string typeLabel2 = isPrefab ? "预制体" : "地图";
            _state.StatusMessage = string.IsNullOrWhiteSpace(bridgeError) ? $"桥接：发送打开{typeLabel2}请求失败。" : bridgeError;
            return false;
        }

        OpenFileInTab(fullPath, Path.GetFileName(fullPath));
        AddRecentFile(fullPath);
        return true;
    }

    private void DrawSettingsWindow()
    {
        bool open = _state.ShowSettingsWindow;
        if (!ImGui.Begin("设置", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            _state.ShowSettingsWindow = open;
            ImGui.End();
            return;
        }

        if (!open)
        {
            _state.ShowSettingsWindow = false;
            ImGui.End();
            return;
        }

        SettingsSection section = _state.CurrentSettingsSection;
        bool sectionChanged = false;

        if (ImGui.RadioButton("应用", section == SettingsSection.Application))
        {
            section = SettingsSection.Application;
            sectionChanged = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("数据路径", section == SettingsSection.DataPaths))
        {
            section = SettingsSection.DataPaths;
            sectionChanged = true;
        }

        if (sectionChanged)
        {
            _state.CurrentSettingsSection = section;
            _state.PreferencesDirty = true;
        }

        ImGui.Separator();

        if (section == SettingsSection.Application)
        {
            float scale = _state.UiScale;
            if (ImGui.SliderFloat("UI 缩放", ref scale, 0.5f, 3.0f, "%.2f"))
            {
                _state.UiScale = scale;
                _state.PreferencesDirty = true;
            }

            int grid = _state.GridCellSize;
            if (ImGui.SliderInt("网格大小", ref grid, 32, 128))
            {
                _state.GridCellSize = grid;
                _state.PreferencesDirty = true;
            }

            bool restore = _state.RestoreState;
            if (ImGui.Checkbox("退出时保存并恢复标签页", ref restore))
            {
                _state.RestoreState = restore;
                _state.PreferencesDirty = true;
            }

            ImGui.Separator();
            ImGui.TextUnformatted("主题：");
            UiTheme theme = _state.Theme;
            if (ImGui.RadioButton("深色", theme == UiTheme.Dark))
            {
                _state.Theme = UiTheme.Dark;
                _state.PreferencesDirty = true;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("浅色", theme == UiTheme.Light))
            {
                _state.Theme = UiTheme.Light;
                _state.PreferencesDirty = true;
            }

            ImGui.Separator();
            ImGui.TextUnformatted("性能（预览）：");
            int maxSide = _state.PreviewMaxSide;
            if (ImGui.SliderInt("预览最大边(px)", ref maxSide, 64, 4096))
            {
                _state.PreviewMaxSide = maxSide;
                _state.PreferencesDirty = true;
            }

            int maxItems = _state.PreviewCacheMaxItems;
            if (ImGui.SliderInt("预览缓存上限(张)", ref maxItems, 8, 2048))
            {
                _state.PreviewCacheMaxItems = maxItems;
                _state.PreferencesDirty = true;
            }

            if (ImGui.Button("清空预览缓存"))
            {
                ClearPreviewCache();
            }

            ImGui.TextUnformatted($"偏好设置文件：{GetPreferencesPath()}");
            ImGui.End();
            _state.ShowSettingsWindow = open;
            return;
        }

        DrawDataFoldersEditorUi(showHintText: false);

        ImGui.End();
        _state.ShowSettingsWindow = open;
    }

    private void DrawDataFoldersPanel()
    {
        if (!ImGui.Begin("数据目录"))
        {
            ImGui.End();
            return;
        }

        DrawDataFoldersEditorUi(showHintText: true);

        ImGui.End();
    }

    private void DrawDataFoldersEditorUi(bool showHintText)
    {
        if (showHintText)
        {
            ImGui.TextUnformatted("提示：修改目录会触发重新索引；监控到资源文件变更会自动刷新。");
            ImGui.Separator();
        }

        if (ImGui.Button("添加目录"))
        {
            _state.DataFolders.Add(new DataFolder { DisplayName = "Data", Path = string.Empty });
            MarkDataFoldersChanged();
        }

        ImGui.SameLine();
        if (ImGui.Button("刷新索引"))
        {
            _state.DataFoldersDirty = true;
        }

        ImGui.Separator();

        for (int i = 0; i < _state.DataFolders.Count; i++)
        {
            DataFolder folder = _state.DataFolders[i];

            ImGui.PushID(i);
            string displayName = folder.DisplayName;
            if (ImGui.InputText("名称", ref displayName, 128))
            {
                folder.DisplayName = displayName;
                MarkDataFoldersChanged();
            }

            string path = folder.Path;
            float browseButtonWidth = ImGui.CalcTextSize("路径").X + ImGui.GetStyle().FramePadding.X * 2.0f;
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(-browseButtonWidth - spacing);
            if (ImGui.InputText("##data_folder_path_input", ref path, 1024))
            {
                folder.Path = path;
                MarkDataFoldersChanged();
            }

            ImGui.SameLine();
            if (ImGui.Button("路径##data_folder_path_browse"))
            {
                _pendingDataFolderBrowseIndex = i;
                _state.PendingBrowserAction = PendingFileAction.PickDataFolderPath;

                string startDir = GetDefaultBrowserStartPath();
                if (!string.IsNullOrWhiteSpace(folder.Path))
                {
                    try
                    {
                        string full = Path.GetFullPath(folder.Path.Trim());
                        if (Directory.Exists(full))
                        {
                            startDir = full;
                        }
                        else
                        {
                            string? dir = Path.GetDirectoryName(full);
                            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            {
                                startDir = dir;
                            }
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                _fileDialog.Open(SimpleFileDialogMode.OpenFolder, "选择数据目录", startDir);
            }

            if (ImGui.Button("删除"))
            {
                _state.DataFolders.RemoveAt(i);
                ImGui.PopID();
                MarkDataFoldersChanged();
                i--;
                continue;
            }

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void MarkDataFoldersChanged()
    {
        _state.DataFoldersDirty = true;
        _state.PreferencesDirty = true;
        _watcherRootsDirty = true;
        _state.BatchHashScanned = false;
    }

    private void DrawBatchHashValidationWindow()
    {
        if (!_state.ShowBatchHashValidation)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(700.0f, 450.0f), ImGuiCond.FirstUseEver);

        bool open = _state.ShowBatchHashValidation;
        if (!ImGui.Begin("批量 Hash 校验", ref open))
        {
            _state.ShowBatchHashValidation = open;
            ImGui.End();
            return;
        }

        _state.ShowBatchHashValidation = open;
        if (!open)
        {
            ImGui.End();
            return;
        }

        if (!_state.BatchHashScanned)
        {
            ScanBatchHashEntries();
            _state.BatchHashScanned = true;
        }

        ImGui.TextUnformatted("选择要校验的 hash 文件：");
        ImGui.Separator();

        if (_state.BatchHashEntries.Count == 0)
        {
            ImGui.TextDisabled("未在数据目录中发现 .wpf.hash 文件。");
            ImGui.End();
            return;
        }

        if (ImGui.Button("全选"))
        {
            for (int i = 0; i < _state.BatchHashEntries.Count; i++)
            {
                _state.BatchHashEntries[i].Selected = true;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("全不选"))
        {
            for (int i = 0; i < _state.BatchHashEntries.Count; i++)
            {
                _state.BatchHashEntries[i].Selected = false;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("校验已选择"))
        {
            ValidateSelectedBatchHashEntries();
        }

        int totalMissing = 0;
        for (int i = 0; i < _state.BatchHashResults.Count; i++)
        {
            BatchHashResult r = _state.BatchHashResults[i];
            if (!string.IsNullOrWhiteSpace(r.Error)) continue;
            totalMissing += r.MissingFromWpfCount;
        }

        bool hasResults = _state.BatchHashResults.Count > 0;
        bool hasMissing = hasResults && totalMissing > 0;

        ImGui.SameLine();
        if (!hasMissing)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("导出缺失到 CSV"))
        {
            _state.PendingBrowserAction = PendingFileAction.ExportBatchMissingHashesCsv;
            _fileDialog.Open(SimpleFileDialogMode.SaveFile, "导出缺失 Hash 到 CSV", GetDefaultBrowserStartPath(), defaultFilename: "missing_hashes.csv");
        }

        if (!hasMissing)
        {
            ImGui.EndDisabled();
        }

        if (hasMissing)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({totalMissing} 个缺失)");
        }

        ImGui.Spacing();

        if (_state.BatchImportRunning)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("导入缺失数据..."))
        {
            _state.PendingBrowserAction = PendingFileAction.ImportMissingDataToWpf;
            _fileDialog.Open(SimpleFileDialogMode.OpenFolder, "选择导入数据根目录", GetDefaultBrowserStartPath());
        }

        if (_state.BatchImportRunning)
        {
            ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "选择一个目录，目录内的每个子文件夹都应以 WPF 文件名命名。\n" +
                "例如：<导入根>/monster10/150/15013.tex\n" +
                "程序会把这些文件注入到数据目录中同名的 monster10.wpf 内（会修改源文件）。");
        }

        if (_state.BatchImportRunning && _state.BatchImportProgress is not null)
        {
            BatchImportProgress p = _state.BatchImportProgress;
            int total = p.TotalFolders;
            int done = p.ProcessedFolders;
            int updated = p.UpdatedWpfCount;
            int added = p.AddedFileCount;
            float fraction = total > 0 ? (done / (float)total) : 0.0f;

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.75f, 0.9f, 1.0f, 1.0f), "导入进行中...");
            ImGui.ProgressBar(fraction, new Vector2(-1.0f, 0.0f));
            ImGui.TextUnformatted($"已处理：{done}/{total}   |   已更新 WPF：{updated}   |   已添加文件：{added}");
            if (!string.IsNullOrWhiteSpace(p.CurrentFolder))
            {
                ImGui.TextUnformatted($"当前：{p.CurrentFolder}");
            }
        }

        ImGui.Separator();

        for (int i = 0; i < _state.BatchHashEntries.Count; i++)
        {
            BatchHashEntry bhe = _state.BatchHashEntries[i];
            bool selected = bhe.Selected;
            if (ImGui.Checkbox(bhe.DisplayName, ref selected))
            {
                bhe.Selected = selected;
            }
        }

        if (_state.BatchHashResults.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted("结果：");

            const ImGuiTableFlags tableFlags =
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable("##BatchResults", 6, tableFlags, new Vector2(0.0f, 0.0f)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("文件", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("Matching", ImGuiTableColumnFlags.WidthFixed, 80.0f);
                ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 80.0f);
                ImGui.TableSetupColumn("New", ImGuiTableColumnFlags.WidthFixed, 60.0f);
                ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 120.0f);
                ImGui.TableHeadersRow();

                for (int i = 0; i < _state.BatchHashResults.Count; i++)
                {
                    BatchHashResult r = _state.BatchHashResults[i];
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(r.DisplayName);

                    if (!string.IsNullOrWhiteSpace(r.Error))
                    {
                        ImGui.TableSetColumnIndex(5);
                        ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), r.Error);
                        continue;
                    }

                    float pct = r.TotalEntries > 0
                        ? (r.MatchCount / (float)r.TotalEntries * 100.0f)
                        : 0.0f;

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{r.TotalEntries}");

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), $"{r.MatchCount}");

                    ImGui.TableSetColumnIndex(3);
                    if (r.MissingFromWpfCount > 0)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"{r.MissingFromWpfCount}");
                    }
                    else
                    {
                        ImGui.Text("0");
                    }

                    ImGui.TableSetColumnIndex(4);
                    if (r.NewInWpfCount > 0)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), $"{r.NewInWpfCount}");
                    }
                    else
                    {
                        ImGui.Text("0");
                    }

                    ImGui.TableSetColumnIndex(5);
                    if (r.MissingFromWpfCount == 0 && r.NewInWpfCount == 0)
                    {
                        ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), $"{pct:F1}% OK");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), $"{pct:F1}% match");
                    }
                }

                ImGui.EndTable();
            }

            int totalAll = 0;
            int matchAll = 0;
            int missingAll = 0;
            int newAll = 0;

            for (int i = 0; i < _state.BatchHashResults.Count; i++)
            {
                BatchHashResult r = _state.BatchHashResults[i];
                if (!string.IsNullOrWhiteSpace(r.Error)) continue;
                totalAll += r.TotalEntries;
                matchAll += r.MatchCount;
                missingAll += r.MissingFromWpfCount;
                newAll += r.NewInWpfCount;
            }

            float overallPct = totalAll > 0
                ? (matchAll / (float)totalAll * 100.0f)
                : 0.0f;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.8f, 0.9f, 1.0f, 1.0f), "总体：");
            ImGui.SameLine();
            ImGui.Text($"{matchAll} / {totalAll} entries matching across {_state.BatchHashResults.Count} files  -");
            ImGui.SameLine();

            Vector4 pctColor = overallPct >= 100.0f
                ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f)
                : overallPct >= 80.0f
                    ? new Vector4(1.0f, 0.85f, 0.3f, 1.0f)
                    : new Vector4(1.0f, 0.4f, 0.4f, 1.0f);

            ImGui.TextColored(pctColor, $"{overallPct:F1}% complete");
            if (missingAll > 0 || newAll > 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  {missingAll} missing");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), $"  {newAll} new");
            }
        }

        ImGui.End();
    }

    private void DrawHashComparisonWindow()
    {
        if (_state.HashTabs.Count == 0)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(0.0f, 300.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Hash Comparison"))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##HashTabs"))
        {
            for (int i = 0; i < _state.HashTabs.Count; i++)
            {
                HashComparisonTab ht = _state.HashTabs[i];
                if (!ht.Open)
                {
                    continue;
                }

                ImGuiTabItemFlags flags = ImGuiTabItemFlags.None;
                if (_state.PendingHashTabSwitch == i)
                {
                    flags |= ImGuiTabItemFlags.SetSelected;
                    _state.PendingHashTabSwitch = -1;
                }

                bool tabOpen = ht.Open;
                if (ImGui.BeginTabItem(ht.Title, ref tabOpen, flags))
                {
                    _state.ActiveHashTabIndex = i;

                    DrawHashComparisonTabBody(ht, i);

                    ImGui.EndTabItem();
                }

                ht.Open = tabOpen;
            }

            ImGui.EndTabBar();
        }

        for (int i = _state.HashTabs.Count - 1; i >= 0; i--)
        {
            if (_state.HashTabs[i].Open)
            {
                continue;
            }

            _state.HashTabs.RemoveAt(i);
            if (_state.ActiveHashTabIndex >= i)
            {
                _state.ActiveHashTabIndex--;
                if (_state.ActiveHashTabIndex < 0 && _state.HashTabs.Count > 0)
                {
                    _state.ActiveHashTabIndex = 0;
                }
            }
        }

        ImGui.End();
    }

    private void DrawHashComparisonTabBody(HashComparisonTab ht, int hashTabIndex)
    {
        WpfHashComparison comp = ht.Comparison;
        int total = comp.Entries.Count;
        float matchPct = total > 0 ? (comp.MatchCount / (float)total * 100.0f) : 0.0f;
        float missingPct = total > 0 ? (comp.MissingFromWpfCount / (float)total * 100.0f) : 0.0f;
        float newPct = total > 0 ? (comp.NewInWpfCount / (float)total * 100.0f) : 0.0f;

        ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1.0f), $"{comp.MatchCount} matching ({matchPct:F1}%)");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"  {comp.MissingFromWpfCount} missing from WPF ({missingPct:F1}%)");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), $"  {comp.NewInWpfCount} new in WPF ({newPct:F1}%)");

        string wpfLabel = ht.WpfPath;
        try
        {
            wpfLabel = Path.GetFileName(ht.WpfPath);
        }
        catch
        {
            // ignore
        }

        ImGui.TextUnformatted($"Total entries: {total}   |   WPF: {wpfLabel}");
        ImGui.Separator();

        string filter = ht.FilterText ?? string.Empty;
        ImGui.SetNextItemWidth(250.0f);
        if (ImGui.InputText("过滤##hash_filter", ref filter, 256))
        {
            ht.FilterText = filter;
            ht.VisibleEntryIndicesDirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("清空##hash_filter_clear") && !string.IsNullOrWhiteSpace(ht.FilterText))
        {
            ht.FilterText = string.Empty;
            ht.VisibleEntryIndicesDirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        bool showMatching = ht.ShowMatching;
        if (ImGui.Checkbox("Matching", ref showMatching))
        {
            ht.ShowMatching = showMatching;
            ht.VisibleEntryIndicesDirty = true;
        }

        ImGui.SameLine();
        bool showMissing = ht.ShowMissing;
        if (ImGui.Checkbox("Missing", ref showMissing))
        {
            ht.ShowMissing = showMissing;
            ht.VisibleEntryIndicesDirty = true;
        }

        ImGui.SameLine();
        bool showNew = ht.ShowNew;
        if (ImGui.Checkbox("New", ref showNew))
        {
            ht.ShowNew = showNew;
            ht.VisibleEntryIndicesDirty = true;
        }

        // Export missing hashes - keep visible even when table scrolls
        {
            bool hasMissing = comp.MissingFromWpfCount > 0;
            if (!hasMissing)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("导出缺失 Hash"))
            {
                string defaultName = "missing.txt";
                try
                {
                    defaultName = Path.GetFileNameWithoutExtension(ht.HashFilePath) + "_missing.txt";
                }
                catch
                {
                    // ignore
                }

                string startDir = string.Empty;
                try
                {
                    startDir = Path.GetDirectoryName(ht.HashFilePath) ?? string.Empty;
                }
                catch
                {
                    startDir = string.Empty;
                }

                _state.PendingBrowserAction = PendingFileAction.ExportMissingHashes;
                _state.PendingExportHashTabIndex = hashTabIndex;
                _fileDialog.Open(SimpleFileDialogMode.SaveFile, "导出缺失 Hash（每行一个 0x...）", startDir, defaultFilename: defaultName);
            }

            if (!hasMissing)
            {
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({comp.MissingFromWpfCount} missing)");
            }
        }

        if (ht.VisibleEntryIndicesDirty)
        {
            RebuildVisibleHashEntryIndices(ht);
        }

        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##HashTable", 4, tableFlags, new Vector2(0.0f, 0.0f)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 60.0f);
            ImGui.TableSetupColumn("Name / Path", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.WidthFixed, 160.0f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110.0f);
            ImGui.TableHeadersRow();

            for (int row = 0; row < ht.VisibleEntryIndices.Count; row++)
            {
                int idx = ht.VisibleEntryIndices[row];
                if ((uint)idx >= (uint)comp.Entries.Count)
                {
                    continue;
                }

                WpfHashComparisonEntry entry = comp.Entries[idx];

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted($"{row + 1}");

                ImGui.TableSetColumnIndex(1);
                if (!string.IsNullOrWhiteSpace(entry.FullPath))
                {
                    ImGui.TextUnformatted(entry.FullPath);
                }
                else if (!string.IsNullOrWhiteSpace(entry.Name))
                {
                    ImGui.TextUnformatted(entry.Name);
                }
                else
                {
                    ImGui.TextDisabled("(unknown)");
                }

                ImGui.TableSetColumnIndex(2);
                ulong h = unchecked((ulong)entry.Hash);
                ImGui.TextUnformatted($"0x{h:X16}");

                ImGui.TableSetColumnIndex(3);
                switch (entry.Status)
                {
                    case WpfHashComparisonStatus.Match:
                        ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "Match");
                        break;
                    case WpfHashComparisonStatus.MissingFromWpf:
                        ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), "Missing");
                        break;
                    case WpfHashComparisonStatus.NewInWpf:
                        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "New");
                        break;
                }
            }

            ImGui.EndTable();
        }
    }

    private static void RebuildVisibleHashEntryIndices(HashComparisonTab ht)
    {
        ht.VisibleEntryIndices.Clear();

        string filter = (ht.FilterText ?? string.Empty).Trim();
        bool useFilter = filter.Length > 0;

        IReadOnlyList<WpfHashComparisonEntry> entries = ht.Comparison.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            WpfHashComparisonEntry e = entries[i];
            bool include = e.Status switch
            {
                WpfHashComparisonStatus.Match => ht.ShowMatching,
                WpfHashComparisonStatus.MissingFromWpf => ht.ShowMissing,
                WpfHashComparisonStatus.NewInWpf => ht.ShowNew,
                _ => true,
            };

            if (!include)
            {
                continue;
            }

            if (useFilter)
            {
                bool matched = (!string.IsNullOrWhiteSpace(e.FullPath) && e.FullPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                               || (!string.IsNullOrWhiteSpace(e.Name) && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

                if (!matched)
                {
                    continue;
                }
            }

            ht.VisibleEntryIndices.Add(i);
        }

        ht.VisibleEntryIndicesDirty = false;
    }

    private void ScanBatchHashEntries()
    {
        _state.BatchHashEntries.Clear();
        _state.BatchHashResults.Clear();

        for (int i = 0; i < _state.DataFolders.Count; i++)
        {
            DataFolder folder = _state.DataFolders[i];
            if (folder is null) continue;
            if (string.IsNullOrWhiteSpace(folder.Path)) continue;

            string root = folder.Path.Trim();
            try
            {
                root = Path.GetFullPath(root);
            }
            catch
            {
                // ignore
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string hashPath in Directory.EnumerateFiles(root, "*.wpf.hash", SearchOption.TopDirectoryOnly))
                {
                    string wpfPath = hashPath;
                    if (wpfPath.EndsWith(".hash", StringComparison.OrdinalIgnoreCase) && wpfPath.Length > 5)
                    {
                        wpfPath = wpfPath.Substring(0, wpfPath.Length - 5);
                    }

                    _state.BatchHashEntries.Add(new BatchHashEntry
                    {
                        HashFilePath = hashPath,
                        WpfPath = wpfPath,
                        DisplayName = Path.GetFileName(hashPath),
                        Selected = true,
                    });
                }
            }
            catch
            {
                // ignore directory errors to match OldProj behavior
            }
        }

        _state.BatchHashEntries.Sort(static (a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private void ValidateSelectedBatchHashEntries()
    {
        _state.BatchHashResults.Clear();

        int selectedCount = 0;

        for (int i = 0; i < _state.BatchHashEntries.Count; i++)
        {
            BatchHashEntry bhe = _state.BatchHashEntries[i];
            if (!bhe.Selected)
            {
                continue;
            }

            selectedCount++;

            var result = new BatchHashResult
            {
                DisplayName = bhe.DisplayName,
            };

            if (!WpfHashFileCodec.TryReadWpfHashFile(bhe.HashFilePath, out WpfHashFileData hashData, out string hashError))
            {
                result.Error = hashError;
                _state.BatchHashResults.Add(result);
                continue;
            }

            if (WpfCodec.TryEnumerateEntriesFromFile(bhe.WpfPath, out List<WpfEntry> wpfEntries, out _))
            {
                WpfHashComparison comp = WpfHashFileCodec.CompareWpfHash(hashData, wpfEntries);
                result.MatchCount = comp.MatchCount;
                result.MissingFromWpfCount = comp.MissingFromWpfCount;
                result.NewInWpfCount = comp.NewInWpfCount;
                result.TotalEntries = comp.Entries.Count;

                for (int e = 0; e < comp.Entries.Count; e++)
                {
                    WpfHashComparisonEntry entry = comp.Entries[e];
                    if (entry.Status == WpfHashComparisonStatus.MissingFromWpf)
                    {
                        result.MissingHashes.Add(entry.Hash);
                    }
                }
            }
            else
            {
                // No WPF available - assume every hash in the file is old/missing so it can still be imported later.
                result.MatchCount = 0;
                result.MissingFromWpfCount = hashData.Hashes.Length;
                result.NewInWpfCount = 0;
                result.TotalEntries = hashData.Hashes.Length;
                result.MissingHashes.AddRange(hashData.Hashes);
            }

            _state.BatchHashResults.Add(result);
        }

        _state.StatusMessage = selectedCount == 0
            ? "批量校验：未选择任何 .wpf.hash 文件。"
            : $"批量校验完成：{selectedCount} 个文件。";
    }

    private void StartBatchImportMissingData(string importRoot)
    {
        if (_state.BatchImportRunning)
        {
            _state.StatusMessage = "导入正在进行中。";
            return;
        }

        if (string.IsNullOrWhiteSpace(importRoot))
        {
            _state.StatusMessage = "导入失败：导入根目录为空。";
            return;
        }

        try
        {
            if (!Directory.Exists(importRoot))
            {
                _state.StatusMessage = $"导入失败：目录不存在: {importRoot}";
                return;
            }
        }
        catch
        {
            // ignore invalid path checks
        }

        var dataRoots = new List<string>(_state.DataFolders.Count);
        for (int i = 0; i < _state.DataFolders.Count; i++)
        {
            DataFolder df = _state.DataFolders[i];
            if (df is null) continue;
            if (string.IsNullOrWhiteSpace(df.Path)) continue;
            dataRoots.Add(df.Path);
        }

        var progress = new BatchImportProgress
        {
            TotalFolders = 0,
            ProcessedFolders = 0,
            UpdatedWpfCount = 0,
            AddedFileCount = 0,
            CurrentFolder = string.Empty,
        };

        _state.BatchImportProgress = progress;
        _state.BatchImportRunning = true;
        _state.BatchImportTask = Task.Run(() => ImportMissingDataWorker(importRoot, dataRoots, progress));
        _state.StatusMessage = "开始导入缺失数据...";
    }

    private static BatchImportResult ImportMissingDataWorker(
        string importRoot,
        IReadOnlyList<string> dataRoots,
        BatchImportProgress progress)
    {
        var result = new BatchImportResult();

        string rootFullPath;
        try
        {
            rootFullPath = Path.GetFullPath(importRoot);
        }
        catch
        {
            rootFullPath = importRoot;
        }

        List<string> wpfFolders = new();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(rootFullPath))
            {
                wpfFolders.Add(dir);
            }
        }
        catch (Exception ex)
        {
            result.ErrorLog = $"Failed to scan import root: {ex.Message}";
            return result;
        }

        result.TotalFolders = wpfFolders.Count;
        progress.TotalFolders = result.TotalFolders;

        for (int i = 0; i < wpfFolders.Count; i++)
        {
            string folderPath = wpfFolders[i];
            string dirName = string.Empty;
            try
            {
                dirName = Path.GetFileName(folderPath) ?? string.Empty;
            }
            catch
            {
                dirName = string.Empty;
            }

            progress.CurrentFolder = dirName;

            string wpfPath = FindMatchingWpfForFolder(dirName, dataRoots);
            if (string.IsNullOrWhiteSpace(wpfPath))
            {
                result.SkippedNoMatchCount++;
                result.ProcessedFolders++;
                progress.ProcessedFolders = result.ProcessedFolders;
                continue;
            }

            if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> existingEntries, out string err))
            {
                result.ErrorLog += Path.GetFileName(wpfPath) + ": " + err + "\n";
                result.ProcessedFolders++;
                progress.ProcessedFolders = result.ProcessedFolders;
                continue;
            }

            var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int e = 0; e < existingEntries.Count; e++)
            {
                WpfEntry entry = existingEntries[e];
                if (entry.IsDirectory) continue;
                if (string.IsNullOrWhiteSpace(entry.FullPath)) continue;
                existingPaths.Add(entry.FullPath);
            }

            var newFiles = new List<WpfPackEntry>();
            try
            {
                foreach (string filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    string rel;
                    try
                    {
                        rel = Path.GetRelativePath(folderPath, filePath).Replace('\\', '/');
                    }
                    catch
                    {
                        rel = string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    if (existingPaths.Contains(rel)) continue;

                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(filePath);
                    }
                    catch (Exception ex)
                    {
                        result.ErrorLog += Path.GetFileName(wpfPath) + " cannot read " + rel + ": " + ex.Message + "\n";
                        continue;
                    }

                    newFiles.Add(new WpfPackEntry(rel, bytes));
                }
            }
            catch (Exception ex)
            {
                result.ErrorLog += Path.GetFileName(wpfPath) + " scan error: " + ex.Message + "\n";
                result.ProcessedFolders++;
                progress.ProcessedFolders = result.ProcessedFolders;
                continue;
            }

            if (newFiles.Count == 0)
            {
                result.ProcessedFolders++;
                progress.ProcessedFolders = result.ProcessedFolders;
                continue;
            }

            // Rebuild WPF archive (fallback-only strategy vs old project append+FAT update).
            var allFiles = new List<WpfPackEntry>(existingEntries.Count + newFiles.Count);
            bool extractionFailed = false;
            try
            {
                using var archive = new WpfArchive();
                if (!archive.Open(wpfPath, out err))
                {
                    result.ErrorLog += Path.GetFileName(wpfPath) + " open: " + err + "\n";
                    extractionFailed = true;
                }
                else
                {
                    for (int e = 0; e < existingEntries.Count; e++)
                    {
                        WpfEntry entry = existingEntries[e];
                        if (entry.IsDirectory) continue;
                        if (string.IsNullOrWhiteSpace(entry.FullPath)) continue;

                        if (!archive.ExtractEntry(entry, out byte[] bytes, out err))
                        {
                            result.ErrorLog += Path.GetFileName(wpfPath) + " extract \"" + entry.FullPath + "\": " + err + "\n";
                            extractionFailed = true;
                            break;
                        }

                        allFiles.Add(new WpfPackEntry(entry.FullPath, bytes));
                    }
                }
            }
            catch (Exception ex)
            {
                result.ErrorLog += Path.GetFileName(wpfPath) + " extract: " + ex.Message + "\n";
                extractionFailed = true;
            }

            if (!extractionFailed)
            {
                allFiles.AddRange(newFiles);

                if (!WpfCodec.TryWriteArchive(wpfPath, allFiles, out err))
                {
                    result.ErrorLog += Path.GetFileName(wpfPath) + " write: " + err + "\n";
                }
                else
                {
                    result.UpdatedWpfCount++;
                    result.AddedFileCount += newFiles.Count;
                    result.UpdatedWpfPaths.Add(wpfPath);
                    progress.UpdatedWpfCount = result.UpdatedWpfCount;
                    progress.AddedFileCount = result.AddedFileCount;
                }
            }

            result.ProcessedFolders++;
            progress.ProcessedFolders = result.ProcessedFolders;
        }

        progress.CurrentFolder = string.Empty;
        return result;
    }

    private static string FindMatchingWpfForFolder(string folderName, IReadOnlyList<string> dataRoots)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return string.Empty;
        }

        for (int i = 0; i < dataRoots.Count; i++)
        {
            string root = dataRoots[i];
            if (string.IsNullOrWhiteSpace(root)) continue;

            string candidate;
            try
            {
                candidate = Path.Combine(root, folderName + ".wpf");
            }
            catch
            {
                continue;
            }

            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore invalid roots
            }
        }

        return string.Empty;
    }

    private void PollBatchImportCompletion()
    {
        if (!_state.BatchImportRunning || _state.BatchImportTask is null)
        {
            return;
        }

        Task<BatchImportResult> task = _state.BatchImportTask;
        if (!task.IsCompleted)
        {
            return;
        }

        try
        {
            BatchImportResult result = task.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(result.ErrorLog))
            {
                _state.StatusMessage = $"导入完成：新增 {result.AddedFileCount} 个文件，更新 {result.UpdatedWpfCount} 个 WPF。";
            }
            else
            {
                _state.StatusMessage =
                    $"导入完成但存在错误（新增 {result.AddedFileCount}，更新 {result.UpdatedWpfCount}，无匹配 {result.SkippedNoMatchCount}）。";
            }

            for (int i = 0; i < result.UpdatedWpfPaths.Count; i++)
            {
                string p = result.UpdatedWpfPaths[i];
                if (!string.IsNullOrWhiteSpace(p))
                {
                    _state.AssetLibrary.EvictWpfArchive(p);
                }
            }
        }
        catch (Exception ex)
        {
            _state.StatusMessage = $"导入失败：{ex.Message}";
        }

        _state.BatchImportRunning = false;
        _state.BatchImportProgress = null;
        _state.BatchImportTask = null;

        // Force a rescan so validation reflects newly written archives.
        _state.BatchHashScanned = false;
        _state.BatchHashResults.Clear();
        _state.DataFoldersDirty = true;
    }

    private void DrawAssetLibraryPanel()
    {
        if (!ImGui.Begin("文件浏览器"))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted($"已发现文件：{_state.AssetLibrary.DiscoveredFiles.Count}");

        if (!string.IsNullOrWhiteSpace(_state.AssetLibrary.LastError))
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "扫描警告/错误：");
            ImGui.TextWrapped(_state.AssetLibrary.LastError);
        }

        ImGui.Separator();
        ImGui.InputText("过滤", ref _assetFilterText, 256);
        ImGui.Separator();

        string filter = _assetFilterText?.Trim() ?? string.Empty;
        DrawImportedAssetsSection(filter);
        ImGui.Separator();
        for (int i = 0; i < _state.AssetLibrary.Roots.Count; i++)
        {
            DrawAssetTreeNode(_state.AssetLibrary.Roots[i], filter);
        }

        ImGui.End();
    }

    private void DrawImportedAssetsSection(string filter)
    {
        IReadOnlyList<ImportedAssetEntry> assets = _state.ImportedAssets;
        if (assets.Count == 0)
        {
            ImGui.TextDisabled("导入资产：暂无（桥接/导入的资产会出现在这里）");
            return;
        }

        bool hasFilter = !string.IsNullOrWhiteSpace(filter);

        bool anyVisible = false;
        for (int i = 0; i < assets.Count; i++)
        {
            ImportedAssetEntry asset = assets[i];
            if (asset is null) continue;

            if (!hasFilter || ImportedAssetMatchesFilter(asset, filter))
            {
                anyVisible = true;
                break;
            }
        }

        if (!anyVisible)
        {
            ImGui.TextDisabled("导入资产：无匹配结果。");
            return;
        }

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow
                                   | ImGuiTreeNodeFlags.SpanAvailWidth
                                   | ImGuiTreeNodeFlags.Framed;
        if (hasFilter)
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }

        bool open = ImGui.TreeNodeEx("导入资产 (Imported)", flags);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip("这里显示通过桥接/导入打开过的资源入口，可双击快速重新打开。");
        }

        if (!open)
        {
            return;
        }

        int removeIndex = -1;
        string selectedPath = _state.PendingImportedSelectionPath ?? string.Empty;

        for (int i = 0; i < assets.Count; i++)
        {
            ImportedAssetEntry asset = assets[i];
            if (asset is null) continue;
            if (hasFilter && !ImportedAssetMatchesFilter(asset, filter)) continue;

            ImGui.PushID(i);

            if (asset.HasChild)
            {
                bool containerSelected = string.Equals(selectedPath, asset.Path, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(selectedPath, asset.ContainerPath, StringComparison.OrdinalIgnoreCase);

                ImGuiTreeNodeFlags entryFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
                if (containerSelected)
                {
                    entryFlags |= ImGuiTreeNodeFlags.Selected;
                    ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                }

                string containerLabel = $"{asset.ContainerDisplayName} [{asset.ContainerTypeLabel}]";
                bool entryOpen = ImGui.TreeNodeEx(containerLabel, entryFlags);
                if (ImGui.IsItemClicked())
                {
                    _state.PendingImportedSelectionPath = asset.ContainerPath;
                }

                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("打开"))
                    {
                        ReopenImportedAsset(asset);
                    }
                    if (ImGui.MenuItem("移除"))
                    {
                        removeIndex = i;
                    }
                    ImGui.EndPopup();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                {
                    ImGui.SetTooltip(asset.ContainerPath);
                }

                if (entryOpen)
                {
                    bool childSelected = string.Equals(selectedPath, asset.Path, StringComparison.OrdinalIgnoreCase);
                    string childLabel = asset.ChildDisplayName
                                        + (string.IsNullOrWhiteSpace(asset.ChildTypeLabel) ? string.Empty : $" [{asset.ChildTypeLabel}]");

                    if (ImGui.Selectable(childLabel, childSelected, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        _state.PendingImportedSelectionPath = asset.Path;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            ReopenImportedAsset(asset);
                        }
                    }

                    if (ImGui.BeginPopupContextItem("imported_child_popup"))
                    {
                        if (ImGui.MenuItem("打开"))
                        {
                            ReopenImportedAsset(asset);
                        }
                        if (ImGui.MenuItem("移除"))
                        {
                            removeIndex = i;
                        }
                        ImGui.EndPopup();
                    }

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                    {
                        ImGui.SetTooltip(asset.Path + "\n双击打开");
                    }

                    ImGui.TreePop();
                }
            }
            else
            {
                bool isSelected = string.Equals(selectedPath, asset.Path, StringComparison.OrdinalIgnoreCase);
                string label = $"{asset.ContainerDisplayName} [{asset.ContainerTypeLabel}]";
                if (ImGui.Selectable(label, isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _state.PendingImportedSelectionPath = asset.Path;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        ReopenImportedAsset(asset);
                    }
                }

                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("打开"))
                    {
                        ReopenImportedAsset(asset);
                    }
                    if (ImGui.MenuItem("移除"))
                    {
                        removeIndex = i;
                    }
                    ImGui.EndPopup();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
                {
                    ImGui.SetTooltip(asset.Path + "\n双击打开");
                }
            }

            ImGui.PopID();
        }

        if (removeIndex >= 0 && removeIndex < _state.ImportedAssets.Count)
        {
            _state.ImportedAssets.RemoveAt(removeIndex);
            if (removeIndex == 0)
            {
                _state.PendingImportedSelectionPath = _state.ImportedAssets.Count > 0 ? _state.ImportedAssets[0].Path : string.Empty;
            }
        }

        ImGui.TreePop();
    }

    private static bool ImportedAssetMatchesFilter(ImportedAssetEntry asset, string filter)
    {
        string needle = filter ?? string.Empty;
        if (needle.Length == 0) return true;

        if (!string.IsNullOrWhiteSpace(asset.ContainerDisplayName)
            && asset.ContainerDisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(asset.Path)
            && asset.Path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (asset.HasChild
            && !string.IsNullOrWhiteSpace(asset.ChildDisplayName)
            && asset.ChildDisplayName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private void ReopenImportedAsset(ImportedAssetEntry asset)
    {
        if (asset is null || string.IsNullOrWhiteSpace(asset.Path))
        {
            return;
        }

        string normalizedKey = NormalizeImportedAssetKey(asset.Path);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            _state.StatusMessage = "打开失败：导入资产路径为空。";
            return;
        }

        // Bump to top so it behaves like the old editor's imported-asset history.
        RememberImportedAsset(normalizedKey, asset.SelectedImageIndex);

        string title = asset.HasChild && !string.IsNullOrWhiteSpace(asset.ChildDisplayName)
            ? asset.ChildDisplayName
            : (asset.ContainerDisplayName ?? normalizedKey);

        OpenFileInTab(normalizedKey, title);

        if (asset.SelectedImageIndex >= 0
            && GetExtensionSafe(normalizedKey).Equals(".sgl", StringComparison.OrdinalIgnoreCase)
            && TryFindOpenTabByKey(normalizedKey, out ImageTab? tab))
        {
            tab!.SelectedImageIndex = asset.SelectedImageIndex;
            tab.SelectedFrame = 0;
        }
    }

    private void RememberImportedAsset(string targetPath, int selectedImageIndex)
    {
        string normalized = NormalizeImportedAssetKey(targetPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        ImportedAssetEntry entry = BuildImportedAssetEntry(normalized, selectedImageIndex);

        int existingIndex = -1;
        for (int i = 0; i < _state.ImportedAssets.Count; i++)
        {
            ImportedAssetEntry existing = _state.ImportedAssets[i];
            if (existing is null) continue;

            if (!string.IsNullOrWhiteSpace(entry.ContainerPath)
                && string.Equals(existing.ContainerPath, entry.ContainerPath, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }

            if (string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            _state.ImportedAssets.RemoveAt(existingIndex);
        }

        _state.ImportedAssets.Insert(0, entry);

        const int limit = 32;
        if (_state.ImportedAssets.Count > limit)
        {
            _state.ImportedAssets.RemoveRange(limit, _state.ImportedAssets.Count - limit);
        }

        _state.PendingImportedSelectionPath = entry.Path;
        _state.ShowLibraryTexturesPanel = true;
    }

    private static ImportedAssetEntry BuildImportedAssetEntry(string normalizedTargetPath, int selectedImageIndex)
    {
        var entry = new ImportedAssetEntry
        {
            Path = normalizedTargetPath ?? string.Empty,
            SelectedImageIndex = selectedImageIndex,
        };

        string wpfPath;
        string entryPath;
        if (TryParseWpfKeyLoose(entry.Path, out wpfPath, out entryPath))
        {
            string archiveName = wpfPath;
            try
            {
                archiveName = Path.GetFileName(wpfPath);
                if (string.IsNullOrWhiteSpace(archiveName))
                {
                    archiveName = wpfPath;
                }
            }
            catch
            {
                archiveName = wpfPath;
            }

            entry.ContainerPath = wpfPath;
            entry.ContainerDisplayName = archiveName;
            entry.ContainerTypeLabel = "WPF";
            entry.HasChild = true;

            string leaf = entryPath;
            string ext = string.Empty;
            try
            {
                ext = Path.GetExtension(leaf);
            }
            catch
            {
                ext = string.Empty;
            }

            if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
            {
                entry.ChildTypeLabel = "SGL";
                entry.ChildDisplayName = selectedImageIndex >= 0
                    ? (leaf + " -> Image " + selectedImageIndex)
                    : leaf;
            }
            else if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            {
                entry.ChildTypeLabel = "TEX";
                entry.ChildDisplayName = leaf;
            }
            else
            {
                entry.ChildTypeLabel = "FILE";
                entry.ChildDisplayName = leaf;
            }

            return entry;
        }

        entry.ContainerPath = entry.Path;
        try
        {
            entry.ContainerDisplayName = Path.GetFileName(entry.Path);
        }
        catch
        {
            entry.ContainerDisplayName = entry.Path;
        }

        string fileExt = string.Empty;
        try
        {
            fileExt = Path.GetExtension(entry.Path);
        }
        catch
        {
            fileExt = string.Empty;
        }

        if (fileExt.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            entry.ContainerTypeLabel = "SGL";
            if (selectedImageIndex >= 0)
            {
                entry.HasChild = true;
                entry.ChildDisplayName = "Image " + selectedImageIndex;
                entry.ChildTypeLabel = "IMG";
            }
        }
        else if (fileExt.Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            entry.ContainerTypeLabel = "TEX";
        }
        else if (fileExt.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            entry.ContainerTypeLabel = "WPF";
        }
        else
        {
            entry.ContainerTypeLabel = "FILE";
        }

        return entry;
    }

    private static string NormalizeImportedAssetKey(string targetPath)
    {
        string raw = (targetPath ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        if (TryParseWpfKeyLoose(raw, out string wpfPath, out string entryPath))
        {
            string normalizedWpf;
            try
            {
                normalizedWpf = Path.GetFullPath(wpfPath);
            }
            catch
            {
                normalizedWpf = wpfPath;
            }

            string normalizedEntry = (entryPath ?? string.Empty).Trim();
            normalizedEntry = normalizedEntry.Replace('\\', '/');
            while (normalizedEntry.StartsWith("/", StringComparison.Ordinal))
            {
                normalizedEntry = normalizedEntry.Substring(1);
            }

            return WpfKey.Make(normalizedWpf, normalizedEntry);
        }

        try
        {
            return Path.GetFullPath(raw);
        }
        catch
        {
            return raw;
        }
    }

    private static bool TryParseWpfKeyLoose(string key, out string wpfPath, out string entryPath)
    {
        wpfPath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        int sep = key.IndexOf(WpfKey.Separator, StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        int entryStart = sep + WpfKey.Separator.Length;
        if (entryStart >= key.Length)
        {
            return false;
        }

        wpfPath = key.Substring(0, sep);
        entryPath = key.Substring(entryStart);

        while (entryPath.Length > 0 && (entryPath[0] == '/' || entryPath[0] == '\\'))
        {
            entryPath = entryPath.Substring(1);
        }

        entryPath = entryPath.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(wpfPath) && !string.IsNullOrWhiteSpace(entryPath);
    }

    private bool TryFindOpenTabByKey(string key, out ImageTab? tab)
    {
        tab = null;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        for (int i = 0; i < _state.Tabs.Count; i++)
        {
            ImageTab t = _state.Tabs[i];
            if (t is null) continue;

            if (string.Equals(t.SglKey, key, StringComparison.OrdinalIgnoreCase))
            {
                tab = t;
                return true;
            }
        }

        return false;
    }

    private void DrainEditorBridgeRequests()
    {
        LocalEditorBridge? bridge = _state.EditorBridge;
        if (bridge is null || !bridge.Initialized)
        {
            return;
        }

        List<EditorBridgeRequest> requests = bridge.DrainRequests();
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
                case EditorBridgeRequestKind.OpenAsset:
                    if (TryOpenBridgeAssetTarget(req.Path, req.ImageIndex, out string msg))
                    {
                        if (!string.IsNullOrWhiteSpace(msg))
                        {
                            _state.StatusMessage = msg;
                        }
                    }
                    else
                    {
                        _state.StatusMessage = string.IsNullOrWhiteSpace(msg) ? "桥接打开资产失败。" : msg;
                    }
                    break;

                case EditorBridgeRequestKind.ReloadDataFolder:
                    _state.DataFoldersDirty = true;
                    _liveReloadQueued = true;
                    _liveReloadOverflowed = true;
                    _liveReloadChangedPathCount = 0;
                    _liveReloadChangedPaths.Clear();

                    string reloadDetail = req.ExtraPath?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(reloadDetail))
                    {
                        _state.StatusMessage = string.IsNullOrWhiteSpace(req.Path)
                            ? "桥接：收到刷新索引请求，准备重建并刷新已打开页签…"
                            : $"桥接：收到刷新索引请求：{req.Path}，准备重建并刷新已打开页签…";
                    }
                    else
                    {
                        _state.StatusMessage = string.IsNullOrWhiteSpace(req.Path)
                            ? $"桥接：{reloadDetail}（准备重建并刷新已打开页签…）"
                            : $"桥接：{reloadDetail}（data={req.Path}，准备重建并刷新已打开页签…）";
                    }
                    break;

                // MapEditor consumes OpenMap; ContentEditor consumes OpenAsset.
                default:
                    break;
            }
        }
    }

    private bool TryOpenBridgeAssetTarget(string targetPath, int selectedImageIndex, out string message)
    {
        message = string.Empty;

        string normalizedKey = NormalizeImportedAssetKey(targetPath);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            message = "桥接打开失败：路径为空。";
            return false;
        }

        RememberImportedAsset(normalizedKey, selectedImageIndex);

        ImportedAssetEntry entry = BuildImportedAssetEntry(normalizedKey, selectedImageIndex);
        string title = entry.HasChild && !string.IsNullOrWhiteSpace(entry.ChildDisplayName)
            ? entry.ChildDisplayName
            : (entry.ContainerDisplayName ?? normalizedKey);

        OpenFileInTab(normalizedKey, title);

        if (selectedImageIndex >= 0
            && GetExtensionSafe(normalizedKey).Equals(".sgl", StringComparison.OrdinalIgnoreCase)
            && TryFindOpenTabByKey(normalizedKey, out ImageTab? tab))
        {
            tab!.SelectedImageIndex = selectedImageIndex;
            tab.SelectedFrame = 0;
        }

        message = "桥接：已打开资产";
        return true;
    }

    private void DrawAssetTreeNode(AssetTreeNode node, string filter)
    {
        if (node is null)
        {
            return;
        }

        if (!HasAnyMatch(node, filter))
        {
            return;
        }

        if (node.IsDirectory)
        {
            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            bool open = ImGui.TreeNodeEx(node.Name, flags);
            if (open)
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    DrawAssetTreeNode(node.Children[i], filter);
                }
                ImGui.TreePop();
            }
            return;
        }

        DiscoveredFile? file = node.File;
        if (file is null)
        {
            ImGui.BulletText(node.Name);
            return;
        }

        bool selected = IsSelected(file);
        if (ImGui.Selectable(node.Name, selected, ImGuiSelectableFlags.AllowDoubleClick))
        {
            SelectFile(file);
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            TryOpenPath(file.FullPath);
        }

        if (ImGui.BeginPopupContextItem())
        {
            string ext = GetExtensionSafe(file.FullPath);
            bool isMap = NmpFileName.IsMapExtension(ext);
            bool isPrefab = !isMap && NmpFileName.IsPrefabExtension(ext);
            bool isMapLike = isMap || isPrefab;

            if (isMapLike)
            {
                bool enabled = _state.EditorBridge is not null;
                if (ImGui.MenuItem("在 MapEditor 中打开", null, selected: false, enabled: enabled))
                {
                    TryOpenPath(file.FullPath);
                }

                if (!enabled
                    && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("EditorBridge 未初始化，无法发送打开请求。");
                }
            }
            else if (ImGui.MenuItem("打开"))
            {
                TryOpenPath(file.FullPath);
            }

            if (ImGui.MenuItem("复制路径"))
            {
                ImGui.SetClipboardText(file.FullPath);
                _state.StatusMessage = "已复制路径到剪贴板。";
            }

            ImGui.EndPopup();
        }
    }

    private static bool HasAnyMatch(AssetTreeNode node, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (NodeMatches(node, filter))
        {
            return true;
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (HasAnyMatch(node.Children[i], filter))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NodeMatches(AssetTreeNode node, string filter)
    {
        if (node is null) return false;

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

    private bool IsSelected(DiscoveredFile file)
    {
        if (_state.SelectedAssetIndex < 0)
        {
            return false;
        }

        IReadOnlyList<DiscoveredFile> list = _state.AssetLibrary.DiscoveredFiles;
        if (_state.SelectedAssetIndex >= list.Count)
        {
            return false;
        }

        return ReferenceEquals(list[_state.SelectedAssetIndex], file);
    }

    private void SelectFile(DiscoveredFile file)
    {
        IReadOnlyList<DiscoveredFile> list = _state.AssetLibrary.DiscoveredFiles;
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], file))
            {
                _state.SelectedAssetIndex = i;
                return;
            }
        }

        _state.SelectedAssetIndex = -1;
    }

    private void DrawWorkspacePanel()
    {
        if (!ImGui.Begin("工作区"))
        {
            ImGui.End();
            return;
        }

        if (_state.Tabs.Count == 0)
        {
            ImGui.TextUnformatted("双击左侧文件以打开标签页。");
            ImGui.End();
            return;
        }

        if (!ImGui.BeginTabBar("content_tabs", ImGuiTabBarFlags.Reorderable))
        {
            ImGui.End();
            return;
        }

        int previousActive = _state.ActiveTabIndex;
        var closedIndices = new List<int>();

        for (int i = 0; i < _state.Tabs.Count; i++)
        {
            ImageTab tab = _state.Tabs[i];
            if (!tab.Open)
            {
                closedIndices.Add(i);
                continue;
            }

            bool open = tab.Open;
            string displayLabel = tab.Title ?? string.Empty;
            if (tab.HasUnsavedChanges)
            {
                displayLabel += " *";
            }

            // Use "displayLabel###sglKey" so ImGui identifies each tab by its unique key,
            // allowing tabs with the same visible title to coexist.
            string idKey = string.IsNullOrWhiteSpace(tab.SglKey) ? i.ToString() : tab.SglKey;
            string tabImGuiLabel = $"{displayLabel}###{idKey}";

            ImGuiTabItemFlags flags = ImGuiTabItemFlags.None;
            if (_state.PendingTabSwitch == i)
            {
                flags |= ImGuiTabItemFlags.SetSelected;
                _state.PendingTabSwitch = -1;
            }

            if (ImGui.BeginTabItem(tabImGuiLabel, ref open, flags))
            {
                if (_state.ActiveTabIndex != i)
                {
                    _state.ActiveTabIndex = i;
                }

                tab.LastActiveFrame = _state.AssetLibrary.FrameIndex;
                DrawTabContent(tab);
                ImGui.EndTabItem();
            }

            if (tab.Open != open)
            {
                tab.Open = open;
                if (!open)
                {
                    closedIndices.Add(i);
                }
                _state.PreferencesDirty = true;
            }
        }

        if (previousActive != _state.ActiveTabIndex)
        {
            _state.PreferencesDirty = true;
        }

        if (closedIndices.Count > 0)
        {
            closedIndices.Sort();
            for (int idx = closedIndices.Count - 1; idx >= 0; idx--)
            {
                int i = closedIndices[idx];
                if (i >= 0 && i < _state.Tabs.Count)
                {
                    string sglKey = _state.Tabs[i].SglKey;
                    EvictPreviewTexturesForSource(sglKey);
                    _state.Tabs.RemoveAt(i);
                    if (_state.ActiveTabIndex >= i)
                    {
                        _state.ActiveTabIndex--;
                    }
                }
            }

            if (_state.ActiveTabIndex < 0 && _state.Tabs.Count > 0)
            {
                _state.ActiveTabIndex = 0;
            }
        }

        ImGui.EndTabBar();
        ImGui.End();
    }

    private void DrawTabContent(ImageTab tab)
    {
        if (tab is null)
        {
            ImGui.TextUnformatted("标签页为空。");
            return;
        }

        string key = tab.SglKey ?? string.Empty;
        ImGui.TextUnformatted($"键(Key)：{key}");
        if (tab.HasUnsavedChanges)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.2f, 1.0f), "（未保存）");
        }
        ImGui.Separator();

        if (string.IsNullOrWhiteSpace(key))
        {
            ImGui.TextUnformatted("路径为空。");
            return;
        }

        if (IsWpfHashPath(key))
        {
            ImGui.TextUnformatted("这是 .wpf.hash 文件（当前仅支持解析/对比，预览与导出尚未接入）。");
            return;
        }

        string ext = GetExtensionSafe(key);
        if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            DrawTexTab(tab, key);
            return;
        }

        if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            DrawSglTab(tab, key);
            return;
        }

        if (ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            DrawWpfTab(tab, key);
            return;
        }

        ImGui.TextUnformatted("当前仅支持预览：.tex / .sgl（WPF/WPF.hash 的更多功能会在后续迁移）。");
    }

    private void DrawTexTab(ImageTab tab, string texPath)
    {
        int frameCount = GetTexFrameCount(texPath);
        if (frameCount < 1) frameCount = 1;

        int frame = tab.SelectedFrame;
        if (frame < 0) frame = 0;
        if (frame >= frameCount) frame = frameCount - 1;

        if (frameCount > 1)
        {
            ImGui.SliderInt("帧", ref frame, 0, frameCount - 1);
            tab.SelectedFrame = frame;
        }
        else
        {
            tab.SelectedFrame = 0;
            frame = 0;
        }

        float zoom = tab.PreviewZoom;
        if (ImGui.SliderFloat("缩放", ref zoom, 0.1f, 8.0f, "%.2f"))
        {
            tab.PreviewZoom = zoom;
        }

        if (ImGui.Button("导出 PNG（同目录）"))
        {
            if (!TryExportTexFrameToPng(texPath, frame))
            {
                // StatusMessage 已在内部设置
            }
        }

        ImGui.Separator();

        int maxSide = Math.Clamp(_state.PreviewMaxSide, 64, 4096);
        string previewKey = MakePreviewKey(texPath, imageIndex: -1, frame, maxSide);

        if (TryDrawPreview(previewKey, tab.PreviewZoom))
        {
            return;
        }

        if (!_pendingPreviewDecodes.ContainsKey(previewKey))
        {
            _pendingPreviewDecodes[previewKey] = Task.Run(() => DecodeTexPreview(texPath, frame, maxSide));
        }

        DrawPreviewStatus(previewKey);
    }

    private void DrawSglTab(ImageTab tab, string sglPath)
    {
        SglLoadStatus status = _state.AssetLibrary.GetSglLoadStatus(sglPath);
        if (status == SglLoadStatus.NotStarted)
        {
            status = _state.AssetLibrary.RequestSglLibraryAsync(sglPath);
        }

        if (status == SglLoadStatus.Loading)
        {
            ImGui.TextUnformatted("正在异步加载 SGL...");
            return;
        }

        if (status == SglLoadStatus.Failed)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "SGL 加载失败：");
            string err = _state.AssetLibrary.GetSglLoadError(sglPath);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(err) ? "未知错误" : err);

            if (ImGui.Button("重试加载"))
            {
                EvictPreviewTexturesForSource(sglPath);
                _state.AssetLibrary.RequestSglLibraryAsync(sglPath, forceReload: true);
            }

            return;
        }

        var library = _state.AssetLibrary.GetSglLibrary(sglPath);
        if (library is null || !library.IsOpen())
        {
            ImGui.TextUnformatted("SGL 状态为“就绪”，但库对象不可用（可尝试重试加载）。");
            if (ImGui.Button("重试加载"))
            {
                EvictPreviewTexturesForSource(sglPath);
                _state.AssetLibrary.RequestSglLibraryAsync(sglPath, forceReload: true);
            }
            return;
        }

        int imageCount = library.GetImageCount();
        if (imageCount <= 0)
        {
            ImGui.TextUnformatted("该 SGL 不包含图片条目。");
            return;
        }

        if (tab.SelectedImageIndex < 0 || tab.SelectedImageIndex >= imageCount)
        {
            tab.SelectedImageIndex = 0;
            tab.SelectedFrame = 0;
        }

        Vector2 avail = ImGui.GetContentRegionAvail();
        float leftWidth = Math.Min(260.0f, Math.Max(160.0f, avail.X * 0.30f));

        ImGui.BeginChild("##sgl_list", new Vector2(leftWidth, 0), (ImGuiChildFlags)0);
        ImGui.TextUnformatted($"图片数：{imageCount}");
        ImGui.Separator();

        int maxListCount = Math.Min(imageCount, 10000);
        if (imageCount > maxListCount)
        {
            ImGui.TextUnformatted($"（列表仅显示前 {maxListCount} 项，避免卡顿）");
        }

        for (int i = 0; i < maxListCount; i++)
        {
            bool selected = tab.SelectedImageIndex == i;
            if (ImGui.Selectable($"{i}", selected))
            {
                tab.SelectedImageIndex = i;
                tab.SelectedFrame = 0;
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##sgl_preview", new Vector2(0, 0), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);

        int frameCount = library.GetFrameCount(tab.SelectedImageIndex);
        if (frameCount < 1) frameCount = 1;

        int frame = tab.SelectedFrame;
        if (frame < 0) frame = 0;
        if (frame >= frameCount) frame = frameCount - 1;

        if (frameCount > 1)
        {
            ImGui.SliderInt("帧", ref frame, 0, frameCount - 1);
            tab.SelectedFrame = frame;
        }
        else
        {
            tab.SelectedFrame = 0;
            frame = 0;
        }

        float zoom = tab.PreviewZoom;
        if (ImGui.SliderFloat("缩放", ref zoom, 0.1f, 8.0f, "%.2f"))
        {
            tab.PreviewZoom = zoom;
        }

        if (ImGui.Button("导出 PNG（同目录）"))
        {
            if (!TryExportSglImageToPng(sglPath, library, tab.SelectedImageIndex, tab.SelectedFrame))
            {
                // StatusMessage 已在内部设置
            }
        }

        ImGui.Separator();

        int maxSide = Math.Clamp(_state.PreviewMaxSide, 64, 4096);
        string previewKey = MakePreviewKey(sglPath, tab.SelectedImageIndex, tab.SelectedFrame, maxSide);

        if (TryDrawPreview(previewKey, tab.PreviewZoom))
        {
            ImGui.EndChild();
            return;
        }

        if (!_pendingPreviewDecodes.ContainsKey(previewKey))
        {
            int imgIndex = tab.SelectedImageIndex;
            int imgFrame = tab.SelectedFrame;
            _pendingPreviewDecodes[previewKey] = Task.Run(() => DecodeSglPreview(library, imgIndex, imgFrame, maxSide));
        }

        DrawPreviewStatus(previewKey);
        ImGui.EndChild();
    }

    private void DrawWpfTab(ImageTab tab, string wpfPath)
    {
        WpfLoadStatus status = _state.AssetLibrary.GetWpfLoadStatus(wpfPath);
        if (status == WpfLoadStatus.NotStarted)
        {
            status = _state.AssetLibrary.RequestWpfArchiveAsync(wpfPath);
        }

        if (status == WpfLoadStatus.Loading)
        {
            ImGui.TextUnformatted("正在异步加载 WPF...");
            return;
        }

        if (status == WpfLoadStatus.Failed)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "WPF 加载失败：");
            string err = _state.AssetLibrary.GetWpfLoadError(wpfPath);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(err) ? "未知错误" : err);

            if (ImGui.Button("重试加载"))
            {
                _state.AssetLibrary.RequestWpfArchiveAsync(wpfPath, forceReload: true);
            }

            return;
        }

        var archive = _state.AssetLibrary.GetWpfArchive(wpfPath);
        if (archive is null || !archive.IsOpen())
        {
            ImGui.TextUnformatted("WPF 状态为“就绪”，但归档对象不可用（可尝试重试加载）。");
            if (ImGui.Button("重试加载"))
            {
                _state.AssetLibrary.RequestWpfArchiveAsync(wpfPath, forceReload: true);
            }
            return;
        }

        int count = archive.GetEntryCount();
        string wpfLabel = wpfPath;
        try
        {
            wpfLabel = Path.GetFileName(wpfPath);
        }
        catch
        {
            // ignore
        }

        ImGui.TextUnformatted($"WPF：{wpfLabel}");
        ImGui.TextUnformatted($"条目数：{count}");

        if (ImGui.Button("导出全部到文件夹..."))
        {
            _state.PendingBrowserAction = PendingFileAction.ExportWpfToFolder;
            _state.PendingExportWpfPath = wpfPath;

            string startDir = string.Empty;
            try
            {
                startDir = Path.GetDirectoryName(wpfPath) ?? string.Empty;
            }
            catch
            {
                startDir = GetDefaultBrowserStartPath();
            }

            _fileDialog.Open(SimpleFileDialogMode.OpenFolder, "选择导出目录（将按 WPF 内路径写出文件）", startDir);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("将 WPF 内全部文件解包到指定目录（会保留子目录结构）");
        }

        ImGui.SameLine();
        if (ImGui.Button("重新加载 WPF"))
        {
            _state.AssetLibrary.RequestWpfArchiveAsync(wpfPath, forceReload: true);
            return;
        }

        ImGui.Separator();

        WpfArchiveTree tree = _state.AssetLibrary.GetWpfTree(wpfPath)
            ?? WpfArchiveTree.BuildFromEntries(archive.GetEntries());

        string filter = tab.WpfFilterText ?? string.Empty;
        ImGui.SetNextItemWidth(250.0f);
        if (ImGui.InputText("过滤##wpf_filter", ref filter, 256))
        {
            tab.WpfFilterText = filter;
        }

        ImGui.SameLine();
        if (ImGui.Button("清空##wpf_filter_clear") && !string.IsNullOrWhiteSpace(tab.WpfFilterText))
        {
            tab.WpfFilterText = string.Empty;
        }

        EnsureWpfFilterMatches(tab, tree);

        Vector2 avail = ImGui.GetContentRegionAvail();
        float leftWidth = Math.Min(320.0f, Math.Max(180.0f, avail.X * 0.35f));

        ImGui.BeginChild("##wpf_dirs", new Vector2(leftWidth, 0), (ImGuiChildFlags)0);
        DrawWpfDirectoryTree(tab, tree);
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##wpf_files", new Vector2(0, 0), (ImGuiChildFlags)0);
        DrawWpfFileList(tab, wpfPath, tree);
        ImGui.EndChild();
    }

    private static void EnsureWpfFilterMatches(ImageTab tab, WpfArchiveTree tree)
    {
        if (tab is null || tree is null)
        {
            return;
        }

        string filter = (tab.WpfFilterText ?? string.Empty).Trim();
        if (string.Equals(tab.WpfFilterLastApplied, filter, StringComparison.Ordinal))
        {
            return;
        }

        tab.WpfFilterLastApplied = filter;
        tab.WpfMatchedDirs.Clear();

        if (string.IsNullOrWhiteSpace(filter))
        {
            return;
        }

        foreach (WpfEntry entry in tree.EntryByFullPath.Values)
        {
            if (entry is null) continue;

            string name = entry.Name ?? string.Empty;
            string full = entry.FullPath ?? string.Empty;

            if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                && full.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string dir = entry.IsDirectory ? full : WpfKey.GetDirectoryPath(full);
            while (true)
            {
                tab.WpfMatchedDirs.Add(dir);
                if (string.IsNullOrWhiteSpace(dir))
                {
                    break;
                }

                dir = WpfKey.GetDirectoryPath(dir);
            }
        }

        tab.WpfMatchedDirs.Add(string.Empty);
    }

    private void DrawWpfDirectoryTree(ImageTab tab, WpfArchiveTree tree)
    {
        if (tab is null || tree is null)
        {
            return;
        }

        bool selectedRoot = string.IsNullOrWhiteSpace(tab.WpfSelectedFolder);
        if (ImGui.Selectable("（根目录）", selectedRoot))
        {
            tab.WpfSelectedFolder = string.Empty;
        }

        ImGui.Separator();

        bool filterActive = !string.IsNullOrWhiteSpace(tab.WpfFilterText);
        DrawWpfDirectoryChildren(tab, tree, parentPath: string.Empty, filterActive: filterActive);
    }

    private void DrawWpfDirectoryChildren(ImageTab tab, WpfArchiveTree tree, string parentPath, bool filterActive)
    {
        if (!tree.Nodes.TryGetValue(parentPath, out WpfTreeNode? node))
        {
            return;
        }

        for (int i = 0; i < node.ChildDirs.Count; i++)
        {
            WpfEntry dirEntry = node.ChildDirs[i];
            if (dirEntry is null) continue;

            string dirPath = dirEntry.FullPath ?? string.Empty;
            if (filterActive && tab.WpfMatchedDirs.Count > 0 && !tab.WpfMatchedDirs.Contains(dirPath))
            {
                continue;
            }

            string label = string.IsNullOrWhiteSpace(dirEntry.Name)
                ? $"dir_{dirEntry.Index}"
                : dirEntry.Name;

            ImGui.PushID(dirEntry.Index);

            bool selected = string.Equals(tab.WpfSelectedFolder, dirPath, StringComparison.OrdinalIgnoreCase);

            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (selected)
            {
                flags |= ImGuiTreeNodeFlags.Selected;
            }

            bool containsSelectedDescendant = !selected && IsWpfAncestorOrSelf(dirPath, tab.WpfSelectedFolder);
            if (filterActive || containsSelectedDescendant)
            {
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            }

            bool open = ImGui.TreeNodeEx(label, flags);
            if (ImGui.IsItemClicked())
            {
                tab.WpfSelectedFolder = dirPath;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            {
                if (tree.Nodes.TryGetValue(dirPath, out WpfTreeNode? dirNode) && dirNode.TexChildren.Count > 0)
                {
                    ImGui.SetTooltip($"{dirPath}\n（包含 {dirNode.TexChildren.Count} 个直系 TEX 子项）");
                }
                else
                {
                    ImGui.SetTooltip(dirPath);
                }
            }

            if (open)
            {
                DrawWpfDirectoryChildren(tab, tree, dirPath, filterActive);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }
    }

    private static bool IsWpfAncestorOrSelf(string ancestor, string path)
    {
        if (string.IsNullOrWhiteSpace(ancestor)) return true;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length < ancestor.Length) return false;

        if (!path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == ancestor.Length || path[ancestor.Length] == '/';
    }

    private void DrawWpfFileList(ImageTab tab, string wpfPath, WpfArchiveTree tree)
    {
        if (tab is null || tree is null)
        {
            return;
        }

        string folder = tab.WpfSelectedFolder ?? string.Empty;
        if (!tree.Nodes.ContainsKey(folder))
        {
            folder = string.Empty;
            tab.WpfSelectedFolder = string.Empty;
        }

        if (!tree.Nodes.TryGetValue(folder, out WpfTreeNode? node))
        {
            ImGui.TextDisabled("无法访问该目录节点。");
            return;
        }

        string folderLabel = string.IsNullOrWhiteSpace(folder) ? "/" : folder;
        ImGui.TextUnformatted($"目录：{folderLabel}");
        ImGui.Separator();

        string filter = (tab.WpfFilterText ?? string.Empty).Trim();
        bool filterActive = !string.IsNullOrWhiteSpace(filter);

        const ImGuiTableFlags tableFlags =
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp |
            ImGuiTableFlags.NoBordersInBody;

        if (!ImGui.BeginTable("##wpf_file_table", 4, tableFlags, new Vector2(0.0f, 0.0f)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 60.0f);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 90.0f);
        ImGui.TableSetupColumn("Hash", ImGuiTableColumnFlags.WidthFixed, 160.0f);
        ImGui.TableHeadersRow();

        for (int i = 0; i < node.ChildFiles.Count; i++)
        {
            WpfEntry e = node.ChildFiles[i];
            if (e is null) continue;

            string displayName = string.IsNullOrWhiteSpace(e.Name) ? WpfKey.GetLeafName(e.FullPath) : e.Name;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = $"entry_{e.Index}";
            }

            if (filterActive
                && displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                && (e.FullPath ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string fullPath = e.FullPath ?? string.Empty;
            string ext = string.Empty;
            try
            {
                ext = Path.GetExtension(fullPath);
            }
            catch
            {
                ext = string.Empty;
            }

            string typeLabel = "FILE";
            bool openable = false;
            bool canSendToMapEditor = false;
            bool isPrefab = false;
            if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
            {
                typeLabel = "SGL";
                openable = true;
            }
            else if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            {
                typeLabel = "TEX";
                openable = true;
            }
            else if (NmpFileName.IsMapExtension(ext))
            {
                typeLabel = "MAP";
                canSendToMapEditor = true;
            }
            else if (NmpFileName.IsPrefabExtension(ext))
            {
                typeLabel = "PREFAB";
                canSendToMapEditor = true;
                isPrefab = true;
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);

            ImGuiSelectableFlags selFlags = ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick;
            if (ImGui.Selectable(displayName, false, selFlags))
            {
                if (openable && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    string key = WpfKey.Make(wpfPath, fullPath);
                    string title;
                    try
                    {
                        title = displayName + " (" + Path.GetFileName(wpfPath) + ")";
                    }
                    catch
                    {
                        title = displayName;
                    }

                    int selectedImageIndex = ext.Equals(".tex", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
                    RememberImportedAsset(key, selectedImageIndex);
                    OpenFileInTab(key, title);
                }
                else if (canSendToMapEditor && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    if (_state.EditorBridge is null)
                    {
                        _state.StatusMessage = "打开失败：EditorBridge 未初始化。";
                    }
                    else
                    {
                        string resolvedWpfPath = wpfPath;
                        try
                        {
                            resolvedWpfPath = Path.GetFullPath(wpfPath);
                        }
                        catch
                        {
                            // ignore
                        }

                        string syntheticPath = LocalEditorBridge.MakeEditorBridgeWpfPath(resolvedWpfPath, fullPath);
                        if (_state.EditorBridge.SendOpenMap(syntheticPath, out string bridgeError))
                        {
                            bool running = _state.EditorBridge.IsAppRunning(EditorBridgeApp.MapEditor);
                            string status = running ? "已发送到 MapEditor" : "已加入队列（MapEditor 未运行）";
                            string typeLabel2 = isPrefab ? "预制体" : "地图";
                            _state.StatusMessage = $"桥接：{status}打开{typeLabel2}：{displayName}";
                        }
                        else
                        {
                            string typeLabel2 = isPrefab ? "预制体" : "地图";
                            _state.StatusMessage = string.IsNullOrWhiteSpace(bridgeError) ? $"桥接：发送打开{typeLabel2}请求失败。" : bridgeError;
                        }
                    }
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            {
                if (openable)
                {
                    ImGui.SetTooltip($"{fullPath}\n双击打开");
                }
                else if (canSendToMapEditor)
                {
                    ImGui.SetTooltip($"{fullPath}\n双击发送到 MapEditor");
                }
                else
                {
                    ImGui.SetTooltip(fullPath);
                }
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(typeLabel);

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted($"{e.ByteSize}");

            ImGui.TableSetColumnIndex(3);
            ulong u = unchecked((ulong)e.Hash);
            ImGui.TextUnformatted("0x" + u.ToString("X16"));
        }

        ImGui.EndTable();
    }

    private static bool IsWpfHashPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string filename = Path.GetFileName(path);
        return filename.EndsWith(".wpf.hash", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExtensionSafe(string path)
    {
        try
        {
            return Path.GetExtension(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private int GetTexFrameCount(string texPath)
    {
        string key = texPath;
        if (_texFrameCountCache.TryGetValue(key, out int cached))
        {
            return cached;
        }

        byte[] bytes;
        if (WpfKey.TryParse(key, out string wpfPath, out string entryPath))
        {
            if (!_state.AssetLibrary.TryExtractWpfEntryBytes(wpfPath, entryPath, out bytes, out _))
            {
                _texFrameCountCache[key] = 1;
                return 1;
            }
        }
        else if (!FileIO.TryReadAllBytes(key, out bytes, out _))
        {
            _texFrameCountCache[key] = 1;
            return 1;
        }

        int count = TexCodec.GetFrameCount(bytes);
        if (count < 1) count = 1;
        _texFrameCountCache[key] = count;
        return count;
    }

    private static string MakePreviewKey(string sourceKey, int imageIndex, int frame, int maxSide)
    {
        return $"{sourceKey}||{imageIndex}||{frame}||{maxSide}";
    }

    private bool TryDrawPreview(string previewKey, float zoom)
    {
        if (_previewTextures.TryGetValue(previewKey, out PreviewTexture? tex) && tex.TextureId != nint.Zero)
        {
            tex.LastUsedFrame = _state.AssetLibrary.FrameIndex;
            Vector2 size = new(tex.Width * zoom, tex.Height * zoom);

            ImGui.BeginChild("##preview_canvas", new Vector2(0, 0), (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
            ImGui.Image(tex.TextureId, size);
            ImGui.EndChild();
            return true;
        }

        return false;
    }

    private void DrawPreviewStatus(string previewKey)
    {
        if (_previewErrors.TryGetValue(previewKey, out string? err) && !string.IsNullOrWhiteSpace(err))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "预览失败：");
            ImGui.TextWrapped(err);
            if (ImGui.Button("清除预览错误"))
            {
                _previewErrors.Remove(previewKey);
            }
            return;
        }

        ImGui.TextUnformatted("预览加载中...");
    }

    private PreviewDecodeResult DecodeTexPreview(string texPath, int frame, int maxSide)
    {
        byte[] bytes;
        if (WpfKey.TryParse(texPath, out string wpfPath, out string entryPath))
        {
            if (!_state.AssetLibrary.TryExtractWpfEntryBytes(wpfPath, entryPath, out bytes, out string readErr))
            {
                return new PreviewDecodeResult(false, readErr, null);
            }
        }
        else if (!FileIO.TryReadAllBytes(texPath, out bytes, out string readErr))
        {
            return new PreviewDecodeResult(false, readErr, null);
        }

        if (!TexCodec.TryDecodeRgba8(bytes, out DecodedImage image, out string decodeErr, frame))
        {
            return new PreviewDecodeResult(false, decodeErr, null);
        }

        if (!image.IsValid)
        {
            return new PreviewDecodeResult(false, "解码输出无效", null);
        }

        if (maxSide > 0)
        {
            image = ScaleNearest(image, maxSide);
        }

        return new PreviewDecodeResult(true, string.Empty, image);
    }

    private static PreviewDecodeResult DecodeSglPreview(SglLibrary library, int imageIndex, int frame, int maxSide)
    {
        DecodedImage? decoded = library.GetImage(imageIndex, frame);
        if (decoded is null || !decoded.IsValid)
        {
            return new PreviewDecodeResult(false, "解码失败（可能为空槽或格式不支持）", null);
        }

        DecodedImage image = decoded;
        if (maxSide > 0)
        {
            image = ScaleNearest(image, maxSide);
        }

        return new PreviewDecodeResult(true, string.Empty, image);
    }

    private static DecodedImage ScaleNearest(DecodedImage src, int maxSide)
    {
        if (!src.IsValid)
        {
            return src;
        }

        if (maxSide <= 0)
        {
            return src;
        }

        int maxDim = Math.Max(src.Width, src.Height);
        if (maxDim <= maxSide)
        {
            return src;
        }

        double scale = maxSide / (double)maxDim;
        int outW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int outH = Math.Max(1, (int)Math.Round(src.Height * scale));

        var dst = new DecodedImage
        {
            Width = outW,
            Height = outH,
            OffsetX = src.OffsetX,
            OffsetY = src.OffsetY,
            CenterX = src.CenterX,
            CenterY = src.CenterY,
            Rgba8 = new byte[outW * outH * 4],
        };

        ReadOnlySpan<byte> s = src.Rgba8;
        Span<byte> d = dst.Rgba8;
        int srcStride = src.Width * 4;
        int dstStride = outW * 4;

        for (int y = 0; y < outH; y++)
        {
            int srcY = (int)((long)y * src.Height / outH);
            int srcRow = srcY * srcStride;
            int dstRow = y * dstStride;

            for (int x = 0; x < outW; x++)
            {
                int srcX = (int)((long)x * src.Width / outW);
                int srcOff = srcRow + srcX * 4;
                int dstOff = dstRow + x * 4;
                d[dstOff + 0] = s[srcOff + 0];
                d[dstOff + 1] = s[srcOff + 1];
                d[dstOff + 2] = s[srcOff + 2];
                d[dstOff + 3] = s[srcOff + 3];
            }
        }

        return dst;
    }

    private void ProcessPreviewDecodes()
    {
        if (_renderer is null)
        {
            return;
        }

        if (_pendingPreviewDecodes.Count == 0)
        {
            return;
        }

        const int budget = 2;
        var completed = new List<string>(capacity: Math.Min(_pendingPreviewDecodes.Count, budget));

        foreach ((string key, Task<PreviewDecodeResult> task) in _pendingPreviewDecodes)
        {
            if (!task.IsCompleted)
            {
                continue;
            }

            completed.Add(key);
            if (completed.Count >= budget)
            {
                break;
            }
        }

        for (int i = 0; i < completed.Count; i++)
        {
            string key = completed[i];
            if (!_pendingPreviewDecodes.Remove(key, out Task<PreviewDecodeResult>? task))
            {
                continue;
            }

            PreviewDecodeResult result;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                result = new PreviewDecodeResult(false, ex.Message, null);
            }

            if (!result.Ok || result.Image is null || !result.Image.IsValid)
            {
                _previewErrors[key] = string.IsNullOrWhiteSpace(result.Error) ? "预览解码失败" : result.Error;
                continue;
            }

            if (!_renderer.TryCreateImGuiTextureRgba8(result.Image.Rgba8, result.Image.Width, result.Image.Height, out nint textureId, out string error))
            {
                _previewErrors[key] = string.IsNullOrWhiteSpace(error) ? "创建 GPU 纹理失败" : error;
                continue;
            }

            if (_previewTextures.TryGetValue(key, out PreviewTexture? old) && old.TextureId != nint.Zero)
            {
                _renderer.DestroyImGuiTexture(old.TextureId);
            }

            _previewTextures[key] = new PreviewTexture
            {
                TextureId = textureId,
                Width = result.Image.Width,
                Height = result.Image.Height,
                LastUsedFrame = _state.AssetLibrary.FrameIndex,
            };

            _previewErrors.Remove(key);
        }
    }

    private void PrunePreviewCache()
    {
        if (_renderer is null)
        {
            return;
        }

        int limit = Math.Clamp(_state.PreviewCacheMaxItems, 8, 2048);
        if (_previewTextures.Count <= limit)
        {
            return;
        }

        var entries = new List<KeyValuePair<string, PreviewTexture>>(_previewTextures);
        entries.Sort(static (a, b) => a.Value.LastUsedFrame.CompareTo(b.Value.LastUsedFrame));

        int toRemove = _previewTextures.Count - limit;
        for (int i = 0; i < toRemove; i++)
        {
            string key = entries[i].Key;
            PreviewTexture tex = entries[i].Value;
            if (tex.TextureId != nint.Zero)
            {
                _renderer.DestroyImGuiTexture(tex.TextureId);
            }
            _previewTextures.Remove(key);
            _previewErrors.Remove(key);
        }
    }

    private void ClearPreviewCache()
    {
        if (_renderer is not null)
        {
            foreach (PreviewTexture tex in _previewTextures.Values)
            {
                if (tex.TextureId != nint.Zero)
                {
                    _renderer.DestroyImGuiTexture(tex.TextureId);
                }
            }
        }

        _previewTextures.Clear();
        _pendingPreviewDecodes.Clear();
        _previewErrors.Clear();
    }

    private void EvictPreviewTexturesForSource(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        string prefix = sourceKey + "||";
        var keys = new List<string>();

        foreach (string key in _previewTextures.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(key);
            }
        }

        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            if (_previewTextures.Remove(key, out PreviewTexture? tex) && _renderer is not null && tex.TextureId != nint.Zero)
            {
                _renderer.DestroyImGuiTexture(tex.TextureId);
            }

            _previewErrors.Remove(key);
            _pendingPreviewDecodes.Remove(key);
        }
    }

    private void DisposePreviewResources()
    {
        ClearPreviewCache();
        _texFrameCountCache.Clear();
        _renderer = null;
    }

    private void PrimeTabOnOpen(ImageTab tab)
    {
        if (tab is null)
        {
            return;
        }

        string key = tab.SglKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (IsWpfHashPath(key))
        {
            return;
        }

        string ext = GetExtensionSafe(key);
        if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            tab.SelectedImageIndex = Math.Max(0, tab.SelectedImageIndex);
            tab.SelectedFrame = 0;
            _state.AssetLibrary.RequestSglLibraryAsync(key);
            return;
        }

        if (ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            _state.AssetLibrary.RequestWpfArchiveAsync(key);
        }
    }

    private bool TryExportTexFrameToPng(string texPath, int frame)
    {
        byte[] bytes;
        string dir;
        string baseName;

        if (WpfKey.TryParse(texPath, out string wpfPath, out string entryPath))
        {
            if (!_state.AssetLibrary.TryExtractWpfEntryBytes(wpfPath, entryPath, out bytes, out string readErr))
            {
                _state.StatusMessage = readErr;
                return false;
            }

            dir = Path.GetDirectoryName(Path.GetFullPath(wpfPath)) ?? Environment.CurrentDirectory;
            string leaf = WpfKey.GetLeafName(entryPath);
            baseName = $"{Path.GetFileNameWithoutExtension(wpfPath)}_{Path.GetFileNameWithoutExtension(leaf)}";
        }
        else
        {
            if (!FileIO.TryReadAllBytes(texPath, out bytes, out string readErr))
            {
                _state.StatusMessage = readErr;
                return false;
            }

            dir = Path.GetDirectoryName(Path.GetFullPath(texPath)) ?? Environment.CurrentDirectory;
            baseName = Path.GetFileNameWithoutExtension(texPath);
        }

        if (!TexCodec.TryDecodeRgba8(bytes, out DecodedImage image, out string decodeErr, frame))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(decodeErr) ? "导出失败：解码失败" : decodeErr;
            return false;
        }

        if (!image.IsValid)
        {
            _state.StatusMessage = "导出失败：解码输出无效";
            return false;
        }

        string outPath = Path.Combine(dir, $"{baseName}_f{frame}.png");

        if (!PngWriter.TryWriteRgba8(outPath, image.Width, image.Height, image.Rgba8, out string err))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(err) ? "导出失败：写 PNG 失败" : err;
            return false;
        }

        _state.StatusMessage = $"已导出：{outPath}";
        return true;
    }

    private bool TryExportSglImageToPng(string sglPath, SglLibrary library, int imageIndex, int frame)
    {
        if (library is null || !library.IsOpen())
        {
            _state.StatusMessage = "导出失败：SGL 未加载";
            return false;
        }

        DecodedImage? image = library.GetImage(imageIndex, frame);
        if (image is null || !image.IsValid)
        {
            _state.StatusMessage = "导出失败：解码失败（可能为空槽或格式不支持）";
            return false;
        }

        string dir;
        string baseName;
        if (WpfKey.TryParse(sglPath, out string wpfPath, out string entryPath))
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(wpfPath)) ?? Environment.CurrentDirectory;
            string leaf = WpfKey.GetLeafName(entryPath);
            baseName = $"{Path.GetFileNameWithoutExtension(wpfPath)}_{Path.GetFileNameWithoutExtension(leaf)}";
        }
        else
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(sglPath)) ?? Environment.CurrentDirectory;
            baseName = Path.GetFileNameWithoutExtension(sglPath);
        }
        string outPath = Path.Combine(dir, $"{baseName}_idx{imageIndex}_f{frame}.png");

        if (!PngWriter.TryWriteRgba8(outPath, image.Width, image.Height, image.Rgba8, out string err))
        {
            _state.StatusMessage = string.IsNullOrWhiteSpace(err) ? "导出失败：写 PNG 失败" : err;
            return false;
        }

        _state.StatusMessage = $"已导出：{outPath}";
        return true;
    }

    private void OpenHashComparisonTab(string hashPath)
    {
        if (string.IsNullOrWhiteSpace(hashPath))
        {
            return;
        }

        string pathStr;
        try
        {
            pathStr = Path.GetFullPath(hashPath);
        }
        catch
        {
            pathStr = hashPath;
        }

        for (int i = 0; i < _state.HashTabs.Count; i++)
        {
            HashComparisonTab ht = _state.HashTabs[i];
            if (string.Equals(ht.HashFilePath, pathStr, StringComparison.OrdinalIgnoreCase))
            {
                ht.Open = true;
                _state.ActiveHashTabIndex = i;
                _state.PendingHashTabSwitch = i;
                return;
            }
        }

        if (!WpfHashFileCodec.TryReadWpfHashFile(pathStr, out WpfHashFileData hashData, out string hashError))
        {
            _state.StatusMessage = hashError;
            return;
        }

        string wpfPathStr = pathStr;
        if (wpfPathStr.EndsWith(".hash", StringComparison.OrdinalIgnoreCase) && wpfPathStr.Length >= 5)
        {
            wpfPathStr = wpfPathStr.Substring(0, wpfPathStr.Length - 5);
        }

        if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPathStr, out List<WpfEntry> wpfEntries, out string wpfError))
        {
            _state.StatusMessage = $"Cannot open adjacent WPF: {wpfError}";
            return;
        }

        WpfHashComparison comparison = WpfHashFileCodec.CompareWpfHash(hashData, wpfEntries);

        var tab = new HashComparisonTab
        {
            Title = Path.GetFileName(pathStr),
            HashFilePath = pathStr,
            WpfPath = wpfPathStr,
            Comparison = comparison,
            Open = true,
        };

        _state.HashTabs.Add(tab);
        _state.ActiveHashTabIndex = _state.HashTabs.Count - 1;
        _state.PendingHashTabSwitch = _state.ActiveHashTabIndex;
        _state.StatusMessage = string.Empty;

        _state.WpfHashCache[wpfPathStr] = comparison;
    }

    private void OpenFileInTab(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (IsWpfHashPath(path))
        {
            OpenHashComparisonTab(path);
            return;
        }

        string key;
        try
        {
            key = Path.GetFullPath(path);
        }
        catch
        {
            key = path;
        }

        for (int i = 0; i < _state.Tabs.Count; i++)
        {
            ImageTab tab = _state.Tabs[i];
            if (string.Equals(tab.SglKey, key, StringComparison.OrdinalIgnoreCase))
            {
                tab.Open = true;
                _state.ActiveTabIndex = i;
                _state.PendingTabSwitch = i;
                _state.PreferencesDirty = true;
                return;
            }
        }

        var newTab = new ImageTab
        {
            Title = string.IsNullOrWhiteSpace(title) ? key : title,
            SglKey = key,
            Open = true,
            Loading = false,
            SelectedImageIndex = -1,
            SelectedFrame = 0,
        };

        _state.Tabs.Add(newTab);
        PrimeTabOnOpen(newTab);

        _state.ActiveTabIndex = _state.Tabs.Count - 1;
        _state.PendingTabSwitch = _state.ActiveTabIndex;
        _state.PreferencesDirty = true;
    }

    private void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        int existingIndex = -1;
        for (int i = 0; i < _state.RecentFiles.Count; i++)
        {
            if (string.Equals(_state.RecentFiles[i], path, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            _state.RecentFiles.RemoveAt(existingIndex);
        }

        _state.RecentFiles.Insert(0, path);

        const int limit = 20;
        if (_state.RecentFiles.Count > limit)
        {
            _state.RecentFiles.RemoveRange(limit, _state.RecentFiles.Count - limit);
        }

        _state.PreferencesDirty = true;
    }

    private void DrawInformationPanel()
    {
        if (!ImGui.Begin("资源信息"))
        {
            ImGui.End();
            return;
        }

        DiscoveredFile? selected = null;
        if (_state.SelectedAssetIndex >= 0 && _state.SelectedAssetIndex < _state.AssetLibrary.DiscoveredFiles.Count)
        {
            selected = _state.AssetLibrary.DiscoveredFiles[_state.SelectedAssetIndex];
        }

        if (selected is null)
        {
            ImGui.TextUnformatted("未选择文件。");
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted($"名称：{selected.DisplayName}");
        ImGui.TextUnformatted($"路径：{selected.FullPath}");
        ImGui.TextUnformatted($"根目录：{selected.RootDisplayName} ({selected.RootPath})");

        try
        {
            var info = new FileInfo(selected.FullPath);
            if (info.Exists)
            {
                ImGui.TextUnformatted($"大小：{info.Length} bytes");
                ImGui.TextUnformatted($"修改时间：{info.LastWriteTime}");
            }
        }
        catch
        {
            // ignore
        }

        ImGui.Separator();

        if (selected.IsTex)
        {
            int frames = GetTexFrameCount(selected.FullPath);
            ImGui.TextUnformatted($"TEX 帧数：{frames}");
        }

        if (selected.IsSgl)
        {
            SglLoadStatus status = _state.AssetLibrary.GetSglLoadStatus(selected.FullPath);
            ImGui.TextUnformatted($"SGL 状态：{status}");

            if (status is SglLoadStatus.NotStarted or SglLoadStatus.Failed)
            {
                if (ImGui.Button(status == SglLoadStatus.Failed ? "重试异步加载 SGL" : "异步加载 SGL"))
                {
                    EvictPreviewTexturesForSource(selected.FullPath);
                    _state.AssetLibrary.RequestSglLibraryAsync(selected.FullPath, forceReload: status == SglLoadStatus.Failed);
                }
            }
            else if (status == SglLoadStatus.Ready)
            {
                var lib = _state.AssetLibrary.GetSglLibrary(selected.FullPath);
                if (lib is not null && lib.IsOpen())
                {
                    ImGui.TextUnformatted($"SGL 图片数：{lib.GetImageCount()}");
                }

                if (ImGui.Button("重新加载 SGL"))
                {
                    EvictPreviewTexturesForSource(selected.FullPath);
                    _state.AssetLibrary.RequestSglLibraryAsync(selected.FullPath, forceReload: true);
                }
            }

            if (status == SglLoadStatus.Failed)
            {
                string err = _state.AssetLibrary.GetSglLoadError(selected.FullPath);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "错误：");
                    ImGui.TextWrapped(err);
                }
            }
        }

        if (selected.IsWpf)
        {
            WpfLoadStatus status = _state.AssetLibrary.GetWpfLoadStatus(selected.FullPath);
            ImGui.TextUnformatted($"WPF 状态：{status}");

            if (status is WpfLoadStatus.NotStarted or WpfLoadStatus.Failed)
            {
                if (ImGui.Button(status == WpfLoadStatus.Failed ? "重试异步加载 WPF" : "异步加载 WPF"))
                {
                    _state.AssetLibrary.RequestWpfArchiveAsync(selected.FullPath, forceReload: status == WpfLoadStatus.Failed);
                }
            }
            else if (status == WpfLoadStatus.Ready)
            {
                var archive = _state.AssetLibrary.GetWpfArchive(selected.FullPath);
                if (archive is not null && archive.IsOpen())
                {
            ImGui.TextUnformatted($"WPF 条目数：{archive.GetEntryCount()}");
                }

                if (ImGui.Button("重新加载 WPF"))
                {
                    _state.AssetLibrary.RequestWpfArchiveAsync(selected.FullPath, forceReload: true);
                }
            }

            if (status == WpfLoadStatus.Failed)
            {
                string err = _state.AssetLibrary.GetWpfLoadError(selected.FullPath);
                if (!string.IsNullOrWhiteSpace(err))
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "错误：");
                    ImGui.TextWrapped(err);
                }
            }
        }

        ImGui.End();
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

        string message = _state.StatusMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "就绪";
        }

        ImGui.TextUnformatted(message);

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 220);
        ImGui.TextUnformatted($"根目录：{_state.AssetLibrary.Roots.Count}  文件：{_state.AssetLibrary.DiscoveredFiles.Count}");

        ImGui.End();
    }

    private sealed class PreviewTexture
    {
        public nint TextureId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public ulong LastUsedFrame { get; set; }
    }

    private readonly record struct PreviewDecodeResult(bool Ok, string Error, DecodedImage? Image);
}
