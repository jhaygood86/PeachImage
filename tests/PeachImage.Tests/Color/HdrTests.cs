using System.Runtime.InteropServices;
using PeachImage.Formats.Png;
using PeachImage.Internal.Color;

namespace PeachImage.Tests.Color;

public class HdrTests
{
    private static Image Signal16(NamedColorSpace space, params double[] rgb)
    {
        var image = Image.Create(1, 1, PixelFormat.Rgb48);
        var s = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        for (int i = 0; i < 3; i++)
        {
            s[i] = (ushort)Math.Round(rgb[i] * 65535);
        }

        image.Metadata.Cicp = CicpCode.Of(space);
        return image;
    }

    private static float[] Floats(Image image) => MemoryMarshal.Cast<byte, float>(image.GetPixelSpan()).ToArray();

    private static readonly ColorConversionOptions KeepHeadroom = new() { GamutHandling = GamutHandling.Preserve, TargetPixelFormat = PixelFormat.RgbF32 };

    [Fact]
    public void Pq_ReferenceWhiteAndPeak_DecodeToHeadroomValues()
    {
        // 203 cd/m2 encodes as PQ 0.5807 and is linear 1.0; the 10000 cd/m2 peak is 10000/203.
        double referenceSignal = Hdr.LinearToPq(1.0);
        Assert.Equal(0.5807, referenceSignal, 3);

        using var white = Signal16(NamedColorSpace.Rec2100Pq, referenceSignal, referenceSignal, referenceSignal);
        using var decoded = white.ConvertColorSpace(NamedColorSpace.LinearRec2020, KeepHeadroom);
        Assert.Equal(1.0, Floats(decoded)[0], 3);

        using var peak = Signal16(NamedColorSpace.Rec2100Pq, 1, 1, 1);
        using var peakDecoded = peak.ConvertColorSpace(NamedColorSpace.LinearRec2020, KeepHeadroom);
        Assert.Equal(10000.0 / 203.0, Floats(peakDecoded)[0], 1);
        Assert.Equal(NamedColorSpace.LinearRec2020, peakDecoded.Metadata.GetNamedColorSpace());
    }

    [Fact]
    public void Hlg_AppliesTheSystemGamma_SoSignal075IsAboutReferenceWhite()
    {
        using var hlg = Signal16(NamedColorSpace.Rec2100Hlg, 0.75, 0.75, 0.75);
        using var decoded = hlg.ConvertColorSpace(NamedColorSpace.LinearRec2020, KeepHeadroom);
        Assert.Equal(1.0, Floats(decoded)[1], 2);

        using var full = Signal16(NamedColorSpace.Rec2100Hlg, 1, 1, 1);
        using var fullDecoded = full.ConvertColorSpace(NamedColorSpace.LinearRec2020, KeepHeadroom);
        Assert.Equal(1000.0 / 203.0, Floats(fullDecoded)[0], 2);
    }

    [Theory]
    [InlineData(NamedColorSpace.Rec2100Pq)]
    [InlineData(NamedColorSpace.Rec2100Hlg)]
    public void HdrRoundTrip_ThroughLinearFloat_RestoresTheSignal(NamedColorSpace hdr)
    {
        using var source = Signal16(hdr, 0.31, 0.52, 0.74);
        using var linear = source.ConvertColorSpace(NamedColorSpace.LinearRec2020, KeepHeadroom);
        using var back = linear.ConvertColorSpace(hdr, new ColorConversionOptions { TargetPixelFormat = PixelFormat.Rgb48 });
        var s = MemoryMarshal.Cast<byte, ushort>(back.GetPixelSpan());
        Assert.InRange(s[0], 0.31 * 65535 - 6, 0.31 * 65535 + 6);
        Assert.InRange(s[1], 0.52 * 65535 - 6, 0.52 * 65535 + 6);
        Assert.InRange(s[2], 0.74 * 65535 - 6, 0.74 * 65535 + 6);
    }

