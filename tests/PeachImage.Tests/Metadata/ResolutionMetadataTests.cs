using System.Buffers.Binary;

namespace PeachImage.Tests.Metadata;

/// <summary><see cref="ImageMetadata.HorizontalResolution"/> is pixels per metre whatever unit the file recorded the density in.</summary>
public class ResolutionMetadataTests
{
    private const double PerInch = 100.0 / 2.54;

    [Theory]
    [InlineData(1, 300, 300 * PerInch)] // dots per inch
    [InlineData(2, 118, 11800)] // dots per centimetre
    public void Jpeg_Jfif_ReportsDensityInPixelsPerMetre(int units, int density, double expected)
    {
        byte[] jpeg = WithJfifDensity(LocationFixtures.BaseJpeg(), units, density, density + 10);

        using var image = Image.Load(new MemoryStream(jpeg));

        Assert.Equal(expected, image.Metadata.HorizontalResolution!.Value, 3);
        Assert.Equal(expected + (10 * (units == 1 ? PerInch : 100)), image.Metadata.VerticalResolution!.Value, 3);
        Assert.Equal(units == 1 ? density : density * 2.54, image.Metadata.HorizontalDpi!.Value, 3);
    }

    [Fact]
    public void Jpeg_JfifWithOnlyAnAspectRatio_HasNoResolution()
    {
        using var image = Image.Load(new MemoryStream(WithJfifDensity(LocationFixtures.BaseJpeg(), 0, 1, 1)));

        Assert.Null(image.Metadata.HorizontalResolution);
        Assert.Null(image.Metadata.VerticalResolution);
        Assert.Null(image.Metadata.HorizontalDpi);
    }

