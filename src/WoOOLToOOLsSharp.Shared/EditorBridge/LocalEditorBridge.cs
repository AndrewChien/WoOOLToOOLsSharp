using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WoOOLToOOLsSharp.Shared.EditorBridge;

public enum EditorBridgeApp
{
    MapEditor,
    ContentEditor,
    Downloader,
}

public enum EditorBridgeRequestKind
{
    OpenAsset,
    OpenMap,
    PatchNmp,
    ReloadDataFolder,
}

public sealed class EditorBridgeRequest
{
    public string RequestId { get; init; } = string.Empty;
    public EditorBridgeApp Sender { get; init; } = EditorBridgeApp.MapEditor;
    public EditorBridgeRequestKind Kind { get; init; } = EditorBridgeRequestKind.OpenAsset;
    public string Path { get; init; } = string.Empty;
    public string ExtraPath { get; init; } = string.Empty;
    public int ImageIndex { get; init; } = -1;
}

/// <summary>
/// 迁移自 OldProj 的文件系统桥接层：用于 MapEditor 与 ContentEditor 之间的互相打开/刷新请求。
/// 约定：临时目录 %TEMP%/woool_editor_bridge 下收发 request 文件与 heartbeat status 文件。
/// </summary>
public sealed class LocalEditorBridge
{
    private const long ProtocolVersion = 1;
    private const long HeartbeatIntervalMs = 1000;
    private const long InboxPollIntervalMs = 100;
    private const long PeerTimeoutMs = 5000;

    private readonly EditorBridgeApp _localApp;
    private readonly string _rootDirectory;
    private readonly string _localInboxDirectory;
    private readonly string _localStatusPath;
    private readonly Dictionary<EditorBridgeApp, string> _inboxDirectories = new();
    private readonly Dictionary<EditorBridgeApp, string> _statusPaths = new();

    private long _lastHeartbeatWriteMs;
    private long _lastInboxPollMs;
    private long _requestCounter;

    private bool _initialized;

    private readonly List<EditorBridgeRequest> _pendingRequests = new();
    private readonly Dictionary<EditorBridgeApp, long> _appLastHeartbeatMs = new();

    public LocalEditorBridge(EditorBridgeApp localApp)
    {
        _localApp = localApp;
        _rootDirectory = GetBridgeRootDirectory();

        foreach (EditorBridgeApp app in Enum.GetValues<EditorBridgeApp>())
        {
            _inboxDirectories[app] = GetInboxDirectory(_rootDirectory, app);
            _statusPaths[app] = GetStatusPath(_rootDirectory, app);
            _appLastHeartbeatMs[app] = 0;
        }

        _localInboxDirectory = _inboxDirectories[_localApp];
        _localStatusPath = _statusPaths[_localApp];
    }

    public string RootDirectory => _rootDirectory;
    public bool Initialized => _initialized;

    public bool Initialize(out string error)
    {
        error = string.Empty;

        if (_initialized)
        {
            return true;
        }

        try
        {
            foreach (string inbox in _inboxDirectories.Values)
            {
                Directory.CreateDirectory(inbox);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_localStatusPath) ?? _rootDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"初始化 EditorBridge 失败：{ex.Message}";
            return false;
        }

        _initialized = true;
        _lastHeartbeatWriteMs = 0;
        _lastInboxPollMs = 0;
        _requestCounter = 0;
        return true;
    }

    public void Tick()
    {
        if (!_initialized)
        {
            return;
        }

        long nowMs = GetNowMs();

        if (_lastHeartbeatWriteMs == 0 || (nowMs - _lastHeartbeatWriteMs) >= HeartbeatIntervalMs)
        {
            WriteHeartbeat();
            _lastHeartbeatWriteMs = nowMs;
        }

        UpdatePeerStatus(nowMs);

        if (_lastInboxPollMs == 0 || (nowMs - _lastInboxPollMs) >= InboxPollIntervalMs)
        {
            PollInbox();
            _lastInboxPollMs = nowMs;
        }
    }

    public List<EditorBridgeRequest> DrainRequests()
    {
        var drained = new List<EditorBridgeRequest>(_pendingRequests);
        _pendingRequests.Clear();
        return drained;
    }

    public bool IsAppRunning(EditorBridgeApp app)
    {
        if (!_initialized)
        {
            return false;
        }

        long last = _appLastHeartbeatMs.GetValueOrDefault(app);
        if (last <= 0)
        {
            return false;
        }

        long nowMs = GetNowMs();
        return nowMs >= last && (nowMs - last) <= PeerTimeoutMs;
    }

    public long GetAppLastHeartbeatMs(EditorBridgeApp app)
        => _appLastHeartbeatMs.GetValueOrDefault(app);

    public bool SendOpenAsset(string path, int imageIndex, out string error)
        => SendRequest(GetPeerApp(_localApp), EditorBridgeRequestKind.OpenAsset, path, extraPath: string.Empty, imageIndex, out error);

    public bool SendOpenMap(string path, out string error)
        => SendRequest(GetPeerApp(_localApp), EditorBridgeRequestKind.OpenMap, path, extraPath: string.Empty, imageIndex: -1, out error);

