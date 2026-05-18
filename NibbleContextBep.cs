// =============================================================================
// NibbleContextBep — Drop 7.1 Idea 4: nibble-level arithmetic coding.
//
// MECHANIC:
//   1. Split each input byte into high nibble (4 bits) and low nibble (4 bits).
//   2. Run an order-2 context arithmetic coder on the nibble stream.
//      Context: previous 2 nibbles (8 bits of context = 256 contexts max,
//      vs 65,536 contexts for the byte-level version).
//   3. Output: arithmetic-coded nibble stream.
//
// WHY THIS HELPS:
//   - Smaller alphabet (16 symbols vs 256) → smaller context table → faster
//     convergence on small chunks. Where ArithmeticContextBep needs 16KB+ to
//     build accurate predictions, NibbleContext converges in 1-2KB.
//   - Catches sub-byte structure that byte-level coding misses (hex strings,
//     packed numeric data, anywhere bytes have nibble-level meaning).
//   - At 4KB chunk size (Adaptive's chunked mode), this is the arithmetic
//     coder that works WITH the chunker instead of against it.
//
// EXPECTED WINS (vs raw):
//   - Mostly 0xFF: should match or beat byte-level (low entropy → both work)
//   - Long Runs: should match or beat (same)
//   - Sequential Ints: nibble structure of integers might catch more
//   - Random/Sensor/Sparse: similar to byte-level
//   - Per-chunk gains: REAL — this is the key benefit. Adaptive's chunked
//     picker should select this on more chunks than byte-level ArithCtx.
//
// FORMAT:
//   [MAGIC "NIBLCTX_"]    8 bytes
//   [VERSION = 1]         8 bits
//   [flag]                1 bit  (0=raw fallback, 1=arithmetic)
//   if arithmetic:
//     [inputLen]          32 bits
//     [first byte raw]    8 bits  (bootstrap: first 2 nibbles)
//     [arithmetic body]   variable
//   if raw:
//     [inputLen]          32 bits
//     [raw bytes]
//
// SELF-VERIFY: encoder decodes its own output before emitting; falls back
// to raw if round-trip fails. Same pattern as ArithmeticContextBep.
// =============================================================================

namespace InBep.Core;

public static class NibbleContextBep
{
    private static readonly byte[] MAGIC = System.Text.Encoding.ASCII.GetBytes("NIBLCTX_");
    private const byte VERSION = 1;
    private const ushort MAGIC_ID = VariantMagicIds.NibbleContextBep;

    public static byte[] Encode(byte[] input)
    {
        // V16.18 TIME-OPT: near-Shannon early-bail.
        if (input != null)
        {
            bool gateFired = LooksNearShannon(input);
            PickerDiagnostics.RecordNearShannonGate("NibbleContextBep", input.Length, gateFired);
            if (gateFired) return BuildRawFallback(input);
        }

        var arithBytes = EncodeArithmetic(input);

        // Self-verify
        try
        {
            var roundTrip = DecodeArithmetic(arithBytes);
            if (roundTrip != null && roundTrip.Length == input.Length)
            {
                bool match = true;
                for (int i = 0; i < input.Length; i++)
                    if (roundTrip[i] != input[i]) { match = false; break; }
                if (match) return arithBytes;
            }
        }
        catch { }

        // Fallback to raw
        return BuildRawFallback(input);
    }

