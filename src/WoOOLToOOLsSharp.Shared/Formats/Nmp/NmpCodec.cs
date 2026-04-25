using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WoOOLToOOLsSharp.Shared.Formats.Nmp;

public static class NmpCodec
{
    private const int ParallelThreshold = 50_000;

    // Legacy MMP format (".mmp"): 6-byte header + fixed u16 grid payload.
    private const uint MmpLegacyVersion = 0;
    private const ushort MmpMagic = 0x0201;
    private const int MmpHeaderBytes = 6;

    // Primary attribute flags (stored in the 1-byte flags field)
    private const byte AtoSmTiles = 0x02;
    private const byte AtoTiles = 0x04;
    private const byte AtoObject = 0x08;
    private const byte AtoSound = 0x10;
    private const byte AtoUnderObj = 0x20;
    private const byte AtoNearGroud = 0x40;

    private const uint OtObjectMask = 0x00FFFFFFu;
    private const uint OtTypeMask = 0x0F000000u;
    private const uint OtWatertide = 0x01000000u;

    private const ushort PackageSmTiles = 3001;
    private const ushort PackageTiles = 3051;
    private const ushort PackageEffect = 49;

    // Extended attribute flags (stored in the 2-byte wExAttr field, V6+)
    private const ushort ExatoOverObj = 0x0001;
    private const ushort ExatoColorAdjT = 0x0002;
    private const ushort ExatoColorAdjSt = 0x0004;
    private const ushort ExatoColorAdjObj = 0x0008;
    private const ushort ExatoColorAdjEff = 0x0010;
    private const ushort ExatoTileLink = 0x0020;
    private const ushort ExatoColorOverObj = 0x0080;
    private const ushort ExatoColorAdjFloor = 0x2000;
    private const ushort ExatoV12Extra = 0x4000;
    private const ushort ExatoNoPayloadMask = 0x0840;

    private static readonly HashSet<uint> SupportedVersions = new()
    {
        MmpLegacyVersion,
        1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12
    };

    public static bool TryReadMapInfo(string nmpPath, out NmpMapInfo info, out string error)
    {
        info = new NmpMapInfo();
        error = string.Empty;

        if (!FileIO.TryReadAllBytes(nmpPath, out byte[] bytes, out error))
        {
            return false;
        }

        return TryReadMapInfoFromMemory(bytes, nmpPath, out info, out error);
    }

    public static bool TryReadMapInfoFromMemory(
        ReadOnlySpan<byte> data,
        string label,
        out NmpMapInfo info,
        out string error)
    {
        info = new NmpMapInfo();
        error = string.Empty;

        if (!TryParseHeader(data, label, out uint headerSize, out uint version, out int width, out int height, out uint dataOffset, out error))
        {
            return false;
        }

        info = new NmpMapInfo
        {
            Path = label,
            HeaderSize = headerSize,
            Version = version,
            Width = width,
            Height = height,
            DataOffset = dataOffset,
        };
        return true;
    }

    public static bool TryReadMapFromMemory(
        ReadOnlySpan<byte> data,
        string label,
        out NmpMapInfo info,
        out NmpCellData[] cells,
        out string error,
        bool forceParallel = false)
    {
        info = new NmpMapInfo();
        cells = Array.Empty<NmpCellData>();
        error = string.Empty;

        if (!TryParseHeader(data, label, out uint headerSize, out uint version, out int width, out int height, out uint dataOffset, out error))
        {
            return false;
        }

        info = new NmpMapInfo
        {
            Path = label,
            HeaderSize = headerSize,
            Version = version,
            Width = width,
            Height = height,
            DataOffset = dataOffset,
        };

        int cellCount = info.CellCount;
        if (cellCount <= 0)
        {
            error = "Invalid NMP file: cellCount out of range";
            return false;
        }

        cells = new NmpCellData[cellCount];
        int startPos = checked((int)dataOffset);

        bool useParallel = forceParallel || cellCount >= ParallelThreshold;
        if (!useParallel)
        {
            int pos = startPos;
            for (int i = 0; i < cellCount; i++)
            {
                if (!TryParseCellByVersion(data, ref pos, ref cells[i], version))
                {
                    error = $"Unexpected end of NMP data at cell {i}";
                    cells = Array.Empty<NmpCellData>();
                    return false;
                }
            }
            return true;
        }

        if (!TryComputeCellOffsets(data, startPos, version, cellCount, out int[] offsets, out error))
        {
            cells = Array.Empty<NmpCellData>();
            return false;
        }

        ReadOnlyMemory<byte> parallelData = data.ToArray();
        NmpCellData[] cellArray = cells;

        int firstErrorIndex = -1;
        Parallel.For(0, cellCount, (i, state) =>
        {
            if (Volatile.Read(ref firstErrorIndex) != -1)
            {
                state.Stop();
                return;
            }

            ReadOnlySpan<byte> parallelSpan = parallelData.Span;
            int pos = offsets[i];
            NmpCellData cell = default;
            if (!TryParseCellByVersion(parallelSpan, ref pos, ref cell, version))
            {
                Interlocked.CompareExchange(ref firstErrorIndex, i, -1);
                state.Stop();
                return;
            }

            cellArray[i] = cell;
        });

        if (firstErrorIndex != -1)
        {
            error = $"Unexpected end of NMP data at cell {firstErrorIndex}";
            cells = Array.Empty<NmpCellData>();
            return false;
        }

        return true;
    }