    public bool SendPatchNmp(string mapPath, string dataPath, out string error)
        => SendRequest(EditorBridgeApp.Downloader, EditorBridgeRequestKind.PatchNmp, mapPath, extraPath: dataPath, imageIndex: -1, out error);

    public bool SendReloadDataFolder(string dataPath, out string error)
        => SendReloadDataFolder(EditorBridgeApp.MapEditor, dataPath, message: string.Empty, out error);

    public bool SendReloadDataFolder(EditorBridgeApp targetApp, string dataPath, string message, out string error)
        => SendRequest(targetApp, EditorBridgeRequestKind.ReloadDataFolder, dataPath, extraPath: message ?? string.Empty, imageIndex: -1, out error);

    private bool SendRequest(EditorBridgeApp targetApp,
        EditorBridgeRequestKind kind,
        string path,
        string extraPath,
        int imageIndex,
        out string error)
    {
        error = string.Empty;

        if (!_initialized && !Initialize(out error))
        {
            return false;
        }

        long nowMs = GetNowMs();
        string requestId = nowMs + "-" + (++_requestCounter).ToString(CultureInfo.InvariantCulture);
        string normalizedPath = NormalizeEditorBridgePath(path);

        var sb = new StringBuilder(256 + normalizedPath.Length);
        sb.AppendLine("version=" + ProtocolVersion);
        sb.AppendLine("id=" + requestId);
        sb.AppendLine("sender=" + _localApp);
        sb.AppendLine("kind=" + kind);
        sb.AppendLine("path=" + normalizedPath);
        if (!string.IsNullOrWhiteSpace(extraPath))
        {
            sb.AppendLine("extra_path=" + extraPath);
        }
        sb.AppendLine("image_index=" + imageIndex.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("timestamp_ms=" + nowMs.ToString(CultureInfo.InvariantCulture));

        string fileName = string.Format(CultureInfo.InvariantCulture, "{0:D20}_{1:D6}.request", nowMs, _requestCounter);
        string outPath = Path.Combine(_inboxDirectories[targetApp], fileName);
        return WriteTextFileAtomically(outPath, sb.ToString(), out error);
    }

    private void WriteHeartbeat()
    {
        long nowMs = GetNowMs();

        var sb = new StringBuilder(128);
        sb.AppendLine("version=" + ProtocolVersion);
        sb.AppendLine("app=" + _localApp);
        sb.AppendLine("timestamp_ms=" + nowMs.ToString(CultureInfo.InvariantCulture));

        try
        {
            sb.AppendLine("pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // ignore
        }

        _ = WriteTextFileAtomically(_localStatusPath, sb.ToString(), out _);
        _appLastHeartbeatMs[_localApp] = nowMs;
    }

    private void UpdatePeerStatus(long nowMs)
    {
        foreach (EditorBridgeApp app in Enum.GetValues<EditorBridgeApp>())
        {
            if (app == _localApp)
            {
                continue;
            }

            _appLastHeartbeatMs[app] = 0;

            string statusPath = _statusPaths[app];
            string text;
            try
            {
                if (!File.Exists(statusPath))
                {
                    continue;
                }

                text = File.ReadAllText(statusPath, Encoding.UTF8);
            }
            catch
            {
                continue;
            }

            foreach ((string key, string value) in ParseKeyValueText(text))
            {
                if (!string.Equals(key, "timestamp_ms", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts))
                {
                    _appLastHeartbeatMs[app] = ts;
                }
            }

            long last = _appLastHeartbeatMs[app];
            if (last <= 0 || nowMs < last || (nowMs - last) > PeerTimeoutMs)
            {
                _appLastHeartbeatMs[app] = 0;
            }
        }
    }

    private void PollInbox()
    {
        List<string> requestFiles;
        try
        {
            if (!Directory.Exists(_localInboxDirectory))
            {
                return;
            }

            requestFiles = new List<string>(Directory.EnumerateFiles(_localInboxDirectory, "*.request", SearchOption.TopDirectoryOnly));
        }
        catch
        {
            return;
        }

        requestFiles.Sort(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < requestFiles.Count; i++)
        {
            string requestPath = requestFiles[i];
            string text;
            try
            {
                text = File.ReadAllText(requestPath, Encoding.UTF8);
            }
            catch
            {
                TryDeleteFile(requestPath);
                continue;
            }

            TryParseRequest(text, out EditorBridgeRequest? request);
            if (request is not null && !string.IsNullOrWhiteSpace(request.Path))
            {
                _pendingRequests.Add(request);
            }

            TryDeleteFile(requestPath);
        }
    }

    private static bool TryParseRequest(string payload, out EditorBridgeRequest? request)
    {
        request = null;

        long version = 0;
        bool haveVersion = false;
        bool haveKind = false;
        bool haveSender = false;
        string id = string.Empty;
        string path = string.Empty;
        string extra = string.Empty;
        int imageIndex = -1;

        EditorBridgeApp sender = EditorBridgeApp.MapEditor;
        EditorBridgeRequestKind kind = EditorBridgeRequestKind.OpenAsset;

        foreach ((string key, string value) in ParseKeyValueText(payload))
        {
            if (string.Equals(key, "version", StringComparison.OrdinalIgnoreCase))
            {
                haveVersion = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
            }
            else if (string.Equals(key, "id", StringComparison.OrdinalIgnoreCase))
            {
                id = value;
            }
            else if (string.Equals(key, "sender", StringComparison.OrdinalIgnoreCase))
            {
                haveSender = Enum.TryParse(value, ignoreCase: true, out sender);
            }
            else if (string.Equals(key, "kind", StringComparison.OrdinalIgnoreCase))
            {
                haveKind = Enum.TryParse(value, ignoreCase: true, out kind);
            }
            else if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                path = NormalizeEditorBridgePath(value);
            }
            else if (string.Equals(key, "extra_path", StringComparison.OrdinalIgnoreCase))
            {
                extra = value;
            }
            else if (string.Equals(key, "image_index", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out imageIndex);
            }
        }

        if (!haveVersion || version != ProtocolVersion || !haveKind || !haveSender || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        request = new EditorBridgeRequest
        {
            RequestId = id,
            Sender = sender,
            Kind = kind,
            Path = path,
            ExtraPath = extra,
            ImageIndex = imageIndex,
        };
        return true;
    }

    public static string NormalizeEditorBridgePath(string path)
    {
        if (ParseEditorBridgeWpfPath(path, out string archive, out string entry))
        {
            return MakeEditorBridgeWpfPath(archive, entry);
        }

        return path ?? string.Empty;
    }

    public static string MakeEditorBridgeWpfPath(string archivePath, string entryPath)
    {
        string normalizedEntry = NormalizeEditorBridgeWpfEntryPath(entryPath);
        if (string.IsNullOrWhiteSpace(normalizedEntry))
        {
            return archivePath ?? string.Empty;
        }

        return (archivePath ?? string.Empty) + "::/" + normalizedEntry;
    }

    public static bool ParseEditorBridgeWpfPath(string path, out string archivePath, out string entryPath)
    {
        archivePath = string.Empty;
        entryPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        int sep = path.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0)
        {
            return false;
        }

        archivePath = path.Substring(0, sep);
        string entry = path.Substring(sep + 2);
        if (entry.Length == 0)
        {
            entryPath = string.Empty;
            return !string.IsNullOrWhiteSpace(archivePath);
        }

        while (entry.Length > 0 && (entry[0] == '/' || entry[0] == '\\'))
        {
            entry = entry.Substring(1);
        }

        entryPath = NormalizeEditorBridgeWpfEntryPath(entry);
        return !string.IsNullOrWhiteSpace(archivePath);
    }

    public static string NormalizeEditorBridgeWpfEntryPath(string entryPath)
    {
        string normalized = (entryPath ?? string.Empty).Replace('\\', '/');
        while (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(1);
        }

        return normalized;
    }

    private static EditorBridgeApp GetPeerApp(EditorBridgeApp app)
        => app == EditorBridgeApp.MapEditor ? EditorBridgeApp.ContentEditor : EditorBridgeApp.MapEditor;

    private static string GetBridgeRootDirectory()
    {
        string tempRoot = string.Empty;
        try
        {
            tempRoot = Path.GetTempPath();
        }
        catch
        {
            tempRoot = Environment.CurrentDirectory;
        }

        if (string.IsNullOrWhiteSpace(tempRoot))
        {
            tempRoot = Environment.CurrentDirectory;
        }

        return Path.Combine(tempRoot, "woool_editor_bridge");
    }

    private static string GetInboxDirectory(string rootDirectory, EditorBridgeApp app)
        => Path.Combine(rootDirectory, "inbox", app switch
        {
            EditorBridgeApp.MapEditor => "map_editor",
            EditorBridgeApp.ContentEditor => "content_editor",
            _ => "downloader",
        });

    private static string GetStatusPath(string rootDirectory, EditorBridgeApp app)
        => Path.Combine(rootDirectory, "status", app switch
        {
            EditorBridgeApp.MapEditor => "map_editor.status",
            EditorBridgeApp.ContentEditor => "content_editor.status",
            _ => "downloader.status",
        });

    private static long GetNowMs()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static bool WriteTextFileAtomically(string path, string text, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空";
            return false;
        }

        string parent = string.Empty;
        try
        {
            parent = Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            parent = string.Empty;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }

        string tmpPath = path + ".tmp_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using (var writer = new StreamWriter(tmpPath, append: false, encoding: utf8NoBom))
            {
                writer.NewLine = "\n";
                writer.Write(text ?? string.Empty);
            }

            File.Move(tmpPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            TryDeleteFile(tmpPath);
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static IEnumerable<(string Key, string Value)> ParseKeyValueText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            string line = text.Substring(start, end - start).TrimEnd('\r');
            start = end + 1;

            if (line.Length == 0)
            {
                continue;
            }

            int sep = line.IndexOf('=');
            if (sep <= 0 || sep + 1 > line.Length)
            {
                continue;
            }

            string key = line.Substring(0, sep).Trim();
            string value = line.Substring(sep + 1);
            if (key.Length == 0)
            {
                continue;
            }

            yield return (key, value);
        }
    }
}
