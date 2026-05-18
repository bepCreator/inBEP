// =============================================================================
// ArithmeticContextBep — Idea 4: predictive context modeling + arithmetic coding.
//
// MECHANIC:
//   1. Order-2 context model: predict next byte's distribution given previous
//      2 bytes. Maintain a per-context frequency table that adapts as we
//      consume input.
//   2. Range coder (arithmetic coding's integer cousin) encodes each byte
//      using its predicted probability under the current context.
//   3. Decoder maintains identical model state, decodes byte by byte.
//
// WHY THIS HELPS:
//   - On byte-correlated data (Sensor — consecutive floats are similar):
//     model predicts next byte well, encoded cost approaches conditional
//     entropy H(byte | prev2) which is much less than H(byte) alone.
//   - On uniform random: contexts predict nothing better than uniform,
//     encoded cost = log2(256) = 8 bits/byte exactly. Matches raw.
//
// EXPECTED WINS (relative to raw):
//   - Sensor:        ~10-15% (closes part of the 7-pt gap to Brotli)
//   - Text-Like:     marginal (uniform random ASCII has flat distribution)
//   - Sequential:    significant (high-bytes are highly predictable)
//   - Sparse/etc:    smaller incremental wins on top of byte-locality
//
// FORMAT:
//   [MAGIC "ARITHCTX"]    8 bytes
//   [VERSION = 1]         8 bits
//   [orderHint]           4 bits   (model order, here 2)
//   [inputLen]            32 bits  (byte count)
//   [first 2 bytes raw]   16 bits  (bootstrap context)
//   [arithmetic-coded body]  variable
//
// IMPLEMENTATION NOTES:
//   - Range coder uses 32-bit registers with 24-bit precision per step.
//   - Frequency tables use a small constant initial count to avoid zero
//     probabilities (Laplace smoothing). When a context-specific table
//     has few observations, we fall back to order-1 then order-0 estimates.
//   - This is a teaching-grade implementation: correct, but not as tight
//     as production codecs (which use ANS, careful escape sequences, etc.)
// =============================================================================

namespace InBep.Core;

public static class ArithmeticContextBep
{
    private static readonly byte[] MAGIC = System.Text.Encoding.ASCII.GetBytes("ARITHCTX");
    private const byte VERSION = 1;
    private const ushort MAGIC_ID = VariantMagicIds.ArithmeticContextBep;
    private const int CONTEXT_ORDER = 2;
    private const int CONTEXTS = 256 * 256;          // order-2 context space

    public static byte[] Encode(byte[] input)
    {
        // V16.18 TIME-OPT: near-Shannon early-bail. Order-2 byte context coder
        // on near-uniform input fills 65,536 contexts but produces no gain;
        // self-verify roundtrip wastes more time. Short-circuit to raw.
        if (input != null)
        {
            bool gateFired = LooksNearShannon(input);
            PickerDiagnostics.RecordNearShannonGate("ArithmeticContextBep", input.Length, gateFired);
            if (gateFired)
            {
                var rawShort = new byte[input.Length + 1];
                rawShort[0] = FLAG_RAW;
                Buffer.BlockCopy(input, 0, rawShort, 1, input.Length);
                return rawShort;
            }
        }

        // Build the arithmetic-coded version
        var arithmeticBytes = EncodeArithmetic(input);

        // SELF-VERIFICATION: round-trip the arithmetic version. If it fails,
        // fall back to a wrapped raw payload. Arithmetic coders are notoriously
        // bug-prone; this defensive check ensures the codec always round-trips
        // even if the implementation has edge-case errors.
        try
        {
            var roundTrip = DecodeArithmetic(arithmeticBytes);
            if (roundTrip != null && ByteEquals(roundTrip, input))
            {
                // Verified — emit with arithmetic flag
                var result = new byte[arithmeticBytes.Length + 1];
                result[0] = FLAG_ARITHMETIC;
                Buffer.BlockCopy(arithmeticBytes, 0, result, 1, arithmeticBytes.Length);
                return result;
            }
        }
        catch { }

        // Fallback: raw with flag
        var raw = new byte[input.Length + 1];
        raw[0] = FLAG_RAW;
        Buffer.BlockCopy(input, 0, raw, 1, input.Length);
        return raw;
    }

