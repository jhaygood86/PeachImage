namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>VP8 loop filter header (RFC 6386 section 9.4), parsed from partition 0.</summary>
internal sealed class Vp8LoopFilterHeader
{
    public const int NumRefLfDeltas = 4;
    public const int NumModeLfDeltas = 4;

    /// <summary>
    /// True selects the "simple" (luma-only) filter, false the "normal" (luma+chroma) filter. Verified against
    /// libwebp's <c>src/dec/vp8_dec.c</c> <c>ParseFilterHeader</c>: <c>hdr-&gt;simple = VP8Get(br)</c> and
    /// <c>dec-&gt;filter_type = hdr-&gt;simple ? 1 : 2</c> where filter_type 1 is the simple filter - so a decoded
    /// bit value of 1 means "simple". This is the easy-to-get-backwards bit the RFC warns about.
    /// </summary>
    public bool Simple { get; private set; }

    public int Level { get; private set; }

    public int Sharpness { get; private set; }

    public bool UseLfDelta { get; private set; }

    public int[] RefLfDelta { get; } = new int[NumRefLfDeltas];

    public int[] ModeLfDelta { get; } = new int[NumModeLfDeltas];

    /// <summary>True if any filtering is enabled at all (level != 0).</summary>
    public bool IsFilteringEnabled => Level != 0;

    public static Vp8LoopFilterHeader Parse(Vp8BoolDecoder br)
    {
        var header = new Vp8LoopFilterHeader
        {
            Simple = br.GetFlag(),
            Level = (int)br.GetValue(6),
            Sharpness = (int)br.GetValue(3),
            UseLfDelta = br.GetFlag(),
        };

        if (header.UseLfDelta)
        {
            bool updateLfDelta = br.GetFlag();
            if (updateLfDelta)
            {
                // All four ref-frame deltas and all four mode deltas are always read (when present) to keep the
                // bitstream aligned, even though a lone VP8 keyframe only ever consults the intra-frame entries
                // (index 0 of each).
                for (int i = 0; i < NumRefLfDeltas; i++)
                {
                    if (br.GetFlag())
                    {
                        header.RefLfDelta[i] = br.GetSignedValue(6);
                    }
                }

                for (int i = 0; i < NumModeLfDeltas; i++)
                {
                    if (br.GetFlag())
                    {
                        header.ModeLfDelta[i] = br.GetSignedValue(6);
                    }
                }
            }
        }

        return header;
    }
}