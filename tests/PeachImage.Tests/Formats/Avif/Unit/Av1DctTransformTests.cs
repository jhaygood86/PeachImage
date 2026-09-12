using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Ports libaom's own DCT transform tests (<c>test/av1_inv_txfm2d_test.cc</c>'s <c>AV1InvTxfm2d</c>/
/// <c>AV1LbdInvTxfm2d</c> fixtures, and indirectly <c>test/av1_fwd_txfm2d_test.cc</c>'s companion forward
/// fixture -- see the remarks below on why this port targets the inverse side) against
/// <see cref="Av1InverseTransform"/>, the decoder-side component real lossy AVIF decoding actually depends on.
///
/// <para><b>Scope, and why this targets <see cref="Av1InverseTransform.InverseDct"/> rather than
/// <see cref="Av1ForwardTransform"/>:</b> <c>test/av1_txfm_test.cc</c> (read in full before writing this file)
/// is shared infrastructure for the whole libaom transform test family -- a double-precision naive reference
/// DCT/ADST/IDTX (<c>reference_dct_1d</c>/<c>reference_hybrid_2d</c>) that the real fixed-point forward/inverse
/// libaom functions are checked against for <em>approximate</em> agreement (<c>fdct4x4_test.cc</c>'s own
/// <c>RunCoeffCheck</c> is the one bit-exact case, but it exercises <c>aom_fdct4x4_c</c>, a legacy standalone
/// aom_dsp transform -- not the <c>av1_fwd_txfm2d</c>/<c>av1_inv_txfm2d</c> family this project's
/// <c>Av1InverseTransform</c>/<c>Av1ForwardTransform</c> actually implement, so that particular test has no
/// PeachImage counterpart to port). <see cref="PeachImage.Formats.Avif.Encoder.Av1.Av1ForwardTransform"/>'s own
/// class remarks establish that it is <em>not</em> an independent transcription of libaom's real forward DCT at
/// all: it numerically constructs its operator as the matrix inverse of <see cref="Av1InverseTransform"/>'s own
/// row/column operator, built once at class-init time by probing <c>InverseDct</c> with impulse vectors. A test
/// comparing <c>Av1ForwardTransform</c>'s output against a real libaom forward-DCT reference would therefore
/// really just be re-testing <c>Av1InverseTransform</c> a second, more indirect way (through a numerically-built
/// pseudo-inverse) -- so this file goes straight to the real target: an independent, bit-exact-by-construction
/// C# transcription of libaom's actual <c>av1_idct4</c>/<c>av1_idct8</c>/<c>av1_idct16</c> fixed-point C
/// functions (<see cref="LibaomReferenceDct"/>, ported fresh from <c>av1/common/av1_inv_txfm1d.c</c>, not
/// derived from <see cref="Av1InverseTransform"/>'s own spec-based generic-butterfly formulation), cross-checked
/// directly against <see cref="Av1InverseTransform.InverseDct"/> and, at the 2D level, against
/// <see cref="Av1InverseTransform.Inverse2D"/> for the DCT_DCT case -- this project's own already-verified
/// decoder path for every real lossy AVIF file it decodes.
///
/// <para><b>Size scope:</b> DCT_DCT at 4x4 (matching this project's WHT precedent's own starting scope) plus
/// 8x8 and 16x16 (this port's "expand to a representative set of other square sizes" allowance) -- 32x32 and
/// 64x64 are excluded: libaom's real <c>av1_idct32</c>/<c>av1_idct64</c> are each several hundred lines of
/// hand-unrolled butterfly stages, and porting them would roughly double this file's size for two sizes that
/// exercise the exact same <c>half_btf</c>/<c>clamp_value</c>/permutation primitives already covered
/// bit-exactly by the 4/8/16 sizes below -- diminishing verification value for a large transcription-risk cost,
/// consistent with this project's established practice of scoping libaom's own exhaustive size/type sweeps down
/// to a representative subset.</para>
///
/// <para>Tx type scope: DCT_DCT only (both row and column passes route through <c>InverseDct</c>/
/// <c>av1_idct*</c> for this type at every square size) -- ADST/IDTX are separate butterfly networks not
/// exercised here; porting them is out of scope for this DCT-focused pass.</para>
/// </summary>
public class Av1DctTransformTests
{
    /// <summary>
    /// 1D-level bit-exact check: <see cref="LibaomReferenceDct"/>'s independent port of libaom's real
    /// <c>av1_idct4</c>/<c>8</c>/<c>16</c> against <see cref="Av1InverseTransform.InverseDct"/>'s spec-based
    /// generic butterfly network, for <see cref="LibaomAcmRandom"/>-seeded random coefficient vectors. <c>r =
    /// 16</c> matches <see cref="Av1InverseTransform.Inverse2D"/>'s own clamp range at 8-bit depth (both
    /// <c>rowClampRange = bitDepth + 8</c> and <c>colClampRange = max(bitDepth + 6, 16)</c> equal 16 when
    /// <c>bitDepth == 8</c>), and per <see cref="LibaomReferenceDct"/>'s own class remarks, using this single
    /// uniform clamp range in both implementations (rather than libaom's real per-stage <c>stage_range[]</c>
    /// table) means any bit-exact disagreement is a genuine transcription bug, not a clamp-width mismatch.
    /// </summary>
    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    [InlineData(16, 4)]
    public void InverseDct_MatchesLibaomReference_ForRandomCoefficients(int size, int log2Size)
    {
        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);
        const int r = 16;

