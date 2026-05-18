// =============================================================================
// RunaRunbTransform — bzip2-style RLE2 on a rank stream
//
// V15 NEW. Operates on the rank stream produced by FreqRank, BEFORE the BEP
// entropy coder. Replaces runs of rank-0 with a logarithmic-length RUNA/RUNB
// encoding, then shifts non-zero ranks up by 1 to make room.
//
// WHY:
//   FreqRank output on text-shape data is heavily dominated by rank-0
//   (typically 50-80% of symbols). RiceBEP encodes each rank-0 in 2-3 bits
//   regardless of how many consecutive rank-0s there are. RUNA/RUNB collapses
//   a run of N rank-0s into ⌊log₂(N+1)⌋ symbols. Combined with the new
//   distribution being re-FreqRanked downstream, the entropy coder sees
//   a much flatter, smaller stream.
//
// HOW (encoding):
//   Walk the rank stream. For each maximal run of rank-0 of length L,
//   emit a sequence of RUNA(0)/RUNB(1) tokens using "bijective base 2":
//
//     while L > 0:
//       if L is odd:  emit RUNA (0); L = (L-1) / 2
//       else:          emit RUNB (1); L = L/2 - 1
//
//   For each non-zero rank R, emit (R + 1) — shift up by 1 to clear the
//   {0, 1} slots for RUNA/RUNB.
//
// HOW (decoding):
//   Walk the transformed stream.
//   Consecutive {0, 1} tokens form a run-length count:
//     L = 0; mult = 1
//     while next symbol is 0 or 1:
//       d = (symbol == 0) ? 1 : 2
//       L += d * mult
//       mult *= 2
//     emit L zeros
//   Other symbols (>= 2) emit (symbol - 1).
//
// ROUND-TRIP:
//   Verified in /home/claude/runa_runb_test.py against 13 hand-built cases
//   plus randomized realistic distributions. All pass.
//
// NUMERIC RESULT (Python sim, on synthetic Zipf rank-0 = 55%):
//   - 21-23% length reduction in the symbol stream itself
//   - On heavy-zero distributions (rank-0 = 90%): 67% reduction
//   - Final compression effect after BEP depends on the resulting
//     distribution; measured per-pass at runtime.
//
// Author: Rich Wagner / Claude — newdawndata.com — Apache 2.0
// =============================================================================

using System;
using System.Collections.Generic;

namespace InBep.Core;

public static class RunaRunbTransform
{
    /// <summary>RUNA token value in the transformed stream.</summary>
    public const int RUNA = 0;
    /// <summary>RUNB token value in the transformed stream.</summary>
    public const int RUNB = 1;

    /// <summary>
    /// Apply the RUNA/RUNB transform to a rank stream. Runs of value 0 are
    /// collapsed; non-zero values are shifted up by 1 to make room.
    /// </summary>
    public static int[] Apply(int[] ranks)
    {
        if (ranks == null) throw new ArgumentNullException(nameof(ranks));
        if (ranks.Length == 0) return Array.Empty<int>();

        // Worst-case output size = input size (no zeros to collapse).
        // Best case is much smaller. Pre-allocate at input size.
        var output = new List<int>(ranks.Length);

        int i = 0;
        int n = ranks.Length;
        while (i < n)
        {
            if (ranks[i] == 0)
            {
                // Count the run length.
                int run = 0;
                while (i < n && ranks[i] == 0)
                {
                    run++;
                    i++;
                }

                // Encode run length in bijective base 2, LSB first.
                while (run > 0)
                {
                    if ((run & 1) == 1)
                    {
                        output.Add(RUNA);
                        run = (run - 1) / 2;
                    }
                    else
                    {
                        output.Add(RUNB);
                        run = run / 2 - 1;
                    }
                }
            }
            else
            {
                // Non-zero rank: shift up by 1.
                output.Add(ranks[i] + 1);
                i++;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Inverse of Apply. Returns the original rank stream.
    /// </summary>
    public static int[] Inverse(int[] transformed)
    {
        if (transformed == null) throw new ArgumentNullException(nameof(transformed));
        if (transformed.Length == 0) return Array.Empty<int>();

        // Output can be much larger than input (each {0,1} token expands to
        // potentially many zeros). We don't know the size in advance.
        var output = new List<int>(transformed.Length * 2);

        int i = 0;
        int n = transformed.Length;
        while (i < n)
        {
            int v = transformed[i];
            if (v <= RUNB)   // 0 or 1 → start of a zero-run encoding
            {
                long run = 0;
                long mult = 1;
                while (i < n && transformed[i] <= RUNB)
                {
                    int d = (transformed[i] == RUNA) ? 1 : 2;
                    run += d * mult;
                    mult *= 2;
                    i++;
                }
                // Defensive: cap run at int.MaxValue. In valid streams this
                // should never trip (rank streams aren't billions long).
                if (run > int.MaxValue)
                    throw new InvalidDataException(
                        $"RunaRunb: run length {run} exceeds int.MaxValue (corrupt stream?)");
                for (int j = 0; j < (int)run; j++)
                    output.Add(0);
            }
            else
            {
                output.Add(v - 1);
                i++;
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Quick estimate: how many tokens would the transform produce on this
    /// stream, without building the output array? Used by the picker to
    /// decide whether the transform is worth applying.
    /// </summary>
    public static long EstimateLength(int[] ranks)
    {
        if (ranks == null || ranks.Length == 0) return 0;
        long count = 0;
        int i = 0;
        int n = ranks.Length;
        while (i < n)
        {
            if (ranks[i] == 0)
            {
                int run = 0;
                while (i < n && ranks[i] == 0) { run++; i++; }
                // Number of bits in bijective base-2 representation of `run`
                int r = run;
                while (r > 0)
                {
                    count++;
                    if ((r & 1) == 1) r = (r - 1) / 2;
                    else              r = r / 2 - 1;
                }
            }
            else
            {
                count++;
                i++;
            }
        }
        return count;
    }
}
