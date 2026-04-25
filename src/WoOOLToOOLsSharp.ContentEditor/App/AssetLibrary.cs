using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Sgl;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.ContentEditor.App;

public sealed class DiscoveredFile
{
    public string DisplayName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsWpf { get; init; }
    public bool IsSgl { get; init; }
    public bool IsTex { get; init; }
    public bool IsWpfHash { get; init; }
    public string RootDisplayName { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
}

public sealed class AssetTreeNode
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public DiscoveredFile? File { get; init; }
    public List<AssetTreeNode> Children { get; } = new();
}

public enum WpfLoadStatus
{
    NotStarted,
    Loading,
    Ready,
    Failed,
}

public enum SglLoadStatus
{
    NotStarted,
    Loading,
    Ready,
    Failed,
}

public sealed class AssetLibrary
{
    private readonly List<DiscoveredFile> _discoveredFiles = new();
    private readonly List<AssetTreeNode> _roots = new();

    private readonly Dictionary<string, WpfLoadState> _wpfLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SglLoadState> _sglLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EditableSglInfo> _editableSgls = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DiscoveredFile> DiscoveredFiles => _discoveredFiles;
    public IReadOnlyList<AssetTreeNode> Roots => _roots;

    public ulong FrameIndex { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    public void TickFrame()
    {
        FrameIndex++;
        ProcessAsyncLoads();
    }

    // --- Async WPF/SGL load ------------------------------------------------

    public WpfLoadStatus RequestWpfArchiveAsync(string wpfPath, bool forceReload = false)
    {
        string key = NormalizeLibraryKey(wpfPath);
        if (string.IsNullOrWhiteSpace(key))
        {
            return WpfLoadStatus.Failed;
        }

        if (!_wpfLoads.TryGetValue(key, out WpfLoadState? state))
        {
            state = new WpfLoadState(key);
            _wpfLoads[key] = state;
        }
        else if (forceReload)
        {
            state.Evict();
        }

        if (state.Status is WpfLoadStatus.Ready or WpfLoadStatus.Loading)
        {
            return state.Status;
        }

        state.Status = WpfLoadStatus.Loading;
        state.Error = string.Empty;
        state.Task = Task.Run(() =>
        {
            var archive = new WpfArchive();
            if (!archive.Open(wpfPath, out string error))
            {
                archive.Dispose();
                return new WpfLoadResult(false, error, null);
            }

            return new WpfLoadResult(true, string.Empty, archive);
        });

        return state.Status;
    }

    public WpfLoadStatus GetWpfLoadStatus(string wpfPath)
    {
        string key = NormalizeLibraryKey(wpfPath);
        return _wpfLoads.TryGetValue(key, out WpfLoadState? state) ? state.Status : WpfLoadStatus.NotStarted;
    }

    public string GetWpfLoadError(string wpfPath)
    {
        string key = NormalizeLibraryKey(wpfPath);
        return _wpfLoads.TryGetValue(key, out WpfLoadState? state) ? state.Error : string.Empty;
    }

    public WpfArchive? GetWpfArchive(string wpfPath)
    {
        string key = NormalizeLibraryKey(wpfPath);
        if (!_wpfLoads.TryGetValue(key, out WpfLoadState? state))
        {
            return null;
        }

        return state.Status == WpfLoadStatus.Ready ? state.Archive : null;
    }

    public WpfArchiveTree? GetWpfTree(string wpfPath)
    {
        string key = NormalizeLibraryKey(wpfPath);
        if (!_wpfLoads.TryGetValue(key, out WpfLoadState? state))
        {
            return null;
        }

        return state.Status == WpfLoadStatus.Ready ? state.Tree : null;
    }

    public void EvictWpfArchive(string wpfPath)
    {
        string key = NormalizeLibraryKey(wpfPath);
        if (_wpfLoads.TryGetValue(key, out WpfLoadState? state))
        {
            state.Evict();
        }
    }

    public bool TryExtractWpfEntryBytes(string wpfPath, string entryPath, out byte[] outBytes, out string error)
    {
        outBytes = Array.Empty<byte>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath) || string.IsNullOrWhiteSpace(entryPath))
        {
            error = "WPF 路径或条目路径为空。";
            return false;
        }

