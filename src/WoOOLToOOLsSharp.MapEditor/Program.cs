using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using WoOOLToOOLsSharp.MapEditor.App;
using WoOOLToOOLsSharp.Rendering.Vulkan;

string[] normalizedArgs = NormalizeLegacyArgs(args);

if (normalizedArgs.Length > 0 && normalizedArgs[0].Equals("--minimap-export", StringComparison.OrdinalIgnoreCase))
{
    return RunHeadlessMinimapExport(normalizedArgs);
}

if (normalizedArgs.Length > 0 && normalizedArgs[0].Equals("--minimap-export-batch", StringComparison.OrdinalIgnoreCase))
{
    return RunHeadlessMinimapBatchExport(normalizedArgs);
}

if (normalizedArgs.Length > 0 && normalizedArgs[0].Equals("--validate-resources", StringComparison.OrdinalIgnoreCase))
{
    return RunHeadlessResourceValidation(normalizedArgs);
}

return VulkanAppRunner.Run("WoOOL 地图编辑器 (C# / Silk.NET.Vulkan)", new MapEditorApp());

static int RunHeadlessMinimapExport(string[] args)
{
    // Usage:
    //   MapEditor --minimap-export <mapPath> <out.png> --data-root <dir> [options...]
    if (args.Length < 3)
    {
        PrintUsage();
        return 2;
    }

    string mapPath = args[1];
    string outputPath = args[2];

    string dataRoot = string.Empty;
    bool recursive = true;
    bool useTextures = true;
    string diagJsonPath = string.Empty;

    var opts = new MinimapExportOptions();

    var remaining = new Queue<string>(args[3..]);
    while (remaining.Count > 0)
    {
        string token = remaining.Dequeue();

        if (token.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || token.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/?", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        if (token.Equals("--data-root", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            dataRoot = remaining.Dequeue();
            continue;
        }

        if (token.Equals("--recursive", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            recursive = ParseBoolLike(remaining.Dequeue(), defaultValue: recursive);
            continue;
        }

        if (token.Equals("--use-textures", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            useTextures = ParseBoolLike(remaining.Dequeue(), defaultValue: useTextures);
            continue;
        }

        if (token.Equals("--diag-json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            diagJsonPath = remaining.Dequeue();
            continue;
        }

        if (token.Equals("--parity", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            bool enable = ParseBoolLike(remaining.Dequeue(), defaultValue: false);
            if (enable)
            {
                ApplyParityDefaults(opts);
            }
            continue;
        }

        if (token.Equals("--scale", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale))
            {
                opts.ScaleDivisor = scale;
            }
            continue;
        }

        if (token.Equals("--separate", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.SeparateLayerFiles = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.SeparateLayerFiles);
            continue;
        }

        if (token.Equals("--crop-mode", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (!TryParseCropMode(value, out MinimapCropMode cropMode))
            {
                Console.Error.WriteLine($"参数错误：--crop-mode 不支持：{value}（支持 none|cell|pixel|auto 或 0..3）。");
                return 2;
            }

            opts.CropMode = cropMode;
            continue;
        }

        if (token.Equals("--crop-cell", StringComparison.OrdinalIgnoreCase))
        {
            if (remaining.Count < 4)
            {
                Console.Error.WriteLine("参数错误：--crop-cell 需要 4 个整数：x y w h（0-based cell）。");
                return 2;
            }

            if (!int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                Console.Error.WriteLine("参数错误：--crop-cell 的 x/y/w/h 必须为整数。");
                return 2;
            }

            opts.CropMode = MinimapCropMode.CellRect;
            opts.CropCellX = x;
            opts.CropCellY = y;
            opts.CropCellWidth = w;
            opts.CropCellHeight = h;
            continue;
        }

        if (token.Equals("--crop-pixel", StringComparison.OrdinalIgnoreCase))
        {
            if (remaining.Count < 4)
            {
                Console.Error.WriteLine("参数错误：--crop-pixel 需要 4 个整数：x y w h（0-based 输出像素；基于当前 --scale 的输出尺寸）。");
                return 2;
            }

            if (!int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                Console.Error.WriteLine("参数错误：--crop-pixel 的 x/y/w/h 必须为整数。");
                return 2;
            }

            opts.CropMode = MinimapCropMode.PixelRect;
            opts.CropPixelX = x;
            opts.CropPixelY = y;
            opts.CropPixelWidth = w;
            opts.CropPixelHeight = h;
            continue;
        }

        if (token.Equals("--auto-crop-padding", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (!int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pad))
            {
                Console.Error.WriteLine("参数错误：--auto-crop-padding 必须为整数。");
                return 2;
            }

            opts.AutoCropPaddingCells = Math.Max(0, pad);
            if (opts.CropMode == MinimapCropMode.None)
            {
                opts.CropMode = MinimapCropMode.AutoNonEmptyCells;
            }
            continue;
        }

        if (token.Equals("--include-back", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeBack = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeBack);
            continue;
        }

        if (token.Equals("--include-middle", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeMiddle = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeMiddle);
            continue;
        }

        if (token.Equals("--include-front", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeFront = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeFront);
            continue;
        }

        if (token.Equals("--include-floor", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeFloor = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeFloor);
            continue;
        }

        if (token.Equals("--include-underfront", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeUnderFront = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeUnderFront);
            continue;
        }

        if (token.Equals("--include-overfront", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeOverFront = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeOverFront);
            continue;
        }

        if (token.Equals("--include-dynscene", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeDynamicScene = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeDynamicScene);
            continue;
        }

        if (token.Equals("--overlay-map-id", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--overlay-map-id 需要一个非空值。");
                return 2;
            }

            opts.DynamicOverlayMapIdOverride = value;
            continue;
        }

        if (token.Equals("--overlay-layout", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--overlay-layout 需要一个布局 JSON 路径。");
                return 2;
            }

            opts.DynamicOverlayLayoutPath = value;
            continue;
        }

        if ((token.Equals("--effects-map-id", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--attached-effects-map-id", StringComparison.OrdinalIgnoreCase))
            && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--effects-map-id 需要一个非空值。");
                return 2;
            }

            opts.AttachedEffectsMapIdOverride = value;
            continue;
        }

        if ((token.Equals("--effects-layout", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--attached-effects-layout", StringComparison.OrdinalIgnoreCase))
            && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--effects-layout 需要一个布局 JSON 路径。");
                return 2;
            }

            opts.AttachedEffectsLayoutPath = value;
            continue;
        }

        if (token.Equals("--include-effects", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeAttachedEffects = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeAttachedEffects);
            continue;
        }

        if (token.Equals("--overlay-max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (long.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                opts.DynamicOverlayMaxDecompressedBytes = Math.Clamp(parsed, 0, 2L * 1024 * 1024 * 1024);
            }
            continue;
        }

        if (token.Equals("--suppress-border", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.SuppressBorderCells = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.SuppressBorderCells);
            continue;
        }

        if (token.Equals("--apply-tints", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyCellTints = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyCellTints);
            continue;
        }

        if (token.Equals("--tint-strength", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.TintStrength = Math.Clamp(parsed, 0.0f, 1.0f);
            }
            continue;
        }

        if (token.Equals("--apply-height-flag", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyCellHeightFlag = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyCellHeightFlag);
            continue;
        }

        if (token.Equals("--cell-height-offset", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.CellHeightFlagOffset = Math.Max(0.0f, parsed);
            }
            continue;
        }

        if (token.Equals("--apply-object-height", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyObjectHeight = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyObjectHeight);
            continue;
        }

        if (token.Equals("--object-height-scale", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.ObjectHeightScale = Math.Max(0.0f, parsed);
            }
            continue;
        }

        if (token.Equals("--apply-lighting", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyLightingOverlay = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyLightingOverlay);
            continue;
        }

        if (token.Equals("--overlay-max-alpha", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                opts.LightingOverlayMaxAlpha = Math.Clamp(parsed, 0, 255);
            }
            continue;
        }

        if (token.Equals("--include-light-sprites", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeLightSprites = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeLightSprites);
            continue;
        }

        if (token.Equals("--night-factor", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.NightFactor = Math.Clamp(parsed, 0.0f, 1.0f);
            }
            continue;
        }
    }

    if (!IsPowerOfTwo(opts.ScaleDivisor) || opts.ScaleDivisor <= 0)
    {
        Console.Error.WriteLine("参数错误：--scale 必须为 2 的幂（1/2/4/8/16/32）。");
        return 2;
    }

    if (useTextures)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            Console.Error.WriteLine("缺少参数：--data-root <贴图库目录>");
            return 2;
        }

        if (!Directory.Exists(dataRoot))
        {
            Console.Error.WriteLine($"贴图库目录不存在：{dataRoot}");
            return 2;
        }
    }

    if (!MapDocument.TryLoad(mapPath, out MapDocument? map, out string loadError) || map is null)
    {
        Console.Error.WriteLine(string.IsNullOrWhiteSpace(loadError) ? "加载地图失败。" : loadError);
        return 1;
    }

    bool ok;
    string exportError;
    string[] writtenFiles = Array.Empty<string>();
    MinimapExportDiagnostics diag = default;

    if (useTextures)
    {
        using var textures = new MapTextureIndex();

        TextureScanResult scan = MapTextureIndex.ScanSglDirectory(dataRoot, recursive);
        if (!scan.Ok)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(scan.Error) ? "贴图库扫描失败。" : scan.Error);
            return 1;
        }

        textures.ApplyIndex(
            scan.RootDirectory,
            scan.PackageToStandaloneSglPath,
            scan.PackageToWpfSglSource,
            scan.PackageToWpfTex);

        ok = MinimapExporter.TryExportTexturedPng(map, textures, opts, outputPath, out writtenFiles, out diag, out exportError);
    }
    else
    {
        ok = MinimapExporter.TryExportPlaceholderPng(map, opts, outputPath, out writtenFiles, out exportError);
    }

    if (!ok)
    {
        Console.Error.WriteLine(string.IsNullOrWhiteSpace(exportError) ? "导出失败。" : exportError);
        return 1;
    }

    foreach (string file in writtenFiles)
    {
        Console.WriteLine(file);
    }

    if (useTextures)
    {
        string warn = diag.BuildWarningSummary();
        if (!string.IsNullOrWhiteSpace(warn))
        {
            Console.Error.WriteLine($"警告：{warn}");
        }
    }

    if (!string.IsNullOrWhiteSpace(diagJsonPath))
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["mapPath"] = mapPath,
            ["outputPath"] = outputPath,
            ["dataRoot"] = dataRoot,
            ["recursiveTextures"] = recursive,
            ["useTextures"] = useTextures,
            ["options"] = opts,
            ["writtenFiles"] = writtenFiles,
            ["diagnostics"] = diag,
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        string json = JsonSerializer.Serialize(payload, jsonOptions);

        try
        {
            string? dir = Path.GetDirectoryName(diagJsonPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(diagJsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"写出诊断 JSON 失败：{diagJsonPath} ({ex.Message})");
            return 1;
        }
    }

    return 0;
}

static int RunHeadlessMinimapBatchExport(string[] args)
{
    // Usage:
    //   MapEditor --minimap-export-batch <mapRoot> <outDir> --data-root <dir> [options...]
    if (args.Length < 3)
    {
        PrintUsage();
        return 2;
    }

    string mapRoot = args[1];
    string outputDir = args[2];

    string dataRoot = string.Empty;
    bool recursiveTextures = true;
    bool recursiveMaps = true;
    bool useTextures = true;
    bool overwrite = false;
    bool includeScaleTag = true;
    int limit = 0;
    string diagJsonPath = string.Empty;

    var opts = new MinimapExportOptions();

    var remaining = new Queue<string>(args[3..]);
    while (remaining.Count > 0)
    {
        string token = remaining.Dequeue();

        if (token.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || token.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/?", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        if (token.Equals("--data-root", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            dataRoot = remaining.Dequeue();
            continue;
        }

        if (token.Equals("--recursive", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            recursiveTextures = ParseBoolLike(remaining.Dequeue(), defaultValue: recursiveTextures);
            continue;
        }

        if (token.Equals("--recursive-maps", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            recursiveMaps = ParseBoolLike(remaining.Dequeue(), defaultValue: recursiveMaps);
            continue;
        }

        if (token.Equals("--use-textures", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            useTextures = ParseBoolLike(remaining.Dequeue(), defaultValue: useTextures);
            continue;
        }

        if (token.Equals("--diag-json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--diag-json 需要一个非空值。");
                return 2;
            }

            diagJsonPath = value;
            continue;
        }

        if (token.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            overwrite = ParseBoolLike(remaining.Dequeue(), defaultValue: overwrite);
            continue;
        }

        if (token.Equals("--include-scale-tag", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            includeScaleTag = ParseBoolLike(remaining.Dequeue(), defaultValue: includeScaleTag);
            continue;
        }

        if (token.Equals("--limit", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                limit = Math.Max(0, parsed);
            }
            continue;
        }

        if (token.Equals("--parity", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            bool enable = ParseBoolLike(remaining.Dequeue(), defaultValue: false);
            if (enable)
            {
                ApplyParityDefaults(opts);
            }
            continue;
        }

        if (token.Equals("--scale", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale))
            {
                opts.ScaleDivisor = scale;
            }
            continue;
        }

        if (token.Equals("--separate", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.SeparateLayerFiles = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.SeparateLayerFiles);
            continue;
        }

        if (token.Equals("--crop-mode", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (!TryParseCropMode(value, out MinimapCropMode cropMode))
            {
                Console.Error.WriteLine($"参数错误：--crop-mode 不支持：{value}（支持 none|cell|pixel|auto 或 0..3）。");
                return 2;
            }

            opts.CropMode = cropMode;
            continue;
        }

        if (token.Equals("--crop-cell", StringComparison.OrdinalIgnoreCase))
        {
            if (remaining.Count < 4)
            {
                Console.Error.WriteLine("参数错误：--crop-cell 需要 4 个整数：x y w h（0-based cell）。");
                return 2;
            }

            if (!int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                Console.Error.WriteLine("参数错误：--crop-cell 的 x/y/w/h 必须为整数。");
                return 2;
            }

            opts.CropMode = MinimapCropMode.CellRect;
            opts.CropCellX = x;
            opts.CropCellY = y;
            opts.CropCellWidth = w;
            opts.CropCellHeight = h;
            continue;
        }

        if (token.Equals("--crop-pixel", StringComparison.OrdinalIgnoreCase))
        {
            if (remaining.Count < 4)
            {
                Console.Error.WriteLine("参数错误：--crop-pixel 需要 4 个整数：x y w h（0-based 输出像素；基于当前 --scale 的输出尺寸）。");
                return 2;
            }

            if (!int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                || !int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
            {
                Console.Error.WriteLine("参数错误：--crop-pixel 的 x/y/w/h 必须为整数。");
                return 2;
            }

            opts.CropMode = MinimapCropMode.PixelRect;
            opts.CropPixelX = x;
            opts.CropPixelY = y;
            opts.CropPixelWidth = w;
            opts.CropPixelHeight = h;
            continue;
        }

        if (token.Equals("--auto-crop-padding", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (!int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pad))
            {
                Console.Error.WriteLine("参数错误：--auto-crop-padding 必须为整数。");
                return 2;
            }

            opts.AutoCropPaddingCells = Math.Max(0, pad);
            if (opts.CropMode == MinimapCropMode.None)
            {
                opts.CropMode = MinimapCropMode.AutoNonEmptyCells;
            }
            continue;
        }

        if (token.Equals("--include-back", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeBack = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeBack);
            continue;
        }

        if (token.Equals("--include-middle", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeMiddle = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeMiddle);
            continue;
        }

        if (token.Equals("--include-front", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeFront = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeFront);
            continue;
        }

        if (token.Equals("--include-floor", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeFloor = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeFloor);
            continue;
        }

        if (token.Equals("--include-underfront", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeUnderFront = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeUnderFront);
            continue;
        }

        if (token.Equals("--include-overfront", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeOverFront = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeOverFront);
            continue;
        }

        if (token.Equals("--include-dynscene", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeDynamicScene = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeDynamicScene);
            continue;
        }

        if (token.Equals("--overlay-map-id", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--overlay-map-id 需要一个非空值。");
                return 2;
            }

            opts.DynamicOverlayMapIdOverride = value;
            continue;
        }

        if (token.Equals("--overlay-layout", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--overlay-layout 需要一个布局 JSON 路径。");
                return 2;
            }

            opts.DynamicOverlayLayoutPath = value;
            continue;
        }

        if ((token.Equals("--effects-map-id", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--attached-effects-map-id", StringComparison.OrdinalIgnoreCase))
            && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--effects-map-id 需要一个非空值。");
                return 2;
            }

            opts.AttachedEffectsMapIdOverride = value;
            continue;
        }

        if ((token.Equals("--effects-layout", StringComparison.OrdinalIgnoreCase)
                || token.Equals("--attached-effects-layout", StringComparison.OrdinalIgnoreCase))
            && remaining.Count > 0)
        {
            string value = remaining.Dequeue();
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("参数错误：--effects-layout 需要一个布局 JSON 路径。");
                return 2;
            }

            opts.AttachedEffectsLayoutPath = value;
            continue;
        }

        if (token.Equals("--include-effects", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeAttachedEffects = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeAttachedEffects);
            continue;
        }

        if (token.Equals("--overlay-max-decompressed-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (long.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                opts.DynamicOverlayMaxDecompressedBytes = Math.Clamp(parsed, 0, 2L * 1024 * 1024 * 1024);
            }
            continue;
        }

        if (token.Equals("--suppress-border", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.SuppressBorderCells = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.SuppressBorderCells);
            continue;
        }

        if (token.Equals("--apply-tints", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyCellTints = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyCellTints);
            continue;
        }

        if (token.Equals("--tint-strength", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.TintStrength = Math.Clamp(parsed, 0.0f, 1.0f);
            }
            continue;
        }

        if (token.Equals("--apply-height-flag", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyCellHeightFlag = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyCellHeightFlag);
            continue;
        }

        if (token.Equals("--cell-height-offset", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.CellHeightFlagOffset = Math.Max(0.0f, parsed);
            }
            continue;
        }

        if (token.Equals("--apply-object-height", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyObjectHeight = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyObjectHeight);
            continue;
        }

        if (token.Equals("--object-height-scale", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.ObjectHeightScale = Math.Max(0.0f, parsed);
            }
            continue;
        }

        if (token.Equals("--apply-lighting", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.ApplyLightingOverlay = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.ApplyLightingOverlay);
            continue;
        }

        if (token.Equals("--overlay-max-alpha", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                opts.LightingOverlayMaxAlpha = Math.Clamp(parsed, 0, 255);
            }
            continue;
        }

        if (token.Equals("--include-light-sprites", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            opts.IncludeLightSprites = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.IncludeLightSprites);
            continue;
        }

        if (token.Equals("--night-factor", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (float.TryParse(remaining.Dequeue(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && float.IsFinite(parsed))
            {
                opts.NightFactor = Math.Clamp(parsed, 0.0f, 1.0f);
            }
            continue;
        }

        Console.Error.WriteLine($"参数错误：未知参数 {token}");
        return 2;
    }

    if (!IsPowerOfTwo(opts.ScaleDivisor) || opts.ScaleDivisor <= 0)
    {
        Console.Error.WriteLine("参数错误：--scale 必须为 2 的幂（1/2/4/8/16/32）。");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(mapRoot) || !Directory.Exists(mapRoot))
    {
        Console.Error.WriteLine($"地图目录不存在：{mapRoot}");
        return 2;
    }

    if (string.IsNullOrWhiteSpace(outputDir))
    {
        Console.Error.WriteLine("输出目录为空。");
        return 2;
    }

    if (useTextures)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            Console.Error.WriteLine("缺少参数：--data-root <贴图库目录>");
            return 2;
        }

        if (!Directory.Exists(dataRoot))
        {
            Console.Error.WriteLine($"贴图库目录不存在：{dataRoot}");
            return 2;
        }
    }

    List<string> files = CollectMapFiles(mapRoot, recursiveMaps, out string listError);
    if (!string.IsNullOrWhiteSpace(listError))
    {
        Console.Error.WriteLine(listError);
        return 1;
    }

    if (limit > 0 && files.Count > limit)
    {
        files = files.Take(limit).ToList();
    }

    if (files.Count == 0)
    {
        Console.Error.WriteLine($"未在目录中发现 .nmp/.mmp：{mapRoot}");
        return 1;
    }

    try
    {
        Directory.CreateDirectory(outputDir);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"无法创建输出目录：{ex.Message}");
        return 1;
    }

    using var textures = new MapTextureIndex();
    if (useTextures)
    {
        TextureScanResult scan = MapTextureIndex.ScanSglDirectory(dataRoot, recursiveTextures);
        if (!scan.Ok)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(scan.Error) ? "贴图库扫描失败。" : scan.Error);
            return 1;
        }

        textures.ApplyIndex(
            scan.RootDirectory,
            scan.PackageToStandaloneSglPath,
            scan.PackageToWpfSglSource,
            scan.PackageToWpfTex);
    }

    int ok = 0;
    int failed = 0;
    int skipped = 0;
    var results = new List<Dictionary<string, object?>>(capacity: files.Count);

    for (int i = 0; i < files.Count; i++)
    {
        string mapPath = files[i];

        string rel;
        try
        {
            rel = Path.GetRelativePath(mapRoot, mapPath);
        }
        catch
        {
            rel = mapPath;
        }

        string outFile = BuildBatchMinimapOutputPath(outputDir, rel, opts.ScaleDivisor, includeScaleTag);

        string? outDirForFile = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(outDirForFile))
        {
            try
            {
                Directory.CreateDirectory(outDirForFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                Console.Error.WriteLine($"[{i + 1}/{files.Count}] {rel}: 无法创建输出目录：{ex.Message}");
                continue;
            }
        }

        if (!overwrite && OutputAlreadyExists(outFile, opts))
        {
            skipped++;
            Console.Error.WriteLine($"[{i + 1}/{files.Count}] {rel}: 跳过（已存在）");
            results.Add(new Dictionary<string, object?>
            {
                ["mapPath"] = mapPath,
                ["relativeMapPath"] = rel,
                ["outputPath"] = outFile,
                ["status"] = "skipped",
            });
            continue;
        }

        if (!MapDocument.TryLoad(mapPath, out MapDocument? map, out string loadError) || map is null)
        {
            failed++;
            Console.Error.WriteLine($"[{i + 1}/{files.Count}] {rel}: 加载失败：{loadError}");
            results.Add(new Dictionary<string, object?>
            {
                ["mapPath"] = mapPath,
                ["relativeMapPath"] = rel,
                ["outputPath"] = outFile,
                ["status"] = "failed",
                ["error"] = loadError,
            });
            continue;
        }

        bool exportOk;
        string exportError;
        string[] writtenFiles = Array.Empty<string>();
        MinimapExportDiagnostics diag = default;

        if (useTextures)
        {
            exportOk = MinimapExporter.TryExportTexturedPng(map, textures, opts, outFile, out writtenFiles, out diag, out exportError);
        }
        else
        {
            exportOk = MinimapExporter.TryExportPlaceholderPng(map, opts, outFile, out writtenFiles, out exportError);
        }

        if (!exportOk)
        {
            failed++;
            Console.Error.WriteLine($"[{i + 1}/{files.Count}] {rel}: 导出失败：{exportError}");
            results.Add(new Dictionary<string, object?>
            {
                ["mapPath"] = mapPath,
                ["relativeMapPath"] = rel,
                ["outputPath"] = outFile,
                ["status"] = "failed",
                ["error"] = exportError,
                ["diagnostics"] = diag,
            });
            continue;
        }

        ok++;
        Console.Error.WriteLine($"[{i + 1}/{files.Count}] {rel}: OK");

        foreach (string file in writtenFiles)
        {
            Console.WriteLine(file);
        }

        if (useTextures)
        {
            string warn = diag.BuildWarningSummary();
            if (!string.IsNullOrWhiteSpace(warn))
            {
                Console.Error.WriteLine($"[{i + 1}/{files.Count}] {rel}: 警告：{warn}");
            }
        }

        results.Add(new Dictionary<string, object?>
        {
            ["mapPath"] = mapPath,
            ["relativeMapPath"] = rel,
            ["outputPath"] = outFile,
            ["status"] = "ok",
            ["writtenFiles"] = writtenFiles,
            ["diagnostics"] = diag,
        });
    }

    Console.Error.WriteLine($"批量导出完成：total={files.Count} ok={ok} failed={failed} skipped={skipped} outDir={outputDir}");

    if (!string.IsNullOrWhiteSpace(diagJsonPath))
    {
        var payload = new Dictionary<string, object?>
        {
            ["mapRoot"] = mapRoot,
            ["outputDir"] = outputDir,
            ["dataRoot"] = dataRoot,
            ["recursiveTextures"] = recursiveTextures,
            ["recursiveMaps"] = recursiveMaps,
            ["useTextures"] = useTextures,
            ["overwrite"] = overwrite,
            ["includeScaleTag"] = includeScaleTag,
            ["limit"] = limit,
            ["options"] = opts,
            ["total"] = files.Count,
            ["ok"] = ok,
            ["failed"] = failed,
            ["skipped"] = skipped,
            ["results"] = results,
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        string json = JsonSerializer.Serialize(payload, jsonOptions);

        try
        {
            string? dir = Path.GetDirectoryName(diagJsonPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(diagJsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"写出诊断 JSON 失败：{diagJsonPath} ({ex.Message})");
            return 1;
        }
    }

    return failed > 0 ? 1 : 0;
}

static int RunHeadlessResourceValidation(string[] args)
{
    // Usage:
    //   MapEditor --validate-resources <mapPath> --data-root <dir> [options...]
    if (args.Length < 2)
    {
        PrintUsage();
        return 2;
    }

    string mapPath = args[1];

    string dataRoot = string.Empty;
    bool recursive = true;
    bool validateCoast = true;
    int maxSamplesPerIssue = 8;
    string outJsonPath = string.Empty;

    var remaining = new Queue<string>(args[2..]);
    while (remaining.Count > 0)
    {
        string token = remaining.Dequeue();

        if (token.Equals("--help", StringComparison.OrdinalIgnoreCase)
            || token.Equals("-h", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/?", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }

        if (token.Equals("--data-root", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            dataRoot = remaining.Dequeue();
            continue;
        }

        if (token.Equals("--recursive", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            recursive = ParseBoolLike(remaining.Dequeue(), defaultValue: recursive);
            continue;
        }

        if (token.Equals("--validate-coast", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            validateCoast = ParseBoolLike(remaining.Dequeue(), defaultValue: validateCoast);
            continue;
        }

        if (token.Equals("--max-samples", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            if (int.TryParse(remaining.Dequeue(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                maxSamplesPerIssue = Math.Clamp(parsed, 0, 256);
            }
            continue;
        }

        if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
        {
            outJsonPath = remaining.Dequeue();
            continue;
        }
    }

    if (string.IsNullOrWhiteSpace(dataRoot))
    {
        Console.Error.WriteLine("缺少参数：--data-root <贴图库目录>");
        return 2;
    }

    if (!Directory.Exists(dataRoot))
    {
        Console.Error.WriteLine($"贴图库目录不存在：{dataRoot}");
        return 2;
    }

    if (!MapDocument.TryLoad(mapPath, out MapDocument? map, out string loadError) || map is null)
    {
        Console.Error.WriteLine(string.IsNullOrWhiteSpace(loadError) ? "加载地图失败。" : loadError);
        return 1;
    }

    using var textures = new MapTextureIndex();

    TextureScanResult scan = MapTextureIndex.ScanSglDirectory(dataRoot, recursive);
    if (!scan.Ok)
    {
        Console.Error.WriteLine(string.IsNullOrWhiteSpace(scan.Error) ? "贴图库扫描失败。" : scan.Error);
        return 1;
    }

    textures.ApplyIndex(
        scan.RootDirectory,
        scan.PackageToStandaloneSglPath,
        scan.PackageToWpfSglSource,
        scan.PackageToWpfTex);

    var options = new MapResourceValidationOptions
    {
        ValidateCoastComposite = validateCoast,
        MaxSamplesPerIssue = maxSamplesPerIssue,
    };

    MapResourceValidationReport report = MapResourceValidator.Validate(map, textures, options, CancellationToken.None);

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    string json = JsonSerializer.Serialize(report, jsonOptions);

    Console.Error.WriteLine($"资源引用验证：uniqueImageRefs={report.UniqueImageRefs} uniqueCoastRefs={report.UniqueCoastCompositeRefs} issues={report.Issues.Count}");

    if (string.IsNullOrWhiteSpace(outJsonPath))
    {
        Console.WriteLine(json);
    }
    else
    {
        try
        {
            string? dir = Path.GetDirectoryName(outJsonPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(outJsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine(outJsonPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"写出 JSON 失败：{outJsonPath} ({ex.Message})");
            return 1;
        }
    }

    return report.Issues.Count > 0 ? 1 : 0;
}

static void ApplyParityDefaults(MinimapExportOptions opts)
{
    if (opts is null)
    {
        return;
    }

    opts.IncludeBack = true;
    opts.IncludeMiddle = true;
    opts.IncludeFloor = true;
    opts.IncludeUnderFront = true;
    opts.IncludeFront = true;
    opts.IncludeOverFront = true;

    opts.SuppressBorderCells = true;
    opts.ApplyCellTints = true;
    opts.TintStrength = 0.35f;

    opts.ApplyCellHeightFlag = true;
    opts.CellHeightFlagOffset = 8.0f;

    opts.ApplyObjectHeight = true;
    opts.ObjectHeightScale = 1.0f;

    opts.ApplyLuminanceToAlpha = true;
    opts.ApplyLightingOverlay = true;
    opts.LightingOverlayMaxAlpha = 120;
    opts.IncludeLightSprites = true;
}

static string[] NormalizeLegacyArgs(string[] args)
{
    if (TryNormalizeLegacyMinimapExportArgs(args, out string[] normalized))
    {
        return normalized;
    }

    return args;
}

static bool TryNormalizeLegacyMinimapExportArgs(string[] args, out string[] normalized)
{
    normalized = args;
    if (args.Length < 4 || !args[0].Equals("--minimap-export", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (args.Skip(3).Any(static token => token.Equals("--data-root", StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    if ((args[1].Length > 0 && args[1][0] == '-')
        || (args[2].Length > 0 && args[2][0] == '-')
        || (args[3].Length > 0 && args[3][0] == '-'))
    {
        return false;
    }

    var rewritten = new List<string>(args.Length + 1)
    {
        "--minimap-export",
        args[1],
        args[3],
        "--data-root",
        args[2],
    };

    if (args.Length > 4)
    {
        rewritten.AddRange(args[4..]);
    }

    normalized = rewritten.ToArray();
    return true;
}

static void PrintUsage()
{
    Console.Error.WriteLine("用法：");
    Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.MapEditor -c Debug -- --minimap-export <mapPath> <out.png> --data-root <dir> [--recursive 1|0] [--use-textures 1|0]");
    Console.Error.WriteLine("    兼容旧调用顺序：--minimap-export <mapPath> <dataRoot> <out.png> [options]");
    Console.Error.WriteLine("    可选：--parity 1（启用旧工程常用默认值：边界抑制/色调/高度/灯光等）");
    Console.Error.WriteLine("    常用：--scale 1|2|4|8|16|32  --separate 1|0  --night-factor 0..1");
    Console.Error.WriteLine("    裁剪：--crop-mode none|cell|pixel|auto  --crop-cell x y w h  --crop-pixel x y w h  --auto-crop-padding N");
    Console.Error.WriteLine("    层：--include-back/--include-middle/--include-front/--include-floor/--include-underfront/--include-overfront 1|0");
    Console.Error.WriteLine("    诊断：--diag-json <out.json>（记录 options/diagnostics/writtenFiles，便于对照定位）");
    Console.Error.WriteLine("    叠加：--include-dynscene 1|0  --include-effects 1|0  --overlay-map-id <ID>  --effects-map-id <ID>");
    Console.Error.WriteLine("         --overlay-layout <layout.json>  --effects-layout <layout.json>  --overlay-max-decompressed-bytes N（0=不限制）");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.MapEditor -c Debug -- --minimap-export-batch <mapRoot> <outDir> --data-root <dir> [--recursive-maps 1|0] [--limit N] [--overwrite 1|0] [--include-scale-tag 1|0]");
    Console.Error.WriteLine("    诊断：--diag-json <out.json>（批量汇总每张地图的 OK/FAILED/SKIPPED 与 diagnostics）");
    Console.Error.WriteLine("    其余可选参数同 --minimap-export（--scale/--separate/--include-*/--include-dynscene/--include-effects/--parity 等）；--recursive 仍用于贴图库扫描。");
    Console.Error.WriteLine("");
    Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.MapEditor -c Debug -- --validate-resources <mapPath> --data-root <dir> [--recursive 1|0] [--validate-coast 1|0] [--max-samples N] [--json out.json]");
}

static bool ParseBoolLike(string value, bool defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    string v = value.Trim();
    if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return defaultValue;
}

static bool TryParseCropMode(string value, out MinimapCropMode cropMode)
{
    cropMode = MinimapCropMode.None;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    string v = value.Trim();
    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int modeNumber))
    {
        cropMode = modeNumber switch
        {
            0 => MinimapCropMode.None,
            1 => MinimapCropMode.CellRect,
            2 => MinimapCropMode.PixelRect,
            3 => MinimapCropMode.AutoNonEmptyCells,
            _ => MinimapCropMode.None,
        };

        return modeNumber is >= 0 and <= 3;
    }

    v = v.ToLowerInvariant();
    if (v is "none" or "off")
    {
        cropMode = MinimapCropMode.None;
        return true;
    }

    if (v is "cell" or "cells" or "cellrect")
    {
        cropMode = MinimapCropMode.CellRect;
        return true;
    }

    if (v is "pixel" or "pixels" or "pixelrect")
    {
        cropMode = MinimapCropMode.PixelRect;
        return true;
    }

    if (v is "auto" or "autononempty" or "autononemptycells")
    {
        cropMode = MinimapCropMode.AutoNonEmptyCells;
        return true;
    }

    return false;
}

static bool IsPowerOfTwo(int value)
{
    return value > 0 && (value & (value - 1)) == 0;
}

static List<string> CollectMapFiles(string inputDir, bool recursive, out string error)
{
    error = string.Empty;

    if (string.IsNullOrWhiteSpace(inputDir))
    {
        error = "输入目录为空。";
        return new List<string>();
    }

    try
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(inputDir, "*.*", option)
            .Where(static path => path.EndsWith(".nmp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return files;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        error = $"枚举地图文件失败：{ex.Message}";
        return new List<string>();
    }
}

static string BuildBatchMinimapOutputPath(string outputDir, string relativeMapPath, int scaleDivisor, bool includeScaleTag)
{
    string dir;
    try
    {
        dir = Path.GetDirectoryName(relativeMapPath) ?? string.Empty;
    }
    catch
    {
        dir = string.Empty;
    }

    string stem;
    try
    {
        stem = Path.GetFileNameWithoutExtension(relativeMapPath);
    }
    catch
    {
        stem = "map";
    }

    if (string.IsNullOrWhiteSpace(stem))
    {
        stem = "map";
    }

    scaleDivisor = Math.Clamp(scaleDivisor, 1, 32);
    string file = $"{stem}_minimap{(includeScaleTag ? $"_s{scaleDivisor}" : string.Empty)}.png";

    return string.IsNullOrWhiteSpace(dir)
        ? Path.Combine(outputDir, file)
        : Path.Combine(outputDir, dir, file);
}

static bool OutputAlreadyExists(string outputPath, MinimapExportOptions opts)
{
    string normalized = outputPath;
    try
    {
        normalized = Path.GetFullPath(outputPath);
    }
    catch
    {
        // ignored
    }

    if (!opts.SeparateLayerFiles)
    {
        return File.Exists(normalized);
    }

    string basePath = Path.ChangeExtension(normalized, null) ?? normalized;
    if (opts.IncludeBack && File.Exists($"{basePath}_back.png")) return true;
    if (opts.IncludeMiddle && File.Exists($"{basePath}_middle.png")) return true;
    if (opts.IncludeFloor && File.Exists($"{basePath}_floor.png")) return true;
    if (opts.IncludeUnderFront && File.Exists($"{basePath}_underfront.png")) return true;
    if (opts.IncludeFront && File.Exists($"{basePath}_front.png")) return true;
    if (opts.IncludeOverFront && File.Exists($"{basePath}_overfront.png")) return true;
    return false;
}
