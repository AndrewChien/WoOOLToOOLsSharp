using System;

namespace WoOOLToOOLsSharp.LegacyCli.WooolNmpDump;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        // 兼容一些脚本直接写：woool_nmp_dump <in> <out>
        if (args.Length >= 2 && !LooksLikeOption(args[0]) && !LooksLikeOption(args[1]))
        {
            var mapped = new string[args.Length + 1];
            mapped[0] = "--nmp-dump";
            Array.Copy(args, 0, mapped, 1, args.Length);
            return RunTools(mapped);
        }

        return RunTools(args);
    }

    private static bool LooksLikeOption(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        return s.StartsWith("-", StringComparison.Ordinal);
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("缺少参数。用法：");
        Console.Error.WriteLine("  woool_nmp_dump --nmp-dump <input.nmp|input.mmp> <output.json>");
        Console.Error.WriteLine("  woool_nmp_dump <input.nmp|input.mmp> <output.json>   (兼容写法)");
    }

    private static int RunTools(string[] args) => WoOOLToOOLsSharp.Tools.Program.Main(args);
}
