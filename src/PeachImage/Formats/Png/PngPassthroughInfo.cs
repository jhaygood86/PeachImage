namespace PeachImage.Formats.Png;

/// <summary>
/// The raw, CRC-validated PNG chunk data needed to embed a PNG's compressed pixel stream directly into
/// another container (e.g. a PDF <c>/FlateDecode</c> image stream) without inflating/deflating it. See
/// <see cref="PngPassthrough.TryRead"/>.
/// </summary>
/// <param name="Width">IHDR width, in pixels.</param>
/// <param name="Height">IHDR height, in pixels.</param>
/// <param name="ColorType">IHDR color type.</param>
/// <param name="BitDepth">IHDR bit depth.</param>
/// <param name="IsInterlaced">Whether IHDR declares Adam7 interlacing. Adam7 data cannot be used as a PDF image stream as-is.</param>
/// <param name="HasTrns">Whether a <c>tRNS</c> chunk is present.</param>
/// <param name="IsAnimated">Whether an <c>acTL</c> chunk is present (the file is an APNG). <see cref="IdatData"/> is still the default image's data; PeachImage does not interpret animation frames.</param>
/// <param name="IdatData">The concatenated, CRC-validated <c>IDAT</c> chunk payloads, in file order — a complete zlib (RFC 1950) stream, byte-for-byte as it appears in the file.</param>
/// <param name="PaletteData">The raw <c>PLTE</c> chunk bytes (tightly packed RGB triples), or <see langword="null"/> if <paramref name="ColorType"/> is not <see cref="PngColorType.Palette"/>.</param>
/// <param name="TrnsData">The raw <c>tRNS</c> chunk bytes, or <see langword="null"/> if <paramref name="HasTrns"/> is <see langword="false"/>.</param>
public readonly record struct PngPassthroughInfo(
    int Width,
    int Height,
    PngColorType ColorType,
    byte BitDepth,
    bool IsInterlaced,
    bool HasTrns,
    bool IsAnimated,
    byte[] IdatData,
    byte[]? PaletteData,
    byte[]? TrnsData);
