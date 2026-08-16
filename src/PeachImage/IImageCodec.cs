namespace PeachImage;

/// <summary>
/// Identifies, decodes, and/or encodes a single image format. This is the only codec abstraction
/// <see cref="Image.Load(Stream, DecoderOptions?)"/>, <see cref="Image.Save(Stream, string, EncoderOptions?)"/>,
/// and <see cref="Image.Identify(Stream)"/> dispatch through — format identity (name/extensions/MIME
/// types) and decode/encode logic live on the same type rather than split across separate decoder and
/// encoder abstractions. Built-in codecs are a fixed, compile-time set; there is no runtime registration.
/// If a codec wants separate decode/encode implementations internally, that split is its own private
/// implementation detail and isn't visible here.
/// </summary>
internal interface IImageCodec
{
    /// <summary>The human-readable name of this format, e.g. <c>"jpeg"</c>.</summary>
    string FormatName { get; }

    /// <summary>File extensions (without the leading dot) commonly associated with this format.</summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>MIME types commonly associated with this format.</summary>
    IReadOnlyList<string> MimeTypes { get; }

    /// <summary>The number of leading bytes <see cref="IsSupportedFileFormat"/> needs to make a determination.</summary>
    int HeaderSize { get; }

    /// <summary>Determines whether <paramref name="header"/> looks like the start of this format.</summary>
    bool IsSupportedFileFormat(ReadOnlySpan<byte> header);

    /// <summary>Whether this codec can decode this format.</summary>
    bool CanDecode { get; }

    /// <summary>Whether this codec can encode this format.</summary>
    bool CanEncode { get; }

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    ImageInfo Identify(Stream stream);

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    Image Decode(Stream stream, DecoderOptions? options = null);

    /// <summary>Encodes <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    void Encode(Image image, Stream stream, EncoderOptions? options = null);
}
