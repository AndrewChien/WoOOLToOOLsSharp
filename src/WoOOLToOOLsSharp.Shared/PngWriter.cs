using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace WoOOLToOOLsSharp.Shared;

public static class PngWriter
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public delegate bool Rgba8RowProvider(int y, Span<byte> rowRgba8, out string error);

    public static bool TryWriteRgba8(string filePath, int width, int height, ReadOnlySpan<byte> rgba8, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "输出路径为空";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "宽高必须大于 0";
            return false;
        }

        int expected = width * height * 4;
        if (rgba8.Length != expected)
        {
            error = $"RGBA8 数据长度不匹配: expected={expected}, actual={rgba8.Length}";
            return false;
        }

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            fs.Write(PngSignature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // color type RGBA
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace

            WriteChunk(fs, "IHDR", ihdr);

            using (var idat = new IdatChunkStream(fs))
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                Span<byte> filter = stackalloc byte[1];
                filter[0] = 0;

                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    z.Write(filter); // filter = None
                    ReadOnlySpan<byte> row = rgba8.Slice(y * stride, stride);
                    z.Write(row);
                }
            }

            WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryWriteRgba8Rows(string filePath, int width, int height, Rgba8RowProvider rowProvider, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "输出路径为空";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            error = "宽高必须大于 0";
            return false;
        }

        if (rowProvider is null)
        {
            error = "RowProvider 为空";
            return false;
        }

        int stride = width * 4;
        var row = new byte[stride];

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            fs.Write(PngSignature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // color type RGBA
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace

            WriteChunk(fs, "IHDR", ihdr);

            using (var idat = new IdatChunkStream(fs))
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                Span<byte> filter = stackalloc byte[1];
                filter[0] = 0;

                for (int y = 0; y < height; y++)
                {
                    if (!rowProvider(y, row, out error))
                    {
                        if (string.IsNullOrWhiteSpace(error))
                        {
                            error = "RowProvider 失败";
                        }

                        return false;
                    }

                    z.Write(filter); // filter = None
                    z.Write(row);
                }
            }

            WriteChunk(fs, "IEND", ReadOnlySpan<byte>.Empty);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed class IdatChunkStream : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _buffer;
        private int _pos;

        public IdatChunkStream(Stream output, int chunkSizeBytes = 64 * 1024)
        {
            if (chunkSizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes));
            }

            _output = output ?? throw new ArgumentNullException(nameof(output));
            _buffer = new byte[chunkSizeBytes];
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            FlushChunk();
            _output.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException();
            }

            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int remaining = _buffer.Length - _pos;
                if (remaining == 0)
                {
                    FlushChunk();
                    remaining = _buffer.Length;
                }

                int n = Math.Min(remaining, buffer.Length);
                buffer[..n].CopyTo(_buffer.AsSpan(_pos, n));
                _pos += n;
                buffer = buffer[n..];

                if (_pos == _buffer.Length)
                {
                    FlushChunk();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushChunk();
            }

            base.Dispose(disposing);
        }

        private void FlushChunk()
        {
            if (_pos <= 0)
            {
                return;
            }

            WriteChunk(_output, "IDAT", _buffer.AsSpan(0, _pos));
            _pos = 0;
        }
    }

    private static byte[] BuildIdatData(int width, int height, ReadOnlySpan<byte> rgba8)
    {
        using var ms = new MemoryStream(capacity: Math.Min(1024 * 1024, rgba8.Length / 2));
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter = None
                ReadOnlySpan<byte> row = rgba8.Slice(y * stride, stride);
                z.Write(row);
            }
        }

        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        if (type is null || type.Length != 4)
        {
            throw new ArgumentException("PNG chunk type 必须为 4 个字符", nameof(type));
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        output.Write(lengthBytes);

        Span<byte> typeBytes = stackalloc byte[4] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        output.Write(typeBytes);

        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            }
            table[i] = c;
        }
        return table;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = crc;
        for (int i = 0; i < data.Length; i++)
        {
            c = CrcTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }
        return c;
    }
}
