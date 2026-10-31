using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Tests.Metadata;

public class LocationMetadataTests
{
    public static TheoryData<string> LocationFileNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in LocationFixtures.AllLocationFiles())
        {
            data.Add(name);
        }

        return data;
    }

    private static byte[] File(string name) => LocationFixtures.AllLocationFiles().First(f => f.Name == name).Bytes;

    [Theory]
    [MemberData(nameof(LocationFileNames))]
    public void Identify_ReportsLocation(string name)
    {
        var info = Image.Identify(new MemoryStream(File(name)));

        Assert.True(info.HasLocation);
    }

    [Theory]
    [MemberData(nameof(LocationFileNames))]
    public void Load_ReportsLocation(string name)
    {
        using var image = Image.Load(File(name));

        Assert.True(image.Metadata.HasLocation);
    }

    [Theory]
    [InlineData("jpeg-exif-LE")]
    [InlineData("jpeg-exif-BE")]
    [InlineData("png-exif-LE")]
    [InlineData("png-exif-BE")]
    [InlineData("webp-exif-LE")]
    [InlineData("webp-exif-BE")]
    public void ExifGps_IsReadInBothByteOrders(string name)
    {
        var info = Image.Identify(new MemoryStream(File(name)));
        using var image = Image.Load(File(name));

        foreach (var location in new[] { info.ExifLocation, image.Metadata.ExifLocation })
        {
            Assert.NotNull(location);
            Assert.Equal(LocationFixtures.Latitude, location.Value.Latitude!.Value, 6);
            Assert.Equal('N', location.Value.LatitudeRef);
            Assert.Equal(LocationFixtures.Latitude, location.Value.Longitude!.Value, 6);
            Assert.Equal('W', location.Value.LongitudeRef);
            Assert.Equal(LocationFixtures.Latitude, location.Value.DestLatitude!.Value, 6);
            Assert.Equal(LocationFixtures.Latitude, location.Value.DestLongitude!.Value, 6);
        }
    }

    public static TheoryData<string> CleanFileNames() =>
        ["jpeg", "png", "webp", "jpeg-no-gps-ifd", "jpeg-xmp-no-gps", "png-xmp-no-gps", "webp-xmp-no-gps", "jpeg-iptc-empty", "jpeg-iptc-no-place", "jpeg-gps-ifd-no-position-tags"];

    private static byte[] CleanFile(string name) => name switch
    {
        "jpeg" => LocationFixtures.BaseJpeg(),
        "png" => LocationFixtures.BasePng(),
        "webp" => LocationFixtures.Webp(),
        "jpeg-no-gps-ifd" => LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, LocationFixtures.Exif(true, orientation: 6, gpsIfd: false))),
        "jpeg-xmp-no-gps" => LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, LocationFixtures.Concat(LocationFixtures.XmpHeader, LocationFixtures.XmpPacket(false)))),
        "png-xmp-no-gps" => LocationFixtures.PngWith(LocationFixtures.BasePng(), ("iTXt", LocationFixtures.PngXmpChunk(LocationFixtures.XmpPacket(false)))),
        "webp-xmp-no-gps" => LocationFixtures.Webp(xmp: LocationFixtures.XmpPacket(false)),
        "jpeg-iptc-empty" => LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xED, LocationFixtures.Photoshop(LocationFixtures.Iim((90, ""), (92, "  "))))),
        "jpeg-iptc-no-place" => LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xED, LocationFixtures.Photoshop(LocationFixtures.Iim((80, "Photographer"))))),
        "jpeg-gps-ifd-no-position-tags" => LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, LocationFixtures.Exif(false, orientation: 1))),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(CleanFileNames))]
    public void NoLocation_IsReportedAsNone(string name)
    {
        var bytes = CleanFile(name);

        var info = Image.Identify(new MemoryStream(bytes));
        using var image = Image.Load(bytes);

        Assert.False(info.HasLocation);
        Assert.Null(info.ExifLocation);
        Assert.False(image.Metadata.HasLocation);
        Assert.Null(image.Metadata.ExifLocation);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(92)]
    [InlineData(95)]
    public void Iptc_CityLocationAndProvince_AreEachDetected(int dataSet)
    {
        var bytes = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xED, LocationFixtures.Photoshop(LocationFixtures.Iim((dataSet, "somewhere")))));

        Assert.True(Image.Identify(new MemoryStream(bytes)).HasLocation);
    }

    [Fact]
    public void ExifGps_WithOnlyLatitude_StillCountsAsLocation()
    {
        var bytes = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, LocationFixtures.Exif(true, latitude: true)));

        var info = Image.Identify(new MemoryStream(bytes));

        Assert.True(info.HasLocation);
        Assert.Null(info.ExifLocation!.Value.Longitude);
    }

    [Fact]
    public void Identify_OnANonSeekableStream_GivesTheSameAnswerAndStillDecodes()
    {
        var bytes = File("jpeg-exif-LE");

        var info = Image.Identify(new NonSeekableStream(bytes));
        using var image = Image.Load(new NonSeekableStream(bytes));

        Assert.True(info.HasLocation);
        Assert.True(image.Metadata.HasLocation);
        Assert.Equal(8, image.Width);
    }

    [Fact]
    public void Location_SurvivesCloneButNotAMetadataFreeResize()
    {
        using var image = Image.Load(File("png-exif-LE"));
        using var clone = image.Clone();
        using var resized = image.Resize(4, 4);
        using var resizedWithMetadata = image.Resize(4, 4, new ResizeOptions { CopyMetadata = true });

        Assert.True(clone.Metadata.HasLocation);
        Assert.False(resized.Metadata.HasLocation);
        Assert.True(resizedWithMetadata.Metadata.HasLocation);
    }

    [Fact]
    public void JpegIptc_IsExposedAsAnIptcProfile_AndOtherApp13PayloadsAreNot()
    {
        var photoshop = LocationFixtures.Photoshop(LocationFixtures.Iim((90, "San Francisco")));
        var withIptc = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xED, photoshop));
        var foreign = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xED, "not photoshop"u8.ToArray()));

        using var image = Image.Load(withIptc);
        using var other = Image.Load(foreign);
        using var skipped = Image.Load(withIptc, new DecoderOptions { SkipMetadata = true });

        Assert.Equal(photoshop, Assert.Single(image.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Iptc).Data);
        Assert.DoesNotContain(other.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Iptc);
        Assert.Empty(skipped.Metadata.Profiles);
    }

    [Fact]
    public void PngExif_IsExposedAsAnExifProfile_AndCarriedIntoJxlOutput()
    {
        var exif = LocationFixtures.Exif(true, latitude: true, orientation: 3, prefix: false);
        var png = LocationFixtures.PngWith(LocationFixtures.BasePng(), ("eXIf", exif));

        using var image = Image.Load(png);
        using var skipped = Image.Load(png, new DecoderOptions { SkipMetadata = true });
        using var jxl = new MemoryStream();
        image.Save(jxl, "jxl");
        using var reloaded = Image.Load(jxl.ToArray());

        Assert.Equal(exif, Assert.Single(image.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Exif).Data);
        Assert.Empty(skipped.Metadata.Profiles);
        Assert.Contains(reloaded.Metadata.Profiles, p => p.Kind == MetadataProfileKind.Exif);
        Assert.True(reloaded.Metadata.HasLocation);
    }

    [Fact]
    public void IptcProfile_AddedByHand_CountsToo()
    {
        using var image = Image.Create(2, 2, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Iptc, Data = LocationFixtures.Photoshop(LocationFixtures.Iim((95, "CA"))) });

        Assert.True(image.Metadata.HasLocation);
    }

    [Fact]
    public void Profiles_AddedByHand_CountToo()
    {
        using var image = Image.Create(2, 2, PixelFormat.Rgb24);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = LocationFixtures.Exif(true, longitude: true) });

        Assert.True(image.Metadata.HasLocation);
        Assert.NotNull(image.Metadata.ExifLocation!.Value.Longitude);
    }

    [Fact]
    public void TiffFile_ExifXmpAndIptcTags_AreFound()
    {
        // A TIFF file is its own EXIF container: IFD0 carries the GPS pointer, and tag 700 / 33723 hold XMP / IPTC.
        var gps = ExifLocationReader.Read(LocationFixtures.Exif(false, latitude: true, prefix: false), isTiffFile: true);
        Assert.True(gps.ExifGps);

        var xmp = LocationFixtures.XmpPacket(true);
        Assert.True(ExifLocationReader.Read(TiffWithByteTag(700, xmp), isTiffFile: true).Xmp);
        Assert.False(ExifLocationReader.Read(TiffWithByteTag(700, LocationFixtures.XmpPacket(false)), isTiffFile: true).Xmp);

        Assert.True(ExifLocationReader.Read(TiffWithByteTag(33723, LocationFixtures.Iim((92, "Mission District"))), isTiffFile: true).Iptc);
        Assert.True(ExifLocationReader.Read(TiffWithByteTag(34377, LocationFixtures.Photoshop(LocationFixtures.Iim((95, "CA")))), isTiffFile: true).Iptc);
    }

    // Little-endian TIFF whose IFD0 has a single UNDEFINED entry holding `payload` right after the IFD.
    private static byte[] TiffWithByteTag(ushort tag, byte[] payload)
    {
        using var ms = new MemoryStream();
        ms.Write("II*\0"u8);
        ms.Write(BitConverter.GetBytes(8u));
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.Write(BitConverter.GetBytes(tag));
        ms.Write(BitConverter.GetBytes((ushort)7));
        ms.Write(BitConverter.GetBytes((uint)payload.Length));
        ms.Write(BitConverter.GetBytes(26u));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(payload);
        return ms.ToArray();
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
