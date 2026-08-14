namespace PeachImage;

/// <summary>Decodes a single image format from a <see cref="Stream"/>.</summary>
public interface IImageDecoder
{
    /// <summary>The human-readable name of the format this decoder handles, e.g. <c>"jpeg"</c>.</summary>
    string FormatName { get; }

    /// <summary>File extensions (without the leading dot) commonly associated with this format.</summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>MIME types commonly associated with this format.</summary>
    IReadOnlyList<string> MimeTypes { get; }

    /// <summary>The number of leading bytes <see cref="IsSupportedFileFormat"/> needs to make a determination.</summary>
    int HeaderSize { get; }

    /// <summary>Determines whether <paramref name="header"/> looks like the start of this decoder's format.</summary>
    bool IsSupportedFileFormat(ReadOnlySpan<byte> header);

    /// <summary>Reads image dimensions and format information from <paramref name="stream"/> without fully decoding pixel data.</summary>
    ImageInfo Identify(Stream stream);

    /// <summary>Fully decodes <paramref name="stream"/> into an in-memory <see cref="Image"/>.</summary>
    Image Decode(Stream stream, DecoderOptions? options = null);
}
