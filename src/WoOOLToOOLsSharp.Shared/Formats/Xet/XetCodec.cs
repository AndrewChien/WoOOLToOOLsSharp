using System;
using System.Buffers.Binary;

namespace WoOOLToOOLsSharp.Shared.Formats.Xet;

public static class XetCodec
{
    public const ushort XetMagic = 0x0178; // bytes: 0x78 0x01（与部分 zlib header 重叠，需结合 fmt 判定）

    private static readonly uint[] UpTab =
    {
        0x00000760, 0x00500800, 0x00100800, 0x00730814, 0x001F0712, 0x00700800, 0x00300800, 0x00C00900,
        0x000A0710, 0x00600800, 0x00200800, 0x00A00900, 0x00000800, 0x00800800, 0x00400800, 0x00E00900,
        0x00060710, 0x00580800, 0x00180800, 0x00900900, 0x003B0713, 0x00780800, 0x00380800, 0x00D00900,
        0x00110711, 0x00680800, 0x00280800, 0x00B00900, 0x00080800, 0x00880800, 0x00480800, 0x00F00900,
        0x00040710, 0x00540800, 0x00140800, 0x00E30815, 0x002B0713, 0x00740800, 0x00340800, 0x00C80900,
        0x000D0711, 0x00640800, 0x00240800, 0x00A80900, 0x00040800, 0x00840800, 0x00440800, 0x00E80900,
        0x00080710, 0x005C0800, 0x001C0800, 0x00980900, 0x00530714, 0x007C0800, 0x003C0800, 0x00D80900,
        0x00170712, 0x006C0800, 0x002C0800, 0x00B80900, 0x000C0800, 0x008C0800, 0x004C0800, 0x00F80900,
        0x00030710, 0x00520800, 0x00120800, 0x00A30815, 0x00230713, 0x00720800, 0x00320800, 0x00C40900,
        0x000B0711, 0x00620800, 0x00220800, 0x00A40900, 0x00020800, 0x00820800, 0x00420800, 0x00E40900,
        0x00070710, 0x005A0800, 0x001A0800, 0x00940900, 0x00430714, 0x007A0800, 0x003A0800, 0x00D40900,
        0x00130712, 0x006A0800, 0x002A0800, 0x00B40900, 0x000A0800, 0x008A0800, 0x004A0800, 0x00F40900,
        0x00050710, 0x00560800, 0x00160800, 0x00000840, 0x00330713, 0x00760800, 0x00360800, 0x00CC0900,
        0x000F0711, 0x00660800, 0x00260800, 0x00AC0900, 0x00060800, 0x00860800, 0x00460800, 0x00EC0900,
        0x00090710, 0x005E0800, 0x001E0800, 0x009C0900, 0x00630714, 0x007E0800, 0x003E0800, 0x00DC0900,
        0x001B0712, 0x006E0800, 0x002E0800, 0x00BC0900, 0x000E0800, 0x008E0800, 0x004E0800, 0x00FC0900,
        0x00000760, 0x00510800, 0x00110800, 0x00830815, 0x001F0712, 0x00710800, 0x00310800, 0x00C20900,
        0x000A0710, 0x00610800, 0x00210800, 0x00A20900, 0x00010800, 0x00810800, 0x00410800, 0x00E20900,
        0x00060710, 0x00590800, 0x00190800, 0x00920900, 0x003B0713, 0x00790800, 0x00390800, 0x00D20900,
        0x00110711, 0x00690800, 0x00290800, 0x00B20900, 0x00090800, 0x00890800, 0x00490800, 0x00F20900,
        0x00040710, 0x00550800, 0x00150800, 0x01020810, 0x002B0713, 0x00750800, 0x00350800, 0x00CA0900,
        0x000D0711, 0x00650800, 0x00250800, 0x00AA0900, 0x00050800, 0x00850800, 0x00450800, 0x00EA0900,
        0x00080710, 0x005D0800, 0x001D0800, 0x009A0900, 0x00530714, 0x007D0800, 0x003D0800, 0x00DA0900,
        0x00170712, 0x006D0800, 0x002D0800, 0x00BA0900, 0x000D0800, 0x008D0800, 0x004D0800, 0x00FA0900,
        0x00030710, 0x00530800, 0x00130800, 0x00C30815, 0x00230713, 0x00730800, 0x00330800, 0x00C60900,
        0x000B0711, 0x00630800, 0x00230800, 0x00A60900, 0x00030800, 0x00830800, 0x00430800, 0x00E60900,
        0x00070710, 0x005B0800, 0x001B0800, 0x00960900, 0x00430714, 0x007B0800, 0x003B0800, 0x00D60900,
        0x00130712, 0x006B0800, 0x002B0800, 0x00B60900, 0x000B0800, 0x008B0800, 0x004B0800, 0x00F60900,
        0x00050710, 0x00570800, 0x00170800, 0x00000840, 0x00330713, 0x00770800, 0x00370800, 0x00CE0900,
        0x000F0711, 0x00670800, 0x00270800, 0x00AE0900, 0x00070800, 0x00870800, 0x00470800, 0x00EE0900,
        0x00090710, 0x005F0800, 0x001F0800, 0x009E0900, 0x00630714, 0x007F0800, 0x003F0800, 0x00DE0900,
        0x001B0712, 0x006F0800, 0x002F0800, 0x00BE0900, 0x000F0800, 0x008F0800, 0x004F0800, 0x00FE0900,
        0x00000760, 0x00500800, 0x00100800, 0x00730814, 0x001F0712, 0x00700800, 0x00300800, 0x00C10900,
        0x000A0710, 0x00600800, 0x00200800, 0x00A10900, 0x00000800, 0x00800800, 0x00400800, 0x00E10900,
        0x00060710, 0x00580800, 0x00180800, 0x00910900, 0x003B0713, 0x00780800, 0x00380800, 0x00D10900,
        0x00110711, 0x00680800, 0x00280800, 0x00B10900, 0x00080800, 0x00880800, 0x00480800, 0x00F10900,
        0x00040710, 0x00540800, 0x00140800, 0x00E30815, 0x002B0713, 0x00740800, 0x00340800, 0x00C90900,
        0x000D0711, 0x00640800, 0x00240800, 0x00A90900, 0x00040800, 0x00840800, 0x00440800, 0x00E90900,
        0x00080710, 0x005C0800, 0x001C0800, 0x00990900, 0x00530714, 0x007C0800, 0x003C0800, 0x00D90900,
        0x00170712, 0x006C0800, 0x002C0800, 0x00B90900, 0x000C0800, 0x008C0800, 0x004C0800, 0x00F90900,
        0x00030710, 0x00520800, 0x00120800, 0x00A30815, 0x00230713, 0x00720800, 0x00320800, 0x00C50900,
        0x000B0711, 0x00620800, 0x00220800, 0x00A50900, 0x00020800, 0x00820800, 0x00420800, 0x00E50900,
        0x00070710, 0x005A0800, 0x001A0800, 0x00950900, 0x00430714, 0x007A0800, 0x003A0800, 0x00D50900,
        0x00130712, 0x006A0800, 0x002A0800, 0x00B50900, 0x000A0800, 0x008A0800, 0x004A0800, 0x00F50900,
        0x00050710, 0x00560800, 0x00160800, 0x00000840, 0x00330713, 0x00760800, 0x00360800, 0x00CD0900,
        0x000F0711, 0x00660800, 0x00260800, 0x00AD0900, 0x00060800, 0x00860800, 0x00460800, 0x00ED0900,
        0x00090710, 0x005E0800, 0x001E0800, 0x009D0900, 0x00630714, 0x007E0800, 0x003E0800, 0x00DD0900,
        0x001B0712, 0x006E0800, 0x002E0800, 0x00BD0900, 0x000E0800, 0x008E0800, 0x004E0800, 0x00FD0900,
        0x00000760, 0x00510800, 0x00110800, 0x00830815, 0x001F0712, 0x00710800, 0x00310800, 0x00C30900,
        0x000A0710, 0x00610800, 0x00210800, 0x00A30900, 0x00010800, 0x00810800, 0x00410800, 0x00E30900,
        0x00060710, 0x00590800, 0x00190800, 0x00930900, 0x003B0713, 0x00790800, 0x00390800, 0x00D30900,
        0x00110711, 0x00690800, 0x00290800, 0x00B30900, 0x00090800, 0x00890800, 0x00490800, 0x00F30900,
        0x00040710, 0x00550800, 0x00150800, 0x01020810, 0x002B0713, 0x00750800, 0x00350800, 0x00CB0900,
        0x000D0711, 0x00650800, 0x00250800, 0x00AB0900, 0x00050800, 0x00850800, 0x00450800, 0x00EB0900,
        0x00080710, 0x005D0800, 0x001D0800, 0x009B0900, 0x00530714, 0x007D0800, 0x003D0800, 0x00DB0900,
        0x00170712, 0x006D0800, 0x002D0800, 0x00BB0900, 0x000D0800, 0x008D0800, 0x004D0800, 0x00FB0900,
        0x00030710, 0x00530800, 0x00130800, 0x00C30815, 0x00230713, 0x00730800, 0x00330800, 0x00C70900,
        0x000B0711, 0x00630800, 0x00230800, 0x00A70900, 0x00030800, 0x00830800, 0x00430800, 0x00E70900,
        0x00070710, 0x005B0800, 0x001B0800, 0x00970900, 0x00430714, 0x007B0800, 0x003B0800, 0x00D70900,
        0x00130712, 0x006B0800, 0x002B0800, 0x00B70900, 0x000B0800, 0x008B0800, 0x004B0800, 0x00F70900,
        0x00050710, 0x00570800, 0x00170800, 0x00000840, 0x00330713, 0x00770800, 0x00370800, 0x00CF0900,
        0x000F0711, 0x00670800, 0x00270800, 0x00AF0900, 0x00070800, 0x00870800, 0x00470800, 0x00EF0900,
        0x00090710, 0x005F0800, 0x001F0800, 0x009F0900, 0x00630714, 0x007F0800, 0x003F0800, 0x00DF0900,
        0x001B0712, 0x006F0800, 0x002F0800, 0x00BF0900, 0x000F0800, 0x008F0800, 0x004F0800, 0x00FF0900,
    };

