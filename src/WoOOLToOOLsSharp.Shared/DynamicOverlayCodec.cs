using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WoOOLToOOLsSharp.Shared;

public static class DynamicOverlayCodec
{
    public const long DefaultMaxDecompressedBytes = 16L * 1024 * 1024;
    private const uint BinaryFixtureMagic = 0x31564F57; // "WOV1"
    private const uint BinaryFixtureVersion = 1;
    private const int BinaryFixtureHeaderSize = 12;
    private const int BinaryFixtureRecordSize = 40;

    public static bool TryWriteBinaryFixtureV1(IReadOnlyList<DynamicOverlayRecord> records, out byte[] bytes, out string error)
    {
        bytes = [];
        error = string.Empty;

        if (records is null)
        {
            error = "records 为空。";
            return false;
        }

        int count = records.Count;
        if (count < 0)
        {
            error = "记录数非法。";
            return false;
        }

        long totalBytes = BinaryFixtureHeaderSize + (long)count * BinaryFixtureRecordSize;
        if (totalBytes > int.MaxValue)
        {
            error = $"输出过大：{totalBytes} bytes。";
            return false;
        }

        byte[] buffer = new byte[(int)totalBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), BinaryFixtureMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), BinaryFixtureVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), (uint)count);

        for (int i = 0; i < count; i++)
        {
            DynamicOverlayRecord record = records[i];
            int offset = BinaryFixtureHeaderSize + i * BinaryFixtureRecordSize;
            Span<byte> span = buffer.AsSpan(offset, BinaryFixtureRecordSize);

            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), record.X);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), record.Y);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), record.PackageId);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12, 4), record.ImageId);

            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(16, 2), ClampToInt16(record.OffsetX));
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(18, 2), ClampToInt16(record.OffsetY));

            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), ClampToUInt16(record.Frame));
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), ClampToUInt16(record.Order));

            float scale = record.Scale;
            if (float.IsNaN(scale) || float.IsInfinity(scale))
            {
                scale = 1.0f;
            }
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), BitConverter.SingleToInt32Bits(scale));

            span[28] = record.Alpha;
            span[29] = EncodeKindByte(record.Kind);
            span[30] = EncodeLayerByte(record.Layer);
            span[31] = EncodeCoordinateSpaceByte(record.CoordinateSpace);
            span[32] = EncodeBlendModeByte(record.BlendMode);
            span[33] = record.TintR;
            span[34] = record.TintG;
            span[35] = record.TintB;
            span[36] = record.TintA;
        }

        bytes = buffer;
        return true;
    }

    public static bool TryReadFromFile(string path, out DynamicOverlayDocument document, out string error, long maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        document = new();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空。";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (TryReadFromMemory(bytes, path, out document, out error, maxDecompressedBytes))
            {
                return true;
            }

            string primaryError = error;
            foreach (string layoutPath in EnumerateLayoutSidecars(path))
            {
                if (!DynamicOverlayBinaryLayout.TryLoadFromFile(layoutPath, out DynamicOverlayBinaryLayout layout, out string layoutError))
                {
                    error = $"布局文件加载失败：{layoutPath} ({layoutError})";
                    return false;
                }

                if (TryReadFromMemory(bytes, path, layout, out document, out error, maxDecompressedBytes))
                {
                    return true;
                }

                primaryError = $"{primaryError}；布局 {Path.GetFileName(layoutPath)} 解析失败：{error}";
            }

            error = primaryError;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryReadFromFile(string path, DynamicOverlayBinaryLayout layout, out DynamicOverlayDocument document, out string error, long maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        document = new();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空。";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        try
        {
            return TryReadFromMemory(File.ReadAllBytes(path), path, layout, out document, out error, maxDecompressedBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryReadFromMemory(ReadOnlySpan<byte> bytes, string sourcePath, out DynamicOverlayDocument document, out string error, long maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        document = new();
        error = string.Empty;
        if (bytes.IsEmpty)
        {
            error = "输入为空。";
            return false;
        }

        byte[] payload = bytes.ToArray();
        bool wasCompressed = false;
        string? compression = DynamicOverlayInspector.GuessCompression(payload, payload.LongLength);
        if (!string.IsNullOrWhiteSpace(compression))
        {
            if (!TryDecompress(payload, compression, maxDecompressedBytes, out payload, out error))
            {
                error = $"动态覆盖解析失败：{error}";
                return false;
            }

            wasCompressed = true;
        }

        if (TryReadBinaryFixture(payload, sourcePath, wasCompressed, out bool handledBinary, out document, out error))
        {
            return true;
        }

        if (handledBinary)
        {
            return false;
        }

        string encodingHint = DynamicOverlayInspector.GuessEncodingHint(payload.AsSpan(0, Math.Min(payload.Length, 64)).ToArray()) ?? "unknown";
        string text = DecodeText(payload, encodingHint);
        string trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.Length == 0)
        {
            error = "文本内容为空。";
            return false;
        }

            return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)
            ? TryReadJson(trimmed, sourcePath, encodingHint, wasCompressed, out document, out error)
            : TryReadDelimited(text, sourcePath, encodingHint, wasCompressed, out document, out error);
    }

    public static bool TryReadFromMemory(ReadOnlySpan<byte> bytes, string sourcePath, DynamicOverlayBinaryLayout layout, out DynamicOverlayDocument document, out string error, long maxDecompressedBytes = DefaultMaxDecompressedBytes)
    {
        document = new();
        error = string.Empty;

        if (bytes.IsEmpty)
        {
            error = "输入为空。";
            return false;
        }

        byte[] payload = bytes.ToArray();
        bool wasCompressed = false;
        string? compression = DynamicOverlayInspector.GuessCompression(payload, payload.LongLength);
        if (!string.IsNullOrWhiteSpace(compression))
        {
            if (!TryDecompress(payload, compression, maxDecompressedBytes, out payload, out error))
            {
                error = $"动态覆盖解析失败：{error}";
                return false;
            }

            wasCompressed = true;
        }

        return TryReadBinaryLayout(payload, sourcePath, layout, wasCompressed, out document, out error);
    }

    private static IEnumerable<string> EnumerateLayoutSidecars(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        string candidateA = path + ".layout.json";
        if (File.Exists(candidateA))
        {
            yield return candidateA;
        }

        string extension = Path.GetExtension(path);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            string candidateB = path[..^extension.Length] + ".layout.json";
            if (!string.Equals(candidateB, candidateA, StringComparison.OrdinalIgnoreCase) && File.Exists(candidateB))
            {
                yield return candidateB;
            }
        }
    }

    private static bool TryReadJson(string text, string sourcePath, string encodingHint, bool wasCompressed, out DynamicOverlayDocument document, out string error)
    {
        document = new();
        error = string.Empty;

        try
        {
            using JsonDocument json = JsonDocument.Parse(text);
            string format;
            JsonElement root;
            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                format = "json-array";
                root = json.RootElement;
            }
            else if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProperty(json.RootElement, "records", out JsonElement records) && records.ValueKind == JsonValueKind.Array)
                {
                    format = "json-object";
                    root = records;
                }
                else
                {
                    format = "json-single";
                    root = json.RootElement;
                }
            }
            else
            {
                error = $"不支持的 JSON 根节点类型：{json.RootElement.ValueKind}";
                return false;
            }

            List<DynamicOverlayRecord> list = [];
            if (root.ValueKind == JsonValueKind.Array)
            {
                int index = 1;
                foreach (JsonElement item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        error = $"第 {index} 条记录不是对象。";
                        return false;
                    }

                    if (!TryParseJsonRecord(item, index, out DynamicOverlayRecord record, out error))
                    {
                        return false;
                    }

                    list.Add(record);
                    index++;
                }
            }
            else
            {
                if (!TryParseJsonRecord(root, 1, out DynamicOverlayRecord record, out error))
                {
                    return false;
                }

                list.Add(record);
            }

            document = new DynamicOverlayDocument
            {
                SourcePath = sourcePath,
                Format = format,
                EncodingHint = encodingHint,
                WasCompressed = wasCompressed,
                Records = list,
            };
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadDelimited(string text, string sourcePath, string encodingHint, bool wasCompressed, out DynamicOverlayDocument document, out string error)
    {
        document = new();
        error = string.Empty;

        string[] rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        List<(int LineNumber, string Line)> lines = [];
        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i].Trim();
            if (!IsIgnoredLine(line))
            {
                lines.Add((i + 1, line));
            }
        }

        if (lines.Count < 2)
        {
            error = "文本格式至少需要一行表头和一行数据。";
            return false;
        }

        DelimitedFormat format = DetectDelimitedFormat(lines[0].Line);
        string[] headers = SplitLine(lines[0].Line, format);
        if (headers.Length == 0)
        {
            error = $"第 {lines[0].LineNumber} 行表头为空。";
            return false;
        }

        List<DynamicOverlayRecord> records = [];
        for (int i = 1; i < lines.Count; i++)
        {
            (int lineNumber, string line) = lines[i];
            string[] values = SplitLine(line, format);
            if (values.Length > headers.Length)
            {
                error = $"第 {lineNumber} 行字段数 {values.Length} 超过表头字段数 {headers.Length}。";
                return false;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int col = 0; col < headers.Length; col++)
            {
                string key = NormalizeFieldName(headers[col]);
                if (key.Length > 0)
                {
                    fields[key] = col < values.Length ? values[col].Trim() : string.Empty;
                }
            }

            if (!TryParseTextRecord(fields, lineNumber, out DynamicOverlayRecord record, out error))
            {
                return false;
            }

            records.Add(record);
        }

        document = new DynamicOverlayDocument
        {
            SourcePath = sourcePath,
            Format = format.Label,
            EncodingHint = encodingHint,
            WasCompressed = wasCompressed,
            Records = records,
        };
        return true;
    }

    private static bool TryParseJsonRecord(JsonElement element, int index, out DynamicOverlayRecord record, out string error)
    {
        record = new();
        error = string.Empty;
        if (!TryGetRequiredInt(element, index, out int x, out error, "x")
            || !TryGetRequiredInt(element, index, out int y, out error, "y")
            || !TryGetRequiredInt(element, index, out int packageId, out error, "package", "packageid", "pkg", "lib", "library")
            || !TryGetRequiredInt(element, index, out int imageId, out error, "image", "imageid", "img"))
        {
            return false;
        }

        ReadTint(element, out byte tintR, out byte tintG, out byte tintB, out byte tintA);
        record = new DynamicOverlayRecord
        {
            Kind = NormalizeKind(GetString(element, "kind", "type") ?? "scene"),
            Layer = NormalizeLayer(GetString(element, "layer", "pass") ?? "front"),
            CoordinateSpace = NormalizeCoordinateSpace(GetString(element, "coordinatespace", "coordspace", "coord", "space") ?? "pixel"),
            X = x,
            Y = y,
            PackageId = packageId,
            ImageId = imageId,
            Frame = GetOptionalInt(element, 0, "frame", "animframe"),
            OffsetX = GetOptionalInt(element, 0, "offsetx", "ox"),
            OffsetY = GetOptionalInt(element, 0, "offsety", "oy"),
            Alpha = GetOptionalAlpha(element, 255, "alpha", "opacity"),
            Scale = GetOptionalFloat(element, 1.0f, "scale", "zoom"),
            TintR = tintR,
            TintG = tintG,
            TintB = tintB,
            TintA = tintA,
            BlendMode = NormalizeBlendMode(GetString(element, "blendmode", "blend") ?? "alpha"),
            Order = GetOptionalInt(element, 0, "order", "z", "zindex"),
            Label = GetString(element, "label", "name") ?? string.Empty,
        };
        return true;
    }

    private static bool TryParseTextRecord(IReadOnlyDictionary<string, string> fields, int lineNumber, out DynamicOverlayRecord record, out string error)
    {
        record = new();
        error = string.Empty;
        if (!TryGetRequiredInt(fields, lineNumber, out int x, out error, "x", "posx", "px")
            || !TryGetRequiredInt(fields, lineNumber, out int y, out error, "y", "posy", "py")
            || !TryGetRequiredInt(fields, lineNumber, out int packageId, out error, "package", "packageid", "pkg", "lib", "library")
            || !TryGetRequiredInt(fields, lineNumber, out int imageId, out error, "image", "imageid", "img"))
        {
            return false;
        }

        ReadTint(fields, out byte tintR, out byte tintG, out byte tintB, out byte tintA);
        record = new DynamicOverlayRecord
        {
            Kind = NormalizeKind(GetString(fields, "kind", "type") ?? "scene"),
            Layer = NormalizeLayer(GetString(fields, "layer", "pass") ?? "front"),
            CoordinateSpace = NormalizeCoordinateSpace(GetString(fields, "coordinatespace", "coordspace", "coord", "space") ?? "pixel"),
            X = x,
            Y = y,
            PackageId = packageId,
            ImageId = imageId,
            Frame = GetOptionalInt(fields, 0, "frame", "animframe"),
            OffsetX = GetOptionalInt(fields, 0, "offsetx", "ox"),
            OffsetY = GetOptionalInt(fields, 0, "offsety", "oy"),
            Alpha = GetOptionalAlpha(fields, 255, "alpha", "opacity"),
            Scale = GetOptionalFloat(fields, 1.0f, "scale", "zoom"),
            TintR = tintR,
            TintG = tintG,
            TintB = tintB,
            TintA = tintA,
            BlendMode = NormalizeBlendMode(GetString(fields, "blendmode", "blend") ?? "alpha"),
            Order = GetOptionalInt(fields, 0, "order", "z", "zindex"),
            Label = GetString(fields, "label", "name") ?? string.Empty,
        };
        return true;
    }

    private static bool TryDecompress(byte[] bytes, string compression, long maxDecompressedBytes, out byte[] payload, out string error)
    {
        payload = [];
        error = string.Empty;

        if (compression == "chunked-zlib")
        {
            return TryDecompressChunkedZlib(bytes, maxDecompressedBytes, out payload, out error);
        }

        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using Stream stream = compression switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "zlib" => new ZLibStream(input, CompressionMode.Decompress),
                _ => throw new NotSupportedException($"不支持的压缩类型：{compression}"),
            };
            using var output = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                long total = 0;
                long limit = maxDecompressedBytes > 0 ? maxDecompressedBytes : long.MaxValue;
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > limit)
                    {
                        error = $"解压后超过限制：{maxDecompressedBytes} bytes。";
                        return false;
                    }

                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            payload = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDecompressChunkedZlib(byte[] bytes, long maxDecompressedBytes, out byte[] payload, out string error)
    {
        payload = [];
        error = string.Empty;

        if (bytes is null || bytes.Length == 0)
        {
            error = "输入为空。";
            return false;
        }

        long limit = maxDecompressedBytes > 0 ? maxDecompressedBytes : long.MaxValue;

        try
        {
            using var output = new MemoryStream();
            int pos = 0;
            long total = 0;

            while (pos < bytes.Length)
            {
                if (pos + 4 > bytes.Length)
                {
                    error = $"Chunked zlib: 在偏移 {pos} 处截断（缺少 4 字节块头）";
                    return false;
                }

                uint chunkCompSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
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
                if (pos + chunkSize > bytes.Length)
                {
                    error = $"Chunked zlib: 在偏移 {pos - 4} 处声明块大小 {chunkSize}，但剩余仅 {bytes.Length - pos} 字节";
                    return false;
                }

                if (!ZlibUtils.TryDecompress(bytes.AsSpan(pos, chunkSize), out byte[] chunkOut, out string chunkError))
                {
                    error = chunkError;
                    return false;
                }

                pos += chunkSize;

                total += chunkOut.LongLength;
                if (total > limit)
                {
                    error = $"解压后超过限制：{maxDecompressedBytes} bytes。";
                    return false;
                }

                output.Write(chunkOut, 0, chunkOut.Length);
            }

            payload = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string DecodeText(byte[] payload, string encodingHint)
    {
        try
        {
            return encodingHint switch
            {
                "utf16-le-bom" => Encoding.Unicode.GetString(payload, 2, payload.Length - 2),
                "utf16-be-bom" => Encoding.BigEndianUnicode.GetString(payload, 2, payload.Length - 2),
                "utf16-le?" => Encoding.Unicode.GetString(payload),
                "utf16-be?" => Encoding.BigEndianUnicode.GetString(payload),
                "utf8-bom" => Encoding.UTF8.GetString(payload, 3, payload.Length - 3),
                _ => new UTF8Encoding(false, false).GetString(payload),
            };
        }
        catch
        {
            return Encoding.UTF8.GetString(payload);
        }
    }

    private static bool TryReadBinaryFixture(byte[] payload, string sourcePath, bool wasCompressed, out bool handled, out DynamicOverlayDocument document, out string error)
    {
        handled = false;
        document = new();
        error = string.Empty;

        if (payload.Length < BinaryFixtureHeaderSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) != BinaryFixtureMagic)
        {
            return false;
        }

        handled = true;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
        if (version != BinaryFixtureVersion)
        {
            error = $"不支持的 overlay 二进制 fixture 版本：{version}";
            return false;
        }

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
        if (count < 0)
        {
            error = "overlay 二进制 fixture 记录数非法。";
            return false;
        }

        long requiredBytes = BinaryFixtureHeaderSize + (long)count * BinaryFixtureRecordSize;
        if (requiredBytes > payload.Length)
        {
            error = $"overlay 二进制 fixture 长度不足：需要 {requiredBytes} bytes，实际 {payload.Length} bytes。";
            return false;
        }

        List<DynamicOverlayRecord> records = new(capacity: count);
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryFixtureHeaderSize + i * BinaryFixtureRecordSize;
            ReadOnlySpan<byte> span = payload.AsSpan(offset, BinaryFixtureRecordSize);

            records.Add(new DynamicOverlayRecord
            {
                Kind = span[29] == 1 ? "effect" : "scene",
                Layer = span[30] switch
                {
                    0 => "back",
                    1 => "middle",
                    2 => "floor",
                    3 => "underfront",
                    5 => "overfront",
                    _ => "front",
                },
                CoordinateSpace = span[31] == 1 ? "cell" : "pixel",
                X = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0, 4)),
                Y = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4)),
                PackageId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4)),
                ImageId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12, 4)),
                OffsetX = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(16, 2)),
                OffsetY = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(18, 2)),
                Frame = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(20, 2)),
                Order = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(22, 2)),
                Scale = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span.Slice(24, 4))),
                Alpha = span[28],
                BlendMode = span[32] == 1 ? "additive" : "alpha",
                TintR = span[33],
                TintG = span[34],
                TintB = span[35],
                TintA = span[36],
            });
        }

        document = new DynamicOverlayDocument
        {
            SourcePath = sourcePath,
            Format = "binary-fixture-v1",
            EncodingHint = "binary",
            WasCompressed = wasCompressed,
            Records = records,
        };
        return true;
    }

    private static bool TryReadBinaryLayout(byte[] payload, string sourcePath, DynamicOverlayBinaryLayout layout, bool wasCompressed, out DynamicOverlayDocument document, out string error)
    {
        document = new();
        error = string.Empty;

        if (layout is null)
        {
            error = "二进制布局为空。";
            return false;
        }

        if (layout.RecordSize <= 0)
        {
            error = "二进制布局 recordSize 必须大于 0。";
            return false;
        }

        int offset = Math.Clamp(layout.Offset, 0, payload.Length);
        int availableBytes = payload.Length - offset;
        int recordCount = layout.Count > 0 ? layout.Count : availableBytes / layout.RecordSize;
        if (recordCount <= 0)
        {
            error = "根据当前布局未找到任何完整记录。";
            return false;
        }

        long requiredBytes = (long)offset + (long)recordCount * layout.RecordSize;
        if (requiredBytes > payload.Length)
        {
            error = $"布局所需长度不足：需要 {requiredBytes} bytes，实际 {payload.Length} bytes。";
            return false;
        }

        List<DynamicOverlayRecord> records = new(recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            ReadOnlySpan<byte> span = payload.AsSpan(offset + i * layout.RecordSize, layout.RecordSize);
            if (!TryReadRecordFromLayout(span, layout, i + 1, out DynamicOverlayRecord record, out error))
            {
                return false;
            }

            records.Add(record);
        }

        document = new DynamicOverlayDocument
        {
            SourcePath = sourcePath,
            Format = "binary-layout",
            EncodingHint = "binary",
            WasCompressed = wasCompressed,
            Records = records,
        };
        return true;
    }

    private static bool TryReadRecordFromLayout(ReadOnlySpan<byte> span, DynamicOverlayBinaryLayout layout, int index, out DynamicOverlayRecord record, out string error)
    {
        record = new();
        error = string.Empty;

        if (!TryReadRequiredInt(span, layout.X, "x", index, out int x, out error)
            || !TryReadRequiredInt(span, layout.Y, "y", index, out int y, out error)
            || !TryReadRequiredInt(span, layout.PackageId, "packageId", index, out int packageId, out error)
            || !TryReadRequiredInt(span, layout.ImageId, "imageId", index, out int imageId, out error))
        {
            return false;
        }

        string kind = ReadMappedString(span, layout.KindField, layout.Kind);
        string layer = ReadMappedString(span, layout.LayerField, layout.Layer);
        string coordinateSpace = ReadMappedString(span, layout.CoordinateSpaceField, layout.CoordinateSpace);
        string blendMode = ReadMappedString(span, layout.BlendModeField, layout.BlendMode);

        record = new DynamicOverlayRecord
        {
            Kind = NormalizeKind(kind),
            Layer = NormalizeLayer(layer),
            CoordinateSpace = NormalizeCoordinateSpace(coordinateSpace),
            X = x,
            Y = y,
            PackageId = packageId,
            ImageId = imageId,
            OffsetX = ReadOptionalInt(span, layout.OffsetX, 0),
            OffsetY = ReadOptionalInt(span, layout.OffsetY, 0),
            Frame = ReadOptionalInt(span, layout.Frame, 0),
            Order = ReadOptionalInt(span, layout.Order, 0),
            Scale = ReadOptionalFloat(span, layout.Scale, 1.0f),
            Alpha = ReadOptionalAlpha(span, layout.Alpha, 255),
            TintR = ReadOptionalByte(span, layout.TintR, 255),
            TintG = ReadOptionalByte(span, layout.TintG, 255),
            TintB = ReadOptionalByte(span, layout.TintB, 255),
            TintA = ReadOptionalByte(span, layout.TintA, 255),
            BlendMode = NormalizeBlendMode(blendMode),
        };

        return true;
    }

    private static bool TryReadRequiredInt(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec? spec, string fieldName, int index, out int value, out string error)
    {
        value = 0;
        error = string.Empty;
        if (spec is null)
        {
            error = $"第 {index} 条记录缺少布局字段 {fieldName}。";
            return false;
        }

        if (!TryReadInt(span, spec, out value))
        {
            error = $"第 {index} 条记录字段 {fieldName} 读取失败。";
            return false;
        }

        return true;
    }

    private static int ReadOptionalInt(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec? spec, int defaultValue) =>
        spec is not null && TryReadInt(span, spec, out int value) ? value : defaultValue;

    private static float ReadOptionalFloat(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec? spec, float defaultValue) =>
        spec is not null && TryReadFloat(span, spec, out float value) ? value : defaultValue;

    private static byte ReadOptionalByte(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec? spec, byte defaultValue)
    {
        if (spec is null || !TryReadInteger(span, spec, out long value))
        {
            return defaultValue;
        }

        return (byte)Math.Clamp(value, 0, 255);
    }

    private static byte ReadOptionalAlpha(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec? spec, byte defaultValue)
    {
        if (spec is null)
        {
            return defaultValue;
        }

        if (string.Equals(spec.Type, "f32", StringComparison.OrdinalIgnoreCase) && TryReadFloat(span, spec, out float normalized))
        {
            if (normalized >= 0 && normalized <= 1)
            {
                return (byte)Math.Clamp((int)MathF.Round(normalized * 255f), 0, 255);
            }

            return (byte)Math.Clamp((int)MathF.Round(normalized), 0, 255);
        }

        return ReadOptionalByte(span, spec, defaultValue);
    }

    private static string ReadMappedString(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec? spec, string defaultValue)
    {
        if (spec is null || spec.Mapping is null || !TryReadInteger(span, spec, out long key))
        {
            return defaultValue;
        }

        string textKey = key.ToString(CultureInfo.InvariantCulture);
        return spec.Mapping.TryGetValue(textKey, out string? mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : defaultValue;
    }

    private static bool TryReadInt(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec spec, out int value)
    {
        value = 0;
        if (string.Equals(spec.Type, "f32", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadFloat(span, spec, out float f))
            {
                return false;
            }

            value = (int)MathF.Round(f);
            return true;
        }

        if (!TryReadInteger(span, spec, out long raw))
        {
            return false;
        }

        if (raw < int.MinValue || raw > int.MaxValue)
        {
            return false;
        }

        value = (int)raw;
        return true;
    }

    private static bool TryReadFloat(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec spec, out float value)
    {
        value = 0;
        if (!TrySlice(span, spec, out ReadOnlySpan<byte> data))
        {
            return false;
        }

        switch (NormalizeBinaryType(spec.Type))
        {
            case "f32":
                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));
                return !float.IsNaN(value) && !float.IsInfinity(value);
            case "u8":
                value = data[0];
                return true;
            case "i16":
                value = BinaryPrimitives.ReadInt16LittleEndian(data);
                return true;
            case "u16":
                value = BinaryPrimitives.ReadUInt16LittleEndian(data);
                return true;
            case "i32":
                value = BinaryPrimitives.ReadInt32LittleEndian(data);
                return true;
            case "u32":
                value = BinaryPrimitives.ReadUInt32LittleEndian(data);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadInteger(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec spec, out long value)
    {
        value = 0;
        if (!TrySlice(span, spec, out ReadOnlySpan<byte> data))
        {
            return false;
        }

        switch (NormalizeBinaryType(spec.Type))
        {
            case "u8":
                value = data[0];
                return true;
            case "i16":
                value = BinaryPrimitives.ReadInt16LittleEndian(data);
                return true;
            case "u16":
                value = BinaryPrimitives.ReadUInt16LittleEndian(data);
                return true;
            case "i32":
                value = BinaryPrimitives.ReadInt32LittleEndian(data);
                return true;
            case "u32":
                value = BinaryPrimitives.ReadUInt32LittleEndian(data);
                return true;
            default:
                return false;
        }
    }

    private static bool TrySlice(ReadOnlySpan<byte> span, DynamicOverlayBinaryFieldSpec spec, out ReadOnlySpan<byte> data)
    {
        data = default;
        int size = BinaryTypeSize(spec.Type);
        if (size <= 0 || spec.Offset < 0 || spec.Offset + size > span.Length)
        {
            return false;
        }

        data = span.Slice(spec.Offset, size);
        return true;
    }

    private static string NormalizeBinaryType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant();

    private static int BinaryTypeSize(string? type) => NormalizeBinaryType(type) switch
    {
        "u8" => 1,
        "i16" or "u16" => 2,
        "i32" or "u32" or "f32" => 4,
        _ => 0,
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        string normalized = NormalizeFieldName(name);
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (NormalizeFieldName(prop.Name) == normalized)
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        return null;
    }

    private static string? GetString(IReadOnlyDictionary<string, string> fields, params string[] names)
    {
        foreach (string name in names)
        {
            if (fields.TryGetValue(NormalizeFieldName(name), out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetRequiredInt(JsonElement element, int index, out int value, out string error, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement prop) && TryParseInt(prop, out value))
            {
                error = string.Empty;
                return true;
            }
        }

        value = 0;
        error = $"第 {index} 条记录缺少或无法解析字段 {names[0]}。";
        return false;
    }

    private static bool TryGetRequiredInt(IReadOnlyDictionary<string, string> fields, int lineNumber, out int value, out string error, params string[] names)
    {
        foreach (string name in names)
        {
            if (!fields.TryGetValue(NormalizeFieldName(name), out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = string.Empty;
                return true;
            }
        }

        value = 0;
        error = $"第 {lineNumber} 行缺少或无法解析字段 {names[0]}。";
        return false;
    }

    private static int GetOptionalInt(JsonElement element, int defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement prop) && TryParseInt(prop, out int value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static int GetOptionalInt(IReadOnlyDictionary<string, string> fields, int defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (fields.TryGetValue(NormalizeFieldName(name), out string? raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static float GetOptionalFloat(JsonElement element, float defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement prop) && TryParseFloat(prop, out float value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static float GetOptionalFloat(IReadOnlyDictionary<string, string> fields, float defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (fields.TryGetValue(NormalizeFieldName(name), out string? raw)
                && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static byte GetOptionalAlpha(JsonElement element, byte defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement prop) && TryParseAlpha(prop, out byte value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static byte GetOptionalAlpha(IReadOnlyDictionary<string, string> fields, byte defaultValue, params string[] names)
    {
        foreach (string name in names)
        {
            if (fields.TryGetValue(NormalizeFieldName(name), out string? raw) && TryParseAlpha(raw, out byte value))
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static void ReadTint(JsonElement element, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 255;
        string? tint = GetString(element, "tint", "color", "colour");
        if (!string.IsNullOrWhiteSpace(tint) && TryParseTint(tint, out r, out g, out b, out a))
        {
            return;
        }

        r = (byte)Math.Clamp(GetOptionalInt(element, 255, "tintr"), 0, 255);
        g = (byte)Math.Clamp(GetOptionalInt(element, 255, "tintg"), 0, 255);
        b = (byte)Math.Clamp(GetOptionalInt(element, 255, "tintb"), 0, 255);
        a = (byte)Math.Clamp(GetOptionalInt(element, 255, "tinta"), 0, 255);
    }

    private static void ReadTint(IReadOnlyDictionary<string, string> fields, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 255;
        string? tint = GetString(fields, "tint", "color", "colour");
        if (!string.IsNullOrWhiteSpace(tint) && TryParseTint(tint, out r, out g, out b, out a))
        {
            return;
        }

        r = (byte)Math.Clamp(GetOptionalInt(fields, 255, "tintr"), 0, 255);
        g = (byte)Math.Clamp(GetOptionalInt(fields, 255, "tintg"), 0, 255);
        b = (byte)Math.Clamp(GetOptionalInt(fields, 255, "tintb"), 0, 255);
        a = (byte)Math.Clamp(GetOptionalInt(fields, 255, "tinta"), 0, 255);
    }

    private static bool TryParseInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryParseFloat(JsonElement element, out float value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetSingle(out value)
                   || element.TryGetDouble(out double d) && TryConvertDouble(d, out value);
        }

        return element.ValueKind == JsonValueKind.String
               && float.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryConvertDouble(double input, out float value)
    {
        value = (float)input;
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool TryParseAlpha(JsonElement element, out byte value)
    {
        value = 255;
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out int i) => TryParseAlpha(i.ToString(CultureInfo.InvariantCulture), out value),
            JsonValueKind.Number when element.TryGetDouble(out double d) => TryParseAlpha(d.ToString(CultureInfo.InvariantCulture), out value),
            JsonValueKind.String => TryParseAlpha(element.GetString(), out value),
            _ => false,
        };
    }

    private static bool TryParseAlpha(string? value, out byte alpha)
    {
        alpha = 255;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out alpha))
        {
            return true;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            return false;
        }

        if (parsed >= 0 && parsed <= 1)
        {
            alpha = (byte)Math.Clamp((int)MathF.Round(parsed * 255f), 0, 255);
            return true;
        }

        if (parsed >= 0 && parsed <= 255)
        {
            alpha = (byte)Math.Clamp((int)MathF.Round(parsed), 0, 255);
            return true;
        }

        return false;
    }

    private static bool TryParseTint(string value, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 255;
        string text = value.Trim();
        if (text.StartsWith('#'))
        {
            return TryParseHexTint(text[1..], false, out r, out g, out b, out a);
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHexTint(text[2..], text.Length > 8, out r, out g, out b, out a);
        }

        string[] parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 3 or 4
            && byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
        {
            a = parts.Length == 4 && byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedA)
                ? parsedA
                : (byte)255;
            return true;
        }

        return false;
    }

    private static bool TryParseHexTint(string text, bool argb, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 255;
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
        {
            return false;
        }

        if (text.Length == 6)
        {
            r = (byte)((raw >> 16) & 0xFF);
            g = (byte)((raw >> 8) & 0xFF);
            b = (byte)(raw & 0xFF);
            return true;
        }

        if (text.Length != 8)
        {
            return false;
        }

        if (argb)
        {
            a = (byte)((raw >> 24) & 0xFF);
            r = (byte)((raw >> 16) & 0xFF);
            g = (byte)((raw >> 8) & 0xFF);
            b = (byte)(raw & 0xFF);
        }
        else
        {
            r = (byte)((raw >> 24) & 0xFF);
            g = (byte)((raw >> 16) & 0xFF);
            b = (byte)((raw >> 8) & 0xFF);
            a = (byte)(raw & 0xFF);
        }

        return true;
    }

    private static string[] SplitLine(string line, DelimitedFormat format) =>
        format.Kind == DelimitedKind.Whitespace
            ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : line.Split(format.Delimiter, StringSplitOptions.TrimEntries);

    private static DelimitedFormat DetectDelimitedFormat(string line)
    {
        if (line.IndexOf('\t') >= 0) return new(DelimitedKind.Tab, '\t', "tsv");
        if (line.IndexOf(',') >= 0) return new(DelimitedKind.Comma, ',', "csv");
        if (line.IndexOf('|') >= 0) return new(DelimitedKind.Pipe, '|', "pipe");
        if (line.IndexOf(';') >= 0) return new(DelimitedKind.Semicolon, ';', "semicolon");
        return new(DelimitedKind.Whitespace, ' ', "whitespace");
    }

    private static bool IsIgnoredLine(string line) =>
        string.IsNullOrWhiteSpace(line)
        || line.StartsWith('#')
        || line.StartsWith(';')
        || line.StartsWith("//", StringComparison.Ordinal);

    private static string NormalizeFieldName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string NormalizeKind(string value) =>
        value.Trim().ToLowerInvariant() is "effect" or "effects" ? "effect" : "scene";

    private static string NormalizeLayer(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "over" or "overfront" => "overfront",
            "under" or "underfront" => "underfront",
            "middle" or "mid" => "middle",
            "back" or "smtiles" => "back",
            "floor" or "nearground" => "floor",
            _ => normalized.Length == 0 ? "front" : normalized,
        };
    }

    private static string NormalizeCoordinateSpace(string value) =>
        value.Trim().ToLowerInvariant() is "cell" or "tile" or "grid" ? "cell" : "pixel";

    private static string NormalizeBlendMode(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "add" => "additive",
            "normal" => "alpha",
            _ => normalized.Length == 0 ? "alpha" : normalized,
        };
    }

    private readonly record struct DelimitedFormat(DelimitedKind Kind, char Delimiter, string Label);

    private enum DelimitedKind
    {
        Comma,
        Tab,
        Pipe,
        Semicolon,
        Whitespace,
    }

    private static short ClampToInt16(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    private static ushort ClampToUInt16(int value) => (ushort)Math.Clamp(value, 0, ushort.MaxValue);

    private static byte EncodeKindByte(string value) => NormalizeKind(value) == "effect" ? (byte)1 : (byte)0;

    private static byte EncodeLayerByte(string value) => NormalizeLayer(value) switch
    {
        "back" => 0,
        "middle" => 1,
        "floor" => 2,
        "underfront" => 3,
        "overfront" => 5,
        _ => 4,
    };

    private static byte EncodeCoordinateSpaceByte(string value) => NormalizeCoordinateSpace(value) == "cell" ? (byte)1 : (byte)0;

    private static byte EncodeBlendModeByte(string value) => NormalizeBlendMode(value) == "additive" ? (byte)1 : (byte)0;
}
