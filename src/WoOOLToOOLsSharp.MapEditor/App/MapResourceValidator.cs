using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WoOOLToOOLsSharp.Shared;
using WoOOLToOOLsSharp.Shared.Formats.Nmp;

namespace WoOOLToOOLsSharp.MapEditor.App;

public sealed class MapResourceValidationOptions
{
    public bool ValidateCoastComposite { get; init; } = true;
    public int MaxSamplesPerIssue { get; init; } = 8;
}

public sealed class MapResourceValidationReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public string DocumentPath { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int UniqueImageRefs { get; init; }
    public int UniqueCoastCompositeRefs { get; init; }
    public List<MapResourceValidationIssue> Issues { get; init; } = new();
}

public enum MapResourceIssueKind
{
    DecodeFailed,
    CoastCompositeInvalid,
    CoastCompositeFailed,
}

public readonly record struct MapResourceValidationSample(int X, int Y);

public sealed class MapResourceValidationIssue
{
    public MapResourceIssueKind Kind { get; init; }
    public int PackageId { get; init; }
    public int ImageIndex { get; init; }
    public int MaskImageIndex { get; init; }
    public int Occurrences { get; init; }
    public string Layers { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public List<MapResourceValidationSample> Samples { get; init; } = new();
}

public static class MapResourceValidator
{
    private const ushort DefaultSmTilesLibrary = 3001;
    private const ushort DefaultTilesLibrary = 3051;
    private const byte DefaultObjectLibrary = 5;
    private const int DefaultCoastLibrary = 49;

    [Flags]
    private enum LayerMask
    {
        None = 0,
        Back = 1 << 0,
        Middle = 1 << 1,
        Middle2 = 1 << 2,
        Front = 1 << 3,
        UnderObject = 1 << 4,
        OverObject = 1 << 5,
        NearGround = 1 << 6,
        CoastComposite = 1 << 7,
    }

    private readonly record struct ImageKey(int PackageId, int ImageIndex);
    private readonly record struct CoastKey(int PackageId, int CoastImageIndex, int MaskImageIndex);

    private sealed class RefStats
    {
        public int Occurrences;
        public LayerMask Layers;
        public List<MapResourceValidationSample> Samples { get; } = new();
    }

