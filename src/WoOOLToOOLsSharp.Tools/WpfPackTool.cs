using System;
using System.Collections.Generic;
using System.IO;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.Tools;

internal static class WpfPackTool
{
    public static int RunPack(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("缺少参数：wpf pack <out.wpf> --add <entryPath> <filePath> ...");
            return 2;
        }

        string outPath = args[0];
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("参数错误：out.wpf 为空。");
            return 2;
        }

        var adds = new List<(string EntryPath, string FilePath)>();

        var remaining = new Queue<string>(args[1..]);
        while (remaining.Count > 0)
        {
            string token = remaining.Dequeue();

            if (token.Equals("--add", StringComparison.OrdinalIgnoreCase) && remaining.Count >= 2)
            {
                string entryPath = remaining.Dequeue();
                string filePath = remaining.Dequeue();
                adds.Add((entryPath, filePath));
                continue;
            }
        }

        if (adds.Count == 0)
        {
            Console.Error.WriteLine("缺少参数：至少需要一个 --add <entryPath> <filePath>。");
            return 2;
        }

        try
        {
            string? parent = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"创建输出目录失败：{ex.Message}");
            return 1;
        }

        using var archive = new WpfArchive();
        archive.CreateNew();

        foreach ((string entryPath, string filePath) in adds)
        {
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                Console.Error.WriteLine("参数错误：entryPath 为空。");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                Console.Error.WriteLine($"参数错误：filePath 为空（entryPath={entryPath}）。");
                return 2;
            }

            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"文件不存在：{filePath}");
                return 1;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"读取失败：{filePath}：{ex.Message}");
                return 1;
            }

            archive.AddFileEntry(entryPath, bytes);
        }

        if (!archive.SaveAs(outPath, out string error))
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(error) ? "写入 WPF 失败。" : error);
            return 1;
        }

        Console.WriteLine(outPath);
        return 0;
    }
}

