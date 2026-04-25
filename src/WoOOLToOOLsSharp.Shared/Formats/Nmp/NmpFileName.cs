using System;
using System.IO;

namespace WoOOLToOOLsSharp.Shared.Formats.Nmp;

public static class NmpFileName
{
    public static bool IsMapExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".nmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mmp", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrefabExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        if (extension.Equals(".nmpo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!extension.StartsWith(".nmpo", StringComparison.OrdinalIgnoreCase) || extension.Length is < 6 or > 7)
        {
            return false;
        }

        int version = 0;
        for (int i = 5; i < extension.Length; i++)
        {
            char c = extension[i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            version = checked(version * 10 + (c - '0'));
        }

        return version is >= 1 and <= 99;
    }

    public static bool IsMapFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch
        {
            return false;
        }

        return IsMapExtension(extension);
    }

    public static bool IsPrefabFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extension;
        try
        {
            extension = Path.GetExtension(path);
        }
        catch
        {
            return false;
        }

        return IsPrefabExtension(extension);
    }
}

