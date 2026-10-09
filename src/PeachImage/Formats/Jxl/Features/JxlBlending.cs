using System.Buffers;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Features;

/// <summary>How a patch is combined with the pixels underneath it.</summary>
internal enum JxlPatchBlendMode : byte
{
    None = 0,
    Replace = 1,
    Add = 2,
    Mul = 3,
    BlendAbove = 4,
    BlendBelow = 5,
    AlphaWeightedAddAbove = 6,
    AlphaWeightedAddBelow = 7,
}

/// <summary>The blending of one channel group of a patch: the mode, which extra channel is the alpha it uses, and whether alpha is clamped.</summary>
internal readonly record struct JxlPatchBlending(JxlPatchBlendMode Mode, int AlphaChannel, bool Clamp)
{
    public const int ModeCount = 8;

    public bool UsesAlpha => Mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.BlendBelow
        or JxlPatchBlendMode.AlphaWeightedAddAbove or JxlPatchBlendMode.AlphaWeightedAddBelow;

    public bool UsesClamp => UsesAlpha || Mode == JxlPatchBlendMode.Mul;
}

/// <summary>Float blending primitives shared by patches and (later) frame blending.</summary>
internal static class JxlBlending
{
    private static float Clamp01(float x) => Math.Min(Math.Max(x, 0f), 1f);

    /// <summary>Alpha-composites one colour or extra channel of <paramref name="fg"/> over <paramref name="bg"/>.</summary>
    public static void AlphaBlend(
        ReadOnlySpan<float> bg,
        ReadOnlySpan<float> bgAlpha,
        ReadOnlySpan<float> fg,
        ReadOnlySpan<float> fgAlpha,
        Span<float> output,
        bool sameChannelAsAlpha,
        bool alphaIsPremultiplied,
        bool clamp)
    {
        int count = output.Length;
        if (sameChannelAsAlpha)
        {
            for (int x = 0; x < count; x++)
            {
                float fa = clamp ? Clamp01(fgAlpha[x]) : fgAlpha[x];
                output[x] = 1f - ((1f - fa) * (1f - bgAlpha[x]));
            }

            return;
        }

        if (alphaIsPremultiplied)
        {
            for (int x = 0; x < count; x++)
            {
                float fa = clamp ? Clamp01(fgAlpha[x]) : fgAlpha[x];
                output[x] = fg[x] + (bg[x] * (1f - fa));
            }

            return;
        }

        for (int x = 0; x < count; x++)
        {
            float fa = clamp ? Clamp01(fgAlpha[x]) : fgAlpha[x];
            float newAlpha = 1f - ((1f - fa) * (1f - bgAlpha[x]));
            float reciprocal = newAlpha > 0 ? 1f / newAlpha : 0f;
            output[x] = ((fg[x] * fa) + (bg[x] * bgAlpha[x] * (1f - fa))) * reciprocal;
        }
    }

    /// <summary>Adds <paramref name="fg"/> weighted by its alpha to <paramref name="bg"/>.</summary>
    public static void AlphaWeightedAdd(ReadOnlySpan<float> bg, ReadOnlySpan<float> fg, ReadOnlySpan<float> fgAlpha, Span<float> output, bool sameChannelAsAlpha, bool clamp)
    {
        int count = output.Length;
        if (sameChannelAsAlpha)
        {
            bg[..count].CopyTo(output);
        }
        else if (clamp)
        {
            for (int x = 0; x < count; x++)
            {
                output[x] = bg[x] + (fg[x] * Clamp01(fgAlpha[x]));
            }
        }
        else
        {
            for (int x = 0; x < count; x++)
            {
                output[x] = bg[x] + (fg[x] * fgAlpha[x]);
            }
        }
    }

    /// <summary>Multiplies <paramref name="bg"/> by <paramref name="fg"/>.</summary>
    public static void Multiply(ReadOnlySpan<float> bg, ReadOnlySpan<float> fg, Span<float> output, bool clamp)
    {
        for (int x = 0; x < output.Length; x++)
        {
            output[x] = bg[x] * (clamp ? Clamp01(fg[x]) : fg[x]);
        }
    }

