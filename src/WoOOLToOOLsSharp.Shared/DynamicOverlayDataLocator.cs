using System;
using System.Collections.Generic;
using System.IO;

namespace WoOOLToOOLsSharp.Shared;

/// <summary>
/// 动态覆盖数据的候选路径（包含可读 label，便于留档与统计）。
/// 约定：label 使用 <c>root{N}:{relative}</c> 的格式，N 为探测根目录索引（0=最接近 dataRoot）。
/// </summary>
public sealed record DynamicOverlayCandidatePath
{
    public string Path { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// 为“动态覆盖（DynScene）/挂接 effects”提供数据探测：
/// 仅负责从 <paramref name="dataRoot"/> 推导候选路径与优先级，并尝试解析出第一个存在的文件路径。
/// </summary>
public static class DynamicOverlayDataLocator
{
    /// <summary>
    /// 从 map 文件路径或 mapId 推导规范化 mapId（去扩展名、取文件名、转大写；兼容 WPF::// 前缀）。
    /// </summary>
    public static string DeriveMapId(string? mapPathOrId)
    {
        if (string.IsNullOrWhiteSpace(mapPathOrId))
        {
            return string.Empty;
        }

        string value = mapPathOrId;

        const string wpfSep = ":://";
        int wpfPos = value.IndexOf(wpfSep, StringComparison.Ordinal);
        if (wpfPos >= 0 && wpfPos + wpfSep.Length < value.Length)
        {
            value = value.Substring(wpfPos + wpfSep.Length);
        }

        int slashPos = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slashPos != -1 && slashPos + 1 < value.Length)
        {
            value = value.Substring(slashPos + 1);
        }

        int dotPos = value.LastIndexOf('.');
        if (dotPos > 0)
        {
            value = value.Substring(0, dotPos);
        }

        return value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// 生成 DynScene 数据文件候选路径（按优先级排序）。
    /// 说明：旧工程仅发现“includeDynamicScene”开关，但未检索到实际加载实现，因此这里先以“多候选 + 可回退”的方式探测。
    /// </summary>
    public static IReadOnlyList<string> BuildDynSceneCandidatePaths(string? dataRoot, string? mapPathOrId)
    {
        IReadOnlyList<DynamicOverlayCandidatePath> labeled = BuildDynSceneCandidatePathsLabeled(dataRoot, mapPathOrId);
        if (labeled.Count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(capacity: labeled.Count);
        foreach (DynamicOverlayCandidatePath item in labeled)
        {
            paths.Add(item.Path);
        }

        return paths;
    }

    /// <summary>
    /// 生成 DynScene 数据文件候选路径（按优先级排序，带 label）。
    /// </summary>
    public static IReadOnlyList<DynamicOverlayCandidatePath> BuildDynSceneCandidatePathsLabeled(string? dataRoot, string? mapPathOrId)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return Array.Empty<DynamicOverlayCandidatePath>();
        }

        string mapId = DeriveMapId(mapPathOrId);

        var candidates = new List<DynamicOverlayCandidatePath>(capacity: 64);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> probeRoots = EnumerateProbeRoots(dataRoot);
        for (int rootIndex = 0; rootIndex < probeRoots.Count; rootIndex++)
        {
            string probe = probeRoots[rootIndex];

            AddCandidate(candidates, seen, Path.Combine(probe, "DynScene.dat"), BuildLabel(rootIndex, "DynScene.dat"));
            AddCandidate(candidates, seen, Path.Combine(probe, "DynScence.dat"), BuildLabel(rootIndex, "DynScence.dat")); // 旧工程注释里疑似拼写
            AddCandidate(candidates, seen, Path.Combine(probe, "Data", "DynScene.dat"), BuildLabel(rootIndex, "Data/DynScene.dat"));
            AddCandidate(candidates, seen, Path.Combine(probe, "Data", "DynScence.dat"), BuildLabel(rootIndex, "Data/DynScence.dat"));
            AddCandidate(candidates, seen, Path.Combine(probe, "data", "DynScene.dat"), BuildLabel(rootIndex, "data/DynScene.dat"));
            AddCandidate(candidates, seen, Path.Combine(probe, "data", "DynScence.dat"), BuildLabel(rootIndex, "data/DynScence.dat"));

            if (!string.IsNullOrWhiteSpace(mapId))
            {
                AddCandidate(candidates, seen, Path.Combine(probe, "DynScene", $"{mapId}.dat"), BuildLabel(rootIndex, "DynScene/<mapId>.dat"));
                AddCandidate(candidates, seen, Path.Combine(probe, "DynScence", $"{mapId}.dat"), BuildLabel(rootIndex, "DynScence/<mapId>.dat"));
                AddCandidate(candidates, seen, Path.Combine(probe, "Data", "DynScene", $"{mapId}.dat"), BuildLabel(rootIndex, "Data/DynScene/<mapId>.dat"));
                AddCandidate(candidates, seen, Path.Combine(probe, "Data", "DynScence", $"{mapId}.dat"), BuildLabel(rootIndex, "Data/DynScence/<mapId>.dat"));
                AddCandidate(candidates, seen, Path.Combine(probe, "data", "DynScene", $"{mapId}.dat"), BuildLabel(rootIndex, "data/DynScene/<mapId>.dat"));
                AddCandidate(candidates, seen, Path.Combine(probe, "data", "DynScence", $"{mapId}.dat"), BuildLabel(rootIndex, "data/DynScence/<mapId>.dat"));
            }
        }

        return candidates;
    }

    /// <summary>
    /// 生成“挂接 effects”数据文件候选路径（按优先级排序）。
    /// 说明：具体格式/命名/目录结构尚未有样本确认，因此这里使用保守的多候选命名探测（不会递归扫描目录）。
    /// </summary>
    public static IReadOnlyList<string> BuildAttachedEffectsCandidatePaths(string? dataRoot)
    {
        return BuildAttachedEffectsCandidatePaths(dataRoot, mapPathOrId: null);
    }

    /// <summary>
    /// 生成“挂接 effects”数据文件候选路径（按优先级排序），并在末尾追加少量“按 mapId 分目录/分文件”的保守探测候选。
    /// 注意：由于真实样本未确认，这里仅追加候选，不调整既有候选的优先级顺序。
    /// </summary>
    public static IReadOnlyList<string> BuildAttachedEffectsCandidatePaths(string? dataRoot, string? mapPathOrId)
    {
        IReadOnlyList<DynamicOverlayCandidatePath> labeled = BuildAttachedEffectsCandidatePathsLabeled(dataRoot, mapPathOrId);
        if (labeled.Count == 0)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>(capacity: labeled.Count);
        foreach (DynamicOverlayCandidatePath item in labeled)
        {
            paths.Add(item.Path);
        }

        return paths;
    }

    /// <summary>
    /// 生成“挂接 effects”数据文件候选路径（按优先级排序，带 label），并在末尾追加少量“按 mapId 分目录/分文件”的保守探测候选。
    /// 注意：由于真实样本未确认，这里仅追加候选，不调整既有候选的优先级顺序。
    /// </summary>
    public static IReadOnlyList<DynamicOverlayCandidatePath> BuildAttachedEffectsCandidatePathsLabeled(string? dataRoot, string? mapPathOrId)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return Array.Empty<DynamicOverlayCandidatePath>();
        }

