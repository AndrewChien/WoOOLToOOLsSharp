using System;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public sealed record VulkanRendererOptions
{
    public bool PreferDiscreteGpu { get; init; } = true;
    public string? PreferredDeviceName { get; init; }
    public bool EnableValidationLayers { get; init; } = DefaultEnableValidationLayers;
    public bool EnableDebugUtils { get; init; } = DefaultEnableDebugUtils;
    public int FramesInFlight { get; init; } = 2;
    public bool Verbose { get; init; }

    public static bool DefaultEnableValidationLayers =>
#if DEBUG
        true;
#else
        false;
#endif

    public static bool DefaultEnableDebugUtils =>
#if DEBUG
        true;
#else
        false;
#endif

    public static VulkanRendererOptions FromEnvironment()
    {
        return new VulkanRendererOptions
        {
            PreferDiscreteGpu = ReadBool("WOOOL_VK_PREFER_DISCRETE", defaultValue: true),
            PreferredDeviceName = ReadString("WOOOL_VK_DEVICE"),
            EnableValidationLayers = ReadBool("WOOOL_VK_VALIDATION", defaultValue: DefaultEnableValidationLayers),
            EnableDebugUtils = ReadBool("WOOOL_VK_DEBUG_UTILS", defaultValue: DefaultEnableDebugUtils),
            FramesInFlight = ClampFrames(ReadInt("WOOOL_VK_FRAMES", defaultValue: 2)),
            Verbose = ReadBool("WOOOL_VK_VERBOSE", defaultValue: false),
        };
    }

    private static int ClampFrames(int value)
    {
        return Math.Clamp(value, 2, 3);
    }

    private static string? ReadString(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int ReadInt(string name, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) ? parsed : defaultValue;
    }

    private static bool ReadBool(string name, bool defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        value = value.Trim();
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

