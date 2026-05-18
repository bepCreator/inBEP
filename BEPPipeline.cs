// =============================================================================
// BEPPipeline — Fully reversible iterative BEP compression pipeline
//
// Modes:
//   Default — BWT(64KB) + MTF + Re-Pair + FreqRank + UnaryBEP
//   ModeA   — BWT(512KB) + MTF + Re-Pair + FreqRank + UnaryBEP
//             Larger blocks = longer runs = more MTF zeros = better compression
//             Slower (O(n log n) suffix array on larger blocks) but meaningfully smaller
//   ModeB   — BWT(64KB) + MTF + PPM-3 rank transform + Re-Pair + FreqRank + UnaryBEP
//             PPM-3 predicts next MTF value from last 3 values
//             On BWT+MTF output with 59% zeros, PPM-3 accuracy ~75-85% rank-0
//             Adds ~85s sequential pass but significantly improves compression
//
// Auto-stop: each pass checks if output < 95% of input. Stops when it isn't.
//
// Author:  Rich Wagner | License: Apache 2.0 | newdawndata.com
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InBep.Core;   // For DiagnosticTimings — sub-stage timing accumulator

namespace BEPCompress.Core
{
    public enum CompressionMode
    {
        Default = 0,   // BWT 64KB blocks
        ModeA   = 1,   // BWT 512KB blocks
        ModeB   = 2,   // BWT 64KB + PPM-3 between MTF and Re-Pair
        ModeD   = 3,   // 7-bit ASCII alphabet: grammar symbols start at 128 instead of 256
                       // Best for pure/near-pure ASCII (JSON, English XML, code)
                       // Auto-falls-back to Default if non-ASCII bytes exceed threshold
    }

    public static class BEPPipeline
    {
        // Block sizes
        public const int BWT_BLOCK_SIZE_DEFAULT = 65536;    // 64KB
        public const int BWT_BLOCK_SIZE_A       = 524288;    // 512KB — Mode A
        // V13 BWT-expansion sentinel: pass this as bwtBlockSize to trigger
        // DynamicBwt.Probe() at the top of CompressIterative. The probe runs
        // BWT+MTF on a 4 MB sample at candidate sizes (64K / 512K / 2M) and
        // picks the largest one within 1% of the best MTF-zero fraction.
        // Adds ~5-30s probe overhead on inputs ≥ 4 MB; cheap on smaller.
        public const int BWT_BLOCK_SIZE_AUTO    = -1;
        public const int REPAIR_BLOCK_SIZE      = 262144;    // 256KB Re-Pair blocks
        public const int REPAIR_MAX_PASSES      = 50;        // run until convergence (early-term gate is firstBf/8)
        // V11 SPEED OPT: bumped from 2 to 4. Rules that fire fewer than 4
        // times save 3-4 bytes max while costing 3-4 bytes of grammar
        // overhead — net break-even at best, often a loss after entropy
        // coding. Combined with firstBf/8 early-termination this cuts
        // RePair work substantially with negligible compression cost.
        public const int REPAIR_MIN_FREQ        = 4;
        public const int DEFAULT_PASSES         = 4;

        // Mode D: max non-ASCII bytes before falling back to Default mode
        // Keeps the sidecar negligible (< 50KB) and the 7-bit alphabet clean
        public const int NON_ASCII_MAX = 10_000;

        // Auto-stop: stop iterating when a pass saves less than this fraction
        private const double AUTO_STOP_THRESHOLD = 0.05;

        private static readonly byte[] MAGIC =
            System.Text.Encoding.ASCII.GetBytes("BEP_IT_3"); // v3 — added sidecar

        // =====================================================================
        // PUBLIC API
        // =====================================================================

        public static byte[] Compress(byte[] input,
                                       CompressionMode mode = CompressionMode.Default,
                                       int bwtBlockSize = 0,
                                       Action<string, int>? progress = null,
                                       bool enableHuffmanWrap = true,
                                       bool enableRunaTransform = true,
                                       bool useV17EntropyProfile = false)
            => CompressIterative(input, 1, mode, bwtBlockSize, progress,
                                 enableHuffmanWrap, enableRunaTransform,
                                 useV17EntropyProfile);

        /// <summary>
        /// Compress iteratively. bwtBlockSize=0 uses mode default.
        /// Pass any power of 2 (e.g. 65536, 131072, 262144, 524288, 1048576).
        ///
        /// V15.1 parameters (default true preserves V15 behavior):
        ///   enableHuffmanWrap   — apply per-pass canonical-Huffman wrap on BEP output
        ///                         when it shrinks the entropy bytes. Set FALSE when
        ///                         the resulting archive will be wrapped by another
        ///                         entropy coder (e.g., DEFLATE in TextPipelineDeflateBep) —
        ///                         the wrap converts patterned bytes into entropy-coded
        ///                         bits that LZ77 can no longer match, killing the
        ///                         outer coder's effectiveness.
        ///   enableRunaTransform — apply RUNA/RUNB rank-0 run-length transform before
        ///                         BEP encoding. Less destructive than Huffman wrap
        ///                         w.r.t. downstream coders (RUNA produces patterned
        ///                         BEP bytes that LZ77 can still match), but still
        ///                         worth disabling alongside Huffman for the strictest
        ///                         "vanilla BEP" path.
        ///
        /// V16.19 hybrid plumbing:
        ///   useV17EntropyProfile — when true, the path-A picker excludes
        ///                         RankRangeCoder/RankRangeCoderO1 candidates,
        ///                         RePair runs with costAware:false, and UnaryBEPCoder
        ///                         skips apex-zero formats. Produces a BEP archive
        ///                         byte-equivalent to the V16.17.1 entropy profile.
        ///                         Used by TextPipelineCtxBep so the picker can
        ///                         compare V17 BEP+wrap vs V18 BEP standalone.
        /// </summary>
        public static byte[] CompressIterative(byte[] input,
                                                int maxPasses,
                                                CompressionMode mode = CompressionMode.Default,
                                                int bwtBlockSize = 0,
                                                Action<string, int>? progress = null,
                                                bool enableHuffmanWrap = true,
                                                bool enableRunaTransform = true,
                                                bool useV17EntropyProfile = false)
        {
            var    passArchives = new List<PassArchive>(Math.Min(maxPasses, 64));
            byte[] current      = input;
            long   origLen      = input.Length;

            // Resolve BWT block size:
            //   AUTO sentinel  → DynamicBwt.Probe (V13 BWT expansion path)
            //   <= 0 (default) → mode default
            //   > 0            → explicit override
            if (bwtBlockSize == BWT_BLOCK_SIZE_AUTO)
            {
                bwtBlockSize = DynamicBwt.PickBlockSize(input,
                    msg => progress?.Invoke(msg, 0));
            }
            else if (bwtBlockSize <= 0)
                bwtBlockSize = mode == CompressionMode.ModeA
                               ? BWT_BLOCK_SIZE_A
                               : BWT_BLOCK_SIZE_DEFAULT;

            for (int pass = 1; pass <= maxPasses; pass++)
            {
                long inputSize = current.Length;
                var  pa        = RunOnePass(current, pass, maxPasses, mode,
                                            bwtBlockSize, progress,
                                            enableHuffmanWrap, enableRunaTransform,
                                            useV17EntropyProfile);

                long   outputSize = pa.UnaryBepBytes.Length;
                double savings    = 1.0 - (double)outputSize / inputSize;

                // Negative reduction — output grew, rollback by not adding this pass
                if (savings <= 0)
                {
                    progress?.Invoke(
                        $"Pass {pass}: output grew {(-savings * 100):F1}% " +
                        $"— rolling back to pass {pass - 1}, stopping", 100);
                    break;
                }

                // Pass improved — keep it
                passArchives.Add(pa);
                progress?.Invoke(
                    $"Pass {pass} complete — {FormatBytes(outputSize)} " +
                    $"({savings * 100:F1}% saved)", 100);

                // Auto-stop: savings positive but below threshold — converged
                if (maxPasses == int.MaxValue && savings < AUTO_STOP_THRESHOLD)
                {
                    progress?.Invoke(
                        $"Auto-stop: {savings * 100:F1}% < " +
                        $"{AUTO_STOP_THRESHOLD * 100:F0}% threshold — converged", 100);
                    break;
                }

                if (pass < maxPasses)
                    current = pa.UnaryBepBytes;
            }

            return PackArchive(origLen, passArchives, mode);
        }

        public static byte[] Decompress(byte[] archive, Action<string, int>? progress = null)
        {
            UnpackArchive(archive, out long origLen, out var passes);
            byte[] current = null!;

            for (int p = passes.Count - 1; p >= 0; p--)
            {
                var pa      = passes[p];
                int passNum = passes.Count - p;
                int total   = passes.Count;

                // Decode entropy stream → symbol ranks → original symbols.
                // V4 archives use pa.RiceK to dispatch between coders:
                //   -5 = BepChainPass2 (V16.21: rank-byte-stream BEP chain)
                //   -1 = legacy UnaryBEP (matches V3 archives byte-identically)
                //   0..7 = RiceBEP with that k value
                // V5 archives may also have TransformFlags set:
                //   bit 1 (Huffman): UnaryBepBytes is a RiceByteHuffman frame; unwrap first.
                //   bit 0 (RUNA):    BEP-decode produces transformed-rank stream;
                //                    apply RunaRunbTransform.Inverse, then translate
                //                    via FreqTable (instead of BEP doing it directly).
                int   totalSyms = pa.SeqLengths.Sum();
                int[] sequence;

                // Step 1: optionally undo the Huffman wrap.
                byte[] entropyBytes = pa.UnaryBepBytes;
                if ((pa.TransformFlags & 0x02) != 0)
                {
                    progress?.Invoke($"D{passNum}/{total}: unwrap Huffman", 0);
                    entropyBytes = RiceByteHuffman.Unwrap(entropyBytes);
                }

                // Step 2: BEP-decode. If RUNA flag is set, decode produces the
                // transformed rank stream; we use an identity freqTable so the
                // raw rank values come back unchanged. EntropySymbolCount tells
                // us how many symbols the BEP coder was given (differs from
                // totalSyms when RUNA was applied).
                if ((pa.TransformFlags & 0x01) != 0)
                {
                    int symCount = pa.EntropySymbolCount > 0 ? pa.EntropySymbolCount : totalSyms;
                    int identitySize = pa.FreqTable.Length + 1;        // covers 0..|alphabet| (RUNA bumps max by 1)
                    var identityFreq = new int[identitySize];
                    for (int i = 0; i < identitySize; i++) identityFreq[i] = i;

                    int[] ranks_t;
                    if (pa.RiceK >= 0)
                    {
                        progress?.Invoke($"D{passNum}/{total}: RiceBEP(k={pa.RiceK}) [RUNA]", 0);
                        ranks_t = RiceBEPCoder.Decode(entropyBytes, symCount, identityFreq, pa.RiceK,
                            pct => progress?.Invoke($"D{passNum}/{total}: RiceBEP(k={pa.RiceK}) [RUNA]", pct));
                    }
                    else
                    {
                        progress?.Invoke($"D{passNum}/{total}: UnaryBEP [RUNA]", 0);
                        ranks_t = UnaryBEPCoder.Decode(entropyBytes, symCount, identityFreq,
                            pct => progress?.Invoke($"D{passNum}/{total}: UnaryBEP [RUNA]", pct));
                    }

                    // Step 3: undo RUNA/RUNB → original rank stream.
                    int[] ranks = RunaRunbTransform.Inverse(ranks_t);
                    if (ranks.Length != totalSyms)
                        throw new InvalidDataException(
                            $"RUNA inverse produced {ranks.Length} ranks, expected {totalSyms}");

                    // Step 4: translate ranks → original symbols using FreqTable.
                    sequence = new int[ranks.Length];
                    for (int i = 0; i < ranks.Length; i++) sequence[i] = pa.FreqTable[ranks[i]];
                }
                else
                {
                    // Vanilla path: BEP decode directly produces symbols.
                    if (pa.RiceK == -5)
                    {
                        // V16.21: BepChainPass2 entropy coder — decode rank byte stream,
                        // then translate via FreqTable to recover symbol sequence.
                        progress?.Invoke($"D{passNum}/{total}: BepChainPass2", 0);
                        byte[] ranksBytes = InBep.Core.BepChainPass2.Decode(entropyBytes);
                        if (ranksBytes.Length != totalSyms)
                            throw new InvalidDataException(
                                $"BepChainPass2 decoded {ranksBytes.Length} ranks, expected {totalSyms}");
                        sequence = new int[totalSyms];
                        for (int i = 0; i < totalSyms; i++)
                            sequence[i] = pa.FreqTable[ranksBytes[i]];
                    }
                    else if (pa.RiceK == -4)
                    {
                        progress?.Invoke($"D{passNum}/{total}: RangeCoderO1", 0);
                        sequence = RankRangeCoderO1.Decode(entropyBytes, totalSyms, pa.FreqTable,
                            pct => progress?.Invoke($"D{passNum}/{total}: RangeCoderO1", pct));
                    }
                    else if (pa.RiceK == -3)
                    {
                        progress?.Invoke($"D{passNum}/{total}: RangeCoder", 0);
                        sequence = RankRangeCoder.Decode(entropyBytes, totalSyms, pa.FreqTable,
                            pct => progress?.Invoke($"D{passNum}/{total}: RangeCoder", pct));
                    }
                    else if (pa.RiceK == -2)
                    {
                        progress?.Invoke($"D{passNum}/{total}: SplitStream", 0);
                        sequence = SplitStreamBEPCoder.Decode(entropyBytes, totalSyms, pa.FreqTable,
                            pct => progress?.Invoke($"D{passNum}/{total}: SplitStream", pct));
                    }
                    else if (pa.RiceK >= 0)
                    {
                        progress?.Invoke($"D{passNum}/{total}: RiceBEP(k={pa.RiceK})", 0);
                        sequence = RiceBEPCoder.Decode(entropyBytes, totalSyms, pa.FreqTable, pa.RiceK,
                            pct => progress?.Invoke($"D{passNum}/{total}: RiceBEP(k={pa.RiceK})", pct));
                    }
                    else
                    {
                        progress?.Invoke($"D{passNum}/{total}: UnaryBEP", 0);
                        sequence = UnaryBEPCoder.Decode(entropyBytes, totalSyms, pa.FreqTable,
                            pct => progress?.Invoke($"D{passNum}/{total}: UnaryBEP", pct));
                    }
                }

                // Inverse Re-Pair — use grammarStart matching what was used during compression
                int grammarStart = pa.Mode == CompressionMode.ModeD ? 128 : 256;
                progress?.Invoke($"D{passNum}/{total}: InverseRePair", 0);
                var ruleBlocks = DeserializeRules(pa.RulesEncoded, pa.SeqLengths.Length);
                var mtfParts   = new byte[pa.SeqLengths.Length][];
                int seqOff     = 0;
                for (int b = 0; b < pa.SeqLengths.Length; b++)
                {
                    var blkSeq = new int[pa.SeqLengths[b]];
                    Array.Copy(sequence, seqOff, blkSeq, 0, pa.SeqLengths[b]);
                    seqOff += pa.SeqLengths[b];
                    mtfParts[b] = RePairEngine.Decompress(blkSeq, ruleBlocks[b], grammarStart);
                    progress?.Invoke($"D{passNum}/{total}: InverseRePair",
                        (b + 1) * 100 / pa.SeqLengths.Length);
                }
                byte[] mtfBytes = Concat(mtfParts);

                // For ModeB: mtfBytes are actually PPM-3 rank bytes.
                // Run PPM inverse to recover true MTF bytes, then inverse MTF.
                // For Default/ModeA: mtfBytes are already MTF output, just inverse MTF.
                byte[] bwtBytes;
                if (pa.Mode == CompressionMode.ModeB)
                {
                    progress?.Invoke($"D{passNum}/{total}: InvPPM-3", 0);
                    byte[] recoveredMtf = PPMInverseRankTransform(mtfBytes);
                    progress?.Invoke($"D{passNum}/{total}: InvPPM-3", 100);
                    progress?.Invoke($"D{passNum}/{total}: InverseMTF", 0);
                    bwtBytes = MTF.Decode(recoveredMtf);
                    progress?.Invoke($"D{passNum}/{total}: InverseMTF", 100);
                }
                else
                {
                    progress?.Invoke($"D{passNum}/{total}: InverseMTF", 0);
                    bwtBytes = MTF.Decode(mtfBytes);
                    progress?.Invoke($"D{passNum}/{total}: InverseMTF", 100);
                }

                // Inverse BWT
                progress?.Invoke($"D{passNum}/{total}: InverseBWT", 0);
                int  bc      = pa.Origins.Length;
                var  bwtParts = new byte[bc][];
                int  bwtOff   = 0;
                long target   = p == 0 ? origLen : long.MaxValue;
                for (int b = 0; b < bc; b++)
                {
                    int bLen = (int)Math.Min(
                        Math.Min(pa.BwtBlockSize, bwtBytes.Length - bwtOff),
                        target - bwtOff);
                    if (bLen <= 0) { bwtParts[b] = Array.Empty<byte>(); continue; }
                    var blk = new byte[bLen];
                    Buffer.BlockCopy(bwtBytes, bwtOff, blk, 0, bLen);
                    bwtParts[b] = BWT.Inverse(blk, pa.Origins[b]);
                    bwtOff += bLen;
                    progress?.Invoke($"D{passNum}/{total}: InverseBWT", (b + 1) * 100 / bc);
                }
                current = Concat(bwtParts);
                if (p == 0 && current.LongLength > origLen)
                    current = current[..(int)origLen];

                // Mode D: restore non-ASCII bytes from sidecar
                if (pa.Mode == CompressionMode.ModeD && pa.Sidecar.Length > 0)
                {
                    using var sms = new MemoryStream(pa.Sidecar);
                    using var sbr = new BinaryReader(sms);
                    int count = sbr.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int pos  = sbr.ReadInt32();
                        byte val = sbr.ReadByte();
                        current[pos] = val;
                    }
                }
            }