    [Fact]
    public void ToSdr_CompressesHighlightsIntoRangeAndKeepsHue()
    {
        using var pq1000 = Signal16(NamedColorSpace.Rec2100Pq, Hdr.LinearToPq(1000.0 / 203.0), Hdr.LinearToPq(500.0 / 203.0), Hdr.LinearToPq(250.0 / 203.0));
        var options = new ColorConversionOptions { HdrToneMapping = HdrToneMapping.ToSdr, HdrPeakNits = 1000, GamutHandling = GamutHandling.Clip, TargetPixelFormat = PixelFormat.RgbF32 };
        using var sdr = pq1000.ConvertColorSpace(NamedColorSpace.LinearRec2020, options);
        var f = Floats(sdr);
        Assert.All(f, v => Assert.InRange(v, 0f, 1.0001f));
        Assert.True(f[0] > f[1] && f[1] > f[2]);

        // The luminance of the 1000 cd/m2 peak lands on white after mapping the luminance, not each channel.
        using var white = Signal16(NamedColorSpace.Rec2100Pq, Hdr.LinearToPq(1000.0 / 203.0), Hdr.LinearToPq(1000.0 / 203.0), Hdr.LinearToPq(1000.0 / 203.0));
        using var whiteSdr = white.ConvertColorSpace(NamedColorSpace.LinearRec2020, options);
        Assert.Equal(1.0, Floats(whiteSdr)[0], 3);

        // SDR sources are untouched by the option.
        using var sdrSource = Signal16(NamedColorSpace.Srgb, 0.5, 0.5, 0.5);
        using var plain = sdrSource.ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions { TargetPixelFormat = PixelFormat.RgbF32 });
        using var mapped = sdrSource.ConvertColorSpace(NamedColorSpace.LinearSrgb, new ColorConversionOptions { TargetPixelFormat = PixelFormat.RgbF32, HdrToneMapping = HdrToneMapping.ToSdr });
        Assert.Equal(Floats(plain), Floats(mapped));
    }

    [Fact]
    public void HdrKernels_MatchTheirScalarTwins()
    {
        var random = new Random(3);
        float[] Make(float max) => Enumerable.Range(0, 1003).Select(_ => (float)random.NextDouble() * max).ToArray();
        var (r, g, b) = (Make(1), Make(1), Make(1));
        var (vr, vg, vb) = ((float[])r.Clone(), (float[])g.Clone(), (float[])b.Clone());
        ColorKernels.HlgOotf(vr, vg, vb);
        ColorKernels.HlgOotfScalar(r, g, b);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.Equal(r[i], vr[i], 3);
            Assert.Equal(g[i], vg[i], 3);
        }

        (r, g, b) = (Make(5), Make(5), Make(5));
        (vr, vg, vb) = ((float[])r.Clone(), (float[])g.Clone(), (float[])b.Clone());
        ColorKernels.HlgInverseOotf(vr, vg, vb);
        ColorKernels.HlgInverseOotfScalar(r, g, b);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.Equal(r[i], vr[i], 2);
        }

        (r, g, b) = (Make(6), Make(6), Make(6));
        (vr, vg, vb) = ((float[])r.Clone(), (float[])g.Clone(), (float[])b.Clone());
        ColorKernels.ToneMapReinhard(vr, vg, vb, 4.9f);
        ColorKernels.ToneMapReinhardScalar(r, g, b, 4.9f);
        for (int i = 0; i < r.Length; i++)
        {
            Assert.Equal(r[i], vr[i], 4);
            Assert.Equal(b[i], vb[i], 4);
        }
    }

    [Fact]
    public void ExtendedLinearFloat_EncodedAsPq_KeepsValuesAboveOne()
    {
        using var source = Image.Create(2, 1, PixelFormat.RgbF32);
        float[] values = [4f, 2f, 1f, 0.5f, 0.25f, 0.1f];
        values.CopyTo(MemoryMarshal.Cast<byte, float>(source.GetPixelSpan()));
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearRec2020);

        using var ms = new MemoryStream();
        source.Save(ms, "png", new PngEncoderOptions { TargetColorSpace = NamedColorSpace.Rec2100Pq });
        var bytes = ms.ToArray();

        var info = Image.Identify(new MemoryStream(bytes));
        Assert.True(info.IsHdr);
        Assert.Equal(CicpCode.Of(NamedColorSpace.Rec2100Pq), info.Cicp);
        Assert.Equal(16, info.BitsPerSample);

        using var back = Image.Load(bytes);
        Assert.Equal(PixelFormat.Rgb48, back.PixelFormat);
        using var linear = back.ConvertColorSpace(NamedColorSpace.LinearRec2020, KeepHeadroom);
        var f = Floats(linear);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], f[i], values[i] > 1 ? 1 : 2);
        }

        Assert.Contains(f, v => v > 1.5f);
    }

    [Fact]
    public void LoadBitmap_ToneMapsHdrByDefault_AndKeepsHeadroomForFloatOutput()
    {
        using var pq = Signal16(NamedColorSpace.Rec2100Pq, Hdr.LinearToPq(3.0), Hdr.LinearToPq(3.0), Hdr.LinearToPq(3.0));
        using var ms = new MemoryStream();
        pq.Save(ms, "png");
        var bytes = ms.ToArray();

        using var sdr = Image.LoadBitmap(bytes);
        Assert.Equal(NamedColorSpace.Srgb, sdr.Metadata.GetNamedColorSpace());
        Assert.Equal(PixelFormat.Rgb48, sdr.PixelFormat);
        Assert.All(MemoryMarshal.Cast<byte, ushort>(sdr.GetPixelSpan()).ToArray(), v => Assert.True(v < 65535));

        using var hdr = Image.LoadBitmap(bytes, new BitmapRequest { TargetColorSpace = NamedColorSpace.LinearRec2020, HdrToneMapping = HdrToneMapping.Preserve });
        Assert.Equal(PixelFormat.RgbF32, hdr.PixelFormat);
        Assert.Equal(3.0, Floats(hdr)[0], 1);
    }
}
