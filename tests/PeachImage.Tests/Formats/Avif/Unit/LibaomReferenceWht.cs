namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own <c>av1_fwht4x4_c</c>
/// (<c>av1/encoder/hybrid_fwd_txfm.c</c>) -- the real 4-point reversible Walsh-Hadamard forward transform AV1
/// lossless coding uses, kept deliberately separate from <see cref="PeachImage.Formats.Avif.Encoder.Av1.Av1ForwardWht"/>'s
/// own implementation so a test comparing the two is a genuine check against libaom's own real algorithm,
/// not merely a check that PeachImage's own forward/inverse pair are each other's algebraic inverse (which
/// a self-consistently-wrong pair could still pass). Takes an explicit <paramref name="stride"/> on
/// <see cref="Fwht4x4"/>, matching libaom's own real signature and its own test's deliberate use of a
/// non-4 stride ("to catch cases where the transforms use the stride incorrectly", per
/// <c>transform_test_base.h</c>'s own <c>RunCoeffCheck</c>/<c>RunMemCheck</c> remarks).
/// </summary>
internal static class LibaomReferenceWht
{
    private const int UnitQuantFactor = 4; // UNIT_QUANT_FACTOR (1 << UNIT_QUANT_SHIFT), aom_dsp/txfm_common.h.

    public static void Fwht4x4(ReadOnlySpan<short> input, Span<int> output, int stride)
    {
        Span<int> intermediate = stackalloc int[16];

        // Pass 0: reads down each column (stride-separated), writes row-major into intermediate -- matches
        // av1_fwht4x4_c's own ip_pass0/op pointer walk (ip_pass0++ each outer iteration, op += 4).
        for (int i = 0; i < 4; i++)
        {
            long a1 = input[(0 * stride) + i];
            long b1 = input[(1 * stride) + i];
            long c1 = input[(2 * stride) + i];
            long d1 = input[(3 * stride) + i];

            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;

            int opBase = i * 4;
            intermediate[opBase + 0] = (int)a1;
            intermediate[opBase + 1] = (int)c1;
            intermediate[opBase + 2] = (int)d1;
            intermediate[opBase + 3] = (int)b1;
        }

        // Pass 1: reads column i of intermediate (av1_fwht4x4_c's own ip++ pointer walk, ip[4*0..4*3] at
        // ip = intermediate + i). av1_fwht4x4_c's own raw write (op[4*0..4*3] at op = output + i) lands in
        // COLUMN i of a stride-4 buffer, not row i -- confirmed empirically, not just by re-deriving the
        // pointer arithmetic: an earlier version of this method matched that raw layout exactly and produced
        // a real, measured transpose (round-tripping this method's own output through
        // Av1InverseTransform.Inverse2D -- this project's own already-verified decoder-side WHT -- gave back
        // the TRANSPOSE of the true original residual, e.g. [1,5,9,13,2,6,...] instead of [1,2,3,...] for a
        // simple 1..16 input). Writing row i here instead (this method's own public contract, not libaom's
        // raw byte layout) makes the output directly, sensibly comparable against Av1ForwardWht.Forward4x4's
        // own row-major convention with no separate transpose step needed at any call site.
        for (int i = 0; i < 4; i++)
        {
            long a1 = intermediate[(4 * 0) + i];
            long b1 = intermediate[(4 * 1) + i];
            long c1 = intermediate[(4 * 2) + i];
            long d1 = intermediate[(4 * 3) + i];

            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;

            int opBase = i * 4;
            output[opBase + 0] = (int)(a1 * UnitQuantFactor);
            output[opBase + 1] = (int)(c1 * UnitQuantFactor);
            output[opBase + 2] = (int)(d1 * UnitQuantFactor);
            output[opBase + 3] = (int)(b1 * UnitQuantFactor);
        }
    }
}
