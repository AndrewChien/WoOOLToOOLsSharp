using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.Tools;

internal static class PngDiffTool
{
    public static int RunDiff(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("缺少参数：png diff <a.png> <b.png>");
            return 2;
        }

        string aPath = args[0];
        string bPath = args[1];

        int maxAbsDelta = 0;
        int sample = 20;
        string? outDiffPath = null;
        string? outJsonPath = null;

        var remaining = new Queue<string>(args.Skip(2));
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();

            if (token.Equals("--max-abs-delta", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                if (int.TryParse(remaining.Dequeue(), out int parsed))
                {
                    maxAbsDelta = Math.Clamp(parsed, 0, 255);
                }
                continue;
            }

            if (token.Equals("--sample", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                if (int.TryParse(remaining.Dequeue(), out int parsed))
                {
                    sample = Math.Clamp(parsed, 0, 10_000);
                }
                continue;
            }

            if (token.Equals("--out", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outDiffPath = remaining.Dequeue();
                continue;
            }

            if (token.Equals("--json", StringComparison.OrdinalIgnoreCase) && remaining.Count > 0)
            {
                outJsonPath = remaining.Dequeue();
                continue;
            }
        }

        if (!File.Exists(aPath))
        {
            Console.Error.WriteLine($"文件不存在：{aPath}");
            return 1;
        }

        if (!File.Exists(bPath))
        {
            Console.Error.WriteLine($"文件不存在：{bPath}");
            return 1;
        }

        if (!PngReader.TryReadRgba8(aPath, out int wA, out int hA, out byte[] rgbaA, out string error))
        {
            Console.Error.WriteLine($"读取 PNG 失败：{aPath} ({error})");
            return 1;
        }

        if (!PngReader.TryReadRgba8(bPath, out int wB, out int hB, out byte[] rgbaB, out error))
        {
            Console.Error.WriteLine($"读取 PNG 失败：{bPath} ({error})");
            return 1;
        }

        if (wA != wB || hA != hB)
        {
            Console.Error.WriteLine($"PNG 尺寸不一致：a={wA}x{hA}, b={wB}x{hB}");
            return 1;
        }

        int width = wA;
        int height = hA;

        long pixelCount = (long)width * height;
        long channelCount = pixelCount * 4;

        byte[]? diffRgba = null;
        if (!string.IsNullOrWhiteSpace(outDiffPath))
        {
            try
            {
                diffRgba = new byte[checked(width * height * 4)];
            }
            catch (OverflowException)
            {
                Console.Error.WriteLine($"diff 图像过大：{width}x{height}");
                return 1;
            }
        }

        long differentPixels = 0;
        int maxAbsDeltaObserved = 0;
        long sumAbsDelta = 0;
        long sumSquaredDelta = 0;

        var samples = new List<Dictionary<string, object?>>(Math.Min(sample, 256));

        for (long p = 0; p < pixelCount; p++)
        {
            int i = checked((int)p * 4);

            int dr = Math.Abs(rgbaA[i + 0] - rgbaB[i + 0]);
            int dg = Math.Abs(rgbaA[i + 1] - rgbaB[i + 1]);
            int db = Math.Abs(rgbaA[i + 2] - rgbaB[i + 2]);
            int da = Math.Abs(rgbaA[i + 3] - rgbaB[i + 3]);

            int pixelMax = Math.Max(Math.Max(dr, dg), Math.Max(db, da));
            if (pixelMax > maxAbsDelta)
            {
                differentPixels++;
                if (samples.Count < sample)
                {
                    int x = (int)(p % width);
                    int y = (int)(p / width);
                    samples.Add(new Dictionary<string, object?>
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["dr"] = dr,
                        ["dg"] = dg,
                        ["db"] = db,
                        ["da"] = da,
                        ["max"] = pixelMax,
                    });
                }
            }

            maxAbsDeltaObserved = Math.Max(maxAbsDeltaObserved, pixelMax);

            int absSum = dr + dg + db + da;
            sumAbsDelta += absSum;

            sumSquaredDelta += (long)dr * dr + (long)dg * dg + (long)db * db + (long)da * da;

            if (diffRgba is not null)
            {
                if (pixelMax <= maxAbsDelta)
                {
                    diffRgba[i + 0] = 0;
                    diffRgba[i + 1] = 0;
                    diffRgba[i + 2] = 0;
                    diffRgba[i + 3] = 255;
                }
                else
                {
                    diffRgba[i + 0] = (byte)dr;
                    diffRgba[i + 1] = (byte)dg;
                    diffRgba[i + 2] = (byte)Math.Max(db, da);
                    diffRgba[i + 3] = 255;
                }
            }
        }

        double meanAbs = channelCount > 0 ? (double)sumAbsDelta / channelCount : 0;
        double rmse = channelCount > 0 ? Math.Sqrt((double)sumSquaredDelta / channelCount) : 0;

        bool withinTolerance = differentPixels == 0;

        var payload = new Dictionary<string, object?>
        {
            ["aPath"] = aPath,
            ["bPath"] = bPath,
            ["width"] = width,
            ["height"] = height,
            ["maxAbsDeltaAllowed"] = maxAbsDelta,
            ["withinTolerance"] = withinTolerance,
            ["differentPixels"] = differentPixels,
            ["totalPixels"] = pixelCount,
            ["maxAbsDeltaObserved"] = maxAbsDeltaObserved,
            ["meanAbsDelta"] = meanAbs,
            ["rmse"] = rmse,
            ["samples"] = samples,
        };

        string json = JsonSerializer.Serialize(payload);
        Console.WriteLine(json);

        if (!string.IsNullOrWhiteSpace(outJsonPath))
        {
            try
            {
                File.WriteAllText(outJsonPath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"写出 JSON 失败：{outJsonPath} ({ex.Message})");
                return 1;
            }
        }

        if (diffRgba is not null && !string.IsNullOrWhiteSpace(outDiffPath))
        {
            if (!PngWriter.TryWriteRgba8(outDiffPath, width, height, diffRgba, out error))
            {
                Console.Error.WriteLine($"写出 diff PNG 失败：{outDiffPath} ({error})");
                return 1;
            }
        }

        return withinTolerance ? 0 : 1;
    }
}

