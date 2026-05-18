// =============================================================================
// BepArchive.cs — the .bep file container.
//
// A thin frame around a single variant's encoded output, so a compressed
// file can be written to disk and read back. The codec itself is unchanged —
// this is purely a packaging layer for the `compress` / `decompress` CLI
// verbs.
//
// Layout (little-endian, no compression of the frame itself):
//   offset  size   field
//   ------  ----   ---------------------------------------------------------
//   0       4      magic "BEPZ"  (0x42 0x45 0x50 0x5A)
//   4       1      format version (currently 1)
//   5       1      variant name length L (1..63)
//   6       L      variant name, ASCII (e.g. "textctx", "nibctx3", "lzma")
//   6+L     4      original (decompressed) length, uint32
//   10+L    4      payload length N, uint32
//   14+L    N      payload — the variant's Encode() output, byte-for-byte
//
// Header overhead is 14 + L bytes (typically 21–26 bytes for current variant
// names). No checksum — the variant's Decode() round-trips deterministically
// from the payload, so corruption of the payload surfaces as a decode-time
// exception or a length mismatch against the stored OriginalLength field.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;
using System.Text;

namespace InBep.Core;

public static class BepArchive
{
    private static readonly byte[] MAGIC = { (byte)'B', (byte)'E', (byte)'P', (byte)'Z' };
    public const byte FormatVersion = 1;
    public const int MaxVariantNameLength = 63;

    public sealed class Header
    {
        public string VariantName = "";
        public int    OriginalLength;
        public int    PayloadLength;
        public int    HeaderLength;   // bytes of header before the payload starts
        public int    FormatVersion;
    }

    /// <summary>Write a .bep file. Throws on I/O error or invalid name.</summary>
    public static void Write(string outPath, string variantName, int originalLength, byte[] payload)
    {
        if (variantName == null) throw new ArgumentNullException(nameof(variantName));
        if (variantName.Length == 0 || variantName.Length > MaxVariantNameLength)
            throw new ArgumentException($"variant name must be 1..{MaxVariantNameLength} chars");

        byte[] nameBytes = Encoding.ASCII.GetBytes(variantName);
        if (nameBytes.Length != variantName.Length)
            throw new ArgumentException("variant name must be pure ASCII");

        using var fs = File.Create(outPath);
        fs.Write(MAGIC, 0, 4);
        fs.WriteByte(FormatVersion);
        fs.WriteByte((byte)nameBytes.Length);
        fs.Write(nameBytes, 0, nameBytes.Length);
        WriteUInt32LE(fs, (uint)originalLength);
        WriteUInt32LE(fs, (uint)payload.Length);
        fs.Write(payload, 0, payload.Length);
    }

    /// <summary>Read just the header (no payload). Cheap; useful for `info`.</summary>
    public static Header ReadHeader(string inPath)
    {
        using var fs = File.OpenRead(inPath);
        return ReadHeader(fs);
    }

    /// <summary>Read header + payload from a .bep file.</summary>
    public static (Header header, byte[] payload) Read(string inPath)
    {
        using var fs = File.OpenRead(inPath);
        var h = ReadHeader(fs);
        byte[] payload = ReadExact(fs, h.PayloadLength);
        return (h, payload);
    }

    private static Header ReadHeader(Stream s)
    {
        byte[] magic = ReadExact(s, 4);
        if (magic[0] != MAGIC[0] || magic[1] != MAGIC[1] ||
            magic[2] != MAGIC[2] || magic[3] != MAGIC[3])
            throw new InvalidDataException("bad magic — not a .bep file");

        int version = s.ReadByte();
        if (version < 0) throw new EndOfStreamException();
        if (version != FormatVersion)
            throw new InvalidDataException($"unsupported format version {version} (this build reads version {FormatVersion})");

        int nameLen = s.ReadByte();
        if (nameLen <= 0 || nameLen > MaxVariantNameLength)
            throw new InvalidDataException($"invalid variant name length {nameLen}");

        byte[] nameBytes = ReadExact(s, nameLen);
        string variantName = Encoding.ASCII.GetString(nameBytes);

        uint originalLength = ReadUInt32LE(s);
        uint payloadLength  = ReadUInt32LE(s);

        return new Header
        {
            VariantName    = variantName,
            OriginalLength = (int)originalLength,
            PayloadLength  = (int)payloadLength,
            HeaderLength   = 4 + 1 + 1 + nameLen + 4 + 4,
            FormatVersion  = version,
        };
    }

    private static byte[] ReadExact(Stream s, int n)
    {
        byte[] buf = new byte[n];
        int read = 0;
        while (read < n)
        {
            int got = s.Read(buf, read, n - read);
            if (got <= 0) throw new EndOfStreamException();
            read += got;
        }
        return buf;
    }

    private static void WriteUInt32LE(Stream s, uint v)
    {
        s.WriteByte((byte)(v & 0xFF));
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)((v >> 16) & 0xFF));
        s.WriteByte((byte)((v >> 24) & 0xFF));
    }

    private static uint ReadUInt32LE(Stream s)
    {
        int b0 = s.ReadByte(), b1 = s.ReadByte(), b2 = s.ReadByte(), b3 = s.ReadByte();
        if (b3 < 0) throw new EndOfStreamException();
        return (uint)b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
    }
}
