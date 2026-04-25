using System;
using System.Buffers.Binary;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Dxt;
using WoOOLToOOLsSharp.Shared.Formats.Xet;

namespace WoOOLToOOLsSharp.Shared.Formats.Tex;

public static class TexCodec
{
    private const uint Tex1Magic = 0x54455831u; // "1XET" (LE)

    private const byte TexRle = 0x01;
    private const byte TexS3Tc = 0x04;
    private const byte TexS3TcAlpha = 0x08;

    private const int WrapperMagicScanBytes = 256;

    private const int MaxDecodedImageBytes = 256 * 1024 * 1024;
    private const int MaxDecodedBlockBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 从 TEX 数据中读取“动画帧数”。这是迁移期优先实现的能力（用于 SGL/WPF 预览与缓存策略），不依赖完整解码。
    /// </summary>
    public static int GetFrameCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return 1;
        }

        uint magic = ReadU32(data, 0);

        if (IsTexNewFormatMagic(magic))
        {
            uint childOff = data.Length >= 8 ? ReadU32(data, 4) : 0;
            uint xorKey = GetTex1XorKey(magic);
            if (xorKey != 0)
            {
                childOff ^= xorKey;
            }
            if (childOff > 20)
            {
                childOff = 20;
            }

            Span<byte> hdrBuf = stackalloc byte[20];
            hdrBuf.Clear();

            int available = data.Length > 8 ? data.Length - 8 : 0;
            int hdrLen = Math.Min(20, Math.Min((int)childOff, available));
            if (hdrLen > 0)
            {
                data.Slice(8, hdrLen).CopyTo(hdrBuf);
            }

            uint frameSize = ReadU32(hdrBuf, 0);
            uint blockSize = ReadU32(hdrBuf, 4);
            uint frames = ReadU32(hdrBuf, 16);

            if (xorKey != 0)
            {
                frames ^= xorKey;
                blockSize ^= xorKey;
            }

            if (frameSize == 0) frameSize = 20;
            if (blockSize == 0) blockSize = 12;

            int frameCount = ClampTexFrameCount((int)frames);
            if (frameCount > 1)
            {
                frameCount = CountStructurallyValidTex1Frames(data, childOff, frameSize, blockSize, frameCount);
            }

            return frameCount;
        }

        int oldCount = ClampTexFrameCount((int)((magic >> 16) & 0xFFFFu));
        if (oldCount > 1 && data.Length >= 10)
        {
            byte option = (byte)((magic >> 8) & 0xFFu);
            oldCount = CountStructurallyValidOldTexFrames(data, oldCount, option);
        }

        return oldCount;
    }

    /// <summary>
    /// 解码 TEX（当前优先支持旧 TEX 格式）并输出 RGBA8。
    /// </summary>
    public static bool TryDecodeRgba8(ReadOnlySpan<byte> data, out DecodedImage image, out string error, int frame = 0)
    {
        return TryDecodeRgba8Impl(data, out image, out error, frame, tryDecompress: true);
    }

    private static bool TryDecodeRgba8Impl(
        ReadOnlySpan<byte> data,
        out DecodedImage image,
        out string error,
        int frame,
        bool tryDecompress)
    {
        image = new DecodedImage();
        error = string.Empty;

        if (data.Length < 8)
        {
            error = "TEX data too small";
            return false;
        }

        uint magic = ReadU32(data, 0);
        bool isNewFormat = IsTexNewFormatMagic(magic);

        // 旧 TEX 的快速可疑性判断：byte0 应该是 0~6（旧工程用这个启发式来决定是否直接按 old TEX 解析）
        bool looksLikeOldTex = data[0] <= 6;

        if (isNewFormat)
        {
            if (TryDecodeTex1(data, frame, out image, out error))
            {
                return true;
            }
        }
        else if (looksLikeOldTex)
        {
            if (TryDecodeOldTex(data, frame, out image, out error))
            {
                return true;
            }
        }

        string bestError = error;

        // zlib / chunked-zlib 包裹：一些资源会把压缩后的 payload 直接塞入容器（flag 未标记）。
        if (tryDecompress && IsChunkedZlib(data))
        {
            if (!ZlibUtils.TryDecompressChunked(data, out byte[] inflated, out string zErr))
            {
                if (string.IsNullOrWhiteSpace(bestError))
                {
                    bestError = zErr;
                }
            }
            else if (TryDecodeRgba8Impl(inflated, out image, out error, frame, tryDecompress: false))
            {
                return true;
            }
            else if (string.IsNullOrWhiteSpace(bestError) && !string.IsNullOrWhiteSpace(error))
            {
                bestError = error;
            }
        }

        if (tryDecompress && IsZlibHeader(data))
        {
            if (!ZlibUtils.TryDecompress(data, out byte[] inflated, out string zErr))
            {
                if (string.IsNullOrWhiteSpace(bestError))
                {
                    bestError = zErr;
                }
            }
            else if (TryDecodeRgba8Impl(inflated, out image, out error, frame, tryDecompress: false))
            {
                return true;
            }
            else if (string.IsNullOrWhiteSpace(bestError) && !string.IsNullOrWhiteSpace(error))
            {
                bestError = error;
            }
        }

        // 数据不像 old TEX 时，再尝试一次 old TEX（旧工程的容错分支：至少能拿到更具体的错误；少量情况下也能直接成功）。
        if (!isNewFormat && !looksLikeOldTex)
        {
            if (TryDecodeOldTex(data, frame, out image, out string oldErr))
            {
                error = oldErr;
                return true;
            }

            if (string.IsNullOrWhiteSpace(bestError) && !string.IsNullOrWhiteSpace(oldErr))
            {
                bestError = oldErr;
            }
        }

        // 扫描前 256 bytes：兼容未知 wrapper（旧工程逻辑：在任意偏移处尝试 TEX1/old TEX）。
        int scanLimit = Math.Min(data.Length, WrapperMagicScanBytes);
        for (int skip = 1; skip + 8 <= scanLimit; skip++)
        {
            uint magicN = ReadU32(data, skip);
            bool hasNewTex = IsTexNewFormatMagic(magicN);
            bool hasOldTex = data[skip] <= 6;

            if (!hasNewTex && !hasOldTex)
            {
                continue;
            }

            if (TryDecodeRgba8Impl(data.Slice(skip), out image, out string subErr, frame, tryDecompress: false))
            {
                error = subErr;
                return true;
            }

            if (string.IsNullOrWhiteSpace(bestError) && !string.IsNullOrWhiteSpace(subErr))
            {
                bestError = subErr;
            }
        }

        error = string.IsNullOrWhiteSpace(bestError) ? DescribeUnknownTexHeader(data) : bestError;
        return false;
    }

    private static uint GetTex1XorKey(uint magic)
    {
        uint key = magic - Tex1Magic;
        return (key is >= 1 and <= 10000 && (magic & 0xFFFF0000u) == 0x54450000u)
            ? key
            : 0u;
    }

    private static bool IsTexNewFormatMagic(uint magic)
    {
        return magic == Tex1Magic || GetTex1XorKey(magic) != 0;
    }

    private static int ClampTexFrameCount(int frameCount)
    {
        return (frameCount <= 0 || frameCount > 256) ? 1 : frameCount;
    }

    private static int CountStructurallyValidTex1Frames(
        ReadOnlySpan<byte> data,
        uint childOff,
        uint frameSize,
        uint blockSize,
        int declaredFrames)
    {
        if (data.Length < 8 || declaredFrames <= 0) return 1;
        if (childOff > 20 || 8u + childOff > (uint)data.Length) return 1;
        if (frameSize < 20 || frameSize > 1024 || blockSize < 12 || blockSize > 1024) return 1;

        ulong offset = 8u + childOff;
        int validFrames = 0;

        for (int frame = 0; frame < declaredFrames; frame++)
        {
            if (offset + frameSize > (ulong)data.Length) break;

            ushort xBlocks = ReadU16(data, (int)(offset + 16u));
            ushort yBlocks = ReadU16(data, (int)(offset + 18u));
            if (xBlocks == 0 || yBlocks == 0 || xBlocks > 256 || yBlocks > 256) break;

            uint blockCount = (uint)xBlocks * (uint)yBlocks;
            if (blockCount == 0 || blockCount > 4096) break;

            offset += frameSize;

            bool frameValid = true;
            for (uint block = 0; block < blockCount; block++)
            {
                if (offset + blockSize > (ulong)data.Length)
                {
                    frameValid = false;
                    break;
                }

                uint payloadSize = ReadU32(data, (int)(offset + 8u));
                if (payloadSize == 0 || offset + blockSize + payloadSize > (ulong)data.Length)
                {
                    frameValid = false;
                    break;
                }

                offset += blockSize + payloadSize;
            }

            if (!frameValid) break;
            validFrames++;
        }

        return validFrames > 0 ? validFrames : 1;
    }

    private enum Tex1PixelType : byte
    {
        A8R8G8B8 = 0,
        A4R4G4B4 = 1,
        A0R5G6B5 = 2,
        Dxt1 = 4,
        Dxt3 = 5,
        Dxt5 = 6,
    }

    private struct Tex1FrameCandidate
    {
        public int Offset;
        public ushort Width0;
        public ushort Height0;
        public ushort Width;
        public ushort Height;
        public short OffsetX;
        public short OffsetY;
        public short CenterX;
        public short CenterY;
        public ushort XBlocks;
        public ushort YBlocks;
    }

    private static bool IsLikelyDimension(ushort x)
    {
        if (x < 8 || x > 4096) return false;

        if ((x & (x - 1)) == 0) return true;
        if (x >= 32 && (x % 8) == 0) return true;

        ushort[] common = { 64, 128, 256, 512, 1024, 2048 };
        foreach (ushort c in common)
        {
            if (x >= (ushort)(c * 4 / 5) && x <= (ushort)(c * 6 / 5)) return true;
        }

        return false;
    }

    private static bool TryReadTex1FrameCandidate(ReadOnlySpan<byte> data, int offset, out Tex1FrameCandidate candidate)
    {
        candidate = default;

        const int frameHeaderSize = 20;
        const int blockHeaderSize = 12;
        if (offset < 0) return false;
        if ((uint)offset + (uint)frameHeaderSize + (uint)blockHeaderSize > (uint)data.Length) return false;

        Tex1FrameCandidate c = new Tex1FrameCandidate
        {
            Offset = offset,
            Width0 = ReadU16(data, offset + 0),
            Height0 = ReadU16(data, offset + 2),
            Width = ReadU16(data, offset + 4),
            Height = ReadU16(data, offset + 6),
            OffsetX = ReadI16(data, offset + 8),
            OffsetY = ReadI16(data, offset + 10),
            CenterX = ReadI16(data, offset + 12),
            CenterY = ReadI16(data, offset + 14),
            XBlocks = ReadU16(data, offset + 16),
            YBlocks = ReadU16(data, offset + 18),
        };

        ushort effectiveW = IsLikelyDimension(c.Width) ? c.Width : c.Width0;
        ushort effectiveH = IsLikelyDimension(c.Height) ? c.Height : c.Height0;
        if (!IsLikelyDimension(effectiveW) || !IsLikelyDimension(effectiveH)) return false;
        if (c.XBlocks == 0 || c.YBlocks == 0 || c.XBlocks > 256 || c.YBlocks > 256) return false;
        if ((uint)c.XBlocks * c.YBlocks > 4096) return false;

        int blockOff = offset + frameHeaderSize;
        byte compr = data[blockOff + 0];
        byte pixelType = data[blockOff + 1];
        ushort blockW = ReadU16(data, blockOff + 4);
        ushort blockH = ReadU16(data, blockOff + 6);
        uint blockBytes = ReadU32(data, blockOff + 8);

        if (pixelType is not ((byte)Tex1PixelType.A8R8G8B8)
            and not ((byte)Tex1PixelType.A4R4G4B4)
            and not ((byte)Tex1PixelType.A0R5G6B5)
            and not ((byte)Tex1PixelType.Dxt1)
            and not ((byte)Tex1PixelType.Dxt3)
            and not ((byte)Tex1PixelType.Dxt5))
        {
            return false;
        }

        if (!IsLikelyDimension(blockW) || !IsLikelyDimension(blockH)) return false;
        if (blockW > effectiveW || blockH > effectiveH) return false;
        if (blockBytes == 0 || (uint)blockOff + (uint)blockHeaderSize + blockBytes > (uint)data.Length) return false;

        ReadOnlySpan<byte> payload = data.Slice(blockOff + blockHeaderSize, (int)blockBytes);
        bool payloadLooksCompressed =
            IsZlibHeader(payload)
            || (payload.Length >= 4 && XetCodec.IsXetCompressed(payload));

        if (compr != 0 && !payloadLooksCompressed) return false;

        candidate = c;
        return true;
    }

    private static bool FindVariantTex1Frame(ReadOnlySpan<byte> data, out Tex1FrameCandidate candidate)
    {
        candidate = default;

        int[] preferredOffsets = { 26, 28, 24, 30, 32, 34, 36, 40 };
        foreach (int off in preferredOffsets)
        {
            if (TryReadTex1FrameCandidate(data, off, out candidate)) return true;
        }

        int limit = Math.Min(data.Length, 128);
        for (int off = 20; off + 32 <= limit; off += 2)
        {
            if (TryReadTex1FrameCandidate(data, off, out candidate)) return true;
        }

        return false;
    }

    private static bool TryDecodeTex1(ReadOnlySpan<byte> data, int frame, out DecodedImage image, out string error)
    {
        image = new DecodedImage();
        error = string.Empty;

        if (data.Length < 8)
        {
            error = "TEX1 truncated";
            return false;
        }

        uint magic = ReadU32(data, 0);
        uint xorKey = GetTex1XorKey(magic);
        bool doXor = xorKey != 0;

        uint childOff = ReadU32(data, 4);
        if (doXor) childOff ^= xorKey;
        if (childOff > 20) childOff = 20;

        if (childOff > (uint)data.Length || 8u + childOff > (uint)data.Length)
        {
            error = $"TEX1 childOff out of bounds (childOff={childOff}, size={data.Length})";
            return false;
        }

        Span<byte> hdrBuf = stackalloc byte[20];
        hdrBuf.Clear();

        int available = Math.Max(0, data.Length - 8);
        int hdrLen = Math.Min(20, Math.Min((int)childOff, available));
        if (hdrLen > 0)
        {
            data.Slice(8, hdrLen).CopyTo(hdrBuf);
        }

        uint frameSize = ReadU32(hdrBuf, 0);
        uint blockSize = ReadU32(hdrBuf, 4);
        uint option = ReadU32(hdrBuf, 8);
        uint speed = ReadU32(hdrBuf, 12);
        uint frames = ReadU32(hdrBuf, 16);

        if (doXor)
        {
            blockSize ^= xorKey;
            option ^= xorKey;
            speed ^= xorKey;
            frames ^= xorKey;
        }

        _ = option;
        _ = speed;

        if (frameSize == 0) frameSize = 20;
        if (blockSize == 0) blockSize = 12;
        if (frames == 0 || frames > 10000)
        {
            if (!doXor)
            {
                frames = ReadU32(data, 20);
            }

            if (frames == 0 || frames > 10000)
            {
                frames = 1;
            }
        }

        bool standardHeaderLooksSane = frameSize is >= 20 and <= 1024
                                       && blockSize is >= 12 and <= 1024;

        if (frame < 0 || frame >= (int)frames) frame = 0;

        ulong offset = 8u + childOff;
        bool usingVariantFrame = false;

        Tex1FrameCandidate variantFrame = default;
        bool offsetLooksLikeFrame = offset <= int.MaxValue && TryReadTex1FrameCandidate(data, (int)offset, out variantFrame);
        if ((!standardHeaderLooksSane || !offsetLooksLikeFrame) && FindVariantTex1Frame(data, out variantFrame))
        {
            offset = (ulong)variantFrame.Offset;
            frameSize = 20;
            blockSize = 12;
            frames = 1;
            usingVariantFrame = true;
        }

        if (!usingVariantFrame && !standardHeaderLooksSane)
        {
            error = $"Invalid TEX1 parameters (childOff={childOff}, frameSize={frameSize}, blockSize={blockSize}, frames={frames})";
            return false;
        }

        // Skip to requested frame
        for (int f = 0; f < frame && offset < (ulong)data.Length; f++)
        {
            if (offset + frameSize > (ulong)data.Length) break;

            ushort skipXBlocks = ReadU16(data, (int)(offset + 16u));
            ushort skipYBlocks = ReadU16(data, (int)(offset + 18u));
            offset += frameSize;

            uint skipBlockCount = (uint)skipXBlocks * skipYBlocks;
            for (uint b = 0; b < skipBlockCount && offset < (ulong)data.Length; b++)
            {
                if (offset + blockSize > (ulong)data.Length) break;

                uint bSize = ReadU32(data, (int)(offset + 8u));
                offset += blockSize + bSize;
            }
        }

        if (offset + frameSize > (ulong)data.Length)
        {
            error = "TEX1 frame truncated";
            return false;
        }

        ushort width0 = ReadU16(data, (int)(offset + 0u));
        ushort height0 = ReadU16(data, (int)(offset + 2u));
        ushort width = ReadU16(data, (int)(offset + 4u));
        ushort height = ReadU16(data, (int)(offset + 6u));
        short offX = ReadI16(data, (int)(offset + 8u));
        short offY = ReadI16(data, (int)(offset + 10u));
        short centerX = ReadI16(data, (int)(offset + 12u));
        short centerY = ReadI16(data, (int)(offset + 14u));
        ushort xBlocks = ReadU16(data, (int)(offset + 16u));
        ushort yBlocks = ReadU16(data, (int)(offset + 18u));

        if (usingVariantFrame)
        {
            width0 = variantFrame.Width0;
            height0 = variantFrame.Height0;
            width = variantFrame.Width;
            height = variantFrame.Height;
            offX = variantFrame.OffsetX;
            offY = variantFrame.OffsetY;
            centerX = variantFrame.CenterX;
            centerY = variantFrame.CenterY;
            xBlocks = variantFrame.XBlocks;
            yBlocks = variantFrame.YBlocks;
        }

        if (xBlocks == 0 || yBlocks == 0 || xBlocks > 256 || yBlocks > 256 || (uint)xBlocks * yBlocks > 4096)
        {
            error = $"TEX1 invalid block grid ({xBlocks} x {yBlocks})";
            return false;
        }

        if ((width == 0 || width > 16384 || height == 0 || height > 16384)
            && width0 > 0 && width0 <= 16384 && height0 > 0 && height0 <= 16384)
        {
            (width, width0) = (width0, width);
            (height, height0) = (height0, height);
        }

        offset += frameSize;

        if (width == 0 || height == 0)
        {
            error = $"TEX1 empty frame (w0={width0}, h0={height0}, w={width}, h={height})";
            return false;
        }
        if (width > 16384 || height > 16384)
        {
            error = $"TEX1 dimensions too large ({width} x {height})";
            return false;
        }

        int outW = width0 > 0 ? width0 : width;
        int outH = height0 > 0 ? height0 : height;

        if (!TryComputeByteSize(outW, outH, 4, MaxDecodedImageBytes, out int outRgbaBytes))
        {
            error = $"TEX1 decoded image too large ({outW} x {outH})";
            return false;
        }

        var outImage = new DecodedImage
        {
            Width = outW,
            Height = outH,
            OffsetX = offX,
            OffsetY = offY,
            CenterX = centerX,
            CenterY = centerY,
            Rgba8 = new byte[outRgbaBytes],
        };

        // Pre-scan block headers to determine actual column widths / row heights.
        int[] colWidths = new int[xBlocks];
        int[] rowHeights = new int[yBlocks];
        {
            ulong scanOff = offset;
            for (uint block = 0; block < (uint)xBlocks * yBlocks && scanOff < (ulong)data.Length; block++)
            {
                if (scanOff + blockSize > (ulong)data.Length) break;

                ushort bw = ReadU16(data, (int)(scanOff + 4u));
                ushort bh = ReadU16(data, (int)(scanOff + 6u));
                uint bs = ReadU32(data, (int)(scanOff + 8u));

                int col = (int)(block % xBlocks);
                int row = (int)(block / xBlocks);
                if (bw > colWidths[col]) colWidths[col] = bw;
                if (bh > rowHeights[row]) rowHeights[row] = bh;

                scanOff += blockSize + bs;
            }
        }

        int[] colStartX = new int[xBlocks];
        int[] rowStartY = new int[yBlocks];
        for (int c = 1; c < xBlocks; c++)
        {
            colStartX[c] = colStartX[c - 1] + colWidths[c - 1];
        }
        for (int r = 1; r < yBlocks; r++)
        {
            rowStartY[r] = rowStartY[r - 1] + rowHeights[r - 1];
        }

        uint decodedBlocks = 0;
        for (uint block = 0; block < (uint)xBlocks * yBlocks && offset < (ulong)data.Length; block++)
        {
            if (offset + blockSize > (ulong)data.Length) break;

            byte compr = data[(int)(offset + 0u)];
            byte pixelType = data[(int)(offset + 1u)];
            ushort bWidth = ReadU16(data, (int)(offset + 4u));
            ushort bHeight = ReadU16(data, (int)(offset + 6u));
            uint bSize = ReadU32(data, (int)(offset + 8u));
            offset += blockSize;

            if (bSize == 0)
            {
                continue;
            }

            ulong remaining = (ulong)data.Length - offset;
            if (bSize > remaining)
            {
                ulong overshoot = bSize - remaining;
                if (compr != 0 && overshoot <= 8 && remaining > 0)
                {
                    bSize = (uint)remaining;
                }
                else
                {
                    continue;
                }
            }

            ReadOnlySpan<byte> blockData = data.Slice((int)offset, (int)bSize);
            offset += bSize;

            // Decompress zlib if needed
            if (compr != 0)
            {
                if (IsChunkedZlib(blockData))
                {
                    if (!ZlibUtils.TryDecompressChunked(blockData, out byte[] inflated, out _))
                    {
                        continue;
                    }
                    blockData = inflated;
                }
                else
                {
                    if (!ZlibUtils.TryDecompress(blockData, out byte[] inflated, out _))
                    {
                        continue;
                    }
                    blockData = inflated;
                }
            }

            // XET 压缩：先解包为原始像素/块数据，再走像素类型分支解码。
            if (blockData.Length >= 4 && XetCodec.IsXetCompressed(blockData))
            {
                int expectedXetBytes = 0;
                switch ((Tex1PixelType)pixelType)
                {
                    case Tex1PixelType.A8R8G8B8:
                        expectedXetBytes = bWidth * bHeight * 4;
                        break;
                    case Tex1PixelType.A4R4G4B4:
                    case Tex1PixelType.A0R5G6B5:
                        expectedXetBytes = bWidth * bHeight * 2;
                        break;
                    case Tex1PixelType.Dxt1:
                        expectedXetBytes = ((bWidth + 3) / 4) * ((bHeight + 3) / 4) * 8;
                        break;
                    case Tex1PixelType.Dxt3:
                    case Tex1PixelType.Dxt5:
                        expectedXetBytes = ((bWidth + 3) / 4) * ((bHeight + 3) / 4) * 16;
                        break;
                    default:
                        expectedXetBytes = bWidth * bHeight;
                        break;
                }

                if (expectedXetBytes <= 0 || expectedXetBytes > MaxDecodedBlockBytes)
                {
                    continue;
                }

                byte[] xetOut = new byte[expectedXetBytes];
                if (!XetCodec.TryDecode(blockData, xetOut, out int written, out _))
                {
                    continue;
                }

                if (written <= 0)
                {
                    continue;
                }

                blockData = xetOut.AsSpan(0, written);
            }

            if (bWidth == 0 || bHeight == 0) continue;
            if (!TryComputeByteSize(bWidth, bHeight, 4, MaxDecodedBlockBytes, out int blockRgbaBytes)) continue;

            byte[] blockRgba = new byte[blockRgbaBytes];
            bool blockDecoded = false;

            switch ((Tex1PixelType)pixelType)
            {
                case Tex1PixelType.A8R8G8B8:
                    if (blockData.Length >= bWidth * bHeight * 4)
                    {
                        ConvertA8R8G8B8ToRgba8(blockData, blockRgba, bWidth, bHeight);
                        blockDecoded = true;
                    }
                    break;
                case Tex1PixelType.A4R4G4B4:
                    if (blockData.Length >= bWidth * bHeight * 2)
                    {
                        ConvertA4R4G4B4ToRgba8(blockData, blockRgba, bWidth, bHeight);
                        blockDecoded = true;
                    }
                    break;
                case Tex1PixelType.A0R5G6B5:
                    if (blockData.Length >= bWidth * bHeight * 2)
                    {
                        ConvertR5G6B5ToRgba8(blockData, blockRgba, bWidth, bHeight);
                        blockDecoded = true;
                    }
                    break;
                case Tex1PixelType.Dxt1:
                    {
                        int expected = ((bWidth + 3) / 4) * ((bHeight + 3) / 4) * 8;
                        if (blockData.Length >= expected && DxtCodec.TryDecodeDxt1ToRgba8(blockData, blockRgba, bWidth, bHeight, out _))
                        {
                            blockDecoded = true;
                        }
                    }
                    break;
                case Tex1PixelType.Dxt3:
                    {
                        int expected = ((bWidth + 3) / 4) * ((bHeight + 3) / 4) * 16;
                        if (blockData.Length >= expected && DxtCodec.TryDecodeDxt3ToRgba8(blockData, blockRgba, bWidth, bHeight, out _))
                        {
                            blockDecoded = true;
                        }
                    }
                    break;
                case Tex1PixelType.Dxt5:
                    {
                        int expected = ((bWidth + 3) / 4) * ((bHeight + 3) / 4) * 16;
                        if (blockData.Length >= expected && DxtCodec.TryDecodeDxt5ToRgba8(blockData, blockRgba, bWidth, bHeight, out _))
                        {
                            blockDecoded = true;
                        }
                    }
                    break;
                default:
                    // XET 解码后续再补齐
                    break;
            }

            if (!blockDecoded) continue;
            decodedBlocks++;

            int curCol = (int)(block % xBlocks);
            int curRow = (int)(block / xBlocks);
            int destX = colStartX[curCol];
            int destY = rowStartY[curRow];

            if (destX >= outW || destY >= outH) continue;

            int copyWidth = Math.Min(bWidth, outW - destX);
            int copyHeight = Math.Min(bHeight, outH - destY);
            for (int y = 0; y < copyHeight; y++)
            {
                int si = (y * bWidth) * 4;
                int di = ((destY + y) * outW + destX) * 4;
                blockRgba.AsSpan(si, copyWidth * 4).CopyTo(outImage.Rgba8.AsSpan(di));
            }
        }

        if (decodedBlocks == 0)
        {
            error = "TEX1 decoded 0 blocks（可能包含未支持的 DXT/XET 压缩）";
            return false;
        }

        image = outImage;
        return true;
    }

    private static void ConvertA8R8G8B8ToRgba8(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        int pixelCount = Math.Min(width * height, Math.Min(src.Length / 4, dst.Length / 4));
        for (int i = 0; i < pixelCount; i++)
        {
            int si = i * 4;
            int di = i * 4;
            dst[di + 0] = src[si + 2];
            dst[di + 1] = src[si + 1];
            dst[di + 2] = src[si + 0];
            dst[di + 3] = src[si + 3];
        }
    }

    private static void ConvertA4R4G4B4ToRgba8(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        int pixelCount = Math.Min(width * height, Math.Min(src.Length / 2, dst.Length / 4));
        for (int i = 0; i < pixelCount; i++)
        {
            ushort p = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
            int di = i * 4;
            dst[di + 0] = (byte)(((p >> 8) & 0x0F) * 17);
            dst[di + 1] = (byte)(((p >> 4) & 0x0F) * 17);
            dst[di + 2] = (byte)((p & 0x0F) * 17);
            dst[di + 3] = (byte)(((p >> 12) & 0x0F) * 17);
        }
    }

    private static void ConvertR5G6B5ToRgba8(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        int pixelCount = Math.Min(width * height, Math.Min(src.Length / 2, dst.Length / 4));
        for (int i = 0; i < pixelCount; i++)
        {
            ushort p = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
            byte r = (byte)(((p >> 11) & 0x1F) * 255 / 31);
            byte g = (byte)(((p >> 5) & 0x3F) * 255 / 63);
            byte b = (byte)((p & 0x1F) * 255 / 31);

            int di = i * 4;
            dst[di + 0] = r;
            dst[di + 1] = g;
            dst[di + 2] = b;
            dst[di + 3] = 255;
        }
    }

    private static int CountStructurallyValidOldTexFrames(
        ReadOnlySpan<byte> data,
        int declaredFrames,
        byte option)
    {
        if (data.Length < 10 || declaredFrames <= 0) return 1;

        bool hasUnknownBits = (option & 0x60u) != 0;
        bool hasSizePrefix = (option & (TexRle | 0x02u | TexS3Tc)) != 0 || hasUnknownBits;

        ulong offset = 6u;
        int validFrames = 0;

        for (int frame = 0; frame < declaredFrames; frame++)
        {
            if (offset + 10u > (ulong)data.Length) break;

            ushort width = ReadU16(data, (int)(offset + 0u));
            ushort height = ReadU16(data, (int)(offset + 2u));
            byte xBlocks = data[(int)(offset + 8u)];
            byte yBlocks = data[(int)(offset + 9u)];
            offset += 10u;

            if (width == 0 || height == 0 || width > 8192 || height > 8192) break;
            if (xBlocks == 0 || yBlocks == 0) break;

            uint blockCount = (uint)xBlocks * (uint)yBlocks;
            if (blockCount == 0 || blockCount > 4096) break;

            bool frameValid = true;
            for (uint block = 0; block < blockCount; block++)
            {
                if (offset + 2u > (ulong)data.Length)
                {
                    frameValid = false;
                    break;
                }

                byte bWidth = (byte)(data[(int)(offset + 0u)] + 1u);
                byte bHeight = (byte)(data[(int)(offset + 1u)] + 1u);
                offset += 2u;

                if (bWidth == 0 || bHeight == 0)
                {
                    frameValid = false;
                    break;
                }

                if (hasSizePrefix)
                {
                    if (offset + 4u > (ulong)data.Length)
                    {
                        frameValid = false;
                        break;
                    }

                    uint blockSize = ReadU32(data, (int)offset);
                    offset += 4u;

                    if (offset + blockSize > (ulong)data.Length)
                    {
                        frameValid = false;
                        break;
                    }

                    offset += blockSize;
                }
                else
                {
                    ulong rawSize = (ulong)bWidth * bHeight * 2u;
                    if (offset + rawSize > (ulong)data.Length)
                    {
                        frameValid = false;
                        break;
                    }

                    offset += rawSize;
                }

                if ((option & TexS3TcAlpha) != 0)
                {
                    if (offset + 4u > (ulong)data.Length)
                    {
                        frameValid = false;
                        break;
                    }

                    uint alphaSize = ReadU32(data, (int)offset);
                    offset += 4u;

                    if (offset + alphaSize > (ulong)data.Length)
                    {
                        frameValid = false;
                        break;
                    }

                    offset += alphaSize;
                }
            }

            if (!frameValid) break;
            validFrames++;
        }

        return validFrames > 0 ? validFrames : 1;
    }

    private static bool TryDecodeOldTex(ReadOnlySpan<byte> data, int frame, out DecodedImage image, out string error)
    {
        image = new DecodedImage();
        error = string.Empty;

        if (data.Length < 8)
        {
            error = "Old TEX truncated";
            return false;
        }

        uint magic = ReadU32(data, 0);
        byte option = (byte)((magic >> 8) & 0xFFu);
        ushort frames = (ushort)((magic >> 16) & 0xFFFFu);

        if (frames > 256)
        {
            frames = (ushort)(frames & 0xFF);
        }
        if (frames == 0)
        {
            frames = 1;
        }

        if (frame < 0 || frame >= frames)
        {
            frame = 0;
        }

        ulong offset = 6; // 4 magic + 2 speed
        bool hasUnknownBits = (option & 0x60) != 0;
        bool hasSizePrefix = (option & (TexRle | 0x02 | TexS3Tc)) != 0 || hasUnknownBits;

        // Skip to requested frame
        for (int f = 0; f < frame && offset + 10 <= (ulong)data.Length; f++)
        {
            offset += 8; // skip width, height, offX, offY
            if (offset + 2 > (ulong)data.Length) break;

            int xBlks = data[(int)offset++];
            int yBlks = data[(int)offset++];

            for (int bb = 0; bb < xBlks * yBlks && offset + 2 < (ulong)data.Length; bb++)
            {
                int bW = data[(int)offset++] + 1;
                int bH = data[(int)offset++] + 1;
                ulong expectedRaw = (ulong)bW * (ulong)bH * 2u;

                if (hasSizePrefix)
                {
                    if (offset + 4 > (ulong)data.Length) break;
                    uint blkSize = ReadU32(data, (int)offset);
                    offset += 4 + blkSize;
                }
                else
                {
                    offset += expectedRaw;
                }

                if ((option & TexS3TcAlpha) != 0)
                {
                    if (offset + 4 > (ulong)data.Length) break;
                    uint alphaSize = ReadU32(data, (int)offset);
                    offset += 4 + alphaSize;
                }
            }
        }

        if (offset + 10 > (ulong)data.Length)
        {
            error = "Old TEX truncated";
            return false;
        }

        ushort fWidth = ReadU16(data, (int)offset); offset += 2;
        ushort fHeight = ReadU16(data, (int)offset); offset += 2;
        short offX = ReadI16(data, (int)offset); offset += 2;
        short offY = ReadI16(data, (int)offset); offset += 2;
        int xBlocks = data[(int)offset++];
        int yBlocks = data[(int)offset++];

        if (xBlocks == 0 || yBlocks == 0 || (uint)(xBlocks * yBlocks) > 4096)
        {
            error = $"Invalid old TEX block grid (opt=0x{option:X2}, frames={frames}, blocks={xBlocks} x {yBlocks})";
            return false;
        }

        if (fWidth == 0 || fHeight == 0 || fWidth > 8192 || fHeight > 8192)
        {
            error = $"Invalid old TEX dimensions (opt=0x{option:X2}, frames={frames}, w={fWidth}, h={fHeight})";
            return false;
        }

        int alignedW = AlignOldTexDimension(fWidth);
        int alignedH = AlignOldTexDimension(fHeight);

        if (!TryComputeByteSize(fWidth, fHeight, 4, MaxDecodedImageBytes, out int rgbaBytes))
        {
            error = $"Old TEX decoded image too large (w={fWidth}, h={fHeight})";
            return false;
        }

        if (!TryComputeByteSize(alignedW, alignedH, 2, MaxDecodedImageBytes, out int pixels4444Bytes))
        {
            error = $"Old TEX aligned buffer too large (w={alignedW}, h={alignedH})";
            return false;
        }

        var outImage = new DecodedImage
        {
            Width = fWidth,
            Height = fHeight,
            OffsetX = offX,
            OffsetY = offY,
            CenterX = 0,
            CenterY = 0,
            Rgba8 = new byte[rgbaBytes],
        };

        ushort[] pixels4444 = new ushort[pixels4444Bytes / 2];

        // First pass: scan block headers to determine layout
        int[] colWidths = new int[xBlocks];
        int[] rowHeights = new int[yBlocks];
        {
            ulong scanOff = offset;
            for (int bb = 0; bb < xBlocks * yBlocks && scanOff + 2 < (ulong)data.Length; bb++)
            {
                int scanBW = data[(int)scanOff++] + 1;
                int scanBH = data[(int)scanOff++] + 1;
                int col = bb % xBlocks;
                int row = bb / xBlocks;
                if (scanBW > colWidths[col]) colWidths[col] = scanBW;
                if (scanBH > rowHeights[row]) rowHeights[row] = scanBH;

                if (hasSizePrefix)
                {
                    if (scanOff + 4 > (ulong)data.Length) break;
                    uint blkSz = ReadU32(data, (int)scanOff);
                    scanOff += 4 + blkSz;
                }
                else
                {
                    ulong expectedScanRaw = (ulong)scanBW * (ulong)scanBH * 2u;
                    scanOff += expectedScanRaw;
                }

                if ((option & TexS3TcAlpha) != 0)
                {
                    if (scanOff + 4 > (ulong)data.Length) break;
                    uint alphaSz = ReadU32(data, (int)scanOff);
                    scanOff += 4 + alphaSz;
                }
            }
        }

        // Calculate cumulative block positions
        int[] colStartX = new int[xBlocks];
        int[] rowStartY = new int[yBlocks];
        for (int c = 1; c < xBlocks; c++)
        {
            colStartX[c] = colStartX[c - 1] + colWidths[c - 1];
        }
        for (int r = 1; r < yBlocks; r++)
        {
            rowStartY[r] = rowStartY[r - 1] + rowHeights[r - 1];
        }

        // Second pass: decode blocks
        for (int bb = 0; bb < xBlocks * yBlocks && offset + 2 < (ulong)data.Length; bb++)
        {
            int bW = data[(int)offset++] + 1;
            int bH = data[(int)offset++] + 1;
            ulong expectedRaw = (ulong)bW * (ulong)bH * 2u;

            if (expectedRaw > MaxDecodedBlockBytes)
            {
                error = $"Old TEX block too large (w={bW}, h={bH})";
                return false;
            }

            int curBlockCol = bb % xBlocks;
            int curBlockRow = bb / xBlocks;

            ushort[] blockPixels = new ushort[bW * bH];

            if ((option & TexS3Tc) != 0 || hasUnknownBits)
            {
                if (offset + 4 > (ulong)data.Length) break;
                uint blkSize = ReadU32(data, (int)offset);
                offset += 4;
                if (blkSize == 0 || offset + blkSize > (ulong)data.Length) break;

                DecodeS3TcWoool(data.Slice((int)offset, (int)blkSize), blockPixels, bW, bH);
                offset += blkSize;
            }
            else if ((option & (TexRle | 0x02)) != 0)
            {
                if (offset + 4 > (ulong)data.Length) break;
                uint blkSize = ReadU32(data, (int)offset);
                offset += 4;
                if (blkSize == 0 || offset + blkSize > (ulong)data.Length) break;

                byte[] decoded = new byte[blockPixels.Length * 2];
                DecodeRle(data.Slice((int)offset, (int)blkSize), decoded);
                FillU16LittleEndian(decoded, blockPixels);
                offset += blkSize;
            }
            else
            {
                // Raw A4R4G4B4
                if (offset + expectedRaw <= (ulong)data.Length)
                {
                    byte[] raw = data.Slice((int)offset, (int)expectedRaw).ToArray();
                    FillU16LittleEndian(raw, blockPixels);
                    offset += expectedRaw;
                }
                else if (offset + 4 <= (ulong)data.Length)
                {
                    // Fallback: try size-prefixed RLE
                    uint blkSize = ReadU32(data, (int)offset);
                    offset += 4;
                    if (blkSize > 0 && offset + blkSize <= (ulong)data.Length)
                    {
                        byte[] decoded = new byte[blockPixels.Length * 2];
                        DecodeRle(data.Slice((int)offset, (int)blkSize), decoded);
                        FillU16LittleEndian(decoded, blockPixels);
                        offset += blkSize;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if ((option & TexS3TcAlpha) != 0)
            {
                if (offset + 4 > (ulong)data.Length) break;
                uint alphaSize = ReadU32(data, (int)offset);
                offset += 4;
                if (alphaSize == 0 || offset + alphaSize > (ulong)data.Length) break;

                ApplyAlpha(blockPixels, data.Slice((int)offset, (int)alphaSize));
                offset += alphaSize;
            }

            int destX = colStartX[curBlockCol];
            int destY = rowStartY[curBlockRow];
            for (int y = 0; y < bH && (destY + y) < alignedH; y++)
            {
                for (int x = 0; x < bW && (destX + x) < alignedW; x++)
                {
                    pixels4444[(destY + y) * alignedW + (destX + x)] = blockPixels[y * bW + x];
                }
            }
        }

        // Convert A4R4G4B4 to RGBA8
        for (int y = 0; y < fHeight; y++)
        {
            for (int x = 0; x < fWidth; x++)
            {
                ushort p = pixels4444[y * alignedW + x];
                int di = (y * fWidth + x) * 4;
                outImage.Rgba8[di + 0] = (byte)(((p >> 8) & 0x0F) * 17);
                outImage.Rgba8[di + 1] = (byte)(((p >> 4) & 0x0F) * 17);
                outImage.Rgba8[di + 2] = (byte)((p & 0x0F) * 17);
                outImage.Rgba8[di + 3] = (byte)(((p >> 12) & 0x0F) * 17);
            }
        }

        image = outImage;
        return true;
    }

    private static int AlignOldTexDimension(int v)
    {
        int aligned = v;
        if ((aligned % 256) < 128) aligned = ((aligned + 7) / 8) * 8;
        else aligned = ((aligned + 15) / 16) * 16;
        return aligned;
    }

    private static bool TryComputeByteSize(int width, int height, int bytesPerPixel, int maxBytes, out int bytes)
    {
        bytes = 0;
        if (width <= 0 || height <= 0 || bytesPerPixel <= 0) return false;

        long pixelCount = (long)width * height;
        long total = pixelCount * bytesPerPixel;
        if (total <= 0 || total > maxBytes) return false;
        bytes = (int)total;
        return true;
    }

    private static void FillU16LittleEndian(ReadOnlySpan<byte> src, Span<ushort> dst)
    {
        int count = Math.Min(dst.Length, src.Length / 2);
        for (int i = 0; i < count; i++)
        {
            dst[i] = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
        }
    }

    private static void DecodeRle(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int sp = 0;
        int dp = 0;
        while (sp < src.Length && dp < dst.Length)
        {
            byte ctrl = src[sp++];
            if ((ctrl & 0x80) != 0)
            {
                int count = ctrl - 0x80;
                for (int i = 0; i < count && sp + 2 <= src.Length && dp + 2 <= dst.Length; i++)
                {
                    dst[dp++] = src[sp++];
                    dst[dp++] = src[sp++];
                }
            }
            else
            {
                if (sp + 2 > src.Length) break;
                byte v0 = src[sp++];
                byte v1 = src[sp++];
                for (int i = 0; i < ctrl && dp + 2 <= dst.Length; i++)
                {
                    dst[dp++] = v0;
                    dst[dp++] = v1;
                }
            }
        }
    }

    private static void DecodeS3TcWoool(ReadOnlySpan<byte> src, Span<ushort> dstPixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return;
        if (dstPixels.Length < width * height) return;

        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);

        int sp = 0;
        int blockX = 0;
        int blockY = 0;

        Span<ushort> palette = stackalloc ushort[4];

        while (sp + 7 <= src.Length && blockY < blocksY)
        {
            uint cd = (uint)(src[sp] | (src[sp + 1] << 8) | (src[sp + 2] << 16) | (0xFFu << 24));
            sp += 3;

            byte b0 = (byte)((cd >> 4) & 0x0F);
            byte g0 = (byte)((cd >> 12) & 0x0F);
            byte r0 = (byte)((cd >> 20) & 0x0F);
            byte b1 = (byte)(cd & 0x0F);
            byte g1 = (byte)((cd >> 8) & 0x0F);
            byte r1 = (byte)((cd >> 16) & 0x0F);

            byte r2 = (byte)((2 * r0 + r1 + 1) / 3);
            byte g2 = (byte)((2 * g0 + g1 + 1) / 3);
            byte b2 = (byte)((2 * b0 + b1 + 1) / 3);
            byte r3 = (byte)((r0 + 2 * r1 + 1) / 3);
            byte g3 = (byte)((g0 + 2 * g1 + 1) / 3);
            byte b3 = (byte)((b0 + 2 * b1 + 1) / 3);

            palette[0] = (ushort)(b0 | (g0 << 4) | (r0 << 8) | ((r0 == 0 && g0 == 0 && b0 == 0) ? 0x0000 : 0xF000));
            palette[1] = (ushort)(b1 | (g1 << 4) | (r1 << 8) | ((r1 == 0 && g1 == 0 && b1 == 0) ? 0x0000 : 0xF000));
            palette[2] = (ushort)(b2 | (g2 << 4) | (r2 << 8) | ((r2 == 0 && g2 == 0 && b2 == 0) ? 0x0000 : 0xF000));
            palette[3] = (ushort)(b3 | (g3 << 4) | (r3 << 8) | ((r3 == 0 && g3 == 0 && b3 == 0) ? 0x0000 : 0xF000));

            int baseX = blockX * 4;
            int baseY = blockY * 4;
            for (int row = 0; row < 4; row++)
            {
                byte indices = src[sp++];
                int y = baseY + row;
                if (y >= height) continue;

                for (int col = 0; col < 4; col++)
                {
                    int x = baseX + col;
                    if (x < width)
                    {
                        dstPixels[y * width + x] = palette[(indices >> (col * 2)) & 0x03];
                    }
                }
            }

            blockX++;
            if (blockX >= blocksX)
            {
                blockX = 0;
                blockY++;
            }
        }
    }

    private static void ApplyAlpha(Span<ushort> pixels4444, ReadOnlySpan<byte> alphaSrc)
    {
        int sp = 0;
        int pixelIndex = 0; // 每个 alpha byte 对应 2 个 16-bit 像素（高/低 nibble）

        while (sp < alphaSrc.Length && pixelIndex < pixels4444.Length)
        {
            byte ctrl = alphaSrc[sp++];
            if (ctrl > 0x80)
            {
                int count = ctrl - 0x80;
                for (int i = 0; i < count && sp < alphaSrc.Length; i++)
                {
                    byte alpha = alphaSrc[sp++];
                    ApplyPackedAlphaByte(pixels4444, ref pixelIndex, alpha);
                }
            }
            else
            {
                int count = ctrl;
                if (sp >= alphaSrc.Length) break;
                byte alpha = alphaSrc[sp++];
                for (int i = 0; i < count; i++)
                {
                    ApplyPackedAlphaByte(pixels4444, ref pixelIndex, alpha);
                }
            }
        }
    }

    private static void ApplyPackedAlphaByte(Span<ushort> pixels4444, ref int pixelIndex, byte alpha)
    {
        byte a0 = (byte)(alpha >> 4);
        byte a1 = (byte)(alpha & 0x0F);

        if (pixelIndex < pixels4444.Length)
        {
            pixels4444[pixelIndex] = ApplyAlphaNibble(pixels4444[pixelIndex], a0);
            pixelIndex++;
        }

        if (pixelIndex < pixels4444.Length)
        {
            pixels4444[pixelIndex] = ApplyAlphaNibble(pixels4444[pixelIndex], a1);
            pixelIndex++;
        }
    }

    private static ushort ApplyAlphaNibble(ushort pixel4444, byte alphaNibble)
    {
        ushort a = (ushort)((pixel4444 >> 12) & 0x0F);
        ushort newA = (ushort)(a & alphaNibble);
        return (ushort)((pixel4444 & 0x0FFF) | (newA << 12));
    }

    private static bool IsZlibHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;
        byte cmf = data[0];
        byte flg = data[1];
        return (cmf & 0x0F) == 0x08 && (((cmf * 256) + flg) % 31) == 0;
    }

    private static bool IsChunkedZlib(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6) return false;
        uint firstChunkSize = ReadU32(data, 0);
        return firstChunkSize > 0
               && (ulong)firstChunkSize + 4 <= (ulong)data.Length
               && IsZlibHeader(data.Slice(4));
    }

    private static string DescribeUnknownTexHeader(ReadOnlySpan<byte> data)
    {
        int head = Math.Min(16, data.Length);
        if (head <= 0)
        {
            return "Unknown format []";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("Unknown format [");
        for (int i = 0; i < head; i++)
        {
            sb.Append(data[i].ToString("X2"));
            sb.Append(' ');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset + 2u > (uint)data.Length) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    private static short ReadI16(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset + 2u > (uint)data.Length) return 0;
        return BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset + 4u > (uint)data.Length) return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }
}
