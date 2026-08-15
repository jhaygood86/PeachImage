namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>VP8 segmentation header (RFC 6386 section 9.3), parsed from partition 0.</summary>
internal sealed class Vp8SegmentHeader
{
    public const int NumSegments = 4;
    public const int NumTreeProbabilities = 3;

    public bool UseSegment { get; private set; }

    public bool UpdateMap { get; private set; }

    /// <summary>When true, per-segment quantizer/filter-strength values are absolute; when false, they are deltas from the frame's base values.</summary>
    public bool AbsoluteDelta { get; private set; } = true;

    public int[] Quantizer { get; } = new int[NumSegments];

    public int[] FilterStrength { get; } = new int[NumSegments];

    /// <summary>Tree probabilities used to decode each macroblock's segment id, valid only when <see cref="UpdateMap"/> is true.</summary>
    public byte[] SegmentTreeProbabilities { get; } = new byte[NumTreeProbabilities];

    public static Vp8SegmentHeader Parse(Vp8BoolDecoder br)
    {
        var header = new Vp8SegmentHeader
        {
            UseSegment = br.GetFlag(),
        };

        if (header.UseSegment)
        {
            header.UpdateMap = br.GetFlag();
            bool updateData = br.GetFlag();
            if (updateData)
            {
                header.AbsoluteDelta = br.GetFlag();
                for (int s = 0; s < NumSegments; s++)
                {
                    header.Quantizer[s] = br.GetFlag() ? br.GetSignedValue(7) : 0;
                }

                for (int s = 0; s < NumSegments; s++)
                {
                    header.FilterStrength[s] = br.GetFlag() ? br.GetSignedValue(6) : 0;
                }
            }

            if (header.UpdateMap)
            {
                for (int s = 0; s < NumTreeProbabilities; s++)
                {
                    header.SegmentTreeProbabilities[s] = br.GetFlag() ? (byte)br.GetValue(8) : (byte)255;
                }
            }
        }
        else
        {
            header.UpdateMap = false;
        }

        return header;
    }
}