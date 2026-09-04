using System.Buffers;
using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Chooses and applies AV1's CDEF filter (spec §7.15) for a non-lossless frame, by reusing the decoder's own
/// filter implementation (<see cref="Av1Cdef"/>) against the encoder's (already deblocked -- see
/// <see cref="Av1InLoopFilterSearch"/>, which must run first per spec's own deblock-then-CDEF ordering) local
/// reconstruction -- the same "no second, driftable copy of real spec logic" approach this encoder already
/// takes for entropy costing (<see cref="Av1TrialSymbolSink"/>) and deblocking.
///
/// <para>Real per-64x64-unit-adaptive CDEF (spec's up to <c>CDEF_MAX_STRENGTHS</c> = 8 strength combos, each
/// unit independently choosing which one to use via a signalled <c>cdef_idx</c>) is out of scope for this v1
/// pass -- this always writes <c>cdef_bits = 0</c> (exactly one combo, applied to the whole frame uniformly),
/// searched the same way <see cref="Av1InLoopFilterSearch"/> searches one shared deblocking level: like
/// deblocking, CDEF's own signaling cost doesn't vary with which combo is chosen (<c>cdef_params()</c> always
/// writes the same 14 bits for one combo, spec §5.9.19), so this is a pure <c>min D</c> search over a fixed
/// candidate list, not a Lagrangian one.</para>
///
/// <para><b>Buffer ownership</b>: <see cref="Av1Cdef.Apply"/> always replaces every <c>Planes</c> element with
/// a freshly <see cref="ArrayPool{T}.Shared"/>-rented buffer and returns whatever was passed in to the same
/// pool -- including a plain <c>new int[]</c> array, which throws (<c>ArgumentException</c>, "not associated
/// with this pool") rather than being silently accepted. Every buffer this type feeds to <see cref="Av1Cdef.Apply"/>
/// is therefore explicitly pool-rented first (<see cref="RentAndCopy"/>), and every array <see cref="Av1Cdef.Apply"/>
/// hands back is explicitly returned (<see cref="ReturnPlanes"/>) once this type is done reading it --
/// <see cref="SearchAndApply"/>'s own <c>reconY</c>/<c>reconU</c>/<c>reconV</c> parameters are never fed to
/// <see cref="Av1Cdef.Apply"/> directly; the winning candidate's filtered content is copied back into them
/// instead, so their own allocation (plain arrays, owned by <see cref="Av1FrameEncoder.Encode"/>) never
/// changes.</para>
/// </summary>
internal static class Av1CdefSearch
{
    // (primary, secondary) pairs applied identically to Y and UV -- secondary strengths are restricted to
    // {0, 1, 2, 4} (see Av1CdefChoice's remarks on why 3 is unreachable). Coarse and shared-across-planes
    // by design (a real encoder would search luma/chroma and primary/secondary more finely, and per-unit
    // rather than per-frame) -- a natural follow-up once this is proven out, not a v1 requirement.
    private static readonly (int Pri, int Sec)[] Candidates =
    [
        (1, 0), (2, 1), (4, 1), (4, 2), (8, 2), (8, 4), (12, 4), (15, 4),
    ];

