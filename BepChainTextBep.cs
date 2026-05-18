// =============================================================================
// BepChainTextBep — V16.21
//
// WHY THIS EXISTS:
//   BepChainPass2, when wired into TextPipelineCtxBep.AddCandidates, receives
//   the output of BEPPipeline.Compress — which is already fully entropy-coded
//   (BWT+MTF+RePair+Rice/Range/Unary). That output is near-Shannon, so
//   BepChainPass2 correctly returns null (passthrough) on every file.
//
//   The fix: this class operates at the RIGHT pipeline position by doing
//   BWT+MTF itself and feeding the post-MTF bytes directly into BepChainPass2.
//   Post-MTF bytes have ~50% rank-0 (geometric decay), so BepChainPass2
//   genuinely compresses them — no RePair or BEP entropy coding in between.
//
// PIPELINE POSITION:
//   raw → BWT → MTF → [BepChainPass2 here] ← this class does exactly that
//
//   Contrast with BepChainPass2 inside AddCandidates:
//   raw → BWT → MTF → RePair → FreqRank → Rice/Range/Unary → [BepChainPass2]
//                                          ^^^^^ near-Shannon, nothing to do
//
// FRAME FORMAT:
//   [2]  Magic = 0x8002
//   [1]  Version = 1
//   [1]  stopBelow (BepChainPass2 stop_below param: 2 or 16)
//   [4]  original_length (LE int32)
//   [4]  blockSize (LE int32, BWT block size used)
//   [4]  numBlocks (LE int32)
//   [4 × numBlocks]  origins (LE int32 each, BWT origin per block)
//   [N]  BepChainPass2 frame (the encoded MTF output)
//
// SELF-VERIFY: every successful Encode round-trips before returning.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.IO;
using BEPCompress.Core;   // BWT, MTF

namespace InBep.Core;

public static class BepChainTextBep
{
    private const ushort MAGIC   = 0x8002;
    private const byte   VERSION = 1;

    // Use the same default block size as BEPPipeline
    private const int BWT_BLOCK = 65536;  // 64KB — matches BEPPipeline.BWT_BLOCK_SIZE_DEFAULT

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Encode: BWT → MTF → BepChainPass2.
    /// Returns null if the result is not smaller than the raw input.
    /// </summary>
    public static byte[]? Encode(byte[] input, int stopBelow = 2)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (stopBelow < 2 || stopBelow > 128)
            throw new ArgumentOutOfRangeException(nameof(stopBelow));

        int origLen   = input.Length;
        int blockSize = BWT_BLOCK;
        int numBlocks = (int)Math.Ceiling((double)origLen / blockSize);

        // ── BWT (block by block, same as BEPPipeline) ────────────────────────
        var bwtParts = new byte[numBlocks][];
        var origins  = new int[numBlocks];
        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * blockSize;
            int len   = Math.Min(blockSize, origLen - start);
            var blk   = new byte[len];
            Buffer.BlockCopy(input, start, blk, 0, len);
            (bwtParts[b], origins[b]) = BWT.Forward(blk);
        }

        // ── Concatenate BWT output ────────────────────────────────────────────
        byte[] bwtBytes = Concat(bwtParts);

        // ── MTF ───────────────────────────────────────────────────────────────
        byte[] mtfBytes = MTF.Encode(bwtBytes);

        // ── BepChainPass2 on the post-MTF bytes ───────────────────────────────
        // This is the correct pipeline position — mtfBytes has ~50% rank-0
        // and geometric decay, making BepChainPass2 effective.
        byte[]? chainFrame = BepChainPass2.Encode(mtfBytes, stopBelow);
        if (chainFrame == null)
            return null;  // BepChain couldn't compress the MTF output

        // ── Assemble frame ────────────────────────────────────────────────────
        int headerLen = 2 + 1 + 1 + 4 + 4 + 4 + numBlocks * 4;
        int frameLen  = headerLen + chainFrame.Length;

        if (frameLen >= origLen)
            return null;  // No net gain vs raw input

        byte[] frame = new byte[frameLen];
        int pos = 0;

        frame[pos++] = (byte)(MAGIC >> 8);
        frame[pos++] = (byte)(MAGIC & 0xFF);
        frame[pos++] = VERSION;
        frame[pos++] = (byte)stopBelow;
        WriteInt32LE(frame, pos, origLen);   pos += 4;
        WriteInt32LE(frame, pos, blockSize); pos += 4;
        WriteInt32LE(frame, pos, numBlocks); pos += 4;
        for (int b = 0; b < numBlocks; b++)
        {
            WriteInt32LE(frame, pos, origins[b]);
            pos += 4;
        }
        Buffer.BlockCopy(chainFrame, 0, frame, pos, chainFrame.Length);

        // ── Self-verify ───────────────────────────────────────────────────────
        try
        {
            byte[] rt = Decode(frame);
            if (rt.Length != origLen) return null;
            for (int i = 0; i < origLen; i++)
                if (rt[i] != input[i]) return null;
        }
        catch { return null; }

        return frame;
    }

    /// <summary>Decode a BepChainTextBep frame back to the original bytes.</summary>
    public static byte[] Decode(byte[] frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        int pos = 0;

        ushort magic = (ushort)((frame[pos] << 8) | frame[pos + 1]); pos += 2;
        if (magic != MAGIC)
            throw new InvalidDataException($"BepChainTextBep: bad magic 0x{magic:X4}");
        byte version  = frame[pos++];
        if (version != VERSION)
            throw new InvalidDataException($"BepChainTextBep: version {version}");
        int stopBelow = frame[pos++];
        int origLen   = ReadInt32LE(frame, pos); pos += 4;
        int blockSize = ReadInt32LE(frame, pos); pos += 4;
        int numBlocks = ReadInt32LE(frame, pos); pos += 4;

        var origins = new int[numBlocks];
        for (int b = 0; b < numBlocks; b++)
        {
            origins[b] = ReadInt32LE(frame, pos);
            pos += 4;
        }

        // Remaining bytes = BepChainPass2 frame encoding the MTF output
        int chainLen   = frame.Length - pos;
        byte[] chainFrame = new byte[chainLen];
        Buffer.BlockCopy(frame, pos, chainFrame, 0, chainLen);

        // ── Decode: BepChainPass2 → MTF bytes → BWT bytes → original ─────────
        byte[] mtfBytes = BepChainPass2.Decode(chainFrame);
        byte[] bwtBytes = MTF.Decode(mtfBytes);

        // Split BWT bytes back into blocks and invert each block's BWT
        var blocks = new byte[numBlocks][];
        int bwtPos = 0;
        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * blockSize;
            int len   = Math.Min(blockSize, origLen - start);
            var blk   = new byte[len];
            Buffer.BlockCopy(bwtBytes, bwtPos, blk, 0, len);
            bwtPos += len;
            blocks[b] = BWT.Inverse(blk, origins[b]);
        }

        return Concat(blocks);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] Concat(byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    private static void WriteInt32LE(byte[] buf, int pos, int val)
    {
        buf[pos]     = (byte)(val         & 0xFF);
        buf[pos + 1] = (byte)((val >>  8) & 0xFF);
        buf[pos + 2] = (byte)((val >> 16) & 0xFF);
        buf[pos + 3] = (byte)((val >> 24) & 0xFF);
    }

    private static int ReadInt32LE(byte[] buf, int pos) =>
        buf[pos] | (buf[pos+1] << 8) | (buf[pos+2] << 16) | (buf[pos+3] << 24);
}
