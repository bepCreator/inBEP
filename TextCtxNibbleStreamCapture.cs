// =============================================================================
// TextCtxNibbleStreamCapture — V17.2
//
// Opt-in capture of the nibble streams that NibbleContextBep /
// NibbleContextOrder3Bep observe WHEN CALLED FROM TextPipelineCtxBep
// as a post-coder over BEP archive bytes. This is a different distribution
// from the raw-input case (post-entropy-coded BEP-archive framing vs. raw
// text/binary bytes), so it earns its own capture stream and its own
// trained prior.
//
// Wired up in Program.cs via the TEXTCTX_NIB_DUMP_NIBBLES env var. Independent
// of NIB_DUMP_NIBBLES — both can be enabled simultaneously, they write to
// different files and are consumed by separate trainer runs.
//
// File format: identical to NibbleStreamCapture (one stream per Encode() call,
// 1-byte bootstrap_len + bootstrap + uint32 LE nibble_count + packed nibbles).
// Reuses the same NibTableTrainer with no changes.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;

namespace InBep.Core
{
    /// <summary>
    /// Thread-safe nibble-stream capture for offline training of the
    /// post-pipeline (BEP-archive-input) prior. Per-thread accumulator on the
    /// hot path; lock taken once per completed stream on EndStream.
    /// </summary>
    public static class TextCtxNibbleStreamCapture
    {
        private static FileStream? _stream;
        private static readonly object _lock = new();

        // Per-thread state.
        [ThreadStatic] private static byte[]? _bootstrap;
        [ThreadStatic] private static int     _bootstrapLen;
        [ThreadStatic] private static byte[]? _packed;
        [ThreadStatic] private static int     _nibbleCount;
        [ThreadStatic] private static bool    _open;

        public static void SetOutput(string? path)
        {
            lock (_lock)
            {
                _stream?.Flush();
                _stream?.Dispose();
                _stream = path == null
                    ? null
                    : new FileStream(path, FileMode.Append, FileAccess.Write,
                                     FileShare.Read, 1 << 20);
            }
        }

        public static bool IsEnabled => _stream != null;

        public static void BeginStream(ReadOnlySpan<byte> bootstrap)
        {
            if (_stream == null) return;
            if (_packed == null)    _packed = new byte[4096];
            if (_bootstrap == null) _bootstrap = new byte[8];

            _bootstrapLen = Math.Min(bootstrap.Length, 255);
            if (_bootstrap.Length < _bootstrapLen)
                _bootstrap = new byte[_bootstrapLen];
            for (int i = 0; i < _bootstrapLen; i++) _bootstrap[i] = bootstrap[i];

            _nibbleCount = 0;
            _open = true;
        }

        public static void EmitNibble(int nibble)
        {
            if (_stream == null || !_open) return;
            int byteIdx = _nibbleCount >> 1;
            bool isHi = (_nibbleCount & 1) == 0;
            if (byteIdx >= _packed!.Length)
            {
                Array.Resize(ref _packed, _packed.Length * 2);
            }
            if (isHi) _packed[byteIdx] = (byte)((nibble & 0x0F) << 4);
            else      _packed[byteIdx] |= (byte)(nibble & 0x0F);
            _nibbleCount++;
        }

        public static void EndStream()
        {
            if (_stream == null || !_open) return;
            _open = false;
            if (_nibbleCount == 0 && _bootstrapLen == 0) return;

            int packedBytes = (_nibbleCount + 1) >> 1;

            Span<byte> header = stackalloc byte[1 + _bootstrapLen + 4];
            header[0] = (byte)_bootstrapLen;
            for (int i = 0; i < _bootstrapLen; i++) header[1 + i] = _bootstrap![i];
            uint nc = (uint)_nibbleCount;
            int p = 1 + _bootstrapLen;
            header[p++] = (byte)(nc & 0xFF);
            header[p++] = (byte)((nc >> 8) & 0xFF);
            header[p++] = (byte)((nc >> 16) & 0xFF);
            header[p++] = (byte)((nc >> 24) & 0xFF);

            lock (_lock)
            {
                if (_stream == null) return;
                _stream.Write(header);
                _stream.Write(_packed!, 0, packedBytes);
            }
        }

        public static void Flush()
        {
            lock (_lock)
            {
                _stream?.Flush();
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
