using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WoOOLToOOLsSharp.Rendering.Vulkan;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.MapEditor.App;

public readonly record struct LoadedTextureInfo(
    nint TextureId,
    int Width,
    int Height,
    short OffsetX,
    short OffsetY,
    short CenterX,
    short CenterY);

public readonly record struct TextureErrorInfo(int PackageId, int ImageIndex, int Frame, string Error);

public sealed class AsyncTextureLoader : IDisposable
{
    private const int ErrorHistoryMaxItems = 128;
    private const ulong RetryableErrorCooldownFrames = 30;
    private const int MaxRetryableErrorAttempts = 4;
    private const int LinearFilteringEdgeRepairPasses = 3;
    private const byte LinearFilteringLowAlphaRepairThreshold = 24;
    private const int LinearFilteringLowAlphaMaxCurrentBrightness = 32;
    private const int LinearFilteringLowAlphaMinBrightnessDelta = 72;

    private readonly VulkanRenderer _renderer;
    private readonly MapTextureIndex _index;

    private readonly Dictionary<TextureKey, LoadedTexture> _textures = new();
    private readonly Dictionary<TextureKey, PendingDecode> _pending = new();
    private readonly Dictionary<TextureKey, TextureFailureState> _errors = new();
    private readonly List<TextureErrorInfo> _recentErrors = new();

    private CancellationTokenSource _decodeCts = new();

    private ulong _frameIndex;
    private int _generation;
    private int _submittedThisFrame;

    private int _submittedTotal;
    private int _createdTotal;
    private int _failedTotal;
    private int _canceledTotal;

    public int MaxCacheItems { get; set; } = 32768;
    public int SubmitBudgetPerFrame { get; set; } = 2048;
    public int CreateBudgetPerFrame { get; set; } = 256;

    public int CachedCount => _textures.Count;
    public int PendingCount => _pending.Count;
    public int ErrorCount => _errors.Count;

    public int SubmittedTotal => _submittedTotal;
    public int CreatedTotal => _createdTotal;
    public int FailedTotal => _failedTotal;
    public int CanceledTotal => _canceledTotal;
    public Action<MapEditorConsoleLogLevel, string>? LogSink { get; set; }

    public AsyncTextureLoader(VulkanRenderer renderer, MapTextureIndex index)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public void TickFrame()
    {
        _frameIndex++;
        _submittedThisFrame = 0;

        ProcessCompletedDecodes();
        PruneCacheIfNeeded();
    }

    public void InvalidateAll()
    {
        Log(MapEditorConsoleLogLevel.Info, $"纹理缓存失效：cached={_textures.Count} pending={_pending.Count} errors={_errors.Count}");
        _generation++;
        CancelDecodeTasks();
        Clear();
    }

    public void Clear()
    {
        ResetStats();

        foreach ((_, LoadedTexture tex) in _textures)
        {
            if (tex.TextureId != nint.Zero)
            {
                _renderer.DestroyImGuiTexture(tex.TextureId);
            }
        }

        _textures.Clear();
        _pending.Clear();
        _errors.Clear();
        _recentErrors.Clear();
    }

    public void ResetStats()
    {
        _submittedTotal = 0;
        _createdTotal = 0;
        _failedTotal = 0;
        _canceledTotal = 0;
    }

    public void CancelPendingDecodes()
    {
        if (_pending.Count > 0)
        {
            Log(MapEditorConsoleLogLevel.Info, $"取消待处理纹理解码：pending={_pending.Count}");
        }

        _generation++;
        CancelDecodeTasks();
        _pending.Clear();
        _submittedThisFrame = 0;
    }

    public void ClearErrors()
    {
        if (_errors.Count > 0)
        {
            Log(MapEditorConsoleLogLevel.Info, $"清空纹理错误：count={_errors.Count}");
        }

        _errors.Clear();
        _recentErrors.Clear();
    }

    public TextureErrorInfo[] GetRecentErrors(int maxItems = 32)
    {
        if (maxItems <= 0 || _recentErrors.Count == 0)
        {
            return Array.Empty<TextureErrorInfo>();
        }

        int take = Math.Min(maxItems, _recentErrors.Count);
        var result = new TextureErrorInfo[take];
        int start = _recentErrors.Count - take;
        for (int i = 0; i < take; i++)
        {
            result[i] = _recentErrors[start + i];
        }

        return result;
    }

