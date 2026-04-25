using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using ImGuiNET;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public enum SimpleFileDialogMode
{
    OpenFile = 0,
    OpenFolder = 1,
    SaveFile = 2,
    OpenFileOrFolder = 3,
}

public enum SimpleFileDialogResult
{
    None = 0,
    Ok = 1,
    Cancel = 2,
}

/// <summary>
/// 一个轻量文件/文件夹选择器：Windows 默认使用系统原生对话框，其它平台回退为 ImGui 内置弹窗。
/// </summary>
public sealed class SimpleFileDialog
{
    private static int _nextDialogId;
    private readonly string _popupName = $"文件选择##woool_simple_file_dialog_{Interlocked.Increment(ref _nextDialogId)}";

    private sealed class Entry
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
    }

    private bool _openRequested;
    private bool _justOpened;

    private SimpleFileDialogMode _mode;
    private string _title = string.Empty;

    private string _currentDirectory = string.Empty;
    private string _pathInput = string.Empty;
    private string _filenameInput = string.Empty;

    private readonly List<Entry> _entries = new();
    private bool _entriesDirty = true;
    private int _selectedIndex = -1;
    private string _lastError = string.Empty;

    private readonly object _resultLock = new();
    private int _openRequestId;

    private SimpleFileDialogResult _result = SimpleFileDialogResult.None;
    private string _selectedPath = string.Empty;

    public bool IsOpen { get; private set; }
    public SimpleFileDialogResult Result => _result;
    public string SelectedPath => _selectedPath;

    public void Open(SimpleFileDialogMode mode, string title, string? startDirectory, string? defaultFilename = null)
    {
        _mode = mode;
        _title = title ?? string.Empty;

        lock (_resultLock)
        {
            _result = SimpleFileDialogResult.None;
            _selectedPath = string.Empty;
        }

        _lastError = string.Empty;

        _selectedIndex = -1;
        _entries.Clear();
        _entriesDirty = true;

        _currentDirectory = NormalizeDirectory(startDirectory);
        if (string.IsNullOrWhiteSpace(_currentDirectory))
        {
            _currentDirectory = GetDefaultStartDirectory();
        }

        _pathInput = _currentDirectory;
        _filenameInput = defaultFilename ?? string.Empty;

        IsOpen = true;

        // Windows 下优先走原生对话框（不阻塞渲染线程：在 STA 后台线程中打开，结果异步回填）。
        int requestId = Interlocked.Increment(ref _openRequestId);
        if (OperatingSystem.IsWindows()
            && TryStartWindowsNativeDialog(requestId, mode, _title, _currentDirectory, defaultFilename))
        {
            _openRequested = false;
            _justOpened = false;
            return;
        }

        _openRequested = true;
        _justOpened = true;
    }

    public bool TryConsumeResult(out SimpleFileDialogResult result, out string selectedPath)
    {
        lock (_resultLock)
        {
            result = _result;
            selectedPath = _selectedPath;
            if (result == SimpleFileDialogResult.None)
            {
                return false;
            }

            _result = SimpleFileDialogResult.None;
            _selectedPath = string.Empty;
            return true;
        }
    }

    public void Draw()
    {
        if (!IsOpen && !_openRequested)
        {
            return;
        }

        if (_openRequested)
        {
            ImGui.OpenPopup(_popupName);
            _openRequested = false;
        }

        bool open = true;
        ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize;
        if (!ImGui.BeginPopupModal(_popupName, ref open, flags))
        {
            return;
        }

        if (!open)
        {
            Close(SimpleFileDialogResult.Cancel, string.Empty);
            ImGui.EndPopup();
            return;
        }

        DrawHeader();
        DrawPathBar();
        DrawEntries();
        DrawFooter();

        if (_justOpened)
        {
            ImGui.SetKeyboardFocusHere(-1);
            _justOpened = false;
        }

        ImGui.EndPopup();
    }

    private void DrawHeader()
    {
        if (!string.IsNullOrWhiteSpace(_title))
        {
            ImGui.TextUnformatted(_title);
            ImGui.Separator();
        }
    }

    private void DrawPathBar()
    {
        bool hasCurrent = !string.IsNullOrWhiteSpace(_currentDirectory);

        if (!hasCurrent)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("上一级"))
        {
            GoUp();
        }

        if (!hasCurrent)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        ImGui.SetNextItemWidth(520.0f);
        ImGui.InputText("路径", ref _pathInput, 2048);

        ImGui.SameLine();
        if (ImGui.Button("转到"))
        {
            NavigateTo(_pathInput);
        }

        if (_mode == SimpleFileDialogMode.SaveFile)
        {
            ImGui.SetNextItemWidth(520.0f);
            ImGui.InputText("文件名", ref _filenameInput, 256);
        }

        if (!string.IsNullOrWhiteSpace(_lastError))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), _lastError);
        }
    }

    private void DrawEntries()
    {
        if (_entriesDirty)
        {
            RefreshEntries();
        }

        ImGui.BeginChild("##file_dialog_entries", new Vector2(760.0f, 320.0f), ImGuiChildFlags.None);

        if (_entries.Count == 0)
        {
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(_currentDirectory) ? "没有可用的根路径。" : "目录为空或无法读取。");
            ImGui.EndChild();
            return;
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];

            bool selectable = _mode switch
            {
                SimpleFileDialogMode.OpenFolder => e.IsDirectory,
                SimpleFileDialogMode.OpenFile => !e.IsDirectory,
                SimpleFileDialogMode.SaveFile => true,
                SimpleFileDialogMode.OpenFileOrFolder => true,
                _ => true,
            };

            if (!selectable)
            {
                ImGui.BeginDisabled();
            }

            bool selected = i == _selectedIndex;

            string label = e.IsDirectory ? $"{e.Name}/" : e.Name;
            if (ImGui.Selectable(label, selected))
            {
                _selectedIndex = i;
            }

            if (!selectable)
            {
                ImGui.EndDisabled();
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                if (e.IsDirectory)
                {
                    NavigateTo(e.FullPath);
                }
                else if (_mode == SimpleFileDialogMode.OpenFile)
                {
                    Close(SimpleFileDialogResult.Ok, e.FullPath);
                    ImGui.CloseCurrentPopup();
                    break;
                }
                else if (_mode == SimpleFileDialogMode.OpenFileOrFolder && !e.IsDirectory)
                {
                    Close(SimpleFileDialogResult.Ok, e.FullPath);
                    ImGui.CloseCurrentPopup();
                    break;
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawFooter()
    {
        string preview = BuildPreviewPath();
        if (!string.IsNullOrWhiteSpace(preview))
        {
            ImGui.TextUnformatted($"选择：{preview}");
        }

        bool canOk = CanConfirmSelection(out string okPath);
        if (!canOk)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("确定"))
        {
            Close(SimpleFileDialogResult.Ok, okPath);
            ImGui.CloseCurrentPopup();
        }

        if (!canOk)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            Close(SimpleFileDialogResult.Cancel, string.Empty);
            ImGui.CloseCurrentPopup();
        }
    }

    private bool CanConfirmSelection(out string outPath)
    {
        outPath = string.Empty;

        string? current = NormalizeDirectory(_currentDirectory);

        if (_mode == SimpleFileDialogMode.OpenFolder)
        {
            if (TryGetSelected(out Entry? selected) && selected is not null && selected.IsDirectory)
            {
                outPath = selected.FullPath;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                outPath = current;
                return true;
            }

            return false;
        }

        if (_mode == SimpleFileDialogMode.OpenFile)
        {
            if (!TryGetSelected(out Entry? selected) || selected is null || selected.IsDirectory)
            {
                return false;
            }

            outPath = selected.FullPath;
            return true;
        }

        if (_mode == SimpleFileDialogMode.OpenFileOrFolder)
        {
            if (TryGetSelected(out Entry? selected) && selected is not null)
            {
                outPath = selected.FullPath;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                outPath = current;
                return true;
            }

            return false;
        }

        // SaveFile
        if (string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        string filename = (_filenameInput ?? string.Empty).Trim();
        if (filename.Length == 0)
        {
            return false;
        }

        try
        {
            outPath = Path.Combine(current, filename);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string BuildPreviewPath()
    {
        if (_mode == SimpleFileDialogMode.SaveFile)
        {
            if (CanConfirmSelection(out string okPath))
            {
                return okPath;
            }

            return string.Empty;
        }

        if (TryGetSelected(out Entry? selected) && selected is not null)
        {
            return selected.FullPath;
        }

        return string.Empty;
    }

    private bool TryGetSelected(out Entry? entry)
    {
        entry = null;
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count)
        {
            return false;
        }

        entry = _entries[_selectedIndex];
        return entry is not null;
    }

    private void Close(SimpleFileDialogResult result, string selectedPath)
    {
        lock (_resultLock)
        {
            _result = result;
            _selectedPath = selectedPath ?? string.Empty;
        }
        IsOpen = false;

        _entries.Clear();
        _entriesDirty = false;
        _selectedIndex = -1;
        _lastError = string.Empty;
    }

    private bool TryStartWindowsNativeDialog(
        int requestId,
        SimpleFileDialogMode mode,
        string title,
        string startDirectory,
        string? defaultFilename)
    {
        try
        {
            Thread t = new(() =>
            {
                try
                {
                    bool handled = WindowsNativeFileDialog.TryShow(
                        mode,
                        title,
                        startDirectory,
                        defaultFilename,
                        out SimpleFileDialogResult nativeResult,
                        out string nativeSelectedPath);

                    // 若期间又打开了新的对话框，则丢弃旧结果。
                    if (requestId != Volatile.Read(ref _openRequestId))
                    {
                        return;
                    }

                    if (!handled)
                    {
                        // 原生失败：回退到 ImGui 弹窗（在主线程 Draw() 中执行 OpenPopup）。
                        Volatile.Write(ref _openRequested, true);
                        Volatile.Write(ref _justOpened, true);
                        return;
                    }

                    lock (_resultLock)
                    {
                        _result = nativeResult;
                        _selectedPath = nativeSelectedPath ?? string.Empty;
                    }

                    IsOpen = false;
                }
                catch
                {
                    if (requestId != Volatile.Read(ref _openRequestId))
                    {
                        return;
                    }

                    Volatile.Write(ref _openRequested, true);
                    Volatile.Write(ref _justOpened, true);
                }
            })
            {
                IsBackground = true,
                Name = "WOOOL Native File Dialog",
            };

            // 平台兼容性分析器需要显式的 Windows 守卫。
            if (OperatingSystem.IsWindows())
            {
                t.SetApartmentState(ApartmentState.STA);
            }
            t.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void GoUp()
    {
        if (string.IsNullOrWhiteSpace(_currentDirectory))
        {
            return;
        }

        try
        {
            DirectoryInfo? parent = Directory.GetParent(_currentDirectory);
            if (parent is null)
            {
                // Windows drive root: back to drive list
                _currentDirectory = string.Empty;
                _pathInput = string.Empty;
                _entriesDirty = true;
                _selectedIndex = -1;
                return;
            }

            NavigateTo(parent.FullName);
        }
        catch
        {
            _currentDirectory = string.Empty;
            _pathInput = string.Empty;
            _entriesDirty = true;
            _selectedIndex = -1;
        }
    }

    private void NavigateTo(string? path)
    {
        string dir = NormalizeDirectory(path);
        if (string.IsNullOrWhiteSpace(dir))
        {
            _lastError = "路径不可用或不是目录。";
            return;
        }

        _lastError = string.Empty;
        _currentDirectory = dir;
        _pathInput = dir;
        _selectedIndex = -1;
        _entriesDirty = true;
    }

    private void RefreshEntries()
    {
        _entriesDirty = false;
        _entries.Clear();
        _lastError = string.Empty;

        if (string.IsNullOrWhiteSpace(_currentDirectory))
        {
            try
            {
                foreach (string drive in Directory.GetLogicalDrives())
                {
                    if (string.IsNullOrWhiteSpace(drive)) continue;
                    _entries.Add(new Entry
                    {
                        Name = drive.TrimEnd('\\', '/'),
                        FullPath = drive,
                        IsDirectory = true,
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lastError = ex.Message;
            }

            _entries.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return;
        }

        try
        {
            var dirs = new List<Entry>();
            var files = new List<Entry>();

            foreach (string p in Directory.EnumerateFileSystemEntries(_currentDirectory))
            {
                string name;
                try
                {
                    name = Path.GetFileName(p);
                }
                catch
                {
                    name = p;
                }

                bool isDir;
                try
                {
                    isDir = Directory.Exists(p);
                }
                catch
                {
                    isDir = false;
                }

                if (isDir)
                {
                    dirs.Add(new Entry { Name = name, FullPath = p, IsDirectory = true });
                }
                else if (_mode != SimpleFileDialogMode.OpenFolder)
                {
                    files.Add(new Entry { Name = name, FullPath = p, IsDirectory = false });
                }
            }

            dirs.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            files.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            _entries.AddRange(dirs);
            _entries.AddRange(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastError = ex.Message;
        }
    }

    private static string NormalizeDirectory(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string p = input.Trim();
        try
        {
            p = Path.GetFullPath(p);
        }
        catch
        {
            // ignore invalid paths
        }

        try
        {
            if (!Directory.Exists(p))
            {
                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }

        return p;
    }

    private static string GetDefaultStartDirectory()
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            {
                return home;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            string cwd = Environment.CurrentDirectory;
            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                return cwd;
            }
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }
}
