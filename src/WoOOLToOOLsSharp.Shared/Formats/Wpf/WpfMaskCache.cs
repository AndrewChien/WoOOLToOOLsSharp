using System;
using System.Collections.Generic;
using System.IO;

namespace WoOOLToOOLsSharp.Shared.Formats.Wpf;

/// <summary>
/// 仅用于按路径提取 WPF 内的海岸遮罩（.msk/.Msk），并缓存 WPF 的条目索引以减少重复解析。
/// 设计目标：避免一次性读取整个 WPF 到内存。
/// </summary>
public static class WpfMaskCache
{
    private sealed class CacheEntry
    {
        public DateTime WriteTimeUtc { get; set; }
        public long FileSize { get; set; }
        public Dictionary<string, WpfEntry> MaskEntriesByPath { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasIndex { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryExtractMaskByPath(string wpfPath, string entryPath, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath) || string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        string normalized = NormalizeEntryPath(entryPath);
        if (!IsMaskEntryPath(normalized))
        {
            return false;
        }

        if (!TryGetIndex(wpfPath, out CacheEntry index, out error))
        {
            return false;
        }

        if (!index.MaskEntriesByPath.TryGetValue(normalized, out WpfEntry? entry) || entry is null)
        {
            return false;
        }

        if (!WpfCodec.TryExtractEntryFromFile(wpfPath, entry, out bytes, out error))
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        return bytes.Length > 0;
    }

    private static bool TryGetIndex(string wpfPath, out CacheEntry entry, out string error)
    {
        entry = new CacheEntry();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath))
        {
            error = "WPF 路径为空";
            return false;
        }

        if (!File.Exists(wpfPath))
        {
            error = $"WPF 文件不存在: {wpfPath}";
            return false;
        }

        DateTime writeTimeUtc;
        long fileSize;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(wpfPath);
            fileSize = new FileInfo(wpfPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 WPF 文件信息失败: {wpfPath}\n{ex.Message}";
            return false;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(wpfPath, out CacheEntry? cached)
                && cached.HasIndex
                && cached.WriteTimeUtc == writeTimeUtc
                && cached.FileSize == fileSize)
            {
                entry = cached;
                return true;
            }

            if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> entries, out error))
            {
                Cache.Remove(wpfPath);
                return false;
            }

            var dict = new Dictionary<string, WpfEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (WpfEntry e in entries)
            {
                if (e is null || e.IsDirectory || e.ByteSize == 0)
                {
                    continue;
                }

                string full = NormalizeEntryPath(e.FullPath);
                if (!IsMaskEntryPath(full))
                {
                    continue;
                }

                if (!dict.ContainsKey(full))
                {
                    dict.Add(full, e);
                }
            }

            var rebuilt = new CacheEntry
            {
                WriteTimeUtc = writeTimeUtc,
                FileSize = fileSize,
                MaskEntriesByPath = dict,
                HasIndex = true,
            };

            Cache[wpfPath] = rebuilt;
            entry = rebuilt;
            return true;
        }
    }

    private static bool IsMaskEntryPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        string p = normalizedPath.Replace('\\', '/');
        if (!p.EndsWith(".msk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return p.StartsWith("mask/", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("data/mask/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string p = path.Replace('\\', '/').Trim();
        while (p.Length > 0)
        {
            if (p.StartsWith("/", StringComparison.Ordinal))
            {
                p = p.Substring(1);
                continue;
            }

            if (p.StartsWith("./", StringComparison.Ordinal))
            {
                p = p.Substring(2);
                continue;
            }

            if (p.StartsWith(".", StringComparison.Ordinal))
            {
                p = p.Substring(1);
                continue;
            }

            break;
        }

        return p;
    }
}
