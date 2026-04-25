namespace WoOOLToOOLsSharp.Shared.Formats.Nmp;

public sealed class NmpMapInfo
{
    public string Path { get; init; } = string.Empty;
    public uint HeaderSize { get; init; }
    public uint Version { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint DataOffset { get; init; }

    public int CellCount
    {
        get
        {
            long count = (long)Width * Height;
            return count is > 0 and <= int.MaxValue ? (int)count : 0;
        }
    }
}

