using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.Shared.Formats.Wpf;

public enum WpfNameDecoding
{
    /// <summary>默认：由环境变量决定（未设置则等同 <see cref="Utf8StrictThenLatin1"/>）。</summary>
    Default = 0,

    /// <summary>先严格 UTF-8；失败则回退 Latin1（保留原始字节）。</summary>
    Utf8StrictThenLatin1 = 1,

    /// <summary>先严格 UTF-8；失败则尝试 GBK；仍失败则回退 Latin1。</summary>
    Utf8StrictThenGbkThenLatin1 = 2,
}

public static class WpfCodec
{
    public const uint Magic = 0x57504601;

    private const uint AttrDir = 0x0001;
    private const uint AttrCompress = 0x0004;

    private const int HeaderProbeSize = 128;
    private const int Fcb1Size = 16;
    private const int Fcb2Size = 56;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding?> GbkEncoding = new(static () =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch
        {
            return null;
        }
    });

    public static bool TryEnumerateEntries(
        string wpfPath,
        out List<WpfEntry> entries,
        out string error,
        WpfNameDecoding nameDecoding = WpfNameDecoding.Default)
    {
        entries = new List<WpfEntry>();
        error = string.Empty;

        if (!FileIO.TryReadAllBytes(wpfPath, out byte[] bytes, out error))
        {
            return false;
        }

        return TryEnumerateEntriesFromMemory(bytes, wpfPath, out entries, out error, nameDecoding);
    }

    /// <summary>
    /// 仅读取 WPF 头部与 FAT 元数据来枚举条目，避免一次性读取整个 WPF 到内存。
    /// </summary>
    public static bool TryEnumerateEntriesFromFile(
        string wpfPath,
        out List<WpfEntry> entries,
        out string error,
        WpfNameDecoding nameDecoding = WpfNameDecoding.Default)
    {
        entries = new List<WpfEntry>();
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

        try
        {
            using var fs = new FileStream(wpfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long fileSize = fs.Length;
            if (fileSize < HeaderProbeSize)
            {
                error = $"WPF 文件过短: {wpfPath}";
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderProbeSize];
            if (!TryReadExactly(fs, header, out error))
            {
                return false;
            }

            uint magic = ReadU32(header, 0);
            if (magic != Magic)
            {
                error = "Invalid WPF magic";
                return false;
            }

            ushort headerSize = ReadU16(header, 4);
            ushort bytesPerBlock = ReadU16(header, 6);
            if (bytesPerBlock == 0) bytesPerBlock = 128;

            uint dirCountLegacy = ReadU32(header, 28);
            uint fileCountLegacy = ReadU32(header, 32);
            long fatPosLegacy = ReadI64(header, 20);

            uint dirCountPrimary = ReadU32(header, 8);
            uint fileCountPrimary = ReadU32(header, 12);
            long fatPosPrimary = ReadI64(header, 16);

            static bool IsValidFat(long pos, long size) => pos > 0 && pos < size;

            uint dirCount;
            uint fileCount;
            long fatPos;

            if (IsValidFat(fatPosPrimary, fileSize) && (dirCountPrimary != 0 || fileCountPrimary != 0))
            {
                dirCount = dirCountPrimary;
                fileCount = fileCountPrimary;
                fatPos = fatPosPrimary;
            }
            else
            {
                dirCount = dirCountLegacy;
                fileCount = fileCountLegacy;
                fatPos = fatPosLegacy;
            }

            uint totalEntries = dirCount + fileCount;
            if (totalEntries == 0)
            {
                return true;
            }

            if (!IsValidFat(fatPos, fileSize))
            {
                fatPos = headerSize;
            }
            if (!IsValidFat(fatPos, fileSize))
            {
                error = "Invalid FAT position";
                return false;
            }

            ulong fcb1TableSize = (ulong)totalEntries * Fcb1Size;
            ulong fcb2TableSize = (ulong)totalEntries * Fcb2Size;
            ulong required = (ulong)fatPos + fcb1TableSize + fcb2TableSize;
            if (required > (ulong)fileSize)
            {
                error = "WPF FAT 超出文件范围";
                return false;
            }

            if (required > int.MaxValue)
            {
                error = "WPF 元数据过大，当前实现不支持（超过 int 范围）";
                return false;
            }

            byte[] meta = new byte[(int)required];
            fs.Position = 0;
            if (!TryReadExactly(fs, meta.AsSpan(), out error))
            {
                return false;
            }

            return TryEnumerateEntriesFromMemoryCore(meta, wpfPath, fileSize, out entries, out error, nameDecoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 WPF 失败: {wpfPath}\n{ex.Message}";
            return false;
        }
    }

    public static bool TryEnumerateEntriesFromMemory(
        ReadOnlySpan<byte> wpfBytes,
        string label,
        out List<WpfEntry> entries,
        out string error,
        WpfNameDecoding nameDecoding = WpfNameDecoding.Default)
    {
        return TryEnumerateEntriesFromMemoryCore(wpfBytes, label, wpfBytes.Length, out entries, out error, nameDecoding);
    }

    public static bool TryExtractEntryFromMemory(
        ReadOnlySpan<byte> wpfBytes,
        WpfEntry entry,
        out byte[] outBytes,
        out string error)
    {
        outBytes = Array.Empty<byte>();
        error = string.Empty;

        if (entry.IsDirectory || entry.ByteSize == 0)
        {
            return true;
        }

        if (entry.ByteOffset > int.MaxValue || entry.ByteSize > int.MaxValue)
        {
            error = "WPF entry 太大，当前实现不支持（超过 int 范围）";
            return false;
        }

        int offset = (int)entry.ByteOffset;
        int size = (int)entry.ByteSize;
        if (offset < 0 || size < 0 || offset + size > wpfBytes.Length)
        {
            error = "WPF entry 超出文件范围";
            return false;
        }

        ReadOnlySpan<byte> raw = wpfBytes.Slice(offset, size);

        if (entry.IsCompressed)
        {
            if (TryDecompressWpfData(raw, out outBytes, out _))
            {
                return true;
            }

            // 解压失败：兼容旧工程逻辑，尝试按特征再次自动探测，最终回退原始数据
            if (IsChunkedCompression(raw) && ZlibUtils.TryDecompressChunked(raw, out outBytes, out _))
            {
                return true;
            }
            if (IsZlibHeader(raw) && ZlibUtils.TryDecompress(raw, out outBytes, out _))
            {
                return true;
            }

            outBytes = raw.ToArray();
            return true;
        }

        // 即使 flag 未标记压缩，也尝试按特征自动探测
        if (IsChunkedCompression(raw) && ZlibUtils.TryDecompressChunked(raw, out outBytes, out _))
        {
            return true;
        }
        if (IsZlibHeader(raw) && ZlibUtils.TryDecompress(raw, out outBytes, out _))
        {
            return true;
        }

        outBytes = raw.ToArray();
        return true;
    }

    public static bool TryExtractEntryFromFile(
        string wpfPath,
        WpfEntry entry,
        out byte[] outBytes,
        out string error)
    {
        outBytes = Array.Empty<byte>();
        error = string.Empty;

        if (entry.IsDirectory || entry.ByteSize == 0)
        {
            return true;
        }

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

        if (entry.ByteOffset > long.MaxValue || entry.ByteSize > int.MaxValue)
        {
            error = "WPF entry 太大，当前实现不支持（超过 int 范围）";
            return false;
        }

        long offset = (long)entry.ByteOffset;
        int size = (int)entry.ByteSize;

        try
        {
            using var fs = new FileStream(wpfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset < 0 || size < 0 || offset + size > fs.Length)
            {
                error = "WPF entry 超出文件范围";
                return false;
            }

            fs.Position = offset;
            byte[] rawBytes = new byte[size];
            if (!TryReadExactly(fs, rawBytes.AsSpan(), out error))
            {
                return false;
            }

            ReadOnlySpan<byte> raw = rawBytes;

            if (entry.IsCompressed)
            {
                if (TryDecompressWpfData(raw, out outBytes, out _))
                {
                    return true;
                }

                // 解压失败：兼容旧工程逻辑，尝试按特征再次自动探测，最终回退原始数据
                if (IsChunkedCompression(raw) && ZlibUtils.TryDecompressChunked(raw, out outBytes, out _))
                {
                    return true;
                }
                if (IsZlibHeader(raw) && ZlibUtils.TryDecompress(raw, out outBytes, out _))
                {
                    return true;
                }

                outBytes = rawBytes;
                return true;
            }

            // 即使 flag 未标记压缩，也尝试按特征自动探测
            if (IsChunkedCompression(raw) && ZlibUtils.TryDecompressChunked(raw, out outBytes, out _))
            {
                return true;
            }
            if (IsZlibHeader(raw) && ZlibUtils.TryDecompress(raw, out outBytes, out _))
            {
                return true;
            }

            outBytes = rawBytes;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 WPF entry 失败: {wpfPath}\n{ex.Message}";
            outBytes = Array.Empty<byte>();
            return false;
        }
    }

    public static bool TryWriteArchive(string wpfPath, IReadOnlyList<WpfPackEntry> files, out string error)
    {
        error = string.Empty;

        try
        {
            var root = new TreeNode();
            foreach (var f in files ?? Array.Empty<WpfPackEntry>())
            {
                string rel = NormalizePackPath(f.Path);
                if (string.IsNullOrWhiteSpace(rel)) continue;

                string[] parts = SplitPathParts(rel);
                if (parts.Length == 0) continue;

                TreeNode node = root;
                for (int i = 0; i + 1 < parts.Length; i++)
                {
                    node = node.Dirs.GetOrAdd(parts[i], static _ => new TreeNode());
                }

                node.LeafFiles[parts[^1]] = f.Bytes ?? Array.Empty<byte>();
            }

            // WPF 通常以“根目录 entry”作为索引树的起点（index=0，DIR），其子节点覆盖全部条目。
            // 旧工程 EnumerateWpfEntries 在检测到 entry0 为 DIR 时会跳过它，只枚举其 children。
            // 为保持读写闭环一致，这里总是写入一个空名字的 root directory entry。
            var flat = new List<FlatEntry>
            {
                new FlatEntry { Name = string.Empty, IsDir = true, Start = 0, Size = 0, Rev = 0, Payload = Array.Empty<byte>() },
            };

            uint childStart = (uint)flat.Count;
            AppendChildren(root, flat);
            uint childCount = (uint)flat.Count - childStart;
            flat[0].Start = childStart;
            flat[0].Size = childCount;

            uint total = (uint)flat.Count;
            uint dirCount = (uint)flat.Count(static e => e.IsDir);
            uint fileCount = total - dirCount;

            const ushort headerSize = 128;
            const ushort bytesPerBlock = 128;
            const ulong fatPos = headerSize;

            ulong fatSize = (ulong)total * (Fcb1Size + Fcb2Size);
            ulong dataStart = fatPos + fatSize;

            static ulong CeilDiv(ulong a, ulong b) => (a + b - 1) / b;
            ulong blockCursor = CeilDiv(dataStart - headerSize, bytesPerBlock);

            foreach (var e in flat)
            {
                if (e.IsDir) continue;
                e.Start = (uint)Math.Min(blockCursor, uint.MaxValue);
                ulong blocks = CeilDiv((ulong)e.Payload.Length, bytesPerBlock);
                blockCursor += blocks;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(wpfPath) ?? ".");
            using var fs = new FileStream(wpfPath, FileMode.Create, FileAccess.Write, FileShare.None);

            // Header
            byte[] header = new byte[headerSize];
            WriteU32(header, 0, Magic);
            WriteU16(header, 4, headerSize);
            WriteU16(header, 6, bytesPerBlock);
            WriteU32(header, 8, dirCount);
            WriteU32(header, 12, fileCount);
            WriteI64(header, 16, (long)fatPos);
            WriteI64(header, 20, (long)fatPos);
            WriteU32(header, 28, dirCount);
            WriteU32(header, 32, fileCount);
            fs.Write(header);

            // FAT
            fs.Position = (long)fatPos;

            Span<byte> fcb1 = stackalloc byte[Fcb1Size];
            foreach (var e in flat)
            {
                fcb1.Clear();
                WriteU32(fcb1, 0, e.Start);
                WriteU32(fcb1, 4, e.Size);
                // hash 置 0（与旧工程 writer 保持一致）
                fs.Write(fcb1);
            }

            Span<byte> fcb2 = stackalloc byte[Fcb2Size];
            foreach (var e in flat)
            {
                fcb2.Clear();

                string name = e.Name;
                while (Encoding.UTF8.GetByteCount(name) > 31)
                {
                    name = name.Substring(0, name.Length - 1);
                }

                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 31)).CopyTo(fcb2.Slice(0, 32));

                uint attr = e.IsDir ? AttrDir : 0u;
                WriteU32(fcb2, 48, attr);
                WriteU32(fcb2, 52, e.Rev);

                fs.Write(fcb2);
            }

            // Payloads
            foreach (var e in flat)
            {
                if (e.IsDir || e.Payload.Length == 0) continue;
                ulong offset = (ulong)e.Start * bytesPerBlock + headerSize;
                fs.Position = (long)offset;
                fs.Write(e.Payload, 0, e.Payload.Length);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"写入 WPF 失败: {wpfPath}\n{ex.Message}";
            return false;
        }
    }

    private sealed class TreeNode
    {
        public SortedDictionary<string, TreeNode> Dirs { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, byte[]> LeafFiles { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FlatEntry
    {
        public string Name { get; init; } = string.Empty;
        public bool IsDir { get; init; }
        public uint Start { get; set; }
        public uint Size { get; set; }
        public uint Rev { get; set; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }

    private static void AppendChildren(TreeNode node, List<FlatEntry> flat)
    {
        foreach (var (dirName, dirNode) in node.Dirs)
        {
            int dirPos = flat.Count;
            flat.Add(new FlatEntry { Name = dirName, IsDir = true, Start = 0, Size = 0, Rev = 0, Payload = Array.Empty<byte>() });

            uint childStart = (uint)flat.Count;
            AppendChildren(dirNode, flat);
            uint childCount = (uint)flat.Count - childStart;

            flat[dirPos].Start = childStart;
            flat[dirPos].Size = childCount;
        }

        foreach (var (fileName, bytes) in node.LeafFiles)
        {
            flat.Add(new FlatEntry
            {
                Name = fileName,
                IsDir = false,
                Start = 0,
                Size = (uint)(bytes?.Length ?? 0),
                Rev = 0,
                Payload = bytes ?? Array.Empty<byte>(),
            });
        }
    }

    private static bool TryDecompressWpfData(ReadOnlySpan<byte> raw, out byte[] bytes, out string error)
    {
        if (IsChunkedCompression(raw))
        {
            return ZlibUtils.TryDecompressChunked(raw, out bytes, out error);
        }

        return ZlibUtils.TryDecompress(raw, out bytes, out error);
    }

    private static bool IsZlibHeader(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 2) return false;
        byte cmf = raw[0];
        byte flg = raw[1];
        return (cmf & 0x0F) == 0x08 && (((cmf * 256) + flg) % 31) == 0;
    }

    private static bool IsChunkedCompression(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 6) return false;
        uint firstChunkSize = ReadU32(raw, 0);
        return firstChunkSize > 0
               && (ulong)firstChunkSize + 4 <= (ulong)raw.Length
               && IsZlibHeader(raw.Slice(4));
    }

    private static string DecodeName(ReadOnlySpan<byte> nameBytes, WpfNameDecoding nameDecoding)
    {
        int end = nameBytes.IndexOf((byte)0);
        if (end < 0) end = nameBytes.Length;
        ReadOnlySpan<byte> slice = nameBytes.Slice(0, end);
        if (slice.IsEmpty) return string.Empty;

        // 优先严格 UTF-8；失败则按策略回退（默认 Latin1 保留原始字节，避免直接丢失信息）。
        try
        {
            return StrictUtf8.GetString(slice);
        }
        catch (DecoderFallbackException)
        {
            if (nameDecoding == WpfNameDecoding.Utf8StrictThenGbkThenLatin1 && TryDecodeGbk(slice, out string gbk))
            {
                return gbk;
            }

            return Encoding.Latin1.GetString(slice);
        }
    }

    private static WpfNameDecoding ResolveNameDecoding(WpfNameDecoding requested)
    {
        if (requested != WpfNameDecoding.Default)
        {
            return requested;
        }

        string? env = Environment.GetEnvironmentVariable("WOOOL_WPF_NAME_FALLBACK");
        if (string.IsNullOrWhiteSpace(env))
        {
            return WpfNameDecoding.Utf8StrictThenLatin1;
        }

        string v = env.Trim();
        return v.Equals("gbk", StringComparison.OrdinalIgnoreCase)
               || v.Equals("936", StringComparison.OrdinalIgnoreCase)
               || v.Equals("cp936", StringComparison.OrdinalIgnoreCase)
               || v.Equals("gb2312", StringComparison.OrdinalIgnoreCase)
               || v.Equals("gb18030", StringComparison.OrdinalIgnoreCase)
            ? WpfNameDecoding.Utf8StrictThenGbkThenLatin1
            : WpfNameDecoding.Utf8StrictThenLatin1;
    }

    private static bool TryDecodeGbk(ReadOnlySpan<byte> bytes, out string text)
    {
        text = string.Empty;

        Encoding? enc = GbkEncoding.Value;
        if (enc is null)
        {
            return false;
        }

        try
        {
            text = enc.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string NormalizePackPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string p = input.Replace('\\', '/');
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

    private static string[] SplitPathParts(string p)
    {
        return p.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool ProcessEntries(
        uint start,
        uint count,
        string basePath,
        uint totalEntries,
        uint[] starts,
        uint[] sizes,
        long[] hashes,
        string[] names,
        uint[] attrs,
        uint[] revs,
        ushort bytesPerBlock,
        ushort headerSize,
        int fileSize,
        List<WpfEntry> output,
        out string error)
    {
        error = string.Empty;

        uint end = Math.Min(totalEntries, start + count);
        for (uint i = start; i < end; i++)
        {
            string name = names[i];
            string fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";

            bool isDir = (attrs[i] & AttrDir) != 0;
            bool isCompressed = (attrs[i] & AttrCompress) != 0;

            if (isDir)
            {
                output.Add(new WpfEntry
                {
                    Index = (int)i,
                    Name = name,
                    FullPath = fullPath,
                    IsDirectory = true,
                    IsCompressed = isCompressed,
                    Hash = hashes[i],
                    ByteOffset = 0,
                    ByteSize = 0,
                    Offset = 0,
                    Size = 0,
                    UncompressedSize = 0,
                });

                uint childStart = starts[i];
                uint childCount = sizes[i];
                if (!ProcessEntries(childStart, childCount, fullPath,
                        totalEntries, starts, sizes, hashes, names, attrs, revs,
                        bytesPerBlock, headerSize, fileSize, output,
                        out error))
                {
                    return false;
                }
            }
            else
            {
                ulong dataOffset = (ulong)starts[i] * bytesPerBlock + headerSize;
                uint byteSize = sizes[i];
                ulong dataEnd = dataOffset + byteSize;

                if (dataEnd > (ulong)fileSize)
                {
                    error = $"WPF entry 数据超出文件范围: {fullPath}";
                    return false;
                }

                output.Add(new WpfEntry
                {
                    Index = (int)i,
                    Name = name,
                    FullPath = fullPath,
                    IsDirectory = false,
                    IsCompressed = isCompressed,
                    Hash = hashes[i],
                    ByteOffset = dataOffset,
                    ByteSize = byteSize,
                    Offset = dataOffset,
                    Size = byteSize,
                    UncompressedSize = isCompressed ? revs[i] : byteSize,
                });
            }
        }

        return true;
    }

    private static bool TryEnumerateEntriesFromMemoryCore(
        ReadOnlySpan<byte> wpfBytes,
        string label,
        long fileSize,
        out List<WpfEntry> entries,
        out string error,
        WpfNameDecoding nameDecoding)
    {
        entries = new List<WpfEntry>();
        error = string.Empty;

        WpfNameDecoding effectiveDecoding = ResolveNameDecoding(nameDecoding);

        if (wpfBytes.Length < HeaderProbeSize)
        {
            error = $"WPF 文件过短: {label}";
            return false;
        }

        uint magic = ReadU32(wpfBytes, 0);
        if (magic != Magic)
        {
            error = "Invalid WPF magic";
            return false;
        }

        ushort headerSize = ReadU16(wpfBytes, 4);
        ushort bytesPerBlock = ReadU16(wpfBytes, 6);
        if (bytesPerBlock == 0) bytesPerBlock = 128;

        uint dirCountLegacy = ReadU32(wpfBytes, 28);
        uint fileCountLegacy = ReadU32(wpfBytes, 32);
        long fatPosLegacy = ReadI64(wpfBytes, 20);

        uint dirCountPrimary = ReadU32(wpfBytes, 8);
        uint fileCountPrimary = ReadU32(wpfBytes, 12);
        long fatPosPrimary = ReadI64(wpfBytes, 16);

        static bool IsValidFat(long pos, long size) => pos > 0 && pos < size;

        uint dirCount;
        uint fileCount;
        long fatPos;

        if (IsValidFat(fatPosPrimary, fileSize) && (dirCountPrimary != 0 || fileCountPrimary != 0))
        {
            dirCount = dirCountPrimary;
            fileCount = fileCountPrimary;
            fatPos = fatPosPrimary;
        }
        else
        {
            dirCount = dirCountLegacy;
            fileCount = fileCountLegacy;
            fatPos = fatPosLegacy;
        }

        uint totalEntries = dirCount + fileCount;
        if (totalEntries == 0)
        {
            return true;
        }

        if (!IsValidFat(fatPos, fileSize))
        {
            fatPos = headerSize;
        }
        if (!IsValidFat(fatPos, fileSize))
        {
            error = "Invalid FAT position";
            return false;
        }

        ulong fcb1TableSize = (ulong)totalEntries * Fcb1Size;
        ulong fcb2TableSize = (ulong)totalEntries * Fcb2Size;
        ulong required = (ulong)fatPos + fcb1TableSize + fcb2TableSize;
        if (required > (ulong)wpfBytes.Length)
        {
            error = "WPF FAT 超出文件范围";
            return false;
        }

        var starts = new uint[totalEntries];
        var sizes = new uint[totalEntries];
        var hashes = new long[totalEntries];
        for (uint i = 0; i < totalEntries; i++)
        {
            int off = checked((int)fatPos + (int)(i * Fcb1Size));
            starts[i] = ReadU32(wpfBytes, off);
            sizes[i] = ReadU32(wpfBytes, off + 4);
            hashes[i] = ReadI64(wpfBytes, off + 8);
        }

        var names = new string[totalEntries];
        var attrs = new uint[totalEntries];
        var revs = new uint[totalEntries];
        long fcb2Base = fatPos + (long)fcb1TableSize;
        for (uint i = 0; i < totalEntries; i++)
        {
            int off = checked((int)fcb2Base + (int)(i * Fcb2Size));

            ReadOnlySpan<byte> nameBytes = wpfBytes.Slice(off, 32);
            names[i] = DecodeName(nameBytes, effectiveDecoding);

            attrs[i] = ReadU32(wpfBytes, off + 48);
            revs[i] = ReadU32(wpfBytes, off + 52);
        }

        var result = new List<WpfEntry>();

        int sizeInt = fileSize > int.MaxValue ? int.MaxValue : (int)fileSize;

        if ((attrs[0] & AttrDir) != 0)
        {
            if (!ProcessEntries(
                    start: starts[0],
                    count: sizes[0],
                    basePath: string.Empty,
                    totalEntries: totalEntries,
                    starts: starts,
                    sizes: sizes,
                    hashes: hashes,
                    names: names,
                    attrs: attrs,
                    revs: revs,
                    bytesPerBlock: bytesPerBlock,
                    headerSize: headerSize,
                    fileSize: sizeInt,
                    output: result,
                    error: out error))
            {
                return false;
            }
        }
        else
        {
            if (!ProcessEntries(
                    start: 0,
                    count: totalEntries,
                    basePath: string.Empty,
                    totalEntries: totalEntries,
                    starts: starts,
                    sizes: sizes,
                    hashes: hashes,
                    names: names,
                    attrs: attrs,
                    revs: revs,
                    bytesPerBlock: bytesPerBlock,
                    headerSize: headerSize,
                    fileSize: sizeInt,
                    output: result,
                    error: out error))
            {
                return false;
            }
        }

        entries = result;
        return true;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer, out string error)
    {
        error = string.Empty;

        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(offset));
            if (read <= 0)
            {
                error = "意外的 EOF";
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4));
    private static ushort ReadU16(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(offset, 2));
    private static long ReadI64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadInt64LittleEndian(s.Slice(offset, 8));

    private static void WriteU32(byte[] buf, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);
    private static void WriteU16(byte[] buf, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), v);
    private static void WriteI64(byte[] buf, int off, long v) => BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off, 8), v);
    private static void WriteU32(Span<byte> buf, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(off, 4), v);
}

internal static class SortedDictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this SortedDictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue> factory)
        where TKey : notnull
    {
        if (dict.TryGetValue(key, out TValue? value))
        {
            return value;
        }

        value = factory(key);
        dict.Add(key, value);
        return value;
    }
}