    public static byte[] Decode(byte[] encoded)
    {
        if (encoded == null || encoded.Length == 0)
            throw new InvalidDataException("Empty input");
        byte flag = encoded[0];
        var body = new byte[encoded.Length - 1];
        Buffer.BlockCopy(encoded, 1, body, 0, body.Length);
        return flag switch
        {
            FLAG_RAW => body,
            FLAG_ARITHMETIC => DecodeArithmetic(body) ?? throw new InvalidDataException("Decode returned null"),
            _ => throw new InvalidDataException($"Unknown flag 0x{flag:X2}"),
        };
    }

    private const byte FLAG_RAW = 0x00;
    private const byte FLAG_ARITHMETIC = 0x01;

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
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

    private static byte[] EncodeArithmetic(byte[] input)
    {
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);
        bw.WriteBits(CONTEXT_ORDER, 4);
        bw.WriteBits((ulong)input.Length, 32);

        if (input.Length < 3)
        {
            // Too small for context model; emit raw
            bw.WriteBits(0UL, 1);  // raw flag
            for (int i = 0; i < input.Length; i++) bw.WriteBits(input[i], 8);
            return bw.ToArray();
        }
        bw.WriteBits(1UL, 1);  // arithmetic flag

        // Bootstrap: first 2 bytes raw
        bw.WriteBits(input[0], 8);
        bw.WriteBits(input[1], 8);

        // Build the model & encode the rest
        var model = new ContextModel();
        var coder = new RangeCoderEncoder(bw);

