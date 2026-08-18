using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Formats.Webp.RoundTrip;

public class EncodeDecodeRoundTripTests
{
    [Theory]
    [InlineData(64, 48)]
    [InlineData(5, 3)] // Non-power-of-two size exercises predictor/tile boundary math.
    [InlineData(1, 1)]
    [InlineData(17, 31)]
    public void Rgb24Gradient_RoundTrips_Exactly(int width, int height)
    {
        var source = CreateGradientImage(width, height);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Rgba32Image_RoundTrips_Exactly_IncludingAlpha()
    {
        var source = CreateRgbaImage(40, 24, alphaGradient: true);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Rgba32Image_FullyOpaque_RoundTrips_Exactly_ViaAutoDowngrade()
    {
        var source = CreateRgbaImage(20, 16, alphaGradient: false);

        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, new WebpEncoderOptions());

        ms.Position = 0;
        var decoded = WebpDecoder.Decode(ms, new WebpDecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Gray8Image_RoundTrips_Exactly()
    {
        var source = CreateGrayscaleImage(32, 32);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions(), targetPixelFormat: PixelFormat.Gray8);

        Assert.Equal(PixelFormat.Gray8, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void FewColorImage_RoundTrips_Exactly_ViaColorIndexingTransform()
    {
        var source = CreateFewColorImage(48, 40);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void ManyColorImage_RoundTrips_Exactly_ViaPredictorTransform()
    {
        var source = CreateNoisyImage(96, 64, seed: 12345);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Theory]
    [InlineData(WebpCompressionLevel.Fastest)]
    [InlineData(WebpCompressionLevel.Default)]
    [InlineData(WebpCompressionLevel.SmallestSize)]
    public void NoisyImage_RoundTrips_Exactly_AtEveryCompressionLevel(WebpCompressionLevel level)
    {
        var source = CreateNoisyImage(40, 32, seed: 777);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions { CompressionLevel = level });

        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void IccProfile_RoundTrips_ViaExtendedContainer()
    {
        var source = CreateGradientImage(8, 8);
        byte[] iccBytes = [1, 2, 3, 4, 5, 6, 7, 8];
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = iccBytes });

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions());

        var iccProfile = Assert.Single(decoded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(iccBytes, iccProfile.Data);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Gray16Image_Encode_Throws()
    {
        var source = Image.Create(4, 4, PixelFormat.Gray16);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    [Fact]
    public void Rgb48Image_Encode_Throws()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb48);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    [Fact]
    public void Rgba64Image_Encode_Throws()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgba64);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    [Fact]
    public void Cmyk32Image_Encode_Throws()
    {
        var source = Image.Create(4, 4, PixelFormat.Cmyk32);
        using var ms = new MemoryStream();

        Assert.Throws<WebpEncodingException>(() => WebpEncoder.Encode(source, ms));
    }

    // -- Lossy (VP8) encoding --------------------------------------------------------------------------------
    //
    // Unlike the lossless tests above, these assert PSNR thresholds rather than exact pixel equality --
    // placeholders calibrated against this encoder's actual current output, mirroring how the AVIF encoder's
    // own round-trip tests (and originally this WebP encoder's own lossless tolerances) were set from measured
    // worst-case differences, not guessed up front.

    [Theory]
    [InlineData(64, 48)]
    [InlineData(17, 31)] // Non-macroblock-aligned dimensions exercise edge-replication padding.
    public void Rgb24Gradient_LossyRoundTrip_MeetsPsnrThreshold(int width, int height)
    {
        var source = CreateGradientImage(width, height);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions { Lossless = false });

        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        AssertPsnrAtLeast(source, decoded, 25.0);
    }

    [Fact]
    public void NoisyImage_LossyRoundTrip_MeetsPsnrThreshold()
    {
        var source = CreateNoisyImage(64, 48, seed: 999);

        var decoded = EncodeThenDecode(source, new WebpEncoderOptions { Lossless = false });

        // Uncorrelated per-pixel noise is worst-case content for any intra-only lossy codec (no spatial
        // redundancy for prediction to exploit); measured ~12.6 dB at default quality, asserted with a small
        // margin below that, not guessed up front.
        AssertPsnrAtLeast(source, decoded, 11.0);
    }

    [Fact]
    public void Lossless_DefaultOption_ProducesVp8LChunk()
    {
        var source = CreateGradientImage(16, 16);
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, new WebpEncoderOptions());

        Assert.Equal("VP8L", ReadFirstPayloadChunkFourCc(ms.ToArray()));
    }

    [Fact]
    public void Lossless_False_ProducesVp8Chunk()
    {
        var source = CreateGradientImage(16, 16);
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, new WebpEncoderOptions { Lossless = false });

        Assert.Equal("VP8 ", ReadFirstPayloadChunkFourCc(ms.ToArray()));
    }