    public static MapResourceValidationReport Validate(
        MapDocument map,
        MapTextureIndex textureIndex,
        MapResourceValidationOptions options,
        CancellationToken token)
    {
        if (map is null) throw new ArgumentNullException(nameof(map));
        if (textureIndex is null) throw new ArgumentNullException(nameof(textureIndex));
        if (options is null) throw new ArgumentNullException(nameof(options));

        var imageRefs = new Dictionary<ImageKey, RefStats>();
        var coastRefs = new Dictionary<CoastKey, RefStats>();

        int width = map.Width;
        int height = map.Height;

        for (int index = 0; index < map.Cells.Length; index++)
        {
            token.ThrowIfCancellationRequested();

            int x = width > 0 ? index % width : 0;
            int y = width > 0 ? index / width : 0;
            NmpCellData cell = map.Cells[index];

            int backImage = cell.BackImage;
            if (backImage > 0)
            {
                int backPkg = cell.BackLibrary != 0 ? cell.BackLibrary : DefaultSmTilesLibrary;
                AddImage(imageRefs, backPkg, backImage, LayerMask.Back, x, y, options.MaxSamplesPerIssue);
            }

            if (cell.MiddleImage2 != 0)
            {
                int groundIdx = cell.MiddleImage2;
                if (groundIdx > 0)
                {
                    int groundPkg = cell.MiddleLibrary2 != 0 ? cell.MiddleLibrary2 : DefaultTilesLibrary;
                    AddImage(imageRefs, groundPkg, groundIdx, LayerMask.Middle2, x, y, options.MaxSamplesPerIssue);
                }

                int coastIdx = cell.MiddleImage;
                int maskIdx = cell.MiddleAlphaMask;
                int coastPkg = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultCoastLibrary;

                if (coastIdx > 0 && maskIdx > 0)
                {
                    AddCoast(coastRefs, coastPkg, coastIdx, maskIdx, x, y, options.MaxSamplesPerIssue);
                }
                else if (coastIdx > 0 || maskIdx > 0)
                {
                    // Coast cell with incomplete params: report as invalid composite ref.
                    AddCoast(coastRefs, coastPkg, coastIdx, maskIdx, x, y, options.MaxSamplesPerIssue);
                }
            }
            else
            {
                int middleImage = cell.MiddleImage;
                if (middleImage > 0)
                {
                    int middlePkg = cell.MiddleLibrary != 0 ? cell.MiddleLibrary : DefaultTilesLibrary;
                    AddImage(imageRefs, middlePkg, middleImage, LayerMask.Middle, x, y, options.MaxSamplesPerIssue);
                }
            }

            int frontImage = (int)(cell.FrontImage & 0xFFFF);
            if (frontImage > 0)
            {
                int frontPkg = (int)((cell.FrontImage >> 16) & 0xFF);
                if (frontPkg == 0)
                {
                    frontPkg = cell.FrontLibrary;
                }

                if (frontPkg == 0)
                {
                    frontPkg = DefaultObjectLibrary;
                }

                AddImage(imageRefs, frontPkg, frontImage, LayerMask.Front, x, y, options.MaxSamplesPerIssue);
            }

            if (TryResolvePackedObject(cell.UnderObject, out int underPkg, out int underImg))
            {
                AddImage(imageRefs, underPkg, underImg, LayerMask.UnderObject, x, y, options.MaxSamplesPerIssue);
            }

            if (TryResolvePackedObject(cell.OverObject, out int overPkg, out int overImg))
            {
                AddImage(imageRefs, overPkg, overImg, LayerMask.OverObject, x, y, options.MaxSamplesPerIssue);
            }

            if (TryResolvePackedObject(cell.NearGround, out int nearPkg, out int nearImg))
            {
                AddImage(imageRefs, nearPkg, nearImg, LayerMask.NearGround, x, y, options.MaxSamplesPerIssue);
            }
        }

        var issues = new List<MapResourceValidationIssue>();

        foreach ((ImageKey key, RefStats stats) in imageRefs)
        {
            token.ThrowIfCancellationRequested();

            if (!textureIndex.TryDecodeImage(key.PackageId, key.ImageIndex, frame: 0, token, out DecodedImage _, out string error))
            {
                issues.Add(new MapResourceValidationIssue
                {
                    Kind = MapResourceIssueKind.DecodeFailed,
                    PackageId = key.PackageId,
                    ImageIndex = key.ImageIndex,
                    MaskImageIndex = 0,
                    Occurrences = stats.Occurrences,
                    Layers = FormatLayers(stats.Layers),
                    Error = error ?? string.Empty,
                    Samples = stats.Samples.ToList(),
                });
            }
        }

        if (options.ValidateCoastComposite)
        {
            foreach ((CoastKey key, RefStats stats) in coastRefs)
            {
                token.ThrowIfCancellationRequested();

                if (key.CoastImageIndex <= 0 || key.MaskImageIndex <= 0)
                {
                    issues.Add(new MapResourceValidationIssue
                    {
                        Kind = MapResourceIssueKind.CoastCompositeInvalid,
                        PackageId = key.PackageId,
                        ImageIndex = key.CoastImageIndex,
                        MaskImageIndex = key.MaskImageIndex,
                        Occurrences = stats.Occurrences,
                        Layers = "海岸合成",
                        Error = "海岸格参数不完整（coastIdx/maskIdx 需要同时为正数）。",
                        Samples = stats.Samples.ToList(),
                    });
                    continue;
                }

                if (!textureIndex.TryDecodeCoastCompositeImage(key.PackageId, key.CoastImageIndex, key.MaskImageIndex, frame: 0, token, out DecodedImage _, out string error))
                {
                    issues.Add(new MapResourceValidationIssue
                    {
                        Kind = MapResourceIssueKind.CoastCompositeFailed,
                        PackageId = key.PackageId,
                        ImageIndex = key.CoastImageIndex,
                        MaskImageIndex = key.MaskImageIndex,
                        Occurrences = stats.Occurrences,
                        Layers = "海岸合成",
                        Error = error ?? string.Empty,
                        Samples = stats.Samples.ToList(),
                    });
                }
            }
        }

        string docPath = string.IsNullOrWhiteSpace(map.Path) ? map.Info.Path : map.Path;
        if (string.IsNullOrWhiteSpace(docPath))
        {
            docPath = "(未命名文档)";
        }

        return new MapResourceValidationReport
        {
            DocumentPath = docPath,
            Width = width,
            Height = height,
            UniqueImageRefs = imageRefs.Count,
            UniqueCoastCompositeRefs = coastRefs.Count,
            Issues = issues,
        };
    }

