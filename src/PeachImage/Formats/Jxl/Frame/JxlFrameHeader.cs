using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Frame;

internal enum JxlFrameEncoding : uint
{
    VarDct = 0,
    Modular = 1,
}

internal enum JxlFrameType : uint
{
    /// <summary>A regular frame: may be cropped, blended on earlier frames, and displayed.</summary>
    Regular = 0,

    /// <summary>A downsampled frame used only as the DC of a later frame.</summary>
    Dc = 1,

    /// <summary>A frame used only as a source for patches.</summary>
    ReferenceOnly = 2,

    /// <summary>Like <see cref="Regular"/> but not used for progressive rendering.</summary>
    SkipProgressive = 3,
}

internal enum JxlColorTransform : uint
{
    Xyb = 0,
    None = 1,
    YCbCr = 2,
}

internal enum JxlBlendMode : uint
{
    Replace = 0,
    Add = 1,
    Blend = 2,
    AlphaWeightedAdd = 3,
    Mul = 4,
}

/// <summary>Flag bits of <see cref="JxlFrameHeader.Flags"/>.</summary>
[Flags]
internal enum JxlFrameFlags : ulong
{
    None = 0,
    Noise = 1,
    Patches = 2,
    Splines = 16,
    UseDcFrame = 32,
    SkipAdaptiveDcSmoothing = 128,
}

/// <summary>How a frame is combined with a previously saved one.</summary>
internal readonly record struct JxlBlendingInfo(JxlBlendMode Mode, uint AlphaChannel, bool Clamp, uint Source)
{
    public static JxlBlendingInfo Default => new(JxlBlendMode.Replace, 0, false, 0);

    public static JxlBlendingInfo Read(ref JxlBitReader br, int numExtraChannels, bool isPartialFrame)
    {
        uint rawMode = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.BitsOffset(2, 3));
        if (rawMode > (uint)JxlBlendMode.Mul)
        {
            throw new JxlDecodingException("Invalid blend_mode.");
        }

        var mode = (JxlBlendMode)rawMode;
        bool usesAlpha = numExtraChannels > 0 && mode is JxlBlendMode.Blend or JxlBlendMode.AlphaWeightedAdd;
        uint alphaChannel = 0;
        if (usesAlpha)
        {
            alphaChannel = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.BitsOffset(3, 3));
            if (alphaChannel >= numExtraChannels)
            {
                throw new JxlDecodingException("Invalid alpha channel for blending.");
            }
        }

        bool clamp = false;
        if (usesAlpha || mode == JxlBlendMode.Mul)
        {
            clamp = br.ReadBool();
        }

        uint source = 0;
        if (mode != JxlBlendMode.Replace || isPartialFrame)
        {
            source = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3));
        }

        br.ThrowIfOverrun();
        return new JxlBlendingInfo(mode, alphaChannel, clamp, source);
    }
}

/// <summary>The loop-filter parameters of a frame: Gabor-like smoothing and the edge-preserving filter.</summary>
internal sealed class JxlLoopFilter
{
    public const int EpfSharpEntries = 8;

    private const float GabWeight1 = 1.1f * 0.104699568f;
    private const float GabWeight2 = 1.1f * 0.055680538f;

    public bool Gab { get; private set; } = true;

    /// <summary>A loop filter with default weights but the given Gaborish setting and EPF iteration count.</summary>
    internal static JxlLoopFilter Create(bool gaborish, int epfIterations) => new() { Gab = gaborish, EpfIterations = epfIterations };

    /// <summary>Gabor weights: for each of X, Y, B the (weight1, weight2) pair.</summary>
    public float[] GabWeights { get; private set; } = [GabWeight1, GabWeight2, GabWeight1, GabWeight2, GabWeight1, GabWeight2];

    public int EpfIterations { get; private set; } = 2;

    public float[] EpfSharpLut { get; private set; } = DefaultSharpLut();

    public float[] EpfChannelScale { get; private set; } = [40f, 5f, 3.5f];

    public float EpfPass1ZeroFlush { get; private set; } = 0.45f;

    public float EpfPass2ZeroFlush { get; private set; } = 0.6f;

    public float EpfQuantMul { get; private set; } = 0.46f;

    public float EpfPass0SigmaScale { get; private set; } = 0.9f;

    public float EpfPass2SigmaScale { get; private set; } = 6.5f;

    public float EpfBorderSadMul { get; private set; } = 0.6666666666666666f;

