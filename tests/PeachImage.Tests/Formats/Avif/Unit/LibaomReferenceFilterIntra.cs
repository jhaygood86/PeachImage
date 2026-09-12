namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Direct, independent transcription of libaom's own <c>av1_filter_intra_predictor_c</c> and its
/// <c>av1_filter_intra_taps</c> table (<c>av1/common/reconintra.c</c>), kept deliberately separate from
/// <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1IntraPrediction"/>'s own private
/// <c>PredictRecursive</c> so a test comparing the two is a genuine check against libaom's own real
/// algorithm, not a self-consistency check against a single spec-text transcription.
/// <c>Av1IntraPrediction</c> was built directly from the AV1 spec's own §7.11.2.3 "Recursive intra
/// prediction process" text (working from a <c>p[0..6]</c> array gathered via index arithmetic into
/// <c>AboveRow</c>/<c>LeftCol</c>/already-written prediction samples), not from libaom's C source (which
/// instead fills a single <c>uint8_t buffer[33][33]</c> scratch grid up front and reads/writes directly
/// into it) -- so this is a real second, independent source, not a restatement of the same one.
///
/// <para>libaom's own real reference operates on <c>uint8_t</c> pixels/taps throughout (a mode's 7 taps
/// per sub-position always sum to exactly 16, so the weighted sum <c>pr</c> can go negative when the
/// current-position pixel dominates a negatively-weighted tap, but never overflows <c>int</c>); this
/// transcription keeps the same <see langword="int"/>-accumulation width libaom's own C uses rather than
/// PeachImage's own <see langword="long"/> accumulator, since 7 taps in [-12, 16] times 8-bit pixels stays
/// far inside <see langword="int"/> range.</para>
///
/// <para>libaom's own real rounding step is <c>clip_pixel(ROUND_POWER_OF_TWO(pr, FILTER_INTRA_SCALE_BITS))</c>
/// -- a plain arithmetic-shift Round2, not the sign-magnitude Round2Signed the AV1 spec text (and
/// <c>Av1IntraPrediction.PredictRecursive</c>) call for. libaom's own source comment at this exact line
/// explains why that's still correct: the spec's <c>Clip1(Round2Signed(pr, ...))</c> and libaom's own
/// <c>clip_pixel(Round2(pr, ...))</c> only disagree on negative <c>pr</c>, and both a negative
/// <c>Round2Signed</c> result and a negative-or-zero <c>Round2</c> result clip to the same 0 -- so this
/// transcription reproduces libaom's own real <c>Round2</c>-then-clip exactly (via C#'s own arithmetic
/// right shift on <see langword="int"/>, matching C's shift on this platform) rather than "fixing" it to
/// match the spec's <c>Round2Signed</c> wording, because that would stop being a faithful port of the real
/// reference function under test.</para>
/// </summary>
internal static class LibaomReferenceFilterIntra
{
    private const int FilterIntraScaleBits = 4;

    /// <summary><c>av1_filter_intra_taps[FILTER_INTRA_MODES][8][8]</c> (<c>av1/common/reconintra.c</c>),
    /// indexed <c>[mode][k]</c> giving the 7 real nonzero taps <c>[p0..p6]</c> for sub-position <c>k</c>
    /// (libaom's own real table declares an 8th tap column that is 0 in every one of the 40 rows below --
    /// dropped here exactly as PeachImage's own <c>IntraFilterTaps</c> already drops it, since carrying an
    /// always-0 column changes no value).
    /// <c>mode</c>: 0 = FILTER_DC_PRED, 1 = FILTER_V_PRED, 2 = FILTER_H_PRED, 3 = FILTER_D157_PRED,
    /// 4 = FILTER_PAETH_PRED.</summary>
    private static readonly int[][][] FilterIntraTaps =
    [
        [
            [-6, 10, 0, 0, 0, 12, 0],
            [-5, 2, 10, 0, 0, 9, 0],
            [-3, 1, 1, 10, 0, 7, 0],
            [-3, 1, 1, 2, 10, 5, 0],
            [-4, 6, 0, 0, 0, 2, 12],
            [-3, 2, 6, 0, 0, 2, 9],
            [-3, 2, 2, 6, 0, 2, 7],
            [-3, 1, 2, 2, 6, 3, 5],
        ],
        [
            [-10, 16, 0, 0, 0, 10, 0],
            [-6, 0, 16, 0, 0, 6, 0],
            [-4, 0, 0, 16, 0, 4, 0],
            [-2, 0, 0, 0, 16, 2, 0],
            [-10, 16, 0, 0, 0, 0, 10],
            [-6, 0, 16, 0, 0, 0, 6],
            [-4, 0, 0, 16, 0, 0, 4],
            [-2, 0, 0, 0, 16, 0, 2],
        ],
        [
            [-8, 8, 0, 0, 0, 16, 0],
            [-8, 0, 8, 0, 0, 16, 0],
            [-8, 0, 0, 8, 0, 16, 0],
            [-8, 0, 0, 0, 8, 16, 0],
            [-4, 4, 0, 0, 0, 0, 16],
            [-4, 0, 4, 0, 0, 0, 16],
            [-4, 0, 0, 4, 0, 0, 16],
            [-4, 0, 0, 0, 4, 0, 16],
        ],
        [
            [-2, 8, 0, 0, 0, 10, 0],
            [-1, 3, 8, 0, 0, 6, 0],
            [-1, 2, 3, 8, 0, 4, 0],
            [0, 1, 2, 3, 8, 2, 0],
            [-1, 4, 0, 0, 0, 3, 10],
            [-1, 3, 4, 0, 0, 4, 6],
            [-1, 2, 3, 4, 0, 4, 4],
            [-1, 2, 2, 3, 4, 3, 3],
        ],
        [
            [-12, 14, 0, 0, 0, 14, 0],
            [-10, 0, 14, 0, 0, 12, 0],
            [-9, 0, 0, 14, 0, 11, 0],
            [-8, 0, 0, 0, 14, 10, 0],
            [-10, 12, 0, 0, 0, 0, 14],
            [-9, 1, 12, 0, 0, 0, 12],
            [-8, 0, 0, 12, 0, 1, 11],
            [-7, 0, 0, 1, 12, 1, 9],
        ],
    ];

