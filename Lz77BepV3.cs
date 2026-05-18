// =============================================================================
// Lz77BepV3 — V13/Drop 14 (Avenue 2): hybrid LZ77 + context-coded literals.
//
// V19.2 PARAMETER UPDATE (window enlargement):
//   WINDOW_SIZE  32_768   → 524_288  (32 KB → 512 KB)
//   HASH_BITS    15       → 17       (32 K   → 128 K hash slots)
//   MAX_CHAIN    128      → 4_096    (deeper search to find far-back matches)
//
// Motivation: lower-bound analysis of LZ77 parsing on news (377 KB, Calgary)
// found that 42% of optimal matches live at distances > 32 KB, i.e. outside
// the V19.1 window entirely. With the enlarged window the LZ77+H1-literal
// lower bound on news drops from 133,915 B to 87,753 B — a 46,162 B
// improvement from window size ALONE, holding the cost model constant.
// LZMA at the same preset hits 118,846 B, so an LZ77 implementation hitting
// even ~10% above the lower bound (~96K B) would beat LZMA by ~20%.
//
// On smaller inputs (≤ 32 KB) the window expansion is inert — paper5 and
// paper4 have 0% of matches above the old window. On 80–500 KB text inputs
// it is the dominant win available to V3's architecture.
//
// Memory cost: `prev[]` grows from 32 K ints (128 KB) to 512 K ints (2 MB)
// per encode. `head[]` grows from 32 K to 128 K ints (128 KB → 512 KB).
// Total ~2.5 MB transient allocation per Encode call. Fine for desktop,
// expensive for embedded — if needed a small-input fast path can clamp
// WINDOW_SIZE = min(WINDOW_SIZE, input.Length + 1).
//
// V19.4 RETROSPECTIVE on the V19.2 enlargement:
//   When V19.2 ran on the full corpus the news.txt result was identical to
//   V19.1 because TextPipelineCtxBep's LZ_GATE was skipping Lz77BepV3 as a
//   TextCtx candidate on coherent text. V19.3 added a size escape hatch to
//   that gate so Lz77BepV3 would run on news/lcet10/plrabn12/alice29. It
//   ran, lost the picker every time, and produced byte-identical output.
//   The LZ77+H1-literal lower-bound math above remains correct in isolation
//   but the LZ77 candidate is dominated by BEP candidates on coherent text
//   regardless of window depth — BEP's bit-level entropy floor on literals
//   is tighter than what LZ77+NibbleCtx hits in practice.
//
//   The window enlargement is kept anyway: on heterogeneous-binary inputs
//   (samba, mozilla, sum, the non-text-shaped wedge) Lz77BepV3 runs without
//   gating and the enlarged window may still pay there. Negligible cost on
//   small files (head/prev arrays size to input on the inner path), small
//   transient cost on large non-text files.
//
// =============================================================================
// CHANGE FROM V2
// =============================================================================
//
// Lz77BepV2 used canonical Huffman codes for literals, lengths, distances.
// Lz77BepV3 keeps Huffman for lengths and distances (their distributions are
// well-suited to Huffman) but feeds the literal stream through NibbleContextBep.
//
// Why this should help: after LZ77 strips long-distance repetition, the
// residual literals still have order-1 / order-2 byte-level structure that
// Huffman (which is order-0) can't capture. NibbleContextBep's order-2 nibble
// context model captures it.
//
// Concretely on English text:
//   - Common literals (after match extraction) tend to be word boundaries,
//     punctuation, and rare characters.
//   - These have strong context dependence: ' ' is likely after a letter,
//     '.' is likely before a space, etc.
//   - Order-0 Huffman: ~5 bits per literal (entropy of literal alphabet)
//   - Order-2 nibble context: ~3-4 bits per literal (real entropy)
//
// =============================================================================
// FORMAT
// =============================================================================
//
// [magic:8 "LZ77V3BP"] [version:1] [flag:1 raw/coded] [origLen:4]
// If coded:
//   [tokenCount:4]
//   [matchCount:4]
//   [literalCount:4]
//   [literalEnc: NibbleContextBep-encoded literal stream]
//   [lengthHuffmanTable: code-lengths for 29-symbol alphabet, 4 bits each]
//   [distanceHuffmanTable: code-lengths for 30-symbol alphabet]
//   [flagBytesLen:4] [flagBytes: bit-packed literal/match flags]
//   [matchStream: Huffman-coded length+distance pairs]
//
// =============================================================================
// EXPECTED IMPACT vs V2
// =============================================================================
//
//   English prose (dickens):     +1-3pt (literals carry context)
//   Source code (samba/osdb):    +1-2pt
//   XML:                          ~0pt (literals are mostly markup, low context)
//   Binary files:                 +0.5-1pt
// =============================================================================

