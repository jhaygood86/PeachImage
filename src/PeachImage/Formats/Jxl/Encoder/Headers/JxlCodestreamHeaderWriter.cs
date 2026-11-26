using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Encoder.Headers;

/// <summary>What the encoder needs to describe an image in the codestream headers.</summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="BitsPerSample">Sample depth of the color channels (and alpha): 1 to 31 for integers, 32 for IEEE floats.</param>
/// <param name="IsGray">Whether the image has a single gray color channel instead of RGB.</param>
/// <param name="HasAlpha">Whether one straight (non-associated) alpha extra channel follows the color channels.</param>
/// <param name="XybEncoded">Whether the color channels are stored in XYB (VarDCT) rather than the original space (lossless Modular).</param>
/// <param name="Orientation">EXIF-style orientation, 1 to 8.</param>
/// <param name="IccProfile">An ICC profile describing the color space instead of the enumerated sRGB encoding, or <see langword="null"/>.</param>
/// <param name="IsFloat">Whether samples are 32-bit IEEE floats (coded as their bit patterns when lossless).</param>
/// <param name="IsHalf">With <paramref name="IsFloat"/>: the samples are IEEE binary16 (16 bits, 5 exponent bits) rather than binary32.</param>
/// <param name="ColorSpace">An RGB space to declare with enums instead of an ICC profile, or <see langword="null"/> for sRGB.</param>
/// <param name="IntensityTarget">The peak luminance in cd/m2 to declare in the tone mapping header, or <see langword="null"/> for the default (255).</param>
internal readonly record struct JxlEncodedImageInfo(
    uint Width,
    uint Height,
    int BitsPerSample,
    bool IsGray,
    bool HasAlpha,
    bool XybEncoded,
    int Orientation = 1,
    byte[]? IccProfile = null,
    bool IsFloat = false,
    bool IsHalf = false,
    NamedColorSpace? ColorSpace = null,
    float? IntensityTarget = null);

/// <summary>
/// Writes everything a codestream carries before its first frame: the signature, <c>SizeHeader</c>,
/// <c>ImageMetadata</c> and <c>CustomTransformData</c>. The inverse of <c>JxlCodestreamHeaders.Read</c>. The
/// color space is signalled as an enumerated sRGB-primaries / sRGB-transfer encoding (gray for one channel) unless an
/// ICC profile is supplied, in which case the profile's stream follows the headers.
/// </summary>
internal static class JxlCodestreamHeaderWriter
{
    // Numerator/denominator of the seven fixed aspect ratios a SizeHeader can imply; index = ratio code - 1.
    private static readonly (uint Num, uint Den)[] Ratios = [(1, 1), (12, 10), (4, 3), (3, 2), (16, 9), (5, 4), (2, 1)];

    public static void Write(JxlBitWriter writer, in JxlEncodedImageInfo info)
    {
        if (info.Width == 0 || info.Height == 0)
        {
            throw new JxlEncodingException("The image has an empty dimension.");
        }

        writer.WriteBits(0xFF, 8);
        writer.WriteBits(0x0A, 8);
        WriteSizeHeader(writer, info.Width, info.Height);
        WriteImageMetadata(writer, info);

        // CustomTransformData: all_default (default opsin inverse matrix, biases and upsampling kernels).
        writer.WriteBool(true);
        if (info.IccProfile is not null)
        {
            JxlIccEncoder.Write(writer, info.IccProfile);
        }

        writer.ZeroPadToByte();
    }

    private static void WriteSizeHeader(JxlBitWriter writer, uint width, uint height)
    {
        uint ratio = 0;
        for (int i = 0; i < Ratios.Length; i++)
        {
            if ((ulong)height * Ratios[i].Num / Ratios[i].Den == width)
            {
                ratio = (uint)i + 1;
                break;
            }
        }

        bool small = height % 8 == 0 && height <= 256 && (ratio != 0 || (width % 8 == 0 && width <= 256));
        writer.WriteBool(small);
        if (small)
        {
            writer.WriteBits(height / 8 - 1, 5);
        }
        else
        {
            WriteDimension(writer, height);
        }

        writer.WriteBits(ratio, 3);
        if (ratio == 0)
        {
            if (small)
            {
                writer.WriteBits(width / 8 - 1, 5);
            }
            else
            {
                WriteDimension(writer, width);
            }
        }
    }

    private static void WriteDimension(JxlBitWriter writer, uint value) =>
        writer.WriteU32(value, U32Dist.BitsOffset(9, 1), U32Dist.BitsOffset(13, 1), U32Dist.BitsOffset(18, 1), U32Dist.BitsOffset(30, 1));

