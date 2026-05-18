// =============================================================================
// TextPipelineCtxBep — V17.0 (LZ77 candidates + V17/V18 profile gating)
//
// V17.0 ADDITIONS over V16.21:
//   1. Lz77BepV3 and DictPreLz77V3Bep added as candidate paths inside TextCtx.
//      Previously these were sibling variants only. Adding them here lets
//      the picker select them on heterogeneous large files (mozilla-class)
//      where LZ-style match-finding beats BWT+RePair, without disturbing the
//      wins on coherent text. Each LZ candidate also gets a stride-2 variant.
//      New flag values: POSTCODE_LZ77 = 0x05, POSTCODE_DICTLZ77 = 0x06.
//   2. V17/V18 profile gating. Diagnostic data shows V17 wins 100% on text
//      and V18 wins 100% on binary, perfectly separable by a printable-ASCII
//      fraction probe. The gate skips computing the unlikely-to-win profile,
//      cutting encode time roughly in half on confidently-typed inputs.
//      Conservative: when the probe returns a mid-range value (0.30–0.85),
//      both profiles still run as before. Constant ENABLE_PROFILE_GATE = true
//      can be flipped to false to restore V16.19 always-both behavior.
//
// Wraps BEPPipeline.Compress (V15 transforms DISABLED, to preserve byte-level
// structure that downstream context coders need) with optional post-coding
// passes through NibbleContextBep, NibbleContextOrder3Bep, or
// ArithmeticContextBep. ALSO tries running the same pipeline on a stride-2
// deinterleaved version of the input. Picks the smallest of all candidates.
//
// V16.19 — HYBRID ENTROPY PROFILE
// V16.18 tightened the BEP byte-level archive via RangeCoder/RangeCoderO1,
// apex-rank-zero UnaryBEP shortcuts, and cost-aware RePair. On medium/large
// text this is a clear net win. On small text, it eliminates the byte-level
// slack that the Nibble/Nibble3/Arith wrap layers used to profit from, so
// V17 BEP+wrap > V18 BEP-no-wrap by ~500 bytes per file. Rather than guessing
// a size threshold, V16.19 computes BOTH profiles and feeds candidates from
// each into the picker — which already picks the global minimum. No archive
// format change: BEP body is self-describing via its own header.
//
// V16.1 ADDITIONS over V16:
//   1. NibbleContextOrder3Bep added as a fourth post-coding option (12-bit
//      context, intermediate between Nibble's 8-bit and ArithCtx's 16-bit).
//      Empirically wins or ties on 13/20 medical-TIFF-8bit files in the
//      v15_recursive results.
//   2. Stride-2 byte-position deinterleaving as an optional preprocessor.
//      Splits the input into even/odd byte streams, runs the full pipeline
//      on the concatenation, and picks if smaller. Targets 16-bit medical
//      data where consecutive byte pairs are samples — separating the high-
//      and low-byte streams gives each downstream coder a more coherent
//      distribution to model.
//
// FRAME (1 byte tag prefix):
//   bits 0-1 = post-coding choice
//     00 = no post-coding (vanilla BEP archive)
//     01 = NibbleContextBep
//     10 = NibbleContextOrder3Bep
//     11 = ArithmeticContextBep
//   bit 2 = stride preprocessing applied (0 = raw input, 1 = stride-2 deinterleaved)
//   bit 3 reserved
//   bits 4-7 = sentinel value for FLAG_RAW (0xF0) when no encode helped
//
// SPECIFIC FLAG VALUES:
//   0x00  FLAG_BEP           — vanilla BEP                 (no stride, no post)
//   0x01  FLAG_BEP_NIBBLE    — BEP + Nibble                 (no stride)
//   0x02  FLAG_BEP_NIBBLE3   — BEP + NibbleOrder3           (no stride)
//   0x03  FLAG_BEP_ARITH     — BEP + ArithCtx               (no stride)
//   0x04  FLAG_S2_BEP        — stride-2 + vanilla BEP
//   0x05  FLAG_S2_BEP_NIBBLE — stride-2 + BEP + Nibble
//   0x06  FLAG_S2_BEP_NIBBLE3— stride-2 + BEP + NibbleOrder3
//   0x07  FLAG_S2_BEP_ARITH  — stride-2 + BEP + ArithCtx
//   0xF0  FLAG_RAW           — none of the above beat raw input
//
// V16-archive compatibility: V16 archives use only flags 0x00-0x03, which
// are preserved unchanged here. New flags 0x04-0x07 are V16.1-only. FLAG_RAW
// changed from 0x00 to 0xF0 (0x00 is now BEP-only, which used to be flag 1
// in V16). To preserve V16 archive decode, we'd need a version byte. Instead,
// since FLAG_RAW is structurally distinct (input bytes follow), we use 0xF0
// which is unreachable in V16 (V16 used 0x00-0x03 only). Old V16 archives
// remain decodable: any V16 flag (0x00-0x03) decodes the same way in V16.1.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;
using BEPCompress.Core;