namespace InBep.Core;

public static class Lz77BepV3
{
    private static readonly byte[] MAGIC = System.Text.Encoding.ASCII.GetBytes("LZ77V3BP");
    private const byte VERSION = 1;
    private const ushort MAGIC_ID = VariantMagicIds.Lz77BepV3;

    private const int WINDOW_SIZE = 524288;  // V19.2: was 32_768 — see header for rationale
    private const int MIN_MATCH = 4;
    private const int MAX_MATCH = 258;
    private const int HASH_BITS = 17;        // V19.2: was 15 — 4× more hash slots to keep
                                              // chain lengths sane with the enlarged window
    private const int HASH_SIZE = 1 << HASH_BITS;
    private const int HASH_MASK = HASH_SIZE - 1;
    private const int MAX_CHAIN = 4096;      // V19.2: was 128 — deeper search reaches the
                                              // far-back matches the V19.1 chain couldn't
    private const int MIN_INPUT = 1024;

    // Reuse the same length/distance tables as V2.
    private static readonly (int lenBase, int extraBits)[] LengthTable = BuildLengthTable();
    private static readonly (int distBase, int extraBits)[] DistanceTable = BuildDistanceTable();

    private static (int, int)[] BuildLengthTable()
    {
        var t = new List<(int, int)>();
        for (int i = 0; i < 8; i++) t.Add((4 + i, 0));
        int b = 12;
        for (int i = 0; i < 4; i++) { t.Add((b, 1)); b += 2; }
        for (int i = 0; i < 4; i++) { t.Add((b, 2)); b += 4; }
        for (int i = 0; i < 4; i++) { t.Add((b, 3)); b += 8; }
        for (int i = 0; i < 4; i++) { t.Add((b, 4)); b += 16; }
        for (int i = 0; i < 4; i++) { int extra = 5; int width = 32; if (b + width > 258) width = 258 - b + 1; t.Add((b, extra)); b += width; }
        t.Add((258, 0));
        return t.ToArray();
    }

    private static (int, int)[] BuildDistanceTable()
    {
        var t = new List<(int, int)>();
        int distBase = 1;
        for (int i = 0; i < 4; i++) { t.Add((distBase, 0)); distBase += 1; }
        int extra = 1;
        while (t.Count < 30)
        {
            int width = 1 << extra;
            for (int j = 0; j < 2 && t.Count < 30; j++) { t.Add((distBase, extra)); distBase += width; }
            extra++;
        }
        return t.ToArray();
    }

