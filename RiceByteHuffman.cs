// =============================================================================
// RiceByteHuffman — Optional canonical Huffman wrapper for RiceBEP output
//
// V15 NEW. Wraps the byte stream emitted by RiceBEP/UnaryBEP with a static
// canonical Huffman code if and only if the wrap would produce smaller output.
//
// WHY:
//   On heavily skewed FreqRank distributions (rank-0 dominates), RiceBEP
//   output bytes collapse onto a small set of recurring rotation patterns.
//   At k=1 with rank-0 = 80%, three byte values (0x92, 0x24, 0x49) account
//   for ~58% of all output bytes. A single static Huffman table over the
//   256-byte alphabet captures this concentration in 2-3 bits per common
//   byte instead of 8.
//
//   On flatter distributions (Zipf with rank-0 = 25-30%, typical English
//   prose post-FreqRank), the byte histogram is nearly uniform; Huffman
//   gains nothing and the table overhead would cost a few hundred bytes.
//
// FRAMING (when Huffman is applied):
//   [2 bytes payload-bit-count (little-endian uint16 ... actually 4 bytes uint32)]
//   [256 bytes: code length per byte value, 0 = symbol absent]
//   [N bytes: Huffman-coded payload, MSB-first packing]
//
// PUBLIC API:
//   MaybeWrap(byte[] raw) → (byte[] result, bool wrapped)
//     result is the smaller of {raw, huffmanFrame}.
//     wrapped = false → result IS raw (return value identical to input).
//     wrapped = true  → result is the Huffman-framed payload.
//
//   Unwrap(byte[] huffmanFrame) → byte[] raw bytes.
//
// CODE LENGTH CAP: 15 bits. With 256 symbols, the natural Huffman build can
// produce codes up to 30+ bits on extremely skewed distributions. We use a
// simple length-cap that clips long codes to 15 and rebalances by
// incrementing shorter codes; this is suboptimal vs. package-merge but
// matches Drop 14's existing canonical-Huffman style in Lz77BepV2/V3 and is
// robust against the heaviest distributions.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace InBep.Core;

public static class RiceByteHuffman
{
    /// <summary>Maximum Huffman code length we'll allow. 15 keeps codes
    /// representable in a ushort with room to spare.</summary>
    private const int MAX_CODE_LEN = 15;

    /// <summary>Try to wrap the input bytes with canonical Huffman. Returns
    /// the smaller of (raw input, wrapped frame). Caller must store the
    /// boolean so the decoder knows which path to take.</summary>
    public static (byte[] result, bool wrapped) MaybeWrap(byte[] raw)
    {
        if (raw == null) throw new ArgumentNullException(nameof(raw));
        // Tiny inputs: framing overhead is large relative to the body.
        // 256-byte length table + 4-byte payload-len header = 260 bytes
        // of fixed overhead. Don't bother.
        if (raw.Length < 512) return (raw, false);

        // 1. Histogram
        var freq = new long[256];
        for (int i = 0; i < raw.Length; i++) freq[raw[i]]++;

        int distinct = 0;
        for (int i = 0; i < 256; i++) if (freq[i] > 0) distinct++;
        // If only one byte value, raw is already minimal — Huffman would
        // emit zero-length codes which break downstream invariants.
        if (distinct < 2) return (raw, false);

        // 2. Build code lengths
        var lengths = BuildHuffmanLengths(freq);

        // 3. Compute total bit cost. If wrapped frame would be larger, bail.
        long bitCost = 0;
        for (int i = 0; i < 256; i++) bitCost += freq[i] * lengths[i];
        long byteCost = (bitCost + 7) / 8;
        long frameCost = 4 + 256 + byteCost;   // 4-byte header + 256-byte length table + payload
        if (frameCost >= raw.Length) return (raw, false);

        // 4. Build canonical codes from lengths.
        var codes = CanonicalCodes(lengths);

        // 5. Encode the payload.
        var payload = new byte[byteCost];
        ulong buf = 0;
        int bits = 0;
        int bytePos = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            int sym = raw[i];
            int len = lengths[sym];
            ulong code = (ulong)codes[sym];
            // Emit `len` bits of code, MSB first.
            buf = (buf << len) | code;
            bits += len;
            while (bits >= 8)
            {
                bits -= 8;
                payload[bytePos++] = (byte)((buf >> bits) & 0xFF);
            }
        }
        if (bits > 0)
        {
            payload[bytePos++] = (byte)((buf << (8 - bits)) & 0xFF);
        }
        if (bytePos != byteCost)
            throw new InvalidOperationException(
                $"RiceByteHuffman: byte-count mismatch (wrote {bytePos}, expected {byteCost})");