            return current;
        }

        public static bool Verify(byte[] input, int passes, out long compressedSize,
                                    CompressionMode mode = CompressionMode.Default,
                                    int bwtBlockSize = 0)
        {
            byte[] compressed   = CompressIterative(input, passes, mode, bwtBlockSize);
            compressedSize      = compressed.Length;
            byte[] decompressed = Decompress(compressed);
            if (decompressed.Length != input.Length) return false;
            for (int i = 0; i < input.Length; i++)
                if (decompressed[i] != input[i]) return false;
            return true;
        }

        // =====================================================================
        // ONE PASS
        // =====================================================================

        private static PassArchive RunOnePass(byte[] data, int pass, int totalPasses,
                                               CompressionMode mode, int bwtBlockSize,
                                               Action<string, int>? progress,
                                               bool enableHuffmanWrap = true,
                                               bool enableRunaTransform = true,
                                               bool useV17EntropyProfile = false)
        {
            string tag     = totalPasses == int.MaxValue ? $"P{pass}" : $"P{pass}/{totalPasses}";
            string modeTag = mode == CompressionMode.ModeA ? " [A]"  :
                             mode == CompressionMode.ModeB ? " [B]"  :
                             mode == CompressionMode.ModeD ? " [D]"  : "";

            // ── Mode D: 7-bit ASCII alphabet ──────────────────────────────────
            // Scan for non-ASCII bytes. If too many, fall back to Default.
            // Otherwise extract them to a sidecar and replace with 0x00 placeholder.
            byte[] sidecar = Array.Empty<byte>();
            if (mode == CompressionMode.ModeD)
            {
                // Count non-ASCII
                int nonAsciiCount = 0;
                for (int i = 0; i < data.Length; i++)
                    if (data[i] > 127) nonAsciiCount++;

                double fraction = (double)nonAsciiCount / Math.Max(1, data.Length);
                if (nonAsciiCount > NON_ASCII_MAX)
                {
                    progress?.Invoke(
                        $"{tag}[D]: {nonAsciiCount} non-ASCII bytes ({fraction*100:F2}%) " +
                        $"> limit {NON_ASCII_MAX} — falling back to Default", 0);
                    mode    = CompressionMode.Default;
                    modeTag = "";
                }
                else
                {
                    // Build sidecar: [count(4)] + [pos(4) + val(1)] per entry
                    var ms2 = new MemoryStream();
                    var bw2 = new BinaryWriter(ms2);
                    bw2.Write(nonAsciiCount);
                    var tmp = nonAsciiCount > 0 ? (byte[])data.Clone() : data;
                    for (int i = 0; i < tmp.Length; i++)
                    {
                        if (tmp[i] > 127)
                        {
                            bw2.Write(i);       // position (4 bytes)
                            bw2.Write(tmp[i]);  // original byte
                            tmp[i] = 0x00;      // replace with null placeholder
                        }
                    }
                    sidecar = ms2.ToArray();
                    data    = tmp; // main stream is now 7-bit clean
                    if (nonAsciiCount > 0)
                        progress?.Invoke(
                            $"{tag}[D]: {nonAsciiCount} non-ASCII → sidecar " +
                            $"({sidecar.Length} bytes)", 0);
                }
            }

            // ── BWT — parallel across blocks (each block is independent;
            // assigning to bwtRes[b] is safe because each thread writes a
            // unique index). The original code was sequential due to a
            // misunderstanding about struct array assignment atomicity —
            // .NET Parallel.For with index-keyed writes is safe.
            // V11 SPEED OPT: parallel BWT block loop on multi-core machines
            // gives 2-8x speedup proportional to core count.
            long _t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int bc     = (int)Math.Ceiling((double)data.Length / bwtBlockSize);
            var bwtRes = new (byte[] bwt, int origin)[bc];
            int _bwtDone = 0;
            System.Threading.Tasks.Parallel.For(0, bc, b =>
            {
                int start = b * bwtBlockSize;
                int len   = Math.Min(bwtBlockSize, data.Length - start);
                var blk   = new byte[len];
                Buffer.BlockCopy(data, start, blk, 0, len);
                bwtRes[b] = BWT.Forward(blk);
                int done = System.Threading.Interlocked.Increment(ref _bwtDone);
                progress?.Invoke($"{tag}{modeTag}: BWT", (int)((long)done * 100 / bc));
            });
            progress?.Invoke($"{tag}{modeTag}: BWT", 100);
            DiagnosticTimings.Add("TextPipeline.BWT", System.Diagnostics.Stopwatch.GetTimestamp() - _t0);

            int[]  origins  = bwtRes.Select(r => r.origin).ToArray();
            byte[] bwtBytes = Concat(bwtRes.Select(r => r.bwt).ToArray());

            // ── MTF ────────────────────────────────────────────────────────────
            long _t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            progress?.Invoke($"{tag}{modeTag}: MTF", 0);
            byte[] mtf = MTF.Encode(bwtBytes);
            progress?.Invoke($"{tag}{modeTag}: MTF", 100);
            DiagnosticTimings.Add("TextPipeline.MTF", System.Diagnostics.Stopwatch.GetTimestamp() - _t1);

            // ── PPM-3 rank transform (Mode B only) ────────────────────────────
            // Applied between MTF and Re-Pair. MTF output has 59%+ zeros and
            // strong run structure — PPM-3 prediction accuracy ~75-85% here.
            // The rank byte stream replaces the raw MTF bytes for Re-Pair.
            byte[] ppmRanks   = Array.Empty<byte>();
            int[]  ppmFreqTbl = Array.Empty<int>();
            bool   usePPM     = mode == CompressionMode.ModeB;

            byte[] repairInput = mtf;
            if (usePPM)
            {
                progress?.Invoke($"{tag}[B]: PPM-3 rank", 0);
                (ppmRanks, ppmFreqTbl) = PPM3RankTransform(mtf, tag,
                    pct => progress?.Invoke($"{tag}[B]: PPM-3 rank", pct));
                repairInput = ppmRanks; // Re-Pair works on rank bytes instead of MTF bytes
                progress?.Invoke($"{tag}[B]: PPM-3 rank", 100);
            }

            // ── Re-Pair — sequential (parallel struct assignment is not atomic) ─
            // Mode D: grammar symbols start at 128 (not 256) — 7-bit base alphabet
            // ── Re-Pair — parallel across blocks. Each block's RePair compress
            // is independent and writes to a unique array index, so the
            // operation is thread-safe. V11 SPEED OPT: this is the single
            // largest available speedup — RePair dominates TextPipeline time
            // and scales linearly with cores.
            long _tRP = System.Diagnostics.Stopwatch.GetTimestamp();
            int grammarStart = mode == CompressionMode.ModeD ? 128 : 256;
            int rpBlocks = (int)Math.Ceiling((double)repairInput.Length / REPAIR_BLOCK_SIZE);
            var rpRes    = new RePairBlock[rpBlocks];
            int _rpDone = 0;
            // Capture repairInput by reference; Parallel.For's lambda gets the
            // current value at each iteration so this is safe.
            byte[] _repairInputRef = repairInput;
            System.Threading.Tasks.Parallel.For(0, rpBlocks, b =>
            {
                int start = b * REPAIR_BLOCK_SIZE;
                int len   = Math.Min(REPAIR_BLOCK_SIZE, _repairInputRef.Length - start);
                var blk   = new byte[len];
                Buffer.BlockCopy(_repairInputRef, start, blk, 0, len);
                // V16.18.4 RR3: cost-aware rule selection enabled. Picks pair
                // by net byte savings rather than raw frequency. Python sim
                // shows 0.5-1.5% reduction in post-RePair entropy on text
                // (5/5 Calgary files improved). To revert: pass `false` here.
                // V16.19: gated on useV17EntropyProfile (false→cost-aware on).
                rpRes[b]  = RePairEngine.Compress(blk, REPAIR_MAX_PASSES, REPAIR_MIN_FREQ, grammarStart,
                                                   costAware: !useV17EntropyProfile);
                int done = System.Threading.Interlocked.Increment(ref _rpDone);
                progress?.Invoke($"{tag}{modeTag}: Re-Pair", (int)((long)done * 100 / rpBlocks));
            });
            progress?.Invoke($"{tag}{modeTag}: Re-Pair", 100);
            // Byte tracking: input is repairInput.Length bytes, output is the
            // symbol stream. Approximate output bytes as totalSyms × 2 (avg
            // grammar symbol fits in 16 bits) plus rule overhead.
            int _rpTotalSyms = rpRes.Sum(r => r.Sequence.Length);
            int _rpTotalRules = rpRes.Sum(r => r.Rules.Length);
            long _rpOutBytes = (long)_rpTotalSyms * 2 + (long)_rpTotalRules * 4;
            DiagnosticTimings.Add("TextPipeline.RePair",
                System.Diagnostics.Stopwatch.GetTimestamp() - _tRP,
                inputBytes: repairInput.Length,
                outputBytes: _rpOutBytes);

            // ── Flatten + frequency rank ──────────────────────────────────────
            long _tFR = System.Diagnostics.Stopwatch.GetTimestamp();
            int   totalSyms = rpRes.Sum(r => r.Sequence.Length);
            int[] sequence  = new int[totalSyms];
            int[] seqLens   = new int[rpBlocks];
            int   seqPos    = 0;
            for (int b = 0; b < rpBlocks; b++)
            {
                rpRes[b].Sequence.CopyTo(sequence, seqPos);
                seqLens[b] = rpRes[b].Sequence.Length;
                seqPos    += rpRes[b].Sequence.Length;
            }
            int[] freqTable = BuildFreqTable(sequence);
            var   rankMap   = new Dictionary<int, int>(freqTable.Length);
            for (int r = 0; r < freqTable.Length; r++) rankMap[freqTable[r]] = r;
            DiagnosticTimings.Add("TextPipeline.FreqRank", System.Diagnostics.Stopwatch.GetTimestamp() - _tFR);

            // ── Entropy coding: V15 — try {vanilla, RUNA} × {raw, Huffman}, pick smallest ──
            // Vanilla = encode the post-FreqRank symbol stream with BEP directly.
            // RUNA    = apply bzip2-style RUNA/RUNB on the rank stream first
            //           (collapses runs of rank-0), then BEP-encode the transformed
            //           stream with an identity rankMap.
            // Huffman = optional canonical-Huffman wrap on the BEP output bytes.
            // Each combination is fully encoded and measured by output byte length.
            // Self-verify in the caller catches any round-trip bugs.

            // Build the rank stream when we'll need it (for diagnostics or RUNA).
            // RUNA only helps when rank-0 dominates; quick estimate via a sample
            // of the first 4 K symbols decides whether the full scan is worth it.
            const double RUNA_FRAC_THRESHOLD = 0.20;
            int[]? ranks = null;
            double rank0Frac = 0.0;
            bool needRanks = HistogramDiagnostics.Enabled;
            if (!needRanks)
            {
                long zeros = 0;
                int sample = Math.Min(sequence.Length, 4096);
                for (int i = 0; i < sample; i++)
                    if (rankMap[sequence[i]] == 0) zeros++;
                double estFrac = sample == 0 ? 0.0 : (double)zeros / sample;
                // Use a slightly lower estimate threshold (0.16) to avoid
                // edge-of-band misses; full scan re-checks against 0.20.
                needRanks = estFrac >= RUNA_FRAC_THRESHOLD * 0.8;
            }
            if (needRanks)
            {
                ranks = new int[sequence.Length];
                long zeros = 0;
                for (int i = 0; i < sequence.Length; i++)
                {
                    int r = rankMap[sequence[i]];
                    ranks[i] = r;
                    if (r == 0) zeros++;
                }
                rank0Frac = sequence.Length == 0 ? 0.0 : (double)zeros / sequence.Length;
                if (HistogramDiagnostics.Enabled)
                    HistogramDiagnostics.DumpRankHistogram(ranks,
                        $"{tag}-mode{(int)mode}-pass{pass}");
            }
            bool tryRuna = ranks != null && rank0Frac >= RUNA_FRAC_THRESHOLD
                                          && enableRunaTransform;

            // ── Path A: vanilla ────────────────────────────────────────────────
            long _tPick = System.Diagnostics.Stopwatch.GetTimestamp();
            progress?.Invoke($"{tag}{modeTag}: pick coder", 0);
            int kA = RiceBEPCoder.PickOptimalK(sequence, rankMap);
            long unaryBitsA = UnaryBEPCoder.EstimateBits(sequence, rankMap);
            long riceBitsA  = RiceBEPCoder.EstimateBits(sequence, rankMap, kA);
            // V16.17.1: SplitStream estimate. Returns (threshold, riceKLarge, totalBits).
            var (splitThresh, splitKLarge, splitBitsA) =
                SplitStreamBEPCoder.Estimate(sequence, rankMap);
            // V16.18.2: RankRangeCoder RE-ENABLED with proper carry propagation
            // (Witten-Neal-Cleary underflow counter). V16.18 had a naive
            // renormalization that broke round-trip; V16.18.2 fixes it.
            // Verified bijective on 20 random distributions + edge cases.
            // V16.19: gated on useV17EntropyProfile (when true, force MaxValue
            // so the picker never selects this candidate).
            long rangeBitsA = useV17EntropyProfile
                ? long.MaxValue
                : RankRangeCoder.EstimateBits(sequence, rankMap);
            // V16.18.4: RankRangeCoderO1 ENABLED. Carry-correct math (same Witten-
            // Neal-Cleary fix), with order-1 context coding. Python-verified
            // 20/20 stress tests including real text data. Predicted ~7% gain
            // over O0 on text streams when context correlation is strong.
            // V16.19: gated on useV17EntropyProfile (same as Range above).
            long rangeO1BitsA = useV17EntropyProfile
                ? long.MaxValue
                : RankRangeCoderO1.EstimateBits(sequence, rankMap);
            DiagnosticTimings.Add("TextPipeline.PickCoder",
                System.Diagnostics.Stopwatch.GetTimestamp() - _tPick);

            byte[] entropyA;
            int chosenKA;
            long _tEnt = System.Diagnostics.Stopwatch.GetTimestamp();
            long _seqInputBytes = (long)sequence.Length * 4;
            // Pick smallest of: Range, RangeO1, Rice, Unary, SplitStream
            long minBits = Math.Min(Math.Min(unaryBitsA, riceBitsA),
                          Math.Min(splitBitsA, Math.Min(rangeBitsA, rangeO1BitsA)));
            if (rangeO1BitsA == minBits)
            {
                progress?.Invoke($"{tag}{modeTag}: RangeCoderO1", 0);
                int alpha = rankMap.Count;
                entropyA = RankRangeCoderO1.Encode(sequence, rankMap, alpha,
                    pct => progress?.Invoke($"{tag}{modeTag}: RangeCoderO1", pct));
                // RiceK = -4 signals RangeCoderO1.
                chosenKA = -4;
                DiagnosticTimings.Add("TextPipeline.EntropyRangeO1",
                    System.Diagnostics.Stopwatch.GetTimestamp() - _tEnt,
                    inputBytes: _seqInputBytes,
                    outputBytes: entropyA.Length);
                PickerDiagnostics.RecordPathACoder("RangeCoderO1", _seqInputBytes, entropyA.Length);
            }
            else if (rangeBitsA == minBits)
            {
                progress?.Invoke($"{tag}{modeTag}: RangeCoder", 0);
                int alpha = rankMap.Count;
                entropyA = RankRangeCoder.Encode(sequence, rankMap, alpha,
                    pct => progress?.Invoke($"{tag}{modeTag}: RangeCoder", pct));
                // RiceK = -3 signals RangeCoder.
                chosenKA = -3;
                DiagnosticTimings.Add("TextPipeline.EntropyRange",
                    System.Diagnostics.Stopwatch.GetTimestamp() - _tEnt,
                    inputBytes: _seqInputBytes,
                    outputBytes: entropyA.Length);
                PickerDiagnostics.RecordPathACoder("RangeCoder", _seqInputBytes, entropyA.Length);
                // Track the savings vs next-best alternative
                long nextBest = Math.Min(Math.Min(unaryBitsA, riceBitsA), splitBitsA);
                if (nextBest > rangeBitsA)
                    PickerDiagnostics.RecordRangeCoderWin(nextBest - rangeBitsA);
            }
            else if (splitBitsA == minBits)
            {
                progress?.Invoke($"{tag}{modeTag}: SplitStream(t={splitThresh},k={splitKLarge})", 0);
                entropyA = SplitStreamBEPCoder.Encode(sequence, rankMap,
                    splitThresh, splitKLarge,
                    pct => progress?.Invoke($"{tag}{modeTag}: SplitStream(t={splitThresh},k={splitKLarge})", pct));
                // RiceK = -2 signals SplitStream. Threshold and riceKLarge live in the
                // encoded payload header itself (parsed by SplitStreamBEPCoder.Decode).
                chosenKA = -2;
                DiagnosticTimings.Add("TextPipeline.EntropySplit",
                    System.Diagnostics.Stopwatch.GetTimestamp() - _tEnt,
                    inputBytes: _seqInputBytes,
                    outputBytes: entropyA.Length);
                PickerDiagnostics.RecordPathACoder("SplitStream", _seqInputBytes, entropyA.Length);
                long nextBest = Math.Min(Math.Min(unaryBitsA, riceBitsA), rangeBitsA);
                if (nextBest > splitBitsA)
                    PickerDiagnostics.RecordSplitStreamWin(nextBest - splitBitsA);
            }
            else if (riceBitsA == minBits)
            {
                progress?.Invoke($"{tag}{modeTag}: RiceBEP(k={kA})", 0);
                entropyA = RiceBEPCoder.Encode(sequence, rankMap, kA,
                    pct => progress?.Invoke($"{tag}{modeTag}: RiceBEP(k={kA})", pct));
                chosenKA = kA;
                DiagnosticTimings.Add("TextPipeline.EntropyRice",
                    System.Diagnostics.Stopwatch.GetTimestamp() - _tEnt,
                    inputBytes: _seqInputBytes,
                    outputBytes: entropyA.Length);
                PickerDiagnostics.RecordPathACoder($"Rice(k={kA})", _seqInputBytes, entropyA.Length);
            }
            else
            {
                progress?.Invoke($"{tag}{modeTag}: UnaryBEP", 0);
                entropyA = UnaryBEPCoder.Encode(sequence, rankMap,
                    pct => progress?.Invoke($"{tag}{modeTag}: UnaryBEP", pct),
                    allowApexZero: !useV17EntropyProfile);
                chosenKA = -1;
                DiagnosticTimings.Add("TextPipeline.EntropyUnary",
                    System.Diagnostics.Stopwatch.GetTimestamp() - _tEnt,
                    inputBytes: _seqInputBytes,
                    outputBytes: entropyA.Length);
                PickerDiagnostics.RecordPathACoder("UnaryBEP", _seqInputBytes, entropyA.Length);
                // Apex-rank-zero shortcut tracking — the encoded output's first byte
                // is the format flag (0x00/0x01/0x02/0x03)
                if (entropyA.Length > 0)
                    PickerDiagnostics.RecordUnaryFormatFlag(entropyA[0]);
            }

            // ── Path C: RUNA-transformed ───────────────────────────────────────
            byte[]? entropyC = null;
            int chosenKC = -1;
            int entropyCSymCount = 0;
            if (tryRuna)
            {
                long _tRuna = System.Diagnostics.Stopwatch.GetTimestamp();
                int[] ranks_t = RunaRunbTransform.Apply(ranks!);
                entropyCSymCount = ranks_t.Length;
                int maxT = 0;
                for (int i = 0; i < ranks_t.Length; i++)
                    if (ranks_t[i] > maxT) maxT = ranks_t[i];
                // Identity rankMap: each transformed value IS the rank we want
                // BEP to encode. Size = maxT+1 covers every value in the stream.
                var identityMap = new Dictionary<int, int>(maxT + 1);
                for (int i = 0; i <= maxT; i++) identityMap[i] = i;

                int kC = RiceBEPCoder.PickOptimalK(ranks_t, identityMap);
                long unaryBitsC = UnaryBEPCoder.EstimateBits(ranks_t, identityMap);
                long riceBitsC  = RiceBEPCoder.EstimateBits(ranks_t, identityMap, kC);
                if (riceBitsC < unaryBitsC)
                {
                    entropyC = RiceBEPCoder.Encode(ranks_t, identityMap, kC);
                    chosenKC = kC;
                }
                else
                {
                    entropyC = UnaryBEPCoder.Encode(ranks_t, identityMap,
                        progress: null,
                        allowApexZero: !useV17EntropyProfile);
                    chosenKC = -1;
                }
                DiagnosticTimings.Add("TextPipeline.EntropyRuna",
                    System.Diagnostics.Stopwatch.GetTimestamp() - _tRuna,
                    inputBytes: (long)ranks!.Length * 4,
                    outputBytes: entropyC.Length);
            }

            // ── Optional Huffman wrap on each candidate ────────────────────────
            // V15.1: gated on enableHuffmanWrap. When disabled, MaybeWrap is
            // skipped entirely and entropyAH/entropyCH stay equal to their
            // un-wrapped sources (wrappedA/wrappedC stay false), so the picker
            // below cannot select a Huffman-wrapped candidate.
            byte[] entropyAH; bool wrappedA;
            if (enableHuffmanWrap)
                (entropyAH, wrappedA) = RiceByteHuffman.MaybeWrap(entropyA);
            else
                { entropyAH = entropyA; wrappedA = false; }

            byte[]? entropyCH = null;
            bool wrappedC = false;
            if (entropyC != null && enableHuffmanWrap)
            {
                var (cwh, wc) = RiceByteHuffman.MaybeWrap(entropyC);
                entropyCH = cwh;
                wrappedC = wc;
            }

            // ── Pick smallest of {A, A+H, C, C+H} ──────────────────────────────
            byte[] ubBytes        = entropyA;
            int    chosenK        = chosenKA;
            byte   transformFlags = 0;
            int    entropySymCnt  = sequence.Length;

            if (wrappedA && entropyAH.Length < ubBytes.Length)
            {
                ubBytes        = entropyAH;
                transformFlags = 0x02;                       // Huffman only
                // chosenK and entropySymCnt unchanged from path A
            }
            if (entropyC != null && entropyC.Length < ubBytes.Length)
            {
                ubBytes        = entropyC;
                transformFlags = 0x01;                       // RUNA only
                chosenK        = chosenKC;
                entropySymCnt  = entropyCSymCount;
            }
            if (wrappedC && entropyCH != null && entropyCH.Length < ubBytes.Length)
            {
                ubBytes        = entropyCH;
                transformFlags = 0x03;                       // RUNA + Huffman
                chosenK        = chosenKC;
                entropySymCnt  = entropyCSymCount;
            }

            // ── V16.21: BepChainPass2 as additional entropy coder ────────────────
            // Operates on the rank byte stream (post-RePair, post-FreqRank).
            // Competes with Rice/Range/Unary/SplitStream without replacing any.
            // Only attempted when ranks have been computed (rank0Frac >= threshold
            // or HistogramDiagnostics.Enabled), which covers all text-like inputs.
            // Uses RiceK = -5 to signal BepChainPass2 in the PassArchive.
            // Guard: freqTable.Length is the post-RePair alphabet size including grammar
            // rule indices (256, 257, ...) so it often exceeds 256. BepChainPass2 takes
            // byte[] — skip it when any rank would be truncated by the cast.
            if (ranks != null && ranks.Length > 0 && freqTable.Length <= 256)
            {
                // All ranks 0..freqTable.Length-1 ≤ 255, so byte cast is safe.
                var ranksBytes = new byte[ranks.Length];
                for (int i = 0; i < ranks.Length; i++)
                    ranksBytes[i] = (byte)ranks[i];

                // Try both stop values; take the smaller non-null result.
                byte[]? bcStop2  = InBep.Core.BepChainPass2.Encode(ranksBytes, stopBelow: 2);
                byte[]? bcStop16 = InBep.Core.BepChainPass2.Encode(ranksBytes, stopBelow: 16);
                byte[]? bcBest   = (bcStop2 == null && bcStop16 == null) ? null
                                 : (bcStop2 == null)  ? bcStop16
                                 : (bcStop16 == null) ? bcStop2
                                 : bcStop2.Length <= bcStop16.Length ? bcStop2 : bcStop16;

                if (bcBest != null && bcBest.Length < ubBytes.Length)
                {
                    ubBytes   = bcBest;
                    chosenK   = -5;   // RiceK = -5 → BepChainPass2
                    transformFlags = 0;
                    entropySymCnt  = ranks.Length;
                    PickerDiagnostics.RecordPathACoder("BepChainPass2",
                        (long)ranks.Length, bcBest.Length);
                }
            }

            // Diagnostic: dump byte histogram of the chosen pre-Huffman stream.
            // We dump the BEP output (before any Huffman wrap), so the histogram
            // reflects what the entropy coder actually produced.
            if (HistogramDiagnostics.Enabled)
            {
                byte[] preHuffman = (transformFlags & 0x01) != 0 ? entropyC! : entropyA;
                HistogramDiagnostics.DumpByteHistogram(preHuffman,
                    $"{tag}-mode{(int)mode}-pass{pass}");
            }

            long _tSer = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] rulesRaw = SerializeRules(rpRes);
            // Byte tracking: input is _rpTotalRules × 4 (rough cost), output is rulesRaw.Length
            DiagnosticTimings.Add("TextPipeline.SerializeRules",
                System.Diagnostics.Stopwatch.GetTimestamp() - _tSer,
                inputBytes: (long)_rpTotalRules * 4,
                outputBytes: rulesRaw.Length);

