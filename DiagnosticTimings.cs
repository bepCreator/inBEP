// =============================================================================
// DiagnosticTimings — accumulates sub-stage timing AND data-flow data.
//
// V2 (Drop 11 prep): in addition to ticks and call counts, each Add()
// optionally carries input bytes and output bytes flowing through that stage.
// This lets the report show compression ratios per stage, which reveals
// where real compression happens vs where stages are just transforming.
//
// USAGE PATTERN inside an encoder:
//   long t0 = Stopwatch.GetTimestamp();
//   var result = DoWork(input);
//   DiagnosticTimings.Add("TextPipeline.RePair",
//       Stopwatch.GetTimestamp() - t0,
//       inputBytes:  input.Length,
//       outputBytes: result.SequenceLength * 4);  // approximation OK
//
// Pure-timing call (transforms with no clean byte boundary, or 1:1 stages):
//   DiagnosticTimings.Add("TextPipeline.MTF", elapsedTicks);
//
// AT END OF RUN:
//   Console.WriteLine(DiagnosticTimings.FormatReport());
//
// Categories follow "Module.Substage" naming for grouping in the report.
// =============================================================================

namespace InBep.Core;

public static class DiagnosticTimings
{
    private sealed class Bucket
    {
        public long Ticks;
        public long Calls;
        public long InputBytes;       // total bytes flowing IN across all calls
        public long OutputBytes;      // total bytes flowing OUT across all calls
        public bool HasByteData;      // false until the first Add() with byte counts
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bucket> _buckets
        = new();

    /// <summary>Time-only logger for stages with no clean byte boundary.</summary>
    public static void Add(string category, long ticks)
    {
        var b = _buckets.GetOrAdd(category, _ => new Bucket());
        System.Threading.Interlocked.Add(ref b.Ticks, ticks);
        System.Threading.Interlocked.Increment(ref b.Calls);
    }

    /// <summary>Full logger: timing plus input/output byte counts. Use for
    /// stages that consume one buffer and emit another (encoders, decoders,
    /// global-mode measurements). Byte counts accumulate across calls so the
    /// report shows total throughput per stage.</summary>
    public static void Add(string category, long ticks, long inputBytes, long outputBytes)
    {
        var b = _buckets.GetOrAdd(category, _ => new Bucket());
        System.Threading.Interlocked.Add(ref b.Ticks, ticks);
        System.Threading.Interlocked.Increment(ref b.Calls);
        System.Threading.Interlocked.Add(ref b.InputBytes, inputBytes);
        System.Threading.Interlocked.Add(ref b.OutputBytes, outputBytes);
        b.HasByteData = true;  // best-effort flag, race-tolerable
    }

    /// <summary>Convenience wrapper: time a delegate. Use only when delegate
    /// allocation cost is negligible relative to the work.</summary>
    public static T Time<T>(string category, Func<T> work)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return work(); }
        finally { Add(category, System.Diagnostics.Stopwatch.GetTimestamp() - start); }
    }

    public static void Time(string category, Action work)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { work(); }
        finally { Add(category, System.Diagnostics.Stopwatch.GetTimestamp() - start); }
    }

    /// <summary>Reset all accumulators. Call before starting a fresh corpus or bench run.</summary>
    public static void Reset() => _buckets.Clear();

    /// <summary>Snapshot for external analysis.</summary>
    public static IReadOnlyList<(string Category, long Ticks, long Calls, long InputBytes, long OutputBytes, bool HasByteData)> Snapshot()
    {
        var result = new List<(string, long, long, long, long, bool)>();
        foreach (var kv in _buckets)
        {
            var b = kv.Value;
            result.Add((kv.Key, b.Ticks, b.Calls, b.InputBytes, b.OutputBytes, b.HasByteData));
        }
        return result;
    }

    /// <summary>Format a human-readable report grouped by top-level category.
    /// Categories with byte data show input bytes, output bytes, and compression
    /// ratio. Categories without byte data (pure timers) show only time and calls.</summary>
    public static string FormatReport()
    {
        if (_buckets.IsEmpty) return "No diagnostic timings recorded.";

        var snapshot = Snapshot()
            .OrderBy(t => t.Category)
            .ToList();

        var groups = snapshot.GroupBy(t =>
                t.Category.Contains('.') ? t.Category.Substring(0, t.Category.IndexOf('.')) : t.Category)
            .OrderByDescending(g => g.Sum(t => t.Ticks))
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("═══ TIMING + DATA-FLOW DIAGNOSTICS ════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"{"Stage",-40} {"Time",10} {"Calls",8} {"In",13} {"Out",13} {"Ratio",8} {"Avg",10}");
        sb.AppendLine(new string('─', 110));

        double tickFreq = System.Diagnostics.Stopwatch.Frequency;

        foreach (var group in groups)
        {
            long groupTicks = group.Sum(t => t.Ticks);
            double groupSeconds = groupTicks / tickFreq;
            sb.AppendLine($"{group.Key,-40} {FormatTime(groupSeconds),10}");

            foreach (var (category, ticks, calls, inBytes, outBytes, hasBytes) in
                     group.OrderByDescending(t => t.Ticks))
            {
                if (!category.Contains('.')) continue;  // already shown as group total
                string subname = category.Substring(category.IndexOf('.') + 1);
                double seconds = ticks / tickFreq;
                double avgMs = calls > 0 ? (seconds * 1000.0 / calls) : 0;

                if (hasBytes && inBytes > 0)
                {
                    double ratio = 100.0 * outBytes / (double)inBytes;
                    sb.AppendLine(
                        $"  {subname,-38} {FormatTime(seconds),10} {calls,8:N0} " +
                        $"{FormatBytes(inBytes),13} {FormatBytes(outBytes),13} " +
                        $"{ratio,7:F2}% {avgMs,9:F2}ms");
                }
                else
                {
                    sb.AppendLine(
                        $"  {subname,-38} {FormatTime(seconds),10} {calls,8:N0} " +
                        $"{"—",13} {"—",13} {"—",8} {avgMs,9:F2}ms");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0.001) return $"{seconds * 1_000_000:F1}μs";
        if (seconds < 1.0) return $"{seconds * 1000:F1}ms";
        if (seconds < 60.0) return $"{seconds:F2}s";
        return $"{(int)(seconds / 60)}m{seconds % 60:F1}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2}GB";
    }
}
