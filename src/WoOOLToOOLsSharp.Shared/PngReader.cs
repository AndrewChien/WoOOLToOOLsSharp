using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace WoOOLToOOLsSharp.Shared;

public static class PngReader
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private const uint ChunkIHdr = 0x49484452; // IHDR
    private const uint ChunkIDat = 0x49444154; // IDAT
    private const uint ChunkIEnd = 0x49454E44; // IEND
    private const uint ChunkPlte = 0x504C5445; // PLTE
    private const uint ChunkTrns = 0x74524E53; // tRNS

    public static bool TryReadRgba8(string filePath, out int width, out int height, out byte[] rgba8, out string error)
    {
        width = 0;
        height = 0;
        rgba8 = Array.Empty<byte>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "PNG 路径为空";
            return false;
        }

        if (!FileIO.TryReadAllBytes(filePath, out byte[] bytes, out error))
        {
            return false;
        }

        return TryReadRgba8FromMemory(bytes, out width, out height, out rgba8, out error);
    }

    public static bool TryReadRgba8FromMemory(ReadOnlySpan<byte> pngBytes, out int width, out int height, out byte[] rgba8, out string error)
    {
        width = 0;
        height = 0;
        rgba8 = Array.Empty<byte>();
        error = string.Empty;

        if (pngBytes.Length < PngSignature.Length + 12)
        {
            error = "PNG 数据过短";
            return false;
        }

        if (!pngBytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            error = "PNG 签名不匹配";
            return false;
        }

        bool hasIhdr = false;
        bool hasIend = false;

        int w = 0;
        int h = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte compressionMethod = 0;
        byte filterMethod = 0;
        byte interlaceMethod = 0;

        byte[]? palette = null;
        byte[]? paletteAlpha = null;

        using var idat = new MemoryStream();

        int pos = PngSignature.Length;
        while (pos + 12 <= pngBytes.Length)
        {
            uint lenU32 = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(pos, 4));
            pos += 4;

            uint type = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(pos, 4));
            pos += 4;

            if (lenU32 > int.MaxValue)
            {
                error = $"PNG chunk 过大: len={lenU32}";
                return false;
            }

            int len = (int)lenU32;

            if (pos + len + 4 > pngBytes.Length)
            {
                error = "PNG chunk 越界（数据损坏或截断）";
                return false;
            }

            ReadOnlySpan<byte> chunk = pngBytes.Slice(pos, len);
            pos += len;

            // Skip CRC (we do not validate CRC here).
            pos += 4;

            if (type == ChunkIHdr)
            {
                if (len != 13)
                {
                    error = $"IHDR 长度非法: {len}";
                    return false;
                }

                uint wU = BinaryPrimitives.ReadUInt32BigEndian(chunk[..4]);
                uint hU = BinaryPrimitives.ReadUInt32BigEndian(chunk.Slice(4, 4));

                if (wU == 0 || hU == 0 || wU > int.MaxValue || hU > int.MaxValue)
                {
                    error = $"IHDR 宽高非法: {wU} x {hU}";
                    return false;
                }

                w = (int)wU;
                h = (int)hU;
                bitDepth = chunk[8];
                colorType = chunk[9];
                compressionMethod = chunk[10];
                filterMethod = chunk[11];
                interlaceMethod = chunk[12];

                hasIhdr = true;
                continue;
            }

            if (type == ChunkPlte)
            {
                // 3 bytes per entry.
                if (len <= 0 || (len % 3) != 0)
                {
                    error = $"PLTE 长度非法: {len}";
                    return false;
                }

                palette = chunk.ToArray();
                continue;
            }

            if (type == ChunkTrns)
            {
                // For indexed-color (type 3): alpha for palette entries.
                paletteAlpha = chunk.ToArray();
                continue;
            }

            if (type == ChunkIDat)
            {
                if (len > 0)
                {
                    idat.Write(chunk);
                }
                continue;
            }

            if (type == ChunkIEnd)
            {
                hasIend = true;
                break;
            }
        }

        if (!hasIhdr)
        {
            error = "PNG 缺少 IHDR";
            return false;
        }

        if (!hasIend)
        {
            error = "PNG 缺少 IEND";
            return false;
        }

        if (compressionMethod != 0 || filterMethod != 0)
        {
            error = $"PNG 压缩/滤波方法不受支持: compression={compressionMethod} filter={filterMethod}";
            return false;
        }

        if (interlaceMethod != 0)
        {
            error = $"PNG 不支持隔行扫描: interlace={interlaceMethod}";
            return false;
        }

        if (bitDepth != 8)
        {
            error = $"PNG 暂仅支持 8-bit 深度: bitDepth={bitDepth}";
            return false;
        }

        int channels = colorType switch
        {
            0 => 1, // grayscale
            2 => 3, // rgb
            3 => 1, // indexed
            4 => 2, // grayscale+alpha
            6 => 4, // rgba
            _ => 0
        };

        if (channels == 0)
        {
            error = $"PNG 颜色类型不受支持: colorType={colorType}";
            return false;
        }

        if (colorType == 3 && palette is null)
        {
            error = "PNG 为索引色（colorType=3），但缺少 PLTE";
            return false;
        }

        int stride;
        int expectedDecompressed;
        try
        {
            stride = checked(w * channels);
            expectedDecompressed = checked((stride + 1) * h);
        }
        catch (OverflowException)
        {
            error = $"PNG 图像过大: {w} x {h}";
            return false;
        }

        if (idat.Length <= 0)
        {
            error = "PNG 缺少 IDAT 数据";
            return false;
        }

        byte[] decompressed = new byte[expectedDecompressed];
        idat.Position = 0;

        try
        {
            using (var z = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
            {
                int readTotal = 0;
                while (readTotal < decompressed.Length)
                {
                    int n = z.Read(decompressed, readTotal, decompressed.Length - readTotal);
                    if (n <= 0)
                    {
                        break;
                    }
                    readTotal += n;
                }

                if (readTotal != decompressed.Length)
                {
                    error = $"PNG 解压长度不匹配: expected={decompressed.Length}, actual={readTotal}";
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            error = $"PNG 解压失败: {ex.Message}";
            return false;
        }

        int bpp = channels; // 8-bit => bytes per pixel
        var raw = new byte[checked(stride * h)];

        int src = 0;
        for (int y = 0; y < h; y++)
        {
            byte filter = decompressed[src];
            src++;

            Span<byte> dstRow = raw.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> srcRow = new ReadOnlySpan<byte>(decompressed, src, stride);
            src += stride;

            switch (filter)
            {
                case 0:
                    srcRow.CopyTo(dstRow);
                    break;
                case 1:
                    UnfilterSub(srcRow, dstRow, bpp);
                    break;
                case 2:
                    UnfilterUp(srcRow, dstRow, raw.AsSpan((y - 1) * stride, stride));
                    break;
                case 3:
                    UnfilterAverage(srcRow, dstRow, y > 0 ? raw.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty, bpp);
                    break;
                case 4:
                    UnfilterPaeth(srcRow, dstRow, y > 0 ? raw.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty, bpp);
                    break;
                default:
                    error = $"PNG 行滤波类型不受支持: filter={filter}";
                    return false;
            }
        }

        if (colorType == 6)
        {
            width = w;
            height = h;
            rgba8 = raw;
            return true;
        }

        byte[] outRgba;
        try
        {
            outRgba = new byte[checked(w * h * 4)];
        }
        catch (OverflowException)
        {
            error = $"PNG 图像过大: {w} x {h}";
            return false;
        }

        if (colorType == 2)
        {
            ExpandRgbToRgba(raw, outRgba);
        }
        else if (colorType == 0)
        {
            ExpandGrayToRgba(raw, outRgba);
        }
        else if (colorType == 4)
        {
            ExpandGrayAlphaToRgba(raw, outRgba);
        }
        else if (colorType == 3)
        {
            ExpandIndexedToRgba(raw, palette!, paletteAlpha, outRgba, out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }
        }
        else
        {
            error = $"PNG 颜色类型不受支持: colorType={colorType}";
            return false;
        }

        width = w;
        height = h;
        rgba8 = outRgba;
        return true;
    }

    private static void UnfilterSub(ReadOnlySpan<byte> src, Span<byte> dst, int bpp)
    {
        src.CopyTo(dst);
        for (int i = 0; i < dst.Length; i++)
        {
            int left = i >= bpp ? dst[i - bpp] : 0;
            dst[i] = unchecked((byte)(dst[i] + left));
        }
    }

    private static void UnfilterUp(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> prev)
    {
        src.CopyTo(dst);
        if (prev.IsEmpty)
        {
            return;
        }

        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = unchecked((byte)(dst[i] + prev[i]));
        }
    }

    private static void UnfilterAverage(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> prev, int bpp)
    {
        src.CopyTo(dst);
        for (int i = 0; i < dst.Length; i++)
        {
            int left = i >= bpp ? dst[i - bpp] : 0;
            int up = !prev.IsEmpty ? prev[i] : 0;
            int avg = (left + up) >> 1;
            dst[i] = unchecked((byte)(dst[i] + avg));
        }
    }

    private static void UnfilterPaeth(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> prev, int bpp)
    {
        src.CopyTo(dst);
        for (int i = 0; i < dst.Length; i++)
        {
            int left = i >= bpp ? dst[i - bpp] : 0;
            int up = !prev.IsEmpty ? prev[i] : 0;
            int upLeft = (i >= bpp && !prev.IsEmpty) ? prev[i - bpp] : 0;
            int paeth = PaethPredictor(left, up, upLeft);
            dst[i] = unchecked((byte)(dst[i] + paeth));
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static void ExpandRgbToRgba(ReadOnlySpan<byte> rgb, Span<byte> rgba)
    {
        int pixels = rgb.Length / 3;
        for (int i = 0; i < pixels; i++)
        {
            int si = i * 3;
            int di = i * 4;
            rgba[di + 0] = rgb[si + 0];
            rgba[di + 1] = rgb[si + 1];
            rgba[di + 2] = rgb[si + 2];
            rgba[di + 3] = 255;
        }
    }

    private static void ExpandGrayToRgba(ReadOnlySpan<byte> gray, Span<byte> rgba)
    {
        for (int i = 0; i < gray.Length; i++)
        {
            byte v = gray[i];
            int di = i * 4;
            rgba[di + 0] = v;
            rgba[di + 1] = v;
            rgba[di + 2] = v;
            rgba[di + 3] = 255;
        }
    }

    private static void ExpandGrayAlphaToRgba(ReadOnlySpan<byte> grayAlpha, Span<byte> rgba)
    {
        int pixels = grayAlpha.Length / 2;
        for (int i = 0; i < pixels; i++)
        {
            int si = i * 2;
            byte v = grayAlpha[si + 0];
            byte a = grayAlpha[si + 1];
            int di = i * 4;
            rgba[di + 0] = v;
            rgba[di + 1] = v;
            rgba[di + 2] = v;
            rgba[di + 3] = a;
        }
    }

    private static void ExpandIndexedToRgba(ReadOnlySpan<byte> indices, ReadOnlySpan<byte> palette, byte[]? paletteAlpha, Span<byte> rgba, out string error)
    {
        error = string.Empty;

        int entryCount = palette.Length / 3;
        if (entryCount <= 0)
        {
            error = "PLTE 为空";
            return;
        }

        for (int i = 0; i < indices.Length; i++)
        {
            int idx = indices[i];
            if ((uint)idx >= (uint)entryCount)
            {
                error = $"索引色越界: index={idx}, palette={entryCount}";
                return;
            }

            int pi = idx * 3;
            byte r = palette[pi + 0];
            byte g = palette[pi + 1];
            byte b = palette[pi + 2];
            byte a = (paletteAlpha is not null && idx < paletteAlpha.Length) ? paletteAlpha[idx] : (byte)255;

            int di = i * 4;
            rgba[di + 0] = r;
            rgba[di + 1] = g;
            rgba[di + 2] = b;
            rgba[di + 3] = a;
        }
    }
}