    private static void AddImage(
        Dictionary<ImageKey, RefStats> dict,
        int packageId,
        int imageIndex,
        LayerMask layer,
        int x,
        int y,
        int maxSamples)
    {
        if (packageId <= 0 || imageIndex <= 0)
        {
            return;
        }

        var key = new ImageKey(packageId, imageIndex);
        if (!dict.TryGetValue(key, out RefStats? stats))
        {
            stats = new RefStats();
            dict[key] = stats;
        }

        stats.Occurrences++;
        stats.Layers |= layer;
        if (maxSamples > 0 && stats.Samples.Count < maxSamples)
        {
            stats.Samples.Add(new MapResourceValidationSample(x, y));
        }
    }

    private static void AddCoast(
        Dictionary<CoastKey, RefStats> dict,
        int packageId,
        int coastImageIndex,
        int maskImageIndex,
        int x,
        int y,
        int maxSamples)
    {
        if (packageId <= 0)
        {
            return;
        }

        var key = new CoastKey(packageId, coastImageIndex, maskImageIndex);
        if (!dict.TryGetValue(key, out RefStats? stats))
        {
            stats = new RefStats();
            dict[key] = stats;
        }

        stats.Occurrences++;
        stats.Layers |= LayerMask.CoastComposite;
        if (maxSamples > 0 && stats.Samples.Count < maxSamples)
        {
            stats.Samples.Add(new MapResourceValidationSample(x, y));
        }
    }

    private static bool TryResolvePackedObject(uint raw, out int packageId, out int imageIndex)
    {
        packageId = 0;
        imageIndex = 0;

        if (raw == 0)
        {
            return false;
        }

        imageIndex = (int)(raw & 0xFFFF);
        if (imageIndex == 0)
        {
            return false;
        }

        packageId = (int)((raw >> 16) & 0xFF);
        if (packageId == 0)
        {
            packageId = DefaultObjectLibrary;
        }

        return true;
    }

    private static string FormatLayers(LayerMask mask)
    {
        if (mask == LayerMask.None)
        {
            return string.Empty;
        }

        static void Append(List<string> parts, LayerMask mask, LayerMask flag, string label)
        {
            if ((mask & flag) != 0)
            {
                parts.Add(label);
            }
        }

        var parts = new List<string>(8);
        Append(parts, mask, LayerMask.Back, "Back(后景)");
        Append(parts, mask, LayerMask.Middle, "Middle(中景)");
        Append(parts, mask, LayerMask.Middle2, "Middle2(海岸地面)");
        Append(parts, mask, LayerMask.Front, "Front(前景)");
        Append(parts, mask, LayerMask.UnderObject, "UnderObject(下层物件)");
        Append(parts, mask, LayerMask.OverObject, "OverObject(上层物件)");
        Append(parts, mask, LayerMask.NearGround, "NearGround(近景地面)");
        Append(parts, mask, LayerMask.CoastComposite, "海岸合成");

        return string.Join(", ", parts);
    }
}
