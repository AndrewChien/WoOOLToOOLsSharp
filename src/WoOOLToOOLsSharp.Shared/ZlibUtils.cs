using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace WoOOLToOOLsSharp.Shared;

public static class ZlibUtils
{
    public static bool TryDecompress(ReadOnlySpan<byte> src, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = string.Empty;

        if (src.IsEmpty)
        {
            error = "无效输入数据";
            return false;
        }

        if (TryDecompressStream(src, static s => new ZLibStream(s, CompressionMode.Decompress, leaveOpen: true), out data, out _))
        {
            return true;
        }

        if (TryDecompressStream(src, static s => new GZipStream(s, CompressionMode.Decompress, leaveOpen: true), out data, out _))
        {
            return true;
        }

        if (TryDecompressStream(src, static s => new DeflateStream(s, CompressionMode.Decompress, leaveOpen: true), out data, out _))
        {
            return true;
        }

        error = "解压失败: 输入不是有效的 zlib/gzip/deflate 数据，或数据已损坏";
        return false;
    }

    public static bool TryCompress(ReadOnlySpan<byte> src, out byte[] data, out string error, CompressionLevel level = CompressionLevel.Optimal)
    {
        data = Array.Empty<byte>();
        error = string.Empty;

        try
        {
            using var output = new MemoryStream();
            using (var zs = new ZLibStream(output, level, leaveOpen: true))
            {
                zs.Write(src);
            }

            data = output.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"压缩失败（zlib）\n{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 压缩为“分块 zlib”格式：
    /// 每块结构为：<c>[4-byte little-endian compressed_size][zlib_compressed_data]</c>，并以 <c>compressed_size=0</c> 的哨兵块结束。
    /// </summary>
    public static bool TryCompressChunked(
        ReadOnlySpan<byte> src,
        out byte[] data,
        out string error,
        int chunkUncompressedBytes = 64 * 1024,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        data = Array.Empty<byte>();
        error = string.Empty;

        if (chunkUncompressedBytes <= 0)
        {
            error = "Chunked zlib: chunkUncompressedBytes 必须 > 0";
            return false;
        }

        try
        {
            using var output = new MemoryStream();
            Span<byte> u32 = stackalloc byte[4];
            Span<byte> zero = stackalloc byte[4];
            zero.Clear();

            int pos = 0;
            while (pos < src.Length)
            {
                int take = Math.Min(chunkUncompressedBytes, src.Length - pos);
                ReadOnlySpan<byte> chunkSrc = src.Slice(pos, take);

                if (!TryCompress(chunkSrc, out byte[] chunkComp, out string chunkError, level))
                {
                    error = chunkError;
                    return false;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)chunkComp.Length);
                output.Write(u32);
                output.Write(chunkComp, 0, chunkComp.Length);

                pos += take;
            }

            // Sentinel
            output.Write(zero);

            data = output.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            error = $"压缩失败（chunked-zlib）\n{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 解压“分块 zlib”格式（新 WPF 格式使用）：
    /// 每块结构为：<c>[4-byte little-endian compressed_size][zlib_compressed_data]</c>，重复直到遇到 size=0 的哨兵块。
    /// </summary>
    public static bool TryDecompressChunked(ReadOnlySpan<byte> src, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = string.Empty;

        if (src.IsEmpty)
        {
            error = "无效输入数据";
            return false;
        }

        try
        {
            using var output = new MemoryStream();

            int pos = 0;
            while (pos < src.Length)
            {
                if (pos + 4 > src.Length)
                {
                    error = $"Chunked zlib: 在偏移 {pos} 处截断（缺少 4 字节块头）";
                    return false;
                }

                uint chunkCompSize = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos, 4));
                pos += 4;

                if (chunkCompSize == 0)
                {
                    break;
                }

                if (chunkCompSize > int.MaxValue)
                {
                    error = $"Chunked zlib: 块大小过大: {chunkCompSize}";
                    return false;
                }

                int chunkSize = (int)chunkCompSize;
                if (pos + chunkSize > src.Length)
                {
                    error = $"Chunked zlib: 在偏移 {pos - 4} 处声明块大小 {chunkSize}，但剩余仅 {src.Length - pos} 字节";
                    return false;
                }

                if (!TryDecompress(src.Slice(pos, chunkSize), out byte[] chunkOut, out string chunkError))
                {
                    error = chunkError;
                    return false;
                }

                output.Write(chunkOut, 0, chunkOut.Length);
                pos += chunkSize;
            }

            data = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Chunked zlib: 解压失败\n{ex.Message}";
            return false;
        }
    }

    private static bool TryDecompressStream(
        ReadOnlySpan<byte> src,
        Func<Stream, Stream> createDecompressor,
        out byte[] data,
        out string error)
    {
        data = Array.Empty<byte>();
        error = string.Empty;

        using var input = new MemoryStream(src.ToArray(), writable: false);
        Stream? decompressor = null;
        using var output = new MemoryStream();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        bool producedOutput = false;

        try
        {
            decompressor = createDecompressor(input);

            while (true)
            {
                int read = decompressor.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                producedOutput = true;
                output.Write(buffer, 0, read);
            }

            data = output.ToArray();
            return true;
        }
        catch (InvalidDataException ex)
        {
            // 一些旧资源可能截断/缺少尾部校验（zlib Adler32 / gzip CRC+ISIZE）。
            // 与旧 C++ 版本的容错逻辑一致：若已产生输出且输入已基本消耗完，则当作成功。
            if (producedOutput && input.Position >= Math.Max(0, input.Length - 8))
            {
                data = output.ToArray();
                return true;
            }

            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                decompressor?.Dispose();
            }
            catch
            {
                // ignored
            }

            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
