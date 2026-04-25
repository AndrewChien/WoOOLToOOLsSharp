namespace WoOOLToOOLsSharp.MapEditor.App;

internal enum ObjectListEntryKind
{
    BackTile = 0,
    MiddleTile = 1,
    FrontObject = 2,
    UnderObject = 3,
    OverObject = 4,
    NearGround = 5,
}

internal readonly record struct ObjectListKey(ObjectListEntryKind Kind, int Library, int Image);

internal readonly record struct ObjectListEntry(
    ObjectListEntryKind Kind,
    int Library,
    int Image,
    int Count,
    int SampleX,
    int SampleY)
{
    public ObjectListKey Key => new(Kind, Library, Image);

    public string KindLabel => Kind switch
    {
        ObjectListEntryKind.BackTile => "后景(Back)",
        ObjectListEntryKind.MiddleTile => "中景(Middle)",
        ObjectListEntryKind.FrontObject => "前景(Front)",
        ObjectListEntryKind.UnderObject => "底层对象(UnderObject)",
        ObjectListEntryKind.OverObject => "上层对象(OverObject)",
        ObjectListEntryKind.NearGround => "近地(NearGround)",
        _ => Kind.ToString(),
    };

    public uint Packed24 => (uint)(((Library & 0xFF) << 16) | (Image & 0xFFFF));

    public string ToDisplayString()
        => $"{KindLabel}  库={Library} 图={Image}  次数={Count}  样本=({SampleX},{SampleY})  (0x{Packed24:X6})";
}
