using System;
using System.Collections.Generic;
using System.IO;

namespace WoOOLToOOLsSharp.Shared;

public static class FileIO
{
    public static bool TryReadAllBytes(string path, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                string? resolved = ResolveCaseInsensitivePath(path);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    path = resolved;
                }
            }

            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取文件失败: {path}\n{ex.Message}";
            return false;
        }
    }

    public static bool TryWriteAllBytes(string path, ReadOnlySpan<byte> bytes, out string error)
    {
        error = string.Empty;

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, bytes.ToArray());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"写入文件失败: {path}\n{ex.Message}";
            return false;
        }
    }

    public static string? ResolveCaseInsensitivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            return path;
        }

        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return null;
        }

        string targetName = Path.GetFileName(path);
        foreach (string entry in Directory.EnumerateFileSystemEntries(parent))
        {
            string name = Path.GetFileName(entry);
            if (IEqualAscii(name, targetName))
            {
                return entry;
            }
        }

        return null;
    }

    public static bool IEqualAscii(string a, string b)
    {
        return a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}


