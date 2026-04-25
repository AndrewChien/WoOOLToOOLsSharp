using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.MapEditor.App;

public enum MinimapCropMode
{
    None = 0,
    CellRect = 1,
    PixelRect = 2,
    AutoNonEmptyCells = 3,
}

public sealed class MinimapExportOptions
{
    /// <summary>
    /// 输出缩放（像素除数），必须为 1/2/4/8/16/32 这类 2 的幂；值越大，导出越小。
    /// </summary>
    public int ScaleDivisor { get; set; } = 4;

    public bool IncludeBack { get; set; } = true;
    public bool IncludeMiddle { get; set; } = true;
    public bool IncludeFloor { get; set; }
    public bool IncludeUnderFront { get; set; }
    public bool IncludeFront { get; set; } = true;
    public bool IncludeOverFront { get; set; }

    /// <summary>
    /// 动态覆盖（DynScene.dat）与挂接 effects：实验性支持。
    /// 仅当数据文件能被 <see cref="DynamicOverlayCodec"/> 解析时才会叠加到导出结果（文本/JSON，或二进制 + sidecar layout）。
    /// 缺失或解析失败会输出警告并忽略，不影响基础导出。
    /// </summary>
    public bool IncludeDynamicScene { get; set; }
    public bool IncludeAttachedEffects { get; set; }

    /// <summary>
    /// （可选）覆盖 DynScene 的 mapId 推导：当真实数据的 DynScene/&lt;MAPID&gt;.dat 使用的 mapId 不是地图文件名时，用此值来推导 mapId。
    /// 仅影响 DynScene 的候选路径探测，不影响挂接 effects（AttachedEffects）；挂接 effects 如需覆盖请用 <see cref="AttachedEffectsMapIdOverride"/>。
    /// </summary>
    public string DynamicOverlayMapIdOverride { get; set; } = string.Empty;

    /// <summary>
    /// （可选）DynScene 的二进制布局文件（JSON）。
    /// 当 DynScene 数据为“二进制记录 + layout”时，可通过该参数直接指定布局文件路径，避免需要在原始数据文件旁边放置 <c>.layout.json</c> sidecar。
    /// </summary>
    public string DynamicOverlayLayoutPath { get; set; } = string.Empty;

    /// <summary>
    /// （可选）覆盖挂接 effects 的 mapId 推导：当真实数据的 AttachedEffects/&lt;MAPID&gt;.dat 使用的 mapId 不是地图文件名时，用此值来推导 mapId。
    /// 仅影响挂接 effects 的候选路径探测，不影响 DynScene（DynScene 通过 <see cref="DynamicOverlayMapIdOverride"/> 覆盖）。
    /// </summary>
    public string AttachedEffectsMapIdOverride { get; set; } = string.Empty;

    /// <summary>
    /// （可选）挂接 effects 的二进制布局文件（JSON）。
    /// 当挂接 effects 数据为“二进制记录 + layout”时，可通过该参数直接指定布局文件路径，避免需要在原始数据文件旁边放置 <c>.layout.json</c> sidecar。
    /// </summary>
    public string AttachedEffectsLayoutPath { get; set; } = string.Empty;

    /// <summary>
    /// 动态覆盖数据若为 gzip/zlib 等压缩格式，允许的最大解压后字节数；0 表示不设上限（可能占用大量内存）。
    /// </summary>
    public long DynamicOverlayMaxDecompressedBytes { get; set; } = DynamicOverlayCodec.DefaultMaxDecompressedBytes;

    public bool SeparateLayerFiles { get; set; }

    // ---- Client parity settings（逐步对齐旧工程）----

    public bool SuppressBorderCells { get; set; }

    public bool ApplyCellTints { get; set; }

    /// <summary>
    /// 0=不应用（白色），1=完全使用原始色调值。与旧工程一致，默认 0.35（仅在 ApplyCellTints=true 时生效）。
    /// </summary>
    public float TintStrength { get; set; } = 0.35f;

    public bool ApplyCellHeightFlag { get; set; }

    /// <summary>
    /// 当 flags 含 NEARGROUND(0x40) 时，对该格子整体做 Y 方向抬升（单位：未缩放像素；实际输出会再除以 ScaleDivisor）。
    /// </summary>
    public float CellHeightFlagOffset { get; set; } = 0.0f;

    public bool ApplyObjectHeight { get; set; }

    /// <summary>
    /// objectHeight → 像素抬升的缩放（单位：未缩放像素/单位；实际输出会再除以 ScaleDivisor）。
    /// </summary>
    public float ObjectHeightScale { get; set; } = 0.0f;

    /// <summary>
    /// 对 objects18/19（package 46/47）应用“亮度转 alpha”的混合规则（与旧工程一致）。
    /// </summary>
    public bool ApplyLuminanceToAlpha { get; set; } = true;

    public LuminanceSettings LuminanceSettings { get; set; } = new();

    public bool ApplyLightingOverlay { get; set; } = true;

    /// <summary>
    /// 夜晚系数（0=白天，1=完全夜晚）。用于模拟旧工程的 nightFactor。
    /// </summary>
    public float NightFactor { get; set; } = 0.0f;

    /// <summary>
    /// 光照叠加的最大 alpha（旧工程会从 SceneLightMaskConfig 中解析不同地图的上限；这里先暴露为参数）。
    /// </summary>
    public int LightingOverlayMaxAlpha { get; set; } = 120;

    /// <summary>
    /// 导出灯光 sprite（旧工程：Objects10(package 38) 的部分 overObject 范围）。
    /// </summary>
    public bool IncludeLightSprites { get; set; } = true;

    /// <summary>
    /// 为避免一次性分配过大的 RGBA buffer，超过该上限将拒绝导出并提示调大 Scale。
    /// </summary>
    public long MaxUncompressedBytes { get; set; } = 512L * 1024L * 1024L;

    // ---- Crop（可选裁剪范围）----

    /// <summary>
    /// 裁剪模式：None=不裁剪；CellRect=按格子矩形；PixelRect=按导出图像像素矩形；AutoNonEmptyCells=按“非空 cell 的最小包围盒”。
    /// </summary>
    public MinimapCropMode CropMode { get; set; } = MinimapCropMode.None;

    // CellRect：格子坐标（0-based）
    public int CropCellX { get; set; }
    public int CropCellY { get; set; }
    public int CropCellWidth { get; set; }
    public int CropCellHeight { get; set; }

    // PixelRect：导出图像像素坐标（0-based，基于当前 ScaleDivisor 的输出尺寸）
    public int CropPixelX { get; set; }
    public int CropPixelY { get; set; }
    public int CropPixelWidth { get; set; }
    public int CropPixelHeight { get; set; }

    /// <summary>
    /// AutoNonEmptyCells：在包围盒基础上额外扩展的边距（单位：cell）。
    /// </summary>
    public int AutoCropPaddingCells { get; set; }
}

public readonly record struct MinimapExportDiagnostics(
    int MissingTextures,
    bool SceneLightMaskConfigMissing,
    bool DynamicSceneRequested,
    string? DynamicSceneDataPath,
    int? DynamicSceneResolvedCandidateIndex,
    string? DynamicSceneResolvedCandidateLabel,
    int DynamicSceneCandidateCount,
    string? DynamicSceneWarning,
    bool AttachedEffectsRequested,
    string? AttachedEffectsDataPath,
    int? AttachedEffectsResolvedCandidateIndex,
    string? AttachedEffectsResolvedCandidateLabel,
    int AttachedEffectsCandidateCount,
    string? AttachedEffectsWarning)
{
    public bool HasWarnings => MissingTextures > 0
        || SceneLightMaskConfigMissing
        || !string.IsNullOrWhiteSpace(DynamicSceneWarning)
        || !string.IsNullOrWhiteSpace(AttachedEffectsWarning);

    public string BuildWarningSummary()
    {
        if (!HasWarnings)
        {
            return string.Empty;
        }

        var parts = new List<string>(capacity: 4);
        if (MissingTextures > 0)
        {
            parts.Add($"缺失贴图：{MissingTextures} 次");
        }

        if (SceneLightMaskConfigMissing)
        {
            parts.Add("SceneLightMaskCfg.xml 未找到，已跳过 scene light mask 叠加");
        }

        if (!string.IsNullOrWhiteSpace(DynamicSceneWarning))
        {
            parts.Add(DynamicSceneWarning);
        }

        if (!string.IsNullOrWhiteSpace(AttachedEffectsWarning))
        {
            parts.Add(AttachedEffectsWarning);
        }

        return string.Join("；", parts);
    }

    public string ToSummaryText()
    {
        return BuildWarningSummary();
    }
}

public static class MinimapExporter
{
    private const int MinimapCellW = 64;
    private const int MinimapCellH = 32;
    private const byte NmpFlagBorderCell = 0x04;
    private const byte NmpFlagNearGround = 0x40;
    private const int DefaultBackPackageId = 3001;
    private const int DefaultMiddlePackageId = 3051;
    private const int DefaultObjectPackageId = 5;
    private const int DefaultCoastPackageId = 49;

    private readonly record struct CropRect(int X, int Y, int Width, int Height);
    private sealed record OverlayRenderState(
        string DynamicSceneDataPath,
        int? DynamicSceneResolvedCandidateIndex,
        string? DynamicSceneResolvedCandidateLabel,
        int DynamicSceneCandidateCount,
        DynamicOverlayDocument? DynamicSceneDocument,
        string? DynamicSceneWarning,
        string AttachedEffectsDataPath,
        int? AttachedEffectsResolvedCandidateIndex,
        string? AttachedEffectsResolvedCandidateLabel,
        int AttachedEffectsCandidateCount,
        DynamicOverlayDocument? AttachedEffectsDocument,
        string? AttachedEffectsWarning);

    private static bool TryComputeCropRect(
        MapDocument map,
        MinimapExportOptions opts,
        int cellPxW,
        int cellPxH,
        out CropRect crop,
        out string error)
    {
        crop = default;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int fullW;
        int fullH;
        try
        {
            fullW = checked(map.Width * cellPxW);
            fullH = checked(map.Height * cellPxH);
        }
        catch (OverflowException)
        {
            error = "导出图像尺寸溢出。";
            return false;
        }

        if (fullW <= 0 || fullH <= 0)
        {
            error = "导出图像尺寸无效。";
            return false;
        }

        switch (opts.CropMode)
        {
            case MinimapCropMode.None:
                crop = new CropRect(0, 0, fullW, fullH);
                return true;

            case MinimapCropMode.CellRect:
            {
                int cellX = opts.CropCellX;
                int cellY = opts.CropCellY;
                int cellW = opts.CropCellWidth;
                int cellH = opts.CropCellHeight;

                if (cellW <= 0 || cellH <= 0)
                {
                    error = "裁剪参数错误：CropCellWidth/CropCellHeight 必须 > 0。";
                    return false;
                }

                if (cellX < 0 || cellY < 0)
                {
                    error = "裁剪参数错误：CropCellX/CropCellY 必须 >= 0。";
                    return false;
                }

                if (cellX >= map.Width || cellY >= map.Height)
                {
                    error = "裁剪参数错误：裁剪起点超出地图范围。";
                    return false;
                }

                int x0Cell = Math.Clamp(cellX, 0, map.Width);
                int y0Cell = Math.Clamp(cellY, 0, map.Height);
                int x1Cell = Math.Clamp(cellX + cellW, 0, map.Width);
                int y1Cell = Math.Clamp(cellY + cellH, 0, map.Height);

                if (x0Cell >= x1Cell || y0Cell >= y1Cell)
                {
                    error = "裁剪范围为空（超出地图边界后被裁剪为 0）。";
                    return false;
                }

                crop = new CropRect(
                    X: x0Cell * cellPxW,
                    Y: y0Cell * cellPxH,
                    Width: (x1Cell - x0Cell) * cellPxW,
                    Height: (y1Cell - y0Cell) * cellPxH);
                return true;
            }

            case MinimapCropMode.PixelRect:
            {
                int pxX = opts.CropPixelX;
                int pxY = opts.CropPixelY;
                int pxW = opts.CropPixelWidth;
                int pxH = opts.CropPixelHeight;

                if (pxW <= 0 || pxH <= 0)
                {
                    error = "裁剪参数错误：CropPixelWidth/CropPixelHeight 必须 > 0。";
                    return false;
                }

                if (pxX < 0 || pxY < 0)
                {
                    error = "裁剪参数错误：CropPixelX/CropPixelY 必须 >= 0。";
                    return false;
                }

                long x0 = Math.Clamp((long)pxX, 0, fullW);
                long y0 = Math.Clamp((long)pxY, 0, fullH);
                long x1 = Math.Clamp((long)pxX + pxW, 0, fullW);
                long y1 = Math.Clamp((long)pxY + pxH, 0, fullH);

                if (x0 >= x1 || y0 >= y1)
                {
                    error = "裁剪范围为空（超出地图边界后被裁剪为 0）。";
                    return false;
                }

                crop = new CropRect((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0));
                return true;
            }

            case MinimapCropMode.AutoNonEmptyCells:
            {
                if (!TryComputeNonEmptyCellBounds(map, opts, out int minX, out int minY, out int maxX, out int maxY))
                {
                    crop = new CropRect(0, 0, fullW, fullH);
                    return true;
                }

                int pad = Math.Max(0, opts.AutoCropPaddingCells);
                int x0Cell = Math.Max(0, minX - pad);
                int y0Cell = Math.Max(0, minY - pad);
                int x1Cell = Math.Min(map.Width, maxX + pad + 1);
                int y1Cell = Math.Min(map.Height, maxY + pad + 1);

                if (x0Cell >= x1Cell || y0Cell >= y1Cell)
                {
                    crop = new CropRect(0, 0, fullW, fullH);
                    return true;
                }

                crop = new CropRect(
                    X: x0Cell * cellPxW,
                    Y: y0Cell * cellPxH,
                    Width: (x1Cell - x0Cell) * cellPxW,
                    Height: (y1Cell - y0Cell) * cellPxH);
                return true;
            }

            default:
                crop = new CropRect(0, 0, fullW, fullH);
                return true;
        }
    }

