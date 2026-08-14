namespace PeachImage;

/// <summary>
/// Bundles the decoder and/or encoder for a single image format so it can be registered with
/// <see cref="ImageFormatManager"/> as a unit.
/// </summary>
public interface IImageCodec
{
    /// <summary>The human-readable name of the format this codec handles, e.g. <c>"jpeg"</c>.</summary>
    string FormatName { get; }

    /// <summary>The decoder for this format, or <see langword="null"/> if this codec is write-only.</summary>
    IImageDecoder? Decoder { get; }

    /// <summary>The encoder for this format, or <see langword="null"/> if this codec is read-only.</summary>
    IImageEncoder? Encoder { get; }
}
