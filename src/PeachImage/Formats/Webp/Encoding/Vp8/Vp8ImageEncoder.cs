using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Top-level entry point for VP8 (lossy) WebP encode: assembles one complete raw <c>VP8 </c> chunk payload
/// (uncompressed frame tag, partition 0 header + mode data, coefficient partition) from an RGB24 image — the
/// lossy counterpart of <see cref="Vp8L.Vp8LImageEncoder"/>.
/// </summary>
/// <remarks>
/// Partition 0 (header + per-macroblock mode data, written by <see cref="Vp8HeaderWriter"/>/
/// <see cref="Vp8FrameEncoder"/> via <see cref="Vp8ModeWriter"/>) and the coefficient partition (written by
/// <see cref="Vp8FrameEncoder"/> via <see cref="Vp8CoefficientEncoder"/>) are two independent boolean-coded
/// streams, matching <see cref="Decoding.Vp8.Vp8FrameDecoder"/>'s own <c>partition0</c>/<c>coefficientPartitions</c>
/// split — v1 always uses a single coefficient partition (no partition-count-driven size table needed), so the
/// two streams are simply concatenated after the uncompressed frame tag.
/// </remarks>
internal static class Vp8ImageEncoder
{
    /// <summary>Uncompressed frame tag + keyframe start code + width/height fields, matching <see cref="Decoding.Vp8.Vp8FrameHeader"/>'s <c>HeaderByteLength</c>.</summary>
    private const int UncompressedHeaderLength = 10;

    /// <summary><see cref="Decoding.Vp8.Vp8FrameHeader"/>'s <c>first_partition_length</c> field is 19 bits wide.</summary>
    private const int MaxFirstPartitionLength = (1 << 19) - 1;

    /// <summary>v1 placeholder skip-flag probability, used both in the header's raw skip-probability byte and as every macroblock's skip-bit probability. Recalibrating this from the actual fraction of skipped macroblocks is a real compression win left for a later milestone (see <see cref="Vp8HeaderWriter"/>'s remarks).</summary>
    private const int PlaceholderSkipFalseProbability = 200;

    public static byte[] Encode(byte[] rgb, int width, int height, WebpEncoderOptions options)
    {
        int baseQIndex = Vp8QualityMapping.QualityToBaseQIndex(options.Quality);
        int filterLevel = Vp8QualityMapping.QualityToFilterLevel(options.Quality);

        Vp8QuantMatrix quant = Vp8HeaderWriter.ResolveBaseQuantMatrix(baseQIndex);

        var partition0Encoder = new Vp8BoolEncoder();
        Vp8HeaderWriter.WritePartition0Header(
            partition0Encoder,
            baseQIndex,
            filterLevel,
            filterSharpness: 0,
            simpleFilter: false,
            skipFalseProbability: PlaceholderSkipFalseProbability);

        var coefficientEncoder = new Vp8BoolEncoder();

        Vp8FrameEncoder.Encode(
            rgb, width, height,
            partition0Encoder, coefficientEncoder,
            quant, useSkipProbability: true, PlaceholderSkipFalseProbability);

        byte[] partition0 = partition0Encoder.Finish();
        byte[] coefficientPartition = coefficientEncoder.Finish();

        if (partition0.Length > MaxFirstPartitionLength)
        {
            throw new WebpEncodingException($"VP8 partition 0 ({partition0.Length} bytes) exceeds the 19-bit first_partition_length field's maximum of {MaxFirstPartitionLength} bytes.");
        }

        return AssembleChunk(width, height, partition0, coefficientPartition);
    }

    private static byte[] AssembleChunk(int width, int height, byte[] partition0, byte[] coefficientPartition)
    {
        var chunk = new byte[UncompressedHeaderLength + partition0.Length + coefficientPartition.Length];

        // key_frame=0 (keyframe), version=0, show_frame=1, first_partition_length in the high 19 bits --
        // matching Vp8FrameHeader.Parse's `tag & 1`, `(tag >> 1) & 7`, `(tag >> 4) & 1`, `tag >> 5` reads.
        uint tag = (1u << 4) | ((uint)partition0.Length << 5);
        chunk[0] = (byte)tag;
        chunk[1] = (byte)(tag >> 8);
        chunk[2] = (byte)(tag >> 16);

        chunk[3] = 0x9d;
        chunk[4] = 0x01;
        chunk[5] = 0x2a;

        // 14-bit width/height, top 2 bits of each 16-bit field (horizontal/vertical scale) left at 0.
        chunk[6] = (byte)width;
        chunk[7] = (byte)(width >> 8);
        chunk[8] = (byte)height;
        chunk[9] = (byte)(height >> 8);

        partition0.CopyTo(chunk, UncompressedHeaderLength);
        coefficientPartition.CopyTo(chunk, UncompressedHeaderLength + partition0.Length);

        return chunk;
    }
}
