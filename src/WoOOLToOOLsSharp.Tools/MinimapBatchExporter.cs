using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;

namespace WoOOLToOOLsSharp.Tools;

internal static class MinimapBatchExporter
{
    private const int MinimapCellW = 64;
    private const int MinimapCellH = 32;

    public static int RunExport(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("缺少参数：minimap export <inputPath> <outputPath>");
            return 2;
        }

        string inputPath = args[0];
        string outputPath = args[1];

        var opts = new MinimapBatchExportOptions();

        var remaining = new Queue<string>(args.Skip(2));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();

            if (token.Equals("--recursive", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                opts.Recursive = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.Recursive);
                continue;
            }

            if (token.Equals("--scale", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                if (int.TryParse(remaining.Dequeue(), out int scale))
                {
                    opts.ScaleDivisor = scale;
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

            if (token.Equals("--separate", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                opts.SeparateLayerFiles = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.SeparateLayerFiles);
                continue;
            }

            if (token.Equals("--overwrite", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                opts.Overwrite = ParseBoolLike(remaining.Dequeue(), defaultValue: opts.Overwrite);
                continue;
            }
        }

        if (!IsPowerOfTwo(opts.ScaleDivisor) || opts.ScaleDivisor <= 0)
        {
            Console.Error.WriteLine("参数错误：--scale 必须为 2 的幂（1/2/4/8/16/32）。");
            return 2;
        }

        if (!opts.IncludeBack && !opts.IncludeMiddle && !opts.IncludeFront)
        {
            Console.Error.WriteLine("参数错误：未选择任何导出层（请至少启用一个 include 开关）。");
            return 2;
        }

        if (File.Exists(inputPath))
        {
            bool outputIsDirectory = Directory.Exists(outputPath);
            if (!outputIsDirectory)
            {
                try
                {
                    outputIsDirectory = !Path.HasExtension(outputPath);
                }
                catch
                {
                    outputIsDirectory = false;
                }
            }

            if (outputIsDirectory)
            {
                Directory.CreateDirectory(outputPath);
                string rel = Path.GetFileName(inputPath);
                string outFile = BuildOutputPathForMapFile(outputPath, rel, opts.ScaleDivisor, suffix: null);
                return ExportSingleFile(inputPath, outFile, opts) ? 0 : 1;
            }

            return ExportSingleFile(inputPath, outputPath, opts) ? 0 : 1;
        }

        if (!Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"输入路径不存在：{inputPath}");
            return 1;
        }

        if (File.Exists(outputPath))
        {
            Console.Error.WriteLine("输出路径是文件，但输入路径是目录：请将 outputPath 设为目录。");
            return 2;
        }

        Directory.CreateDirectory(outputPath);

        SearchOption searchOption = opts.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        List<string> mapFiles = Directory.EnumerateFiles(inputPath, "*.*", searchOption)
            .Where(static p => HasMapExtension(p))
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mapFiles.Count == 0)
        {
            Console.Error.WriteLine($"未在目录中发现 .nmp/.mmp：{inputPath}");
            return 1;
        }

        int ok = 0;
        int failed = 0;

        for (int i = 0; i < mapFiles.Count; i++)
        {
            string file = mapFiles[i];
            string rel;
            try
            {
                rel = Path.GetRelativePath(inputPath, file);
            }
            catch
            {
                rel = Path.GetFileName(file);
            }

            string outFile = BuildOutputPathForMapFile(outputPath, rel, opts.ScaleDivisor, suffix: null);

            if (ExportSingleFile(file, outFile, opts))
            {
                ok++;
            }
            else
            {
                failed++;
            }
        }

        Console.WriteLine($"minimap export: total={mapFiles.Count} ok={ok} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static bool ExportSingleFile(string inputFile, string outputPath, MinimapBatchExportOptions opts)
    {
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"文件不存在：{inputFile}");
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(inputFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{inputFile}: 读取失败：{ex.Message}");
            return false;
        }

        if (!NmpCodec.TryReadMapFromMemory(bytes, inputFile, out NmpMapInfo info, out NmpCellData[] cells, out string error))
        {
            Console.Error.WriteLine($"{inputFile}: 解析失败：{error}");
            return false;
        }

        if (!opts.SeparateLayerFiles)
        {
            string outFile = EnsurePngExtension(outputPath);
            if (!opts.Overwrite && File.Exists(outFile))
            {
                Console.Error.WriteLine($"{inputFile}: 已存在，跳过：{outFile}");
                return true;
            }

            if (!TryRenderPlaceholderToRgba(info, cells, opts, out byte[] rgba, out int w, out int h, out error))
            {
                Console.Error.WriteLine($"{inputFile}: 渲染失败：{error}");
                return false;
            }

            if (!PngWriter.TryWriteRgba8(outFile, w, h, rgba, out error))
            {
                Console.Error.WriteLine($"{inputFile}: 写 PNG 失败：{error}");
                return false;
            }

            return true;
        }

        string basePath = Path.ChangeExtension(EnsurePngExtension(outputPath), null) ?? outputPath;

        bool ok = true;

        if (opts.IncludeBack)
        {
            ok &= ExportSingleLayer(info, cells, opts, basePath, "back");
        }

        if (opts.IncludeMiddle)
        {
            ok &= ExportSingleLayer(info, cells, opts, basePath, "middle");
        }

        if (opts.IncludeFront)
        {
            ok &= ExportSingleLayer(info, cells, opts, basePath, "front");
        }

        return ok;
    }

    private static bool ExportSingleLayer(NmpMapInfo info, NmpCellData[] cells, MinimapBatchExportOptions opts, string basePath, string layerName)
    {
        bool includeBack = layerName == "back";
        bool includeMiddle = layerName == "middle";
        bool includeFront = layerName == "front";

        var layerOpts = opts.CloneWithLayers(includeBack, includeMiddle, includeFront);

        string outFile = EnsurePngExtension($"{basePath}_{layerName}.png");
        if (!layerOpts.Overwrite && File.Exists(outFile))
        {
            return true;
        }

        if (!TryRenderPlaceholderToRgba(info, cells, layerOpts, out byte[] rgba, out int w, out int h, out string error))
        {
            Console.Error.WriteLine($"渲染失败：{outFile}: {error}");
            return false;
        }

        if (!PngWriter.TryWriteRgba8(outFile, w, h, rgba, out error))
        {
            Console.Error.WriteLine($"写 PNG 失败：{outFile}: {error}");
            return false;
        }

        return true;
    }

    private static string BuildOutputPathForMapFile(string outputDir, string relativeInputPath, int scaleDivisor, string? suffix)
    {
        string stem = relativeInputPath;
        try
        {
            stem = Path.ChangeExtension(relativeInputPath, null) ?? relativeInputPath;
        }
        catch
        {
            stem = relativeInputPath;
        }

        string dir = Path.GetDirectoryName(stem) ?? string.Empty;
        string file = Path.GetFileName(stem);

        string tag = $"_s{scaleDivisor}";
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            tag += $"_{suffix}";
        }

        string outName = $"{file}{tag}.png";
        return Path.Combine(outputDir, dir, outName);
    }