    private static void WriteImageMetadata(JxlBitWriter writer, in JxlEncodedImageInfo info)
    {
        bool defaultColor = info.ColorSpace is null or NamedColorSpace.Srgb;
        bool allDefault = info.IccProfile is null && defaultColor && !info.IsFloat && !info.IsGray && !info.HasAlpha && info.BitsPerSample == 8 && info.XybEncoded && info.Orientation == 1;
        writer.WriteBool(allDefault);
        if (allDefault)
        {
            return;
        }

        bool extraFields = info.Orientation != 1 || info.IntensityTarget is not null;
        writer.WriteBool(extraFields);
        if (extraFields)
        {
            writer.WriteBits((uint)info.Orientation - 1, 3);
            writer.WriteBool(false); // have_intrinsic_size
            writer.WriteBool(false); // have_preview
            writer.WriteBool(false); // have_animation
        }

        WriteBitDepth(writer, info.BitsPerSample, info.IsFloat, info.IsHalf);
        writer.WriteBool(!info.IsFloat && info.BitsPerSample <= 12); // modular_16bit_buffer_sufficient

        uint extraChannels = info.HasAlpha ? 1u : 0u;
        writer.WriteU32(extraChannels, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(4, 2), U32Dist.BitsOffset(12, 1));
        if (info.HasAlpha)
        {
            WriteAlphaChannel(writer, info.BitsPerSample, info.IsFloat, info.IsHalf);
        }

        writer.WriteBool(info.XybEncoded);
        WriteColorEncoding(writer, info.IsGray, info.IccProfile is not null, info.ColorSpace);
        if (extraFields)
        {
            if (info.IntensityTarget is { } target)
            {
                writer.WriteBool(false); // tone mapping: explicit
                writer.WriteF16(target); // intensity_target
                writer.WriteF16(0f); // min_nits
                writer.WriteBool(false); // relative_to_max_display
                writer.WriteF16(0f); // linear_below
            }
            else
            {
                writer.WriteBool(true); // tone mapping: all_default
            }
        }

        writer.WriteU64(0); // extensions
    }

    private static void WriteBitDepth(JxlBitWriter writer, int bits, bool isFloat, bool isHalf = false)
    {
        if (isFloat && isHalf)
        {
            writer.WriteBool(true);
            writer.WriteU32(16, U32Dist.Val(32), U32Dist.Val(16), U32Dist.Val(24), U32Dist.BitsOffset(6, 1));
            writer.WriteBits(4, 4); // 5 exponent bits: IEEE binary16
            return;
        }

        if (isFloat)
        {
            writer.WriteBool(true);
            writer.WriteU32(32, U32Dist.Val(32), U32Dist.Val(16), U32Dist.Val(24), U32Dist.BitsOffset(6, 1));
            writer.WriteBits(7, 4); // 8 exponent bits
            return;
        }

        if (bits is < 1 or > 31)
        {
            throw new JxlEncodingException($"Unsupported bits per sample: {bits}.");
        }

        writer.WriteBool(false); // integer samples
        writer.WriteU32((uint)bits, U32Dist.Val(8), U32Dist.Val(10), U32Dist.Val(12), U32Dist.BitsOffset(6, 1));
    }

    private static void WriteAlphaChannel(JxlBitWriter writer, int bits, bool isFloat, bool isHalf = false)
    {
        if (bits == 8 && !isFloat)
        {
            writer.WriteBool(true); // all_default: 8-bit straight alpha
            return;
        }

        writer.WriteBool(false);
        writer.WriteEnum(0); // Alpha
        WriteBitDepth(writer, bits, isFloat, isHalf);
        writer.WriteU32(0, U32Dist.Val(0), U32Dist.Val(3), U32Dist.Val(4), U32Dist.BitsOffset(3, 1)); // dim_shift
        writer.WriteName(string.Empty);
        writer.WriteBool(false); // alpha_associated
    }

    private static JxlTransferFunction TransferOf(NamedColorSpace? space) => space switch
    {
        null or NamedColorSpace.Srgb or NamedColorSpace.DisplayP3 => JxlTransferFunction.Srgb,
        NamedColorSpace.LinearSrgb or NamedColorSpace.LinearDisplayP3 or NamedColorSpace.LinearRec2020 => JxlTransferFunction.Linear,
        NamedColorSpace.Rec2020 => JxlTransferFunction.Bt709,
        NamedColorSpace.Rec2100Pq => JxlTransferFunction.Pq,
        _ => JxlTransferFunction.Hlg,
    };

    private static void WriteColorEncoding(JxlBitWriter writer, bool isGray, bool wantIcc, NamedColorSpace? named)
    {
        if (wantIcc)
        {
            writer.WriteBool(false);
            writer.WriteBool(true); // want_icc
            writer.WriteEnum((uint)(isGray ? JxlColorSpace.Gray : JxlColorSpace.Rgb));
            return;
        }

        if (!isGray && named is null or NamedColorSpace.Srgb)
        {
            writer.WriteBool(true); // all_default: sRGB, relative intent
            return;
        }

        writer.WriteBool(false);
        writer.WriteBool(false); // want_icc
        if (isGray)
        {
            writer.WriteEnum((uint)JxlColorSpace.Gray);
            writer.WriteEnum((uint)JxlWhitePoint.D65);
            writer.WriteBool(false); // have_gamma
            writer.WriteEnum((uint)TransferOf(named));
            writer.WriteEnum((uint)JxlRenderingIntent.Relative);
            return;
        }

        // An RGB space named by enums: the colors are written exactly as stored and a decoder reads them in this space.
        var space = named!.Value;
        var primaries = space switch
        {
            NamedColorSpace.Srgb or NamedColorSpace.LinearSrgb => JxlPrimaries.Srgb,
            NamedColorSpace.DisplayP3 or NamedColorSpace.LinearDisplayP3 => JxlPrimaries.P3,
            _ => JxlPrimaries.Bt2100,
        };
        var transfer = TransferOf(space);
        writer.WriteEnum((uint)JxlColorSpace.Rgb);
        writer.WriteEnum((uint)JxlWhitePoint.D65);
        writer.WriteEnum((uint)primaries);
        writer.WriteBool(false); // have_gamma
        writer.WriteEnum((uint)transfer);
        writer.WriteEnum((uint)JxlRenderingIntent.Relative);
    }
}