    /// <summary>VP8 lossy has no alpha plane; requesting <c>Lossless = false</c> on an image that actually needs alpha must silently fall back to VP8L rather than losing alpha (see <see cref="WebpEncoderOptions.Lossless"/>'s remarks).</summary>
    [Fact]
    public void Lossless_False_OnAlphaBearingImage_FallsBackToVp8LAndPreservesAlphaExactly()
    {
        var source = CreateRgbaImage(24, 16, alphaGradient: true);
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, new WebpEncoderOptions { Lossless = false });

        Assert.Equal("VP8L", ReadFirstPayloadChunkFourCc(ms.ToArray()));

        ms.Position = 0;
        var decoded = WebpDecoder.Decode(ms);
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Lossless_False_OnFullyOpaqueRgba32Image_StillEncodesLossy()
    {
        var source = CreateRgbaImage(24, 16, alphaGradient: false);
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, new WebpEncoderOptions { Lossless = false });

        // Uniformly-opaque Rgba32 is downgraded to "no real alpha" (matching the existing lossless
        // auto-downgrade behavior), so this does NOT need the alpha fallback and encodes as true VP8 lossy.
        Assert.Equal("VP8 ", ReadFirstPayloadChunkFourCc(ms.ToArray()));
    }

    [Fact]
    public void Quality_HigherValue_ProducesLargerOrEqualFileSize()
    {
        var source = CreateNoisyImage(48, 32, seed: 2024);

        int lowQualitySize = EncodeAndMeasureSize(source, new WebpEncoderOptions { Lossless = false, Quality = 10 });
        int highQualitySize = EncodeAndMeasureSize(source, new WebpEncoderOptions { Lossless = false, Quality = 90 });

        Assert.True(highQualitySize >= lowQualitySize, $"Expected quality 90 ({highQualitySize} bytes) >= quality 10 ({lowQualitySize} bytes).");
    }

    private static void AssertPsnrAtLeast(Image source, Image decoded, double minPsnrDb)
    {
        ReadOnlySpan<byte> a = source.GetPixelSpan();
        ReadOnlySpan<byte> b = decoded.GetPixelSpan();
        Assert.Equal(a.Length, b.Length);

        double sumSquaredError = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int diff = a[i] - b[i];
            sumSquaredError += diff * diff;
        }

        double psnr = sumSquaredError == 0
            ? double.PositiveInfinity
            : 10 * Math.Log10((255.0 * 255.0) / (sumSquaredError / a.Length));

        Assert.True(psnr >= minPsnrDb, $"Expected PSNR >= {minPsnrDb} dB, got {psnr:F2} dB.");
    }

    private static int EncodeAndMeasureSize(Image source, WebpEncoderOptions options)
    {
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, options);
        return (int)ms.Length;
    }

    /// <summary>Reads the FourCC of the first payload chunk (<c>VP8L</c> or <c>VP8 </c>) in an encoded WebP file, skipping the optional <c>VP8X</c>/<c>ICCP</c> chunks that may precede it.</summary>
    private static string ReadFirstPayloadChunkFourCc(byte[] webpBytes)
    {
        int pos = 12; // Past the 12-byte RIFF/size/"WEBP" header.
        while (pos + 8 <= webpBytes.Length)
        {
            string fourCc = System.Text.Encoding.ASCII.GetString(webpBytes, pos, 4);
            if (fourCc is "VP8L" or "VP8 ")
            {
                return fourCc;
            }

            uint payloadLength = BitConverter.ToUInt32(webpBytes, pos + 4);
            pos += 8 + (int)payloadLength + ((int)payloadLength & 1);
        }

        throw new InvalidOperationException("No VP8L/VP8 payload chunk found.");
    }

    private static Image EncodeThenDecode(Image source, WebpEncoderOptions encoderOptions, PixelFormat? targetPixelFormat = null)
    {
        using var ms = new MemoryStream();
        WebpEncoder.Encode(source, ms, encoderOptions);

        ms.Position = 0;
        var decoderOptions = targetPixelFormat is { } target ? new WebpDecoderOptions { TargetPixelFormat = target } : null;
        return WebpDecoder.Decode(ms, decoderOptions);
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 3) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 3) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 3) + 2] = (byte)((x + y) % 256);
            }
        }

        return image;
    }

    private static Image CreateGrayscaleImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Gray8);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = (byte)((x * 7) + (y * 13));
            }
        }

        return image;
    }

    private static Image CreateRgbaImage(int width, int height, bool alphaGradient)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 4) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 4) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 4) + 2] = (byte)((x + y) % 256);
                row[(x * 4) + 3] = alphaGradient ? (byte)((x * 255) / Math.Max(1, width - 1)) : (byte)255;
            }
        }

        return image;
    }

    private static Image CreateFewColorImage(int width, int height)
    {
        (byte R, byte G, byte B)[] palette =
        [
            (0, 0, 0),
            (255, 255, 255),
            (255, 0, 0),
            (0, 255, 0),
            (0, 0, 255),
        ];

        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                var color = palette[(x + y) % palette.Length];
                row[(x * 3) + 0] = color.R;
                row[(x * 3) + 1] = color.G;
                row[(x * 3) + 2] = color.B;
            }
        }

        return image;
    }

    private static Image CreateNoisyImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            random.NextBytes(row);
        }

        return image;
    }
}
