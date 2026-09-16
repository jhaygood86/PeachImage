using System.IO.Compression;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Decoding;

/// <summary>
/// <see cref="PngAlphaSplit"/> de-interleaves a PNG's color+alpha <c>IDAT</c> data into two independent
/// zlib streams without a full pixel decode, for callers (e.g. PDF embedding) that want a color XObject +
/// <c>/SMask</c> pair. These tests build minimal PNGs via <see cref="PngTestFileBuilder"/> and check the
/// returned planes against the known-good interleaved input.
/// </summary>
public class PngAlphaSplitTests
{
    [Fact]
    public void GrayscaleAlpha_SplitsGrayAndAlphaPlanes()
    {
        // 2x1, color type 4 (GrayscaleAlpha), 8-bit: pixel0 = (gray 100, alpha 200), pixel1 = (gray 150, alpha 50).
        byte[] row = [0, 100, 200, 150, 50];
        byte[] file = PngTestFileBuilder.Build(2, 1, 8, colorType: 4, palette: null, trns: null, scanlines: [row]);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.Equal(2, info.Width);
        Assert.Equal(1, info.Height);
        Assert.Equal(8, info.AlphaBitDepth);
        Assert.Equal(8, info.ColorBitDepth);
        Assert.False(info.ColorIsRgb);
        Assert.Equal(new byte[] { 100, 150 }, InflatePlaneSamples(info.ColorData!, info.Height, samplesPerRow: 2));
        Assert.Equal(new byte[] { 200, 50 }, InflatePlaneSamples(info.AlphaData, info.Height, samplesPerRow: 2));
    }

    [Fact]
    public void TruecolorAlpha_SplitsRgbAndAlphaPlanes()
    {
        // 2x1, color type 6 (TruecolorAlpha), 8-bit: pixel0 = (10,20,30, a=255), pixel1 = (40,50,60, a=0).
        byte[] row = [0, 10, 20, 30, 255, 40, 50, 60, 0];
        byte[] file = PngTestFileBuilder.Build(2, 1, 8, colorType: 6, palette: null, trns: null, scanlines: [row]);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.ColorIsRgb);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, InflatePlaneSamples(info.ColorData!, info.Height, samplesPerRow: 6));
        Assert.Equal(new byte[] { 255, 0 }, InflatePlaneSamples(info.AlphaData, info.Height, samplesPerRow: 2));
    }

    [Fact]
    public void PaletteWithPartialAlphaTrns_ReturnsNullColorAndResolvedAlphaPlane()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0]; // index 0: red, index 1: green
        byte[] trns = [128, 255]; // index 0: partial alpha, index 1: opaque
        byte[] row = [0, 0, 1]; // filter type None, indices 0 and 1
        byte[] file = PngTestFileBuilder.Build(2, 1, 8, colorType: 3, palette: palette, trns: trns, scanlines: [row]);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.Null(info.ColorData);
        Assert.Equal(0, info.ColorBitDepth);
        Assert.Equal(new byte[] { 128, 255 }, InflatePlaneSamples(info.AlphaData, info.Height, samplesPerRow: 2));
    }

    [Fact]
    public void PaletteWithBinaryOnlyTrns_ReturnsFalse()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0];
        byte[] trns = [0, 255]; // pure binary mask, no genuine partial-alpha entry
        byte[] row = [0, 0, 1];
        byte[] file = PngTestFileBuilder.Build(2, 1, 8, colorType: 3, palette: palette, trns: trns, scanlines: [row]);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out _);

        Assert.False(ok);
    }

    [Fact]
    public void OpaqueTruecolor_ReturnsFalse()
    {
        byte[] row = [0, 10, 20, 30];
        byte[] file = PngTestFileBuilder.Build(1, 1, 8, colorType: 2, palette: null, trns: null, scanlines: [row]);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out _);

        Assert.False(ok);
    }

    [Fact]
    public void InterlacedGrayscaleAlpha_ReturnsFalse()
    {
        byte[] row = [0, 1, 2];
        byte[] file = PngTestFileBuilder.Build(1, 1, 8, colorType: 4, palette: null, trns: null, scanlines: [row], interlace: true);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out _);

        Assert.False(ok);
    }

    [Fact]
    public void SixteenBitTruecolorAlpha_ReturnsFalse()
    {
        byte[] row = [0, 0, 10, 0, 20, 0, 30, 0, 255];
        byte[] file = PngTestFileBuilder.Build(1, 1, 16, colorType: 6, palette: null, trns: null, scanlines: [row]);

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(file), out _);

        Assert.False(ok);
    }

    [Fact]
    public void TrySplit_OperatesOnAlreadyReadPassthroughInfo()
    {
        byte[] row = [0, 100, 200];
        byte[] file = PngTestFileBuilder.Build(1, 1, 8, colorType: 4, palette: null, trns: null, scanlines: [row]);

        bool readOk = PngPassthrough.TryRead(new MemoryStream(file), out var passthrough);
        Assert.True(readOk);

        bool splitOk = PngAlphaSplit.TrySplit(in passthrough, out var info);

        Assert.True(splitOk);
        Assert.Equal(new byte[] { 100 }, InflatePlaneSamples(info.ColorData!, info.Height, samplesPerRow: 1));
        Assert.Equal(new byte[] { 200 }, InflatePlaneSamples(info.AlphaData, info.Height, samplesPerRow: 1));
    }

    [Fact]
    public void NonPngStream_ReturnsFalse()
    {
        byte[] notAPng = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        bool ok = PngAlphaSplit.TryRead(new MemoryStream(notAPng), out var info);

        Assert.False(ok);
        Assert.Equal(default, info);
    }

    /// <summary>
    /// Inflates a <see cref="PngAlphaSplit"/> plane stream and strips each row's leading PNG filter-type
    /// byte (always 0/None), returning just the concatenated sample bytes — <see cref="PngAlphaSplitInfo"/>'s
    /// planes are PNG-row-filtered on purpose (see its doc comments), not raw flat pixel bytes.
    /// </summary>
    private static byte[] InflatePlaneSamples(byte[] zlibData, int height, int samplesPerRow)
    {
        using var compressed = new MemoryStream(zlibData);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] inflated = raw.ToArray();

        var result = new List<byte>(height * samplesPerRow);
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            offset++; // skip the filter-type byte
            result.AddRange(inflated.AsSpan(offset, samplesPerRow).ToArray());
            offset += samplesPerRow;
        }

        return [.. result];
    }
}
