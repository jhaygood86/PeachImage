namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// VP8 quantization indices (RFC 6386 section 9.6): a base index plus five optional signed deltas, parsed from
/// partition 0. See <see cref="Vp8Dequantizer"/> for how these resolve into per-segment dequantization factors.
/// </summary>
internal sealed class Vp8QuantIndices
{
    /// <summary>Base luma AC quantizer index, in [0,127].</summary>
    public required int BaseQ0 { get; init; }

    public required int Y1DcDelta { get; init; }

    public required int Y2DcDelta { get; init; }

    public required int Y2AcDelta { get; init; }

    public required int UvDcDelta { get; init; }

    public required int UvAcDelta { get; init; }

    public static Vp8QuantIndices Parse(Vp8BoolDecoder br)
    {
        int baseQ0 = (int)br.GetValue(7);
        int y1Dc = br.GetFlag() ? br.GetSignedValue(4) : 0;
        int y2Dc = br.GetFlag() ? br.GetSignedValue(4) : 0;
        int y2Ac = br.GetFlag() ? br.GetSignedValue(4) : 0;
        int uvDc = br.GetFlag() ? br.GetSignedValue(4) : 0;
        int uvAc = br.GetFlag() ? br.GetSignedValue(4) : 0;

        return new Vp8QuantIndices
        {
            BaseQ0 = baseQ0,
            Y1DcDelta = y1Dc,
            Y2DcDelta = y2Dc,
            Y2AcDelta = y2Ac,
            UvDcDelta = uvDc,
            UvAcDelta = uvAc,
        };
    }
}