# inBEP

**Lossless general-purpose compressor for `.bep` files.**

**Author:** Rich Wagner  
**Contact:** rich@newdawndata.com  
**Last Updated:** 5/18/2026  
**License:** Apache 2.0

---

## Corpus benchmark summary

Overnight run on 857 files totalling 2.24 GiB of raw input, grouped here by source corpus. Compression-ratio columns are *bytes saved vs raw input*. `Top variant` is the inBEP variant that won on the most files in the corpus. `Encode time` and the allocation columns are measured against the **winning variant per file** (i.e. what `inBEP compress --variant fast` would spend if the structural predictor hit every file correctly — the floor on encode cost for the ratio achieved).

| Corpus | Files | Total size | inBEP ratio | LZMA ratio | Δ vs LZMA | Top variant | Encode time | Avg alloc | Peak alloc |
|---|---:|---:|---:|---:|---:|---|---:|---:|---:|
| **Canterbury** | 14 | 13.3 MiB | 78.27% | 78.15% | +0.12 pp | TextCtx (86%) | 1m 17s | 372.0 MiB | 2.01 GiB |
| **Calgary** | 18 | 3.1 MiB | 68.71% | 72.83% | -4.12 pp | TextCtx (89%) | 22.3 s | 125.2 MiB | 534.0 MiB |
| **Silesia** | 12 | 202.1 MiB | 74.78% | 76.89% | -2.11 pp | TextCtx (92%) | 53m 59s | 22.06 GiB | 150.11 GiB |
| **Lukas (DICOM)** | 714 | 598.3 MiB | 68.22% | 66.13% | +2.10 pp | StrideSplit (95%) | 55m 48s | 670.4 MiB | 19.90 GiB |
| **Lukas (TIF)** | 74 | 268.5 MiB | 68.69% | 65.41% | +3.29 pp | TextCtx (92%) | 1h 5m | 10.76 GiB | 40.06 GiB |
| **DNA** | 11 | 1.2 MiB | 75.55% | 74.28% | +1.27 pp | NibCtx3 (91%) | 1.0 s | 2.8 MiB | 28.0 MiB |
| **Protein** | 4 | 6.8 MiB | 47.40% | 49.42% | -2.02 pp | ProtNibCtx3 (100%) | 2.1 s | 5.1 MiB | 8.9 MiB |
| **Enwik9** | 2 | 1.06 GiB | 67.57% | 71.24% | -3.66 pp | TextCtx (100%) | 2h 54m | 333.37 GiB | 341.81 GiB |
| **Total** | **857** | **2.24 GiB** | **68.26%** | **69.34%** | **-1.08 pp** | **StrideSplit (79%)** | **6h 40m** | **2.84 GiB** | **341.81 GiB** |

**Reading the columns:**

- **inBEP ratio / LZMA ratio** — bytes saved vs raw input (higher is better).
- **Δ vs LZMA** — percentage-point difference in saved-bytes ratio. Positive means inBEP recovered more bytes than LZMA preset 6 on that corpus.
- **Top variant** — which inBEP variant won the most files in the corpus, with its win share in parentheses. A high percentage (>80%) signals a clean structural fingerprint; mixed percentages mean `--variant auto` is doing real work picking different winners across the corpus.
- **Encode time** — wall-clock summed across all files in the corpus, using only the winning variant per file. Multiply by roughly 5× for a realistic `--variant auto` budget on the same corpus (auto tries all five and keeps the smallest).
- **Avg alloc / Peak alloc** — *cumulative* managed-heap bytes allocated during the winning encode (the `GC.GetTotalAllocatedBytes` delta around the call, with a `GC.Collect`-settle immediately before each measurement). This is allocation throughput, **not** peak working-set memory — for an encoder that rebuilds buffers across many internal passes the cumulative figure runs an order of magnitude (or two) above the actual peak RSS. Read it as a measure of allocator pressure / GC load, not as the RAM you need to run the codec.

