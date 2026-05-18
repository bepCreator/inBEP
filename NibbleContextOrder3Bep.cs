// =============================================================================
// NibbleContextOrder3Bep — V9 bonus: order-3 nibble context model.
//
// MOTIVATION:
//   Order-2 NibbleContextBep was the breakthrough of V8 — it beats Deflate
//   on Sparse, Long Runs, Mostly 0xFF. Order-3 expands the context from 8
//   to 12 bits (4096 contexts vs 256), which captures finer patterns at the
//   cost of slower convergence.
//
//   For enwik9 with ~1GB of structured XML, context tables saturate quickly
//   even at order-3 (we'd need only ~16KB of input to fill 4096 contexts
//   with reasonable observations). So this should be strictly better than
//   order-2 on large structured inputs.
//
// FORMAT: identical to NibbleContextBep but with MAGIC "NIBLCTX3" and a
// 12-bit context in the model (3 nibbles instead of 2).
//
// SELF-VERIFY: encoder verifies round-trip and falls back to raw on failure.
// =============================================================================

namespace InBep.Core;

public static class NibbleContextOrder3Bep
{
    private static readonly byte[] MAGIC = System.Text.Encoding.ASCII.GetBytes("NIBLCTX3");
    // V17.1: VERSION 1 = legacy (model starts uniform, learns online). VERSION 2
    // = static prior K=12 loaded at init from NibblePriorLoader. Encoder emits
    // VERSION_PRIOR when the prior is embedded; falls back to VERSION_LEGACY
    // (and bare-init model) when the prior isn't present in the binary.
    // Decoder accepts all versions — old archives still decode forever.
    //
    // V17.2: VERSION 3 = TEXTCTX prior. Same K=12 format, but trained on the
    // post-pipeline BEP-archive byte distribution (not raw input nibbles).
    // Used when this encoder is invoked as a post-coder from
    // TextPipelineCtxBep — the input bytes there are already entropy-coded
    // BEP archive bytes, so the raw-input prior is wrong for them.
    // Caller selects via the useTextCtxPrior parameter on Encode().
    private const byte VERSION_LEGACY        = 1;
    private const byte VERSION_PRIOR         = 2;
    private const byte VERSION_TEXTCTX_PRIOR = 3;
    private const byte VERSION = VERSION_LEGACY;  // legacy raw-passthrough header still uses 1
    // V17.1 → V17.2 → V17.3: prior weight per context. Empirical tuning curve:
    //   weight=64  : -2.19% on Calgary aggregate (over-weighted, prior fights runtime)
    //   weight=16  : +0.22% on Calgary aggregate (under-weighted on small files)
    //   weight=40  : +50-200B/file on small text (Rich's empirical tune on paper5)
    //
    // V17.3 makes weight size-adaptive. Small files have very few nibbles per
    // context (10K nibbles / 4096 contexts = ~2.5 obs/ctx), so the runtime
    // model never reaches steady state — bigger prior weight wins. Large
    // files saturate every context with hundreds of obs each, so small prior
    // weight + runtime convergence wins.
    //
    // Encoder and decoder MUST agree on the weight; both call this method
    // with the input length (encoder has it directly, decoder reads it from
    // the archive header before constructing the model).
    private static int PriorWeightForInput(int inputLen)
    {
        if (inputLen < 16_000)  return 40;   // paper5/4 territory: prior dominates
        if (inputLen < 100_000) return 24;   // medium text: hybrid
        return 16;                            // book/news territory: runtime takes over
    }
    private const ushort MAGIC_ID = VariantMagicIds.NibbleContextOrder3Bep;
    private const int CONTEXT_BITS = 12;  // 3 nibbles
    private const int CONTEXT_MASK = 0xFFF;
    private const int NUM_CONTEXTS = 1 << CONTEXT_BITS;

    /// <summary>
    /// Standard encode (raw-input mode). Uses the K=12 raw-input prior if
    /// embedded; otherwise legacy bare-init.
    /// </summary>
    public static byte[] Encode(byte[] input) => Encode(input, useTextCtxPrior: false);

