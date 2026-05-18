// =============================================================================
// StrideSplitBep — V13/Drop 14 (Avenue 2): byte-position-split encoder for
// structured-record data.
//
// =============================================================================
// MOTIVATION
// =============================================================================
//
// Database files (osdb) and structured binary formats (executables, file
// formats with fixed headers) contain repeated record templates where the
// same byte position carries similar values across records. Example:
//   record[0] = byte at offset 0 in each record (often a "type" tag, low entropy)
//   record[1] = byte at offset 1 (often a length field, low entropy)
//   record[N-1] = byte at last offset (sometimes a checksum, high entropy)
//
// A unified byte stream mixes all these byte-positions together and the
// resulting entropy is the average. Splitting by position-mod-N exposes
// the per-position structure, which entropy-codes much better.
//
// We previously verified this on real_binary at +1.5% (s=16). On osdb-shape
// data the predicted gain is much higher because record sizes are typically
// small (8-256 bytes) and per-position entropy is very low.
//
// =============================================================================
// HOW IT WORKS
// =============================================================================
//
// 1. Detect the optimal stride S in {1, 2, 4, 8, 16, 32, 64} by measuring
//    sum-of-per-stream-entropies for each. Smallest sum wins.
// 2. Split input into S streams (round-robin by position mod S).
// 3. Encode each stream with NibbleContextBep (context coding handles
//    per-stream statistical structure).
// 4. Self-verify by decoding and comparing.
//
// =============================================================================
// FORMAT
// =============================================================================
//
// [magic:8 "STRDSPLT"] [version:1] [flag:1 raw/coded] [origLen:4]
// If coded:
//   [stride:4 bits — value 0..15 means stride = (value+1)*1 actually we encode
//                    log2 of stride 0..6 so 7 distinct strides fit in 3 bits;
//                    we use 4 bits for headroom]
//   For each of S streams:
//     [streamLen:4]
//     [streamEnc:varlen — NibbleContextBep-encoded]
//
// =============================================================================
// EXPECTED IMPACT
// =============================================================================
//
//   osdb (structured records):  +5-10pt (tightest fit for this approach)
//   ooffice (mixed binary):     +1-3pt (executables have section structure)
//   real_binary equivalent:     +1-2pt
//   English text:               ±0pt (no stride structure)
//   Random / Sensor:            ±0pt (gating prevents activation)
//   xml / source code:          0-1pt (some stride pattern from indentation)
//
// =============================================================================

namespace InBep.Core;

public static class StrideSplitBep
{
    private static readonly byte[] MAGIC = System.Text.Encoding.ASCII.GetBytes("STRDSPLT");
    private const byte VERSION = 1;
    private const ushort MAGIC_ID = VariantMagicIds.StrideSplitBep;
    private const int MIN_INPUT = 2048;

    // Strides to try: powers of 2 covering common record/word sizes.
    // 1 means "no split" — always loses to plain NibbleCtx, included only as
    // baseline. 2,4 catch 16/32-bit data. 8,16 catch typical record sizes.
    // 32,64 catch larger record templates.
    private static readonly int[] STRIDES = { 2, 4, 8, 16, 32, 64 };

    public static byte[] Encode(byte[] input)
    {
        if (input == null || input.Length < MIN_INPUT)
            return BuildRaw(input ?? Array.Empty<byte>());

        // Detect optimal stride. Compute per-stream entropy at each candidate
        // stride and pick the smallest sum.
        int bestStride = DetectBestStride(input);
        if (bestStride <= 1)
        {
            // No stride structure detected — would lose vs plain NibbleCtx
            return BuildRaw(input);
        }

        // Split and encode each sub-stream
        byte[][] streams = Split(input, bestStride);
        byte[][] encoded = new byte[bestStride][];
        try
        {
            for (int s = 0; s < bestStride; s++)
                encoded[s] = NibbleContextBep.Encode(streams[s]);
        }
        catch
        {
            return BuildRaw(input);
        }

        // Frame
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);
        bw.WriteBits(1UL, 1);  // coded flag
        bw.WriteVarUInt((ulong)input.Length);
        bw.WriteBits((ulong)bestStride, 8);  // 1-255 fits
        for (int s = 0; s < bestStride; s++)
        {
            bw.WriteVarUInt((ulong)encoded[s].Length);
            bw.WriteBytes(encoded[s]);
        }
        var coded = bw.ToArray();