namespace InBep.Core;

public static class TextPipelineCtxBep
{
    // Post-coding bits (bits 0-1)
    private const byte POSTCODE_BEP        = 0x00;
    private const byte POSTCODE_NIBBLE     = 0x01;
    private const byte POSTCODE_NIBBLE3    = 0x02;
    private const byte POSTCODE_ARITH      = 0x03;
    private const byte POSTCODE_MASK       = 0x07;   // V16.21: widened from 0x03

    // Stride bit (bit 2)
    private const byte STRIDE_FLAG         = 0x08;   // V16.21: moved from 0x04
    private const byte POSTCODE_CHAIN      = 0x04;   // V16.21: BepChainTextBep (BWT+MTF+Chain)

    // V17.0 additions: LZ77-family candidates inside TextCtx. Each slot is
    // self-decoding (does not pass through BEPPipeline). Decode routes to
    // Lz77BepV3.Decode / DictPreLz77V3Bep.Decode directly, with a stride-2
    // re-interleave on top if STRIDE_FLAG is set.
    private const byte POSTCODE_LZ77       = 0x05;   // V17.0: Lz77BepV3
    private const byte POSTCODE_DICTLZ77   = 0x06;   // V17.0: DictPreLz77V3Bep
    // 0x07 reserved for future use (POSTCODE_MASK = 0x07 leaves it as the
    // last available postcode slot).

    // V17.0: profile-gating master switch. When true (default), the gate
    // probes the input's byte distribution and skips the V17 or V18 profile
    // when the probe is high-confidence. When false, both profiles always
    // run (V16.19 hybrid behavior).
    private const bool ENABLE_PROFILE_GATE = true;

    // V17.0: printable-ASCII thresholds for profile gating. Above HIGH means
    // "definitely text" → only V17. Below LOW means "definitely binary" →
    // only V18. Between LOW and HIGH the gate stays open and both run.
    private const double PROFILE_GATE_TEXT_THRESHOLD   = 0.85;
    private const double PROFILE_GATE_BINARY_THRESHOLD = 0.30;
    private const int    PROFILE_GATE_SAMPLE_BYTES     = 16384;

    // V19.1: internal candidate gating. These trim the per-encode candidate
    // pool based on cheap input-shape probes BEFORE running expensive
    // candidates that almost certainly won't win. Each is independently
    // toggleable for research.
    //
    // ENABLE_LZ_GATE: skip Lz77BepV3 and DictPreLz77V3Bep candidates inside
    // TextCtx when the input looks like coherent text (high printable-ASCII
    // fraction + small alphabet). The LZ candidates exist for the
    // heterogeneous-binary wedge (mozilla / samba / ooffice); they don't
    // win on text and they cost real time.
    //
    // V19.4: gate kept at V19.1 form (no size-based escape hatch). V19.3
    // experimentally bypassed this gate for inputs >= 100 KB to test whether
    // V19.2's window enlargement (Lz77BepV3 WINDOW_SIZE 32K→512K, MAX_CHAIN
    // 128→4096) would let Lz77BepV3 win on large coherent text. The
    // experiment ran on alice29, news, lcet10, plrabn12 and produced
    // byte-identical compressed output to V19.2 in every case — Lz77BepV3
    // entered the picker 3 times per file and lost every time. The BEP
    // candidate is strictly better on coherent text regardless of LZ77
    // window depth. Cost of the experiment was +5s of encode time per file.
    // V19.2's Lz77BepV3 enlargement is kept (it may still pay on non-text
    // binary inputs that bypass this gate naturally), but the gate stays
    // closed on coherent text where the answer is now settled.
    //
    // ENABLE_STRIDE_GATE: skip stride-2 sibling candidates inside TextCtx
    // when the input shows no stride structure (cheap deinterleave-entropy
    // probe). On coherent text these almost never win — the stride-2 BEP
    // archive is consistently larger than the raw BEP archive. Saves the
    // cost of the per-sibling BEP encode (both V17 and V18 profiles).
    private const bool ENABLE_LZ_GATE      = true;
    private const bool ENABLE_STRIDE_GATE  = true;

