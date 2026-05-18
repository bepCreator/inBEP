// =============================================================================
// DictionaryPreprocessorBep — V16.9
//
// Byte-level absent-marker dictionary preprocessor. Identifies frequent
// multi-byte phrases in the input, substitutes each with an absent single-
// byte marker. The decoder reverses by expanding each marker back to its
// phrase. Composes cleanly upstream of TextPipelineCtxBep / Adaptive.
//
// MOTIVATION
// ──────────
// Brotli wins on text/code files (samba, news, obj2) by ~1.5-2.7pt because
// of its 120 KB static dictionary. We don't have a dictionary preprocessor.
//
// This variant builds a per-file dictionary dynamically from the actual
// input content. Trade-off: the dictionary is transmitted per archive,
// so it must amortize over enough substitutions to be net-positive.
//
// CORE INSIGHT
// ────────────
// Most text inputs use only ~80-128 distinct byte values out of 256. The
// absent byte values are unused codes, by definition. Each absent byte is
// a free single-byte marker we can use to replace a multi-byte phrase.
// Per substitution: replace N-byte phrase with 1-byte marker → save N-1
// bytes per substitution, with NO flag bit needed (marker is structurally
// absent from the input, so its presence in the encoded stream is
// unambiguous).
//
// FORMAT
// ──────
// [1 byte]    flag       — 0x00 = passthrough, 0x01 = framed
// [varint]    inputLen   — original byte count
// [1 byte]    ruleCount  — number of dictionary entries (0..255)
// per rule:
//   [1 byte]   marker_byte
//   [1 byte]   phrase_length (1..255)
//   [N bytes]  phrase_bytes
// [N bytes]   substituted payload
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace InBep.Core;

public static class DictionaryPreprocessorBep
{
    private const byte FLAG_PASSTHROUGH = 0x00;
    private const byte FLAG_FRAMED      = 0x01;

    public const int MIN_USEFUL_INPUT = 4096;

    /// <summary>Phrase lengths to scan for, longest first. Longer phrases
    /// take precedence in greedy selection so common big chunks get caught
    /// before being consumed by smaller phrases.</summary>
    private static readonly int[] PHRASE_LENGTHS = { 16, 12, 8, 6, 4, 3 };

    /// <summary>Minimum frequency. Below this, per-rule overhead exceeds savings.</summary>
    private const int MIN_FREQUENCY = 8;

    /// <summary>Maximum dictionary entries. We don't have more than 256 absent
    /// bytes anyway, and diminishing returns kick in past ~64 entries.</summary>
    private const int MAX_RULES = 64;

    public static byte[] Encode(byte[] input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Length < MIN_USEFUL_INPUT) return WrapPassThrough(input);

        // V16.18 TIME-OPT: near-Shannon early-bail. On near-uniform input,
        // no absent-byte markers will be available (alphabet is full) AND
        // no phrases will repeat enough to be substitutable. Pre-detect via
        // byte entropy and short-circuit.
        bool gateFired = LooksNearShannon(input);
        PickerDiagnostics.RecordNearShannonGate("DictionaryPreprocessorBep", input.Length, gateFired);
        if (gateFired) return WrapPassThrough(input);

        // 1. Find absent byte values
        var byteUsed = new bool[256];
        foreach (byte b in input) byteUsed[b] = true;
        var absentBytes = new List<byte>();
        for (int b = 0; b < 256; b++) if (!byteUsed[b]) absentBytes.Add((byte)b);
        if (absentBytes.Count == 0) return WrapPassThrough(input);

        // 2. Greedy rule selection.
        //
        // We track per byte-position: lengthAt[i] = the substituted phrase
        // length starting at i (0 if no substitution starts there).
        // markerForLength[marker] tells us the phrase length for a marker.
        // The "occupied" bool[] marks bytes inside (or at the start of)
        // any substitution range — used to enforce non-overlap.
        var occupied = new bool[input.Length];
        var lengthAt = new byte[input.Length];   // 0 = no substitution starts here
        var markerAt = new byte[input.Length];   // valid only when lengthAt[i] > 0
        var rules = new List<(byte marker, byte[] phrase)>();
        int markerIdx = 0;