    private static bool TryComputeNonEmptyCellBounds(
        MapDocument map,
        MinimapExportOptions opts,
        out int minX,
        out int minY,
        out int maxX,
        out int maxY)
    {
        minX = int.MaxValue;
        minY = int.MaxValue;
        maxX = -1;
        maxY = -1;

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        for (int y = 0; y < map.Height; y++)
        {
            int row = y * map.Width;
            for (int x = 0; x < map.Width; x++)
            {
                int index = row + x;
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cell = map.Cells[index];
                if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                {
                    continue;
                }

                uint key = ResolveCellKey(cell, includeBack, includeMiddle, includeFloor, includeUnderFront, includeFront, includeOverFront);
                if (key == 0)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        return maxX >= 0;
    }

    public static bool TryExportPlaceholderPng(
        MapDocument map,
        MinimapExportOptions opts,
        string outputPath,
        out string[] writtenFiles,
        out string error)
    {
        writtenFiles = Array.Empty<string>();
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (!TryNormalizeOutputPath(outputPath, out string normalized, out error))
        {
            return false;
        }

        if (!opts.SeparateLayerFiles)
        {
            if (!TryWritePlaceholderPng(map, opts, normalized, transparentBackground: false, out error))
            {
                return false;
            }

            writtenFiles = new[] { normalized };
            return true;
        }

        var files = new List<string>();

        string basePath = Path.ChangeExtension(normalized, null) ?? normalized;

        bool anyLayer = false;

        if (opts.IncludeBack)
        {
            anyLayer = true;
            if (!TryExportSingleLayer(map, opts, basePath, "back", includeBack: true, includeMiddle: false, includeFloor: false, includeUnderFront: false, includeFront: false, includeOverFront: false, out string file, out error))
            {
                return false;
            }
            files.Add(file);
        }

        if (opts.IncludeMiddle)
        {
            anyLayer = true;
            if (!TryExportSingleLayer(map, opts, basePath, "middle", includeBack: false, includeMiddle: true, includeFloor: false, includeUnderFront: false, includeFront: false, includeOverFront: false, out string file, out error))
            {
                return false;
            }
            files.Add(file);
        }

        if (opts.IncludeFloor)
        {
            anyLayer = true;
            if (!TryExportSingleLayer(map, opts, basePath, "floor", includeBack: false, includeMiddle: false, includeFloor: true, includeUnderFront: false, includeFront: false, includeOverFront: false, out string file, out error))
            {
                return false;
            }
            files.Add(file);
        }

        if (opts.IncludeUnderFront)
        {
            anyLayer = true;
            if (!TryExportSingleLayer(map, opts, basePath, "underfront", includeBack: false, includeMiddle: false, includeFloor: false, includeUnderFront: true, includeFront: false, includeOverFront: false, out string file, out error))
            {
                return false;
            }
            files.Add(file);
        }

        if (opts.IncludeFront)
        {
            anyLayer = true;
            if (!TryExportSingleLayer(map, opts, basePath, "front", includeBack: false, includeMiddle: false, includeFloor: false, includeUnderFront: false, includeFront: true, includeOverFront: false, out string file, out error))
            {
                return false;
            }
            files.Add(file);
        }

        if (opts.IncludeOverFront)
        {
            anyLayer = true;
            if (!TryExportSingleLayer(map, opts, basePath, "overfront", includeBack: false, includeMiddle: false, includeFloor: false, includeUnderFront: false, includeFront: false, includeOverFront: true, out string file, out error))
            {
                return false;
            }
            files.Add(file);
        }

        if (!anyLayer)
        {
            error = "未选择任何导出层。";
            return false;
        }

        writtenFiles = files.ToArray();
        return true;
    }

    public static bool TryExportTexturedPng(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        string outputPath,
        out string[] writtenFiles,
        out string error)
    {
        return TryExportTexturedPng(map, textures, opts, outputPath, out writtenFiles, out _, out error);
    }

    public static bool TryExportTexturedPng(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        string outputPath,
        out string[] writtenFiles,
        out MinimapExportDiagnostics diagnostics,
        out string error)
    {
        writtenFiles = Array.Empty<string>();
        diagnostics = default;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (textures is null)
        {
            error = "贴图索引为空。";
            return false;
        }

        if (!textures.IsReady)
        {
            error = "贴图库未就绪（请先扫描贴图库：SGL/WPF）。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (!TryNormalizeOutputPath(outputPath, out string normalized, out error))
        {
            return false;
        }

        if (!opts.SeparateLayerFiles)
        {
            if (!TryWriteTexturedPngStriped(map, textures, opts, normalized, transparentBackground: false, out diagnostics, out error))
            {
                return false;
            }

            writtenFiles = new[] { normalized };
            return true;
        }

        var files = new List<string>();
        string basePath = Path.ChangeExtension(normalized, null) ?? normalized;

        bool anyLayer = false;
        int missingTexturesTotal = 0;
        bool cfgMissing = false;

        if (opts.IncludeBack)
        {
            anyLayer = true;
            if (!TryExportSingleLayerTextured(map, textures, opts, basePath, "back", includeBack: true, includeMiddle: false, includeFloor: false, includeUnderFront: false, includeFront: false, includeOverFront: false, out string file, out MinimapExportDiagnostics diag, out error))
            {
                return false;
            }
            files.Add(file);
            missingTexturesTotal = (int)Math.Min(int.MaxValue, (long)missingTexturesTotal + diag.MissingTextures);
            cfgMissing |= diag.SceneLightMaskConfigMissing;
        }

        if (opts.IncludeMiddle)
        {
            anyLayer = true;
            if (!TryExportSingleLayerTextured(map, textures, opts, basePath, "middle", includeBack: false, includeMiddle: true, includeFloor: false, includeUnderFront: false, includeFront: false, includeOverFront: false, out string file, out MinimapExportDiagnostics diag, out error))
            {
                return false;
            }
            files.Add(file);
            missingTexturesTotal = (int)Math.Min(int.MaxValue, (long)missingTexturesTotal + diag.MissingTextures);
            cfgMissing |= diag.SceneLightMaskConfigMissing;
        }

        if (opts.IncludeFloor)
        {
            anyLayer = true;
            if (!TryExportSingleLayerTextured(map, textures, opts, basePath, "floor", includeBack: false, includeMiddle: false, includeFloor: true, includeUnderFront: false, includeFront: false, includeOverFront: false, out string file, out MinimapExportDiagnostics diag, out error))
            {
                return false;
            }
            files.Add(file);
            missingTexturesTotal = (int)Math.Min(int.MaxValue, (long)missingTexturesTotal + diag.MissingTextures);
            cfgMissing |= diag.SceneLightMaskConfigMissing;
        }

        if (opts.IncludeUnderFront)
        {
            anyLayer = true;
            if (!TryExportSingleLayerTextured(map, textures, opts, basePath, "underfront", includeBack: false, includeMiddle: false, includeFloor: false, includeUnderFront: true, includeFront: false, includeOverFront: false, out string file, out MinimapExportDiagnostics diag, out error))
            {
                return false;
            }
            files.Add(file);
            missingTexturesTotal = (int)Math.Min(int.MaxValue, (long)missingTexturesTotal + diag.MissingTextures);
            cfgMissing |= diag.SceneLightMaskConfigMissing;
        }

        if (opts.IncludeFront)
        {
            anyLayer = true;
            if (!TryExportSingleLayerTextured(map, textures, opts, basePath, "front", includeBack: false, includeMiddle: false, includeFloor: false, includeUnderFront: false, includeFront: true, includeOverFront: false, out string file, out MinimapExportDiagnostics diag, out error))
            {
                return false;
            }
            files.Add(file);
            missingTexturesTotal = (int)Math.Min(int.MaxValue, (long)missingTexturesTotal + diag.MissingTextures);
            cfgMissing |= diag.SceneLightMaskConfigMissing;
        }

        if (opts.IncludeOverFront)
        {
            anyLayer = true;
            if (!TryExportSingleLayerTextured(map, textures, opts, basePath, "overfront", includeBack: false, includeMiddle: false, includeFloor: false, includeUnderFront: false, includeFront: false, includeOverFront: true, out string file, out MinimapExportDiagnostics diag, out error))
            {
                return false;
            }
            files.Add(file);
            missingTexturesTotal = (int)Math.Min(int.MaxValue, (long)missingTexturesTotal + diag.MissingTextures);
            cfgMissing |= diag.SceneLightMaskConfigMissing;
        }

        if (!anyLayer)
        {
            error = "未选择任何导出层。";
            return false;
        }

        OverlayRenderState overlayState = PrepareOverlayRenderState(textures, map, opts);
        writtenFiles = files.ToArray();
        diagnostics = new MinimapExportDiagnostics(
            MissingTextures: missingTexturesTotal,
            SceneLightMaskConfigMissing: cfgMissing,
            DynamicSceneRequested: opts.IncludeDynamicScene,
            DynamicSceneDataPath: overlayState.DynamicSceneDataPath,
            DynamicSceneResolvedCandidateIndex: overlayState.DynamicSceneResolvedCandidateIndex,
            DynamicSceneResolvedCandidateLabel: overlayState.DynamicSceneResolvedCandidateLabel,
            DynamicSceneCandidateCount: overlayState.DynamicSceneCandidateCount,
            DynamicSceneWarning: overlayState.DynamicSceneWarning,
            AttachedEffectsRequested: opts.IncludeAttachedEffects,
            AttachedEffectsDataPath: overlayState.AttachedEffectsDataPath,
            AttachedEffectsResolvedCandidateIndex: overlayState.AttachedEffectsResolvedCandidateIndex,
            AttachedEffectsResolvedCandidateLabel: overlayState.AttachedEffectsResolvedCandidateLabel,
            AttachedEffectsCandidateCount: overlayState.AttachedEffectsCandidateCount,
            AttachedEffectsWarning: overlayState.AttachedEffectsWarning);
        return true;
    }

    /// <summary>
    /// Render a minimap into an in-memory RGBA buffer (placeholder colors, no textures).
    /// Used by the MapEditor runtime minimap overlay.
    /// </summary>
    public static bool TryRenderPlaceholderRgba8(
        MapDocument map,
        MinimapExportOptions opts,
        bool transparentBackground,
        out byte[] rgba8,
        out int width,
        out int height,
        out string error)
    {
        rgba8 = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        if (!IsPowerOfTwo(scale) || scale <= 0)
        {
            error = "ScaleDivisor 必须为 2 的幂（1/2/4/8/16/32）。";
            return false;
        }

        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        if (!TryComputeCropRect(map, opts, cellPxW, cellPxH, out CropRect crop, out error))
        {
            return false;
        }

        int outW = crop.Width;
        int outH = crop.Height;
        width = outW;
        height = outH;
        int cropX = crop.X;
        int cropY = crop.Y;

        long bytesNeeded = (long)outW * outH * 4;
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

        if (bytesNeeded > int.MaxValue)
        {
            error = "导出图像过大（RGBA buffer 分配失败）。";
            return false;
        }

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        if (!includeBack && !includeMiddle && !includeFloor && !includeUnderFront && !includeFront && !includeOverFront)
        {
            error = "未选择任何导出层。";
            return false;
        }

        int bytesPerRow = checked(outW * 4);
        rgba8 = new byte[checked(bytesPerRow * outH)];

        bool ProvideRow(int y, Span<byte> rowRgba8, out string rowError)
        {
            rowError = string.Empty;

            rowRgba8.Clear();
            if (!transparentBackground)
            {
                for (int i = 3; i < rowRgba8.Length; i += 4)
                {
                    rowRgba8[i] = 255;
                }
            }

            int fullY = y + cropY;
            int cellY = fullY / cellPxH;
            if ((uint)cellY >= (uint)map.Height)
            {
                return true;
            }

            int mapRow = cellY * map.Width;

            int fullX0 = cropX;
            int fullX1 = cropX + outW;

            int startCellX = fullX0 / cellPxW;
            int endCellXExclusive = (fullX1 + cellPxW - 1) / cellPxW;
            startCellX = Math.Clamp(startCellX, 0, map.Width);
            endCellXExclusive = Math.Clamp(endCellXExclusive, 0, map.Width);

            for (int cellX = startCellX; cellX < endCellXExclusive; cellX++)
            {
                int index = mapRow + cellX;
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cell = map.Cells[index];
                if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                {
                    continue;
                }

                uint key = ResolveCellKey(cell, includeBack, includeMiddle, includeFloor, includeUnderFront, includeFront, includeOverFront);
                if (key == 0)
                {
                    continue;
                }

                (byte r, byte g, byte b, byte a) = HashToColor(key, alpha: 255);

                int cellFullX0 = cellX * cellPxW;
                int cellFullX1 = cellFullX0 + cellPxW;
                int segFullX0 = Math.Max(fullX0, cellFullX0);
                int segFullX1 = Math.Min(fullX1, cellFullX1);
                if (segFullX0 >= segFullX1)
                {
                    continue;
                }

                int segX0 = segFullX0 - fullX0;
                int segX1 = segFullX1 - fullX0;

                int start = segX0 * 4;
                int end = segX1 * 4;
                for (int p = start; p < end; p += 4)
                {
                    rowRgba8[p + 0] = r;
                    rowRgba8[p + 1] = g;
                    rowRgba8[p + 2] = b;
                    rowRgba8[p + 3] = a;
                }
            }

            return true;
        }

        for (int y = 0; y < outH; y++)
        {
            if (!ProvideRow(y, rgba8.AsSpan(y * bytesPerRow, bytesPerRow), out string rowError))
            {
                error = string.IsNullOrWhiteSpace(rowError) ? "渲染失败。" : rowError;
                rgba8 = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rowError))
            {
                error = rowError;
                rgba8 = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Render a minimap into an in-memory RGBA buffer (textured compositor).
    /// Used by the MapEditor runtime minimap overlay.
    /// </summary>
    public static bool TryRenderTexturedRgba8(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        bool transparentBackground,
        out byte[] rgba8,
        out int width,
        out int height,
        out MinimapExportDiagnostics diagnostics,
        out string error)
    {
        rgba8 = Array.Empty<byte>();
        width = 0;
        height = 0;
        diagnostics = default;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (textures is null)
        {
            error = "贴图索引为空。";
            return false;
        }

        if (!textures.IsReady)
        {
            error = "贴图库未就绪（请先扫描贴图库：SGL/WPF）。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        if (!IsPowerOfTwo(scale) || scale <= 0)
        {
            error = "ScaleDivisor 必须为 2 的幂（1/2/4/8/16/32）。";
            return false;
        }

        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        if (!TryComputeCropRect(map, opts, cellPxW, cellPxH, out CropRect crop, out error))
        {
            return false;
        }

        int outW = crop.Width;
        int outH = crop.Height;
        width = outW;
        height = outH;
        int cropX = crop.X;
        int cropY = crop.Y;

        long bytesNeeded = (long)outW * outH * 4;
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

        if (bytesNeeded > int.MaxValue)
        {
            error = "导出图像过大（RGBA buffer 分配失败）。";
            return false;
        }

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        if (!includeBack && !includeMiddle && !includeFloor && !includeUnderFront && !includeFront && !includeOverFront)
        {
            error = "未选择任何导出层。";
            return false;
        }

        int bytesPerRow = checked(outW * 4);

        int stripHeight = ComputeStripHeightPixels(bytesPerRow, cellPxH, outH);
        long stripBytesLong = (long)bytesPerRow * stripHeight;
        if (stripBytesLong <= 0 || stripBytesLong > int.MaxValue)
        {
            error = "导出图像过大（strip buffer 分配失败）。";
            return false;
        }

        var cache = new Dictionary<long, ScaledImage>();
        var mskMaskCache = new Dictionary<int, byte[]?>();
        SceneLightMaskConfig? sceneLightConfig = SceneLightMaskConfigProvider.GetForDataPath(textures.RootDirectory);
        string sceneLightMapId = SceneLightMaskConfigProvider.DeriveSceneLightMapId(map.Path);
        OverlayRenderState overlayState = PrepareOverlayRenderState(textures, map, opts);
        int missingTextures = 0;

        rgba8 = new byte[checked(bytesPerRow * outH)];

        byte[] stripRgba = new byte[(int)stripBytesLong];
        int stripY0 = int.MinValue;
        int stripH = 0;

        bool ProvideRow(int y, Span<byte> rowRgba8, out string rowError)
        {
            rowError = string.Empty;

            if (stripY0 == int.MinValue || y < stripY0 || y >= stripY0 + stripH)
            {
                stripY0 = (y / stripHeight) * stripHeight;
                stripH = Math.Min(stripHeight, outH - stripY0);
                int usedBytes = bytesPerRow * stripH;

                Array.Clear(stripRgba, 0, usedBytes);
                if (!transparentBackground)
                {
                    FillSolidRgba(stripRgba.AsSpan(0, usedBytes), r: 0, g: 0, b: 0, a: 255);
                }

                RenderTexturedIntoBuffer(
                    map,
                    textures,
                    scale,
                    cache,
                    mskMaskCache,
                    stripRgba,
                    outW,
                    stripH,
                    stripY0,
                    cropX,
                    cropY,
                    cellPxW,
                    cellPxH,
                    opts,
                    overlayState,
                    sceneLightConfig,
                    sceneLightMapId,
                    ref missingTextures);
            }

            stripRgba.AsSpan((y - stripY0) * bytesPerRow, bytesPerRow).CopyTo(rowRgba8);
            return true;
        }

        for (int y = 0; y < outH; y++)
        {
            Span<byte> row = rgba8.AsSpan(y * bytesPerRow, bytesPerRow);
            if (!ProvideRow(y, row, out string rowError))
            {
                error = string.IsNullOrWhiteSpace(rowError) ? "渲染失败。" : rowError;
                rgba8 = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rowError))
            {
                error = rowError;
                rgba8 = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }
        }

        diagnostics = new MinimapExportDiagnostics(
            MissingTextures: missingTextures,
            SceneLightMaskConfigMissing: opts.ApplyLightingOverlay && sceneLightConfig is null,
            DynamicSceneRequested: opts.IncludeDynamicScene,
            DynamicSceneDataPath: overlayState.DynamicSceneDataPath,
            DynamicSceneResolvedCandidateIndex: overlayState.DynamicSceneResolvedCandidateIndex,
            DynamicSceneResolvedCandidateLabel: overlayState.DynamicSceneResolvedCandidateLabel,
            DynamicSceneCandidateCount: overlayState.DynamicSceneCandidateCount,
            DynamicSceneWarning: overlayState.DynamicSceneWarning,
            AttachedEffectsRequested: opts.IncludeAttachedEffects,
            AttachedEffectsDataPath: overlayState.AttachedEffectsDataPath,
            AttachedEffectsResolvedCandidateIndex: overlayState.AttachedEffectsResolvedCandidateIndex,
            AttachedEffectsResolvedCandidateLabel: overlayState.AttachedEffectsResolvedCandidateLabel,
            AttachedEffectsCandidateCount: overlayState.AttachedEffectsCandidateCount,
            AttachedEffectsWarning: overlayState.AttachedEffectsWarning);
        return true;
    }

    private static bool TryExportSingleLayer(
        MapDocument map,
        MinimapExportOptions opts,
        string basePath,
        string suffix,
        bool includeBack,
        bool includeMiddle,
        bool includeFloor,
        bool includeUnderFront,
        bool includeFront,
        bool includeOverFront,
        out string writtenFile,
        out string error)
    {
        writtenFile = string.Empty;
        error = string.Empty;

        var layerOpts = new MinimapExportOptions
        {
            ScaleDivisor = opts.ScaleDivisor,
            IncludeBack = includeBack,
            IncludeMiddle = includeMiddle,
            IncludeFloor = includeFloor,
            IncludeUnderFront = includeUnderFront,
            IncludeFront = includeFront,
            IncludeOverFront = includeOverFront,
            IncludeDynamicScene = opts.IncludeDynamicScene,
            IncludeAttachedEffects = opts.IncludeAttachedEffects,
            DynamicOverlayMaxDecompressedBytes = opts.DynamicOverlayMaxDecompressedBytes,
            SeparateLayerFiles = false,
            SuppressBorderCells = opts.SuppressBorderCells,
            ApplyCellTints = opts.ApplyCellTints,
            TintStrength = opts.TintStrength,
            ApplyCellHeightFlag = opts.ApplyCellHeightFlag,
            CellHeightFlagOffset = opts.CellHeightFlagOffset,
            ApplyObjectHeight = opts.ApplyObjectHeight,
            ObjectHeightScale = opts.ObjectHeightScale,
            ApplyLuminanceToAlpha = opts.ApplyLuminanceToAlpha,
            LuminanceSettings = opts.LuminanceSettings,
            ApplyLightingOverlay = opts.ApplyLightingOverlay,
            NightFactor = opts.NightFactor,
            LightingOverlayMaxAlpha = opts.LightingOverlayMaxAlpha,
            IncludeLightSprites = opts.IncludeLightSprites,
            MaxUncompressedBytes = opts.MaxUncompressedBytes,
            CropMode = opts.CropMode,
            CropCellX = opts.CropCellX,
            CropCellY = opts.CropCellY,
            CropCellWidth = opts.CropCellWidth,
            CropCellHeight = opts.CropCellHeight,
            CropPixelX = opts.CropPixelX,
            CropPixelY = opts.CropPixelY,
            CropPixelWidth = opts.CropPixelWidth,
            CropPixelHeight = opts.CropPixelHeight,
            AutoCropPaddingCells = opts.AutoCropPaddingCells,
        };

        string file = $"{basePath}_{suffix}.png";
        if (!TryWritePlaceholderPng(map, layerOpts, file, transparentBackground: true, out error))
        {
            return false;
        }

        writtenFile = file;
        return true;
    }

    private static bool TryExportSingleLayerTextured(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        string basePath,
        string suffix,
        bool includeBack,
        bool includeMiddle,
        bool includeFloor,
        bool includeUnderFront,
        bool includeFront,
        bool includeOverFront,
        out string writtenFile,
        out MinimapExportDiagnostics diagnostics,
        out string error)
    {
        writtenFile = string.Empty;
        diagnostics = default;
        error = string.Empty;

        var layerOpts = new MinimapExportOptions
        {
            ScaleDivisor = opts.ScaleDivisor,
            IncludeBack = includeBack,
            IncludeMiddle = includeMiddle,
            IncludeFloor = includeFloor,
            IncludeUnderFront = includeUnderFront,
            IncludeFront = includeFront,
            IncludeOverFront = includeOverFront,
            IncludeDynamicScene = opts.IncludeDynamicScene,
            IncludeAttachedEffects = opts.IncludeAttachedEffects,
            DynamicOverlayMaxDecompressedBytes = opts.DynamicOverlayMaxDecompressedBytes,
            SeparateLayerFiles = false,
            SuppressBorderCells = opts.SuppressBorderCells,
            ApplyCellTints = opts.ApplyCellTints,
            TintStrength = opts.TintStrength,
            ApplyCellHeightFlag = opts.ApplyCellHeightFlag,
            CellHeightFlagOffset = opts.CellHeightFlagOffset,
            ApplyObjectHeight = opts.ApplyObjectHeight,
            ObjectHeightScale = opts.ObjectHeightScale,
            ApplyLuminanceToAlpha = opts.ApplyLuminanceToAlpha,
            LuminanceSettings = opts.LuminanceSettings,
            ApplyLightingOverlay = opts.ApplyLightingOverlay,
            NightFactor = opts.NightFactor,
            LightingOverlayMaxAlpha = opts.LightingOverlayMaxAlpha,
            IncludeLightSprites = opts.IncludeLightSprites,
            MaxUncompressedBytes = opts.MaxUncompressedBytes,
            CropMode = opts.CropMode,
            CropCellX = opts.CropCellX,
            CropCellY = opts.CropCellY,
            CropCellWidth = opts.CropCellWidth,
            CropCellHeight = opts.CropCellHeight,
            CropPixelX = opts.CropPixelX,
            CropPixelY = opts.CropPixelY,
            CropPixelWidth = opts.CropPixelWidth,
            CropPixelHeight = opts.CropPixelHeight,
            AutoCropPaddingCells = opts.AutoCropPaddingCells,
        };

        string file = $"{basePath}_{suffix}.png";
        if (!TryWriteTexturedPngStriped(map, textures, layerOpts, file, transparentBackground: true, out diagnostics, out error))
        {
            return false;
        }

        writtenFile = file;
        return true;
    }

    private static bool TryWritePlaceholderPng(
        MapDocument map,
        MinimapExportOptions opts,
        string outputPath,
        bool transparentBackground,
        out string error)
    {
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        if (!IsPowerOfTwo(scale) || scale <= 0)
        {
            error = "ScaleDivisor 必须为 2 的幂（1/2/4/8/16/32）。";
            return false;
        }

        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        if (!TryComputeCropRect(map, opts, cellPxW, cellPxH, out CropRect crop, out error))
        {
            return false;
        }

        int width = crop.Width;
        int height = crop.Height;
        int cropX = crop.X;
        int cropY = crop.Y;

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

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        if (!includeBack && !includeMiddle && !includeFloor && !includeUnderFront && !includeFront && !includeOverFront)
        {
            error = "未选择任何导出层。";
            return false;
        }

        bool ProvideRow(int y, Span<byte> rowRgba8, out string rowError)
        {
            rowError = string.Empty;

            rowRgba8.Clear();
            if (!transparentBackground)
            {
                for (int i = 3; i < rowRgba8.Length; i += 4)
                {
                    rowRgba8[i] = 255;
                }
            }

            int fullY = y + cropY;
            int cellY = fullY / cellPxH;
            if ((uint)cellY >= (uint)map.Height)
            {
                return true;
            }

            int mapRow = cellY * map.Width;

            int fullX0 = cropX;
            int fullX1 = cropX + width;

            int startCellX = fullX0 / cellPxW;
            int endCellXExclusive = (fullX1 + cellPxW - 1) / cellPxW;
            startCellX = Math.Clamp(startCellX, 0, map.Width);
            endCellXExclusive = Math.Clamp(endCellXExclusive, 0, map.Width);

            for (int cellX = startCellX; cellX < endCellXExclusive; cellX++)
            {
                int index = mapRow + cellX;
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cell = map.Cells[index];
                if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                {
                    continue;
                }

                uint key = ResolveCellKey(cell, includeBack, includeMiddle, includeFloor, includeUnderFront, includeFront, includeOverFront);
                if (key == 0)
                {
                    continue;
                }

                (byte r, byte g, byte b, byte a) = HashToColor(key, alpha: 255);

                int cellFullX0 = cellX * cellPxW;
                int cellFullX1 = cellFullX0 + cellPxW;
                int segFullX0 = Math.Max(fullX0, cellFullX0);
                int segFullX1 = Math.Min(fullX1, cellFullX1);
                if (segFullX0 >= segFullX1)
                {
                    continue;
                }

                int segX0 = segFullX0 - fullX0;
                int segX1 = segFullX1 - fullX0;

                int start = segX0 * 4;
                int end = segX1 * 4;
                for (int p = start; p < end; p += 4)
                {
                    rowRgba8[p + 0] = r;
                    rowRgba8[p + 1] = g;
                    rowRgba8[p + 2] = b;
                    rowRgba8[p + 3] = a;
                }
            }

            return true;
        }

        return PngWriter.TryWriteRgba8Rows(outputPath, width, height, ProvideRow, out error);
    }

    private static bool TryWriteTexturedPngStriped(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        string outputPath,
        bool transparentBackground,
        out MinimapExportDiagnostics diagnostics,
        out string error)
    {
        diagnostics = default;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (textures is null)
        {
            error = "贴图索引为空。";
            return false;
        }

        if (!textures.IsReady)
        {
            error = "贴图库未就绪（请先扫描贴图库：SGL/WPF）。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        if (!IsPowerOfTwo(scale) || scale <= 0)
        {
            error = "ScaleDivisor 必须为 2 的幂（1/2/4/8/16/32）。";
            return false;
        }

        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        if (!TryComputeCropRect(map, opts, cellPxW, cellPxH, out CropRect crop, out error))
        {
            return false;
        }

        int width = crop.Width;
        int height = crop.Height;
        int cropX = crop.X;
        int cropY = crop.Y;

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

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        if (!includeBack && !includeMiddle && !includeFloor && !includeUnderFront && !includeFront && !includeOverFront)
        {
            error = "未选择任何导出层。";
            return false;
        }

        int bytesPerRow = checked(width * 4);

        int stripHeight = ComputeStripHeightPixels(bytesPerRow, cellPxH, height);
        long stripBytesLong = (long)bytesPerRow * stripHeight;
        if (stripBytesLong <= 0 || stripBytesLong > int.MaxValue)
        {
            error = "导出图像过大（strip buffer 分配失败）。";
            return false;
        }

        var cache = new Dictionary<long, ScaledImage>();
        var mskMaskCache = new Dictionary<int, byte[]?>();
        SceneLightMaskConfig? sceneLightConfig = SceneLightMaskConfigProvider.GetForDataPath(textures.RootDirectory);
        string sceneLightMapId = SceneLightMaskConfigProvider.DeriveSceneLightMapId(map.Path);
        OverlayRenderState overlayState = PrepareOverlayRenderState(textures, map, opts);
        int missingTextures = 0;

        byte[] stripRgba = new byte[(int)stripBytesLong];
        int stripY0 = int.MinValue;
        int stripH = 0;

        bool ProvideRow(int y, Span<byte> rowRgba8, out string rowError)
        {
            rowError = string.Empty;

            if (stripY0 == int.MinValue || y < stripY0 || y >= stripY0 + stripH)
            {
                stripY0 = (y / stripHeight) * stripHeight;
                stripH = Math.Min(stripHeight, height - stripY0);
                int usedBytes = bytesPerRow * stripH;

                Array.Clear(stripRgba, 0, usedBytes);
                if (!transparentBackground)
                {
                    FillSolidRgba(stripRgba.AsSpan(0, usedBytes), r: 0, g: 0, b: 0, a: 255);
                }

                RenderTexturedIntoBuffer(
                    map,
                    textures,
                    scale,
                    cache,
                    mskMaskCache,
                    stripRgba,
                    width,
                    stripH,
                    stripY0,
                    cropX,
                    cropY,
                    cellPxW,
                    cellPxH,
                    opts,
                    overlayState,
                    sceneLightConfig,
                    sceneLightMapId,
                    ref missingTextures);
            }

            stripRgba.AsSpan((y - stripY0) * bytesPerRow, bytesPerRow).CopyTo(rowRgba8);
            return true;
        }

        bool ok = PngWriter.TryWriteRgba8Rows(outputPath, width, height, ProvideRow, out error);
        diagnostics = new MinimapExportDiagnostics(
            MissingTextures: missingTextures,
            SceneLightMaskConfigMissing: opts.ApplyLightingOverlay && sceneLightConfig is null,
            DynamicSceneRequested: opts.IncludeDynamicScene,
            DynamicSceneDataPath: overlayState.DynamicSceneDataPath,
            DynamicSceneResolvedCandidateIndex: overlayState.DynamicSceneResolvedCandidateIndex,
            DynamicSceneResolvedCandidateLabel: overlayState.DynamicSceneResolvedCandidateLabel,
            DynamicSceneCandidateCount: overlayState.DynamicSceneCandidateCount,
            DynamicSceneWarning: overlayState.DynamicSceneWarning,
            AttachedEffectsRequested: opts.IncludeAttachedEffects,
            AttachedEffectsDataPath: overlayState.AttachedEffectsDataPath,
            AttachedEffectsResolvedCandidateIndex: overlayState.AttachedEffectsResolvedCandidateIndex,
            AttachedEffectsResolvedCandidateLabel: overlayState.AttachedEffectsResolvedCandidateLabel,
            AttachedEffectsCandidateCount: overlayState.AttachedEffectsCandidateCount,
            AttachedEffectsWarning: overlayState.AttachedEffectsWarning);
        return ok;
    }

    private static (string ResolvedPath, int? ResolvedCandidateIndex, string? ResolvedCandidateLabel, int CandidateCount) ResolveDynSceneDataCandidate(
        string dataRoot,
        string mapPath,
        bool requested,
        string? mapIdOverride)
    {
        if (!requested)
        {
            return (string.Empty, null, null, 0);
        }

        string mapPathOrId = string.IsNullOrWhiteSpace(mapIdOverride) ? mapPath : mapIdOverride;
        IReadOnlyList<DynamicOverlayCandidatePath> candidates = DynamicOverlayDataLocator.BuildDynSceneCandidatePathsLabeled(dataRoot, mapPathOrId);
        (string path, int? index, string? label) = DynamicOverlayDataLocator.ResolveFirstExistingCandidate(candidates);
        return (path, index, label, candidates.Count);
    }

    private static (string ResolvedPath, int? ResolvedCandidateIndex, string? ResolvedCandidateLabel, int CandidateCount) ResolveAttachedEffectsDataCandidate(
        string dataRoot,
        string mapPath,
        bool requested,
        string? mapIdOverride)
    {
        if (!requested)
        {
            return (string.Empty, null, null, 0);
        }

        string mapPathOrId = string.IsNullOrWhiteSpace(mapIdOverride) ? mapPath : mapIdOverride;
        IReadOnlyList<DynamicOverlayCandidatePath> candidates = DynamicOverlayDataLocator.BuildAttachedEffectsCandidatePathsLabeled(dataRoot, mapPathOrId);
        (string path, int? index, string? label) = DynamicOverlayDataLocator.ResolveFirstExistingCandidate(candidates);
        return (path, index, label, candidates.Count);
    }

    private static OverlayRenderState PrepareOverlayRenderState(MapTextureIndex textures, MapDocument map, MinimapExportOptions opts)
    {
        (string dynScenePath, int? dynSceneResolvedCandidateIndex, string? dynSceneResolvedCandidateLabel, int dynSceneCandidateCount) =
            ResolveDynSceneDataCandidate(textures.RootDirectory, map.Path, opts.IncludeDynamicScene, opts.DynamicOverlayMapIdOverride);
        DynamicOverlayDocument? dynSceneDocument = null;
        string? dynSceneWarning = null;
        if (opts.IncludeDynamicScene)
        {
            if (string.IsNullOrWhiteSpace(dynScenePath))
            {
                dynSceneWarning = "DynScene：未找到数据文件，已忽略动态覆盖选项。";
            }
            else
            {
                string label = string.IsNullOrWhiteSpace(dynSceneResolvedCandidateLabel) ? string.Empty : $"（{dynSceneResolvedCandidateLabel}）";
                bool parsedOk = false;
                string? layoutError = null;
                string? parseError = null;
                string? fallbackError = null;

                if (!string.IsNullOrWhiteSpace(opts.DynamicOverlayLayoutPath))
                {
                    if (!DynamicOverlayBinaryLayout.TryLoadFromFile(opts.DynamicOverlayLayoutPath, out DynamicOverlayBinaryLayout layout, out layoutError))
                    {
                        dynSceneWarning = $"DynScene：布局文件加载失败，已改用自动解析（{layoutError}）。";
                    }
                    else if (DynamicOverlayCodec.TryReadFromFile(dynScenePath, layout, out dynSceneDocument, out parseError, maxDecompressedBytes: opts.DynamicOverlayMaxDecompressedBytes))
                    {
                        parsedOk = true;
                    }
                    else
                    {
                        dynSceneWarning = $"DynScene：{Path.GetFileName(dynScenePath)}{label} 使用指定布局解析失败，已尝试自动解析（{parseError}）。";
                    }
                }

                if (!parsedOk)
                {
                    if (DynamicOverlayCodec.TryReadFromFile(dynScenePath, out dynSceneDocument, out fallbackError, maxDecompressedBytes: opts.DynamicOverlayMaxDecompressedBytes))
                    {
                        parsedOk = true;
                    }
                }

                if (!parsedOk)
                {
                    string combined = string.Join("；", new[] { layoutError, parseError, fallbackError }.Where(static x => !string.IsNullOrWhiteSpace(x)));
                    dynSceneWarning = $"DynScene：{Path.GetFileName(dynScenePath)}{label} 解析失败，已忽略（{combined}）。";
                }
                else if (dynSceneDocument is { Records.Count: 0 })
                {
                    dynSceneWarning = $"DynScene：{Path.GetFileName(dynScenePath)}{label} 解析成功但没有记录。";
                    dynSceneDocument = null;
                }
            }
        }

        (string attachedEffectsPath, int? attachedEffectsResolvedCandidateIndex, string? attachedEffectsResolvedCandidateLabel, int attachedEffectsCandidateCount) =
            ResolveAttachedEffectsDataCandidate(textures.RootDirectory, map.Path, opts.IncludeAttachedEffects, opts.AttachedEffectsMapIdOverride);
        DynamicOverlayDocument? attachedEffectsDocument = null;
        string? attachedEffectsWarning = null;
        if (opts.IncludeAttachedEffects)
        {
            if (string.IsNullOrWhiteSpace(attachedEffectsPath))
            {
                attachedEffectsWarning = "挂接 effects：未找到数据文件，已忽略挂接 effects 选项。";
            }
            else
            {
                string label = string.IsNullOrWhiteSpace(attachedEffectsResolvedCandidateLabel) ? string.Empty : $"（{attachedEffectsResolvedCandidateLabel}）";
                bool parsedOk = false;
                string? layoutError = null;
                string? parseError = null;
                string? fallbackError = null;

                if (!string.IsNullOrWhiteSpace(opts.AttachedEffectsLayoutPath))
                {
                    if (!DynamicOverlayBinaryLayout.TryLoadFromFile(opts.AttachedEffectsLayoutPath, out DynamicOverlayBinaryLayout layout, out layoutError))
                    {
                        attachedEffectsWarning = $"挂接 effects：布局文件加载失败，已改用自动解析（{layoutError}）。";
                    }
                    else if (DynamicOverlayCodec.TryReadFromFile(attachedEffectsPath, layout, out attachedEffectsDocument, out parseError, maxDecompressedBytes: opts.DynamicOverlayMaxDecompressedBytes))
                    {
                        parsedOk = true;
                    }
                    else
                    {
                        attachedEffectsWarning = $"挂接 effects：{Path.GetFileName(attachedEffectsPath)}{label} 使用指定布局解析失败，已尝试自动解析（{parseError}）。";
                    }
                }

                if (!parsedOk)
                {
                    if (DynamicOverlayCodec.TryReadFromFile(attachedEffectsPath, out attachedEffectsDocument, out fallbackError, maxDecompressedBytes: opts.DynamicOverlayMaxDecompressedBytes))
                    {
                        parsedOk = true;
                    }
                }

                if (!parsedOk)
                {
                    string combined = string.Join("；", new[] { layoutError, parseError, fallbackError }.Where(static x => !string.IsNullOrWhiteSpace(x)));
                    attachedEffectsWarning = $"挂接 effects：{Path.GetFileName(attachedEffectsPath)}{label} 解析失败，已忽略（{combined}）。";
                }
                else if (attachedEffectsDocument is { Records.Count: 0 })
                {
                    attachedEffectsWarning = $"挂接 effects：{Path.GetFileName(attachedEffectsPath)}{label} 解析成功但没有记录。";
                    attachedEffectsDocument = null;
                }
            }
        }

        return new OverlayRenderState(
            DynamicSceneDataPath: dynScenePath,
            DynamicSceneResolvedCandidateIndex: dynSceneResolvedCandidateIndex,
            DynamicSceneResolvedCandidateLabel: dynSceneResolvedCandidateLabel,
            DynamicSceneCandidateCount: dynSceneCandidateCount,
            DynamicSceneDocument: dynSceneDocument,
            DynamicSceneWarning: dynSceneWarning,
            AttachedEffectsDataPath: attachedEffectsPath,
            AttachedEffectsResolvedCandidateIndex: attachedEffectsResolvedCandidateIndex,
            AttachedEffectsResolvedCandidateLabel: attachedEffectsResolvedCandidateLabel,
            AttachedEffectsCandidateCount: attachedEffectsCandidateCount,
            AttachedEffectsDocument: attachedEffectsDocument,
            AttachedEffectsWarning: attachedEffectsWarning);
    }

    private static void RenderOverlayDocuments(
        OverlayRenderState overlayState,
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        ref int missingTextures,
        string[] layers)
    {
        RenderOverlayDocument(overlayState.DynamicSceneDocument, textures, scaleDivisor, cache, dstRgba, dstW, dstH, dstYOffset, cropX, cropY, cellPxW, cellPxH, opts, ref missingTextures, layers);
        RenderOverlayDocument(overlayState.AttachedEffectsDocument, textures, scaleDivisor, cache, dstRgba, dstW, dstH, dstYOffset, cropX, cropY, cellPxW, cellPxH, opts, ref missingTextures, layers);
    }

    private static void RenderOverlayDocument(
        DynamicOverlayDocument? document,
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        ref int missingTextures,
        params string[] layers)
    {
        if (document is null || document.Records.Count == 0)
        {
            return;
        }

        foreach (DynamicOverlayRecord record in document.Records
                     .Where(r => ShouldRenderOverlayLayer(r.Layer, layers))
                     .OrderBy(static r => r.Order)
                     .ThenBy(static r => r.Y)
                     .ThenBy(static r => r.X))
        {
            DrawOverlayRecord(
                textures,
                record,
                scaleDivisor,
                cache,
                dstRgba,
                dstW,
                dstH,
                dstYOffset,
                cropX,
                cropY,
                cellPxW,
                cellPxH,
                opts,
                ref missingTextures);
        }
    }

    private static bool ShouldRenderOverlayLayer(string? layer, IReadOnlyList<string> expectedLayers)
    {
        if (expectedLayers is null || expectedLayers.Count == 0)
        {
            return true;
        }

        string normalized = (layer ?? string.Empty).Trim().ToLowerInvariant();
        for (int i = 0; i < expectedLayers.Count; i++)
        {
            if (string.Equals(normalized, expectedLayers[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly struct CellOverlayRecord
    {
        public int CellIndex { get; }
        public DynamicOverlayRecord Record { get; }

        public CellOverlayRecord(int cellIndex, DynamicOverlayRecord record)
        {
            CellIndex = cellIndex;
            Record = record;
        }
    }

    private readonly struct OverlayCellPlan
    {
        public CellOverlayRecord[] CellRecords { get; }
        public DynamicOverlayRecord[] GlobalRecords { get; }

        public bool HasAny => CellRecords.Length > 0 || GlobalRecords.Length > 0;

        public OverlayCellPlan(CellOverlayRecord[]? cellRecords, DynamicOverlayRecord[]? globalRecords)
        {
            CellRecords = cellRecords ?? Array.Empty<CellOverlayRecord>();
            GlobalRecords = globalRecords ?? Array.Empty<DynamicOverlayRecord>();
        }
    }

    private static OverlayCellPlan BuildOverlayCellPlan(OverlayRenderState overlayState, int mapWidth, int mapHeight, string[] layers)
    {
        var cellRecords = new List<CellOverlayRecord>(capacity: 64);
        var globalRecords = new List<DynamicOverlayRecord>(capacity: 16);

        AddOverlayDocumentToPlan(overlayState.DynamicSceneDocument, mapWidth, mapHeight, layers, cellRecords, globalRecords);
        AddOverlayDocumentToPlan(overlayState.AttachedEffectsDocument, mapWidth, mapHeight, layers, cellRecords, globalRecords);

        if (cellRecords.Count > 1)
        {
            cellRecords.Sort(CompareCellOverlayRecord);
        }

        if (globalRecords.Count > 1)
        {
            globalRecords.Sort(CompareOverlayRecordOrderYx);
        }

        return new OverlayCellPlan(cellRecords.ToArray(), globalRecords.ToArray());
    }

    private static void AddOverlayDocumentToPlan(
        DynamicOverlayDocument? document,
        int mapWidth,
        int mapHeight,
        IReadOnlyList<string> layers,
        List<CellOverlayRecord> cellRecords,
        List<DynamicOverlayRecord> globalRecords)
    {
        if (document is null || document.Records.Count == 0)
        {
            return;
        }

        foreach (DynamicOverlayRecord record in document.Records)
        {
            if (!ShouldRenderOverlayLayer(record.Layer, layers))
            {
                continue;
            }

            if (TryMapOverlayRecordToCellIndex(record, mapWidth, mapHeight, out int cellIndex))
            {
                cellRecords.Add(new CellOverlayRecord(cellIndex, record));
                continue;
            }

            globalRecords.Add(record);
        }
    }

    private static bool TryMapOverlayRecordToCellIndex(DynamicOverlayRecord record, int mapWidth, int mapHeight, out int cellIndex)
    {
        cellIndex = -1;

        if (record is null || mapWidth <= 0 || mapHeight <= 0)
        {
            return false;
        }

        int cellX;
        int cellY;

        if (record.CoordinateSpace.Equals("cell", StringComparison.OrdinalIgnoreCase))
        {
            cellX = record.X;
            cellY = record.Y;
        }
        else if (record.CoordinateSpace.Equals("pixel", StringComparison.OrdinalIgnoreCase))
        {
            // 像素坐标：按 64x32 的 minimap cell 尺寸粗映射到 cell 坐标，仅用于插入顺序。
            // 实际绘制仍使用 record 的像素坐标（DrawOverlayRecord 内部计算 drawX/drawY）。
            cellX = DivFloor(record.X, MinimapCellW);
            cellY = DivFloor(record.Y, MinimapCellH);
        }
        else
        {
            return false;
        }

        if (cellX < 0 || cellY < 0 || cellX >= mapWidth || cellY >= mapHeight)
        {
            return false;
        }

        cellIndex = checked(cellY * mapWidth + cellX);
        return true;
    }

    private static int DivFloor(int value, int divisor)
    {
        if (divisor <= 0)
        {
            return 0;
        }

        int q = value / divisor;
        int r = value % divisor;
        if (r != 0 && value < 0)
        {
            q--;
        }

        return q;
    }

    private static void RenderOverlayCellRecordsForIndex(
        CellOverlayRecord[] cellRecords,
        ref int cursor,
        int cellIndex,
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        ref int missingTextures)
    {
        if (cellRecords.Length == 0)
        {
            return;
        }

        cursor = Math.Max(0, cursor);

        while ((uint)cursor < (uint)cellRecords.Length && cellRecords[cursor].CellIndex == cellIndex)
        {
            DrawOverlayRecord(
                textures,
                cellRecords[cursor].Record,
                scaleDivisor,
                cache,
                dstRgba,
                dstW,
                dstH,
                dstYOffset,
                cropX,
                cropY,
                cellPxW,
                cellPxH,
                opts,
                ref missingTextures);
            cursor++;
        }
    }

    private static void RenderOverlayRecords(
        IReadOnlyList<DynamicOverlayRecord> records,
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        ref int missingTextures)
    {
        if (records is null || records.Count == 0)
        {
            return;
        }

        for (int i = 0; i < records.Count; i++)
        {
            DrawOverlayRecord(
                textures,
                records[i],
                scaleDivisor,
                cache,
                dstRgba,
                dstW,
                dstH,
                dstYOffset,
                cropX,
                cropY,
                cellPxW,
                cellPxH,
                opts,
                ref missingTextures);
        }
    }

    private static int CompareCellOverlayRecord(CellOverlayRecord a, CellOverlayRecord b)
    {
        int c = a.CellIndex.CompareTo(b.CellIndex);
        if (c != 0)
        {
            return c;
        }

        return CompareOverlayRecordOrderYx(a.Record, b.Record);
    }

    private static int CompareOverlayRecordOrderYx(DynamicOverlayRecord a, DynamicOverlayRecord b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        int c = a.Order.CompareTo(b.Order);
        if (c != 0)
        {
            return c;
        }

        c = a.Y.CompareTo(b.Y);
        if (c != 0)
        {
            return c;
        }

        return a.X.CompareTo(b.X);
    }

    private static int ComputeStripHeightPixels(int bytesPerRow, int cellPxH, int outputHeight)
    {
        const int StripBudgetBytes = 64 * 1024 * 1024;

        bytesPerRow = Math.Max(1, bytesPerRow);
        cellPxH = Math.Max(1, cellPxH);
        outputHeight = Math.Max(1, outputHeight);

        int maxRows = StripBudgetBytes / bytesPerRow;
        maxRows = Math.Max(maxRows, cellPxH);
        maxRows = Math.Min(maxRows, outputHeight);

        // Align to whole cell rows to reduce strip count while preserving determinism.
        maxRows = (maxRows / cellPxH) * cellPxH;
        if (maxRows <= 0)
        {
            maxRows = Math.Min(cellPxH, outputHeight);
        }

        return Math.Max(1, maxRows);
    }

    private static void RenderTexturedIntoBuffer(
        MapDocument map,
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        Dictionary<int, byte[]?> mskMaskCache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        OverlayRenderState overlayState,
        SceneLightMaskConfig? sceneLightConfig,
        string sceneLightMapId,
        ref int missingTextures)
    {
        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        int xBase = -cropX;
        int yBase = -cropY - dstYOffset;

        // ---- Pass 1: Middle layer (Tiles) ----
        if (includeMiddle)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH + yBase;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scaleDivisor);
                    }

                    int cellOriginX = x * cellPxW + xBase;
                    MinimapTint middleTint = BuildMiniTint(cell.ColorAdjTile, opts);

                    if (cell.MiddleImage2 != 0)
                    {
                        if (TryResolveMiddleGround(cell, out int groundPkg, out int groundIdx))
                        {
                            DrawTileTextured(
                                textures, groundPkg, groundIdx, scaleDivisor, cache,
                                dstRgba, dstW, dstH,
                                cellOriginX, cellOriginY,
                                cellHeightLiftPx,
                                middleTint,
                                ref missingTextures);
                        }

                        if (TryResolveCoastComposite(cell, out int coastPkg, out int coastIdx, out int maskIdx))
                        {
                            uint coastTintRaw = cell.ColorAdjEffect != 0 ? cell.ColorAdjEffect : cell.ColorAdjTile;
                            MinimapTint coastTint = BuildMiniTint(coastTintRaw, opts);

                            DrawCoastCompositeTextured(
                                textures, coastPkg, coastIdx, maskIdx, scaleDivisor, cache,
                                dstRgba, dstW, dstH,
                                cellOriginX, cellOriginY,
                                cellHeightLiftPx,
                                coastTint,
                                mskMaskCache,
                                ref missingTextures);
                        }

                        continue;
                    }

                    if (!TryResolveMiddleTile(cell, out int pkg, out int idx))
                    {
                        continue;
                    }

                    DrawTileTextured(
                        textures, pkg, idx, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY,
                        cellHeightLiftPx,
                        middleTint,
                        ref missingTextures);
                }
            }
        }

        RenderOverlayDocuments(
            overlayState,
            textures,
            scaleDivisor,
            cache,
            dstRgba,
            dstW,
            dstH,
            dstYOffset,
            cropX,
            cropY,
            cellPxW,
            cellPxH,
            opts,
            ref missingTextures,
            ["middle"]);

        // ---- Pass 2: Back layer (SmTiles) ----
        if (includeBack)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH + yBase;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    if (!TryResolveBackTile(cell, out int pkg, out int idx))
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scaleDivisor);
                    }

                    int cellOriginX = x * cellPxW + xBase;
                    MinimapTint backTint = BuildMiniTint(cell.ColorAdjSmTile, opts);

                    DrawTileTextured(
                        textures, pkg, idx, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY,
                        cellHeightLiftPx,
                        backTint,
                        ref missingTextures);
                }
            }
        }

        RenderOverlayDocuments(
            overlayState,
            textures,
            scaleDivisor,
            cache,
            dstRgba,
            dstW,
            dstH,
            dstYOffset,
            cropX,
            cropY,
            cellPxW,
            cellPxH,
            opts,
            ref missingTextures,
            ["back"]);

        // ---- Pass 3: nearGround (floor objects) ----
        if (includeFloor)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH + yBase;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    if (!TryResolveExtraObject(cell.NearGround, out int pkg, out int idx))
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scaleDivisor);
                    }

