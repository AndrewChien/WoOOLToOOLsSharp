using System;
using System.Collections.Generic;
using System.IO;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.EditorBridge;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.MapEditor.App;

public enum MapLayer
{
    Back,
    Middle,
    Floor,
    UnderFront,
    Front,
    OverFront,
}

public readonly record struct TileRef(ushort Library, ushort Image)
{
    public bool IsEmpty => Image == 0;
}

public readonly record struct MapChunk(int ChunkX, int ChunkY, int StartX, int StartY, int Width, int Height);

public sealed class MapDocument
{
    public const int DefaultChunkSize = 32;

    public string Path { get; }
    public NmpMapInfo Info { get; }
    public NmpCellData[] Cells { get; }

    public int Width => Info.Width;
    public int Height => Info.Height;
    public uint Version => Info.Version;

    public int ChunkSize { get; }
    public int ChunkCountX { get; }
    public int ChunkCountY { get; }

    private MapDocument(string path, NmpMapInfo info, NmpCellData[] cells, int chunkSize)
    {
        Path = path ?? string.Empty;
        Info = info ?? new NmpMapInfo();
        Cells = cells ?? Array.Empty<NmpCellData>();

        ChunkSize = chunkSize > 0 ? chunkSize : DefaultChunkSize;
        ChunkCountX = Width > 0 ? (Width + ChunkSize - 1) / ChunkSize : 0;
        ChunkCountY = Height > 0 ? (Height + ChunkSize - 1) / ChunkSize : 0;
    }