    public bool TryGetTexture(int packageId, int imageIndex, int frame, out LoadedTextureInfo info)
    {
        info = default;

        if (packageId <= 0 || imageIndex <= 0)
        {
            return false;
        }

        if (frame < 0)
        {
            frame = 0;
        }

        var key = new TextureKey(TextureKind.Normal, packageId, imageIndex, 0, frame);

        if (_textures.TryGetValue(key, out LoadedTexture? cached))
        {
            cached.LastUsedFrame = _frameIndex;
            info = cached.ToInfo();
            return cached.TextureId != nint.Zero;
        }

        if (_pending.ContainsKey(key))
        {
            return false;
        }

        if (_errors.TryGetValue(key, out TextureFailureState failure))
        {
            if (!ShouldRetryFailure(failure))
            {
                return false;
            }

            _errors.Remove(key);
            Log(MapEditorConsoleLogLevel.Info, $"重试纹理：pkg={packageId} img={imageIndex} frame={frame} attempts={failure.Attempts + 1}");
        }

        if (_submittedThisFrame >= SubmitBudgetPerFrame)
        {
            return false;
        }

        _submittedThisFrame++;
        _submittedTotal++;
        int gen = _generation;
        CancellationToken token = _decodeCts.Token;
        Task<DecodeResult> task = Task.Run(() => DecodeOnWorker(key, token), token);
        _pending[key] = new PendingDecode(gen, task);
        return false;
    }

    public bool TryGetCoastCompositeTexture(int packageId, int imageIndex, int maskImageIndex, int frame, out LoadedTextureInfo info)
    {
        info = default;

        if (packageId <= 0 || imageIndex <= 0 || maskImageIndex <= 0)
        {
            return false;
        }

        if (frame < 0)
        {
            frame = 0;
        }

        var key = new TextureKey(TextureKind.CoastComposite, packageId, imageIndex, maskImageIndex, frame);

        if (_textures.TryGetValue(key, out LoadedTexture? cached))
        {
            cached.LastUsedFrame = _frameIndex;
            info = cached.ToInfo();
            return cached.TextureId != nint.Zero;
        }

        if (_pending.ContainsKey(key))
        {
            return false;
        }

        if (_errors.TryGetValue(key, out TextureFailureState failure))
        {
            if (!ShouldRetryFailure(failure))
            {
                return false;
            }

            _errors.Remove(key);
            Log(MapEditorConsoleLogLevel.Info, $"重试海岸合成纹理：pkg={packageId} img={imageIndex} mask={maskImageIndex} frame={frame} attempts={failure.Attempts + 1}");
        }

        if (_submittedThisFrame >= SubmitBudgetPerFrame)
        {
            return false;
        }

        _submittedThisFrame++;
        _submittedTotal++;
        int gen = _generation;
        CancellationToken token = _decodeCts.Token;
        Task<DecodeResult> task = Task.Run(() => DecodeOnWorker(key, token), token);
        _pending[key] = new PendingDecode(gen, task);
        return false;
    }

    public bool TryGetError(int packageId, int imageIndex, int frame, out string error)
    {
        error = string.Empty;
        if (packageId <= 0 || imageIndex <= 0) return false;

        var key = new TextureKey(TextureKind.Normal, packageId, imageIndex, 0, Math.Max(0, frame));
        if (_errors.TryGetValue(key, out TextureFailureState failure))
        {
            error = failure.Error;
            return true;
        }

        return false;
    }

