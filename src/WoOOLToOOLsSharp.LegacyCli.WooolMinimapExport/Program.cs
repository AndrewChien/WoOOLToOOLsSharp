using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WoOOLToOOLsSharp.LegacyCli.WooolMinimapExport;

internal static class Program
{
    private const string MapEditorExeName = "WoOOLToOOLsSharp.MapEditor.exe";
    private const string MapEditorDllName = "WoOOLToOOLsSharp.MapEditor.dll";
    private const string SolutionMarker = "WoOOLToOOLsSharp.slnx";

    public static int Main(string[] args)
    {
        if (!TryResolveMapEditor(out string fileName, out string? dotnetDllArgument, out string error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(dotnetDllArgument))
        {
            psi.ArgumentList.Add(dotnetDllArgument);
        }

        foreach (string a in args ?? Array.Empty<string>())
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process? child = Process.Start(psi);
            if (child is null)
            {
                Console.Error.WriteLine("启动失败：无法创建子进程。");
                return 1;
            }

            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"启动失败：{ex.Message}");
            return 1;
        }
    }

    private static bool TryResolveMapEditor(out string fileName, out string? dotnetDllArgument, out string error)
    {
        fileName = string.Empty;
        dotnetDllArgument = null;
        error = string.Empty;

        string baseDir = AppContext.BaseDirectory;

        // 1) Typical publish layout: wrapper + MapEditor in the same folder.
        string sameDirExe = Path.Combine(baseDir, MapEditorExeName);
        if (File.Exists(sameDirExe))
        {
            fileName = sameDirExe;
            return true;
        }

        string sameDirDll = Path.Combine(baseDir, MapEditorDllName);
        if (File.Exists(sameDirDll))
        {
            fileName = "dotnet";
            dotnetDllArgument = sameDirDll;
            return true;
        }

        // 2) Dev layout: locate the repository root then search MapEditor/bin.
        string? repoRoot = FindRepoRoot(Directory.GetCurrentDirectory())
                           ?? FindRepoRoot(baseDir);

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            string binRoot = Path.Combine(repoRoot, "src", "WoOOLToOOLsSharp.MapEditor", "bin");
            if (Directory.Exists(binRoot))
            {
                string? newestExe = FindNewest(binRoot, MapEditorExeName);
                if (!string.IsNullOrWhiteSpace(newestExe))
                {
                    fileName = newestExe;
                    return true;
                }

                string? newestDll = FindNewest(binRoot, MapEditorDllName);
                if (!string.IsNullOrWhiteSpace(newestDll))
                {
                    fileName = "dotnet";
                    dotnetDllArgument = newestDll;
                    return true;
                }
            }
        }

        error = "无法定位 WoOOLToOOLsSharp.MapEditor 可执行文件。\n"
                + $"已尝试：\n"
                + $"- 同目录：{sameDirExe}\n"
                + $"- 同目录（dll）：{sameDirDll}\n"
                + (!string.IsNullOrWhiteSpace(repoRoot)
                    ? $"- 仓库 bin 搜索：{Path.Combine(repoRoot, "src", "WoOOLToOOLsSharp.MapEditor", "bin")}\n"
                    : "- 仓库 bin 搜索：未找到仓库根目录（WoOOLToOOLsSharp.slnx）。\n")
                + "请确保 MapEditor 已编译，或将 MapEditor 发布物与本 wrapper 放在同一目录。";
        return false;
    }

    private static string? FindRepoRoot(string startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        string? current = startPath;
        for (int i = 0; i < 16 && !string.IsNullOrWhiteSpace(current); i++)
        {
            string marker = Path.Combine(current, SolutionMarker);
            if (File.Exists(marker))
            {
                return current;
            }

            try
            {
                current = Directory.GetParent(current)?.FullName;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? FindNewest(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Select(static f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

