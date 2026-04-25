using System;
using System.Text;
using System.Threading;

namespace WoOOLToOOLsSharp.Shared.Formats.Wpf;

/// <summary>
/// WPF 路径哈希（modified MPQ-style）。
/// 迁移自旧工程 <c>OldProj/shared/src/WpfPathHash.cpp</c>，用于：
/// - WPF FCB1 hash 字段
/// - <c>.wpf.hash</c> sidecar
/// - 下载协议相关的 key
/// </summary>
public static class WpfPathHash
{
    private const int CryptoTableSize = 5 * 256;

    private static readonly ushort[] CryptoTable = new ushort[CryptoTableSize];
    private static int _cryptoTableInit;
    private static readonly object CryptoGate = new();

    public static void InitCryptoTable()
    {
        EnsureCryptoTable();
    }

    public static uint HashString(string path, int hashType)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        return HashString(bytes, hashType);
    }

    public static uint HashString(ReadOnlySpan<byte> pathBytes, int hashType)
    {
        EnsureCryptoTable();

        if ((uint)hashType > 4u)
        {
            throw new ArgumentOutOfRangeException(nameof(hashType), "hashType 必须在 [0,4] 范围内");
        }

        unchecked
        {
            uint seed1 = 0x7FED7FEDu;
            uint seed2 = 0xEEEEEEEEu;
            int typeOff = hashType * 256;

            foreach (byte b in pathBytes)
            {
                byte c = ToUpperAscii(b);
                uint tbl = CryptoTable[typeOff + c];
                seed1 = (seed1 + seed2) ^ tbl;
                seed2 = seed2 * 0x21u + c + 3u + seed1;
            }

            return seed1;
        }
    }

    /// <summary>返回 (HashString(path,1) &lt;&lt; 32) | HashString(path,2)。</summary>
    public static long Compute(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        return Compute(bytes);
    }

    public static long Compute(ReadOnlySpan<byte> pathBytes)
    {
        uint ha = HashString(pathBytes, 1);
        uint hb = HashString(pathBytes, 2);
        ulong v = ((ulong)ha << 32) | hb;
        return unchecked((long)v);
    }

    private static void EnsureCryptoTable()
    {
        if (Volatile.Read(ref _cryptoTableInit) == 1)
        {
            return;
        }

        lock (CryptoGate)
        {
            if (_cryptoTableInit == 1)
            {
                return;
            }

            // Identical to wpf_build_crypto_table() in the C reference.
            uint seed = 0x100001u;
            for (int i = 0; i < 256; i++)
            {
                for (int t = 0; t < 5; t++)
                {
                    seed = (seed * 125u + 3u) % 0x2AAAABu;
                    seed = (seed * 125u + 3u) % 0x2AAAABu;
                    CryptoTable[t * 256 + i] = (ushort)(seed & 0xFFFFu);
                }
            }

            Volatile.Write(ref _cryptoTableInit, 1);
        }
    }

    private static byte ToUpperAscii(byte c)
    {
        return c is >= (byte)'a' and <= (byte)'z'
            ? (byte)(c - 32)
            : c;
    }
}

