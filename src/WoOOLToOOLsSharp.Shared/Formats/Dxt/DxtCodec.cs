using System;
using System.Buffers.Binary;

namespace WoOOLToOOLsSharp.Shared.Formats.Dxt;

public static class DxtCodec
{
    public static bool TryDecodeDxt1ToRgba8(ReadOnlySpan<byte> src, Span<byte> dstRgba8, int width, int height, out string error)
    {
        error = string.Empty;
        if (!ValidateDecodeArgs(src, dstRgba8, width, height, blockBytes: 8, out error))
        {
            return false;
        }

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int expectedBytes = blocksX * blocksY * 8;
        if (src.Length < expectedBytes)
        {
            error = $"DXT1 数据截断：期望至少 {expectedBytes} 字节，实际 {src.Length} 字节";
            return false;
        }

        int sp = 0;
        Span<byte> block = stackalloc byte[4 * 4 * 4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                DecodeDxt1Block(src.Slice(sp, 8), block);
                sp += 8;

                int destX = bx * 4;
                int destY = by * 4;
                CopyBlockToImage(block, dstRgba8, width, height, destX, destY);
            }
        }

        return true;
    }

    public static bool TryDecodeDxt3ToRgba8(ReadOnlySpan<byte> src, Span<byte> dstRgba8, int width, int height, out string error)
    {
        error = string.Empty;
        if (!ValidateDecodeArgs(src, dstRgba8, width, height, blockBytes: 16, out error))
        {
            return false;
        }

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int expectedBytes = blocksX * blocksY * 16;
        if (src.Length < expectedBytes)
        {
            error = $"DXT3 数据截断：期望至少 {expectedBytes} 字节，实际 {src.Length} 字节";
            return false;
        }

        int sp = 0;
        Span<byte> block = stackalloc byte[4 * 4 * 4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                DecodeDxt3Block(src.Slice(sp, 16), block);
                sp += 16;

                int destX = bx * 4;
                int destY = by * 4;
                CopyBlockToImage(block, dstRgba8, width, height, destX, destY);
            }
        }

        return true;
    }

    public static bool TryDecodeDxt5ToRgba8(ReadOnlySpan<byte> src, Span<byte> dstRgba8, int width, int height, out string error)
    {
        error = string.Empty;
        if (!ValidateDecodeArgs(src, dstRgba8, width, height, blockBytes: 16, out error))
        {
            return false;
        }

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int expectedBytes = blocksX * blocksY * 16;
        if (src.Length < expectedBytes)
        {
            error = $"DXT5 数据截断：期望至少 {expectedBytes} 字节，实际 {src.Length} 字节";
            return false;
        }

        int sp = 0;
        Span<byte> block = stackalloc byte[4 * 4 * 4];
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                DecodeDxt5Block(src.Slice(sp, 16), block);
                sp += 16;

                int destX = bx * 4;
                int destY = by * 4;
                CopyBlockToImage(block, dstRgba8, width, height, destX, destY);
            }
        }

        return true;
    }

    private static bool ValidateDecodeArgs(
        ReadOnlySpan<byte> src,
        ReadOnlySpan<byte> dstRgba8,
        int width,
        int height,
        int blockBytes,
        out string error)
    {
        error = string.Empty;

        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
        {
            error = $"无效尺寸：{width} x {height}";
            return false;
        }

        long requiredBytes = (long)width * height * 4;
        if (requiredBytes > int.MaxValue)
        {
            error = "输出缓冲区过大";
            return false;
        }

        if (dstRgba8.Length < (int)requiredBytes)
        {
            error = $"输出缓冲区不足：需要 {requiredBytes} 字节，实际 {dstRgba8.Length} 字节";
            return false;
        }

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        long expected = (long)blocksX * blocksY * blockBytes;
        if (expected > int.MaxValue)
        {
            error = "输入数据过大";
            return false;
        }

        if (src.Length < (int)expected)
        {
            error = "输入数据不足";
            return false;
        }

        return true;
    }

    private static void CopyBlockToImage(ReadOnlySpan<byte> blockRgba8, Span<byte> dstRgba8, int width, int height, int destX, int destY)
    {
        int copyWidth = Math.Min(4, width - destX);
        int copyHeight = Math.Min(4, height - destY);
        if (copyWidth <= 0 || copyHeight <= 0) return;

        for (int y = 0; y < copyHeight; y++)
        {
            int srcRow = y * 16;
            int dstRow = ((destY + y) * width + destX) * 4;
            blockRgba8.Slice(srcRow, copyWidth * 4).CopyTo(dstRgba8.Slice(dstRow));
        }
    }

    private static void DecodeDxt1Block(ReadOnlySpan<byte> src, Span<byte> dstBlockRgba8)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(0, 2));
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2, 2));
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));

        UnpackRgb565(c0, out byte r0, out byte g0, out byte b0);
        UnpackRgb565(c1, out byte r1, out byte g1, out byte b1);

        Span<byte> colors = stackalloc byte[4 * 4];
        SetColor(colors, 0, r0, g0, b0, 255);
        SetColor(colors, 1, r1, g1, b1, 255);

        if (c0 > c1)
        {
            SetColor(colors, 2, (byte)((2 * r0 + r1) / 3), (byte)((2 * g0 + g1) / 3), (byte)((2 * b0 + b1) / 3), 255);
            SetColor(colors, 3, (byte)((r0 + 2 * r1) / 3), (byte)((g0 + 2 * g1) / 3), (byte)((b0 + 2 * b1) / 3), 255);
        }
        else
        {
            SetColor(colors, 2, (byte)((r0 + r1) / 2), (byte)((g0 + g1) / 2), (byte)((b0 + b1) / 2), 255);
            SetColor(colors, 3, 0, 0, 0, 0);
        }

        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((indices >> (2 * i)) & 0x3u);
            int di = i * 4;
            int ci = idx * 4;
            dstBlockRgba8[di + 0] = colors[ci + 0];
            dstBlockRgba8[di + 1] = colors[ci + 1];
            dstBlockRgba8[di + 2] = colors[ci + 2];
            dstBlockRgba8[di + 3] = colors[ci + 3];
        }
    }

    private static void DecodeDxt3Block(ReadOnlySpan<byte> src, Span<byte> dstBlockRgba8)
    {
        Span<byte> alphas = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            byte a = src[i];
            alphas[i * 2 + 0] = (byte)((a & 0x0F) * 17);
            alphas[i * 2 + 1] = (byte)((a >> 4) * 17);
        }

        Span<byte> colorBlock = stackalloc byte[4 * 4 * 4];
        DecodeDxt1Block(src.Slice(8, 8), colorBlock);

        for (int i = 0; i < 16; i++)
        {
            int di = i * 4;
            dstBlockRgba8[di + 0] = colorBlock[di + 0];
            dstBlockRgba8[di + 1] = colorBlock[di + 1];
            dstBlockRgba8[di + 2] = colorBlock[di + 2];
            dstBlockRgba8[di + 3] = alphas[i];
        }
    }

    private static void DecodeDxt5Block(ReadOnlySpan<byte> src, Span<byte> dstBlockRgba8)
    {
        byte a0 = src[0];
        byte a1 = src[1];

        Span<byte> alphaPalette = stackalloc byte[8];
        alphaPalette[0] = a0;
        alphaPalette[1] = a1;

        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
            {
                alphaPalette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
        }
        else
        {
            for (int i = 1; i < 5; i++)
            {
                alphaPalette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            }
            alphaPalette[6] = 0;
            alphaPalette[7] = 255;
        }

        ulong alphaBits = 0;
        for (int i = 2; i < 8; i++)
        {
            alphaBits |= (ulong)src[i] << ((i - 2) * 8);
        }

        Span<byte> pixelAlphas = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((alphaBits >> (i * 3)) & 0x7u);
            pixelAlphas[i] = alphaPalette[idx];
        }

        Span<byte> colorBlock = stackalloc byte[4 * 4 * 4];
        DecodeDxt1Block(src.Slice(8, 8), colorBlock);

        for (int i = 0; i < 16; i++)
        {
            int di = i * 4;
            dstBlockRgba8[di + 0] = colorBlock[di + 0];
            dstBlockRgba8[di + 1] = colorBlock[di + 1];
            dstBlockRgba8[di + 2] = colorBlock[di + 2];
            dstBlockRgba8[di + 3] = pixelAlphas[i];
        }
    }

    private static void UnpackRgb565(ushort c, out byte r, out byte g, out byte b)
    {
        r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
        g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
        b = (byte)((c & 0x1F) * 255 / 31);
    }

    private static void SetColor(Span<byte> colors, int idx, byte r, byte g, byte b, byte a)
    {
        int o = idx * 4;
        colors[o + 0] = r;
        colors[o + 1] = g;
        colors[o + 2] = b;
        colors[o + 3] = a;
    }
}

