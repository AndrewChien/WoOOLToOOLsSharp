using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Sgl;
using WoOOLToOOLsSharp.Shared.Formats.Tex;
using WoOOLToOOLsSharp.Shared.Formats.Wpf;

namespace WoOOLToOOLsSharp.MapEditor.App;

public sealed class MapTextureIndex : IDisposable
{
    private const string WpfSourcePrefix = "wpf:://";
    private const char WpfSourceSeparator = '|';
    private const int MaxReasonableAnimationFrames = 64;

    private readonly object _gate = new();
    private readonly Dictionary<int, string> _packageToStandaloneSglPath = new();
    private readonly Dictionary<int, string> _packageToWpfSglSource = new();
    private readonly Dictionary<int, WpfTexPackageIndex> _packageToWpfTex = new();
    private readonly Dictionary<long, int> _frameCountCache = new();
    private readonly Dictionary<int, byte[]?> _mskMaskBytesCache = new();
    private readonly Dictionary<int, SglLibrary> _openLibraries = new();
    private readonly Dictionary<int, string> _openFailures = new();
    private readonly Dictionary<string, WpfSglCacheEntry> _wpfSglCache = new(StringComparer.OrdinalIgnoreCase);

    public TextureSourceMode TextureSourceMode { get; set; } = TextureSourceMode.WpfSglFallback;
    public bool CoastMaskPreferTex { get; set; } = true;

    // --- Luminance-to-alpha settings (packages 46/47, objects18/19) ---
    private bool _skipLuminanceToAlpha;
    private LuminanceSettings _luminanceSettings = new();

    public bool SkipLuminanceToAlpha
    {
        get
        {
            lock (_gate)
            {
                return _skipLuminanceToAlpha;
            }
        }
        set
        {
            lock (_gate)
            {
                _skipLuminanceToAlpha = value;
            }
        }
    }

    public LuminanceSettings LuminanceSettings
    {
        get
        {
            lock (_gate)
            {
                return _luminanceSettings;
            }
        }
        set
        {
            lock (_gate)
            {
                _luminanceSettings = value;
            }
        }
    }

    internal void GetLuminanceToAlphaSettings(out bool skip, out LuminanceSettings settings)
    {
        lock (_gate)
        {
            skip = _skipLuminanceToAlpha;
            settings = _luminanceSettings;
        }
    }

    public string RootDirectory { get; private set; } = string.Empty;