    public static byte[] Decode(byte[] encoded)
    {
        if (encoded == null || encoded.Length < 4)
            throw new InvalidDataException("Encoded too short");
        // Try as arithmetic first; if magic doesn't match raw, throw.
        var br = new BitReader(encoded);
        br.ReadMagic16(MAGIC_ID, "NIBLCTX_");
        int version = (int)br.ReadBits(8);
        if (version != VERSION) throw new InvalidDataException($"Version {version}");
        bool useArith = br.ReadBits(1) == 1;
        int len = (int)br.ReadBits(32);

        if (!useArith)
        {
            var raw = new byte[len];
            for (int i = 0; i < len; i++) raw[i] = (byte)br.ReadBits(8);
            return raw;
        }

        return DecodeArithmeticBody(br, len);
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    // ============================================================
    // ARITHMETIC ENCODING (nibble-level)
    // ============================================================

    private static byte[] EncodeArithmetic(byte[] input)
    {
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);

        if (input.Length < 2)
        {
            // Tiny input — emit raw
            bw.WriteBits(0UL, 1);  // flag: raw
            bw.WriteBits((ulong)input.Length, 32);
            for (int i = 0; i < input.Length; i++) bw.WriteBits(input[i], 8);
            return bw.ToArray();
        }

        bw.WriteBits(1UL, 1);  // flag: arithmetic
        bw.WriteBits((ulong)input.Length, 32);

        // Bootstrap: first byte raw (provides 2 nibbles of initial context)
        bw.WriteBits(input[0], 8);

        // Build nibble stream: nibbles[2*i] = high, nibbles[2*i+1] = low
        // We've already emitted nibbles 0 and 1 (as input[0]). So the model
        // starts predicting nibble 2 (= high nibble of input[1]).

        var model = new NibbleContextModel();
        var coder = new RangeCoderEncoder(bw);

        // Initialize context with the first 2 nibbles (from input[0])
        int ctx = ((input[0] >> 4) << 4) | (input[0] & 0x0F);
        // ctx is now 8 bits: high nibble in upper 4, low in lower 4

        // V17.1: optional capture of the (context, observed-nibble) stream
        // for offline static-prior training. No-op when env var not set.
        NibbleStreamCapture.BeginStream(new ReadOnlySpan<byte>(input, 0, 1));

        for (int i = 1; i < input.Length; i++)
        {
            int hiNibble = (input[i] >> 4) & 0x0F;
            int loNibble = input[i] & 0x0F;

            // Encode hi nibble using current context
            int[] cum = model.GetCumulative(ctx);
            int total = cum[16];
            coder.Encode((uint)cum[hiNibble], (uint)cum[hiNibble + 1], (uint)total);
            model.Observe(ctx, hiNibble);
            NibbleStreamCapture.EmitNibble(hiNibble);

            // Update context: shift in hi nibble
            ctx = ((ctx << 4) & 0xF0) | hiNibble;

            // Encode lo nibble using updated context
            cum = model.GetCumulative(ctx);
            total = cum[16];
            coder.Encode((uint)cum[loNibble], (uint)cum[loNibble + 1], (uint)total);
            model.Observe(ctx, loNibble);
            NibbleStreamCapture.EmitNibble(loNibble);

            // Update context: shift in lo nibble
            ctx = ((ctx << 4) & 0xF0) | loNibble;
        }

        NibbleStreamCapture.EndStream();
        coder.Finish();
        return bw.ToArray();
    }

    private static byte[]? DecodeArithmetic(byte[] encoded)
    {
        try
        {
            var br = new BitReader(encoded);
            try { br.ReadMagic16(MAGIC_ID, "NIBLCTX_"); } catch { return null; }
            int version = (int)br.ReadBits(8);
            if (version != VERSION) return null;
            bool useArith = br.ReadBits(1) == 1;
            int len = (int)br.ReadBits(32);

            if (!useArith)
            {
                var raw = new byte[len];
                for (int i = 0; i < len; i++) raw[i] = (byte)br.ReadBits(8);
                return raw;
            }

            return DecodeArithmeticBody(br, len);
        }
        catch { return null; }
    }

