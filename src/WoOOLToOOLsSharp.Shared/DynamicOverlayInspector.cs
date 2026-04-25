using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WoOOLToOOLsSharp.Shared;

public sealed record DynamicOverlayInspectOptions
{
    public int ReadBytes { get; init; } = 64;
    public int SampleCount { get; init; } = 6;
    public int MaxAnalysisBytes { get; init; } = 1024 * 1024;
    public bool TryDecompress { get; init; } = true;
    public long MaxDecompressedBytes { get; init; } = 4L * 1024 * 1024;
}

public sealed record DynamicOverlayFileInspection
{
    public string Path { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public long? SizeBytes { get; init; }
    public int AnalysisBytesRead { get; init; }
    public bool AnalysisTruncated { get; init; }
    public string HeadHex { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public string? CompressionHint { get; init; }
    public string? EncodingHint { get; init; }
    public string? TextHint { get; init; }
    public DynamicOverlayTextInspection? TextShape { get; init; }
    public DynamicOverlayBinaryInspection? BinaryShape { get; init; }
    public DynamicOverlayDecompressionInspection? Decompression { get; init; }
    public string? Error { get; init; }
}

public sealed record DynamicOverlayTextInspection
{
    public int LineCount { get; init; }
    public int NonEmptyLineCount { get; init; }
    public string DelimiterHint { get; init; } = "none";
    public int MaxColumns { get; init; }
    public int NumericLineCount { get; init; }
    public IReadOnlyList<string> SampleLines { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<long>> SampleNumberRows { get; init; } = [];
}

public sealed record DynamicOverlayBinaryInspection
{
    public IReadOnlyList<int> CandidateRecordSizes { get; init; } = [];
    public IReadOnlyList<DynamicOverlayFixedRecordLayoutCandidate> CandidateRecordLayouts { get; init; } = [];
    public IReadOnlyList<string> SampleAsciiStrings { get; init; } = [];
    public IReadOnlyList<string> SampleUtf16LeStrings { get; init; } = [];
    public IReadOnlyList<ushort> SampleUInt16Le { get; init; } = [];
    public IReadOnlyList<uint> SampleUInt32Le { get; init; } = [];
}

public sealed record DynamicOverlayFixedRecordLayoutCandidate
{
    public int Offset { get; init; }
    public int RecordSize { get; init; }
    public int RecordCount { get; init; }
}

public sealed record DynamicOverlayDecompressionInspection
{
    public bool Success { get; init; }
    public string Kind { get; init; } = string.Empty;
    public long MaxDecompressedBytes { get; init; }
    public long DecompressedBytesRead { get; init; }
    public bool Truncated { get; init; }
    public int AnalysisBytesRead { get; init; }
    public bool AnalysisTruncated { get; init; }
    public string HeadHex { get; init; } = string.Empty;
    public string? EncodingHint { get; init; }
    public string? TextHint { get; init; }
    public bool LooksLikeText { get; init; }
    public DynamicOverlayTextInspection? TextShape { get; init; }
    public DynamicOverlayBinaryInspection? BinaryShape { get; init; }
    public string? Error { get; init; }
}

public static class DynamicOverlayInspector
{
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly int[] CommonRecordSizes =
    [
        4, 8, 12, 16, 20, 24, 28, 32, 36, 40, 44, 48, 56, 64,
    ];

    private const int MaxFixedRecordOffsetProbeBytes = 256;

    public static DynamicOverlayFileInspection InspectFile(string path, DynamicOverlayInspectOptions? options = null)
    {
        DynamicOverlayInspectOptions inspectOptions = SanitizeOptions(options);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DynamicOverlayFileInspection
            {
                Path = string.Empty,
                Exists = false,
                Error = "Path is empty.",
            };
        }

        if (!SafeFileExists(path))
        {
            return new DynamicOverlayFileInspection
            {
                Path = path,
                Exists = false,
            };
        }

        try
        {
            var info = new FileInfo(path);
            byte[] analysisBytes = ReadAnalysisBytes(path, inspectOptions.MaxAnalysisBytes, out bool analysisTruncated);
            byte[] head = analysisBytes.AsSpan(0, Math.Min(analysisBytes.Length, inspectOptions.ReadBytes)).ToArray();

            string? encodingHint = GuessEncodingHint(head);
            string? textHint = GuessTextHint(head);
            string? compressionHint = GuessCompression(head, info.Length);

            bool looksText = LooksLikeText(analysisBytes, encodingHint, textHint);
            DynamicOverlayTextInspection? textShape = looksText
                ? AnalyzeText(analysisBytes, inspectOptions, encodingHint)
                : null;

            DynamicOverlayBinaryInspection? binaryShape = !looksText || textShape is null
                ? AnalyzeBinary(analysisBytes, info.Length, inspectOptions)
                : null;

            DynamicOverlayDecompressionInspection? decompression = null;
            if (inspectOptions.TryDecompress && !string.IsNullOrWhiteSpace(compressionHint) && inspectOptions.MaxDecompressedBytes > 0)
            {
                decompression = InspectDecompression(path, compressionHint, inspectOptions);
            }

            return new DynamicOverlayFileInspection
            {
                Path = path,
                Exists = true,
                SizeBytes = info.Length,
                AnalysisBytesRead = analysisBytes.Length,
                AnalysisTruncated = analysisTruncated,
                HeadHex = head.Length == 0 ? string.Empty : Convert.ToHexString(head),
                Sha256 = Convert.ToHexString(ComputeSha256(path)),
                CompressionHint = compressionHint,
                EncodingHint = encodingHint,
                TextHint = textHint,
                TextShape = textShape,
                BinaryShape = binaryShape,
                Decompression = decompression,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DynamicOverlayFileInspection
            {
                Path = path,
                Exists = true,
                Error = ex.Message,
            };
        }
    }

    public static string? GuessCompression(byte[] head) =>
        head is null ? null : GuessCompression(head.AsSpan(), head.LongLength);

    public static string? GuessCompression(ReadOnlySpan<byte> head) =>
        GuessCompression(head, head.Length);

    public static string? GuessCompression(ReadOnlySpan<byte> head, long totalLength)
    {
        if (head.Length < 2 || totalLength < 2)
        {
            return null;
        }

        if (head[0] == 0x1F && head[1] == 0x8B)
        {
            return "gzip";
        }

        if (head.Length >= 6 && totalLength >= 6)
        {
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(0, 4));
            if (chunkSize != 0 && chunkSize <= totalLength - 4)
            {
                byte cmf2 = head[4];
                byte flg2 = head[5];
                if (LooksLikeZlibHeader(cmf2, flg2))
                {
                    return "chunked-zlib";
                }
            }
        }

        byte cmf = head[0];
        byte flg = head[1];
        return LooksLikeZlibHeader(cmf, flg) ? "zlib" : null;

        static bool LooksLikeZlibHeader(byte cmf, byte flg)
        {
            if ((cmf & 0x0F) != 8)
            {
                return false;
            }

            int check = (cmf << 8) + flg;
            return check % 31 == 0;
        }
    }

    public static string? GuessEncodingHint(byte[] head)
    {
        if (head is null || head.Length == 0)
        {
            return null;
        }

        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return "utf8-bom";
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return "utf16-le-bom";
        }

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return "utf16-be-bom";
        }

        if (LooksLikeUtf16Le(head))
        {
            return "utf16-le?";
        }

        if (LooksLikeUtf16Be(head))
        {
            return "utf16-be?";
        }

        int zeros = 0;
        int controlBytes = 0;
        int sample = Math.Min(head.Length, 64);
        for (int i = 0; i < sample; i++)
        {
            byte b = head[i];
            if (b == 0)
            {
                zeros++;
            }
            else if (b < 32 && b is not 9 and not 10 and not 13)
            {
                controlBytes++;
            }
        }

        if (sample >= 8 && (zeros > 0 || controlBytes > sample / 8))
        {
            return "maybe-utf16-or-binary";
        }

        if (LooksLikeUtf8(head))
        {
            return "utf8";
        }

        if (sample >= 8 && zeros >= sample / 4)
        {
            return "maybe-utf16-or-binary";
        }

        return "unknown";
    }