    public float EpfSigmaForModular { get; private set; } = 1.0f;

    public static JxlLoopFilter Read(ref JxlBitReader br, bool isModular)
    {
        var result = new JxlLoopFilter();
        if (br.ReadBool())
        {
            return result;
        }

        result.Gab = br.ReadBool();
        if (result.Gab && br.ReadBool())
        {
            var weights = new float[6];
            for (int channel = 0; channel < 3; channel++)
            {
                weights[channel * 2] = JxlFieldReader.ReadF16(ref br);
                weights[(channel * 2) + 1] = JxlFieldReader.ReadF16(ref br);
                if (Math.Abs(1.0f + ((weights[channel * 2] + weights[(channel * 2) + 1]) * 4)) < 1e-8)
                {
                    throw new JxlDecodingException("Gaborish weights lead to a near-zero unnormalized kernel.");
                }
            }

            result.GabWeights = weights;
        }

        result.EpfIterations = (int)br.ReadBits(2);
        if (result.EpfIterations > 0)
        {
            if (!isModular && br.ReadBool())
            {
                var lut = new float[EpfSharpEntries];
                for (int i = 0; i < lut.Length; i++)
                {
                    lut[i] = JxlFieldReader.ReadF16(ref br);
                }

                result.EpfSharpLut = lut;
            }

            if (br.ReadBool())
            {
                result.EpfChannelScale = [JxlFieldReader.ReadF16(ref br), JxlFieldReader.ReadF16(ref br), JxlFieldReader.ReadF16(ref br)];
                result.EpfPass1ZeroFlush = JxlFieldReader.ReadF16(ref br);
                result.EpfPass2ZeroFlush = JxlFieldReader.ReadF16(ref br);
            }

            if (br.ReadBool())
            {
                if (!isModular)
                {
                    result.EpfQuantMul = JxlFieldReader.ReadF16(ref br);
                }

                result.EpfPass0SigmaScale = JxlFieldReader.ReadF16(ref br);
                result.EpfPass2SigmaScale = JxlFieldReader.ReadF16(ref br);
                result.EpfBorderSadMul = JxlFieldReader.ReadF16(ref br);
            }

            if (isModular)
            {
                result.EpfSigmaForModular = JxlFieldReader.ReadF16(ref br);
                if (result.EpfSigmaForModular < 1e-8f)
                {
                    throw new JxlDecodingException("EPF: sigma for modular is too small.");
                }
            }
        }

        JxlFieldReader.SkipExtensions(ref br);
        br.ThrowIfOverrun();
        return result;
    }

    private static float[] DefaultSharpLut()
    {
        var lut = new float[EpfSharpEntries];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = i / (float)(EpfSharpEntries - 1);
        }

        return lut;
    }
}

/// <summary>Frame dimensions and the derived group geometry, in pixels, blocks and groups.</summary>
internal readonly struct JxlFrameDimensions
{
    public const int BlockDim = 8;
    public const int GroupDim = 256;

    public JxlFrameDimensions(long xsizePx, long ysizePx, int groupSizeShift, int maxHShift, int maxVShift, bool modular, int upsampling)
    {
        GroupDimension = (GroupDim >> 1) << groupSizeShift;
        DcGroupDimension = GroupDimension * BlockDim;
        XSizeUpsampled = (int)xsizePx;
        YSizeUpsampled = (int)ysizePx;
        XSize = (int)DivCeil(xsizePx, upsampling);
        YSize = (int)DivCeil(ysizePx, upsampling);
        XSizeBlocks = (int)(DivCeil(XSize, BlockDim << maxHShift) << maxHShift);
        YSizeBlocks = (int)(DivCeil(YSize, BlockDim << maxVShift) << maxVShift);
        XSizePadded = modular ? XSize : XSizeBlocks * BlockDim;
        YSizePadded = modular ? YSize : YSizeBlocks * BlockDim;
        XSizeGroups = (int)DivCeil(XSize, GroupDimension);
        YSizeGroups = (int)DivCeil(YSize, GroupDimension);
        XSizeDcGroups = (int)DivCeil(XSizeBlocks, GroupDimension);
        YSizeDcGroups = (int)DivCeil(YSizeBlocks, GroupDimension);
        NumGroups = XSizeGroups * YSizeGroups;
        NumDcGroups = XSizeDcGroups * YSizeDcGroups;
    }

    /// <summary>Group edge in pixels (<c>128 &lt;&lt; group_size_shift</c>).</summary>
    public int GroupDimension { get; }

    public int DcGroupDimension { get; }

    /// <summary>Width after dividing by the upsampling factor.</summary>
    public int XSize { get; }

    public int YSize { get; }

    /// <summary>Output width before dividing by the upsampling factor.</summary>
    public int XSizeUpsampled { get; }

    public int YSizeUpsampled { get; }

    public int XSizePadded { get; }

    public int YSizePadded { get; }

    public int XSizeBlocks { get; }

    public int YSizeBlocks { get; }

    public int XSizeGroups { get; }

    public int YSizeGroups { get; }

    public int XSizeDcGroups { get; }

    public int YSizeDcGroups { get; }

    public int NumGroups { get; }

    public int NumDcGroups { get; }

    private static long DivCeil(long a, long b) => (a + b - 1) / b;
}

