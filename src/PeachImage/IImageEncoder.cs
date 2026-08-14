namespace PeachImage;

/// <summary>Encodes an <see cref="Image"/> to a single image format.</summary>
public interface IImageEncoder
{
    /// <summary>The human-readable name of the format this encoder produces, e.g. <c>"jpeg"</c>.</summary>
    string FormatName { get; }

    /// <summary>File extensions (without the leading dot) commonly associated with this format.</summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>MIME types commonly associated with this format.</summary>
    IReadOnlyList<string> MimeTypes { get; }

    /// <summary>Encodes <paramref name="image"/> and writes the result to <paramref name="stream"/>.</summary>
    void Encode(Image image, Stream stream, EncoderOptions? options = null);
}
