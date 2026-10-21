namespace PeachImage.Formats.Ccitt;

/// <summary>
/// Options for decoding a raw CCITT fax stream (Group 3 or Group 4), such as the data of a PDF <c>CCITTFaxDecode</c>
/// filter. Raw fax data has no signature or header, so passing these options to <see cref="Image.Load(Stream, DecoderOptions?)"/>
/// selects the fax decoder directly. The property names and defaults mirror the PDF filter parameters.
/// </summary>
/// <remarks>
/// The result is a <see cref="PixelFormat.Gray8"/> image whose samples are the filter's output bits scaled to 0 or 255.
/// With <see cref="BlackIs1"/> false (the default) black pixels are 0 and white pixels 255; with it true they are reversed.
/// </remarks>
public sealed class CcittFaxDecoderOptions : DecoderOptions, IFormatSelectingOptions
{
    /// <summary>Negative: Group 4 (T.6). Zero: Group 3 one-dimensional. Positive: Group 3 mixed one- and two-dimensional. Default 0.</summary>
    public int K { get; init; }

    /// <summary>Width of the image in pixels. Default 1728.</summary>
    public int Columns { get; init; } = 1728;

    /// <summary>Height of the image in rows, or 0 (the default) to decode until the data ends.</summary>
    public int Rows { get; init; }

    /// <summary>Whether Group 3 EOL codes are expected in the data. They are accepted either way. Default false.</summary>
    public bool EndOfLine { get; init; }

    /// <summary>Whether each encoded row begins on a byte boundary. Default false.</summary>
    public bool EncodedByteAlign { get; init; }

    /// <summary>Whether the data ends with an end-of-block pattern (EOFB or RTC). Default true.</summary>
    public bool EndOfBlock { get; init; } = true;

    /// <summary>Whether 1 bits are black pixels, the reverse of the usual PDF convention. Default false.</summary>
    public bool BlackIs1 { get; init; }

    /// <summary>How many damaged rows to repair (by repeating the previous row) before failing. Repair needs EOL codes in the data. Default 0.</summary>
    public int DamagedRowsBeforeError { get; init; }

    /// <summary>Whether bits within each byte are in least-significant-first order (TIFF FillOrder 2). Default false.</summary>
    public bool LsbFirst { get; init; }

    IImageCodec? IFormatSelectingOptions.Codec => CcittFaxCodec.Instance;
}