    /// <summary>
    /// Searches <see cref="Candidates"/> for the CDEF strength combo that minimizes squared error against the
    /// true (padded) source, starting from <paramref name="reconY"/>/<paramref name="reconU"/>/<paramref name="reconV"/>'s
    /// current (already-deblocked) content. Copies the winner's filtered content back into those same buffers
    /// in place (or leaves them untouched if no candidate beats the deblocked-only baseline --
    /// <see cref="Av1CdefChoice.Off"/>, this method's default return), and returns the winning choice for
    /// <see cref="Av1FrameHeaderWriter.Write"/>'s <c>cdef</c> parameter to actually signal.
    /// </summary>
    public static Av1CdefChoice SearchAndApply(
        int[] reconY, int[]? reconU, int[]? reconV,
        int[] sourceY, int[]? sourceU, int[]? sourceV,
        int width, int height, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx, int loopFilterLevel)
    {
        int miCols = 2 * ((width + 7) >> 3);
        int miRows = 2 * ((height + 7) >> 3);
        int lumaLen = width * height;
        int chromaLen = chromaWidth * chromaHeight;

        var seq = BuildSequenceHeader(monoChrome);

        var bestChoice = Av1CdefChoice.Off;
        long bestSse = ComputeSse(reconY, sourceY, lumaLen) + ComputeSse(reconU, sourceU, chromaLen) + ComputeSse(reconV, sourceV, chromaLen);

        foreach ((int pri, int sec) in Candidates)
        {
            var choice = new Av1CdefChoice(Damping: 3, YPriStrength: pri, YSecStrength: sec, UvPriStrength: pri, UvSecStrength: sec);

            var (trialY, trialU, trialV) = RentAndCopy(reconY, reconU, reconV, lumaLen, chromaLen);
            var frame = BuildFrameHeaderForChoice(width, height, monoChrome, baseQIdx, loopFilterLevel, choice);
            var result = BuildDecodeResult(seq, frame, trialY, trialU, trialV, miCols, miRows, width, height, chromaWidth, chromaHeight);
            Av1Cdef.Apply(result);

            long sse = ComputeSse(result.Planes[0], sourceY, lumaLen) + ComputeSse(monoChrome ? null : result.Planes[1], sourceU, chromaLen) + ComputeSse(monoChrome ? null : result.Planes[2], sourceV, chromaLen);
            ReturnPlanes(result, monoChrome);

            if (sse < bestSse)
            {
                bestSse = sse;
                bestChoice = choice;
            }
        }

        if (bestChoice != Av1CdefChoice.Off)
        {
            var (trialY, trialU, trialV) = RentAndCopy(reconY, reconU, reconV, lumaLen, chromaLen);
            var frame = BuildFrameHeaderForChoice(width, height, monoChrome, baseQIdx, loopFilterLevel, bestChoice);
            var result = BuildDecodeResult(seq, frame, trialY, trialU, trialV, miCols, miRows, width, height, chromaWidth, chromaHeight);
            Av1Cdef.Apply(result);

            Array.Copy(result.Planes[0], reconY, lumaLen);
            if (reconU is not null)
            {
                Array.Copy(result.Planes[1], reconU, chromaLen);
                Array.Copy(result.Planes[2], reconV!, chromaLen);
            }

            ReturnPlanes(result, monoChrome);
        }

        return bestChoice;
    }

    private static (int[] Y, int[]? U, int[]? V) RentAndCopy(int[] y, int[]? u, int[]? v, int lumaLen, int chromaLen)
    {
        int[] rentedY = ArrayPool<int>.Shared.Rent(lumaLen);
        Array.Copy(y, rentedY, lumaLen);

        if (u is null)
        {
            return (rentedY, null, null);
        }

        int[] rentedU = ArrayPool<int>.Shared.Rent(chromaLen);
        Array.Copy(u, rentedU, chromaLen);
        int[] rentedV = ArrayPool<int>.Shared.Rent(chromaLen);
        Array.Copy(v!, rentedV, chromaLen);
        return (rentedY, rentedU, rentedV);
    }

    /// <summary>Returns every plane <see cref="Av1Cdef.Apply"/> replaced <see cref="Av1FrameDecodeResult.Planes"/> with -- always pool-rented by <see cref="Av1Cdef.Apply"/> itself (see this type's own class-level ownership remarks), regardless of whether the arrays fed into that call originated from <see cref="RentAndCopy"/>.</summary>
    private static void ReturnPlanes(Av1FrameDecodeResult result, bool monoChrome)
    {
        ArrayPool<int>.Shared.Return(result.Planes[0]);
        if (!monoChrome)
        {
            ArrayPool<int>.Shared.Return(result.Planes[1]);
            ArrayPool<int>.Shared.Return(result.Planes[2]);
        }
    }

    private static long ComputeSse(int[]? filtered, int[]? source, int length)
    {
        if (filtered is null || source is null)
        {
            return 0;
        }

        long sse = 0;
        for (int i = 0; i < length; i++)
        {
            int diff = filtered[i] - source[i];
            sse += (long)diff * diff;
        }

        return sse;
    }

