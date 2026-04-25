using System.Text.Json;

namespace WoOOLToOOLsSharp.Shared;

public sealed record DynamicOverlayBinaryFieldSpec
{
    public int Offset { get; init; }
    public string Type { get; init; } = "i32";
    public IReadOnlyDictionary<string, string>? Mapping { get; init; }
}

public sealed record DynamicOverlayBinaryLayout
{
    public int Offset { get; init; }
    public int RecordSize { get; init; }
    public int Count { get; init; }

    public string Kind { get; init; } = "scene";
    public string Layer { get; init; } = "front";
    public string CoordinateSpace { get; init; } = "pixel";
    public string BlendMode { get; init; } = "alpha";

    public DynamicOverlayBinaryFieldSpec? X { get; init; }
    public DynamicOverlayBinaryFieldSpec? Y { get; init; }
    public DynamicOverlayBinaryFieldSpec? PackageId { get; init; }
    public DynamicOverlayBinaryFieldSpec? ImageId { get; init; }
    public DynamicOverlayBinaryFieldSpec? OffsetX { get; init; }
    public DynamicOverlayBinaryFieldSpec? OffsetY { get; init; }
    public DynamicOverlayBinaryFieldSpec? Frame { get; init; }
    public DynamicOverlayBinaryFieldSpec? Order { get; init; }
    public DynamicOverlayBinaryFieldSpec? Scale { get; init; }
    public DynamicOverlayBinaryFieldSpec? Alpha { get; init; }
    public DynamicOverlayBinaryFieldSpec? TintR { get; init; }
    public DynamicOverlayBinaryFieldSpec? TintG { get; init; }
    public DynamicOverlayBinaryFieldSpec? TintB { get; init; }
    public DynamicOverlayBinaryFieldSpec? TintA { get; init; }
    public DynamicOverlayBinaryFieldSpec? KindField { get; init; }
    public DynamicOverlayBinaryFieldSpec? LayerField { get; init; }
    public DynamicOverlayBinaryFieldSpec? CoordinateSpaceField { get; init; }
    public DynamicOverlayBinaryFieldSpec? BlendModeField { get; init; }

    public static bool TryLoadFromFile(string path, out DynamicOverlayBinaryLayout layout, out string error)
    {
        layout = new DynamicOverlayBinaryLayout();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空。";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
            };
            DynamicOverlayBinaryLayout? parsed = JsonSerializer.Deserialize<DynamicOverlayBinaryLayout>(json, options);
            if (parsed is null)
            {
                error = "布局 JSON 为空。";
                return false;
            }

            if (parsed.RecordSize <= 0)
            {
                error = "布局的 recordSize 必须大于 0。";
                return false;
            }

            if (parsed.X is null || parsed.Y is null || parsed.PackageId is null || parsed.ImageId is null)
            {
                error = "布局缺少必需字段：x/y/packageId/imageId。";
                return false;
            }

            layout = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }
}
