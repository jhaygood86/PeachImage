using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Formats.Jxl.RoundTrip;

/// <summary>Encodes with the lossless Modular encoder and decodes with the existing decoder; the pixels must come back bit for bit.</summary>
public class JxlLosslessRoundTripTests
{
    private static readonly JxlEncoderOptions Lossless = new() { Lossless = true };

    public static TheoryData<int, int> Sizes => new()
    {
        { 1, 1 },
        { 5, 3 },
        { 17, 31 },
        { 64, 48 },
        { 256, 256 },
        { 257, 17 },
        { 300, 200 },
        { 520, 530 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgb24_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Rgb24, noisy: false), PixelFormat.Rgb24);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgb24Noise_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Rgb24, noisy: true), PixelFormat.Rgb24);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Gray8_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Gray8, noisy: true), PixelFormat.Gray8);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgba32_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Rgba32, noisy: true), PixelFormat.Rgba32);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgb48_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Rgb48, noisy: true), PixelFormat.Rgb48);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Gray16_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Gray16, noisy: true), PixelFormat.Gray16);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Rgba64_RoundTrips(int width, int height) => AssertRoundTrip(Create(width, height, PixelFormat.Rgba64, noisy: true), PixelFormat.Rgba64);

    [Fact]
    public void OpaqueRgba32_IsStoredWithoutAlpha()
    {
        var source = Create(40, 30, PixelFormat.Rgba32, noisy: false);
        var pixels = source.GetPixelSpan();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        byte[] encoded = Encode(source);
        using var stream = new MemoryStream(encoded);
        Assert.False(Image.Identify(stream).HasAlpha);
        AssertRoundTrip(source, PixelFormat.Rgb24, expectEqualBytes: false);
    }

    [Fact]
    public void LosslessOutput_IsReportedAsLossless()
    {
        byte[] encoded = Encode(Create(70, 20, PixelFormat.Rgb24, noisy: false));
        using var stream = new MemoryStream(encoded);
        Assert.True(Image.Identify(stream).IsLosslessEncoding);
    }

    [Fact]
    public void Metadata_RoundTripsThroughContainer()
    {
        var source = Create(33, 21, PixelFormat.Rgb24, noisy: false);
        byte[] exif = [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0];
        byte[] xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = xmp });

        using var stream = new MemoryStream(Encode(source));
        var decoded = Image.Load(stream);

        Assert.Contains(decoded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Exif && p.Data.SequenceEqual(exif));
        Assert.Contains(decoded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Xmp && p.Data.SequenceEqual(xmp));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(4000)]
    public void IccPrediction_RoundTripsThroughTheDecoder(int size)
    {
        byte[] icc = new byte[size];
        new Random(size).NextBytes(icc);
        if (size >= 40)
        {
            "acsp"u8.CopyTo(icc.AsSpan(36));
        }

        byte[] encoded = PeachImage.Formats.Jxl.Encoder.Headers.JxlIccEncoder.Predict(icc);
        Assert.Equal(icc, PeachImage.Formats.Jxl.Headers.JxlIccDecoder.Unpredict(encoded));
    }

    [Fact]
    public void IccProfile_IsEmbeddedAndRestored()
    {
        var source = Create(40, 30, PixelFormat.Rgb24, noisy: false);
        byte[] icc = new byte[900];
        new Random(5).NextBytes(icc);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });

        using var stream = new MemoryStream(Encode(source));
        var decoded = Image.Load(stream);

        Assert.Contains(decoded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc && p.Data.SequenceEqual(icc));
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void IncludeMetadataFalse_WritesBareCodestream()
    {
        var source = Create(16, 16, PixelFormat.Rgb24, noisy: false);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = "<x/>"u8.ToArray() });

        using var output = new MemoryStream();
        source.Save(output, "jxl", new JxlEncoderOptions { Lossless = true, IncludeMetadata = false });

        byte[] bytes = output.ToArray();
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0x0A, bytes[1]);
    }

    [Fact]
    public void Exif_Orientation_IsWrittenToTheHeader()
    {
        var source = Create(16, 16, PixelFormat.Rgb24, noisy: false);

        // Big-endian TIFF with one IFD entry: Orientation (0x0112) = 6.
        byte[] exif = [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });

        using var stream = new MemoryStream(Encode(source));
        Assert.Equal(ImageOrientation.Rotate90, Image.Identify(stream).Orientation);
    }

    [Fact]
    public void UnsupportedPixelFormat_Throws() =>
        Assert.Throws<JxlEncodingException>(() => Encode(Image.Create(4, 4, PixelFormat.Cmyk32)));

    private static byte[] Encode(Image image)
    {
        using var output = new MemoryStream();
        image.Save(output, "jxl", Lossless);
        return output.ToArray();
    }

    private static void AssertRoundTrip(Image source, PixelFormat expectedFormat, bool expectEqualBytes = true)
    {
        using var stream = new MemoryStream(Encode(source));
        var decoded = Image.Load(stream);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.Equal(expectedFormat, decoded.PixelFormat);
        if (expectEqualBytes)
        {
            Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()), "Decoded pixels differ from the source.");
        }
        else
        {
            // Opaque RGBA source: the colour channels must match the decoded RGB pixels.
            var s = source.GetPixelSpan();
            var d = decoded.GetPixelSpan();
            for (int i = 0; i < source.Width * source.Height; i++)
            {
                Assert.True(s[i * 4] == d[i * 3] && s[(i * 4) + 1] == d[(i * 3) + 1] && s[(i * 4) + 2] == d[(i * 3) + 2]);
            }
        }
    }

    private static Image Create(int width, int height, PixelFormat format, bool noisy)
    {
        var image = Image.Create(width, height, format);
        var random = new Random((width * 31) + height);
        var pixels = image.GetPixelSpan();
        bool sixteen = format is PixelFormat.Gray16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        if (sixteen)
        {
            var samples = MemoryMarshal.Cast<byte, ushort>(pixels);
            int channels = format == PixelFormat.Gray16 ? 1 : format == PixelFormat.Rgb48 ? 3 : 4;
            for (int i = 0; i < samples.Length; i++)
            {
                int pixel = i / channels;
                int x = pixel % width;
                int y = pixel / width;
                samples[i] = noisy ? (ushort)random.Next(65536) : (ushort)(((x * 255) + (y * 97) + (i % channels * 4000)) & 0xFFFF);
            }

            return image;
        }

        int ch = format == PixelFormat.Gray8 ? 1 : format == PixelFormat.Rgb24 ? 3 : 4;
        for (int i = 0; i < pixels.Length; i++)
        {
            int pixel = i / ch;
            int x = pixel % width;
            int y = pixel / width;
            pixels[i] = noisy ? (byte)((((x + y) * 2) + random.Next(24)) & 0xFF) : (byte)((x * 3) + (y * 5) + (i % ch * 40));
        }

        return image;
    }
}
