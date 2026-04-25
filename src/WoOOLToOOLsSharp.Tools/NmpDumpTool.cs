using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;

namespace WoOOLToOOLsSharp.Tools;

internal static class NmpDumpTool
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static int RunDump(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("缺少参数：nmp dump <path> [--out file.json] [--sample N] [--seed N] [--cell x y]...");
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"文件不存在：{path}");
            return 1;
        }

        string? outPath = null;
        int sampleCount = 12;
        int seed = 12345;
        var requestedCells = new List<(int X, int Y)>();

        var remaining = new Queue<string>(args.Skip(1));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();

            if (token.Equals("--out", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--sample", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                if (int.TryParse(remaining.Dequeue(), out int parsed))
                {
                    sampleCount = Math.Clamp(parsed, 0, 10_000);
                }
                continue;
            }

            if (token.Equals("--seed", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                if (int.TryParse(remaining.Dequeue(), out int parsed))
                {
                    seed = parsed;
                }
                continue;
            }

            if (token.Equals("--cell", StringComparison.OrdinalIgnoreCase) && remaining.Count >= 2)
            {
                if (int.TryParse(remaining.Dequeue(), out int x) && int.TryParse(remaining.Dequeue(), out int y))
                {
                    requestedCells.Add((x, y));
                }
                continue;
            }

            if ((token.Length == 0 || token[0] != '-') && string.IsNullOrWhiteSpace(outPath))
            {
                outPath = token;
                continue;
            }
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"读取失败：{ex.Message}");
            return 1;
        }

        if (!NmpCodec.TryReadMapFromMemory(bytes, path, out NmpMapInfo info, out NmpCellData[] cells, out string error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        ulong cellsHash = ComputeCellsFnv1a64(cells);

        var samples = BuildSamples(info, cells, requestedCells, sampleCount, seed);

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
            ["cellsHashFnv1a64"] = $"0x{cellsHash:X16}",
            ["samples"] = samples,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
        };

        string json = JsonSerializer.Serialize(payload, options);

        if (string.IsNullOrWhiteSpace(outPath))
        {
            Console.WriteLine(json);
            return 0;
        }

        try
        {
            File.WriteAllText(outPath, json, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"写入失败：{ex.Message}");
            return 1;
        }

        Console.WriteLine(outPath);
        return 0;
    }

    private static List<Dictionary<string, object?>> BuildSamples(
        NmpMapInfo info,
        NmpCellData[] cells,
        List<(int X, int Y)> requestedCells,
        int sampleCount,
        int seed)
    {
        var samples = new List<Dictionary<string, object?>>();

        int w = info.Width;
        int h = info.Height;
        if (w <= 0 || h <= 0 || cells.Length == 0)
        {
            return samples;
        }

        var seen = new HashSet<long>();

        void AddCell(int x, int y, string reason)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
            {
                samples.Add(new Dictionary<string, object?>
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["reason"] = reason,
                    ["error"] = "坐标越界。",
                });
                return;
            }

            int index = (y * w) + x;
            if ((uint)index >= (uint)cells.Length)
            {
                samples.Add(new Dictionary<string, object?>
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["reason"] = reason,
                    ["error"] = "索引越界。",
                });
                return;
            }

            long key = ((long)x << 32) | (uint)y;
            if (!seen.Add(key))
            {
                return;
            }

            samples.Add(new Dictionary<string, object?>
            {
                ["x"] = x,
                ["y"] = y,
                ["index"] = index,
                ["reason"] = reason,
                ["cell"] = cells[index],
            });
        }

        // user requested cells first
        for (int i = 0; i < requestedCells.Count; i++)
        {
            (int x, int y) = requestedCells[i];
            AddCell(x, y, "requested");
        }

        // a few deterministic anchors
        AddCell(0, 0, "anchor");
        AddCell(w - 1, 0, "anchor");
        AddCell(0, h - 1, "anchor");
        AddCell(w - 1, h - 1, "anchor");
        AddCell(w / 2, h / 2, "anchor");

        // fill the rest with pseudo-random sampling
        if (sampleCount > 0)
        {
            var rng = new Random(seed);
            int attempts = 0;
            int target = sampleCount;

            while (attempts < target * 20 && samples.Count < target + requestedCells.Count + 5)
            {
                attempts++;
                int x = rng.Next(0, w);
                int y = rng.Next(0, h);
                AddCell(x, y, "sampled");
            }
        }

        return samples;
    }

    private static ulong ComputeCellsFnv1a64(NmpCellData[] cells)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        static ulong HashByte(ulong hash, byte value)
            => unchecked((hash ^ value) * prime);

        static ulong HashU16(ulong hash, ushort value)
        {
            hash = HashByte(hash, (byte)(value & 0xFF));
            hash = HashByte(hash, (byte)((value >> 8) & 0xFF));
            return hash;
        }

        static ulong HashU32(ulong hash, uint value)
        {
            hash = HashByte(hash, (byte)(value & 0xFF));
            hash = HashByte(hash, (byte)((value >> 8) & 0xFF));
            hash = HashByte(hash, (byte)((value >> 16) & 0xFF));
            hash = HashByte(hash, (byte)((value >> 24) & 0xFF));
            return hash;
        }

        ulong hash = offsetBasis;

        for (int i = 0; i < cells.Length; i++)
        {
            NmpCellData cell = cells[i];

            // 32-bit fields (keep order consistent with NmpCellData struct / old project)
            hash = HashU32(hash, cell.FrontImage);
            hash = HashU32(hash, cell.UnderObject);
            hash = HashU32(hash, cell.OverObject);
            hash = HashU32(hash, cell.ColorAdjTile);
            hash = HashU32(hash, cell.ColorAdjSmTile);
            hash = HashU32(hash, cell.ColorAdjObject);
            hash = HashU32(hash, cell.ColorAdjEffect);
            hash = HashU32(hash, cell.ColorAdjFloor);
            hash = HashU32(hash, cell.NearGround);
            hash = HashU32(hash, cell.Group);
            hash = HashU32(hash, cell.ColorOverObj);

            // 16-bit fields
            hash = HashU16(hash, cell.BackImage);
            hash = HashU16(hash, cell.BackLibrary);
            hash = HashU16(hash, cell.MiddleImage);
            hash = HashU16(hash, cell.MiddleLibrary);
            hash = HashU16(hash, cell.ObjectHeight);
            hash = HashU16(hash, cell.ExtendedAttributes);
            hash = HashU16(hash, cell.MiddleImage2);
            hash = HashU16(hash, cell.MiddleLibrary2);
            hash = HashU16(hash, cell.MiddleAlphaMask);

            // 8-bit fields
            hash = HashByte(hash, cell.FrontLibrary);
            hash = HashByte(hash, cell.Flags);
            hash = HashByte(hash, cell.DoorIndex);
            hash = HashByte(hash, cell.DoorOffset);
            hash = HashByte(hash, cell.Light);
            hash = HashByte(hash, cell.FrontAnimFrame);
            hash = HashByte(hash, cell.FrontAnimTick);
            hash = HashByte(hash, cell.Sound);
            hash = HashByte(hash, cell.BackAnimTick);
            hash = HashByte(hash, cell.MiddleAnimTick);

            hash = HashByte(hash, cell.ExtraAttrV12_0);
            hash = HashByte(hash, cell.ExtraAttrV12_1);
            hash = HashByte(hash, cell.ExtraAttrV12_2);
            hash = HashByte(hash, cell.ExtraAttrV12_3);
            hash = HashByte(hash, cell.ExtraAttrV12_4);
            hash = HashByte(hash, cell.ExtraAttrV12_5);
        }

        return hash;
    }
}
