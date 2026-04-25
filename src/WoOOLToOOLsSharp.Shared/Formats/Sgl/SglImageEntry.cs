namespace WoOOLToOOLsSharp.Shared.Formats.Sgl;

public sealed class SglImageEntry
{
    public int Index { get; init; }
    public uint Offset { get; init; }
    public uint Size { get; init; }

    public bool IsEmpty => Size == 0;
}

