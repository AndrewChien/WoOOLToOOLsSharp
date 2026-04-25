using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WoOOLToOOLsSharp.Shared.Formats.Sgl;

public static class SglCodec
{
    private const int ShandaHeaderSize = 36;
    private static readonly byte[] ShandaMagic = Encoding.ASCII.GetBytes("shanda game lib"); // 15 bytes

    public static bool TryEnumerateEntries(string sglPath, out List<SglImageEntry> entries, out string error)
    {
        entries = new List<SglImageEntry>();
        error = string.Empty;

        if (!FileIO.TryReadAllBytes(sglPath, out byte[] bytes, out error))
        {
            return false;
        }

        return TryEnumerateEntriesFromMemory(bytes, sglPath, out entries, out error);
    }

    public static bool TryEnumerateEntriesFromMemory(
        ReadOnlySpan<byte> sglBytes,
        string label,
        out List<SglImageEntry> entries,
        out string error)
    {
        entries = new List<SglImageEntry>();
        error = string.Empty;

        if (sglBytes.Length < 8)
        {
            error = $"SGL 文件过短: {label}";
            return false;
        }

        bool isShanda = sglBytes.Length >= 16 && IsShandaHeader(sglBytes);
        if (isShanda)
        {
            return TryEnumerateShandaEntries(sglBytes, label, out entries, out error);
        }

        return TryEnumerateStandardEntries(sglBytes, label, out entries, out error);
    }

    private static bool TryEnumerateStandardEntries(
        ReadOnlySpan<byte> sglBytes,
        string label,
        out List<SglImageEntry> entries,
        out string error)
    {
        entries = new List<SglImageEntry>();
        error = string.Empty;

        if (sglBytes.Length < 4)
        {
            error = $"读取 SGL 文件数量失败: {label}";
            return false;
        }

        uint fileCountU32 = ReadU32(sglBytes, 0);
        if (fileCountU32 is 0 or > 100000)
        {
            error = $"SGL fileCount 非法: {fileCountU32}";
            return false;
        }

        int fileCount = (int)fileCountU32;
        int pos = 4;

        if (sglBytes.Length < pos + fileCount * 4)
        {
            error = $"SGL 索引表越界: fileCount={fileCount}";
            return false;
        }

        uint[] offsets = new uint[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            offsets[i] = ReadU32(sglBytes, pos);
            pos += 4;
        }

        uint fileSize = (uint)sglBytes.Length;
        for (int i = 0; i < fileCount; i++)
        {
            uint off = offsets[i];
            uint size = 0;

            if (off != 0)
            {
                uint nextOff = fileSize;
                for (int j = i + 1; j < fileCount; j++)
                {
                    uint o2 = offsets[j];
                    if (o2 != 0 && o2 >= off)
                    {
                        nextOff = o2;
                        break;
                    }
                }

                size = nextOff > off ? nextOff - off : 0;
            }

            entries.Add(new SglImageEntry { Index = i, Offset = off, Size = size });
        }

        return true;
    }

    private static bool TryEnumerateShandaEntries(
        ReadOnlySpan<byte> sglBytes,
        string label,
        out List<SglImageEntry> entries,
        out string error)
    {
        entries = new List<SglImageEntry>();
        error = string.Empty;

        if (sglBytes.Length < ShandaHeaderSize + 4)
        {
            error = $"Shanda SGL 文件过短: {label}";
            return false;
        }

        uint indexTableOffset = ReadU32(sglBytes, ShandaHeaderSize);
        if (indexTableOffset >= (uint)sglBytes.Length || indexTableOffset < ShandaHeaderSize + 4)
        {
            error = "Shanda indexTableOffset 非法";
            return false;
        }

        if (indexTableOffset > int.MaxValue)
        {
            error = "SGL 文件过大（不支持 >2GB）";
            return false;
        }

        int pos = (int)indexTableOffset;
        if (pos + 4 > sglBytes.Length)
        {
            error = "读取 Shanda fileCount 失败";
            return false;
        }

        uint fileCountU32 = ReadU32(sglBytes, pos);
        pos += 4;
        if (fileCountU32 is 0 or > 0xFFFF)
        {
            error = $"Shanda fileCount 非法: {fileCountU32}";
            return false;
        }

        int fileCount = (int)fileCountU32;
        if (sglBytes.Length < pos + fileCount * 4)
        {
            error = "Shanda 索引表越界";
            return false;
        }

        uint[] offsets = new uint[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            offsets[i] = ReadU32(sglBytes, pos);
            pos += 4;
        }

        for (int i = 0; i < fileCount; i++)
        {
            uint off = offsets[i];
            uint size = 0;

            if (off != 0)
            {
                uint nextOff = indexTableOffset;
                for (int j = i + 1; j < fileCount; j++)
                {
                    uint o2 = offsets[j];
                    if (o2 != 0 && o2 >= off)
                    {
                        nextOff = o2;
                        break;
                    }
                }

                size = nextOff > off ? nextOff - off : 0;
            }

            entries.Add(new SglImageEntry { Index = i, Offset = off, Size = size });
        }

        return true;
    }