Measured on Windows 11, .NET 8, single-thread encode. Decode times and decode allocation are tracked separately and are typically an order of magnitude lighter than encode on these workloads.


---

## What this is

inBEP is the practical, file-level lossless compressor built on top of the
**Binary Equation Path (BEP)** primitive defined in the companion repository:

> **Sister repo:** [`bepCreator/ndd`](https://github.com/bepCreator/ndd) — Binary
> Equation Paths. Contains the formal definition of the BEP walk, the proof
> of convergence (via a modified Collatz function `f(n) = ⌊n/2⌋`), the
> bit-level compression worked examples, and the unified `BEP.Compress` /
> `BEP.Decompress` C# entry points. **Read that first if you want the math.**

Where `ndd` defines the BEP walk on a single integer, inBEP wraps it with
transforms (BWT, MTF, Re-Pair, context models, LZ77, …) so it can compete
with general-purpose lossless codecs on heterogeneous real-world files.
inBEP ships five algorithmic variants and a raw BEP pipeline that exposes
every internal knob.

The output format is a single-file container (`.bep`) holding the variant
name and its encoded payload. Decompression reads the variant name from
the header and dispatches to the matching decoder. There is no
inter-variant ambiguity — each `.bep` carries unambiguous self-identification.

---

## Table of Contents

1. [Install / build](#install--build)
2. [Quick start](#quick-start)
3. [CLI reference](#cli-reference)
4. [How BEP is integrated](#how-bep-is-integrated)
5. [The five variants](#the-five-variants)
   - [TextCtx](#textctx--bwt--re-pair--bep-with-picker)
   - [NibCtx3](#nibctx3--12-bit-nibble-context-arithmetic-coded)
   - [ProtNibCtx3](#protnibctx3--nibctx3-with-amino-acid-prior)
   - [ArithCtx](#arithctx--order-2-byte-context-range-coded)
   - [StrideSplit](#stridesplit--evenodd-byte-split--per-stream-context)
6. [Raw BEP pipeline (`--variant bep`)](#raw-bep-pipeline---variant-bep)
7. [The `.bep` file format](#the-bep-file-format)
8. [Build from source](#build-from-source)
9. [Project layout](#project-layout)
10. [License](#license)

---

## Install / build

inBEP targets .NET 8. Clone, restore, build:

```
git clone https://github.com/bepCreator/inBEP inBEP
cd inBEP
dotnet build -c Release
```

The resulting executable is `bin/Release/net8.0/inBEP` (or `inBEP.exe` on
Windows). No native dependencies — everything is managed code. The
optional embedded nibble-prior tables (`nibble_prior_k12.bin`,
`textctx_nibble_prior_k12.bin`) are picked up by the project file if they
are present in the project root at build time; their absence falls back
to a legacy bare-init path with a modest ratio cost on text inputs.

---

## Quick start

```
# Compress (auto-picks the best variant)
inBEP compress mydata.bin
# → writes mydata.bin.bep, prints which variant won

# Decompress
inBEP decompress mydata.bin.bep
# → writes mydata.bin

# Inspect a .bep file's header without decoding
inBEP info mydata.bin.bep

# Force a specific variant
inBEP compress mydata.bin --variant textctx

# Use the raw BEP pipeline with tuned parameters
inBEP compress mydata.bin --variant bep --bwt-block 524288 --mode A --passes 3
```

---

## CLI reference

### Commands

| Command       | Purpose                                              |
|---------------|------------------------------------------------------|
| `compress`    | Encode an input file into a `.bep` archive.          |
| `decompress`  | Decode a `.bep` archive back to its original bytes.  |
| `info`        | Print a `.bep` archive's header without decoding.    |
| `help`        | Print the full command and option reference.         |

Aliases: `c` / `d` / `i` for `compress` / `decompress` / `info`.

### `compress` options

```
inBEP compress <input> [-o <output>] [options]
```

| Option                | Default      | Effect                                                              |
|-----------------------|--------------|---------------------------------------------------------------------|
| `-o`, `--output`      | `<input>.bep`| Output file path.                                                   |
| `--variant <name>`    | `auto`       | Selection strategy or specific variant. See table below.            |
| `--bwt-block <N>`     | `0`          | BWT block size in bytes (raw BEP only). `0` = mode default, `-1` = auto-probe. |
| `--mode <name>`       | `default`    | BEP pipeline mode (raw BEP only). `default` / `A` / `B` / `D`.      |
| `--passes <N>`        | `1`          | Iterative compression passes (raw BEP only). Auto-stops at 5%.      |
| `--no-huffman-wrap`   | off          | Disable per-pass Huffman wrap on BEP output (raw BEP only).         |
| `--no-runa`           | off          | Disable RUNA/RUNB rank-0 RLE transform (raw BEP only).              |

The tuning flags (`--bwt-block`, `--mode`, `--passes`, `--no-huffman-wrap`,
`--no-runa`) **only apply to `--variant bep`**. The five named variants
manage their internal parameters themselves and ignore these flags;
inBEP prints a one-line notice if you pass tuning flags with a non-`bep`
variant.

### `--variant` values

| Name           | What runs                                                                                   |
|----------------|---------------------------------------------------------------------------------------------|
| `auto`         | (default) Runs all five named variants, keeps the smallest output, prints the ranking.      |
| `fast`         | Computes a structural fingerprint (entropy, alphabet, stride-2 delta, …) and runs the one variant the predictor selects. Single encode. |
| `textctx`      | TextPipelineCtxBep — BWT + Re-Pair + BEP with a picker over Nibble/Nibble3/ArithCtx/BepChain/Lz77V3/DictLz77V3, plus stride-2 siblings. |
| `nibctx3`      | NibbleContextOrder3Bep — 12-bit (order-3) nibble context, arithmetic-coded.                 |
| `protnibctx3`  | ProteinNibbleContextOrder3Bep — NibCtx3 with an embedded amino-acid pre-trained prior.      |
| `arithctx`     | ArithmeticContextBep — order-2 byte context, range-coded.                                   |
| `stridesplit`  | StrideSplitBep — even/odd byte split, each stream context-coded separately.                 |
| `bep`          | Direct `BEPPipeline.Compress` with the full knob set exposed via CLI.                       |

### `decompress` options

```
inBEP decompress <input.bep> [-o <output>]
```

| Option            | Default                                       | Effect           |
|-------------------|-----------------------------------------------|------------------|
| `-o`, `--output`  | strip `.bep` (or append `.decoded` if absent) | Output file path |

The decoder reads the variant name from the `.bep` header and dispatches.
No `--variant` flag exists for `decompress` — it would be ignored anyway.

### Diagnostic output

Every `compress` and `decompress` invocation prints, in order:

1. **Input and output sizes** (raw bytes, KiB/MiB/GiB display)
2. **Chosen variant** and the reason it was selected (auto-ranking, prediction reason, or `explicit`)
3. **Per-variant ranking table** (auto mode only) — every variant's output size and encode time, with a `★` on the winner
4. **Wall-clock encode/decode time** and **peak managed allocation** during the operation
5. **Output file path** with payload-vs-input savings and file-vs-input savings (so you can see exactly what the 14-byte–class header costs you)
6. **Per-function timing report** — accumulated time, call count, input bytes, output bytes, and per-stage compression ratio for every internal stage (BWT, MTF, RePair, BEP-coding, FreqRank, RankRangeCoder, etc.) that fired during the operation

The per-function timing report is the runtime equivalent of a flame-graph
slice; it shows where time and bytes went, stage-by-stage, for whichever
variant ran.

---

## How BEP is integrated

The BEP walk from the sister repo
([`bepCreator/ndd`](https://github.com/bepCreator/ndd)) takes a positive
integer `v ≥ 2` and produces a binary path of length `⌊log₂(v)⌋` —
formally, `f(n) = ⌊n/2⌋` iterated until `n = 1`, with each step's primary
bit recorded. In inBEP this primitive enters in **two distinct ways**:

### 1. BEP as the entropy coder on ranked-symbol streams

After a front-end transform (BWT → MTF → Re-Pair → frequency-ranking),
the result is a stream of small non-negative integers — the symbol ranks.
The post-Re-Pair rank distribution is heavily skewed toward zero (often
55–65% zeros on BWT+MTF output). The BEP path of an integer `v` is
`⌊log₂(v)⌋` bits, so small values cost very few bits — the natural fit
for a rank-zero-heavy stream.

Three BEP-coding variants live inside `BEPPipeline.cs`:

| Coder                  | Strategy                                                                 |
|------------------------|--------------------------------------------------------------------------|
| `UnaryBEPCoder`        | Emits each rank's BEP path concatenated. Tightest on zero-heavy streams. |
| `RiceBEPCoder`         | Splits a rank into quotient (BEP-coded) and remainder (raw `k` bits). Picks optimal `k` per block. Better when zeros aren't dominant. |
| `SplitStreamBEPCoder`  | Per-block threshold: small values one way, large values another. Two BEP streams interleaved. |

The pipeline picks among these per-block by estimated bit-cost. This is
where the BEP **walk** itself does compression work.

### 2. BEP as magnitude-class integer coding for metadata

The header and metadata fields throughout the codec family are encoded
via `BepIntCoding` — a non-negative integer `v` is written as a pair
`(L, path)` where:

```
L    = ⌊log₂(v)⌋      written in a fixed Lbits-wide field (3..7 bits)
path = v − 2^L         written in L bits
```

The decoder recovers `v = 2^L | path`. The implicit `(L+1)`-th bit is the
high bit, which is always 1 for `v ≥ 1`. For `v < 2` an escape encoding
emits `L = 0` and a 1-bit literal. This is the same magnitude-class
scheme used by the format wrappers in newdawndata's wider BEP family
(blep1, gzipb, xzb, zipb, tarb), so any change to the encoding is
localized to one file.

Every variant in inBEP uses `BepIntCoding` for its frame header (magic
ID, length fields, alphabet sizes). The 12-bit `MAGIC_ID` constants in
`VariantMagicIds` namespace the variants inside the BEP family
(0x20xx = byte-shape, 0x40xx = LZ77, 0x50xx = context-coding, 0x80xx =
BepChain).

### Where each form lives

| Variant         | Uses BEP-walk entropy coder?            | Uses BepIntCoding for headers? |
|-----------------|-----------------------------------------|--------------------------------|
| TextCtx         | **Yes** (via BEPPipeline & BepChain)    | Yes                            |
| NibCtx3         | No (arithmetic coder on nibbles)        | Yes                            |
| ProtNibCtx3     | No (arithmetic coder on nibbles)        | Yes                            |
| ArithCtx        | No (range coder on bytes)               | Yes                            |
| StrideSplit     | No (NibbleContextBep per stream)        | Yes                            |
| `bep`           | **Yes** (BEPPipeline directly)          | Yes                            |

The non-BEP-walk variants are still part of the BEP codec family — they
share framing, magic IDs, and the iterative-archive container layout.
They exist because on certain input shapes (small alphabets, strong
order-1/order-2 context, even/odd-stride binary data) a pure
context-model entropy coder beats the BWT-based BEP pipeline. The `auto`
strategy tries all five so the winner emerges from measurement.

---

## The five variants

### TextCtx — BWT + Re-Pair + BEP with picker

**Class:** `TextPipelineCtxBep` &nbsp;·&nbsp; **Magic:** —  (uses BEPPipeline frame)

The flagship variant for coherent text and many heterogeneous binaries.
Three-stage architecture:

```
input
  │
  ├─► BWT (block size: auto-probed)        ── Burrows-Wheeler transform groups
  │                                            local context together
  │
  ├─► MTF (Move-to-Front)                  ── Convert grouped runs into
  │                                            zero-heavy rank stream
  │
  ├─► Re-Pair grammar                      ── Replace repeated pairs with
  │                                            new symbols; build a rule set
  │
  ├─► FreqRank                             ── Rank symbols by frequency,
  │                                            making the stream zero-heavy
  │
  └─► BEP-coding (picked per block):       ◄── This is where BEP enters
        Unary BEP  /  Rice BEP  /  Split-Stream BEP  /  BepChainPass2

After producing the BEP archive bytes, TextCtx runs an outer PICKER:

    pick min over { raw BEP, Nibble-wrap, NibCtx3-wrap, ArithCtx-wrap,
                    BepChainTextBep, Lz77BepV3, DictPreLz77V3Bep,
                    + stride-2 siblings of each, gated by structural
                      probes (printable-ASCII fraction, alphabet size,
                      stride-2 entropy delta) }
```

**BEP integration:** Two layers. First, the inner BEPPipeline uses
`UnaryBEPCoder` / `RiceBEPCoder` / `SplitStreamBEPCoder` to entropy-code
the post-Re-Pair rank stream (this is the bit-walk from the sister repo
applied to a stream of ranks). Second, the outer picker may wrap the BEP
archive bytes in a further context model — turning the BEP output into
the next coder's input.

**Profile gating (V17/V18 entropy profiles):** A printable-ASCII fraction
probe over the first 16 KB classifies inputs as text-confident,
binary-confident, or ambiguous. Confident inputs skip the unlikely-to-win
entropy profile (V17 = legacy Rice/Unary/Split; V18 = adds RangeCoder/
RangeCoderO1 + apex-zero shortcuts + cost-aware Re-Pair). Ambiguous
inputs compute both profiles and keep the smaller result.

**When it wins:** coherent text (English, code, JSON, XML, logs),
mid-alphabet binary with order-1 context, heterogeneous binaries on the
"text-shaped" side of the spectrum.

**Per-variant subcommands:** none — fully self-tuning.

---

### NibCtx3 — 12-bit nibble context, arithmetic-coded

**Class:** `NibbleContextOrder3Bep` &nbsp;·&nbsp; **Magic:** `0x5002`

```
input bytes
  │
  ├─► Split each byte into [hi-nibble, lo-nibble]        (2 nibbles per byte)
  │
  ├─► Build 12-bit context from previous 3 nibbles       (4096 contexts max)
  │
  └─► Arithmetic-code each nibble against its context's
      predicted distribution.

  Optional: 12-bit context prior (k12) loaded from
  embedded resource at startup — skips the warmup phase
  on small files. ~128 KB resource. Falls back to
  bare-init if the resource isn't present.
```

**BEP integration:** Headers and metadata via `BepIntCoding`. The
arithmetic-coder body itself is not a BEP bit-walk; it's a standard
range coder. NibCtx3 is part of the BEP **codec family** by virtue of
its magic-ID namespace, frame layout, and shared `BepIntCoding`
primitives — not because the entropy coder is a BEP walk.

**When it wins:** mid- to large-alphabet inputs with strong order-1 or
order-2 byte-level context (most binaries, structured data, mixed
content). Converges fast — useful even at 1–2 KB chunk sizes.

**Per-variant subcommands:** none.

---

### ProtNibCtx3 — NibCtx3 with amino-acid prior

**Class:** `ProteinNibbleContextOrder3Bep` &nbsp;·&nbsp; **Magic:** `0x5005`

Same pipeline as NibCtx3, but the order-3 nibble context model starts
from an **embedded static prior trained on protein/amino-acid data**
rather than a flat distribution. The prior is compiled directly into
the binary via an embedded byte resource.

```
input bytes
  │
  ├─► (same splitting + 12-bit-context pipeline as NibCtx3)
  │
  └─► Arithmetic-code against a model initialized from
      a pre-trained amino-acid prior, then adapted online.
```

**BEP integration:** identical to NibCtx3 (BepIntCoding for headers; no
BEP walk in the body).

**When it wins:** protein sequences, DNA/RNA, small-alphabet biological
data (alphabet ≤ 32 with strong order-1 signal). On non-protein inputs
it underperforms NibCtx3 by 1–5 percentage points because the protein
prior biases the model away from the input's true distribution; the
`auto` strategy and the routing predictor both skip ProtNibCtx3 when
the alphabet exceeds 32.

**Per-variant subcommands:** none.

---

### ArithCtx — Order-2 byte context, range-coded

**Class:** `ArithmeticContextBep` &nbsp;·&nbsp; **Magic:** `0x5003`

```
input bytes
  │
  ├─► Build 16-bit context from previous 2 bytes         (65,536 contexts)
  │
  └─► Range-code each byte against its context's
      predicted distribution.
```

**BEP integration:** headers via `BepIntCoding`; range coder body is not
a BEP walk. Same family-membership relationship as NibCtx3.

**When it wins:** the safe-default fallback. No structural prejudice —
gives reasonable compression on anything. The structural-fingerprint
predictor selects ArithCtx whenever no other variant has a dominant
structural signal. Particularly competitive on inputs in the 16 KB–1 MB
range where the 16-bit context has time to converge but the input is
too small for BWT-based variants to amortize the block-cost overhead.

**Per-variant subcommands:** none.

---

### StrideSplit — Even/odd byte split + per-stream context

**Class:** `StrideSplitBep` &nbsp;·&nbsp; **Magic:** `0x2002`

```
input bytes  [b0, b1, b2, b3, b4, b5, ...]
  │
  ├─► Deinterleave into two streams:
  │     even = [b0, b2, b4, ...]
  │     odd  = [b1, b3, b5, ...]
  │
  ├─► Encode each stream separately with NibbleContextBep
  │   (order-2 nibble context, arithmetic-coded)
  │
  └─► Concatenate both encoded streams + per-stream length headers.
```

**BEP integration:** the per-stream encoders are `NibbleContextBep`,
which is arithmetic-coding, not BEP-walk. Headers via `BepIntCoding`.

**When it wins:** any input where adjacent byte pairs come from
different distributions — most often 16-bit-sample data (medical imaging
TIFFs, raw sensor readings, audio samples, integer arrays). The
structural fingerprint exposes this via the **stride-2 entropy delta**
(`s2dH`):

```
s2dH = H₀(input) − ½·( H₀(even-stream) + H₀(odd-stream) )
```

Positive `s2dH` means splitting reduces order-0 entropy — exactly the
structural signal StrideSplit can monetize. Often `> 0.3` bits per byte
on 16-bit sensor data. The routing predictor recommends StrideSplit
when `s2dH ≥ 0.20`.

**Per-variant subcommands:** none.

---

## Raw BEP pipeline (`--variant bep`)

`--variant bep` bypasses the five named variants and exposes
`BEPPipeline.Compress` directly. This is the lowest-level entry point —
useful for research, for files where you know the best parameters from
prior runs, or for outputs you intend to wrap in a further layer.

```
inBEP compress <input> --variant bep [tuning options]
```

| Option              | Meaning                                                                          |
|---------------------|----------------------------------------------------------------------------------|
| `--bwt-block <N>`   | BWT block size in bytes. Power of two recommended: `65536`, `131072`, `262144`, `524288`, `1048576`. `0` = mode default. `-1` (or `auto`) = run `DynamicBwt.Probe` to pick the largest block within 1% of the best MTF-zero fraction. |
| `--mode <name>`     | Pipeline mode: `default` / `A` / `B` / `D` — see table below.                    |
| `--passes <N>`      | Iterative compression passes (default 1). Each pass re-feeds the previous BEP archive as input. Auto-stops when a pass saves less than 5%. |
| `--no-huffman-wrap` | Disable the per-pass canonical-Huffman wrap on BEP output. Useful when you intend to wrap the result in a further entropy coder (DEFLATE, LZMA, etc.) — the Huffman wrap converts patterned bytes into entropy-coded bits that LZ77 can no longer match. |
| `--no-runa`         | Disable the RUNA/RUNB rank-0 run-length transform. Less destructive than the Huffman wrap w.r.t. downstream coders, but worth disabling for the strictest "vanilla BEP" path. |

### Mode reference

| Mode      | Pipeline                                                                                                   |
|-----------|------------------------------------------------------------------------------------------------------------|
| `default` | BWT (64 KB blocks) + MTF + Re-Pair + FreqRank + BEP-coding                                                 |
| `A`       | BWT (512 KB blocks) + MTF + Re-Pair + FreqRank + BEP-coding. Larger blocks → longer MTF runs → more zeros → better ratio. Slower (`O(n log n)` suffix array on bigger blocks). |
| `B`       | `default` + PPM-3 rank transform between MTF and Re-Pair. PPM-3 predicts the next MTF rank from the previous three; on zero-heavy MTF streams it converts ~75–85% of values to rank-0, which Re-Pair and BEP-coding then exploit. Adds a sequential pass but improves the ratio. |
| `D`       | 7-bit ASCII alphabet — grammar symbols start at 128 instead of 256. Best on pure or near-pure ASCII (JSON, English XML, code). Auto-falls-back to `default` if non-ASCII byte count exceeds the threshold. |

### Example tunings

```
# Large coherent text (multi-MB) — bigger BWT block + iterative passes
inBEP compress book.txt --variant bep --bwt-block 524288 --mode A --passes 3

# Pure ASCII source code — 7-bit alphabet
inBEP compress source.tar --variant bep --mode D

# Auto-pick block size via DynamicBwt probe
inBEP compress unknown.bin --variant bep --bwt-block -1

# Hand the BEP archive bytes to LZMA next (strip Huffman wrap and RUNA)
inBEP compress mixed.dat --variant bep --no-huffman-wrap --no-runa
```

---

## The `.bep` file format

`.bep` is a thin frame holding a single variant's encoded output. Layout
(little-endian):

```
offset  size       field
------  ---------  --------------------------------------------------------
  0       4        magic "BEPZ"  (0x42 0x45 0x50 0x5A)
  4       1        format version (currently 1)
  5       1        variant name length L (1..63)
  6       L        variant name, ASCII (e.g. "textctx", "nibctx3", "bep")
  6+L     4        original (decompressed) length, uint32
 10+L     4        payload length N, uint32
 14+L     N        payload — the variant's Encode() output, byte-for-byte
```

Header overhead is `14 + L` bytes — typically **21–26 bytes** for the
current variant names. There is no checksum: each variant's `Decode()`
is deterministic and surfaces corruption as either a decode-time
exception or a length mismatch against the stored `OriginalLength`
field, which `decompress` checks before writing the output.

`inBEP info <file.bep>` prints the parsed header without running the
decoder — useful for checking which variant produced a given archive
without paying the decode cost.

---

## Build from source

Requirements:
- .NET 8 SDK
- No native libraries (the previous Joveler / liblzma / LZMA-SDK / ZstdSharp
  dependencies, used only by the reference-codec research path, are gone)

```
git clone https://github.com/bepCreator/inBEP inBEP
cd inBEP
dotnet build -c Release
```

Run from the build output:
```
./bin/Release/net8.0/inBEP help
```

Or publish a self-contained executable:
```
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r win-x64   --self-contained
dotnet publish -c Release -r osx-arm64 --self-contained
```

### Optional embedded priors

If you have `nibble_prior_k12.bin` and/or `textctx_nibble_prior_k12.bin`
from a prior training run, drop them in the project root before
building. The `.csproj` picks them up conditionally — present they are
embedded; absent the build is unaffected and NibCtx3 falls back to a
bare-init warmup path.

### Optional capture environment variables

Three environment variables enable opt-in stream captures for offline
prior training. None affect output:

```
BEP_DUMP_PATHS=<file>            # Dump BEP path bits emitted by the encoder
NIB_DUMP_NIBBLES=<file>          # Dump nibble streams (raw NibCtx3 path)
TEXTCTX_NIB_DUMP_NIBBLES=<file>  # Dump nibble streams (TextCtx post-coder path)
```

These exist for trainer pipelines that build new static priors; for
normal compression they should remain unset.

---

## Project layout

```
inBEP/
├── Program.cs                       Entry-point dispatcher
├── CliCommands.cs                   compress / decompress / info handlers
├── BepArchive.cs                    .bep file container (read/write/header)
├── BEPPipeline.cs                   The BEP pipeline (BWT/MTF/RePair/BEP-code)
├── DynamicBwt.cs                    BWT block-size auto-probe
├── BepIntCoding.cs                  Magnitude-class integer encoding
├── VariantMagicIds.cs               16-bit per-variant magic constants
├── DiagnosticTimings.cs             Per-function timing accumulator
├── StructuralDiagnostics.cs         Structural fingerprint + variant predictor
│
├── TextPipelineCtxBep.cs            Variant: TextCtx
├── NibbleContextOrder3Bep.cs        Variant: NibCtx3
├── ProteinNibbleContextOrder3Bep.cs Variant: ProtNibCtx3
├── ArithmeticContextBep.cs          Variant: ArithCtx
├── StrideSplitBep.cs                Variant: StrideSplit
│
├── NibbleContextBep.cs              Internal: nibble-level range coder
├── BepChainPass2.cs                 Internal: BEP-chain entropy coder
├── BepChainTextBep.cs               Internal: TextCtx's BepChain wrapper
├── Lz77BepV3.cs                     Internal: LZ77 candidate inside TextCtx
├── DictPreLz77V3Bep.cs              Internal: dictionary-pre-LZ77 candidate
├── DictionaryPreprocessorBep.cs     Internal: TextCtx dictionary preprocessor
├── NibblePriorLoader.cs             Internal: embedded-prior loader
├── RiceByteHuffman.cs               Internal: byte-level Huffman wrap
├── RunaRunbTransform.cs             Internal: rank-0 RLE transform
├── BitIO.cs / BitIOExtensions.cs    Internal: MSB-first bit-level I/O
│
├── Bep{,Chain,Histogram,Picker}Diagnostics.cs   Stubs + DiagnosticTimings
├── {Nibble,TextCtxNibble}StreamCapture.cs       Opt-in capture (env-var gated)
├── BepPathCapture.cs                            Opt-in BEP-bit capture
│
└── inBEP.csproj                     net8.0, no NuGet dependencies
```

---

## License

Copyright 2026 Rich Wagner — [newdawndata.com](https://newdawndata.com)

Licensed under the Apache License, Version 2.0. You may obtain a copy
of the License at <http://www.apache.org/licenses/LICENSE-2.0>.

Unless required by applicable law or agreed to in writing, software
distributed under this license is distributed on an **"AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND**, either express or
implied. See the License for the specific language governing
permissions and limitations.

---

## See also

- **[`bepCreator/ndd`](https://github.com/bepCreator/ndd)** — Binary Equation Paths.
  The mathematical foundation: definition of the BEP walk, proof of
  convergence, bit-level worked examples, endianness handling, and the
  unified `BEP.Compress` / `BEP.Decompress` entry points used as a
  building block here.
- **[newdawndata.com](https://newdawndata.com)** — project home.
