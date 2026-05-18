// =============================================================================
// BitIOExtensions — V16.5
//
// Varint and small-magic helpers for BitWriter / BitReader. These were
// previously inline in PieceDictBep; lifted here so every variant can use
// them without duplication.
//
// Conventions:
//   - WriteVarUInt / ReadVarUInt: ULEB128 (7 data bits per byte, top bit =
//     continuation). Same wire format used by BEPPipeline V6 and the
//     AbsentMarkerPostProcessor V16.4. Stays byte-aligned within a bit
//     stream — each varint emits whole bytes.
//   - WriteMagic16 / ReadMagic16: 2-byte little-endian magic ID. Replaces
//     the per-variant 8-byte ASCII magic from V13/V14/V15. Saves 6 bytes
//     per archive.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;

namespace InBep.Core;

public static class BitIOExtensions
{
    // ── ULEB128 varint ──────────────────────────────────────────────────────

    public static void WriteVarUInt(this BitWriter bw, ulong v)
    {
        while (v >= 0x80) { bw.WriteBits(((v & 0x7F) | 0x80), 8); v >>= 7; }
        bw.WriteBits(v & 0x7F, 8);
    }

    public static ulong ReadVarUInt(this BitReader br)
    {
        ulong r = 0;
        int shift = 0;
        while (true)
        {
            ulong b = br.ReadBits(8);
            r |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new InvalidOperationException("BitIOExtensions: varint too long");
        }
        return r;
    }

    /// <summary>Number of bytes a ULEB128-encoded value will consume.</summary>
    public static int VarUIntByteCount(ulong v)
    {
        int n = 1;
        while (v >= 0x80) { n++; v >>= 7; }
        return n;
    }

    // ── 2-byte (16-bit) magic ID ─────────────────────────────────────────────

    /// <summary>Write a 16-bit variant ID. Replaces the previous 8-byte ASCII
    /// magic. 6 bytes saved per archive.</summary>
    public static void WriteMagic16(this BitWriter bw, ushort id)
    {
        bw.WriteBits((ulong)(id & 0xFF), 8);
        bw.WriteBits((ulong)((id >> 8) & 0xFF), 8);
    }

    /// <summary>Read a 16-bit variant ID and verify it matches. Throws on mismatch.</summary>
    public static void ReadMagic16(this BitReader br, ushort expected, string variantName)
    {
        ushort got = (ushort)((br.ReadBits(8)) | (br.ReadBits(8) << 8));
        if (got != expected)
            throw new System.IO.InvalidDataException(
                $"Not {variantName}: expected magic 0x{expected:X4}, got 0x{got:X4}");
    }
}