    private static bool TryParseHeader(
        ReadOnlySpan<byte> data,
        string label,
        out uint headerSize,
        out uint version,
        out int width,
        out int height,
        out uint dataOffset,
        out string error)
    {
        headerSize = 0;
        version = 0;
        width = 0;
        height = 0;
        dataOffset = 0;
        error = string.Empty;

        // Legacy MMP header (little-endian):
        //   [0..1] u16 magic (=0x0201)
        //   [2..3] u16 height
        //   [4..5] u16 width
        // payload: width*height*u16
        if (data.Length >= MmpHeaderBytes)
        {
            ushort magic16 = ReadU16(data, 0);
            if (magic16 == MmpMagic)
            {
                height = ReadU16(data, 2);
                width = ReadU16(data, 4);
                version = MmpLegacyVersion;
                headerSize = MmpHeaderBytes;
                dataOffset = MmpHeaderBytes;

                if (width < 1 || height < 1 || width > 2000 || height > 2000)
                {
                    error = "Invalid MMP file: dimensions out of range";
                    return false;
                }

                long cellBytes = (long)width * height * 2;
                long expectedSize = MmpHeaderBytes + cellBytes;
                if (expectedSize > data.Length)
                {
                    error = "Invalid MMP file: truncated payload";
                    return false;
                }

                if (expectedSize != data.Length)
                {
                    int expected = (int)expectedSize;
                    if (!IsZeroFilledRange(data, expected, data.Length))
                    {
                        error = "Invalid MMP file: trailing bytes not zero";
                        return false;
                    }
                }

                return true;
            }
        }

        if (data.Length < 16)
        {
            error = $"Invalid NMP file: header too small: {label}";
            return false;
        }

        headerSize = ReadU32(data, 0);
        version = ReadU32(data, 4);
        width = ReadI32(data, 8);
        height = ReadI32(data, 12);
        uint extraOffset = data.Length >= 20 ? ReadU32(data, 16) : 0;

        if (headerSize < 16 || headerSize > (uint)data.Length)
        {
            error = "Invalid NMP file: header/data offset out of range";
            return false;
        }

        if (!SupportedVersions.Contains(version))
        {
            error = "Invalid NMP file: unsupported version";
            return false;
        }

        if (width < 1 || height < 1 || width > 2000 || height > 2000)
        {
            error = "Invalid NMP file: dimensions out of range";
            return false;
        }

        uint cellDataOffset = headerSize;
        if (data.Length >= 20 && extraOffset > cellDataOffset && extraOffset <= (uint)data.Length)
        {
            if (IsZeroFilledRange(data, 20, checked((int)extraOffset)))
            {
                cellDataOffset = extraOffset;
            }
        }

        if (cellDataOffset < 16 || cellDataOffset > (uint)data.Length)
        {
            error = "Invalid NMP file: cell-data offset out of range";
            return false;
        }

        ulong minPayload = (ulong)width * (ulong)height;
        if ((ulong)data.Length < (ulong)cellDataOffset + minPayload)
        {
            error = "Invalid NMP file: truncated payload";
            return false;
        }

        dataOffset = cellDataOffset;
        return true;
    }