    private static bool TryRenderPlaceholderToRgba(
        NmpMapInfo info,
        NmpCellData[] cells,
        MinimapBatchExportOptions opts,
        out byte[] rgba,
        out int width,
        out int height,
        out string error)
    {
        rgba = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (info is null)
        {
            error = "地图信息为空。";
            return false;
        }

        if (cells is null)
        {
            error = "地图数据为空。";
            return false;
        }

        if (info.Width <= 0 || info.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        width = checked(info.Width * cellPxW);
        height = checked(info.Height * cellPxH);

        long bytesNeeded = (long)width * height * 4;
        if (bytesNeeded <= 0)
        {
            error = "导出图像尺寸无效。";
            return false;
        }

        if (bytesNeeded > opts.MaxUncompressedBytes)
        {
            error = $"导出图像过大（RGBA {bytesNeeded / (1024 * 1024)} MB），请调大 ScaleDivisor（例如 8/16/32）。";
            return false;
        }

        rgba = new byte[bytesNeeded];

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFront = opts.IncludeFront;

        int mapWidth = info.Width;

        for (int y = 0; y < info.Height; y++)
        {
            int mapRow = y * mapWidth;
            int py0 = y * cellPxH;

            for (int x = 0; x < mapWidth; x++)
            {
                int index = mapRow + x;
                if ((uint)index >= (uint)cells.Length)
                {
                    continue;
                }

                NmpCellData cell = cells[index];
                uint key = ResolveCellKey(cell, includeBack, includeMiddle, includeFront);
                (byte r, byte g, byte b, byte a) = key == 0
                    ? ((byte)18, (byte)18, (byte)20, (byte)255)
                    : HashToColor(key, alpha: 255);

                int px0 = x * cellPxW;

                for (int dy = 0; dy < cellPxH; dy++)
                {
                    int py = py0 + dy;
                    int rowOffset = (py * width + px0) * 4;
                    for (int dx = 0; dx < cellPxW; dx++)
                    {
                        int p = rowOffset + dx * 4;
                        rgba[p + 0] = r;
                        rgba[p + 1] = g;
                        rgba[p + 2] = b;
                        rgba[p + 3] = a;
                    }
                }
            }
        }

        return true;
    }

    private static uint ResolveCellKey(NmpCellData cell, bool includeBack, bool includeMiddle, bool includeFront)
    {
        if (includeFront)
        {
            uint front = cell.FrontImage & 0x00FFFFFFu;
            if (front != 0)
            {
                return front;
            }
        }

        if (includeMiddle && cell.MiddleImage != 0)
        {
            return ((uint)cell.MiddleLibrary << 16) | cell.MiddleImage;
        }

        if (includeBack && cell.BackImage != 0)
        {
            return ((uint)cell.BackLibrary << 16) | cell.BackImage;
        }

        return 0;
    }

    private static (byte r, byte g, byte b, byte a) HashToColor(uint key, byte alpha)
    {
        uint x = key;
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;

        byte r = (byte)(40 + (x & 0x7F));
        byte g = (byte)(40 + ((x >> 8) & 0x7F));
        byte b = (byte)(40 + ((x >> 16) & 0x7F));
        return (r, g, b, alpha);
    }

    private static bool IsPowerOfTwo(int x)
    {
        return x > 0 && (x & (x - 1)) == 0;
    }

    private static bool HasMapExtension(string path)
    {
        string ext;
        try
        {
            ext = Path.GetExtension(path);
        }
        catch
        {
            return false;
        }

        return ext.Equals(".nmp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsurePngExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "minimap.png";
        }

        string trimmed = path.Trim();
        string ext;
        try
        {
            ext = Path.GetExtension(trimmed);
        }
        catch
        {
            return trimmed + ".png";
        }

        if (string.IsNullOrWhiteSpace(ext))
        {
            return trimmed + ".png";
        }

        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return Path.ChangeExtension(trimmed, ".png");
        }

        return trimmed;
    }

    private static bool ParseBoolLike(string s, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return defaultValue;
        }

        string v = s.Trim().ToLowerInvariant();
        return v switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => defaultValue,
        };
    }

    private sealed class MinimapBatchExportOptions
    {
        public int ScaleDivisor { get; set; } = 4;

        public bool IncludeBack { get; set; } = true;
        public bool IncludeMiddle { get; set; } = true;
        public bool IncludeFront { get; set; } = true;

        public bool SeparateLayerFiles { get; set; }

        public bool Recursive { get; set; }
        public bool Overwrite { get; set; }

        public long MaxUncompressedBytes { get; set; } = 512L * 1024L * 1024L;

        public MinimapBatchExportOptions CloneWithLayers(bool includeBack, bool includeMiddle, bool includeFront)
        {
            return new MinimapBatchExportOptions
            {
                ScaleDivisor = ScaleDivisor,
                IncludeBack = includeBack,
                IncludeMiddle = includeMiddle,
                IncludeFront = includeFront,
                SeparateLayerFiles = SeparateLayerFiles,
                Recursive = Recursive,
                Overwrite = Overwrite,
                MaxUncompressedBytes = MaxUncompressedBytes,
            };
        }
    }
}