        foreach (int phraseLen in PHRASE_LENGTHS)
        {
            if (rules.Count >= MAX_RULES) break;
            if (markerIdx >= absentBytes.Count) break;
            if (phraseLen >= input.Length / 4) continue;

            // Count phrases by hash, with first-occurrence verification later.
            var counts = new Dictionary<long, int>();
            var firstPos = new Dictionary<long, int>();

            for (int i = 0; i + phraseLen <= input.Length; i++)
            {
                if (RangeOccupied(occupied, i, phraseLen)) continue;
                long hash = ComputeHash(input, i, phraseLen);
                if (counts.TryGetValue(hash, out int c))
                {
                    int fp = firstPos[hash];
                    if (BytesEqualAt(input, i, fp, phraseLen))
                    {
                        counts[hash] = c + 1;
                    }
                }
                else
                {
                    counts[hash] = 1;
                    firstPos[hash] = i;
                }
            }

            // Sort candidates by predicted savings (per-phrase, descending).
            // Predicted savings = freq * (phraseLen - 1) - (2 + phraseLen)
            //   (savings per substitution × frequency, minus dictionary overhead)
            var candidates = new List<(int firstPos, int freq)>();
            foreach (var kv in counts)
            {
                if (kv.Value < MIN_FREQUENCY) continue;
                int predictedSavings = kv.Value * (phraseLen - 1) - (2 + phraseLen);
                if (predictedSavings <= 0) continue;
                candidates.Add((firstPos[kv.Key], kv.Value));
            }
            candidates.Sort((a, b) => b.freq.CompareTo(a.freq));

            // Try each candidate phrase
            foreach (var (fp, _) in candidates)
            {
                if (rules.Count >= MAX_RULES) break;
                if (markerIdx >= absentBytes.Count) break;

                var phrase = new byte[phraseLen];
                Buffer.BlockCopy(input, fp, phrase, 0, phraseLen);
                long phraseHash = ComputeHash(input, fp, phraseLen);

                // Find non-overlapping occurrences in unoccupied territory
                var positions = new List<int>();
                int i = 0;
                while (i + phraseLen <= input.Length)
                {
                    if (RangeOccupied(occupied, i, phraseLen)) { i++; continue; }
                    if (ComputeHash(input, i, phraseLen) != phraseHash) { i++; continue; }
                    if (!BytesEqualAt(input, i, fp, phraseLen)) { i++; continue; }
                    positions.Add(i);
                    i += phraseLen;  // greedy non-overlap
                }

                if (positions.Count < MIN_FREQUENCY) continue;
                int actualSavings = positions.Count * (phraseLen - 1) - (2 + phraseLen);
                if (actualSavings <= 0) continue;

                byte marker = absentBytes[markerIdx++];
                rules.Add((marker, phrase));
                foreach (int pos in positions)
                {
                    markerAt[pos] = marker;
                    lengthAt[pos] = (byte)phraseLen;
                    for (int k = 0; k < phraseLen; k++) occupied[pos + k] = true;
                }
            }
        }

        if (rules.Count == 0) return WrapPassThrough(input);

        // 3. Build the substituted payload by walking input.
        // At each position: lengthAt[i] > 0 means a substitution starts here;
        // emit the marker and skip the phrase. Otherwise emit literal byte.
        var payload = new List<byte>(input.Length);
        for (int i = 0; i < input.Length; )
        {
            if (lengthAt[i] > 0)
            {
                payload.Add(markerAt[i]);
                i += lengthAt[i];
            }
            else
            {
                payload.Add(input[i]);
                i++;
            }
        }

        // 4. Frame
        byte[] framed = FrameOutput(input.Length, rules, payload);

        // 5. Self-verify
        try
        {
            byte[] roundTrip = Decode(framed);
            if (!ByteEquals(roundTrip, input)) return WrapPassThrough(input);
        }
        catch
        {
            return WrapPassThrough(input);
        }