    public static string? GuessTextHint(byte[] head)
    {
        if (head is null || head.Length == 0)
        {
            return null;
        }

        string text = DecodeBestEffort(head, GuessEncodingHint(head));
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return "xml";
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return "json-object?";
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return "json-array?";
        }

        return null;
    }

    private static DynamicOverlayInspectOptions SanitizeOptions(DynamicOverlayInspectOptions? options)
    {
        DynamicOverlayInspectOptions value = options ?? new DynamicOverlayInspectOptions();
        return value with
        {
            ReadBytes = Math.Clamp(value.ReadBytes, 0, 1024 * 1024),
            SampleCount = Math.Clamp(value.SampleCount, 1, 64),
            MaxAnalysisBytes = Math.Clamp(value.MaxAnalysisBytes, 0, 16 * 1024 * 1024),
            MaxDecompressedBytes = Math.Clamp(value.MaxDecompressedBytes, 0, 256L * 1024 * 1024),
        };
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadAnalysisBytes(string path, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (maxBytes <= 0)
        {
            return [];
        }

        using FileStream stream = File.OpenRead(path);
        long length = stream.Length;
        int take = (int)Math.Min(length, maxBytes);
        truncated = length > take;
        if (take <= 0)
        {
            return [];
        }

        byte[] buffer = new byte[take];
        int readTotal = 0;
        while (readTotal < buffer.Length)
        {
            int read = stream.Read(buffer, readTotal, buffer.Length - readTotal);
            if (read <= 0)
            {
                break;
            }

            readTotal += read;
        }

        return readTotal == buffer.Length ? buffer : buffer.AsSpan(0, readTotal).ToArray();
    }

    private static byte[] ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }

    private static bool LooksLikeText(byte[] bytes, string? encodingHint, string? textHint)
    {
        if (!string.IsNullOrWhiteSpace(textHint))
        {
            return true;
        }

        if (encodingHint is "utf8" or "utf8-bom" or "utf16-le-bom" or "utf16-be-bom" or "utf16-le?" or "utf16-be?")
        {
            return true;
        }

        if (bytes.Length == 0)
        {
            return false;
        }

        int printable = 0;
        int suspicious = 0;
        int sample = Math.Min(bytes.Length, 512);
        for (int i = 0; i < sample; i++)
        {
            byte b = bytes[i];
            if (b == 0)
            {
                suspicious += 2;
                continue;
            }

            if (b is 9 or 10 or 13 || (b >= 32 && b <= 126))
            {
                printable++;
            }
            else if (b < 32)
            {
                suspicious++;
            }
        }

        return printable >= (sample * 3 / 4) && suspicious <= sample / 8;
    }

    private static DynamicOverlayTextInspection AnalyzeText(byte[] bytes, DynamicOverlayInspectOptions options, string? encodingHint)
    {
        string text = DecodeBestEffort(bytes, encodingHint);
        string[] allLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        List<string> nonEmptyLines = allLines
            .Select(static line => line.Trim())
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        string delimiterHint = DetectDelimiter(nonEmptyLines, out int maxColumns);
        int numericLineCount = 0;
        List<IReadOnlyList<long>> sampleNumberRows = [];

        foreach (string line in nonEmptyLines)
        {
            List<long> numbers = ExtractIntegers(line, 16);
            if (numbers.Count >= 2)
            {
                numericLineCount++;
                if (sampleNumberRows.Count < options.SampleCount)
                {
                    sampleNumberRows.Add(numbers);
                }
            }
        }

        List<string> sampleLines = nonEmptyLines
            .Take(options.SampleCount)
            .Select(static line => line.Length <= 160 ? line : line[..160])
            .ToList();

        return new DynamicOverlayTextInspection
        {
            LineCount = allLines.Length,
            NonEmptyLineCount = nonEmptyLines.Count,
            DelimiterHint = delimiterHint,
            MaxColumns = maxColumns,
            NumericLineCount = numericLineCount,
            SampleLines = sampleLines,
            SampleNumberRows = sampleNumberRows,
        };
    }

    private static DynamicOverlayBinaryInspection AnalyzeBinary(byte[] bytes, long totalSize, DynamicOverlayInspectOptions options)
    {
        List<int> candidateRecordSizes = [];
        foreach (int size in CommonRecordSizes)
        {
            if (size <= 0 || totalSize < size * 2)
            {
                continue;
            }

            if (totalSize % size == 0)
            {
                candidateRecordSizes.Add(size);
            }
        }

        List<DynamicOverlayFixedRecordLayoutCandidate> candidateLayouts = SuggestFixedRecordLayouts(totalSize);

        return new DynamicOverlayBinaryInspection
        {
            CandidateRecordSizes = candidateRecordSizes,
            CandidateRecordLayouts = candidateLayouts,
            SampleAsciiStrings = ExtractAsciiStrings(bytes, options.SampleCount),
            SampleUtf16LeStrings = ExtractUtf16LeStrings(bytes, options.SampleCount),
            SampleUInt16Le = ExtractUInt16Le(bytes, options.SampleCount * 2),
            SampleUInt32Le = ExtractUInt32Le(bytes, options.SampleCount * 2),
        };
    }

    private static List<DynamicOverlayFixedRecordLayoutCandidate> SuggestFixedRecordLayouts(long totalSize)
    {
        var candidates = new List<DynamicOverlayFixedRecordLayoutCandidate>();
        if (totalSize <= 0)
        {
            return candidates;
        }

        foreach (int recordSize in CommonRecordSizes)
        {
            if (recordSize <= 0)
            {
                continue;
            }

            long minBytes = (long)recordSize * 2;
            if (totalSize < minBytes)
            {
                continue;
            }

            long maxOffsetLong = Math.Min(MaxFixedRecordOffsetProbeBytes, totalSize - minBytes);
            int maxOffset = (int)Math.Clamp(maxOffsetLong, 0, MaxFixedRecordOffsetProbeBytes);

            int bestOffset = -1;
            int bestCount = 0;
            for (int offset = 0; offset <= maxOffset; offset++)
            {
                long remaining = totalSize - offset;
                if (remaining < minBytes)
                {
                    break;
                }

                if (remaining % recordSize != 0)
                {
                    continue;
                }

                long recordCount = remaining / recordSize;
                if (recordCount < 2)
                {
                    continue;
                }

                bestOffset = offset;
                bestCount = recordCount > int.MaxValue ? int.MaxValue : (int)recordCount;
                break;
            }

            if (bestOffset >= 0)
            {
                candidates.Add(new DynamicOverlayFixedRecordLayoutCandidate
                {
                    Offset = bestOffset,
                    RecordSize = recordSize,
                    RecordCount = bestCount,
                });
            }
        }

        return candidates;
    }

    private static DynamicOverlayDecompressionInspection InspectDecompression(string path, string compression, DynamicOverlayInspectOptions options)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            if (compression == "chunked-zlib")
            {
                return InspectChunkedZlibDecompression(stream, compression, options);
            }

            using Stream decompressor = compression switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "zlib" => new ZLibStream(stream, CompressionMode.Decompress),
                _ => throw new NotSupportedException($"Unsupported compression kind: {compression}"),
            };

            long maxDecompressedBytes = options.MaxDecompressedBytes > 0 ? options.MaxDecompressedBytes : long.MaxValue;
            int readBytes = Math.Clamp(options.ReadBytes, 0, 1024 * 1024);
            int maxAnalysisBytes = Math.Clamp(options.MaxAnalysisBytes, 0, 16 * 1024 * 1024);

            long total = 0;
            byte[] buffer = new byte[16 * 1024];
            using var headStream = new MemoryStream(capacity: Math.Min(readBytes, 1024 * 1024));
            using var analysisStream = new MemoryStream(capacity: Math.Min(maxAnalysisBytes, 1024 * 1024));
            bool truncated = false;

            while (true)
            {
                int readTarget = buffer.Length;
                if (maxDecompressedBytes != long.MaxValue)
                {
                    long remaining = maxDecompressedBytes - total;
                    if (remaining <= 0)
                    {
                        truncated = true;
                        break;
                    }

                    if (remaining < readTarget)
                    {
                        readTarget = remaining > int.MaxValue ? int.MaxValue : (int)remaining;
                    }

                    if (readTarget <= 0)
                    {
                        truncated = true;
                        break;
                    }
                }

                int read = decompressor.Read(buffer, 0, readTarget);
                if (read <= 0)
                {
                    break;
                }

                total += read;

                if (readBytes > 0)
                {
                    int headWanted = Math.Max(0, readBytes - (int)headStream.Length);
                    if (headWanted > 0)
                    {
                        headStream.Write(buffer, 0, Math.Min(headWanted, read));
                    }
                }

                if (maxAnalysisBytes > 0)
                {
                    int analysisWanted = Math.Max(0, maxAnalysisBytes - (int)analysisStream.Length);
                    if (analysisWanted > 0)
                    {
                        analysisStream.Write(buffer, 0, Math.Min(analysisWanted, read));
                    }
                }
            }

            byte[] head = headStream.ToArray();
            byte[] analysisBytes = analysisStream.ToArray();
            bool analysisTruncated = total > analysisBytes.LongLength;

            string? encodingHint = GuessEncodingHint(head);
            string? textHint = GuessTextHint(head);

            bool looksText = LooksLikeText(analysisBytes, encodingHint, textHint);
            DynamicOverlayTextInspection? textShape = looksText
                ? AnalyzeText(analysisBytes, options, encodingHint)
                : null;

            DynamicOverlayBinaryInspection? binaryShape = !looksText || textShape is null
                ? AnalyzeBinary(analysisBytes, total, options)
                : null;

            return new DynamicOverlayDecompressionInspection
            {
                Success = true,
                Kind = compression,
                MaxDecompressedBytes = options.MaxDecompressedBytes,
                DecompressedBytesRead = total,
                Truncated = truncated,
                HeadHex = head.Length == 0 ? string.Empty : Convert.ToHexString(head),
                AnalysisBytesRead = analysisBytes.Length,
                AnalysisTruncated = analysisTruncated,
                EncodingHint = encodingHint,
                TextHint = textHint,
                LooksLikeText = looksText,
                TextShape = textShape,
                BinaryShape = binaryShape,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DynamicOverlayDecompressionInspection
            {
                Success = false,
                Kind = compression,
                MaxDecompressedBytes = options.MaxDecompressedBytes,
                Error = ex.Message,
            };
        }
    }

    private static DynamicOverlayDecompressionInspection InspectChunkedZlibDecompression(FileStream stream, string compression, DynamicOverlayInspectOptions options)
    {
        long maxDecompressedBytes = options.MaxDecompressedBytes > 0 ? options.MaxDecompressedBytes : long.MaxValue;
        int readBytes = Math.Clamp(options.ReadBytes, 0, 1024 * 1024);
        int maxAnalysisBytes = Math.Clamp(options.MaxAnalysisBytes, 0, 16 * 1024 * 1024);

        long total = 0;
        using var headStream = new MemoryStream(capacity: Math.Min(readBytes, 1024 * 1024));
        using var analysisStream = new MemoryStream(capacity: Math.Min(maxAnalysisBytes, 1024 * 1024));
        bool truncated = false;

        Span<byte> header = stackalloc byte[4];
        while (true)
        {
            if (!ReadExactly(stream, header))
            {
                throw new InvalidDataException("Chunked zlib: 输入截断（缺少块头）。");
            }

            uint chunkCompSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (chunkCompSize == 0)
            {
                break;
            }

            if (chunkCompSize > int.MaxValue)
            {
                throw new InvalidDataException($"Chunked zlib: 块大小过大: {chunkCompSize}");
            }

            int chunkSize = (int)chunkCompSize;
            byte[] chunk = new byte[chunkSize];
            if (!ReadExactly(stream, chunk))
            {
                throw new InvalidDataException($"Chunked zlib: 输入截断（缺少块数据，size={chunkSize}）。");
            }

            if (!ZlibUtils.TryDecompress(chunk, out byte[] chunkOut, out string chunkError))
            {
                throw new InvalidDataException(chunkError);
            }

            if (maxDecompressedBytes != long.MaxValue && total + chunkOut.LongLength > maxDecompressedBytes)
            {
                int allowed = (int)Math.Clamp(maxDecompressedBytes - total, 0, int.MaxValue);
                if (allowed > 0)
                {
                    AppendSample(chunkOut.AsSpan(0, allowed));
                    total += allowed;
                }

                truncated = true;
                break;
            }

            AppendSample(chunkOut);
            total += chunkOut.LongLength;
        }

        byte[] head = headStream.ToArray();
        byte[] analysisBytes = analysisStream.ToArray();
        bool analysisTruncated = maxAnalysisBytes > 0 && total > analysisBytes.LongLength;

        string? encodingHint = GuessEncodingHint(head);
        string? textHint = GuessTextHint(head);

        bool looksText = LooksLikeText(analysisBytes, encodingHint, textHint);
        DynamicOverlayTextInspection? textShape = looksText
            ? AnalyzeText(analysisBytes, options, encodingHint)
            : null;

        DynamicOverlayBinaryInspection? binaryShape = !looksText || textShape is null
            ? AnalyzeBinary(analysisBytes, total, options)
            : null;

        return new DynamicOverlayDecompressionInspection
        {
            Success = true,
            Kind = compression,
            MaxDecompressedBytes = options.MaxDecompressedBytes,
            DecompressedBytesRead = total,
            Truncated = truncated,
            HeadHex = head.Length == 0 ? string.Empty : Convert.ToHexString(head),
            AnalysisBytesRead = analysisBytes.Length,
            AnalysisTruncated = analysisTruncated,
            EncodingHint = encodingHint,
            TextHint = textHint,
            LooksLikeText = looksText,
            TextShape = textShape,
            BinaryShape = binaryShape,
        };

        void AppendSample(ReadOnlySpan<byte> bytes)
        {
            if (readBytes > 0)
            {
                int wanted = Math.Max(0, readBytes - (int)headStream.Length);
                if (wanted > 0)
                {
                    headStream.Write(bytes.Slice(0, Math.Min(wanted, bytes.Length)));
                }
            }

            if (maxAnalysisBytes > 0)
            {
                int wanted = Math.Max(0, maxAnalysisBytes - (int)analysisStream.Length);
                if (wanted > 0)
                {
                    analysisStream.Write(bytes.Slice(0, Math.Min(wanted, bytes.Length)));
                }
            }
        }

        static bool ReadExactly(Stream stream, Span<byte> buffer)
        {
            int readTotal = 0;
            while (readTotal < buffer.Length)
            {
                int read = stream.Read(buffer.Slice(readTotal));
                if (read <= 0)
                {
                    return false;
                }

                readTotal += read;
            }

            return true;
        }

    }

    private static string DecodeBestEffort(byte[] bytes, string? encodingHint)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return encodingHint switch
            {
                "utf16-le-bom" => Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
                "utf16-be-bom" => Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
                "utf16-le?" => Encoding.Unicode.GetString(bytes),
                "utf16-be?" => Encoding.BigEndianUnicode.GetString(bytes),
                "utf8-bom" => Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3),
                _ => Utf8Strict.GetString(bytes),
            };
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static bool LooksLikeUtf8(byte[] bytes)
    {
        try
        {
            _ = Utf8Strict.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeUtf16Le(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        int pairs = Math.Min(bytes.Length / 2, 32);
        int zeroHigh = 0;
        int printableLow = 0;
        for (int i = 0; i < pairs; i++)
        {
            byte low = bytes[i * 2];
            byte high = bytes[i * 2 + 1];
            if (high == 0)
            {
                zeroHigh++;
            }

            if (low is 9 or 10 or 13 || (low >= 32 && low <= 126))
            {
                printableLow++;
            }
        }

        return zeroHigh >= (pairs * 3 / 4) && printableLow >= (pairs * 3 / 4);
    }

    private static bool LooksLikeUtf16Be(byte[] bytes)
    {
        if (bytes.Length < 4)
        {
            return false;
        }

        int pairs = Math.Min(bytes.Length / 2, 32);
        int zeroLow = 0;
        int printableHigh = 0;
        for (int i = 0; i < pairs; i++)
        {
            byte high = bytes[i * 2];
            byte low = bytes[i * 2 + 1];
            if (high == 0)
            {
                zeroLow++;
            }

            if (low is 9 or 10 or 13 || (low >= 32 && low <= 126))
            {
                printableHigh++;
            }
        }

        return zeroLow >= (pairs * 3 / 4) && printableHigh >= (pairs * 3 / 4);
    }

    private static string DetectDelimiter(IReadOnlyList<string> lines, out int maxColumns)
    {
        maxColumns = lines.Count == 0 ? 0 : 1;
        if (lines.Count == 0)
        {
            return "none";
        }

        (char Delimiter, string Label)[] candidates =
        [
            (',', "comma"),
            ('\t', "tab"),
            ('|', "pipe"),
            (';', "semicolon"),
        ];

        int bestHits = 0;
        int bestColumns = 1;
        string bestLabel = "none";

        foreach ((char delimiter, string label) in candidates)
        {
            int hits = 0;
            int columns = 1;
            foreach (string line in lines)
            {
                if (line.IndexOf(delimiter) < 0)
                {
                    continue;
                }

                hits++;
                columns = Math.Max(columns, line.Split(delimiter).Length);
            }

            if (hits > bestHits || (hits == bestHits && columns > bestColumns))
            {
                bestHits = hits;
                bestColumns = columns;
                bestLabel = label;
            }
        }

        if (bestHits > 0)
        {
            maxColumns = bestColumns;
            return bestLabel;
        }

        int whitespaceColumns = 1;
        int whitespaceHits = 0;
        foreach (string line in lines)
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
            {
                continue;
            }

            whitespaceHits++;
            whitespaceColumns = Math.Max(whitespaceColumns, parts.Length);
        }

        maxColumns = whitespaceColumns;
        return whitespaceHits > 0 ? "whitespace" : "none";
    }

    private static List<long> ExtractIntegers(string text, int maxCount)
    {
        List<long> values = [];
        if (string.IsNullOrWhiteSpace(text) || maxCount <= 0)
        {
            return values;
        }

        int index = 0;
        while (index < text.Length && values.Count < maxCount)
        {
            while (index < text.Length && !IsNumberStart(text, index))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            int start = index;
            index++;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            if (long.TryParse(text.AsSpan(start, index - start), out long value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool IsNumberStart(string text, int index)
    {
        char c = text[index];
        if (char.IsDigit(c))
        {
            return true;
        }

        if ((c == '-' || c == '+') && index + 1 < text.Length && char.IsDigit(text[index + 1]))
        {
            return true;
        }

        return false;
    }

    private static List<string> ExtractAsciiStrings(byte[] bytes, int maxCount)
    {
        List<string> values = [];
        if (bytes.Length == 0 || maxCount <= 0)
        {
            return values;
        }

        var current = new StringBuilder();
        for (int i = 0; i < bytes.Length && values.Count < maxCount; i++)
        {
            byte b = bytes[i];
            if (b >= 32 && b <= 126)
            {
                current.Append((char)b);
                continue;
            }

            FlushCurrent();
        }

        FlushCurrent();
        return values;

        void FlushCurrent()
        {
            if (current.Length >= 4 && values.Count < maxCount)
            {
                values.Add(current.ToString());
            }

            current.Clear();
        }
    }

    private static List<string> ExtractUtf16LeStrings(byte[] bytes, int maxCount)
    {
        List<string> values = [];
        if (bytes.Length < 8 || maxCount <= 0)
        {
            return values;
        }

        var current = new StringBuilder();
        for (int i = 0; i + 1 < bytes.Length && values.Count < maxCount; i += 2)
        {
            byte low = bytes[i];
            byte high = bytes[i + 1];
            if (high == 0 && (low is 9 or 10 or 13 || (low >= 32 && low <= 126)))
            {
                current.Append((char)low);
                continue;
            }

            FlushCurrent();
        }

        FlushCurrent();
        return values;

        void FlushCurrent()
        {
            if (current.Length >= 4 && values.Count < maxCount)
            {
                values.Add(current.ToString());
            }

            current.Clear();
        }
    }

    private static List<ushort> ExtractUInt16Le(byte[] bytes, int maxCount)
    {
        List<ushort> values = [];
        int pairs = Math.Min(bytes.Length / 2, maxCount);
        for (int i = 0; i < pairs; i++)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2)));
        }

        return values;
    }

    private static List<uint> ExtractUInt32Le(byte[] bytes, int maxCount)
    {
        List<uint> values = [];
        int groups = Math.Min(bytes.Length / 4, maxCount);
        for (int i = 0; i < groups; i++)
        {
            values.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
        }

        return values;
    }
}
