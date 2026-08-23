namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Everything <see cref="TiffImageDecoder"/> needs to decode pixel data, resolved and validated from a
/// <see cref="TiffIfd"/> by <see cref="TiffValidation.Validate"/> — defaults applied, scope checked, and the
/// destination <see cref="PixelFormat"/> already decided, so nothing downstream re-derives these values or
/// re-checks scope.
/// </summary>
internal sealed record TiffImageDescriptor
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Bits per sample, uniform across every channel (validated by <see cref="TiffValidation"/>).</summary>
    public required int BitsPerSample { get; init; }

    public required int SamplesPerPixel { get; init; }

    /// <summary>Raw TIFF Compression tag value: 1 (none), 5 (LZW), or 32773 (PackBits) — the only values that pass validation.</summary>
    public required int Compression { get; init; }

    /// <summary>Raw TIFF PhotometricInterpretation tag value: 0/1 (grayscale), 2 (RGB), 3 (palette), or 5 (CMYK/Separated) — the only values that pass validation.</summary>
    public required int Photometric { get; init; }

    /// <summary>1 (none) or 2 (horizontal differencing) — the only values that pass validation.</summary>
    public required int Predictor { get; init; }

    public required uint RowsPerStrip { get; init; }

    public required uint[] StripOffsets { get; init; }

    public required uint[] StripByteCounts { get; init; }

    /// <summary>Whether the source has an alpha channel (RGB with SamplesPerPixel=4 only — grayscale/palette/CMYK alpha are out of scope).</summary>
    public required bool HasAlpha { get; init; }

    /// <summary>Whether the alpha channel is premultiplied (ExtraSamples=1) rather than straight, when <see cref="HasAlpha"/> is true.</summary>
    public required bool AlphaIsPremultiplied { get; init; }

    /// <summary>The resolved ColorMap: three consecutive 16-bit-scale arrays of length <c>1 &lt;&lt; BitsPerSample</c> (R, then G, then B), non-empty only when <see cref="Photometric"/> is 3 (palette).</summary>
    public required uint[] ColorMap { get; init; }

    /// <summary>The destination pixel format this file decodes to.</summary>
    public required PixelFormat PixelFormat { get; init; }
}
