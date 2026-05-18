// =============================================================================
// NibblePriorLoader — V17.2
//
// Loads embedded K=12 static nibble priors at first use and caches them.
// V17.1 shipped one prior (raw input distribution). V17.2 adds a second
// prior trained on the post-pipeline BEP-archive distribution, used when
// NibbleContextOrder3Bep is invoked as a post-coder from TextPipelineCtxBep.
//
// Both priors are shipped as embedded resources and compiled into
// inBEP assembly, so there are no separate files to ship. Compressed archives
// carry only a 1-byte version field (1=legacy/no prior, 2=raw prior,
// 3=textctx prior) — no prior table is duplicated into each archive.
//
// Format of each embedded blob (matches NibTableTrainer output):
//   Raw little-endian uint16, (1 << K) * 16 entries.
//   prior[ctx * 16 + nibble] = P(nibble | ctx) * 65536, clamped to [1, 65534].
//   K = 12 → 4096 * 16 = 65536 entries = 131072 bytes total.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;
using System.Reflection;

namespace InBep.Core
{
    public static class NibblePriorLoader
    {
        // Resource names — must match what the .csproj <EmbeddedResource>
        // entries produce. SDK-style projects flatten the path into the
        // default namespace.
        private const string RESOURCE_NAME_K12         = "InBep.nibble_prior_k12.bin";
        private const string RESOURCE_NAME_K12_TEXTCTX = "InBep.textctx_nibble_prior_k12.bin";
        private const int    K12_NUM_CONTEXTS  = 1 << 12;
        private const int    K12_BLOB_BYTES    = K12_NUM_CONTEXTS * 16 * 2; // 131072

        private static byte[]? _k12Cache;
        private static bool    _k12Loaded;
        private static byte[]? _k12TextCtxCache;
        private static bool    _k12TextCtxLoaded;
        private static readonly object _lock = new();

        /// <summary>
        /// Returns the K=12 raw-input prior, or null if it isn't embedded.
        /// </summary>
        public static byte[]? GetK12()
        {
            if (_k12Loaded) return _k12Cache;
            lock (_lock)
            {
                if (_k12Loaded) return _k12Cache;
                _k12Cache  = LoadResource(RESOURCE_NAME_K12, expectedBytes: K12_BLOB_BYTES);
                _k12Loaded = true;
                return _k12Cache;
            }
        }

        /// <summary>True iff GetK12() would return non-null.</summary>
        public static bool HasK12 => GetK12() != null;

        /// <summary>
        /// V17.2: Returns the K=12 textctx prior (trained on BEP-archive
        /// post-pipeline byte distribution), or null if it isn't embedded.
        /// </summary>
        public static byte[]? GetK12TextCtx()
        {
            if (_k12TextCtxLoaded) return _k12TextCtxCache;
            lock (_lock)
            {
                if (_k12TextCtxLoaded) return _k12TextCtxCache;
                _k12TextCtxCache  = LoadResource(RESOURCE_NAME_K12_TEXTCTX, expectedBytes: K12_BLOB_BYTES);
                _k12TextCtxLoaded = true;
                return _k12TextCtxCache;
            }
        }

        /// <summary>True iff GetK12TextCtx() would return non-null.</summary>
        public static bool HasK12TextCtx => GetK12TextCtx() != null;

        private static byte[]? LoadResource(string name, int expectedBytes)
        {
            try
            {
                var asm = typeof(NibblePriorLoader).Assembly;
                using var s = asm.GetManifestResourceStream(name);
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                byte[] blob = ms.ToArray();
                if (blob.Length != expectedBytes)
                {
                    Console.Error.WriteLine(
                        $"[NibblePriorLoader] embedded resource '{name}' has size " +
                        $"{blob.Length}, expected {expectedBytes}. Falling back to " +
                        $"legacy bare-init (no prior).");
                    return null;
                }
                return blob;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[NibblePriorLoader] failed to load '{name}': {ex.Message}. " +
                    $"Falling back to legacy bare-init.");
                return null;
            }
        }
    }
}