    private static bool IsZeroFilledRange(ReadOnlySpan<byte> data, int begin, int endExclusive)
    {
        if (begin < 0) return false;
        if (endExclusive < begin) return false;
        if (endExclusive > data.Length) return false;
        for (int i = begin; i < endExclusive; i++)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseCellMmp(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell)
    {
        if (pos + 2 > data.Length)
        {
            return false;
        }

        ushort tile = ReadU16(data, pos);
        pos += 2;

        cell.Flags = tile != 0 ? AtoSmTiles : (byte)0;
        cell.BackImage = tile;
        cell.BackLibrary = tile != 0 ? PackageSmTiles : (ushort)0;
        return true;
    }

    private static bool TryParseCellV1(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell)
    {
        if (pos + 12 > data.Length)
        {
            return false;
        }

        ushort back = ReadU16(data, pos + 0);
        ushort middle = ReadU16(data, pos + 2);
        uint uObject = ReadU32(data, pos + 4);
        cell.Flags = data[pos + 8];
        cell.Sound = data[pos + 9];
        pos += 12;

        cell.BackImage = back;
        cell.BackLibrary = back != 0 ? PackageSmTiles : (ushort)0;
        cell.MiddleImage = middle;
        cell.MiddleLibrary = middle != 0 ? PackageTiles : (ushort)0;

        cell.FrontImage = uObject & OtObjectMask;
        cell.FrontLibrary = (byte)((cell.FrontImage >> 16) & 0xFF);
        if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
        {
            cell.FrontLibrary = 5;
        }

        return true;
    }

    private static bool TryParseCellV2(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell)
    {
        if (pos + 1 > data.Length)
        {
            return false;
        }

        byte cTemp = data[pos++];
        cell.Flags = cTemp;

        ushort back = 0;
        ushort middle = 0;
        uint uObject = 0;

        if ((cTemp & AtoSmTiles) != 0)
        {
            if (pos + 2 > data.Length) return false;
            back = ReadU16(data, pos);
            pos += 2;
        }

        if ((cTemp & AtoTiles) != 0)
        {
            if (pos + 2 > data.Length) return false;
            middle = ReadU16(data, pos);
            pos += 2;
        }

        if ((cTemp & AtoObject) != 0)
        {
            if (pos + 4 > data.Length) return false;
            uObject = ReadU32(data, pos);
            pos += 4;
        }

        cell.BackImage = back;
        cell.BackLibrary = back != 0 ? PackageSmTiles : (ushort)0;
        cell.MiddleImage = middle;
        cell.MiddleLibrary = middle != 0 ? PackageTiles : (ushort)0;

        cell.FrontImage = uObject & OtObjectMask;
        cell.FrontLibrary = (byte)((cell.FrontImage >> 16) & 0xFF);
        if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
        {
            cell.FrontLibrary = 5;
        }

        return true;
    }

    private static bool TryComputeCellOffsets(
        ReadOnlySpan<byte> data,
        int startPos,
        uint version,
        int cellCount,
        out int[] offsets,
        out string error)
    {
        offsets = Array.Empty<int>();
        error = string.Empty;

        if (startPos < 0 || startPos > data.Length)
        {
            error = "Invalid NMP file: cell-data offset out of range";
            return false;
        }

        offsets = new int[cellCount + 1];
        int pos = startPos;
        NmpCellData dummy = default;

        for (int i = 0; i < cellCount; i++)
        {
            offsets[i] = pos;
            dummy = default;
            if (!TryParseCellByVersion(data, ref pos, ref dummy, version))
            {
                error = $"Unexpected end of NMP data at cell {i}";
                offsets = Array.Empty<int>();
                return false;
            }
        }

        offsets[cellCount] = pos;
        return true;
    }

    private static bool TryParseCellByVersion(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell, uint version)
    {
        return version switch
        {
            MmpLegacyVersion => TryParseCellMmp(data, ref pos, ref cell),
            1 => TryParseCellV1(data, ref pos, ref cell),
            2 => TryParseCellV2(data, ref pos, ref cell),
            3 or 5 => TryParseCellV3V5(data, ref pos, ref cell, version),
            6 or 7 => TryParseCellV6V7(data, ref pos, ref cell, version),
            8 or 9 => TryParseCellV8V9(data, ref pos, ref cell, version),
            >= 10 and <= 12 => TryParseCellV10Plus(data, ref pos, ref cell, version),
            _ => false,
        };
    }

    private static bool TryParseCellV3V5(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell, uint version)
    {
        if (pos + 1 > data.Length)
        {
            return false;
        }

        byte cTemp = data[pos++];
        cell.Flags = cTemp;

        ushort back = 0;
        ushort middle = 0;
        uint uObject = 0;

        if ((cTemp & AtoSmTiles) != 0)
        {
            if (pos + 2 > data.Length) return false;
            back = ReadU16(data, pos);
            pos += 2;
        }

        if ((cTemp & AtoTiles) != 0)
        {
            if (pos + 2 > data.Length) return false;
            middle = ReadU16(data, pos);
            pos += 2;
        }

        if ((cTemp & AtoObject) != 0)
        {
            if (pos + 4 > data.Length) return false;
            uObject = ReadU32(data, pos);
            pos += 4;

            if (version >= 5)
            {
                if (pos + 2 > data.Length) return false;
                cell.ObjectHeight = ReadU16(data, pos);
                pos += 2;
            }
        }

        if ((cTemp & AtoSound) != 0)
        {
            if (pos + 1 > data.Length) return false;
            cell.Sound = data[pos++];
        }

        if (version >= 5 && (cTemp & AtoUnderObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.UnderObject = ReadU32(data, pos);
            pos += 4;
        }

        cell.BackImage = back;
        cell.BackLibrary = back != 0 ? PackageSmTiles : (ushort)0;
        cell.MiddleImage = middle;

        if (middle != 0)
        {
            cell.MiddleLibrary = (version >= 5 && (uObject & OtTypeMask) != 0) ? PackageEffect : PackageTiles;
        }
        else
        {
            cell.MiddleLibrary = 0;
        }

        if (version >= 5 && (uObject & OtTypeMask) == OtWatertide && cell.MiddleImage > 0)
        {
            uint coast = uObject & 0xFFu;
            uint mask = (uObject >> 8) & 0xFFu;
            if (coast > 0 && mask > 0)
            {
                cell.MiddleImage2 = cell.MiddleImage;
                cell.MiddleLibrary2 = cell.MiddleLibrary;
                cell.MiddleAlphaMask = (ushort)mask;
                cell.MiddleImage = (ushort)coast;
            }

            cell.FrontImage = 0;
            cell.FrontLibrary = 0;
            return true;
        }

        cell.FrontImage = uObject & OtObjectMask;
        cell.FrontLibrary = (byte)((cell.FrontImage >> 16) & 0xFF);
        if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
        {
            cell.FrontLibrary = 5;
        }

        return true;
    }

    private static bool TryParseCellV6V7(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell, uint version)
    {
        if (pos + 3 > data.Length)
        {
            return false;
        }

        byte cTemp = data[pos++];
        ushort wExAttr = ReadU16(data, pos);
        pos += 2;

        cell.Flags = cTemp;
        cell.ExtendedAttributes = wExAttr;

        uint dwSmTile = 0;
        uint dwTile = 0;
        uint uObject = 0;

        if (version == 6)
        {
            if ((cTemp & AtoSmTiles) != 0)
            {
                if (pos + 2 > data.Length) return false;
                dwSmTile = ReadU16(data, pos);
                pos += 2;
            }

            if ((cTemp & AtoTiles) != 0)
            {
                if (pos + 2 > data.Length) return false;
                dwTile = ReadU16(data, pos);
                pos += 2;
            }
        }
        else
        {
            if ((cTemp & AtoSmTiles) != 0)
            {
                if (pos + 4 > data.Length) return false;
                dwSmTile = ReadU32(data, pos);
                pos += 4;
            }

            if ((cTemp & AtoTiles) != 0)
            {
                if (pos + 4 > data.Length) return false;
                dwTile = ReadU32(data, pos);
                pos += 4;
            }
        }

        if ((cTemp & AtoObject) != 0)
        {
            if (pos + 6 > data.Length) return false;
            uObject = ReadU32(data, pos);
            pos += 4;
            cell.ObjectHeight = ReadU16(data, pos);
            pos += 2;
        }

        if (dwSmTile != 0 && (dwSmTile >> 16) == 0)
        {
            dwSmTile |= (uint)PackageSmTiles << 16;
        }

        if (dwTile != 0 && (dwTile >> 16) == 0)
        {
            dwTile |= (uint)(((uObject & OtTypeMask) != 0) ? PackageEffect : PackageTiles) << 16;
        }

        if ((cTemp & AtoSound) != 0)
        {
            if (pos + 1 > data.Length) return false;
            cell.Sound = data[pos++];
        }

        if ((cTemp & AtoUnderObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.UnderObject = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoOverObj) != 0)
        {
            if (pos + 2 > data.Length) return false;
            cell.OverObject = ReadU16(data, pos);
            pos += 2;
        }

        if ((wExAttr & ExatoColorAdjT) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjSt) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjSmTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjObject = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjEff) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjEffect = ReadU32(data, pos);
            pos += 4;
        }

        cell.BackImage = (ushort)(dwSmTile & 0xFFFF);
        cell.BackLibrary = ResolveGroundLibrary(dwSmTile, PackageSmTiles);
        cell.MiddleImage = (ushort)(dwTile & 0xFFFF);
        cell.MiddleLibrary = ResolveGroundLibrary(dwTile, PackageTiles);

        if ((uObject & OtTypeMask) == OtWatertide && cell.MiddleImage > 0)
        {
            uint coast = uObject & 0xFFu;
            uint mask = (uObject >> 8) & 0xFFu;
            if (coast > 0 && mask > 0)
            {
                cell.MiddleImage2 = cell.MiddleImage;
                cell.MiddleLibrary2 = cell.MiddleLibrary;
                cell.MiddleAlphaMask = (ushort)mask;
                cell.MiddleImage = (ushort)coast;
            }

            cell.FrontImage = 0;
            cell.FrontLibrary = 0;
            return true;
        }

        cell.FrontImage = uObject & OtObjectMask;
        cell.FrontLibrary = (byte)((cell.FrontImage >> 16) & 0xFF);
        if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
        {
            cell.FrontLibrary = 5;
        }

        return true;
    }