        string mapId = DeriveMapId(mapPathOrId);

        string[] fileNames =
        [
            "AttachedEffects.dat",
            "AttachedEffect.dat",
            "AttachEffects.dat",
            "AttachEffect.dat",
            "EffectAttach.dat",
            "EffectAttached.dat",
        ];

        var candidates = new List<DynamicOverlayCandidatePath>(capacity: 96);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> probeRoots = EnumerateProbeRoots(dataRoot);
        for (int rootIndex = 0; rootIndex < probeRoots.Count; rootIndex++)
        {
            string probe = probeRoots[rootIndex];
            foreach (string fileName in fileNames)
            {
                AddCandidate(candidates, seen, Path.Combine(probe, fileName), BuildLabel(rootIndex, fileName));
                AddCandidate(candidates, seen, Path.Combine(probe, "Data", fileName), BuildLabel(rootIndex, $"Data/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "data", fileName), BuildLabel(rootIndex, $"data/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "effect", fileName), BuildLabel(rootIndex, $"effect/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "effects", fileName), BuildLabel(rootIndex, $"effects/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "Data", "effect", fileName), BuildLabel(rootIndex, $"Data/effect/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "Data", "effects", fileName), BuildLabel(rootIndex, $"Data/effects/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "data", "effect", fileName), BuildLabel(rootIndex, $"data/effect/{fileName}"));
                AddCandidate(candidates, seen, Path.Combine(probe, "data", "effects", fileName), BuildLabel(rootIndex, $"data/effects/{fileName}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(mapId))
        {
            string[] mapDirs =
            [
                "AttachedEffects",
                "AttachedEffect",
                "AttachEffects",
                "AttachEffect",
                "EffectAttach",
                "EffectAttached",
            ];

            for (int rootIndex = 0; rootIndex < probeRoots.Count; rootIndex++)
            {
                string probe = probeRoots[rootIndex];
                foreach (string dirName in mapDirs)
                {
                    AddCandidate(candidates, seen, Path.Combine(probe, dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "Data", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"Data/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "data", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"data/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "effect", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"effect/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "effects", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"effects/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "Data", "effect", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"Data/effect/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "Data", "effects", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"Data/effects/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "data", "effect", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"data/effect/{dirName}/<mapId>.dat"));
                    AddCandidate(candidates, seen, Path.Combine(probe, "data", "effects", dirName, $"{mapId}.dat"), BuildLabel(rootIndex, $"data/effects/{dirName}/<mapId>.dat"));
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 返回候选路径里第一个存在的文件；若都不存在则返回空字符串。
    /// </summary>
    public static string ResolveFirstExistingPath(IReadOnlyList<string>? candidates)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return string.Empty;
        }

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore and continue probing
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 返回候选列表里第一个存在的文件（并附带候选索引与 label）；若都不存在则返回空。
    /// </summary>
    public static (string ResolvedPath, int? ResolvedCandidateIndex, string? ResolvedCandidateLabel) ResolveFirstExistingCandidate(
        IReadOnlyList<DynamicOverlayCandidatePath>? candidates)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return (string.Empty, null, null);
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            string path = candidates[i].Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    return (path, i, candidates[i].Label);
                }
            }
            catch
            {
                // ignore and continue probing
            }
        }

        return (string.Empty, null, null);
    }

    /// <summary>
    /// 返回探测根目录列表（用于 Tools 做额外扫描/诊断）。
    /// 规则：以 <paramref name="dataRoot"/> 规范化后的路径为起点，向上最多 3 层父目录（共 4 层）。
    /// </summary>
    public static IReadOnlyList<string> GetProbeRoots(string? dataRoot)
    {
        return string.IsNullOrWhiteSpace(dataRoot) ? Array.Empty<string>() : EnumerateProbeRoots(dataRoot);
    }

    private static IReadOnlyList<string> EnumerateProbeRoots(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return Array.Empty<string>();
        }

        string current = dataRoot;
        try
        {
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                string? parent = Path.GetDirectoryName(current);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    current = parent;
                }
            }

            current = Path.GetFullPath(current);
        }
        catch
        {
            // keep original path
        }

        var roots = new List<string>(capacity: 4);

        string probe = current;
        for (int depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(probe); depth++)
        {
            roots.Add(probe);

            string? parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            probe = parent;
        }

        return roots;
    }

    private static string BuildLabel(int rootIndex, string relative)
    {
        return $"root{rootIndex}:{relative}";
    }

    private static void AddCandidate(List<DynamicOverlayCandidatePath> list, HashSet<string> seen, string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!seen.Add(path))
        {
            return;
        }

        list.Add(new DynamicOverlayCandidatePath
        {
            Path = path,
            Label = label,
        });
    }
}