    public int PackageCount
    {
        get
        {
            lock (_gate)
            {
                int count = _packageToStandaloneSglPath.Count;

                foreach (int packageId in _packageToWpfSglSource.Keys)
                {
                    if (!_packageToStandaloneSglPath.ContainsKey(packageId))
                    {
                        count++;
                    }
                }

                foreach (int packageId in _packageToWpfTex.Keys)
                {
                    if (!_packageToStandaloneSglPath.ContainsKey(packageId) && !_packageToWpfSglSource.ContainsKey(packageId))
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _packageToStandaloneSglPath.Count > 0 || _packageToWpfSglSource.Count > 0 || _packageToWpfTex.Count > 0;
            }
        }
    }

    public void ClearRuntimeCaches()
    {
        lock (_gate)
        {
            foreach (var kv in _openLibraries)
            {
                kv.Value.Dispose();
            }

            _openLibraries.Clear();
            _openFailures.Clear();
            _frameCountCache.Clear();
            _mskMaskBytesCache.Clear();
            _wpfSglCache.Clear();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            foreach (var kv in _openLibraries)
            {
                kv.Value.Dispose();
            }

            _openLibraries.Clear();
            _openFailures.Clear();
            _packageToStandaloneSglPath.Clear();
            _packageToWpfSglSource.Clear();
            _packageToWpfTex.Clear();
            _frameCountCache.Clear();
            _mskMaskBytesCache.Clear();
            _wpfSglCache.Clear();
            RootDirectory = string.Empty;
        }
    }

    public void ApplyIndex(
        string rootDirectory,
        Dictionary<int, string> packageToStandaloneSglPath,
        Dictionary<int, string> packageToWpfSglSource,
        Dictionary<int, WpfTexPackageIndex> packageToWpfTex)
    {
        if (packageToStandaloneSglPath is null) throw new ArgumentNullException(nameof(packageToStandaloneSglPath));
        if (packageToWpfSglSource is null) throw new ArgumentNullException(nameof(packageToWpfSglSource));
        if (packageToWpfTex is null) throw new ArgumentNullException(nameof(packageToWpfTex));

        Reset();

        lock (_gate)
        {
            RootDirectory = rootDirectory ?? string.Empty;

            foreach ((int packageId, string path) in packageToStandaloneSglPath)
            {
                if (packageId < 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                _packageToStandaloneSglPath[packageId] = path;
            }

            foreach ((int packageId, string path) in packageToWpfSglSource)
            {
                if (packageId < 0)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (_packageToWpfSglSource.ContainsKey(packageId))
                {
                    continue;
                }

                _packageToWpfSglSource[packageId] = path;
            }

            foreach ((int packageId, WpfTexPackageIndex pkg) in packageToWpfTex)
            {
                if (packageId < 0)
                {
                    continue;
                }

                if (pkg is null)
                {
                    continue;
                }

                if (_packageToWpfTex.ContainsKey(packageId))
                {
                    continue;
                }

                _packageToWpfTex[packageId] = pkg;
            }
        }
    }

    public bool TryGetLibrary(int packageId, out SglLibrary library, out string error)
    {
        library = null!;
        error = string.Empty;

        lock (_gate)
        {
            if (_openLibraries.TryGetValue(packageId, out library!))
            {
                return true;
            }

            if (_openFailures.TryGetValue(packageId, out string? cachedError) && !string.IsNullOrWhiteSpace(cachedError))
            {
                error = cachedError;
                return false;
            }

            string? path = string.Empty;
            bool isWpfSource = false;
            TextureSourceMode mode = TextureSourceMode;
            if (mode == TextureSourceMode.SglOnly)
            {
                if (!_packageToStandaloneSglPath.TryGetValue(packageId, out path) || string.IsNullOrWhiteSpace(path))
                {
                    error = $"TextureSourceMode=SglOnly：未找到 packageId={packageId} 的 standalone SGL。";
                    _openFailures[packageId] = error;
                    return false;
                }
            }
            else if (mode == TextureSourceMode.WpfOnly)
            {
                if (!_packageToWpfSglSource.TryGetValue(packageId, out path) || string.IsNullOrWhiteSpace(path))
                {
                    error = $"TextureSourceMode=WpfOnly：未找到 packageId={packageId} 的 WPF 内嵌 SGL。";
                    _openFailures[packageId] = error;
                    return false;
                }

                isWpfSource = true;
            }
            else
            {
                if (_packageToWpfSglSource.TryGetValue(packageId, out string? wpfSource) && !string.IsNullOrWhiteSpace(wpfSource))
                {
                    path = wpfSource;
                    isWpfSource = true;
                }
                else if (_packageToStandaloneSglPath.TryGetValue(packageId, out string? standalone) && !string.IsNullOrWhiteSpace(standalone))
                {
                    path = standalone;
                }
                else
                {
                    error = $"未找到 packageId={packageId} 的 SGL。";
                    _openFailures[packageId] = error;
                    return false;
                }
            }

            var lib = new SglLibrary();

            if (isWpfSource)
            {
                if (!TryParseWpfSourcePath(path!, out string wpfPath, out string entryPath))
                {
                    lib.Dispose();
                    error = $"WPF 内嵌 SGL 路径无效：{path}";
                    _openFailures[packageId] = error;
                    return false;
                }

                if (!TryExtractSglFromWpf(wpfPath, entryPath, out byte[] sglBytes, out error))
                {
                    lib.Dispose();
                    _openFailures[packageId] = error;
                    return false;
                }

                string label = BuildWpfSourcePath(wpfPath, entryPath);
                if (!lib.OpenFromMemory(sglBytes, label, out error))
                {
                    lib.Dispose();
                    _openFailures[packageId] = error;
                    return false;
                }
            }
            else if (!lib.Open(path!, out error))
            {
                lib.Dispose();
                _openFailures[packageId] = error;
                return false;
            }

            _openLibraries[packageId] = lib;
            library = lib;
            return true;
        }
    }

    public bool TryGetSglPath(int packageId, out string path)
    {
        path = string.Empty;

        lock (_gate)
        {
            TextureSourceMode mode = TextureSourceMode;
            if (mode == TextureSourceMode.SglOnly)
            {
                return _packageToStandaloneSglPath.TryGetValue(packageId, out path!);
            }

            if (mode == TextureSourceMode.WpfOnly)
            {
                return _packageToWpfSglSource.TryGetValue(packageId, out path!);
            }

            if (_packageToWpfSglSource.TryGetValue(packageId, out string? wpf) && !string.IsNullOrWhiteSpace(wpf))
            {
                path = wpf;
                return true;
            }

            return _packageToStandaloneSglPath.TryGetValue(packageId, out path!);
        }
    }

    /// <summary>
    /// Resolve an editor-bridge target for a (packageId, imageIndex) reference.
    /// Returns a path suitable for ContentEditor `OpenAsset` requests plus the image index to select.
    /// </summary>
    public bool TryGetImageBridgeTarget(int packageId, int imageIndex, out string path, out int selectedImageIndex, out string error)
    {
        path = string.Empty;
        selectedImageIndex = -1;
        error = string.Empty;

        if (packageId <= 0 || imageIndex <= 0)
        {
            error = "packageId 或 imageIndex 无效。";
            return false;
        }

        lock (_gate)
        {
            // Priority 1: WPF TEX package image -> open the .tex entry itself.
            TextureSourceMode mode = TextureSourceMode;
            if (mode != TextureSourceMode.SglOnly
                && _packageToWpfTex.TryGetValue(packageId, out WpfTexPackageIndex? texPkg)
                && texPkg is not null
                && texPkg.Images.TryGetValue(imageIndex, out WpfTexImageIndex? texImg)
                && texImg is not null)
            {
                string wpfPath = !string.IsNullOrWhiteSpace(texImg.WpfPath) ? texImg.WpfPath : texPkg.WpfPath;
                string entryPath = texImg.Entry?.FullPath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(wpfPath) || string.IsNullOrWhiteSpace(entryPath))
                {
                    error = "WPF TEX 索引条目不完整（WpfPath/EntryPath 为空）。";
                    return false;
                }

                path = BuildContentEditorWpfKey(wpfPath, entryPath);
                selectedImageIndex = 0;
                return !string.IsNullOrWhiteSpace(path);
            }

            // Priority 2: SGL package -> open the library container (SGL file or WPF:SGL entry).
            string rawPath = string.Empty;
            if (mode == TextureSourceMode.SglOnly)
            {
                if (!_packageToStandaloneSglPath.TryGetValue(packageId, out rawPath!) || string.IsNullOrWhiteSpace(rawPath))
                {
                    error = $"TextureSourceMode=SglOnly：未找到 packageId={packageId} 的 standalone SGL。";
                    return false;
                }
            }
            else if (mode == TextureSourceMode.WpfOnly)
            {
                if (!_packageToWpfSglSource.TryGetValue(packageId, out rawPath!) || string.IsNullOrWhiteSpace(rawPath))
                {
                    error = $"TextureSourceMode=WpfOnly：未找到 packageId={packageId} 的 WPF 内嵌 SGL。";
                    return false;
                }
            }
            else
            {
                if (_packageToWpfSglSource.TryGetValue(packageId, out string? wpf) && !string.IsNullOrWhiteSpace(wpf))
                {
                    rawPath = wpf;
                }
                else if (_packageToStandaloneSglPath.TryGetValue(packageId, out string? standalone) && !string.IsNullOrWhiteSpace(standalone))
                {
                    rawPath = standalone;
                }
                else
                {
                    error = $"未找到 packageId={packageId} 的贴图来源。";
                    return false;
                }
            }

            if (TryParseWpfSourcePath(rawPath, out string wpfPath2, out string entryPath2))
            {
                path = BuildContentEditorWpfKey(wpfPath2, entryPath2);
            }
            else
            {
                path = rawPath;
            }

            selectedImageIndex = imageIndex;
            return !string.IsNullOrWhiteSpace(path);
        }
    }

    public bool TryGetCachedFrameCount(int packageId, int imageIndex, out int frameCount)
    {
        frameCount = 1;

        if (packageId <= 0 || imageIndex <= 0)
        {
            return false;
        }

        long key = FrameCountKey(packageId, imageIndex);

        lock (_gate)
        {
            return _frameCountCache.TryGetValue(key, out frameCount);
        }
    }

    /// <summary>
    /// Lightweight existence check for debug overlays (does not decode image data).
    /// </summary>
    public bool HasImage(int packageId, int imageIndex)
    {
        if (packageId <= 0 || imageIndex <= 0)
        {
            return false;
        }

        TextureSourceMode sourceMode = TextureSourceMode;
        if (sourceMode != TextureSourceMode.SglOnly
            && TryGetWpfTexImage(packageId, imageIndex, out WpfTexImageIndex? tex, out _))
        {
            return tex is not null && tex.Entry.ByteSize > 0;
        }

        if (!TryGetLibrary(packageId, out var lib, out _))
        {
            return false;
        }

        SglImageEntry? entry = lib.GetEntry(imageIndex);
        return entry is not null && !entry.IsEmpty;
    }

    private void CacheFrameCount(int packageId, int imageIndex, int frameCount)
    {
        if (packageId <= 0 || imageIndex <= 0)
        {
            return;
        }

        long key = FrameCountKey(packageId, imageIndex);
        int normalized = NormalizeFrameCount(frameCount);

        lock (_gate)
        {
            _frameCountCache[key] = normalized;
        }
    }

    private static long FrameCountKey(int packageId, int imageIndex)
    {
        return ((long)packageId << 32) | (uint)imageIndex;
    }

    private static int NormalizeFrameCount(int frameCount)
    {
        if (frameCount <= 0)
        {
            return 1;
        }

        if (frameCount > MaxReasonableAnimationFrames)
        {
            return 1;
        }

        return frameCount;
    }

    public bool TryDecodeCoastCompositeImage(
        int packageId,
        int imageIndex,
        int maskImageIndex,
        int frame,
        CancellationToken token,
        out DecodedImage image,
        out string error)
    {
        image = null!;
        error = string.Empty;

        if (packageId <= 0 || imageIndex <= 0 || maskImageIndex <= 0)
        {
            error = "packageId/imageIndex/maskImageIndex 无效。";
            return false;
        }

        if (frame < 0)
        {
            frame = 0;
        }

        token.ThrowIfCancellationRequested();

        if (!TryDecodeImage(packageId, imageIndex, frame, token, out DecodedImage coast, out error))
        {
            return false;
        }

        token.ThrowIfCancellationRequested();

        bool preferTex = CoastMaskPreferTex;
        if (preferTex)
        {
            // 优先尝试 TEX mask（同 package 内的 maskImageIndex）。
            if (TryDecodeImage(packageId, maskImageIndex, frame: 0, token, out DecodedImage mask, out _))
            {
                image = ComposeCoastCompositeByMaskImage(coast, mask);
                if (image.IsValid)
                {
                    return true;
                }
            }

            // TEX mask 不存在/不可用时：按旧工程规则回退读取 `.msk`（优先 dataRoot/mask/...；若不存在则回退 WPF 内的 mask 条目）。
            if (!TryGetMskMaskBytes(maskImageIndex, token, out byte[] maskBits, out error))
            {
                return false;
            }

            image = ComposeCoastCompositeByMskBits(coast, maskBits);
            return image.IsValid;
        }

        // 优先使用 `.msk`（旧字段 coast_mask_source=msk）。
        string mskError = string.Empty;
        if (TryGetMskMaskBytes(maskImageIndex, token, out byte[] preferMskBits, out mskError))
        {
            image = ComposeCoastCompositeByMskBits(coast, preferMskBits);
            if (image.IsValid)
            {
                return true;
            }

            mskError = string.IsNullOrWhiteSpace(mskError) ? "使用 .msk 合成失败。" : mskError;
        }

        string texError = string.Empty;
        if (TryDecodeImage(packageId, maskImageIndex, frame: 0, token, out DecodedImage mask2, out texError))
        {
            image = ComposeCoastCompositeByMaskImage(coast, mask2);
            if (image.IsValid)
            {
                return true;
            }

            texError = string.IsNullOrWhiteSpace(texError) ? "使用 TEX mask 合成失败。" : texError;
        }

        if (string.IsNullOrWhiteSpace(mskError) && string.IsNullOrWhiteSpace(texError))
        {
            error = "未找到海岸遮罩。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(mskError))
        {
            error = $"TEX mask 不可用：{texError}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(texError))
        {
            error = $"MSK mask 不可用：{mskError}";
            return false;
        }

        error = $"MSK mask 不可用：{mskError}；TEX mask 不可用：{texError}";
        return false;
    }

    private bool TryGetMskMaskBytes(int maskImageIndex, CancellationToken token, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (maskImageIndex <= 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_mskMaskBytesCache.TryGetValue(maskImageIndex, out byte[]? cached))
            {
                if (cached is null || cached.Length == 0)
                {
                    return false;
                }

                bytes = cached;
                return true;
            }
        }

        token.ThrowIfCancellationRequested();

        if (!TryLoadMskMaskBytesFromDataRoot(RootDirectory, maskImageIndex, out bytes)
            && !TryLoadMskMaskBytesFromWpf(RootDirectory, maskImageIndex, out bytes))
        {
            lock (_gate)
            {
                _mskMaskBytesCache[maskImageIndex] = null;
            }

            bytes = Array.Empty<byte>();
            error = "未找到 .msk 海岸遮罩。";
            return false;
        }

        lock (_gate)
        {
            _mskMaskBytesCache[maskImageIndex] = bytes;
        }

        return bytes.Length > 0;
    }

    private static bool TryLoadMskMaskBytesFromDataRoot(string? dataRootDirectory, int maskImageIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(dataRootDirectory))
        {
            return false;
        }

        string root = dataRootDirectory;
        if (!Directory.Exists(root))
        {
            try
            {
                string? parent = Path.GetDirectoryName(root);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    root = parent;
                }
            }
            catch
            {
                return false;
            }
        }

        if (!Directory.Exists(root))
        {
            return false;
        }

        int folderId = maskImageIndex / 100;
        string folder = folderId.ToString("D3", CultureInfo.InvariantCulture);
        string fileStem = maskImageIndex.ToString("D5", CultureInfo.InvariantCulture);

        string pathUpper = Path.Combine(root, "mask", folder, $"{fileStem}.Msk");
        if (TryReadAllBytesSafe(pathUpper, out bytes))
        {
            return true;
        }

        string pathLower = Path.Combine(root, "mask", folder, $"{fileStem}.msk");
        if (TryReadAllBytesSafe(pathLower, out bytes))
        {
            return true;
        }

        return false;
    }