    private static bool IsShandaHeader(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < ShandaMagic.Length)
        {
            return false;
        }

        for (int i = 0; i < ShandaMagic.Length; i++)
        {
            byte c = buf[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c = (byte)(c + 32);
            }

            if (c != ShandaMagic[i])
            {
                return false;
            }
        }

        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>
    /// 写入标准 SGL（非 shanda 变体）：<c>u32 fileCount</c> + <c>u32 offsets[fileCount]</c> + 连续 TEX payload。
    /// 迁移自旧工程 <c>OldProj/shared/src/SglCodec.cpp</c> 的 <c>WriteSglLibrary</c>。
    /// </summary>
    public static bool TryWriteLibrary(string sglPath, IReadOnlyList<byte[]> texSlots, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sglPath))
        {
            error = "SGL 路径为空";
            return false;
        }

        texSlots ??= Array.Empty<byte[]>();

        try
        {
            string? parent = Path.GetDirectoryName(sglPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"创建输出目录失败：{ex.Message}";
            return false;
        }

        int count = texSlots.Count;
        ulong headerSize = 4ul + ((ulong)count * 4ul);

        var offsets = new uint[count];
        ulong cursor = headerSize;

        for (int i = 0; i < texSlots.Count; i++)
        {
            byte[]? tex = texSlots[i];
            if (tex is null || tex.Length == 0)
            {
                continue;
            }

            if (cursor > uint.MaxValue)
            {
                error = "SGL 文件太大（偏移超过 32-bit 范围）";
                return false;
            }

            offsets[i] = (uint)cursor;
            cursor += (ulong)tex.Length;
        }

        try
        {
            using var fs = new FileStream(sglPath, FileMode.Create, FileAccess.Write, FileShare.None);

            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)count);
            fs.Write(buf);

            for (int i = 0; i < offsets.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf, offsets[i]);
                fs.Write(buf);
            }

            for (int i = 0; i < texSlots.Count; i++)
            {
                byte[]? tex = texSlots[i];
                if (tex is null || tex.Length == 0)
                {
                    continue;
                }

                fs.Write(tex);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"写入 SGL 失败: {ex.Message}";
            return false;
        }
    }

    public static bool TryWriteLibraryToBytes(IReadOnlyList<byte[]> texSlots, out byte[] outBytes, out string error)
    {
        outBytes = Array.Empty<byte>();
        error = string.Empty;

        texSlots ??= Array.Empty<byte[]>();

        int count = texSlots.Count;
        ulong headerSize = 4ul + ((ulong)count * 4ul);

        var offsets = new uint[count];
        ulong cursor = headerSize;

        for (int i = 0; i < texSlots.Count; i++)
        {
            byte[]? tex = texSlots[i];
            if (tex is null || tex.Length == 0)
            {
                continue;
            }

            if (cursor > uint.MaxValue)
            {
                error = "SGL 文件太大（偏移超过 32-bit 范围）";
                return false;
            }

            offsets[i] = (uint)cursor;
            cursor += (ulong)tex.Length;
        }

        if (cursor > int.MaxValue)
        {
            error = "SGL 文件太大（超过 2GB，当前实现不支持）";
            return false;
        }

        try
        {
            using var ms = new MemoryStream((int)cursor);

            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)count);
            ms.Write(buf);

            for (int i = 0; i < offsets.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf, offsets[i]);
                ms.Write(buf);
            }

            for (int i = 0; i < texSlots.Count; i++)
            {
                byte[]? tex = texSlots[i];
                if (tex is null || tex.Length == 0)
                {
                    continue;
                }

                ms.Write(tex);
            }

            outBytes = ms.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"写入 SGL 到内存失败: {ex.Message}";
            return false;
        }
    }
}