    private const double LZ_GATE_TEXT_PRINT_THRESHOLD = 0.90;
    private const int    LZ_GATE_TEXT_ALPHABET_MAX    = 110;
    private const double STRIDE_GATE_MIN_S2_DH        = 0.05;
    private const int    STRIDE_GATE_SAMPLE_BYTES     = 16384;

    // Sentinel: nothing helped, body is raw input
    private const byte FLAG_RAW            = 0xF0;

    // Below this size, framing overhead dominates and post-coding can't earn
    // its keep. Same threshold as TextPipelineAdapter / TextPipelineDeflateBep.
    private const int MIN_USEFUL_INPUT     = 2048;

    // Stride-2 preprocessing requires at least this much data; below this,
    // the per-stream overhead would consume any savings.
    private const int MIN_STRIDE_INPUT     = 8192;

    public static byte[] Encode(byte[] input)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Length < MIN_USEFUL_INPUT) return WrapRaw(input);

        // V16.18 TIME-OPT: near-Shannon early-bail.
        // BWT+MTF+RePair on already-near-Shannon byte input adds significant
        // encode time and has zero chance of compressing — every stage produces
        // output ≥ input size. The variant's framing then has to discover this
        // via measurement and fall back to raw, wasting seconds on multi-MB
        // inputs. Pre-detect via byte entropy and short-circuit to raw.
        bool gateFired = LooksNearShannon(input);
        PickerDiagnostics.RecordNearShannonGate("TextPipelineCtxBep", input.Length, gateFired);
        if (gateFired) return WrapRaw(input);

        // ── Compute the BEP archive on the raw input ──
        // V17.0: profile gate. The diagnostic data shows V17 wins 100% on
        // text and V18 wins 100% on binary. A cheap printable-ASCII fraction
        // probe over the first 16 KB lets us skip the unlikely-to-win profile
        // when the input is confidently typed. ENABLE_PROFILE_GATE = false
        // restores V16.19 behavior (always run both).
        ProfileGateHint gateHint = ENABLE_PROFILE_GATE
            ? ProbeProfileGate(input)
            : ProfileGateHint.Both;
        PickerDiagnostics.RecordTextCtxProfileGate(gateHint.ToString(), input.Length);

        // V16.19 hybrid: compute BOTH the V18 entropy profile (RangeCoder/O1 +
        // apex-zero + cost-aware RePair available to the path-A picker) AND
        // the V17 profile (legacy: Rice/Unary/Split only, no apex-0, frequency-
        // mode RePair). The V18 profile produces a tighter byte-level archive
        // but also flatter byte distribution, which leaves nothing for the
        // Nibble/Nibble3/Arith wrap layers to remove. The V17 profile leaves
        // the slack the wrap layers profit from. Adding both into the candidate
        // pool below lets the existing picker globally pick the smaller of
        // {V17 BEP+wrap, V18 BEP±wrap} per file. No magic threshold required.
        // V17.0: gate may skip one of the two profiles on confidently-typed
        // inputs to halve encode time.
        byte[]? bepRawV18 = (gateHint != ProfileGateHint.V17Only)
                            ? TryBep(input, useV17EntropyProfile: false) : null;
        byte[]? bepRawV17 = (gateHint != ProfileGateHint.V18Only)
                            ? TryBep(input, useV17EntropyProfile: true)  : null;

        // ── Compute the BEP archive on the stride-2 deinterleaved input ──
        // Only worth attempting if input is large enough and even-length.
        // Odd-length input gets the trailing byte appended unchanged after
        // deinterleaving (handled inside Deinterleave2).
        //
        // V19.1: gate behind a cheap deinterleave-entropy probe. On coherent
        // text the stride-2 split doesn't reduce entropy and the resulting
        // candidates lose to the non-stride siblings by a wide margin. The
        // probe samples 16 KB and computes s2dH (same metric as
        // StructuralDiagnostics.Stride2HDelta). When s2dH is below the gate
        // threshold, skip the whole stride-2 branch — saves two BEP encodes
        // (V17 + V18) per file.
        byte[]? bepStrideV18 = null;
        byte[]? bepStrideV17 = null;
        byte[]? strideInput  = null;
        bool runStride = input.Length >= MIN_STRIDE_INPUT
                         && (!ENABLE_STRIDE_GATE || LooksStrideStructured(input));
        if (runStride)
        {
            strideInput  = Deinterleave2(input);
            bepStrideV18 = (gateHint != ProfileGateHint.V17Only)
                           ? TryBep(strideInput, useV17EntropyProfile: false) : null;
            bepStrideV17 = (gateHint != ProfileGateHint.V18Only)
                           ? TryBep(strideInput, useV17EntropyProfile: true)  : null;
        }

        // ── Try every post-coding on each ──
        // Both V17 and V18 archives use the same TextCtx flag values — the
        // BEP archive itself is self-describing (BEP header carries the
        // path-A coder choice, UnaryBEP format flag, etc.) so the decoder
        // doesn't need to know which profile was used. We segregate the
        // candidate lists per profile so we can record which profile won.
        var v18Candidates = new System.Collections.Generic.List<(byte[] body, byte flag)>();
        var v17Candidates = new System.Collections.Generic.List<(byte[] body, byte flag)>();
        if (bepRawV18    != null) AddCandidates(v18Candidates, bepRawV18,    postFlags: 0);
        if (bepRawV17    != null) AddCandidates(v17Candidates, bepRawV17,    postFlags: 0);
        if (bepStrideV18 != null) AddCandidates(v18Candidates, bepStrideV18, postFlags: STRIDE_FLAG);
        if (bepStrideV17 != null) AddCandidates(v17Candidates, bepStrideV17, postFlags: STRIDE_FLAG);

        var candidates = new System.Collections.Generic.List<(byte[] body, byte flag)>(
            v18Candidates.Count + v17Candidates.Count + 2);
        candidates.AddRange(v18Candidates);
        candidates.AddRange(v17Candidates);

        // V16.21: BepChainTextBep — operates on post-MTF bytes, not the BEP archive.
        // This is the correct pipeline position for BepChain: post-MTF bytes have ~50%
        // rank-0 and geometric-decay distribution, giving BepChainPass2 real signal.
        // Try stop=2 and stop=16; BepChainTextBep.Encode self-verifies and returns null
        // if no gain. stop=16 typically wins on text by 3-10pp over stop=2.
        try
        {
            byte[]? bc2  = BepChainTextBep.Encode(input, stopBelow: 2);
            byte[]? bc16 = BepChainTextBep.Encode(input, stopBelow: 16);
            byte[]? bcBest = (bc2 == null && bc16 == null) ? null
                           : (bc2 == null)  ? bc16
                           : (bc16 == null) ? bc2
                           : bc2.Length <= bc16.Length ? bc2 : bc16;
            if (bcBest != null)
                candidates.Add((bcBest, POSTCODE_CHAIN));
        }
        catch { }

        // Stride-2 variant of BepChainTextBep
        if (strideInput != null)
        {
            try
            {
                byte[]? sc2  = BepChainTextBep.Encode(strideInput, stopBelow: 2);
                byte[]? sc16 = BepChainTextBep.Encode(strideInput, stopBelow: 16);
                byte[]? scBest = (sc2 == null && sc16 == null) ? null
                               : (sc2 == null)  ? sc16
                               : (sc16 == null) ? sc2
                               : sc2.Length <= sc16.Length ? sc2 : sc16;
                if (scBest != null)
                    candidates.Add((scBest, (byte)(POSTCODE_CHAIN | STRIDE_FLAG)));
            }
            catch { }
        }

        // V17.0: Lz77BepV3 and DictPreLz77V3Bep as candidates inside TextCtx.
        // These are self-contained codecs (not BEP wrappers); like
        // BepChainTextBep, they operate on the input directly. Adding them
        // here lets the picker select an LZ-style approach on heterogeneous
        // large files (mozilla, samba, ooffice) where BWT+RePair is currently
        // bounded by block-size context, without disturbing the existing
        // wins on coherent text. Each .Encode self-verifies and may throw or
        // return non-beneficial output; we measure-and-pick like everything
        // else.
        //
        // V19.1: gate behind a coherent-text probe. On printable-heavy small-
        // alphabet inputs the LZ candidates consistently lose to the BEP-
        // family candidates above, and they're expensive to compute. Skip
        // them when the input is confidently typed as text.
        bool runLz = !ENABLE_LZ_GATE || !LooksLikeCoherentText(input);
        if (runLz)
        {
            try
            {
                byte[] lz = Lz77BepV3.Encode(input);
                if (lz != null && lz.Length > 0)
                    candidates.Add((lz, POSTCODE_LZ77));
            }
            catch { }
            try
            {
                byte[] dlz = DictPreLz77V3Bep.Encode(input);
                if (dlz != null && dlz.Length > 0)
                    candidates.Add((dlz, POSTCODE_DICTLZ77));
            }
            catch { }
        }

        // Stride-2 variants of the LZ candidates. Only worth trying if the
        // stride pre-pass already produced strideInput (gated by
        // MIN_STRIDE_INPUT above) AND the LZ candidates themselves weren't
        // gated out as unlikely-to-win.
        if (runLz && strideInput != null)
        {
            try
            {
                byte[] slz = Lz77BepV3.Encode(strideInput);
                if (slz != null && slz.Length > 0)
                    candidates.Add((slz, (byte)(POSTCODE_LZ77 | STRIDE_FLAG)));
            }
            catch { }
            try
            {
                byte[] sdlz = DictPreLz77V3Bep.Encode(strideInput);
                if (sdlz != null && sdlz.Length > 0)
                    candidates.Add((sdlz, (byte)(POSTCODE_DICTLZ77 | STRIDE_FLAG)));
            }
            catch { }
        }

        if (candidates.Count == 0) return WrapRaw(input);

        // ── Pick smallest ──
        byte[] bestBody = candidates[0].body;
        byte   bestFlag = candidates[0].flag;
        foreach (var (body, flag) in candidates)
        {
            if (body.Length < bestBody.Length)
            {
                bestBody = body; bestFlag = flag;
            }
        }

        // ── V16.19 hybrid profile diagnostic ──
        // If both profiles produced candidates, record which profile owns the
        // global winner. Savings is the byte delta vs the loser's best.
        if (v18Candidates.Count > 0 && v17Candidates.Count > 0)
        {
            int bestV18 = int.MaxValue;
            foreach (var c in v18Candidates) if (c.body.Length < bestV18) bestV18 = c.body.Length;
            int bestV17 = int.MaxValue;
            foreach (var c in v17Candidates) if (c.body.Length < bestV17) bestV17 = c.body.Length;
            if (bestV18 <= bestV17)
                PickerDiagnostics.RecordTextCtxProfileWin("V18", bestV17 - bestV18);
            else
                PickerDiagnostics.RecordTextCtxProfileWin("V17", bestV18 - bestV17);
        }

        // ── Compare to raw ──
        if (bestBody.Length + 1 >= input.Length + 1)
            return WrapRaw(input);

        // ── Frame and self-verify ──
        var framed = new byte[bestBody.Length + 1];
        framed[0] = bestFlag;
        Buffer.BlockCopy(bestBody, 0, framed, 1, bestBody.Length);

        try
        {
            byte[] roundtrip = Decode(framed);
            if (!ByteEquals(roundtrip, input))
                return WrapRaw(input);
        }
        catch
        {
            return WrapRaw(input);
        }

        return framed;
    }

    public static byte[] Decode(byte[] wrapped)
    {
        if (wrapped == null || wrapped.Length == 0)
            throw new InvalidDataException("TextPipelineCtxBep: empty input");

        byte flag = wrapped[0];
        var body = new byte[wrapped.Length - 1];
        Buffer.BlockCopy(wrapped, 1, body, 0, body.Length);

        if (flag == FLAG_RAW) return body;

        bool stride = (flag & STRIDE_FLAG) != 0;
        byte post   = (byte)(flag & POSTCODE_MASK);

        // V16.21: BepChainTextBep is a self-contained BWT+MTF+Chain codec.
        // It does NOT go through BEPPipeline — the body is a BepChainTextBep
        // frame that decodes directly to the source (or deinterleaved source).
        if (post == POSTCODE_CHAIN)
        {
            byte[] chainDecoded = BepChainTextBep.Decode(body);
            return stride ? Reinterleave2(chainDecoded) : chainDecoded;
        }

        // V17.0: POSTCODE_LZ77 and POSTCODE_DICTLZ77 are also self-contained
        // codecs — they don't go through BEPPipeline. Decode directly.
        if (post == POSTCODE_LZ77)
        {
            byte[] lzDecoded = Lz77BepV3.Decode(body);
            return stride ? Reinterleave2(lzDecoded) : lzDecoded;
        }
        if (post == POSTCODE_DICTLZ77)
        {
            byte[] dlzDecoded = DictPreLz77V3Bep.Decode(body);
            return stride ? Reinterleave2(dlzDecoded) : dlzDecoded;
        }

        // Step 1: undo post-coding to get the BEP archive
        byte[] bepArchive = post switch
        {
            POSTCODE_BEP        => body,
            POSTCODE_NIBBLE     => NibbleContextBep.Decode(body),
            POSTCODE_NIBBLE3    => NibbleContextOrder3Bep.Decode(body),
            POSTCODE_ARITH      => ArithmeticContextBep.Decode(body),
            _                   => throw new InvalidDataException(
                $"TextPipelineCtxBep: unknown post-coding bits {post}")
        };

        // Step 2: BEP-decode to get the (possibly deinterleaved) source
        byte[] decoded = BEPPipeline.Decompress(bepArchive);

        // Step 3: undo stride-2 deinterleave if it was applied
        return stride ? Reinterleave2(decoded) : decoded;
    }

    public static long MeasureBits(byte[] input)
    {
        try { return (long)Encode(input).Length * 8; }
        catch { return long.MaxValue; }
    }

    // ── Candidate generation ─────────────────────────────────────────────────

    private static void AddCandidates(
        System.Collections.Generic.List<(byte[] body, byte flag)> candidates,
        byte[] bepArchive,
        byte postFlags)
    {
        // Vanilla BEP path
        candidates.Add((bepArchive, (byte)(POSTCODE_BEP | postFlags)));

        // Each post-coding, swallowing any per-coder failure.
        // V17.2: pass useTextCtxPrior=true into NibCtx3 so it uses the
        // post-pipeline-distribution prior (trained on BEP-archive nibbles)
        // instead of the raw-input prior. The two priors live as separate
        // embedded resources; the archive's version byte tells the decoder
        // which one to load. NibbleContextBep (4-bit ctx) is unchanged for
        // V17.2 — its prior would be a follow-on if this one validates.
        try
        {
            byte[] withNibble = NibbleContextBep.Encode(bepArchive);
            candidates.Add((withNibble, (byte)(POSTCODE_NIBBLE | postFlags)));
        }
        catch { }
        try
        {
            byte[] withNibble3 = NibbleContextOrder3Bep.Encode(bepArchive, useTextCtxPrior: true);
            candidates.Add((withNibble3, (byte)(POSTCODE_NIBBLE3 | postFlags)));
        }
        catch { }
        try
        {
            byte[] withArith = ArithmeticContextBep.Encode(bepArchive);
            candidates.Add((withArith, (byte)(POSTCODE_ARITH | postFlags)));
        }
        catch { }
    }

    private static byte[]? TryBep(byte[] data, bool useV17EntropyProfile)
    {
        try
        {
            byte[] bep = BEPPipeline.Compress(
                data,
                BEPCompress.Core.CompressionMode.Default,
                bwtBlockSize: BEPPipeline.BWT_BLOCK_SIZE_AUTO,
                enableHuffmanWrap: false,
                enableRunaTransform: false,
                useV17EntropyProfile: useV17EntropyProfile);
            // Quick round-trip check on the BEP archive itself
            if (bep == null) return null;
            try { _ = BEPPipeline.Decompress(bep); }
            catch { return null; }
            return bep;
        }
        catch { return null; }
    }

    // ── Stride-2 deinterleave / reinterleave ─────────────────────────────────
    //
    // For 16-bit-per-sample data (medical images, audio, sensor streams),
    // consecutive byte pairs are samples. The high byte and low byte of each
    // sample have different statistical properties — high bytes are smooth
    // and low-magnitude (slow-changing magnitudes), low bytes are noisier
    // (fine-grained variation). Splitting and concatenating gives the
    // downstream coder two coherent streams instead of one mixed-shape one.

    /// <summary>
    /// Split bytes into even-position (indices 0, 2, 4, ...) followed by
    /// odd-position (indices 1, 3, 5, ...). Trailing odd byte stays at end.
    /// Same length out as in.
    /// </summary>
    private static byte[] Deinterleave2(byte[] input)
    {
        int n = input.Length;
        int evenCount = (n + 1) / 2;   // indices 0, 2, 4, ...
        int oddCount  = n / 2;          // indices 1, 3, 5, ...
        var output = new byte[n];
        // Even bytes
        for (int i = 0, j = 0; i < n; i += 2, j++)
            output[j] = input[i];
        // Odd bytes
        for (int i = 1, j = evenCount; i < n; i += 2, j++)
            output[j] = input[i];
        return output;
    }

    /// <summary>Inverse of Deinterleave2.</summary>
    private static byte[] Reinterleave2(byte[] split)
    {
        int n = split.Length;
        int evenCount = (n + 1) / 2;
        var output = new byte[n];
        for (int j = 0, i = 0; i < evenCount; i++, j += 2)
            output[j] = split[i];
        for (int j = 1, i = evenCount; j < n; i++, j += 2)
            output[j] = split[i];
        return output;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static byte[] WrapRaw(byte[] input)
    {
        var raw = new byte[input.Length + 1];
        raw[0] = FLAG_RAW;
        Buffer.BlockCopy(input, 0, raw, 1, input.Length);
        return raw;
    }

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>V16.18 TIME-OPT: detect inputs whose byte entropy is already near
    /// the Shannon limit (>7.95 bits/byte). These compress no better than raw via
    /// any byte-level codec; running BWT+MTF+RePair+BEP is wasted work. Sample
    /// up to 16KB for speed; threshold high enough to avoid false positives on
    /// text-after-BWT (entropy ~7.5-7.9, still meaningfully compressible bit-level).</summary>
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

    // V17.0: profile-gate hint for V17 vs V18 entropy profile selection.
    private enum ProfileGateHint
    {
        Both,        // mid-range distribution, can't decide → run both (V16.19 behavior)
        V17Only,     // text-like, only V17 has been observed to win → skip V18
        V18Only,     // binary-like, only V18 has been observed to win → skip V17
    }

    /// <summary>V17.0: cheap byte-distribution probe to decide whether to
    /// run V17, V18, or both entropy profiles. Diagnostic data shows V17
    /// wins 100% on text-like inputs and V18 wins 100% on binary-like
    /// inputs; the gate uses printable-ASCII fraction as the proxy.
    /// Mid-range (between LOW and HIGH thresholds) keeps the gate open and
    /// both profiles run (preserving V16.19 behavior on ambiguous inputs).</summary>
    private static ProfileGateHint ProbeProfileGate(byte[] data)
    {
        if (data == null || data.Length < 1024) return ProfileGateHint.Both;

        int sampleLen = Math.Min(PROFILE_GATE_SAMPLE_BYTES, data.Length);
        int printable = 0;
        for (int i = 0; i < sampleLen; i++)
        {
            byte b = data[i];
            // Printable ASCII range plus tab, LF, CR. These are the bytes
            // that dominate natural text and code.
            if ((b >= 0x20 && b <= 0x7E) || b == 0x09 || b == 0x0A || b == 0x0D)
                printable++;
        }
        double frac = (double)printable / sampleLen;

        if (frac >= PROFILE_GATE_TEXT_THRESHOLD)   return ProfileGateHint.V17Only;
        if (frac <  PROFILE_GATE_BINARY_THRESHOLD) return ProfileGateHint.V18Only;
        return ProfileGateHint.Both;
    }

    /// <summary>V19.1: cheap probe — does this input look like coherent
    /// text? Single 16 KB scan computing printable-ASCII fraction and
    /// distinct-byte count. Used to gate the LZ77/DictLz77 candidates,
    /// which lose on text and are expensive to compute. Threshold tuned
    /// conservatively — false negatives (running LZ on borderline text)
    /// are cheap (LZ just loses by a few KB), but false positives (skipping
    /// LZ on borderline binary) could cost real compression.</summary>
    private static bool LooksLikeCoherentText(byte[] data)
    {
        if (data == null || data.Length < 1024) return false;
        int sampleLen = Math.Min(PROFILE_GATE_SAMPLE_BYTES, data.Length);
        int printable = 0;
        var seen = new bool[256];
        int alphabet = 0;
        for (int i = 0; i < sampleLen; i++)
        {
            byte b = data[i];
            if (!seen[b]) { seen[b] = true; alphabet++; }
            if ((b >= 0x20 && b <= 0x7E) || b == 0x09 || b == 0x0A || b == 0x0D)
                printable++;
        }
        double frac = (double)printable / sampleLen;
        return frac >= LZ_GATE_TEXT_PRINT_THRESHOLD && alphabet <= LZ_GATE_TEXT_ALPHABET_MAX;
    }

    /// <summary>V19.1: cheap probe — does this input show stride-2 structure
    /// worth a BEP encode pass? Computes H0 on full sample vs 0.5*(H0(evens)
    /// + H0(odds)). Returns true if the delta exceeds STRIDE_GATE_MIN_S2_DH.
    /// Same metric as StructuralDiagnostics.Stride2HDelta but limited to a
    /// 16 KB sample so per-encode probe cost stays bounded.</summary>
    private static bool LooksStrideStructured(byte[] data)
    {
        if (data == null || data.Length < 1024) return false;
        int sampleLen = Math.Min(STRIDE_GATE_SAMPLE_BYTES, data.Length);
        // Trim to even length for clean even/odd partition.
        if ((sampleLen & 1) != 0) sampleLen--;
        if (sampleLen < 2) return false;

        int[] histAll  = new int[256];
        int[] histEven = new int[256];
        int[] histOdd  = new int[256];
        for (int i = 0; i < sampleLen; i++)
        {
            byte b = data[i];
            histAll[b]++;
            if ((i & 1) == 0) histEven[b]++; else histOdd[b]++;
        }
        double hAll  = ShannonH(histAll,  sampleLen);
        double hEven = ShannonH(histEven, sampleLen / 2);
        double hOdd  = ShannonH(histOdd,  sampleLen / 2);
        double s2dH  = hAll - 0.5 * (hEven + hOdd);
        return s2dH >= STRIDE_GATE_MIN_S2_DH;
    }

    private static double ShannonH(int[] hist, int total)
    {
        if (total <= 0) return 0.0;
        double h = 0.0;
        double inv = 1.0 / total;
        for (int v = 0; v < 256; v++)
        {
            if (hist[v] == 0) continue;
            double p = hist[v] * inv;
            h -= p * System.Math.Log2(p);
        }
        return h;
    }
}