        for (int trial = 0; trial < 500; trial++)
        {
            var coeffs = new int[size];
            for (int i = 0; i < size; i++)
            {
                // Signed 16-bit-magnitude coefficients: a realistic post-dequantization coefficient range,
                // comfortably inside [-2^(r-1), 2^(r-1)) so this test explores real transform behavior rather
                // than only the clamp-saturation edge case.
                coeffs[i] = rnd.Rand16Signed();
            }

            var expected = new int[size];
            RunReference(size, coeffs, expected, r);

            var actual = (int[])coeffs.Clone();
            Av1InverseTransform.InverseDct(actual, log2Size, r);

            for (int i = 0; i < size; i++)
            {
                Assert.True(
                    expected[i] == actual[i],
                    $"size {size}, trial {trial}, index {i}: libaom reference={expected[i]}, PeachImage={actual[i]}.");
            }
        }
    }

    [Fact]
    public void InverseDct_MatchesLibaomReference_ForZeroInput()
    {
        foreach (var (size, log2Size) in new[] { (4, 2), (8, 3), (16, 4) })
        {
            var coeffs = new int[size];
            var expected = new int[size];
            RunReference(size, coeffs, expected, 16);

            var actual = (int[])coeffs.Clone();
            Av1InverseTransform.InverseDct(actual, log2Size, 16);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void InverseDct_MatchesLibaomReference_ForExtremeMagnitudeInput()
    {
        foreach (var (size, log2Size) in new[] { (4, 2), (8, 3), (16, 4) })
        {
            // Extreme case, mirroring av1_inv_txfm2d_test.cc's own RunRoundtripCheck ci==0 special case
            // (int16_max-ish extreme coefficients), still safely inside the r=16 clamp range so both
            // implementations exercise the same clamp arithmetic rather than diverging on an out-of-range
            // input neither is contractually required to handle identically.
            var coeffs = new int[size];
            Array.Fill(coeffs, short.MaxValue);
            var expected = new int[size];
            RunReference(size, coeffs, expected, 16);

            var actual = (int[])coeffs.Clone();
            Av1InverseTransform.InverseDct(actual, log2Size, 16);
            Assert.Equal(expected, actual);

            Array.Fill(coeffs, short.MinValue);
            RunReference(size, coeffs, expected, 16);
            actual = (int[])coeffs.Clone();
            Av1InverseTransform.InverseDct(actual, log2Size, 16);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 2D-level bit-exact check for DCT_DCT: reproduces exactly what <see cref="Av1InverseTransform.Inverse2D"/>
    /// does internally (row pass via <c>InverseDct</c>/row shift, clamp, column pass via <c>InverseDct</c>/
    /// column shift) but using <see cref="LibaomReferenceDct"/>'s independent row/column primitives instead of
    /// <see cref="Av1InverseTransform.InverseDct"/> -- so this exercises the full 2D pipeline PeachImage's real
    /// decoder invokes for every non-lossless DCT_DCT block, not just the isolated 1D primitive.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Inverse2D_DctDct_MatchesLibaomReference_ForRandomCoefficients(int size)
    {
        int txSz = size switch
        {
            4 => Av1TxSize.Tx4x4,
            8 => Av1TxSize.Tx8x8,
            _ => Av1TxSize.Tx16x16,
        };
        int log2Size = size switch { 4 => 2, 8 => 3, _ => 4 };
        int rowShift = size switch { 4 => 0, 8 => 1, _ => 2 }; // Transform_Row_Shift[txSz], bitDepth=8.
        const int colShift = 4;
        const int r = 16; // bitDepth(8)+8 == max(bitDepth(8)+6, 16) == 16 for both passes.

        var rnd = new LibaomAcmRandom(LibaomAcmRandom.DeterministicSeed);

        for (int trial = 0; trial < 100; trial++)
        {
            var dequant = new int[64 * 64];
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    dequant[(i * 64) + j] = rnd.Rand16Signed();
                }
            }

            // -- Independent reference: row pass, round2(rowShift), clamp to [-2^(r-1), 2^(r-1)-1], column pass, round2(colShift).
            var expected = new int[size * size];
            var rowBuf = new int[size];
            var rowOut = new int[size];
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    rowBuf[j] = dequant[(i * 64) + j];
                }

                RunReference(size, rowBuf, rowOut, r);

                for (int j = 0; j < size; j++)
                {
                    expected[(i * size) + j] = Round2(rowOut[j], rowShift);
                }
            }

            int bound = 1 << (r - 1);
            for (int i = 0; i < expected.Length; i++)
            {
                expected[i] = Math.Clamp(expected[i], -bound, bound - 1);
            }

            var colBuf = new int[size];
            var colOut = new int[size];
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    colBuf[i] = expected[(i * size) + j];
                }

                RunReference(size, colBuf, colOut, r);

                for (int i = 0; i < size; i++)
                {
                    expected[(i * size) + j] = Round2(colOut[i], colShift);
                }
            }

            // -- PeachImage's real decoder path.
            var actual = new int[size * size];
            Av1InverseTransform.Inverse2D(dequant, actual, txSz, Av1TxType.DctDct, lossless: false, bitDepth: 8);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(
                    expected[i] == actual[i],
                    $"size {size}, trial {trial}, index {i}: libaom reference={expected[i]}, PeachImage={actual[i]}.");
            }
        }
    }

    private static void RunReference(int size, ReadOnlySpan<int> input, Span<int> output, int r)
    {
        switch (size)
        {
            case 4:
                LibaomReferenceDct.Idct4(input, output, r);
                break;
            case 8:
                LibaomReferenceDct.Idct8(input, output, r);
                break;
            case 16:
                LibaomReferenceDct.Idct16(input, output, r);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(size), size, "Only sizes 4, 8, 16 are ported.");
        }
    }

    /// <summary><c>Round2(x, n)</c> (spec §4.7), matching <see cref="Av1InverseTransform"/>'s own private helper.</summary>
    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);
}