/// <summary>A frame's header: encoding, flags, color transform, sizing, passes, blending, animation timing and loop filter.</summary>
internal sealed class JxlFrameHeader
{
    public const int MaxNumPasses = 11;

    public JxlFrameEncoding Encoding { get; private set; } = JxlFrameEncoding.VarDct;

    public JxlFrameType FrameType { get; private set; } = JxlFrameType.Regular;

    public JxlFrameFlags Flags { get; private set; }

    public JxlColorTransform ColorTransform { get; private set; } = JxlColorTransform.Xyb;

    /// <summary>The per-channel (Cb, Y, Cr) chroma subsampling modes (JPEG order) when <see cref="ColorTransform"/> is YCbCr.</summary>
    public int[] ChromaSubsamplingModes { get; private set; } = [0, 0, 0];

    public int GroupSizeShift { get; private set; } = 1;

    public uint XQmScale { get; private set; } = 3;

    public uint BQmScale { get; private set; } = 2;

    public int NumPasses { get; private set; } = 1;

    public int[] PassShift { get; private set; } = new int[MaxNumPasses];

    public int[] PassDownsample { get; private set; } = [];

    public int[] PassLastPass { get; private set; } = [];

    public int DcLevel { get; private set; }

    public bool CustomSizeOrOrigin { get; private set; }

    public int FrameWidth { get; private set; }

    public int FrameHeight { get; private set; }

    public int OriginX { get; private set; }

    public int OriginY { get; private set; }

    public int Upsampling { get; private set; } = 1;

    public int[] ExtraChannelUpsampling { get; private set; } = [];

    public JxlBlendingInfo Blending { get; private set; } = JxlBlendingInfo.Default;

    public JxlBlendingInfo[] ExtraChannelBlending { get; private set; } = [];

    public uint Duration { get; private set; }

    public uint Timecode { get; private set; }

    public bool IsLast { get; private set; } = true;

    public uint SaveAsReference { get; private set; }

