using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Tests.Metadata;

/// <summary>Every encoder that has somewhere to put an ICC profile or EXIF block writes the ones on the image, and honours the options that turn them off.</summary>
public class EncoderMetadataTests
{
    private static readonly byte[] Icc = CreateIcc();

    private static readonly byte[] Exif = LocationFixtures.Exif(true, latitude: true, orientation: 6, prefix: false);

    private static byte[] CreateIcc()
    {
        var bytes = new byte[400];
        new Random(11).NextBytes(bytes);
        return bytes;
    }

    private static Image Source(PixelFormat format = PixelFormat.Rgb24, bool exifPrefixed = false)
    {
        var image = Image.Create(16, 16, format);
        image.GetPixelSpan().Fill(100);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = Icc });
        var exif = exifPrefixed ? [.. "Exif\0\0"u8.ToArray(), .. Exif] : Exif;
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        return image;
    }

    private static byte[] Encode(Image image, string format, EncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, options);
        return ms.ToArray();
    }

    private static byte[]? IccOf(Image image) => image.Metadata.Profiles.FirstOrDefault(p => p.Kind == MetadataProfileKind.Icc)?.Data;

    private static byte[]? ExifTiffOf(Image image)
    {
        var data = image.Metadata.Profiles.FirstOrDefault(p => p.Kind == MetadataProfileKind.Exif)?.Data;
        return data is null ? null : data.AsSpan().StartsWith("Exif\0\0"u8) ? data[6..] : data;
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("avif")]
    public void IccAndExif_AreWrittenByDefault(string format)
    {
        using var image = Source();

        var bytes = Encode(image, format);
        using var reloaded = Image.Load(bytes);

        Assert.Equal(Icc, IccOf(reloaded));
        Assert.Equal(Exif, ExifTiffOf(reloaded));
        Assert.True(reloaded.Metadata.HasLocation);
        Assert.Equal(ImageOrientation.Rotate90, Image.Identify(new MemoryStream(bytes)).Orientation);
    }

    [Fact]
    public void LosslessJxl_WritesIccAndExif()
    {
        using var image = Source();

        using var reloaded = Image.Load(Encode(image, "jxl", new PeachImage.Formats.Jxl.JxlEncoderOptions { Lossless = true }));

        Assert.Equal(Icc, IccOf(reloaded));
        Assert.NotNull(ExifTiffOf(reloaded));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("avif")]
    public void ExifWithTheExifPrefix_IsWrittenAsTheSameBlock(string format)
    {
        using var image = Source(exifPrefixed: true);

        using var reloaded = Image.Load(Encode(image, format));

        Assert.Equal(Exif, ExifTiffOf(reloaded));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("avif")]
    public void IncludeMetadataFalse_DropsExifAndKeepsIccOnlyWhenAskedTo(string format)
    {
        using var image = Source();

        using var none = Image.Load(Encode(image, format, OptionsFor(format, includeMetadata: false, icc: null)));
        using var iccOnly = Image.Load(Encode(image, format, OptionsFor(format, includeMetadata: false, icc: true)));
        using var exifOnly = Image.Load(Encode(image, format, OptionsFor(format, includeMetadata: true, icc: false)));

        Assert.Null(IccOf(none));
        Assert.Null(ExifTiffOf(none));
        Assert.Equal(Icc, IccOf(iccOnly));
        Assert.Null(ExifTiffOf(iccOnly));
        Assert.Null(IccOf(exifOnly));
        Assert.Equal(Exif, ExifTiffOf(exifOnly));
    }

    private static EncoderOptions OptionsFor(string format, bool includeMetadata, bool? icc) => format switch
    {
        "jpeg" => new PeachImage.Formats.Jpeg.JpegEncoderOptions { IncludeMetadata = includeMetadata, IncludeIccProfile = icc },
        "png" => new PeachImage.Formats.Png.PngEncoderOptions { IncludeMetadata = includeMetadata, IncludeIccProfile = icc },
        "webp" => new PeachImage.Formats.Webp.WebpEncoderOptions { IncludeMetadata = includeMetadata, IncludeIccProfile = icc },
        _ => new PeachImage.Formats.Avif.AvifEncoderOptions { IncludeMetadata = includeMetadata, IncludeIccProfile = icc },
    };

    private static byte[]? XmpOf(Image image) => image.Metadata.Profiles.FirstOrDefault(p => p.Kind == MetadataProfileKind.Xmp)?.Data;

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("avif")]
    [InlineData("jxl")]
    [InlineData("gif")]
    public void Xmp_IsWrittenAndReadBack_AndDroppedWithIncludeMetadataFalse(string format)
    {
        var packet = LocationFixtures.XmpPacket(true);
        using var image = Image.Create(16, 16, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = packet });

        var written = Encode(image, format);
        using var reloaded = Image.Load(written);
        using var none = Image.Load(Encode(image, format, format switch
        {
            "jpeg" => new PeachImage.Formats.Jpeg.JpegEncoderOptions { IncludeMetadata = false },
            "png" => new PeachImage.Formats.Png.PngEncoderOptions { IncludeMetadata = false },
            "webp" => new PeachImage.Formats.Webp.WebpEncoderOptions { IncludeMetadata = false },
            "avif" => new PeachImage.Formats.Avif.AvifEncoderOptions { IncludeMetadata = false },
            "gif" => new PeachImage.Formats.Gif.GifEncoderOptions { IncludeMetadata = false },
            _ => new PeachImage.Formats.Jxl.JxlEncoderOptions { IncludeMetadata = false },
        }));

        Assert.Equal(packet, XmpOf(reloaded));
        Assert.True(reloaded.Metadata.HasLocation);
        Assert.True(Image.Identify(new MemoryStream(written)).HasLocation);
        Assert.Null(XmpOf(none));
    }

    [Fact]
    public void GifXmp_IsWrittenWithTheMagicTrailer_ReadBack_AndSkippedByOrdinaryReaders()
    {
        var packet = LocationFixtures.XmpPacket(true);
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = packet });

        var gif = Encode(image, "gif");
        using var reloaded = Image.Load(gif);
        using var none = Image.Load(Encode(image, "gif", new PeachImage.Formats.Gif.GifEncoderOptions { IncludeMetadata = false }));

        Assert.Equal(packet, XmpOf(reloaded));
        Assert.Null(XmpOf(none));
        Assert.Equal(8, reloaded.Width);
        Assert.True(Image.Identify(new MemoryStream(gif)).HasLocation);
        // The extension ends 0x01 0xFF ... 0x01 0x00 0x00 so a reader that knows nothing about XMP still finds the next block.
        var trailer = new byte[258];
        trailer[0] = 1;
        for (int i = 1; i <= 256; i++)
        {
            trailer[i] = (byte)(256 - i);
        }

        Assert.True(gif.AsSpan().IndexOf(trailer) > 0);
        // AnimatedImage's decoder walks the same bytes as ordinary blocks.
        Assert.Single(AnimatedImage.Load(new MemoryStream(gif)).Frames);
    }

    [Fact]
    public void GifXmpAfterTheFirstFrame_IsStillFoundByTheLocationScan()
    {
        var plain = Encode(Image.Create(8, 8, PixelFormat.Rgb24), "gif");
        // Insert an XMP extension right before the trailer (0x3B).
        using var ms = new MemoryStream();
        ms.Write(plain, 0, plain.Length - 1);
        ms.Write([0x21, 0xFF, 11]);
        ms.Write("XMP DataXMP"u8);
        ms.Write(LocationFixtures.XmpPacket(true));
        ms.WriteByte(0x01);
        for (int value = 0xFF; value >= 0; value--)
        {
            ms.WriteByte((byte)value);
        }

        ms.WriteByte(0x00);
        ms.WriteByte(0x3B);

        Assert.True(Image.Identify(new MemoryStream(ms.ToArray())).HasLocation);
        using var image = Image.Load(ms.ToArray());
        Assert.True(image.Metadata.HasLocation);
    }

    [Fact]
    public void JpegXmp_IsAnXmpProfile_NotAnExifProfile()
    {
        var packet = LocationFixtures.XmpPacket(false);
        var jpeg = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, LocationFixtures.Concat(LocationFixtures.XmpHeader, packet)));

        using var image = Image.Load(jpeg);

        Assert.Equal(packet, XmpOf(image));
        Assert.DoesNotContain(image.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Exif);
    }

    [Fact]
    public void PngXmp_IsAnXmpProfile_NotATextEntry_AndSurvivesAPngRoundTrip()
    {
        var packet = LocationFixtures.XmpPacket(true);
        var png = LocationFixtures.PngWith(LocationFixtures.BasePng(), ("iTXt", LocationFixtures.PngXmpChunk(packet)));

        using var image = Image.Load(png);
        using var again = Image.Load(Encode(image, "png"));

        Assert.Equal(packet, XmpOf(image));
        Assert.DoesNotContain(image.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Text);
        Assert.Equal(packet, XmpOf(again));
    }

    [Fact]
    public void CompressedPngXmp_IsInflated()
    {
        var packet = LocationFixtures.XmpPacket(true);
        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(packet);
        }

        byte[] chunk = [.. "XML:com.adobe.xmp\0\x01\0\0\0"u8.ToArray(), .. deflated.ToArray()];
        var png = LocationFixtures.PngWith(LocationFixtures.BasePng(), ("iTXt", chunk));

        using var image = Image.Load(png);

        Assert.Equal(packet, XmpOf(image));
    }

    [Fact]
    public void TiffXmp_IsReadFromTag700()
    {
        var packet = LocationFixtures.XmpPacket(true);
        var tiff = new PeachImage.Tests.Formats.Tiff.Unit.TiffFixtureBuilder
        {
            Width = 1,
            Height = 1,
            BitsPerSample = 8,
            SamplesPerPixel = 1,
            Photometric = 1,
            XmpPacket = packet,
            Strips = [[0]],
        }.Build();

        using var image = Image.Load(tiff);

        Assert.Equal(packet, XmpOf(image));
        Assert.True(image.Metadata.HasLocation);
    }

    [Fact]
    public void XmpPacketsFiledAsExifByHand_AreNotWrittenAsExif()
    {
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = [.. LocationFixtures.XmpHeader, .. LocationFixtures.XmpPacket(true)] });

        using var reloaded = Image.Load(Encode(image, "png"));

        Assert.Null(ExifTiffOf(reloaded));
    }

    [Fact]
    public void JpegRoundTrip_KeepsIccExifAndLocation()
    {
        var original = LocationFixtures.JpegWith(
            LocationFixtures.BaseJpeg(),
            (0xE1, LocationFixtures.Exif(false, latitude: true, longitude: true, orientation: 3)),
            (0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, .. Icc]));

        using var loaded = Image.Load(original);
        using var reloaded = Image.Load(Encode(loaded, "jpeg"));

        Assert.Equal(Icc, IccOf(reloaded));
        Assert.Equal(ExifTiffOf(loaded), ExifTiffOf(reloaded));
        Assert.True(reloaded.Metadata.HasLocation);
    }

    [Fact]
    public void ExifTooLargeForOneJpegSegment_IsLeftOutRatherThanCorruptingTheFile()
    {
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        var huge = new byte[70_000];
        Exif.CopyTo(huge, 0);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = huge });

        using var reloaded = Image.Load(Encode(image, "jpeg"));

        Assert.Null(ExifTiffOf(reloaded));
        Assert.Equal(8, reloaded.Width);
    }

    [Fact]
    public void AvifWithAlpha_StillWritesIccAndExif()
    {
        using var image = Source(PixelFormat.Rgba32);

        using var reloaded = Image.Load(Encode(image, "avif"));

        Assert.True(reloaded.HasAlpha);
        Assert.Equal(Icc, IccOf(reloaded));
        Assert.Equal(Exif, ExifTiffOf(reloaded));
    }

    [Fact]
    public void AvifOrientation_IsMirroredIntoIrotAndImir_ForEveryExifOrientation()
    {
        foreach (var orientation in Enum.GetValues<ImageOrientation>())
        {
            using var image = Image.Create(16, 16, PixelFormat.Rgb24);
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = LocationFixtures.Exif(true, orientation: (int)orientation, prefix: false) });

            var info = Image.Identify(new MemoryStream(Encode(image, "avif")));

            Assert.Equal(orientation, info.Orientation);
        }
    }

    [Fact]
    public void ExifEncodableMetadata_ReturnsNullWithoutExifOrWhenMetadataIsOff()
    {
        using var image = Source();
        using var empty = Image.Create(2, 2, PixelFormat.Rgb24);

        Assert.NotNull(EncodableMetadata.Exif(image, new PeachImage.Formats.Png.PngEncoderOptions()));
        Assert.Null(EncodableMetadata.Exif(image, new PeachImage.Formats.Png.PngEncoderOptions { IncludeMetadata = false }));
        Assert.Null(EncodableMetadata.Exif(empty, new PeachImage.Formats.Png.PngEncoderOptions()));
    }
}