    /// <summary><c>av1_filter_intra_predictor_c</c> (<c>av1/common/reconintra.c:860-905</c>).
    /// <paramref name="above"/> holds <c>bw</c> samples (libaom's own real <c>above[0..bw-1]</c>, i.e. the
    /// caller-side <c>&amp;above[1]</c> the real test harness passes -- <paramref name="topLeft"/> is
    /// libaom's own real <c>above[-1]</c>, kept as a separate parameter since C#'s <see cref="Span{T}"/>
    /// can't be negatively indexed the way libaom's own real pointer arithmetic can).
    /// <paramref name="left"/> holds <c>bh</c> samples. <paramref name="dst"/> is row-major, stride
    /// <paramref name="bw"/>, length <c>bw * bh</c>.</summary>
    public static void Predict(ReadOnlySpan<int> above, ReadOnlySpan<int> left, int topLeft, int bw, int bh, int mode, Span<int> dst)
    {
        // libaom's own real buffer[33][33] -- 1-indexed so buffer[0][0] is the top-left corner sample and
        // buffer[r+1][c+1] is the (r, c) prediction sample, exactly mirroring the real C source's own
        // layout (bw, bh <= 32 here, same as the real assert(bw <= 32 && bh <= 32)).
        var buffer = new int[bh + 2, bw + 2];

        for (int r = 0; r < bh; r++)
        {
            buffer[r + 1, 0] = left[r];
        }

        buffer[0, 0] = topLeft;
        for (int c = 0; c < bw; c++)
        {
            buffer[0, c + 1] = above[c];
        }

        for (int r = 1; r < bh + 1; r += 2)
        {
            for (int c = 1; c < bw + 1; c += 4)
            {
                int p0 = buffer[r - 1, c - 1];
                int p1 = buffer[r - 1, c];
                int p2 = buffer[r - 1, c + 1];
                int p3 = buffer[r - 1, c + 2];
                int p4 = buffer[r - 1, c + 3];
                int p5 = buffer[r, c - 1];
                int p6 = buffer[r + 1, c - 1];

                for (int k = 0; k < 8; k++)
                {
                    int rOffset = k >> 2;
                    int cOffset = k & 0x03;
                    var taps = FilterIntraTaps[mode][k];
                    int pr = (taps[0] * p0) + (taps[1] * p1) + (taps[2] * p2) + (taps[3] * p3)
                        + (taps[4] * p4) + (taps[5] * p5) + (taps[6] * p6);

                    // Round2(pr, 4) then clip_pixel -- see the type doc comment above for why this
                    // (libaom's own real rounding), not Round2Signed, is the faithful transcription.
                    buffer[r + rOffset, c + cOffset] = ClipPixel(Round2(pr, FilterIntraScaleBits));
                }
            }
        }

        for (int r = 0; r < bh; r++)
        {
            for (int c = 0; c < bw; c++)
            {
                dst[(r * bw) + c] = buffer[r + 1, c + 1];
            }
        }
    }

    /// <summary><c>ROUND_POWER_OF_TWO</c> (<c>aom_ports/mem.h</c>): <c>(value + (1 &lt;&lt; (n - 1))) &gt;&gt; n</c>,
    /// using C#'s own arithmetic right shift on <see langword="int"/> for negative <paramref name="value"/>
    /// exactly as C's <c>&gt;&gt;</c> does on every platform libaom actually ships on.</summary>
    private static int Round2(int value, int n) => (value + (1 << (n - 1))) >> n;

    /// <summary><c>clip_pixel</c> (<c>aom_dsp/aom_dsp_common.h</c>).</summary>
    private static int ClipPixel(int val) => val > 255 ? 255 : val < 0 ? 0 : val;
}