        for (int i = 2; i < input.Length; i++)
        {
            int ctx = (input[i - 2] << 8) | input[i - 1];
            byte sym = input[i];
            int[] cum = model.GetCumulative(ctx);
            int total = cum[256];
            int low = cum[sym];
            int high = cum[sym + 1];
            coder.Encode((uint)low, (uint)high, (uint)total);
            model.Observe(ctx, sym);
        }
        coder.Finish();
        return bw.ToArray();
    }

    private static byte[]? DecodeArithmetic(byte[] encoded)
    {
        var br = new BitReader(encoded);
        try { br.ReadMagic16(MAGIC_ID, "ARITHCTX"); } catch { return null; }
        int version = (int)br.ReadBits(8);
        if (version != VERSION) return null;
        int order = (int)br.ReadBits(4);
        if (order != CONTEXT_ORDER) return null;
        int len = (int)br.ReadBits(32);
        bool useArithmetic = br.ReadBits(1) == 1;

        var output = new byte[len];

        if (!useArithmetic)
        {
            for (int i = 0; i < len; i++) output[i] = (byte)br.ReadBits(8);
            return output;
        }

        if (len < 3)
        {
            for (int i = 0; i < len; i++) output[i] = (byte)br.ReadBits(8);
            return output;
        }

        output[0] = (byte)br.ReadBits(8);
        output[1] = (byte)br.ReadBits(8);

        var model = new ContextModel();
        var coder = new RangeCoderDecoder(br);
        coder.Init();

        for (int i = 2; i < len; i++)
        {
            int ctx = (output[i - 2] << 8) | output[i - 1];
            int[] cum = model.GetCumulative(ctx);
            int total = cum[256];
            uint target = coder.GetTarget((uint)total);
            // V16.7.4 SPEED OPT: binary search instead of O(256) linear scan.
            // cum[] is monotonically non-decreasing in [0, total]. Find largest
            // s such that cum[s] <= target. With 256 symbols this is at most
            // 8 comparisons vs 256 — ~32× faster on the decode hot path.
            int lo = 0, hi = 256;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if ((uint)cum[mid + 1] <= target) lo = mid + 1;
                else hi = mid;
            }
            int sym = lo;
            coder.Decode((uint)cum[sym], (uint)cum[sym + 1], (uint)total);
            output[i] = (byte)sym;
            model.Observe(ctx, (byte)sym);
        }

        return output;
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    // ============================================================
    // CONTEXT MODEL — order-2 with Laplace smoothing and lazy init
    // ============================================================

    private sealed class ContextModel
    {
        // V10 SPEED OPT: was Dictionary<int, int[]> _ctx with up to 65536 keys
        // (256² = byte-pair contexts). Direct array is faster — no hashing,
        // no allocation per insert. Memory cost: 512KB of references max,
        // trivial. Runtime win typically 2-3x on the inner loop.
        private const int NUM_CTX2 = 1 << 16;  // 65536 order-2 byte contexts
        private readonly int[]?[] _ctx = new int[NUM_CTX2][];
        // Order-1 fallback (just previous byte)
        private readonly int[][] _ctx1 = new int[256][];
        // Order-0 fallback (no context)
        private readonly int[] _ctx0;

        public ContextModel()
        {
            _ctx0 = new int[257];
            for (int s = 0; s < 256; s++) _ctx0[s] = 1;  // initial uniform prior
            _ctx0[256] = 256;
        }

        /// <summary>Returns cumulative table [c0, c1, ..., c256] where the
        /// probability of symbol s is (cum[s+1] - cum[s]) / cum[256]. Falls
        /// back to lower-order contexts when high-order has too few obs.</summary>
        // V16.7.4 SPEED OPT: previously allocated a fresh int[257] per call
        // (2.9M allocations on a 2.9MB sc encode). Now we reuse a scratch
        // buffer — caller uses the result before next call.
        private readonly int[] _cumScratch = new int[257];

        public int[] GetCumulative(int ctx)
        {
            int[]? table = _ctx[ctx];
            if (table != null && table[256] >= 8)
            {
                return BuildCumulativeInto(table, _cumScratch);
            }
            int prevByte = ctx & 0xFF;
            if (_ctx1[prevByte] != null && _ctx1[prevByte]![256] >= 8)
            {
                return BuildCumulativeInto(_ctx1[prevByte]!, _cumScratch);
            }
            return BuildCumulativeInto(_ctx0, _cumScratch);
        }

        public void Observe(int ctx, byte sym)
        {
            // Order-2
            int[]? t2 = _ctx[ctx];
            if (t2 == null)
            {
                t2 = new int[257];
                for (int s = 0; s < 256; s++) t2[s] = 1;
                t2[256] = 256;
                _ctx[ctx] = t2;
            }
            t2[sym]++;
            t2[256]++;
            // Order-1
            int prevByte = ctx & 0xFF;
            if (_ctx1[prevByte] == null)
            {
                _ctx1[prevByte] = new int[257];
                for (int s = 0; s < 256; s++) _ctx1[prevByte]![s] = 1;
                _ctx1[prevByte]![256] = 256;
            }
            _ctx1[prevByte]![sym]++;
            _ctx1[prevByte]![256]++;
            // Order-0
            _ctx0[sym]++;
            _ctx0[256]++;

            // Renormalize if any single counter gets too big
            const int MAX_COUNT = 1 << 14;
            if (t2[256] >= MAX_COUNT) RenormCounts(t2);
            if (_ctx1[prevByte]![256] >= MAX_COUNT) RenormCounts(_ctx1[prevByte]!);
            if (_ctx0[256] >= MAX_COUNT) RenormCounts(_ctx0);
        }

        private static void RenormCounts(int[] t)
        {
            int total = 0;
            for (int s = 0; s < 256; s++)
            {
                t[s] = Math.Max(1, t[s] >> 1);
                total += t[s];
            }
            t[256] = total;
        }

        private static int[] BuildCumulativeInto(int[] freq, int[] dest)
        {
            dest[0] = 0;
            for (int s = 0; s < 256; s++) dest[s + 1] = dest[s] + freq[s];
            return dest;
        }
    }

    // ============================================================
    // RANGE CODER — 32-bit precision encoder/decoder
    // ============================================================

    private sealed class RangeCoderEncoder
    {
        private readonly BitWriter _bw;
        private uint _low = 0;
        private uint _range = 0xFFFFFFFF;
        private int _pending = 0;

        public RangeCoderEncoder(BitWriter bw) { _bw = bw; }

        public void Encode(uint low, uint high, uint total)
        {
            // V12 BUGFIX: protect against total > range. With renormalization
            // the range stays >= 0x01000000 and total is capped at MAX_COUNT
            // (1<<14), so the divisor stays sane — but we add a defensive check
            // because the silent self-verify failure on reymont/sao traced to
            // edge cases here.
            if (total == 0 || _range < total) total = 1; // never legitimately 0; defensive
            uint r = _range / total;
            _low += low * r;
            _range = (high - low) * r;
            // Renormalize — V12 BUGFIX: avoid overflow in (_low + _range) > 2^32
            // by testing the high half directly. The original test
            // `_low + _range - 1 < 0x80000000U` overflowed silently when
            // _low + _range crossed 2^32, causing decoder desync on rare
            // byte-distribution edge cases (reymont/sao with arithmetic and
            // delta context coders). New form computes the boundary without
            // overflow.
            while (true)
            {
                // === V13 RUNAWAY-LOOP FIX ===
                // _range == 2^31 entering this iteration causes _range <<= 1
                // to wrap to 0 in 32-bit arithmetic. With _range = 0 the
                // first condition fires forever (any _low <= 2^31 satisfies
                // _low + 0 <= 2^31), and OutBit pumps bits into the BitWriter's
                // List<byte> until List growth exceeds Array.MaxLength,
                // throwing "Array dimensions exceeded supported range".
                // Pattern observed on ooffice/reymont/sao/osdb/samba/mozilla.
                // Decrement by 1 to dodge the trap — costs 1 LSB of precision
                // (~0 compression). Decoder applies the same fix in lockstep.
                if (_range == 0x80000000U) _range = 0x7FFFFFFFU;

                // Check if [_low, _low + _range) lies entirely in the lower half [0, 2^31)
                // i.e., _low + _range <= 2^31 (treating as 64-bit to avoid overflow)
                if ((ulong)_low + _range <= 0x80000000UL)
                {
                    OutBit(0);
                }
                else if (_low >= 0x80000000U)
                {
                    OutBit(1);
                    _low -= 0x80000000U;
                }
                else if (_low >= 0x40000000U && (ulong)_low + _range <= 0xC0000000UL)
                {
                    _pending++;
                    _low -= 0x40000000U;
                }
                else break;
                _low <<= 1;
                _range <<= 1;
            }
        }

        public void Finish()
        {
            _pending++;
            if (_low < 0x40000000U) OutBit(0);
            else OutBit(1);
            // Emit remaining padding bits (decoder reads up to 33 bits past data)
            for (int i = 0; i < 32; i++) _bw.WriteBits(0UL, 1);
        }

        private void OutBit(int bit)
        {
            _bw.WriteBits(bit == 1 ? 1UL : 0UL, 1);
            for (int i = 0; i < _pending; i++)
                _bw.WriteBits(bit == 1 ? 0UL : 1UL, 1);
            _pending = 0;
        }
    }

    private sealed class RangeCoderDecoder
    {
        private readonly BitReader _br;
        private uint _low = 0;
        private uint _range = 0xFFFFFFFF;
        private uint _code = 0;

        public RangeCoderDecoder(BitReader br) { _br = br; }

        public void Init()
        {
            for (int i = 0; i < 32; i++)
                _code = (_code << 1) | (uint)_br.ReadBits(1);
        }

        public uint GetTarget(uint total)
        {
            // V12 BUGFIX: defensive total-check matches encoder
            if (total == 0 || _range < total) total = 1;
            uint r = _range / total;
            return (_code - _low) / r;
        }

        public void Decode(uint low, uint high, uint total)
        {
            // V12 BUGFIX: defensive total-check matches encoder
            if (total == 0 || _range < total) total = 1;
            uint r = _range / total;
            _low += low * r;
            _range = (high - low) * r;
            // V12 BUGFIX: same overflow-safe boundary checks as encoder
            while (true)
            {
                // V13 runaway-loop fix; mirrors encoder. See encoder for full comment.
                if (_range == 0x80000000U) _range = 0x7FFFFFFFU;

                if ((ulong)_low + _range <= 0x80000000UL) { /* shift in 0 */ }
                else if (_low >= 0x80000000U) { _code -= 0x80000000U; _low -= 0x80000000U; }
                else if (_low >= 0x40000000U && (ulong)_low + _range <= 0xC0000000UL)
                { _code -= 0x40000000U; _low -= 0x40000000U; }
                else break;
                _low <<= 1;
                _range <<= 1;
                _code = (_code << 1) | (uint)_br.ReadBits(1);
            }
        }
    }
}
