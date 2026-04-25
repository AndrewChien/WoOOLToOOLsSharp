using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WoOOLToOOLsSharp.MapEditor.App;

internal readonly record struct FolderMetaEntry(string FileName, string DisplayName, string DataFolderName);

/// <summary>
/// Old MapEditor stores per-folder metadata in a ".meta" file:
/// - display name (for UI listing / tab title)
/// - data folder name (which configured Data Path to use for this file)
/// </summary>
internal static class FolderMetaCodec
{
    private const string MetaFileName = ".meta";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string GetMetaPathForDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        try
        {
            return Path.Combine(directory, MetaFileName);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryReadDirectory(string directory, out Dictionary<string, FolderMetaEntry> entries, out string error)
    {
        entries = new Dictionary<string, FolderMetaEntry>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return true;
        }

        string metaPath = GetMetaPathForDirectory(directory);
        if (string.IsNullOrWhiteSpace(metaPath))
        {
            return true;
        }

        if (!File.Exists(metaPath))
        {
            return true;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(metaPath, Utf8NoBom);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = (lines[i] ?? string.Empty).Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            const string Prefix = "entry ";
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line[Prefix.Length..];
            int idx = 0;
            if (!TryReadQuoted(payload, ref idx, out string fileName)
                || !TryReadQuoted(payload, ref idx, out string displayName)
                || !TryReadQuoted(payload, ref idx, out string dataFolderName))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            entries[fileName] = new FolderMetaEntry(fileName, displayName ?? string.Empty, dataFolderName ?? string.Empty);
        }

        return true;
    }

    public static bool TryGetEntryForFilePath(string filePath, out FolderMetaEntry entry, out string error)
    {
        entry = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string directory;
        string fileName;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
            fileName = Path.GetFileName(filePath) ?? string.Empty;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (!TryReadDirectory(directory, out Dictionary<string, FolderMetaEntry> entries, out error))
        {
            return false;
        }

        return entries.TryGetValue(fileName, out entry);
    }

    public static bool TryUpsertFileEntry(string filePath, string displayName, string dataFolderName, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "filePath 为空。";
            return false;
        }

        string directory;
        string fileName;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
            fileName = Path.GetFileName(filePath) ?? string.Empty;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            error = "路径无效。";
            return false;
        }

        if (!TryReadDirectory(directory, out Dictionary<string, FolderMetaEntry> entries, out string readError))
        {
            error = $"读取 .meta 失败：{readError}";
            return false;
        }

        entries[fileName] = new FolderMetaEntry(fileName, displayName ?? string.Empty, dataFolderName ?? string.Empty);

        string metaPath = GetMetaPathForDirectory(directory);
        if (string.IsNullOrWhiteSpace(metaPath))
        {
            error = "无法生成 .meta 路径。";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        try
        {
            using var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs, Utf8NoBom);
            writer.NewLine = "\n";

            writer.WriteLine("# WoOOLTools folder metadata");
            writer.WriteLine("version=1");

            List<FolderMetaEntry> sorted = new(entries.Values);
            sorted.Sort(static (a, b) =>
                StringComparer.OrdinalIgnoreCase.Compare(a.FileName ?? string.Empty, b.FileName ?? string.Empty));

            for (int i = 0; i < sorted.Count; i++)
            {
                FolderMetaEntry e = sorted[i];
                writer.Write("entry ");
                writer.Write(Quote(e.FileName));
                writer.Write(' ');
                writer.Write(Quote(e.DisplayName));
                writer.Write(' ');
                writer.Write(Quote(e.DataFolderName));
                writer.Write('\n');
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    private static string Quote(string value)
    {
        value ??= string.Empty;
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' || c == '"')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool TryReadQuoted(string s, ref int index, out string value)
    {
        value = string.Empty;

        if (s is null)
        {
            return false;
        }

        int i = index;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        if (i >= s.Length || s[i] != '"')
        {
            return false;
        }

        i++; // consume opening quote
        var sb = new StringBuilder(capacity: 64);
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"')
            {
                index = i;
                value = sb.ToString();
                return true;
            }

            if (c == '\\' && i < s.Length)
            {
                // Match C++ std::quoted default escaping: backslash escapes the next char.
                sb.Append(s[i++]);
                continue;
            }

            sb.Append(c);
        }

        return false;
    }
}