    private static byte[] DecodeArithmeticBody(BitReader br, int len)
    {
        var output = new byte[len];
        if (len == 0) return output;

        output[0] = (byte)br.ReadBits(8);
        if (len == 1) return output;

        var model = new NibbleContextModel();
        var coder = new RangeCoderDecoder(br);
        coder.Init();

        int ctx = ((output[0] >> 4) << 4) | (output[0] & 0x0F);

        for (int i = 1; i < len; i++)
        {
            int[] cum = model.GetCumulative(ctx);
            int total = cum[16];
            uint target = coder.GetTarget((uint)total);
            int hiNibble = 0;
            for (int s = 0; s < 16; s++)
            {
                if (target >= (uint)cum[s] && target < (uint)cum[s + 1])
                {
                    hiNibble = s;
                    break;
                }
            }
            coder.Decode((uint)cum[hiNibble], (uint)cum[hiNibble + 1], (uint)total);
            model.Observe(ctx, hiNibble);
            ctx = ((ctx << 4) & 0xF0) | hiNibble;

            cum = model.GetCumulative(ctx);
            total = cum[16];
            target = coder.GetTarget((uint)total);
            int loNibble = 0;
            for (int s = 0; s < 16; s++)
            {
                if (target >= (uint)cum[s] && target < (uint)cum[s + 1])
                {
                    loNibble = s;
                    break;
                }
            }
            coder.Decode((uint)cum[loNibble], (uint)cum[loNibble + 1], (uint)total);
            model.Observe(ctx, loNibble);
            ctx = ((ctx << 4) & 0xF0) | loNibble;

            output[i] = (byte)((hiNibble << 4) | loNibble);
        }

        return output;
    }

    private static byte[] BuildRawFallback(byte[] input)
    {
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);
        bw.WriteBits(0UL, 1);  // raw
        bw.WriteBits((ulong)input.Length, 32);
        for (int i = 0; i < input.Length; i++) bw.WriteBits(input[i], 8);
        return bw.ToArray();
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

    // ============================================================
    // NIBBLE CONTEXT MODEL
    // ============================================================

    private sealed class NibbleContextModel
    {
        // 256 contexts × 17 entries each (16 nibble symbols + 1 total slot)
        private readonly int[][] _ctx = new int[256][];
        private readonly int[] _ctx0 = new int[17];

        public NibbleContextModel()
        {
            for (int s = 0; s < 16; s++) _ctx0[s] = 1;
            _ctx0[16] = 16;
        }

        // V16.7.4 SPEED OPT: reuse a scratch cumulative buffer instead of
        // allocating fresh int[17] per byte.
        private readonly int[] _cumScratch = new int[17];

        public int[] GetCumulative(int ctx)
        {
            int[]? table = _ctx[ctx];
            if (table != null && table[16] >= 4) return BuildCumulativeInto(table, _cumScratch);
            return BuildCumulativeInto(_ctx0, _cumScratch);
        }

        public void Observe(int ctx, int nibble)
        {
            if (_ctx[ctx] == null)
            {
                _ctx[ctx] = new int[17];
                for (int s = 0; s < 16; s++) _ctx[ctx]![s] = 1;
                _ctx[ctx]![16] = 16;
            }
            _ctx[ctx]![nibble]++;
            _ctx[ctx]![16]++;
            _ctx0[nibble]++;
            _ctx0[16]++;

            const int MAX_COUNT = 1 << 14;
            if (_ctx[ctx]![16] >= MAX_COUNT) RenormCounts(_ctx[ctx]!);
            if (_ctx0[16] >= MAX_COUNT) RenormCounts(_ctx0);
        }

        private static void RenormCounts(int[] t)
        {
            int total = 0;
            for (int s = 0; s < 16; s++)
            {
                t[s] = Math.Max(1, t[s] >> 1);
                total += t[s];
            }
            t[16] = total;
        }

        private static int[] BuildCumulativeInto(int[] freq, int[] dest)
        {
            dest[0] = 0;
            for (int s = 0; s < 16; s++) dest[s + 1] = dest[s] + freq[s];
            return dest;
        }
    }

    // ============================================================
    // RANGE CODER (same as ArithmeticContextBep)
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
            uint r = _range / total;
            _low += low * r;
            _range = (high - low) * r;
            while (true)
            {
                // V13 runaway-loop fix: _range == 2^31 here causes _range <<= 1
                // to wrap to 0, yielding an infinite OutBit loop that explodes
                // the BitWriter. See ArithmeticContextBep encoder for the full
                // explanation. Decoder applies the same fix in lockstep.
                if (_range == 0x80000000U) _range = 0x7FFFFFFFU;

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
            uint r = _range / total;
            return (_code - _low) / r;
        }

        public void Decode(uint low, uint high, uint total)
        {
            uint r = _range / total;
            _low += low * r;
            _range = (high - low) * r;
            while (true)
            {
                // V13 runaway-loop fix; mirrors encoder.
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
