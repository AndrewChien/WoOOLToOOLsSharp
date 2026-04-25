using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WoOOLToOOLsSharp.Rendering.Vulkan;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.MapEditor.App;

public readonly record struct PrefabThumbnailInfo(nint TextureId, int Width, int Height);

public sealed class AsyncPrefabThumbnailLoader : IDisposable
{
    private readonly VulkanRenderer _renderer;
    private readonly MapTextureIndex _textureIndex;

    private readonly Dictionary<string, CachedThumbnail> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingThumbnail> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _decodeCts = new();
    private ulong _frameIndex;
    private int _generation;

    private int _submittedThisFrame;
    private int _createdThisFrame;

    public int MaxCacheItems { get; set; } = 128;
    public int SubmitBudgetPerFrame { get; set; } = 4;
    public int CreateBudgetPerFrame { get; set; } = 2;
    public int MaxThumbnailSize { get; set; } = 96;
    public bool PreferTextureThumbnails { get; set; } = true;

    public int CachedCount => _cache.Count;
    public int PendingCount => _pending.Count;
    public int ErrorCount => _errors.Count;

    public AsyncPrefabThumbnailLoader(VulkanRenderer renderer, MapTextureIndex textureIndex)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _textureIndex = textureIndex ?? throw new ArgumentNullException(nameof(textureIndex));
    }

    public void TickFrame()
    {
        _frameIndex++;
        _submittedThisFrame = 0;
        _createdThisFrame = 0;

        ProcessCompleted();
        PruneCacheIfNeeded();
    }

    public void InvalidateAll()
    {
        _generation++;
        CancelDecodeTasks();
        Clear();
    }

    public void Clear()
    {
        foreach ((_, CachedThumbnail thumb) in _cache)
        {
            if (thumb.TextureId != nint.Zero)
            {
                _renderer.DestroyImGuiTexture(thumb.TextureId);
            }
        }

        _cache.Clear();
        _pending.Clear();
        _errors.Clear();
    }

    public bool TryGetError(string prefabPath, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return false;
        }

        return _errors.TryGetValue(prefabPath, out error!);
    }

    public bool TryGetThumbnail(string prefabPath, out PrefabThumbnailInfo info)
    {
        info = default;

        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return false;
        }

        if (_cache.TryGetValue(prefabPath, out CachedThumbnail? cached))
        {
            cached.LastUsedFrame = _frameIndex;
            info = cached.ToInfo();
            return cached.TextureId != nint.Zero;
        }

        if (_pending.ContainsKey(prefabPath) || _errors.ContainsKey(prefabPath))
        {
            return false;
        }

        if (_submittedThisFrame >= SubmitBudgetPerFrame)
        {
            return false;
        }

        int maxDim = Math.Clamp(MaxThumbnailSize, 16, 256);

        _submittedThisFrame++;
        int gen = _generation;
        CancellationToken token = _decodeCts.Token;
        Task<DecodeResult> task = Task.Run(() => DecodeOnWorker(prefabPath, maxDim, token), token);
        _pending[prefabPath] = new PendingThumbnail(gen, task);
        return false;
    }

    private DecodeResult DecodeOnWorker(string prefabPath, int maxDim, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();

            if (!MapDocument.TryLoad(prefabPath, out MapDocument? map, out string error))
            {
                return DecodeResult.Fail(string.IsNullOrWhiteSpace(error) ? "读取 prefab 失败。" : error);
            }

            if (map is null)
            {
                return DecodeResult.Fail("Prefab 地图为空。");
            }

            token.ThrowIfCancellationRequested();

            if (PreferTextureThumbnails
                && _textureIndex.IsReady
                && (TryBuildCompositedStampThumbnail(map, _textureIndex, maxDim, token, out byte[] rgba8, out int width, out int height, out _)
                    || TryBuildTextureThumbnail(map, _textureIndex, maxDim, token, out rgba8, out width, out height, out _)))
            {
                return DecodeResult.Success(rgba8, width, height);
            }

            if (!TryBuildPlaceholderThumbnail(map, maxDim, token, out rgba8, out width, out height, out error))
            {
                return DecodeResult.Fail(string.IsNullOrWhiteSpace(error) ? "生成缩略图失败。" : error);
            }

            return DecodeResult.Success(rgba8, width, height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DecodeResult.Fail(ex.Message);
        }
    }

    private static bool TryBuildPlaceholderThumbnail(
        MapDocument map,
        int maxDim,
        CancellationToken token,
        out byte[] rgba8,
        out int width,
        out int height,
        out string error)
    {
        rgba8 = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空";
            return false;
        }

        int srcW = map.Width;
        int srcH = map.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            error = "地图尺寸无效";
            return false;
        }

        if (map.Cells.Length <= 0)
        {
            error = "地图格子为空";
            return false;
        }

        float scale = Math.Min(1.0f, Math.Min((float)maxDim / srcW, (float)maxDim / srcH));
        width = Math.Max(1, (int)MathF.Round(srcW * scale));
        height = Math.Max(1, (int)MathF.Round(srcH * scale));

        long totalBytesLong = (long)width * height * 4;
        if (totalBytesLong is <= 0 or > int.MaxValue)
        {
            error = "缩略图尺寸过大";
            return false;
        }

        rgba8 = new byte[(int)totalBytesLong];

        for (int y = 0; y < height; y++)
        {
            if ((y & 7) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            int srcY = (int)(((y + 0.5f) / height) * srcH);
            srcY = Math.Clamp(srcY, 0, srcH - 1);

            for (int x = 0; x < width; x++)
            {
                int srcX = (int)(((x + 0.5f) / width) * srcW);
                srcX = Math.Clamp(srcX, 0, srcW - 1);

                int srcIndex = srcY * srcW + srcX;
                byte r = 18;
                byte g = 18;
                byte b = 18;
                byte a = 255;

                if ((uint)srcIndex < (uint)map.Cells.Length)
                {
                    var cell = map.Cells[srcIndex];

                    bool hasBack = cell.BackImage != 0;
                    bool hasMiddle = cell.MiddleImage != 0 || cell.MiddleImage2 != 0;
                    bool hasFront = (cell.FrontImage & 0x00FFFFFFu) != 0;
                    bool hasUnder = (cell.UnderObject & 0x00FFFFFFu) != 0;
                    bool hasOver = (cell.OverObject & 0x00FFFFFFu) != 0;
                    bool hasNear = (cell.NearGround & 0x00FFFFFFu) != 0;

                    int rr = r;
                    int gg = g;
                    int bb = b;

                    if (hasBack) bb += 70;
                    if (hasMiddle) gg += 90;
                    if (hasFront) { rr += 150; gg += 60; }
                    if (hasUnder) { rr += 90; bb += 90; }
                    if (hasOver) { rr += 80; gg += 80; }
                    if (hasNear) { gg += 70; bb += 120; }

                    r = (byte)Math.Clamp(rr, 0, 255);
                    g = (byte)Math.Clamp(gg, 0, 255);
                    b = (byte)Math.Clamp(bb, 0, 255);
                }

                int dst = (y * width + x) * 4;
                rgba8[dst + 0] = r;
                rgba8[dst + 1] = g;
                rgba8[dst + 2] = b;
                rgba8[dst + 3] = a;
            }
        }

        return true;
    }

    private static bool TryBuildCompositedStampThumbnail(
        MapDocument map,
        MapTextureIndex textureIndex,
        int maxDim,
        CancellationToken token,
        out byte[] rgba8,
        out int width,
        out int height,
        out string error)
    {
        rgba8 = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空";
            return false;
        }

        if (textureIndex is null)
        {
            error = "贴图库索引为空";
            return false;
        }

        int objW = map.Width;
        int objH = map.Height;
        if (objW <= 0 || objH <= 0 || map.Cells.Length == 0)
        {
            error = "Prefab 尺寸无效或无格子数据。";
            return false;
        }

        maxDim = Math.Clamp(maxDim, 16, 256);
        width = maxDim;
        height = maxDim;

        long totalBytesLong = (long)width * height * 4;
        if (totalBytesLong is <= 0 or > int.MaxValue)
        {
            error = "缩略图尺寸过大";
            return false;
        }

        rgba8 = new byte[(int)totalBytesLong];

        const float baseCellW = 64.0f;
        const float baseCellH = 32.0f;

        float fullW = objW * baseCellW;
        float fullH = objH * baseCellH;
        if (fullW <= 0.0f || fullH <= 0.0f)
        {
            error = "Prefab 尺寸无效。";
            return false;
        }

        float scale = Math.Min(1.0f, Math.Min(width / fullW, height / fullH));
        if (!float.IsFinite(scale) || scale <= 0.0f)
        {
            error = "缩放系数无效。";
            return false;
        }

        float drawW = fullW * scale;
        float drawH = fullH * scale;

        float offX = (width - drawW) * 0.5f;
        float offY = (height - drawH) * 0.5f;

        float cellW = baseCellW * scale;
        float cellH = baseCellH * scale;

        var decodedCache = new Dictionary<long, DecodedImage>();
        const int maxUniqueImages = 256;
        bool drawnAny = false;

        for (int y = 0; y < objH; y++)
        {
            if ((y & 7) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            for (int x = 0; x < objW; x++)
            {
                int cellIndex = y * objW + x;
                if ((uint)cellIndex >= (uint)map.Cells.Length)
                {
                    continue;
                }

                var cell = map.Cells[cellIndex];

                float cx = offX + x * cellW;
                float cy = offY + y * cellH;

                if (TryResolveBackTileRef(cell, out int pkg, out int idx)
                    && TryGetDecodedImage(textureIndex, pkg, idx, decodedCache, maxUniqueImages, token, out DecodedImage img))
                {
                    float dx = cx + img.OffsetX * scale;
                    float dy = cy + img.OffsetY * scale;
                    drawnAny |= BlitDecodedImageNearest(rgba8, width, height, img, dx, dy, scale, token);
                }

                if (TryResolveMiddleTileRef(cell, out pkg, out idx)
                    && TryGetDecodedImage(textureIndex, pkg, idx, decodedCache, maxUniqueImages, token, out img))
                {
                    float dx = cx + img.OffsetX * scale;
                    float dy = cy + img.OffsetY * scale;
                    drawnAny |= BlitDecodedImageNearest(rgba8, width, height, img, dx, dy, scale, token);
                }

                if (TryResolveExtraObjectTileRef(cell.NearGround, out pkg, out idx)
                    && TryGetDecodedImage(textureIndex, pkg, idx, decodedCache, maxUniqueImages, token, out img))
                {
                    drawnAny |= BlitDecodedObjectNearest(rgba8, width, height, img, cx, cy, cellH, scale, token);
                }

                if (TryResolveExtraObjectTileRef(cell.UnderObject, out pkg, out idx)
                    && TryGetDecodedImage(textureIndex, pkg, idx, decodedCache, maxUniqueImages, token, out img))
                {
                    drawnAny |= BlitDecodedObjectNearest(rgba8, width, height, img, cx, cy, cellH, scale, token);
                }

                if (TryResolveFrontObjectTileRef(cell, out pkg, out idx)
                    && TryGetDecodedImage(textureIndex, pkg, idx, decodedCache, maxUniqueImages, token, out img))
                {
                    drawnAny |= BlitDecodedObjectNearest(rgba8, width, height, img, cx, cy, cellH, scale, token);
                }

                if (TryResolveExtraObjectTileRef(cell.OverObject, out pkg, out idx)
                    && TryGetDecodedImage(textureIndex, pkg, idx, decodedCache, maxUniqueImages, token, out img))
                {
                    drawnAny |= BlitDecodedObjectNearest(rgba8, width, height, img, cx, cy, cellH, scale, token);
                }
            }
        }

        if (!drawnAny)
        {
            error = "未能合成缩略图（贴图缺失或均为空）。";
            return false;
        }

        return true;
    }

    private static bool TryResolveBackTileRef(in WoOOLToOOLsSharp.Shared.Formats.Nmp.NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (cell.BackImage == 0)
        {
            return false;
        }

        imageIndex = cell.BackImage;
        packageId = cell.BackLibrary != 0 ? cell.BackLibrary : 3001;
        return packageId > 0 && imageIndex > 0;
    }

    private static bool TryResolveMiddleTileRef(in WoOOLToOOLsSharp.Shared.Formats.Nmp.NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (cell.MiddleImage == 0)
        {
            return false;
        }

        imageIndex = cell.MiddleImage;
        packageId = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : 3051;
        return packageId > 0 && imageIndex > 0;
    }

    private static bool TryResolveFrontObjectTileRef(in WoOOLToOOLsSharp.Shared.Formats.Nmp.NmpCellData cell, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        uint masked = cell.FrontImage & 0x00FFFFFFu;
        if (masked == 0)
        {
            return false;
        }

        imageIndex = (int)(masked & 0xFFFF);
        packageId = (int)((masked >> 16) & 0xFF);
        if (packageId == 0)
        {
            packageId = cell.FrontLibrary;
        }

        if (packageId == 0 && imageIndex > 0)
        {
            packageId = 5;
        }

        return packageId > 0 && imageIndex > 0;
    }

    private static bool TryResolveExtraObjectTileRef(uint rawField, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        uint masked = rawField & 0x00FFFFFFu;
        if (masked == 0)
        {
            return false;
        }

        imageIndex = (int)(masked & 0xFFFF);
        if (imageIndex == 0)
        {
            return false;
        }

        packageId = (int)((masked >> 16) & 0xFF);
        if (packageId == 0)
        {
            packageId = 5;
        }

        return packageId > 0;
    }

    private static bool TryGetDecodedImage(
        MapTextureIndex textureIndex,
        int packageId,
        int imageIndex,
        Dictionary<long, DecodedImage> cache,
        int maxUnique,
        CancellationToken token,
        out DecodedImage image)
    {
        image = null!;

        if (packageId <= 0 || imageIndex <= 0)
        {
            return false;
        }

        long key = ((long)packageId << 32) | (uint)imageIndex;
        if (cache.TryGetValue(key, out image!))
        {
            return image is not null && image.IsValid;
        }

        if (cache.Count >= maxUnique && maxUnique > 0)
        {
            return false;
        }

        token.ThrowIfCancellationRequested();

        if (!textureIndex.TryDecodeImage(packageId, imageIndex, frame: 0, token, out DecodedImage decoded, out _))
        {
            return false;
        }

        if (decoded is null || !decoded.IsValid)
        {
            return false;
        }

        cache[key] = decoded;
        image = decoded;
        return true;
    }

    private static bool BlitDecodedObjectNearest(
        byte[] dstRgba8,
        int dstWidth,
        int dstHeight,
        DecodedImage img,
        float cellX,
        float cellY,
        float cellH,
        float scale,
        CancellationToken token)
    {
        float tw = img.Width * scale;
        float th = img.Height * scale;
        float baseY = cellY + cellH - th;
        float dx = cellX - img.CenterX * scale + img.OffsetX * scale;
        float dy = baseY - img.CenterY * scale + img.OffsetY * scale;
        return BlitDecodedImageNearest(dstRgba8, dstWidth, dstHeight, img, dx, dy, scale, token);
    }

    private static bool BlitDecodedImageNearest(
        byte[] dstRgba8,
        int dstWidth,
        int dstHeight,
        DecodedImage img,
        float dstX,
        float dstY,
        float scale,
        CancellationToken token)
    {
        if (img is null || !img.IsValid || img.Rgba8 is null || img.Rgba8.Length == 0)
        {
            return false;
        }

        if (!float.IsFinite(scale) || scale <= 0.0f)
        {
            return false;
        }

        float drawW = img.Width * scale;
        float drawH = img.Height * scale;
        if (drawW <= 0.0f || drawH <= 0.0f)
        {
            return false;
        }

        int x0 = (int)MathF.Floor(dstX);
        int y0 = (int)MathF.Floor(dstY);
        int x1 = (int)MathF.Ceiling(dstX + drawW);
        int y1 = (int)MathF.Ceiling(dstY + drawH);

        if (x1 <= 0 || y1 <= 0 || x0 >= dstWidth || y0 >= dstHeight)
        {
            return false;
        }

        int clippedX0 = Math.Max(0, x0);
        int clippedY0 = Math.Max(0, y0);
        int clippedX1 = Math.Min(dstWidth, x1);
        int clippedY1 = Math.Min(dstHeight, y1);

        if (clippedX1 <= clippedX0 || clippedY1 <= clippedY0)
        {
            return false;
        }

        byte[] src = img.Rgba8;
        bool drewAny = false;

        for (int y = clippedY0; y < clippedY1; y++)
        {
            if ((y & 15) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            int srcY = (int)MathF.Floor((y + 0.5f - dstY) / scale);
            srcY = Math.Clamp(srcY, 0, img.Height - 1);
            int srcRow = srcY * img.Width;

            int dstRow = y * dstWidth;
            for (int x = clippedX0; x < clippedX1; x++)
            {
                int srcX = (int)MathF.Floor((x + 0.5f - dstX) / scale);
                srcX = Math.Clamp(srcX, 0, img.Width - 1);

                int srcIdx = (srcRow + srcX) * 4;
                byte sa = src[srcIdx + 3];
                if (sa == 0)
                {
                    continue;
                }

                int dstIdx = (dstRow + x) * 4;
                BlendPixel(dstRgba8, dstIdx, src, srcIdx);
                drewAny = true;
            }
        }

        return drewAny;
    }

    private static void BlendPixel(byte[] dst, int dstIndex, byte[] src, int srcIndex)
    {
        int sa = src[srcIndex + 3];
        if (sa <= 0)
        {
            return;
        }

        int dr = dst[dstIndex + 0];
        int dg = dst[dstIndex + 1];
        int db = dst[dstIndex + 2];
        int da = dst[dstIndex + 3];

        int sr = src[srcIndex + 0];
        int sg = src[srcIndex + 1];
        int sb = src[srcIndex + 2];

        int invSa = 255 - sa;
        int outAN = sa * 255 + da * invSa;
        if (outAN <= 0)
        {
            dst[dstIndex + 0] = 0;
            dst[dstIndex + 1] = 0;
            dst[dstIndex + 2] = 0;
            dst[dstIndex + 3] = 0;
            return;
        }

        int outA = (outAN + 127) / 255;

        long outRN = (long)sr * sa * 255 + (long)dr * da * invSa;
        long outGN = (long)sg * sa * 255 + (long)dg * da * invSa;
        long outBN = (long)sb * sa * 255 + (long)db * da * invSa;

        int outR = (int)((outRN + outAN / 2) / outAN);
        int outG = (int)((outGN + outAN / 2) / outAN);
        int outB = (int)((outBN + outAN / 2) / outAN);

        dst[dstIndex + 0] = (byte)Math.Clamp(outR, 0, 255);
        dst[dstIndex + 1] = (byte)Math.Clamp(outG, 0, 255);
        dst[dstIndex + 2] = (byte)Math.Clamp(outB, 0, 255);
        dst[dstIndex + 3] = (byte)Math.Clamp(outA, 0, 255);
    }

    private static bool TryBuildTextureThumbnail(
        MapDocument map,
        MapTextureIndex textureIndex,
        int maxDim,
        CancellationToken token,
        out byte[] rgba8,
        out int width,
        out int height,
        out string error)
    {
        rgba8 = Array.Empty<byte>();
        width = 0;
        height = 0;
        error = string.Empty;

        if (map is null)
        {
            error = "地图为空";
            return false;
        }

        if (textureIndex is null)
        {
            error = "贴图库索引为空";
            return false;
        }

        if (maxDim <= 0)
        {
            error = "maxDim 无效";
            return false;
        }

        if (!TryFindRepresentativeTextureRef(map, out int packageId, out int imageIndex))
        {
            error = "未找到可用于缩略图的贴图引用。";
            return false;
        }

        token.ThrowIfCancellationRequested();

        if (!textureIndex.TryDecodeImage(packageId, imageIndex, frame: 0, token, out DecodedImage decoded, out string decodeError))
        {
            error = string.IsNullOrWhiteSpace(decodeError) ? "解码贴图失败。" : decodeError;
            return false;
        }

        if (decoded is null || !decoded.IsValid || decoded.Rgba8 is null || decoded.Rgba8.Length == 0)
        {
            error = "解码贴图无效。";
            return false;
        }

        if (!TryDownscaleRgba8(decoded.Rgba8, decoded.Width, decoded.Height, maxDim, token, out rgba8, out width, out height, out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryFindRepresentativeTextureRef(MapDocument map, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (map is null)
        {
            return false;
        }

        int w = map.Width;
        int h = map.Height;
        if (w <= 0 || h <= 0 || map.Cells.Length == 0)
        {
            return false;
        }

        int cx = w / 2;
        int cy = h / 2;
        int maxR = Math.Max(w, h);
        int checkedCount = 0;
        int maxChecks = Math.Min(map.Cells.Length, 4096);

        for (int r = 0; r <= maxR && checkedCount < maxChecks; r++)
        {
            int x0 = cx - r;
            int x1 = cx + r;
            int y0 = cy - r;
            int y1 = cy + r;

            for (int x = x0; x <= x1 && checkedCount < maxChecks; x++)
            {
                if (TryPickFromCell(map, w, h, x, y0, ref packageId, ref imageIndex))
                {
                    return true;
                }
                checkedCount++;
            }

            for (int y = y0 + 1; y <= y1 && checkedCount < maxChecks; y++)
            {
                if (TryPickFromCell(map, w, h, x1, y, ref packageId, ref imageIndex))
                {
                    return true;
                }
                checkedCount++;
            }

            for (int x = x1 - 1; x >= x0 && checkedCount < maxChecks; x--)
            {
                if (TryPickFromCell(map, w, h, x, y1, ref packageId, ref imageIndex))
                {
                    return true;
                }
                checkedCount++;
            }

            for (int y = y1 - 1; y > y0 && checkedCount < maxChecks; y--)
            {
                if (TryPickFromCell(map, w, h, x0, y, ref packageId, ref imageIndex))
                {
                    return true;
                }
                checkedCount++;
            }
        }

        return false;
    }

    private static bool TryPickFromCell(MapDocument map, int w, int h, int x, int y, ref int packageId, ref int imageIndex)
    {
        if (x < 0 || y < 0 || x >= w || y >= h)
        {
            return false;
        }

        int index = y * w + x;
        if ((uint)index >= (uint)map.Cells.Length)
        {
            return false;
        }

        var cell = map.Cells[index];

        if (TryUnpackPacked24(cell.FrontImage, out packageId, out imageIndex))
        {
            return true;
        }

        if (TryUnpackPacked24(cell.UnderObject, out packageId, out imageIndex))
        {
            return true;
        }

        if (TryUnpackPacked24(cell.OverObject, out packageId, out imageIndex))
        {
            return true;
        }

        if (TryUnpackPacked24(cell.NearGround, out packageId, out imageIndex))
        {
            return true;
        }

        if (cell.MiddleImage != 0 && cell.MiddleLibrary != 0)
        {
            packageId = cell.MiddleLibrary;
            imageIndex = cell.MiddleImage;
            return true;
        }

        if (cell.BackImage != 0 && cell.BackLibrary != 0)
        {
            packageId = cell.BackLibrary;
            imageIndex = cell.BackImage;
            return true;
        }

        return false;
    }

    private static bool TryUnpackPacked24(uint packed, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        packed &= 0x00FFFFFFu;
        if (packed == 0)
        {
            return false;
        }

        packageId = (int)((packed >> 16) & 0xFF);
        imageIndex = (int)(packed & 0xFFFF);
        return packageId > 0 && imageIndex > 0;
    }

    private static bool TryDownscaleRgba8(
        ReadOnlySpan<byte> srcRgba8,
        int srcWidth,
        int srcHeight,
        int maxDim,
        CancellationToken token,
        out byte[] dstRgba8,
        out int dstWidth,
        out int dstHeight,
        out string error)
    {
        dstRgba8 = Array.Empty<byte>();
        dstWidth = 0;
        dstHeight = 0;
        error = string.Empty;

        if (srcWidth <= 0 || srcHeight <= 0)
        {
            error = "源贴图尺寸无效。";
            return false;
        }

        long expectedBytesLong = (long)srcWidth * srcHeight * 4;
        if (expectedBytesLong is <= 0 or > int.MaxValue)
        {
            error = "源贴图尺寸过大。";
            return false;
        }

        if (srcRgba8.Length < (int)expectedBytesLong)
        {
            error = "源贴图像素数据长度不足。";
            return false;
        }

        maxDim = Math.Clamp(maxDim, 16, 256);
        float scale = Math.Min(1.0f, Math.Min((float)maxDim / srcWidth, (float)maxDim / srcHeight));
        dstWidth = Math.Max(1, (int)MathF.Round(srcWidth * scale));
        dstHeight = Math.Max(1, (int)MathF.Round(srcHeight * scale));

        long outBytesLong = (long)dstWidth * dstHeight * 4;
        if (outBytesLong is <= 0 or > int.MaxValue)
        {
            error = "输出贴图尺寸过大。";
            return false;
        }

        dstRgba8 = new byte[(int)outBytesLong];

        for (int y = 0; y < dstHeight; y++)
        {
            if ((y & 7) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            int srcY = (int)(((y + 0.5f) / dstHeight) * srcHeight);
            srcY = Math.Clamp(srcY, 0, srcHeight - 1);

            for (int x = 0; x < dstWidth; x++)
            {
                int srcX = (int)(((x + 0.5f) / dstWidth) * srcWidth);
                srcX = Math.Clamp(srcX, 0, srcWidth - 1);

                int src = (srcY * srcWidth + srcX) * 4;
                int dst = (y * dstWidth + x) * 4;

                dstRgba8[dst + 0] = srcRgba8[src + 0];
                dstRgba8[dst + 1] = srcRgba8[src + 1];
                dstRgba8[dst + 2] = srcRgba8[src + 2];
                dstRgba8[dst + 3] = srcRgba8[src + 3];
            }
        }

        return true;
    }

    private void ProcessCompleted()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var finished = new List<string>(capacity: Math.Min(_pending.Count, 64));

        foreach ((string path, PendingThumbnail pending) in _pending)
        {
            if (!pending.Task.IsCompleted)
            {
                continue;
            }

            if (pending.Generation != _generation)
            {
                finished.Add(path);
                continue;
            }

            if (pending.Task.IsCanceled)
            {
                finished.Add(path);
                continue;
            }

            if (pending.Task.IsFaulted)
            {
                _errors[path] = pending.Task.Exception?.GetBaseException().Message ?? "解码任务异常。";
                finished.Add(path);
                continue;
            }

            if (_createdThisFrame >= CreateBudgetPerFrame)
            {
                continue;
            }

            DecodeResult result;
            try
            {
                result = pending.Task.Result;
            }
            catch (OperationCanceledException)
            {
                finished.Add(path);
                continue;
            }

            if (!result.Ok || result.Rgba8 is null || result.Rgba8.Length <= 0 || result.Width <= 0 || result.Height <= 0)
            {
                _errors[path] = string.IsNullOrWhiteSpace(result.Error) ? "生成缩略图失败。" : result.Error;
                finished.Add(path);
                continue;
            }

            if (!_renderer.TryCreateImGuiTextureRgba8(result.Rgba8, result.Width, result.Height, out nint textureId, out string createError))
            {
                _errors[path] = string.IsNullOrWhiteSpace(createError) ? "创建 GPU 纹理失败。" : createError;
                finished.Add(path);
                continue;
            }

            _createdThisFrame++;
            _cache[path] = new CachedThumbnail
            {
                TextureId = textureId,
                Width = result.Width,
                Height = result.Height,
                LastUsedFrame = _frameIndex
            };

            finished.Add(path);
        }

        for (int i = 0; i < finished.Count; i++)
        {
            _pending.Remove(finished[i]);
        }
    }

    private void PruneCacheIfNeeded()
    {
        int max = Math.Clamp(MaxCacheItems, 8, 2048);
        if (_cache.Count <= max)
        {
            return;
        }

        int removeCount = _cache.Count - max;
        if (removeCount <= 0)
        {
            return;
        }

        var list = new List<(string Path, ulong LastUsed)>(_cache.Count);
        foreach ((string path, CachedThumbnail thumb) in _cache)
        {
            list.Add((path, thumb.LastUsedFrame));
        }

        list.Sort(static (a, b) => a.LastUsed.CompareTo(b.LastUsed));

        for (int i = 0; i < removeCount && i < list.Count; i++)
        {
            string path = list[i].Path;
            if (_cache.TryGetValue(path, out CachedThumbnail? thumb))
            {
                if (thumb.TextureId != nint.Zero)
                {
                    _renderer.DestroyImGuiTexture(thumb.TextureId);
                }

                _cache.Remove(path);
            }
        }
    }

    private void CancelDecodeTasks()
    {
        try
        {
            _decodeCts.Cancel();
        }
        catch
        {
            // ignored
        }

        _decodeCts.Dispose();
        _decodeCts = new CancellationTokenSource();
    }

    public void Dispose()
    {
        try
        {
            _decodeCts.Cancel();
        }
        catch
        {
            // ignored
        }

        _decodeCts.Dispose();
        Clear();
    }

    private readonly record struct PendingThumbnail(int Generation, Task<DecodeResult> Task);

    private readonly record struct DecodeResult(bool Ok, byte[]? Rgba8, int Width, int Height, string Error)
    {
        public static DecodeResult Success(byte[] rgba8, int width, int height)
            => new(true, rgba8, width, height, string.Empty);

        public static DecodeResult Fail(string error)
            => new(false, null, 0, 0, error ?? string.Empty);
    }

    private sealed class CachedThumbnail
    {
        public nint TextureId { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public ulong LastUsedFrame { get; set; }

        public PrefabThumbnailInfo ToInfo() => new(TextureId, Width, Height);
    }
}
