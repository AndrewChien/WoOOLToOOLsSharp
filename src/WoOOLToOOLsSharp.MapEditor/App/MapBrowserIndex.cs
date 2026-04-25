using System;
using System.Collections.Generic;
using System.IO;
using WoOOLToOOLsSharp.Shared.EditorBridge;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.MapEditor.App;

internal static class MapBrowserIndex
{
    public static bool TryScan(
        string rootDirectory,
        bool recursive,
        bool includePrefabs,
        out MapBrowserDirectoryNode root,
        out int fileCount,
        out string error)
    {
        root = new MapBrowserDirectoryNode(name: "(root)", fullPath: rootDirectory ?? string.Empty);
        fileCount = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            error = "地图目录为空。";
            return false;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(rootDirectory);
        }
        catch (Exception ex)
        {
            error = $"地图目录无效：{ex.Message}";
            return false;
        }

        if (!Directory.Exists(fullRoot))
        {
            error = $"地图目录不存在：{fullRoot}";
            return false;
        }

        root = new MapBrowserDirectoryNode(name: Path.GetFileName(fullRoot), fullPath: fullRoot);

        var metaCache = new Dictionary<string, Dictionary<string, FolderMetaEntry>>(StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary | FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        try
        {
            foreach (string filePath in Directory.EnumerateFiles(fullRoot, "*.*", options))
            {
                string ext = GetExtensionSafe(filePath);
                if (IsWpfArchiveExtension(ext))
                {
                    if (TryAddWpfEntries(root, fullRoot, filePath, includePrefabs, out int added, out _))
                    {
                        fileCount += added;
                    }

                    continue;
                }

                if (!ShouldIncludeFileExtension(ext, includePrefabs))
                {
                    continue;
                }

                AddFile(root, fullRoot, filePath, metaCache);
                fileCount++;
            }
        }
        catch (Exception ex)
        {
            error = $"扫描地图目录失败：{ex.Message}";
            return false;
        }

        root.SortChildren();
        return true;
    }

    public static IEnumerable<MapBrowserFileEntry> EnumerateAllFiles(MapBrowserDirectoryNode root)
    {
        if (root is null)
        {
            yield break;
        }

        foreach (MapBrowserFileEntry file in root.Files)
        {
            yield return file;
        }

        foreach (MapBrowserDirectoryNode dir in root.Directories)
        {
            foreach (MapBrowserFileEntry file in EnumerateAllFiles(dir))
            {
                yield return file;
            }
        }
    }

    private static bool ShouldIncludeFileExtension(string ext, bool includePrefabs)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        if (IsWpfArchiveExtension(ext))
        {
            return true;
        }

        if (NmpFileName.IsMapExtension(ext))
        {
            return true;
        }

        if (!includePrefabs)
        {
            return false;
        }

