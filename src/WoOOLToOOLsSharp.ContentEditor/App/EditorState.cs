using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WoOOLToOOLsSharp.Shared.EditorBridge;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.ContentEditor.App;

public enum SettingsSection
{
    Application,
    DataPaths,
}

public enum UiTheme
{
    Dark,
    Light,
}

public sealed class DataFolder
{
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public enum OffsetMode
{
    Ignore,
    Add,
    Subtract,
}

public sealed class ImageTab
{
    public string Title { get; set; } = string.Empty;
    public string SglKey { get; set; } = string.Empty;
    public int SelectedImageIndex { get; set; } = -1;
    public int SelectedFrame { get; set; }
    public bool Open { get; set; } = true;
    public bool Loading { get; set; }
    public string LoadingMessage { get; set; } = string.Empty;

    public bool HasUnsavedChanges { get; set; }
    public bool CacheEvicted { get; set; }
    public ulong LastActiveFrame { get; set; }
    public float LastGridScrollY { get; set; }

    public float PreviewZoom { get; set; } = 1.0f;
    public float CameraX { get; set; }
    public float CameraY { get; set; }

    public OffsetMode OffsetMode { get; set; } = OffsetMode.Ignore;

    public bool CanSaveToSource { get; set; }

    // --- WPF tab state (only used when SglKey points to a .wpf) ----------------