    [Theory]
    [InlineData(true, 2, 144, 144 * PerInch)]
    [InlineData(false, 3, 50, 5000)]
    [InlineData(true, 0, 96, 96 * PerInch)] // no ResolutionUnit tag: the default is inch
    public void Jpeg_Exif_UsedWhenJfifHasNoUnit(bool littleEndian, int unit, uint density, double expected)
    {
        byte[] exif = ExifWithDensity(littleEndian, unit, density);
        byte[] jpeg = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, exif));

        using var image = Image.Load(new MemoryStream(jpeg));

        Assert.Equal(expected, image.Metadata.HorizontalResolution!.Value, 3);
        Assert.Equal(expected, image.Metadata.VerticalResolution!.Value, 3);
    }

    [Fact]
    public void Jpeg_ExifWithNoAbsoluteUnit_HasNoResolution()
    {
        byte[] jpeg = LocationFixtures.JpegWith(LocationFixtures.BaseJpeg(), (0xE1, ExifWithDensity(true, 1, 72)));

        using var image = Image.Load(new MemoryStream(jpeg));

        Assert.Null(image.Metadata.HorizontalResolution);
    }

    [Fact]
    public void Jpeg_JfifWinsOverExif()
    {
        byte[] jpeg = WithJfifDensity(LocationFixtures.BaseJpeg(), 1, 200, 200);
        jpeg = LocationFixtures.JpegWith(jpeg, (0xE1, ExifWithDensity(true, 2, 72)));

        using var image = Image.Load(new MemoryStream(jpeg));

        Assert.Equal(200 * PerInch, image.Metadata.HorizontalResolution!.Value, 3);
    }

    [Theory]
    [InlineData(true, null, 72, 72 * PerInch)] // TIFF default unit is inch
    [InlineData(false, 2, 300, 300 * PerInch)]
    [InlineData(true, 3, 100, 10000)]
    public void Tiff_ReportsXAndYResolutionInPixelsPerMetre(bool littleEndian, int? unit, uint density, double expected)
    {
        byte[] file = Tiff(littleEndian, unit, (density, density + 1));

        using var image = Image.Load(new MemoryStream(file));

        Assert.Equal(expected, image.Metadata.HorizontalResolution!.Value, 3);
        Assert.Equal(expected + (unit == 3 ? 100 : PerInch), image.Metadata.VerticalResolution!.Value, 3);
    }

    [Fact]
    public void Tiff_WithUnitNone_OrNoDensity_HasNoResolution()
    {
        using var none = Image.Load(new MemoryStream(Tiff(true, 1, (72u, 72u))));
        using var absent = Image.Load(new MemoryStream(Tiff(true, null, null)));

        Assert.Null(none.Metadata.HorizontalResolution);
        Assert.Null(absent.Metadata.HorizontalResolution);
    }

    [Fact]
    public void Tiff_WithZeroDensity_HasNoResolution()
    {
        using var image = Image.Load(new MemoryStream(Tiff(true, 2, (0u, 0u))));

        Assert.Null(image.Metadata.HorizontalResolution);
    }

    [Fact]
    public void Png_KeepsPixelsPerMetre()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        source.Metadata.HorizontalResolution = 2835;
        source.Metadata.VerticalResolution = 2835;
        using var ms = new MemoryStream();
        source.Save(ms, "png");
        ms.Position = 0;

        using var decoded = Image.Load(ms);

        Assert.Equal(2835, decoded.Metadata.HorizontalResolution);
        Assert.Equal(2835 / PerInch, decoded.Metadata.HorizontalDpi!.Value, 3);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("avif")]
    [InlineData("jxl")]
    public void ExifDensity_SurvivesFormatsWithoutADensityField(string format)
    {
        using var source = Image.Create(16, 16, PixelFormat.Rgb24);
        source.GetPixelSpan().Fill(100);
        source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = ExifWithDensity(true, 2, 150)[6..] });
        using var ms = new MemoryStream();
        source.Save(ms, format);
        ms.Position = 0;

        using var decoded = Image.Load(ms);

        Assert.Equal(150 * PerInch, decoded.Metadata.HorizontalResolution!.Value, 3);
    }

    [Fact]
    public void Bmp_ReportsPelsPerMetre()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        using var ms = new MemoryStream();
        source.Save(ms, "bmp");
        byte[] bmp = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(38), 3780);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(42), 2835);

        using var decoded = Image.Load(new MemoryStream(bmp));

        Assert.Equal(3780, decoded.Metadata.HorizontalResolution);
        Assert.Equal(2835, decoded.Metadata.VerticalResolution);
    }

    [Fact]
    public void Bmp_WithZeroPelsPerMetre_HasNoResolution()
    {
        using var source = Image.Create(4, 4, PixelFormat.Rgb24);
        using var ms = new MemoryStream();
        source.Save(ms, "bmp");
        ms.Position = 0;

        using var decoded = Image.Load(ms);

        Assert.Null(decoded.Metadata.HorizontalResolution);
    }

    [Theory]
    [InlineData(0x72657363, 0x72657364, 3780)] // capture box wins
    [InlineData(0x72657364, 0, 7)]
    public void Jpeg2000_ReadsCaptureThenDisplayResolution(int firstBox, int secondBox, int expectedHorizontal)
    {
        byte[] jp2 = ReadAsset("box_gray.jp2");
        var resBoxes = new List<byte>();
        // vertical N/D = 2835 / 1 (E 0); horizontal N/D = expectedHorizontal-or-3780 / 1.
        resBoxes.AddRange(Box(firstBox, Rational(2835, 1, firstBox == 0x72657363 ? 3780 : 7, 1, 0, 0)));
        if (secondBox != 0)
        {
            resBoxes.AddRange(Box(secondBox, Rational(1, 1, 5, 1, 0, 0)));
        }

        byte[] res = Box(0x72657320, [.. resBoxes]);
        byte[] patched = InsertIntoJp2h(jp2, res);

        using var image = Image.Load(new MemoryStream(patched));

        Assert.Equal(expectedHorizontal, image.Metadata.HorizontalResolution);
        Assert.Equal(2835, image.Metadata.VerticalResolution);
    }

    [Fact]
    public void Jpeg2000_ResolutionExponent_Scales()
    {
        byte[] patched = InsertIntoJp2h(ReadAsset("box_gray.jp2"), Box(0x72657320, Box(0x72657363, Rational(1, 1, 5, 1, 3, 2))));

        using var image = Image.Load(new MemoryStream(patched));

        Assert.Equal(500, image.Metadata.HorizontalResolution);
        Assert.Equal(1000, image.Metadata.VerticalResolution);
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(ResolutionMetadataTests).Assembly.GetManifestResourceStream("PeachImage.Tests.Formats.Jpeg2000.Assets." + name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] Rational(int vn, int vd, int hn, int hd, int ve, int he)
    {
        var b = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)vn);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)vd);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)hn);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)hd);
        b[8] = (byte)ve;
        b[9] = (byte)he;
        return b;
    }

    private static byte[] Box(int type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)type);
        body.CopyTo(b, 8);
        return b;
    }

    /// <summary>Appends <paramref name="box"/> to the file's top-level <c>jp2h</c> box and grows its length.</summary>
    private static byte[] InsertIntoJp2h(byte[] jp2, byte[] box)
    {
        int pos = 0;
        while (pos + 8 <= jp2.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(jp2.AsSpan(pos));
            if (BinaryPrimitives.ReadUInt32BigEndian(jp2.AsSpan(pos + 4)) == 0x6A703268)
            {
                int end = pos + (int)length;
                byte[] result = [.. jp2.AsSpan(0, end), .. box, .. jp2.AsSpan(end)];
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), length + (uint)box.Length);
                return result;
            }

            pos += (int)length;
        }

        throw new InvalidOperationException("no jp2h");
    }

    private static byte[] Tiff(bool littleEndian, int? unit, (uint X, uint Y)? density) =>
        new Formats.Tiff.Unit.TiffFixtureBuilder
        {
            Width = 2,
            Height = 2,
            Strips = [new byte[4]],
            LittleEndian = littleEndian,
            Density = density,
            ResolutionUnit = unit,
        }.Build();

    private static byte[] WithJfifDensity(byte[] jpeg, int units, int x, int y)
    {
        int at = jpeg.AsSpan().IndexOf("JFIF\0"u8);
        Assert.True(at > 0);
        byte[] copy = (byte[])jpeg.Clone();
        copy[at + 7] = (byte)units;
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(at + 8), (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(at + 10), (ushort)y);
        return copy;
    }

    /// <summary>An EXIF block (with the <c>Exif\0\0</c> prefix) whose IFD0 holds XResolution, YResolution and, when <paramref name="unit"/> is non-zero, ResolutionUnit.</summary>
    private static byte[] ExifWithDensity(bool little, int unit, uint density)
    {
        using var ms = new MemoryStream();
        ms.Write("Exif\0\0"u8);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }

        ms.Write(little ? "II"u8 : "MM"u8);
        U16(42);
        U32(8);
        int entries = unit != 0 ? 3 : 2;
        U16(entries);
        uint dataStart = (uint)(8 + 2 + (entries * 12) + 4);
        U16(0x011A); U16(5); U32(1); U32(dataStart);
        U16(0x011B); U16(5); U32(1); U32(dataStart + 8);
        if (unit != 0)
        {
            U16(0x0128); U16(3); U32(1);
            U16(unit);
            U16(0);
        }

        U32(0);
        U32(density); U32(1);
        U32(density); U32(1);
        return ms.ToArray();
    }
}
