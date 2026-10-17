using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Bitstream;

namespace PeachImage.Formats.Jxl.Encoder.Headers;

/// <summary>What a still, single, full-size frame needs from its header.</summary>
/// <param name="Modular">Whether the frame is Modular-coded rather than VarDCT.</param>
/// <param name="XybEncoded">Whether the image metadata declares XYB colour (then the frame carries the XYB transform).</param>
/// <param name="NumExtraChannels">The number of extra channels declared in the image metadata.</param>
/// <param name="XQmScale">VarDCT with XYB only: the X-channel quantization-matrix scale, 0 to 7.</param>
/// <param name="BQmScale">VarDCT with XYB only: the B-channel quantization-matrix scale, 0 to 7.</param>
/// <param name="Gaborish">Whether the decoder applies the Gabor-like smoothing filter.</param>
/// <param name="EpfIterations">The number of edge-preserving-filter passes (0 to 3).</param>
/// <param name="ChromaSubsampling">VarDCT without XYB only: the per-channel (Cb, Y, Cr) JPEG sampling modes of a frame that holds a YCbCr image, or <see langword="null"/> for a frame without the colour transform.</param>
/// <param name="Flags">The frame flags.</param>
internal readonly record struct JxlFrameHeaderSpec(
    bool Modular,
    bool XybEncoded,
    int NumExtraChannels,
    int XQmScale = 2,
    int BQmScale = 2,
    bool Gaborish = false,
    int EpfIterations = 0,
    int[]? ChromaSubsampling = null,
    ulong Flags = 0);

/// <summary>Writes the header of the one (last, regular, full-size, replacing) frame an encoded still image consists of.</summary>
internal static class JxlFrameHeaderWriter
{
    /// <summary>The group edge in pixels the encoder uses: <c>128 &lt;&lt; GroupSizeShift</c> with the default shift of 1.</summary>
    public const int GroupSizeShift = 1;

    public static void Write(JxlBitWriter writer, in JxlFrameHeaderSpec spec)
    {
        if (spec.Modular && spec.EpfIterations > 0)
        {
            throw new JxlEncodingException("The edge-preserving filter is only written for VarDCT frames.");
        }

        writer.WriteBool(false); // not all_default
        writer.WriteU32(0, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3)); // regular frame
        writer.WriteBool(spec.Modular);
        writer.WriteU64(spec.Flags);
        if (!spec.XybEncoded)
        {
            writer.WriteBool(spec.ChromaSubsampling is not null); // do_YCbCr
            if (spec.ChromaSubsampling is { } modes)
            {
                foreach (int mode in modes)
                {
                    writer.WriteBits((uint)mode, 2);
                }
            }
        }

        writer.WriteBits(0, 2); // upsampling 1
        for (int i = 0; i < spec.NumExtraChannels; i++)
        {
            writer.WriteBits(0, 2);
        }

        if (spec.Modular)
        {
            writer.WriteBits(GroupSizeShift, 2);
        }

        if (!spec.Modular && spec.XybEncoded)
        {
            writer.WriteBits((uint)spec.XQmScale, 3);
            writer.WriteBits((uint)spec.BQmScale, 3);
        }

        writer.WriteU32(1, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.BitsOffset(3, 4)); // one pass
        writer.WriteBool(false); // custom_size_or_origin

        // Blending: replace, for the colour channels and every extra channel.
        for (int i = 0; i <= spec.NumExtraChannels; i++)
        {
            writer.WriteU32(0, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.BitsOffset(2, 3));
        }

        writer.WriteBool(true); // is_last
        writer.WriteName(string.Empty);
        WriteLoopFilter(writer, spec.Gaborish, spec.EpfIterations);
        writer.WriteU64(0); // extensions
    }

    private static void WriteLoopFilter(JxlBitWriter writer, bool gaborish, int epfIterations)
    {
        if (gaborish && epfIterations == 2)
        {
            // The default filter (Gaborish with the standard weights and two EPF passes).
            writer.WriteBool(true);
            return;
        }

        writer.WriteBool(false);
        writer.WriteBool(gaborish);
        if (gaborish)
        {
            writer.WriteBool(false); // default Gaborish weights
        }

        writer.WriteBits((uint)epfIterations, 2);
        if (epfIterations > 0)
        {
            writer.WriteBool(false); // default sharpness table (VarDCT only)
            writer.WriteBool(false); // default channel scales
            writer.WriteBool(false); // default sigma parameters
        }

        writer.WriteU64(0); // extensions
    }
}
