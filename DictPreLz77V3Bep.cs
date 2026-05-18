// =============================================================================
// DictPreLz77V3Bep — V16.12 hybrid #21
//
// Hybrid born from the Round 2 hybridization analysis: cascade
// DictionaryPreprocessor + Lz77BepV3.
//
// MOTIVATION
// ──────────
// Python simulation showed that DictionaryPreprocessor pairs structurally with
// LZ77-based codecs but NOT with BWT-based codecs:
//
//   On book1 (768 KB), with 32 dict rules:
//     dict + zlib    →  -6,915 bytes vs raw zlib   (LZ77, wins)
//     dict + bz2     →  +1,541 bytes vs raw bz2    (BWT, loses)
//
// Reason: BWT depends on byte-level locality (long runs of similar bytes after
// the transform). Substituting 4-byte phrases with single-byte markers destroys
// those runs. LZ77 just finds back-references — it doesn't care about runs, so
// shorter representation = strictly fewer back-pointer bits to encode.
//
// This is also why Brotli wins on text where TextPipelineCtxBep loses: Brotli
// is LZ77-based with a static dictionary. Lz77BepV3 already has LZ77; this
// cascade adds the dynamic per-file dictionary, closing that architectural gap.
//
// EXPECTED WIN ZONE
// ─────────────────
// Text files (English, source code, XML, log) where:
//   - Common multi-byte phrases occur at frequency ≥8
//   - Input has at least 1 absent byte value (≥7-bit content)
//
// Predicted to win on: news, paper1-6, progc/progl/progp, sum, trans,
// alice29, lcet10, plrabn12, book1, book2, samba, dickens, world192.
//
// Predicted to passthrough on: binary data without absent bytes (mesh,
// medical TIFF, x-ray, audio).
//
// FALLBACK
// ────────
// If DictionaryPreprocessor passes through (no absent bytes / no useful rules),
// just call Lz77BepV3 directly with a flag indicating no-dict-stage.
// If output isn't smaller than raw Lz77V3, return raw Lz77V3.
//
// FORMAT
// ──────
// [1 byte]   flag       0x00 = passthrough, 0x01 = dict+lz77, 0x02 = lz77 only
// [varint]   inputLen
// — if flag == 0x01: dictPayload bytes follow (which themselves are framed by
//   DictionaryPreprocessor; Lz77V3-encoded bytes after that)
// — if flag == 0x02: just Lz77V3 bytes (skip-dict case)
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;

namespace InBep.Core;

public static class DictPreLz77V3Bep
{
    private const byte FLAG_PASSTHROUGH = 0x00;
    private const byte FLAG_DICT_LZ77   = 0x01;
    private const byte FLAG_LZ77_ONLY   = 0x02;

    /// <summary>Below this, the cascade overhead (1 byte flag + varint length +
    /// per-rule dict header) exceeds plausible savings.</summary>
    public const int MIN_USEFUL_INPUT = 4096;

    public static byte[] Encode(byte[] input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Length < MIN_USEFUL_INPUT) return WrapPassThrough(input);

        // V16.18 TIME-OPT: near-Shannon early-bail. Both stages (DictPre
        // and Lz77V3) waste significant time on already-near-Shannon input
        // where neither can compress. Short-circuit to passthrough.
        bool gateFired = LooksNearShannon(input);
        PickerDiagnostics.RecordNearShannonGate("DictPreLz77V3Bep", input.Length, gateFired);
        if (gateFired) return WrapPassThrough(input);

        // Stage 1: dictionary preprocessing
        byte[] dictOut;
        try
        {
            dictOut = DictionaryPreprocessorBep.Encode(input);
        }
        catch
        {
            return WrapPassThrough(input);
        }

        // Three sub-cases for stage 2:
        //   a) dict-preprocess made the data smaller → Lz77V3 on dict output
        //   b) dict-preprocess passed through (no absent bytes / no rules)
        //      → Lz77V3 on raw input directly
        //   c) dict-preprocess made it larger → Lz77V3 on raw input directly
        //
        // For (a) and (b)/(c) we want to actually compare both pathways and
        // pick whichever produces the smaller final output. That's the only
        // way to be sure the cascade isn't hurting us on edge cases.

        byte[] lz77OnDict;
        byte[] lz77OnRaw;
        try
        {
            lz77OnDict = Lz77BepV3.Encode(dictOut);
            lz77OnRaw = Lz77BepV3.Encode(input);
        }
        catch
        {
            return WrapPassThrough(input);
        }

        // Build candidate frames for each pathway
        byte[] cascadeFrame = BuildFrame(FLAG_DICT_LZ77, input.Length, lz77OnDict);
        byte[] lz77OnlyFrame = BuildFrame(FLAG_LZ77_ONLY, input.Length, lz77OnRaw);
        byte[] rawFrame = WrapPassThrough(input);