        // NOTE: this method is called from background workers (SGL load, preview decode),
        // so it must not touch the UI-thread dictionaries. Always open a temporary archive.
        using var archive = new WpfArchive();
        if (!archive.Open(wpfPath, out error))
        {
            return false;
        }

        WpfEntry? entry = archive.FindEntry(entryPath);
        if (entry is null)
        {
            error = $"WPF 中找不到条目: {entryPath}";
            return false;
        }

        return archive.ExtractEntry(entry, out outBytes, out error);
    }

    public SglLoadStatus RequestSglLibraryAsync(string sglPath, bool forceReload = false)
    {
        string key = NormalizeLibraryKey(sglPath);
        if (string.IsNullOrWhiteSpace(key))
        {
            return SglLoadStatus.Failed;
        }

        if (!_sglLoads.TryGetValue(key, out SglLoadState? state))
        {
            state = new SglLoadState(key);
            _sglLoads[key] = state;
        }
        else if (forceReload)
        {
            state.Evict();
        }

        if (state.Status is SglLoadStatus.Ready or SglLoadStatus.Loading)
        {
            return state.Status;
        }

        state.Status = SglLoadStatus.Loading;
        state.Error = string.Empty;
        state.Task = Task.Run(() =>
        {
            try
            {
                if (WpfKey.TryParse(key, out string wpfPath, out string entryPath))
                {
                    if (!TryExtractWpfEntryBytes(wpfPath, entryPath, out byte[] bytes, out string error))
                    {
                        return new SglLoadResult(false, error, null);
                    }

                    var library = new SglLibrary();
                    string label = WpfKey.Make(wpfPath, entryPath);
                    if (!library.OpenFromMemory(bytes, label, out error))
                    {
                        library.Dispose();
                        return new SglLoadResult(false, error, null);
                    }

                    return new SglLoadResult(true, string.Empty, library);
                }

                {
                    var library = new SglLibrary();
                    if (!library.Open(key, out string error))
                    {
                        library.Dispose();
                        return new SglLoadResult(false, error, null);
                    }

                    return new SglLoadResult(true, string.Empty, library);
                }
            }
            catch (Exception ex)
            {
                return new SglLoadResult(false, ex.Message, null);
            }
        });

        return state.Status;
    }

    public SglLoadStatus GetSglLoadStatus(string sglKey)
    {
        string key = NormalizeLibraryKey(sglKey);
        return _sglLoads.TryGetValue(key, out SglLoadState? state) ? state.Status : SglLoadStatus.NotStarted;
    }

    public string GetSglLoadError(string sglKey)
    {
        string key = NormalizeLibraryKey(sglKey);
        return _sglLoads.TryGetValue(key, out SglLoadState? state) ? state.Error : string.Empty;
    }

    public SglLibrary? GetSglLibrary(string sglKey)
    {
        string key = NormalizeLibraryKey(sglKey);
        if (!_sglLoads.TryGetValue(key, out SglLoadState? state))
        {
            return null;
        }

        return state.Status == SglLoadStatus.Ready ? state.Library : null;
    }

    public void EvictSglLibrary(string sglKey)
    {
        string key = NormalizeLibraryKey(sglKey);
        if (_sglLoads.TryGetValue(key, out SglLoadState? state))
        {
            state.Evict();
        }
    }

    /// <summary>
    /// Discards the editable working copy for this SGL key (if any).
    /// Used by Live Reload to avoid showing stale in-memory previews after source changes.
    /// </summary>
    public bool DiscardEditableSgl(string sglKey)
    {
        string key = NormalizeLibraryKey(sglKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return _editableSgls.Remove(key);
    }

    // --- Editable SGL (迁移自旧工程 ContentEditor 的资源编辑工作流) ------------------

    public bool InsertEmptyCell(string sglKey, int insertIndex)
    {
        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        int pos = Math.Clamp(insertIndex, 0, info.TexSlots.Count);
        info.TexSlots.Insert(pos, Array.Empty<byte>());
        info.Dirty = true;

        return ApplyEditableSglPreview(sglKey, info);
    }

    public bool InsertImagesBefore(string sglKey, int selectedIndex, IReadOnlyList<string> texPaths)
    {
        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        int pos = Math.Clamp(selectedIndex, 0, info.TexSlots.Count);
        return InsertImagesAt(sglKey, info, pos, texPaths);
    }

    public bool InsertImagesAfter(string sglKey, int selectedIndex, IReadOnlyList<string> texPaths)
    {
        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        int pos = Math.Clamp(selectedIndex + 1, 0, info.TexSlots.Count);
        return InsertImagesAt(sglKey, info, pos, texPaths);
    }

    public bool ReplaceImage(string sglKey, int selectedIndex, string texPath)
    {
        if (string.IsNullOrWhiteSpace(texPath))
        {
            LastError = "替换失败：TEX 路径为空。";
            return false;
        }

        if (!HasTexExtension(texPath))
        {
            LastError = "仅支持 .tex 文件";
            return false;
        }

        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        int pos = selectedIndex;
        if (pos < 0 || pos >= info.TexSlots.Count)
        {
            LastError = "选中的图片索引无效";
            return false;
        }

        if (!FileIO.TryReadAllBytes(texPath, out byte[] bytes, out string error))
        {
            LastError = error;
            return false;
        }

        info.TexSlots[pos] = bytes;
        info.Dirty = true;

        return ApplyEditableSglPreview(sglKey, info);
    }

    public bool RemoveImage(string sglKey, int selectedIndex)
    {
        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        int pos = selectedIndex;
        if (pos < 0 || pos >= info.TexSlots.Count)
        {
            LastError = "选中的图片索引无效";
            return false;
        }

        info.TexSlots.RemoveAt(pos);
        info.Dirty = true;

        return ApplyEditableSglPreview(sglKey, info);
    }

    public bool BlankCell(string sglKey, int selectedIndex)
    {
        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        int pos = selectedIndex;
        if (pos < 0 || pos >= info.TexSlots.Count)
        {
            LastError = "选中的图片索引无效";
            return false;
        }

        info.TexSlots[pos] = Array.Empty<byte>();
        info.Dirty = true;

        return ApplyEditableSglPreview(sglKey, info);
    }

    public bool SaveLibrary(string sglKey)
    {
        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        if (!TryBuildCurrentSglBytes(info, out byte[] sglBytes, out string error))
        {
            LastError = error;
            return false;
        }

        if (!info.FromWpf)
        {
            if (!TryWriteSlotsToPath(info.SourcePath, info.TexSlots, sglBytes, out error))
            {
                LastError = error;
                return false;
            }

            info.Dirty = false;
            return true;
        }

        if (!TryOverwriteWpfEntry(info.WpfPath, info.WpfEntryPath, sglBytes, out error))
        {
            LastError = error;
            return false;
        }

        info.Dirty = false;

        // Force reload next time; the editor keeps using its in-memory preview anyway.
        EvictWpfArchive(info.WpfPath);
        return true;
    }

    public bool SaveLibraryAsCopy(string sglKey, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            LastError = "保存失败：目标路径为空。";
            return false;
        }

        if (!TryEnsureEditableSglLoaded(sglKey, out EditableSglInfo? info))
        {
            return false;
        }

        if (!TryBuildCurrentSglBytes(info, out byte[] sglBytes, out string error))
        {
            LastError = error;
            return false;
        }

        if (!TryWriteSlotsToPath(targetPath, info.TexSlots, sglBytes, out error))
        {
            LastError = error;
            return false;
        }

        return true;
    }

    private bool InsertImagesAt(string sglKey, EditableSglInfo info, int pos, IReadOnlyList<string> texPaths)
    {
        if (texPaths is null || texPaths.Count == 0)
        {
            LastError = "未找到可插入的 .tex 文件";
            return false;
        }

        var incoming = new List<byte[]>(texPaths.Count);
        for (int i = 0; i < texPaths.Count; i++)
        {
            string p = texPaths[i];
            if (!HasTexExtension(p))
            {
                LastError = "仅支持 .tex 文件";
                return false;
            }

            if (!FileIO.TryReadAllBytes(p, out byte[] bytes, out string error))
            {
                LastError = error;
                return false;
            }

            incoming.Add(bytes);
        }

        info.TexSlots.InsertRange(pos, incoming);
        info.Dirty = true;

        return ApplyEditableSglPreview(sglKey, info);
    }

    private bool TryEnsureEditableSglLoaded(string sglKey, [NotNullWhen(true)] out EditableSglInfo? info)
    {
        info = null;

        string key = NormalizeLibraryKey(sglKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            LastError = "SGL key 为空。";
            return false;
        }

        if (_editableSgls.TryGetValue(key, out info))
        {
            return true;
        }

        byte[] sglBytes;
        bool fromWpf = WpfKey.TryParse(key, out string wpfPath, out string entryPath);
        if (fromWpf)
        {
            if (!TryExtractWpfEntryBytes(wpfPath, entryPath, out sglBytes, out string error))
            {
                LastError = error;
                return false;
            }
        }
        else
        {
            if (!FileIO.TryReadAllBytes(key, out sglBytes, out string error))
            {
                LastError = error;
                return false;
            }
        }

        if (!SglCodec.TryEnumerateEntriesFromMemory(sglBytes, key, out List<SglImageEntry> entries, out string enumErr))
        {
            LastError = enumErr;
            return false;
        }

        var slots = new List<byte[]>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            SglImageEntry e = entries[i];
            if (e is null || e.IsEmpty)
            {
                slots.Add(Array.Empty<byte>());
                continue;
            }

            if (e.Offset > int.MaxValue || e.Size > int.MaxValue)
            {
                LastError = "SGL 条目太大（超过 2GB，不支持）";
                return false;
            }

            int offset = (int)e.Offset;
            int size = (int)e.Size;
            if (offset < 0 || size < 0 || offset + size > sglBytes.Length)
            {
                LastError = "SGL 条目超出文件范围";
                return false;
            }

            slots.Add(new ReadOnlySpan<byte>(sglBytes, offset, size).ToArray());
        }

        info = new EditableSglInfo
        {
            Key = key,
            FromWpf = fromWpf,
            SourcePath = fromWpf ? string.Empty : key,
            WpfPath = fromWpf ? wpfPath : string.Empty,
            WpfEntryPath = fromWpf ? entryPath : string.Empty,
            TexSlots = slots,
            Dirty = false,
        };

        _editableSgls[key] = info;
        return true;
    }

    private bool ApplyEditableSglPreview(string sglKey, EditableSglInfo info)
    {
        if (info is null)
        {
            LastError = "内部错误：EditableSglInfo 为空。";
            return false;
        }

        if (!TryBuildCurrentSglBytes(info, out byte[] sglBytes, out string error))
        {
            LastError = error;
            return false;
        }

        string key = NormalizeLibraryKey(sglKey);
        if (!_sglLoads.TryGetValue(key, out SglLoadState? state))
        {
            state = new SglLoadState(key);
            _sglLoads[key] = state;
        }

        state.Task = null;

        if (state.Library is null)
        {
            state.Library = new SglLibrary();
        }

        if (!state.Library.OpenFromMemory(sglBytes, key, out error))
        {
            state.Error = error;
            state.Status = SglLoadStatus.Failed;
            LastError = error;
            return false;
        }

        state.Error = string.Empty;
        state.Status = SglLoadStatus.Ready;
        return true;
    }

    private static bool HasTexExtension(string path)
    {
        try
        {
            return Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildCurrentSglBytes(EditableSglInfo info, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (info is null)
        {
            error = "EditableSglInfo 为空";
            return false;
        }

        if (!SglCodec.TryWriteLibraryToBytes(info.TexSlots, out bytes, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryWriteSlotsToPath(string targetPath, IReadOnlyList<byte[]> texSlots, byte[] sglBytes, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            error = "目标路径为空";
            return false;
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

        if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            if (texSlots.Count != 1)
            {
                error = "仅当库只有 1 个槽位时才允许保存为 .tex";
                return false;
            }

            ReadOnlySpan<byte> bytes = texSlots[0] ?? Array.Empty<byte>();
            return FileIO.TryWriteAllBytes(targetPath, bytes, out error);
        }

        if (ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            string leaf = Path.GetFileNameWithoutExtension(targetPath);
            if (string.IsNullOrWhiteSpace(leaf))
            {
                leaf = "library";
            }

            string entryPath = leaf + ".sgl";
            var pack = new List<WpfPackEntry>
            {
                new(entryPath, sglBytes),
            };
            return WpfCodec.TryWriteArchive(targetPath, pack, out error);
        }

        if (!ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            // Default to .sgl
            try
            {
                targetPath = Path.ChangeExtension(targetPath, ".sgl");
            }
            catch
            {
                // ignore invalid target normalization
            }
        }

        return SglCodec.TryWriteLibrary(targetPath, texSlots, out error);
    }

    private static bool TryOverwriteWpfEntry(string wpfPath, string entryPath, byte[] replacementBytes, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath))
        {
            error = "WPF 路径为空";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entryPath))
        {
            error = "WPF entry 路径为空";
            return false;
        }

        if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> entries, out string enumErr))
        {
            error = enumErr;
            return false;
        }

        using var archive = new WpfArchive();
        if (!archive.Open(wpfPath, out error))
        {
            return false;
        }

        var packFiles = new List<WpfPackEntry>(entries.Count);
        bool replaced = false;

        for (int i = 0; i < entries.Count; i++)
        {
            WpfEntry e = entries[i];
            if (e.IsDirectory || string.IsNullOrWhiteSpace(e.FullPath))
            {
                continue;
            }

            if (string.Equals(e.FullPath, entryPath, StringComparison.OrdinalIgnoreCase))
            {
                packFiles.Add(new WpfPackEntry(e.FullPath, replacementBytes));
                replaced = true;
                continue;
            }

            if (!archive.ExtractEntry(e, out byte[] bytes, out error))
            {
                return false;
            }

            packFiles.Add(new WpfPackEntry(e.FullPath, bytes));
        }

        if (!replaced)
        {
            error = $"未在 WPF 中找到目标条目: {entryPath}";
            return false;
        }

        return WpfCodec.TryWriteArchive(wpfPath, packFiles, out error);
    }

    private sealed class EditableSglInfo
    {
        public string Key { get; init; } = string.Empty;
        public bool FromWpf { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string WpfPath { get; init; } = string.Empty;
        public string WpfEntryPath { get; init; } = string.Empty;
        public List<byte[]> TexSlots { get; init; } = new();
        public bool Dirty { get; set; }
    }

    public void Refresh(IReadOnlyList<DataFolder> dataFolders)
    {
        _discoveredFiles.Clear();
        _roots.Clear();
        LastError = string.Empty;

        if (dataFolders is null || dataFolders.Count == 0)
        {
            return;
        }

        var warnings = new List<string>();

        foreach (DataFolder folder in dataFolders)
        {
            if (folder is null) continue;

            string rootPath = folder.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            try
            {
                rootPath = Path.GetFullPath(rootPath);
            }
            catch
            {
                warnings.Add($"无法规范化路径: {folder.Path}");
                continue;
            }

            if (!Directory.Exists(rootPath))
            {
                warnings.Add($"目录不存在: {rootPath}");
                continue;
            }

            var rootNode = new AssetTreeNode
            {
                Name = string.IsNullOrWhiteSpace(folder.DisplayName) ? Path.GetFileName(rootPath) : folder.DisplayName,
                FullPath = rootPath,
                IsDirectory = true,
            };
            _roots.Add(rootNode);

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    if (!TryClassify(filePath, out bool isWpf, out bool isSgl, out bool isTex, out bool isWpfHash))
                    {
                        continue;
                    }

                    var discovered = new DiscoveredFile
                    {
                        DisplayName = Path.GetFileName(filePath),
                        FullPath = filePath,
                        IsWpf = isWpf,
                        IsSgl = isSgl,
                        IsTex = isTex,
                        IsWpfHash = isWpfHash,
                        RootDisplayName = rootNode.Name,
                        RootPath = rootPath,
                    };

                    _discoveredFiles.Add(discovered);
                    AddToTree(rootNode, rootPath, discovered);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"扫描目录失败: {rootPath} ({ex.Message})");
            }

            SortTree(rootNode);
        }

        if (warnings.Count > 0)
        {
            LastError = string.Join(Environment.NewLine, warnings);
        }
    }

    private void ProcessAsyncLoads()
    {
        ProcessWpfLoads();
        ProcessSglLoads();
    }

    private void ProcessWpfLoads()
    {
        foreach ((string key, WpfLoadState state) in _wpfLoads)
        {
            if (state.Status != WpfLoadStatus.Loading)
            {
                continue;
            }

            Task<WpfLoadResult>? task = state.Task;
            if (task is null || !task.IsCompleted)
            {
                continue;
            }

            WpfLoadResult result;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                result = new WpfLoadResult(false, ex.Message, null);
            }

            state.Task = null;

            if (result.Ok && result.Archive is not null)
            {
                state.Archive?.Dispose();
                state.Archive = result.Archive;
                state.Tree = WpfArchiveTree.BuildFromEntries(state.Archive.GetEntries());
                state.Error = string.Empty;
                state.Status = WpfLoadStatus.Ready;
                continue;
            }

            result.Archive?.Dispose();
            state.Archive?.Dispose();
            state.Archive = null;
            state.Tree = null;
            state.Error = string.IsNullOrWhiteSpace(result.Error) ? "WPF 加载失败" : result.Error;
            state.Status = WpfLoadStatus.Failed;
            _ = key;
        }
    }

    private void ProcessSglLoads()
    {
        foreach ((string key, SglLoadState state) in _sglLoads)
        {
            if (state.Status != SglLoadStatus.Loading)
            {
                continue;
            }

            Task<SglLoadResult>? task = state.Task;
            if (task is null || !task.IsCompleted)
            {
                continue;
            }

            SglLoadResult result;
            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                result = new SglLoadResult(false, ex.Message, null);
            }

            state.Task = null;

            if (result.Ok && result.Library is not null)
            {
                state.Library?.Dispose();
                state.Library = result.Library;
                state.Error = string.Empty;
                state.Status = SglLoadStatus.Ready;
                continue;
            }

            result.Library?.Dispose();
            state.Library?.Dispose();
            state.Library = null;
            state.Error = string.IsNullOrWhiteSpace(result.Error) ? "SGL 加载失败" : result.Error;
            state.Status = SglLoadStatus.Failed;
            _ = key;
        }
    }

    private static string NormalizeLibraryKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        // 未来会用到类似 "wpfPath::entryId" 的 key，这里先避免把它当成磁盘路径规范化。
        if (key.Contains("::", StringComparison.Ordinal))
        {
            return key;
        }

        try
        {
            return FileWatcher.NormalizeWatchPath(key);
        }
        catch
        {
            return key;
        }
    }

    private sealed class WpfLoadState
    {
        public string Key { get; }
        public WpfLoadStatus Status { get; set; } = WpfLoadStatus.NotStarted;
        public Task<WpfLoadResult>? Task { get; set; }
        public WpfArchive? Archive { get; set; }
        public WpfArchiveTree? Tree { get; set; }
        public string Error { get; set; } = string.Empty;

        public WpfLoadState(string key)
        {
            Key = key;
        }

        public void Evict()
        {
            Task = null;
            Archive?.Dispose();
            Archive = null;
            Tree = null;
            Error = string.Empty;
            Status = WpfLoadStatus.NotStarted;
        }
    }

    private readonly record struct WpfLoadResult(bool Ok, string Error, WpfArchive? Archive);

    private sealed class SglLoadState
    {
        public string Key { get; }
        public SglLoadStatus Status { get; set; } = SglLoadStatus.NotStarted;
        public Task<SglLoadResult>? Task { get; set; }
        public SglLibrary? Library { get; set; }
        public string Error { get; set; } = string.Empty;

        public SglLoadState(string key)
        {
            Key = key;
        }

        public void Evict()
        {
            Task = null;
            Library?.Dispose();
            Library = null;
            Error = string.Empty;
            Status = SglLoadStatus.NotStarted;
        }
    }

    private readonly record struct SglLoadResult(bool Ok, string Error, SglLibrary? Library);

    private static bool TryClassify(string filePath, out bool isWpf, out bool isSgl, out bool isTex, out bool isWpfHash)
    {
        isWpf = false;
        isSgl = false;
        isTex = false;
        isWpfHash = false;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string filename = Path.GetFileName(filePath);
        if (filename.EndsWith(".wpf.hash", StringComparison.OrdinalIgnoreCase))
        {
            isWpfHash = true;
            return true;
        }

        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
        {
            isWpf = true;
            return true;
        }

        if (ext.Equals(".sgl", StringComparison.OrdinalIgnoreCase))
        {
            isSgl = true;
            return true;
        }

        if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            isTex = true;
            return true;
        }

        return false;
    }

    private static void AddToTree(AssetTreeNode root, string rootPath, DiscoveredFile file)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(rootPath, file.FullPath);
        }
        catch
        {
            relative = file.DisplayName;
        }

        string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        AssetTreeNode current = root;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (string.IsNullOrWhiteSpace(part)) continue;

            bool isLeaf = i == parts.Length - 1;
            if (isLeaf)
            {
                current.Children.Add(new AssetTreeNode
                {
                    Name = part,
                    FullPath = file.FullPath,
                    IsDirectory = false,
                    File = file,
                });
                return;
            }

            AssetTreeNode? next = null;
            for (int c = 0; c < current.Children.Count; c++)
            {
                AssetTreeNode child = current.Children[c];
                if (child.IsDirectory && child.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                string fullPath = Path.Combine(rootPath, string.Join(Path.DirectorySeparatorChar, parts[..(i + 1)]));
                next = new AssetTreeNode
                {
                    Name = part,
                    FullPath = fullPath,
                    IsDirectory = true,
                };
                current.Children.Add(next);
            }

            current = next;
        }
    }

    // --- OldProj Build parity helpers -------------------------------------

    /// <summary>
    /// 将文件夹内所有文件（递归）打包为一个新的 <c>.wpf</c>。
    /// 迁移自旧工程 <c>OldProj/ContentEditor/src/app/AssetLibrary.cpp</c> 的 <c>ConvertFolderToWpf</c>。
    /// </summary>
    public bool ConvertFolderToWpf(string folderPath, out string outWpfPath)
    {
        outWpfPath = string.Empty;
        LastError = string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            LastError = "Folder does not exist";
            return false;
        }

        string normalizedFolder = TrimTrailingSeparators(TryFullPath(folderPath));
        outWpfPath = normalizedFolder + ".wpf";

        var files = new List<WpfPackEntry>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(normalizedFolder, "*", SearchOption.AllDirectories))
            {
                string rel;
                try
                {
                    rel = Path.GetRelativePath(normalizedFolder, file);
                }
                catch
                {
                    rel = Path.GetFileName(file);
                }

                rel = rel.Replace('\\', '/');

                if (!FileIO.TryReadAllBytes(file, out byte[] bytes, out string error))
                {
                    LastError = error;
                    return false;
                }

                files.Add(new WpfPackEntry(rel, bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return false;
        }

        if (!WpfCodec.TryWriteArchive(outWpfPath, files, out string writeError))
        {
            LastError = writeError;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从文件夹内的 <c>.tex</c> 文件构建 <c>.sgl</c>（非递归）。若存在 <c>placements.txt</c>，则按其最大索引生成 slots，
    /// 并将形如 <c>00000005.tex</c> 的文件映射到 slot 5；否则按文件名排序后顺序打包。
    /// 迁移自旧工程 <c>OldProj/ContentEditor/src/app/AssetLibrary.cpp</c> 的 <c>CreateSglFromFolder</c>。
    /// </summary>
    public bool CreateSglFromFolder(string folderPath, out string outSglPath)
    {
        outSglPath = string.Empty;
        LastError = string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            LastError = "Folder does not exist";
            return false;
        }

        string normalizedFolder = TrimTrailingSeparators(TryFullPath(folderPath));

        var texFiles = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(normalizedFolder, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetExtension(file).Equals(".tex", StringComparison.OrdinalIgnoreCase))
                {
                    texFiles.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return false;
        }

        texFiles.Sort(StringComparer.OrdinalIgnoreCase);

        if (texFiles.Count == 0)
        {
            LastError = "No .tex files found in folder";
            return false;
        }

        bool havePlacements = false;
        int maxPlacementIndex = -1;
        string placementsPath = Path.Combine(normalizedFolder, "placements.txt");
        if (File.Exists(placementsPath))
        {
            try
            {
                foreach (string rawLine in File.ReadLines(placementsPath))
                {
                    string line = rawLine?.Trim() ?? string.Empty;
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    // per line: <index> <offsetX> <offsetY>
                    int sp = line.IndexOfAny(new[] { ' ', '\t' });
                    string head = sp >= 0 ? line.Substring(0, sp) : line;
                    if (!int.TryParse(head, out int idx))
                    {
                        continue;
                    }

                    havePlacements = true;
                    if (idx > maxPlacementIndex)
                    {
                        maxPlacementIndex = idx;
                    }
                }
            }
            catch
            {
                havePlacements = false;
                maxPlacementIndex = -1;
            }
        }

        IReadOnlyList<byte[]> slots;

        if (havePlacements && maxPlacementIndex >= 0)
        {
            var texSlots = new byte[maxPlacementIndex + 1][];

            for (int i = 0; i < texFiles.Count; i++)
            {
                string texPath = texFiles[i];

                string stem;
                try
                {
                    stem = Path.GetFileNameWithoutExtension(texPath);
                }
                catch
                {
                    continue;
                }

                if (!int.TryParse(stem, out int fileIdx))
                {
                    continue;
                }

                if (fileIdx < 0 || fileIdx > maxPlacementIndex)
                {
                    continue;
                }

                if (!FileIO.TryReadAllBytes(texPath, out byte[] bytes, out string readError))
                {
                    LastError = readError;
                    return false;
                }

                texSlots[fileIdx] = bytes;
            }

            // ensure we never pass null slots down to the writer
            for (int i = 0; i < texSlots.Length; i++)
            {
                texSlots[i] ??= Array.Empty<byte>();
            }

            slots = texSlots;
        }
        else
        {
            var texSlots = new List<byte[]>(capacity: texFiles.Count);
            for (int i = 0; i < texFiles.Count; i++)
            {
                string texPath = texFiles[i];
                if (!FileIO.TryReadAllBytes(texPath, out byte[] bytes, out string readError))
                {
                    LastError = readError;
                    return false;
                }

                texSlots.Add(bytes);
            }

            slots = texSlots;
        }

        outSglPath = normalizedFolder + ".sgl";

        if (!SglCodec.TryWriteLibrary(outSglPath, slots, out string writeError))
        {
            LastError = writeError;
            return false;
        }

        return true;
    }

    private static string TryFullPath(string path)
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

    private static string TrimTrailingSeparators(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void SortTree(AssetTreeNode node)
    {
        node.Children.Sort(static (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (node.Children[i].IsDirectory)
            {
                SortTree(node.Children[i]);
            }
        }
    }
}
