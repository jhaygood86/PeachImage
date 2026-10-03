namespace PeachImage.Formats.Png.Decoding;

/// <summary>A decoded PLTE (+ optional tRNS) palette: RGB triples plus per-entry alpha for color type 3.</summary>
internal sealed class PngPalette
{
    private readonly byte[] _rgb;
    private byte[]? _alpha;

    public PngPalette(byte[] rgb)
    {
        _rgb = rgb;
    }

    public int EntryCount => _rgb.Length / 3;

    public void SetAlpha(byte[] alpha)
    {
        _alpha = alpha;
        _rgbaLut = null;
    }

    /// <summary>The palette's RGB triples, three bytes per entry.</summary>
    public ReadOnlySpan<byte> Rgb => _rgb;

    private uint[]? _rgbaLut;

    /// <summary>
    /// One packed little-endian <c>R,G,B,A</c> word per entry (alpha 255 where tRNS has no entry), built on
    /// first use, so a whole pixel resolves with one load and one 4-byte store.
    /// </summary>
    public ReadOnlySpan<uint> RgbaLut => _rgbaLut ??= BuildRgbaLut();

    private uint[] BuildRgbaLut()
    {
        var lut = new uint[EntryCount];
        for (int i = 0; i < lut.Length; i++)
        {
            byte alpha = _alpha is { } a ? (i < a.Length ? a[i] : (byte)255) : (byte)255;
            lut[i] = _rgb[i * 3] | ((uint)_rgb[(i * 3) + 1] << 8) | ((uint)_rgb[(i * 3) + 2] << 16) | ((uint)alpha << 24);
        }

        return lut;
    }

    public (byte R, byte G, byte B, byte A) Resolve(int index)
    {
        if ((uint)index >= (uint)EntryCount)
        {
            throw new PngDecodingException($"Palette index {index} is out of range for a {EntryCount}-entry palette.");
        }

        int offset = index * 3;
        byte alpha = _alpha is { } a ? (index < a.Length ? a[index] : (byte)255) : (byte)255;
        return (_rgb[offset], _rgb[offset + 1], _rgb[offset + 2], alpha);
    }

    public bool HasTransparency => _alpha is { Length: > 0 };

    public static PngPalette Read(byte[] plteData)
    {
        if (plteData.Length == 0 || plteData.Length % 3 != 0)
        {
            throw new PngDecodingException($"PLTE chunk has an invalid length of {plteData.Length} bytes (must be a positive multiple of 3).");
        }

        if (plteData.Length / 3 > 256)
        {
            throw new PngDecodingException("PLTE chunk declares more than 256 entries.");
        }

        return new PngPalette(plteData);
    }
}