    /// <summary>
    /// V17.2: Encode with explicit prior selection. Pass useTextCtxPrior=true
    /// when this encoder is being used as a post-coder over BEP-archive bytes
    /// (i.e., from TextPipelineCtxBep). The textctx K=12 prior is trained on
    /// that distribution and is wildly different from the raw-input prior.
    /// Passing useTextCtxPrior=true on a build without the textctx prior
    /// embedded falls through to VERSION_LEGACY (no prior) — never errors.
    /// </summary>
    public static byte[] Encode(byte[] input, bool useTextCtxPrior)
    {
        // V16.18 TIME-OPT: near-Shannon early-bail. Order-3 context coder
        // on near-uniform input fills 4096 contexts that don't accumulate
        // useful signal — output ≈ input size + framing overhead, plus the
        // self-verify roundtrip wastes more time. Short-circuit to raw.
        if (input != null)
        {
            bool gateFired = LooksNearShannon(input);
            PickerDiagnostics.RecordNearShannonGate("NibbleContextOrder3Bep", input.Length, gateFired);
            if (gateFired) return BuildRawFallback(input);
        }

        var arithBytes = EncodeArithmetic(input, useTextCtxPrior);
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
        return BuildRawFallback(input);
    }

    public static byte[] Decode(byte[] encoded)
    {
        if (encoded == null || encoded.Length < 4)
            throw new InvalidDataException("Encoded too short");
        var br = new BitReader(encoded);
        br.ReadMagic16(MAGIC_ID, "NIBLCTX3");
        int version = (int)br.ReadBits(8);
        // V17.1+V17.2: accept VERSION_LEGACY (1), VERSION_PRIOR (2), and
        // VERSION_TEXTCTX_PRIOR (3). Old archives forever decode with bare-init
        // model; V17.1 archives decode with the embedded raw K=12 prior;
        // V17.2 textctx-flavor archives decode with the textctx K=12 prior.
        if (version != VERSION_LEGACY
            && version != VERSION_PRIOR
            && version != VERSION_TEXTCTX_PRIOR)
            throw new InvalidDataException($"Version {version}");
        bool useArith = br.ReadBits(1) == 1;
        int len = (int)br.ReadBits(32);

        if (!useArith)
        {
            var raw = new byte[len];
            for (int i = 0; i < len; i++) raw[i] = (byte)br.ReadBits(8);
            return raw;
        }

        return DecodeArithmeticBody(br, len, version);
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    private static byte[] EncodeArithmetic(byte[] input, bool useTextCtxPrior)
    {
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);

        // V17.2: pick which prior to embed-reference and which version byte
        // to emit. Selection priority:
        //   useTextCtxPrior=true  → try textctx prior; fall back to legacy
        //   useTextCtxPrior=false → try raw prior;     fall back to legacy
        // Decoder reads the version byte to know which prior to load.
        byte[]? prior;
        byte versionToEmit;
        if (useTextCtxPrior)
        {
            prior = NibblePriorLoader.GetK12TextCtx();
            versionToEmit = (prior != null) ? VERSION_TEXTCTX_PRIOR : VERSION_LEGACY;
        }
        else
        {
            prior = NibblePriorLoader.GetK12();
            versionToEmit = (prior != null) ? VERSION_PRIOR : VERSION_LEGACY;
        }
        bw.WriteBits(versionToEmit, 8);

        if (input.Length < 3)  // Need 2 bytes for bootstrap (4 nibbles → fills order-3 context partially)
        {
            bw.WriteBits(0UL, 1);
            bw.WriteBits((ulong)input.Length, 32);
            for (int i = 0; i < input.Length; i++) bw.WriteBits(input[i], 8);
            return bw.ToArray();
        }

        bw.WriteBits(1UL, 1);
        bw.WriteBits((ulong)input.Length, 32);

        // Bootstrap: first 2 bytes raw (4 nibbles, gives full order-3 starting context)
        bw.WriteBits(input[0], 8);
        bw.WriteBits(input[1], 8);

        // V17.1: pass prior in if available. Constructor handles null gracefully
        // (bare-init = legacy behavior). Encoder/decoder MUST match — both use
        // the prior referenced by versionToEmit, neither if VERSION_LEGACY.
        // V17.3: weight is size-adaptive; decoder will compute the same value
        // from the `len` field already encoded in the archive header.
        int weight = PriorWeightForInput(input.Length);
        var model = (prior != null)
            ? new NibbleOrder3ContextModel(prior, weight)
            : new NibbleOrder3ContextModel();
        var coder = new RangeCoderEncoder(bw);

        // Initial 12-bit context from first 2 bytes (skip the leading nibble; use last 3)
        // input[0]=AB, input[1]=CD → nibbles A,B,C,D → context starts as B,C,D = (B<<8)|(C<<4)|D
        int n0Hi = (input[0] >> 4) & 0xF;
        int n0Lo = input[0] & 0xF;
        int n1Hi = (input[1] >> 4) & 0xF;
        int n1Lo = input[1] & 0xF;
        int ctx = (n0Lo << 8) | (n1Hi << 4) | n1Lo;

        // V17.1+V17.2: optional capture of (context, observed-nibble) stream
        // for offline static-prior training. Two separate capture streams:
        //   raw mode (useTextCtxPrior=false) → NibbleStreamCapture
        //   textctx mode (useTextCtxPrior=true) → TextCtxNibbleStreamCapture
        // Each is wired by its own env var in Program.cs; both are no-ops
        // when the env var is unset.
        if (useTextCtxPrior)
            TextCtxNibbleStreamCapture.BeginStream(new ReadOnlySpan<byte>(input, 0, 2));
        else
            NibbleStreamCapture.BeginStream(new ReadOnlySpan<byte>(input, 0, 2));

        for (int i = 2; i < input.Length; i++)
        {
            int hiNibble = (input[i] >> 4) & 0xF;
            int loNibble = input[i] & 0xF;

            int[] cum = model.GetCumulative(ctx);
            int total = cum[16];
            coder.Encode((uint)cum[hiNibble], (uint)cum[hiNibble + 1], (uint)total);
            model.Observe(ctx, hiNibble);
            if (useTextCtxPrior) TextCtxNibbleStreamCapture.EmitNibble(hiNibble);
            else                 NibbleStreamCapture.EmitNibble(hiNibble);
            ctx = ((ctx << 4) | hiNibble) & CONTEXT_MASK;

            cum = model.GetCumulative(ctx);
            total = cum[16];
            coder.Encode((uint)cum[loNibble], (uint)cum[loNibble + 1], (uint)total);
            model.Observe(ctx, loNibble);
            if (useTextCtxPrior) TextCtxNibbleStreamCapture.EmitNibble(loNibble);
            else                 NibbleStreamCapture.EmitNibble(loNibble);
            ctx = ((ctx << 4) | loNibble) & CONTEXT_MASK;
        }

        if (useTextCtxPrior) TextCtxNibbleStreamCapture.EndStream();
        else                 NibbleStreamCapture.EndStream();
        coder.Finish();
        return bw.ToArray();
    }

