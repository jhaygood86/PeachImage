using System.IO.Compression;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Decoding;

/// <summary>
/// <see cref="PngPassthrough.TryRead"/> reads IHDR/PLTE/tRNS/IDAT chunk data verbatim with no pixel
/// decode, for callers (e.g. PDF embedding) that want the raw compressed bytes rather than a decoded
/// <see cref="Image"/>. These tests build minimal PNGs via <see cref="PngTestFileBuilder"/> (independent
/// of PeachImage's own encoder) and check the reported facts/raw bytes against what was actually written.
/// </summary>
public class PngPassthroughTests
{
    [Fact]
    public void OpaqueTruecolorPng_ReturnsIhdrFactsAndInflatesToOriginalScanlines()
    {
        byte[] row0 = [0, 10, 20, 30, 40, 50, 60]; // filter type None, 2 pixels x 3 samples
        byte[] row1 = [0, 70, 80, 90, 100, 110, 120];
        byte[] file = PngTestFileBuilder.Build(
            width: 2,
            height: 2,
            bitDepth: 8,
            colorType: 2,
            palette: null,
            trns: null,
            scanlines: [row0, row1]);

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.Equal(2, info.Width);
        Assert.Equal(2, info.Height);
        Assert.Equal(PngColorType.Truecolor, info.ColorType);
        Assert.Equal(8, info.BitDepth);
        Assert.False(info.IsInterlaced);
        Assert.False(info.HasTrns);
        Assert.False(info.IsAnimated);
        Assert.Null(info.PaletteData);
        Assert.Null(info.TrnsData);
        Assert.Equal(row0.Concat(row1), InflateAll(info.IdatData));
    }

    [Fact]
    public void PalettePng_ReturnsRawPlteBytes()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0]; // 2 entries: red, green
        byte[] row = [0, 0, 1]; // filter type None, 2 palette indices
        byte[] file = PngTestFileBuilder.Build(
            width: 2,
            height: 1,
            bitDepth: 8,
            colorType: 3,
            palette: palette,
            trns: null,
            scanlines: [row]);

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.Equal(PngColorType.Palette, info.ColorType);
        Assert.Equal(palette, info.PaletteData);
        Assert.Equal(row, InflateAll(info.IdatData));
    }

    [Fact]
    public void GrayscalePngWithTrns_ReportsHasTrnsAndRawBytesWhileStillReturningIdat()
    {
        byte[] trns = [0, 42]; // chroma-key gray value 42
        byte[] row = [0, 42]; // filter type None, 1 gray sample
        byte[] file = PngTestFileBuilder.Build(
            width: 1,
            height: 1,
            bitDepth: 8,
            colorType: 0,
            palette: null,
            trns: trns,
            scanlines: [row]);

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.HasTrns);
        Assert.Equal(trns, info.TrnsData);
        Assert.Equal(row, InflateAll(info.IdatData));
    }

    [Fact]
    public void InterlacedPng_ReportsIsInterlacedTrue()
    {
        byte[] row = [0, 5, 6, 7];
        byte[] file = PngTestFileBuilder.Build(
            width: 1,
            height: 1,
            bitDepth: 8,
            colorType: 2,
            palette: null,
            trns: null,
            scanlines: [row],
            interlace: true);

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.IsInterlaced);
    }

    [Fact]
    public void AnimatedPng_ReportsIsAnimatedTrueButStillReturnsDefaultImageIdat()
    {
        byte[] row = [0, 1, 2, 3];
        byte[] acTl = new byte[8]; // num_frames/num_plays content is irrelevant to passthrough
        byte[] file = PngTestFileBuilder.Build(
            width: 1,
            height: 1,
            bitDepth: 8,
            colorType: 2,
            palette: null,
            trns: null,
            scanlines: [row],
            extraChunks: [("acTL", acTl)]);

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out var info);

        Assert.True(ok);
        Assert.True(info.IsAnimated);
        Assert.Equal(row, InflateAll(info.IdatData));
    }

    [Fact]
    public void NonPngStream_ReturnsFalse()
    {
        byte[] notAPng = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        bool ok = PngPassthrough.TryRead(new MemoryStream(notAPng), out var info);

        Assert.False(ok);
        Assert.Equal(default, info);
    }

    [Fact]
    public void TruncatedFile_ReturnsFalse()
    {
        byte[] row = [0, 1, 2, 3];
        byte[] file = PngTestFileBuilder.Build(2, 1, 8, 2, null, null, [row]);
        byte[] truncated = file[..(file.Length - 10)];

        bool ok = PngPassthrough.TryRead(new MemoryStream(truncated), out _);

        Assert.False(ok);
    }

    [Fact]
    public void CorruptIdatCrc_ReturnsFalse()
    {
        byte[] row = [0, 1, 2, 3];
        byte[] file = PngTestFileBuilder.Build(2, 1, 8, 2, null, null, [row]);
        // Trailing bytes after the last IDAT data byte: 4 (IDAT CRC) + 4 (IEND length) + 4 (IEND type) + 4 (IEND CRC) = 16.
        file[^17] ^= 0xFF; // flip the last byte of the compressed IDAT payload

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out _);

        Assert.False(ok);
    }

    [Fact]
    public void PaletteColorTypeWithoutPlteChunk_ReturnsFalse()
    {
        byte[] row = [0, 0];
        byte[] file = PngTestFileBuilder.Build(1, 1, 8, 3, palette: null, trns: null, scanlines: [row]);

        bool ok = PngPassthrough.TryRead(new MemoryStream(file), out _);

        Assert.False(ok);
    }

    private static byte[] InflateAll(byte[] idatData)
    {
        using var compressed = new MemoryStream(idatData);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        return raw.ToArray();
    }
}
