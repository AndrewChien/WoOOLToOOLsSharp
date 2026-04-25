using System;

namespace WoOOLToOOLsSharp.MapEditor.App;

public enum MapLightingMode
{
    Day = 0,
    Night,
    Auto,
    CustomTime,
    Manual,
}

public sealed class MapLightingSettings
{
    public MapLightingMode Mode { get; set; } = MapLightingMode.Day;
    public int CustomHour { get; set; } = 20;
    public int CustomMinute { get; set; } = 0;

    /// <summary>
    /// 仅当 <see cref="Mode"/> 为 <see cref="MapLightingMode.Manual"/> 时生效。
    /// 0=白天，1=完全夜晚。
    /// </summary>
    public float ManualNightFactor { get; set; } = 0.0f;
}

public readonly record struct ResolvedMapLighting(MapLightingMode Mode, int Hour, int Minute, int Second, float NightFactor);

public static class MapLighting
{
    /// <summary>
    /// 计算“夜晚系数”（0=白天，1=完全夜晚）。对齐旧工程：<c>OldProj/MapEditor/src/app/MapLighting.h</c>。
    /// </summary>
    public static float ComputeNightFactorForClock(
        int hour,
        int minute,
        int second = 0,
        int sunriseHour = 6,
        int sunriseMinute = 0,
        int sunsetHour = 18,
        int sunsetMinute = 0)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        second = Math.Clamp(second, 0, 59);

        float currentMinutes = (hour * 60.0f) + minute + (second / 60.0f);
        float sunriseMinutes = (sunriseHour * 60.0f) + sunriseMinute;
        float sunsetMinutes = (sunsetHour * 60.0f) + sunsetMinute;
        const float transitionMinutes = 30.0f;

        if (sunriseMinutes >= sunsetMinutes)
        {
            return hour >= 6 && hour < 18 ? 0.0f : 1.0f;
        }

        if (currentMinutes < sunriseMinutes - transitionMinutes)
        {
            return 1.0f;
        }

        if (currentMinutes < sunriseMinutes + transitionMinutes)
        {
            float t = (currentMinutes - (sunriseMinutes - transitionMinutes)) / (transitionMinutes * 2.0f);
            return Math.Clamp(1.0f - t, 0.0f, 1.0f);
        }

        if (currentMinutes < sunsetMinutes - transitionMinutes)
        {
            return 0.0f;
        }

        if (currentMinutes < sunsetMinutes + transitionMinutes)
        {
            float t = (currentMinutes - (sunsetMinutes - transitionMinutes)) / (transitionMinutes * 2.0f);
            return Math.Clamp(t, 0.0f, 1.0f);
        }

        return 1.0f;
    }

    public static ResolvedMapLighting Resolve(MapLightingSettings settings, DateTime? now = null)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        DateTime localNow = now ?? DateTime.Now;

        if (settings.Mode == MapLightingMode.Day)
        {
            return new ResolvedMapLighting(MapLightingMode.Day, 12, 0, 0, 0.0f);
        }

        if (settings.Mode == MapLightingMode.Night)
        {
            return new ResolvedMapLighting(MapLightingMode.Night, 22, 0, 0, 1.0f);
        }

        if (settings.Mode == MapLightingMode.CustomTime)
        {
            int hour = Math.Clamp(settings.CustomHour, 0, 23);
            int minute = Math.Clamp(settings.CustomMinute, 0, 59);
            float factor = ComputeNightFactorForClock(hour, minute, second: 0);
            return new ResolvedMapLighting(MapLightingMode.CustomTime, hour, minute, 0, factor);
        }

        if (settings.Mode == MapLightingMode.Auto)
        {
            int hour = localNow.Hour;
            int minute = localNow.Minute;
            int second = localNow.Second;
            float factor = ComputeNightFactorForClock(hour, minute, second);
            return new ResolvedMapLighting(MapLightingMode.Auto, hour, minute, second, factor);
        }

        float manualFactor = settings.ManualNightFactor;
        if (!float.IsFinite(manualFactor))
        {
            manualFactor = 0.0f;
        }

        manualFactor = Math.Clamp(manualFactor, 0.0f, 1.0f);
        return new ResolvedMapLighting(MapLightingMode.Manual, localNow.Hour, localNow.Minute, localNow.Second, manualFactor);
    }
}