    private static byte[]? DecodeArithmetic(byte[] encoded)
    {
        try
        {
            var br = new BitReader(encoded);
            try { br.ReadMagic16(MAGIC_ID, "NIBLCTX3"); } catch { return null; }
            int version = (int)br.ReadBits(8);
            // V17.2: accept legacy / raw prior / textctx prior on self-verify path.
            if (version != VERSION_LEGACY
                && version != VERSION_PRIOR
                && version != VERSION_TEXTCTX_PRIOR) return null;
            bool useArith = br.ReadBits(1) == 1;
            int len = (int)br.ReadBits(32);

            if (!useArith)
            {
                var raw = new byte[len];
                for (int i = 0; i < len; i++) raw[i] = (byte)br.ReadBits(8);
                return raw;
            }
            return DecodeArithmeticBody(br, len, version);
        }
        catch { return null; }
    }

    private static byte[] DecodeArithmeticBody(BitReader br, int len, int version)
    {
        var output = new byte[len];
        if (len == 0) return output;
        if (len < 3)
        {
            for (int i = 0; i < len; i++) output[i] = (byte)br.ReadBits(8);
            return output;
        }
        output[0] = (byte)br.ReadBits(8);
        output[1] = (byte)br.ReadBits(8);

        // V17.1+V17.2: load the embedded prior iff the archive was written
        // with one. VERSION_LEGACY archives decode with bare-init model
        // regardless of whether priors are currently embedded — backward
        // compatibility forever.
        // V17.3: weight is size-adaptive on input length. Encoder used
        // input.Length; decoder computes from the same `len` field that was
        // encoded in the archive header (already read into the `len`
        // parameter of this method). Encoder & decoder must agree exactly.
        int weight = PriorWeightForInput(len);
        NibbleOrder3ContextModel model;
        if (version == VERSION_PRIOR)
        {
            byte[]? prior = NibblePriorLoader.GetK12();
            if (prior == null)
                throw new InvalidDataException(
                    "Archive uses NibCtx3 raw prior (VERSION 2) but the embedded " +
                    "prior table is missing from this build of inBEP. " +
                    "Rebuild the binary with nibble_prior_k12.bin in the project root.");
            model = new NibbleOrder3ContextModel(prior, weight);
        }
        else if (version == VERSION_TEXTCTX_PRIOR)
        {
            byte[]? prior = NibblePriorLoader.GetK12TextCtx();
            if (prior == null)
                throw new InvalidDataException(
                    "Archive uses NibCtx3 textctx prior (VERSION 3) but the " +
                    "embedded textctx_nibble_prior_k12.bin is missing from this " +
                    "build of inBEP. Rebuild the binary with the resource " +
                    "in the project root.");
            model = new NibbleOrder3ContextModel(prior, weight);
        }
        else
        {
            model = new NibbleOrder3ContextModel();
        }
        var coder = new RangeCoderDecoder(br);
        coder.Init();

        int n0Lo = output[0] & 0xF;
        int n1Hi = (output[1] >> 4) & 0xF;
        int n1Lo = output[1] & 0xF;
        int ctx = (n0Lo << 8) | (n1Hi << 4) | n1Lo;

        for (int i = 2; i < len; i++)
        {
            int[] cum = model.GetCumulative(ctx);
            int total = cum[16];
            uint target = coder.GetTarget((uint)total);
            int hiNibble = 0;
            for (int s = 0; s < 16; s++)
            {
                if (target >= (uint)cum[s] && target < (uint)cum[s + 1])
                { hiNibble = s; break; }
            }
            coder.Decode((uint)cum[hiNibble], (uint)cum[hiNibble + 1], (uint)total);
            model.Observe(ctx, hiNibble);
            ctx = ((ctx << 4) | hiNibble) & CONTEXT_MASK;

            cum = model.GetCumulative(ctx);
            total = cum[16];
            target = coder.GetTarget((uint)total);
            int loNibble = 0;
            for (int s = 0; s < 16; s++)
            {
                if (target >= (uint)cum[s] && target < (uint)cum[s + 1])
                { loNibble = s; break; }
            }
            coder.Decode((uint)cum[loNibble], (uint)cum[loNibble + 1], (uint)total);
            model.Observe(ctx, loNibble);
            ctx = ((ctx << 4) | loNibble) & CONTEXT_MASK;

            output[i] = (byte)((hiNibble << 4) | loNibble);
        }
        return output;
    }

