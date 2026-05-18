// =============================================================================
// CliCommands — inBEP command handlers.
//
// Three commands:
//   compress    <input> [-o <output>] [options]
//   decompress  <input.bep> [-o <output>]
//   info        <input.bep>
//
// Compress options:
//   --variant <name>   Force a specific variant. Names:
//                        auto         (default) try all five, pick smallest
//                        fast         predict best from structural fingerprint
//                                     and run only that one
//                        textctx      TextPipelineCtxBep
//                        nibctx3      NibbleContextOrder3Bep
//                        protnibctx3  ProteinNibbleContextOrder3Bep
//                        arithctx     ArithmeticContextBep
//                        stridesplit  StrideSplitBep
//                        bep          raw BEPPipeline.Compress with full knobs
//
//   These flags affect only --variant bep (the raw BEPPipeline path):
//     --bwt-block <N>   BWT block size in bytes. Powers of two recommended:
//                       65536, 131072, 262144, 524288, 1048576. 0 = mode default.
//                       -1 = auto-probe (DynamicBwt picks the best block size).
//     --mode <name>     Pipeline mode: default | A | B | D
//                         default: BWT 64K + MTF + Re-Pair + FreqRank + UnaryBEP
//                         A:       BWT 512K (larger blocks, slower, better ratio)
//                         B:       default + PPM-3 rank transform pre-Re-Pair
//                         D:       7-bit ASCII alphabet (best for pure ASCII)
//     --passes <N>      Iterative passes (default 1). Auto-stop at 5% threshold.
//     --no-huffman-wrap Disable per-pass canonical-Huffman wrap on BEP output.
//     --no-runa         Disable RUNA/RUNB rank-0 run-length transform.
//
// Output report on every compress:
//   - Chosen variant (when auto/fast picks)
//   - Wall-clock encode time
//   - Peak managed allocation during encode
//   - Per-function timing breakdown (DiagnosticTimings report)
//   - Output payload size, file size including .bep header, % saved
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System.Diagnostics;
using BEPCompress.Core;

namespace InBep.Core;

public static class CliCommands
{
    // ─────────────────────────────────────────────────────────────────────
    // Variant registry — the five named BEP variants.
    //
    // The "bep" variant is NOT in this registry because it carries
    // user-tunable knobs (BWT block size, mode, etc.) and is built
    // on-demand in RunCompress from parsed CLI flags. On decompress
    // the .bep file's header tells us which variant produced the
    // payload and we dispatch through this same table.
    // ─────────────────────────────────────────────────────────────────────

    private sealed class VariantHandle
    {
        public string Name = "";
        public string Description = "";
        public Func<byte[], byte[]> Encode = null!;
        public Func<byte[], byte[]> Decode = null!;
    }

    private static readonly Dictionary<string, VariantHandle> Variants = BuildVariantMap();

    private static Dictionary<string, VariantHandle> BuildVariantMap()
    {
        var d = new Dictionary<string, VariantHandle>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string desc, Func<byte[], byte[]> enc, Func<byte[], byte[]> dec)
            => d[name] = new VariantHandle { Name = name, Description = desc, Encode = enc, Decode = dec };

        Add("textctx",     "BWT + RePair + BEP with picker over Nibble / Nibble3 / ArithCtx / BepChain / Lz77V3 / DictLz77V3 (+ stride siblings)",
            TextPipelineCtxBep.Encode, TextPipelineCtxBep.Decode);
        Add("nibctx3",     "12-bit nibble context, arithmetic-coded",
            NibbleContextOrder3Bep.Encode, NibbleContextOrder3Bep.Decode);
        Add("protnibctx3", "Protein-trained NibCtx3 (embedded amino-acid static prior)",
            ProteinNibbleContextOrder3Bep.Encode, ProteinNibbleContextOrder3Bep.Decode);
        Add("arithctx",    "Order-2 byte context, range-coded",
            ArithmeticContextBep.Encode, ArithmeticContextBep.Decode);
        Add("stridesplit", "Even/odd byte split, separate context-coded streams",
            StrideSplitBep.Encode, StrideSplitBep.Decode);

