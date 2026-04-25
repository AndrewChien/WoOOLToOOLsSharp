namespace WoOOLToOOLsSharp.Shared.Formats.Wpf;

public sealed class WpfEntry
{
    /// <summary>0-based entry index in the WPF.</summary>
    public int Index { get; init; }

    /// <summary>Absolute byte offset into the WPF file where the raw payload starts.</summary>
    public ulong ByteOffset { get; init; }

    /// <summary>Raw payload size in bytes (may be compressed).</summary>
    public uint ByteSize { get; init; }

    /// <summary>FCB1 hash field（旧工程称 FCB1 content hash / download key）。</summary>
    public long Hash { get; init; }

    // --- Legacy fields / convenience (与旧工程结构保持一致) -------------------

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;

    public ulong Offset { get; init; }
    public uint Size { get; init; }
    public uint UncompressedSize { get; init; }

    public bool IsDirectory { get; init; }
    public bool IsCompressed { get; init; }
}

