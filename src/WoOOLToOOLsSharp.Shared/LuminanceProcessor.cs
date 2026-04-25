using System;

namespace WoOOLToOOLsSharp.Shared;

public enum LuminanceMode
{
    Rec709 = 0,
    Hsl = 1,
    Hsv = 2,
    Average = 3,
    RedChannel = 4,
    GreenChannel = 5,
    BlueChannel = 6,
}

public enum AlphaBlendMode
{
    Replace = 0,
    Multiply = 1,
    Screen = 2,
    Overlay = 3,
}

public readonly struct LuminanceSettings
{
    public LuminanceMode Mode { get; init; }
    public AlphaBlendMode BlendMode { get; init; }
    public float Gamma { get; init; }
    public float Contrast { get; init; }
    public byte Threshold { get; init; }
    public bool Inverted { get; init; }

    public LuminanceSettings()
    {
        Mode = LuminanceMode.Rec709;
        BlendMode = AlphaBlendMode.Replace;
        Gamma = 1.0f;
        Contrast = 0.0f;
        Threshold = 0;
        Inverted = false;
    }
}

public static class LuminanceProcessor
{
    public static byte CalculateLuminance(byte r, byte g, byte b, LuminanceMode mode)
    {
        return mode switch
        {
            LuminanceMode.Rec709 => (byte)((54 * r + 183 * g + 19 * b) >> 8),
            LuminanceMode.Hsl => (byte)((Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2),
            LuminanceMode.Hsv => Math.Max(r, Math.Max(g, b)),
            LuminanceMode.Average => (byte)(((uint)r + g + b) / 3),
            LuminanceMode.RedChannel => r,
            LuminanceMode.GreenChannel => g,
            LuminanceMode.BlueChannel => b,
            _ => (byte)((54 * r + 183 * g + 19 * b) >> 8),
        };
    }

    public static byte ApplyLuminanceAdjustments(byte luminance, LuminanceSettings settings)
    {
        float lum = luminance / 255.0f;

        if (settings.Gamma != 1.0f && settings.Gamma > 0.0f)
        {
            lum = MathF.Pow(lum, 1.0f / settings.Gamma);
        }

        if (settings.Contrast != 0.0f)
        {
            float factor = MathF.Tan((settings.Contrast + 1.0f) * MathF.PI / 4.0f);
            lum = (lum - 0.5f) * factor + 0.5f;
        }

        if (settings.Threshold > 0)
        {
            if (lum < settings.Threshold / 255.0f)
            {
                lum = 0.0f;
            }
        }

        if (settings.Inverted)
        {
            lum = 1.0f - lum;
        }

        lum = Math.Clamp(lum, 0.0f, 1.0f);
        return (byte)(lum * 255.0f);
    }

    public static byte BlendAlpha(byte calculatedAlpha, byte existingAlpha, AlphaBlendMode mode)
    {
        float calc = calculatedAlpha / 255.0f;
        float existing = existingAlpha / 255.0f;

        float result = mode switch
        {
            AlphaBlendMode.Multiply => existing * calc,
            AlphaBlendMode.Screen => 1.0f - (1.0f - existing) * (1.0f - calc),
            AlphaBlendMode.Overlay => existing < 0.5f ? 2.0f * existing * calc : 1.0f - 2.0f * (1.0f - existing) * (1.0f - calc),
            _ => calc,
        };

        result = Math.Clamp(result, 0.0f, 1.0f);
        return (byte)(result * 255.0f);
    }
}