        // 6. Assemble frame: [4-byte uint32 totalBits] [256-byte length table] [payload].
        var frame = new byte[4 + 256 + (int)byteCost];
        frame[0] = (byte)(bitCost & 0xFF);
        frame[1] = (byte)((bitCost >> 8) & 0xFF);
        frame[2] = (byte)((bitCost >> 16) & 0xFF);
        frame[3] = (byte)((bitCost >> 24) & 0xFF);
        for (int i = 0; i < 256; i++) frame[4 + i] = (byte)lengths[i];
        Buffer.BlockCopy(payload, 0, frame, 4 + 256, (int)byteCost);

        return (frame, true);
    }

    /// <summary>Decode a Huffman-wrapped frame back to the original bytes.</summary>
    public static byte[] Unwrap(byte[] frame)
    {
        if (frame == null || frame.Length < 4 + 256)
            throw new InvalidDataException("RiceByteHuffman.Unwrap: frame too short");

        long totalBits = (long)frame[0]
                       | ((long)frame[1] << 8)
                       | ((long)frame[2] << 16)
                       | ((long)frame[3] << 24);
        var lengths = new int[256];
        for (int i = 0; i < 256; i++) lengths[i] = frame[4 + i];

        var codes = CanonicalCodes(lengths);

        // Build a decode table: for each (length, code) pair, map back to symbol.
        // Simple linear table indexed by (length, code) is fine for 256-symbol
        // alphabet — small enough that lookup speed isn't the bottleneck.
        // We use a per-length list of (code, symbol).
        var byLen = new List<(uint code, int sym)>[MAX_CODE_LEN + 1];
        for (int L = 0; L <= MAX_CODE_LEN; L++) byLen[L] = new List<(uint, int)>();
        for (int s = 0; s < 256; s++)
            if (lengths[s] > 0)
                byLen[lengths[s]].Add(((uint)codes[s], s));
        // Sort each length's entries by code for binary-search-style lookup.
        // Linear scan is fine for ≤ 256 entries.

        int payloadStart = 4 + 256;
        var output = new List<byte>((int)(totalBits / 4));

        long bitsRead = 0;
        ulong buf = 0;
        int bufBits = 0;
        int bytePos = payloadStart;

        while (bitsRead < totalBits)
        {
            // Refill the buffer up to >= MAX_CODE_LEN bits.
            while (bufBits < MAX_CODE_LEN && bytePos < frame.Length)
            {
                buf = (buf << 8) | frame[bytePos++];
                bufBits += 8;
            }
            // Try lengths in increasing order (shortest first — the canonical
            // structure means a shorter prefix is preferred when it matches).
            bool found = false;
            for (int L = 1; L <= MAX_CODE_LEN; L++)
            {
                if (byLen[L].Count == 0) continue;
                if (bufBits < L) break;   // not enough bits left
                uint candidate = (uint)((buf >> (bufBits - L)) & ((1U << L) - 1));
                foreach (var (code, sym) in byLen[L])
                {
                    if (code == candidate)
                    {
                        output.Add((byte)sym);
                        bufBits -= L;
                        bitsRead += L;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            if (!found)
                throw new InvalidDataException(
                    $"RiceByteHuffman.Unwrap: no matching code at bit {bitsRead}/{totalBits}");
        }

        return output.ToArray();
    }

    // ── Huffman code-length build (length-limited) ───────────────────────────

    private static int[] BuildHuffmanLengths(long[] freq)
    {
        var lengths = new int[256];

        // Standard Huffman tree build using a priority queue.
        // Each entry: (weight, depth-or-marker, symbol if leaf, child indices if internal)
        // For length-extraction we only care about leaf depth, so we track
        // parent links and walk depths after the tree is complete.

        // Node arrays — symbols 0..255 are leaves; internal nodes start at 256.
        var weight = new long[512];   // up to 256 leaves + 255 internal = 511 nodes
        var parent = new int[512];
        for (int i = 0; i < 512; i++) parent[i] = -1;

        int leafCount = 0;
        for (int s = 0; s < 256; s++)
        {
            if (freq[s] > 0)
            {
                weight[s] = freq[s];
                leafCount++;
            }
        }

        // Min-heap of active node indices, ordered by weight.
        // For the small node count, a simple sorted list is fine, but we'll
        // use a List<(weight, idx)> sorted by weight on each insertion.
        // Performance: 256 inserts × O(n log n) = trivial.
        var heap = new List<(long w, int idx)>();
        for (int s = 0; s < 256; s++)
            if (freq[s] > 0)
                heap.Add((weight[s], s));
        heap.Sort((a, b) => a.w.CompareTo(b.w));

        int nextInternal = 256;
        while (heap.Count > 1)
        {
            var a = heap[0];
            var b = heap[1];
            heap.RemoveAt(0); heap.RemoveAt(0);
            int newIdx = nextInternal++;
            weight[newIdx] = a.w + b.w;
            parent[a.idx] = newIdx;
            parent[b.idx] = newIdx;
            // Insert merged node maintaining sorted order
            int insertAt = 0;
            while (insertAt < heap.Count && heap[insertAt].w <= weight[newIdx]) insertAt++;
            heap.Insert(insertAt, (weight[newIdx], newIdx));
        }

        // Walk from each leaf to the root counting depth.
        for (int s = 0; s < 256; s++)
        {
            if (freq[s] == 0) { lengths[s] = 0; continue; }
            int depth = 0;
            int cur = s;
            while (parent[cur] != -1) { depth++; cur = parent[cur]; }
            lengths[s] = depth == 0 ? 1 : depth;   // single-leaf edge case
        }

        // Cap lengths to MAX_CODE_LEN. Simple clip-and-rebalance: clip long
        // codes down to MAX_CODE_LEN, then walk shorter codes upward by 1
        // until the Kraft sum is balanced.
        if (NeedsClip(lengths))
            ClipLengths(lengths);

        return lengths;
    }

    private static bool NeedsClip(int[] lengths)
    {
        for (int i = 0; i < 256; i++) if (lengths[i] > MAX_CODE_LEN) return true;
        return false;
    }

    private static void ClipLengths(int[] lengths)
    {
        // Step 1: clip everything > MAX to MAX.
        for (int i = 0; i < 256; i++)
            if (lengths[i] > MAX_CODE_LEN) lengths[i] = MAX_CODE_LEN;

        // Step 2: compute Kraft sum K = Σ 2^(-len_i).
        // For valid prefix code: K must equal 1.0 exactly (in our scaling, sum of 2^(MAX-len_i) == 2^MAX).
        // After clipping, K may exceed 2^MAX (over budget). Move bits down by lengthening short codes.
        long K = 0;
        for (int i = 0; i < 256; i++)
            if (lengths[i] > 0)
                K += 1L << (MAX_CODE_LEN - lengths[i]);
        long target = 1L << MAX_CODE_LEN;

        // While K > target, lengthen the longest code that's still < MAX.
        // (Adding 1 bit halves that code's weight contribution.)
        while (K > target)
        {
            // Find the symbol with the longest length still < MAX_CODE_LEN
            // and a length <= some threshold. Simplest: lengthen the
            // shortest-non-zero code whose length is < MAX.
            int shortestIdx = -1;
            int shortestLen = MAX_CODE_LEN + 1;
            for (int i = 0; i < 256; i++)
            {
                if (lengths[i] > 0 && lengths[i] < MAX_CODE_LEN && lengths[i] < shortestLen)
                {
                    shortestLen = lengths[i];
                    shortestIdx = i;
                }
            }
            if (shortestIdx < 0) break;   // can't fix; shouldn't happen with a 256-symbol alphabet
            K -= 1L << (MAX_CODE_LEN - lengths[shortestIdx]);
            lengths[shortestIdx]++;
            K += 1L << (MAX_CODE_LEN - lengths[shortestIdx]);
        }
    }

    // ── Canonical code generation from lengths ───────────────────────────────

    /// <summary>
    /// Generate canonical Huffman codes from a length-per-symbol table.
    /// Codes are ordered by (length, symbol) — shorter lengths get smaller
    /// numeric codes, ties broken by symbol index.
    /// </summary>
    private static int[] CanonicalCodes(int[] lengths)
    {
        var codes = new int[256];

        // Sort symbol indices by (length, symbol)
        var order = new List<int>(256);
        for (int s = 0; s < 256; s++) if (lengths[s] > 0) order.Add(s);
        order.Sort((a, b) =>
        {
            int dl = lengths[a].CompareTo(lengths[b]);
            return dl != 0 ? dl : a.CompareTo(b);
        });

        int code = 0;
        int prevLen = 0;
        foreach (int s in order)
        {
            int len = lengths[s];
            if (prevLen != 0) code = (code + 1) << (len - prevLen);
            codes[s] = code;
            prevLen = len;
        }

        return codes;
    }
}
