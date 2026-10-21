namespace PeachImage.Formats.Jbig2;

/// <summary>
/// JBIG2-specific decode options. Standalone <c>.jb2</c>/<c>.jbig2</c> files are recognized by their signature and need no
/// options. The segments of a PDF <c>JBIG2Decode</c> stream have no file header, so set <see cref="Embedded"/> to select
/// the decoder directly, and pass the filter's <c>JBIG2Globals</c> stream (if any) as <see cref="Globals"/>.
/// </summary>
/// <remarks>
/// The result is a <see cref="PixelFormat.Gray8"/> image: JBIG2 black pixels become 0 and white pixels 255, or the reverse
/// when <see cref="InvertOutput"/> is set.
/// </remarks>
public sealed class Jbig2DecoderOptions : DecoderOptions, IFormatSelectingOptions
{
    /// <summary>Whether the stream is an embedded (PDF-style) organisation: a bare sequence of segments with no file header.</summary>
    public bool Embedded { get; init; }

    /// <summary>The shared global segments (a PDF <c>JBIG2Globals</c> stream) that embedded page data may refer to. Ignored for standalone files.</summary>
    public ReadOnlyMemory<byte> Globals { get; init; }

    /// <summary>Whether to output black as 255 and white as 0, the reverse of the default.</summary>
    public bool InvertOutput { get; init; }

    IImageCodec? IFormatSelectingOptions.Codec => Embedded ? Jbig2Codec.Instance : null;
}
