namespace PeachImage.Formats.Webp.Decoding.Vp8.LoopFilter;

/// <summary>Resolved per-macroblock filter strength: a limit (0 means "skip filtering this macroblock entirely"), an interior limit, and a high-edge-variance threshold.</summary>
internal readonly struct Vp8FilterStrength
{
    public required int Limit { get; init; }

    public required int InteriorLimit { get; init; }

    public required int HevThreshold { get; init; }

    public bool IsFilteringEnabled => Limit != 0;
}

/// <summary>
/// Resolves, for each of the 4 possible segments crossed with "is this macroblock B_PRED" (2), the effective
/// loop filter level/interior-limit/hev-threshold triple (RFC 6386 section 15.2, libwebp's
/// <c>src/dec/frame_dec.c</c> <c>PrecomputeFilterStrengths</c>). A lone VP8 keyframe only ever consults index 0
/// of the loop filter header's ref-frame delta array (the "intra frame" entry) and index 0 of its mode delta
/// array (the "B_PRED" entry, applied only when the macroblock actually is B_PRED).
/// </summary>
internal static class Vp8LoopFilterLevelResolver
{
    public static Vp8FilterStrength[,] Resolve(Vp8LoopFilterHeader header, Vp8SegmentHeader segmentHeader)
    {
        var result = new Vp8FilterStrength[Vp8SegmentHeader.NumSegments, 2];

        for (int s = 0; s < Vp8SegmentHeader.NumSegments; s++)
        {
            int baseLevel;
            if (segmentHeader.UseSegment)
            {
                baseLevel = segmentHeader.FilterStrength[s];
                if (!segmentHeader.AbsoluteDelta)
                {
                    baseLevel += header.Level;
                }
            }
            else
            {
                baseLevel = header.Level;
            }

            for (int i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                int level = baseLevel;
                if (header.UseLfDelta)
                {
                    level += header.RefLfDelta[0];
                    if (i4x4 != 0)
                    {
                        level += header.ModeLfDelta[0];
                    }
                }

                level = level < 0 ? 0 : level > 63 ? 63 : level;

                if (level > 0)
                {
                    int interiorLimit = level;
                    if (header.Sharpness > 0)
                    {
                        interiorLimit = header.Sharpness > 4 ? interiorLimit >> 2 : interiorLimit >> 1;
                        int cap = 9 - header.Sharpness;
                        if (interiorLimit > cap)
                        {
                            interiorLimit = cap;
                        }
                    }

                    if (interiorLimit < 1)
                    {
                        interiorLimit = 1;
                    }

                    result[s, i4x4] = new Vp8FilterStrength
                    {
                        Limit = (2 * level) + interiorLimit,
                        InteriorLimit = interiorLimit,
                        HevThreshold = level >= 40 ? 2 : level >= 15 ? 1 : 0,
                    };
                }
                else
                {
                    result[s, i4x4] = new Vp8FilterStrength { Limit = 0, InteriorLimit = 0, HevThreshold = 0 };
                }
            }
        }

        return result;
    }
}