    private static int LengthToCode(int length)
    {
        int lo = 0, hi = LengthTable.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (LengthTable[mid].lenBase <= length) lo = mid; else hi = mid - 1; }
        return lo;
    }

    private static int DistanceToCode(int distance)
    {
        int lo = 0, hi = DistanceTable.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (DistanceTable[mid].distBase <= distance) lo = mid; else hi = mid - 1; }
        return lo;
    }

    private struct Token
    {
        public bool IsMatch;
        public byte Literal;
        public int Length;
        public int Distance;
    }

    public static byte[] Encode(byte[] input)
    {
        if (input == null || input.Length < MIN_INPUT)
            return BuildRaw(input ?? Array.Empty<byte>());

        // V16.18 TIME-OPT: near-Shannon early-bail. LZ77 tokenization on
        // already-near-Shannon byte input finds zero useful matches and adds
        // significant encode time. Pre-detect via byte entropy and short-
        // circuit. Threshold 7.95 leaves room for compressible-but-skewed
        // streams to still be tokenized.
        bool gateFired = LooksNearShannon(input);
        PickerDiagnostics.RecordNearShannonGate("Lz77BepV3", input.Length, gateFired);
        if (gateFired) return BuildRaw(input);

        List<Token> tokens;
        try { tokens = TokenizeGreedy(input); }
        catch { return BuildRaw(input); }

        // Separate literal and match streams
        var literals = new List<byte>();
        var matches = new List<(int len, int dist)>();
        var flags = new bool[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsMatch)
            {
                flags[i] = true;
                matches.Add((tokens[i].Length, tokens[i].Distance));
            }
            else
            {
                literals.Add(tokens[i].Literal);
            }
        }

        // Encode literal stream with NibbleContextBep
        byte[] literalEnc;
        try { literalEnc = NibbleContextBep.Encode(literals.ToArray()); }
        catch { return BuildRaw(input); }

        // Build Huffman tables for length/distance codes only
        var lenCounts = new long[29];
        var distCounts = new long[30];
        foreach (var m in matches)
        {
            lenCounts[LengthToCode(m.len)]++;
            distCounts[DistanceToCode(m.dist)]++;
        }
        var lenLens = BuildHuffmanLengths(lenCounts, maxLen: 15);
        var distLens = BuildHuffmanLengths(distCounts, maxLen: 15);
        var lenCodes = BuildCanonicalCodes(lenLens);
        var distCodes = BuildCanonicalCodes(distLens);

        // Frame
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);
        bw.WriteBits(1UL, 1);
        bw.WriteVarUInt((ulong)input.Length);
        bw.WriteVarUInt((ulong)tokens.Count);
        bw.WriteVarUInt((ulong)matches.Count);
        bw.WriteVarUInt((ulong)literals.Count);
        bw.WriteVarUInt((ulong)literalEnc.Length);
        bw.WriteBytes(literalEnc);

        // Length & distance Huffman tables
        for (int i = 0; i < 29; i++) bw.WriteBits((ulong)lenLens[i], 4);
        for (int i = 0; i < 30; i++) bw.WriteBits((ulong)distLens[i], 4);

        // Flags
        var flagsBw = new BitWriter();
        foreach (bool f in flags) flagsBw.WriteBits(f ? 1UL : 0UL, 1);
        var flagBytes = flagsBw.ToArray();
        bw.WriteVarUInt((ulong)flagBytes.Length);
        bw.WriteBytes(flagBytes);

        // Match stream: for each match, length code + extra + distance code + extra
        foreach (var m in matches)
        {
            int lc = LengthToCode(m.len);
            bw.WriteBits(lenCodes[lc].code, lenCodes[lc].length);
            int extraLen = m.len - LengthTable[lc].lenBase;
            if (LengthTable[lc].extraBits > 0)
                bw.WriteBits((ulong)extraLen, LengthTable[lc].extraBits);
            int dc = DistanceToCode(m.dist);
            bw.WriteBits(distCodes[dc].code, distCodes[dc].length);
            int extraDist = m.dist - DistanceTable[dc].distBase;
            if (DistanceTable[dc].extraBits > 0)
                bw.WriteBits((ulong)extraDist, DistanceTable[dc].extraBits);
        }

        var coded = bw.ToArray();

        // Self-verify
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
        br.ReadMagic16(MAGIC_ID, "LZ77V3BP");

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

        int tokenCount = (int)br.ReadVarUInt();
        int matchCount = (int)br.ReadVarUInt();
        int literalCount = (int)br.ReadVarUInt();
        int literalEncLen = (int)br.ReadVarUInt();
        var literalEnc = br.ReadBytes(literalEncLen);
        var literals = NibbleContextBep.Decode(literalEnc);

        var lenLens = new int[29];
        for (int i = 0; i < 29; i++) lenLens[i] = (int)br.ReadBits(4);
        var distLens = new int[30];
        for (int i = 0; i < 30; i++) distLens[i] = (int)br.ReadBits(4);

        var lenDecoder = BuildHuffmanDecoder(lenLens);
        var distDecoder = BuildHuffmanDecoder(distLens);

        int flagBytesLen = (int)br.ReadVarUInt();
        var flagBytes = br.ReadBytes(flagBytesLen);
        var flagBr = new BitReader(flagBytes);
        var flags = new bool[tokenCount];
        for (int i = 0; i < tokenCount; i++) flags[i] = flagBr.ReadBits(1) == 1;

        // Decode matches in order
        var matches = new (int len, int dist)[matchCount];
        for (int i = 0; i < matchCount; i++)
        {
            int lc = DecodeSymbol(br, lenDecoder);
            int len = LengthTable[lc].lenBase;
            if (LengthTable[lc].extraBits > 0) len += (int)br.ReadBits(LengthTable[lc].extraBits);
            int dc = DecodeSymbol(br, distDecoder);
            int dist = DistanceTable[dc].distBase;
            if (DistanceTable[dc].extraBits > 0) dist += (int)br.ReadBits(DistanceTable[dc].extraBits);
            matches[i] = (len, dist);
        }

        // Reconstruct
        var output = new byte[origLen];
        int outPos = 0, litIdx = 0, matchIdx = 0;
        for (int t = 0; t < tokenCount; t++)
        {
            if (flags[t])
            {
                var m = matches[matchIdx++];
                if (m.dist < 1 || m.dist > outPos)
                    throw new InvalidDataException($"Bad distance {m.dist} at pos {outPos}");
                if (outPos + m.len > origLen)
                    throw new InvalidDataException($"Match overruns at pos {outPos}");
                for (int k = 0; k < m.len; k++) output[outPos + k] = output[outPos - m.dist + k];
                outPos += m.len;
            }
            else
            {
                if (litIdx >= literals.Length) throw new InvalidDataException("Literal underrun");
                output[outPos++] = literals[litIdx++];
            }
        }
        if (outPos != origLen) throw new InvalidDataException($"Decoded {outPos} != {origLen}");
        return output;
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    // Reuse V2's tokenization and Huffman code construction logic.
    // Inlined here so V3 doesn't depend on V2 internals.

    private static List<Token> TokenizeGreedy(byte[] input)
    {
        var result = new List<Token>(input.Length / 4);
        var head = new int[HASH_SIZE];
        var prev = new int[WINDOW_SIZE];
        Array.Fill(head, -1);
        Array.Fill(prev, -1);

        int n = input.Length;
        int pos = 0;
        while (pos < n)
        {
            int matchLen = 0, matchDist = 0;
            if (pos + MIN_MATCH <= n)
                FindLongestMatch(input, pos, head, prev, out matchLen, out matchDist);

            if (matchLen >= MIN_MATCH)
            {
                result.Add(new Token { IsMatch = true, Length = matchLen, Distance = matchDist });
                for (int k = 0; k < matchLen; k++)
                    if (pos + k + MIN_MATCH <= n) InsertHash(input, pos + k, head, prev);
                pos += matchLen;
            }
            else
            {
                result.Add(new Token { IsMatch = false, Literal = input[pos] });
                if (pos + MIN_MATCH <= n) InsertHash(input, pos, head, prev);
                pos++;
            }
        }
        return result;
    }

    private static void FindLongestMatch(byte[] input, int pos, int[] head, int[] prev,
                                         out int bestLen, out int bestDist)
    {
        bestLen = 0;
        bestDist = 0;
        int hash = ComputeHash(input, pos);
        int cur = head[hash];
        int chainCount = 0;
        int maxLen = Math.Min(MAX_MATCH, input.Length - pos);
        while (cur >= 0 && cur >= pos - WINDOW_SIZE && chainCount < MAX_CHAIN)
        {
            if (input[cur] == input[pos] &&
                input[cur + 1] == input[pos + 1] &&
                input[cur + 2] == input[pos + 2] &&
                input[cur + 3] == input[pos + 3])
            {
                int len = 4;
                while (len < maxLen && input[cur + len] == input[pos + len]) len++;
                if (len > bestLen) { bestLen = len; bestDist = pos - cur; if (len >= maxLen) break; }
            }
            cur = prev[cur % WINDOW_SIZE];
            chainCount++;
        }
    }

    private static void InsertHash(byte[] input, int pos, int[] head, int[] prev)
    {
        int hash = ComputeHash(input, pos);
        prev[pos % WINDOW_SIZE] = head[hash];
        head[hash] = pos;
    }

    private static int ComputeHash(byte[] input, int pos)
    {
        uint h = 2166136261u;
        h = (h ^ input[pos]) * 16777619u;
        h = (h ^ input[pos + 1]) * 16777619u;
        h = (h ^ input[pos + 2]) * 16777619u;
        h = (h ^ input[pos + 3]) * 16777619u;
        return (int)(h & HASH_MASK);
    }

    private static int[] BuildHuffmanLengths(long[] counts, int maxLen)
    {
        int n = counts.Length;
        var result = new int[n];
        long total = 0;
        for (int i = 0; i < n; i++) total += counts[i];
        if (total == 0) return result;
        int distinct = 0;
        for (int i = 0; i < n; i++) if (counts[i] > 0) distinct++;
        if (distinct == 1) { for (int i = 0; i < n; i++) if (counts[i] > 0) { result[i] = 1; break; } return result; }

        var nodes = new List<(long w, List<int> leaves)>();
        for (int i = 0; i < n; i++) if (counts[i] > 0) nodes.Add((counts[i], new List<int> { i }));
        while (nodes.Count > 1)
        {
            nodes.Sort((a, b) => a.w.CompareTo(b.w));
            var a = nodes[0]; var b = nodes[1];
            nodes.RemoveAt(1); nodes.RemoveAt(0);
            foreach (var leaf in a.leaves) result[leaf]++;
            foreach (var leaf in b.leaves) result[leaf]++;
            var combined = new List<int>(a.leaves.Count + b.leaves.Count);
            combined.AddRange(a.leaves); combined.AddRange(b.leaves);
            nodes.Add((a.w + b.w, combined));
        }
        int maxFound = 0;
        for (int i = 0; i < n; i++) if (result[i] > maxFound) maxFound = result[i];
        if (maxFound > maxLen)
        {
            for (int i = 0; i < n; i++) if (result[i] > maxLen) result[i] = maxLen;
            double kraft = 0;
            for (int i = 0; i < n; i++) if (result[i] > 0) kraft += Math.Pow(2, -result[i]);
            int safety = 1000;
            while (kraft > 1.0 + 1e-9 && safety-- > 0)
            {
                int minLen = int.MaxValue, minIdx = -1;
                for (int i = 0; i < n; i++) if (result[i] > 0 && result[i] < minLen) { minLen = result[i]; minIdx = i; }
                if (minIdx == -1) break;
                kraft -= Math.Pow(2, -result[minIdx]);
                result[minIdx]++;
                if (result[minIdx] > maxLen) result[minIdx] = maxLen;
                kraft += Math.Pow(2, -result[minIdx]);
            }
        }
        return result;
    }

    private static (ulong code, int length)[] BuildCanonicalCodes(int[] lengths)
    {
        int n = lengths.Length;
        var codes = new (ulong, int)[n];
        int maxLen = 0;
        for (int i = 0; i < n; i++) if (lengths[i] > maxLen) maxLen = lengths[i];
        if (maxLen == 0) return codes;
        var blCount = new int[maxLen + 1];
        for (int i = 0; i < n; i++) blCount[lengths[i]]++;
        var nextCode = new ulong[maxLen + 2];
        ulong code = 0;
        blCount[0] = 0;
        for (int bits = 1; bits <= maxLen; bits++)
        {
            code = (code + (ulong)blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }
        for (int sym = 0; sym < n; sym++)
        {
            int len = lengths[sym];
            if (len > 0) { codes[sym] = (nextCode[len], len); nextCode[len]++; }
        }
        return codes;
    }

    private sealed class HuffmanDecoder
    {
        public int MaxLen;
        public ulong[] FirstCode = null!;
        public int[] FirstSymIdx = null!;
        public int[] Symbols = null!;
    }

    private static HuffmanDecoder BuildHuffmanDecoder(int[] lengths)
    {
        var dec = new HuffmanDecoder();
        int n = lengths.Length;
        int maxLen = 0;
        for (int i = 0; i < n; i++) if (lengths[i] > maxLen) maxLen = lengths[i];
        dec.MaxLen = maxLen;
        dec.FirstCode = new ulong[maxLen + 2];
        dec.FirstSymIdx = new int[maxLen + 2];
        if (maxLen == 0) { dec.Symbols = Array.Empty<int>(); return dec; }
        var blCount = new int[maxLen + 1];
        for (int i = 0; i < n; i++) blCount[lengths[i]]++;
        ulong code = 0;
        blCount[0] = 0;
        for (int bits = 1; bits <= maxLen; bits++)
        {
            code = (code + (ulong)blCount[bits - 1]) << 1;
            dec.FirstCode[bits] = code;
        }
        var symList = new List<int>();
        for (int len = 1; len <= maxLen; len++)
        {
            dec.FirstSymIdx[len] = symList.Count;
            for (int sym = 0; sym < n; sym++) if (lengths[sym] == len) symList.Add(sym);
        }
        dec.Symbols = symList.ToArray();
        return dec;
    }

    private static int DecodeSymbol(BitReader br, HuffmanDecoder dec)
    {
        ulong code = 0;
        for (int bits = 1; bits <= dec.MaxLen; bits++)
        {
            code = (code << 1) | (uint)br.ReadBits(1);
            int nextStart = (bits + 1 <= dec.MaxLen) ? dec.FirstSymIdx[bits + 1] : dec.Symbols.Length;
            int symsAtLen = nextStart - dec.FirstSymIdx[bits];
            if (symsAtLen > 0)
            {
                ulong firstAtLen = dec.FirstCode[bits];
                if (code >= firstAtLen && code < firstAtLen + (ulong)symsAtLen)
                {
                    int offset = (int)(code - firstAtLen);
                    return dec.Symbols[dec.FirstSymIdx[bits] + offset];
                }
            }
        }
        throw new InvalidDataException("Huffman decode failed");
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

    /// <summary>V16.18 TIME-OPT: detect near-Shannon byte input where LZ77
    /// tokenization can't find matches. Threshold 7.95 bits/byte.</summary>
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
