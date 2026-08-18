using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Writes the compressed portion of VP8's partition 0 (RFC 6386 section 9.2 onward): color space/clamping
/// flags, segmentation header, loop filter header, coefficient partition count, quantization indices,
/// coefficient probability updates, and the skip-probability flag — the write-side mirror of the sequence
/// <see cref="Decoding.Vp8.Vp8FrameDecoder.Decode"/> reads via <see cref="Vp8SegmentHeader.Parse"/>,
/// <see cref="Vp8LoopFilterHeader.Parse"/>, <see cref="Vp8QuantIndices.Parse"/>, and
/// <see cref="Vp8CoefficientDecoder.ParseProbabilityUpdates"/>. Per-macroblock mode data (segment id, skip flag,
/// prediction modes) follows immediately after on the same <see cref="Vp8BoolEncoder"/> instance, written
/// separately by the frame encoder — partition 0 is one continuous coded stream covering both, exactly as
/// <see cref="Decoding.Vp8.Vp8FrameDecoder"/> reads it.
/// </summary>
internal static class Vp8HeaderWriter
{
    /// <summary>
    /// v1 always writes a single implicit segment (no segmentation) and never updates coefficient
    /// probabilities (every one of <see cref="Vp8CoefficientProbabilities.FlatLength"/> update-gate bits is
    /// written false, leaving <see cref="Vp8CoefficientProbabilities.Default"/> in effect on the decode side --
    /// per-frame coefficient probability adaptation is deferred to a later milestone). Per-segment
    /// adaptive quantization and probability adaptation are both real compression wins left for a later
    /// milestone, not required for a correct v1 bitstream.
    /// </summary>
    public static void WritePartition0Header(
        Vp8BoolEncoder bw,
        int baseQIndex,
        int filterLevel,
        int filterSharpness,
        bool simpleFilter,
        int skipFalseProbability)
    {
        bw.PutFlag(false); // color_space -- always standard studio-range YUV.
        bw.PutFlag(false); // clamping_type -- reconstructed pixels are always clamped to [0,255].

        bw.PutFlag(false); // segmentation_enabled = false (UseSegment).

        bw.PutFlag(simpleFilter);
        bw.PutValue((uint)filterLevel, 6);
        bw.PutValue((uint)filterSharpness, 3);
        bw.PutFlag(false); // loop_filter_adj_enable = false (UseLfDelta).

        bw.PutValue(0, 2); // log2(partition count) = 0 -> a single coefficient partition.

        bw.PutValue((uint)baseQIndex, 7);
        bw.PutFlag(false); // y1dc_delta_q present
        bw.PutFlag(false); // y2dc_delta_q present
        bw.PutFlag(false); // y2ac_delta_q present
        bw.PutFlag(false); // uvdc_delta_q present
        bw.PutFlag(false); // uvac_delta_q present

        bw.PutFlag(true); // refresh_entropy_probs -- irrelevant for a lone keyframe, matching the decoder's own comment.

        WriteNoCoefficientProbabilityUpdates(bw);

        bw.PutFlag(true); // mb_no_skip_coeff (useSkipProbability) = true.
        bw.PutValue((uint)skipFalseProbability, 8);
    }

    /// <summary>Writes a "no update" bit for every coefficient-probability table entry, using the same per-entry gating probability <see cref="Vp8CoefficientDecoder.ParseProbabilityUpdates"/> reads with — the boolean coder requires the encoder and decoder to agree on each bit's probability, not just its value.</summary>
    private static void WriteNoCoefficientProbabilityUpdates(Vp8BoolEncoder bw)
    {
        byte[] updateProbabilities = Vp8CoefficientProbabilities.UpdateProbabilityFlat;
        for (int i = 0; i < Vp8CoefficientProbabilities.FlatLength; i++)
        {
            bw.PutBit(0, updateProbabilities[i]);
        }
    }

    /// <summary>
    /// Resolves the per-segment dequantization factors a decoder will compute for a single-segment,
    /// no-delta v1 frame with base quantizer index <paramref name="baseQIndex"/> — without duplicating
    /// <see cref="Vp8Dequantizer"/>'s lookup tables here. Encodes just the segment/quant-index bits into a
    /// throwaway scratch stream and decodes them straight back through the real
    /// <see cref="Vp8SegmentHeader.Parse"/>/<see cref="Vp8QuantIndices.Parse"/>/<see cref="Vp8Dequantizer.Resolve"/>,
    /// guaranteeing this always matches whatever the real decoder derives from the equivalent bits
    /// <see cref="WritePartition0Header"/> writes into the real partition 0 stream.
    /// </summary>
    public static Vp8QuantMatrix ResolveBaseQuantMatrix(int baseQIndex)
    {
        var scratch = new Vp8BoolEncoder();
        scratch.PutFlag(false); // segmentation_enabled = false.
        scratch.PutValue((uint)baseQIndex, 7);
        scratch.PutFlag(false); // y1dc_delta_q present
        scratch.PutFlag(false); // y2dc_delta_q present
        scratch.PutFlag(false); // y2ac_delta_q present
        scratch.PutFlag(false); // uvdc_delta_q present
        scratch.PutFlag(false); // uvac_delta_q present
        byte[] bytes = scratch.Finish();

        var decoder = new Vp8BoolDecoder(bytes, 0, bytes.Length);
        Vp8SegmentHeader segmentHeader = Vp8SegmentHeader.Parse(decoder);
        Vp8QuantIndices quantIndices = Vp8QuantIndices.Parse(decoder);
        return Vp8Dequantizer.Resolve(quantIndices, segmentHeader)[0];
    }
}
