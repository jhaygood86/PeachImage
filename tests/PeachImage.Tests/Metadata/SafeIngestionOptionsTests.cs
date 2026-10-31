using System.Buffers.Binary;
using PeachImage.Formats.Gif;
using PeachImage.Formats.Png;
using PeachImage.Tests.Formats.Png;

namespace PeachImage.Tests.Metadata;

/// <summary>Allow-list, SkipMetadata, first-frame-only loading and the decode limits.</summary>
public class SafeIngestionOptionsTests
{
    private static DecoderOptions Web => new() { AllowedFormats = new HashSet<string> { "jpeg", "png", "gif", "webp" } };

    // Not parseable as a TIFF: if the TIFF decoder ran on it, it would throw its own TiffDecodingException.
    private static readonly byte[] TiffSignatureOnly = [0x49, 0x49, 0x2A, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void AllowedFormats_Default_AllowsEverything()
    {
        using var image = Image.Load(LocationFixtures.BasePng());

        Assert.Equal(8, image.Width);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void AllowedFormats_LetsListedFormatsThrough(string format)
    {
        byte[] bytes = format switch
        {
            "jpeg" => LocationFixtures.BaseJpeg(),
            "png" => LocationFixtures.BasePng(),
            _ => LocationFixtures.Webp(),
        };

        using var image = Image.Load(bytes, Web);
        var info = Image.Identify(new MemoryStream(bytes), Web);

        Assert.Equal(format, info.FormatName);
    }

    [Fact]
    public void AllowedFormats_RefusesUnlistedFormats_BeforeTheirDecoderRuns()
    {
        var onlyJpeg = new DecoderOptions { AllowedFormats = new HashSet<string> { "jpeg" } };

        var load = Assert.Throws<UnsupportedFormatException>(() => Image.Load(LocationFixtures.BasePng(), onlyJpeg));
        var identify = Assert.Throws<UnsupportedFormatException>(() => Image.Identify(new MemoryStream(LocationFixtures.BasePng()), onlyJpeg));
        // A TIFF-signed blob would make the TIFF decoder throw TiffDecodingException; the allow-list must win.
        var tiff = Assert.Throws<UnsupportedFormatException>(() => Image.Load(TiffSignatureOnly, Web));

        Assert.Equal("png", load.FormatName);
        Assert.Equal("png", identify.FormatName);
        Assert.Equal("tiff", tiff.FormatName);
    }

    [Fact]
    public void AllowedFormats_IsCaseInsensitive_AndAnEmptySetAllowsNothing()
    {
        var upper = new DecoderOptions { AllowedFormats = new HashSet<string> { "JPEG" } };
        var none = new DecoderOptions { AllowedFormats = new HashSet<string>() };

        using var image = Image.Load(LocationFixtures.BaseJpeg(), upper);

        Assert.Throws<UnsupportedFormatException>(() => Image.Load(LocationFixtures.BaseJpeg(), none));
        Assert.Equal(8, image.Width);
    }

    [Fact]
    public void AllowedFormats_UnrecognisedData_StillThrowsUnknownFormat_AndTryLoadReturnsFalse()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        Assert.Throws<UnknownImageFormatException>(() => Image.Load(garbage, Web));
        Assert.False(Image.TryLoad(new MemoryStream(TiffSignatureOnly), out var image, Web));
        Assert.Null(image);
    }

    [Fact]
    public void SkipMetadata_ReturnsNoProfilesAndNoLocation_ForJpegPngAndWebp()
    {
        var icc = new byte[300];
        new Random(1).NextBytes(icc);
        var exif = LocationFixtures.Exif(true, latitude: true, longitude: true);
        byte[][] files =
        [
            LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, exif), (0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. icc])),
            LocationFixtures.PngWith(LocationFixtures.BasePng(), ("eXIf", exif[6..]), ("tEXt", "Comment\0hello"u8.ToArray())),
            LocationFixtures.Webp(exif: exif, icc: icc),
        ];

        foreach (var file in files)
        {
            using var normal = Image.Load(file);
            using var skipped = Image.Load(file, new DecoderOptions { SkipMetadata = true });
            var info = Image.Identify(new MemoryStream(file), new DecoderOptions { SkipMetadata = true });

            Assert.NotEmpty(normal.Metadata.Profiles);
            Assert.True(normal.Metadata.HasLocation);
            Assert.Empty(skipped.Metadata.Profiles);
            Assert.False(skipped.Metadata.HasLocation);
            Assert.False(info.HasLocation);
            Assert.True(normal.GetPixelSpan().SequenceEqual(skipped.GetPixelSpan()));
        }
    }

    [Fact]
    public void IccProfile_IsReturnedAsRawBytes_WithoutParsingItsTags()
    {
        // A header claiming a 4 GB profile with a tag table of 4 billion entries: parsing it would be hostile.
        var crafted = new byte[200];
        BinaryPrimitives.WriteUInt32BigEndian(crafted, 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(crafted.AsSpan(128), 0xFFFFFFFF);
        var file = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. crafted]));

        using var image = Image.Load(file);

        var profile = Assert.Single(image.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc);
        Assert.Equal(crafted, profile.Data);
        Assert.Null(image.Metadata.GetIccColorProfile());
    }

    [Fact]
    public void AnimatedGif_Load_ReturnsOnlyTheFirstFrame_EvenWhenTheRestIsDamaged()
    {
        using var first = Image.Create(20, 20, PixelFormat.Rgba32);
        first.GetPixelSpan().Fill(200);
        using var second = Image.Create(20, 20, PixelFormat.Rgba32);
        second.GetPixelSpan().Fill(50);
        var animation = new AnimatedImage(
            [new AnimatedImageFrame(first.Clone(), TimeSpan.FromMilliseconds(30), FrameDisposalMethod.None), new AnimatedImageFrame(second.Clone(), TimeSpan.FromMilliseconds(30), FrameDisposalMethod.None)],
            width: 20,
            height: 20,
            loopCount: 0);
        using var ms = new MemoryStream();
        animation.Save(ms, "gif", new GifEncoderOptions());
        var bytes = ms.ToArray();

        using var whole = Image.Load(bytes, Web);
        using var damaged = Image.Load(bytes.AsSpan(0, bytes.Length - 6), Web);

        Assert.True(whole.IsAnimated);
        Assert.Equal(20, whole.Width);
        Assert.True(whole.GetPixelSpan().SequenceEqual(damaged.GetPixelSpan()));
        Assert.Equal(200, whole.GetPixelSpan()[0]);
    }

    [Fact]
    public void MaxPixels_RefusesLargerImages_BeforeDecoding_ForEveryCommonFormat()
    {
        var options = new DecoderOptions { MaxPixels = 100 };
        byte[][] big = [LocationFixtures.BaseJpeg(16), LocationFixtures.BasePng(16), LocationFixtures.Webp()];

        Assert.Throws<ImageTooLargeException>(() => Image.Load(big[0], options));
        Assert.Throws<ImageTooLargeException>(() => Image.Load(big[1], options));
        using var fits = Image.Load(big[2], new DecoderOptions { MaxPixels = 64 });
        Assert.Throws<ImageTooLargeException>(() => Image.Load(big[2], new DecoderOptions { MaxPixels = 63 }));
        Assert.Equal(8, fits.Width);
    }

    [Fact]
    public void MaxExpansionRatio_StopsADecompressionBombPng()
    {
        // 4000x4000 grey, all zero: ~16 MB decoded from a few tens of KB of file.
        var row = new byte[4001];
        var png = PngTestFileBuilder.Build(4000, 4000, 8, 0, null, null, Enumerable.Repeat(row, 4000));

        var ex = Assert.Throws<ImageTooLargeException>(() => Image.Load(png, new DecoderOptions { MaxExpansionRatio = 50 }));
        Assert.Contains("MaxExpansionRatio", ex.Message);
        Assert.True(png.Length < 100_000);
    }

    [Fact]
    public void MaxExpansionRatio_StopsADecompressionBombGif()
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        ms.Write([0x88, 0x13, 0x88, 0x13, 0x00, 0x00, 0x00]); // 5000x5000, no global colour table
        ms.Write([0x2C, 0, 0, 0, 0, 0x88, 0x13, 0x88, 0x13, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B]);

        Assert.Throws<ImageTooLargeException>(() => Image.Load(ms.ToArray(), new DecoderOptions { MaxExpansionRatio = 100 }));
    }

    [Fact]
    public void MaxExpansionRatio_StopsADecompressionBombWebp()
    {
        // A lossless bitstream header declaring 16000x16000 followed by a few bytes of data.
        var header = new byte[5];
        header[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), 15999u | (15999u << 14));
        byte[] payload = [.. header, 0, 0, 0, 0];
        using var ms = new MemoryStream();
        ms.Write("RIFF"u8);
        ms.Write(BitConverter.GetBytes((uint)(4 + 8 + payload.Length + 1)));
        ms.Write("WEBPVP8L"u8);
        ms.Write(BitConverter.GetBytes((uint)payload.Length));
        ms.Write(payload);
        ms.WriteByte(0);

        Assert.Throws<ImageTooLargeException>(() => Image.Load(ms.ToArray(), new DecoderOptions { MaxExpansionRatio = 100 }));
    }

    [Fact]
    public void MaxExpansionRatio_AllowsAnOrdinaryImage()
    {
        var jpeg = LocationFixtures.BaseJpeg(64);

        using var image = Image.Load(jpeg, new DecoderOptions { MaxExpansionRatio = 200, MaxPixels = 64 * 64 });

        Assert.Equal(64, image.Width);
    }
}