        // Verify both pathways round-trip (defensive — fall back to raw on any failure)
        byte[]? best = null;
        int bestLen = rawFrame.Length;
        if (TryRoundTrip(cascadeFrame, input))
        {
            if (cascadeFrame.Length < bestLen) { best = cascadeFrame; bestLen = cascadeFrame.Length; }
        }
        if (TryRoundTrip(lz77OnlyFrame, input))
        {
            if (lz77OnlyFrame.Length < bestLen) { best = lz77OnlyFrame; bestLen = lz77OnlyFrame.Length; }
        }
        return best ?? rawFrame;
    }

    public static byte[] Decode(byte[] wrapped)
    {
        if (wrapped == null || wrapped.Length == 0)
            throw new InvalidDataException("DictPreLz77V3: empty input");

        byte flag = wrapped[0];

        if (flag == FLAG_PASSTHROUGH)
        {
            int pos = 1;
            long origLen = (long)ReadVarUInt(wrapped, ref pos);
            int payloadLen = wrapped.Length - pos;
            if (payloadLen != origLen)
                throw new InvalidDataException(
                    $"DictPreLz77V3 passthrough length mismatch: {payloadLen} vs {origLen}");
            var raw = new byte[(int)origLen];
            Buffer.BlockCopy(wrapped, pos, raw, 0, raw.Length);
            return raw;
        }

        if (flag == FLAG_DICT_LZ77)
        {
            int pos = 1;
            long origLen = (long)ReadVarUInt(wrapped, ref pos);
            // Remaining bytes are the Lz77V3-encoded representation of dictOut
            var lz77Body = new byte[wrapped.Length - pos];
            Buffer.BlockCopy(wrapped, pos, lz77Body, 0, lz77Body.Length);
            byte[] dictOut = Lz77BepV3.Decode(lz77Body);
            byte[] orig = DictionaryPreprocessorBep.Decode(dictOut);
            if (orig.Length != origLen)
                throw new InvalidDataException(
                    $"DictPreLz77V3 length mismatch: dict-decoded {orig.Length} vs expected {origLen}");
            return orig;
        }

        if (flag == FLAG_LZ77_ONLY)
        {
            int pos = 1;
            long origLen = (long)ReadVarUInt(wrapped, ref pos);
            var lz77Body = new byte[wrapped.Length - pos];
            Buffer.BlockCopy(wrapped, pos, lz77Body, 0, lz77Body.Length);
            byte[] orig = Lz77BepV3.Decode(lz77Body);
            if (orig.Length != origLen)
                throw new InvalidDataException(
                    $"DictPreLz77V3 lz77-only length mismatch: {orig.Length} vs {origLen}");
            return orig;
        }

        throw new InvalidDataException($"DictPreLz77V3: unknown flag 0x{flag:X2}");
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static byte[] BuildFrame(byte flag, int origLen, byte[] body)
    {
        int headerSize = 1 + VarUIntSize((ulong)origLen);
        var output = new byte[headerSize + body.Length];
        output[0] = flag;
        int pos = 1;
        WriteVarUInt(output, ref pos, (ulong)origLen);
        Buffer.BlockCopy(body, 0, output, pos, body.Length);
        return output;
    }

    private static byte[] WrapPassThrough(byte[] input)
    {
        int headerSize = 1 + VarUIntSize((ulong)input.Length);
        var output = new byte[headerSize + input.Length];
        output[0] = FLAG_PASSTHROUGH;
        int pos = 1;
        WriteVarUInt(output, ref pos, (ulong)input.Length);
        Buffer.BlockCopy(input, 0, output, pos, input.Length);
        return output;
    }

    private static bool TryRoundTrip(byte[] frame, byte[] expectedOriginal)
    {
        try
        {
            byte[] rt = Decode(frame);
            if (rt.Length != expectedOriginal.Length) return false;
            for (int i = 0; i < rt.Length; i++)
                if (rt[i] != expectedOriginal[i]) return false;
            return true;
        }
        catch { return false; }
    }

    // Varint helpers
    private static void WriteVarUInt(byte[] buf, ref int pos, ulong v)
    {
        while (v >= 0x80) { buf[pos++] = (byte)((v & 0x7F) | 0x80); v >>= 7; }
        buf[pos++] = (byte)(v & 0x7F);
    }

    private static ulong ReadVarUInt(byte[] buf, ref int pos)
    {
        ulong v = 0;
        int shift = 0;
        while (true)
        {
            byte b = buf[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("DictPreLz77V3 varint too long");
        }
    }

    private static int VarUIntSize(ulong v)
    {
        int n = 1;
        while (v >= 0x80) { n++; v >>= 7; }
        return n;
    }

    /// <summary>V16.18 TIME-OPT: detect near-Shannon byte input. Threshold 7.95 bits/byte.</summary>
    private static bool LooksNearShannon(byte[] data)
    {
        if (data.Length < 4096) return false;
        int sampleLen = Math.Min(16384, data.Length);
        Span<int> counts = stackalloc int[256];
        for (int i = 0; i < sampleLen; i++) counts[data[i]]++;
        double h = 0;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = (double)counts[i] / sampleLen;
            h -= p * Math.Log2(p);
        }
        return h > 7.95;
    }
}
