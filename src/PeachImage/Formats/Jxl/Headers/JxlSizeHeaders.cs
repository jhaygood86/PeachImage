using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Internal;

namespace PeachImage.Formats.Jxl.Headers;

/// <summary>An image or preview size read from the codestream header.</summary>
internal readonly record struct JxlSize(uint Width, uint Height)
{
    /// <summary>
    /// Reads a <c>SizeHeader</c>: dimensions of the main image. The width may be implied from the height by a
    /// fixed aspect ratio.
    /// </summary>
    public static JxlSize ReadSizeHeader(ref JxlBitReader reader)
    {
        bool small = reader.ReadBool();
        uint height = small
            ? (reader.ReadBits(5) + 1) * 8
            : JxlFieldReader.ReadU32(ref reader, U32Dist.BitsOffset(9, 1), U32Dist.BitsOffset(13, 1), U32Dist.BitsOffset(18, 1), U32Dist.BitsOffset(30, 1));

        uint ratio = reader.ReadBits(3);
        uint width;
        if (ratio != 0)
        {
            width = ApplyRatio(ratio, height);
        }
        else if (small)
        {
            width = (reader.ReadBits(5) + 1) * 8;
        }
        else
        {
            width = JxlFieldReader.ReadU32(ref reader, U32Dist.BitsOffset(9, 1), U32Dist.BitsOffset(13, 1), U32Dist.BitsOffset(18, 1), U32Dist.BitsOffset(30, 1));
        }

        reader.ThrowIfOverrun();
        return new JxlSize(width, height);
    }

    /// <summary>Reads a <c>PreviewHeader</c>: dimensions of the embedded preview frame.</summary>
    public static JxlSize ReadPreviewHeader(ref JxlBitReader reader)
    {
        bool div8 = reader.ReadBool();
        uint height = div8
            ? JxlFieldReader.ReadU32(ref reader, U32Dist.Val(16), U32Dist.Val(32), U32Dist.BitsOffset(5, 1), U32Dist.BitsOffset(9, 33)) * 8
            : JxlFieldReader.ReadU32(ref reader, U32Dist.BitsOffset(6, 1), U32Dist.BitsOffset(8, 65), U32Dist.BitsOffset(10, 321), U32Dist.BitsOffset(12, 1345));

        uint ratio = reader.ReadBits(3);
        uint width;
        if (ratio != 0)
        {
            width = ApplyRatio(ratio, height);
        }
        else if (div8)
        {
            width = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(16), U32Dist.Val(32), U32Dist.BitsOffset(5, 1), U32Dist.BitsOffset(9, 33)) * 8;
        }
        else
        {
            width = JxlFieldReader.ReadU32(ref reader, U32Dist.BitsOffset(6, 1), U32Dist.BitsOffset(8, 65), U32Dist.BitsOffset(10, 321), U32Dist.BitsOffset(12, 1345));
        }

        reader.ThrowIfOverrun();
        return new JxlSize(width, height);
    }

    // floor(height * num / den) for the seven fixed aspect ratios.
    private static uint ApplyRatio(uint ratio, uint height)
    {
        (uint num, uint den) = ratio switch
        {
            1 => (1u, 1u),
            2 => (12u, 10u),
            3 => (4u, 3u),
            4 => (3u, 2u),
            5 => (16u, 9u),
            6 => (5u, 4u),
            _ => (2u, 1u),
        };

        ulong width = (ulong)height * num / den;
        if (width > uint.MaxValue)
        {
            throw new JxlDecodingException("The image width implied by the aspect ratio is too large.");
        }

        return (uint)width;
    }
}

/// <summary>The animation parameters of a codestream whose <c>have_animation</c> flag is set.</summary>
internal readonly record struct JxlAnimationHeader(uint TicksPerSecondNumerator, uint TicksPerSecondDenominator, uint NumLoops, bool HaveTimecodes)
{
    public static JxlAnimationHeader Read(ref JxlBitReader reader)
    {
        uint numerator = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(100), U32Dist.Val(1000), U32Dist.BitsOffset(10, 1), U32Dist.BitsOffset(30, 1));
        uint denominator = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(1), U32Dist.Val(1001), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(10, 1));
        uint loops = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(0), U32Dist.Bits(3), U32Dist.Bits(16), U32Dist.Bits(32));
        bool timecodes = reader.ReadBool();
        reader.ThrowIfOverrun();
        return new JxlAnimationHeader(numerator, denominator, loops, timecodes);
    }
}

/// <summary>The bit depth of a color or extra channel's samples.</summary>
internal readonly record struct JxlBitDepth(bool IsFloat, uint BitsPerSample, uint ExponentBitsPerSample)
{
    public static JxlBitDepth Default => new(false, 8, 0);

    public static JxlBitDepth Read(ref JxlBitReader reader)
    {
        bool isFloat = reader.ReadBool();
        if (!isFloat)
        {
            uint bits = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(8), U32Dist.Val(10), U32Dist.Val(12), U32Dist.BitsOffset(6, 1));
            reader.ThrowIfOverrun();
            if (bits > 31)
            {
                throw new JxlDecodingException($"Invalid bits_per_sample: {bits}.");
            }

            return new JxlBitDepth(false, bits, 0);
        }

        uint floatBits = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(32), U32Dist.Val(16), U32Dist.Val(24), U32Dist.BitsOffset(6, 1));
        uint exponentBits = reader.ReadBits(4) + 1;
        reader.ThrowIfOverrun();
        if (exponentBits is < 2 or > 8)
        {
            throw new JxlDecodingException($"Invalid exponent_bits_per_sample: {exponentBits}.");
        }

        int mantissaBits = (int)floatBits - (int)exponentBits - 1;
        if (mantissaBits is < 2 or > 23)
        {
            throw new JxlDecodingException($"Invalid bits_per_sample: {floatBits}.");
        }

        return new JxlBitDepth(true, floatBits, exponentBits);
    }
}

