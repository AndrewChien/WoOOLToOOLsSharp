namespace WoOOLToOOLsSharp.Shared;

public sealed class DecodedImage
{
    public int Width { get; set; }
    public int Height { get; set; }

    public short OffsetX { get; set; }
    public short OffsetY { get; set; }

    public short CenterX { get; set; }
    public short CenterY { get; set; }

    public byte[] Rgba8 { get; set; } = [];

    public bool IsValid =>
        Width > 0
        && Height > 0
        && Rgba8.Length == Width * Height * 4;
}

