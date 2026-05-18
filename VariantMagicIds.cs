// =============================================================================
// VariantMagicIds — central registry of 2-byte (16-bit) per-variant magic IDs.
//
// inBEP trim: only the IDs referenced by the kept variants and their
// internal building blocks. The framing-only category (0x60xx, used by
// the dropped format wrappers Blep1Lzma / Gzipb / Xzb / Zipb / Tarb /
// Deflateb) is gone with those wrappers.
//
// Top byte is the category for human readability; low byte is per-variant.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

namespace InBep.Core;

public static class VariantMagicIds
{
    // 0x20xx: byte-shape variants.
    public const ushort StrideSplitBep                  = 0x2002;

    // 0x40xx: LZ77 family — V3 is reachable end-to-end as a TextCtx
    // internal candidate. V1/V2 IDs are kept because V3 reuses parts
    // of their frames; the V1/V2 encoders were dropped, but the ID
    // constants stay so V3's frame layout is unambiguous.
    public const ushort Lz77Bep                         = 0x4001;
    public const ushort Lz77BepV2                       = 0x4002;
    public const ushort Lz77BepV3                       = 0x4003;

    // 0x50xx: context-coding variants — the inBEP keep-list.
    public const ushort NibbleContextBep                = 0x5001;  // internal to TextCtx / StrideSplit
    public const ushort NibbleContextOrder3Bep          = 0x5002;  // top-level: NibCtx3
    public const ushort ArithmeticContextBep            = 0x5003;  // top-level: ArithCtx
    public const ushort ProteinNibbleContextOrder3Bep   = 0x5005;  // top-level: ProtNibCtx3

    // 0x80xx: BepChain entropy coder — kept only as an internal candidate
    // inside TextPipelineCtxBep (via BepChainTextBep).
    public const ushort BepChainPass2                   = 0x8001;
    public const ushort BepChainTextBep                 = 0x8002;
}