        if (framed.Length >= input.Length + 1) return WrapPassThrough(input);
        return framed;
    }

    public static byte[] Decode(byte[] wrapped)
    {
        if (wrapped == null || wrapped.Length == 0)
            throw new InvalidDataException("DictionaryPreprocessor: empty input");

        if (wrapped[0] == FLAG_PASSTHROUGH)
        {
            var raw = new byte[wrapped.Length - 1];
            Buffer.BlockCopy(wrapped, 1, raw, 0, raw.Length);
            return raw;
        }
        if (wrapped[0] != FLAG_FRAMED)
            throw new InvalidDataException(
                $"DictionaryPreprocessor: unknown flag 0x{wrapped[0]:X2}");

        int pos = 1;
        long inputLen = (long)ReadVarUInt(wrapped, ref pos);
        int ruleCount = wrapped[pos++];

        var markerToPhrase = new byte[256][];
        var isMarker = new bool[256];
        for (int r = 0; r < ruleCount; r++)
        {
            byte marker = wrapped[pos++];
            int phraseLen = wrapped[pos++];
            var phrase = new byte[phraseLen];
            Buffer.BlockCopy(wrapped, pos, phrase, 0, phraseLen);
            pos += phraseLen;
            markerToPhrase[marker] = phrase;
            isMarker[marker] = true;
        }

        var output = new List<byte>((int)inputLen);
        for (int i = pos; i < wrapped.Length; i++)
        {
            byte b = wrapped[i];
            if (isMarker[b])
            {
                output.AddRange(markerToPhrase[b]!);
            }
            else
            {
                output.Add(b);
            }
        }

        if (output.Count != inputLen)
            throw new InvalidDataException(
                $"DictionaryPreprocessor: decoded length mismatch (got {output.Count}, expected {inputLen})");

        return output.ToArray();
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool RangeOccupied(bool[] occupied, int start, int len)
    {
        for (int k = 0; k < len; k++) if (occupied[start + k]) return true;
        return false;
    }

    private static bool BytesEqualAt(byte[] data, int a, int b, int len)
    {
        for (int k = 0; k < len; k++) if (data[a + k] != data[b + k]) return false;
        return true;
    }

    private static long ComputeHash(byte[] data, int offset, int length)
    {
        // FNV-1a 64-bit
        const long PRIME = 0x100000001B3L;
        long hash = unchecked((long)0xCBF29CE484222325L);
        for (int i = 0; i < length; i++)
        {
            hash ^= data[offset + i];
            hash = unchecked(hash * PRIME);
        }
        return hash;
    }

    private static byte[] WrapPassThrough(byte[] input)
    {
        var raw = new byte[input.Length + 1];
        raw[0] = FLAG_PASSTHROUGH;
        Buffer.BlockCopy(input, 0, raw, 1, input.Length);
        return raw;
    }

    private static byte[] FrameOutput(int inputLen,
                                       List<(byte marker, byte[] phrase)> rules,
                                       List<byte> payload)
    {
        int headerBytes = 1 + VarUIntSize((ulong)inputLen) + 1;
        foreach (var (_, phrase) in rules) headerBytes += 2 + phrase.Length;

        var output = new byte[headerBytes + payload.Count];
        int pos = 0;
        output[pos++] = FLAG_FRAMED;
        WriteVarUInt(output, ref pos, (ulong)inputLen);
        output[pos++] = (byte)rules.Count;
        foreach (var (marker, phrase) in rules)
        {
            output[pos++] = marker;
            output[pos++] = (byte)phrase.Length;
            Buffer.BlockCopy(phrase, 0, output, pos, phrase.Length);
            pos += phrase.Length;
        }
        for (int i = 0; i < payload.Count; i++) output[pos + i] = payload[i];
        return output;
    }

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void WriteVarUInt(byte[] buf, ref int pos, ulong v)
    {
        while (v >= 0x80) { buf[pos++] = (byte)((v & 0x7F) | 0x80); v >>= 7; }
        buf[pos++] = (byte)(v & 0x7F);
    }

    private static ulong ReadVarUInt(byte[] buf, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (true)
        {
            byte b = buf[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("DictionaryPreprocessor varint too long");
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
