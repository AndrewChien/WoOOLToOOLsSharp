using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace WoOOLToOOLsSharp.Shared.Formats.Wpf;

public sealed class WpfHashFileData
{
    public uint Version { get; init; }
    public long[] Hashes { get; init; } = Array.Empty<long>();
}

public enum WpfHashComparisonStatus
{
    Match = 0,
    MissingFromWpf = 1,
    NewInWpf = 2,
}

public sealed class WpfHashComparisonEntry
{
    public int WpfEntryIndex { get; init; } = -1;
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long Hash { get; init; }
    public WpfHashComparisonStatus Status { get; init; } = WpfHashComparisonStatus.Match;
}

public sealed class WpfHashComparison
{
    public List<WpfHashComparisonEntry> Entries { get; } = new();
    public int MatchCount { get; internal set; }
    public int MissingFromWpfCount { get; internal set; }
    public int NewInWpfCount { get; internal set; }
}

/// <summary>
/// `.wpf.hash` sidecar：用于记录 WPF 内文件的 hash 列表（便于比对/增量）。
/// 迁移自旧工程 <c>OldProj/shared/src/WpfHashCodec.cpp</c>。
/// </summary>
public static class WpfHashFileCodec
{
    /// <summary>文件头 magic（小端读取等于 "hsah"）。</summary>
    public const uint Magic = 0x68617368;

    private const int HeaderSize = 16;
    private const ulong MaxEntryCount = 50_000_000;

    public static bool TryReadWpfHashFile(string hashPath, out WpfHashFileData data, out string error)
    {
        data = new WpfHashFileData();
        error = string.Empty;

        if (!FileIO.TryReadAllBytes(hashPath, out byte[] bytes, out error))
        {
            return false;
        }

        return TryReadWpfHashFileFromMemory(bytes, hashPath, out data, out error);
    }

    public static bool TryReadWpfHashFileFromMemory(
        ReadOnlySpan<byte> bytes,
        string label,
        out WpfHashFileData data,
        out string error)
    {
        data = new WpfHashFileData();
        error = string.Empty;

        if (bytes.Length < HeaderSize)
        {
            error = $"hash 文件过短: {label}";
            return false;
        }

        uint magic = ReadU32(bytes, 0);
        if (magic != Magic)
        {
            error = "Invalid hash file magic";
            return false;
        }

        uint version = ReadU32(bytes, 4);
        ulong count = ReadU64(bytes, 8);

        if (count > MaxEntryCount)
        {
            error = "Hash file entry count exceeds sanity limit";
            return false;
        }

        if (count > int.MaxValue)
        {
            error = "hash 条目数量过大（超出当前实现限制）";
            return false;
        }

        ulong maxReadable = (ulong)(bytes.Length - HeaderSize) / 8ul;
        if (count > maxReadable)
        {
            error = "Failed reading hash entries";
            return false;
        }

        var hashes = new long[(int)count];
        int pos = HeaderSize;
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(pos, 8));
            pos += 8;
        }

        data = new WpfHashFileData
        {
            Version = version,
            Hashes = hashes,
        };
        return true;
    }

    public static WpfHashComparison CompareWpfHash(WpfHashFileData hashData, IReadOnlyList<WpfEntry> wpfEntries)
    {
        var result = new WpfHashComparison();

        var wpfHashMap = new Dictionary<long, int>(capacity: wpfEntries.Count);
        for (int i = 0; i < wpfEntries.Count; i++)
        {
            if (!wpfEntries[i].IsDirectory)
            {
                wpfHashMap[wpfEntries[i].Hash] = i;
            }
        }

        var hashFileSeen = new HashSet<long>();
        foreach (long h in hashData.Hashes)
        {
            hashFileSeen.Add(h);

            if (wpfHashMap.TryGetValue(h, out int idx))
            {
                WpfEntry e = wpfEntries[idx];
                result.Entries.Add(new WpfHashComparisonEntry
                {
                    Hash = h,
                    Status = WpfHashComparisonStatus.Match,
                    WpfEntryIndex = idx,
                    Name = e.Name,
                    FullPath = e.FullPath,
                });
                result.MatchCount++;
            }
            else
            {
                result.Entries.Add(new WpfHashComparisonEntry
                {
                    Hash = h,
                    Status = WpfHashComparisonStatus.MissingFromWpf,
                });
                result.MissingFromWpfCount++;
            }
        }

        for (int i = 0; i < wpfEntries.Count; i++)
        {
            WpfEntry e = wpfEntries[i];
            if (e.IsDirectory) continue;
            if (hashFileSeen.Contains(e.Hash)) continue;

            result.Entries.Add(new WpfHashComparisonEntry
            {
                Hash = e.Hash,
                Status = WpfHashComparisonStatus.NewInWpf,
                WpfEntryIndex = i,
                Name = e.Name,
                FullPath = e.FullPath,
            });
            result.NewInWpfCount++;
        }

        return result;
    }

    public static bool IsWpfHashFile(string path)
    {
        string filename = Path.GetFileName(path);
        return filename.EndsWith(".wpf.hash", StringComparison.OrdinalIgnoreCase);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    private static ulong ReadU64(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    }
}

