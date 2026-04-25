using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using ImGuiNET;
using WoOOLToOOLsSharp.Shared;

namespace WoOOLToOOLsSharp.MapEditor.App;

public static class MapEditorPreferences
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool TryLoad(string filePath, MapEditorState state, out string error)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        error = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "偏好设置路径为空";
            return false;
        }

        if (!File.Exists(filePath))
        {
            return true;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath, Utf8NoBom);
        }
        catch (Exception ex)
        {
            error = $"读取偏好设置失败: {ex.Message}";
            return false;
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            const string MapFolderPrefix = "map_folder ";
            if (line.StartsWith(MapFolderPrefix, StringComparison.Ordinal))
            {
                if (TryParseLegacyQuotedPair(line[MapFolderPrefix.Length..], out string displayName, out string path))
                {
                    state.MapPathEntries.Add(new NamedPathEntry
                    {
                        DisplayName = displayName,
                        Path = path,
                    });
                }

                continue;
            }

            const string DataFolderPrefix = "data_folder ";
            if (line.StartsWith(DataFolderPrefix, StringComparison.Ordinal))
            {
                if (TryParseLegacyQuotedPair(line[DataFolderPrefix.Length..], out string displayName, out string path))
                {
                    state.DataPathEntries.Add(new NamedPathEntry
                    {
                        DisplayName = displayName,
                        Path = path,
                    });
                }

                continue;
            }

            const string OpenMapPrefix = "open_map ";
            if (line.StartsWith(OpenMapPrefix, StringComparison.Ordinal))
            {
                if (TryParseLegacyQuotedSingle(line[OpenMapPrefix.Length..], out string path)
                    && !string.IsNullOrWhiteSpace(path))
                {
                    state.RestoreOpenMapPaths.Add(path);
                }

                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq >= line.Length - 1)
            {
                continue;
            }

            string key = line[..eq];
            string value = line[(eq + 1)..];

            if (key == "restore_state")
            {
                state.RestoreState = value == "1";
                continue;
            }
            if (key == "active_map_index")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.RestoreActiveMapIndex = parsed;
                }
                continue;
            }

            if (key == "render_apply_lighting_overlay")
            {
                state.RenderApplyLightingOverlay = value == "1";
                continue;
            }
            if (key == "render_lighting_overlay_max_alpha")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.RenderLightingOverlayMaxAlpha = Math.Clamp(parsed, 0, 255);
                }
                continue;
            }
            if (key == "render_include_light_sprites")
            {
                state.RenderIncludeLightSprites = value == "1";
                continue;
            }
            if (key == "render_lighting_mode")
            {
                if (TryParseLightingMode(value, out MapLightingMode parsed))
                {
                    state.RenderLighting.Mode = parsed;
                }
                continue;
            }
            if (key == "render_lighting_custom_hour")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.RenderLighting.CustomHour = Math.Clamp(parsed, 0, 23);
                }
                continue;
            }
            if (key == "render_lighting_custom_minute")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.RenderLighting.CustomMinute = Math.Clamp(parsed, 0, 59);
                }
                continue;
            }
            if (key == "render_lighting_manual_factor")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && float.IsFinite(parsed))
                {
                    state.RenderLighting.ManualNightFactor = Math.Clamp(parsed, 0.0f, 1.0f);
                }
                continue;
            }

            if (key == "minimap_apply_lighting_overlay")
            {
                state.MinimapApplyLightingOverlay = value == "1";
                continue;
            }
            if (key == "minimap_lighting_overlay_max_alpha")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapLightingOverlayMaxAlpha = Math.Clamp(parsed, 0, 255);
                }
                continue;
            }
            if (key == "minimap_include_light_sprites")
            {
                state.MinimapIncludeLightSprites = value == "1";
                continue;
            }
            if (key == "minimap_lighting_mode")
            {
                if (TryParseLightingMode(value, out MapLightingMode parsed))
                {
                    state.MinimapLighting.Mode = parsed;
                }
                continue;
            }
            if (key == "minimap_lighting_custom_hour")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapLighting.CustomHour = Math.Clamp(parsed, 0, 23);
                }
                continue;
            }
            if (key == "minimap_lighting_custom_minute")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapLighting.CustomMinute = Math.Clamp(parsed, 0, 59);
                }
                continue;
            }
            if (key == "minimap_lighting_manual_factor")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && float.IsFinite(parsed))
                {
                    state.MinimapLighting.ManualNightFactor = Math.Clamp(parsed, 0.0f, 1.0f);
                }
                continue;
            }

            if (key == "minimap_export_path")
            {
                state.MinimapExportPath = value;
                continue;
            }
            if (key == "minimap_scale")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapScale = Math.Clamp(parsed, 1, 32);
                }
                continue;
            }
            if (key == "minimap_include_back")
            {
                state.MinimapIncludeBack = value == "1";
                continue;
            }
            if (key == "minimap_include_middle")
            {
                state.MinimapIncludeMiddle = value == "1";
                continue;
            }
            if (key == "minimap_include_floor")
            {
                state.MinimapIncludeFloor = value == "1";
                continue;
            }
            if (key == "minimap_include_underfront")
            {
                state.MinimapIncludeUnderFront = value == "1";
                continue;
            }
            if (key == "minimap_include_front")
            {
                state.MinimapIncludeFront = value == "1";
                continue;
            }
            if (key == "minimap_include_overfront")
            {
                state.MinimapIncludeOverFront = value == "1";
                continue;
            }
            if (key == "minimap_include_dynscene")
            {
                state.MinimapIncludeDynamicScene = value == "1";
                continue;
            }
            if (key == "minimap_include_effects")
            {
                state.MinimapIncludeAttachedEffects = value == "1";
                continue;
            }
            if (key == "minimap_overlay_map_id")
            {
                state.MinimapOverlayMapIdOverride = value;
                continue;
            }
            if (key == "minimap_effects_map_id")
            {
                state.MinimapAttachedEffectsMapIdOverride = value;
                continue;
            }
            if (key == "minimap_overlay_layout")
            {
                state.MinimapOverlayLayoutPath = value;
                continue;
            }
            if (key == "minimap_effects_layout")
            {
                state.MinimapAttachedEffectsLayoutPath = value;
                continue;
            }
            if (key == "minimap_overlay_max_decompressed_bytes")
            {
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    state.MinimapOverlayMaxDecompressedBytes = Math.Clamp(parsed, 0, 2L * 1024 * 1024 * 1024);
                }
                continue;
            }
            if (key == "minimap_separate_layer_files")
            {
                state.MinimapSeparateLayerFiles = value == "1";
                continue;
            }
            if (key == "minimap_use_textures")
            {
                state.MinimapUseTextures = value == "1";
                continue;
            }
            if (key == "minimap_suppress_border")
            {
                state.MinimapSuppressBorderCells = value == "1";
                continue;
            }
            if (key == "minimap_apply_cell_tints")
            {
                state.MinimapApplyCellTints = value == "1";
                continue;
            }
            if (key == "minimap_tint_strength")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && float.IsFinite(parsed))
                {
                    state.MinimapTintStrength = Math.Clamp(parsed, 0.0f, 1.0f);
                }
                continue;
            }
            if (key == "minimap_apply_height_flag")
            {
                state.MinimapApplyCellHeightFlag = value == "1";
                continue;
            }
            if (key == "minimap_cell_height_offset")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && float.IsFinite(parsed))
                {
                    state.MinimapCellHeightFlagOffset = Math.Max(0.0f, parsed);
                }
                continue;
            }
            if (key == "minimap_apply_object_height")
            {
                state.MinimapApplyObjectHeight = value == "1";
                continue;
            }
            if (key == "minimap_object_height_scale")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && float.IsFinite(parsed))
                {
                    state.MinimapObjectHeightScale = Math.Max(0.0f, parsed);
                }
                continue;
            }
            if (key == "minimap_apply_luminance_to_alpha")
            {
                state.MinimapApplyLuminanceToAlpha = value == "1";
                continue;
            }
            if (key == "minimap_crop_mode")
            {
                state.MinimapCropMode = value switch
                {
                    "cell_rect" => MinimapCropMode.CellRect,
                    "pixel_rect" => MinimapCropMode.PixelRect,
                    "auto_non_empty_cells" => MinimapCropMode.AutoNonEmptyCells,
                    _ => MinimapCropMode.None,
                };
                continue;
            }
            if (key == "minimap_crop_cell_x")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropCellX = Math.Max(0, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_cell_y")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropCellY = Math.Max(0, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_cell_width")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropCellWidth = Math.Max(1, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_cell_height")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropCellHeight = Math.Max(1, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_pixel_x")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropPixelX = Math.Max(0, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_pixel_y")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropPixelY = Math.Max(0, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_pixel_width")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropPixelWidth = Math.Max(1, parsed);
                }
                continue;
            }
            if (key == "minimap_crop_pixel_height")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapCropPixelHeight = Math.Max(1, parsed);
                }
                continue;
            }
            if (key == "minimap_auto_crop_padding_cells")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.MinimapAutoCropPaddingCells = Math.Max(0, parsed);
                }
                continue;
            }
            if (key == "minimap_batch_input_dir")
            {
                state.MinimapBatchInputDirectory = value;
                continue;
            }
            if (key == "minimap_batch_output_dir")
            {
                state.MinimapBatchOutputDirectory = value;
                continue;
            }
            if (key == "minimap_batch_recursive")
            {
                state.MinimapBatchRecursive = value == "1";
                continue;
            }
            if (key == "minimap_batch_overwrite")
            {
                state.MinimapBatchOverwrite = value == "1";
                continue;
            }
            if (key == "minimap_batch_include_scale_tag")
            {
                state.MinimapBatchIncludeScaleTag = value == "1";
                continue;
            }

            if (key == "show_file_browser_panel")
            {
                state.ShowFileBrowserPanel = value == "1";
                continue;
            }
            if (key == "unload_inactive_tabs")
            {
                state.UnloadInactiveTabs = value == "1";
                continue;
            }
            if (key == "show_prefab_browser_panel")
            {
                state.ShowPrefabBrowserPanel = value == "1";
                continue;
            }
            if (key == "show_information_panel")
            {
                state.ShowInformationPanel = value == "1";
                continue;
            }
            if (key == "show_cell_inspector_panel")
            {
                state.ShowCellInspectorPanel = value == "1";
                continue;
            }
            if (key == "prefab_thumbnail_size")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.PrefabThumbnailSize = Math.Clamp(parsed, 32.0f, 128.0f);
                }
                continue;
            }
            if (key == "prefab_browser_view")
            {
                state.PrefabBrowserViewMode = value.Trim().Equals("details", StringComparison.OrdinalIgnoreCase)
                    ? PrefabBrowserViewMode.Details
                    : PrefabBrowserViewMode.Thumbnails;
                continue;
            }
            if (key == "selected_map_path_index")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.SelectedMapPathEntryIndex = parsed;
                }

                continue;
            }
            if (key == "show_grid")
            {
                state.ShowGrid = value == "1";
                continue;
            }
            if (key == "grid_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.GridColor = parsed;
                }
                continue;
            }
            if (key == "grid_thickness")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.GridThickness = Math.Clamp(parsed, 1, 5);
                }
                continue;
            }
            if (key == "show_layer_back")
            {
                state.ShowBackLayer = value == "1";
                continue;
            }
            if (key == "show_layer_middle")
            {
                state.ShowMiddleLayer = value == "1";
                continue;
            }
            if (key == "show_layer_floor")
            {
                state.ShowFloorLayer = value == "1";
                continue;
            }
            if (key == "show_layer_underfront")
            {
                state.ShowUnderFrontLayer = value == "1";
                continue;
            }
            if (key == "show_layer_front")
            {
                state.ShowFrontLayer = value == "1";
                continue;
            }
            if (key == "show_layer_overfront")
            {
                state.ShowOverFrontLayer = value == "1";
                continue;
            }
            if (key == "show_layer_dynscene")
            {
                state.ShowDynamicSceneLayer = value == "1";
                continue;
            }
            if (key == "show_layer_effects")
            {
                state.ShowAttachedEffectsLayer = value == "1";
                continue;
            }
            if (key == "show_blocked")
            {
                state.ShowBlockedOverlay = value == "1";
                continue;
            }
            if (key == "blocked_overlay_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.BlockedOverlayColor = parsed;
                }
                continue;
            }
            if (key == "show_minimap")
            {
                state.ShowMinimapOverlay = value == "1";
                continue;
            }
            if (key == "minimap_opacity")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.MinimapOpacity = Math.Clamp(parsed, 0.0f, 1.0f);
                }
                continue;
            }
            if (key == "show_tile_fill")
            {
                state.ShowTileFill = value == "1";
                continue;
            }
            if (key == "highlight_back_cells")
            {
                state.HighlightBackCells = value == "1";
                continue;
            }
            if (key == "highlight_middle_cells")
            {
                state.HighlightMiddleCells = value == "1";
                continue;
            }
            if (key == "highlight_front_cells")
            {
                state.HighlightFrontCells = value == "1";
                continue;
            }
            if (key == "highlight_floor_cells")
            {
                state.HighlightFloorCells = value == "1";
                continue;
            }
            if (key == "highlight_underfront_cells")
            {
                state.HighlightUnderFrontCells = value == "1";
                continue;
            }
            if (key == "highlight_overfront_cells")
            {
                state.HighlightOverFrontCells = value == "1";
                continue;
            }
            if (key == "highlight_coast_mask_cells")
            {
                state.HighlightCoastMaskCells = value == "1";
                continue;
            }
            if (key == "highlight_missing_texture_cells")
            {
                state.HighlightMissingTextureCells = value == "1";
                continue;
            }
            if (key == "highlight_back_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightBackColor = parsed;
                }
                continue;
            }
            if (key == "highlight_middle_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightMiddleColor = parsed;
                }
                continue;
            }
            if (key == "highlight_front_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightFrontColor = parsed;
                }
                continue;
            }
            if (key == "highlight_floor_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightFloorColor = parsed;
                }
                continue;
            }
            if (key == "highlight_underfront_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightUnderFrontColor = parsed;
                }
                continue;
            }
            if (key == "highlight_overfront_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightOverFrontColor = parsed;
                }
                continue;
            }
            if (key == "highlight_coast_mask_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightCoastMaskColor = parsed;
                }
                continue;
            }
            if (key == "highlight_missing_texture_color")
            {
                if (TryParseVector4Rgba(value, out Vector4 parsed))
                {
                    state.HighlightMissingTextureColor = parsed;
                }
                continue;
            }
            if (key == "map_browser_root")
            {
                state.MapBrowserRootDirectory = value;
                continue;
            }
            if (key == "map_browser_recursive")
            {
                state.MapBrowserRecursive = value == "1";
                continue;
            }
            if (key == "map_browser_include_prefabs")
            {
                state.MapBrowserIncludePrefabs = value == "1";
                continue;
            }
            if (key == "map_browser_filter")
            {
                state.MapBrowserFilter = value;
                continue;
            }
            if (key == "show_object_list_panel")
            {
                state.ShowObjectListPanel = value == "1";
                continue;
            }
            if (key == "object_list_filter")
            {
                state.ObjectListFilter = value;
                continue;
            }
            if (key == "show_scene_tree_panel")
            {
                state.ShowSceneTreePanel = value == "1";
                continue;
            }
            if (key == "show_console_panel")
            {
                state.ShowConsolePanel = value == "1";
                continue;
            }
            if (key == "console_auto_scroll")
            {
                state.ConsoleAutoScroll = value == "1";
                continue;
            }
            if (key == "show_settings_window")
            {
                state.ShowSettingsWindow = value == "1";
                continue;
            }
            if (key == "settings_section")
            {
                if (TryParseSettingsSection(value, out MapEditorSettingsSection parsed))
                {
                    state.CurrentSettingsSection = parsed;
                }

                continue;
            }
            if (key == "zoom_min")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.Camera.MinZoom = Math.Clamp(parsed, 0.1f, 4.0f);
                }

                continue;
            }
            if (key == "zoom_max")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.Camera.MaxZoom = Math.Clamp(parsed, 1.0f, 32.0f);
                }

                continue;
            }
            if (key == "zoom_step")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.ZoomStep = Math.Clamp(parsed, 0.01f, 1.0f);
                }

                continue;
            }
            if (key == "scene_tree_filter")
            {
                state.SceneTreeFilter = value;
                continue;
            }
            if (key == "texture_root")
            {
                state.TextureRootDirectory = value;
                continue;
            }
            if (key == "texture_source_mode")
            {
                if (TryParseTextureSourceMode(value, out TextureSourceMode parsed))
                {
                    state.TextureSourceMode = parsed;
                }
                continue;
            }
            if (key == "coast_mask_source")
            {
                state.CoastMaskPreferTex = !string.Equals(value?.Trim(), "msk", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (key == "luminance_mode")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= 6)
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, mode: (LuminanceMode)parsed);
                }

                continue;
            }
            if (key == "luminance_blend_mode")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= 3)
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, blendMode: (AlphaBlendMode)parsed);
                }

                continue;
            }
            if (key == "luminance_gamma")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed) && parsed > 0.0f)
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, gamma: parsed);
                }

                continue;
            }
            if (key == "luminance_contrast")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, contrast: Math.Clamp(parsed, -1.0f, 1.0f));
                }

                continue;
            }
            if (key == "luminance_threshold")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    parsed = Math.Clamp(parsed, 0, 255);
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, threshold: (byte)parsed);
                }

                continue;
            }
            if (key == "luminance_inverted")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, inverted: parsed);
                }

                continue;
            }
            if (key == "skip_luminance")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.SkipLuminanceToAlpha = parsed;
                }

                continue;
            }

            const string KeybindPrefix = "keybind.";
            if (key.StartsWith(KeybindPrefix, StringComparison.Ordinal))
            {
                string action = key.Substring(KeybindPrefix.Length);
                if (TryParseKeyBinding(value, out KeyBinding parsed))
                {
                    ApplyKeyBinding(state.KeyBindings, action, parsed);
                }

                continue;
            }
            if (key == "selected_data_path_index")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.SelectedDataPathEntryIndex = parsed;
                }

                continue;
            }
            if (key == "texture_scan_recursive")
            {
                state.TextureScanRecursive = value == "1";
                continue;
            }
            if (key == "render_use_textures")
            {
                state.RenderUseTextures = value == "1";
                continue;
            }
            if (key == "render_animate_textures")
            {
                state.RenderAnimateTextures = value == "1";
                continue;
            }
            if (key == "texture_animation_fps")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.TextureAnimationFps = Math.Clamp(parsed, 1.0f, 1000.0f);
                }

                continue;
            }
            if (key == "texture_animation_per_cell_offset")
            {
                state.TextureAnimationPerCellOffset = value == "1";
                continue;
            }
            if (key == "render_apply_cell_tints")
            {
                state.RenderApplyCellTints = value == "1";
                continue;
            }
            if (key == "render_tint_strength")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.RenderTintStrength = Math.Clamp(parsed, 0.0f, 1.0f);
                }

                continue;
            }
            if (key == "render_warn_on_unsupported_parity_data")
            {
                state.RenderWarnOnUnsupportedParityData = value == "1";
                continue;
            }
            if (key == "render_suppress_border_cells")
            {
                state.RenderSuppressBorderCells = value == "1";
                continue;
            }
            if (key == "render_apply_cell_height_flag")
            {
                state.RenderApplyCellHeightFlag = value == "1";
                continue;
            }
            if (key == "render_cell_height_flag_offset")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.RenderCellHeightFlagOffset = Math.Clamp(parsed, 0.0f, 64.0f);
                }

                continue;
            }
            if (key == "render_apply_object_height")
            {
                state.RenderApplyObjectHeight = value == "1";
                continue;
            }
            if (key == "render_object_height_scale")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.RenderObjectHeightScale = Math.Clamp(parsed, 0.0f, 8.0f);
                }

                continue;
            }
            if (key == "texture_max_cache_items")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.TextureMaxCacheItems = Math.Clamp(parsed, 16, 32768);
                }
                continue;
            }
            if (key == "texture_submit_budget_per_frame")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.TextureSubmitBudgetPerFrame = Math.Clamp(parsed, 1, 8192);
                }
                continue;
            }
            if (key == "texture_create_budget_per_frame")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.TextureCreateBudgetPerFrame = Math.Clamp(parsed, 1, 1024);
                }
                continue;
            }

            if (key == "stamp_path")
            {
                state.StampPath = value;
                continue;
            }
            if (key == "stamp_anchor")
            {
                state.StampAnchor = value switch
                {
                    "top_left" => StampAnchorMode.TopLeft,
                    _ => StampAnchorMode.Center,
                };
                continue;
            }
            if (key == "stamp_overwrite_empty")
            {
                state.StampOverwriteEmpty = value == "1";
                continue;
            }
            if (key == "stamp_apply_back")
            {
                state.StampApplyBack = value == "1";
                continue;
            }
            if (key == "stamp_apply_middle")
            {
                state.StampApplyMiddle = value == "1";
                continue;
            }
            if (key == "stamp_apply_front")
            {
                state.StampApplyFront = value == "1";
                continue;
            }
            if (key == "stamp_apply_under_object")
            {
                state.StampApplyUnderObject = value == "1";
                continue;
            }
            if (key == "stamp_apply_over_object")
            {
                state.StampApplyOverObject = value == "1";
                continue;
            }
            if (key == "stamp_apply_near_ground")
            {
                state.StampApplyNearGround = value == "1";
                continue;
            }
            if (key == "stamp_apply_blocked")
            {
                state.StampApplyBlocked = value == "1";
                continue;
            }

            if (key == "move_apply_back")
            {
                state.MoveApplyBack = value == "1";
                continue;
            }
            if (key == "move_apply_middle")
            {
                state.MoveApplyMiddle = value == "1";
                continue;
            }
            if (key == "move_apply_front")
            {
                state.MoveApplyFront = value == "1";
                continue;
            }
            if (key == "move_apply_under_object")
            {
                state.MoveApplyUnderObject = value == "1";
                continue;
            }
            if (key == "move_apply_over_object")
            {
                state.MoveApplyOverObject = value == "1";
                continue;
            }
            if (key == "move_apply_near_ground")
            {
                state.MoveApplyNearGround = value == "1";
                continue;
            }
            if (key == "move_apply_blocked")
            {
                state.MoveApplyBlocked = value == "1";
                continue;
            }

            if (key == "erase_apply_back")
            {
                state.EraseApplyBack = value == "1";
                continue;
            }
            if (key == "erase_apply_middle")
            {
                state.EraseApplyMiddle = value == "1";
                continue;
            }
            if (key == "erase_apply_front")
            {
                state.EraseApplyFront = value == "1";
                continue;
            }
            if (key == "erase_apply_under_object")
            {
                state.EraseApplyUnderObject = value == "1";
                continue;
            }
            if (key == "erase_apply_over_object")
            {
                state.EraseApplyOverObject = value == "1";
                continue;
            }
            if (key == "erase_apply_near_ground")
            {
                state.EraseApplyNearGround = value == "1";
                continue;
            }
            if (key == "erase_apply_blocked")
            {
                state.EraseApplyBlocked = value == "1";
                continue;
            }
        }

        state.SelectedMapPathEntryIndex = NormalizeNamedPathSelection(
            state.MapPathEntries,
            state.SelectedMapPathEntryIndex,
            state.MapBrowserRootDirectory,
            applySelectedPath: selectedPath => state.MapBrowserRootDirectory = selectedPath);

        state.SelectedDataPathEntryIndex = NormalizeNamedPathSelection(
            state.DataPathEntries,
            state.SelectedDataPathEntryIndex,
            state.TextureRootDirectory,
            applySelectedPath: selectedPath => state.TextureRootDirectory = selectedPath);

        // Keep staging copy in sync with the loaded value so the UI starts clean.
        state.PendingLuminanceSettings = state.LuminanceSettings;

        return true;
    }

    public static bool TryLoadLegacy(string filePath, MapEditorState state, out string error, out string note)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        error = string.Empty;
        note = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "旧版偏好设置路径为空";
            return false;
        }

        if (!File.Exists(filePath))
        {
            return true;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            error = $"读取旧版 settings.cfg 失败: {ex.Message}";
            return false;
        }

        int mapFolderCount = 0;
        int dataFolderCount = 0;
        var importedMapEntries = new List<NamedPathEntry>();
        var importedDataEntries = new List<NamedPathEntry>();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            const string MapFolderPrefix = "map_folder ";
            if (line.StartsWith(MapFolderPrefix, StringComparison.Ordinal))
            {
                if (TryParseLegacyQuotedPair(line[MapFolderPrefix.Length..], out string displayName, out string path))
                {
                    mapFolderCount++;
                    importedMapEntries.Add(new NamedPathEntry { DisplayName = displayName, Path = path });
                }

                continue;
            }

            const string DataFolderPrefix = "data_folder ";
            if (line.StartsWith(DataFolderPrefix, StringComparison.Ordinal))
            {
                if (TryParseLegacyQuotedPair(line[DataFolderPrefix.Length..], out string displayName, out string path))
                {
                    dataFolderCount++;
                    importedDataEntries.Add(new NamedPathEntry { DisplayName = displayName, Path = path });
                }

                continue;
            }

            const string OpenMapPrefix = "open_map ";
            if (line.StartsWith(OpenMapPrefix, StringComparison.Ordinal))
            {
                if (TryParseLegacyQuotedSingle(line[OpenMapPrefix.Length..], out string path)
                    && !string.IsNullOrWhiteSpace(path))
                {
                    state.RestoreOpenMapPaths.Add(path);
                }

                continue;
            }

            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq >= line.Length - 1)
            {
                continue;
            }

            string key = line[..eq];
            string value = line[(eq + 1)..];

            if (key == "restore_state")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.RestoreState = parsed;
                }

                continue;
            }
            if (key == "active_map_index")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.RestoreActiveMapIndex = parsed;
                }

                continue;
            }
            if (key == "texture_source_mode")
            {
                if (TryParseTextureSourceMode(value, out TextureSourceMode parsed))
                {
                    state.TextureSourceMode = parsed;
                }

                continue;
            }
            if (key == "coast_mask_source")
            {
                state.CoastMaskPreferTex = !string.Equals(value?.Trim(), "msk", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (key == "luminance_mode")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= 6)
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, mode: (LuminanceMode)parsed);
                }

                continue;
            }
            if (key == "luminance_blend_mode")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 0 and <= 3)
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, blendMode: (AlphaBlendMode)parsed);
                }

                continue;
            }
            if (key == "luminance_gamma")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed) && parsed > 0.0f)
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, gamma: parsed);
                }

                continue;
            }
            if (key == "luminance_contrast")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, contrast: Math.Clamp(parsed, -1.0f, 1.0f));
                }

                continue;
            }
            if (key == "luminance_threshold")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    parsed = Math.Clamp(parsed, 0, 255);
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, threshold: (byte)parsed);
                }

                continue;
            }
            if (key == "luminance_inverted")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.LuminanceSettings = WithLuminanceSettings(state.LuminanceSettings, inverted: parsed);
                }

                continue;
            }
            if (key == "skip_luminance")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.SkipLuminanceToAlpha = parsed;
                }

                continue;
            }

            const string KeybindPrefix = "keybind.";
            if (key.StartsWith(KeybindPrefix, StringComparison.Ordinal))
            {
                string action = key.Substring(KeybindPrefix.Length);
                if (TryParseKeyBinding(value, out KeyBinding parsed))
                {
                    ApplyKeyBinding(state.KeyBindings, action, parsed);
                }

                continue;
            }

            if (key == "show_map_folders_panel")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowFileBrowserPanel = parsed;
                }

                continue;
            }
            if (key == "unload_inactive_tabs")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.UnloadInactiveTabs = parsed;
                }

                continue;
            }
            if (key == "show_prefab_browser_panel")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowPrefabBrowserPanel = parsed;
                }

                continue;
            }
            if (key == "show_information_panel")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowInformationPanel = parsed;
                }

                continue;
            }
            if (key == "show_cell_inspector_panel")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowCellInspectorPanel = parsed;
                }

                continue;
            }
            if (key == "prefab_thumbnail_size")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.PrefabThumbnailSize = Math.Clamp(parsed, 32.0f, 128.0f);
                }

                continue;
            }
            if (key == "prefab_browser_view")
            {
                state.PrefabBrowserViewMode = value.Trim().Equals("details", StringComparison.OrdinalIgnoreCase)
                    ? PrefabBrowserViewMode.Details
                    : PrefabBrowserViewMode.Thumbnails;
                continue;
            }
            if (key == "show_console_panel")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowConsolePanel = parsed;
                }

                continue;
            }
            if (key == "settings_section")
            {
                if (TryParseSettingsSection(value, out MapEditorSettingsSection parsed))
                {
                    state.CurrentSettingsSection = parsed;
                }

                continue;
            }
            if (key == "zoom_min")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.Camera.MinZoom = Math.Clamp(parsed, 0.1f, 4.0f);
                }

                continue;
            }
            if (key == "zoom_max")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.Camera.MaxZoom = Math.Clamp(parsed, 1.0f, 32.0f);
                }

                continue;
            }
            if (key == "zoom_step")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.ZoomStep = Math.Clamp(parsed, 0.01f, 1.0f);
                }

                continue;
            }

            if (key == "default_show_grid")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowGrid = parsed;
                }

                continue;
            }

            if (key == "show_minimap")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.ShowMinimapOverlay = parsed;
                }

                continue;
            }

            if (key == "minimap_opacity")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.MinimapOpacity = Math.Clamp(parsed, 0.0f, 1.0f);
                }

                continue;
            }

            if (key == "render_suppress_border_cells")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.RenderSuppressBorderCells = parsed;
                }

                continue;
            }

            if (key == "render_apply_cell_height_flag")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.RenderApplyCellHeightFlag = parsed;
                }

                continue;
            }

            if (key == "render_cell_height_flag_offset")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.RenderCellHeightFlagOffset = Math.Clamp(parsed, 0.0f, 64.0f);
                }

                continue;
            }

            if (key == "render_apply_object_height")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.RenderApplyObjectHeight = parsed;
                }

                continue;
            }

            if (key == "render_object_height_scale")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.RenderObjectHeightScale = Math.Clamp(parsed, 0.0f, 8.0f);
                }

                continue;
            }

            if (key == "render_apply_cell_tints")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.RenderApplyCellTints = parsed;
                }

                continue;
            }

            if (key == "render_tint_strength")
            {
                if (TryParseInvariantFloat(value, out float parsed) && float.IsFinite(parsed))
                {
                    state.RenderTintStrength = Math.Clamp(parsed, 0.0f, 1.0f);
                }

                continue;
            }

            if (key == "render_warn_on_unsupported_parity_data")
            {
                if (TryParseLegacyBool(value, out bool parsed))
                {
                    state.RenderWarnOnUnsupportedParityData = parsed;
                }

                continue;
            }
        }

        if (state.MapPathEntries.Count == 0 && importedMapEntries.Count > 0)
        {
            state.MapPathEntries.AddRange(importedMapEntries);
            state.SelectedMapPathEntryIndex = 0;
        }

        if (state.DataPathEntries.Count == 0 && importedDataEntries.Count > 0)
        {
            state.DataPathEntries.AddRange(importedDataEntries);
            state.SelectedDataPathEntryIndex = 0;
        }

        if (string.IsNullOrWhiteSpace(state.MapBrowserRootDirectory) && importedMapEntries.Count > 0)
        {
            state.MapBrowserRootDirectory = importedMapEntries[0].Path;
        }

        if (string.IsNullOrWhiteSpace(state.TextureRootDirectory) && importedDataEntries.Count > 0)
        {
            state.TextureRootDirectory = importedDataEntries[0].Path;
        }

        state.SelectedMapPathEntryIndex = NormalizeNamedPathSelection(
            state.MapPathEntries,
            state.SelectedMapPathEntryIndex,
            state.MapBrowserRootDirectory,
            applySelectedPath: selectedPath => state.MapBrowserRootDirectory = selectedPath);

        state.SelectedDataPathEntryIndex = NormalizeNamedPathSelection(
            state.DataPathEntries,
            state.SelectedDataPathEntryIndex,
            state.TextureRootDirectory,
            applySelectedPath: selectedPath => state.TextureRootDirectory = selectedPath);

        // Keep staging copy in sync with the loaded value so the UI starts clean.
        state.PendingLuminanceSettings = state.LuminanceSettings;

        note = BuildLegacyMigrationNote(filePath, mapFolderCount, dataFolderCount, state.MapBrowserRootDirectory, state.TextureRootDirectory);
        return true;
    }

    public static bool TryBuildSaveText(MapEditorState state, out string text, out string error)
    {
        return TrySaveInternal(filePath: null, state, writeToFile: false, out text, out error);
    }

    public static bool TrySave(string filePath, MapEditorState state, out string error)
    {
        return TrySaveInternal(filePath, state, writeToFile: true, out _, out error);
    }

    private static bool TrySaveInternal(string? filePath, MapEditorState state, bool writeToFile, out string text, out string error)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        text = string.Empty;
        error = string.Empty;
        if (writeToFile && string.IsNullOrWhiteSpace(filePath))
        {
            error = "偏好设置路径为空";
            return false;
        }

        try
        {
            var sb = new StringBuilder();

            sb.Append("restore_state=").Append(state.RestoreState ? "1" : "0").Append('\n');
            sb.Append("unload_inactive_tabs=").Append(state.UnloadInactiveTabs ? "1" : "0").Append('\n');
            sb.Append("active_map_index=").Append(state.RestoreActiveMapIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("show_file_browser_panel=").Append(state.ShowFileBrowserPanel ? "1" : "0").Append('\n');
            sb.Append("show_prefab_browser_panel=").Append(state.ShowPrefabBrowserPanel ? "1" : "0").Append('\n');
            sb.Append("show_information_panel=").Append(state.ShowInformationPanel ? "1" : "0").Append('\n');
            sb.Append("show_cell_inspector_panel=").Append(state.ShowCellInspectorPanel ? "1" : "0").Append('\n');

            float prefabThumbSize = state.PrefabThumbnailSize;
            if (!float.IsFinite(prefabThumbSize))
            {
                prefabThumbSize = 96.0f;
            }
            prefabThumbSize = Math.Clamp(prefabThumbSize, 32.0f, 128.0f);
            sb.Append("prefab_thumbnail_size=").Append(prefabThumbSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("prefab_browser_view=").Append(state.PrefabBrowserViewMode == PrefabBrowserViewMode.Details ? "details" : "thumbnails").Append('\n');

            sb.Append("selected_map_path_index=").Append(state.SelectedMapPathEntryIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("show_grid=").Append(state.ShowGrid ? "1" : "0").Append('\n');
            sb.Append("grid_color=").Append(SerializeVector4Rgba(state.GridColor)).Append('\n');
            sb.Append("grid_thickness=").Append(Math.Clamp(state.GridThickness, 1, 5).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("show_layer_back=").Append(state.ShowBackLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_middle=").Append(state.ShowMiddleLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_floor=").Append(state.ShowFloorLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_underfront=").Append(state.ShowUnderFrontLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_front=").Append(state.ShowFrontLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_overfront=").Append(state.ShowOverFrontLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_dynscene=").Append(state.ShowDynamicSceneLayer ? "1" : "0").Append('\n');
            sb.Append("show_layer_effects=").Append(state.ShowAttachedEffectsLayer ? "1" : "0").Append('\n');
            sb.Append("show_blocked=").Append(state.ShowBlockedOverlay ? "1" : "0").Append('\n');
            sb.Append("blocked_overlay_color=").Append(SerializeVector4Rgba(state.BlockedOverlayColor)).Append('\n');
            sb.Append("show_minimap=").Append(state.ShowMinimapOverlay ? "1" : "0").Append('\n');
            float minimapOpacity = state.MinimapOpacity;
            if (!float.IsFinite(minimapOpacity))
            {
                minimapOpacity = 0.85f;
            }
            minimapOpacity = Math.Clamp(minimapOpacity, 0.0f, 1.0f);
            sb.Append("minimap_opacity=").Append(minimapOpacity.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("show_tile_fill=").Append(state.ShowTileFill ? "1" : "0").Append('\n');

            sb.Append("highlight_back_cells=").Append(state.HighlightBackCells ? "1" : "0").Append('\n');
            sb.Append("highlight_middle_cells=").Append(state.HighlightMiddleCells ? "1" : "0").Append('\n');
            sb.Append("highlight_front_cells=").Append(state.HighlightFrontCells ? "1" : "0").Append('\n');
            sb.Append("highlight_floor_cells=").Append(state.HighlightFloorCells ? "1" : "0").Append('\n');
            sb.Append("highlight_underfront_cells=").Append(state.HighlightUnderFrontCells ? "1" : "0").Append('\n');
            sb.Append("highlight_overfront_cells=").Append(state.HighlightOverFrontCells ? "1" : "0").Append('\n');
            sb.Append("highlight_coast_mask_cells=").Append(state.HighlightCoastMaskCells ? "1" : "0").Append('\n');
            sb.Append("highlight_missing_texture_cells=").Append(state.HighlightMissingTextureCells ? "1" : "0").Append('\n');

            sb.Append("highlight_back_color=").Append(SerializeVector4Rgba(state.HighlightBackColor)).Append('\n');
            sb.Append("highlight_middle_color=").Append(SerializeVector4Rgba(state.HighlightMiddleColor)).Append('\n');
            sb.Append("highlight_front_color=").Append(SerializeVector4Rgba(state.HighlightFrontColor)).Append('\n');
            sb.Append("highlight_floor_color=").Append(SerializeVector4Rgba(state.HighlightFloorColor)).Append('\n');
            sb.Append("highlight_underfront_color=").Append(SerializeVector4Rgba(state.HighlightUnderFrontColor)).Append('\n');
            sb.Append("highlight_overfront_color=").Append(SerializeVector4Rgba(state.HighlightOverFrontColor)).Append('\n');
            sb.Append("highlight_coast_mask_color=").Append(SerializeVector4Rgba(state.HighlightCoastMaskColor)).Append('\n');
            sb.Append("highlight_missing_texture_color=").Append(SerializeVector4Rgba(state.HighlightMissingTextureColor)).Append('\n');

            sb.Append("map_browser_root=").Append(state.MapBrowserRootDirectory ?? string.Empty).Append('\n');
            sb.Append("map_browser_recursive=").Append(state.MapBrowserRecursive ? "1" : "0").Append('\n');
            sb.Append("map_browser_include_prefabs=").Append(state.MapBrowserIncludePrefabs ? "1" : "0").Append('\n');
            sb.Append("map_browser_filter=").Append(state.MapBrowserFilter ?? string.Empty).Append('\n');
            AppendNamedPathEntries(sb, "map_folder", state.MapPathEntries);
            sb.Append("show_object_list_panel=").Append(state.ShowObjectListPanel ? "1" : "0").Append('\n');
            sb.Append("object_list_filter=").Append(state.ObjectListFilter ?? string.Empty).Append('\n');
            sb.Append("show_scene_tree_panel=").Append(state.ShowSceneTreePanel ? "1" : "0").Append('\n');
            sb.Append("show_console_panel=").Append(state.ShowConsolePanel ? "1" : "0").Append('\n');
            sb.Append("console_auto_scroll=").Append(state.ConsoleAutoScroll ? "1" : "0").Append('\n');
            sb.Append("show_settings_window=").Append(state.ShowSettingsWindow ? "1" : "0").Append('\n');
            sb.Append("settings_section=").Append(ToSettingsSectionKey(state.CurrentSettingsSection)).Append('\n');
            sb.Append("scene_tree_filter=").Append(state.SceneTreeFilter ?? string.Empty).Append('\n');
            sb.Append("zoom_min=").Append(Math.Clamp(state.Camera.MinZoom, 0.1f, 4.0f).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("zoom_max=").Append(Math.Clamp(state.Camera.MaxZoom, 1.0f, 32.0f).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("zoom_step=").Append(Math.Clamp(state.ZoomStep, 0.01f, 1.0f).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("texture_root=").Append(state.TextureRootDirectory ?? string.Empty).Append('\n');
            sb.Append("texture_source_mode=").Append(ToTextureSourceModeKey(state.TextureSourceMode)).Append('\n');
            sb.Append("coast_mask_source=").Append(state.CoastMaskPreferTex ? "tex" : "msk").Append('\n');
            LuminanceSettings lum = state.LuminanceSettings;
            sb.Append("luminance_mode=").Append(((int)lum.Mode).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("luminance_blend_mode=").Append(((int)lum.BlendMode).ToString(CultureInfo.InvariantCulture)).Append('\n');
            float lumGamma = lum.Gamma;
            if (!float.IsFinite(lumGamma) || lumGamma <= 0.0f) lumGamma = 1.0f;
            sb.Append("luminance_gamma=").Append(lumGamma.ToString(CultureInfo.InvariantCulture)).Append('\n');
            float lumContrast = lum.Contrast;
            if (!float.IsFinite(lumContrast)) lumContrast = 0.0f;
            lumContrast = Math.Clamp(lumContrast, -1.0f, 1.0f);
            sb.Append("luminance_contrast=").Append(lumContrast.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("luminance_threshold=").Append(((int)lum.Threshold).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("luminance_inverted=").Append(lum.Inverted ? "1" : "0").Append('\n');
            sb.Append("skip_luminance=").Append(state.SkipLuminanceToAlpha ? "1" : "0").Append('\n');

            AppendKeyBinding(sb, "tool_blocked_editor", state.KeyBindings.ToolBlockedEditor);
            AppendKeyBinding(sb, "tool_selection", state.KeyBindings.ToolSelection);
            AppendKeyBinding(sb, "tool_erase", state.KeyBindings.ToolErase);
            AppendKeyBinding(sb, "tool_stamp", state.KeyBindings.ToolStamp);
            AppendKeyBinding(sb, "tool_tile_paint", state.KeyBindings.ToolTilePaint);
            AppendKeyBinding(sb, "tool_cancel", state.KeyBindings.ToolCancel);
            AppendKeyBinding(sb, "delete_selection", state.KeyBindings.DeleteSelection);
            AppendKeyBinding(sb, "undo", state.KeyBindings.Undo);
            AppendKeyBinding(sb, "redo", state.KeyBindings.Redo);
            AppendKeyBinding(sb, "save", state.KeyBindings.Save);
            AppendKeyBinding(sb, "zoom_in", state.KeyBindings.ZoomIn);
            AppendKeyBinding(sb, "zoom_out", state.KeyBindings.ZoomOut);
            AppendKeyBinding(sb, "reset_view", state.KeyBindings.ResetView);

            sb.Append("selected_data_path_index=").Append(state.SelectedDataPathEntryIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
            AppendNamedPathEntries(sb, "data_folder", state.DataPathEntries);
            sb.Append("texture_scan_recursive=").Append(state.TextureScanRecursive ? "1" : "0").Append('\n');
            sb.Append("render_use_textures=").Append(state.RenderUseTextures ? "1" : "0").Append('\n');
            sb.Append("render_animate_textures=").Append(state.RenderAnimateTextures ? "1" : "0").Append('\n');
            sb.Append("texture_animation_fps=")
                .Append(Math.Clamp(state.TextureAnimationFps, 1.0f, 1000.0f).ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append("texture_animation_per_cell_offset=").Append(state.TextureAnimationPerCellOffset ? "1" : "0").Append('\n');
            sb.Append("render_apply_cell_tints=").Append(state.RenderApplyCellTints ? "1" : "0").Append('\n');
            sb.Append("render_tint_strength=").Append(Math.Clamp(state.RenderTintStrength, 0.0f, 1.0f).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("render_warn_on_unsupported_parity_data=").Append(state.RenderWarnOnUnsupportedParityData ? "1" : "0").Append('\n');
            sb.Append("render_suppress_border_cells=").Append(state.RenderSuppressBorderCells ? "1" : "0").Append('\n');
            sb.Append("render_apply_cell_height_flag=").Append(state.RenderApplyCellHeightFlag ? "1" : "0").Append('\n');
            sb.Append("render_cell_height_flag_offset=")
                .Append(Math.Clamp(state.RenderCellHeightFlagOffset, 0.0f, 64.0f).ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append("render_apply_object_height=").Append(state.RenderApplyObjectHeight ? "1" : "0").Append('\n');
            sb.Append("render_object_height_scale=")
                .Append(Math.Clamp(state.RenderObjectHeightScale, 0.0f, 8.0f).ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append("texture_max_cache_items=").Append(Math.Clamp(state.TextureMaxCacheItems, 16, 32768).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("texture_submit_budget_per_frame=").Append(Math.Clamp(state.TextureSubmitBudgetPerFrame, 1, 8192).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("texture_create_budget_per_frame=").Append(Math.Clamp(state.TextureCreateBudgetPerFrame, 1, 1024).ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.Append("stamp_path=").Append(state.StampPath ?? string.Empty).Append('\n');
            sb.Append("stamp_anchor=").Append(state.StampAnchor == StampAnchorMode.TopLeft ? "top_left" : "center").Append('\n');
            sb.Append("stamp_overwrite_empty=").Append(state.StampOverwriteEmpty ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_back=").Append(state.StampApplyBack ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_middle=").Append(state.StampApplyMiddle ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_front=").Append(state.StampApplyFront ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_under_object=").Append(state.StampApplyUnderObject ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_over_object=").Append(state.StampApplyOverObject ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_near_ground=").Append(state.StampApplyNearGround ? "1" : "0").Append('\n');
            sb.Append("stamp_apply_blocked=").Append(state.StampApplyBlocked ? "1" : "0").Append('\n');

            sb.Append("move_apply_back=").Append(state.MoveApplyBack ? "1" : "0").Append('\n');
            sb.Append("move_apply_middle=").Append(state.MoveApplyMiddle ? "1" : "0").Append('\n');
            sb.Append("move_apply_front=").Append(state.MoveApplyFront ? "1" : "0").Append('\n');
            sb.Append("move_apply_under_object=").Append(state.MoveApplyUnderObject ? "1" : "0").Append('\n');
            sb.Append("move_apply_over_object=").Append(state.MoveApplyOverObject ? "1" : "0").Append('\n');
            sb.Append("move_apply_near_ground=").Append(state.MoveApplyNearGround ? "1" : "0").Append('\n');
            sb.Append("move_apply_blocked=").Append(state.MoveApplyBlocked ? "1" : "0").Append('\n');

            sb.Append("erase_apply_back=").Append(state.EraseApplyBack ? "1" : "0").Append('\n');
            sb.Append("erase_apply_middle=").Append(state.EraseApplyMiddle ? "1" : "0").Append('\n');
            sb.Append("erase_apply_front=").Append(state.EraseApplyFront ? "1" : "0").Append('\n');
            sb.Append("erase_apply_under_object=").Append(state.EraseApplyUnderObject ? "1" : "0").Append('\n');
            sb.Append("erase_apply_over_object=").Append(state.EraseApplyOverObject ? "1" : "0").Append('\n');
            sb.Append("erase_apply_near_ground=").Append(state.EraseApplyNearGround ? "1" : "0").Append('\n');
            sb.Append("erase_apply_blocked=").Append(state.EraseApplyBlocked ? "1" : "0").Append('\n');

            sb.Append("minimap_export_path=").Append(state.MinimapExportPath ?? string.Empty).Append('\n');
            sb.Append("minimap_scale=").Append(Math.Clamp(state.MinimapScale, 1, 32).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_include_back=").Append(state.MinimapIncludeBack ? "1" : "0").Append('\n');
            sb.Append("minimap_include_middle=").Append(state.MinimapIncludeMiddle ? "1" : "0").Append('\n');
            sb.Append("minimap_include_floor=").Append(state.MinimapIncludeFloor ? "1" : "0").Append('\n');
            sb.Append("minimap_include_underfront=").Append(state.MinimapIncludeUnderFront ? "1" : "0").Append('\n');
            sb.Append("minimap_include_front=").Append(state.MinimapIncludeFront ? "1" : "0").Append('\n');
            sb.Append("minimap_include_overfront=").Append(state.MinimapIncludeOverFront ? "1" : "0").Append('\n');
            sb.Append("minimap_include_dynscene=").Append(state.MinimapIncludeDynamicScene ? "1" : "0").Append('\n');
            sb.Append("minimap_include_effects=").Append(state.MinimapIncludeAttachedEffects ? "1" : "0").Append('\n');
            sb.Append("minimap_overlay_map_id=").Append(state.MinimapOverlayMapIdOverride ?? string.Empty).Append('\n');
            sb.Append("minimap_effects_map_id=").Append(state.MinimapAttachedEffectsMapIdOverride ?? string.Empty).Append('\n');
            sb.Append("minimap_overlay_layout=").Append(state.MinimapOverlayLayoutPath ?? string.Empty).Append('\n');
            sb.Append("minimap_effects_layout=").Append(state.MinimapAttachedEffectsLayoutPath ?? string.Empty).Append('\n');
            sb.Append("minimap_overlay_max_decompressed_bytes=")
                .Append(Math.Clamp(state.MinimapOverlayMaxDecompressedBytes, 0, 2L * 1024 * 1024 * 1024).ToString(CultureInfo.InvariantCulture))
                .Append('\n');
            sb.Append("minimap_separate_layer_files=").Append(state.MinimapSeparateLayerFiles ? "1" : "0").Append('\n');
            sb.Append("minimap_use_textures=").Append(state.MinimapUseTextures ? "1" : "0").Append('\n');
            sb.Append("minimap_suppress_border=").Append(state.MinimapSuppressBorderCells ? "1" : "0").Append('\n');
            sb.Append("minimap_apply_cell_tints=").Append(state.MinimapApplyCellTints ? "1" : "0").Append('\n');
            float tintStrength = state.MinimapTintStrength;
            if (!float.IsFinite(tintStrength))
            {
                tintStrength = 0.35f;
            }
            tintStrength = Math.Clamp(tintStrength, 0.0f, 1.0f);
            sb.Append("minimap_tint_strength=").Append(tintStrength.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_apply_height_flag=").Append(state.MinimapApplyCellHeightFlag ? "1" : "0").Append('\n');
            float cellHeightOffset = state.MinimapCellHeightFlagOffset;
            if (!float.IsFinite(cellHeightOffset))
            {
                cellHeightOffset = 0.0f;
            }
            cellHeightOffset = Math.Max(0.0f, cellHeightOffset);
            sb.Append("minimap_cell_height_offset=").Append(cellHeightOffset.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_apply_object_height=").Append(state.MinimapApplyObjectHeight ? "1" : "0").Append('\n');
            float objectHeightScale = state.MinimapObjectHeightScale;
            if (!float.IsFinite(objectHeightScale))
            {
                objectHeightScale = 0.0f;
            }
            objectHeightScale = Math.Max(0.0f, objectHeightScale);
            sb.Append("minimap_object_height_scale=").Append(objectHeightScale.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_apply_luminance_to_alpha=").Append(state.MinimapApplyLuminanceToAlpha ? "1" : "0").Append('\n');
            sb.Append("minimap_crop_mode=").Append(state.MinimapCropMode switch
            {
                MinimapCropMode.CellRect => "cell_rect",
                MinimapCropMode.PixelRect => "pixel_rect",
                MinimapCropMode.AutoNonEmptyCells => "auto_non_empty_cells",
                _ => "none",
            }).Append('\n');
            sb.Append("minimap_crop_cell_x=").Append(Math.Max(0, state.MinimapCropCellX).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_cell_y=").Append(Math.Max(0, state.MinimapCropCellY).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_cell_width=").Append(Math.Max(1, state.MinimapCropCellWidth).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_cell_height=").Append(Math.Max(1, state.MinimapCropCellHeight).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_pixel_x=").Append(Math.Max(0, state.MinimapCropPixelX).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_pixel_y=").Append(Math.Max(0, state.MinimapCropPixelY).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_pixel_width=").Append(Math.Max(1, state.MinimapCropPixelWidth).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_crop_pixel_height=").Append(Math.Max(1, state.MinimapCropPixelHeight).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_auto_crop_padding_cells=").Append(Math.Max(0, state.MinimapAutoCropPaddingCells).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("minimap_batch_input_dir=").Append(state.MinimapBatchInputDirectory ?? string.Empty).Append('\n');
            sb.Append("minimap_batch_output_dir=").Append(state.MinimapBatchOutputDirectory ?? string.Empty).Append('\n');
            sb.Append("minimap_batch_recursive=").Append(state.MinimapBatchRecursive ? "1" : "0").Append('\n');
            sb.Append("minimap_batch_overwrite=").Append(state.MinimapBatchOverwrite ? "1" : "0").Append('\n');
            sb.Append("minimap_batch_include_scale_tag=").Append(state.MinimapBatchIncludeScaleTag ? "1" : "0").Append('\n');

            AppendLighting(sb,
                prefix: "render",
                applyLightingOverlay: state.RenderApplyLightingOverlay,
                overlayMaxAlpha: state.RenderLightingOverlayMaxAlpha,
                includeLightSprites: state.RenderIncludeLightSprites,
                lighting: state.RenderLighting);

            AppendLighting(sb,
                prefix: "minimap",
                applyLightingOverlay: state.MinimapApplyLightingOverlay,
                overlayMaxAlpha: state.MinimapLightingOverlayMaxAlpha,
                includeLightSprites: state.MinimapIncludeLightSprites,
                lighting: state.MinimapLighting);

            AppendOpenMaps(sb, state.RestoreOpenMapPaths);

            text = sb.ToString();

            if (writeToFile)
            {
                string path = filePath ?? string.Empty;
                string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, text, Utf8NoBom);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = writeToFile
                ? $"写入偏好设置失败: {ex.Message}"
                : $"生成偏好设置文本失败: {ex.Message}";
            return false;
        }
    }

    private static string BuildLegacyMigrationNote(
        string filePath,
        int mapFolderCount,
        int dataFolderCount,
        string firstMapFolder,
        string firstDataFolder)
    {
        var sb = new StringBuilder();
        sb.Append("已兼容读取旧版 settings.cfg：").Append(filePath);

        if (mapFolderCount > 0)
        {
            sb.Append("；Map Paths 共 ").Append(mapFolderCount).Append(" 条");
            if (!string.IsNullOrWhiteSpace(firstMapFolder))
            {
                sb.Append("，已迁移列表并以首条作为默认浏览根目录");
            }
        }

        if (dataFolderCount > 0)
        {
            sb.Append("；Data Paths 共 ").Append(dataFolderCount).Append(" 条");
            if (!string.IsNullOrWhiteSpace(firstDataFolder))
            {
                sb.Append("，已迁移列表并以首条作为默认贴图库根目录");
            }
        }

        sb.Append("；已迁移：restore_state/open_map/active_map_index、unload_inactive_tabs、show_*_panel、prefab_*、texture_source_mode/coast_mask_source、keybind.*、luminance_*、ClientParity 渲染选项等。");
        return sb.ToString();
    }

    private static int NormalizeNamedPathSelection(
        List<NamedPathEntry> entries,
        int selectedIndex,
        string currentPath,
        Action<string> applySelectedPath)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (applySelectedPath is null) throw new ArgumentNullException(nameof(applySelectedPath));

        if (entries.Count == 0)
        {
            return -1;
        }

        if (selectedIndex < 0 || selectedIndex >= entries.Count)
        {
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (string.Equals(entries[i].Path, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            if (selectedIndex < 0 || selectedIndex >= entries.Count)
            {
                selectedIndex = 0;
            }
        }

        if (string.IsNullOrWhiteSpace(currentPath))
        {
            applySelectedPath(entries[selectedIndex].Path);
        }

        return selectedIndex;
    }

    private static bool TryParseLegacyBool(string value, out bool result)
    {
        result = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized == "1" || normalized.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (normalized == "0" || normalized.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        return false;
    }

    private static void AppendNamedPathEntries(StringBuilder sb, string prefix, IEnumerable<NamedPathEntry> entries)
    {
        if (sb is null) throw new ArgumentNullException(nameof(sb));
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(prefix));

        foreach (NamedPathEntry entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? Path.GetFileName(entry.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : entry.DisplayName;

            sb.Append(prefix)
                .Append(' ')
                .Append(QuoteLegacyString(displayName))
                .Append(' ')
                .Append(QuoteLegacyString(entry.Path))
                .Append('\n');
        }
    }

    private static void AppendOpenMaps(StringBuilder sb, IEnumerable<string> paths)
    {
        if (sb is null) throw new ArgumentNullException(nameof(sb));
        if (paths is null) throw new ArgumentNullException(nameof(paths));

        foreach (string p in paths)
        {
            string path = p?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            sb.Append("open_map ").Append(QuoteLegacyString(path)).Append('\n');
        }
    }

    private static string QuoteLegacyString(string value)
    {
        string normalized = value ?? string.Empty;
        normalized = normalized.Replace("\\", "\\\\", StringComparison.Ordinal);
        normalized = normalized.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{normalized}\"";
    }

    private static bool TryParseInvariantFloat(string value, out float result)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static bool TryParseVector4Rgba(string value, out Vector4 rgba)
    {
        rgba = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        if (!TryParseInvariantFloat(parts[0], out float r)
            || !TryParseInvariantFloat(parts[1], out float g)
            || !TryParseInvariantFloat(parts[2], out float b)
            || !TryParseInvariantFloat(parts[3], out float a))
        {
            return false;
        }

        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(a))
        {
            return false;
        }

        rgba = new Vector4(Clamp01(r), Clamp01(g), Clamp01(b), Clamp01(a));
        return true;
    }

    private static string SerializeVector4Rgba(Vector4 rgba)
    {
        float r = Clamp01(rgba.X);
        float g = Clamp01(rgba.Y);
        float b = Clamp01(rgba.Z);
        float a = Clamp01(rgba.W);
        return string.Concat(
            r.ToString("0.###", CultureInfo.InvariantCulture), ",",
            g.ToString("0.###", CultureInfo.InvariantCulture), ",",
            b.ToString("0.###", CultureInfo.InvariantCulture), ",",
            a.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static float Clamp01(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0.0f;
        }

        return Math.Clamp(value, 0.0f, 1.0f);
    }

    private static string ToTextureSourceModeKey(TextureSourceMode mode)
        => mode switch
        {
            TextureSourceMode.WpfOnly => "wpf_only",
            TextureSourceMode.SglOnly => "sgl_only",
            _ => "wpf_sgl_fallback",
        };

    private static bool TryParseTextureSourceMode(string value, out TextureSourceMode mode)
    {
        mode = TextureSourceMode.WpfSglFallback;

        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string lower = trimmed.ToLowerInvariant();
        if (lower == "wpf_sgl_fallback")
        {
            mode = TextureSourceMode.WpfSglFallback;
            return true;
        }

        if (lower == "wpf_only")
        {
            mode = TextureSourceMode.WpfOnly;
            return true;
        }

        if (lower == "sgl_only")
        {
            mode = TextureSourceMode.SglOnly;
            return true;
        }

        return false;
    }

    private static string ToSettingsSectionKey(MapEditorSettingsSection section)
        => section switch
        {
            MapEditorSettingsSection.Application => "application",
            MapEditorSettingsSection.MapPaths => "map_paths",
            MapEditorSettingsSection.DataPaths => "data_paths",
            MapEditorSettingsSection.Assets => "assets",
            MapEditorSettingsSection.ClientParity => "client_parity",
            MapEditorSettingsSection.KeyBindings => "key_bindings",
            MapEditorSettingsSection.Luminance => "luminance",
            _ => "defaults",
        };

    private static bool TryParseSettingsSection(string value, out MapEditorSettingsSection section)
    {
        section = MapEditorSettingsSection.Defaults;

        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        string lower = trimmed.ToLowerInvariant();
        if (lower == "defaults")
        {
            section = MapEditorSettingsSection.Defaults;
            return true;
        }

        if (lower == "application")
        {
            section = MapEditorSettingsSection.Application;
            return true;
        }

        if (lower == "map_paths")
        {
            section = MapEditorSettingsSection.MapPaths;
            return true;
        }

        if (lower == "data_paths")
        {
            section = MapEditorSettingsSection.DataPaths;
            return true;
        }

        if (lower == "assets")
        {
            section = MapEditorSettingsSection.Assets;
            return true;
        }

        if (lower == "client_parity")
        {
            section = MapEditorSettingsSection.ClientParity;
            return true;
        }

        if (lower == "key_bindings")
        {
            section = MapEditorSettingsSection.KeyBindings;
            return true;
        }

        if (lower == "luminance")
        {
            section = MapEditorSettingsSection.Luminance;
            return true;
        }

        return false;
    }

    private static LuminanceSettings WithLuminanceSettings(
        LuminanceSettings current,
        LuminanceMode? mode = null,
        AlphaBlendMode? blendMode = null,
        float? gamma = null,
        float? contrast = null,
        byte? threshold = null,
        bool? inverted = null)
    {
        return new LuminanceSettings
        {
            Mode = mode ?? current.Mode,
            BlendMode = blendMode ?? current.BlendMode,
            Gamma = gamma ?? current.Gamma,
            Contrast = contrast ?? current.Contrast,
            Threshold = threshold ?? current.Threshold,
            Inverted = inverted ?? current.Inverted,
        };
    }

    private static bool TryParseKeyBinding(string value, out KeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        int commaPos = trimmed.IndexOf(',', StringComparison.Ordinal);

        string keyPart = commaPos >= 0 ? trimmed.Substring(0, commaPos) : trimmed;
        string modsPart = commaPos >= 0 && commaPos + 1 < trimmed.Length ? trimmed.Substring(commaPos + 1) : "0";

        if (!int.TryParse(keyPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keyInt))
        {
            return false;
        }

        int modsInt = 0;
        if (commaPos >= 0)
        {
            _ = int.TryParse(modsPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out modsInt);
        }

        keyInt = Math.Max(0, keyInt);
        modsInt = Math.Max(0, modsInt);
        binding = new KeyBinding((ImGuiKey)keyInt, (KeyModFlags)modsInt);
        return true;
    }

    private static void ApplyKeyBinding(MapEditorKeyBindings keyBindings, string action, KeyBinding binding)
    {
        if (keyBindings is null) throw new ArgumentNullException(nameof(keyBindings));
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        switch (action.Trim())
        {
            case "tool_blocked_editor":
                keyBindings.ToolBlockedEditor = binding;
                return;
            case "tool_selection":
                keyBindings.ToolSelection = binding;
                return;
            case "tool_erase":
                keyBindings.ToolErase = binding;
                return;
            case "tool_stamp":
                keyBindings.ToolStamp = binding;
                return;
            case "tool_tile_paint":
                keyBindings.ToolTilePaint = binding;
                return;
            case "tool_cancel":
                keyBindings.ToolCancel = binding;
                return;
            case "delete_selection":
                keyBindings.DeleteSelection = binding;
                return;
            case "undo":
                keyBindings.Undo = binding;
                return;
            case "redo":
                keyBindings.Redo = binding;
                return;
            case "save":
                keyBindings.Save = binding;
                return;
            case "zoom_in":
                keyBindings.ZoomIn = binding;
                return;
            case "zoom_out":
                keyBindings.ZoomOut = binding;
                return;
            case "reset_view":
                keyBindings.ResetView = binding;
                return;
            default:
                return;
        }
    }

    private static void AppendKeyBinding(StringBuilder sb, string action, KeyBinding binding)
    {
        if (sb is null) throw new ArgumentNullException(nameof(sb));

        sb.Append("keybind.")
            .Append(action ?? string.Empty)
            .Append('=')
            .Append(((int)binding.Key).ToString(CultureInfo.InvariantCulture))
            .Append(',')
            .Append(((int)binding.Mods).ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static bool TryParseLegacyQuotedPair(string value, out string first, out string second)
    {
        first = string.Empty;
        second = string.Empty;

        int index = 0;
        if (!TryReadLegacyQuotedString(value, ref index, out first))
        {
            return false;
        }

        if (!TryReadLegacyQuotedString(value, ref index, out second))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseLegacyQuotedSingle(string value, out string parsed)
    {
        parsed = string.Empty;

        int index = 0;
        return TryReadLegacyQuotedString(value, ref index, out parsed);
    }

    private static bool TryReadLegacyQuotedString(string value, ref int index, out string parsed)
    {
        parsed = string.Empty;
        if (value is null)
        {
            return false;
        }

        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        if (index >= value.Length || value[index] != '"')
        {
            return false;
        }

        index++;
        var sb = new StringBuilder();
        while (index < value.Length)
        {
            char c = value[index++];
            if (c == '\\' && index < value.Length)
            {
                sb.Append(value[index++]);
                continue;
            }

            if (c == '"')
            {
                parsed = sb.ToString();
                return true;
            }

            sb.Append(c);
        }

        return false;
    }

    private static void AppendLighting(
        StringBuilder sb,
        string prefix,
        bool applyLightingOverlay,
        int overlayMaxAlpha,
        bool includeLightSprites,
        MapLightingSettings lighting)
    {
        if (sb is null) throw new ArgumentNullException(nameof(sb));
        if (lighting is null) throw new ArgumentNullException(nameof(lighting));
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(prefix));

        sb.Append(prefix).Append("_apply_lighting_overlay=").Append(applyLightingOverlay ? "1" : "0").Append('\n');
        sb.Append(prefix).Append("_lighting_overlay_max_alpha=")
            .Append(Math.Clamp(overlayMaxAlpha, 0, 255).ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        sb.Append(prefix).Append("_include_light_sprites=").Append(includeLightSprites ? "1" : "0").Append('\n');

        sb.Append(prefix).Append("_lighting_mode=").Append(SerializeLightingMode(lighting.Mode)).Append('\n');
        sb.Append(prefix).Append("_lighting_custom_hour=").Append(Math.Clamp(lighting.CustomHour, 0, 23).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(prefix).Append("_lighting_custom_minute=").Append(Math.Clamp(lighting.CustomMinute, 0, 59).ToString(CultureInfo.InvariantCulture)).Append('\n');

        float manualFactor = lighting.ManualNightFactor;
        if (!float.IsFinite(manualFactor))
        {
            manualFactor = 0.0f;
        }
        manualFactor = Math.Clamp(manualFactor, 0.0f, 1.0f);
        sb.Append(prefix).Append("_lighting_manual_factor=").Append(manualFactor.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string SerializeLightingMode(MapLightingMode mode)
        => mode switch
        {
            MapLightingMode.Day => "day",
            MapLightingMode.Night => "night",
            MapLightingMode.Auto => "auto",
            MapLightingMode.CustomTime => "custom_time",
            MapLightingMode.Manual => "manual",
            _ => "day",
        };

    private static bool TryParseLightingMode(string value, out MapLightingMode mode)
    {
        mode = MapLightingMode.Day;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim())
        {
            case "day":
                mode = MapLightingMode.Day;
                return true;
            case "night":
                mode = MapLightingMode.Night;
                return true;
            case "auto":
                mode = MapLightingMode.Auto;
                return true;
            case "custom_time":
                mode = MapLightingMode.CustomTime;
                return true;
            case "manual":
                mode = MapLightingMode.Manual;
                return true;
            default:
                return false;
        }
    }
}