    private DecodeResult DecodeOnWorker(TextureKey key, CancellationToken token)
    {
        try
        {
            if (key.Kind == TextureKind.CoastComposite)
            {
                if (!_index.TryDecodeCoastCompositeImage(key.PackageId, key.ImageIndex, key.MaskImageIndex, key.Frame, token, out DecodedImage img, out string error))
                {
                    return DecodeResult.Fail($"mask={key.MaskImageIndex}: {error}");
                }

                return DecodeResult.Success(img);
            }

            if (!_index.TryDecodeImage(key.PackageId, key.ImageIndex, key.Frame, token, out DecodedImage normal, out string normalError))
            {
                return DecodeResult.Fail(normalError);
            }

            if (MapTextureIndex.IsLuminanceAlphaPackage(key.PackageId))
            {
                _index.GetLuminanceToAlphaSettings(out bool skip, out LuminanceSettings settings);
                if (!skip)
                {
                    normal = ApplyLuminanceToAlpha(normal, settings);
                }
            }

            return DecodeResult.Success(normal);
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

    private static DecodedImage ApplyLuminanceToAlpha(DecodedImage decoded, LuminanceSettings settings)
    {
        if (!decoded.IsValid)
        {
            return decoded;
        }

        byte[] rgba = decoded.Rgba8;
        var outRgba = new byte[rgba.Length];
        Buffer.BlockCopy(rgba, 0, outRgba, 0, rgba.Length);

        for (int i = 0; i + 3 < outRgba.Length; i += 4)
        {
            byte sr = outRgba[i + 0];
            byte sg = outRgba[i + 1];
            byte sb = outRgba[i + 2];
            byte sa = outRgba[i + 3];

            byte lum = LuminanceProcessor.CalculateLuminance(sr, sg, sb, settings.Mode);
            byte adj = LuminanceProcessor.ApplyLuminanceAdjustments(lum, settings);
            outRgba[i + 3] = LuminanceProcessor.BlendAlpha(adj, sa, settings.BlendMode);
        }

        return new DecodedImage
        {
            Width = decoded.Width,
            Height = decoded.Height,
            OffsetX = decoded.OffsetX,
            OffsetY = decoded.OffsetY,
            CenterX = decoded.CenterX,
            CenterY = decoded.CenterY,
            Rgba8 = outRgba,
        };
    }

    private static DecodedImage BleedTransparentEdgeColors(DecodedImage decoded)
    {
        if (!decoded.IsValid || decoded.Width <= 1 || decoded.Height <= 1)
        {
            return decoded;
        }

        byte[] rgba = decoded.Rgba8;
        int width = decoded.Width;
        int height = decoded.Height;
        if (rgba is null || rgba.Length != width * height * 4)
        {
            return decoded;
        }

        byte[] current = new byte[rgba.Length];
        Buffer.BlockCopy(rgba, 0, current, 0, rgba.Length);

        bool changedAny = false;
        for (int pass = 0; pass < LinearFilteringEdgeRepairPasses; pass++)
        {
            bool allowLowAlphaRepair = pass == 0;
            byte[] next = new byte[current.Length];
            Buffer.BlockCopy(current, 0, next, 0, current.Length);

            bool changedPass = false;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int di = (y * width + x) * 4;
                    byte alpha = current[di + 3];
                    if (alpha == byte.MaxValue)
                    {
                        continue;
                    }

                    int sumR = 0;
                    int sumG = 0;
                    int sumB = 0;
                    int weightSum = 0;

                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int ny = y + oy;
                        if ((uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int nx = x + ox;
                            if ((ox == 0 && oy == 0) || (uint)nx >= (uint)width)
                            {
                                continue;
                            }

                            int si = (ny * width + nx) * 4;
                            byte neighborAlpha = current[si + 3];
                            bool hasUsefulNeighborColor =
                                neighborAlpha != 0
                                || current[si + 0] != 0
                                || current[si + 1] != 0
                                || current[si + 2] != 0;
                            if (!hasUsefulNeighborColor)
                            {
                                continue;
                            }

                            if (alpha != 0 && neighborAlpha <= alpha)
                            {
                                continue;
                            }

                            int weight = neighborAlpha != 0 ? neighborAlpha : 1;
                            sumR += current[si + 0] * weight;
                            sumG += current[si + 1] * weight;
                            sumB += current[si + 2] * weight;
                            weightSum += weight;
                        }
                    }

                    if (weightSum == 0)
                    {
                        continue;
                    }

                    byte avgR = (byte)(sumR / weightSum);
                    byte avgG = (byte)(sumG / weightSum);
                    byte avgB = (byte)(sumB / weightSum);

                    bool shouldRepair = alpha == 0;
                    if (!shouldRepair && allowLowAlphaRepair)
                    {
                        shouldRepair = ShouldRepairLowAlphaEdgePixel(
                            current[di + 0],
                            current[di + 1],
                            current[di + 2],
                            alpha,
                            avgR,
                            avgG,
                            avgB);
                    }
                    if (!shouldRepair)
                    {
                        continue;
                    }

                    if (next[di + 0] == avgR && next[di + 1] == avgG && next[di + 2] == avgB)
                    {
                        continue;
                    }

                    next[di + 0] = avgR;
                    next[di + 1] = avgG;
                    next[di + 2] = avgB;
                    changedPass = true;
                }
            }

            if (!changedPass)
            {
                break;
            }

            changedAny = true;
            current = next;
        }

        if (!changedAny)
        {
            return decoded;
        }

        return new DecodedImage
        {
            Width = decoded.Width,
            Height = decoded.Height,
            OffsetX = decoded.OffsetX,
            OffsetY = decoded.OffsetY,
            CenterX = decoded.CenterX,
            CenterY = decoded.CenterY,
            Rgba8 = current,
        };
    }

