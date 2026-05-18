// =============================================================================
// DynamicBwt — Sample-driven BWT block-size selection ("BWT expansion")
//
// V17.0 REVISIONS over V15.2:
//   1. CANDIDATE_SIZES extended upward to 8 MB and 32 MB. The previous max of
//      2 MB meant a 51 MB mozilla still got split into ~26 BWT blocks even at
//      the largest candidate, leaving substantial cross-block context unused.
//      LZMA's preset-6 8 MB window beats us on heterogeneous archives
//      precisely because of this gap. Larger BWT blocks let TextCtx see the
//      same long-range structure.
//
//   2. SAMPLE_BYTES_MAX raised from 4 MB to 16 MB. Required for the new larger
//      candidates: a 4 MB sample can't differentiate 8 MB vs 32 MB block
//      sizes (both collapse to one block on the sample). At 16 MB, the 8 MB
//      candidate gets 2 blocks for measurement and the 32 MB candidate gets
//      a single 16 MB block (still informative — gives the asymptotic
//      MTF-zero fraction for very large blocks).
//
//   3. Distributed sampling — was head-only Buffer.BlockCopy of the first
//      sampleSize bytes. On a 51 MB mozilla that meant probing only the
//      header region, which has different statistics from the body. Now
//      samples 4 equal chunks at 0%, 25%, 50%, 75% of the input when the
//      input is more than 4× the sample size; otherwise falls back to
//      head-only (chunks would overlap meaningfully on smaller inputs).
//
//   4. Single-block fallback for oversized candidates — instead of skipping
//      candidates larger than the sample (the V15 behavior), run them as a
//      single block on the full sample. Provides a directional estimate
//      (slightly biased low because real K-blocks would have more cross-
//      context, but order-of-magnitude correct).
//
//   5. Zero-order entropy of MTF output is now computed alongside MTF-zero
//      fraction and recorded in ProbeResult.MtfEntropyBitsPerByte. This is
//      a more principled proxy for entropy-coded archive size than zero
//      count alone (it captures the full rank distribution, not just the
//      rank-0 mass). For V17.0 the picker still uses MTF-zero fraction with
//      the existing 1% TIE_THRESHOLD; entropy is logged for diagnostics so
//      that V17.1 can switch the metric with benchmark validation rather
//      than guesswork.
//
//   6. Memory headroom check — candidates whose suffix-array working set
//      (~5x block size) exceeds ~25% of available process memory are
//      skipped. Prevents a 32 MB candidate from triggering an OOM on small
//      build agents or constrained dev boxes.
//
// V15 baseline (preserved):
//   - Parallel.For across candidates (read-only on the sample)
//   - Small-input bypass below SMALL_INPUT_THRESHOLD (1 MB)
//   - Tie threshold 0.01 on MTF-zero fraction; pick LARGEST among tied
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace BEPCompress.Core
{
    public static class DynamicBwt
    {
        /// <summary>
        /// Block sizes the probe considers. V17.0 extends the upper end from
        /// 2 MB to 32 MB. The new 8 MB and 32 MB candidates are aimed at huge
        /// heterogeneous files (mozilla, samba) where LZMA's bigger match
        /// window currently dominates.
        /// </summary>
        public static readonly int[] CANDIDATE_SIZES =
        {
            64 * 1024,         //  64 KB - small, for highly local patterns
            512 * 1024,        // 512 KB - Mode A baseline, best on enwik9
            2 * 1024 * 1024,   //   2 MB - best on structured JSON/XML
            8 * 1024 * 1024,   //   8 MB - V17.0: large-text and code archives
            32 * 1024 * 1024,  //  32 MB - V17.0: huge-file (mozilla-class)
        };

        /// <summary>
        /// V17.0: raised from 4 MB to 16 MB to support the larger candidates.
        /// Probe time on 16 MB is ~10-20s on a multi-core box (parallel across
        /// candidates), acceptable for an overnight run. Inputs smaller than
        /// this cap use Math.Min(input.Length, SAMPLE_BYTES_MAX) so behavior
        /// on medium inputs (1-16 MB) is unchanged: sample = whole input.
        /// </summary>
        public const int SAMPLE_BYTES_MAX = 16 * 1024 * 1024;

        /// <summary>
        /// Back-compat alias. Retained as a public constant in case any
        /// external code referenced it; new code should use SAMPLE_BYTES_MAX.
        /// </summary>
        [Obsolete("Use SAMPLE_BYTES_MAX (V17.0). Kept for source compatibility.")]
        public const int SAMPLE_BYTES = SAMPLE_BYTES_MAX;

        /// <summary>
        /// V15: bypass the probe entirely below this input size. The probe is
        /// pure overhead on tiny inputs - the candidate set collapses to just
        /// 64 KB anyway because nothing larger fits. Returning the default
        /// directly saves the BWT+MTF setup cost.
        /// </summary>
        public const int SMALL_INPUT_THRESHOLD = 1 * 1024 * 1024;

        /// <summary>
        /// V17.0: distribute the sample across the input (start, 25%, 50%, 75%)
        /// when input is at least this multiple of the sample size. On smaller
        /// inputs the chunks would overlap meaningfully, so we fall back to
        /// head-only sampling. 4x ensures non-overlapping quartile chunks.
        /// </summary>
        private const int DISTRIBUTED_SAMPLE_RATIO = 4;

        /// <summary>
        /// V17.0: BWT suffix-array working set scales as roughly 5x block size
        /// (4-byte int suffix array plus auxiliary structures). A candidate is
        /// skipped if its working set exceeds this fraction of available
        /// process memory. Conservative - better to miss a candidate than OOM.
        /// </summary>
        private const double SUFFIX_ARRAY_OVERHEAD = 5.0;
        private const double MAX_MEMORY_FRACTION = 0.25;

        /// <summary>
        /// Tie-breaking margin: any candidate whose MTF-zero fraction is within
        /// this margin of the best is considered tied, and we pick the largest
        /// among them (better Re-Pair context).
        /// </summary>
        private const double TIE_THRESHOLD = 0.01;

        public sealed class ProbeResult
        {
            public int BlockSize;
            public double MtfZeroFraction;
            /// <summary>V17.0: zero-order entropy (bits/byte) of the MTF output
            /// at this block size. Lower = more compressible. Computed for
            /// diagnostics; not yet used by the picker.</summary>
            public double MtfEntropyBitsPerByte;
            public long SampleBytes;
            public TimeSpan Elapsed;
            /// <summary>V17.0: true if this candidate was probed as a single
            /// block because it exceeded the sample size. Score is a directional
            /// estimate, not a true K-block measurement.</summary>
            public bool SingleBlockEstimate;
        }

        /// <summary>
        /// Probe candidate block sizes on a sample and return the recommended
        /// size plus the full sweep results (useful for diagnostics).
        /// </summary>
        public static (int recommendedSize, List<ProbeResult> probes) Probe(
            byte[] input, Action<string>? log = null)
        {
            if (input == null || input.Length == 0)
                return (CANDIDATE_SIZES[0], new List<ProbeResult>());

            // V15 small-input bypass: skip the probe entirely on tiny inputs.
            // Only the 64 KB candidate fits, and the probe overhead would be
            // most of the compression time on a small file.
            if (input.Length < SMALL_INPUT_THRESHOLD)
            {
                log?.Invoke($"DynamicBwt: input {FormatBytes(input.Length)} below " +
                            $"{FormatBytes(SMALL_INPUT_THRESHOLD)} threshold - using {FormatBytes(CANDIDATE_SIZES[0])}");
                return (CANDIDATE_SIZES[0], new List<ProbeResult>());
            }

            int sampleSize = Math.Min(input.Length, SAMPLE_BYTES_MAX);
            byte[] sample = BuildSample(input, sampleSize, log);

            // V17.0: filter candidates by available memory headroom.
            // Suffix-array working set ~ 5x block size (int[] SA + aux).
            long availableMem = GetAvailableMemoryEstimate();
            long memBudget = (long)(availableMem * MAX_MEMORY_FRACTION);
            var feasibleCandidates = new List<int>();
            foreach (var size in CANDIDATE_SIZES)
            {
                long working = (long)(size * SUFFIX_ARRAY_OVERHEAD);
                if (working <= memBudget) feasibleCandidates.Add(size);
                else log?.Invoke($"  skip {FormatBytes(size)}: working set " +
                                 $"~{FormatBytes(working)} exceeds budget {FormatBytes(memBudget)}");
            }
            if (feasibleCandidates.Count == 0) feasibleCandidates.Add(CANDIDATE_SIZES[0]);

            log?.Invoke($"DynamicBwt: probing on {FormatBytes(sampleSize)} sample " +
                        $"({feasibleCandidates.Count} candidates in parallel)...");

            // V15 parallel probe: candidates are independent and read-only on
            // the sample. ProbeResult-per-index avoids needing a thread-safe
            // collection.
            var results = new ProbeResult?[feasibleCandidates.Count];

            Parallel.For(0, feasibleCandidates.Count, idx =>
            {
                int size = feasibleCandidates[idx];

                var sw = Stopwatch.StartNew();

                // V17.0: when the candidate exceeds the sample, run as a
                // single block on the full sample rather than skipping. The
                // resulting score is a single-block estimate - biased slightly
                // low (real K-blocks have more BWT context) but directionally
                // correct and far better than no measurement.
                bool singleBlockEstimate = size > sample.Length;
                int effectiveSize = singleBlockEstimate ? sample.Length : size;
                int bc = (sample.Length + effectiveSize - 1) / effectiveSize;
                long totalZeros = 0, totalLen = 0;
                long[] symbolCounts = new long[256];

                for (int b = 0; b < bc; b++)
                {
                    int start = b * effectiveSize;
                    int len = Math.Min(effectiveSize, sample.Length - start);
                    var blk = new byte[len];
                    Buffer.BlockCopy(sample, start, blk, 0, len);

                    var bwtOut = BWT.Forward(blk).bwt;
                    var mtfOut = MTF.Encode(bwtOut);
                    for (int i = 0; i < mtfOut.Length; i++)
                    {
                        byte v = mtfOut[i];
                        if (v == 0) totalZeros++;
                        symbolCounts[v]++;
                    }
                    totalLen += mtfOut.Length;
                }
                sw.Stop();

                double frac = totalLen == 0 ? 0.0 : (double)totalZeros / totalLen;
                double entropy = ZeroOrderEntropy(symbolCounts, totalLen);

                results[idx] = new ProbeResult
                {
                    BlockSize = size,
                    MtfZeroFraction = frac,
                    MtfEntropyBitsPerByte = entropy,
                    SampleBytes = sample.Length,
                    Elapsed = sw.Elapsed,
                    SingleBlockEstimate = singleBlockEstimate
                };
            });

            var probes = results.Where(r => r != null).Select(r => r!).ToList();

            // Tiny-input case: nothing fit. (Shouldn't reach here given the
            // SMALL_INPUT_THRESHOLD bypass and the feasibility fallback above,
            // but handle defensively.)
            if (probes.Count == 0)
            {
                int fallback = Math.Max(input.Length, CANDIDATE_SIZES[0]);
                log?.Invoke($"  -> no candidate fit sample; using {FormatBytes(fallback)} (single block)");
                return (fallback, probes);
            }

            // Log results in candidate-size order (parallel run completed in
            // arbitrary order, but the report should be ordered).
            foreach (var p in probes.OrderBy(p => p.BlockSize))
            {
                string flag = p.SingleBlockEstimate ? " [single-block est.]" : "";
                log?.Invoke($"  {FormatBytes(p.BlockSize),10}:  MTF zeros {p.MtfZeroFraction * 100,6:F2}%  " +
                            $"entropy {p.MtfEntropyBitsPerByte,5:F3} bpb  ({p.Elapsed.TotalSeconds:F2}s){flag}");
            }

            // Pick the LARGEST block whose MTF-zero fraction is within
            // TIE_THRESHOLD of the best. (V17.0: picker logic unchanged;
            // entropy is observation-only this release.)
            double bestFrac = probes.Max(p => p.MtfZeroFraction);
            double cutoff = bestFrac - TIE_THRESHOLD;
            var qualifying = probes.Where(p => p.MtfZeroFraction >= cutoff).ToList();
            int recommended = qualifying.Max(p => p.BlockSize);

            log?.Invoke($"  -> recommended: {FormatBytes(recommended)} block size " +
                        $"(best frac {bestFrac * 100:F2}%, tied with {qualifying.Count} candidate(s))");

            return (recommended, probes);
        }

        /// <summary>
        /// V17.0: build the probe sample. For inputs >= DISTRIBUTED_SAMPLE_RATIO x
        /// the sample size, takes 4 equal chunks at 0%, 25%, 50%, 75%. For
        /// smaller inputs falls back to head-only (chunks would overlap).
        /// </summary>
        private static byte[] BuildSample(byte[] input, int sampleSize, Action<string>? log)
        {
            // Sample == input (small/medium files): just copy.
            if (sampleSize >= input.Length)
            {
                var s = new byte[sampleSize];
                Buffer.BlockCopy(input, 0, s, 0, sampleSize);
                return s;
            }

            // Input not large enough for non-overlapping distributed chunks:
            // fall back to head-only (V15 behavior).
            if (input.Length < (long)sampleSize * DISTRIBUTED_SAMPLE_RATIO)
            {
                var s = new byte[sampleSize];
                Buffer.BlockCopy(input, 0, s, 0, sampleSize);
                log?.Invoke($"DynamicBwt: head-only sample (input {FormatBytes(input.Length)} < " +
                            $"{DISTRIBUTED_SAMPLE_RATIO}x sample size)");
                return s;
            }

            // Distributed sample: 4 chunks at quartile offsets, each chunk
            // sampleSize/4 bytes. Total = sampleSize.
            int chunkSize = sampleSize / 4;
            int residue = sampleSize - chunkSize * 4;
            var sample = new byte[sampleSize];

            // Quartile offsets, computed in long math to avoid overflow on
            // large inputs (input.Length * 3 can exceed int.MaxValue).
            long[] offsets =
            {
                0L,
                input.Length / 4L,
                input.Length / 2L,
                (input.Length * 3L) / 4L
            };

            int writePos = 0;
            for (int q = 0; q < 4; q++)
            {
                int thisChunk = chunkSize + (q == 3 ? residue : 0);
                long src = offsets[q];
                // Clamp: don't read past end of input.
                if (src + thisChunk > input.Length) src = input.Length - thisChunk;
                Buffer.BlockCopy(input, (int)src, sample, writePos, thisChunk);
                writePos += thisChunk;
            }

            log?.Invoke($"DynamicBwt: distributed sample - 4x {FormatBytes(chunkSize)} " +
                        $"chunks at 0%/25%/50%/75% of {FormatBytes(input.Length)}");
            return sample;
        }

        /// <summary>
        /// V17.0: zero-order entropy of the MTF output, in bits per byte.
        /// H(X) = -sum p(x) log2 p(x). This is the lower bound an order-0
        /// entropy coder would achieve on the MTF stream and is a more
        /// principled proxy for compressed size than zero-count alone.
        /// </summary>
        private static double ZeroOrderEntropy(long[] symbolCounts, long total)
        {
            if (total == 0) return 0.0;
            double h = 0.0;
            double invTotal = 1.0 / total;
            for (int i = 0; i < symbolCounts.Length; i++)
            {
                long c = symbolCounts[i];
                if (c == 0) continue;
                double p = c * invTotal;
                h -= p * Math.Log2(p);
            }
            return h;
        }

        /// <summary>
        /// V17.0: estimate available memory for the BWT suffix array. Uses
        /// GC.GetGCMemoryInfo when available, falls back to a conservative
        /// 1 GB assumption on older runtimes.
        /// </summary>
        private static long GetAvailableMemoryEstimate()
        {
            try
            {
                var info = GC.GetGCMemoryInfo();
                long total = info.TotalAvailableMemoryBytes;
                long used = GC.GetTotalMemory(forceFullCollection: false);
                long avail = total - used;
                return avail > 0 ? avail : 1L * 1024 * 1024 * 1024;
            }
            catch
            {
                return 1L * 1024 * 1024 * 1024;
            }
        }

        /// <summary>Convenience wrapper returning only the recommended size.</summary>
        public static int PickBlockSize(byte[] input, Action<string>? log = null)
            => Probe(input, log).recommendedSize;

        private static string FormatBytes(long b)
        {
            if (b >= 1_048_576) return $"{b / 1_048_576.0:F1} MB";
            if (b >= 1_024) return $"{b / 1_024.0:F0} KB";
            return $"{b} B";
        }
    }
}
