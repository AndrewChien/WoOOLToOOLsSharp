using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WoOOLToOOLsSharp.MapEditor.App;

public enum MapEditorConsoleLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed class MapEditorConsoleEntry
{
    public MapEditorConsoleLogLevel Level { get; init; } = MapEditorConsoleLogLevel.Info;
    public string Timestamp { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public readonly record struct MapEditorConsoleSessionFile(string DisplayName, string Path);

public sealed class MapEditorConsoleBackend : IDisposable
{
    private const int MaxLiveEntries = 100;
    private const int ConsolePageSize = 100;

    private readonly object _gate = new();
    private readonly List<MapEditorConsoleEntry> _liveEntries = new(capacity: MaxLiveEntries);
    private readonly List<MapEditorConsoleSessionFile> _sessions = new();

    private string _logDirectory = string.Empty;
    private string _currentSessionPath = string.Empty;

    private BlockingCollection<string>? _pendingLines;
    private Task? _writerTask;
    private StreamWriter? _writer;

    private string _selectedSessionPath = string.Empty;
    private bool _followLive = true;
    private int _historyStartLine;

    private string _indexedPath = string.Empty;
    private DateTime _indexedWriteTimeUtc;
    private long _indexedFileSize;
    private readonly List<long> _indexedLineOffsets = new();
    private int _cachedPageStart = -1;
    private readonly List<string> _cachedPageLines = new();

    public bool Initialized { get; private set; }

    public string LogDirectory => _logDirectory;
    public string CurrentSessionPath => _currentSessionPath;

    public IReadOnlyList<MapEditorConsoleEntry> LiveEntries
    {
        get
        {
            lock (_gate)
            {
                return _liveEntries.ToArray();
            }
        }
    }

    public IReadOnlyList<MapEditorConsoleSessionFile> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToArray();
            }
        }
    }

    public string SelectedSessionPath
    {
        get => _selectedSessionPath;
        set
        {
            string normalized = value ?? string.Empty;
            if (string.Equals(_selectedSessionPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedSessionPath = normalized;
            FollowLive = string.Equals(_selectedSessionPath, _currentSessionPath, StringComparison.OrdinalIgnoreCase);
            HistoryStartLine = 0;
            InvalidateHistoryCache();
        }
    }

    public bool FollowLive
    {
        get => _followLive;
        set => _followLive = value;
    }

    public int HistoryStartLine
    {
        get => _historyStartLine;
        set => _historyStartLine = Math.Max(0, value);
    }

    public bool Initialize(string workingDirectory, out string error)
    {
        error = string.Empty;

        if (Initialized)
        {
            return true;
        }

        string root = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
        string logDir = Path.Combine(root, "console_logs");

        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"初始化 console_logs 失败：{ex.Message}";
            return false;
        }

        string sessionFileName = BuildConsoleSessionFilename(DateTime.Now);
        string sessionPath = Path.Combine(logDir, sessionFileName);

        try
        {
            var fs = new FileStream(sessionPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"初始化 Console session 失败：{ex.Message}";
            return false;
        }

        _pendingLines = new BlockingCollection<string>(new ConcurrentQueue<string>());

        _logDirectory = logDir;
        _currentSessionPath = sessionPath;

        Initialized = true;
        RefreshSessions();
        InvalidateHistoryCache();
        _followLive = true;
        _historyStartLine = 0;

        _writerTask = Task.Run(WriterLoop);
        return true;
    }

    public void Dispose()
    {
        if (!Initialized)
        {
            return;
        }

        try
        {
            _pendingLines?.CompleteAdding();
        }
        catch
        {
            // ignored
        }

        try
        {
            _writerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignored
        }

        try
        {
            _writer?.Flush();
        }
        catch
        {
            // ignored
        }

        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // ignored
        }

        _writer = null;

        _writerTask = null;

        try
        {
            _pendingLines?.Dispose();
        }
        catch
        {
            // ignored
        }

        _pendingLines = null;
        Initialized = false;
    }

    public void ClearLive()
    {
        lock (_gate)
        {
            _liveEntries.Clear();
        }
    }

    public void RefreshSessions()
    {
        if (!Initialized || string.IsNullOrWhiteSpace(_logDirectory))
        {
            return;
        }

        List<MapEditorConsoleSessionFile> sessions = new();
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                foreach (string path in Directory.EnumerateFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(path);
                    sessions.Add(new MapEditorConsoleSessionFile(name, path));
                }
            }
        }
        catch
        {
            // ignore
        }

        sessions.Sort(static (a, b) => string.Compare(b.DisplayName, a.DisplayName, StringComparison.OrdinalIgnoreCase));

        int currentIndex = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (string.Equals(sessions[i].Path, _currentSessionPath, StringComparison.OrdinalIgnoreCase))
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex >= 0)
        {
            MapEditorConsoleSessionFile current = sessions[currentIndex] with { DisplayName = sessions[currentIndex].DisplayName + " (current)" };
            sessions.RemoveAt(currentIndex);
            sessions.Insert(0, current);
        }

        lock (_gate)
        {
            _sessions.Clear();
            _sessions.AddRange(sessions);
        }

        if (string.IsNullOrWhiteSpace(_selectedSessionPath)
            || !sessions.Any(s => string.Equals(s.Path, _selectedSessionPath, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedSessionPath = _currentSessionPath;
        }
    }

    public void Append(MapEditorConsoleLogLevel level, string source, string message)
    {
        if (!Initialized)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var entry = new MapEditorConsoleEntry
        {
            Level = level,
            Timestamp = BuildConsoleTimestamp(DateTime.Now),
            Source = source ?? string.Empty,
            Message = message,
        };

        lock (_gate)
        {
            _liveEntries.Add(entry);
            if (_liveEntries.Count > MaxLiveEntries)
            {
                _liveEntries.RemoveRange(0, _liveEntries.Count - MaxLiveEntries);
            }
        }

        QueueLineForWrite(FormatConsoleLine(entry));
    }

    public bool IsSelectedCurrentSession(out string selectedPath)
    {
        selectedPath = _selectedSessionPath;
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return false;
        }

        return string.Equals(selectedPath, _currentSessionPath, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHistoryTotalLines(string sessionPath)
    {
        EnsureHistoryIndex(sessionPath);
        lock (_gate)
        {
            return _indexedLineOffsets.Count;
        }
    }

    public IReadOnlyList<string> GetHistoryPageLines(string sessionPath, int startLine)
    {
        LoadHistoryPage(sessionPath, startLine);
        lock (_gate)
        {
            return _cachedPageLines.ToArray();
        }
    }

    public int GetCachedPageStart()
    {
        lock (_gate)
        {
            return _cachedPageStart;
        }
    }

    public static string FormatConsoleLine(MapEditorConsoleEntry entry)
    {
        return "[" + entry.Timestamp + "] [" + LevelLabel(entry.Level) + "] [" + entry.Source + "] " + entry.Message;
    }

    public static string LevelLabel(MapEditorConsoleLogLevel level)
        => level switch
        {
            MapEditorConsoleLogLevel.Warning => "WARN",
            MapEditorConsoleLogLevel.Error => "ERROR",
            _ => "INFO",
        };

    private static string BuildConsoleSessionFilename(DateTime now)
        => "console_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log";

    private static string BuildConsoleTimestamp(DateTime now)
        => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private void QueueLineForWrite(string line)
    {
        if (_pendingLines is null)
        {
            return;
        }

        try
        {
            _pendingLines.Add(line);
        }
        catch
        {
            // ignored
        }

        // Appending to the live session invalidates history cache when current session is selected.
        if (string.Equals(_selectedSessionPath, _currentSessionPath, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _cachedPageStart = -1;
            }
        }
    }

    private void WriterLoop()
    {
        if (_pendingLines is null || _writer is null)
        {
            return;
        }

        try
        {
            foreach (string line in _pendingLines.GetConsumingEnumerable())
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch
                {
                    // ignore writer errors
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // ignore
        }
    }

    private void InvalidateHistoryCache()
    {
        lock (_gate)
        {
            _indexedPath = string.Empty;
            _indexedFileSize = 0;
            _indexedWriteTimeUtc = default;
            _indexedLineOffsets.Clear();
            _cachedPageLines.Clear();
            _cachedPageStart = -1;
        }
    }

    private void EnsureHistoryIndex(string sessionPath)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            return;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(sessionPath);
            if (!info.Exists)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        long size = info.Length;
        DateTime writeUtc = info.LastWriteTimeUtc;

        lock (_gate)
        {
            if (string.Equals(_indexedPath, sessionPath, StringComparison.OrdinalIgnoreCase)
                && _indexedFileSize == size
                && _indexedWriteTimeUtc == writeUtc)
            {
                return;
            }

            _indexedPath = sessionPath;
            _indexedFileSize = size;
            _indexedWriteTimeUtc = writeUtc;
            _indexedLineOffsets.Clear();
            _cachedPageLines.Clear();
            _cachedPageStart = -1;
        }

        if (size <= 0)
        {
            return;
        }

        var offsets = new List<long>(capacity: 4096);
        try
        {
            using var fs = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = fs.Length;
            if (length <= 0)
            {
                lock (_gate)
                {
                    _indexedLineOffsets.Clear();
                }
                return;
            }

            offsets.Add(0);

            byte[] buffer = new byte[64 * 1024];
            long position = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    long nextOffset = position + i + 1;
                    if (nextOffset < length)
                    {
                        offsets.Add(nextOffset);
                    }
                }

                position += read;
            }
        }
        catch
        {
            offsets.Clear();
        }

        lock (_gate)
        {
            _indexedLineOffsets.Clear();
            _indexedLineOffsets.AddRange(offsets);
        }
    }

    private void LoadHistoryPage(string sessionPath, int startLine)
    {
        EnsureHistoryIndex(sessionPath);

        int clampedStart;
        int endLineExclusive;

        lock (_gate)
        {
            int total = _indexedLineOffsets.Count;
            if (total <= 0)
            {
                _cachedPageLines.Clear();
                _cachedPageStart = 0;
                return;
            }

            int maxStart = total > ConsolePageSize ? (total - ConsolePageSize) : 0;
            clampedStart = Math.Clamp(startLine, 0, maxStart);
            if (_cachedPageStart == clampedStart)
            {
                return;
            }

            endLineExclusive = Math.Min(clampedStart + ConsolePageSize, total);
            _cachedPageLines.Clear();
        }

        try
        {
            using var fs = new FileStream(sessionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long seek;
            lock (_gate)
            {
                if (clampedStart < 0 || clampedStart >= _indexedLineOffsets.Count)
                {
                    _cachedPageStart = 0;
                    return;
                }

                seek = _indexedLineOffsets[clampedStart];
            }

            fs.Seek(seek, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false);

            var lines = new List<string>(capacity: ConsolePageSize);
            for (int i = clampedStart; i < endLineExclusive; i++)
            {
                string? line = sr.ReadLine();
                if (line is null)
                {
                    break;
                }

                lines.Add(line);
            }

            lock (_gate)
            {
                _cachedPageLines.Clear();
                _cachedPageLines.AddRange(lines);
                _cachedPageStart = clampedStart;
            }
        }
        catch
        {
            lock (_gate)
            {
                _cachedPageLines.Clear();
                _cachedPageStart = 0;
            }
        }
    }
}
