using System.Text;
using WoOOLToOOLsSharp.Shared.EditorBridge;

namespace WoOOLToOOLsSharp.Downloader;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // ignore
        }

        var bridge = new LocalEditorBridge(EditorBridgeApp.Downloader);
        if (!bridge.Initialize(out string initError))
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(initError) ? "初始化 EditorBridge 失败。" : initError);
            return 1;
        }

        Console.WriteLine("WoOOLToOOLsSharp.Downloader 已启动。按 Ctrl+C 退出。");
        Console.WriteLine($"BridgeRoot: {bridge.RootDirectory}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignored
            }
        };

        var cliOptions = DownloaderCliOptions.Parse(args ?? Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(cliOptions.Note))
        {
            Console.WriteLine(cliOptions.Note.Trim());
        }

        while (!cts.IsCancellationRequested)
        {
            bridge.Tick();

            List<EditorBridgeRequest> requests = bridge.DrainRequests();
            for (int i = 0; i < requests.Count; i++)
            {
                EditorBridgeRequest req = requests[i];
                if (req is null)
                {
                    continue;
                }

                if (req.Kind != EditorBridgeRequestKind.PatchNmp)
                {
                    continue;
                }

                HandlePatchNmp(bridge, req, cliOptions, cts.Token);
            }

            Thread.Sleep(50);
        }

        Console.WriteLine("Downloader 已退出。");
        return 0;
    }

    private static void HandlePatchNmp(LocalEditorBridge bridge, EditorBridgeRequest req, DownloaderCliOptions cliOptions, CancellationToken token)
    {
        string mapPath = req.Path?.Trim() ?? string.Empty;
        string dataPath = req.ExtraPath?.Trim() ?? string.Empty;

        List<string> sourceRoots = BuildSourceRoots(mapPath, dataPath, cliOptions);
        var result = FetchTexturesWorker.Fetch(mapPath, dataPath, new FetchTexturesOptions
        {
            SourceRoots = sourceRoots,
            SourceRecursive = cliOptions.SourceRecursive,
            Overwrite = cliOptions.Overwrite,
        }, token);

        string stamp = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{stamp}] PatchNmp: map={mapPath} data={dataPath} => {(result.Success ? "OK" : "FAIL")}");
        Console.WriteLine(result.Message);

        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return;
        }

        // Push a visible message back to the editors and let them rescan.
        if (!bridge.SendReloadDataFolder(EditorBridgeApp.MapEditor, dataPath, result.Message, out string mapEditorError))
        {
            if (!string.IsNullOrWhiteSpace(mapEditorError))
            {
                Console.Error.WriteLine($"发送 ReloadDataFolder 到 MapEditor 失败：{mapEditorError}");
            }
        }

        if (!bridge.SendReloadDataFolder(EditorBridgeApp.ContentEditor, dataPath, result.Message, out string contentEditorError))
        {
            if (!string.IsNullOrWhiteSpace(contentEditorError))
            {
                Console.Error.WriteLine($"发送 ReloadDataFolder 到 ContentEditor 失败：{contentEditorError}");
            }
        }
    }

    private static List<string> BuildSourceRoots(string mapPath, string dataPath, DownloaderCliOptions cliOptions)
    {
        var roots = new List<string>();

        AddRoots(roots, cliOptions.SourceRoots);
        AddRoots(roots, SplitRootsFromEnv("WOOOL_DOWNLOADER_SOURCE_ROOTS"));
        AddRoots(roots, SplitRootsFromEnv("WOOL_DOWNLOADER_SOURCE_ROOTS"));
        AddRoots(roots, SplitRootsFromEnv("WOOOL_CLIENT_DATA_ROOT"));
        AddRoots(roots, SplitRootsFromEnv("WOOL_CLIENT_DATA_ROOT"));

        // Prefer reusing existing MapEditor Data Paths as sources (old workflow: copy from other DataRoots).
        AddRoots(roots, TryReadDataRootsFromPreferences(mapPath));

        // Heuristics: map folder and its parent sometimes contain a sibling data folder.
        try
        {
            string? mapDir = Path.GetDirectoryName(mapPath);
            if (!string.IsNullOrWhiteSpace(mapDir))
            {
                roots.Add(mapDir);
                string? mapParent = Path.GetDirectoryName(mapDir);
                if (!string.IsNullOrWhiteSpace(mapParent))
                {
                    roots.Add(mapParent);
                    roots.Add(Path.Combine(mapParent, "data"));
                    roots.Add(Path.Combine(mapParent, "Data"));
                }
            }
        }
        catch
        {
            // ignore
        }

        // Remove invalid/missing, dedupe, and exclude destination root itself.
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string destFull = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(dataPath))
            {
                destFull = Path.GetFullPath(dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            destFull = dataPath ?? string.Empty;
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
            if (string.IsNullOrWhiteSpace(full) || !Directory.Exists(full))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(destFull) && string.Equals(full, destFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(full))
            {
                normalized.Add(full);
            }
        }

        return normalized;
    }

    private static void AddRoots(List<string> roots, IEnumerable<string> values)
    {
        if (values is null) return;

        foreach (string v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            roots.Add(v.Trim());
        }
    }

    private static IEnumerable<string> SplitRootsFromEnv(string name)
    {
        string? value = null;
        try
        {
            value = Environment.GetEnvironmentVariable(name);
        }
        catch
        {
            value = null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<string> TryReadDataRootsFromPreferences(string mapPath)
    {
        // Keep the lookup in sync with MapEditorApp.GetPreferencesPath (simplified).
        string[] candidates;
        try
        {
            string exeDir = AppContext.BaseDirectory ?? string.Empty;
            candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, "map_editor_prefs.cfg"),
                Path.Combine(exeDir, "map_editor_prefs.cfg"),
                Path.Combine(Environment.CurrentDirectory, "settings.cfg"),
                Path.Combine(exeDir, "settings.cfg"),
            };
        }
        catch
        {
            candidates = Array.Empty<string>();
        }

        string selected = string.Empty;
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = candidates[i];
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (File.Exists(path))
            {
                selected = path;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            // Also probe next to the map file.
            try
            {
                string? mapDir = Path.GetDirectoryName(mapPath);
                if (!string.IsNullOrWhiteSpace(mapDir))
                {
                    string nearPrefs = Path.Combine(mapDir, "map_editor_prefs.cfg");
                    if (File.Exists(nearPrefs))
                    {
                        selected = nearPrefs;
                    }
                    else
                    {
                        string nearLegacy = Path.Combine(mapDir, "settings.cfg");
                        if (File.Exists(nearLegacy))
                        {
                            selected = nearLegacy;
                        }
                    }
                }
            }
            catch
            {
                selected = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            return new List<string>();
        }

        var results = new List<string>();
        try
        {
            foreach (string raw in File.ReadAllLines(selected, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                const string Prefix = "data_folder ";
                if (!line.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryParseLegacyQuotedPair(line[Prefix.Length..], out _, out string path))
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        results.Add(path);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return results;
    }

    private static bool TryParseLegacyQuotedPair(string input, out string left, out string right)
    {
        left = string.Empty;
        right = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        int pos = 0;
        if (!TryReadQuotedString(input, ref pos, out left))
        {
            return false;
        }
        SkipWs(input, ref pos);
        if (!TryReadQuotedString(input, ref pos, out right))
        {
            return false;
        }

        return true;
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    private static bool TryReadQuotedString(string s, ref int pos, out string value)
    {
        value = string.Empty;
        if (pos >= s.Length)
        {
            return false;
        }

        SkipWs(s, ref pos);
        if (pos >= s.Length || s[pos] != '\"')
        {
            return false;
        }

        pos++; // consume '"'
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            char c = s[pos++];
            if (c == '\"')
            {
                value = sb.ToString();
                return true;
            }

            if (c == '\\' && pos < s.Length)
            {
                char next = s[pos++];
                if (next == '\"' || next == '\\')
                {
                    sb.Append(next);
                }
                else
                {
                    // Keep unknown escapes as-is.
                    sb.Append(next);
                }

                continue;
            }

            sb.Append(c);
        }

        return false;
    }

    private sealed class DownloaderCliOptions
    {
        public List<string> SourceRoots { get; } = new();
        public bool SourceRecursive { get; set; } = true;
        public bool Overwrite { get; set; }
        public string Note { get; set; } = string.Empty;

        public static DownloaderCliOptions Parse(string[] args)
        {
            var opts = new DownloaderCliOptions();
            if (args is null || args.Length == 0)
            {
                opts.Note = "提示：可用参数：--source-root <dir>（可重复）、--overwrite、--source-recursive 0|1。也可使用环境变量 WOOOL_DOWNLOADER_SOURCE_ROOTS。";
                return opts;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i]?.Trim() ?? string.Empty;
                if (a.Length == 0) continue;

                if (a == "--source-root" || a == "-s")
                {
                    if (i + 1 < args.Length)
                    {
                        string v = args[++i]?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            opts.SourceRoots.Add(v);
                        }
                    }
                    continue;
                }

                if (a.StartsWith("--source-root=", StringComparison.Ordinal))
                {
                    string v = a.Substring("--source-root=".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        opts.SourceRoots.Add(v);
                    }
                    continue;
                }

                if (a == "--overwrite")
                {
                    opts.Overwrite = true;
                    continue;
                }

                if (a == "--source-recursive" && i + 1 < args.Length)
                {
                    string v = args[++i]?.Trim() ?? string.Empty;
                    opts.SourceRecursive = v != "0";
                    continue;
                }

                if (a.StartsWith("--source-recursive=", StringComparison.Ordinal))
                {
                    string v = a.Substring("--source-recursive=".Length).Trim();
                    opts.SourceRecursive = v != "0";
                    continue;
                }
            }

            return opts;
        }
    }
}
