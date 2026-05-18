// =============================================================================
// HistogramDiagnostics — no-op stub.
//
// Originally an opt-in CSV dumper for offline distribution analysis,
// gated behind the STRIPEBEP_DUMP_HISTOGRAMS environment variable. The
// research workflow that consumed those CSVs is out of scope for inBEP,
// so this is a no-op surface that lets the BEPPipeline call sites
// compile unchanged.
//
// Enabled returns false unconditionally, so the BEPPipeline rank
// histogram code path is dead-eliminated by the JIT at the call sites
// that check it before computing. There is no env-var override; if you
// need histogram dumps for research, restore the original class.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

namespace InBep.Core;

public static class HistogramDiagnostics
{
    public static bool Enabled => false;
    public static void DumpRankHistogram(int[] ranks, string tag) { }
    public static void DumpByteHistogram(byte[] bytes, string tag) { }
}
