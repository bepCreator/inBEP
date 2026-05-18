// =============================================================================
// StructuralDiagnostics — V19.0 NEW.
//
// For every benchmarked input, computes cheap structural metrics that describe
// what the file LOOKS LIKE before any compression. The goal is to give the
// researcher a portable view of "what structure does this file have, and which
// of the kept variants is best positioned to exploit it" — without re-running
// compression to find out.
//
// All metrics are O(n) single-pass. Memory is bounded by the table sizes
// (256-byte hist, 65,536 byte-pair hist). Computed once per file; the result
// is small enough to print as one row in the bench/corpus output.
//
// Metrics computed (one row per file):
//
//   SIZE / SHAPE
//     size_bytes              — raw input size
//     alphabet                — number of distinct byte values seen (1..256)
//     null_pct                — fraction of input that is 0x00
//     ff_pct                  — fraction of input that is 0xFF
//     ascii_print_pct         — fraction in 0x20..0x7E (text-vs-binary discriminator,
//                               same probe TextCtx uses for V17/V18 gating)
//
//   ENTROPY
//     H0_bits                 — Shannon entropy of byte distribution (bits/byte).
//                               Lower bound for any order-0 entropy coder.
//     H1_bits                 — Conditional entropy H(X_{n+1} | X_n) (bits/byte).
//                               Lower bound for any order-1 entropy coder.
//                               H0 - H1 is "what an order-1 model buys you".
//     Hpair_bits              — Entropy of byte pairs (treated as 16-bit symbol),
//                               divided by 2 to make it per-byte-comparable.
//
//   RUN / PERIODICITY
//     max_run                 — longest run of the same byte
//     run_2plus_pct           — fraction of bytes that are part of a run >= 2
//                               (high values favor RLE-style approaches)
//     period_2_score          — fraction of adjacent byte pairs (i, i+1) where
//                               input[i] == input[i+2]; high = even/odd-stride
//                               structure (favors StrideSplit)
//
//   STRIDE-2 ENTROPY DELTA
//     stride2_h_delta         — H0(input) - 0.5*(H0(evens) + H0(odds)).
//                               Positive means stride-2 split reduces order-0
//                               entropy, which is the structural signal that
//                               StrideSplit can monetize. Often >0.3 bits on
//                               16-bit medical / sensor data.
//
//   TOP-BYTE COVERAGE
//     top1_pct                — frequency of the most common byte
//     top3_pct                — combined frequency of the top 3 bytes
//
// All metrics are reported as small floats / ints / percentages — designed to
// be one row per file in the bench table without overflow.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.Text;

namespace InBep.Core;

public static class StructuralDiagnostics
{
    public sealed class Metrics
    {
        public long  SizeBytes;
        public int   Alphabet;
        public double NullPct;
        public double FfPct;
        public double AsciiPrintPct;
        public double H0Bits;
        public double H1Bits;
        public double HpairBits;       // per-byte (entropy of 16-bit pair / 2)
        public int   MaxRun;
        public double Run2PlusPct;
        public double Period2Score;
        public double Stride2HDelta;
        public double Top1Pct;
        public double Top3Pct;
    }