    /// <summary>
    /// Blends a run of <paramref name="count"/> pixels of the foreground channels into the background channels (in place). Both
    /// hold 3 colour channels followed by the extra channels, starting at their given offsets.
    /// <paramref name="extraBlending"/> holds one entry per extra channel.
    /// </summary>
    public static void BlendRun(
        float[][] bgChannels,
        int bgOffset,
        float[][] fgChannels,
        int fgOffset,
        int count,
        JxlPatchBlending colorBlending,
        ReadOnlySpan<JxlPatchBlending> extraBlending,
        IReadOnlyList<JxlExtraChannelInfo> extraInfo)
    {
        int numEc = extraInfo.Count;
        bool hasAlpha = false;
        for (int i = 0; i < numEc; i++)
        {
            if (extraInfo[i].Type == JxlExtraChannelType.Alpha)
            {
                hasAlpha = true;
                break;
            }
        }

        if (count == 0)
        {
            return;
        }

        int channels = 3 + numEc;
        float[] temp = ArrayPool<float>.Shared.Rent(channels * count);
        try
        {
            Span<float> Tmp(int c) => temp.AsSpan(c * count, count);
            ReadOnlySpan<float> Bg(int c) => bgChannels[c].AsSpan(bgOffset, count);
            ReadOnlySpan<float> Fg(int c) => fgChannels[c].AsSpan(fgOffset, count);

            // Extra channels first, so that the alpha they refer to is the pre-blending alpha.
            for (int i = 0; i < numEc; i++)
            {
                var blending = extraBlending[i];
                int c = 3 + i;
                int alpha = 3 + blending.AlphaChannel;
                switch (blending.Mode)
                {
                    case JxlPatchBlendMode.Add:
                        for (int x = 0; x < count; x++)
                        {
                            Tmp(c)[x] = Bg(c)[x] + Fg(c)[x];
                        }

                        break;
                    case JxlPatchBlendMode.BlendAbove:
                        AlphaBlend(Bg(c), Bg(alpha), Fg(c), Fg(alpha), Tmp(c), c == alpha, extraInfo[blending.AlphaChannel].AlphaAssociated, blending.Clamp);
                        break;
                    case JxlPatchBlendMode.BlendBelow:
                        AlphaBlend(Fg(c), Fg(alpha), Bg(c), Bg(alpha), Tmp(c), c == alpha, extraInfo[blending.AlphaChannel].AlphaAssociated, blending.Clamp);
                        break;
                    case JxlPatchBlendMode.AlphaWeightedAddAbove:
                        AlphaWeightedAdd(Bg(c), Fg(c), Fg(alpha), Tmp(c), c == alpha, blending.Clamp);
                        break;
                    case JxlPatchBlendMode.AlphaWeightedAddBelow:
                        AlphaWeightedAdd(Fg(c), Bg(c), Bg(alpha), Tmp(c), c == alpha, blending.Clamp);
                        break;
                    case JxlPatchBlendMode.Mul:
                        Multiply(Bg(c), Fg(c), Tmp(c), blending.Clamp);
                        break;
                    case JxlPatchBlendMode.Replace:
                        Fg(c).CopyTo(Tmp(c));
                        break;
                    default:
                        Bg(c).CopyTo(Tmp(c));
                        break;
                }
            }

            int colorAlpha = colorBlending.AlphaChannel;
            bool premultiplied = hasAlpha && colorAlpha < numEc && extraInfo[colorAlpha].AlphaAssociated;
            void AddAll()
            {
                for (int p = 0; p < 3; p++)
                {
                    for (int x = 0; x < count; x++)
                    {
                        Tmp(p)[x] = Bg(p)[x] + Fg(p)[x];
                    }
                }
            }

            void CopyFg()
            {
                for (int p = 0; p < 3; p++)
                {
                    Fg(p).CopyTo(Tmp(p));
                }
            }

            void BlendWeighted(float[][] bottom, int bottomOffset, float[][] top, int topOffset)
            {
                // Colour channels use the alpha of the layers; the alpha channel itself is blended too.
                int alpha = 3 + colorAlpha;
                for (int p = 0; p < 3; p++)
                {
                    AlphaBlend(
                        bottom[p].AsSpan(bottomOffset, count),
                        bottom[alpha].AsSpan(bottomOffset, count),
                        top[p].AsSpan(topOffset, count),
                        top[alpha].AsSpan(topOffset, count),
                        Tmp(p),
                        false,
                        premultiplied,
                        colorBlending.Clamp);
                }

                AlphaBlend(
                    bottom[alpha].AsSpan(bottomOffset, count),
                    bottom[alpha].AsSpan(bottomOffset, count),
                    top[alpha].AsSpan(topOffset, count),
                    top[alpha].AsSpan(topOffset, count),
                    Tmp(alpha),
                    true,
                    premultiplied,
                    colorBlending.Clamp);
            }

            void AddWeighted(float[][] bottom, int bottomOffset, float[][] top, int topOffset)
            {
                int alpha = 3 + colorAlpha;
                for (int p = 0; p < 3; p++)
                {
                    AlphaWeightedAdd(
                        bottom[p].AsSpan(bottomOffset, count),
                        top[p].AsSpan(topOffset, count),
                        top[alpha].AsSpan(topOffset, count),
                        Tmp(p),
                        false,
                        colorBlending.Clamp);
                }
            }

            switch (colorBlending.Mode)
            {
                case JxlPatchBlendMode.Add:
                    AddAll();
                    break;
                case JxlPatchBlendMode.AlphaWeightedAddAbove:
                    if (hasAlpha)
                    {
                        AddWeighted(bgChannels, bgOffset, fgChannels, fgOffset);
                    }
                    else
                    {
                        AddAll();
                    }

                    break;
                case JxlPatchBlendMode.AlphaWeightedAddBelow:
                    if (hasAlpha)
                    {
                        AddWeighted(fgChannels, fgOffset, bgChannels, bgOffset);
                    }
                    else
                    {
                        AddAll();
                    }

                    break;
                case JxlPatchBlendMode.BlendAbove:
                    if (hasAlpha)
                    {
                        BlendWeighted(bgChannels, bgOffset, fgChannels, fgOffset);
                    }
                    else
                    {
                        CopyFg();
                    }

                    break;
                case JxlPatchBlendMode.BlendBelow:
                    if (hasAlpha)
                    {
                        BlendWeighted(fgChannels, fgOffset, bgChannels, bgOffset);
                    }
                    else
                    {
                        CopyFg();
                    }

                    break;
                case JxlPatchBlendMode.Mul:
                    for (int p = 0; p < 3; p++)
                    {
                        Multiply(Bg(p), Fg(p), Tmp(p), colorBlending.Clamp);
                    }

                    break;
                case JxlPatchBlendMode.Replace:
                    CopyFg();
                    break;
                default:
                    for (int p = 0; p < 3; p++)
                    {
                        Bg(p).CopyTo(Tmp(p));
                    }

                    break;
            }

            for (int c = 0; c < channels; c++)
            {
                temp.AsSpan(c * count, count).CopyTo(bgChannels[c].AsSpan(bgOffset, count));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
        }
    }
}
