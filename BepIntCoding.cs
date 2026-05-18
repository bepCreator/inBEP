// =============================================================================
// BepIntCoding — shared BEP integer encoding primitives.
//
// The BEP magnitude-class scheme: a non-negative integer v is encoded as
//   (L, path)  where  L = floor(log2(v))  and  path = v - 2^L  (L bits wide)
// The decoder recovers v = 2^L | path. The (L+1)-th bit is implicit since
// the high bit of any v >= 1 is always 1.
//
// For v < 2 (v ∈ {0, 1}) we use an ESCAPE: emit L=0 in Lbits, then 1
// literal bit. This keeps the encoding closed under all non-negative
// integers.
//
// The Lbits parameter (the bit-width of the L field) is a per-field choice
// determined by the value's maximum magnitude:
//   Lbits=3 covers v ∈ [0, 2^7] (max L=7)   — properties byte
//   Lbits=4 covers v ∈ [0, 2^15]            — uint16-class fields
//   Lbits=5 covers v ∈ [0, 2^31]            — uint32-class fields
//   Lbits=6 covers v ∈ [0, 2^63]            — uint64-class fields
//   Lbits=7 covers v ∈ [0, 2^127]           — never needed in practice
//
// Total bits emitted for value v with width Lbits:
//   if v < 2:   Lbits + 1                       (escape)
//   else:      Lbits + floor(log2(v))          (normal path)
//
// All wrappers in the BEP family (BLEP1, gzipb, xzb, tarb, zipb, ...) use
// these primitives so the math is consistent and any future change to the
// magnitude-class encoding is localized to this file.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;

namespace InBep.Core;

public static class BepIntCoding
{
    // =========================================================================
    // Write / read a BEP-coded integer value.
    // =========================================================================

    /// <summary>
    /// Encode v using a Lbits-wide L field. Caller guarantees that
    /// floor(log2(v)) fits in Lbits bits, i.e. v &lt; 2^(2^Lbits).
    /// </summary>
    public static void WriteValue(BitWriter bw, ulong v, int Lbits)
    {
        if (Lbits < 1 || Lbits > 7)
            throw new ArgumentOutOfRangeException(nameof(Lbits), "Lbits must be 1..7");

        if (v < 2)
        {
            // Escape: emit L=0 then a 1-bit literal value.
            bw.WriteBits(0UL, Lbits);
            bw.WriteBits(v, 1);
        }
        else
        {
            int L = MsbPosition(v);
            // Defensive: catch encoder bugs where v outgrows Lbits.
            int Lmax = (1 << Lbits) - 1;
            if (L > Lmax)
                throw new ArgumentException(
                    $"BEP: value {v} requires L={L} but Lbits={Lbits} only encodes L<={Lmax}",
                    nameof(v));

            bw.WriteBits((ulong)L, Lbits);
            ulong path = v - (1UL << L);
            bw.WriteBits(path, L);
        }
    }

    /// <summary>Decode a BEP-coded value. Symmetric inverse of WriteValue.</summary>
    public static ulong ReadValue(BitReader br, int Lbits)
    {
        if (Lbits < 1 || Lbits > 7)
            throw new ArgumentOutOfRangeException(nameof(Lbits), "Lbits must be 1..7");

        int L = (int)br.ReadBits(Lbits);
        if (L == 0)
            return br.ReadBits(1);  // escape: literal value bit (0 or 1)
        ulong path = br.ReadBits(L);
        return (1UL << L) | path;
    }

    // =========================================================================
    // Analytic helpers — compute bit counts without actually encoding.
    // Useful for size prediction and benchmarking.
    // =========================================================================

    /// <summary>floor(log2(v)) for v &gt;= 1; returns 0 for v == 1.</summary>
    public static int MsbPosition(ulong v)
    {
        if (v == 0) return 0;
        int pos = 0;
        while (v > 1) { v >>= 1; pos++; }
        return pos;
    }

    /// <summary>
    /// Number of bits a single WriteValue call would emit for (v, Lbits).
    /// Does not allocate; use for size analysis.
    /// </summary>
    public static int PredictBits(ulong v, int Lbits)
    {
        return Lbits + (v < 2 ? 1 : MsbPosition(v));
    }

    /// <summary>
    /// Number of bits required to encode a sequence of (value, Lbits) pairs.
    /// Useful for header-size prediction in wrapper analyzers.
    /// </summary>
    public static int PredictBits(ReadOnlySpan<(ulong v, int Lbits)> fields)
    {
        int total = 0;
        foreach (var (v, lb) in fields) total += PredictBits(v, lb);
        return total;
    }

    // =========================================================================
    // Convenience: minimal Lbits required for a given maximum value.
    // =========================================================================

    /// <summary>
    /// Smallest Lbits such that MsbPosition(maxValue) fits. Useful when
    /// designing a new wrapper and you know the upper bound on a field.
    /// </summary>
    public static int RequiredLbits(ulong maxValue)
    {
        if (maxValue < 2) return 1;
        int Lmax = MsbPosition(maxValue);
        int Lbits = 1;
        while ((1 << Lbits) - 1 < Lmax) Lbits++;
        return Lbits;
    }
}