        // Self-verify: round-trip
        try
        {
            var rt = Decode(coded);
            if (rt.Length == input.Length)
            {
                bool ok = true;
                for (int i = 0; i < input.Length; i++)
                    if (rt[i] != input[i]) { ok = false; break; }
                if (ok)
                {
                    var raw = BuildRaw(input);
                    return coded.Length < raw.Length ? coded : raw;
                }
            }
        }
        catch { }

        return BuildRaw(input);
    }

    public static byte[] Decode(byte[] encoded)
    {
        var br = new BitReader(encoded);
        br.ReadMagic16(MAGIC_ID, "STRDSPLT");

        int version = (int)br.ReadBits(8);
        if (version != VERSION) throw new InvalidDataException($"Bad version {version}");
        bool isCoded = br.ReadBits(1) == 1;
        int origLen = (int)br.ReadVarUInt();
        if (!isCoded)
        {
            var raw = new byte[origLen];
            for (int i = 0; i < origLen; i++) raw[i] = (byte)br.ReadBits(8);
            return raw;
        }

        int stride = (int)br.ReadBits(8);
        if (stride < 2 || stride > 64) throw new InvalidDataException($"Bad stride {stride}");

        var streams = new byte[stride][];
        for (int s = 0; s < stride; s++)
        {
            int encLen = (int)br.ReadVarUInt();
            var encStream = br.ReadBytes(encLen);
            streams[s] = NibbleContextBep.Decode(encStream);
        }

        // Reassemble: byte at position i comes from streams[i mod stride] at index i / stride
        var output = new byte[origLen];
        for (int i = 0; i < origLen; i++)
        {
            int s = i % stride;
            int idx = i / stride;
            // streams[s] has length ceil((origLen - s) / stride)
            output[i] = streams[s][idx];
        }
        return output;
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    /// <summary>Probe-test for whether stride-split would help.
    /// Returns true if any stride > 1 produces sum-entropy below order-0
    /// entropy by a meaningful margin.</summary>
    public static bool LooksStrideStructured(byte[] input)
    {
        if (input.Length < MIN_INPUT) return false;
        return DetectBestStride(input) > 1;
    }

    /// <summary>Detect best stride by measuring per-stream order-0 entropy.
    /// Returns 1 if no stride shows enough advantage to overcome framing
    /// overhead. Otherwise returns the best stride (always > 1).</summary>
    private static int DetectBestStride(byte[] input)
    {
        int sampleLen = Math.Min(input.Length, 32768);
        // Baseline: order-0 entropy of unified stream
        double baseline = SampleEntropy(input, 0, 1, sampleLen);
        double baselineBits = baseline * sampleLen;

        int bestStride = 1;
        double bestScore = baselineBits;

        foreach (int s in STRIDES)
        {
            if (input.Length < s * 64) continue; // need enough data per stream
            double sumBits = 0;
            for (int k = 0; k < s; k++)
            {
                // Stream length within sample: ceil((sampleLen - k) / s)
                int streamLen = (sampleLen - k + s - 1) / s;
                if (streamLen < 16) continue;
                sumBits += SampleEntropy(input, k, s, sampleLen) * streamLen;
            }
            // Add framing overhead estimate: 32 bits per stream + ~50 bytes per
            // NibbleContextBep header (rough)
            sumBits += s * 32 + s * 400;
            if (sumBits < bestScore * 0.97)  // require 3% improvement to overcome real costs
            {
                bestScore = sumBits;
                bestStride = s;
            }
        }
        return bestStride;
    }

    private static double SampleEntropy(byte[] input, int offset, int stride, int sampleLen)
    {
        var counts = new int[256];
        int n = 0;
        for (int i = offset; i < sampleLen && i < input.Length; i += stride)
        {
            counts[input[i]]++;
            n++;
        }
        if (n == 0) return 0;
        double H = 0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / n;
            H -= p * Math.Log2(p);
        }
        return H;
    }

    private static byte[][] Split(byte[] input, int stride)
    {
        var streams = new byte[stride][];
        for (int s = 0; s < stride; s++)
        {
            int len = (input.Length - s + stride - 1) / stride;
            streams[s] = new byte[len];
        }
        var indices = new int[stride];
        for (int i = 0; i < input.Length; i++)
        {
            int s = i % stride;
            streams[s][indices[s]++] = input[i];
        }
        return streams;
    }

    private static byte[] BuildRaw(byte[] input)
    {
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);
        bw.WriteBits(0UL, 1);
        bw.WriteVarUInt((ulong)input.Length);
        for (int i = 0; i < input.Length; i++) bw.WriteBits(input[i], 8);
        return bw.ToArray();
    }
}
