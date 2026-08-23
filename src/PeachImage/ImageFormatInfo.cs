namespace PeachImage;

/// <summary>Capability and identity metadata for a single image format, independent of any particular image.</summary>
/// <param name="FormatName">The name of the format, e.g. <c>"jpeg"</c> — matches <see cref="ImageInfo.FormatName"/>.</param>
/// <param name="FileExtensions">File extensions (without the leading dot) commonly associated with this format.</param>
/// <param name="MimeTypes">MIME types commonly associated with this format.</param>
/// <param name="CanDecode">Whether <see cref="Image.Load(Stream, DecoderOptions?)"/> can decode this format.</param>
/// <param name="CanEncode">Whether <see cref="Image.Save(Stream, string, EncoderOptions?)"/> can encode this format.</param>
/// <param name="CanDecodeTransparency">Whether decoding this format can produce alpha/transparency from an existing file.</param>
/// <param name="CanEncodeTransparency">Whether encoding this format can preserve alpha/transparency from an alpha-bearing source.</param>
public readonly record struct ImageFormatInfo(
    string FormatName,
    IReadOnlyList<string> FileExtensions,
    IReadOnlyList<string> MimeTypes,
    bool CanDecode,
    bool CanEncode,
    bool CanDecodeTransparency,
    bool CanEncodeTransparency);
