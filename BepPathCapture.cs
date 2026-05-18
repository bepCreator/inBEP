// =============================================================================
// BepPathCapture — opt-in capture of emitted BEP path bits for offline
// training of a static-table predictor (BepTableTrainer).
//
// Disabled by default. Wired up in Program.cs via the BEP_DUMP_PATHS env var.
// Cost when disabled: one null-check per EmitBit call. No archive impact.
//
// File format (matches BepTableTrainer --capture):
//   Per path:
//     uint32 LE : length in bits
//     ceil(L/8) : packed bits, LSB-first within each byte
//   Concatenate paths back-to-back. No header, no footer.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;

namespace InBep.Core
{
    /// <summary>
    /// Thread-safe BEP path bit capture. Per-thread accumulator on the hot
    /// path; lock taken once per completed path on EndPath.
    /// </summary>
    public static class BepPathCapture
    {
        private static FileStream? _stream;
        private static readonly object _lock = new();

        // Per-thread state. ThreadStatic avoids contention on EmitBit.
        [ThreadStatic] private static byte[]? _bits;
        [ThreadStatic] private static int     _len;
        [ThreadStatic] private static bool    _open;

        /// <summary>Open the output file for append. Pass null to disable
        /// capture. APPEND MODE: bits accumulate across multiple runs of
        /// the host process. If you want a fresh capture, delete the file
        /// before running. This lets you accumulate paths across multiple
        /// `inBEP corpus ...` or `inBEP recurse ...` invocations.</summary>
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

        /// <summary>True if capture is active.</summary>
        public static bool IsEnabled => _stream != null;

        /// <summary>Begin a new path. Call at the start of each BEP walk emission.</summary>
        public static void BeginPath()
        {
            if (_stream == null) return;
            if (_bits == null) _bits = new byte[1024];
            _len  = 0;
            _open = true;
        }

        /// <summary>Append one emitted bit to the current path.</summary>
        public static void EmitBit(int bit)
        {
            if (_stream == null || !_open) return;
            int byteIdx = _len >> 3;
            int bitIdx  = _len & 7;
            if (byteIdx >= _bits!.Length)
            {
                Array.Resize(ref _bits, _bits.Length * 2);
            }
            if (bitIdx == 0) _bits[byteIdx] = 0;
            _bits[byteIdx] |= (byte)((bit & 1) << bitIdx);
            _len++;
        }

        /// <summary>Finish the current path and write atomically to disk.</summary>
        public static void EndPath()
        {
            if (_stream == null || !_open) return;
            _open = false;
            if (_len == 0) return;
            int byteCount = (_len + 7) >> 3;

            Span<byte> header = stackalloc byte[4];
            uint l = (uint)_len;
            header[0] = (byte)(l & 0xFF);
            header[1] = (byte)((l >> 8) & 0xFF);
            header[2] = (byte)((l >> 16) & 0xFF);
            header[3] = (byte)((l >> 24) & 0xFF);

            lock (_lock)
            {
                if (_stream == null) return;
                _stream.Write(header);
                _stream.Write(_bits!, 0, byteCount);
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
