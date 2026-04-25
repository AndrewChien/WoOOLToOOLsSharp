using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace WoOOLToOOLsSharp.MapEditor.App;

internal readonly record struct SceneLightMaskRule(
    int ObjectPackageId,
    int TextureId,
    int MaskImageId,
    int PosX,
    int PosY,
    int ScalePercent);

internal readonly record struct SceneLightMapRule(bool Enabled, int Light);

internal sealed class SceneLightMaskConfig
{
    public string SourcePath { get; init; } = string.Empty;
    public Dictionary<string, SceneLightMapRule> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<ulong, List<SceneLightMaskRule>> RulesByObjectTexture { get; } = new();
}

internal static class SceneLightMaskConfigProvider
{
    private sealed class CacheEntry
    {
        public string ResolvedPath { get; set; } = string.Empty;
        public DateTime WriteTimeUtc { get; set; }
        public SceneLightMaskConfig? Config { get; set; }
    }

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SceneLightMaskConfig? GetForDataPath(string? dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return null;
        }

        string key = dataPath;

        lock (CacheLock)
        {
            Cache.TryGetValue(key, out CacheEntry? entry);
            entry ??= new CacheEntry();

            string resolved = ResolveConfigPath(key);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                Cache[key] = new CacheEntry();
                return null;
            }

            DateTime writeTimeUtc;
            try
            {
                writeTimeUtc = File.GetLastWriteTimeUtc(resolved);
            }
            catch
            {
                writeTimeUtc = default;
            }

            bool needReload =
                entry.Config is null
                || !string.Equals(entry.ResolvedPath, resolved, StringComparison.OrdinalIgnoreCase)
                || (writeTimeUtc != default && entry.WriteTimeUtc != writeTimeUtc);

            if (needReload)
            {
                if (!TryParseConfigFile(resolved, out SceneLightMaskConfig parsed))
                {
                    Cache[key] = new CacheEntry();
                    return null;
                }

                entry.ResolvedPath = resolved;
                entry.WriteTimeUtc = writeTimeUtc;
                entry.Config = parsed;
            }

