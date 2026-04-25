using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.Tools;

internal static class OverlayTool
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int RunLocate(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("缺少参数：overlay locate <dataRoot> <mapPath>");
            return 2;
        }

        string dataRoot = args[0];
        string mapPath = args[1];
        string? outJsonPath = null;
        string? mapIdOverride = null;
        string? overlayMapIdOverride = null;
        string? effectsMapIdOverride = null;
        int limit = 0;
        bool scan = false;
        bool scanParents = false;
        int scanMaxDepth = 6;
        int scanLimit = 200;

        var remaining = new Queue<string>(args.Skip(2));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--map-id", StringComparison.OrdinalIgnoreCase))
            {
                if (remaining.Count == 0)
                {
                    Console.Error.WriteLine("参数错误：--map-id 需要一个值。");
                    return 2;
                }

                mapIdOverride = remaining.Dequeue();
                if (string.IsNullOrWhiteSpace(mapIdOverride) || mapIdOverride.StartsWith("-", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("参数错误：--map-id 需要一个非空值。");
                    return 2;
                }

                continue;
            }

            if (token.Equals("--overlay-map-id", StringComparison.OrdinalIgnoreCase))
            {
                if (remaining.Count == 0)
                {
                    Console.Error.WriteLine("参数错误：--overlay-map-id 需要一个值。");
                    return 2;
                }

                overlayMapIdOverride = remaining.Dequeue();
                if (string.IsNullOrWhiteSpace(overlayMapIdOverride) || overlayMapIdOverride.StartsWith("-", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("参数错误：--overlay-map-id 需要一个非空值。");
                    return 2;
                }

                continue;
            }

            if (token.Equals("--effects-map-id", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--attached-effects-map-id", StringComparison.OrdinalIgnoreCase))
            {
                if (remaining.Count == 0)
                {
                    Console.Error.WriteLine("参数错误：--effects-map-id 需要一个值。");
                    return 2;
                }

                effectsMapIdOverride = remaining.Dequeue();
                if (string.IsNullOrWhiteSpace(effectsMapIdOverride) || effectsMapIdOverride.StartsWith("-", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("参数错误：--effects-map-id 需要一个非空值。");
                    return 2;
                }

                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsed))
            {
                limit = Math.Clamp(parsed, 0, 10_000);
                continue;
            }

            if (token.Equals("--scan", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                scan = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--scan-parents", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                scanParents = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--scan-max-depth", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedDepth))
            {
                scanMaxDepth = Math.Clamp(parsedDepth, 0, 64);
                continue;
            }

            if (token.Equals("--scan-limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedLimit))
            {
                scanLimit = Math.Clamp(parsedLimit, 0, 100_000);
            }
        }

        string derivedMapId = DynamicOverlayDataLocator.DeriveMapId(mapPath);
        string legacyEffectiveMapId = string.IsNullOrWhiteSpace(mapIdOverride) ? derivedMapId : DynamicOverlayDataLocator.DeriveMapId(mapIdOverride);
        string dynSceneEffectiveMapId = string.IsNullOrWhiteSpace(overlayMapIdOverride) ? legacyEffectiveMapId : DynamicOverlayDataLocator.DeriveMapId(overlayMapIdOverride);
        string attachedEffectsEffectiveMapId = string.IsNullOrWhiteSpace(effectsMapIdOverride) ? legacyEffectiveMapId : DynamicOverlayDataLocator.DeriveMapId(effectsMapIdOverride);

        IReadOnlyList<DynamicOverlayCandidatePath> dynCandidates = DynamicOverlayDataLocator.BuildDynSceneCandidatePathsLabeled(dataRoot, dynSceneEffectiveMapId);
        IReadOnlyList<DynamicOverlayCandidatePath> fxCandidates = DynamicOverlayDataLocator.BuildAttachedEffectsCandidatePathsLabeled(dataRoot, attachedEffectsEffectiveMapId);

        IReadOnlyList<string> probeRoots = DynamicOverlayDataLocator.GetProbeRoots(dataRoot);
        IReadOnlyList<string> scanRoots = scanParents ? probeRoots : probeRoots.Take(1).ToArray();

        List<string>? dynScanHits = null;
        List<string>? fxScanHits = null;
        if (scan && scanLimit > 0 && scanRoots.Count > 0)
        {
            dynScanHits = ScanForDynSceneFiles(scanRoots, dynSceneEffectiveMapId, scanMaxDepth, scanLimit);
            fxScanHits = ScanForAttachedEffectsFiles(scanRoots, attachedEffectsEffectiveMapId, scanMaxDepth, scanLimit);
        }

        (string dynResolvedPath, int? dynResolvedCandidateIndex, string? dynResolvedCandidateLabel) = DynamicOverlayDataLocator.ResolveFirstExistingCandidate(dynCandidates);
        (string fxResolvedPath, int? fxResolvedCandidateIndex, string? fxResolvedCandidateLabel) = DynamicOverlayDataLocator.ResolveFirstExistingCandidate(fxCandidates);

        var payload = new Dictionary<string, object?>
        {
            ["dataRoot"] = dataRoot,
            ["mapPath"] = mapPath,
            ["mapId"] = derivedMapId,
            ["mapIdOverride"] = mapIdOverride,
            ["effectiveMapId"] = legacyEffectiveMapId,
            ["overlayMapIdOverride"] = overlayMapIdOverride,
            ["effectsMapIdOverride"] = effectsMapIdOverride,
            ["dynSceneEffectiveMapId"] = dynSceneEffectiveMapId,
            ["attachedEffectsEffectiveMapId"] = attachedEffectsEffectiveMapId,
            ["dynSceneResolvedPath"] = NullIfEmpty(dynResolvedPath),
            ["dynSceneResolvedCandidateIndex"] = dynResolvedCandidateIndex,
            ["dynSceneResolvedCandidateLabel"] = dynResolvedCandidateLabel,
            ["dynSceneCandidateCount"] = dynCandidates.Count,
            ["attachedEffectsResolvedPath"] = NullIfEmpty(fxResolvedPath),
            ["attachedEffectsResolvedCandidateIndex"] = fxResolvedCandidateIndex,
            ["attachedEffectsResolvedCandidateLabel"] = fxResolvedCandidateLabel,
            ["attachedEffectsCandidateCount"] = fxCandidates.Count,
            ["dynSceneCandidates"] = BuildCandidateList(dynCandidates, limit),
            ["attachedEffectsCandidates"] = BuildCandidateList(fxCandidates, limit),
        };

        if (scan)
        {
            payload["scan"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["scanParents"] = scanParents,
                ["scanMaxDepth"] = scanMaxDepth,
                ["scanLimit"] = scanLimit,
                ["probeRoots"] = probeRoots,
                ["scanRoots"] = scanRoots,
                ["dynSceneHits"] = dynScanHits ?? [],
                ["attachedEffectsHits"] = fxScanHits ?? [],
            };
        }

        return WritePayload(payload, outJsonPath);
    }

    public static int RunScan(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay scan <dataRoot> --pattern <substr> [--ext .dat] ...");
            return 2;
        }

        string dataRoot = args[0];
        string? outJsonPath = null;
        int limit = 20;

        // file filter
        string? pattern = null;
        string? ext = null;

        // scan scope (align with locate/probe)
        bool scanParents = false;
        int scanMaxDepth = 6;
        int scanLimit = 200;

        // inspect options (align with inspect/probe)
        int readBytes = 64;
        int sampleCount = 6;
        int maxAnalyzeBytes = 1024 * 1024;
        long maxDecompressedBytes = 4L * 1024 * 1024;
        bool tryDecompress = true;

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--pattern", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                pattern = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--ext", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                ext = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 0, 10_000);
                continue;
            }

            if (token.Equals("--scan-parents", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                scanParents = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--scan-max-depth", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedDepth))
            {
                scanMaxDepth = Math.Clamp(parsedDepth, 0, 64);
                continue;
            }

            if (token.Equals("--scan-limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedScanLimit))
            {
                scanLimit = Math.Clamp(parsedScanLimit, 0, 100_000);
                continue;
            }

            if (token.Equals("--read-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedRead))
            {
                readBytes = Math.Clamp(parsedRead, 0, 1024 * 1024);
                continue;
            }

            if (token.Equals("--sample", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedSample))
            {
                sampleCount = Math.Clamp(parsedSample, 1, 64);
                continue;
            }

            if (token.Equals("--max-analyze-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedAnalyze))
            {
                maxAnalyzeBytes = Math.Clamp(parsedAnalyze, 0, 16 * 1024 * 1024);
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 256L * 1024 * 1024);
                continue;
            }

            if (token.Equals("--decompress", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                tryDecompress = value is not "0" and not "false";
            }
        }

        pattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern.Trim();
        ext = string.IsNullOrWhiteSpace(ext) ? null : NormalizeExtension(ext);

        if (string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(ext))
        {
            Console.Error.WriteLine("为避免在真实 dataRoot 下误扫过大，请至少指定 --pattern 或 --ext。");
            return 2;
        }

        var options = new DynamicOverlayInspectOptions
        {
            ReadBytes = readBytes,
            SampleCount = sampleCount,
            MaxAnalysisBytes = maxAnalyzeBytes,
            TryDecompress = tryDecompress,
            MaxDecompressedBytes = maxDecompressedBytes,
        };

        IReadOnlyList<string> probeRoots = DynamicOverlayDataLocator.GetProbeRoots(dataRoot);
        IReadOnlyList<string> scanRootsRaw = scanParents ? probeRoots : probeRoots.Take(1).ToArray();
        List<string> hits = ScanForFiles(scanRootsRaw, pattern, ext, scanMaxDepth, scanLimit);

        IEnumerable<string> list = limit > 0 ? hits.Take(limit) : hits;
        object[] probes = list.Select(path => DynamicOverlayInspector.InspectFile(path, options)).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["dataRoot"] = dataRoot,
            ["pattern"] = pattern,
            ["ext"] = ext,
            ["hitCount"] = hits.Count,
            ["hits"] = hits,
            ["inspectOptions"] = new Dictionary<string, object?>
            {
                ["readBytes"] = options.ReadBytes,
                ["sample"] = options.SampleCount,
                ["maxAnalyzeBytes"] = options.MaxAnalysisBytes,
                ["tryDecompress"] = options.TryDecompress,
                ["maxDecompressedBytes"] = options.MaxDecompressedBytes,
                ["probeLimit"] = limit,
            },
            ["scan"] = new Dictionary<string, object?>
            {
                ["scanParents"] = scanParents,
                ["scanMaxDepth"] = scanMaxDepth,
                ["scanLimit"] = scanLimit,
                ["probeRoots"] = probeRoots,
                ["scanRoots"] = scanRootsRaw,
            },
            ["probes"] = probes,
        };

        return WritePayload(payload, outJsonPath);
    }

    public static int RunInspect(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay inspect <path>");
            return 2;
        }

        string path = args[0];
        string? outJsonPath = null;
        DynamicOverlayInspectOptions options = ReadInspectOptions(args.Skip(1), ref outJsonPath);
        return WritePayload(DynamicOverlayInspector.InspectFile(path, options), outJsonPath);
    }

    public static int RunParse(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay parse <path>");
            return 2;
        }

        string path = args[0];
        string? outJsonPath = null;
        int limit = 20;
        long? maxDecompressedBytes = null;

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsed))
            {
                limit = Math.Clamp(parsed, 0, 10_000);
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 2L * 1024 * 1024 * 1024);
                continue;
            }
        }

        bool ok = maxDecompressedBytes.HasValue
            ? DynamicOverlayCodec.TryReadFromFile(path, out DynamicOverlayDocument document, out string error, maxDecompressedBytes: maxDecompressedBytes.Value)
            : DynamicOverlayCodec.TryReadFromFile(path, out document, out error);
        if (!ok)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var payload = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["maxDecompressedBytes"] = maxDecompressedBytes,
            ["format"] = document.Format,
            ["encodingHint"] = document.EncodingHint,
            ["wasCompressed"] = document.WasCompressed,
            ["recordCount"] = document.Records.Count,
            ["records"] = limit > 0 ? document.Records.Take(limit).ToArray() : document.Records,
        };

        return WritePayload(payload, outJsonPath);
    }

    public static int RunStats(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay stats <path>");
            return 2;
        }

        string path = args[0];
        string? outJsonPath = null;
        string? layoutPath = null;
        long? maxDecompressedBytes = null;

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--layout", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                layoutPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 2L * 1024 * 1024 * 1024);
            }
        }

        DynamicOverlayDocument document;
        string error;
        bool ok;
        if (!string.IsNullOrWhiteSpace(layoutPath))
        {
            if (!DynamicOverlayBinaryLayout.TryLoadFromFile(layoutPath, out DynamicOverlayBinaryLayout layout, out error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            ok = maxDecompressedBytes.HasValue
                ? DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
                : DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error);
        }
        else
        {
            ok = maxDecompressedBytes.HasValue
                ? DynamicOverlayCodec.TryReadFromFile(path, out document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
                : DynamicOverlayCodec.TryReadFromFile(path, out document, out error);
        }
        if (!ok)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var stats = BuildOverlayStats(document.Records);
        var payload = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["layoutPath"] = layoutPath,
            ["maxDecompressedBytes"] = maxDecompressedBytes,
            ["format"] = document.Format,
            ["encodingHint"] = document.EncodingHint,
            ["wasCompressed"] = document.WasCompressed,
            ["recordCount"] = document.Records.Count,
            ["stats"] = stats,
        };

        return WritePayload(payload, outJsonPath);
    }

    public static int RunParseBinary(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay parse-binary <path>");
            return 2;
        }

        string path = args[0];
        string? outJsonPath = null;
        string? layoutPath = null;
        int limit = 20;
        long? maxDecompressedBytes = null;

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--layout", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                layoutPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsed))
            {
                limit = Math.Clamp(parsed, 0, 10_000);
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 2L * 1024 * 1024 * 1024);
                continue;
            }
        }

        string effectiveLayoutPath = layoutPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(effectiveLayoutPath))
        {
            string sidecar = path + ".layout.json";
            if (SafeFileExists(sidecar))
            {
                effectiveLayoutPath = sidecar;
            }
        }

        if (!DynamicOverlayBinaryLayout.TryLoadFromFile(effectiveLayoutPath, out DynamicOverlayBinaryLayout layout, out string error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        bool ok = maxDecompressedBytes.HasValue
            ? DynamicOverlayCodec.TryReadFromFile(path, layout, out DynamicOverlayDocument document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
            : DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error);
        if (!ok)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var payload = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["layoutPath"] = effectiveLayoutPath,
            ["maxDecompressedBytes"] = maxDecompressedBytes,
            ["format"] = document.Format,
            ["encodingHint"] = document.EncodingHint,
            ["wasCompressed"] = document.WasCompressed,
            ["recordCount"] = document.Records.Count,
            ["records"] = limit > 0 ? document.Records.Take(limit).ToArray() : document.Records,
        };

        return WritePayload(payload, outJsonPath);
    }

    private static Dictionary<string, object?> BuildOverlayStats(IReadOnlyList<DynamicOverlayRecord> records)
    {
        var kindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blendCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var packageIds = new HashSet<int>();
        var imagePairs = new HashSet<long>();
        var frames = new HashSet<int>();

        bool has = false;
        int xMin = 0, xMax = 0;
        int yMin = 0, yMax = 0;
        int offsetXMin = 0, offsetXMax = 0;
        int offsetYMin = 0, offsetYMax = 0;
        int packageMin = 0, packageMax = 0;
        int imageMin = 0, imageMax = 0;
        int frameMin = 0, frameMax = 0;
        int orderMin = 0, orderMax = 0;
        byte alphaMin = 0, alphaMax = 0;
        float scaleMin = 0, scaleMax = 0;
        byte tintRMin = 0, tintRMax = 0;
        byte tintGMin = 0, tintGMax = 0;
        byte tintBMin = 0, tintBMax = 0;
        byte tintAMin = 0, tintAMax = 0;

        foreach (var record in records)
        {
            IncrementCount(kindCounts, record.Kind);
            IncrementCount(layerCounts, record.Layer);
            IncrementCount(coordCounts, record.CoordinateSpace);
            IncrementCount(blendCounts, record.BlendMode);

            packageIds.Add(record.PackageId);
            frames.Add(record.Frame);
            imagePairs.Add(((long)record.PackageId << 32) ^ (uint)record.ImageId);

            if (!has)
            {
                has = true;
                xMin = xMax = record.X;
                yMin = yMax = record.Y;
                offsetXMin = offsetXMax = record.OffsetX;
                offsetYMin = offsetYMax = record.OffsetY;
                packageMin = packageMax = record.PackageId;
                imageMin = imageMax = record.ImageId;
                frameMin = frameMax = record.Frame;
                orderMin = orderMax = record.Order;
                alphaMin = alphaMax = record.Alpha;
                scaleMin = scaleMax = record.Scale;
                tintRMin = tintRMax = record.TintR;
                tintGMin = tintGMax = record.TintG;
                tintBMin = tintBMax = record.TintB;
                tintAMin = tintAMax = record.TintA;
                continue;
            }

            xMin = Math.Min(xMin, record.X);
            xMax = Math.Max(xMax, record.X);
            yMin = Math.Min(yMin, record.Y);
            yMax = Math.Max(yMax, record.Y);
            offsetXMin = Math.Min(offsetXMin, record.OffsetX);
            offsetXMax = Math.Max(offsetXMax, record.OffsetX);
            offsetYMin = Math.Min(offsetYMin, record.OffsetY);
            offsetYMax = Math.Max(offsetYMax, record.OffsetY);
            packageMin = Math.Min(packageMin, record.PackageId);
            packageMax = Math.Max(packageMax, record.PackageId);
            imageMin = Math.Min(imageMin, record.ImageId);
            imageMax = Math.Max(imageMax, record.ImageId);
            frameMin = Math.Min(frameMin, record.Frame);
            frameMax = Math.Max(frameMax, record.Frame);
            orderMin = Math.Min(orderMin, record.Order);
            orderMax = Math.Max(orderMax, record.Order);
            alphaMin = Math.Min(alphaMin, record.Alpha);
            alphaMax = Math.Max(alphaMax, record.Alpha);
            scaleMin = Math.Min(scaleMin, record.Scale);
            scaleMax = Math.Max(scaleMax, record.Scale);
            tintRMin = Math.Min(tintRMin, record.TintR);
            tintRMax = Math.Max(tintRMax, record.TintR);
            tintGMin = Math.Min(tintGMin, record.TintG);
            tintGMax = Math.Max(tintGMax, record.TintG);
            tintBMin = Math.Min(tintBMin, record.TintB);
            tintBMax = Math.Max(tintBMax, record.TintB);
            tintAMin = Math.Min(tintAMin, record.TintA);
            tintAMax = Math.Max(tintAMax, record.TintA);
        }

        return new Dictionary<string, object?>
        {
            ["kindCounts"] = kindCounts,
            ["layerCounts"] = layerCounts,
            ["coordSpaceCounts"] = coordCounts,
            ["blendModeCounts"] = blendCounts,
            ["uniquePackageCount"] = packageIds.Count,
            ["uniqueImagePairCount"] = imagePairs.Count,
            ["uniqueFrameCount"] = frames.Count,
            ["minMax"] = has
                ? new Dictionary<string, object?>
                {
                    ["xMin"] = xMin,
                    ["xMax"] = xMax,
                    ["yMin"] = yMin,
                    ["yMax"] = yMax,
                    ["offsetXMin"] = offsetXMin,
                    ["offsetXMax"] = offsetXMax,
                    ["offsetYMin"] = offsetYMin,
                    ["offsetYMax"] = offsetYMax,
                    ["packageMin"] = packageMin,
                    ["packageMax"] = packageMax,
                    ["imageMin"] = imageMin,
                    ["imageMax"] = imageMax,
                    ["frameMin"] = frameMin,
                    ["frameMax"] = frameMax,
                    ["orderMin"] = orderMin,
                    ["orderMax"] = orderMax,
                    ["alphaMin"] = alphaMin,
                    ["alphaMax"] = alphaMax,
                    ["scaleMin"] = scaleMin,
                    ["scaleMax"] = scaleMax,
                    ["tintRMin"] = tintRMin,
                    ["tintRMax"] = tintRMax,
                    ["tintGMin"] = tintGMin,
                    ["tintGMax"] = tintGMax,
                    ["tintBMin"] = tintBMin,
                    ["tintBMax"] = tintBMax,
                    ["tintAMin"] = tintAMin,
                    ["tintAMax"] = tintAMax,
                }
                : null,
        };
    }

    private static void IncrementCount(Dictionary<string, int> dict, string value)
    {
        string key = string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        dict.TryGetValue(key, out int existing);
        dict[key] = existing + 1;
    }

    public static int RunExportFixture(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay export-fixture <path> --out <outPath> [--meta <meta.json>] [--format json|csv|wov1|wov1-b64|b64|bin] ...");
            return 2;
        }

        string path = args[0];
        string? outPath = null;
        string? outMetaPath = null;
        string? layoutPath = null;
        string format = "json";
        int limit = 200;
        bool anonymize = false;

        // For b64/bin export:
        bool tryDecompress = false;
        int offset = 0;
        int maxBytes = 1024 * 1024;
        long? maxDecompressedBytes = null;

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--out", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--meta", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outMetaPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--format", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                format = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--layout", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                layoutPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 0, 1_000_000);
                continue;
            }

            if (token.Equals("--anonymize", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                anonymize = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--decompress", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                tryDecompress = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--offset", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedOffset))
            {
                offset = Math.Max(0, parsedOffset);
                continue;
            }

            if (token.Equals("--max-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedMaxBytes))
            {
                maxBytes = Math.Clamp(parsedMaxBytes, 0, 256 * 1024 * 1024);
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 2L * 1024 * 1024 * 1024);
                continue;
            }
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("缺少参数：--out <outPath>");
            return 2;
        }

        if (outMetaPath is not null && string.IsNullOrWhiteSpace(outMetaPath))
        {
            Console.Error.WriteLine("参数错误：--meta 需要一个非空路径。");
            return 2;
        }

        format = (format ?? string.Empty).Trim().ToLowerInvariant();
        switch (format)
        {
            case "json":
            case "csv":
            {
                DynamicOverlayDocument document;
                string error;
                bool ok;
                if (!string.IsNullOrWhiteSpace(layoutPath))
                {
                    if (!DynamicOverlayBinaryLayout.TryLoadFromFile(layoutPath, out DynamicOverlayBinaryLayout layout, out error))
                    {
                        Console.Error.WriteLine(error);
                        return 1;
                    }

                    ok = maxDecompressedBytes.HasValue
                        ? DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
                        : DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error);
                }
                else
                {
                    ok = maxDecompressedBytes.HasValue
                        ? DynamicOverlayCodec.TryReadFromFile(path, out document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
                        : DynamicOverlayCodec.TryReadFromFile(path, out document, out error);
                }
                if (!ok)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                IReadOnlyList<DynamicOverlayRecord> records = limit > 0
                    ? document.Records.Take(limit).ToArray()
                    : document.Records.ToArray();

                if (anonymize)
                {
                    records = AnonymizeRecords(records);
                }

                try
                {
                    if (format == "csv")
                    {
                        string csv = BuildOverlayCsv(records);
                        File.WriteAllText(outPath, csv, Utf8NoBom);
                    }
                    else
                    {
                        string json = BuildOverlayJson(records);
                        File.WriteAllText(outPath, json, Utf8NoBom);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"写出 fixture 失败：{outPath} ({ex.Message})");
                    return 1;
                }

                if (!string.IsNullOrWhiteSpace(outMetaPath))
                {
                    try
                    {
                        var meta = new Dictionary<string, object?>
                        {
                            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["path"] = path,
                            ["outPath"] = outPath,
                            ["metaPath"] = outMetaPath,
                            ["format"] = format,
                            ["limit"] = limit,
                            ["anonymize"] = anonymize,
                            ["layoutPath"] = layoutPath,
                            ["maxDecompressedBytes"] = maxDecompressedBytes,
                            ["recordCount"] = document.Records.Count,
                            ["writtenRecordCount"] = records.Count,
                        };
                        File.WriteAllText(outMetaPath, JsonSerializer.Serialize(meta), Utf8NoBom);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"写出 meta 失败：{outMetaPath} ({ex.Message})");
                        return 1;
                    }
                }

                Console.WriteLine(outPath);
                return 0;
            }

            case "wov1":
            case "wov1-b64":
            {
                DynamicOverlayDocument document;
                string error;
                bool ok;
                if (!string.IsNullOrWhiteSpace(layoutPath))
                {
                    if (!DynamicOverlayBinaryLayout.TryLoadFromFile(layoutPath, out DynamicOverlayBinaryLayout layout, out error))
                    {
                        Console.Error.WriteLine(error);
                        return 1;
                    }

                    ok = maxDecompressedBytes.HasValue
                        ? DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
                        : DynamicOverlayCodec.TryReadFromFile(path, layout, out document, out error);
                }
                else
                {
                    ok = maxDecompressedBytes.HasValue
                        ? DynamicOverlayCodec.TryReadFromFile(path, out document, out error, maxDecompressedBytes: maxDecompressedBytes.Value)
                        : DynamicOverlayCodec.TryReadFromFile(path, out document, out error);
                }
                if (!ok)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                IReadOnlyList<DynamicOverlayRecord> records = limit > 0
                    ? document.Records.Take(limit).ToArray()
                    : document.Records.ToArray();

                if (anonymize)
                {
                    records = AnonymizeRecords(records);
                }

                if (!DynamicOverlayCodec.TryWriteBinaryFixtureV1(records, out byte[] fixtureBytes, out error))
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                string? b64 = null;
                try
                {
                    if (format == "wov1")
                    {
                        File.WriteAllBytes(outPath, fixtureBytes);
                    }
                    else
                    {
                        b64 = Convert.ToBase64String(fixtureBytes);
                        File.WriteAllText(outPath, b64, Utf8NoBom);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"写出 fixture 失败：{outPath} ({ex.Message})");
                    return 1;
                }

                if (!string.IsNullOrWhiteSpace(outMetaPath))
                {
                    try
                    {
                        string sha256 = Convert.ToHexString(SHA256.HashData(fixtureBytes));
                        var meta = new Dictionary<string, object?>
                        {
                            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["path"] = path,
                            ["outPath"] = outPath,
                            ["metaPath"] = outMetaPath,
                            ["format"] = format,
                            ["limit"] = limit,
                            ["anonymize"] = anonymize,
                            ["layoutPath"] = layoutPath,
                            ["maxDecompressedBytes"] = maxDecompressedBytes,
                            ["sourceFormat"] = document.Format,
                            ["sourceWasCompressed"] = document.WasCompressed,
                            ["recordCount"] = document.Records.Count,
                            ["writtenRecordCount"] = records.Count,
                            ["fixtureBytes"] = fixtureBytes.Length,
                            ["fixtureSha256"] = sha256,
                        };
                        if (b64 is not null)
                        {
                            meta["base64Chars"] = b64.Length;
                        }

                        File.WriteAllText(outMetaPath, JsonSerializer.Serialize(meta), Utf8NoBom);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"写出 meta 失败：{outMetaPath} ({ex.Message})");
                        return 1;
                    }
                }

                Console.WriteLine(outPath);
                return 0;
            }

            case "b64":
            case "bin":
            {
                long requiredLong = maxBytes > 0 ? (long)offset + maxBytes : 0;
                if (requiredLong > int.MaxValue)
                {
                    Console.Error.WriteLine($"请求读取的 payload 前缀过大：{requiredLong} bytes（offset={offset}, maxBytes={maxBytes}）。");
                    return 1;
                }

                int requiredBytes = requiredLong > 0 ? (int)requiredLong : 0;
                if (!TryReadProbePayload(
                        path,
                        tryDecompress,
                        requiredBytes,
                        maxDecompressedBytes ?? (256L * 1024 * 1024),
                        out byte[] payload,
                        out string? compressionHint,
                        out bool wasDecompressed,
                        out bool payloadTruncated,
                        out string error))
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }

                if (offset > payload.Length)
                {
                    Console.Error.WriteLine($"offset 越界：{offset}（payloadBytes={payload.Length}）");
                    return 1;
                }

                int available = payload.Length - offset;
                int sliceBytes = maxBytes > 0 ? Math.Min(available, maxBytes) : available;

                string? hint = compressionHint;
                bool outputTruncated = maxBytes > 0 && (payloadTruncated || sliceBytes < available);
                if (tryDecompress && !string.IsNullOrWhiteSpace(hint) && wasDecompressed && outputTruncated)
                {
                    Console.Error.WriteLine($"提示：payload 已解压（{hint}），且输出已截断为 {sliceBytes} bytes（可用 --max-bytes 0 输出全部，或增大 --max-bytes）。");
                }
                else if (outputTruncated)
                {
                    Console.Error.WriteLine($"提示：输出已截断为 {sliceBytes} bytes（可用 --max-bytes 0 输出全部，或增大 --max-bytes）。");
                }

                ReadOnlySpan<byte> slice = payload.AsSpan(offset, sliceBytes);
                string? b64 = null;
                try
                {
                    if (format == "bin")
                    {
                        File.WriteAllBytes(outPath, slice.ToArray());
                    }
                    else
                    {
                        b64 = Convert.ToBase64String(slice);
                        File.WriteAllText(outPath, b64, Utf8NoBom);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"写出 fixture 失败：{outPath} ({ex.Message})");
                    return 1;
                }

                if (!string.IsNullOrWhiteSpace(outMetaPath))
                {
                    try
                    {
                        string sha256 = Convert.ToHexString(SHA256.HashData(slice));
                        var meta = new Dictionary<string, object?>
                        {
                            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            ["path"] = path,
                            ["outPath"] = outPath,
                            ["metaPath"] = outMetaPath,
                            ["format"] = format,
                            ["decompress"] = tryDecompress,
                            ["compressionHint"] = compressionHint,
                            ["wasDecompressed"] = wasDecompressed,
                            ["payloadTruncated"] = payloadTruncated,
                            ["payloadBytesRead"] = payload.Length,
                            ["offset"] = offset,
                            ["maxBytes"] = maxBytes,
                            ["availableBytesFromOffset"] = available,
                            ["sliceBytes"] = sliceBytes,
                            ["outputTruncated"] = outputTruncated,
                            ["maxDecompressedBytes"] = maxDecompressedBytes,
                            ["sliceSha256"] = sha256,
                        };

                        if (format == "b64" && b64 is not null)
                        {
                            meta["base64Chars"] = b64.Length;
                        }

                        File.WriteAllText(outMetaPath, JsonSerializer.Serialize(meta), Utf8NoBom);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"写出 meta 失败：{outMetaPath} ({ex.Message})");
                        return 1;
                    }
                }

                Console.WriteLine(outPath);
                return 0;
            }

            default:
                Console.Error.WriteLine($"不支持的 --format：{format}（支持：json/csv/wov1/wov1-b64/b64/bin）");
                return 2;
        }
    }

    public static int RunDumpRecords(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("缺少参数：overlay dump-records <path>");
            return 2;
        }

        string path = args[0];
        string? outJsonPath = null;
        int offset = 0;
        int recordSize = 40;
        int count = 8;
        bool tryDecompress = true;
        long maxDecompressedBytes = 256L * 1024 * 1024;

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--offset", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedOffset))
            {
                offset = Math.Max(0, parsedOffset);
                continue;
            }

            if (token.Equals("--record-size", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedSize))
            {
                recordSize = Math.Clamp(parsedSize, 1, 1024 * 1024);
                continue;
            }

            if (token.Equals("--count", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedCount))
            {
                count = Math.Clamp(parsedCount, 1, 10_000);
                continue;
            }

            if (token.Equals("--decompress", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                tryDecompress = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 2L * 1024 * 1024 * 1024);
            }
        }

        long requiredLong = (long)offset + (long)recordSize * count;
        if (requiredLong > int.MaxValue)
        {
            Console.Error.WriteLine($"请求读取的 payload 前缀过大：{requiredLong} bytes（offset={offset}, recordSize={recordSize}, count={count}）。");
            return 1;
        }

        int requiredBytes = (int)Math.Max(0, requiredLong);
        if (!TryReadProbePayload(
                path,
                tryDecompress,
                requiredBytes,
                maxDecompressedBytes,
                out byte[] payload,
                out string? compressionHint,
                out bool wasDecompressed,
                out bool payloadTruncated,
                out string error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        IReadOnlyList<DynamicOverlayBinaryRecordView> records = DynamicOverlayBinaryRecordProbe.ProbeFixedRecords(payload, offset, recordSize, count);
        var result = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["compressionHint"] = compressionHint,
            ["wasDecompressed"] = wasDecompressed,
            ["payloadTruncated"] = payloadTruncated,
            ["payloadBytes"] = payload.Length,
            ["offset"] = offset,
            ["recordSize"] = recordSize,
            ["requestedCount"] = count,
            ["actualCount"] = records.Count,
            ["records"] = records,
        };

        return WritePayload(result, outJsonPath);
    }

    public static int RunProbe(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("缺少参数：overlay probe <dataRoot> <mapPath>");
            return 2;
        }

        string dataRoot = args[0];
        string mapPath = args[1];
        string? outJsonPath = null;
        int limit = 2;
        bool scan = false;
        bool scanParents = false;
        int scanMaxDepth = 6;
        int scanLimit = 200;
        string? mapIdOverride = ReadOptionalArg(args.Skip(2), "--map-id");
        string? overlayMapIdOverride = ReadOptionalArg(args.Skip(2), "--overlay-map-id");
        string? effectsMapIdOverride = ReadOptionalArg(args.Skip(2), "--effects-map-id")
            ?? ReadOptionalArg(args.Skip(2), "--attached-effects-map-id");
        DynamicOverlayInspectOptions options = ReadInspectOptions(args.Skip(2), ref outJsonPath, ref limit, ref scan, ref scanParents, ref scanMaxDepth, ref scanLimit);

        string derivedMapId = DynamicOverlayDataLocator.DeriveMapId(mapPath);
        string legacyEffectiveMapId = string.IsNullOrWhiteSpace(mapIdOverride) ? derivedMapId : DynamicOverlayDataLocator.DeriveMapId(mapIdOverride);
        string dynSceneEffectiveMapId = string.IsNullOrWhiteSpace(overlayMapIdOverride) ? legacyEffectiveMapId : DynamicOverlayDataLocator.DeriveMapId(overlayMapIdOverride);
        string attachedEffectsEffectiveMapId = string.IsNullOrWhiteSpace(effectsMapIdOverride) ? legacyEffectiveMapId : DynamicOverlayDataLocator.DeriveMapId(effectsMapIdOverride);

        IReadOnlyList<DynamicOverlayCandidatePath> dynCandidates = DynamicOverlayDataLocator.BuildDynSceneCandidatePathsLabeled(dataRoot, dynSceneEffectiveMapId);
        IReadOnlyList<DynamicOverlayCandidatePath> fxCandidates = DynamicOverlayDataLocator.BuildAttachedEffectsCandidatePathsLabeled(dataRoot, attachedEffectsEffectiveMapId);

        Dictionary<string, object?> dynScene = BuildProbePayload(dynCandidates, limit, options);
        Dictionary<string, object?> attachedEffects = BuildProbePayload(fxCandidates, limit, options);

        IReadOnlyList<string> probeRoots = DynamicOverlayDataLocator.GetProbeRoots(dataRoot);
        IReadOnlyList<string> scanRoots = scanParents ? probeRoots : probeRoots.Take(1).ToArray();
        List<string>? dynScanHits = null;
        List<string>? fxScanHits = null;

        if (scan && scanLimit > 0 && scanRoots.Count > 0)
        {
            dynScanHits = ScanForDynSceneFiles(scanRoots, dynSceneEffectiveMapId, scanMaxDepth, scanLimit);
            fxScanHits = ScanForAttachedEffectsFiles(scanRoots, attachedEffectsEffectiveMapId, scanMaxDepth, scanLimit);

            dynScene["scanHits"] = dynScanHits;
            dynScene["scanProbes"] = dynScanHits.Take(Math.Clamp(limit, 0, dynScanHits.Count))
                .Select(path => DynamicOverlayInspector.InspectFile(path, options)).ToArray();

            attachedEffects["scanHits"] = fxScanHits;
            attachedEffects["scanProbes"] = fxScanHits.Take(Math.Clamp(limit, 0, fxScanHits.Count))
                .Select(path => DynamicOverlayInspector.InspectFile(path, options)).ToArray();
        }

        var payload = new Dictionary<string, object?>
        {
            ["dataRoot"] = dataRoot,
            ["mapPath"] = mapPath,
            ["mapId"] = derivedMapId,
            ["mapIdOverride"] = mapIdOverride,
            ["effectiveMapId"] = legacyEffectiveMapId,
            ["overlayMapIdOverride"] = overlayMapIdOverride,
            ["effectsMapIdOverride"] = effectsMapIdOverride,
            ["dynSceneEffectiveMapId"] = dynSceneEffectiveMapId,
            ["attachedEffectsEffectiveMapId"] = attachedEffectsEffectiveMapId,
            ["probeConfig"] = new Dictionary<string, object?>
            {
                ["limit"] = limit,
                ["readBytes"] = options.ReadBytes,
                ["sampleCount"] = options.SampleCount,
                ["maxAnalyzeBytes"] = options.MaxAnalysisBytes,
                ["tryDecompress"] = options.TryDecompress,
                ["maxDecompressedBytes"] = options.MaxDecompressedBytes,
            },
            ["dynScene"] = dynScene,
            ["attachedEffects"] = attachedEffects,
        };

        if (scan)
        {
            payload["scan"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["scanParents"] = scanParents,
                ["scanMaxDepth"] = scanMaxDepth,
                ["scanLimit"] = scanLimit,
                ["probeRoots"] = probeRoots,
                ["scanRoots"] = scanRoots,
                ["dynSceneHits"] = dynScanHits ?? [],
                ["attachedEffectsHits"] = fxScanHits ?? [],
            };
        }

        return WritePayload(payload, outJsonPath);
    }

    private static string? ReadOptionalArg(IEnumerable<string> args, string name)
    {
        if (args is null)
        {
            return null;
        }

        bool expectValue = false;
        foreach (string token in args)
        {
            if (expectValue)
            {
                if (string.IsNullOrWhiteSpace(token) || token.StartsWith("-", StringComparison.Ordinal))
                {
                    return null;
                }

                return token;
            }

            if (token.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                expectValue = true;
            }
        }

        return null;
    }

    private static DynamicOverlayInspectOptions ReadInspectOptions(IEnumerable<string> args, ref string? outJsonPath)
    {
        int unusedLimit = 0;
        bool unusedScan = false;
        bool unusedScanParents = false;
        int unusedScanMaxDepth = 0;
        int unusedScanLimit = 0;
        return ReadInspectOptions(args, ref outJsonPath, ref unusedLimit, ref unusedScan, ref unusedScanParents, ref unusedScanMaxDepth, ref unusedScanLimit);
    }

    private static DynamicOverlayInspectOptions ReadInspectOptions(IEnumerable<string> args, ref string? outJsonPath, ref int limit)
    {
        bool unusedScan = false;
        bool unusedScanParents = false;
        int unusedScanMaxDepth = 0;
        int unusedScanLimit = 0;
        return ReadInspectOptions(args, ref outJsonPath, ref limit, ref unusedScan, ref unusedScanParents, ref unusedScanMaxDepth, ref unusedScanLimit);
    }

    private static DynamicOverlayInspectOptions ReadInspectOptions(
        IEnumerable<string> args,
        ref string? outJsonPath,
        ref int limit,
        ref bool scan,
        ref bool scanParents,
        ref int scanMaxDepth,
        ref int scanLimit)
    {
        int readBytes = 64;
        int sampleCount = 6;
        int maxAnalyzeBytes = 1024 * 1024;
        long maxDecompressedBytes = 4L * 1024 * 1024;
        bool tryDecompress = true;

        var remaining = new Queue<string>(args);
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 0, 10_000);
                continue;
            }

            if (token.Equals("--read-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedRead))
            {
                readBytes = Math.Clamp(parsedRead, 0, 1024 * 1024);
                continue;
            }

            if (token.Equals("--sample", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedSample))
            {
                sampleCount = Math.Clamp(parsedSample, 1, 64);
                continue;
            }

            if (token.Equals("--max-analyze-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedAnalyze))
            {
                maxAnalyzeBytes = Math.Clamp(parsedAnalyze, 0, 16 * 1024 * 1024);
                continue;
            }

            if (token.Equals("--max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && long.TryParse(remaining.Dequeue(), out long parsedMax))
            {
                maxDecompressedBytes = Math.Clamp(parsedMax, 0, 256L * 1024 * 1024);
                continue;
            }

            if (token.Equals("--decompress", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                tryDecompress = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--scan", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                scan = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--scan-parents", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                scanParents = value is not "0" and not "false";
                continue;
            }

            if (token.Equals("--scan-max-depth", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedDepth))
            {
                scanMaxDepth = Math.Clamp(parsedDepth, 0, 64);
                continue;
            }

            if (token.Equals("--scan-limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedScanLimit))
            {
                scanLimit = Math.Clamp(parsedScanLimit, 0, 100_000);
            }
        }

        return new DynamicOverlayInspectOptions
        {
            ReadBytes = readBytes,
            SampleCount = sampleCount,
            MaxAnalysisBytes = maxAnalyzeBytes,
            TryDecompress = tryDecompress,
            MaxDecompressedBytes = maxDecompressedBytes,
        };
    }

    private static List<Dictionary<string, object?>> BuildCandidateList(IReadOnlyList<DynamicOverlayCandidatePath> candidates, int limit)
    {
        IEnumerable<(DynamicOverlayCandidatePath Candidate, int Index)> list = candidates.Select(static (c, index) => (c, index));
        if (limit > 0)
        {
            list = list.Take(limit);
        }

        return list.Select(static item => new Dictionary<string, object?>
        {
            ["index"] = item.Index,
            ["label"] = item.Candidate.Label,
            ["path"] = item.Candidate.Path,
            ["exists"] = SafeFileExists(item.Candidate.Path),
        }).ToList();
    }

    private static List<string> ScanForFiles(
        IReadOnlyList<string> roots,
        string? fileNameContains,
        string? extension,
        int maxDepth,
        int limit)
    {
        var results = new List<string>();
        if (roots is null || roots.Count == 0 || limit <= 0)
        {
            return results;
        }

        maxDepth = Math.Clamp(maxDepth, 0, 64);
        limit = Math.Clamp(limit, 1, 100_000);

        string? needle = string.IsNullOrWhiteSpace(fileNameContains) ? null : fileNameContains.Trim();
        string? ext = string.IsNullOrWhiteSpace(extension) ? null : NormalizeExtension(extension);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Directory, int Depth)>();
        foreach (string root in roots)
        {
            string scanRoot = NormalizeToDirectory(root);
            if (string.IsNullOrWhiteSpace(scanRoot))
            {
                continue;
            }

            queue.Enqueue((scanRoot, 0));
        }

        while (queue.Count > 0 && results.Count < limit)
        {
            (string directory, int depth) = queue.Dequeue();
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        continue;
                    }

                    if (!seen.Add(file))
                    {
                        continue;
                    }

                    if (ext is not null)
                    {
                        string fileExt;
                        try
                        {
                            fileExt = Path.GetExtension(file);
                        }
                        catch
                        {
                            continue;
                        }

                        if (!fileExt.Equals(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (needle is not null)
                    {
                        string name;
                        try
                        {
                            name = Path.GetFileName(file);
                        }
                        catch
                        {
                            name = file;
                        }

                        if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                    }

                    results.Add(file);
                    if (results.Count >= limit)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // ignore
            }

            if (results.Count >= limit || depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(directory))
                {
                    if (string.IsNullOrWhiteSpace(subDir))
                    {
                        continue;
                    }

                    queue.Enqueue((subDir, depth + 1));
                }
            }
            catch
            {
                // ignore
            }
        }

        return results;
    }

    private static string NormalizeToDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            if (Directory.Exists(path))
            {
                return path;
            }

            if (File.Exists(path))
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
        }
        catch
        {
            // ignore
        }

        return path;
    }

    private static string NormalizeExtension(string ext)
    {
        string value = ext.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value;
    }

    private static List<string> ScanForDynSceneFiles(IReadOnlyList<string> roots, string mapId, int maxDepth, int limit)
    {
        string[] fileNames =
        [
            "DynScene.dat",
            "DynScence.dat",
        ];

        return ScanForKnownFiles(
            roots,
            fileNames,
            maxDepth,
            limit,
            extraCandidatesForDirectory: directory =>
            {
                if (string.IsNullOrWhiteSpace(mapId))
                {
                    return [];
                }

                string? dirName;
                try
                {
                    dirName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch
                {
                    dirName = null;
                }

                if (string.IsNullOrWhiteSpace(dirName))
                {
                    return [];
                }

                if (dirName.Equals("DynScene", StringComparison.OrdinalIgnoreCase)
                    || dirName.Equals("DynScence", StringComparison.OrdinalIgnoreCase))
                {
                    return [Path.Combine(directory, $"{mapId}.dat")];
                }

                return [];
            });
    }

    private static List<string> ScanForAttachedEffectsFiles(IReadOnlyList<string> roots, string mapId, int maxDepth, int limit)
    {
        string[] fileNames =
        [
            "AttachedEffects.dat",
            "AttachedEffect.dat",
            "AttachEffects.dat",
            "AttachEffect.dat",
            "EffectAttach.dat",
            "EffectAttached.dat",
        ];

        return ScanForKnownFiles(
            roots,
            fileNames,
            maxDepth,
            limit,
            extraCandidatesForDirectory: directory =>
            {
                if (string.IsNullOrWhiteSpace(mapId))
                {
                    return [];
                }

                string? dirName;
                try
                {
                    dirName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch
                {
                    dirName = null;
                }

                if (string.IsNullOrWhiteSpace(dirName))
                {
                    return [];
                }

                if (dirName.Equals("AttachedEffects", StringComparison.OrdinalIgnoreCase)
                    || dirName.Equals("AttachedEffect", StringComparison.OrdinalIgnoreCase)
                    || dirName.Equals("AttachEffects", StringComparison.OrdinalIgnoreCase)
                    || dirName.Equals("AttachEffect", StringComparison.OrdinalIgnoreCase)
                    || dirName.Equals("EffectAttach", StringComparison.OrdinalIgnoreCase)
                    || dirName.Equals("EffectAttached", StringComparison.OrdinalIgnoreCase))
                {
                    return [Path.Combine(directory, $"{mapId}.dat")];
                }

                return [];
            });
    }

    private static List<string> ScanForKnownFiles(
        IReadOnlyList<string> roots,
        IReadOnlyList<string> fileNames,
        int maxDepth,
        int limit,
        Func<string, IReadOnlyList<string>>? extraCandidatesForDirectory)
    {
        var results = new List<string>();
        if (roots is null || roots.Count == 0 || fileNames is null || fileNames.Count == 0 || limit <= 0)
        {
            return results;
        }

        maxDepth = Math.Clamp(maxDepth, 0, 64);
        limit = Math.Clamp(limit, 1, 100_000);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Directory, int Depth)>();

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            queue.Enqueue((root, 0));
        }

        while (queue.Count > 0 && results.Count < limit)
        {
            (string directory, int depth) = queue.Dequeue();
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (string fileName in fileNames)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                string candidate;
                try
                {
                    candidate = Path.Combine(directory, fileName);
                }
                catch
                {
                    continue;
                }

                if (!seen.Add(candidate))
                {
                    continue;
                }

                if (!SafeFileExists(candidate))
                {
                    continue;
                }

                results.Add(candidate);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            if (results.Count >= limit)
            {
                break;
            }

            if (extraCandidatesForDirectory is not null)
            {
                IReadOnlyList<string> extra;
                try
                {
                    extra = extraCandidatesForDirectory(directory);
                }
                catch
                {
                    extra = [];
                }

                for (int i = 0; i < extra.Count && results.Count < limit; i++)
                {
                    string candidate = extra[i];
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        continue;
                    }

                    if (!seen.Add(candidate))
                    {
                        continue;
                    }

                    if (!SafeFileExists(candidate))
                    {
                        continue;
                    }

                    results.Add(candidate);
                }
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(directory))
                {
                    if (string.IsNullOrWhiteSpace(subDir))
                    {
                        continue;
                    }

                    queue.Enqueue((subDir, depth + 1));
                }
            }
            catch
            {
                // ignore
            }
        }

        return results;
    }

    private static Dictionary<string, object?> BuildProbePayload(IReadOnlyList<DynamicOverlayCandidatePath> candidates, int limit, DynamicOverlayInspectOptions options)
    {
        (string resolvedPath, int? resolvedCandidateIndex, string? resolvedCandidateLabel) = DynamicOverlayDataLocator.ResolveFirstExistingCandidate(candidates);

        var payload = new Dictionary<string, object?>
        {
            ["resolvedPath"] = NullIfEmpty(resolvedPath),
            ["resolvedCandidateIndex"] = resolvedCandidateIndex,
            ["resolvedCandidateLabel"] = resolvedCandidateLabel,
            ["candidateCount"] = candidates.Count,
        };

        IEnumerable<string> list = limit > 0 ? candidates.Take(limit).Select(static c => c.Path) : candidates.Select(static c => c.Path);
        payload["probes"] = list.Where(SafeFileExists).Select(path => DynamicOverlayInspector.InspectFile(path, options)).ToArray();
        return payload;
    }

    private static int WritePayload(object payload, string? outJsonPath)
    {
        string json = JsonSerializer.Serialize(payload);
        Console.WriteLine(json);

        if (!string.IsNullOrWhiteSpace(outJsonPath))
        {
            try
            {
                File.WriteAllText(outJsonPath, json, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"写出 JSON 失败：{outJsonPath} ({ex.Message})");
                return 1;
            }
        }

        return 0;
    }

    private static bool TryReadProbePayload(
        string path,
        bool tryDecompress,
        int requiredBytes,
        long maxDecompressedBytes,
        out byte[] payload,
        out string? compressionHint,
        out bool wasDecompressed,
        out bool payloadTruncated,
        out string error)
    {
        payload = [];
        compressionHint = null;
        wasDecompressed = false;
        payloadTruncated = false;
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
            requiredBytes = Math.Max(0, requiredBytes);
            maxDecompressedBytes = Math.Clamp(maxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024);

            byte[] head = new byte[16];
            int headRead;
            long fileSize;
            using (FileStream headStream = File.OpenRead(path))
            {
                fileSize = headStream.Length;
                headRead = headStream.Read(head, 0, head.Length);
            }

            compressionHint = DynamicOverlayInspector.GuessCompression(head.AsSpan(0, Math.Max(0, headRead)), fileSize);

            if (!tryDecompress || string.IsNullOrWhiteSpace(compressionHint))
            {
                return TryReadFilePrefix(path, requiredBytes, maxDecompressedBytes, out payload, out payloadTruncated, out error);
            }

            if (requiredBytes > 0 && maxDecompressedBytes > 0 && requiredBytes > maxDecompressedBytes)
            {
                error = $"请求读取的解压后前缀超过限制：{requiredBytes} bytes（maxDecompressedBytes={maxDecompressedBytes}）。";
                return false;
            }

            using FileStream fs = File.OpenRead(path);
            if (compressionHint == "chunked-zlib")
            {
                if (!TryReadChunkedZlibPrefix(fs, requiredBytes, maxDecompressedBytes, out payload, out payloadTruncated, out error))
                {
                    return false;
                }

                wasDecompressed = true;
                return true;
            }

            using Stream stream = compressionHint switch
            {
                "gzip" => new GZipStream(fs, CompressionMode.Decompress),
                "zlib" => new ZLibStream(fs, CompressionMode.Decompress),
                _ => throw new NotSupportedException($"不支持的压缩类型：{compressionHint}"),
            };

            using var output = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            long maxBytes = maxDecompressedBytes > 0 ? maxDecompressedBytes : long.MaxValue;

            while (true)
            {
                int readTarget = buffer.Length;
                if (requiredBytes > 0)
                {
                    int remainingWanted = requiredBytes - (int)output.Length;
                    if (remainingWanted <= 0)
                    {
                        payloadTruncated = true;
                        break;
                    }

                    readTarget = Math.Min(readTarget, remainingWanted);
                }

                int read = stream.Read(buffer, 0, readTarget);
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);

                if (output.Length > maxBytes)
                {
                    error = $"解压后超过限制：{maxDecompressedBytes} bytes。";
                    return false;
                }

                if (requiredBytes > 0)
                {
                    if (output.Length >= requiredBytes)
                    {
                        payloadTruncated = true;
                        break;
                    }
                }
            }

            payload = output.ToArray();
            wasDecompressed = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadChunkedZlibPrefix(Stream stream, int requiredBytes, long maxDecompressedBytes, out byte[] payload, out bool payloadTruncated, out string error)
    {
        payload = [];
        payloadTruncated = false;
        error = string.Empty;

        if (requiredBytes < 0)
        {
            requiredBytes = 0;
        }

        maxDecompressedBytes = Math.Clamp(maxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024);
        long maxBytes = maxDecompressedBytes > 0 ? maxDecompressedBytes : long.MaxValue;

        if (requiredBytes > 0 && maxDecompressedBytes > 0 && requiredBytes > maxDecompressedBytes)
        {
            error = $"请求读取的解压后前缀超过限制：{requiredBytes} bytes（maxDecompressedBytes={maxDecompressedBytes}）。";
            return false;
        }

        try
        {
            using var output = new MemoryStream();
            Span<byte> header = stackalloc byte[4];

            while (true)
            {
                if (!ReadExactly(stream, header))
                {
                    error = "Chunked zlib: 输入截断（缺少块头）。";
                    return false;
                }

                uint chunkCompSize = BinaryPrimitives.ReadUInt32LittleEndian(header);
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
                byte[] chunk = new byte[chunkSize];
                if (!ReadExactly(stream, chunk))
                {
                    error = $"Chunked zlib: 输入截断（缺少块数据，size={chunkSize}）。";
                    return false;
                }

                if (!ZlibUtils.TryDecompress(chunk, out byte[] chunkOut, out string chunkError))
                {
                    error = chunkError;
                    return false;
                }

                int writeCount = chunkOut.Length;
                if (requiredBytes > 0)
                {
                    int remainingWanted = requiredBytes - (int)output.Length;
                    if (remainingWanted <= 0)
                    {
                        payloadTruncated = true;
                        break;
                    }

                    writeCount = Math.Min(writeCount, remainingWanted);
                }

                if (writeCount > 0)
                {
                    output.Write(chunkOut, 0, writeCount);
                }

                if (output.Length > maxBytes)
                {
                    error = $"解压后超过限制：{maxDecompressedBytes} bytes。";
                    return false;
                }

                if (requiredBytes > 0 && output.Length >= requiredBytes)
                {
                    payloadTruncated = true;
                    break;
                }
            }

            payload = output.ToArray();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
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

    private static bool TryReadFilePrefix(string path, int requiredBytes, long maxBytes, out byte[] payload, out bool payloadTruncated, out string error)
    {
        payload = [];
        payloadTruncated = false;
        error = string.Empty;

        if (requiredBytes < 0)
        {
            requiredBytes = 0;
        }

        maxBytes = Math.Clamp(maxBytes, 0, 2L * 1024 * 1024 * 1024);

        try
        {
            long fileSize = 0;
            try
            {
                fileSize = new FileInfo(path).Length;
            }
            catch
            {
                fileSize = 0;
            }

            if (requiredBytes <= 0)
            {
                if (maxBytes > 0 && fileSize > maxBytes)
                {
                    error = $"文件过大：{fileSize} bytes（maxDecompressedBytes={maxBytes}）。";
                    return false;
                }

                payload = File.ReadAllBytes(path);
                return true;
            }

            if (maxBytes > 0 && requiredBytes > maxBytes)
            {
                error = $"请求读取的 payload 前缀超过限制：{requiredBytes} bytes（maxDecompressedBytes={maxBytes}）。";
                return false;
            }

            if (fileSize > 0 && fileSize > requiredBytes)
            {
                payloadTruncated = true;
            }

            payload = new byte[requiredBytes];
            int readTotal = 0;
            using (FileStream fs = File.OpenRead(path))
            {
                while (readTotal < requiredBytes)
                {
                    int read = fs.Read(payload, readTotal, requiredBytes - readTotal);
                    if (read <= 0)
                    {
                        break;
                    }

                    readTotal += read;
                }
            }

            if (readTotal <= 0)
            {
                payload = [];
                error = "读取失败：未读取到任何字节。";
                return false;
            }

            if (readTotal < requiredBytes)
            {
                payload = payload.AsSpan(0, readTotal).ToArray();
                payloadTruncated = false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<DynamicOverlayRecord> AnonymizeRecords(IReadOnlyList<DynamicOverlayRecord> records)
    {
        if (records is null || records.Count == 0)
        {
            return Array.Empty<DynamicOverlayRecord>();
        }

        int minX = records.Min(static r => r.X);
        int minY = records.Min(static r => r.Y);

        int[] packages = records.Select(static r => r.PackageId).Distinct().OrderBy(static v => v).ToArray();
        var packageMap = new Dictionary<int, int>(capacity: packages.Length);
        for (int i = 0; i < packages.Length; i++)
        {
            packageMap[packages[i]] = i + 1;
        }

        long[] pairs = records
            .Select(static r => (((long)r.PackageId) << 32) | (uint)r.ImageId)
            .Distinct()
            .OrderBy(static v => v)
            .ToArray();
        var imageMap = new Dictionary<long, int>(capacity: pairs.Length);
        for (int i = 0; i < pairs.Length; i++)
        {
            imageMap[pairs[i]] = i + 1;
        }

        var output = new List<DynamicOverlayRecord>(capacity: records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            DynamicOverlayRecord r = records[i];
            int pkg = packageMap.TryGetValue(r.PackageId, out int mappedPkg) ? mappedPkg : 1;

            long key = (((long)r.PackageId) << 32) | (uint)r.ImageId;
            int img = imageMap.TryGetValue(key, out int mappedImg) ? mappedImg : 1;

            output.Add(r with
            {
                X = r.X - minX,
                Y = r.Y - minY,
                PackageId = pkg,
                ImageId = img,
                Label = string.Empty,
            });
        }

        return output;
    }

    private static string BuildOverlayCsv(IReadOnlyList<DynamicOverlayRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kind,layer,coordSpace,x,y,package,image,frame,offsetX,offsetY,alpha,scale,tint,blend,order,label");
        for (int i = 0; i < records.Count; i++)
        {
            DynamicOverlayRecord r = records[i];
            string tint = FormatTintRgbaHex(r);
            sb.Append(CsvEscape(r.Kind));
            sb.Append(',');
            sb.Append(CsvEscape(r.Layer));
            sb.Append(',');
            sb.Append(CsvEscape(r.CoordinateSpace));
            sb.Append(',');
            sb.Append(r.X.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.Y.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.PackageId.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.ImageId.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.Frame.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.OffsetX.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.OffsetY.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(((int)r.Alpha).ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(r.Scale.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(CsvEscape(tint));
            sb.Append(',');
            sb.Append(CsvEscape(r.BlendMode));
            sb.Append(',');
            sb.Append(r.Order.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(CsvEscape(r.Label));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildOverlayJson(IReadOnlyList<DynamicOverlayRecord> records)
    {
        object payload = new Dictionary<string, object?>
        {
            ["records"] = records.Select(static r => (object)new Dictionary<string, object?>
            {
                ["kind"] = r.Kind,
                ["layer"] = r.Layer,
                ["coordinateSpace"] = r.CoordinateSpace,
                ["x"] = r.X,
                ["y"] = r.Y,
                ["package"] = r.PackageId,
                ["image"] = r.ImageId,
                ["frame"] = r.Frame,
                ["offsetX"] = r.OffsetX,
                ["offsetY"] = r.OffsetY,
                ["alpha"] = (int)r.Alpha,
                ["scale"] = r.Scale,
                ["tint"] = FormatTintRgbaHex(r),
                ["blend"] = r.BlendMode,
                ["order"] = r.Order,
                ["label"] = r.Label,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string CsvEscape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        bool needsQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ',' or '"' or '\r' or '\n')
            {
                needsQuote = true;
                break;
            }
        }

        if (!needsQuote)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatTintRgbaHex(DynamicOverlayRecord record)
    {
        if (record is null)
        {
            return "#FFFFFF";
        }

        if (record.TintA == 255)
        {
            return $"#{record.TintR:X2}{record.TintG:X2}{record.TintB:X2}";
        }

        return $"#{record.TintR:X2}{record.TintG:X2}{record.TintB:X2}{record.TintA:X2}";
    }
}