    private static ushort ResolveGroundLibrary(uint packedRef, ushort defaultLibrary)
    {
        ushort image = (ushort)(packedRef & 0xFFFF);
        if (image == 0)
        {
            return 0;
        }

        ushort library = (ushort)((packedRef >> 16) & 0xFFFF);
        return library != 0 ? library : defaultLibrary;
    }

    private static bool TryParseCellV8V9(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell, uint version)
    {
        if (pos + 3 > data.Length)
        {
            return false;
        }

        byte cTemp = data[pos++];
        ushort wExAttr = ReadU16(data, pos);
        pos += 2;

        cell.Flags = cTemp;
        cell.ExtendedAttributes = wExAttr;

        uint dwSmTile = 0;
        uint dwTile = 0;
        uint uObject = 0;

        if ((cTemp & AtoSmTiles) != 0)
        {
            if (pos + 4 > data.Length) return false;
            dwSmTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((cTemp & AtoTiles) != 0)
        {
            if (pos + 4 > data.Length) return false;
            dwTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((cTemp & AtoObject) != 0)
        {
            if (pos + 4 > data.Length) return false;
            uObject = ReadU32(data, pos);
            pos += 4;

            if (version == 8)
            {
                if (pos + 2 > data.Length) return false;
                cell.ObjectHeight = ReadU16(data, pos);
                pos += 2;
            }
        }

        if (dwSmTile != 0 && (dwSmTile >> 16) == 0)
        {
            dwSmTile |= (uint)PackageSmTiles << 16;
        }

        if (dwTile != 0 && (dwTile >> 16) == 0)
        {
            dwTile |= (uint)(((uObject & OtTypeMask) != 0) ? PackageEffect : PackageTiles) << 16;
        }

        if ((cTemp & AtoSound) != 0)
        {
            if (pos + 1 > data.Length) return false;
            cell.Sound = data[pos++];
        }

        if ((cTemp & AtoUnderObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.UnderObject = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoOverObj) != 0)
        {
            if (pos + 2 > data.Length) return false;
            cell.OverObject = ReadU16(data, pos);
            pos += 2;
        }

        // Stream order: T, ST, OB, FLOOR, EFF
        if ((wExAttr & ExatoColorAdjT) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjSt) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjSmTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjObject = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjFloor) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjFloor = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjEff) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjEffect = ReadU32(data, pos);
            pos += 4;
        }

