// =============================================================================
// PickerDiagnostics — no-op stub.
//
// Originally a research counter that tracked which candidate coder won
// inside each variant's picker. In inBEP these counters are no longer
// reported anywhere, but the variant source files (BEPPipeline,
// TextPipelineCtxBep, NibbleContextBep, etc.) still emit RecordX() calls
// on hot paths. Rather than touch every variant to strip those calls,
// we leave this class as a no-op surface so the calls compile and do
// nothing measurable.
//
// All methods are intentionally empty. The cost is a couple of method
// dispatches per encode pass — negligible against the work the variants
// themselves do.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

namespace InBep.Core;

public static class PickerDiagnostics
{
    public static void RecordNearShannonGate(string variantName, long inputBytes, bool fired) { }
    public static void RecordPathACoder(string coderName, long inputBytes, long outputBytes) { }
    public static void RecordRangeCoderWin(long savedBits) { }
    public static void RecordSplitStreamWin(long savedBits) { }
    public static void RecordUnaryFormatFlag(byte flag) { }
    public static void RecordTextCtxProfileGate(string outcome, long inputBytes) { }
    public static void RecordTextCtxProfileWin(string profile, long savedBytes) { }

    public static void Reset() { }
    public static string FormatReport() => string.Empty;
}
