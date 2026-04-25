using System;

namespace WoOOLToOOLsSharp.ContentEditor.App;

public static class WpfKey
{
    public const string Separator = "::";

    public static bool TryParse(string key, out string wpfPath, out string entryPath)
    {
        wpfPath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        int sep = key.IndexOf(Separator, StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        int entryStart = sep + Separator.Length;
        if (entryStart >= key.Length)
        {
            return false;
        }

        wpfPath = key.Substring(0, sep);
        entryPath = key.Substring(entryStart);
        return !string.IsNullOrWhiteSpace(wpfPath) && !string.IsNullOrWhiteSpace(entryPath);
    }

    public static string Make(string wpfPath, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(wpfPath))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return wpfPath;
        }

        return wpfPath + Separator + entryPath;
    }

    public static string GetLeafName(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return string.Empty;
        }

        int slash = entryPath.LastIndexOf('/');
        if (slash < 0 || slash + 1 >= entryPath.Length)
        {
            return entryPath;
        }

        return entryPath.Substring(slash + 1);
    }

    public static string GetDirectoryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return string.Empty;
        }

        int slash = entryPath.LastIndexOf('/');
        if (slash <= 0)
        {
            return string.Empty;
        }

        return entryPath.Substring(0, slash);
    }
}