    /// <summary>
    /// Single-pass O(n) structural fingerprint. Allocates one 256-int byte
    /// histogram, one 65,536-int pair histogram (256 KB peak), two stride
    /// histograms when input ≥ 2 bytes. No allocations proportional to n.
    /// </summary>
    public static Metrics Compute(byte[] input)
    {
        var m = new Metrics { SizeBytes = input.Length };
        if (input.Length == 0) return m;

        // ── Byte histogram and basic single-byte features ──
        int[] hist = new int[256];
        int maxRun = 1, curRun = 1;
        long runBytesInRuns = 0;
        byte prev = input[0];
        hist[prev]++;
        for (int i = 1; i < input.Length; i++)
        {
            byte b = input[i];
            hist[b]++;
            if (b == prev) curRun++;
            else
            {
                if (curRun >= 2) runBytesInRuns += curRun;
                if (curRun > maxRun) maxRun = curRun;
                curRun = 1;
            }
            prev = b;
        }
        if (curRun >= 2) runBytesInRuns += curRun;
        if (curRun > maxRun) maxRun = curRun;

        // alphabet + null/ff/ascii_print
        int alphabet = 0;
        long printable = 0;
        for (int v = 0; v < 256; v++)
        {
            if (hist[v] > 0) alphabet++;
            if (v >= 0x20 && v <= 0x7E) printable += hist[v];
        }
        m.Alphabet      = alphabet;
        m.NullPct       = 100.0 * hist[0x00] / input.Length;
        m.FfPct         = 100.0 * hist[0xFF] / input.Length;
        m.AsciiPrintPct = 100.0 * printable / input.Length;
        m.MaxRun        = maxRun;
        m.Run2PlusPct   = 100.0 * runBytesInRuns / input.Length;

        // ── H0 (order-0 Shannon byte entropy) ──
        double h0 = 0.0;
        double invN = 1.0 / input.Length;
        for (int v = 0; v < 256; v++)
        {
            if (hist[v] == 0) continue;
            double p = hist[v] * invN;
            h0 -= p * Math.Log2(p);
        }
        m.H0Bits = h0;

        // ── top1 / top3 coverage ──
        // Find top 3 frequencies without sorting the whole histogram.
        int top1 = 0, top2 = 0, top3 = 0;
        for (int v = 0; v < 256; v++)
        {
            int c = hist[v];
            if (c > top1) { top3 = top2; top2 = top1; top1 = c; }
            else if (c > top2) { top3 = top2; top2 = c; }
            else if (c > top3) { top3 = c; }
        }
        m.Top1Pct = 100.0 * top1 / input.Length;
        m.Top3Pct = 100.0 * (top1 + top2 + top3) / input.Length;

        if (input.Length < 2) { m.H1Bits = h0; m.HpairBits = h0; return m; }

        // ── Pair histogram (input[i] << 8 | input[i+1]) ──
        // 256 KB allocation; one pass; OK for files into the GB range, since
        // the bench corpus is dozens of MB max.
        int[] pairHist = new int[65536];
        int pairs = input.Length - 1;
        for (int i = 0; i < pairs; i++)
            pairHist[(input[i] << 8) | input[i + 1]]++;

        // Hpair (joint entropy of two consecutive bytes, per pair). We report
        // it /2 so it's per-byte-comparable to H0.
        double hPair = 0.0;
        double invP  = 1.0 / pairs;
        for (int v = 0; v < 65536; v++)
        {
            if (pairHist[v] == 0) continue;
            double p = pairHist[v] * invP;
            hPair -= p * Math.Log2(p);
        }
        m.HpairBits = hPair / 2.0;

        // ── H1 (conditional entropy H(X_{n+1} | X_n)) ──
        // H1 = Hpair - H0(prefix), where H0(prefix) is entropy of input[0..n-2].
        // Since the prefix differs from the full input by one byte, just use
        // the existing hist with input[input.Length-1] removed for accuracy
        // on small inputs. On bench-sized inputs (≥1 KB) the difference is
        // negligible, so we use h0 as a close-enough proxy and skip the
        // adjustment.
        m.H1Bits = Math.Max(0.0, hPair - h0);

        // ── period_2 score: how often input[i] == input[i+2] ──
        if (input.Length >= 3)
        {
            long matches = 0;
            int n = input.Length - 2;
            for (int i = 0; i < n; i++)
                if (input[i] == input[i + 2]) matches++;
            m.Period2Score = 100.0 * matches / n;
        }

        // ── stride-2 entropy delta ──
        // Cheaper to compute from the byte histogram of the deinterleaved
        // streams. Build per-stream histograms in one pass.
        int evenLen = (input.Length + 1) / 2;
        int oddLen  = input.Length / 2;
        int[] histE = new int[256];
        int[] histO = new int[256];
        for (int i = 0; i < input.Length; i++)
        {
            if ((i & 1) == 0) histE[input[i]]++;
            else              histO[input[i]]++;
        }
        double hE = ShannonH(histE, evenLen);
        double hO = oddLen == 0 ? 0.0 : ShannonH(histO, oddLen);
        m.Stride2HDelta = h0 - 0.5 * (hE + hO);

        return m;
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
            h -= p * Math.Log2(p);
        }
        return h;
    }

    /// <summary>
    /// One-line label used as the structural-summary column in bench tables.
    /// </summary>
    public static string FormatBrief(Metrics m)
    {
        return $"H0={m.H0Bits:F2} H1={m.H1Bits:F2} dH={m.H0Bits - m.H1Bits:F2} " +
               $"|A|={m.Alphabet,3} prn={m.AsciiPrintPct:F0}% " +
               $"run+={m.Run2PlusPct:F0}% s2dH={m.Stride2HDelta:+0.00;-0.00; 0.00}";
    }

    /// <summary>
    /// Per-file row: name, size, all metric columns. Designed to be one wide
    /// line in the bench output.
    /// </summary>
    public static string FormatRow(string name, Metrics m)
    {
        return $"{name,-32} {m.SizeBytes,12:N0} | " +
               $"H0={m.H0Bits,5:F2} H1={m.H1Bits,5:F2} dH={m.H0Bits - m.H1Bits,5:F2} " +
               $"Hp={m.HpairBits,5:F2} | " +
               $"|A|={m.Alphabet,3} prn={m.AsciiPrintPct,5:F1}% " +
               $"nul={m.NullPct,4:F1}% ff={m.FfPct,4:F1}% | " +
               $"top1={m.Top1Pct,5:F1}% top3={m.Top3Pct,5:F1}% | " +
               $"run+={m.Run2PlusPct,5:F1}% maxR={m.MaxRun,7:N0} | " +
               $"per2={m.Period2Score,5:F1}% s2dH={m.Stride2HDelta:+0.00;-0.00; 0.00}";
    }

    /// <summary>
    /// Header for FormatRow output.
    /// </summary>
    public static string FormatRowHeader()
    {
        return $"{"file",-32} {"size",12} | {"H0",5} {"H1",5} {"dH",5} {"Hp",5} | " +
               $"{"|A|",3} {"prn",6} {"nul",5} {"ff",5} | {"top1",6} {"top3",6} | " +
               $"{"run+",6} {"maxR",7} | {"per2",6} {"s2dH",5}";
    }

    /// <summary>
    /// V19.1 smart-route decision. For each kept variant, returns whether to
    /// run it on this input and (if skipping) a one-line human reason.
    ///
    /// Conservative philosophy: only kill clear losers. NibCtx3 / ArithCtx /
    /// StrideSplit all execute in &lt;200 ms even on multi-MB inputs, so
    /// keeping them gives free coverage of cases where the structural probe
    /// is wrong (e.g. the `sum` file in the Calgary corpus, where StrideSplit
    /// won despite a low s2dH score). Baselines always run — they're the
    /// comparison reference.
    ///
    /// The two variants worth gating:
    ///   TextCtx     — encode-time can hit 18+ seconds on multi-MB text;
    ///                 worth skipping on files where structure says stride
    ///                 dominates or where the alphabet is too small for
    ///                 BWT+RePair to find dictionary headroom.
    ///   ProtNibCtx3 — loses to NibCtx3 by 1-5pp on every non-protein file;
    ///                 only earns its keep on |A| ≤ 32 (amino-acid scale).
    /// </summary>
    public sealed class RoutingDecision
    {
        public bool RunTextCtx     { get; init; } = true;
        public bool RunNibCtx3     { get; init; } = true;
        public bool RunProtNibCtx3 { get; init; } = true;
        public bool RunArithCtx    { get; init; } = true;
        public bool RunStrideSplit { get; init; } = true;

        public string? SkipReasonTextCtx     { get; init; }
        public string? SkipReasonNibCtx3     { get; init; }
        public string? SkipReasonProtNibCtx3 { get; init; }
        public string? SkipReasonArithCtx    { get; init; }
        public string? SkipReasonStrideSplit { get; init; }

        /// <summary>Routing that runs everything — restores V19.0 behavior.
        /// Used when --full-matrix is set, or by code paths that don't
        /// participate in smart-routing (compare all, benchmark all).</summary>
        public static RoutingDecision RunAll => new RoutingDecision();
    }

    /// <summary>
    /// Decide which variants to run on this input based on its structural
    /// fingerprint. See <see cref="RoutingDecision"/> for the philosophy and
    /// the cost/benefit math behind which variants get gated.
    /// </summary>
    public static RoutingDecision DecideRouting(Metrics m)
    {
        // TextCtx — skip when stride structure dominates or when small
        // alphabet leaves BWT+RePair no room to expand the symbol set.
        bool runTextCtx = true;
        string? textCtxReason = null;
        if (m.Stride2HDelta >= 0.20)
        {
            runTextCtx = false;
            textCtxReason = $"s2dH={m.Stride2HDelta:F2} ≥ 0.20 — stride structure dominates (StrideSplit territory)";
        }
        else if (m.Alphabet <= 32 && (m.H0Bits - m.H1Bits) >= 0.30)
        {
            runTextCtx = false;
            textCtxReason = $"|A|={m.Alphabet} ≤ 32 with strong order-1 — BWT+RePair has no headroom (NibCtx3/ProtNibCtx3 territory)";
        }

        // ProtNibCtx3 — the protein prior only helps on amino-acid-scale
        // alphabets. On wider alphabets it's a mild bias the model has to
        // unlearn, and it consistently loses to NibCtx3 by 1-5pp.
        bool runProt = true;
        string? protReason = null;
        if (m.Alphabet > 32)
        {
            runProt = false;
            protReason = $"|A|={m.Alphabet} > 32 — protein prior only helps on amino-acid-scale alphabets";
        }

        return new RoutingDecision
        {
            RunTextCtx           = runTextCtx,
            SkipReasonTextCtx    = textCtxReason,
            RunProtNibCtx3       = runProt,
            SkipReasonProtNibCtx3 = protReason,
            // NibCtx3, ArithCtx, StrideSplit always run.
        };
    }

    /// <summary>
    /// Predictive hint mapping metrics → which kept variant has the most
    /// structural reason to win. NOT a substitute for measurement — just a
    /// research signal. Returns the variant short-name plus a one-line reason.
    /// </summary>
    public static (string variant, string reason) PredictBestVariant(Metrics m)
    {
        // Stride-2 wins are loud when present. Threshold tuned to the
        // medical-TIFF / sensor patterns where stride-split paid off.
        if (m.Stride2HDelta >= 0.20)
            return ("StrideSplit", $"stride-2 H drop {m.Stride2HDelta:F2} bits — byte-pair samples present");

        // Text-shaped input → TextCtx by a wide margin in the V17 results.
        if (m.AsciiPrintPct >= 85.0 && m.Alphabet <= 100)
            return ("TextCtx", $"printable={m.AsciiPrintPct:F0}% alphabet={m.Alphabet} — coherent text");

        // Small alphabet + strong order-1 signal → protein / DNA-like.
        if (m.Alphabet <= 32 && (m.H0Bits - m.H1Bits) >= 0.30)
            return ("ProtNibCtx3", $"alphabet={m.Alphabet} dH={m.H0Bits - m.H1Bits:F2} — small symbol set, strong context");

        // Mid-alphabet with strong order-1 signal → NibCtx3.
        if ((m.H0Bits - m.H1Bits) >= 0.40)
            return ("NibCtx3", $"dH={m.H0Bits - m.H1Bits:F2} bits — order-1 context buys real signal");

        // Otherwise ArithCtx is the floor (it has no structural prejudice).
        return ("ArithCtx", $"no dominant structural signal — order-2 arith is the safe default");
    }
}
