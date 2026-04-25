using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WoOOLToOOLsSharp.Shared.Formats.Wpf;

public sealed class WpfArchive : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private bool _isOpen;
    private string _path = string.Empty;
    private byte[] _bytes = Array.Empty<byte>();
    private List<WpfEntry> _entries = new();

    // Write-mode buffer
    private List<WpfPackEntry>? _pendingFiles;

    public bool Open(string wpfPath, out string error)
    {
        lock (_gate)
        {
            Close();

            if (!FileIO.TryReadAllBytes(wpfPath, out _bytes, out error))
            {
                return false;
            }

            if (!WpfCodec.TryEnumerateEntriesFromMemory(_bytes, wpfPath, out _entries, out error))
            {
                _bytes = Array.Empty<byte>();
                _entries = new();
                return false;
            }

            _path = wpfPath;
            _isOpen = true;
            _pendingFiles = null;
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _isOpen = false;
            _path = string.Empty;
            _bytes = Array.Empty<byte>();
            _entries = new();
            _pendingFiles = null;
        }
    }

    public bool IsOpen()
    {
        lock (_gate)
        {
            return _isOpen;
        }
    }

    public string GetPath()
    {
        lock (_gate)
        {
            return _path;
        }
    }

    public int GetEntryCount()
    {
        lock (_gate)
        {
            return _entries.Count;
        }
    }

    public IReadOnlyList<WpfEntry> GetEntries()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public WpfEntry? GetEntryInfo(int index)
    {
        lock (_gate)
        {
            if (!_isOpen) return null;
            if (index < 0 || index >= _entries.Count) return null;
            return _entries[index];
        }
    }

    public WpfEntry? FindEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_gate)
        {
            if (!_isOpen) return null;
            return _entries.FirstOrDefault(e => e.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ExtractEntry(int index, out byte[] outBytes, out string error)
    {
        lock (_gate)
        {
            outBytes = Array.Empty<byte>();
            error = string.Empty;

            if (!_isOpen)
            {
                error = "WPF archive not open";
                return false;
            }

            if (index < 0 || index >= _entries.Count)
            {
                error = "WPF entry index out of range";
                return false;
            }

            return WpfCodec.TryExtractEntryFromMemory(_bytes, _entries[index], out outBytes, out error);
        }
    }

    public bool ExtractEntry(WpfEntry entry, out byte[] outBytes, out string error)
    {
        lock (_gate)
        {
            if (!_isOpen)
            {
                outBytes = Array.Empty<byte>();
                error = "WPF archive not open";
                return false;
            }

            return WpfCodec.TryExtractEntryFromMemory(_bytes, entry, out outBytes, out error);
        }
    }

    public bool ViewEntry(WpfEntry entry, out ReadOnlyMemory<byte> outData, out string error)
    {
        lock (_gate)
        {
            outData = ReadOnlyMemory<byte>.Empty;
            error = string.Empty;

            if (!_isOpen)
            {
                error = "WPF archive not open";
                return false;
            }

            if (entry.IsDirectory || entry.ByteSize == 0)
            {
                return true;
            }

            if (entry.IsCompressed)
            {
                error = "ViewEntry 仅支持未压缩 entry";
                return false;
            }

            if (entry.ByteOffset > int.MaxValue || entry.ByteSize > int.MaxValue)
            {
                error = "WPF entry 太大，当前实现不支持（超过 int 范围）";
                return false;
            }

            int offset = (int)entry.ByteOffset;
            int size = (int)entry.ByteSize;
            if (offset < 0 || size < 0 || offset + size > _bytes.Length)
            {
                error = "WPF entry 超出文件范围";
                return false;
            }

            outData = new ReadOnlyMemory<byte>(_bytes, offset, size);
            return true;
        }
    }

    // --- Write support ------------------------------------------------------

    public void CreateNew()
    {
        lock (_gate)
        {
            Close();
            _pendingFiles = new List<WpfPackEntry>();
        }
    }

    public void AddFileEntry(string path, byte[] data)
    {
        lock (_gate)
        {
            _pendingFiles ??= new List<WpfPackEntry>();
            _pendingFiles.Add(new WpfPackEntry(path, data ?? Array.Empty<byte>()));
        }
    }

    public bool SaveAs(string wpfPath, out string error)
    {
        lock (_gate)
        {
            if (_pendingFiles is null)
            {
                error = "当前不是可写状态（请先 CreateNew 并 AddFileEntry）";
                return false;
            }

            if (!WpfCodec.TryWriteArchive(wpfPath, _pendingFiles, out error))
            {
                return false;
            }

            // 保存后自动以读模式打开（便于马上回归验证）
            return Open(wpfPath, out error);
        }
    }

    public void Dispose() => Close();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

