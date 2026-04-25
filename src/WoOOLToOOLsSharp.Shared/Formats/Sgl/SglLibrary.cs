using System;
using System.Collections.Generic;
using WoOOLToOOLsSharp.Shared.Formats.Tex;

namespace WoOOLToOOLsSharp.Shared.Formats.Sgl;

public sealed class SglLibrary : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private bool _isOpen;
    private string _path = string.Empty;
    private byte[] _bytes = Array.Empty<byte>();
    private List<SglImageEntry> _entries = new();

    private readonly Dictionary<long, DecodedImage> _cache = new();
    private readonly Dictionary<int, int> _frameCountCache = new();

    public bool Open(string sglPath, out string error)
    {
        lock (_gate)
        {
            Close();

            if (!FileIO.TryReadAllBytes(sglPath, out _bytes, out error))
            {
                return false;
            }

            if (!SglCodec.TryEnumerateEntriesFromMemory(_bytes, sglPath, out _entries, out error))
            {
                _bytes = Array.Empty<byte>();
                _entries = new();
                return false;
            }

            _path = sglPath;
            _isOpen = true;
            return true;
        }
    }

    public bool OpenFromMemory(byte[] bytes, string label, out string error)
    {
        if (bytes is null)
        {
            error = "bytes 不能为空";
            return false;
        }

        lock (_gate)
        {
            Close();

            _bytes = bytes;
            if (!SglCodec.TryEnumerateEntriesFromMemory(_bytes, label, out _entries, out error))
            {
                _bytes = Array.Empty<byte>();
                _entries = new();
                return false;
            }

            _path = label;
            _isOpen = true;
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
            _cache.Clear();
            _frameCountCache.Clear();
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

    public int GetImageCount()
    {
        lock (_gate)
        {
            return _entries.Count;
        }
    }

    public IReadOnlyList<SglImageEntry> GetEntries()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public SglImageEntry? GetEntry(int index)
    {
        lock (_gate)
        {
            if (!_isOpen) return null;
            if (index < 0 || index >= _entries.Count) return null;
            return _entries[index];
        }
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _frameCountCache.Clear();
        }
    }

    public int GetFrameCount(int index)
    {
        lock (_gate)
        {
            if (!_isOpen) return 1;
            if (index < 0 || index >= _entries.Count) return 1;

            if (_frameCountCache.TryGetValue(index, out int cached))
            {
                return cached;
            }

            SglImageEntry entry = _entries[index];
            int frameCount = 1;
            if (!entry.IsEmpty && TryGetEntrySpan(entry, out ReadOnlySpan<byte> texBytes))
            {
                frameCount = TexCodec.GetFrameCount(texBytes);
            }

            _frameCountCache[index] = frameCount;
            return frameCount;
        }
    }

    public DecodedImage? GetImage(int index)
    {
        return GetImage(index, frame: 0);
    }

    public DecodedImage? GetImage(int index, int frame)
    {
        lock (_gate)
        {
            long key = CacheKey(index, frame);
            if (_cache.TryGetValue(key, out DecodedImage? cached))
            {
                return cached.IsValid ? cached : null;
            }

            // Cache negative results too (matches old project behavior)
            _cache[key] = new DecodedImage();

            if (!_isOpen) return null;
            if (index < 0 || index >= _entries.Count) return null;

            SglImageEntry entry = _entries[index];
            if (entry.IsEmpty) return null;

            if (!TryGetEntrySpan(entry, out ReadOnlySpan<byte> texBytes))
            {
                return null;
            }

            if (!TexCodec.TryDecodeRgba8(texBytes, out DecodedImage image, out _, frame))
            {
                return null;
            }

            _cache[key] = image;
            return image.IsValid ? image : null;
        }
    }

    private static long CacheKey(int index, int frame)
    {
        return ((long)index << 16) | (uint)(frame & 0xFFFF);
    }

    private bool TryGetEntrySpan(SglImageEntry entry, out ReadOnlySpan<byte> outBytes)
    {
        outBytes = ReadOnlySpan<byte>.Empty;

        if (entry.Offset > int.MaxValue || entry.Size > int.MaxValue)
        {
            return false;
        }

        int offset = (int)entry.Offset;
        int size = (int)entry.Size;

        if (offset < 0 || size < 0 || offset + size > _bytes.Length)
        {
            return false;
        }

        outBytes = new ReadOnlySpan<byte>(_bytes, offset, size);
        return true;
    }

    public void Dispose()
    {
        Close();
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}