    public bool SaveBeforeColorTransform { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public JxlLoopFilter LoopFilter { get; private set; } = new();

    public JxlFrameDimensions Dimensions { get; private set; }

    public bool IsModular => Encoding == JxlFrameEncoding.Modular;

    /// <summary>Whether this frame may be referenced by a later frame.</summary>
    public bool CanBeReferenced =>
        !IsLast && FrameType != JxlFrameType.Dc && (Duration == 0 || SaveAsReference != 0);

    /// <summary>Whether a color transform applies to this frame.</summary>
    public bool NeedsColorTransform =>
        !SaveBeforeColorTransform || FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive;

    /// <summary>Reads a frame header. <paramref name="imageSize"/> is the size frames default to (the preview size for a preview frame).</summary>
    public static JxlFrameHeader Read(ref JxlBitReader br, JxlImageMetadata metadata, JxlSize imageSize, bool isPreview = false)
    {
        var h = new JxlFrameHeader();
        int numExtraChannels = metadata.ExtraChannels.Count;
        bool isModular = false;

        if (!br.ReadBool())
        {
            uint rawType = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3));
            h.FrameType = (JxlFrameType)rawType;
            if (isPreview && h.FrameType != JxlFrameType.Regular)
            {
                throw new JxlDecodingException("Only a regular frame can be a preview.");
            }

            isModular = br.ReadBool();
            h.Encoding = isModular ? JxlFrameEncoding.Modular : JxlFrameEncoding.VarDct;
            h.Flags = (JxlFrameFlags)JxlFieldReader.ReadU64(ref br);

            if (metadata.XybEncoded)
            {
                h.ColorTransform = JxlColorTransform.Xyb;
            }
            else
            {
                h.ColorTransform = br.ReadBool() ? JxlColorTransform.YCbCr : JxlColorTransform.None;
            }

            if (h.ColorTransform == JxlColorTransform.YCbCr && (h.Flags & JxlFrameFlags.UseDcFrame) == 0)
            {
                h.ChromaSubsamplingModes = [(int)br.ReadBits(2), (int)br.ReadBits(2), (int)br.ReadBits(2)];
            }

            if ((h.Flags & JxlFrameFlags.UseDcFrame) == 0)
            {
                h.Upsampling = 1 << (int)br.ReadBits(2);
                if (numExtraChannels != 0)
                {
                    var ecUpsampling = new int[numExtraChannels];
                    for (int i = 0; i < ecUpsampling.Length; i++)
                    {
                        int dimShift = (int)metadata.ExtraChannels[i].DimShift;
                        int value = (1 << (int)br.ReadBits(2)) << dimShift;
                        if (value < h.Upsampling)
                        {
                            throw new JxlDecodingException($"Extra channel upsampling ({value}) is smaller than color upsampling ({h.Upsampling}).");
                        }

                        if (value > 8)
                        {
                            throw new JxlDecodingException($"Extra channel upsampling is too large ({value}).");
                        }

                        ecUpsampling[i] = value;
                    }

                    h.ExtraChannelUpsampling = ecUpsampling;
                }
            }

            if (isModular)
            {
                h.GroupSizeShift = (int)br.ReadBits(2);
            }

            if (h.Encoding == JxlFrameEncoding.VarDct && h.ColorTransform == JxlColorTransform.Xyb)
            {
                h.XQmScale = br.ReadBits(3);
                h.BQmScale = br.ReadBits(3);
            }
            else
            {
                h.XQmScale = 2;
                h.BQmScale = 2;
            }

            if (h.FrameType != JxlFrameType.ReferenceOnly)
            {
                h.ReadPasses(ref br);
            }

            if (h.FrameType == JxlFrameType.Dc)
            {
                h.DcLevel = (int)JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4));
            }

            bool isPartialFrame = false;
            if (h.FrameType != JxlFrameType.Dc)
            {
                h.CustomSizeOrOrigin = br.ReadBool();
                if (h.CustomSizeOrOrigin)
                {
                    if (h.FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
                    {
                        h.OriginX = JxlFieldReader.UnpackSigned(ReadFrameDimension(ref br));
                        h.OriginY = JxlFieldReader.UnpackSigned(ReadFrameDimension(ref br));
                    }

                    h.FrameWidth = (int)ReadFrameDimension(ref br);
                    h.FrameHeight = (int)ReadFrameDimension(ref br);
                    if (h.FrameWidth <= 0 || h.FrameHeight <= 0)
                    {
                        throw new JxlDecodingException("Invalid crop dimensions for frame: zero width or height.");
                    }

                    if (h.FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
                    {
                        isPartialFrame = h.OriginX > 0 || h.OriginY > 0
                            || (long)h.FrameWidth + h.OriginX < imageSize.Width
                            || (long)h.FrameHeight + h.OriginY < imageSize.Height;
                    }
                }
            }

            if (h.FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
            {
                h.Blending = JxlBlendingInfo.Read(ref br, numExtraChannels, isPartialFrame);
                bool replaceAll = h.Blending.Mode == JxlBlendMode.Replace;
                var ecBlending = new JxlBlendingInfo[numExtraChannels];
                for (int i = 0; i < ecBlending.Length; i++)
                {
                    ecBlending[i] = JxlBlendingInfo.Read(ref br, numExtraChannels, isPartialFrame);
                    replaceAll &= ecBlending[i].Mode == JxlBlendMode.Replace;
                }

                h.ExtraChannelBlending = ecBlending;
                if (isPreview && (!replaceAll || h.CustomSizeOrOrigin))
                {
                    throw new JxlDecodingException("A preview is not compatible with blending.");
                }

                if (metadata.Animation is { } animation)
                {
                    h.Duration = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Bits(8), U32Dist.Bits(32));
                    if (animation.HaveTimecodes)
                    {
                        h.Timecode = br.ReadBits(32);
                    }
                }

                h.IsLast = br.ReadBool();
            }
            else
            {
                h.IsLast = false;
            }

            if (h.FrameType != JxlFrameType.Dc && !h.IsLast)
            {
                h.SaveAsReference = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3));
            }

            if (h.FrameType != JxlFrameType.Dc)
            {
                if (h.CanBeReferenced && h.Blending.Mode == JxlBlendMode.Replace && !isPartialFrame
                    && h.FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
                {
                    h.SaveBeforeColorTransform = br.ReadBool();
                }
                else if (h.FrameType == JxlFrameType.ReferenceOnly)
                {
                    h.SaveBeforeColorTransform = br.ReadBool();
                    long xsize = h.CustomSizeOrOrigin ? h.FrameWidth : imageSize.Width;
                    long ysize = h.CustomSizeOrOrigin ? h.FrameHeight : imageSize.Height;
                    if (!h.SaveBeforeColorTransform
                        && (xsize < imageSize.Width || ysize < imageSize.Height || h.OriginX != 0 || h.OriginY != 0))
                    {
                        throw new JxlDecodingException("A non-patch reference frame has an invalid crop.");
                    }
                }
            }
            else
            {
                h.SaveBeforeColorTransform = true;
            }

            h.Name = JxlFieldReader.ReadName(ref br);
            h.LoopFilter = JxlLoopFilter.Read(ref br, isModular);
            JxlFieldReader.SkipExtensions(ref br);
        }
        else
        {
            // all_default: a regular VarDCT frame, default loop filter, last frame.
            h.LoopFilter = new JxlLoopFilter();
            if (!metadata.XybEncoded)
            {
                h.ColorTransform = JxlColorTransform.None;
            }
        }

        br.ThrowIfOverrun();
        h.ComputeDimensions(imageSize);
        return h;
    }

    private static uint ReadFrameDimension(ref JxlBitReader br) =>
        JxlFieldReader.ReadU32(ref br, U32Dist.Bits(8), U32Dist.BitsOffset(11, 256), U32Dist.BitsOffset(14, 2304), U32Dist.BitsOffset(30, 18688));

    private void ReadPasses(ref JxlBitReader br)
    {
        NumPasses = (int)JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.BitsOffset(3, 4));
        if (NumPasses == 1)
        {
            return;
        }

        int numDownsample = (int)JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.BitsOffset(1, 3));
        if (numDownsample > NumPasses)
        {
            throw new JxlDecodingException($"num_downsample {numDownsample} > num_passes {NumPasses}.");
        }

        var shift = new int[MaxNumPasses];
        for (int i = 0; i < NumPasses - 1; i++)
        {
            shift[i] = (int)br.ReadBits(2);
        }

        PassShift = shift;

        var downsample = new int[numDownsample];
        for (int i = 0; i < numDownsample; i++)
        {
            downsample[i] = (int)JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(4), U32Dist.Val(8));
            if (i > 0 && downsample[i] >= downsample[i - 1])
            {
                throw new JxlDecodingException("The downsample sequence should be decreasing.");
            }
        }

        var lastPass = new int[numDownsample];
        for (int i = 0; i < numDownsample; i++)
        {
            lastPass[i] = (int)JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.Bits(3));
            if (i > 0 && lastPass[i] <= lastPass[i - 1])
            {
                throw new JxlDecodingException("The last_pass sequence should be increasing.");
            }

            if (lastPass[i] >= NumPasses)
            {
                throw new JxlDecodingException($"last_pass {lastPass[i]} >= num_passes {NumPasses}.");
            }
        }

        PassDownsample = downsample;
        PassLastPass = lastPass;
    }

    private void ComputeDimensions(JxlSize imageSize)
    {
        long xsize = FrameWidth != 0 ? FrameWidth : imageSize.Width;
        long ysize = FrameHeight != 0 ? FrameHeight : imageSize.Height;
        if (DcLevel != 0)
        {
            int shift = 3 * DcLevel;
            xsize = (xsize + (1L << shift) - 1) >> shift;
            ysize = (ysize + (1L << shift) - 1) >> shift;
        }

        int maxH = 0;
        int maxV = 0;
        if (ColorTransform == JxlColorTransform.YCbCr)
        {
            ReadOnlySpan<int> hShift = [0, 1, 1, 0];
            ReadOnlySpan<int> vShift = [0, 1, 0, 1];
            foreach (int mode in ChromaSubsamplingModes)
            {
                maxH = Math.Max(maxH, hShift[mode]);
                maxV = Math.Max(maxV, vShift[mode]);
            }
        }

        Dimensions = new JxlFrameDimensions(xsize, ysize, GroupSizeShift, maxH, maxV, IsModular, Upsampling);
    }
}
