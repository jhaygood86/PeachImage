using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Headers;

internal enum JxlColorSpace : uint
{
    Rgb = 0,
    Gray = 1,
    Xyb = 2,
    Unknown = 3,
}

internal enum JxlWhitePoint : uint
{
    D65 = 1,
    Custom = 2,
    E = 10,
    Dci = 11,
}

internal enum JxlPrimaries : uint
{
    Srgb = 1,
    Custom = 2,
    Bt2100 = 9,
    P3 = 11,
}

internal enum JxlTransferFunction : uint
{
    Bt709 = 1,
    Unknown = 2,
    Linear = 8,
    Srgb = 13,
    Pq = 16,
    Dci = 17,
    Hlg = 18,
}

internal enum JxlRenderingIntent : uint
{
    Perceptual = 0,
    Relative = 1,
    Saturation = 2,
    Absolute = 3,
}

/// <summary>A CIE xy chromaticity coordinate, in units of 1e-6.</summary>
internal readonly record struct JxlCustomXy(int X, int Y)
{
    public double XValue => X / 1e6;

    public double YValue => Y / 1e6;

    public static JxlCustomXy Read(ref JxlBitReader reader)
    {
        int x = ReadComponent(ref reader);
        int y = ReadComponent(ref reader);
        return new JxlCustomXy(x, y);
    }

    private static int ReadComponent(ref JxlBitReader reader)
    {
        uint packed = JxlFieldReader.ReadU32(ref reader, U32Dist.Bits(19), U32Dist.BitsOffset(19, 524288), U32Dist.BitsOffset(20, 1048576), U32Dist.BitsOffset(21, 2097152));
        return JxlFieldReader.UnpackSigned(packed);
    }
}

/// <summary>The color space the codestream's samples are in (or, for XYB, are rendered to), as signalled in the header, or an embedded ICC profile.</summary>
internal sealed class JxlColorEncoding
{
    /// <summary>The gamma exponent is stored as <c>Gamma / GammaMultiplier</c>.</summary>
    public const uint GammaMultiplier = 10_000_000;

    private const uint MaxGamma = 8192;

    /// <summary>Whether an ICC profile follows the headers and fully describes the color space.</summary>
    public bool WantIcc { get; private init; }

    public JxlColorSpace ColorSpace { get; private init; } = JxlColorSpace.Rgb;

    public JxlWhitePoint WhitePoint { get; private init; } = JxlWhitePoint.D65;

    public JxlCustomXy CustomWhitePoint { get; private init; }

    public JxlPrimaries Primaries { get; private init; } = JxlPrimaries.Srgb;

    public JxlCustomXy CustomRed { get; private init; }

    public JxlCustomXy CustomGreen { get; private init; }

    public JxlCustomXy CustomBlue { get; private init; }

    public bool HaveGamma { get; private init; }

    /// <summary>The gamma exponent times <see cref="GammaMultiplier"/>; meaningful when <see cref="HaveGamma"/>.</summary>
    public uint Gamma { get; private init; }

    public JxlTransferFunction TransferFunction { get; private init; } = JxlTransferFunction.Srgb;

    public JxlRenderingIntent RenderingIntent { get; private init; } = JxlRenderingIntent.Relative;

    /// <summary>Whether the color space has primaries to signal (everything but gray and XYB).</summary>
    public bool HasPrimaries => ColorSpace is not (JxlColorSpace.Gray or JxlColorSpace.Xyb);

    public static JxlColorEncoding Default { get; } = new();

    /// <summary>An sRGB-primaries encoding with the given transfer function (or, when <paramref name="gamma"/> is non-zero, a pure power law).</summary>
    internal static JxlColorEncoding Create(JxlTransferFunction transferFunction, uint gamma = 0) =>
        new() { TransferFunction = transferFunction, HaveGamma = gamma != 0, Gamma = gamma };

    public static JxlColorEncoding Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        bool wantIcc = reader.ReadBool();
        var colorSpace = (JxlColorSpace)ReadValidatedEnum(ref reader, "color space", 0, 1, 2, 3);

        if (wantIcc)
        {
            reader.ThrowIfOverrun();
            return new JxlColorEncoding
            {
                WantIcc = true,
                ColorSpace = colorSpace,
                TransferFunction = JxlTransferFunction.Unknown,
            };
        }

        var whitePoint = JxlWhitePoint.D65;
        var customWhite = default(JxlCustomXy);
        if (colorSpace != JxlColorSpace.Xyb)
        {
            whitePoint = (JxlWhitePoint)ReadValidatedEnum(ref reader, "white point", 1, 2, 10, 11);
            if (whitePoint == JxlWhitePoint.Custom)
            {
                customWhite = JxlCustomXy.Read(ref reader);
            }
        }

        var primaries = JxlPrimaries.Srgb;
        JxlCustomXy red = default, green = default, blue = default;
        if (colorSpace is not (JxlColorSpace.Gray or JxlColorSpace.Xyb))
        {
            primaries = (JxlPrimaries)ReadValidatedEnum(ref reader, "primaries", 1, 2, 9, 11);
            if (primaries == JxlPrimaries.Custom)
            {
                red = JxlCustomXy.Read(ref reader);
                green = JxlCustomXy.Read(ref reader);
                blue = JxlCustomXy.Read(ref reader);
            }
        }

        bool haveGamma;
        uint gamma = 0;
        var transfer = JxlTransferFunction.Srgb;
        if (colorSpace == JxlColorSpace.Xyb)
        {
            // XYB implies a 1/3 gamma and carries no transfer-function field.
            haveGamma = true;
            gamma = (uint)Math.Round(GammaMultiplier / 3.0);
        }
        else
        {
            haveGamma = reader.ReadBool();
            if (haveGamma)
            {
                gamma = reader.ReadBits(24);
                if (gamma > GammaMultiplier || (ulong)gamma * MaxGamma < GammaMultiplier)
                {
                    throw new JxlDecodingException($"Invalid gamma {gamma}.");
                }
            }
            else
            {
                transfer = (JxlTransferFunction)ReadValidatedEnum(ref reader, "transfer function", 1, 2, 8, 13, 16, 17, 18);
            }
        }

        var intent = (JxlRenderingIntent)ReadValidatedEnum(ref reader, "rendering intent", 0, 1, 2, 3);
        reader.ThrowIfOverrun();

        if (colorSpace == JxlColorSpace.Unknown || (!haveGamma && transfer == JxlTransferFunction.Unknown))
        {
            throw new JxlDecodingException("The color encoding is unknown and no ICC profile is present.");
        }

        return new JxlColorEncoding
        {
            WantIcc = false,
            ColorSpace = colorSpace,
            WhitePoint = whitePoint,
            CustomWhitePoint = customWhite,
            Primaries = primaries,
            CustomRed = red,
            CustomGreen = green,
            CustomBlue = blue,
            HaveGamma = haveGamma,
            Gamma = gamma,
            TransferFunction = transfer,
            RenderingIntent = intent,
        };
    }

    private static uint ReadValidatedEnum(ref JxlBitReader reader, string what, params uint[] valid)
    {
        uint value = JxlFieldReader.ReadEnum(ref reader);
        reader.ThrowIfOverrun();
        if (Array.IndexOf(valid, value) < 0)
        {
            throw new JxlDecodingException($"Invalid {what} value {value}.");
        }

        return value;
    }
}