    private static byte[] BuildRawFallback(byte[] input)
    {
        var bw = new BitWriter();
        bw.WriteMagic16(MAGIC_ID);
        bw.WriteBits(VERSION, 8);
        bw.WriteBits(0UL, 1);
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
    // Order-3 nibble context model with order-2 fallback
    // ============================================================

    private sealed class NibbleOrder3ContextModel
    {
        // V10 SPEED OPT: was Dictionary<int, int[]> _ctx3 with 4096 possible
        // keys. Direct array indexed by 12-bit context is faster (no hashing,
        // no allocation per context entry) and uses only 32KB of references
        // even if all contexts are populated. Memory cost trivial; runtime win
        // typically 2-3x on the inner loop on large inputs.
        private const int NUM_CTX3 = 1 << 12;  // 4096 order-3 contexts
        private readonly int[]?[] _ctx3 = new int[NUM_CTX3][];
        private readonly int[][] _ctx2 = new int[256][];   // order-2 fallback (lower 8 bits)
        private readonly int[] _ctx0 = new int[17];

        public NibbleOrder3ContextModel()
        {
            for (int s = 0; s < 16; s++) _ctx0[s] = 1;
            _ctx0[16] = 16;
        }

        /// <summary>
        /// V17.1: prior-init constructor. Pre-populates the order-3 context
        /// tables from the trained K=12 prior blob, scaled by priorWeight.
        /// Both encoder and decoder must call this with the same blob and
        /// weight or the round-trip breaks.
        ///
        /// Format of priorBlob: raw uint16 LE, NUM_CTX3 * 16 entries.
        /// prior[ctx*16 + nibble] = P * 65536, row sums to ~65536.
        ///
        /// priorWeight is the target sum of effective observations per context.
        /// 16 = matches existing uniform-init mass; 64 = ~4x stronger; 256 =
        /// dominates for thousands of bytes per context. Pick to balance
        /// small-file warmup help vs big-file adaptation friction.
        /// </summary>
        public NibbleOrder3ContextModel(byte[]? priorBlob, int priorWeight)
        {
            // Always init ctx0 — fallback for never-seen contexts (impossible
            // when prior is loaded but kept for code symmetry).
            for (int s = 0; s < 16; s++) _ctx0[s] = 1;
            _ctx0[16] = 16;

            if (priorBlob == null) return;
            int expectedBytes = NUM_CTX3 * 16 * 2;
            if (priorBlob.Length != expectedBytes)
                throw new System.ArgumentException(
                    $"NibbleOrder3 prior blob is {priorBlob.Length} bytes, " +
                    $"expected {expectedBytes}");
            if (priorWeight < 1 || priorWeight > 16384)
                throw new System.ArgumentOutOfRangeException(nameof(priorWeight),
                    "priorWeight must be in [1, 16384]");

            // Walk the blob, populate _ctx3 cells as int counts proportional
            // to the prior probabilities. Each row sums to ~priorWeight after
            // the conversion (clamped: every cell at least 1 to preserve
            // decode safety on a never-seen-during-training nibble).
            for (int c = 0; c < NUM_CTX3; c++)
            {
                int[] t3 = new int[17];
                int total = 0;
                for (int s = 0; s < 16; s++)
                {
                    int blobIdx = (c * 16 + s) * 2;
                    int p = priorBlob[blobIdx] | (priorBlob[blobIdx + 1] << 8);  // LE u16
                    // count = round(p / 65536 * priorWeight). Use integer math
                    // for determinism: count = (p * priorWeight + 32768) >> 16.
                    int cnt = (p * priorWeight + 32768) >> 16;
                    if (cnt < 1) cnt = 1;
                    t3[s] = cnt;
                    total += cnt;
                }
                t3[16] = total;
                _ctx3[c] = t3;
            }
        }

        // V16.7.4 SPEED OPT: reuse a scratch cumulative buffer instead of
        // allocating fresh int[17] per nibble.
        private readonly int[] _cumScratch = new int[17];

        public int[] GetCumulative(int ctx)
        {
            int[]? t3 = _ctx3[ctx];
            if (t3 != null && t3[16] >= 8) return BuildCumulativeInto(t3, _cumScratch);
            int ctx2 = ctx & 0xFF;
            if (_ctx2[ctx2] != null && _ctx2[ctx2]![16] >= 4)
                return BuildCumulativeInto(_ctx2[ctx2]!, _cumScratch);
            return BuildCumulativeInto(_ctx0, _cumScratch);
        }

        public void Observe(int ctx, int nibble)
        {
            int[]? t3 = _ctx3[ctx];
            if (t3 == null)
            {
                t3 = new int[17];
                for (int s = 0; s < 16; s++) t3[s] = 1;
                t3[16] = 16;
                _ctx3[ctx] = t3;
            }
            t3[nibble]++;
            t3[16]++;

            int ctx2 = ctx & 0xFF;
            if (_ctx2[ctx2] == null)
            {
                _ctx2[ctx2] = new int[17];
                for (int s = 0; s < 16; s++) _ctx2[ctx2]![s] = 1;
                _ctx2[ctx2]![16] = 16;
            }
            _ctx2[ctx2]![nibble]++;
            _ctx2[ctx2]![16]++;

            _ctx0[nibble]++;
            _ctx0[16]++;

            const int MAX_COUNT = 1 << 14;
            if (t3[16] >= MAX_COUNT) Renorm(t3);
            if (_ctx2[ctx2]![16] >= MAX_COUNT) Renorm(_ctx2[ctx2]!);
            if (_ctx0[16] >= MAX_COUNT) Renorm(_ctx0);
        }

        private static void Renorm(int[] t)
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
    // RANGE CODER (same as NibbleContextBep)
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
                // to wrap to 0 in 32-bit arithmetic, leading to an infinite
                // OutBit loop. See ArithmeticContextBep encoder for full notes.
                if (_range == 0x80000000U) _range = 0x7FFFFFFFU;

                if ((ulong)_low + _range <= 0x80000000UL) OutBit(0);
                else if (_low >= 0x80000000U) { OutBit(1); _low -= 0x80000000U; }
                else if (_low >= 0x40000000U && (ulong)_low + _range <= 0xC0000000UL) { _pending++; _low -= 0x40000000U; }
                else break;
                _low <<= 1;
                _range <<= 1;
            }
        }

        public void Finish()
        {
            _pending++;
            if (_low < 0x40000000U) OutBit(0); else OutBit(1);
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

                if ((ulong)_low + _range <= 0x80000000UL) { }
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