        return d;
    }

    private static readonly string[] AutoVariantOrder = new[]
    {
        "textctx", "nibctx3", "protnibctx3", "arithctx", "stridesplit"
    };

    // The "bep" variant is its own decoder regardless of the encode-time
    // knobs — BEPPipeline.Decompress reads block size / mode / flags out
    // of the pass-archive header, so a decoder needs nothing else.
    private const string BEP_VARIANT_NAME = "bep";
    private static byte[] BepDecode(byte[] payload) => BEPPipeline.Decompress(payload);

    // =========================================================================
    // compress
    // =========================================================================

    public static int RunCompress(string[] args)
    {
        if (args.Length < 2)
        {
            PrintHelp();
            return 1;
        }

        // ── Parse flags ──
        string? inputPath = null;
        string? outputPath = null;
        string variantArg = "auto";
        int bwtBlock = 0;
        CompressionMode mode = CompressionMode.Default;
        int passes = 1;
        bool enableHuffmanWrap = true;
        bool enableRunaTransform = true;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-o":
                case "--output":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for -o"); return 1; }
                    outputPath = args[i];
                    break;
                case "--variant":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --variant"); return 1; }
                    variantArg = args[i].ToLowerInvariant();
                    break;
                case "--bwt-block":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --bwt-block"); return 1; }
                    if (!TryParseBwtBlock(args[i], out bwtBlock))
                    {
                        Console.Error.WriteLine($"Invalid --bwt-block: {args[i]} (expected integer, 0, or -1/auto)");
                        return 1;
                    }
                    break;
                case "--mode":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --mode"); return 1; }
                    if (!TryParseMode(args[i], out mode))
                    {
                        Console.Error.WriteLine($"Invalid --mode: {args[i]} (expected default | A | B | D)");
                        return 1;
                    }
                    break;
                case "--passes":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --passes"); return 1; }
                    if (!int.TryParse(args[i], out passes) || passes < 1)
                    {
                        Console.Error.WriteLine($"Invalid --passes: {args[i]} (expected positive integer)");
                        return 1;
                    }
                    break;
                case "--no-huffman-wrap":
                    enableHuffmanWrap = false;
                    break;
                case "--no-runa":
                    enableRunaTransform = false;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {a}");
                        return 1;
                    }
                    if (inputPath == null) inputPath = a;
                    else { Console.Error.WriteLine($"Unexpected positional argument: {a}"); return 1; }
                    break;
            }
        }

        if (inputPath == null)
        {
            Console.Error.WriteLine("compress: missing <input> path");
            return 1;
        }
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"File not found: {inputPath}");
            return 1;
        }

        outputPath ??= inputPath + ".bep";

        // Warn if BEP-only flags were used with a non-bep variant.
        bool bepKnobsTouched = bwtBlock != 0 || mode != CompressionMode.Default
                            || passes != 1 || !enableHuffmanWrap || !enableRunaTransform;
        if (bepKnobsTouched && variantArg != BEP_VARIANT_NAME)
        {
            Console.Error.WriteLine(
                $"Note: --bwt-block / --mode / --passes / --no-huffman-wrap / --no-runa only " +
                $"apply to --variant bep. The {variantArg} variant manages those internally.");
        }

        byte[] data = File.ReadAllBytes(inputPath);

        Console.WriteLine();
        Console.WriteLine($"inBEP compress");
        Console.WriteLine($"  Input:    {inputPath} ({FmtBytes(data.Length)}, {data.Length:N0} bytes)");

        // ── Encode ──
        DiagnosticTimings.Reset();
        var totalSw = Stopwatch.StartNew();

        string chosenVariant;
        byte[] payload;
        TimeSpan encElapsed;
        long encMem;
        string? selectionReason = null;

        try
        {
            if (variantArg == BEP_VARIANT_NAME)
            {
                chosenVariant = BEP_VARIANT_NAME;
                (encElapsed, encMem, payload) = MeasureOp(() =>
                    BEPPipeline.CompressIterative(data, passes, mode, bwtBlock,
                                                  progress: null,
                                                  enableHuffmanWrap: enableHuffmanWrap,
                                                  enableRunaTransform: enableRunaTransform,
                                                  useV17EntropyProfile: false));
                selectionReason = $"explicit (mode={mode}, bwtBlock={DescribeBwtBlock(bwtBlock)}, passes={passes}, " +
                                  $"huffmanWrap={enableHuffmanWrap}, runa={enableRunaTransform})";
            }
            else if (variantArg == "auto")
            {
                (chosenVariant, payload, encElapsed, encMem, selectionReason) = EncodeAutoAll(data);
            }
            else if (variantArg == "fast")
            {
                (chosenVariant, payload, encElapsed, encMem, selectionReason) = EncodeFastPredict(data);
            }
            else
            {
                if (!Variants.TryGetValue(variantArg, out var v))
                {
                    Console.Error.WriteLine($"Unknown variant: {variantArg}");
                    Console.Error.WriteLine($"Known: auto, fast, bep, {string.Join(", ", AutoVariantOrder)}");
                    return 1;
                }
                chosenVariant = v.Name;
                (encElapsed, encMem, payload) = MeasureOp(() => v.Encode(data));
                selectionReason = "explicit";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Encode FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        totalSw.Stop();

        // ── Write archive ──
        try
        {
            BepArchive.Write(outputPath, chosenVariant, data.Length, payload);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Write FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        long fileBytes = new FileInfo(outputPath).Length;
        double savedPayload = 100.0 * (1.0 - (double)payload.Length / Math.Max(1, data.Length));
        double savedFile    = 100.0 * (1.0 - (double)fileBytes     / Math.Max(1, data.Length));

        Console.WriteLine($"  Variant:  {chosenVariant}{(selectionReason != null ? $"  ({selectionReason})" : "")}");
        Console.WriteLine($"  Payload:  {FmtBytes(payload.Length)} ({payload.Length:N0} bytes)  — saved {savedPayload:F2}% vs input");
        Console.WriteLine($"  File:     {FmtBytes(fileBytes)} ({fileBytes:N0} bytes)  — saved {savedFile:F2}% vs input (includes {fileBytes - payload.Length}-byte header)");
        Console.WriteLine($"  Time:     {FmtTime(encElapsed)} (encode), {FmtTime(totalSw.Elapsed)} (total wall)");
        Console.WriteLine($"  Memory:   {FmtBytes(encMem)} allocated during encode");
        Console.WriteLine($"  Output:   {outputPath}");

        string timingReport = DiagnosticTimings.FormatReport();
        if (!string.IsNullOrWhiteSpace(timingReport) && timingReport.Trim() != "No diagnostic timings recorded.")
        {
            Console.Write(timingReport);
        }

        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Variant selection helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Try all five named variants, keep the smallest. Returns the
    /// winning variant name, its payload, the winning variant's encode
    /// elapsed and memory delta, and a one-line reason string. Per-function
    /// timings accumulate across every variant tried.</summary>
    private static (string variant, byte[] payload, TimeSpan elapsed, long mem, string reason)
        EncodeAutoAll(byte[] data)
    {
        string bestName = "";
        byte[]? bestPayload = null;
        TimeSpan bestElapsed = TimeSpan.Zero;
        long bestMem = 0;
        long bestSize = long.MaxValue;
        var perVariant = new List<(string name, long size, TimeSpan t)>();

        foreach (var name in AutoVariantOrder)
        {
            var v = Variants[name];
            try
            {
                var (enc, mem, payload) = MeasureOp(() => v.Encode(data));
                perVariant.Add((name, payload.Length, enc));
                if (payload.Length < bestSize)
                {
                    bestSize = payload.Length;
                    bestPayload = payload;
                    bestElapsed = enc;
                    bestMem = mem;
                    bestName = name;
                }
            }
            catch (Exception ex)
            {
                perVariant.Add((name, -1, TimeSpan.Zero));
                Console.Error.WriteLine($"  (variant {name} failed: {ex.Message})");
            }
        }

        if (bestPayload == null)
            throw new InvalidOperationException("All five variants failed to encode the input.");

        // Compact per-variant ranking dump
        Console.WriteLine();
        Console.WriteLine($"  Tried all 5 variants (smallest wins):");
        foreach (var pv in perVariant.OrderBy(x => x.size < 0 ? long.MaxValue : x.size))
        {
            string mark = pv.name == bestName ? "★" : " ";
            string sizeStr = pv.size < 0 ? "ERROR" : $"{pv.size,12:N0} bytes";
            Console.WriteLine($"    {mark} {pv.name,-12}  {sizeStr}  {FmtTime(pv.t),10}");
        }

        return (bestName, bestPayload, bestElapsed, bestMem, "auto-selected by smallest output");
    }

    /// <summary>Run the structural-fingerprint predictor, encode with the
    /// predicted variant only. Cheaper than EncodeAutoAll on big inputs;
    /// gives up some optimality when the predictor is wrong.</summary>
    private static (string variant, byte[] payload, TimeSpan elapsed, long mem, string reason)
        EncodeFastPredict(byte[] data)
    {
        var metrics = StructuralDiagnostics.Compute(data);
        var (predictedVariantDisplay, predictReason) = StructuralDiagnostics.PredictBestVariant(metrics);

        // Map StructuralDiagnostics display names back to registry keys.
        string variantKey = predictedVariantDisplay.ToLowerInvariant() switch
        {
            "textctx"      => "textctx",
            "nibctx3"      => "nibctx3",
            "protnibctx3"  => "protnibctx3",
            "arithctx"     => "arithctx",
            "stridesplit"  => "stridesplit",
            _              => "arithctx"  // safe default
        };

        var v = Variants[variantKey];
        var (enc, mem, payload) = MeasureOp(() => v.Encode(data));
        return (variantKey, payload, enc, mem, $"predicted from structure — {predictReason}");
    }

    // =========================================================================
    // decompress
    // =========================================================================

    public static int RunDecompress(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: inBEP decompress <input.bep> [-o <output>]");
            return 1;
        }

        string? inputPath = null;
        string? outputPath = null;

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-o" || a == "--output")
            {
                if (++i >= args.Length) { Console.Error.WriteLine("Missing value for -o"); return 1; }
                outputPath = args[i];
            }
            else if (a.StartsWith('-'))
            {
                Console.Error.WriteLine($"Unknown option: {a}");
                return 1;
            }
            else
            {
                if (inputPath == null) inputPath = a;
                else { Console.Error.WriteLine($"Unexpected positional argument: {a}"); return 1; }
            }
        }

        if (inputPath == null)
        {
            Console.Error.WriteLine("decompress: missing <input.bep> path");
            return 1;
        }
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"File not found: {inputPath}");
            return 1;
        }

        outputPath ??= inputPath.EndsWith(".bep", StringComparison.OrdinalIgnoreCase)
            ? inputPath[..^4]
            : inputPath + ".decoded";

        BepArchive.Header header;
        byte[] payload;
        try
        {
            (header, payload) = BepArchive.Read(inputPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Read FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Func<byte[], byte[]> decoder;
        if (string.Equals(header.VariantName, BEP_VARIANT_NAME, StringComparison.OrdinalIgnoreCase))
        {
            decoder = BepDecode;
        }
        else if (Variants.TryGetValue(header.VariantName, out var v))
        {
            decoder = v.Decode;
        }
        else
        {
            Console.Error.WriteLine($"Unknown variant in .bep header: '{header.VariantName}'");
            Console.Error.WriteLine($"This build doesn't have a decoder for that variant.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"inBEP decompress");
        Console.WriteLine($"  Input:    {inputPath}");
        Console.WriteLine($"  Variant:  {header.VariantName}");
        Console.WriteLine($"  Payload:  {FmtBytes(header.PayloadLength)} ({header.PayloadLength:N0} bytes)");
        Console.WriteLine($"  Expected: {FmtBytes(header.OriginalLength)} ({header.OriginalLength:N0} bytes)");

        DiagnosticTimings.Reset();
        var totalSw = Stopwatch.StartNew();

        TimeSpan decElapsed;
        long decMem;
        byte[] decoded;
        try
        {
            (decElapsed, decMem, decoded) = MeasureOp(() => decoder(payload));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Decode FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        totalSw.Stop();

        if (decoded.Length != header.OriginalLength)
        {
            Console.Error.WriteLine($"Length mismatch — decoded {decoded.Length:N0} bytes, header said {header.OriginalLength:N0}");
            Console.Error.WriteLine($"Refusing to write the output. The .bep file may be corrupt.");
            return 1;
        }

        try
        {
            File.WriteAllBytes(outputPath, decoded);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Write FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"  Time:     {FmtTime(decElapsed)} (decode), {FmtTime(totalSw.Elapsed)} (total wall)");
        Console.WriteLine($"  Memory:   {FmtBytes(decMem)} allocated during decode");
        Console.WriteLine($"  Output:   {outputPath} ({decoded.Length:N0} bytes)");

        string timingReport = DiagnosticTimings.FormatReport();
        if (!string.IsNullOrWhiteSpace(timingReport) && timingReport.Trim() != "No diagnostic timings recorded.")
        {
            Console.Write(timingReport);
        }

        return 0;
    }

    // =========================================================================
    // info
    // =========================================================================

    public static int RunInfo(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: inBEP info <input.bep>");
            return 1;
        }

        string inputPath = args[1];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"File not found: {inputPath}");
            return 1;
        }

        BepArchive.Header header;
        try { header = BepArchive.ReadHeader(inputPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Read FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        long fileBytes = new FileInfo(inputPath).Length;
        bool variantKnown = string.Equals(header.VariantName, BEP_VARIANT_NAME, StringComparison.OrdinalIgnoreCase)
                         || Variants.ContainsKey(header.VariantName);
        double saved = 100.0 * (1.0 - (double)fileBytes / Math.Max(1, header.OriginalLength));

        Console.WriteLine();
        Console.WriteLine($"File:           {inputPath}");
        Console.WriteLine($"Format:         BEPZ v{header.FormatVersion}");
        Console.WriteLine($"Variant:        {header.VariantName}{(variantKnown ? "" : "  (not registered in this build)")}");
        Console.WriteLine($"Original size:  {header.OriginalLength:N0} bytes");
        Console.WriteLine($"Payload size:   {header.PayloadLength:N0} bytes");
        Console.WriteLine($"Header size:    {header.HeaderLength} bytes");
        Console.WriteLine($"File size:      {fileBytes:N0} bytes");
        Console.WriteLine($"Compression:    {saved:F2}% saved (file-on-disk vs original)");
        return variantKnown ? 0 : 2;
    }

    // =========================================================================
    // help
    // =========================================================================

    public static int PrintHelp(string? unknown = null)
    {
        if (unknown != null)
            Console.Error.WriteLine($"Unknown command: {unknown}");

        Console.WriteLine("inBEP — lossless general-purpose compressor (BEP codec family)");
        Console.WriteLine();
        Console.WriteLine("USAGE");
        Console.WriteLine("  inBEP compress   <input> [-o <output.bep>] [options]");
        Console.WriteLine("  inBEP decompress <input.bep> [-o <output>]");
        Console.WriteLine("  inBEP info       <input.bep>");
        Console.WriteLine("  inBEP help");
        Console.WriteLine();
        Console.WriteLine("VARIANT SELECTION (compress)");
        Console.WriteLine("  --variant <name>     auto         try all five, keep smallest (default)");
        Console.WriteLine("                       fast         predict best from structural fingerprint");
        Console.WriteLine("                       textctx      BWT + RePair + BEP with picker");
        Console.WriteLine("                       nibctx3      12-bit nibble context, arith-coded");
        Console.WriteLine("                       protnibctx3  protein-trained NibCtx3");
        Console.WriteLine("                       arithctx     order-2 byte context, range-coded");
        Console.WriteLine("                       stridesplit  even/odd byte split + per-stream context");
        Console.WriteLine("                       bep          raw BEPPipeline with tunable knobs");
        Console.WriteLine();
        Console.WriteLine("RAW-BEP TUNING (only with --variant bep)");
        Console.WriteLine("  --bwt-block <N>      BWT block size in bytes (power of two recommended)");
        Console.WriteLine("                       0 = mode default, -1 = auto-probe");
        Console.WriteLine("  --mode <name>        default | A | B | D");
        Console.WriteLine("                         A: BWT 512K (slower, better ratio on large inputs)");
        Console.WriteLine("                         B: default + PPM-3 rank transform");
        Console.WriteLine("                         D: 7-bit ASCII alphabet (pure-ASCII inputs)");
        Console.WriteLine("  --passes <N>         iterative passes, default 1 (auto-stop at 5%)");
        Console.WriteLine("  --no-huffman-wrap    disable per-pass Huffman wrap on BEP output");
        Console.WriteLine("  --no-runa            disable RUNA/RUNB rank-0 RLE transform");
        Console.WriteLine();
        Console.WriteLine("EXAMPLES");
        Console.WriteLine("  inBEP compress data.bin                              # auto-pick variant");
        Console.WriteLine("  inBEP compress data.bin --variant fast               # predict, single encode");
        Console.WriteLine("  inBEP compress data.bin --variant textctx            # force a variant");
        Console.WriteLine("  inBEP compress data.bin --variant bep --bwt-block 524288 --mode A --passes 3");
        Console.WriteLine("  inBEP decompress data.bin.bep -o restored.bin");
        Console.WriteLine("  inBEP info data.bin.bep");

        return unknown == null ? 0 : 1;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Time a delegate and capture the managed-heap byte delta.
    /// Forces a GC settle before the timed region so the allocation count
    /// reflects only this operation. Same pattern Benchmark.MeasureOp used
    /// in the research build, inlined here so we can drop Benchmark.cs.</summary>
    internal static (TimeSpan elapsed, long allocatedBytes, byte[] result) MeasureOp(Func<byte[]> op)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        byte[] result = op();
        sw.Stop();
        long after = GC.GetTotalAllocatedBytes(precise: true);
        long delta = after - before;
        if (delta < 0) delta = 0;
        return (sw.Elapsed, delta, result);
    }

    private static bool TryParseBwtBlock(string s, out int v)
    {
        if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase))
        {
            v = BEPPipeline.BWT_BLOCK_SIZE_AUTO;
            return true;
        }
        return int.TryParse(s, out v);
    }

    private static bool TryParseMode(string s, out CompressionMode m)
    {
        switch (s.ToLowerInvariant())
        {
            case "default": case "d0": case "0":           m = CompressionMode.Default; return true;
            case "a": case "modea": case "1":              m = CompressionMode.ModeA;   return true;
            case "b": case "modeb": case "2":              m = CompressionMode.ModeB;   return true;
            case "d": case "moded": case "3":              m = CompressionMode.ModeD;   return true;
            default:                                       m = CompressionMode.Default; return false;
        }
    }

    private static string DescribeBwtBlock(int v) => v switch
    {
        0  => "mode-default",
        -1 => "auto-probe",
        _  => v.ToString("N0")
    };

    private static string FmtTime(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "—";
        double s = t.TotalSeconds;
        if (s < 0.001) return $"{t.TotalMilliseconds * 1000:F0}μs";
        if (s < 1.0)   return $"{t.TotalMilliseconds:F0}ms";
        if (s < 60.0)  return $"{s:F2}s";
        return $"{(int)(s / 60)}m{s % 60:F0}s";
    }

    private static string FmtBytes(long n)
    {
        if (n <= 0) return "0 B";
        if (n < 1024) return $"{n} B";
        double kb = n / 1024.0;
        if (kb < 1024) return $"{kb:F1} KiB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F1} MiB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GiB";
    }
}