    /// <summary>Same fixed-configuration <see cref="Av1SequenceHeader"/> shape as <see cref="Av1InLoopFilterSearch"/>'s own builder, except <c>EnableCdef</c> is always <see langword="true"/> here regardless of what the real sequence header ends up signaling -- this search only ever runs for the CDEF filter, which needs it on to do anything at all (see <see cref="Av1Cdef.Apply"/>'s own early-return gate).</summary>
    private static Av1SequenceHeader BuildSequenceHeader(bool monoChrome) => new()
    {
        SeqProfile = Av1SequenceHeaderWriter.SeqProfile,
        SeqLevelIdx0 = Av1SequenceHeaderWriter.SeqLevelIdx0,
        FrameWidthBits = 0,
        FrameHeightBits = 0,
        MaxFrameWidth = 0,
        MaxFrameHeight = 0,
        Use128x128Superblock = false,
        EnableFilterIntra = true,
        EnableIntraEdgeFilter = Av1SequenceHeaderWriter.EnableIntraEdgeFilter,
        EnableSuperres = false,
        EnableCdef = true,
        EnableRestoration = false,
        BitDepth = 8,
        MonoChrome = monoChrome,
        ColorPrimaries = Av1SequenceHeaderWriter.ColorPrimaries,
        TransferCharacteristics = Av1SequenceHeaderWriter.TransferCharacteristics,
        MatrixCoefficients = Av1SequenceHeaderWriter.MatrixCoefficients,
        ColorRange = Av1SequenceHeaderWriter.ColorRangeFull,
        SubsamplingX = true,
        SubsamplingY = true,
        ChromaSamplePosition = Av1SequenceHeaderWriter.ChromaSamplePosition,
        SeparateUvDeltaQ = false,
        FilmGrainParamsPresent = false,
    };

    /// <summary>Resolves the real <see cref="Av1FrameHeader"/> a candidate <paramref name="cdef"/> choice would produce, the same throwaway-<see cref="Av1BitWriter"/> reuse <see cref="Av1InLoopFilterSearch"/>'s identically-named method uses and for the same reason (reuse <see cref="Av1FrameHeaderWriter.Write"/>'s already-correct field construction instead of a second copy of it).</summary>
    private static Av1FrameHeader BuildFrameHeaderForChoice(int width, int height, bool monoChrome, int baseQIdx, int loopFilterLevel, Av1CdefChoice cdef)
    {
        var scratchWriter = new Av1BitWriter();
        return Av1FrameHeaderWriter.Write(scratchWriter, width, height, monoChrome, baseQIdx, lossless: false, loopFilterLevel, enableCdef: true, cdef);
    }

    private static Av1FrameDecodeResult BuildDecodeResult(Av1SequenceHeader seq, Av1FrameHeader frame, int[] y, int[]? u, int[]? v, int miCols, int miRows, int width, int height, int chromaWidth, int chromaHeight)
    {
        int frameSize = miCols * miRows;

        return new Av1FrameDecodeResult
        {
            Sequence = seq,
            Frame = frame,
            BlocksDecoded = 0,
            TilesStarted = 0,
            Planes = [y, u ?? [], v ?? []],
            PlaneWidths = u is null ? [width, 0, 0] : [width, chromaWidth, chromaWidth],
            PlaneHeights = u is null ? [height, 0, 0] : [height, chromaHeight, chromaHeight],
            StoppedAtResidual = false,
            YModes = [],
            MiSizes = [],

            // CDEF bypasses an 8x8 unit only when it and its 3 neighbor mi positions are *all* skip (spec
            // §7.15.1) -- this encoder's non-lossless leaves never set Skips at all (that flag only ever
            // becomes true for lossless-only features, palette/exact-match IntraBC -- see
            // Av1TileEncoder.EncodeLeaf's own Skips assignment remarks), so an all-false array here exactly
            // matches every real non-lossless leaf's real skip state; CDEF filters every unit accordingly.
            Skips = new bool[frameSize],
            SegmentIds = new int[frameSize],
            DeltaLfs = [new int[frameSize], new int[frameSize], new int[frameSize], new int[frameSize]],
            LoopfilterTxSizes = [[], [], []],
            LoopfilterTxSizeStrides = [0, 0, 0],

            // Every 64x64 unit resolves to strength-combo index 0 (cdef_bits == 0 means exactly one combo
            // exists -- see Av1FrameHeaderWriter.WriteCdefParams's remarks), so an all-zero array here is
            // exactly equivalent to real per-unit cdef_idx values a Bits > 0 encoder would need to populate.
            CdefIdx = new int[frameSize],
            RestorationUnits = [null, null, null],
            MinSymbolMaxBitsAtExit = 0,
        };
    }
}
