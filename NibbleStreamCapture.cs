// =============================================================================
// NibbleStreamCapture — opt-in capture of the nibble streams that the
// NibbleContextBep / NibbleContextOrder3Bep encoders observe, for offline
// training of static-prior tables.
//
// Wired up in Program.cs via the NIB_DUMP_NIBBLES env var (independent of
// BEP_DUMP_PATHS — both can be enabled simultaneously, but they're separate
// streams to separate files).
//
// File format (matches NibTableTrainer --capture):
//   Per stream (one stream per Encode() call):
//     uint8     : bootstrap_byte_count (1 for NibCtx order-2, 2 for NibCtx3)
//     N bytes   : bootstrap bytes (the raw prefix the encoder uses to seed
//                 the initial context). Trainer uses these to set the
//                 initial context exactly as the runtime would.
//     uint32 LE : nibble_count (number of nibbles emitted to model.Observe)
//     packed_bytes : ceil(count/2) bytes. First nibble is HIGH 4 bits of
//                    byte 0, second nibble is LOW 4 bits of byte 0, etc.
//                    (matches natural hi-then-lo emission order)
//   Concatenate streams back-to-back. No header, no footer.
//
// Cost when disabled: one null-check per EmitNibble call.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;

namespace InBep.Core
{
    /// <summary>
    /// Thread-safe nibble stream capture for offline static-prior training.
    /// Per-thread accumulator on the hot path; lock taken once per completed
    /// stream on EndStream.
    /// </summary>
    public static class NibbleStreamCapture
    {
        private static FileStream? _stream;
        private static readonly object _lock = new();

        // Per-thread state. ThreadStatic avoids contention on EmitNibble.
        [ThreadStatic] private static byte[]? _bootstrap;
        [ThreadStatic] private static int     _bootstrapLen;
        [ThreadStatic] private static byte[]? _packed;       // packed nibble buffer
        [ThreadStatic] private static int     _nibbleCount;
        [ThreadStatic] private static bool    _open;

        /// <summary>Open the output file in append mode. Pass null to disable
        /// capture. Append mode lets bits accumulate across multiple inBEP
        /// invocations — delete the file before a fresh training session.</summary>
        public static void SetOutput(string? path)
        {
            lock (_lock)
            {
                _stream?.Flush();
                _stream?.Dispose();
                _stream = path == null
                    ? null
                    : new FileStream(path, FileMode.Append, FileAccess.Write,
                                     FileShare.Read, 1 << 20); // 1 MB buffer
            }
        }

        public static bool IsEnabled => _stream != null;

        /// <summary>Begin a new stream. bootstrap is the raw prefix bytes the
        /// encoder used to seed its initial context — typically input[0..1] for
        /// NibCtx (1 byte) or input[0..2] for NibCtx3 (2 bytes). Pass an
        /// empty span if the encoder has no bootstrap.</summary>
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

        /// <summary>Append one observed nibble (0..15) to the current stream.</summary>
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

        /// <summary>Finish the current stream and write it atomically to disk.</summary>
        public static void EndStream()
        {
            if (_stream == null || !_open) return;
            _open = false;
            if (_nibbleCount == 0 && _bootstrapLen == 0) return;

            int packedBytes = (_nibbleCount + 1) >> 1;

            // Compose header: [u8 bootstrap_len][bootstrap...][u32 LE nibble_count]
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

        /// <summary>Flush and close. Call at process exit.</summary>
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