    private static readonly uint[] DownTab =
    {
        0x00010510, 0x01010517, 0x00110513, 0x1001051B, 0x00050511, 0x04010519, 0x00410515, 0x4001051D,
        0x00030510, 0x02010518, 0x00210514, 0x2001051C, 0x00090512, 0x0801051A, 0x00810516, 0x00000540,
        0x00020510, 0x01810517, 0x00190513, 0x1801051B, 0x00070511, 0x06010519, 0x00610515, 0x6001051D,
        0x00040510, 0x03010518, 0x00310514, 0x3001051C, 0x000D0512, 0x0C01051A, 0x00C10516, 0x00000540,
    };

    public static bool IsXetCompressed(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;

        uint header = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));
        if ((header & 0x0000FFFFu) != XetMagic) return false;

        // Filter out plain zlib streams that share the same leading bytes (78 01).
        uint fmt = (header >> 17) & 0x03u;
        if (fmt == 1) return true;
        if (fmt == 2) return data.Length >= 8;
        return false;
    }

    /// <summary>
    /// 解码 XET 数据到目标缓冲区。返回写入的字节数（0 表示失败/未写入）。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> src, Span<byte> dst, out int bytesWritten, out string error)
    {
        bytesWritten = 0;
        error = string.Empty;

        if (src.Length < 4 || dst.IsEmpty)
        {
            error = "XET：输入数据过小或输出缓冲区为空";
            return false;
        }

        uint header = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(0, 4));
        if ((header & 0x0000FFFFu) != XetMagic)
        {
            error = "XET：magic 不匹配";
            return false;
        }

        uint fmt = (header >> 17) & 0x03u;

        ReadOnlySpan<byte> readerData;
        uint initBits;
        int initCount;

        if (fmt == 1)
        {
            readerData = src.Slice(4);
            initBits = header >> 19;
            initCount = 13;
        }
        else if (fmt == 2)
        {
            if (src.Length < 8)
            {
                error = "XET：fmt=2 但数据截断（缺少第二个 header）";
                return false;
            }

            uint h2 = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
            readerData = src.Slice(8);
            initBits = h2 >> 1;
            initCount = 31;
        }
        else
        {
            error = $"XET：未知 fmt={fmt}";
            return false;
        }

        var reader = new BitReader(readerData);
        reader.Init(initBits, initCount);

        const uint upMask = (1u << 9) - 1;
        const uint downMask = (1u << 5) - 1;

        int dp = 0;

        while (dp < dst.Length && reader.Remaining > 0)
        {
            reader.Ensure(15);
            uint entry = UpTab[reader.Peek(9) & upMask];
            int bits = (int)((entry >> 8) & 0xFFu);
            reader.Consume(bits);
            uint flags = entry & 0xFFu;

            while ((flags & 0x10u) == 0 && (flags & 0x40u) == 0 && flags != 0)
            {
                uint eb = flags & 0x0Fu;
                reader.Ensure((int)eb);
                uint extra = reader.Peek((int)eb);
                uint ni = (entry >> 16) + extra;
                if (ni >= 0x200) break;

                entry = UpTab[ni];
                bits = (int)((entry >> 8) & 0xFFu);
                reader.Consume(bits);
                flags = entry & 0xFFu;
            }

            if (flags == 0)
            {
                dst[dp++] = (byte)((entry >> 16) & 0xFFu);
                continue;
            }

            if ((flags & 0x40u) != 0)
            {
                break;
            }

            uint extraBits = flags & 0x0Fu;
            reader.Ensure((int)extraBits);
            uint extraLen = reader.Read((int)extraBits);
            uint length = (entry >> 16) + extraLen;

            reader.Ensure(15);
            entry = DownTab[reader.Peek(5) & downMask];
            bits = (int)((entry >> 8) & 0xFFu);
            reader.Consume(bits);
            flags = entry & 0xFFu;

            while ((flags & 0x10u) == 0 && (flags & 0x40u) == 0)
            {
                uint deb = flags & 0x0Fu;
                reader.Ensure((int)deb);
                uint de = reader.Peek((int)deb);
                uint ni = (entry >> 16) + de;
                if (ni >= 0x20) break;

                entry = DownTab[ni];
                bits = (int)((entry >> 8) & 0xFFu);
                reader.Consume(bits);
                flags = entry & 0xFFu;
            }

            if ((flags & 0x40u) != 0)
            {
                break;
            }

            extraBits = flags & 0x0Fu;
            reader.Ensure((int)extraBits);
            uint extraDist = reader.Read((int)extraBits);
            uint distance = (entry >> 16) + extraDist;

            for (uint i = 0; i < length && dp < dst.Length; i++)
            {
                if (distance != 0 && dp >= (int)distance)
                {
                    dst[dp] = dst[dp - (int)distance];
                }
                dp++;
            }
        }

        bytesWritten = dp;
        return dp > 0;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private uint _bits;
        private int _count;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public int Remaining => _data.Length > _pos ? _data.Length - _pos : 0;

        public void Init(uint bits, int count)
        {
            _bits = bits;
            _count = count;
        }

        public void Ensure(int n)
        {
            while (_count < n && _pos < _data.Length)
            {
                _bits |= (uint)_data[_pos++] << _count;
                _count += 8;
            }
        }

        public uint Peek(int n)
        {
            Ensure(n);
            return _bits & ((1u << n) - 1u);
        }

        public void Consume(int n)
        {
            _bits >>= n;
            _count -= n;
        }

        public uint Read(int n)
        {
            uint v = Peek(n);
            Consume(n);
            return v;
        }
    }
}
