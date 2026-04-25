using System.Buffers.Binary;
using System.Text;

namespace WoOOLToOOLsSharp.Shared;

public sealed record DynamicOverlayBinaryRecordView
{
    public int RecordIndex { get; init; }
    public int Offset { get; init; }
    public string Hex { get; init; } = string.Empty;
    public IReadOnlyList<short> Int16Le { get; init; } = [];
    public IReadOnlyList<ushort> UInt16Le { get; init; } = [];
    public IReadOnlyList<int> Int32Le { get; init; } = [];
    public IReadOnlyList<uint> UInt32Le { get; init; } = [];
    public IReadOnlyList<float> Float32Le { get; init; } = [];
    public IReadOnlyList<string> AsciiStrings { get; init; } = [];
}

public static class DynamicOverlayBinaryRecordProbe
{
    public static IReadOnlyList<DynamicOverlayBinaryRecordView> ProbeFixedRecords(
        ReadOnlySpan<byte> bytes,
        int offset,
        int recordSize,
        int count)
    {
        if (recordSize <= 0 || count <= 0 || bytes.IsEmpty)
        {
            return [];
        }

        int start = Math.Clamp(offset, 0, bytes.Length);
        if (start >= bytes.Length)
        {
            return [];
        }

        int available = (bytes.Length - start) / recordSize;
        int actualCount = Math.Min(count, available);
        if (actualCount <= 0)
        {
            return [];
        }

        List<DynamicOverlayBinaryRecordView> records = new(actualCount);
        for (int i = 0; i < actualCount; i++)
        {
            int recordOffset = start + i * recordSize;
            ReadOnlySpan<byte> slice = bytes.Slice(recordOffset, recordSize);
            records.Add(new DynamicOverlayBinaryRecordView
            {
                RecordIndex = i,
                Offset = recordOffset,
                Hex = Convert.ToHexString(slice),
                Int16Le = ReadInt16Le(slice),
                UInt16Le = ReadUInt16Le(slice),
                Int32Le = ReadInt32Le(slice),
                UInt32Le = ReadUInt32Le(slice),
                Float32Le = ReadFloat32Le(slice),
                AsciiStrings = ReadAsciiStrings(slice),
            });
        }

        return records;
    }

    private static IReadOnlyList<short> ReadInt16Le(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 2;
        List<short> values = new(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)));
        }

        return values;
    }

    private static IReadOnlyList<ushort> ReadUInt16Le(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 2;
        List<ushort> values = new(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2)));
        }

        return values;
    }

    private static IReadOnlyList<int> ReadInt32Le(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 4;
        List<int> values = new(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4)));
        }

        return values;
    }

    private static IReadOnlyList<uint> ReadUInt32Le(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 4;
        List<uint> values = new(count);
        for (int i = 0; i < count; i++)
        {
            values.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4)));
        }

        return values;
    }

    private static IReadOnlyList<float> ReadFloat32Le(ReadOnlySpan<byte> bytes)
    {
        int count = bytes.Length / 4;
        List<float> values = new(count);
        for (int i = 0; i < count; i++)
        {
            int raw = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * 4, 4));
            float value = BitConverter.Int32BitsToSingle(raw);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                value = 0.0f;
            }

            values.Add(value);
        }

        return values;
    }

    private static IReadOnlyList<string> ReadAsciiStrings(ReadOnlySpan<byte> bytes)
    {
        List<string> values = [];
        var current = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b is >= 32 and <= 126)
            {
                current.Append((char)b);
                continue;
            }

            FlushCurrent();
        }

        FlushCurrent();
        return values;

        void FlushCurrent()
        {
            if (current.Length >= 3)
            {
                values.Add(current.ToString());
            }

            current.Clear();
        }
    }
}
