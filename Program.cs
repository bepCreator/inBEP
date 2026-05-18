// =============================================================================
// inBEP — Lossless general-purpose compressor for .bep files.
//
// Built on the BEP (Binary Equation Path) codec family. Five variants
// ship in this binary: TextCtx, NibCtx3, ProtNibCtx3, ArithCtx, StrideSplit.
// On compress the encoder picks the best variant for the input (or you
// can name one explicitly); on decompress the .bep header tells inBEP
// which variant produced the payload.
//
// Run `inBEP help` for the full command reference.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using InBep.Core;

// Opt-in captures for offline prior training. Inert unless the env var is set.
if (Environment.GetEnvironmentVariable("BEP_DUMP_PATHS") is string _bepDumpPath
    && !string.IsNullOrWhiteSpace(_bepDumpPath))
{
    BepPathCapture.SetOutput(_bepDumpPath);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => BepPathCapture.Flush();
}
if (Environment.GetEnvironmentVariable("NIB_DUMP_NIBBLES") is string _nibDumpPath
    && !string.IsNullOrWhiteSpace(_nibDumpPath))
{
    NibbleStreamCapture.SetOutput(_nibDumpPath);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => NibbleStreamCapture.Flush();
}
if (Environment.GetEnvironmentVariable("TEXTCTX_NIB_DUMP_NIBBLES") is string _textCtxDumpPath
    && !string.IsNullOrWhiteSpace(_textCtxDumpPath))
{
    TextCtxNibbleStreamCapture.SetOutput(_textCtxDumpPath);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => TextCtxNibbleStreamCapture.Flush();
}

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

return cmd switch
{
    "compress"   or "c" => CliCommands.RunCompress(args),
    "decompress" or "d" => CliCommands.RunDecompress(args),
    "info"       or "i" => CliCommands.RunInfo(args),
    "help" or "-h" or "--help" => CliCommands.PrintHelp(),
    _ => CliCommands.PrintHelp(unknown: cmd),
};