        return NmpFileName.IsPrefabExtension(ext);
    }

    private static bool IsWpfArchiveExtension(string ext)
        => ext is not null && ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase);

    private static string GetExtensionSafe(string path)
    {
        try
        {
            return Path.GetExtension(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryAddWpfEntries(
        MapBrowserDirectoryNode root,
        string fullRoot,
        string wpfPath,
        bool includePrefabs,
        out int addedCount,
        out string error)
    {
        addedCount = 0;
        error = string.Empty;

        if (root is null || string.IsNullOrWhiteSpace(fullRoot) || string.IsNullOrWhiteSpace(wpfPath))
        {
            return true; // ignore
        }

        if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> entries, out string enumError))
        {
            error = enumError;
            return false;
        }

        string wpfRelative;
        try
        {
            wpfRelative = Path.GetRelativePath(fullRoot, wpfPath);
        }
        catch
        {
            wpfRelative = wpfPath;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            WpfEntry e = entries[i];
            if (e is null || e.IsDirectory || string.IsNullOrWhiteSpace(e.FullPath))
            {
                continue;
            }

            string entryExt = GetExtensionSafe(e.FullPath);
            bool isMap = NmpFileName.IsMapExtension(entryExt);
            bool isPrefab = !isMap && includePrefabs && NmpFileName.IsPrefabExtension(entryExt);
            if (!isMap && !isPrefab)
            {
                continue;
            }

            string entryRel = e.FullPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string relative = Path.Combine(wpfRelative, entryRel);

            string fileName;
            try
            {
                fileName = Path.GetFileName(entryRel);
            }
            catch
            {
                fileName = e.FullPath;
            }

            string fullPath = LocalEditorBridge.MakeEditorBridgeWpfPath(wpfPath, e.FullPath);
            string displayName = GetDisplayNameForFileName(fileName, overrideDisplayName: null);
            AddRelativeFile(root, relative, fullPath, fileName, displayName, preferredDataFolderName: string.Empty);
            addedCount++;
        }

        return true;
    }

    private static void AddFile(
        MapBrowserDirectoryNode root,
        string fullRoot,
        string fullPath,
        Dictionary<string, Dictionary<string, FolderMetaEntry>> metaCache)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(fullRoot, fullPath);
        }
        catch
        {
            relative = fullPath;
        }

        string fileName;
        try
        {
            fileName = Path.GetFileName(relative);
        }
        catch
        {
            fileName = relative;
        }

        string displayNameOverride = string.Empty;
        string preferredDataFolderName = string.Empty;
        TryGetFileMeta(fullPath, fileName, metaCache, out displayNameOverride, out preferredDataFolderName);

        string displayName = GetDisplayNameForFileName(fileName, displayNameOverride);
        AddRelativeFile(root, relative, fullPath, fileName, displayName, preferredDataFolderName);
    }

    private static void AddRelativeFile(
        MapBrowserDirectoryNode root,
        string relativePath,
        string fullPath,
        string fileName,
        string displayName,
        string preferredDataFolderName)
    {
        if (root is null)
        {
            return;
        }

        string[] parts = (relativePath ?? string.Empty).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        MapBrowserDirectoryNode current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string name = parts[i];
            MapBrowserDirectoryNode? child = current.FindDirectory(name);
            if (child is null)
            {
                string path = Path.Combine(current.FullPath, name);
                child = new MapBrowserDirectoryNode(name, path);
                current.Directories.Add(child);
            }

            current = child;
        }

        current.Files.Add(new MapBrowserFileEntry(
            fullPath,
            relativePath ?? string.Empty,
            fileName ?? string.Empty,
            displayName ?? string.Empty,
            preferredDataFolderName ?? string.Empty));
    }

    private static bool TryGetFileMeta(
        string fullPath,
        string fileName,
        Dictionary<string, Dictionary<string, FolderMetaEntry>> metaCache,
        out string displayNameOverride,
        out string preferredDataFolderName)
    {
        displayNameOverride = string.Empty;
        preferredDataFolderName = string.Empty;

        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (metaCache is null)
        {
            return false;
        }

        string directory = string.Empty;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(fullPath)) ?? string.Empty;
        }
        catch
        {
            directory = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        if (!metaCache.TryGetValue(directory, out Dictionary<string, FolderMetaEntry>? entries))
        {
            if (!FolderMetaCodec.TryReadDirectory(directory, out Dictionary<string, FolderMetaEntry> loaded, out _))
            {
                loaded = new Dictionary<string, FolderMetaEntry>(StringComparer.OrdinalIgnoreCase);
            }

            entries = loaded;
            metaCache[directory] = loaded;
        }

        if (entries is null)
        {
            return false;
        }

        if (!entries.TryGetValue(fileName, out FolderMetaEntry entry))
        {
            return false;
        }

        displayNameOverride = entry.DisplayName ?? string.Empty;
        preferredDataFolderName = entry.DataFolderName ?? string.Empty;
        return true;
    }

    private static string GetDisplayNameForFileName(string fileName, string? overrideDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(overrideDisplayName))
        {
            return overrideDisplayName.Trim();
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(fileName);
        }
        catch
        {
            return fileName;
        }
    }
}

internal sealed class MapBrowserDirectoryNode
{
    public string Name { get; }
    public string FullPath { get; }

    public List<MapBrowserDirectoryNode> Directories { get; } = new();
    public List<MapBrowserFileEntry> Files { get; } = new();

    public MapBrowserDirectoryNode(string name, string fullPath)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "(dir)" : name;
        FullPath = fullPath ?? string.Empty;
    }

    public MapBrowserDirectoryNode? FindDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        for (int i = 0; i < Directories.Count; i++)
        {
            MapBrowserDirectoryNode node = Directories[i];
            if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    public void SortChildren()
    {
        Directories.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a?.Name, b?.Name));
        Files.Sort(static (a, b) =>
        {
            string an = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName;
            string bn = string.IsNullOrWhiteSpace(b.DisplayName) ? b.Name : b.DisplayName;
            return StringComparer.OrdinalIgnoreCase.Compare(an, bn);
        });

        for (int i = 0; i < Directories.Count; i++)
        {
            Directories[i].SortChildren();
        }
    }
}

internal readonly record struct MapBrowserFileEntry(
    string FullPath,
    string RelativePath,
    string Name,
    string DisplayName,
    string PreferredDataFolderName);

internal sealed class MapBrowserScanResult
{
    public bool Ok { get; }
    public string RootDirectory { get; }
    public MapBrowserDirectoryNode Root { get; }
    public int FileCount { get; }
    public string Error { get; }

    private MapBrowserScanResult(bool ok, string rootDirectory, MapBrowserDirectoryNode root, int fileCount, string error)
    {
        Ok = ok;
        RootDirectory = rootDirectory ?? string.Empty;
        Root = root ?? new MapBrowserDirectoryNode(name: "(root)", fullPath: RootDirectory);
        FileCount = fileCount;
        Error = error ?? string.Empty;
    }

    public static MapBrowserScanResult Success(string rootDirectory, MapBrowserDirectoryNode root, int fileCount)
        => new(ok: true, rootDirectory, root, fileCount, error: string.Empty);

    public static MapBrowserScanResult Failed(string rootDirectory, string error)
        => new(ok: false, rootDirectory, root: new MapBrowserDirectoryNode(name: "(error)", fullPath: rootDirectory ?? string.Empty), fileCount: 0, error);
}
