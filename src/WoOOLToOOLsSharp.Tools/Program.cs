using System.Text.Json;
using System.Security.Cryptography;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.Tools;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string[] normalizedArgs = NormalizeLegacyArgs(args);
        string cmd = normalizedArgs[0].Trim().ToLowerInvariant();
        string[] rest = normalizedArgs.Skip(1).ToArray();

        return cmd switch
        {
            "wpf" => RunWpf(rest),
            "nmp" => RunNmp(rest),
            "minimap" => RunMinimap(rest),
            "png" => RunPng(rest),
            "overlay" => RunOverlay(rest),
            "-h" or "--help" or "help" => PrintUsageAndReturn(),
            _ => UnknownCommand(cmd),
        };
    }

    private static int RunMinimap(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string sub = args[0].Trim().ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        return sub switch
        {
            "export" => MinimapBatchExporter.RunExport(rest),
            _ => UnknownCommand($"minimap {sub}"),
        };
    }

    private static int RunPng(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string sub = args[0].Trim().ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        return sub switch
        {
            "diff" => PngDiffTool.RunDiff(rest),
            _ => UnknownCommand($"png {sub}"),
        };
    }

    private static int RunNmp(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string sub = args[0].Trim().ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        return sub switch
        {
            "stats" => RunNmpStats(rest),
            "dump" => NmpDumpTool.RunDump(rest),
            _ => UnknownCommand($"nmp {sub}"),
        };
    }

    private static int RunWpf(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string sub = args[0].Trim().ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        return sub switch
        {
            "stats" => RunWpfStats(rest),
            "pack" => WpfPackTool.RunPack(rest),
            _ => UnknownCommand($"wpf {sub}"),
        };
    }

    private static int RunWpfStats(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("缺少参数：wpf 路径。");
            PrintUsage();
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"文件不存在：{path}");
            return 1;
        }

        int extractCount = 5;
        int maxBytes = 64 * 1024 * 1024;
        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--extract-count", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedExtract) && parsedExtract is >= 0 and <= 1000)
            {
                extractCount = parsedExtract;
                continue;
            }

            if (token.Equals("--max-bytes", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0
                && int.TryParse(remaining.Dequeue(), out int parsedBytes) && parsedBytes is > 0 and <= (1024 * 1024 * 1024))
            {
                maxBytes = parsedBytes;
            }
        }

        using var archive = new WpfArchive();
        if (!archive.Open(path, out string openError))
        {
            Console.Error.WriteLine(openError);
            return 1;
        }

        var entries = archive.GetEntries();
        var fileEntries = entries.Where(static e => !e.IsDirectory && e.ByteSize > 0).ToList();

        int extracted = 0;
        long extractedTotalBytes = 0;
        int skippedTooLarge = 0;
        var sampleEntries = new List<Dictionary<string, object?>>();
        foreach (var entry in fileEntries)
        {
            if (extracted >= extractCount)
            {
                break;
            }

            if (entry.UncompressedSize > (uint)maxBytes)
            {
                skippedTooLarge++;
                continue;
            }

            if (!archive.ExtractEntry(entry, out byte[] bytes, out string extractError))
            {
                Console.Error.WriteLine(extractError);
                return 1;
            }

            string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            sampleEntries.Add(new Dictionary<string, object?>
            {
                ["index"] = entry.Index,
                ["name"] = entry.Name,
                ["fullPath"] = entry.FullPath,
                ["fcb1Hash"] = entry.Hash,
                ["byteOffset"] = entry.ByteOffset,
                ["byteSize"] = entry.ByteSize,
                ["uncompressedSize"] = entry.UncompressedSize,
                ["isCompressed"] = entry.IsCompressed,
                ["extractedBytes"] = bytes.LongLength,
                ["contentSha256"] = sha256,
            });

            extracted++;
            extractedTotalBytes += bytes.LongLength;
        }

        var payload = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["totalEntries"] = entries.Count,
            ["directoryEntries"] = entries.Count(static e => e.IsDirectory),
            ["fileEntries"] = fileEntries.Count,
            ["compressedFileEntries"] = fileEntries.Count(static e => e.IsCompressed),
            ["maxUncompressedBytes"] = fileEntries.Count == 0 ? 0 : fileEntries.Max(static e => e.UncompressedSize),
            ["maxRawBytes"] = fileEntries.Count == 0 ? 0 : fileEntries.Max(static e => e.ByteSize),
            ["extractCountLimit"] = extractCount,
            ["maxBytesLimit"] = maxBytes,
            ["skippedTooLargeCount"] = skippedTooLarge,
            ["extractedCount"] = extracted,
            ["extractedTotalBytes"] = extractedTotalBytes,
            ["sampleEntries"] = sampleEntries,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload));
        return 0;
    }

    private static int RunNmpStats(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("缺少参数：nmp 路径。");
            PrintUsage();
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"文件不存在：{path}");
            return 1;
        }

        bool doRoundTrip = true;
        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();
            if (token.Equals("--roundtrip", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                string value = remaining.Dequeue();
                doRoundTrip = value is not "0" and not "false";
            }
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (!NmpCodec.TryReadMapFromMemory(bytes, path, out var info, out var cells, out string error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        bool roundTripOk = false;
        int roundTripBytes = 0;
        if (doRoundTrip)
        {
            if (!NmpCodec.TryWriteMapDataToMemory(info, cells, out byte[] written, out error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            roundTripBytes = written.Length;
            if (!NmpCodec.TryReadMapFromMemory(written, "roundtrip", out var info2, out var cells2, out error))
            {
                Console.Error.WriteLine(error);
                return 1;
            }

            roundTripOk = info2.Version == info.Version
                          && info2.Width == info.Width
                          && info2.Height == info.Height
                          && cells2.Length == cells.Length
                          && cells2.SequenceEqual(cells);
        }

        var payload = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["fileBytes"] = bytes.Length,
            ["version"] = info.Version,
            ["width"] = info.Width,
            ["height"] = info.Height,
            ["cellCount"] = info.CellCount,
            ["headerSize"] = info.HeaderSize,
            ["dataOffset"] = info.DataOffset,
            ["roundTripEnabled"] = doRoundTrip,
            ["roundTripOk"] = roundTripOk,
            ["roundTripBytes"] = roundTripBytes,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload));
        return 0;
    }

    private static int RunOverlay(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        string sub = args[0].Trim().ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();
        return sub switch
        {
            "locate" => OverlayTool.RunLocate(rest),
            "scan" => OverlayTool.RunScan(rest),
            "inspect" => OverlayTool.RunInspect(rest),
            "stats" => OverlayTool.RunStats(rest),
            "parse" => OverlayTool.RunParse(rest),
            "parse-binary" => OverlayTool.RunParseBinary(rest),
            "export-fixture" => OverlayTool.RunExportFixture(rest),
            "dump-records" => OverlayTool.RunDumpRecords(rest),
            "probe" => OverlayTool.RunProbe(rest),
            _ => UnknownCommand($"overlay {sub}"),
        };
    }

    private static string[] NormalizeLegacyArgs(string[] args)
    {
        if (TryNormalizeLegacyNmpDumpArgs(args, out string[] normalized))
        {
            return normalized;
        }

        return args;
    }

    private static bool TryNormalizeLegacyNmpDumpArgs(string[] args, out string[] normalized)
    {
        normalized = args;
        if (args.Length < 3 || !args[0].Equals("--nmp-dump", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((args[1].Length > 0 && args[1][0] == '-') || (args[2].Length > 0 && args[2][0] == '-'))
        {
            return false;
        }

        var rewritten = new List<string>(args.Length + 2)
        {
            "nmp",
            "dump",
            args[1],
            "--out",
            args[2],
        };

        if (args.Length > 3)
        {
            rewritten.AddRange(args[3..]);
        }

        normalized = rewritten.ToArray();
        return true;
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}");
        PrintUsage();
        return 2;
    }

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("用法：");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- wpf stats <path> [--extract-count N] [--max-bytes N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- wpf pack <out.wpf> --add <entryPath> <filePath> [--add ...]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- nmp stats <path> [--roundtrip 1|0]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- nmp dump <path> [--out file.json] [--sample N] [--seed N] [--cell x y]...");
        Console.Error.WriteLine("    兼容旧调用：--nmp-dump <input.nmp|input.mmp> <output.json>");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- minimap export <inputPath> <outputPath> [--recursive 1|0] [--scale 1|2|4|8|16|32] [--include-back 1|0] [--include-middle 1|0] [--include-front 1|0] [--separate 1|0] [--overwrite 1|0]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- png diff <a.png> <b.png> [--max-abs-delta N] [--out diff.png] [--json out.json] [--sample N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay locate <dataRoot> <mapPath> [--map-id ID] [--overlay-map-id ID] [--effects-map-id ID] [--json out.json] [--limit N] [--scan 1|0] [--scan-parents 1|0] [--scan-max-depth N] [--scan-limit N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay scan <dataRoot> --pattern <substr> [--ext .dat] [--json out.json] [--limit N] [--scan-parents 1|0] [--scan-max-depth N] [--scan-limit N] [--read-bytes N] [--sample N] [--max-analyze-bytes N] [--decompress 1|0] [--max-decompressed-bytes N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay inspect <path> [--json out.json] [--read-bytes N] [--sample N] [--max-analyze-bytes N] [--decompress 1|0] [--max-decompressed-bytes N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay stats <path> [--layout <layout.json>] [--json out.json] [--max-decompressed-bytes N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay parse <path> [--json out.json] [--limit N] [--max-decompressed-bytes N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay parse-binary <path> [--layout <layout.json>] [--json out.json] [--limit N] [--max-decompressed-bytes N]（默认尝试 <path>.layout.json）");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay export-fixture <path> --out <outPath> [--layout <layout.json>] [--format json|csv|b64|bin] [--limit N] [--anonymize 1|0] [--decompress 1|0] [--offset N] [--max-bytes N] [--max-decompressed-bytes N]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay dump-records <path> [--offset N] [--record-size N] [--count N] [--decompress 1|0] [--max-decompressed-bytes N] [--json out.json]");
        Console.Error.WriteLine("  dotnet run --project src/WoOOLToOOLsSharp.Tools -- overlay probe <dataRoot> <mapPath> [--map-id ID] [--overlay-map-id ID] [--effects-map-id ID] [--json out.json] [--limit N] [--read-bytes N] [--sample N] [--max-analyze-bytes N] [--decompress 1|0] [--max-decompressed-bytes N] [--scan 1|0] [--scan-parents 1|0] [--scan-max-depth N] [--scan-limit N]");
    }
}