    private static bool ShouldRepairLowAlphaEdgePixel(
        byte r,
        byte g,
        byte b,
        byte alpha,
        byte avgR,
        byte avgG,
        byte avgB)
    {
        if (alpha == 0 || alpha >= LinearFilteringLowAlphaRepairThreshold)
        {
            return false;
        }

        int currentMax = Math.Max(r, Math.Max(g, b));
        if (currentMax > LinearFilteringLowAlphaMaxCurrentBrightness)
        {
            return false;
        }

        int averageMax = Math.Max(avgR, Math.Max(avgG, avgB));
        if (averageMax - currentMax < LinearFilteringLowAlphaMinBrightnessDelta)
        {
            return false;
        }

        return averageMax >= 64;
    }

    private void ProcessCompletedDecodes()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        int budget = Math.Max(1, CreateBudgetPerFrame);
        List<TextureKey>? done = null;

        foreach ((TextureKey key, PendingDecode pending) in _pending)
        {
            if (!pending.Task.IsCompleted)
            {
                continue;
            }

            done ??= new List<TextureKey>(capacity: Math.Min(_pending.Count, budget));
            done.Add(key);

            if (done.Count >= budget)
            {
                break;
            }
        }

        if (done is null)
        {
            return;
        }

        for (int i = 0; i < done.Count; i++)
        {
            TextureKey key = done[i];
            if (!_pending.TryGetValue(key, out PendingDecode pending))
            {
                continue;
            }

            _pending.Remove(key);

            if (pending.Generation != _generation)
            {
                continue;
            }

            if (pending.Task.IsCanceled)
            {
                _canceledTotal++;
                continue;
            }

            if (pending.Task.IsFaulted)
            {
                AddError(key, pending.Task.Exception?.GetBaseException().Message ?? "解码任务异常。");
                continue;
            }

            DecodeResult result;
            try
            {
                result = pending.Task.Result;
            }
            catch (OperationCanceledException)
            {
                _canceledTotal++;
                continue;
            }

            if (!result.Ok || result.Image is null)
            {
                AddError(key, string.IsNullOrWhiteSpace(result.Error) ? "解码失败。" : result.Error);
                continue;
            }

            DecodedImage img = result.Image;
            if (!_renderer.TryCreateImGuiTextureRgba8(img.Rgba8, img.Width, img.Height, out nint textureId, out string createError))
            {
                AddError(key, string.IsNullOrWhiteSpace(createError) ? "创建 GPU 纹理失败。" : createError);
                continue;
            }

            _createdTotal++;
            _textures[key] = new LoadedTexture
            {
                TextureId = textureId,
                Width = img.Width,
                Height = img.Height,
                OffsetX = img.OffsetX,
                OffsetY = img.OffsetY,
                CenterX = img.CenterX,
                CenterY = img.CenterY,
                LastUsedFrame = _frameIndex,
            };
        }
    }

    private void PruneCacheIfNeeded()
    {
        int max = Math.Clamp(MaxCacheItems, 16, 32768);
        if (_textures.Count <= max)
        {
            return;
        }

        int removeCount = _textures.Count - max;
        if (removeCount <= 0)
        {
            return;
        }

        var list = new List<(TextureKey Key, ulong LastUsed)>(_textures.Count);
        foreach ((TextureKey key, LoadedTexture tex) in _textures)
        {
            list.Add((key, tex.LastUsedFrame));
        }

        list.Sort(static (a, b) => a.LastUsed.CompareTo(b.LastUsed));

        int removed = 0;
        for (int i = 0; i < removeCount && i < list.Count; i++)
        {
            TextureKey key = list[i].Key;
            if (_textures.TryGetValue(key, out LoadedTexture? tex))
            {
                if (tex.TextureId != nint.Zero)
                {
                    _renderer.DestroyImGuiTexture(tex.TextureId);
                }

                _textures.Remove(key);
                removed++;
            }
        }

        if (removed > 0)
        {
            Log(MapEditorConsoleLogLevel.Info, $"GPU 纹理缓存淘汰：removed={removed} remaining={_textures.Count} max={max}");
        }
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

    private enum TextureKind
    {
        Normal = 0,
        CoastComposite = 1,
    }

    private readonly record struct TextureKey(TextureKind Kind, int PackageId, int ImageIndex, int MaskImageIndex, int Frame);

    private sealed class LoadedTexture
    {
        public nint TextureId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public short OffsetX { get; set; }
        public short OffsetY { get; set; }
        public short CenterX { get; set; }
        public short CenterY { get; set; }
        public ulong LastUsedFrame { get; set; }

        public LoadedTextureInfo ToInfo()
        {
            return new LoadedTextureInfo(TextureId, Width, Height, OffsetX, OffsetY, CenterX, CenterY);
        }
    }

    private readonly record struct PendingDecode(int Generation, Task<DecodeResult> Task);

    private readonly record struct DecodeResult(bool Ok, string Error, DecodedImage? Image)
    {
        public static DecodeResult Success(DecodedImage image) => new(true, string.Empty, image);
        public static DecodeResult Fail(string error) => new(false, error ?? string.Empty, null);
    }

    private readonly record struct TextureFailureState(string Error, int Attempts, ulong RetryAfterFrame, bool Retryable);

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

        try
        {
            _decodeCts.Dispose();
        }
        catch
        {
            // ignored
        }

        _decodeCts = new CancellationTokenSource();
    }

    private void AddError(TextureKey key, string error)
    {
        error ??= string.Empty;
        bool retryable = IsRetryableError(error);
        int attempts = 1;
        if (_errors.TryGetValue(key, out TextureFailureState existing))
        {
            attempts = existing.Attempts + 1;
        }

        bool canRetry = retryable && attempts < MaxRetryableErrorAttempts;
        ulong retryAfterFrame = canRetry ? _frameIndex + RetryableErrorCooldownFrames : ulong.MaxValue;
        _errors[key] = new TextureFailureState(error, attempts, retryAfterFrame, canRetry);
        _failedTotal++;

        _recentErrors.Add(new TextureErrorInfo(key.PackageId, key.ImageIndex, key.Frame, error));
        if (_recentErrors.Count > ErrorHistoryMaxItems)
        {
            int removeCount = _recentErrors.Count - ErrorHistoryMaxItems;
            _recentErrors.RemoveRange(0, removeCount);
        }

        string trimmed = TrimErrorForLog(error);
        string retryText = canRetry ? $" retryInFrames={RetryableErrorCooldownFrames} attempts={attempts}/{MaxRetryableErrorAttempts}" : $" attempts={attempts}";
        Log(canRetry ? MapEditorConsoleLogLevel.Warning : MapEditorConsoleLogLevel.Error,
            $"纹理失败：pkg={key.PackageId} img={key.ImageIndex} frame={key.Frame}{retryText} err={trimmed}");
    }

    private bool ShouldRetryFailure(TextureFailureState failure)
    {
        if (!failure.Retryable)
        {
            return false;
        }

        if (failure.Attempts >= MaxRetryableErrorAttempts)
        {
            return false;
        }

        return _frameIndex >= failure.RetryAfterFrame;
    }

    private static bool IsRetryableError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return true;
        }

        return error.Contains("创建 GPU 纹理失败", StringComparison.Ordinal)
            || error.Contains("解码任务异常", StringComparison.Ordinal)
            || error.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Descriptor", StringComparison.OrdinalIgnoreCase)
            || error.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase)
            || error.Contains("DeviceLost", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimErrorForLog(string error)
    {
        string text = error?.Trim() ?? string.Empty;
        if (text.Length <= 240)
        {
            return text;
        }

        return text.Substring(0, 240) + "...";
    }

    private void Log(MapEditorConsoleLogLevel level, string message)
    {
        LogSink?.Invoke(level, message ?? string.Empty);
    }
}