    public string WpfSelectedFolder { get; set; } = string.Empty; // "" means WPF root
    public string WpfFilterText { get; set; } = string.Empty;
    public string WpfFilterLastApplied { get; set; } = string.Empty;
    public HashSet<string> WpfMatchedDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum PendingFileAction
{
    None,
    InsertAfter,
    InsertBefore,
    ReplaceSelected,
    BlankCell,
    SaveAsCopy,
    ConvertFolderToWpf,
    CreateSglFromFolder,
    ExportWpfToFolder,
    ExportSglToFolder,
    ExportMissingHashes,
    ExportBatchMissingHashesCsv,
    ImportMissingDataToWpf,
    OpenByPath,
    PickDataFolderPath,
}

public sealed class ConfirmDialogState
{
    public bool Open { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public PendingFileAction Action { get; set; } = PendingFileAction.None;
    public string Path { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

public sealed class ColumnBrowserDialog
{
    public string CurrentPath { get; set; } = string.Empty;
}

public sealed class ImportedAssetEntry
{
    public string Path { get; set; } = string.Empty;
    public string ContainerPath { get; set; } = string.Empty;
    public string ContainerDisplayName { get; set; } = string.Empty;
    public string ContainerTypeLabel { get; set; } = string.Empty;
    public string ChildDisplayName { get; set; } = string.Empty;
    public string ChildTypeLabel { get; set; } = string.Empty;
    public bool HasChild { get; set; }
    public int SelectedImageIndex { get; set; } = -1;
}

public sealed class HashComparisonTab
{
    public string Title { get; set; } = string.Empty;
    public string HashFilePath { get; set; } = string.Empty;
    public string WpfPath { get; set; } = string.Empty;
    public bool Open { get; set; } = true;
    public WpfHashComparison Comparison { get; set; } = new();
    public string FilterText { get; set; } = string.Empty;
    public bool ShowMatching { get; set; } = true;
    public bool ShowMissing { get; set; } = true;
    public bool ShowNew { get; set; } = true;
    public List<int> VisibleEntryIndices { get; } = new();
    public bool VisibleEntryIndicesDirty { get; set; } = true;
}

public sealed class WpfHashLoadResult
{
    public string WpfPath { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public WpfHashComparison Comparison { get; set; } = new();
}

public sealed class PendingWpfHashLoad
{
    public string WpfPath { get; set; } = string.Empty;
    public Task<WpfHashLoadResult>? Task { get; set; }
}

public sealed class BatchHashEntry
{
    public string HashFilePath { get; set; } = string.Empty;
    public string WpfPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Selected { get; set; } = true;
}

public sealed class BatchHashResult
{
    public string DisplayName { get; set; } = string.Empty;
    public int MatchCount { get; set; }
    public int MissingFromWpfCount { get; set; }
    public int NewInWpfCount { get; set; }
    public int TotalEntries { get; set; }
    public string Error { get; set; } = string.Empty;
    public List<long> MissingHashes { get; } = new();
}

public sealed class BatchImportProgress
{
    public int TotalFolders { get; set; }
    public int ProcessedFolders { get; set; }
    public int UpdatedWpfCount { get; set; }
    public int AddedFileCount { get; set; }

    public string CurrentFolder { get; set; } = string.Empty;
}

public sealed class BatchImportResult
{
    public int TotalFolders { get; set; }
    public int ProcessedFolders { get; set; }
    public int UpdatedWpfCount { get; set; }
    public int AddedFileCount { get; set; }
    public int SkippedNoMatchCount { get; set; }
    public string ErrorLog { get; set; } = string.Empty;
    public List<string> UpdatedWpfPaths { get; } = new();
}

public readonly record struct SavedTabInfo(string Title, string SglKey);

public sealed class EditorState
{
    public bool RequestExit { get; set; }
    public bool ShowSettingsWindow { get; set; }
    public bool ShowDataFoldersPanel { get; set; } = true;
    public bool ShowInformationPanel { get; set; } = true;
    public bool ShowLibraryTexturesPanel { get; set; } = true;
    public bool RequestResetLayout { get; set; } = true;
    public bool DataFoldersDirty { get; set; } = true;
    public bool PreferencesDirty { get; set; }
    public bool RestoreState { get; set; } = true;
    public SettingsSection CurrentSettingsSection { get; set; } = SettingsSection.Application;

    public float UiScale { get; set; } = 1.0f;
    public int GridCellSize { get; set; } = 64;
    public UiTheme Theme { get; set; } = UiTheme.Dark;
    public int PreviewMaxSide { get; set; } = 1024;
    public int PreviewCacheMaxItems { get; set; } = 64;
    public bool HideEmptyCells { get; set; }
    public string DataFolderSearchFilter { get; set; } = string.Empty;
    public int SelectedFolderIndex { get; set; } = -1;
    public int SelectedAssetIndex { get; set; } = -1;
    public int ActiveTabIndex { get; set; } = -1;
    public int PendingTabSwitch { get; set; } = -1;

    public string StatusMessage { get; set; } = string.Empty;
    public LocalEditorBridge? EditorBridge { get; set; }
    public bool MapEditorRunning { get; set; }

    public List<ImportedAssetEntry> ImportedAssets { get; } = new();
    public string PendingImportedSelectionPath { get; set; } = string.Empty;

    public ColumnBrowserDialog FolderBrowser { get; } = new();
    public int FolderBrowseRowIndex { get; set; } = -1;

    public ColumnBrowserDialog FileBrowser { get; } = new();
    public PendingFileAction PendingBrowserAction { get; set; } = PendingFileAction.None;
    public string PendingExportWpfPath { get; set; } = string.Empty;
    public string PendingExportSglKey { get; set; } = string.Empty;
    public int PendingExportHashTabIndex { get; set; } = -1;
    public string PendingEditSglKey { get; set; } = string.Empty;
    public int PendingEditImageIndex { get; set; } = -1;
    public ConfirmDialogState ConfirmDialog { get; } = new();

    public List<DataFolder> DataFolders { get; } = new();
    public AssetLibrary AssetLibrary { get; } = new();

    public List<ImageTab> Tabs { get; } = new();

    public List<HashComparisonTab> HashTabs { get; } = new();
    public int ActiveHashTabIndex { get; set; } = -1;
    public int PendingHashTabSwitch { get; set; } = -1;

    public Dictionary<string, WpfHashComparison> WpfHashCache { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<PendingWpfHashLoad> PendingWpfHashLoads { get; } = new();

    public bool ShowBatchHashValidation { get; set; }
    public List<BatchHashEntry> BatchHashEntries { get; } = new();
    public List<BatchHashResult> BatchHashResults { get; } = new();
    public bool BatchHashScanned { get; set; }
    public bool BatchImportRunning { get; set; }
    public BatchImportProgress? BatchImportProgress { get; set; }
    public Task<BatchImportResult>? BatchImportTask { get; set; }

    public List<SavedTabInfo> PendingTabRestore { get; } = new();
    public int PendingActiveTabIndex { get; set; } = -1;

    public List<string> RecentFiles { get; } = new();
}
