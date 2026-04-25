using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WoOOLToOOLsSharp.Shared;

public enum FileChangeAction
{
    Added,
    Removed,
    Modified,
    Renamed,
    Overflow,
}

public readonly record struct FileChangeEvent(
    FileChangeAction Action,
    string RootPath,
    string Path,
    string? OldPath = null);

public interface IFileWatcher : IDisposable
{
    bool SetRoots(IEnumerable<string> roots, out string error);
    IReadOnlyList<FileChangeEvent> PollEvents();
}

public sealed class FileWatcher : IFileWatcher, IAsyncDisposable
{
    private readonly ConcurrentQueue<FileChangeEvent> _pending = new();
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _disposed;

    public int InternalBufferSizeBytes { get; init; } = 256 * 1024;
    public bool IncludeSubdirectories { get; init; } = true;

    public bool SetRoots(IEnumerable<string> roots, out string error)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileWatcher));

        lock (_gate)
        {
            ClearWatchers();

            var warnings = new List<string>();
            foreach (string root in roots?.Where(static r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)
                         ?? Enumerable.Empty<string>())
            {
                string normalizedRoot;
                try
                {
                    normalizedRoot = NormalizeWatchPath(root);
                }
                catch (Exception ex)
                {
                    warnings.Add($"无法规范化监控路径: {root} ({ex.Message})");
                    continue;
                }

                if (!Directory.Exists(normalizedRoot))
                {
                    warnings.Add($"监控目录不存在: {normalizedRoot}");
                    continue;
                }

                var watcher = new FileSystemWatcher(normalizedRoot)
                {
                    IncludeSubdirectories = IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size,
                    Filter = "*",
                    EnableRaisingEvents = true,
                };

                if (InternalBufferSizeBytes > 0)
                {
                    watcher.InternalBufferSize = InternalBufferSizeBytes;
                }

                watcher.Created += (_, e) => Enqueue(FileChangeAction.Added, normalizedRoot, e.FullPath);
                watcher.Changed += (_, e) => Enqueue(FileChangeAction.Modified, normalizedRoot, e.FullPath);
                watcher.Deleted += (_, e) => Enqueue(FileChangeAction.Removed, normalizedRoot, e.FullPath);
                watcher.Renamed += (_, e) => Enqueue(FileChangeAction.Renamed, normalizedRoot, e.FullPath, e.OldFullPath);
                watcher.Error += (_, _) => Enqueue(FileChangeAction.Overflow, normalizedRoot, normalizedRoot);

                _watchers.Add(watcher);
            }

            error = warnings.Count == 0 ? string.Empty : string.Join(Environment.NewLine, warnings);
            return true;
        }
    }

    public IReadOnlyList<FileChangeEvent> PollEvents()
    {
        if (_disposed) return Array.Empty<FileChangeEvent>();

        var events = new List<FileChangeEvent>();
        while (_pending.TryDequeue(out FileChangeEvent ev))
        {
            events.Add(ev);
        }

        return events;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            ClearWatchers();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ClearWatchers()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch
            {
                // ignored
            }

            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void Enqueue(FileChangeAction action, string rootPath, string path, string? oldPath = null)
    {
        string normalizedPath;
        string? normalizedOldPath;

        try
        {
            normalizedPath = NormalizeWatchPath(path);
        }
        catch
        {
            normalizedPath = path;
        }

        if (oldPath is null)
        {
            normalizedOldPath = null;
        }
        else
        {
            try
            {
                normalizedOldPath = NormalizeWatchPath(oldPath);
            }
            catch
            {
                normalizedOldPath = oldPath;
            }
        }

        _pending.Enqueue(new FileChangeEvent(action, rootPath, normalizedPath, normalizedOldPath));
    }

    public static string NormalizeWatchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string full = Path.GetFullPath(path);
        full = Path.TrimEndingDirectorySeparator(full);

        if (OperatingSystem.IsWindows())
        {
            return full.ToLowerInvariant();
        }

        return full;
    }
}