    private static bool TryLoadMskMaskBytesFromWpf(string? dataRootDirectory, int maskImageIndex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (maskImageIndex <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(dataRootDirectory))
        {
            return false;
        }

        string root = dataRootDirectory;
        if (!Directory.Exists(root))
        {
            try
            {
                string? parent = Path.GetDirectoryName(root);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    root = parent;
                }
            }
            catch
            {
                return false;
            }
        }

        if (!Directory.Exists(root))
        {
            return false;
        }

        string[] wpfPaths;
        try
        {
            wpfPaths = Directory.GetFiles(root, "*.wpf", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
            return false;
        }

        if (wpfPaths.Length == 0)
        {
            return false;
        }

        Array.Sort(wpfPaths, static (a, b) => CompareWpfPaths(a, b));

        int folderId = maskImageIndex / 100;
        string folder = folderId.ToString("D3", CultureInfo.InvariantCulture);
        string fileStem = maskImageIndex.ToString("D5", CultureInfo.InvariantCulture);

        // 兼容大小写：WPF 内路径按 OrdinalIgnoreCase 匹配；这里仍给出两套候选，便于不一致的归档内容。
        string relUpper = $"mask/{folder}/{fileStem}.Msk";
        string relLower = $"mask/{folder}/{fileStem}.msk";
        string relDataUpper = $"Data/mask/{folder}/{fileStem}.Msk";
        string relDataLower = $"Data/mask/{folder}/{fileStem}.msk";

        foreach (string wpf in wpfPaths)
        {
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relUpper, out bytes, out _))
            {
                return true;
            }
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relLower, out bytes, out _))
            {
                return true;
            }
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relDataUpper, out bytes, out _))
            {
                return true;
            }
            if (WpfMaskCache.TryExtractMaskByPath(wpf, relDataLower, out bytes, out _))
            {
                return true;
            }
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    private static bool TryReadAllBytesSafe(string path, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            return bytes.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static DecodedImage ComposeCoastCompositeByMaskImage(DecodedImage coast, DecodedImage mask)
    {
        if (coast is null || !coast.IsValid)
        {
            return new DecodedImage();
        }

        if (mask is null || !mask.IsValid)
        {
            return new DecodedImage();
        }

        int srcW = coast.Width;
        int srcH = coast.Height;
        int maskW = mask.Width;
        int maskH = mask.Height;
        if (srcW <= 0 || srcH <= 0 || maskW <= 0 || maskH <= 0)
        {
            return new DecodedImage();
        }

        bool sameDims = maskW == srcW && maskH == srcH;

        ReadOnlySpan<byte> c = coast.Rgba8;
        ReadOnlySpan<byte> m = mask.Rgba8;

        byte[] outRgba = new byte[srcW * srcH * 4];
        Span<byte> o = outRgba;

        for (int y = 0; y < srcH; y++)
        {
            int row = y * srcW * 4;

            int mySame = y;
            int myScaled = srcH > 0 ? (y * maskH / srcH) : 0;
            if (myScaled < 0) myScaled = 0;
            if (myScaled >= maskH) myScaled = maskH - 1;

            for (int x = 0; x < srcW; x++)
            {
                int cIdx = row + x * 4;

                byte sa0 = c[cIdx + 3];
                if (sa0 == 0)
                {
                    // 透明像素直接输出透明
                    continue;
                }

                int mx;
                int my;
                if (sameDims)
                {
                    mx = x;
                    my = mySame;
                }
                else
                {
                    mx = srcW > 0 ? (x * maskW / srcW) : 0;
                    my = myScaled;
                }

                if (mx < 0) mx = 0;
                if (mx >= maskW) mx = maskW - 1;

                int mIdx = (my * maskW + mx) * 4;
                byte mr = m[mIdx + 0];
                byte mg = m[mIdx + 1];
                byte mb = m[mIdx + 2];
                byte maskLum = (byte)((mr * 77 + mg * 150 + mb * 29) >> 8);

                // Invert mask: bright mask => coast transparent.
                int sa = (sa0 * (255 - maskLum) + 127) / 255;
                if (sa <= 0)
                {
                    continue;
                }

                o[cIdx + 0] = c[cIdx + 0];
                o[cIdx + 1] = c[cIdx + 1];
                o[cIdx + 2] = c[cIdx + 2];
                o[cIdx + 3] = (byte)sa;
            }
        }

        return new DecodedImage
        {
            Width = coast.Width,
            Height = coast.Height,
            OffsetX = coast.OffsetX,
            OffsetY = coast.OffsetY,
            CenterX = coast.CenterX,
            CenterY = coast.CenterY,
            Rgba8 = outRgba,
        };
    }

    private static DecodedImage ComposeCoastCompositeByMskBits(DecodedImage coast, byte[] maskBits)
    {
        if (coast is null || !coast.IsValid)
        {
            return new DecodedImage();
        }

        if (maskBits is null || maskBits.Length == 0)
        {
            return new DecodedImage();
        }

        int srcW = coast.Width;
        int srcH = coast.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            return new DecodedImage();
        }

        long bitCount = (long)srcW * srcH;
        if (bitCount <= 0)
        {
            return new DecodedImage();
        }

        long bytesNeeded = (bitCount + 7) / 8;
        if (maskBits.Length < bytesNeeded)
        {
            return new DecodedImage();
        }

        ReadOnlySpan<byte> c = coast.Rgba8;
        byte[] outRgba = new byte[srcW * srcH * 4];
        Span<byte> o = outRgba;

        for (int y = 0; y < srcH; y++)
        {
            int row = y * srcW * 4;
            for (int x = 0; x < srcW; x++)
            {
                int cIdx = row + x * 4;

                byte sa0 = c[cIdx + 3];
                if (sa0 == 0)
                {
                    continue;
                }

                long bitIndex = (long)y * srcW + x;
                int byteIndex = (int)(bitIndex / 8);
                int bitInByte = 7 - (int)(bitIndex % 8);
                bool on = ((maskBits[byteIndex] >> bitInByte) & 0x01) != 0;
                int maskLum = on ? 255 : 0;

                int sa = (sa0 * (255 - maskLum) + 127) / 255;
                if (sa <= 0)
                {
                    continue;
                }

                o[cIdx + 0] = c[cIdx + 0];
                o[cIdx + 1] = c[cIdx + 1];
                o[cIdx + 2] = c[cIdx + 2];
                o[cIdx + 3] = (byte)sa;
            }
        }

        return new DecodedImage
        {
            Width = coast.Width,
            Height = coast.Height,
            OffsetX = coast.OffsetX,
            OffsetY = coast.OffsetY,
            CenterX = coast.CenterX,
            CenterY = coast.CenterY,
            Rgba8 = outRgba,
        };
    }

    public bool TryDecodeImage(
        int packageId,
        int imageIndex,
        int frame,
        CancellationToken token,
        out DecodedImage image,
        out string error)
    {
        image = null!;
        error = string.Empty;

        if (packageId <= 0 || imageIndex <= 0)
        {
            error = "packageId 或 imageIndex 无效。";
            return false;
        }

        if (frame < 0)
        {
            frame = 0;
        }

        token.ThrowIfCancellationRequested();

        TextureSourceMode sourceMode = TextureSourceMode;
        if (sourceMode != TextureSourceMode.SglOnly
            && TryGetWpfTexImage(packageId, imageIndex, out WpfTexImageIndex? tex, out WpfTexPackageIndex? texPkg))
        {
            if (tex is null)
            {
                error = "WPF TEX 索引条目为空。";
                return false;
            }

            if (tex.Entry.ByteSize == 0)
            {
                error = BuildMissingWpfTexMessage(packageId, imageIndex, texPkg, tex);
                return false;
            }

            token.ThrowIfCancellationRequested();

            if (!WpfCodec.TryExtractEntryFromFile(tex.WpfPath, tex.Entry, out byte[] texBytes, out string extractError) || texBytes.Length == 0)
            {
                error = string.IsNullOrWhiteSpace(extractError)
                    ? BuildMissingWpfTexMessage(packageId, imageIndex, texPkg, tex)
                    : extractError;
                return false;
            }

            token.ThrowIfCancellationRequested();

            CacheFrameCount(packageId, imageIndex, TexCodec.GetFrameCount(texBytes));

            if (!TexCodec.TryDecodeRgba8(texBytes, out DecodedImage decoded, out string decodeError, frame))
            {
                error = string.IsNullOrWhiteSpace(decodeError) ? "解码 TEX 失败。" : decodeError;
                return false;
            }

            image = decoded;
            return image.IsValid;
        }

        token.ThrowIfCancellationRequested();

        if (!TryGetLibrary(packageId, out var lib, out error))
        {
            return false;
        }

        token.ThrowIfCancellationRequested();

        CacheFrameCount(packageId, imageIndex, lib.GetFrameCount(imageIndex));

        DecodedImage? img = frame == 0 ? lib.GetImage(imageIndex) : lib.GetImage(imageIndex, frame);
        if (img is null || !img.IsValid)
        {
            error = "解码 TEX 失败（图像为空或无效）。";
            return false;
        }

        image = img!;
        return true;
    }

    public bool TryDecodeImage(int packageId, int imageIndex, int frame, out DecodedImage image, out string error)
    {
        return TryDecodeImage(packageId, imageIndex, frame, CancellationToken.None, out image, out error);
    }

    public void Dispose()
    {
        Reset();
    }

    internal static bool IsLuminanceAlphaPackage(int packageId)
        => packageId is 46 or 47;

    public static TextureScanResult ScanSglDirectory(string rootDirectory, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return TextureScanResult.Fail("贴图库路径为空。");
        }

        if (!Directory.Exists(rootDirectory))
        {
            return TextureScanResult.Fail($"目录不存在：{rootDirectory}");
        }

        var standaloneSgl = new Dictionary<int, string>();
        var wpfSgl = new Dictionary<int, string>();
        var wpfTex = new Dictionary<int, WpfTexPackageIndex>();

        SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            foreach (string path in Directory.EnumerateFiles(rootDirectory, "*.sgl", searchOption))
            {
                int packageId = GetPackageIdFromFilename(Path.GetFileName(path));
                if (packageId < 0)
                {
                    continue;
                }

                if (!standaloneSgl.ContainsKey(packageId))
                {
                    standaloneSgl[packageId] = path;
                }
            }

            var wpfPaths = new List<string>(Directory.EnumerateFiles(rootDirectory, "*.wpf", searchOption));
            wpfPaths.Sort(static (a, b) => CompareWpfPaths(a, b));

            foreach (string wpfPath in wpfPaths)
            {
                string wpfHashPath = string.Empty;
                try
                {
                    string candidateHashPath = wpfPath + ".hash";
                    if (File.Exists(candidateHashPath))
                    {
                        wpfHashPath = candidateHashPath;
                    }
                }
                catch
                {
                    wpfHashPath = string.Empty;
                }

                if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> entries, out _))
                {
                    continue;
                }

                var packageRoots = new Dictionary<string, (int PackageId, string RootPath)>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < entries.Count; i++)
                {
                    WpfEntry e = entries[i];
                    if (e is null || !e.IsDirectory)
                    {
                        continue;
                    }

                    string entryId = GetWpfEntryId(e);
                    if (string.IsNullOrWhiteSpace(entryId))
                    {
                        continue;
                    }

                    if (entryId.IndexOfAny(new[] { '/', '\\' }) >= 0)
                    {
                        continue;
                    }

                    int packageId = GetPackageIdFromFilename(entryId);
                    if (packageId < 0)
                    {
                        continue;
                    }

                    if (!packageRoots.ContainsKey(entryId))
                    {
                        packageRoots[entryId] = (packageId, entryId);
                    }
                }

                var pendingTexPackages = new Dictionary<int, PendingWpfTexPackage>();

                for (int i = 0; i < entries.Count; i++)
                {
                    WpfEntry e = entries[i];
                    if (e is null || e.IsDirectory)
                    {
                        continue;
                    }

                    string entryId = GetWpfEntryId(e);
                    if (string.IsNullOrWhiteSpace(entryId))
                    {
                        continue;
                    }

                    string ext = StrPathExtension(entryId).ToLowerInvariant();

                    if (ext == ".sgl")
                    {
                        int packageId = GetPackageIdFromFilename(StrPathFilename(entryId));
                        if (packageId < 0)
                        {
                            continue;
                        }

                        if (!wpfSgl.ContainsKey(packageId))
                        {
                            wpfSgl[packageId] = BuildWpfSourcePath(wpfPath, entryId);
                        }

                        continue;
                    }

                    if (ext != ".tex")
                    {
                        continue;
                    }

                    string topLevel = GetTopLevelSegment(entryId).ToLowerInvariant();
                    if (!packageRoots.TryGetValue(topLevel, out (int PackageId, string RootPath) root))
                    {
                        continue;
                    }

                    int imageIndex = ParseNumericStemStr(StrPathFilename(entryId));
                    if (imageIndex < 0)
                    {
                        continue;
                    }

                    int packageIdFromRoot = root.PackageId;
                    if (!pendingTexPackages.TryGetValue(packageIdFromRoot, out PendingWpfTexPackage? pending))
                    {
                        pending = new PendingWpfTexPackage
                        {
                            RootPath = root.RootPath,
                            Images = new Dictionary<int, WpfTexImageIndex>(),
                            MaxImageIndex = -1,
                        };
                        pendingTexPackages[packageIdFromRoot] = pending;
                    }
                    else if (string.IsNullOrWhiteSpace(pending.RootPath))
                    {
                        pending.RootPath = root.RootPath;
                    }

                    pending.MaxImageIndex = Math.Max(pending.MaxImageIndex, imageIndex);

                    var img = new WpfTexImageIndex
                    {
                        WpfPath = wpfPath,
                        Entry = e,
                    };

                    if (!pending.Images.TryGetValue(imageIndex, out WpfTexImageIndex? existing)
                        || (existing.Entry.ByteSize == 0 && e.ByteSize > 0))
                    {
                        pending.Images[imageIndex] = img;
                    }
                }

                foreach ((int packageId, PendingWpfTexPackage pending) in pendingTexPackages)
                {
                    if (pending.Images.Count == 0)
                    {
                        continue;
                    }

                    if (wpfTex.TryGetValue(packageId, out WpfTexPackageIndex? pkg))
                    {
                        if (string.IsNullOrWhiteSpace(pkg.WpfHashPath) && !string.IsNullOrWhiteSpace(wpfHashPath))
                        {
                            pkg.WpfHashPath = wpfHashPath;
                        }

                        foreach ((int imageIndex, WpfTexImageIndex img) in pending.Images)
                        {
                            if (pkg.Images.TryGetValue(imageIndex, out WpfTexImageIndex? existImg))
                            {
                                if (existImg.Entry.ByteSize > 0 || img.Entry.ByteSize == 0)
                                {
                                    continue;
                                }

                                pkg.Images[imageIndex] = img;
                            }
                            else
                            {
                                pkg.Images[imageIndex] = img;
                            }

                            pkg.MaxImageIndex = Math.Max(pkg.MaxImageIndex, imageIndex);
                        }

                        continue;
                    }

                    wpfTex[packageId] = new WpfTexPackageIndex
                    {
                        WpfPath = wpfPath,
                        WpfHashPath = wpfHashPath,
                        RootPath = pending.RootPath ?? string.Empty,
                        Images = pending.Images,
                        MaxImageIndex = pending.MaxImageIndex,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TextureScanResult.Fail(ex.Message);
        }

        if (standaloneSgl.Count == 0 && wpfSgl.Count == 0 && wpfTex.Count == 0)
        {
            return TextureScanResult.Fail($"未在目录中发现可识别的贴图库（.sgl/.wpf）：{rootDirectory}");
        }

        return TextureScanResult.Success(rootDirectory, standaloneSgl, wpfSgl, wpfTex);
    }

    public static int GetPackageIdFromFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return -1;
        }

        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        }
        catch
        {
            return -1;
        }

        // SmTiles:
        // "smtiles"      -> 3001
        // "smtiles2-50"  -> 3002-3050
        if (stem == "smtiles") return 3001;
        if (stem.StartsWith("smtiles", StringComparison.Ordinal) && stem.Length > 7)
        {
            if (TryParseIntInvariant(stem.AsSpan(7), out int num) && num is >= 2 and <= 50)
            {
                return 3000 + num;
            }
        }

        // Tiles:
        // "tiles"        -> 3051
        // "tiles2-99"    -> 3052-3149
        if (stem == "tiles") return 3051;
        if (stem.StartsWith("tiles", StringComparison.Ordinal) && stem.Length > 5)
        {
            if (TryParseIntInvariant(stem.AsSpan(5), out int num) && num is >= 2 and <= 99)
            {
                return 3050 + num;
            }
        }

        // Objects:
        // "objects1-4"   -> 5-8
        // "objects5-19"  -> 33-47
        // "objects20-X"  -> 210-255 (clamped to stay within byte range)
        if (stem.StartsWith("objects", StringComparison.Ordinal) && stem.Length > 7)
        {
            if (TryParseIntInvariant(stem.AsSpan(7), out int num))
            {
                if (num is >= 1 and <= 4) return 4 + num;     // 5-8
                if (num is >= 5 and <= 19) return 28 + num;   // 33-47
                if (num >= 20)
                {
                    int id = 190 + num;                       // 210...
                    return id <= 255 ? id : -1;
                }
            }
        }

        // Effects / Misc:
        // "effect" -> 49
        // "others" -> 50
        if (stem == "effect") return 49;
        if (stem == "others") return 50;

        return -1;
    }

    private static int CompareWpfPaths(string a, string b)
    {
        int ap = GetWpfPriority(a);
        int bp = GetWpfPriority(b);

        bool aHas = ap >= 0;
        bool bHas = bp >= 0;
        if (aHas != bHas)
        {
            return aHas ? -1 : 1;
        }

        if (aHas && bHas && ap != bp)
        {
            // Prefer higher numbered archives first (Texture9 > Texture2)
            return bp.CompareTo(ap);
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetWpfPriority(string wpfPath)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(wpfPath).ToLowerInvariant();
            if (string.IsNullOrEmpty(stem))
            {
                return -1;
            }

            int start = stem.Length;
            while (start > 0 && char.IsDigit(stem[start - 1]))
            {
                start--;
            }

            if (start == stem.Length)
            {
                return -1;
            }

            return TryParseIntInvariant(stem.AsSpan(start), out int num) ? num : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string GetWpfEntryId(WpfEntry entry)
    {
        if (entry is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(entry.FullPath))
        {
            return entry.FullPath;
        }

        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            return entry.Name;
        }

        return entry.Index.ToString(CultureInfo.InvariantCulture);
    }

    private static string StrPathFilename(string p)
    {
        if (string.IsNullOrEmpty(p)) return string.Empty;

        int sep = p.LastIndexOfAny(new[] { '/', '\\' });
        return sep < 0 ? p : p.Substring(sep + 1);
    }

    private static string StrPathExtension(string p)
    {
        string fn = StrPathFilename(p);
        int dot = fn.LastIndexOf('.');
        return dot < 0 ? string.Empty : fn.Substring(dot);
    }

    private static string StrPathStem(string p)
    {
        string fn = StrPathFilename(p);
        int dot = fn.LastIndexOf('.');
        return dot < 0 ? fn : fn.Substring(0, dot);
    }

    private static int ParseNumericStemStr(string path)
    {
        string stem = StrPathStem(path);
        if (string.IsNullOrEmpty(stem))
        {
            return -1;
        }

        for (int i = 0; i < stem.Length; i++)
        {
            if (!char.IsDigit(stem[i]))
            {
                return -1;
            }
        }

        return TryParseIntInvariant(stem.AsSpan(), out int num) ? num : -1;
    }

    private static string GetTopLevelSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        int slash = path.IndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? path : path.Substring(0, slash);
    }

    private bool TryGetWpfTexImage(int packageId, int imageIndex, out WpfTexImageIndex? image, out WpfTexPackageIndex? pkg)
    {
        image = null;
        pkg = null;

        lock (_gate)
        {
            if (!_packageToWpfTex.TryGetValue(packageId, out pkg))
            {
                return false;
            }

            return pkg.Images.TryGetValue(imageIndex, out image);
        }
    }

    private string BuildMissingWpfTexMessage(int packageId, int imageIndex, WpfTexPackageIndex? pkg, WpfTexImageIndex? img)
    {
        if (!TryGetMissingWpfTexHashInfo(packageId, imageIndex, pkg, img, out string wpfHashFile, out long hash, out string path))
        {
            return "读取 WPF TEX 失败（可能未下载或索引不完整）。";
        }

        return $"读取 WPF TEX 失败（可能未下载）。hashFile={wpfHashFile} hash={FormatHash(hash)} path={path}";
    }

    private static string FormatHash(long hash)
    {
        return $"0x{unchecked((ulong)hash):X16}";
    }

    private bool TryGetMissingWpfTexHashInfo(
        int packageId,
        int imageIndex,
        WpfTexPackageIndex? pkg,
        WpfTexImageIndex? img,
        out string wpfHashFile,
        out long hash,
        out string path)
    {
        wpfHashFile = string.Empty;
        hash = 0;
        path = string.Empty;

        if (imageIndex <= 0)
        {
            return false;
        }

        WpfTexPackageIndex? realPkg = pkg;
        if (realPkg is null)
        {
            lock (_gate)
            {
                _packageToWpfTex.TryGetValue(packageId, out realPkg);
            }
        }

        if (realPkg is null)
        {
            return false;
        }

        // Tier 1/2: entry exists in FAT but has no data locally
        if (img is not null)
        {
            if (img.Entry.ByteSize > 0)
            {
                return false;
            }

            string sourcePath = !string.IsNullOrWhiteSpace(img.WpfPath) ? img.WpfPath : realPkg.WpfPath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return false;
            }

            wpfHashFile = Path.GetFileName(sourcePath) + ".hash";

            string hashPath = img.Entry.FullPath ?? string.Empty;
            if (hashPath.Length > 0)
            {
                hashPath = hashPath.Replace('/', '\\');
            }

            if (img.Entry.Hash != 0)
            {
                hash = img.Entry.Hash;
                path = hashPath;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(hashPath))
            {
                hash = WpfPathHash.Compute(hashPath);
                path = hashPath;
                return true;
            }

            return false;
        }

        // Tier 3: entry absent — synthesize path from rootPath + imageIndex
        if (string.IsNullOrWhiteSpace(realPkg.RootPath) || string.IsNullOrWhiteSpace(realPkg.WpfPath))
        {
            return false;
        }

        wpfHashFile = Path.GetFileName(realPkg.WpfPath) + ".hash";
        string subfolder = (imageIndex / 100).ToString("000", CultureInfo.InvariantCulture);
        string filename = imageIndex.ToString("00000", CultureInfo.InvariantCulture) + ".tex";
        string hashPathTier3 = $"{realPkg.RootPath}\\{subfolder}\\{filename}";
        hash = WpfPathHash.Compute(hashPathTier3);
        path = hashPathTier3;
        return true;
    }

    private sealed class PendingWpfTexPackage
    {
        public string RootPath { get; set; } = string.Empty;
        public Dictionary<int, WpfTexImageIndex> Images { get; set; } = new();
        public int MaxImageIndex { get; set; } = -1;
    }

    private static bool TryParseIntInvariant(ReadOnlySpan<char> s, out int value)
    {
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string BuildWpfSourcePath(string wpfPath, string entryPath)
    {
        return $"{WpfSourcePrefix}{wpfPath}{WpfSourceSeparator}{entryPath}";
    }

    private static string BuildContentEditorWpfKey(string wpfPath, string entryPath)
    {
        string archive = (wpfPath ?? string.Empty).Trim();
        string entry = (entryPath ?? string.Empty).Trim();

        entry = entry.Replace('\\', '/');
        while (entry.StartsWith("/", StringComparison.Ordinal))
        {
            entry = entry.Substring(1);
        }

        if (entry.Length == 0)
        {
            return archive;
        }

        // ContentEditor side accepts both "wpfPath::entry" and the legacy "wpfPath::/entry" (it normalizes).
        return $"{archive}::{entry}";
    }

    private static bool TryParseWpfSourcePath(string path, out string wpfPath, out string entryPath)
    {
        wpfPath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!path.StartsWith(WpfSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int sep = path.IndexOf(WpfSourceSeparator, WpfSourcePrefix.Length);
        if (sep <= 0 || sep + 1 >= path.Length)
        {
            return false;
        }

        wpfPath = path.Substring(WpfSourcePrefix.Length, sep - WpfSourcePrefix.Length);
        entryPath = path.Substring(sep + 1);
        return !string.IsNullOrWhiteSpace(wpfPath) && !string.IsNullOrWhiteSpace(entryPath);
    }

    private sealed class WpfSglCacheEntry
    {
        public DateTime WriteTimeUtc { get; set; }
        public long FileSize { get; set; }
        public Dictionary<string, WpfEntry> SglEntriesByPath { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasIndex { get; set; }
    }

    private bool TryExtractSglFromWpf(string wpfPath, string entryPath, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        if (!TryGetWpfSglEntry(wpfPath, entryPath, out WpfEntry entry, out error))
        {
            return false;
        }

        if (!WpfCodec.TryExtractEntryFromFile(wpfPath, entry, out bytes, out error))
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        return bytes.Length > 0;
    }

    private bool TryGetWpfSglEntry(string wpfPath, string entryPath, out WpfEntry entry, out string error)
    {
        entry = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(wpfPath) || string.IsNullOrWhiteSpace(entryPath))
        {
            error = "WPF 路径或 entryPath 为空。";
            return false;
        }

        if (!File.Exists(wpfPath))
        {
            error = $"WPF 文件不存在：{wpfPath}";
            return false;
        }

        DateTime writeTimeUtc;
        long fileSize;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(wpfPath);
            fileSize = new FileInfo(wpfPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取 WPF 文件信息失败：{wpfPath}\n{ex.Message}";
            return false;
        }

        string normalized = NormalizeEntryPath(entryPath);

        if (!_wpfSglCache.TryGetValue(wpfPath, out WpfSglCacheEntry? cached)
            || !cached.HasIndex
            || cached.WriteTimeUtc != writeTimeUtc
            || cached.FileSize != fileSize)
        {
            if (!WpfCodec.TryEnumerateEntriesFromFile(wpfPath, out List<WpfEntry> entries, out error))
            {
                _wpfSglCache.Remove(wpfPath);
                return false;
            }

            var dict = new Dictionary<string, WpfEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                WpfEntry e = entries[i];
                if (e is null || e.IsDirectory || e.ByteSize == 0)
                {
                    continue;
                }

                string full = NormalizeEntryPath(e.FullPath);
                if (!full.EndsWith(".sgl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!dict.ContainsKey(full))
                {
                    dict.Add(full, e);
                }
            }

            cached = new WpfSglCacheEntry
            {
                WriteTimeUtc = writeTimeUtc,
                FileSize = fileSize,
                SglEntriesByPath = dict,
                HasIndex = true,
            };

            _wpfSglCache[wpfPath] = cached;
        }

        if (!cached.SglEntriesByPath.TryGetValue(normalized, out entry!))
        {
            error = $"WPF 未找到 SGL entry：{entryPath}";
            return false;
        }

        return true;
    }

    private static string NormalizeEntryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string p = path.Replace('\\', '/').Trim();
        while (p.Length > 0)
        {
            if (p.StartsWith("/", StringComparison.Ordinal))
            {
                p = p.Substring(1);
                continue;
            }

            if (p.StartsWith("./", StringComparison.Ordinal))
            {
                p = p.Substring(2);
                continue;
            }

            if (p.StartsWith(".", StringComparison.Ordinal))
            {
                p = p.Substring(1);
                continue;
            }

            break;
        }

        return p;
    }
}

public sealed class TextureScanResult
{
    public bool Ok { get; private init; }
    public string Error { get; private init; } = string.Empty;
    public string RootDirectory { get; private init; } = string.Empty;
    public Dictionary<int, string> PackageToStandaloneSglPath { get; private init; } = new();
    public Dictionary<int, string> PackageToWpfSglSource { get; private init; } = new();
    public Dictionary<int, WpfTexPackageIndex> PackageToWpfTex { get; private init; } = new();

    public static TextureScanResult Fail(string error)
    {
        return new TextureScanResult
        {
            Ok = false,
            Error = error ?? string.Empty,
        };
    }

    public static TextureScanResult Success(
        string rootDirectory,
        Dictionary<int, string> packageToStandaloneSglPath,
        Dictionary<int, string> packageToWpfSglSource,
        Dictionary<int, WpfTexPackageIndex> packageToWpfTex)
    {
        return new TextureScanResult
        {
            Ok = true,
            Error = string.Empty,
            RootDirectory = rootDirectory ?? string.Empty,
            PackageToStandaloneSglPath = packageToStandaloneSglPath ?? new Dictionary<int, string>(),
            PackageToWpfSglSource = packageToWpfSglSource ?? new Dictionary<int, string>(),
            PackageToWpfTex = packageToWpfTex ?? new Dictionary<int, WpfTexPackageIndex>(),
        };
    }
}

public sealed class WpfTexImageIndex
{
    public string WpfPath { get; init; } = string.Empty;
    public WpfEntry Entry { get; init; } = new();
}

public sealed class WpfTexPackageIndex
{
    public string WpfPath { get; init; } = string.Empty;
    public string WpfHashPath { get; set; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public int MaxImageIndex { get; set; } = -1;
    public Dictionary<int, WpfTexImageIndex> Images { get; init; } = new();
}