        if ((cTemp & AtoNearGroud) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.NearGround = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoTileLink) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.Group = ReadU32(data, pos);
            pos += 4;
        }

        cell.BackImage = (ushort)(dwSmTile & 0xFFFF);
        cell.BackLibrary = ResolveGroundLibrary(dwSmTile, PackageSmTiles);
        cell.MiddleImage = (ushort)(dwTile & 0xFFFF);
        cell.MiddleLibrary = ResolveGroundLibrary(dwTile, PackageTiles);

        if ((uObject & OtTypeMask) == OtWatertide && cell.MiddleImage > 0)
        {
            uint coast = uObject & 0xFFu;
            uint mask = (uObject >> 8) & 0xFFu;
            if (coast > 0 && mask > 0)
            {
                cell.MiddleImage2 = cell.MiddleImage;
                cell.MiddleLibrary2 = cell.MiddleLibrary;
                cell.MiddleAlphaMask = (ushort)mask;
                cell.MiddleImage = (ushort)coast;
            }

            cell.FrontImage = 0;
            cell.FrontLibrary = 0;
            return true;
        }

        cell.FrontImage = uObject & OtObjectMask;
        cell.FrontLibrary = (byte)((cell.FrontImage >> 16) & 0xFF);
        if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
        {
            cell.FrontLibrary = 5;
        }

        return true;
    }

    private static bool TryParseCellV10Plus(ReadOnlySpan<byte> data, ref int pos, ref NmpCellData cell, uint version)
    {
        if (pos + 3 > data.Length)
        {
            return false;
        }

        byte cTemp = data[pos++];
        ushort wExAttr = ReadU16(data, pos);
        pos += 2;

        cell.Flags = cTemp;
        cell.ExtendedAttributes = wExAttr;

        uint dwSmTile = 0;
        uint dwTile = 0;
        uint uObject = 0;

        if ((cTemp & AtoSmTiles) != 0)
        {
            if (pos + 4 > data.Length) return false;
            dwSmTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((cTemp & AtoTiles) != 0)
        {
            if (pos + 4 > data.Length) return false;
            dwTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((cTemp & AtoObject) != 0)
        {
            if (pos + 4 > data.Length) return false;
            uObject = ReadU32(data, pos);
            pos += 4;
        }

        if (dwSmTile != 0 && (dwSmTile >> 16) == 0)
        {
            dwSmTile |= (uint)PackageSmTiles << 16;
        }

        if (dwTile != 0 && (dwTile >> 16) == 0)
        {
            dwTile |= (uint)(((uObject & OtTypeMask) != 0) ? PackageEffect : PackageTiles) << 16;
        }

        if ((cTemp & AtoSound) != 0)
        {
            if (pos + 1 > data.Length) return false;
            cell.Sound = data[pos++];
        }

        if ((cTemp & AtoNearGroud) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.NearGround = ReadU32(data, pos);
            pos += 4;
        }

        if ((cTemp & AtoUnderObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.UnderObject = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoOverObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.OverObject = ReadU32(data, pos);
            pos += 4;
        }

        // Stream order: T, ST, OB, FLOOR, EFF
        if ((wExAttr & ExatoColorAdjT) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjSt) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjSmTile = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjObject = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjFloor) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjFloor = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorAdjEff) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorAdjEffect = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoColorOverObj) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.ColorOverObj = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoTileLink) != 0)
        {
            if (pos + 4 > data.Length) return false;
            cell.Group = ReadU32(data, pos);
            pos += 4;
        }

        if ((wExAttr & ExatoV12Extra) != 0)
        {
            int extraSize = version == 11 ? 2 : 6;
            if (pos + extraSize > data.Length) return false;

            cell.ExtraAttrV12_0 = data[pos + 0];
            cell.ExtraAttrV12_1 = extraSize >= 2 ? data[pos + 1] : (byte)0;
            cell.ExtraAttrV12_2 = extraSize >= 3 ? data[pos + 2] : (byte)0;
            cell.ExtraAttrV12_3 = extraSize >= 4 ? data[pos + 3] : (byte)0;
            cell.ExtraAttrV12_4 = extraSize >= 5 ? data[pos + 4] : (byte)0;
            cell.ExtraAttrV12_5 = extraSize >= 6 ? data[pos + 5] : (byte)0;

            pos += extraSize;
        }

        cell.BackImage = (ushort)(dwSmTile & 0xFFFF);
        cell.BackLibrary = ResolveGroundLibrary(dwSmTile, PackageSmTiles);
        cell.MiddleImage = (ushort)(dwTile & 0xFFFF);
        cell.MiddleLibrary = ResolveGroundLibrary(dwTile, PackageTiles);

        if ((uObject & OtTypeMask) == OtWatertide && cell.MiddleImage > 0)
        {
            uint coast = uObject & 0xFFu;
            uint mask = (uObject >> 8) & 0xFFu;
            if (coast > 0 && mask > 0)
            {
                cell.MiddleImage2 = cell.MiddleImage;
                cell.MiddleLibrary2 = cell.MiddleLibrary;
                cell.MiddleAlphaMask = (ushort)mask;
                cell.MiddleImage = (ushort)coast;
            }

            cell.FrontImage = 0;
            cell.FrontLibrary = 0;
            return true;
        }

        cell.FrontImage = uObject & OtObjectMask;
        cell.FrontLibrary = (byte)((cell.FrontImage >> 16) & 0xFF);
        if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
        {
            cell.FrontLibrary = 5;
        }

        if (version != 12)
        {
            int fPkg = cell.FrontLibrary;
            int fIdx = (int)(cell.FrontImage & 0xFFFF);
            if (fPkg == 45 && fIdx is >= 2680 and <= 2840)
            {
                uint savedFrontImage = cell.FrontImage;

                uint underMasked = cell.UnderObject & OtObjectMask;
                cell.FrontImage = underMasked;
                cell.FrontLibrary = (byte)((underMasked >> 16) & 0xFF);
                if (cell.FrontLibrary == 0 && cell.FrontImage != 0)
                {
                    cell.FrontLibrary = 5;
                }

                cell.UnderObject = savedFrontImage;
            }
        }

        return true;
    }

    private static bool TryWriteMmpMapDataToMemory(NmpMapInfo info, IReadOnlyList<NmpCellData> cells, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (info.Width < 1 || info.Height < 1)
        {
            error = "Invalid MMP map dimensions";
            return false;
        }

        if (info.Width > ushort.MaxValue || info.Height > ushort.MaxValue)
        {
            error = "Invalid MMP map dimensions (exceeds u16 range)";
            return false;
        }

        long cellCountLong = (long)info.Width * info.Height;
        if (cellCountLong is <= 0 or > int.MaxValue)
        {
            error = "Invalid MMP map: cellCount out of range";
            return false;
        }

        int cellCount = (int)cellCountLong;
        if (cells is null)
        {
            cells = Array.Empty<NmpCellData>();
        }

        if (cells.Count != cellCount)
        {
            error = $"Cell count mismatch: expected {cellCount}, got {cells.Count}";
            return false;
        }

        long totalBytesLong = MmpHeaderBytes + cellCountLong * 2;
        if (totalBytesLong is <= 0 or > int.MaxValue)
        {
            error = "Invalid MMP map: output size out of range";
            return false;
        }

        int totalBytes = (int)totalBytesLong;
        bytes = new byte[totalBytes];

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), MmpMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), (ushort)info.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), (ushort)info.Width);

        int pos = MmpHeaderBytes;
        for (int i = 0; i < cellCount; i++)
        {
            ushort tile = cells[i].BackImage;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pos, 2), tile);
            pos += 2;
        }

        return true;
    }

    public static bool WriteNmpMapData(string nmpPath, NmpMapInfo info, IReadOnlyList<NmpCellData> cells, out string error)
    {
        error = string.Empty;
        if (!TryWriteMapDataToMemory(info, cells, out byte[] bytes, out error))
        {
            return false;
        }

        return FileIO.TryWriteAllBytes(nmpPath, bytes, out error);
    }

    public static bool TryWriteMapDataToMemory(NmpMapInfo info, IReadOnlyList<NmpCellData> cells, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (info is null)
        {
            error = "Invalid NMP map info: null";
            return false;
        }

        uint version = info.Version;
        if (version == MmpLegacyVersion)
        {
            return TryWriteMmpMapDataToMemory(info, cells, out bytes, out error);
        }
        if (!SupportedVersions.Contains(version))
        {
            error = $"Unsupported NMP version {version}";
            return false;
        }

        if (info.Width < 1 || info.Height < 1)
        {
            error = "Invalid NMP map dimensions";
            return false;
        }

        long cellCountLong = (long)info.Width * info.Height;
        if (cellCountLong is <= 0 or > int.MaxValue)
        {
            error = "Invalid NMP map: cellCount out of range";
            return false;
        }

        int cellCount = (int)cellCountLong;
        if (cells is null)
        {
            cells = Array.Empty<NmpCellData>();
        }

        if (cells.Count != cellCount)
        {
            error = $"Cell count mismatch: expected {cellCount}, got {cells.Count}";
            return false;
        }

        uint headerSize = info.HeaderSize >= 20 ? info.HeaderSize : 20u;
        if (headerSize > int.MaxValue)
        {
            error = "Invalid NMP map: headerSize out of range";
            return false;
        }

        int capacity;
        try
        {
            capacity = checked((int)headerSize + checked(cellCount * 64));
        }
        catch (OverflowException)
        {
            capacity = (int)headerSize;
        }

        var buf = new List<byte>(capacity);

        PushU32(buf, headerSize);
        PushU32(buf, version);
        PushI32(buf, info.Width);
        PushI32(buf, info.Height);
        PushU32(buf, headerSize);

        while (buf.Count < (int)headerSize)
        {
            buf.Add(0);
        }

        for (int i = 0; i < cellCount; i++)
        {
            NmpCellData cell = cells[i];
            bool isCoastCell = cell.MiddleImage2 != 0;

            uint backRef32 = ComposeGroundRef(cell.BackImage, cell.BackLibrary, PackageSmTiles);
            uint middleRef32 = isCoastCell
                ? ComposeGroundRef(cell.MiddleImage2, cell.MiddleLibrary2, PackageTiles)
                : ComposeGroundRef(cell.MiddleImage, cell.MiddleLibrary, PackageTiles);

            uint uObject;
            if (isCoastCell)
            {
                uObject = OtWatertide
                    | (uint)((cell.MiddleAlphaMask & 0xFF) << 8)
                    | (uint)(cell.MiddleImage & 0xFF);
            }
            else
            {
                uObject = cell.FrontImage & OtObjectMask;
            }

            if (version == 1)
            {
                PushU16(buf, cell.BackImage);
                PushU16(buf, cell.MiddleImage);
                PushU32(buf, uObject);
                buf.Add(cell.Flags);
                buf.Add(cell.Sound);
                buf.Add(0);
                buf.Add(0);
                continue;
            }

            if (version == 2)
            {
                byte cTemp = (byte)(cell.Flags & unchecked((byte)~(AtoSmTiles | AtoTiles | AtoObject)));
                if (cell.BackImage != 0 || (cell.Flags & AtoSmTiles) != 0) cTemp |= AtoSmTiles;
                if (cell.MiddleImage != 0 || (cell.Flags & AtoTiles) != 0) cTemp |= AtoTiles;
                if (cell.FrontImage != 0 || (cell.Flags & AtoObject) != 0) cTemp |= AtoObject;

                buf.Add(cTemp);
                if ((cTemp & AtoSmTiles) != 0) PushU16(buf, cell.BackImage);
                if ((cTemp & AtoTiles) != 0) PushU16(buf, cell.MiddleImage);
                if ((cTemp & AtoObject) != 0) PushU32(buf, uObject);
                continue;
            }

            if (version == 3 || version == 5)
            {
                byte cTemp = (byte)(cell.Flags & 0x01);
                if (cell.BackImage != 0 || (cell.Flags & AtoSmTiles) != 0) cTemp |= AtoSmTiles;

                ushort writeMiddle = isCoastCell ? cell.MiddleImage2 : cell.MiddleImage;
                if (writeMiddle != 0 || (cell.Flags & AtoTiles) != 0) cTemp |= AtoTiles;
                if (cell.FrontImage != 0 || isCoastCell || (cell.Flags & AtoObject) != 0) cTemp |= AtoObject;
                if (cell.Sound != 0 || (cell.Flags & AtoSound) != 0) cTemp |= AtoSound;
                if (version >= 5 && (cell.UnderObject != 0 || (cell.Flags & AtoUnderObj) != 0)) cTemp |= AtoUnderObj;
                cTemp |= (byte)(cell.Flags & 0xC0);

                buf.Add(cTemp);
                if ((cTemp & AtoSmTiles) != 0) PushU16(buf, cell.BackImage);
                if ((cTemp & AtoTiles) != 0) PushU16(buf, writeMiddle);
                if ((cTemp & AtoObject) != 0)
                {
                    PushU32(buf, uObject);
                    if (version >= 5) PushU16(buf, cell.ObjectHeight);
                }
                if ((cTemp & AtoSound) != 0) buf.Add(cell.Sound);
                if (version >= 5 && (cTemp & AtoUnderObj) != 0) PushU32(buf, cell.UnderObject);
                continue;
            }

            if (version == 6 || version == 7)
            {
                byte cTemp = (byte)(cell.Flags & 0x01);
                if (cell.BackImage != 0 || (cell.Flags & AtoSmTiles) != 0) cTemp |= AtoSmTiles;

                ushort writeMiddle = isCoastCell ? cell.MiddleImage2 : cell.MiddleImage;
                if (writeMiddle != 0 || (cell.Flags & AtoTiles) != 0) cTemp |= AtoTiles;

                if (cell.FrontImage != 0 || isCoastCell || (cell.Flags & AtoObject) != 0) cTemp |= AtoObject;
                if (cell.Sound != 0 || (cell.Flags & AtoSound) != 0) cTemp |= AtoSound;
                if (cell.UnderObject != 0 || (cell.Flags & AtoUnderObj) != 0) cTemp |= AtoUnderObj;
                cTemp |= (byte)(cell.Flags & 0xC0);

                ushort wExAttr = (ushort)(cell.ExtendedAttributes & ExatoNoPayloadMask);
                if (cell.OverObject != 0 || (cell.ExtendedAttributes & ExatoOverObj) != 0) wExAttr |= ExatoOverObj;
                if (cell.ColorAdjTile != 0 || (cell.ExtendedAttributes & ExatoColorAdjT) != 0) wExAttr |= ExatoColorAdjT;
                if (cell.ColorAdjSmTile != 0 || (cell.ExtendedAttributes & ExatoColorAdjSt) != 0) wExAttr |= ExatoColorAdjSt;
                if (cell.ColorAdjObject != 0 || (cell.ExtendedAttributes & ExatoColorAdjObj) != 0) wExAttr |= ExatoColorAdjObj;
                if (cell.ColorAdjEffect != 0 || (cell.ExtendedAttributes & ExatoColorAdjEff) != 0) wExAttr |= ExatoColorAdjEff;

                buf.Add(cTemp);
                PushU16(buf, wExAttr);

                if (version == 6)
                {
                    if ((cTemp & AtoSmTiles) != 0) PushU16(buf, cell.BackImage);
                    if ((cTemp & AtoTiles) != 0) PushU16(buf, writeMiddle);
                }
                else
                {
                    if ((cTemp & AtoSmTiles) != 0) PushU32(buf, backRef32);
                    if ((cTemp & AtoTiles) != 0) PushU32(buf, middleRef32);
                }

                if ((cTemp & AtoObject) != 0)
                {
                    PushU32(buf, uObject);
                    PushU16(buf, cell.ObjectHeight);
                }
                if ((cTemp & AtoSound) != 0) buf.Add(cell.Sound);
                if ((cTemp & AtoUnderObj) != 0) PushU32(buf, cell.UnderObject);
                if ((wExAttr & ExatoOverObj) != 0) PushU16(buf, (ushort)(cell.OverObject & 0xFFFF));
                if ((wExAttr & ExatoColorAdjT) != 0) PushU32(buf, cell.ColorAdjTile);
                if ((wExAttr & ExatoColorAdjSt) != 0) PushU32(buf, cell.ColorAdjSmTile);
                if ((wExAttr & ExatoColorAdjObj) != 0) PushU32(buf, cell.ColorAdjObject);
                if ((wExAttr & ExatoColorAdjEff) != 0) PushU32(buf, cell.ColorAdjEffect);
                continue;
            }

            if (version == 8 || version == 9)
            {
                byte cTemp = (byte)(cell.Flags & 0x01);
                if (cell.BackImage != 0 || (cell.Flags & AtoSmTiles) != 0) cTemp |= AtoSmTiles;
                if (cell.MiddleImage != 0 || (cell.Flags & AtoTiles) != 0) cTemp |= AtoTiles;
                if (cell.FrontImage != 0 || isCoastCell || (cell.Flags & AtoObject) != 0) cTemp |= AtoObject;
                if (cell.Sound != 0 || (cell.Flags & AtoSound) != 0) cTemp |= AtoSound;
                if (cell.UnderObject != 0 || (cell.Flags & AtoUnderObj) != 0) cTemp |= AtoUnderObj;
                if (cell.NearGround != 0 || (cell.Flags & AtoNearGroud) != 0) cTemp |= AtoNearGroud;
                cTemp |= (byte)(cell.Flags & 0x80);

                ushort wExAttr = (ushort)(cell.ExtendedAttributes & ExatoNoPayloadMask);
                if (cell.OverObject != 0 || (cell.ExtendedAttributes & ExatoOverObj) != 0) wExAttr |= ExatoOverObj;
                if (cell.ColorAdjTile != 0 || (cell.ExtendedAttributes & ExatoColorAdjT) != 0) wExAttr |= ExatoColorAdjT;
                if (cell.ColorAdjSmTile != 0 || (cell.ExtendedAttributes & ExatoColorAdjSt) != 0) wExAttr |= ExatoColorAdjSt;
                if (cell.ColorAdjObject != 0 || (cell.ExtendedAttributes & ExatoColorAdjObj) != 0) wExAttr |= ExatoColorAdjObj;
                if (cell.ColorAdjEffect != 0 || (cell.ExtendedAttributes & ExatoColorAdjEff) != 0) wExAttr |= ExatoColorAdjEff;
                if (cell.Group != 0 || (cell.ExtendedAttributes & ExatoTileLink) != 0) wExAttr |= ExatoTileLink;
                if (cell.ColorAdjFloor != 0 || (cell.ExtendedAttributes & ExatoColorAdjFloor) != 0) wExAttr |= ExatoColorAdjFloor;

                buf.Add(cTemp);
                PushU16(buf, wExAttr);

                if ((cTemp & AtoSmTiles) != 0) PushU32(buf, backRef32);
                if ((cTemp & AtoTiles) != 0) PushU32(buf, middleRef32);
                if ((cTemp & AtoObject) != 0)
                {
                    PushU32(buf, uObject);
                    if (version == 8) PushU16(buf, cell.ObjectHeight);
                }
                if ((cTemp & AtoSound) != 0) buf.Add(cell.Sound);
                if ((cTemp & AtoUnderObj) != 0) PushU32(buf, cell.UnderObject);
                if ((wExAttr & ExatoOverObj) != 0) PushU16(buf, (ushort)(cell.OverObject & 0xFFFF));
                if ((wExAttr & ExatoColorAdjT) != 0) PushU32(buf, cell.ColorAdjTile);
                if ((wExAttr & ExatoColorAdjSt) != 0) PushU32(buf, cell.ColorAdjSmTile);
                if ((wExAttr & ExatoColorAdjObj) != 0) PushU32(buf, cell.ColorAdjObject);
                if ((wExAttr & ExatoColorAdjFloor) != 0) PushU32(buf, cell.ColorAdjFloor);
                if ((wExAttr & ExatoColorAdjEff) != 0) PushU32(buf, cell.ColorAdjEffect);
                if ((cTemp & AtoNearGroud) != 0) PushU32(buf, cell.NearGround);
                if ((wExAttr & ExatoTileLink) != 0) PushU32(buf, cell.Group);
                continue;
            }

            if (version >= 10)
            {
                byte cTemp = (byte)(cell.Flags & 0x01);
                if (cell.BackImage != 0 || (cell.Flags & AtoSmTiles) != 0) cTemp |= AtoSmTiles;

                ushort tileForFlags = isCoastCell ? cell.MiddleImage2 : cell.MiddleImage;
                if (tileForFlags != 0 || (cell.Flags & AtoTiles) != 0) cTemp |= AtoTiles;

                if (cell.FrontImage != 0 || isCoastCell || (cell.Flags & AtoObject) != 0) cTemp |= AtoObject;
                if (cell.Sound != 0 || (cell.Flags & AtoSound) != 0) cTemp |= AtoSound;
                if (cell.NearGround != 0 || (cell.Flags & AtoNearGroud) != 0) cTemp |= AtoNearGroud;
                if (cell.UnderObject != 0 || (cell.Flags & AtoUnderObj) != 0) cTemp |= AtoUnderObj;
                cTemp |= (byte)(cell.Flags & 0x80);

                ushort wExAttr = (ushort)(cell.ExtendedAttributes & ExatoNoPayloadMask);
                if (cell.OverObject != 0 || (cell.ExtendedAttributes & ExatoOverObj) != 0) wExAttr |= ExatoOverObj;
                if (cell.ColorAdjTile != 0 || (cell.ExtendedAttributes & ExatoColorAdjT) != 0) wExAttr |= ExatoColorAdjT;
                if (cell.ColorAdjSmTile != 0 || (cell.ExtendedAttributes & ExatoColorAdjSt) != 0) wExAttr |= ExatoColorAdjSt;
                if (cell.ColorAdjObject != 0 || (cell.ExtendedAttributes & ExatoColorAdjObj) != 0) wExAttr |= ExatoColorAdjObj;
                if (cell.ColorAdjEffect != 0 || (cell.ExtendedAttributes & ExatoColorAdjEff) != 0) wExAttr |= ExatoColorAdjEff;
                if (cell.Group != 0 || (cell.ExtendedAttributes & ExatoTileLink) != 0) wExAttr |= ExatoTileLink;
                if (cell.ColorOverObj != 0 || (cell.ExtendedAttributes & ExatoColorOverObj) != 0) wExAttr |= ExatoColorOverObj;
                if (cell.ColorAdjFloor != 0 || (cell.ExtendedAttributes & ExatoColorAdjFloor) != 0) wExAttr |= ExatoColorAdjFloor;

                if (version == 11)
                {
                    if (HasAnyExtraAttrBytes(cell, 2) || (cell.ExtendedAttributes & ExatoV12Extra) != 0) wExAttr |= ExatoV12Extra;
                }
                else if (version == 12)
                {
                    if (HasAnyExtraAttrBytes(cell, 6) || (cell.ExtendedAttributes & ExatoV12Extra) != 0) wExAttr |= ExatoV12Extra;
                }

                buf.Add(cTemp);
                PushU16(buf, wExAttr);

                if ((cTemp & AtoSmTiles) != 0) PushU32(buf, backRef32);
                if ((cTemp & AtoTiles) != 0) PushU32(buf, middleRef32);
                if ((cTemp & AtoObject) != 0) PushU32(buf, uObject);
                if ((cTemp & AtoSound) != 0) buf.Add(cell.Sound);
                if ((cTemp & AtoNearGroud) != 0) PushU32(buf, cell.NearGround);
                if ((cTemp & AtoUnderObj) != 0) PushU32(buf, cell.UnderObject);
                if ((wExAttr & ExatoOverObj) != 0) PushU32(buf, cell.OverObject);
                if ((wExAttr & ExatoColorAdjT) != 0) PushU32(buf, cell.ColorAdjTile);
                if ((wExAttr & ExatoColorAdjSt) != 0) PushU32(buf, cell.ColorAdjSmTile);
                if ((wExAttr & ExatoColorAdjObj) != 0) PushU32(buf, cell.ColorAdjObject);
                if ((wExAttr & ExatoColorAdjFloor) != 0) PushU32(buf, cell.ColorAdjFloor);
                if ((wExAttr & ExatoColorAdjEff) != 0) PushU32(buf, cell.ColorAdjEffect);
                if ((wExAttr & ExatoColorOverObj) != 0) PushU32(buf, cell.ColorOverObj);
                if ((wExAttr & ExatoTileLink) != 0) PushU32(buf, cell.Group);
                if ((wExAttr & ExatoV12Extra) != 0)
                {
                    int extraCount = version == 11 ? 2 : 6;
                    buf.Add(cell.ExtraAttrV12_0);
                    if (extraCount >= 2) buf.Add(cell.ExtraAttrV12_1);
                    if (extraCount >= 3) buf.Add(cell.ExtraAttrV12_2);
                    if (extraCount >= 4) buf.Add(cell.ExtraAttrV12_3);
                    if (extraCount >= 5) buf.Add(cell.ExtraAttrV12_4);
                    if (extraCount >= 6) buf.Add(cell.ExtraAttrV12_5);
                }
                continue;
            }

            error = $"Unsupported NMP version {version}";
            return false;
        }

        bytes = buf.ToArray();
        return true;
    }

    private static uint ComposeGroundRef(ushort image, ushort library, ushort defaultLibrary)
    {
        if (image == 0)
        {
            return 0;
        }

        ushort lib = library != 0 ? library : defaultLibrary;
        return ((uint)lib << 16) | image;
    }

    private static bool HasAnyExtraAttrBytes(NmpCellData cell, int count)
    {
        if (count >= 1 && cell.ExtraAttrV12_0 != 0) return true;
        if (count >= 2 && cell.ExtraAttrV12_1 != 0) return true;
        if (count >= 3 && cell.ExtraAttrV12_2 != 0) return true;
        if (count >= 4 && cell.ExtraAttrV12_3 != 0) return true;
        if (count >= 5 && cell.ExtraAttrV12_4 != 0) return true;
        if (count >= 6 && cell.ExtraAttrV12_5 != 0) return true;
        return false;
    }

    private static void PushU32(List<byte> buf, uint value)
    {
        buf.Add((byte)(value & 0xFF));
        buf.Add((byte)((value >> 8) & 0xFF));
        buf.Add((byte)((value >> 16) & 0xFF));
        buf.Add((byte)((value >> 24) & 0xFF));
    }

    private static void PushI32(List<byte> buf, int value)
    {
        PushU32(buf, unchecked((uint)value));
    }

    private static void PushU16(List<byte> buf, ushort value)
    {
        buf.Add((byte)(value & 0xFF));
        buf.Add((byte)((value >> 8) & 0xFF));
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    private static int ReadI32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }
}