/// <summary>The kind of data an extra channel carries.</summary>
internal enum JxlExtraChannelType : uint
{
    Alpha = 0,
    Depth = 1,
    SpotColor = 2,
    SelectionMask = 3,
    Black = 4,
    Cfa = 5,
    Thermal = 6,
    Unknown = 15,
    Optional = 16,
}

/// <summary>Describes one extra channel (alpha, depth, spot color, black, ...).</summary>
internal sealed class JxlExtraChannelInfo
{
    public JxlExtraChannelType Type { get; private init; } = JxlExtraChannelType.Alpha;

    public JxlBitDepth BitDepth { get; private init; } = JxlBitDepth.Default;

    /// <summary>The channel is stored downsampled by <c>2^DimShift</c> in each dimension.</summary>
    public uint DimShift { get; private init; }

    public string Name { get; private init; } = string.Empty;

    /// <summary>For alpha: whether the color channels are premultiplied by this alpha.</summary>
    public bool AlphaAssociated { get; private init; }

    /// <summary>For spot color: R, G, B and solidity.</summary>
    public float[]? SpotColor { get; private init; }

    public uint CfaChannel { get; private init; } = 1;

    /// <summary>An alpha channel description, for callers that need one without parsing a header.</summary>
    public static JxlExtraChannelInfo CreateAlpha(bool associated) => new() { Type = JxlExtraChannelType.Alpha, AlphaAssociated = associated };

    public static JxlExtraChannelInfo Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return new JxlExtraChannelInfo();
        }

        uint rawType = JxlFieldReader.ReadEnum(ref reader);
        var bitDepth = JxlBitDepth.Read(ref reader);
        uint dimShift = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(0), U32Dist.Val(3), U32Dist.Val(4), U32Dist.BitsOffset(3, 1));
        reader.ThrowIfOverrun();
        if ((1u << (int)Math.Min(dimShift, 31)) > 8)
        {
            throw new JxlDecodingException($"dim_shift {dimShift} is too large.");
        }

        string name = JxlFieldReader.ReadName(ref reader);

        bool associated = false;
        float[]? spot = null;
        uint cfa = 1;
        if (rawType == (uint)JxlExtraChannelType.Alpha)
        {
            associated = reader.ReadBool();
        }
        else if (rawType == (uint)JxlExtraChannelType.SpotColor)
        {
            spot = new float[4];
            for (int i = 0; i < spot.Length; i++)
            {
                spot[i] = JxlFieldReader.ReadF16(ref reader);
            }
        }
        else if (rawType == (uint)JxlExtraChannelType.Cfa)
        {
            cfa = JxlFieldReader.ReadU32(ref reader, U32Dist.Val(1), U32Dist.Bits(2), U32Dist.BitsOffset(4, 3), U32Dist.BitsOffset(8, 19));
        }

        reader.ThrowIfOverrun();

        // Types 7..14 are reserved and 15 is unknown: the reference decoder rejects both.
        if (rawType is (>= 7 and <= 15) or > (uint)JxlExtraChannelType.Optional)
        {
            throw new JxlUnsupportedFeatureException($"Unknown extra channel type {rawType}.");
        }

        return new JxlExtraChannelInfo
        {
            Type = (JxlExtraChannelType)rawType,
            BitDepth = bitDepth,
            DimShift = dimShift,
            Name = name,
            AlphaAssociated = associated,
            SpotColor = spot,
            CfaChannel = cfa,
        };
    }
}

/// <summary>Display tone-mapping hints.</summary>
internal readonly record struct JxlToneMapping(float IntensityTarget, float MinNits, bool RelativeToMaxDisplay, float LinearBelow)
{
    /// <summary>The default peak luminance in nits (<c>kDefaultIntensityTarget</c> in libjxl).</summary>
    public const float DefaultIntensityTarget = 255f;

    public static JxlToneMapping Default => new(DefaultIntensityTarget, 0f, false, 0f);

    public static JxlToneMapping Read(ref JxlBitReader reader)
    {
        if (reader.ReadBool())
        {
            return Default;
        }

        float intensity = JxlFieldReader.ReadF16(ref reader);
        if (intensity <= 0f)
        {
            throw new JxlDecodingException("Invalid intensity target.");
        }

        float minNits = JxlFieldReader.ReadF16(ref reader);
        if (minNits < 0f || minNits > intensity)
        {
            throw new JxlDecodingException("Invalid minimum luminance.");
        }

        bool relative = reader.ReadBool();
        float linearBelow = JxlFieldReader.ReadF16(ref reader);
        if (linearBelow < 0f || (relative && linearBelow > 1f))
        {
            throw new JxlDecodingException("Invalid linear_below.");
        }

        reader.ThrowIfOverrun();
        return new JxlToneMapping(intensity, minNits, relative, linearBelow);
    }
}
