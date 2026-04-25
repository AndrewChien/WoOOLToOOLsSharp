namespace WoOOLToOOLsSharp.Shared.Formats.Nmp;

public struct NmpCellData
{
    // 32-bit fields (align with old struct for future parity)
    public uint FrontImage;
    public uint UnderObject;
    public uint OverObject;
    public uint ColorAdjTile;
    public uint ColorAdjSmTile;
    public uint ColorAdjObject;
    public uint ColorAdjEffect;
    public uint ColorAdjFloor;
    public uint NearGround;
    public uint Group;
    public uint ColorOverObj;

    // 16-bit fields
    public ushort BackImage;
    public ushort BackLibrary;
    public ushort MiddleImage;
    public ushort MiddleLibrary;
    public ushort ObjectHeight;
    public ushort ExtendedAttributes;

    public ushort MiddleImage2;
    public ushort MiddleLibrary2;
    public ushort MiddleAlphaMask;

    // 8-bit fields
    public byte FrontLibrary;
    public byte Flags;
    public byte DoorIndex;
    public byte DoorOffset;
    public byte Light;
    public byte FrontAnimFrame;
    public byte FrontAnimTick;
    public byte Sound;
    public byte BackAnimTick;
    public byte MiddleAnimTick;

    public byte ExtraAttrV12_0;
    public byte ExtraAttrV12_1;
    public byte ExtraAttrV12_2;
    public byte ExtraAttrV12_3;
    public byte ExtraAttrV12_4;
    public byte ExtraAttrV12_5;
}

