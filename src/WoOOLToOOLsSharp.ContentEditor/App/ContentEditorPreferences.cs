using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WoOOLToOOLsSharp.ContentEditor.App;

public static class ContentEditorPreferences
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static bool TryLoad(string filePath, EditorState state, out string error)
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

        state.DataFolders.Clear();
        state.PendingTabRestore.Clear();
        state.PendingActiveTabIndex = -1;
        state.RecentFiles.Clear();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("ui_scale=", StringComparison.Ordinal))
            {
                string value = line["ui_scale=".Length..];
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    && parsed is >= 0.5f and <= 3.0f)
                {
                    state.UiScale = parsed;
                }
                continue;
            }

            if (line.StartsWith("grid_cell_size=", StringComparison.Ordinal))
            {
                string value = line["grid_cell_size=".Length..];
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 32 and <= 128)
                {
                    state.GridCellSize = parsed;
                }
                continue;
            }

            if (line.StartsWith("theme=", StringComparison.Ordinal))
            {
                string value = line["theme=".Length..];
                state.Theme = value == "light" ? UiTheme.Light : UiTheme.Dark;
                continue;
            }

            if (line.StartsWith("preview_max_side=", StringComparison.Ordinal))
            {
                string value = line["preview_max_side=".Length..];
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 64 and <= 4096)
                {
                    state.PreviewMaxSide = parsed;
                }
                continue;
            }

            if (line.StartsWith("preview_cache_max_items=", StringComparison.Ordinal))
            {
                string value = line["preview_cache_max_items=".Length..];
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    && parsed is >= 8 and <= 2048)
                {
                    state.PreviewCacheMaxItems = parsed;
                }
                continue;
            }

            if (line.StartsWith("restore_state=", StringComparison.Ordinal))
            {
                string value = line["restore_state=".Length..];
                state.RestoreState = value == "1";
                continue;
            }

            if (line.StartsWith("show_data_folders_panel=", StringComparison.Ordinal))
            {
                string value = line["show_data_folders_panel=".Length..];
                state.ShowDataFoldersPanel = value == "1";
                continue;
            }

            if (line.StartsWith("show_information_panel=", StringComparison.Ordinal))
            {
                string value = line["show_information_panel=".Length..];
                state.ShowInformationPanel = value == "1";
                continue;
            }

            if (line.StartsWith("show_library_textures_panel=", StringComparison.Ordinal))
            {
                string value = line["show_library_textures_panel=".Length..];
                state.ShowLibraryTexturesPanel = value == "1";
                continue;
            }

            if (line.StartsWith("settings_section=", StringComparison.Ordinal))
            {
                string value = line["settings_section=".Length..];
                state.CurrentSettingsSection = value == "data_paths"
                    ? SettingsSection.DataPaths
                    : SettingsSection.Application;
                continue;
            }

            if (line.StartsWith("open_tab=", StringComparison.Ordinal))
            {
                string payload = line["open_tab=".Length..];
                int split = FindDelimiter(payload);
                if (split < 0) continue;

                string title = Unescape(payload[..split]);
                string sglKey = Unescape(payload[(split + 1)..]);
                state.PendingTabRestore.Add(new SavedTabInfo(title, sglKey));
                continue;
            }

            if (line.StartsWith("active_tab_index=", StringComparison.Ordinal))
            {
                string value = line["active_tab_index=".Length..];
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    state.PendingActiveTabIndex = parsed;
                }
                continue;
            }

            if (line.StartsWith("data_folder=", StringComparison.Ordinal))
            {
                string payload = line["data_folder=".Length..];
                int split = FindDelimiter(payload);
                if (split < 0) continue;

                state.DataFolders.Add(new DataFolder
                {
                    DisplayName = Unescape(payload[..split]),
                    Path = Unescape(payload[(split + 1)..]),
                });
                continue;
            }

            if (line.StartsWith("recent_file=", StringComparison.Ordinal))
            {
                string payload = line["recent_file=".Length..];
                string path = Unescape(payload);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    state.RecentFiles.Add(path);
                }
                continue;
            }
        }

        state.DataFoldersDirty = true;
        state.PreferencesDirty = false;
        return true;
    }

    public static bool TrySave(string filePath, EditorState state, out string error)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        error = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "偏好设置路径为空";
            return false;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append("ui_scale=").Append(state.UiScale.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("grid_cell_size=").Append(state.GridCellSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("theme=").Append(state.Theme == UiTheme.Light ? "light" : "dark").Append('\n');
            sb.Append("preview_max_side=").Append(state.PreviewMaxSide.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("preview_cache_max_items=").Append(state.PreviewCacheMaxItems.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("restore_state=").Append(state.RestoreState ? "1" : "0").Append('\n');
            sb.Append("show_data_folders_panel=").Append(state.ShowDataFoldersPanel ? "1" : "0").Append('\n');
            sb.Append("show_information_panel=").Append(state.ShowInformationPanel ? "1" : "0").Append('\n');
            sb.Append("show_library_textures_panel=").Append(state.ShowLibraryTexturesPanel ? "1" : "0").Append('\n');
            sb.Append("settings_section=").Append(state.CurrentSettingsSection == SettingsSection.DataPaths ? "data_paths" : "application").Append('\n');

            foreach (DataFolder folder in state.DataFolders)
            {
                sb.Append("data_folder=").Append(Escape(folder.DisplayName)).Append('|').Append(Escape(folder.Path)).Append('\n');
            }

            int recentLimit = Math.Min(state.RecentFiles.Count, 20);
            for (int i = 0; i < recentLimit; i++)
            {
                sb.Append("recent_file=").Append(Escape(state.RecentFiles[i])).Append('\n');
            }

            if (state.RestoreState)
            {
                foreach (ImageTab tab in state.Tabs)
                {
                    if (!tab.Open) continue;
                    sb.Append("open_tab=").Append(Escape(tab.Title)).Append('|').Append(Escape(tab.SglKey)).Append('\n');
                }

                sb.Append("active_tab_index=").Append(state.ActiveTabIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(filePath, sb.ToString(), Utf8NoBom);
            return true;
        }
        catch (Exception ex)
        {
            error = $"写入偏好设置失败: {ex.Message}";
            return false;
        }
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '\\' or '|')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        bool escaped = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static int FindDelimiter(string value)
    {
        bool escaped = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == '|')
            {
                return i;
            }
        }
        return -1;
    }
}
