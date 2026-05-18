// =============================================================================
// BepChainDiagnostics — no-op stub.
//
// Originally accumulated encode/passthrough/verify counters for the
// BepChainPass2 inner coder during research. These counters are no
// longer reported anywhere in inBEP, so we leave the recording surface
// in place (so BepChainPass2 compiles unchanged) and throw the data
// away.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

namespace InBep.Core;

public static class BepChainDiagnostics
{
    public static void RecordEncode(
        long inputBytes, long outputBytes, int stopBelow,
        double encodeMs,
        long depStreamBytes = 0, long epStreamBytes = 0, long lzStreamBytes = 0) { }

    public static void RecordPassthrough(long inputBytes) { }
    public static void RecordVerifyFailure() { }
    public static void Reset() { }
    public static string FormatReport() => string.Empty;
}