    public static MapDocument CreateInMemory(string label, int width, int height, uint version, NmpCellData[] cells)
    {
        if (cells is null) throw new ArgumentNullException(nameof(cells));

        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "width 必须为正数。");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "height 必须为正数。");

        int expected;
        try
        {
            expected = checked(width * height);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "width*height 溢出。");
        }

        if (cells.Length != expected)
        {
            throw new ArgumentException($"cells 长度必须为 width*height={expected}（当前={cells.Length}）。", nameof(cells));
        }

        var info = new NmpMapInfo
        {
            Path = label ?? string.Empty,
            HeaderSize = 0,
            Version = version,
            Width = width,
            Height = height,
            DataOffset = 0,
        };

        return new MapDocument(label ?? string.Empty, info, cells, DefaultChunkSize);
    }

    public static bool TryLoad(string path, out MapDocument? map, out string error)
    {
        map = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "地图路径为空。";
            return false;
        }

        byte[] bytes;
        string label = path;
        if (TryParseWpfSyntheticPath(path, out string wpfPath, out string entryPath))
        {
            string resolvedWpfPath = wpfPath;
            try
            {
                resolvedWpfPath = global::System.IO.Path.GetFullPath(wpfPath);
            }
            catch
            {
                // ignore
            }

            label = LocalEditorBridge.MakeEditorBridgeWpfPath(resolvedWpfPath, entryPath);
            using var archive = new WpfArchive();
            if (!archive.Open(resolvedWpfPath, out error))
            {
                return false;
            }

            WpfEntry? entry = archive.FindEntry(entryPath);
            if (entry is null || entry.IsDirectory)
            {
                error = $"WPF entry 不存在：{resolvedWpfPath}::{entryPath}";
                return false;
            }

            if (!archive.ExtractEntry(entry, out bytes, out error))
            {
                return false;
            }
        }
        else if (!FileIO.TryReadAllBytes(path, out bytes, out error))
        {
            return false;
        }

        if (!NmpCodec.TryReadMapFromMemory(bytes, label, out NmpMapInfo info, out NmpCellData[] cells, out error))
        {
            return false;
        }

        map = new MapDocument(label, info, cells, DefaultChunkSize);
        return true;
    }

    public static bool TryParseWpfSyntheticPath(string path, out string wpfPath, out string entryPath)
    {
        wpfPath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!LocalEditorBridge.ParseEditorBridgeWpfPath(path, out wpfPath, out entryPath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(wpfPath) || string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        try
        {
            string ext = global::System.IO.Path.GetExtension(wpfPath);
            if (!ext.Equals(".wpf", StringComparison.OrdinalIgnoreCase))
            {
                wpfPath = string.Empty;
                entryPath = string.Empty;
                return false;
            }
        }
        catch
        {
            wpfPath = string.Empty;
            entryPath = string.Empty;
            return false;
        }

        return true;
    }

    public bool IsInBounds(int x, int y)
    {
        return x >= 0 && y >= 0 && x < Width && y < Height;
    }

    public int GetIndex(int x, int y)
    {
        return checked(y * Width + x);
    }

    public bool TryGetCell(int x, int y, out NmpCellData cell)
    {
        cell = default;
        if (!IsInBounds(x, y))
        {
            return false;
        }

        int index = GetIndex(x, y);
        if ((uint)index >= (uint)Cells.Length)
        {
            return false;
        }

        cell = Cells[index];
        return true;
    }

    public TileRef GetTile(MapLayer layer, int x, int y)
    {
        if (!TryGetCell(x, y, out NmpCellData cell))
        {
            return default;
        }

        static TileRef ResolveExtraObject(uint raw)
        {
            uint masked = raw & 0x00FFFFFFu;
            ushort image = (ushort)(masked & 0xFFFFu);
            if (image == 0)
            {
                return default;
            }

            ushort pkg = (ushort)((masked >> 16) & 0xFFu);
            if (pkg == 0)
            {
                pkg = 5;
            }

            return new TileRef(pkg, image);
        }

        static TileRef ResolveFrontObject(NmpCellData cell)
        {
            uint masked = cell.FrontImage & 0x00FFFFFFu;
            ushort image = (ushort)(masked & 0xFFFFu);
            if (image == 0)
            {
                return default;
            }

            ushort pkg = (ushort)((masked >> 16) & 0xFFu);
            if (pkg == 0)
            {
                pkg = cell.FrontLibrary;
            }
            if (pkg == 0)
            {
                pkg = 5;
            }

            return new TileRef(pkg, image);
        }

        return layer switch
        {
            MapLayer.Back => new TileRef(cell.BackLibrary, cell.BackImage),
            MapLayer.Middle => new TileRef(cell.MiddleLibrary, cell.MiddleImage),
            MapLayer.Floor => ResolveExtraObject(cell.NearGround),
            MapLayer.UnderFront => ResolveExtraObject(cell.UnderObject),
            MapLayer.Front => ResolveFrontObject(cell),
            MapLayer.OverFront => ResolveExtraObject(cell.OverObject),
            _ => default,
        };
    }

    public IEnumerable<MapChunk> EnumerateChunksCoveringRange(int startX, int startY, int endX, int endY)
    {
        if (Width <= 0 || Height <= 0)
        {
            yield break;
        }

        if (ChunkCountX <= 0 || ChunkCountY <= 0)
        {
            yield break;
        }

        startX = Math.Clamp(startX, 0, Width - 1);
        endX = Math.Clamp(endX, 0, Width - 1);
        startY = Math.Clamp(startY, 0, Height - 1);
        endY = Math.Clamp(endY, 0, Height - 1);

        if (endX < startX || endY < startY)
        {
            yield break;
        }

        int chunkX0 = startX / ChunkSize;
        int chunkY0 = startY / ChunkSize;
        int chunkX1 = endX / ChunkSize;
        int chunkY1 = endY / ChunkSize;

        for (int cy = chunkY0; cy <= chunkY1; cy++)
        {
            for (int cx = chunkX0; cx <= chunkX1; cx++)
            {
                int chunkStartX = cx * ChunkSize;
                int chunkStartY = cy * ChunkSize;
                int chunkW = Math.Min(ChunkSize, Width - chunkStartX);
                int chunkH = Math.Min(ChunkSize, Height - chunkStartY);
                yield return new MapChunk(cx, cy, chunkStartX, chunkStartY, chunkW, chunkH);
            }
        }
    }
}
