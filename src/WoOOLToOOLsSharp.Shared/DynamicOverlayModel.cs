namespace WoOOLToOOLsSharp.Shared;

public sealed record DynamicOverlayRecord
{
    public string Kind { get; init; } = "scene";
    public string Layer { get; init; } = "front";
    public string CoordinateSpace { get; init; } = "pixel";
    public int X { get; init; }
    public int Y { get; init; }
    public int PackageId { get; init; }
    public int ImageId { get; init; }
    public int Frame { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
    public byte Alpha { get; init; } = 255;
    public float Scale { get; init; } = 1.0f;
    public byte TintR { get; init; } = 255;
    public byte TintG { get; init; } = 255;
    public byte TintB { get; init; } = 255;
    public byte TintA { get; init; } = 255;
    public string BlendMode { get; init; } = "alpha";
    public int Order { get; init; }
    public string Label { get; init; } = string.Empty;
}

public sealed record DynamicOverlayDocument
{
    public string SourcePath { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string EncodingHint { get; init; } = string.Empty;
    public bool WasCompressed { get; init; }
    public IReadOnlyList<DynamicOverlayRecord> Records { get; init; } = [];
}