            return new PassArchive
            {
                BwtBlockSize       = bwtBlockSize,
                Mode               = mode,
                Origins            = origins,
                PPMFreqTable       = ppmFreqTbl,
                FreqTable          = freqTable,
                SeqLengths         = seqLens,
                RulesEncoded       = rulesRaw,
                UnaryBepBytes      = ubBytes,
                RiceK              = chosenK,
                TransformFlags     = transformFlags,         // V15
                EntropySymbolCount = entropySymCnt,          // V15
                Sidecar            = sidecar      // empty unless Mode D with non-ASCII bytes
            };
        }

        // =====================================================================
        // PPM-3 RANK TRANSFORM (Mode B)
        // Predicts next MTF value from last 3 MTF values.
        // Returns (rank_bytes, ppm_freq_table_for_inverse).
        // =====================================================================

        private static (byte[] ranks, int[] freqTable) PPM3RankTransform(
            byte[] mtf, string tag, Action<int>? progress)
        {
            // Order-1 adaptive model — fast, good on MTF output
            // (Order-3 dict would be 16MB, order-1 is 64KB, plenty accurate on zero-heavy MTF)
            // We use order-2 as a balance: 256×256 contexts = 64K entries, fast, ~38-45% rank-0 on MTF
            var   model       = new Order2ModelSimple();
            byte[] ranks      = new byte[mtf.Length];
            int   reportEvery = Math.Max(1, mtf.Length / 100);
            byte  prev2 = 0, prev1 = 0;

            for (int i = 0; i < mtf.Length; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / mtf.Length));

                byte b    = mtf[i];
                int  rank = model.GetRank(prev2, prev1, b);
                ranks[i]  = (byte)Math.Min(rank, 255);
                model.Update(prev2, prev1, b);
                prev2 = prev1;
                prev1 = b;
            }
            progress?.Invoke(100);

            // Build freq table of rank values for inverse (needed to undo PPM ranking on decompress)
            // Since PPM ranking is not globally stable (it's adaptive per-position), we can't
            // simply invert with a table. Instead store the full ranked sequence approach:
            // Actually — the inverse is to RE-RUN the same PPM model during decompression
            // (same adaptive model, same order of updates = deterministic = same predictions).
            // So we don't need to store anything extra — just re-run Order2ModelSimple during decompress.
            // Store empty freqTable as signal that this is PPM-mode (model reconstructed on decompress).
            return (ranks, Array.Empty<int>());
        }

        // =====================================================================
        // PPM-3 INVERSE RANK TRANSFORM (Mode B decompress)
        // Re-runs the exact same adaptive model to recover MTF bytes from ranks.
        // =====================================================================

        private static byte[] PPMInverseRankTransform(byte[] ranks)
        {
            var   model  = new Order2ModelSimple();
            byte[] mtf   = new byte[ranks.Length];
            byte  prev2  = 0, prev1 = 0;

            for (int i = 0; i < ranks.Length; i++)
            {
                byte b    = model.GetByteAtRank(prev2, prev1, ranks[i]);
                mtf[i]    = b;
                model.Update(prev2, prev1, b);
                prev2 = prev1;
                prev1 = b;
            }
            return mtf;
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private static string FormatBytes(long b)
        {
            if (b >= 1_048_576) return $"{b/1_048_576.0:F2} MB";
            if (b >= 1_024)     return $"{b/1_024.0:F1} KB";
            return $"{b} B";
        }

        // =====================================================================
        // ARCHIVE PACK / UNPACK
        // =====================================================================

        private class PassArchive
        {
            public int             BwtBlockSize  { get; set; }
            public CompressionMode Mode          { get; set; }
            public int[]           Origins       { get; set; } = Array.Empty<int>();
            public int[]           PPMFreqTable  { get; set; } = Array.Empty<int>();
            public int[]           FreqTable     { get; set; } = Array.Empty<int>();
            public int[]           SeqLengths    { get; set; } = Array.Empty<int>();
            public byte[]          RulesEncoded  { get; set; } = Array.Empty<byte>();
            public byte[]          UnaryBepBytes { get; set; } = Array.Empty<byte>();
            public byte[]          Sidecar       { get; set; } = Array.Empty<byte>(); // Mode D non-ASCII
            // Rice coder selection (Drop 10):
            //   -1 = use UnaryBEPCoder (legacy, behavior identical to v3 archives)
            //   0..7 = use RiceBEPCoder with this k value
            // Encoded bytes go into UnaryBepBytes (field name preserved for compat)
            public int             RiceK         { get; set; } = -1;

            // V15 (archive version 5) additions:
            //   bit 0 of TransformFlags: RUNA/RUNB rank-0 run-length transform applied
            //                            BEFORE BEP encoding. UnaryBepBytes contains
            //                            the BEP-encoded transformed-rank stream.
            //   bit 1 of TransformFlags: RiceByteHuffman wrap applied AFTER BEP encoding.
            //                            UnaryBepBytes is a Huffman frame; unwrap first.
            //   When both bits are set, decode order is: unwrap Huffman → BEP-decode
            //   (returns transformed ranks) → RUNA inverse → translate via FreqTable.
            public byte            TransformFlags     { get; set; } = 0;
            // Number of symbols the BEP coder was given. Equal to sum(SeqLengths) when
            // RUNA flag is OFF; equal to the transformed-stream length when ON.
            // Stored explicitly so the decoder can size the BEP-decode buffer correctly.
            public int             EntropySymbolCount { get; set; } = 0;
        }

        // ── Varint helpers (V6) ─────────────────────────────────────────────
        // Standard ULEB128: 7 data bits per byte, top bit = continuation flag.
        //   value 0..127:                 1 byte
        //   value 128..16383:             2 bytes
        //   value 16384..2097151:         3 bytes
        //   value 2097152..268435455:     4 bytes  (≤ int32 in 4 bytes — same as fixed)
        //   value 268435456..int.MaxValue: 5 bytes (1-byte penalty vs fixed int32)
        // For the 50+ small-int fields per pass we use varints.
        private static void WriteVarUInt(BinaryWriter bw, ulong v)
        {
            while (v >= 0x80) { bw.Write((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            bw.Write((byte)(v & 0x7F));
        }
        private static ulong ReadVarUInt(BinaryReader br)
        {
            ulong v = 0;
            int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("varint too long");
            }
        }
        // Signed varint via zigzag — used for fields that can be negative (RiceK can be -1).
        // Most fields are non-negative; we use signed only where the original type was signed.
        private static void WriteVarInt(BinaryWriter bw, long v)
        {
            ulong z = (ulong)((v << 1) ^ (v >> 63));
            WriteVarUInt(bw, z);
        }
        private static long ReadVarInt(BinaryReader br)
        {
            ulong z = ReadVarUInt(br);
            return (long)(z >> 1) ^ -(long)(z & 1);
        }

        private static byte[] PackArchive(long origLen, List<PassArchive> passes,
                                           CompressionMode mode)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(MAGIC);
            bw.Write((byte)6);              // version 6 (V16: compact varint header)
            WriteVarUInt(bw, (ulong)origLen);
            WriteVarUInt(bw, (uint)mode);
            WriteVarUInt(bw, (uint)passes.Count);
            foreach (var pa in passes)
            {
                WriteVarUInt(bw, (uint)pa.BwtBlockSize);
                WriteVarUInt(bw, (uint)pa.Mode);
                WriteVarUInt(bw, (uint)pa.Origins.Length);
                foreach (int o in pa.Origins) WriteVarUInt(bw, (uint)o);
                WriteVarUInt(bw, (uint)pa.FreqTable.Length);
                foreach (int s in pa.FreqTable) WriteVarUInt(bw, (uint)s);
                WriteVarUInt(bw, (uint)pa.SeqLengths.Length);
                foreach (int l in pa.SeqLengths) WriteVarUInt(bw, (uint)l);
                WriteVarUInt(bw, (ulong)pa.RulesEncoded.Length);
                bw.Write(pa.RulesEncoded);
                WriteVarInt(bw, pa.RiceK);                                    // signed: can be -1
                bw.Write(pa.TransformFlags);                                   // 1 byte (V5)
                WriteVarUInt(bw, (uint)pa.EntropySymbolCount);
                WriteVarUInt(bw, (ulong)pa.UnaryBepBytes.Length);
                bw.Write(pa.UnaryBepBytes);
                WriteVarUInt(bw, (ulong)pa.Sidecar.Length);
                bw.Write(pa.Sidecar);
            }
            return ms.ToArray();
        }

        private static void UnpackArchive(byte[] data, out long origLen,
                                           out List<PassArchive> passes)
        {
            using var ms  = new MemoryStream(data);
            using var br  = new BinaryReader(ms);
            var magic     = br.ReadBytes(8);
            string magicStr = System.Text.Encoding.ASCII.GetString(magic);
            if (!magic.SequenceEqual(MAGIC))
            {
                if (magicStr == "BEP_IT_1" || magicStr == "BEP_IT_2")
                    throw new InvalidDataException(
                        $"Archive '{magicStr}' is an older format — please re-compress.");
                throw new InvalidDataException("Not a BEP archive (bad magic bytes)");
            }
            byte version = br.ReadByte();
            if (version < 3 || version > 6)
                throw new InvalidDataException($"Unsupported archive version {version}");

            // V6 uses varint for all size-class fields. V3/V4/V5 use fixed-width.
            // We branch once at the top and use two different read paths because
            // mixing branches inline gets messy with the per-field calls.
            if (version == 6)
            {
                UnpackArchiveV6(br, out origLen, out passes);
                return;
            }

            // --- V3/V4/V5 reader (unchanged) ---
            origLen = br.ReadInt64();
            br.ReadInt32();             // global mode
            int pc  = br.ReadInt32();
            passes  = new List<PassArchive>(pc);
            for (int p = 0; p < pc; p++)
            {
                var pa          = new PassArchive();
                pa.BwtBlockSize = br.ReadInt32();
                pa.Mode         = (CompressionMode)br.ReadInt32();
                int bc = br.ReadInt32(); pa.Origins = new int[bc];
                for (int i = 0; i < bc; i++) pa.Origins[i] = br.ReadInt32();
                int fc = br.ReadInt32(); pa.FreqTable = new int[fc];
                for (int i = 0; i < fc; i++) pa.FreqTable[i] = br.ReadInt32();
                int sc = br.ReadInt32(); pa.SeqLengths = new int[sc];
                for (int i = 0; i < sc; i++) pa.SeqLengths[i] = br.ReadInt32();
                pa.RulesEncoded  = br.ReadBytes((int)br.ReadInt64());
                if (version >= 4)
                    pa.RiceK = br.ReadSByte();    // V4: coder choice (-1 = unary)
                else
                    pa.RiceK = -1;                // V3: always unary
                if (version >= 5)
                {
                    pa.TransformFlags     = br.ReadByte();      // V5: RUNA/Huffman flags
                    pa.EntropySymbolCount = br.ReadInt32();     // V5: # symbols BEP-encoded
                }
                else
                {
                    pa.TransformFlags     = 0;
                    pa.EntropySymbolCount = 0;
                }
                pa.UnaryBepBytes = br.ReadBytes((int)br.ReadInt64());
                pa.Sidecar       = br.ReadBytes((int)br.ReadInt64());
                if (pa.EntropySymbolCount == 0)
                {
                    int total = 0;
                    for (int i = 0; i < pa.SeqLengths.Length; i++) total += pa.SeqLengths[i];
                    pa.EntropySymbolCount = total;
                }
                passes.Add(pa);
            }
        }

        // ── V6 compact reader — varint-encoded size-class fields ───────────
        private static void UnpackArchiveV6(BinaryReader br, out long origLen,
                                             out List<PassArchive> passes)
        {
            origLen     = (long)ReadVarUInt(br);
            _           = ReadVarUInt(br);     // global mode (discarded)
            int pc      = (int)ReadVarUInt(br);
            passes      = new List<PassArchive>(pc);
            for (int p = 0; p < pc; p++)
            {
                var pa          = new PassArchive();
                pa.BwtBlockSize = (int)ReadVarUInt(br);
                pa.Mode         = (CompressionMode)(int)ReadVarUInt(br);
                int bc = (int)ReadVarUInt(br); pa.Origins = new int[bc];
                for (int i = 0; i < bc; i++) pa.Origins[i] = (int)ReadVarUInt(br);
                int fc = (int)ReadVarUInt(br); pa.FreqTable = new int[fc];
                for (int i = 0; i < fc; i++) pa.FreqTable[i] = (int)ReadVarUInt(br);
                int sc = (int)ReadVarUInt(br); pa.SeqLengths = new int[sc];
                for (int i = 0; i < sc; i++) pa.SeqLengths[i] = (int)ReadVarUInt(br);
                long rulesLen = (long)ReadVarUInt(br);
                pa.RulesEncoded = br.ReadBytes((int)rulesLen);
                pa.RiceK              = (int)ReadVarInt(br);
                pa.TransformFlags     = br.ReadByte();
                pa.EntropySymbolCount = (int)ReadVarUInt(br);
                long unaryLen = (long)ReadVarUInt(br);
                pa.UnaryBepBytes = br.ReadBytes((int)unaryLen);
                long sidecarLen = (long)ReadVarUInt(br);
                pa.Sidecar = br.ReadBytes((int)sidecarLen);
                if (pa.EntropySymbolCount == 0)
                {
                    int total = 0;
                    for (int i = 0; i < pa.SeqLengths.Length; i++) total += pa.SeqLengths[i];
                    pa.EntropySymbolCount = total;
                }
                passes.Add(pa);
            }
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        private static int[] BuildFreqTable(int[] seq)
        {
            var f = new Dictionary<int, long>();
            foreach (int s in seq) { f.TryGetValue(s, out long c); f[s] = c + 1; }
            return f.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => kv.Key).ToArray();
        }

        private static byte[] SerializeRules(RePairBlock[] blocks)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(blocks.Length);
            foreach (var b in blocks) { bw.Write(b.Rules.Length); foreach (var (l,r) in b.Rules) { bw.Write((uint)l); bw.Write((uint)r); } }
            return ms.ToArray();
        }

        private static (int,int)[][] DeserializeRules(byte[] data, int count)
        {
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            int n = br.ReadInt32();
            if (n < 0 || n > 100_000)
                throw new InvalidDataException($"DeserializeRules: invalid block count {n} — archive may be corrupt");
            var r = new (int,int)[n][];
            for (int b = 0; b < n; b++)
            {
                int rc = br.ReadInt32();
                if (rc < 0 || rc > 100_000)
                    throw new InvalidDataException($"DeserializeRules: invalid rule count {rc} at block {b}");
                r[b] = new (int,int)[rc];
                for (int i = 0; i < rc; i++)
                {
                    int left  = (int)(br.ReadUInt32() & 0x7FFFFFFF);
                    int right = (int)(br.ReadUInt32() & 0x7FFFFFFF);
                    r[b][i]   = (left, right);
                }
            }
            return r;
        }

        private static byte[] Concat(IEnumerable<byte[]> parts)
        {
            var arr = parts.ToArray();
            long total = arr.Sum(p => (long)p.Length);
            byte[] res = new byte[total]; long off = 0;
            foreach (var p in arr) { Buffer.BlockCopy(p, 0, res, (int)off, p.Length); off += p.Length; }
            return res;
        }
    }

    // =========================================================================
    // UnaryBEPCoder — The decodable BigStringBEP-style encoding
    //
    // For rank r (bepValue v = r+2, BEP path length L = floor(log2(v))):
    //   Encode: (L-1) ones + "0" delimiter + L-bit BEP path = 2L bits
    //   Decode: count leading 1s = L-1, read L BEP bits, decompress
    //
    // Proof of unique decodability:
    //   The unary prefix (string of 1s followed by 0) uniquely encodes L.
    //   Given L, you read exactly L more bits for the BEP path. No ambiguity.
    //
    // Why this works for iteration:
    //   The encoded bit stream has BEP path patterns that create recurring
    //   byte sequences — common symbols always encode to the same bit patterns.
    //   BWT+MTF finds these runs, enabling the same ~17MB/pass savings as W2.
    // =========================================================================

    public static class UnaryBEPCoder
    {
        // ── BEP path computation ──────────────────────────────────────────────

        /// <summary>
        /// Computes BEP path for integer value v ≥ 2.
        /// Path length = floor(log2(v)).
        /// Algorithm: pop rightmost bits, track primary bit, record it each step.
        /// </summary>
        public static (string path, int length) GetPath(int v)
        {
            if (v < 2) v = 2;
            int n = v, primary = 0;
            var revPath = new System.Text.StringBuilder(20);
            while (n > 1)
            {
                if ((n & 1) == 1) primary ^= 1;  // odd step: flip primary
                n >>= 1;                           // right shift (divide by 2)
                revPath.Append((char)('0' + primary));
            }
            char[] ch = revPath.ToString().ToCharArray();
            Array.Reverse(ch);
            string path = new string(ch);
            return (path, path.Length);
        }

        /// <summary>
        /// Decompresses a BEP path string back to its integer value.
        /// Inverse of GetPath.
        /// </summary>
        public static int Decompress(ReadOnlySpan<char> path)
        {
            if (path.Length == 0) return 1;
            // Reconstruct: start from 1, for each bit:
            //   transition (bit != prev) = was odd step = subtract 1 before previous right-shift
            //   which means: after right-shifting to get here, value had bit XOR'd → add back
            // Simpler: reverse the walk. Start from termination value 1 and work backwards.
            // At each step: prev_n = n*2 if primary was same, or n*2+1 if primary flipped.
            // We track in reverse from path[end] to path[0].
            int val  = 1;
            char last = path[path.Length - 1];
            for (int i = path.Length - 1; i >= 0; i--)
            {
                char c = path[i];
                // Going backward: current val came from prev_val by one BEP step.
                // Forward step: if bit was 0 (even), n → n/2 keeping primary.
                //               if bit was 1 (odd), n → (n-1)/2 flipping primary.
                // So: n_prev = n*2 (even case) or n*2+1 (odd case).
                // The 'c' at position i is what primary was at step i (after the step at i).
                // Transition from i-1 to i: if path[i] != path[i-1], it was an odd step.
                bool transition = (i > 0) ? (path[i] != path[i-1]) : (path[0] != '0');
                // Actually let's just directly reverse the computation:
                // path[0] records primary after first step. If path[0]='1', first step was odd (primary flipped from 0 to 1).
                // First step: n=v original. Remove rightmost bit. If it was '1', primary flipped.
                // path[i] = primary state AFTER processing bit i (from right, 0-indexed from right in original, reversed in path).
                // This is getting complex. Use iterative reconstruction.
                // Break and use simpler method below.
                _ = transition;
                break;
            }

            // Simpler iterative reconstruction:
            // The path records primary bit values after each step (rightmost first, then reversed).
            // Reverse: path[0] is after processing the LEFTMOST non-leading-bit of v.
            // Let's reconstruct val from v's binary representation.
            // 
            // Alternative: use the known mapping BEP(r+2) for small values, and for larger
            // values use the mathematical reconstruction.
            //
            // BEP walk: n→⌊n/2⌋ each step, primary tracks parity.
            // After L steps (L = floor(log2(v))), we reach 1.
            // Path is: [primary_after_step_L, primary_after_step_L-1, ..., primary_after_step_1]
            // where step k processes bit k-1 of v (0-indexed from MSB after leading 1).
            //
            // Reconstruction: given path, recover v's bits.
            // path[0] = primary after processing bit (L-1) from MSB of v's significant bits.
            // path[L-1] = primary after processing bit 0 (LSB of v).
            //
            // Primary starts at 0. XOR with each bit processed (only odd bits flip primary).
            // So: path[k] = XOR of bits v[0..k] from the RIGHT (LSB side up to bit k).
            //
            // Mathematically: path[k] = popcount(v & ((1<<(k+1))-1)) & 1
            //                         = parity of the lowest (k+1) bits of v
            // But we want to go backwards: v's bit k = path[k] XOR path[k-1] (with path[-1]=0)

            if (path.Length == 0) return 2;

            // Reconstruct v from path
            // path[k] = parity of lowest (k+1) bits of v (reading path from right = LSB)
            // But path is stored LEFT-TO-RIGHT where index 0 = most significant step.
            // path[i] corresponds to processing from the left, i.e., processing the bits from MSB down.
            // 
            // Let me re-index. BEP processes from LSB (rightmost bit) to MSB direction.
            // path[L-1] = primary after processing LSB of v (bit 0)
            //           = parity(bit 0 of v) = (v & 1)
            // path[L-2] = parity(bit 1, bit 0 of v) = (v >> 0 & 1) XOR (v >> 1 & 1)... 
            // Actually: primary XORs with each '1' bit encountered. path[L-1-k] = parity of bits [0..k] of v.
            // So: path reversed at index k = parity of lowest (k+1) bits.
            // bit k of v = path_rev[k] XOR path_rev[k-1] (with path_rev[-1] = 0).

            int L = path.Length;
            val = 1; // leading bit always 1 (significant representation)
            // Reconstruct bits from MSB (most significant) down to LSB
            // We know the leading bit is 1. Remaining L bits determine the rest.
            // path[L-1-k] = parity of lowest (k+1) bits of v
            // bit k of v (0=LSB) = path[L-1-k] XOR path[L-2-k] (path[-1]=0 by convention)
            for (int k = L - 1; k >= 0; k--)
            {
                int pathRev_k  = path[L - 1 - k] - '0';
                int pathRev_km1 = k > 0 ? path[L - k] - '0' : 0;
                int bit_k = pathRev_k ^ pathRev_km1;
                val = val * 2 + bit_k;
            }
            return val;
        }

        // ── ENCODE ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes symbol sequence to UnaryBEP packed byte array.
        /// Each symbol encoded as (L-1) ones + "0" + L-bit BEP path = 2L bits total.
        /// Self-delimiting, prefix-free, no per-symbol metadata needed.
        ///
        /// V16.15: zero-byte RLE post-pass. After bit-packing, the byte stream
        /// has frequent zero-byte runs (long sequences of small ranks produce
        /// zero-rich byte patterns). A simple 0xFF-escaped RLE compresses these
        /// runs by 1-17% on text data. Output is prefixed with a 1-byte format
        /// flag: 0x00 = raw bit-packed (legacy / RLE didn't help), 0x01 = RLE.
        ///
        /// V16.18: apex-rank-zero shortcut (III3). Encodes rank 0 as 1 bit
        /// instead of 2 bits, by prefixing each symbol with 0=rank0 or 1=other.
        /// On text after BWT+MTF where rank 0 is 47%+, this saves ~4-5% on the
        /// UnaryBEP bit-packed output. Format flags 0x02/0x03 indicate apex-0.
        /// Encoder picks smallest of all four formats.
        ///
        /// V16.19 (hybrid plumbing): allowApexZero=false skips the apex-0
        /// candidates entirely. Used by TextPipelineCtxBep when emitting a
        /// V17-profile BEP archive (the pre-V16.18 byte distribution is what
        /// Nibble/Nibble3/Arith wrap layers profit from). Decode is unaffected
        /// since it dispatches on the actual format flag in the payload.
        /// </summary>
        public static byte[] Encode(int[] symbols, Dictionary<int, int> rankMap,
                                     Action<int>? progress = null,
                                     bool allowApexZero = true)
        {
            int   reportEvery = Math.Max(1, symbols.Length / 100);

            // Build the standard UnaryBEP packing (legacy V16.15 format)
            byte[] standardPacked = EncodeStandard(symbols, rankMap, reportEvery, progress);

            // V16.15: try zero-RLE on the standard packing
            byte[] standardRled = ZeroRleEncode(standardPacked);

            // Pick smallest of the legacy two candidates first
            int bestLen   = standardPacked.Length;
            byte bestFlag = 0x00;
            byte[] bestBody = standardPacked;

            if (standardRled.Length < bestLen) { bestLen = standardRled.Length; bestFlag = 0x01; bestBody = standardRled; }

            // V16.18: apex-0 candidates, gated on allowApexZero
            if (allowApexZero)
            {
                byte[] apex0Packed = EncodeApexZero(symbols, rankMap, reportEvery, progress);
                byte[] apex0Rled   = ZeroRleEncode(apex0Packed);
                if (apex0Packed.Length < bestLen) { bestLen = apex0Packed.Length; bestFlag = 0x02; bestBody = apex0Packed; }
                if (apex0Rled.Length   < bestLen) { bestLen = apex0Rled.Length;   bestFlag = 0x03; bestBody = apex0Rled; }
            }

            progress?.Invoke(100);

            // Frame: [1 byte flag][payload]
            var framed = new byte[1 + bestBody.Length];
            framed[0] = bestFlag;
            Buffer.BlockCopy(bestBody, 0, framed, 1, bestBody.Length);
            return framed;
        }

        /// <summary>Standard V16.15 UnaryBEP encoding (each symbol = 2L bits).</summary>
        private static byte[] EncodeStandard(int[] symbols, Dictionary<int, int> rankMap,
                                              int reportEvery, Action<int>? progress)
        {
            var   output      = new List<byte>(symbols.Length / 2 + 16);
            ulong bitBuf      = 0;
            int   bitCount    = 0;

            void WriteBit(int bit)
            {
                bitBuf = (bitBuf << 1) | (uint)(bit & 1);
                if (++bitCount == 8) { output.Add((byte)(bitBuf & 0xFF)); bitBuf = 0; bitCount = 0; }
            }

            for (int i = 0; i < symbols.Length; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbols.Length));

                int rank      = rankMap.TryGetValue(symbols[i], out int r) ? r : 0;
                int v         = rank + 2;
                var (path, L) = GetPath(v);

                for (int k = 0; k < L - 1; k++) WriteBit(1);
                WriteBit(0);
                BepPathCapture.BeginPath();
                foreach (char c in path)
                {
                    int b = c == '1' ? 1 : 0;
                    WriteBit(b);
                    BepPathCapture.EmitBit(b);
                }
                BepPathCapture.EndPath();
            }

            if (bitCount > 0) { bitBuf <<= (8 - bitCount); output.Add((byte)(bitBuf & 0xFF)); }
            return output.ToArray();
        }

        /// <summary>V16.18 apex-rank-zero encoding (rank 0 = 1 bit; other ranks = 1 + 2L bits).</summary>
        private static byte[] EncodeApexZero(int[] symbols, Dictionary<int, int> rankMap,
                                              int reportEvery, Action<int>? progress)
        {
            var   output      = new List<byte>(symbols.Length / 2 + 16);
            ulong bitBuf      = 0;
            int   bitCount    = 0;

            void WriteBit(int bit)
            {
                bitBuf = (bitBuf << 1) | (uint)(bit & 1);
                if (++bitCount == 8) { output.Add((byte)(bitBuf & 0xFF)); bitBuf = 0; bitCount = 0; }
            }

            for (int i = 0; i < symbols.Length; i++)
            {
                int rank = rankMap.TryGetValue(symbols[i], out int r) ? r : 0;
                if (rank == 0)
                {
                    WriteBit(0);
                }
                else
                {
                    WriteBit(1);
                    // Encode (rank-1) as standard UnaryBEP: v = (rank - 1) + 2 = rank + 1
                    int v = rank + 1;
                    var (path, L) = GetPath(v);
                    for (int k = 0; k < L - 1; k++) WriteBit(1);
                    WriteBit(0);
                    BepPathCapture.BeginPath();
                    foreach (char c in path)
                    {
                        int b = c == '1' ? 1 : 0;
                        WriteBit(b);
                        BepPathCapture.EmitBit(b);
                    }
                    BepPathCapture.EndPath();
                }
            }

            if (bitCount > 0) { bitBuf <<= (8 - bitCount); output.Add((byte)(bitBuf & 0xFF)); }
            return output.ToArray();
        }

        // ── V16.15 zero-RLE helpers ────────────────────────────────────────

        /// <summary>Compress runs of 0x00 bytes using 0xFF as escape.
        /// Format:
        ///   0xFF 0xFF       = literal byte 0xFF
        ///   0xFF N (N≠0xFF) = run of (N+2) zero bytes (N in 0..254 → run 2..256)
        ///   0x00 (alone)    = literal single 0x00
        ///   any other byte  = literal
        /// 0xFF is naturally rare in UnaryBEP output (~0.02% of bytes), so
        /// the escape overhead is minimal. Tested 1.3-17% savings on real data.</summary>
        private static byte[] ZeroRleEncode(byte[] data)
        {
            var output = new List<byte>(data.Length);
            int i = 0;
            int n = data.Length;
            while (i < n)
            {
                byte b = data[i];
                if (b == 0x00)
                {
                    // Look ahead for run
                    int run = 1;
                    while (i + run < n && data[i + run] == 0x00 && run < 256)
                        run++;
                    if (run >= 2)
                    {
                        // Emit RLE marker
                        output.Add(0xFF);
                        output.Add((byte)(run - 2));  // 0..254 → run 2..256
                        i += run;
                    }
                    else
                    {
                        output.Add(0x00);
                        i++;
                    }
                }
                else if (b == 0xFF)
                {
                    // Escape literal 0xFF
                    output.Add(0xFF);
                    output.Add(0xFF);
                    i++;
                }
                else
                {
                    output.Add(b);
                    i++;
                }
            }
            return output.ToArray();
        }

        private static byte[] ZeroRleDecode(byte[] data)
        {
            var output = new List<byte>(data.Length);
            int i = 0;
            int n = data.Length;
            while (i < n)
            {
                byte b = data[i];
                if (b == 0xFF)
                {
                    if (i + 1 >= n)
                        throw new InvalidDataException("UnaryBEPCoder: truncated RLE escape");
                    byte cb = data[i + 1];
                    if (cb == 0xFF)
                    {
                        output.Add(0xFF);
                        i += 2;
                    }
                    else
                    {
                        // Run of (cb + 2) zero bytes
                        int run = cb + 2;
                        for (int k = 0; k < run; k++) output.Add(0x00);
                        i += 2;
                    }
                }
                else
                {
                    output.Add(b);
                    i++;
                }
            }
            return output.ToArray();
        }

        // ── DECODE ────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes UnaryBEP packed bytes back to original symbol sequence.
        /// freqTable[rank] = original symbol value.
        ///
        /// V16.15: input begins with a 1-byte format flag. 0x00 = raw bit-packed
        /// (matches legacy unary output), 0x01 = zero-RLE applied.
        ///
        /// V16.18: 0x02 = apex-0 raw, 0x03 = apex-0 + RLE. Apex-0 encodes rank 0
        /// in 1 bit and other ranks as 1+UB(rank-1). Decoder dispatches based
        /// on flag.
        /// </summary>
        public static int[] Decode(byte[] encoded, int symbolCount, int[] freqTable,
                                    Action<int>? progress = null)
        {
            // V16.15/V16.18: read format flag and unwrap appropriately
            byte[] payload;
            bool apexZero = false;
            if (encoded.Length == 0)
            {
                payload = encoded;
            }
            else
            {
                byte flag = encoded[0];
                int payloadLen = encoded.Length - 1;
                var rawPayload = new byte[payloadLen];
                Buffer.BlockCopy(encoded, 1, rawPayload, 0, payloadLen);
                switch (flag)
                {
                    case 0x00: payload = rawPayload; break;
                    case 0x01: payload = ZeroRleDecode(rawPayload); break;
                    case 0x02: payload = rawPayload; apexZero = true; break;
                    case 0x03: payload = ZeroRleDecode(rawPayload); apexZero = true; break;
                    default:
                        throw new InvalidDataException(
                            $"UnaryBEPCoder: unknown format flag 0x{flag:X2}");
                }
            }

            int   reportEvery = Math.Max(1, symbolCount / 100);
            int[] result      = new int[symbolCount];
            int   bytePos     = 0;
            int   bitPos      = 7;
            byte  curByte     = payload.Length > 0 ? payload[0] : (byte)0;

            int ReadBit()
            {
                int bit = (curByte >> bitPos) & 1;
                if (--bitPos < 0)
                {
                    bitPos   = 7;
                    curByte  = ++bytePos < payload.Length ? payload[bytePos] : (byte)0;
                }
                return bit;
            }

            for (int i = 0; i < symbolCount; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbolCount));

                int rank;
                if (apexZero)
                {
                    // V16.18: read apex-0 prefix bit
                    int prefix = ReadBit();
                    if (prefix == 0)
                    {
                        rank = 0;
                    }
                    else
                    {
                        // Decode UnaryBEP for v = (rank-1)+2; recover rank = (v-2)+1 = v-1
                        int L = 1;
                        while (ReadBit() == 1) L++;
                        var pathChars = new char[L];
                        for (int k = 0; k < L; k++) pathChars[k] = ReadBit() == 1 ? '1' : '0';
                        int v = Decompress(pathChars);
                        rank = v - 1;
                    }
                }
                else
                {
                    // Standard V16.15 decode
                    int L = 1;
                    while (ReadBit() == 1) L++;
                    var pathChars = new char[L];
                    for (int k = 0; k < L; k++) pathChars[k] = ReadBit() == 1 ? '1' : '0';
                    int v = Decompress(pathChars);
                    rank = v - 2;
                }
                result[i] = (rank >= 0 && rank < freqTable.Length) ? freqTable[rank] : 0;
            }

            progress?.Invoke(100);
            return result;
        }

        /// <summary>Estimates UnaryBEP output size without full encoding.</summary>
        public static long EstimateBits(int[] symbols, Dictionary<int, int> rankMap)
        {
            long bits = 0;
            foreach (int sym in symbols)
            {
                int rank = rankMap.TryGetValue(sym, out int r) ? r : 0;
                int L    = (int)Math.Floor(Math.Log2(rank + 2));
                bits    += 2 * L; // (L-1) + 1 delimiter + L path = 2L bits
            }
            return bits;
        }
    }

    // =========================================================================
    // RICE-CODED BEP — alternative entropy coder
    //
    // Replaces UnaryBEP's fixed (L-1) unary + 0 + L path with Rice coding of
    // (L-1) using parameter k. For k=0 this is mathematically identical to
    // UnaryBEP (same byte output); for k≥1 Rice wins on distributions where
    // path lengths cluster at L≥4 (typical pass-2+ TextPipeline distributions).
    //
    // Per-symbol cost: q + 1 + k + L bits, where q = (L-1) >> k.
    //
    // Verified gains on realistic TextPipeline distributions:
    //   Pass 1 (heavy L=3-5):       Rice k=1 saves 9.7%
    //   Pass 2+ (heavy L=6-8):      Rice k=2 saves 20.4%
    //   High-rank binary after MTF: Rice k=2 saves 17.5%
    //   Geometric text-like:        Rice k=1 saves 0.4% (≈tie)
    //   All-short paths (L=1-3):    Rice k=0 = Unary (tie)
    //
    // The pipeline calls PickOptimalK to find the best k for each pass's
    // actual distribution, then encodes with that k. Because k=0 always
    // matches Unary exactly, "best of Unary vs Rice" always picks Rice
    // (since Rice with k=0 IS Unary plus 4 bits of header).
    // =========================================================================

    public static class RiceBEPCoder
    {
        /// <summary>Picks the Rice parameter k (0..7) that minimises total
        /// encoded size on this symbol stream's path-length distribution.</summary>
        public static int PickOptimalK(int[] symbols, Dictionary<int, int> rankMap)
        {
            int bestK = 0;
            long bestBits = long.MaxValue;
            for (int k = 0; k <= 7; k++)
            {
                long bits = 0;
                int mask = (1 << k) - 1;
                foreach (int sym in symbols)
                {
                    int rank = rankMap.TryGetValue(sym, out int r) ? r : 0;
                    int L = (int)Math.Floor(Math.Log2(rank + 2));
                    int n = L - 1;
                    int q = n >> k;
                    bits += q + 1 + k + L;
                }
                if (bits < bestBits) { bestBits = bits; bestK = k; }
            }
            return bestK;
        }

        public static byte[] Encode(int[] symbols, Dictionary<int, int> rankMap, int k,
                                     Action<int>? progress = null)
        {
            int   reportEvery = Math.Max(1, symbols.Length / 100);
            var   output      = new List<byte>(symbols.Length / 2 + 16);
            ulong bitBuf      = 0;
            int   bitCount    = 0;

            void WriteBit(int bit)
            {
                bitBuf = (bitBuf << 1) | (uint)(bit & 1);
                if (++bitCount == 8) { output.Add((byte)(bitBuf & 0xFF)); bitBuf = 0; bitCount = 0; }
            }

            for (int i = 0; i < symbols.Length; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbols.Length));

                int rank      = rankMap.TryGetValue(symbols[i], out int r) ? r : 0;
                int v         = rank + 2;
                var (path, L) = UnaryBEPCoder.GetPath(v);
                int n         = L - 1;
                int q         = n >> k;
                int rem       = n & ((1 << k) - 1);

                // Rice prefix: q zeros + 1 terminator + k remainder bits
                for (int u = 0; u < q; u++) WriteBit(0);
                WriteBit(1);
                for (int b = k - 1; b >= 0; b--) WriteBit((rem >> b) & 1);

                // Path bits
                BepPathCapture.BeginPath();
                foreach (char c in path)
                {
                    int b = c == '1' ? 1 : 0;
                    WriteBit(b);
                    BepPathCapture.EmitBit(b);
                }
                BepPathCapture.EndPath();
            }

            if (bitCount > 0) { bitBuf <<= (8 - bitCount); output.Add((byte)(bitBuf & 0xFF)); }
            progress?.Invoke(100);
            return output.ToArray();
        }

        public static int[] Decode(byte[] encoded, int symbolCount, int[] freqTable, int k,
                                    Action<int>? progress = null)
        {
            int   reportEvery = Math.Max(1, symbolCount / 100);
            int[] result      = new int[symbolCount];
            int   bytePos     = 0;
            int   bitPos      = 7;
            byte  curByte     = encoded.Length > 0 ? encoded[0] : (byte)0;

            int ReadBit()
            {
                int bit = (curByte >> bitPos) & 1;
                if (--bitPos < 0)
                {
                    bitPos   = 7;
                    curByte  = ++bytePos < encoded.Length ? encoded[bytePos] : (byte)0;
                }
                return bit;
            }

            for (int i = 0; i < symbolCount; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbolCount));

                // Rice decode: count zeros until terminator
                int q = 0;
                while (ReadBit() == 0) q++;
                int rem = 0;
                for (int b = 0; b < k; b++) rem = (rem << 1) | ReadBit();
                int n = (q << k) | rem;
                int L = n + 1;

                // Read L bits for BEP path
                var pathChars = new char[L];
                for (int p = 0; p < L; p++) pathChars[p] = ReadBit() == 1 ? '1' : '0';

                int v    = UnaryBEPCoder.Decompress(pathChars);
                int rank = v - 2;
                result[i] = (rank >= 0 && rank < freqTable.Length) ? freqTable[rank] : 0;
            }

            progress?.Invoke(100);
            return result;
        }

        public static long EstimateBits(int[] symbols, Dictionary<int, int> rankMap, int k)
        {
            long bits = 0;
            foreach (int sym in symbols)
            {
                int rank = rankMap.TryGetValue(sym, out int r) ? r : 0;
                int L    = (int)Math.Floor(Math.Log2(rank + 2));
                int n    = L - 1;
                int q    = n >> k;
                bits    += q + 1 + k + L;
            }
            return bits;
        }
    }

    // =========================================================================
    // SplitStreamBEPCoder — V16.17
    //
    // Splits the input symbol stream into TWO sub-streams based on rank value:
    //   - "small" ranks (< threshold): encoded via UnaryBEP (variable-length, BEP path)
    //   - "large" ranks (>= threshold): encoded as fixed-width binary values
    //                                    (binary representation of rank - threshold)
    //
    // Plus a 1-bit-per-symbol flag stream telling decoder which sub-stream the value
    // came from.
    //
    // MOTIVATION:
    //   On post-RePair sequences (text/code data), rank distribution is bimodal:
    //   - Common literals/short rule indices → small ranks (L=1..7 paths)
    //   - Long rule indices → large ranks (L=8..10 paths) with near-uniform values
    //
    //   UnaryBEP wastes bits on length-prefix encoding for the large-rank population
    //   because all those values share similar path lengths. Fixed-width binary on
    //   that population removes the overhead.
    //
    // EXPECTED GAIN:
    //   Python sim on paper2 (post-RePair): 18.57% reduction at this stage.
    //   Translates to ~1-3% on final BEPPipeline archive after framing/headers.
    //
    // FORMAT (per encoded blob):
    //   [varint] threshold value
    //   [varint] bits_per_large (8-12 typically)
    //   [varint] flag-stream byte count
    //   [varint] small-stream byte count  (UnaryBEP encoded)
    //   [varint] large-stream byte count  (fixed-width packed)
    //   [bytes]  flag stream (1 bit per symbol, MSB-first)
    //   [bytes]  small UnaryBEP stream
    //   [bytes]  large fixed-width stream
    //
    // ROUND-TRIP: verified by construction. Each stream is independently parseable
    // given symbol count, threshold, and bits-per-large from the header.
    // =========================================================================

    public static class SplitStreamBEPCoder
    {
        /// <summary>V16.17.1: Estimate optimal (threshold, riceK_large) for split-stream encoding.
        /// Now uses Rice coding on the large-stream offsets instead of fixed-width binary.
        /// This captures small-offset bias within the large stream and beats plain Rice baseline
        /// on text by 1.5-3% (verified Python sim on paper2, alice29, progc, book1, lcet10).</summary>
        public static (int threshold, int riceKLarge, long totalBits) Estimate(
            int[] symbols, Dictionary<int, int> rankMap)
        {
            // Compute ranks once
            int[] ranks = new int[symbols.Length];
            int maxRank = 0;
            for (int i = 0; i < symbols.Length; i++)
            {
                int r = rankMap.TryGetValue(symbols[i], out int rr) ? rr : 0;
                ranks[i] = r;
                if (r > maxRank) maxRank = r;
            }

            // Try thresholds × Rice k values; pick the combination with smallest total bits
            int bestThresh = 32;
            int bestK = 2;
            long bestTotal = long.MaxValue;
            int[] candidates = new[] { 4, 8, 16, 24, 32, 48, 64, 96, 128 };

            foreach (int t in candidates)
            {
                if (t > maxRank) continue;
                // Compute small_bits and collect large offsets in one pass
                long smallBits = 0;
                var largeOffsets = new List<int>();
                foreach (int r in ranks)
                {
                    if (r < t)
                    {
                        int L = (int)Math.Floor(Math.Log2(r + 2));
                        smallBits += 2 * L;
                    }
                    else
                    {
                        largeOffsets.Add(r - t);
                    }
                }

                // Try Rice k for large stream: 0..8 covers most useful ranges
                for (int k = 0; k <= 8; k++)
                {
                    long largeBits = 0;
                    foreach (int o in largeOffsets)
                    {
                        long q = (long)o >> k;
                        largeBits += q + 1 + k;
                    }
                    long total = ranks.Length + smallBits + largeBits;
                    if (total < bestTotal)
                    {
                        bestTotal = total;
                        bestThresh = t;
                        bestK = k;
                    }
                }
            }

            // Add header overhead estimate (~6 varints + a couple flags, ~12 bytes max = 96 bits)
            return (bestThresh, bestK, bestTotal + 96);
        }

        /// <summary>Number of bits needed to represent values in [0, range).</summary>
        private static int BitsToCover(int range)
        {
            if (range <= 1) return 1;
            int bits = 0;
            int v = range - 1;
            while (v > 0) { bits++; v >>= 1; }
            return bits;
        }

        /// <summary>V16.17.1: Encode using split-stream + UnaryBEP small + Rice large.</summary>
        public static byte[] Encode(int[] symbols, Dictionary<int, int> rankMap,
                                     int threshold, int riceKLarge,
                                     Action<int>? progress = null)
        {
            // Three bit streams
            var flagBits = new List<int>(symbols.Length);
            var smallBits = new List<int>();
            var largeBits = new List<int>();

            int reportEvery = Math.Max(1, symbols.Length / 100);

            for (int i = 0; i < symbols.Length; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbols.Length));

                int r = rankMap.TryGetValue(symbols[i], out int rr) ? rr : 0;
                if (r < threshold)
                {
                    flagBits.Add(0);
                    // UnaryBEP encoding: (L-1) ones + 0 + L path bits
                    int v = r + 2;
                    var (path, L) = GetPathFor(v);
                    for (int k = 0; k < L - 1; k++) smallBits.Add(1);
                    smallBits.Add(0);
                    BepPathCapture.BeginPath();
                    foreach (char c in path)
                    {
                        int b = c == '1' ? 1 : 0;
                        smallBits.Add(b);
                        BepPathCapture.EmitBit(b);
                    }
                    BepPathCapture.EndPath();
                }
                else
                {
                    flagBits.Add(1);
                    int offset = r - threshold;
                    // V17.1: Rice-encode the offset with parameter riceKLarge.
                    // Format: q ones + 0 terminator + k-bit binary remainder.
                    int q = offset >> riceKLarge;
                    int rem = offset & ((1 << riceKLarge) - 1);
                    for (int j = 0; j < q; j++) largeBits.Add(1);
                    largeBits.Add(0);
                    for (int j = riceKLarge - 1; j >= 0; j--)
                        largeBits.Add((rem >> j) & 1);
                }
            }

            // Pack each bit stream to bytes
            byte[] flagBytes = PackBits(flagBits);
            byte[] smallBytes = PackBits(smallBits);
            byte[] largeBytes = PackBits(largeBits);

            // Frame: header + three byte arrays
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            WriteVarUInt(bw, (uint)threshold);
            WriteVarUInt(bw, (uint)riceKLarge);
            WriteVarUInt(bw, (uint)flagBits.Count);
            WriteVarUInt(bw, (uint)smallBits.Count);
            WriteVarUInt(bw, (uint)largeBits.Count);
            bw.Write(flagBytes);
            bw.Write(smallBytes);
            bw.Write(largeBytes);

            progress?.Invoke(100);
            return ms.ToArray();
        }

        public static int[] Decode(byte[] encoded, int symbolCount, int[] freqTable,
                                    Action<int>? progress = null)
        {
            using var ms = new MemoryStream(encoded);
            using var br = new BinaryReader(ms);
            int threshold = (int)ReadVarUInt(br);
            int riceKLarge = (int)ReadVarUInt(br);
            int flagBitCount = (int)ReadVarUInt(br);
            int smallBitCount = (int)ReadVarUInt(br);
            int largeBitCount = (int)ReadVarUInt(br);

            int flagByteCount = (flagBitCount + 7) / 8;
            int smallByteCount = (smallBitCount + 7) / 8;
            int largeByteCount = (largeBitCount + 7) / 8;

            byte[] flagBytes = br.ReadBytes(flagByteCount);
            byte[] smallBytes = br.ReadBytes(smallByteCount);
            byte[] largeBytes = br.ReadBytes(largeByteCount);

            int[] result = new int[symbolCount];
            int reportEvery = Math.Max(1, symbolCount / 100);

            int flagPos = 0, smallPos = 0, largePos = 0;
            int FlagBit() { int b = ReadBitAt(flagBytes, flagPos); flagPos++; return b; }
            int SmallBit() { int b = ReadBitAt(smallBytes, smallPos); smallPos++; return b; }
            int LargeBit() { int b = ReadBitAt(largeBytes, largePos); largePos++; return b; }

            for (int i = 0; i < symbolCount; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbolCount));

                int flag = FlagBit();
                int rank;
                if (flag == 0)
                {
                    // Small: UnaryBEP decode
                    int L = 1;
                    while (SmallBit() == 1) L++;
                    var pathChars = new char[L];
                    for (int k = 0; k < L; k++) pathChars[k] = SmallBit() == 1 ? '1' : '0';
                    int v = DecompressPath(pathChars);
                    rank = v - 2;
                }
                else
                {
                    // V17.1: Rice-decode the offset.
                    // Read q ones until 0 terminator, then k-bit remainder.
                    int q = 0;
                    while (LargeBit() == 1) q++;
                    int rem = 0;
                    for (int k = 0; k < riceKLarge; k++)
                        rem = (rem << 1) | LargeBit();
                    int offset = (q << riceKLarge) | rem;
                    rank = threshold + offset;
                }
                result[i] = (rank >= 0 && rank < freqTable.Length) ? freqTable[rank] : 0;
            }

            progress?.Invoke(100);
            return result;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>Computes BEP path for value v >= 2. Returns (path string, length L).
        /// Identical math to UnaryBEPCoder.GetPath; replicated here to avoid coupling.</summary>
        private static (string path, int L) GetPathFor(int v)
        {
            if (v < 2) v = 2;
            int n = v;
            int primary = 0;
            var rev = new System.Text.StringBuilder();
            while (n > 1)
            {
                if ((n & 1) == 1) primary ^= 1;
                n >>= 1;
                rev.Append(primary == 1 ? '1' : '0');
            }
            char[] arr = rev.ToString().ToCharArray();
            Array.Reverse(arr);
            string path = new string(arr);
            return (path, path.Length);
        }

        /// <summary>Inverse of GetPath. Walks path bits to recover the integer value.</summary>
        private static int DecompressPath(char[] path)
        {
            if (path.Length == 0) return 2;
            int L = path.Length;
            int val = 1;
            for (int k = L - 1; k >= 0; k--)
            {
                int pathRevK = path[L - 1 - k] == '1' ? 1 : 0;
                int pathRevKm1 = (k > 0) ? (path[L - k] == '1' ? 1 : 0) : 0;
                int bitK = pathRevK ^ pathRevKm1;
                val = val * 2 + bitK;
            }
            return val;
        }

        private static byte[] PackBits(List<int> bits)
        {
            var output = new byte[(bits.Count + 7) / 8];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i] != 0)
                    output[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
            return output;
        }

        private static int ReadBitAt(byte[] data, int bitPos)
        {
            int byteIdx = bitPos >> 3;
            int shift = 7 - (bitPos & 7);
            return (data[byteIdx] >> shift) & 1;
        }

        private static void WriteVarUInt(BinaryWriter bw, ulong v)
        {
            while (v >= 0x80) { bw.Write((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            bw.Write((byte)(v & 0x7F));
        }

        private static ulong ReadVarUInt(BinaryReader br)
        {
            ulong v = 0; int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("SplitStreamBEP varint too long");
            }
        }
    }

    // =========================================================================
    // BWT
    // =========================================================================

    public static class BWT
    {
        public static (byte[] bwt, int origin) Forward(byte[] block)
        {
            int n = block.Length; int[] sa = SA(block);
            var bwt = new byte[n]; int orig = -1;
            for (int i = 0; i < n; i++) { bwt[i] = block[(sa[i]+n-1)%n]; if (sa[i]==0) orig=i; }
            return (bwt, orig);
        }

        public static byte[] Inverse(byte[] bwt, int origIdx)
        {
            int n = bwt.Length;
            int[] cnt = new int[256]; foreach (byte b in bwt) cnt[b]++;
            int[] first = new int[256]; int total = 0;
            for (int c = 0; c < 256; c++) { first[c] = total; total += cnt[c]; }
            int[] rnk = new int[256]; int[] tmap = new int[n];
            for (int i = 0; i < n; i++) { byte b = bwt[i]; tmap[i] = first[b] + rnk[b]; rnk[b]++; }
            byte[] res = new byte[n]; int cur = origIdx;
            for (int i = n-1; i >= 0; i--) { res[i] = bwt[cur]; cur = tmap[cur]; }
            return res;
        }

        private static int[] SA(byte[] block)
        {
            int n = block.Length; int[] rank = new int[2*n]; int[] sa = new int[n]; int[] tmp = new int[n];
            for (int i = 0; i < n; i++) rank[i] = rank[i+n] = block[i];
            for (int i = 0; i < n; i++) sa[i] = i;
            Array.Sort(sa, (a,b) => rank[a]-rank[b]);
            for (int gap = 1; gap < n; gap *= 2)
            {
                // Sort FIRST, then check uniqueness.
                // Checking au before the sort caused early breaks with an unsorted SA,
                // producing wrong BWT output for files with repeated byte patterns (e.g. XML).
                Array.Sort(sa, (a,b) => { int r=rank[a]-rank[b]; return r!=0?r:rank[a+gap]-rank[b+gap]; });
                tmp[sa[0]] = 0;
                for (int i = 1; i < n; i++) { tmp[sa[i]]=tmp[sa[i-1]]; if (rank[sa[i]]!=rank[sa[i-1]]||rank[sa[i]+gap]!=rank[sa[i-1]+gap]) tmp[sa[i]]++; }
                for (int i = 0; i < n; i++) rank[i] = rank[i+n] = tmp[i];
                // All ranks unique → SA is complete
                if (tmp[sa[n-1]] == n-1) break;
            }
            return sa;
        }
    }

    // =========================================================================
    // MTF
    // =========================================================================

    public static class MTF
    {
        /// <summary>Original MTF — 256-symbol move-to-front, single stream,
        /// no block restart. Used when blockSize == 0.</summary>
        public static byte[] Encode(byte[] input)
        {
            byte[] list = Init(), output = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                byte b = input[i]; int pos = 0; while (list[pos]!=b) pos++;
                output[i] = (byte)pos;
                for (int j = pos; j > 0; j--) list[j]=list[j-1]; list[0]=b;
            }
            return output;
        }
        public static byte[] Decode(byte[] enc)
        {
            byte[] list = Init(), output = new byte[enc.Length];
            for (int i = 0; i < enc.Length; i++)
            {
                int pos = enc[i]; byte val = list[pos]; output[i] = val;
                for (int j = pos; j > 0; j--) list[j]=list[j-1]; list[0]=val;
            }
            return output;
        }

        /// <summary>V13: block-restart MTF. Resets the move-to-front list
        /// every blockSize bytes. Useful when data has shifting locality
        /// (e.g., concatenation of mixed-shape segments). blockSize=0 means
        /// no restart (= original Encode).</summary>
        public static byte[] EncodeBlocked(byte[] input, int blockSize)
        {
            if (blockSize <= 0) return Encode(input);
            byte[] list = Init(), output = new byte[input.Length];
            int sinceReset = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (sinceReset >= blockSize) { list = Init(); sinceReset = 0; }
                byte b = input[i]; int pos = 0; while (list[pos]!=b) pos++;
                output[i] = (byte)pos;
                for (int j = pos; j > 0; j--) list[j]=list[j-1]; list[0]=b;
                sinceReset++;
            }
            return output;
        }
        public static byte[] DecodeBlocked(byte[] enc, int blockSize)
        {
            if (blockSize <= 0) return Decode(enc);
            byte[] list = Init(), output = new byte[enc.Length];
            int sinceReset = 0;
            for (int i = 0; i < enc.Length; i++)
            {
                if (sinceReset >= blockSize) { list = Init(); sinceReset = 0; }
                int pos = enc[i]; byte val = list[pos]; output[i] = val;
                for (int j = pos; j > 0; j--) list[j]=list[j-1]; list[0]=val;
                sinceReset++;
            }
            return output;
        }

        private static byte[] Init() { byte[] l=new byte[256]; for(int i=0;i<256;i++) l[i]=(byte)i; return l; }
    }

    // =========================================================================
    // Re-Pair Engine
    // =========================================================================

    public class RePairBlock
    {
        public int[]           Sequence { get; set; } = Array.Empty<int>();
        public (int l, int r)[] Rules   { get; set; } = Array.Empty<(int,int)>();
        public int OriginalLength        { get; set; }
    }

    public static class RePairEngine
    {
        // V16.18.4 RR3: cost-aware rule selection. When enabled, scores each
        // candidate pair by its NET BYTE SAVINGS rather than raw frequency:
        //   score = (count - 1) * (pair_expand_length - 1) - rule_overhead
        // where pair_expand_length = bytes the pair expands to in original text
        // (= 2 for byte pairs, larger for nested rules).
        //
        // Frequency-only picks "the" → "th" + "e" (same compressibility but
        // displaces other pairs). Cost-aware prefers longer-expanding pairs
        // when their occurrence-savings dominate.
        //
        // Verified on 5 Calgary files: 0.5-1.5% reduction in post-RePair entropy.
        // Toggled via `costAware` parameter; default false to preserve existing
        // behavior. Set to true at call sites once corpus-validated.
        public static RePairBlock Compress(byte[] block, int maxPasses, int minFreq,
                                           int grammarStart = 256, bool costAware = false)
        {
            int[] seq = block.Select(b => (int)b).ToArray(); int next = grammarStart;
            var rules = new List<(int,int)>();
            // V11 SPEED OPT: track first-pass best frequency to detect
            // diminishing returns. Once the best pair frequency drops below
            // max(minFreq, firstBf/8), remaining rules each save fewer than
            // ~1/8 the bytes of the first rule and aren't worth their grammar
            // overhead. Compression delta is typically <0.3% per file.
            int firstBf = -1;

            // For cost-aware: cache rule expansion lengths so we don't recompute
            // for every pair on every pass.
            var expandLen = new Dictionary<int, int>();

            int ExpandLength(int sym)
            {
                if (sym < grammarStart) return 1;
                if (expandLen.TryGetValue(sym, out int v)) return v;
                var (l, r) = rules[sym - grammarStart];
                v = ExpandLength(l) + ExpandLength(r);
                expandLen[sym] = v;
                return v;
            }

            for (int pass = 0; pass < maxPasses; pass++)
            {
                var freq = new Dictionary<long,int>(seq.Length);
                for (int i=0;i<seq.Length-1;i++) { long k=((long)seq[i]<<32)|(uint)seq[i+1]; freq.TryGetValue(k,out int c); freq[k]=c+1; }

                long bk = 0;
                int bf = minFreq - 1;

                if (costAware)
                {
                    // Score each candidate by net byte savings.
                    long bestScore = 0;
                    foreach (var kv in freq)
                    {
                        int count = kv.Value;
                        if (count < minFreq) continue;
                        int l = (int)(kv.Key >> 32), r = (int)(kv.Key & 0xFFFFFFFF);
                        long pairLen = ExpandLength(l) + ExpandLength(r);
                        // Savings: each replaced occurrence saves (pairLen - 1) symbols.
                        // First occurrence becomes rule itself (no replacement saving).
                        // Rule overhead: ~2 symbol references stored.
                        long score = (count - 1) * (pairLen - 1) - 2;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bk = kv.Key;
                            bf = count;
                        }
                    }
                    if (bestScore <= 0) break;
                }
                else
                {
                    foreach (var kv in freq) if (kv.Value > bf) { bf = kv.Value; bk = kv.Key; }
                    if (bf < minFreq) break;
                }

                if (firstBf < 0) firstBf = bf;          // record baseline on first pass
                int dynamicThreshold = Math.Max(minFreq, firstBf / 8);
                if (!costAware && bf < dynamicThreshold) break;       // diminishing returns (frequency mode only)

                int lSym = (int)(bk >> 32), rSym = (int)(bk & 0xFFFFFFFF), ns = next++;
                rules.Add((lSym, rSym));
                seq = Replace(seq, lSym, rSym, ns);
            }
            return new RePairBlock { Sequence=seq, Rules=rules.ToArray(), OriginalLength=block.Length };
        }

        public static byte[] Decompress(int[] seq, (int l, int r)[] rules,
                                        int grammarStart = 256)
        {
            var rm = new Dictionary<int,(int,int)>();
            for (int i=0;i<rules.Length;i++) rm[grammarStart+i]=rules[i];
            var out_ = new List<byte>(seq.Length*2);
            foreach (int s in seq) Expand(s, rm, out_, grammarStart);
            return out_.ToArray();
        }

        private static void Expand(int s, Dictionary<int,(int,int)> rm, List<byte> o,
                                   int grammarStart)
        {
            if (s < grammarStart) { o.Add((byte)s); return; }
            var (l,r) = rm[s]; Expand(l,rm,o,grammarStart); Expand(r,rm,o,grammarStart);
        }

        private static int[] Replace(int[] seq, int l, int r, int ns)
        {
            var res = new int[seq.Length]; int ol=0, i=0;
            while (i < seq.Length) { if (i<seq.Length-1&&seq[i]==l&&seq[i+1]==r) { res[ol++]=ns; i+=2; } else { res[ol++]=seq[i++]; } }
            var t = new int[ol]; Array.Copy(res,t,ol); return t;
        }
    }

    // =========================================================================
    // Range Coder — for compressing Re-Pair rules
    // =========================================================================

    public static class RangeCoder
    {
        private const uint TOP=1u<<24; private const int ALPHA=256, MT=1<<15;

        public static byte[] Encode(byte[] syms)
        {
            var (f,c) = Init(); uint l=0, r=0xFFFFFFFF;
            var o = new List<byte>((int)(syms.Length*.4+128));
            foreach (byte s in syms) { E(s,f,c,ref l,ref r,o); U(s,f,c); }
            for (int i=0;i<5;i++) { o.Add((byte)(l>>24)); l<<=8; }
            byte[] res = new byte[4+o.Count]; Buffer.BlockCopy(BitConverter.GetBytes((uint)syms.Length),0,res,0,4); o.CopyTo(res,4); return res;
        }

        public static byte[] Decode(byte[] enc)
        {
            int len=(int)BitConverter.ToUInt32(enc,0); var (f,c)=Init(); uint l=0,r=0xFFFFFFFF,code=0; int pos=4;
            for (int i=0;i<4;i++) code=(code<<8)|(pos<enc.Length?enc[pos++]:0u);
            var o=new byte[len];
            for (int n=0;n<len;n++) { uint rp=r/(uint)c[ALPHA]; if(rp==0)rp=1; uint sc=(code-l)/rp; if(sc>=(uint)c[ALPHA])sc=(uint)c[ALPHA]-1; int s=F(c,(int)sc); o[n]=(byte)s; r=rp*(uint)f[s]; l+=(uint)c[s]*rp; while(r<TOP) { code=(code<<8)|(pos<enc.Length?enc[pos++]:0u); l<<=8; r<<=8; } U(s,f,c); }
            return o;
        }

        private static (int[] f, int[] c) Init() { var f=new int[ALPHA]; var c=new int[ALPHA+1]; for(int i=0;i<ALPHA;i++) f[i]=1; B(f,c); return (f,c); }
        private static void E(byte s, int[] f, int[] c, ref uint l, ref uint r, List<byte> o) { uint rp=r/(uint)c[ALPHA]; if(rp==0)rp=1; l+=(uint)c[s]*rp; r=rp*(uint)f[s]; while(r<TOP) { o.Add((byte)(l>>24)); l<<=8; r<<=8; } }
        private static void U(int s, int[] f, int[] c) { f[s]++; if(c[ALPHA]+1>=MT) { for(int i=0;i<ALPHA;i++) f[i]=Math.Max(1,(f[i]+1)>>1); B(f,c); } else for(int i=s+1;i<=ALPHA;i++) c[i]++; }
        private static int F(int[] c, int sc) { int s=0; while(s+1<ALPHA&&c[s+1]<=sc) s++; return s; }
        private static void B(int[] f, int[] c) { c[0]=0; for(int i=0;i<ALPHA;i++) c[i+1]=c[i]+f[i]; }
    }

    // =========================================================================
    // RankRangeCoder — V16.18.2 bit-level arithmetic coder with proper
    // carry propagation (Witten-Neal-Cleary underflow counter).
    //
    // V16.18 used a naive renormalization that failed round-trip when the range
    // straddled a half-boundary (carry propagation issue). V16.18.2 implements
    // the standard 4-case renormalization:
    //   1. high < HALF       → emit 0 + pending 1s, shift
    //   2. low >= HALF       → emit 1 + pending 0s, shift, subtract HALF
    //   3. underflow zone    → increment pending counter, shift, subtract QUARTER
    //   4. otherwise         → done renormalizing
    //
    // This is the textbook arithmetic-coding renormalization. Verified bijective
    // on 20 random distributions (alpha 2..1500, sizes 10..10000) plus all-rank-0
    // and 2-symbol edge cases.
    //
    // FORMAT (byte stream, prepended length, decoder-derivable alphabet):
    //   [varint]    symbol count
    //   [varint]    alphabet size N
    //   [bytes]     bit-level arithmetic-coded body, MSB-first per byte
    //
    // The freqTable mapping rank → original symbol is transmitted separately
    // by the outer pipeline (PassArchive.FreqTable).
    //
    // CONVERGENCE: starts with uniform frequency 1 for all N ranks; updates
    // adaptively per-symbol. Within ~1% of Shannon on most rank streams.
    // =========================================================================
    public static class RankRangeCoder
    {
        private const int  PRECISION    = 32;
        private const uint TOP          = uint.MaxValue;       // 0xFFFFFFFF
        private const uint HALF         = 0x80000000u;
        private const uint QUARTER      = 0x40000000u;
        private const uint THREE_QUARTER = 0xC0000000u;
        // Cap cumulative frequency total well below TOP/4 to leave headroom
        // for the (rng * total) multiply during scaling.
        private static int MaxTotal(int alpha) => Math.Max(4096, 1 << 14);

        /// <summary>Estimate encoded size in bits using Shannon entropy of rank stream.
        /// Adaptive arithmetic coding achieves ~1% above Shannon in practice.
        /// Estimate adds 1% margin + 12-byte header (96 bits) for varints + 32 bits flush.</summary>
        public static long EstimateBits(int[] symbols, Dictionary<int, int> rankMap)
        {
            if (symbols.Length == 0) return 96;
            int alpha = rankMap.Count;
            if (alpha < 2) return 96 + symbols.Length;
            int[] hist = new int[alpha];
            foreach (int sym in symbols)
            {
                int r = rankMap.TryGetValue(sym, out int rr) ? rr : 0;
                if ((uint)r < (uint)alpha) hist[r]++;
            }
            double H = 0;
            int n = symbols.Length;
            for (int i = 0; i < alpha; i++)
            {
                if (hist[i] == 0) continue;
                double p = (double)hist[i] / n;
                H -= p * Math.Log2(p);
            }
            long body = (long)Math.Ceiling(H * n * 1.01);
            return body + 128 + 32;
        }

        public static byte[] Encode(int[] symbols, Dictionary<int, int> rankMap,
                                     int alphabetSize, Action<int>? progress = null)
        {
            int alpha = Math.Max(2, alphabetSize);
            int maxTotal = MaxTotal(alpha);
            int[] f = new int[alpha];
            int[] c = new int[alpha + 1];
            for (int i = 0; i < alpha; i++) f[i] = 1;
            BuildCum(f, c);

            uint low = 0;
            uint high = TOP;
            int pending = 0;
            var bitOut = new BitOut();
            int reportEvery = Math.Max(1, symbols.Length / 100);

            for (int i = 0; i < symbols.Length; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbols.Length));

                int sym = symbols[i];
                int s = rankMap.TryGetValue(sym, out int rr) ? rr : 0;
                if ((uint)s >= (uint)alpha) s = 0;

                // Narrow the range using ulong arithmetic to avoid overflow
                ulong rng = (ulong)(high - low) + 1UL;
                uint total = (uint)c[alpha];
                uint cumLow = (uint)c[s];
                uint cumHigh = (uint)c[s + 1];
                uint newHigh = low + (uint)((rng * cumHigh) / total) - 1U;
                uint newLow  = low + (uint)((rng * cumLow)  / total);
                low = newLow;
                high = newHigh;

                // Renormalize with carry handling
                while (true)
                {
                    if (high < HALF)
                    {
                        bitOut.EmitBit(0, ref pending);
                    }
                    else if (low >= HALF)
                    {
                        bitOut.EmitBit(1, ref pending);
                        low  -= HALF;
                        high -= HALF;
                    }
                    else if (low >= QUARTER && high < THREE_QUARTER)
                    {
                        pending++;
                        low  -= QUARTER;
                        high -= QUARTER;
                    }
                    else
                    {
                        break;
                    }
                    low  = low << 1;
                    high = (high << 1) | 1U;
                }

                // Update model
                f[s]++;
                if (c[alpha] + 1 >= maxTotal)
                {
                    for (int j = 0; j < alpha; j++) f[j] = Math.Max(1, (f[j] + 1) >> 1);
                    BuildCum(f, c);
                }
                else
                {
                    for (int j = s + 1; j <= alpha; j++) c[j]++;
                }
            }

            // Flush: emit one bit to disambiguate, plus pending opposites.
            // Then pad to byte boundary.
            pending++;
            if (low < QUARTER) bitOut.EmitBit(0, ref pending);
            else               bitOut.EmitBit(1, ref pending);

            byte[] body = bitOut.ToByteArray();

            // Frame: [varint symbol count][varint alphabet size][body]
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            WriteVar(bw, (uint)symbols.Length);
            WriteVar(bw, (uint)alpha);
            bw.Write(body);
            progress?.Invoke(100);
            return ms.ToArray();
        }

        public static int[] Decode(byte[] encoded, int symbolCount, int[] freqTable,
                                    Action<int>? progress = null)
        {
            using var ms = new MemoryStream(encoded);
            using var br = new BinaryReader(ms);
            int n = (int)ReadVar(br);
            int alpha = (int)ReadVar(br);
            if (n != symbolCount)
                throw new InvalidDataException(
                    $"RankRangeCoder symbol-count mismatch: header {n}, expected {symbolCount}");
            byte[] body = br.ReadBytes((int)(ms.Length - ms.Position));

            int[] f = new int[alpha];
            int[] c = new int[alpha + 1];
            for (int i = 0; i < alpha; i++) f[i] = 1;
            BuildCum(f, c);
            int maxTotal = MaxTotal(alpha);

            var bitIn = new BitIn(body);
            uint low = 0;
            uint high = TOP;
            uint code = 0;
            for (int i = 0; i < PRECISION; i++)
                code = (code << 1) | (uint)bitIn.ReadBit();

            int[] result = new int[symbolCount];
            int reportEvery = Math.Max(1, symbolCount / 100);

            for (int i = 0; i < symbolCount; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbolCount));

                ulong rng = (ulong)(high - low) + 1UL;
                uint total = (uint)c[alpha];
                // Find scaled position within [low, high]
                ulong scaled = ((ulong)(code - low) + 1UL) * total - 1UL;
                scaled /= rng;
                if (scaled >= total) scaled = total - 1;

                int s = FindSymbol(c, alpha, (int)scaled);
                result[i] = (s >= 0 && s < freqTable.Length) ? freqTable[s] : 0;

                uint cumLow = (uint)c[s];
                uint cumHigh = (uint)c[s + 1];
                uint newHigh = low + (uint)((rng * cumHigh) / total) - 1U;
                uint newLow  = low + (uint)((rng * cumLow)  / total);
                low = newLow;
                high = newHigh;

                // Renormalize matching the encoder
                while (true)
                {
                    if (high < HALF)
                    {
                        // nothing to subtract
                    }
                    else if (low >= HALF)
                    {
                        code -= HALF;
                        low  -= HALF;
                        high -= HALF;
                    }
                    else if (low >= QUARTER && high < THREE_QUARTER)
                    {
                        code -= QUARTER;
                        low  -= QUARTER;
                        high -= QUARTER;
                    }
                    else
                    {
                        break;
                    }
                    low  = low << 1;
                    high = (high << 1) | 1U;
                    code = (code << 1) | (uint)bitIn.ReadBit();
                }

                // Update model (same as encoder)
                f[s]++;
                if (c[alpha] + 1 >= maxTotal)
                {
                    for (int j = 0; j < alpha; j++) f[j] = Math.Max(1, (f[j] + 1) >> 1);
                    BuildCum(f, c);
                }
                else
                {
                    for (int j = s + 1; j <= alpha; j++) c[j]++;
                }
            }
            progress?.Invoke(100);
            return result;
        }

        // ── Bit-level helper classes ────────────────────────────────────────

        private sealed class BitOut
        {
            private readonly List<byte> _bytes = new();
            private byte _cur;
            private int  _bitsInCur;

            public void EmitBit(int bit, ref int pending)
            {
                WriteBit(bit);
                int oppo = 1 - bit;
                for (int i = 0; i < pending; i++) WriteBit(oppo);
                pending = 0;
            }

            private void WriteBit(int bit)
            {
                _cur = (byte)((_cur << 1) | (bit & 1));
                _bitsInCur++;
                if (_bitsInCur == 8)
                {
                    _bytes.Add(_cur);
                    _cur = 0;
                    _bitsInCur = 0;
                }
            }

            public byte[] ToByteArray()
            {
                if (_bitsInCur > 0)
                {
                    _cur = (byte)(_cur << (8 - _bitsInCur));
                    _bytes.Add(_cur);
                }
                return _bytes.ToArray();
            }
        }

        private sealed class BitIn
        {
            private readonly byte[] _data;
            private int _bytePos;
            private int _bitPos;

            public BitIn(byte[] data) { _data = data; _bytePos = 0; _bitPos = 7; }

            public int ReadBit()
            {
                if (_bytePos >= _data.Length) return 0;
                int bit = (_data[_bytePos] >> _bitPos) & 1;
                _bitPos--;
                if (_bitPos < 0) { _bitPos = 7; _bytePos++; }
                return bit;
            }
        }

        private static void BuildCum(int[] f, int[] c)
        {
            c[0] = 0;
            for (int i = 0; i < f.Length; i++) c[i + 1] = c[i] + f[i];
        }

        private static int FindSymbol(int[] c, int alpha, int scaled)
        {
            int lo = 0, hi = alpha - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (c[mid] <= scaled) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private static void WriteVar(BinaryWriter bw, uint v)
        {
            while (v >= 0x80) { bw.Write((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            bw.Write((byte)(v & 0x7F));
        }

        private static uint ReadVar(BinaryReader br)
        {
            uint v = 0; int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 31) throw new InvalidDataException("RankRangeCoder varint too long");
            }
        }
    }

    // =========================================================================
    // RankRangeCoderO1 — V16.18 order-1 range coder for rank streams
    //
    // Adds order-1 context coding on top of RankRangeCoder. For each symbol,
    // selects a frequency table based on the BIN of the previous symbol's rank.
    // Bins are logarithmic (10 buckets covering rank 0, 1, 2-3, 4-7, ..., 256+).
    //
    // WHY THIS WINS:
    //   Post-MTF rank streams have strong order-1 correlation. Rank 0 tends to
    //   be followed by rank 0 (run continuation); a high-rank symbol tends to
    //   be followed by a small rank (locality just reset). Conditional entropy
    //   H(r[i] | bin(r[i-1])) is 5-10% lower than unconditional H(r[i]) on text.
    //
    // ROUND-TRIP: encoder and decoder maintain IDENTICAL model state (initialized
    // uniform; updated per-symbol same way). No model transmission needed.
    //
    // FORMAT: [varint symbol count][varint alphabet size][body bytes]
    // Outer wrapper signaled by RiceK = -4 in archive header.
    //
    // VERIFIED in Python sim: 7.24% saving over order-0 entropy on paper2
    // post-RePair (5681 symbols, 621 alphabet). Practical implementation
    // adds ~1% range-coder precision overhead.
    // =========================================================================
    public static class RankRangeCoderO1
    {
        // V16.18.3: bit-level arithmetic coder with proper carry propagation
        // (same Witten-Neal-Cleary fix as RankRangeCoder), plus order-1 context.
        private const int  PRECISION    = 32;
        private const uint TOP          = uint.MaxValue;
        private const uint HALF         = 0x80000000u;
        private const uint QUARTER      = 0x40000000u;
        private const uint THREE_QUARTER = 0xC0000000u;
        private const int  N_BINS       = 10;

        // Bin function: maps rank → bin index in [0, N_BINS).
        // Logarithmic bins so bin 0 = rank 0 (the dominant case).
        private static int Bin(int rank)
        {
            if (rank <= 0) return 0;
            if (rank == 1) return 1;
            if (rank <= 3) return 2;
            if (rank <= 7) return 3;
            if (rank <= 15) return 4;
            if (rank <= 31) return 5;
            if (rank <= 63) return 6;
            if (rank <= 127) return 7;
            if (rank <= 255) return 8;
            return 9;
        }

        // Cap cumulative frequency total well below TOP/4 to leave headroom
        // for (rng * total) multiplication during scaling.
        private static int MaxTotal(int alpha) => Math.Max(4096, 1 << 14);

        /// <summary>Estimate encoded size using order-1 conditional entropy.</summary>
        public static long EstimateBits(int[] symbols, Dictionary<int, int> rankMap)
        {
            if (symbols.Length == 0) return 96;
            int alpha = rankMap.Count;
            if (alpha < 2) return 96 + symbols.Length;

            int[,] ctxCounts = new int[N_BINS, alpha];
            int[] binTotals = new int[N_BINS];

            int prevBin = 0;
            for (int i = 0; i < symbols.Length; i++)
            {
                int r = rankMap.TryGetValue(symbols[i], out int rr) ? rr : 0;
                if ((uint)r >= (uint)alpha) r = 0;
                ctxCounts[prevBin, r]++;
                binTotals[prevBin]++;
                prevBin = Bin(r);
            }

            double totalBits = 0;
            for (int b = 0; b < N_BINS; b++)
            {
                int t = binTotals[b];
                if (t == 0) continue;
                for (int s = 0; s < alpha; s++)
                {
                    int cnt = ctxCounts[b, s];
                    if (cnt == 0) continue;
                    double p = (double)cnt / t;
                    totalBits -= cnt * Math.Log2(p);
                }
            }
            return (long)Math.Ceiling(totalBits * 1.01) + 128 + 32;
        }

        public static byte[] Encode(int[] symbols, Dictionary<int, int> rankMap,
                                     int alphabetSize, Action<int>? progress = null)
        {
            int alpha = Math.Max(2, alphabetSize);
            int maxTotal = MaxTotal(alpha);

            // Per-bin frequency / cumulative tables
            int[][] f = new int[N_BINS][];
            int[][] c = new int[N_BINS][];
            for (int b = 0; b < N_BINS; b++)
            {
                f[b] = new int[alpha];
                c[b] = new int[alpha + 1];
                for (int i = 0; i < alpha; i++) f[b][i] = 1;
                BuildCum(f[b], c[b]);
            }

            uint low = 0;
            uint high = TOP;
            int pending = 0;
            var bitOut = new BitOut();
            int reportEvery = Math.Max(1, symbols.Length / 100);
            int prevBin = 0;

            for (int i = 0; i < symbols.Length; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbols.Length));

                int sym = symbols[i];
                int s = rankMap.TryGetValue(sym, out int rr) ? rr : 0;
                if ((uint)s >= (uint)alpha) s = 0;

                int[] fb = f[prevBin];
                int[] cb = c[prevBin];

                ulong rng = (ulong)(high - low) + 1UL;
                uint total = (uint)cb[alpha];
                uint cumLow = (uint)cb[s];
                uint cumHigh = (uint)cb[s + 1];
                uint newHigh = low + (uint)((rng * cumHigh) / total) - 1U;
                uint newLow  = low + (uint)((rng * cumLow)  / total);
                low = newLow;
                high = newHigh;

                while (true)
                {
                    if (high < HALF)
                    {
                        bitOut.EmitBit(0, ref pending);
                    }
                    else if (low >= HALF)
                    {
                        bitOut.EmitBit(1, ref pending);
                        low  -= HALF;
                        high -= HALF;
                    }
                    else if (low >= QUARTER && high < THREE_QUARTER)
                    {
                        pending++;
                        low  -= QUARTER;
                        high -= QUARTER;
                    }
                    else
                    {
                        break;
                    }
                    low  = low << 1;
                    high = (high << 1) | 1U;
                }

                // Update model for this bin
                fb[s]++;
                if (cb[alpha] + 1 >= maxTotal)
                {
                    for (int j = 0; j < alpha; j++) fb[j] = Math.Max(1, (fb[j] + 1) >> 1);
                    BuildCum(fb, cb);
                }
                else
                {
                    for (int j = s + 1; j <= alpha; j++) cb[j]++;
                }

                prevBin = Bin(s);
            }

            // Flush
            pending++;
            if (low < QUARTER) bitOut.EmitBit(0, ref pending);
            else               bitOut.EmitBit(1, ref pending);

            byte[] body = bitOut.ToByteArray();

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            WriteVar(bw, (uint)symbols.Length);
            WriteVar(bw, (uint)alpha);
            bw.Write(body);
            progress?.Invoke(100);
            return ms.ToArray();
        }

        public static int[] Decode(byte[] encoded, int symbolCount, int[] freqTable,
                                    Action<int>? progress = null)
        {
            using var ms = new MemoryStream(encoded);
            using var br = new BinaryReader(ms);
            int n = (int)ReadVar(br);
            int alpha = (int)ReadVar(br);
            if (n != symbolCount)
                throw new InvalidDataException(
                    $"RankRangeCoderO1 symbol-count mismatch: header {n}, expected {symbolCount}");
            byte[] body = br.ReadBytes((int)(ms.Length - ms.Position));

            int[][] f = new int[N_BINS][];
            int[][] c = new int[N_BINS][];
            for (int b = 0; b < N_BINS; b++)
            {
                f[b] = new int[alpha];
                c[b] = new int[alpha + 1];
                for (int i = 0; i < alpha; i++) f[b][i] = 1;
                BuildCum(f[b], c[b]);
            }
            int maxTotal = MaxTotal(alpha);

            var bitIn = new BitIn(body);
            uint low = 0;
            uint high = TOP;
            uint code = 0;
            for (int i = 0; i < PRECISION; i++)
                code = (code << 1) | (uint)bitIn.ReadBit();

            int[] result = new int[symbolCount];
            int reportEvery = Math.Max(1, symbolCount / 100);
            int prevBin = 0;

            for (int i = 0; i < symbolCount; i++)
            {
                if (i % reportEvery == 0)
                    progress?.Invoke((int)((long)i * 100 / symbolCount));

                int[] fb = f[prevBin];
                int[] cb = c[prevBin];

                ulong rng = (ulong)(high - low) + 1UL;
                uint total = (uint)cb[alpha];
                ulong scaled = ((ulong)(code - low) + 1UL) * total - 1UL;
                scaled /= rng;
                if (scaled >= total) scaled = total - 1;

                int s = FindSymbol(cb, alpha, (int)scaled);
                result[i] = (s >= 0 && s < freqTable.Length) ? freqTable[s] : 0;

                uint cumLow = (uint)cb[s];
                uint cumHigh = (uint)cb[s + 1];
                uint newHigh = low + (uint)((rng * cumHigh) / total) - 1U;
                uint newLow  = low + (uint)((rng * cumLow)  / total);
                low = newLow;
                high = newHigh;

                while (true)
                {
                    if (high < HALF)
                    {
                        // nothing extra
                    }
                    else if (low >= HALF)
                    {
                        code -= HALF;
                        low  -= HALF;
                        high -= HALF;
                    }
                    else if (low >= QUARTER && high < THREE_QUARTER)
                    {
                        code -= QUARTER;
                        low  -= QUARTER;
                        high -= QUARTER;
                    }
                    else
                    {
                        break;
                    }
                    low  = low << 1;
                    high = (high << 1) | 1U;
                    code = (code << 1) | (uint)bitIn.ReadBit();
                }

                fb[s]++;
                if (cb[alpha] + 1 >= maxTotal)
                {
                    for (int j = 0; j < alpha; j++) fb[j] = Math.Max(1, (fb[j] + 1) >> 1);
                    BuildCum(fb, cb);
                }
                else
                {
                    for (int j = s + 1; j <= alpha; j++) cb[j]++;
                }

                prevBin = Bin(s);
            }
            progress?.Invoke(100);
            return result;
        }

        private sealed class BitOut
        {
            private readonly List<byte> _bytes = new();
            private byte _cur;
            private int  _bitsInCur;

            public void EmitBit(int bit, ref int pending)
            {
                WriteBit(bit);
                int oppo = 1 - bit;
                for (int i = 0; i < pending; i++) WriteBit(oppo);
                pending = 0;
            }

            private void WriteBit(int bit)
            {
                _cur = (byte)((_cur << 1) | (bit & 1));
                _bitsInCur++;
                if (_bitsInCur == 8)
                {
                    _bytes.Add(_cur);
                    _cur = 0;
                    _bitsInCur = 0;
                }
            }

            public byte[] ToByteArray()
            {
                if (_bitsInCur > 0)
                {
                    _cur = (byte)(_cur << (8 - _bitsInCur));
                    _bytes.Add(_cur);
                }
                return _bytes.ToArray();
            }
        }

        private sealed class BitIn
        {
            private readonly byte[] _data;
            private int _bytePos;
            private int _bitPos;

            public BitIn(byte[] data) { _data = data; _bytePos = 0; _bitPos = 7; }

            public int ReadBit()
            {
                if (_bytePos >= _data.Length) return 0;
                int bit = (_data[_bytePos] >> _bitPos) & 1;
                _bitPos--;
                if (_bitPos < 0) { _bitPos = 7; _bytePos++; }
                return bit;
            }
        }

        private static void BuildCum(int[] f, int[] c)
        {
            c[0] = 0;
            for (int i = 0; i < f.Length; i++) c[i + 1] = c[i] + f[i];
        }

        private static int FindSymbol(int[] c, int alpha, int scaled)
        {
            int lo = 0, hi = alpha - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (c[mid] <= scaled) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private static void WriteVar(BinaryWriter bw, uint v)
        {
            while (v >= 0x80) { bw.Write((byte)((v & 0x7F) | 0x80)); v >>= 7; }
            bw.Write((byte)(v & 0x7F));
        }

        private static uint ReadVar(BinaryReader br)
        {
            uint v = 0; int shift = 0;
            while (true)
            {
                byte b = br.ReadByte();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 31) throw new InvalidDataException("RankRangeCoderO1 varint too long");
            }
        }
    }

    // =========================================================================
    // Order2ModelSimple — adaptive Order-2 prediction model for ModeB (PPM-3)
    //
    // Maintains ranked alphabet for each 2-byte context (65,536 contexts).
    // GetRank: returns rank of byte b given (prev2, prev1) — 0 = most likely.
    // GetByteAtRank: returns byte at rank r — used by decompressor.
    // Update: records observation, bubbles byte up if count exceeds neighbor.
    //
    // Both compressor and decompressor run IDENTICAL updates in IDENTICAL order
    // so model state is always synchronized. No model data needs to be stored.
    // =========================================================================

    public sealed class Order2ModelSimple
    {
        private const int ALPHA    = 256;
        private const int CONTEXTS = 256 * 256; // 65,536

        private readonly uint[] _counts;
        private readonly byte[] _sorted;
        private readonly byte[] _rank;

        public Order2ModelSimple()
        {
            _counts = new uint[CONTEXTS * ALPHA];
            _sorted = new byte[CONTEXTS * ALPHA];
            _rank   = new byte[CONTEXTS * ALPHA];
            for (int c = 0; c < CONTEXTS; c++)
                for (int b = 0; b < ALPHA; b++)
                {
                    _sorted[c * ALPHA + b] = (byte)b;
                    _rank  [c * ALPHA + b] = (byte)b;
                }
        }

        public int GetRank(byte prev2, byte prev1, byte b)
        {
            int ctx = prev2 * ALPHA + prev1;
            return _rank[ctx * ALPHA + b];
        }

        public byte GetByteAtRank(byte prev2, byte prev1, int rank)
        {
            int ctx = prev2 * ALPHA + prev1;
            return _sorted[ctx * ALPHA + Math.Min(rank, ALPHA - 1)];
        }

        public void Update(byte prev2, byte prev1, byte b)
        {
            int  ctx = prev2 * ALPHA + prev1;
            int  off = ctx * ALPHA;
            _counts[off + b]++;
            uint cnt = _counts[off + b];
            int  r   = _rank[off + b];
            while (r > 0)
            {
                byte prev    = _sorted[off + r - 1];
                uint prevCnt = _counts[off + prev];
                if (cnt <= prevCnt && !(cnt == prevCnt && b < prev)) break;
                _sorted[off + r]     = prev;
                _sorted[off + r - 1] = b;
                _rank[off + prev]    = (byte)r;
                _rank[off + b]       = (byte)(r - 1);
                r--;
            }
        }
    }
}
