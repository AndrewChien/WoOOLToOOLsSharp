using System.Globalization;
using System.Text;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.Downloader;

internal sealed class FetchTexturesOptions
{
    public IReadOnlyList<string> SourceRoots { get; init; } = Array.Empty<string>();
    public bool SourceRecursive { get; init; } = true;
    public bool Overwrite { get; init; }

    public int MaxSglIndexFiles { get; init; } = 20_000;
    public int MaxWpfIndexFiles { get; init; } = 2_000;
    public int MaxWpfProbeFiles { get; init; } = 300;
}

internal sealed class FetchTexturesResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

internal static class FetchTexturesWorker
{
    private const int DefaultSmTilesLibrary = 3001;
    private const int DefaultTilesLibrary = 3051;
    private const int DefaultObjectLibrary = 5;
    private const int DefaultCoastLibrary = 49;

    public static FetchTexturesResult Fetch(string mapPath, string dataRoot, FetchTexturesOptions options, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(mapPath))
        {
            return Fail("失败：mapPath 为空。");
        }

        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return Fail("失败：dataRoot 为空。");
        }

        mapPath = mapPath.Trim();
        dataRoot = dataRoot.Trim();

        if (!File.Exists(mapPath))
        {
            return Fail($"失败：地图文件不存在：{mapPath}");
        }

        try
        {
            Directory.CreateDirectory(dataRoot);
        }
        catch (Exception ex)
        {
            return Fail($"失败：创建 DataRoot 目录失败：{ex.Message}");
        }

        if (!FileIO.TryReadAllBytes(mapPath, out byte[] mapBytes, out string readError))
        {
            return Fail(string.IsNullOrWhiteSpace(readError) ? "失败：读取地图文件失败。" : readError);
        }

        if (!NmpCodec.TryReadMapFromMemory(mapBytes, mapPath, out NmpMapInfo info, out NmpCellData[] cells, out string parseError))
        {
            return Fail(string.IsNullOrWhiteSpace(parseError) ? "失败：解析地图文件失败。" : parseError);
        }

        token.ThrowIfCancellationRequested();

        var requiredPackageIds = new HashSet<int>();
        var requiredMaskIds = new HashSet<int>();
        CollectRequiredPackagesAndMasks(cells, requiredPackageIds, requiredMaskIds);

        var requiredStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownPackages = new List<int>();
        foreach (int pkg in requiredPackageIds)
        {
            if (TryGetPackageStem(pkg, out string stem))
            {
                requiredStems.Add(stem);
            }
            else
            {
                unknownPackages.Add(pkg);
            }
        }

        if (requiredStems.Count == 0 && requiredMaskIds.Count == 0)
        {
            return new FetchTexturesResult
            {
                Success = true,
                Message = "Fetch Textures：地图未引用任何可识别的贴图包（无操作）。",
            };
        }

        List<string> sourceRoots = NormalizeSourceRoots(options.SourceRoots);
        var indexErrors = new List<string>();

        var sourceSglIndex = BuildFileNameIndex(
            sourceRoots,
            pattern: "*.sgl",
            recursive: options.SourceRecursive,
            maxFiles: Math.Max(1, options.MaxSglIndexFiles),
            errors: indexErrors);

        var sourceWpfPaths = BuildFileListIndex(
            sourceRoots,
            pattern: "*.wpf",
            recursive: options.SourceRecursive,
            maxFiles: Math.Max(1, options.MaxWpfIndexFiles),
            errors: indexErrors);

        token.ThrowIfCancellationRequested();

        var availableStems = DetectAvailableStemsInDataRoot(dataRoot, requiredStems, token);
        var missingStems = new HashSet<string>(requiredStems, StringComparer.OrdinalIgnoreCase);
        missingStems.ExceptWith(availableStems);

        int copied = 0;
        int skipped = 0;
        int missing = 0;
        int errors = 0;

        var missingItems = new List<string>();
        var errorItems = new List<string>();

        // Step 1: copy missing SGLs by expected file name.
        if (missingStems.Count > 0)
        {
            foreach (string stem in missingStems.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                token.ThrowIfCancellationRequested();

                string fileName = stem + ".sgl";
                if (!sourceSglIndex.TryGetValue(fileName, out string? srcPath) || string.IsNullOrWhiteSpace(srcPath))
                {
                    missing++;
                    missingItems.Add(fileName);
                    continue;
                }

                string destPath = Path.Combine(dataRoot, fileName);
                CopyOutcome outcome = TryCopyFile(srcPath, destPath, overwrite: options.Overwrite, out string copyError);
                if (outcome == CopyOutcome.Copied)
                {
                    copied++;
                    availableStems.Add(stem);
                    missingStems.Remove(stem);
                }
                else if (outcome == CopyOutcome.Skipped)
                {
                    skipped++;
                    availableStems.Add(stem);
                    missingStems.Remove(stem);
                }
                else
                {
                    errors++;
                    errorItems.Add($"{fileName}: {copyError}");
                }
            }
        }

        token.ThrowIfCancellationRequested();

        // Step 2: copy WPFs that cover the remaining missing stems.
        if (missingStems.Count > 0 && sourceWpfPaths.Count > 0)
        {
            var candidates = new List<WpfCandidate>();
            var seenWpf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int probeCount = 0;
            for (int i = 0; i < sourceWpfPaths.Count && probeCount < options.MaxWpfProbeFiles; i++)
            {
                token.ThrowIfCancellationRequested();

                string wpfPath = sourceWpfPaths[i];
                if (string.IsNullOrWhiteSpace(wpfPath))
                {
                    continue;
                }

                if (!seenWpf.Add(wpfPath))
                {
                    continue;
                }

                probeCount++;
                if (!TryInspectWpfCoverage(wpfPath, missingStems, out HashSet<string> coverage, out string inspectError))
                {
                    if (!string.IsNullOrWhiteSpace(inspectError))
                    {
                        // Non-fatal: keep going.
                        errorItems.Add($"WPF 枚举失败：{Path.GetFileName(wpfPath)}：{inspectError}");
                    }
                    continue;
                }

                if (coverage.Count == 0)
                {
                    continue;
                }

                candidates.Add(new WpfCandidate(wpfPath, coverage));
            }

            // Greedy cover: pick the WPF that covers most missing stems each round.
            while (missingStems.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                int bestIndex = -1;
                int bestScore = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int score = 0;
                    foreach (string stem in candidates[i].Coverage)
                    {
                        if (missingStems.Contains(stem))
                        {
                            score++;
                        }
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0 || bestScore <= 0)
                {
                    break;
                }

                WpfCandidate best = candidates[bestIndex];
                candidates.RemoveAt(bestIndex);

                string wpfName = Path.GetFileName(best.Path);
                string destWpfPath = Path.Combine(dataRoot, wpfName);

                CopyOutcome wpfOutcome = TryCopyFile(best.Path, destWpfPath, overwrite: options.Overwrite, out string wpfCopyError);
                if (wpfOutcome == CopyOutcome.Copied)
                {
                    copied++;
                }
                else if (wpfOutcome == CopyOutcome.Skipped)
                {
                    skipped++;
                }
                else
                {
                    errors++;
                    errorItems.Add($"{wpfName}: {wpfCopyError}");
                    continue;
                }

                // Copy sidecar hash if present.
                string srcHash = best.Path + ".hash";
                if (File.Exists(srcHash))
                {
                    string destHash = destWpfPath + ".hash";
                    CopyOutcome hashOutcome = TryCopyFile(srcHash, destHash, overwrite: options.Overwrite, out string hashCopyError);
                    if (hashOutcome == CopyOutcome.Copied)
                    {
                        copied++;
                    }
                    else if (hashOutcome == CopyOutcome.Skipped)
                    {
                        skipped++;
                    }
                    else
                    {
                        errors++;
                        errorItems.Add($"{Path.GetFileName(srcHash)}: {hashCopyError}");
                    }
                }

                HashSet<string> appliedCoverage = best.Coverage;
                if (wpfOutcome == CopyOutcome.Skipped && !options.Overwrite && File.Exists(destWpfPath))
                {
                    // The destination might already contain a different file with the same name.
                    // Only clear stems that are actually covered by the existing destination file.
                    if (TryInspectWpfCoverage(destWpfPath, missingStems, out HashSet<string> destCoverage, out _))
                    {
                        appliedCoverage = destCoverage;
                    }
                    else
                    {
                        appliedCoverage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                }

                foreach (string stem in appliedCoverage)
                {
                    availableStems.Add(stem);
                    missingStems.Remove(stem);
                }
            }
        }

        token.ThrowIfCancellationRequested();

        // Step 3: copy coast mask .msk files.
        if (requiredMaskIds.Count > 0)
        {
            foreach (int maskId in requiredMaskIds.OrderBy(static v => v))
            {
                token.ThrowIfCancellationRequested();

                if (maskId <= 0)
                {
                    continue;
                }

                string folder = (maskId / 100).ToString("D3", CultureInfo.InvariantCulture);
                string stem = maskId.ToString("D5", CultureInfo.InvariantCulture);
                string relLower = Path.Combine("mask", folder, stem + ".msk");
                string relUpper = Path.Combine("mask", folder, stem + ".Msk");

                string destLower = Path.Combine(dataRoot, relLower);
                string destUpper = Path.Combine(dataRoot, relUpper);

                if (File.Exists(destLower) || File.Exists(destUpper))
                {
                    skipped++;
                    continue;
                }

                if (!TryFindMaskSourcePath(sourceRoots, relUpper, relLower, out string srcMask))
                {
                    missing++;
                    missingItems.Add(relLower);
                    continue;
                }

                string destMask = Path.Combine(dataRoot, relLower);
                CopyOutcome maskOutcome = TryCopyFile(srcMask, destMask, overwrite: options.Overwrite, out string maskCopyError);
                if (maskOutcome == CopyOutcome.Copied)
                {
                    copied++;
                }
                else if (maskOutcome == CopyOutcome.Skipped)
                {
                    skipped++;
                }
                else
                {
                    errors++;
                    errorItems.Add($"{relLower}: {maskCopyError}");
                }
            }
        }

        // Final verification: see which stems remain missing.
        var finalAvailableStems = DetectAvailableStemsInDataRoot(dataRoot, requiredStems, token);
        var finalMissingStems = new HashSet<string>(requiredStems, StringComparer.OrdinalIgnoreCase);
        finalMissingStems.ExceptWith(finalAvailableStems);

        var sb = new StringBuilder();
        sb.Append("Fetch Textures 完成：").Append(Path.GetFileName(mapPath)).Append('\n');
        sb.Append("Map: ").Append(mapPath).Append('\n');
        sb.Append("DataRoot: ").Append(dataRoot).Append('\n');
        sb.Append("MapVersion: ").Append(info.Version.ToString(CultureInfo.InvariantCulture))
            .Append(" Size: ").Append(info.Width.ToString(CultureInfo.InvariantCulture))
            .Append('x').Append(info.Height.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("需求包数量: ").Append(requiredStems.Count.ToString(CultureInfo.InvariantCulture)).Append("（未知=").Append(unknownPackages.Count.ToString(CultureInfo.InvariantCulture)).Append("）").Append('\n');
        sb.Append("需求 Mask 数量: ").Append(requiredMaskIds.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("来源 Data Roots: ").Append(sourceRoots.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        for (int i = 0; i < sourceRoots.Count; i++)
        {
            sb.Append("  - ").Append(sourceRoots[i]).Append('\n');
        }

        if (indexErrors.Count > 0)
        {
            sb.Append("来源索引警告: ").Append(indexErrors.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < Math.Min(10, indexErrors.Count); i++)
            {
                sb.Append("  - ").Append(indexErrors[i]).Append('\n');
            }
        }

        sb.Append("复制: ").Append(copied.ToString(CultureInfo.InvariantCulture))
            .Append(" 跳过: ").Append(skipped.ToString(CultureInfo.InvariantCulture))
            .Append(" 缺失: ").Append(missing.ToString(CultureInfo.InvariantCulture))
            .Append(" 错误: ").Append(errors.ToString(CultureInfo.InvariantCulture)).Append('\n');

        if (finalMissingStems.Count > 0)
        {
            sb.Append("仍缺失贴图包: ").Append(finalMissingStems.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            int shown = 0;
            foreach (string stem in finalMissingStems.OrderBy(static s => s, StringComparer.OrdinalIgnoreCase))
            {
                if (shown++ >= 30)
                {
                    sb.Append("  ...\n");
                    break;
                }
                sb.Append("  - ").Append(stem).Append('\n');
            }
        }

        if (missingItems.Count > 0)
        {
            sb.Append("未找到源文件: ").Append(missingItems.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < Math.Min(30, missingItems.Count); i++)
            {
                sb.Append("  - ").Append(missingItems[i]).Append('\n');
            }
            if (missingItems.Count > 30)
            {
                sb.Append("  ...\n");
            }
        }

        if (errorItems.Count > 0)
        {
            sb.Append("错误: ").Append(errorItems.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < Math.Min(30, errorItems.Count); i++)
            {
                sb.Append("  - ").Append(errorItems[i]).Append('\n');
            }
            if (errorItems.Count > 30)
            {
                sb.Append("  ...\n");
            }
        }

        if (unknownPackages.Count > 0)
        {
            sb.Append("未知 packageId（无法推导文件名，可能需要 WPF 覆盖）：").Append('\n');
            for (int i = 0; i < Math.Min(30, unknownPackages.Count); i++)
            {
                sb.Append("  - ").Append(unknownPackages[i].ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            if (unknownPackages.Count > 30)
            {
                sb.Append("  ...\n");
            }
        }

        bool ok = finalMissingStems.Count == 0 && errors == 0;
        if (!ok && finalMissingStems.Count > 0)
        {
            sb.Append('\n');
            sb.Append("提示：请确保 MapEditor 的 Data Paths 中至少有一个条目指向包含完整贴图库（.sgl/.wpf/.msk）的 DataRoot，");
            sb.Append("或者通过环境变量提供来源路径（WOOOL_DOWNLOADER_SOURCE_ROOTS / WOOOL_CLIENT_DATA_ROOT / WOOL_CLIENT_DATA_ROOT）。");
        }

        return new FetchTexturesResult
        {
            Success = ok,
            Message = sb.ToString().TrimEnd(),
        };
    }

    private static FetchTexturesResult Fail(string message)
    {
        return new FetchTexturesResult
        {
            Success = false,
            Message = message ?? string.Empty,
        };
    }

    private static void CollectRequiredPackagesAndMasks(NmpCellData[] cells, HashSet<int> packages, HashSet<int> masks)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            NmpCellData cell = cells[i];

            if (cell.BackImage > 0)
            {
                packages.Add(cell.BackLibrary != 0 ? cell.BackLibrary : DefaultSmTilesLibrary);
            }

            if (cell.MiddleImage2 != 0)
            {
                int groundIdx = cell.MiddleImage2;
                if (groundIdx > 0)
                {
                    packages.Add(cell.MiddleLibrary2 != 0 ? cell.MiddleLibrary2 : DefaultTilesLibrary);
                }

                int coastIdx = cell.MiddleImage;
                int maskIdx = cell.MiddleAlphaMask;
                if (coastIdx > 0 || maskIdx > 0)
                {
                    packages.Add(cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultCoastLibrary);
                    if (maskIdx > 0)
                    {
                        masks.Add(maskIdx);
                    }
                }
            }
            else
            {
                int middleIdx = cell.MiddleImage;
                if (middleIdx > 0)
                {
                    packages.Add(cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultTilesLibrary);
                }
            }

            int frontIdx = (int)(cell.FrontImage & 0xFFFF);
            if (frontIdx > 0)
            {
                int frontPkg = (int)((cell.FrontImage >> 16) & 0xFF);
                if (frontPkg == 0)
                {
                    frontPkg = cell.FrontLibrary;
                }

                if (frontPkg == 0)
                {
                    frontPkg = DefaultObjectLibrary;
                }

                packages.Add(frontPkg);
            }

            if (TryResolvePackedObject(cell.UnderObject, out int underPkg))
            {
                packages.Add(underPkg);
            }
            if (TryResolvePackedObject(cell.OverObject, out int overPkg))
            {
                packages.Add(overPkg);
            }
            if (TryResolvePackedObject(cell.NearGround, out int nearPkg))
            {
                packages.Add(nearPkg);
            }
        }
    }

    private static bool TryResolvePackedObject(uint raw, out int packageId)
    {
        packageId = 0;

        if (raw == 0)
        {
            return false;
        }

        int imageIndex = (int)(raw & 0xFFFF);
        if (imageIndex == 0)
        {
            return false;
        }

        packageId = (int)((raw >> 16) & 0xFF);
        if (packageId == 0)
        {
            packageId = DefaultObjectLibrary;
        }

        return packageId > 0;
    }

    private static bool TryGetPackageStem(int packageId, out string stem)
    {
        stem = string.Empty;

        if (packageId == 3001)
        {
            stem = "smtiles";
            return true;
        }

        if (packageId is >= 3002 and <= 3050)
        {
            stem = "smtiles" + (packageId - 3000).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (packageId == 3051)
        {
            stem = "tiles";
            return true;
        }

        if (packageId is >= 3052 and <= 3149)
        {
            stem = "tiles" + (packageId - 3050).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (packageId is >= 5 and <= 8)
        {
            stem = "objects" + (packageId - 4).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (packageId is >= 33 and <= 47)
        {
            stem = "objects" + (packageId - 28).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (packageId is >= 210 and <= 255)
        {
            stem = "objects" + (packageId - 190).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (packageId == 49)
        {
            stem = "effect";
            return true;
        }

        if (packageId == 50)
        {
            stem = "others";
            return true;
        }

        return false;
    }

    private static List<string> NormalizeSourceRoots(IReadOnlyList<string>? roots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (roots is null)
        {
            return result;
        }

        for (int i = 0; i < roots.Count; i++)
        {
            string r = roots[i]?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(r))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(r);
            }
            catch
            {
                continue;
            }

            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 0 || !Directory.Exists(full))
            {
                continue;
            }

            if (seen.Add(full))
            {
                result.Add(full);
            }
        }

        return result;
    }

    private static Dictionary<string, string> BuildFileNameIndex(
        IReadOnlyList<string> roots,
        string pattern,
        bool recursive,
        int maxFiles,
        List<string> errors)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        int remaining = maxFiles;

        foreach (string root in roots)
        {
            if (remaining <= 0)
            {
                break;
            }

            IEnumerable<string> seq;
            try
            {
                seq = Directory.EnumerateFiles(root, pattern, opt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"枚举失败：{root}：{ex.Message}");
                continue;
            }

            foreach (string path in seq)
            {
                if (remaining-- <= 0)
                {
                    break;
                }

                string name;
                try
                {
                    name = Path.GetFileName(path);
                }
                catch
                {
                    continue;
                }

                if (!dict.ContainsKey(name))
                {
                    dict[name] = path;
                }
            }
        }

        return dict;
    }

    private static List<string> BuildFileListIndex(
        IReadOnlyList<string> roots,
        string pattern,
        bool recursive,
        int maxFiles,
        List<string> errors)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SearchOption opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        int remaining = maxFiles;

        foreach (string root in roots)
        {
            if (remaining <= 0)
            {
                break;
            }

            IEnumerable<string> seq;
            try
            {
                seq = Directory.EnumerateFiles(root, pattern, opt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"枚举失败：{root}：{ex.Message}");
                continue;
            }

            foreach (string path in seq)
            {
                if (remaining-- <= 0)
                {
                    break;
                }

                if (seen.Add(path))
                {
                    list.Add(path);
                }
            }
        }

        return list;
    }

    private static HashSet<string> DetectAvailableStemsInDataRoot(string dataRoot, HashSet<string> requiredStems, CancellationToken token)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(dataRoot) || requiredStems.Count == 0)
        {
            return available;
        }

        try
        {
            foreach (string sglPath in Directory.EnumerateFiles(dataRoot, "*.sgl", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();

                string stem;
                try
                {
                    stem = Path.GetFileNameWithoutExtension(sglPath);
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(stem))
                {
                    continue;
                }

                if (requiredStems.Contains(stem))
                {
                    available.Add(stem);
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            foreach (string wpfPath in Directory.EnumerateFiles(dataRoot, "*.wpf", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();

                if (!TryInspectWpfCoverage(wpfPath, requiredStems, out HashSet<string> coverage, out _))
                {
                    continue;
                }

                foreach (string stem in coverage)
                {
                    available.Add(stem);
                }
            }
        }
        catch
        {
            // ignore
        }

        return available;
    }

    private static bool TryInspectWpfCoverage(string wpfPath, HashSet<string> requiredStems, out HashSet<string> coverage, out string error)
    {
        coverage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath) || requiredStems.Count == 0)
        {
            return true;
        }

        if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> entries, out string enumError))
        {
            error = enumError;
            return false;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            WpfEntry e = entries[i];
            if (e is null)
            {
                continue;
            }

            string id = !string.IsNullOrWhiteSpace(e.FullPath) ? e.FullPath : e.Name;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (e.IsDirectory)
            {
                // Top-level directory only.
                if (id.IndexOfAny(new[] { '/', '\\' }) >= 0)
                {
                    continue;
                }

                if (requiredStems.Contains(id))
                {
                    coverage.Add(id);
                }

                continue;
            }

            // Any *.sgl file entry.
            if (!id.EndsWith(".sgl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string filename = id;
            int sep = filename.LastIndexOfAny(new[] { '/', '\\' });
            if (sep >= 0 && sep + 1 < filename.Length)
            {
                filename = filename.Substring(sep + 1);
            }

            string fileStem;
            try
            {
                fileStem = Path.GetFileNameWithoutExtension(filename);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(fileStem))
            {
                continue;
            }

            if (requiredStems.Contains(fileStem))
            {
                coverage.Add(fileStem);
            }
        }

        return true;
    }

    private static bool TryFindMaskSourcePath(IReadOnlyList<string> sourceRoots, string relUpper, string relLower, out string path)
    {
        path = string.Empty;

        for (int i = 0; i < sourceRoots.Count; i++)
        {
            string root = sourceRoots[i];
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string p1 = Path.Combine(root, relUpper);
            if (File.Exists(p1))
            {
                path = p1;
                return true;
            }

            string p2 = Path.Combine(root, relLower);
            if (File.Exists(p2))
            {
                path = p2;
                return true;
            }
        }

        return false;
    }

    private enum CopyOutcome
    {
        Copied = 0,
        Skipped = 1,
        Failed = 2,
    }

    private static CopyOutcome TryCopyFile(string sourcePath, string destPath, bool overwrite, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
        {
            error = "源/目标路径为空。";
            return CopyOutcome.Failed;
        }

        string srcFull;
        string dstFull;
        try
        {
            srcFull = Path.GetFullPath(sourcePath);
            dstFull = Path.GetFullPath(destPath);
        }
        catch
        {
            srcFull = sourcePath;
            dstFull = destPath;
        }

        if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
        {
            return CopyOutcome.Skipped;
        }

        if (!overwrite && File.Exists(destPath))
        {
            return CopyOutcome.Skipped;
        }

        try
        {
            string? dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(sourcePath, destPath, overwrite: overwrite);
            return CopyOutcome.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return CopyOutcome.Failed;
        }
    }

    private readonly record struct WpfCandidate(string Path, HashSet<string> Coverage);
}