                    int cellOriginX = x * cellPxW + xBase;
                    MinimapTint floorTint = BuildMiniTint(cell.ColorAdjFloor, opts);

                    DrawObjectTextured(
                        textures, pkg, idx, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY,
                        cellPxH,
                        cellHeightLiftPx,
                        floorTint,
                        opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(pkg),
                        opts.LuminanceSettings,
                        ref missingTextures);
                }
            }
        }

        RenderOverlayDocuments(
            overlayState,
            textures,
            scaleDivisor,
            cache,
            dstRgba,
            dstW,
            dstH,
            dstYOffset,
            cropX,
            cropY,
            cellPxW,
            cellPxH,
            opts,
            ref missingTextures,
            ["floor"]);

        // ---- Pass 4: Object layers ----
        bool includeAnyObjectLayer = includeUnderFront || includeFront || includeOverFront;

        OverlayCellPlan underFrontOverlay = BuildOverlayCellPlan(overlayState, map.Width, map.Height, ["underfront"]);
        OverlayCellPlan frontOverlay = BuildOverlayCellPlan(overlayState, map.Width, map.Height, ["front"]);
        OverlayCellPlan overFrontOverlay = BuildOverlayCellPlan(overlayState, map.Width, map.Height, ["overfront"]);
        bool hasAnyObjectOverlays = underFrontOverlay.HasAny || frontOverlay.HasAny || overFrontOverlay.HasAny;

        if (includeAnyObjectLayer)
        {
            int underCursor = 0;
            int frontCursor = 0;
            int overCursor = 0;

            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH + yBase;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scaleDivisor);
                    }

                    int objectHeightLiftPx = 0;
                    if (opts.ApplyObjectHeight && cell.ObjectHeight != 0)
                    {
                        objectHeightLiftPx = ScaleFloatPxRound(cell.ObjectHeight * opts.ObjectHeightScale, scaleDivisor);
                    }

                    int frontLiftPx = cellHeightLiftPx + objectHeightLiftPx;
                    int cellOriginX = x * cellPxW + xBase;

                    int frontPkg = 0;
                    int frontIdx = 0;
                    ScaledImage frontImg = default;
                    bool frontImgOk = false;
                    bool frontIsNearGround = false;
                    bool frontIsShort = false;

                    // 4a. Short front objects (height <= 32 或 flags NEARGROUND)
                    if (includeFront && TryResolveFrontObject(cell, out frontPkg, out frontIdx))
                    {
                        if (TryGetScaledImage(textures, frontPkg, frontIdx, scaleDivisor, cache, out frontImg))
                        {
                            frontImgOk = true;
                            frontIsNearGround = (cell.Flags & NmpFlagNearGround) != 0;
                            frontIsShort = frontImg.SourceHeight <= 32;
                            if (frontIsNearGround || frontIsShort)
                            {
                                MinimapTint frontTint = BuildMiniTint(cell.ColorAdjObject, opts);
                                bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(frontPkg);
                                BlendObjectImage(dstRgba, dstW, dstH, frontImg, cellOriginX, cellOriginY, cellPxH, frontLiftPx, frontTint, applyLum, opts.LuminanceSettings);
                            }
                        }
                        else
                        {
                            missingTextures++;
                        }
                    }

                    // 4b. UnderFront objects (underObject)
                    if (includeUnderFront && TryResolveExtraObject(cell.UnderObject, out int underPkg, out int underIdx))
                    {
                        if (TryGetScaledImage(textures, underPkg, underIdx, scaleDivisor, cache, out ScaledImage underImg))
                        {
                            uint underTintRaw = cell.ColorAdjEffect != 0 ? cell.ColorAdjEffect : cell.ColorAdjObject;
                            MinimapTint underTint = BuildMiniTint(underTintRaw, opts);
                            bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(underPkg);
                            BlendObjectImage(dstRgba, dstW, dstH, underImg, cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx, underTint, applyLum, opts.LuminanceSettings);
                        }
                        else
                        {
                            missingTextures++;
                        }
                    }

                    RenderOverlayCellRecordsForIndex(
                        underFrontOverlay.CellRecords,
                        ref underCursor,
                        index,
                        textures,
                        scaleDivisor,
                        cache,
                        dstRgba,
                        dstW,
                        dstH,
                        dstYOffset,
                        cropX,
                        cropY,
                        cellPxW,
                        cellPxH,
                        opts,
                        ref missingTextures);

                    // 4c. Front objects (tall only)
                    if (includeFront && frontImgOk && !frontIsNearGround && !frontIsShort)
                    {
                        MinimapTint frontTint = BuildMiniTint(cell.ColorAdjObject, opts);
                        bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(frontPkg);
                        BlendObjectImage(dstRgba, dstW, dstH, frontImg, cellOriginX, cellOriginY, cellPxH, frontLiftPx, frontTint, applyLum, opts.LuminanceSettings);
                    }

                    RenderOverlayCellRecordsForIndex(
                        frontOverlay.CellRecords,
                        ref frontCursor,
                        index,
                        textures,
                        scaleDivisor,
                        cache,
                        dstRgba,
                        dstW,
                        dstH,
                        dstYOffset,
                        cropX,
                        cropY,
                        cellPxW,
                        cellPxH,
                        opts,
                        ref missingTextures);

                    // 4d. OverFront objects (overObject) — light images filtered out
                    if (includeOverFront && TryResolveExtraObject(cell.OverObject, out int overPkg, out int overIdx))
                    {
                        if (!IsLightImage(overPkg, overIdx))
                        {
                            if (TryGetScaledImage(textures, overPkg, overIdx, scaleDivisor, cache, out ScaledImage overImg))
                            {
                                uint overTintRaw = cell.ColorOverObj != 0 ? cell.ColorOverObj : cell.ColorAdjEffect;
                                MinimapTint overTint = BuildMiniTint(overTintRaw, opts);
                                bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(overPkg);
                                BlendObjectImage(dstRgba, dstW, dstH, overImg, cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx, overTint, applyLum, opts.LuminanceSettings);
                            }
                            else
                            {
                                missingTextures++;
                            }
                        }
                    }

                    RenderOverlayCellRecordsForIndex(
                        overFrontOverlay.CellRecords,
                        ref overCursor,
                        index,
                        textures,
                        scaleDivisor,
                        cache,
                        dstRgba,
                        dstW,
                        dstH,
                        dstYOffset,
                        cropX,
                        cropY,
                        cellPxW,
                        cellPxH,
                        opts,
                        ref missingTextures);
                }
            }

            if (hasAnyObjectOverlays)
            {
                int totalGlobals = underFrontOverlay.GlobalRecords.Length + frontOverlay.GlobalRecords.Length + overFrontOverlay.GlobalRecords.Length;
                if (totalGlobals > 0)
                {
                    var globals = new List<DynamicOverlayRecord>(totalGlobals);
                    globals.AddRange(underFrontOverlay.GlobalRecords);
                    globals.AddRange(frontOverlay.GlobalRecords);
                    globals.AddRange(overFrontOverlay.GlobalRecords);
                    globals.Sort(CompareOverlayRecordOrderYx);

                    RenderOverlayRecords(
                        globals,
                        textures,
                        scaleDivisor,
                        cache,
                        dstRgba,
                        dstW,
                        dstH,
                        dstYOffset,
                        cropX,
                        cropY,
                        cellPxW,
                        cellPxH,
                        opts,
                        ref missingTextures);
                }
            }
        }
        else if (hasAnyObjectOverlays)
        {
            RenderOverlayDocuments(
                overlayState,
                textures,
                scaleDivisor,
                cache,
                dstRgba,
                dstW,
                dstH,
                dstYOffset,
                cropX,
                cropY,
                cellPxW,
                cellPxH,
                opts,
                ref missingTextures,
                ["underfront", "front", "overfront"]);
        }

        if (opts.ApplyLightingOverlay)
        {
            int maxAlpha = SceneLightMaskConfigProvider.GetMapLightLevel(sceneLightConfig, sceneLightMapId, opts.LightingOverlayMaxAlpha);
            ApplyLightingOverlay(dstRgba.AsSpan(0, dstW * dstH * 4), opts.NightFactor, maxAlpha);
        }

        if (opts.ApplyLightingOverlay)
        {
            byte sceneMaskAlpha = ComputeLightSpriteAlpha(opts.NightFactor);
            if (sceneMaskAlpha > 0 && SceneLightMaskConfigProvider.IsMapEnabled(sceneLightConfig, sceneLightMapId) && sceneLightConfig is not null)
            {
                var sceneMaskTint = new MinimapTint(255, 232, 186, sceneMaskAlpha);
                ApplySceneLightMasks(
                    map,
                    textures,
                    scaleDivisor,
                    cache,
                    dstRgba,
                    dstW,
                    dstH,
                    dstYOffset,
                    cropX,
                    cropY,
                    cellPxW,
                    cellPxH,
                    opts,
                    sceneLightConfig,
                    sceneMaskTint,
                    ref missingTextures);
            }
        }

        if (opts.IncludeLightSprites && includeOverFront)
        {
            byte alpha = ComputeLightSpriteAlpha(opts.NightFactor);
            if (alpha > 0)
            {
                var lightTint = new MinimapTint(255, 232, 186, alpha);

                for (int y = 0; y < map.Height; y++)
                {
                    int mapRow = y * map.Width;
                    int cellOriginY = y * cellPxH + yBase;

                    for (int x = 0; x < map.Width; x++)
                    {
                        int index = mapRow + x;
                        if ((uint)index >= (uint)map.Cells.Length)
                        {
                            continue;
                        }

                        NmpCellData cell = map.Cells[index];
                        if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                        {
                            continue;
                        }

                        if (!TryResolveExtraObject(cell.OverObject, out int packageId, out int imageIndex))
                        {
                            continue;
                        }

                        if (!IsLightImage(packageId, imageIndex))
                        {
                            continue;
                        }

                        int cellHeightLiftPx = 0;
                        if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                        {
                            cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scaleDivisor);
                        }

                        int cellOriginX = x * cellPxW + xBase;
                        DrawObjectTextured(
                            textures, packageId, imageIndex, scaleDivisor, cache,
                            dstRgba, dstW, dstH,
                            cellOriginX, cellOriginY,
                            cellPxH,
                            cellHeightLiftPx,
                            lightTint,
                            opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(packageId),
                            opts.LuminanceSettings,
                            ref missingTextures);
                    }
                }
            }
        }

    }

    public static bool TryRenderPlaceholderToRgba(
        MapDocument map,
        MinimapExportOptions opts,
        out byte[] rgba,
        out int width,
        out int height,
        out string error)
    {
        return TryRenderPlaceholderToRgba(map, opts, transparentBackground: false, out rgba, out width, out height, out error);
    }

    private static bool TryRenderPlaceholderToRgba(
        MapDocument map,
        MinimapExportOptions opts,
        bool transparentBackground,
        out byte[] rgba,
        out int width,
        out int height,
        out string error)
    {
        rgba = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        if (!IsPowerOfTwo(scale) || scale <= 0)
        {
            error = "ScaleDivisor 必须为 2 的幂（1/2/4/8/16/32）。";
            return false;
        }

        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        width = checked(map.Width * cellPxW);
        height = checked(map.Height * cellPxH);

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
        if (!transparentBackground)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }
        }

        bool includeBack = opts.IncludeBack;
        bool includeMiddle = opts.IncludeMiddle;
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        if (!includeBack && !includeMiddle && !includeFloor && !includeUnderFront && !includeFront && !includeOverFront)
        {
            error = "未选择任何导出层。";
            return false;
        }

        for (int y = 0; y < map.Height; y++)
        {
            int mapRow = y * map.Width;
            int py0 = y * cellPxH;

            for (int x = 0; x < map.Width; x++)
            {
                int index = mapRow + x;
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cell = map.Cells[index];
                if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                {
                    continue;
                }
                uint key = ResolveCellKey(cell, includeBack, includeMiddle, includeFloor, includeUnderFront, includeFront, includeOverFront);
                (byte r, byte g, byte b, byte a) = key == 0
                    ? (transparentBackground ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0, (byte)255))
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

    public static bool TryRenderTexturedToRgba(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        out byte[] rgba,
        out int width,
        out int height,
        out string error)
    {
        return TryRenderTexturedToRgba(map, textures, opts, transparentBackground: false, out rgba, out width, out height, out error);
    }

    private static bool TryRenderTexturedToRgba(
        MapDocument map,
        MapTextureIndex textures,
        MinimapExportOptions opts,
        bool transparentBackground,
        out byte[] rgba,
        out int width,
        out int height,
        out string error)
    {
        rgba = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空。";
            return false;
        }

        if (textures is null)
        {
            error = "贴图索引为空。";
            return false;
        }

        if (!textures.IsReady)
        {
            error = "贴图库未就绪（请先扫描贴图库：SGL/WPF）。";
            return false;
        }

        if (opts is null)
        {
            error = "导出选项为空。";
            return false;
        }

        if (map.Width <= 0 || map.Height <= 0)
        {
            error = "地图尺寸无效。";
            return false;
        }

        int scale = opts.ScaleDivisor;
        if (!IsPowerOfTwo(scale) || scale <= 0)
        {
            error = "ScaleDivisor 必须为 2 的幂（1/2/4/8/16/32）。";
            return false;
        }

        int cellPxW = MinimapCellW / scale;
        int cellPxH = MinimapCellH / scale;
        if (cellPxW <= 0 || cellPxH <= 0)
        {
            error = $"ScaleDivisor 过大：cellPxW={cellPxW}, cellPxH={cellPxH}（请使用 1/2/4/8/16/32）。";
            return false;
        }

        width = checked(map.Width * cellPxW);
        height = checked(map.Height * cellPxH);

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
        bool includeFloor = opts.IncludeFloor;
        bool includeUnderFront = opts.IncludeUnderFront;
        bool includeFront = opts.IncludeFront;
        bool includeOverFront = opts.IncludeOverFront;

        if (!includeBack && !includeMiddle && !includeFloor && !includeUnderFront && !includeFront && !includeOverFront)
        {
            error = "未选择任何导出层。";
            return false;
        }

        if (!transparentBackground)
        {
            // 单文件导出：旧工程为不透明黑底（便于直接作为一张“可见的完整图”使用）。
            FillSolidRgba(rgba, r: 0, g: 0, b: 0, a: 255);
        }

        var cache = new Dictionary<long, ScaledImage>();
        var mskMaskCache = new Dictionary<int, byte[]?>();
        SceneLightMaskConfig? sceneLightConfig = SceneLightMaskConfigProvider.GetForDataPath(textures.RootDirectory);
        string sceneLightMapId = SceneLightMaskConfigProvider.DeriveSceneLightMapId(map.Path);
        int missingTextures = 0;

        // ---- Pass 1: Middle layer (Tiles) ----
        // 与旧工程一致：Middle 先画（匹配实际地图渲染顺序）。
        if (includeMiddle)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scale);
                    }

                    int cellOriginX = x * cellPxW;
                    MinimapTint middleTint = BuildMiniTint(cell.ColorAdjTile, opts);

                    if (cell.MiddleImage2 != 0)
                    {
                        if (TryResolveMiddleGround(cell, out int groundPkg, out int groundIdx))
                        {
                            DrawTileTextured(
                                textures, groundPkg, groundIdx, scale, cache,
                                rgba, width, height,
                                cellOriginX, cellOriginY,
                                cellHeightLiftPx,
                                middleTint,
                                ref missingTextures);
                        }

                        if (TryResolveCoastComposite(cell, out int coastPkg, out int coastIdx, out int maskIdx))
                        {
                            uint coastTintRaw = cell.ColorAdjEffect != 0 ? cell.ColorAdjEffect : cell.ColorAdjTile;
                            MinimapTint coastTint = BuildMiniTint(coastTintRaw, opts);

                            DrawCoastCompositeTextured(
                                textures, coastPkg, coastIdx, maskIdx, scale, cache,
                                rgba, width, height,
                                cellOriginX, cellOriginY,
                                cellHeightLiftPx,
                                coastTint,
                                mskMaskCache,
                                ref missingTextures);
                        }

                        continue;
                    }

                    if (!TryResolveMiddleTile(cell, out int pkg, out int idx))
                    {
                        continue;
                    }

                    DrawTileTextured(
                        textures, pkg, idx, scale, cache,
                        rgba, width, height,
                        cellOriginX, cellOriginY,
                        cellHeightLiftPx,
                        middleTint,
                        ref missingTextures);
                }
            }
        }

        // ---- Pass 2: Back layer (SmTiles) ----
        if (includeBack)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    if (!TryResolveBackTile(cell, out int pkg, out int idx))
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scale);
                    }

                    int cellOriginX = x * cellPxW;
                    MinimapTint backTint = BuildMiniTint(cell.ColorAdjSmTile, opts);

                    DrawTileTextured(
                        textures, pkg, idx, scale, cache,
                        rgba, width, height,
                        cellOriginX, cellOriginY,
                        cellHeightLiftPx,
                        backTint,
                        ref missingTextures);
                }
            }
        }

        // ---- Pass 3: nearGround (floor objects) ----
        if (includeFloor)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    if (!TryResolveExtraObject(cell.NearGround, out int pkg, out int idx))
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scale);
                    }

                    int cellOriginX = x * cellPxW;
                    MinimapTint floorTint = BuildMiniTint(cell.ColorAdjFloor, opts);

                    DrawObjectTextured(
                        textures, pkg, idx, scale, cache,
                        rgba, width, height,
                        cellOriginX, cellOriginY,
                        cellPxH,
                        cellHeightLiftPx,
                        floorTint,
                        opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(pkg),
                        opts.LuminanceSettings,
                        ref missingTextures);
                }
            }
        }

        // ---- Pass 4: Object layers — single top-to-bottom, left-to-right traversal ----
        // 与旧工程一致：按 cell 的 Y 顺序遍历，并在一个 cell 内按
        // short-front -> underfront -> front(tall) -> overfront 的顺序绘制。
        if (includeUnderFront || includeFront || includeOverFront)
        {
            for (int y = 0; y < map.Height; y++)
            {
                int mapRow = y * map.Width;
                int cellOriginY = y * cellPxH;

                for (int x = 0; x < map.Width; x++)
                {
                    int index = mapRow + x;
                    if ((uint)index >= (uint)map.Cells.Length)
                    {
                        continue;
                    }

                    NmpCellData cell = map.Cells[index];
                    if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                    {
                        continue;
                    }

                    int cellHeightLiftPx = 0;
                    if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                    {
                        cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scale);
                    }

                    int objectHeightLiftPx = 0;
                    if (opts.ApplyObjectHeight && cell.ObjectHeight != 0)
                    {
                        objectHeightLiftPx = ScaleFloatPxRound(cell.ObjectHeight * opts.ObjectHeightScale, scale);
                    }

                    int frontLiftPx = cellHeightLiftPx + objectHeightLiftPx;
                    int cellOriginX = x * cellPxW;

                    int frontPkg = 0;
                    int frontIdx = 0;
                    ScaledImage frontImg = default;
                    bool frontImgOk = false;
                    bool frontIsNearGround = false;
                    bool frontIsShort = false;

                    // 4a. Short front objects (height <= 32 或 flags NEARGROUND)
                    if (includeFront && TryResolveFrontObject(cell, out frontPkg, out frontIdx))
                    {
                        if (TryGetScaledImage(textures, frontPkg, frontIdx, scale, cache, out frontImg))
                        {
                            frontImgOk = true;
                            frontIsNearGround = (cell.Flags & NmpFlagNearGround) != 0;
                            frontIsShort = frontImg.SourceHeight <= 32;
                            if (frontIsNearGround || frontIsShort)
                            {
                                MinimapTint frontTint = BuildMiniTint(cell.ColorAdjObject, opts);
                                bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(frontPkg);
                                BlendObjectImage(rgba, width, height, frontImg, cellOriginX, cellOriginY, cellPxH, frontLiftPx, frontTint, applyLum, opts.LuminanceSettings);
                            }
                        }
                        else
                        {
                            missingTextures++;
                        }
                    }

                    // 4b. UnderFront objects (underObject)
                    if (includeUnderFront && TryResolveExtraObject(cell.UnderObject, out int underPkg, out int underIdx))
                    {
                        if (TryGetScaledImage(textures, underPkg, underIdx, scale, cache, out ScaledImage underImg))
                        {
                            uint underTintRaw = cell.ColorAdjEffect != 0 ? cell.ColorAdjEffect : cell.ColorAdjObject;
                            MinimapTint underTint = BuildMiniTint(underTintRaw, opts);
                            bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(underPkg);
                            BlendObjectImage(rgba, width, height, underImg, cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx, underTint, applyLum, opts.LuminanceSettings);
                        }
                        else
                        {
                            missingTextures++;
                        }
                    }

                    // 4c. Front objects (tall only — skip short/nearGround already drawn in 4a)
                    if (includeFront && frontImgOk && !frontIsNearGround && !frontIsShort)
                    {
                        MinimapTint frontTint = BuildMiniTint(cell.ColorAdjObject, opts);
                        bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(frontPkg);
                        BlendObjectImage(rgba, width, height, frontImg, cellOriginX, cellOriginY, cellPxH, frontLiftPx, frontTint, applyLum, opts.LuminanceSettings);
                    }

                    // 4d. OverFront objects (overObject) — light images filtered out（单独在灯光 sprite pass 中渲染）
                    if (includeOverFront && TryResolveExtraObject(cell.OverObject, out int overPkg, out int overIdx))
                    {
                        if (!IsLightImage(overPkg, overIdx))
                        {
                            if (TryGetScaledImage(textures, overPkg, overIdx, scale, cache, out ScaledImage overImg))
                            {
                                uint overTintRaw = cell.ColorOverObj != 0 ? cell.ColorOverObj : cell.ColorAdjEffect;
                                MinimapTint overTint = BuildMiniTint(overTintRaw, opts);
                                bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(overPkg);
                                BlendObjectImage(rgba, width, height, overImg, cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx, overTint, applyLum, opts.LuminanceSettings);
                            }
                            else
                            {
                                missingTextures++;
                            }
                        }
                    }
                }
            }
        }

        if (opts.ApplyLightingOverlay)
        {
            int maxAlpha = SceneLightMaskConfigProvider.GetMapLightLevel(sceneLightConfig, sceneLightMapId, opts.LightingOverlayMaxAlpha);
            ApplyLightingOverlay(rgba, opts.NightFactor, maxAlpha);
        }

        if (opts.ApplyLightingOverlay)
        {
            byte sceneMaskAlpha = ComputeLightSpriteAlpha(opts.NightFactor);
            if (sceneMaskAlpha > 0 && SceneLightMaskConfigProvider.IsMapEnabled(sceneLightConfig, sceneLightMapId) && sceneLightConfig is not null)
            {
                var sceneMaskTint = new MinimapTint(255, 232, 186, sceneMaskAlpha);
                ApplySceneLightMasks(
                    map,
                    textures,
                    scale,
                    cache,
                    rgba,
                    width,
                    height,
                    dstYOffset: 0,
                    cropX: 0,
                    cropY: 0,
                    cellPxW,
                    cellPxH,
                    opts,
                    sceneLightConfig,
                    sceneMaskTint,
                    ref missingTextures);
            }
        }

        if (opts.IncludeLightSprites && includeOverFront)
        {
            byte alpha = ComputeLightSpriteAlpha(opts.NightFactor);
            if (alpha > 0)
            {
                var lightTint = new MinimapTint(255, 232, 186, alpha);

                for (int y = 0; y < map.Height; y++)
                {
                    int mapRow = y * map.Width;
                    int cellOriginY = y * cellPxH;

                    for (int x = 0; x < map.Width; x++)
                    {
                        int index = mapRow + x;
                        if ((uint)index >= (uint)map.Cells.Length)
                        {
                            continue;
                        }

                        NmpCellData cell = map.Cells[index];
                        if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                        {
                            continue;
                        }

                        if (!TryResolveExtraObject(cell.OverObject, out int packageId, out int imageIndex))
                        {
                            continue;
                        }

                        if (!IsLightImage(packageId, imageIndex))
                        {
                            continue;
                        }

                        int cellHeightLiftPx = 0;
                        if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                        {
                            cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scale);
                        }

                        int cellOriginX = x * cellPxW;
                        DrawObjectTextured(
                            textures, packageId, imageIndex, scale, cache,
                            rgba, width, height,
                            cellOriginX, cellOriginY,
                            cellPxH,
                            cellHeightLiftPx,
                            lightTint,
                            opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(packageId),
                            opts.LuminanceSettings,
                            ref missingTextures);
                    }
                }
            }
        }

        _ = missingTextures;
        return true;
    }

    private static uint ResolveCellKey(
        NmpCellData cell,
        bool includeBack,
        bool includeMiddle,
        bool includeFloor,
        bool includeUnderFront,
        bool includeFront,
        bool includeOverFront)
    {
        if (includeOverFront && cell.OverObject != 0)
        {
            return cell.OverObject;
        }

        if (includeFront)
        {
            uint front = cell.FrontImage & 0x00FFFFFFu;
            if (front != 0)
            {
                return front;
            }
        }

        if (includeUnderFront && cell.UnderObject != 0)
        {
            return cell.UnderObject;
        }

        if (includeFloor && cell.NearGround != 0)
        {
            return cell.NearGround;
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

    private readonly struct MinimapTint
    {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte A { get; }

        public bool IsNeutral => R == 255 && G == 255 && B == 255 && A == 255;

        public MinimapTint(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static MinimapTint White => new(255, 255, 255, 255);
    }

    private static int ScaleFloatPxRound(float px, int scaleDivisor)
    {
        if (!float.IsFinite(px))
        {
            return 0;
        }

        if (px <= 0.0f)
        {
            return 0;
        }

        double scaled = scaleDivisor <= 1 ? px : (px / scaleDivisor);
        return (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private static MinimapTint BuildMiniTint(uint rawTint, MinimapExportOptions opts)
    {
        if (opts is null)
        {
            return MinimapTint.White;
        }

        if (!opts.ApplyCellTints)
        {
            return MinimapTint.White;
        }

        uint rgb = rawTint & 0x00FFFFFFu;
        if (rgb == 0u || rgb == 0x00FFFFFFu)
        {
            return MinimapTint.White;
        }

        float strength = Math.Clamp(opts.TintStrength, 0.0f, 1.0f);
        if (strength <= 0.0f)
        {
            return MinimapTint.White;
        }

        float alphaScale = ((rawTint >> 24) & 0xFFu) != 0u
            ? ((rawTint >> 24) & 0xFFu) / 255.0f
            : 1.0f;
        float blend = Math.Clamp(strength * alphaScale, 0.0f, 1.0f);
        if (blend <= 0.0f)
        {
            return MinimapTint.White;
        }

        static byte MixChannel(byte src, float blend)
        {
            float value = 255.0f + (src - 255.0f) * blend;
            int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded < 0) rounded = 0;
            if (rounded > 255) rounded = 255;
            return (byte)rounded;
        }

        byte r = MixChannel((byte)((rgb >> 16) & 0xFFu), blend);
        byte g = MixChannel((byte)((rgb >> 8) & 0xFFu), blend);
        byte b = MixChannel((byte)(rgb & 0xFFu), blend);
        return new MinimapTint(r, g, b, 255);
    }

    private static void ApplyLightingOverlay(byte[] rgba, float nightFactor, int maxAlpha)
    {
        if (rgba is null || rgba.Length == 0)
        {
            return;
        }

        ApplyLightingOverlay(rgba.AsSpan(), nightFactor, maxAlpha);
    }

    private static void ApplyLightingOverlay(Span<byte> rgba, float nightFactor, int maxAlpha)
    {
        if (rgba.IsEmpty)
        {
            return;
        }

        if (!float.IsFinite(nightFactor))
        {
            return;
        }

        nightFactor = Math.Clamp(nightFactor, 0.0f, 1.0f);
        if (nightFactor <= 0.0f)
        {
            return;
        }

        maxAlpha = Math.Clamp(maxAlpha, 0, 255);
        if (maxAlpha == 0)
        {
            return;
        }

        int overlayAlpha = (int)Math.Round(nightFactor * maxAlpha, MidpointRounding.AwayFromZero);
        overlayAlpha = Math.Clamp(overlayAlpha, 0, 255);
        if (overlayAlpha == 0)
        {
            return;
        }

        const int overlayR = 24;
        const int overlayG = 34;
        const int overlayB = 58;

        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int pixelAlpha = rgba[i + 3];
            if (pixelAlpha == 0)
            {
                continue;
            }

            int alpha = (overlayAlpha * pixelAlpha) / 255;
            int inv = 255 - alpha;

            rgba[i + 0] = (byte)((overlayR * alpha + rgba[i + 0] * inv) / 255);
            rgba[i + 1] = (byte)((overlayG * alpha + rgba[i + 1] * inv) / 255);
            rgba[i + 2] = (byte)((overlayB * alpha + rgba[i + 2] * inv) / 255);
        }
    }

    private static byte ComputeLightSpriteAlpha(float nightFactor)
    {
        if (!float.IsFinite(nightFactor))
        {
            return 0;
        }

        float factor = Math.Clamp(nightFactor, 0.0f, 1.0f);
        float strength = Math.Clamp((factor - 0.08f) / 0.92f, 0.0f, 1.0f);
        if (strength <= 0.0f)
        {
            return 0;
        }

        int alpha = (int)Math.Round(48.0f + strength * 160.0f, MidpointRounding.AwayFromZero);
        alpha = Math.Clamp(alpha, 0, 255);
        return (byte)alpha;
    }

    private static void ApplySceneLightMasks(
        MapDocument map,
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        SceneLightMaskConfig config,
        MinimapTint tint,
        ref int missingTextures)
    {
        if (map is null || textures is null || opts is null || config is null)
        {
            return;
        }

        if (tint.A == 0)
        {
            return;
        }

        int xBase = -cropX;
        int yBase = -cropY - dstYOffset;

        for (int y = 0; y < map.Height; y++)
        {
            int mapRow = y * map.Width;
            int cellOriginY = y * cellPxH + yBase;

            for (int x = 0; x < map.Width; x++)
            {
                int index = mapRow + x;
                if ((uint)index >= (uint)map.Cells.Length)
                {
                    continue;
                }

                NmpCellData cell = map.Cells[index];
                if (opts.SuppressBorderCells && (cell.Flags & NmpFlagBorderCell) != 0)
                {
                    continue;
                }

                int cellHeightLiftPx = 0;
                if (opts.ApplyCellHeightFlag && (cell.Flags & NmpFlagNearGround) != 0)
                {
                    cellHeightLiftPx = ScaleFloatPxRound(opts.CellHeightFlagOffset, scaleDivisor);
                }

                int objectHeightLiftPx = 0;
                if (opts.ApplyObjectHeight && cell.ObjectHeight != 0)
                {
                    objectHeightLiftPx = ScaleFloatPxRound(cell.ObjectHeight * opts.ObjectHeightScale, scaleDivisor);
                }

                int frontLiftPx = cellHeightLiftPx + objectHeightLiftPx;
                int cellOriginX = x * cellPxW + xBase;

                if (TryResolveExtraObject(cell.NearGround, out int pkg, out int idx))
                {
                    DrawSceneLightMaskForObject(
                        textures, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx,
                        pkg, idx,
                        config, tint,
                        ref missingTextures);
                }

                if (TryResolveExtraObject(cell.UnderObject, out pkg, out idx))
                {
                    DrawSceneLightMaskForObject(
                        textures, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx,
                        pkg, idx,
                        config, tint,
                        ref missingTextures);
                }

                if (TryResolveFrontObject(cell, out pkg, out idx))
                {
                    DrawSceneLightMaskForObject(
                        textures, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY, cellPxH, frontLiftPx,
                        pkg, idx,
                        config, tint,
                        ref missingTextures);
                }

                if (TryResolveExtraObject(cell.OverObject, out pkg, out idx))
                {
                    DrawSceneLightMaskForObject(
                        textures, scaleDivisor, cache,
                        dstRgba, dstW, dstH,
                        cellOriginX, cellOriginY, cellPxH, cellHeightLiftPx,
                        pkg, idx,
                        config, tint,
                        ref missingTextures);
                }
            }
        }
    }

    private static void DrawSceneLightMaskForObject(
        MapTextureIndex textures,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int cellOriginX,
        int cellOriginY,
        int cellHeight,
        int liftY,
        int objectPackageId,
        int textureId,
        SceneLightMaskConfig config,
        MinimapTint tint,
        ref int missingTextures)
    {
        IReadOnlyList<SceneLightMaskRule>? rules = SceneLightMaskConfigProvider.FindRules(config, objectPackageId, textureId);
        if (rules is null || rules.Count == 0)
        {
            return;
        }

        for (int i = 0; i < rules.Count; i++)
        {
            SceneLightMaskRule rule = rules[i];
            if (!TryGetScaledImage(textures, DefaultCoastPackageId, rule.MaskImageId, scaleDivisor, cache, out ScaledImage maskImg))
            {
                missingTextures++;
                continue;
            }

            float scaleFactor = Math.Max(1, rule.ScalePercent) / 100.0f;
            int scaledW = Math.Max(1, RoundAwayFromZero(maskImg.Width * scaleFactor));
            int scaledH = Math.Max(1, RoundAwayFromZero(maskImg.Height * scaleFactor));
            int posX = ScaleIntRound(rule.PosX, scaleDivisor);
            int posY = ScaleIntRound(rule.PosY, scaleDivisor);

            int dx = cellOriginX - RoundAwayFromZero(maskImg.CenterX * scaleFactor) + RoundAwayFromZero(maskImg.OffsetX * scaleFactor) + posX;
            int dy = cellOriginY + cellHeight - scaledH - RoundAwayFromZero(maskImg.CenterY * scaleFactor) + RoundAwayFromZero(maskImg.OffsetY * scaleFactor) + posY - liftY;

            if (scaledW == maskImg.Width && scaledH == maskImg.Height)
            {
                BlendImage(dstRgba, dstW, dstH, maskImg, dx, dy, tint);
            }
            else
            {
                BlendImageScaled(dstRgba, dstW, dstH, maskImg, dx, dy, scaledW, scaledH, tint);
            }
        }
    }

    private static int ScaleIntRound(int px, int scaleDivisor)
    {
        if (scaleDivisor <= 1)
        {
            return px;
        }

        return (int)Math.Round(px / (double)scaleDivisor, MidpointRounding.AwayFromZero);
    }

    private static int RoundAwayFromZero(float value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static bool TryResolveExtraObject(uint raw, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (raw == 0)
        {
            return false;
        }

        imageIndex = (int)(raw & 0xFFFF);
        if (imageIndex == 0)
        {
            return false;
        }

        packageId = (int)((raw >> 16) & 0xFF);
        if (packageId == 0)
        {
            packageId = DefaultObjectPackageId;
        }

        return true;
    }

    private static bool IsLightImage(int packageId, int imageIndex)
    {
        if (packageId != 38)
        {
            return false;
        }

        return (imageIndex >= 9249 && imageIndex <= 9271) || (imageIndex >= 9499 && imageIndex <= 9518);
    }

    private static bool IsLuminanceAlphaPackage(int packageId)
    {
        return packageId is 46 or 47;
    }

    private static bool TryResolveBackTile(NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (cell.BackImage == 0)
        {
            return false;
        }

        imageIndex = cell.BackImage;
        packageId = cell.BackLibrary != 0 ? cell.BackLibrary : DefaultBackPackageId;
        return packageId > 0;
    }

    private static bool TryResolveMiddleTile(NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (cell.MiddleImage == 0)
        {
            return false;
        }

        imageIndex = cell.MiddleImage;
        packageId = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultMiddlePackageId;
        return packageId > 0;
    }

    private static bool TryResolveMiddleGround(NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (cell.MiddleImage2 == 0)
        {
            return false;
        }

        imageIndex = cell.MiddleImage2;
        packageId = cell.MiddleLibrary2 != 0 ? cell.MiddleLibrary2 : DefaultMiddlePackageId;
        return packageId > 0;
    }

    private static bool TryResolveCoastComposite(NmpCellData cell, out int packageId, out int imageIndex, out int maskImageIndex)
    {
        packageId = 0;
        imageIndex = 0;
        maskImageIndex = 0;

        if (cell.MiddleImage == 0 || cell.MiddleAlphaMask == 0)
        {
            return false;
        }

        imageIndex = cell.MiddleImage;
        maskImageIndex = cell.MiddleAlphaMask;

        // MiddleImage2 存在时：旧工程默认 coastPkg=49(effect)；否则走常规 tiles 包（3051...）
        packageId = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultCoastPackageId;
        return packageId > 0;
    }

    private static bool TryResolveFrontObject(NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (cell.FrontImage == 0)
        {
            return false;
        }

        imageIndex = (int)(cell.FrontImage & 0xFFFF);
        if (imageIndex == 0)
        {
            return false;
        }

        packageId = (int)((cell.FrontImage >> 16) & 0xFF);
        if (packageId == 0)
        {
            packageId = cell.FrontLibrary;
        }

        if (packageId == 0)
        {
            packageId = DefaultObjectPackageId;
        }

        return packageId > 0;
    }

    private static void DrawTileTextured(
        MapTextureIndex textures,
        int packageId,
        int imageIndex,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int cellOriginX,
        int cellOriginY,
        int liftY,
        MinimapTint tint,
        ref int missingTextures)
    {
        if (packageId <= 0 || imageIndex <= 0)
        {
            return;
        }

        if (!TryGetScaledImage(textures, packageId, imageIndex, scaleDivisor, cache, out ScaledImage img))
        {
            missingTextures++;
            return;
        }

        BlendTileImage(dstRgba, dstW, dstH, img, cellOriginX, cellOriginY, liftY, tint);
    }

    private static void DrawObjectTextured(
        MapTextureIndex textures,
        int packageId,
        int imageIndex,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int cellOriginX,
        int cellOriginY,
        int cellHeight,
        int liftY,
        MinimapTint tint,
        bool applyLuminanceToAlpha,
        LuminanceSettings luminanceSettings,
        ref int missingTextures)
    {
        if (packageId <= 0 || imageIndex <= 0)
        {
            return;
        }

        if (!TryGetScaledImage(textures, packageId, imageIndex, scaleDivisor, cache, out ScaledImage img))
        {
            missingTextures++;
            return;
        }

        BlendObjectImage(dstRgba, dstW, dstH, img, cellOriginX, cellOriginY, cellHeight, liftY, tint, applyLuminanceToAlpha, luminanceSettings);
    }

    private static void DrawCoastCompositeTextured(
        MapTextureIndex textures,
        int packageId,
        int imageIndex,
        int maskImageIndex,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int cellOriginX,
        int cellOriginY,
        int liftY,
        MinimapTint tint,
        Dictionary<int, byte[]?> mskMaskCache,
        ref int missingTextures)
    {
        if (packageId <= 0 || imageIndex <= 0 || maskImageIndex <= 0)
        {
            return;
        }

        if (!TryGetScaledImage(textures, packageId, imageIndex, scaleDivisor, cache, out ScaledImage coastImg))
        {
            missingTextures++;
            return;
        }

        bool preferTex = textures.CoastMaskPreferTex;
        if (preferTex)
        {
            if (TryGetScaledImage(textures, packageId, maskImageIndex, scaleDivisor, cache, out ScaledImage maskImg))
            {
                int texDrawX = cellOriginX + coastImg.OffsetX;
                int texDrawY = cellOriginY + coastImg.OffsetY - liftY;
                BlendCoastComposite(dstRgba, dstW, dstH, coastImg, maskImg, texDrawX, texDrawY, tint);
                return;
            }

            // TEX mask 不存在时：按旧工程规则回退读取 `.msk`（优先 dataRoot/mask/...；若不存在则回退 WPF 内的 mask 条目）。
            if (!TryGetMskMaskBytes(textures, maskImageIndex, mskMaskCache, out byte[] maskBits))
            {
                return;
            }

            int drawX = cellOriginX + coastImg.OffsetX;
            int drawY = cellOriginY + coastImg.OffsetY - liftY;
            BlendCoastCompositeMsk(dstRgba, dstW, dstH, coastImg, maskBits, drawX, drawY, tint);
            return;
        }

        // coast_mask_source=msk: 优先使用 `.msk`。
        if (TryGetMskMaskBytes(textures, maskImageIndex, mskMaskCache, out byte[] preferMskBits))
        {
            int drawX = cellOriginX + coastImg.OffsetX;
            int drawY = cellOriginY + coastImg.OffsetY - liftY;
            BlendCoastCompositeMsk(dstRgba, dstW, dstH, coastImg, preferMskBits, drawX, drawY, tint);
            return;
        }

        if (TryGetScaledImage(textures, packageId, maskImageIndex, scaleDivisor, cache, out ScaledImage fallbackMask))
        {
            int texDrawX = cellOriginX + coastImg.OffsetX;
            int texDrawY = cellOriginY + coastImg.OffsetY - liftY;
            BlendCoastComposite(dstRgba, dstW, dstH, coastImg, fallbackMask, texDrawX, texDrawY, tint);
        }
    }

    private static void DrawOverlayRecord(
        MapTextureIndex textures,
        DynamicOverlayRecord record,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int dstYOffset,
        int cropX,
        int cropY,
        int cellPxW,
        int cellPxH,
        MinimapExportOptions opts,
        ref int missingTextures)
    {
        if (record.PackageId <= 0 || record.ImageId <= 0)
        {
            return;
        }

        if (!TryGetScaledImage(textures, record.PackageId, record.ImageId, scaleDivisor, cache, out ScaledImage img, record.Frame))
        {
            missingTextures++;
            return;
        }

        int baseX = record.CoordinateSpace.Equals("cell", StringComparison.OrdinalIgnoreCase)
            ? checked(record.X * cellPxW)
            : ScaleIntRound(record.X, scaleDivisor);
        int baseY = record.CoordinateSpace.Equals("cell", StringComparison.OrdinalIgnoreCase)
            ? checked(record.Y * cellPxH)
            : ScaleIntRound(record.Y, scaleDivisor);

        int drawX = baseX - cropX + img.OffsetX + ScaleIntRound(record.OffsetX, scaleDivisor);
        int drawY = baseY - cropY - dstYOffset + img.OffsetY + ScaleIntRound(record.OffsetY, scaleDivisor);

        int scaledW = record.Scale <= 0f ? img.Width : Math.Max(1, RoundAwayFromZero(img.Width * record.Scale));
        int scaledH = record.Scale <= 0f ? img.Height : Math.Max(1, RoundAwayFromZero(img.Height * record.Scale));
        MinimapTint tint = BuildOverlayTint(record);
        bool additive = record.BlendMode.Equals("additive", StringComparison.OrdinalIgnoreCase);
        bool applyLum = opts.ApplyLuminanceToAlpha && IsLuminanceAlphaPackage(record.PackageId);

        if (scaledW == img.Width && scaledH == img.Height)
        {
            BlendImageExt(dstRgba, dstW, dstH, img, drawX, drawY, tint, additive, applyLum, opts.LuminanceSettings);
            return;
        }

        BlendImageScaledExt(dstRgba, dstW, dstH, img, drawX, drawY, scaledW, scaledH, tint, additive, applyLum, opts.LuminanceSettings);
    }

    private static MinimapTint BuildOverlayTint(DynamicOverlayRecord record)
    {
        int alpha = (record.Alpha * record.TintA + 127) / 255;
        return new MinimapTint(record.TintR, record.TintG, record.TintB, (byte)Math.Clamp(alpha, 0, 255));
    }

    private static bool TryGetMskMaskBytes(
        MapTextureIndex textures,
        int maskImageIndex,
        Dictionary<int, byte[]?> cache,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (maskImageIndex <= 0)
        {
            return false;
        }

        if (cache is not null && cache.TryGetValue(maskImageIndex, out byte[]? cached))
        {
            if (cached is null || cached.Length == 0)
            {
                return false;
            }

            bytes = cached;
            return true;
        }

        if (!TryLoadMskMaskBytesFromDataRoot(textures?.RootDirectory, maskImageIndex, out bytes)
            && !TryLoadMskMaskBytesFromWpf(textures?.RootDirectory, maskImageIndex, out bytes))
        {
            if (cache is not null)
            {
                cache[maskImageIndex] = null;
            }

            bytes = Array.Empty<byte>();
            return false;
        }

        if (cache is not null)
        {
            cache[maskImageIndex] = bytes;
        }

        return bytes.Length > 0;
    }

    private static bool TryLoadMskMaskBytesFromDataRoot(string? dataRootDirectory, int maskImageIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(dataRootDirectory))
        {
            return false;
        }

        string root = dataRootDirectory;

        int folderId = maskImageIndex / 100;
        string folder = folderId.ToString("D3");
        string fileStem = maskImageIndex.ToString("D5");

        string pathUpper = Path.Combine(root, "mask", folder, $"{fileStem}.Msk");
        if (TryReadAllBytesSafe(pathUpper, out bytes))
        {
            return true;
        }

        string pathLower = Path.Combine(root, "mask", folder, $"{fileStem}.msk");
        if (TryReadAllBytesSafe(pathLower, out bytes))
        {
            return true;
        }

        return false;
    }

    private static bool TryLoadMskMaskBytesFromWpf(string? dataRootDirectory, int maskImageIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (maskImageIndex <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(dataRootDirectory))
        {
            return false;
        }

        string root = dataRootDirectory;
        if (!Directory.Exists(root))
        {
            try
            {
                string? parent = Path.GetDirectoryName(root);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    root = parent;
                }
            }
            catch
            {
                return false;
            }
        }

        if (!Directory.Exists(root))
        {
            return false;
        }

        string[] wpfPaths;
        try
        {
            wpfPaths = Directory.GetFiles(root, "*.wpf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return false;
        }

        if (wpfPaths.Length == 0)
        {
            return false;
        }

        Array.Sort(wpfPaths, CompareWpfPaths);

        int folderId = maskImageIndex / 100;
        string folder = folderId.ToString("D3");
        string fileStem = maskImageIndex.ToString("D5");

        // 兼容大小写：WPF 内路径按 OrdinalIgnoreCase 匹配；这里仍给出两套候选，便于不一致的归档内容。
        string relUpper = $"mask/{folder}/{fileStem}.Msk";
        string relLower = $"mask/{folder}/{fileStem}.msk";
        string relDataUpper = $"Data/mask/{folder}/{fileStem}.Msk";
        string relDataLower = $"Data/mask/{folder}/{fileStem}.msk";

        foreach (string wpf in wpfPaths)
        {
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relUpper, out bytes, out _))
            {
                return true;
            }
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relLower, out bytes, out _))
            {
                return true;
            }
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relDataUpper, out bytes, out _))
            {
                return true;
            }
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relDataLower, out bytes, out _))
            {
                return true;
            }
        }

        bytes = Array.Empty<byte>();
        return false;

        static int CompareWpfPaths(string? a, string? b)
        {
            a ??= string.Empty;
            b ??= string.Empty;

            int ap = GetWpfPriority(a);
            int bp = GetWpfPriority(b);
            bool aHas = ap >= 0;
            bool bHas = bp >= 0;
            if (aHas != bHas)
            {
                // Prefer numbered archives first (Texture9 > Texture2), then others.
                return aHas ? -1 : 1;
            }

            if (aHas && bHas && ap != bp)
            {
                // Larger number first.
                return bp.CompareTo(ap);
            }

            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static int GetWpfPriority(string path)
        {
            string stem;
            try
            {
                stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            }
            catch
            {
                return -1;
            }

            if (string.IsNullOrWhiteSpace(stem))
            {
                return -1;
            }

            int end = stem.Length;
            int start = end;
            while (start > 0 && char.IsDigit(stem[start - 1]))
            {
                start--;
            }

            if (start == end)
            {
                return -1;
            }

            if (int.TryParse(stem.Substring(start), out int value))
            {
                return value;
            }

            return -1;
        }
    }

    private static bool TryReadAllBytesSafe(string path, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            return bytes.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static void BlendTileImage(byte[] dstRgba, int dstW, int dstH, ScaledImage src, int cellOriginX, int cellOriginY, int liftY, MinimapTint tint)
    {
        int drawX = cellOriginX + src.OffsetX;
        int drawY = cellOriginY + src.OffsetY;
        BlendImage(dstRgba, dstW, dstH, src, drawX, drawY - liftY, tint);
    }

    private static void BlendObjectImage(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage src,
        int cellOriginX,
        int cellOriginY,
        int cellHeight,
        int liftY,
        MinimapTint tint,
        bool applyLuminanceToAlpha,
        LuminanceSettings luminanceSettings)
    {
        int baseY = cellOriginY + cellHeight - src.Height;
        int drawX = cellOriginX - src.CenterX + src.OffsetX;
        int drawY = baseY - src.CenterY + src.OffsetY;
        BlendImage(dstRgba, dstW, dstH, src, drawX, drawY - liftY, tint, applyLuminanceToAlpha, luminanceSettings);
    }

    private static void BlendCoastComposite(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage coast,
        ScaledImage mask,
        int drawX,
        int drawY,
        MinimapTint tint)
    {
        if (!coast.IsValid || !mask.IsValid)
        {
            return;
        }

        int srcW = coast.Width;
        int srcH = coast.Height;

        int srcStartX = 0;
        int srcStartY = 0;
        int dstStartX = drawX;
        int dstStartY = drawY;

        if (dstStartX < 0)
        {
            srcStartX = -dstStartX;
            dstStartX = 0;
        }

        if (dstStartY < 0)
        {
            srcStartY = -dstStartY;
            dstStartY = 0;
        }

        int copyW = Math.Min(srcW - srcStartX, dstW - dstStartX);
        int copyH = Math.Min(srcH - srcStartY, dstH - dstStartY);
        if (copyW <= 0 || copyH <= 0)
        {
            return;
        }

        bool sameDims = mask.Width == srcW && mask.Height == srcH;
        ReadOnlySpan<byte> c = coast.Rgba8;
        ReadOnlySpan<byte> m = mask.Rgba8;
        Span<byte> d = dstRgba;

        int cStride = srcW * 4;
        int dStride = dstW * 4;

        for (int y = 0; y < copyH; y++)
        {
            int srcY = srcStartY + y;
            int dstY = dstStartY + y;
            int cRow = srcY * cStride + srcStartX * 4;
            int dRow = dstY * dStride + dstStartX * 4;

            for (int x = 0; x < copyW; x++)
            {
                int cIdx = cRow + x * 4;

                byte sa0 = c[cIdx + 3];
                if (sa0 == 0)
                {
                    continue;
                }

                int mx;
                int my;
                if (sameDims)
                {
                    mx = srcStartX + x;
                    my = srcY;
                }
                else
                {
                    mx = (srcW > 0) ? ((srcStartX + x) * mask.Width / srcW) : 0;
                    my = (srcH > 0) ? (srcY * mask.Height / srcH) : 0;
                }

                mx = Math.Clamp(mx, 0, mask.Width - 1);
                my = Math.Clamp(my, 0, mask.Height - 1);

                int mIdx = (my * mask.Width + mx) * 4;
                byte mr = m[mIdx + 0];
                byte mg = m[mIdx + 1];
                byte mb = m[mIdx + 2];
                byte maskLum = (byte)((mr * 77 + mg * 150 + mb * 29) >> 8);

                // Invert mask: bright mask => coast transparent.
                int sa = (sa0 * (255 - maskLum) + 127) / 255;
                if (tint.A != 255)
                {
                    sa = (sa * tint.A + 127) / 255;
                }

                if (sa <= 0)
                {
                    continue;
                }

                byte sr = c[cIdx + 0];
                byte sg = c[cIdx + 1];
                byte sb = c[cIdx + 2];

                if (!tint.IsNeutral)
                {
                    sr = (byte)((sr * tint.R + 127) / 255);
                    sg = (byte)((sg * tint.G + 127) / 255);
                    sb = (byte)((sb * tint.B + 127) / 255);
                }

                int dp = dRow + x * 4;

                if (sa >= 255)
                {
                    d[dp + 0] = sr;
                    d[dp + 1] = sg;
                    d[dp + 2] = sb;
                    d[dp + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                d[dp + 0] = (byte)((sr * sa + d[dp + 0] * inv + 127) / 255);
                d[dp + 1] = (byte)((sg * sa + d[dp + 1] * inv + 127) / 255);
                d[dp + 2] = (byte)((sb * sa + d[dp + 2] * inv + 127) / 255);
                byte da = d[dp + 3];
                d[dp + 3] = (byte)(sa + (da * inv + 127) / 255);
            }
        }
    }

    private static void BlendCoastCompositeMsk(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage coast,
        byte[] maskBits,
        int drawX,
        int drawY,
        MinimapTint tint)
    {
        if (!coast.IsValid)
        {
            return;
        }

        if (maskBits is null || maskBits.Length == 0)
        {
            return;
        }

        int srcW = coast.Width;
        int srcH = coast.Height;

        int srcStartX = 0;
        int srcStartY = 0;
        int dstStartX = drawX;
        int dstStartY = drawY;

        if (dstStartX < 0)
        {
            srcStartX = -dstStartX;
            dstStartX = 0;
        }

        if (dstStartY < 0)
        {
            srcStartY = -dstStartY;
            dstStartY = 0;
        }

        int copyW = Math.Min(srcW - srcStartX, dstW - dstStartX);
        int copyH = Math.Min(srcH - srcStartY, dstH - dstStartY);
        if (copyW <= 0 || copyH <= 0)
        {
            return;
        }

        int mw = coast.SourceWidth > 0 ? coast.SourceWidth : srcW;
        int mh = coast.SourceHeight > 0 ? coast.SourceHeight : srcH;

        long bitCount = (long)mw * mh;
        if (bitCount <= 0)
        {
            return;
        }

        long bytesNeeded = (bitCount + 7) / 8;
        if (maskBits.Length < bytesNeeded)
        {
            return;
        }

        ReadOnlySpan<byte> c = coast.Rgba8;
        Span<byte> d = dstRgba;

        int cStride = srcW * 4;
        int dStride = dstW * 4;

        for (int y = 0; y < copyH; y++)
        {
            int srcY = srcStartY + y;
            int dstY = dstStartY + y;
            int cRow = srcY * cStride + srcStartX * 4;
            int dRow = dstY * dStride + dstStartX * 4;

            int origY = mh > 0 ? (int)((long)srcY * mh / srcH) : 0;
            if (origY < 0) origY = 0;
            if (origY >= mh) origY = mh - 1;

            for (int x = 0; x < copyW; x++)
            {
                int srcX = srcStartX + x;
                int cIdx = cRow + x * 4;

                byte sa0 = c[cIdx + 3];
                if (sa0 == 0)
                {
                    continue;
                }

                int origX = mw > 0 ? (int)((long)srcX * mw / srcW) : 0;
                if (origX < 0) origX = 0;
                if (origX >= mw) origX = mw - 1;

                long bitIndex = (long)origY * mw + origX;
                int byteIndex = (int)(bitIndex / 8);
                int bitInByte = 7 - (int)(bitIndex % 8);
                bool on = ((maskBits[byteIndex] >> bitInByte) & 0x01) != 0;
                int maskLum = on ? 255 : 0;

                int sa = (sa0 * (255 - maskLum) + 127) / 255;
                if (tint.A != 255)
                {
                    sa = (sa * tint.A + 127) / 255;
                }

                if (sa <= 0)
                {
                    continue;
                }

                byte sr = c[cIdx + 0];
                byte sg = c[cIdx + 1];
                byte sb = c[cIdx + 2];

                if (!tint.IsNeutral)
                {
                    sr = (byte)((sr * tint.R + 127) / 255);
                    sg = (byte)((sg * tint.G + 127) / 255);
                    sb = (byte)((sb * tint.B + 127) / 255);
                }

                int dp = dRow + x * 4;

                if (sa >= 255)
                {
                    d[dp + 0] = sr;
                    d[dp + 1] = sg;
                    d[dp + 2] = sb;
                    d[dp + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                d[dp + 0] = (byte)((sr * sa + d[dp + 0] * inv + 127) / 255);
                d[dp + 1] = (byte)((sg * sa + d[dp + 1] * inv + 127) / 255);
                d[dp + 2] = (byte)((sb * sa + d[dp + 2] * inv + 127) / 255);
                byte da = d[dp + 3];
                d[dp + 3] = (byte)(sa + (da * inv + 127) / 255);
            }
        }
    }

    private static void DrawCellLayerTextured(
        MapTextureIndex textures,
        MapLayer layer,
        NmpCellData cell,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int cellOriginX,
        int cellOriginY,
        int cellHeight,
        int liftY,
        MinimapTint tint,
        ref int missingTextures)
    {
        int packageId;
        int imageIndex;
        bool isObject = layer == MapLayer.Front;

        if (layer == MapLayer.Back)
        {
            packageId = cell.BackLibrary;
            imageIndex = cell.BackImage;
        }
        else if (layer == MapLayer.Middle)
        {
            packageId = cell.MiddleLibrary;
            imageIndex = cell.MiddleImage;
        }
        else if (layer == MapLayer.Front)
        {
            packageId = cell.FrontLibrary;
            imageIndex = (int)(cell.FrontImage & 0xFFFF);
        }
        else
        {
            return;
        }

        if (packageId <= 0 || imageIndex <= 0)
        {
            return;
        }

        if (!TryGetScaledImage(textures, packageId, imageIndex, scaleDivisor, cache, out ScaledImage img))
        {
            missingTextures++;
            return;
        }

        int drawX;
        int drawY;

        if (!isObject)
        {
            drawX = cellOriginX + img.OffsetX;
            drawY = cellOriginY + img.OffsetY;
        }
        else
        {
            int baseY = cellOriginY + cellHeight - img.Height;
            drawX = cellOriginX - img.CenterX + img.OffsetX;
            drawY = baseY - img.CenterY + img.OffsetY;
        }

        BlendImage(dstRgba, dstW, dstH, img, drawX, drawY - liftY, tint);
    }

    private static void DrawExtraObjectTextured(
        MapTextureIndex textures,
        int packageId,
        int imageIndex,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        byte[] dstRgba,
        int dstW,
        int dstH,
        int cellOriginX,
        int cellOriginY,
        int cellHeight,
        int liftY,
        MinimapTint tint,
        ref int missingTextures)
    {
        if (packageId <= 0 || imageIndex <= 0)
        {
            return;
        }

        if (!TryGetScaledImage(textures, packageId, imageIndex, scaleDivisor, cache, out ScaledImage img))
        {
            missingTextures++;
            return;
        }

        int baseY = cellOriginY + cellHeight - img.Height;
        int drawX = cellOriginX - img.CenterX + img.OffsetX;
        int drawY = baseY - img.CenterY + img.OffsetY;

        BlendImage(dstRgba, dstW, dstH, img, drawX, drawY - liftY, tint);
    }

    private static void BlendImageScaled(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage src,
        int drawX,
        int drawY,
        int scaledW,
        int scaledH,
        MinimapTint tint)
    {
        if (!src.IsValid)
        {
            return;
        }

        if (scaledW <= 0 || scaledH <= 0)
        {
            return;
        }

        int srcStartX = 0;
        int srcStartY = 0;
        int dstStartX = drawX;
        int dstStartY = drawY;

        if (dstStartX < 0)
        {
            srcStartX = -dstStartX;
            dstStartX = 0;
        }

        if (dstStartY < 0)
        {
            srcStartY = -dstStartY;
            dstStartY = 0;
        }

        int copyW = Math.Min(scaledW - srcStartX, dstW - dstStartX);
        int copyH = Math.Min(scaledH - srcStartY, dstH - dstStartY);
        if (copyW <= 0 || copyH <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> s = src.Rgba8;
        Span<byte> d = dstRgba;

        int srcStride = src.Width * 4;
        int dstStride = dstW * 4;

        for (int y = 0; y < copyH; y++)
        {
            int outY = srcStartY + y;
            int srcY = (int)((long)outY * src.Height / scaledH);
            srcY = Math.Clamp(srcY, 0, src.Height - 1);

            int srcRow = srcY * srcStride;
            int dstRow = (dstStartY + y) * dstStride + dstStartX * 4;

            for (int x = 0; x < copyW; x++)
            {
                int outX = srcStartX + x;
                int srcX = (int)((long)outX * src.Width / scaledW);
                srcX = Math.Clamp(srcX, 0, src.Width - 1);

                int sp = srcRow + srcX * 4;
                int dp = dstRow + x * 4;

                byte sr = s[sp + 0];
                byte sg = s[sp + 1];
                byte sb = s[sp + 2];

                if (!tint.IsNeutral)
                {
                    sr = (byte)((sr * tint.R + 127) / 255);
                    sg = (byte)((sg * tint.G + 127) / 255);
                    sb = (byte)((sb * tint.B + 127) / 255);
                }

                int sa = s[sp + 3];
                if (tint.A != 255)
                {
                    sa = (sa * tint.A + 127) / 255;
                }

                if (sa <= 0)
                {
                    continue;
                }

                if (sa == 255)
                {
                    d[dp + 0] = sr;
                    d[dp + 1] = sg;
                    d[dp + 2] = sb;
                    d[dp + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                d[dp + 0] = (byte)((sr * sa + d[dp + 0] * inv + 127) / 255);
                d[dp + 1] = (byte)((sg * sa + d[dp + 1] * inv + 127) / 255);
                d[dp + 2] = (byte)((sb * sa + d[dp + 2] * inv + 127) / 255);
                byte da = d[dp + 3];
                d[dp + 3] = (byte)(sa + (da * inv + 127) / 255);
            }
        }
    }

    private static void BlendImageExt(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage src,
        int drawX,
        int drawY,
        MinimapTint tint,
        bool additive,
        bool applyLuminanceToAlpha,
        LuminanceSettings luminanceSettings)
    {
        if (!additive && !applyLuminanceToAlpha)
        {
            BlendImage(dstRgba, dstW, dstH, src, drawX, drawY, tint);
            return;
        }

        BlendImageScaledExt(dstRgba, dstW, dstH, src, drawX, drawY, src.Width, src.Height, tint, additive, applyLuminanceToAlpha, luminanceSettings);
    }

    private static void BlendImageScaledExt(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage src,
        int drawX,
        int drawY,
        int scaledW,
        int scaledH,
        MinimapTint tint,
        bool additive,
        bool applyLuminanceToAlpha,
        LuminanceSettings luminanceSettings)
    {
        if (!src.IsValid || scaledW <= 0 || scaledH <= 0)
        {
            return;
        }

        int srcStartX = 0;
        int srcStartY = 0;
        int dstStartX = drawX;
        int dstStartY = drawY;

        if (dstStartX < 0)
        {
            srcStartX = -dstStartX;
            dstStartX = 0;
        }

        if (dstStartY < 0)
        {
            srcStartY = -dstStartY;
            dstStartY = 0;
        }

        int copyW = Math.Min(scaledW - srcStartX, dstW - dstStartX);
        int copyH = Math.Min(scaledH - srcStartY, dstH - dstStartY);
        if (copyW <= 0 || copyH <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> s = src.Rgba8;
        Span<byte> d = dstRgba;
        int srcStride = src.Width * 4;
        int dstStride = dstW * 4;

        for (int y = 0; y < copyH; y++)
        {
            int outY = srcStartY + y;
            int srcY = Math.Clamp((int)((long)outY * src.Height / scaledH), 0, src.Height - 1);
            int srcRow = srcY * srcStride;
            int dstRow = (dstStartY + y) * dstStride + dstStartX * 4;

            for (int x = 0; x < copyW; x++)
            {
                int outX = srcStartX + x;
                int srcX = Math.Clamp((int)((long)outX * src.Width / scaledW), 0, src.Width - 1);
                int sp = srcRow + srcX * 4;
                int dp = dstRow + x * 4;

                byte sr = s[sp + 0];
                byte sg = s[sp + 1];
                byte sb = s[sp + 2];
                if (!tint.IsNeutral)
                {
                    sr = (byte)((sr * tint.R + 127) / 255);
                    sg = (byte)((sg * tint.G + 127) / 255);
                    sb = (byte)((sb * tint.B + 127) / 255);
                }

                int sa = s[sp + 3];
                if (applyLuminanceToAlpha)
                {
                    byte lum = LuminanceProcessor.CalculateLuminance(sr, sg, sb, luminanceSettings.Mode);
                    byte adj = LuminanceProcessor.ApplyLuminanceAdjustments(lum, luminanceSettings);
                    sa = LuminanceProcessor.BlendAlpha(adj, (byte)sa, luminanceSettings.BlendMode);
                }

                if (tint.A != 255)
                {
                    sa = (sa * tint.A + 127) / 255;
                }

                if (sa <= 0)
                {
                    continue;
                }

                if (additive)
                {
                    d[dp + 0] = (byte)Math.Min(255, d[dp + 0] + ((sr * sa + 127) / 255));
                    d[dp + 1] = (byte)Math.Min(255, d[dp + 1] + ((sg * sa + 127) / 255));
                    d[dp + 2] = (byte)Math.Min(255, d[dp + 2] + ((sb * sa + 127) / 255));
                    d[dp + 3] = Math.Max(d[dp + 3], (byte)sa);
                    continue;
                }

                if (sa == 255)
                {
                    d[dp + 0] = sr;
                    d[dp + 1] = sg;
                    d[dp + 2] = sb;
                    d[dp + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                d[dp + 0] = (byte)((sr * sa + d[dp + 0] * inv + 127) / 255);
                d[dp + 1] = (byte)((sg * sa + d[dp + 1] * inv + 127) / 255);
                d[dp + 2] = (byte)((sb * sa + d[dp + 2] * inv + 127) / 255);
                d[dp + 3] = (byte)(sa + (d[dp + 3] * inv + 127) / 255);
            }
        }
    }

    private static bool TryGetScaledImage(
        MapTextureIndex textures,
        int packageId,
        int imageIndex,
        int scaleDivisor,
        Dictionary<long, ScaledImage> cache,
        out ScaledImage image,
        int frame = 0)
    {
        image = default;
        if (packageId <= 0 || imageIndex <= 0)
        {
            return false;
        }

        long key = HashCode.Combine(packageId, imageIndex, frame, Math.Clamp(scaleDivisor, 1, 32));
        if (cache.TryGetValue(key, out image))
        {
            return image.IsValid;
        }

        if (!textures.TryDecodeImage(packageId, imageIndex, frame, out DecodedImage decoded, out _)
            || !decoded.IsValid)
        {
            cache[key] = default;
            return false;
        }

        ScaledImage scaled = scaleDivisor <= 1 ? ScaledImage.FromDecoded(decoded) : ScaledImage.ScaleNearest(decoded, scaleDivisor);
        cache[key] = scaled;
        image = scaled;
        return image.IsValid;
    }

    private static void BlendImage(
        byte[] dstRgba,
        int dstW,
        int dstH,
        ScaledImage src,
        int drawX,
        int drawY,
        MinimapTint tint,
        bool applyLuminanceToAlpha = false,
        LuminanceSettings luminanceSettings = default)
    {
        if (!src.IsValid)
        {
            return;
        }

        int srcW = src.Width;
        int srcH = src.Height;

        int srcStartX = 0;
        int srcStartY = 0;
        int dstStartX = drawX;
        int dstStartY = drawY;

        if (dstStartX < 0)
        {
            srcStartX = -dstStartX;
            dstStartX = 0;
        }

        if (dstStartY < 0)
        {
            srcStartY = -dstStartY;
            dstStartY = 0;
        }

        int copyW = Math.Min(srcW - srcStartX, dstW - dstStartX);
        int copyH = Math.Min(srcH - srcStartY, dstH - dstStartY);
        if (copyW <= 0 || copyH <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> s = src.Rgba8;
        Span<byte> d = dstRgba;

        int srcStride = srcW * 4;
        int dstStride = dstW * 4;

        for (int y = 0; y < copyH; y++)
        {
            int srcRow = (srcStartY + y) * srcStride + srcStartX * 4;
            int dstRow = (dstStartY + y) * dstStride + dstStartX * 4;

            for (int x = 0; x < copyW; x++)
            {
                int sp = srcRow + x * 4;
                int dp = dstRow + x * 4;
                byte sr = s[sp + 0];
                byte sg = s[sp + 1];
                byte sb = s[sp + 2];

                if (!tint.IsNeutral)
                {
                    sr = (byte)((sr * tint.R + 127) / 255);
                    sg = (byte)((sg * tint.G + 127) / 255);
                    sb = (byte)((sb * tint.B + 127) / 255);
                }

                int sa = s[sp + 3];
                if (applyLuminanceToAlpha)
                {
                    byte lum = LuminanceProcessor.CalculateLuminance(sr, sg, sb, luminanceSettings.Mode);
                    byte adj = LuminanceProcessor.ApplyLuminanceAdjustments(lum, luminanceSettings);
                    sa = LuminanceProcessor.BlendAlpha(adj, (byte)sa, luminanceSettings.BlendMode);
                }

                if (tint.A != 255)
                {
                    sa = (sa * tint.A + 127) / 255;
                }

                if (sa <= 0)
                {
                    continue;
                }

                if (sa == 255)
                {
                    d[dp + 0] = sr;
                    d[dp + 1] = sg;
                    d[dp + 2] = sb;
                    d[dp + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                d[dp + 0] = (byte)((sr * sa + d[dp + 0] * inv + 127) / 255);
                d[dp + 1] = (byte)((sg * sa + d[dp + 1] * inv + 127) / 255);
                d[dp + 2] = (byte)((sb * sa + d[dp + 2] * inv + 127) / 255);
                byte da = d[dp + 3];
                d[dp + 3] = (byte)(sa + (da * inv + 127) / 255);
            }
        }
    }

    private static void FillSolidRgba(byte[] rgba, byte r, byte g, byte b, byte a)
    {
        if (rgba is null || rgba.Length == 0)
        {
            return;
        }

        FillSolidRgba(rgba.AsSpan(), r, g, b, a);
    }

    private static void FillSolidRgba(Span<byte> rgba, byte r, byte g, byte b, byte a)
    {
        if (rgba.IsEmpty)
        {
            return;
        }

        int pixels = rgba.Length / 4;
        for (int i = 0; i < pixels; i++)
        {
            int p = i * 4;
            rgba[p + 0] = r;
            rgba[p + 1] = g;
            rgba[p + 2] = b;
            rgba[p + 3] = a;
        }
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

    private readonly struct ScaledImage
    {
        public int SourceWidth { get; }
        public int SourceHeight { get; }

        public int Width { get; }
        public int Height { get; }

        public short OffsetX { get; }
        public short OffsetY { get; }

        public short CenterX { get; }
        public short CenterY { get; }

        public byte[] Rgba8 { get; }

        public bool IsValid =>
            Width > 0
            && Height > 0
            && Rgba8 is { Length: > 0 }
            && Rgba8.Length == Width * Height * 4;

        private ScaledImage(
            int sourceWidth,
            int sourceHeight,
            int width,
            int height,
            short offsetX,
            short offsetY,
            short centerX,
            short centerY,
            byte[] rgba8)
        {
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            Width = width;
            Height = height;
            OffsetX = offsetX;
            OffsetY = offsetY;
            CenterX = centerX;
            CenterY = centerY;
            Rgba8 = rgba8 ?? Array.Empty<byte>();
        }

        public static ScaledImage FromDecoded(DecodedImage src)
        {
            if (src is null || !src.IsValid)
            {
                return default;
            }

            return new ScaledImage(src.Width, src.Height, src.Width, src.Height, src.OffsetX, src.OffsetY, src.CenterX, src.CenterY, src.Rgba8);
        }

        public static ScaledImage ScaleNearest(DecodedImage src, int scaleDivisor)
        {
            if (src is null || !src.IsValid)
            {
                return default;
            }

            if (scaleDivisor <= 1)
            {
                return FromDecoded(src);
            }

            int outW = Math.Max(1, (int)Math.Round(src.Width / (double)scaleDivisor));
            int outH = Math.Max(1, (int)Math.Round(src.Height / (double)scaleDivisor));

            var dst = new byte[outW * outH * 4];

            ReadOnlySpan<byte> s = src.Rgba8;
            Span<byte> d = dst;

            int srcStride = src.Width * 4;
            int dstStride = outW * 4;

            for (int y = 0; y < outH; y++)
            {
                int srcY = (int)((long)y * src.Height / outH);
                int srcRow = srcY * srcStride;
                int dstRow = y * dstStride;

                for (int x = 0; x < outW; x++)
                {
                    int srcX = (int)((long)x * src.Width / outW);
                    int sp = srcRow + srcX * 4;
                    int dp = dstRow + x * 4;

                    d[dp + 0] = s[sp + 0];
                    d[dp + 1] = s[sp + 1];
                    d[dp + 2] = s[sp + 2];
                    d[dp + 3] = s[sp + 3];
                }
            }

            short ox = ScaleShortRound(src.OffsetX, scaleDivisor);
            short oy = ScaleShortRound(src.OffsetY, scaleDivisor);
            short cx = ScaleShortRound(src.CenterX, scaleDivisor);
            short cy = ScaleShortRound(src.CenterY, scaleDivisor);

            return new ScaledImage(src.Width, src.Height, outW, outH, ox, oy, cx, cy, dst);
        }

        private static short ScaleShortRound(short value, int divisor)
        {
            if (divisor <= 1) return value;

            int scaled = (int)Math.Round(value / (double)divisor);
            if (scaled < short.MinValue) scaled = short.MinValue;
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            return (short)scaled;
        }
    }

    private static bool TryNormalizeOutputPath(string path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "输出路径为空。";
            return false;
        }

        string trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "输出路径为空。";
            return false;
        }

        string ext = Path.GetExtension(trimmed);
        if (string.IsNullOrWhiteSpace(ext))
        {
            trimmed += ".png";
        }
        else if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "输出文件扩展名必须为 .png。";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(trimmed);
            return true;
        }
        catch
        {
            error = "输出路径无效。";
            return false;
        }
    }
}