            Cache[key] = entry;
            return entry.Config;
        }
    }

    public static string DeriveSceneLightMapId(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return string.Empty;
        }

        string path = documentPath;

        const string wpfSep = ":://";
        int wpfPos = path.IndexOf(wpfSep, StringComparison.Ordinal);
        if (wpfPos >= 0 && wpfPos + wpfSep.Length < path.Length)
        {
            path = path.Substring(wpfPos + wpfSep.Length);
        }

        return NormalizeMapId(path);
    }

    public static bool IsMapEnabled(SceneLightMaskConfig? config, string? mapId)
    {
        if (config is null || string.IsNullOrWhiteSpace(mapId))
        {
            return false;
        }

        if (!config.Maps.TryGetValue(NormalizeMapId(mapId), out SceneLightMapRule rule))
        {
            return false;
        }

        return rule.Enabled;
    }

    public static int GetMapLightLevel(SceneLightMaskConfig? config, string? mapId, int fallback)
    {
        if (config is null || string.IsNullOrWhiteSpace(mapId))
        {
            return fallback;
        }

        if (!config.Maps.TryGetValue(NormalizeMapId(mapId), out SceneLightMapRule rule) || !rule.Enabled)
        {
            return fallback;
        }

        return Math.Clamp(rule.Light, 0, 255);
    }

    public static IReadOnlyList<SceneLightMaskRule>? FindRules(SceneLightMaskConfig? config, int objectPackageId, int textureId)
    {
        if (config is null || objectPackageId <= 0 || textureId <= 0)
        {
            return null;
        }

        ulong key = BuildObjectTextureKey(objectPackageId, textureId);
        return config.RulesByObjectTexture.TryGetValue(key, out List<SceneLightMaskRule>? rules) ? rules : null;
    }

    private static ulong BuildObjectTextureKey(int objectPackageId, int textureId)
    {
        return ((ulong)(uint)objectPackageId << 32) | (uint)textureId;
    }

    private static string NormalizeMapId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        int slashPos = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slashPos != -1 && slashPos + 1 < value.Length)
        {
            value = value.Substring(slashPos + 1);
        }

        int dotPos = value.LastIndexOf('.');
        if (dotPos > 0)
        {
            value = value.Substring(0, dotPos);
        }

        return value.Trim().ToUpperInvariant();
    }

    private static string ResolveConfigPath(string dataPath)
    {
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return string.Empty;
        }

        string current = dataPath;
        try
        {
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                string? parent = Path.GetDirectoryName(current);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    current = parent;
                }
            }

            current = Path.GetFullPath(current);
        }
        catch
        {
            // keep original path
        }

        var candidates = new List<string>();

        string probe = current;
        for (int depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(probe); depth++)
        {
            candidates.Add(Path.Combine(probe, "config", "SceneLightMaskCfg.xml"));
            candidates.Add(Path.Combine(probe, "Data", "config", "SceneLightMaskCfg.xml"));

            string? parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            probe = parent;
        }

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore and continue probing
            }
        }

        return string.Empty;
    }

    private static bool TryParseConfigFile(string xmlPath, out SceneLightMaskConfig config)
    {
        config = new SceneLightMaskConfig();

        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
        {
            return false;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(xmlPath, LoadOptions.None);
        }
        catch
        {
            return false;
        }

        XElement? root = doc.Root is { } docRoot && docRoot.Name.LocalName.Equals("SceneMaskItem", StringComparison.Ordinal)
            ? docRoot
            : doc.Element("SceneMaskItem");
        if (root is null)
        {
            return false;
        }

        var parsed = new SceneLightMaskConfig
        {
            SourcePath = xmlPath,
        };

        XElement? textureRoot = root.Element("TextureID");
        if (textureRoot is not null)
        {
            foreach (XElement idNode in textureRoot.Elements("ID"))
            {
                int objectId = ReadIntAttr(idNode, "Object", 0);
                int textureId = ReadIntAttr(idNode, "TextureID", 0);
                int maskId = ReadIntAttr(idNode, "Mask", 0);
                if (objectId <= 0 || textureId <= 0 || maskId <= 0)
                {
                    continue;
                }

                int posX = ReadIntAttr(idNode, "pos_x", 0);
                int posY = ReadIntAttr(idNode, "pos_y", 0);
                int scalePercent = Math.Max(1, ReadIntAttr(idNode, "scale", 100));

                var rule = new SceneLightMaskRule(objectId, textureId, maskId, posX, posY, scalePercent);
                ulong key = BuildObjectTextureKey(objectId, textureId);
                if (!parsed.RulesByObjectTexture.TryGetValue(key, out List<SceneLightMaskRule>? list))
                {
                    list = new List<SceneLightMaskRule>();
                    parsed.RulesByObjectTexture[key] = list;
                }

                list.Add(rule);
            }
        }

        foreach (XElement mapNode in root.Elements("MapId"))
        {
            string mapId = NormalizeMapId(ReadStringAttr(mapNode, "mapId"));
            if (string.IsNullOrWhiteSpace(mapId))
            {
                continue;
            }

            int light = Math.Clamp(ReadIntAttr(mapNode, "light", 150), 0, 255);
            parsed.Maps[mapId] = new SceneLightMapRule(true, light);
        }

        config = parsed;
        return parsed.Maps.Count > 0 || parsed.RulesByObjectTexture.Count > 0;
    }

    private static int ReadIntAttr(XElement element, string name, int fallback)
    {
        XAttribute? attr = element.Attribute(name);
        if (attr is null)
        {
            return fallback;
        }

        return int.TryParse(attr.Value, out int value) ? value : fallback;
    }

    private static string ReadStringAttr(XElement element, string name)
    {
        XAttribute? attr = element.Attribute(name);
        return attr?.Value ?? string.Empty;
    }
}
