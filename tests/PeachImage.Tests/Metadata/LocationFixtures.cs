using System.Buffers.Binary;
using PeachImage.Tests.Formats.Png;

namespace PeachImage.Tests.Metadata;

/// <summary>Builds EXIF/XMP/IPTC blobs and wraps them into JPEG, PNG and WebP files, independent of the readers under test.</summary>
internal static class LocationFixtures
{
    public const double Latitude = 37 + (46 / 60.0) + (30 / 3600.0);

    public static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    /// <summary>A TIFF-structured EXIF block. IFD0 holds an optional orientation and a pointer to a GPS IFD with whichever tags are asked for.</summary>
    public static byte[] Exif(bool littleEndian, bool latitude = false, bool longitude = false, bool destination = false, int orientation = 0, bool gpsIfd = true, bool prefix = true)
    {
        using var ms = new MemoryStream();
        if (prefix)
        {
            ms.Write("Exif\0\0"u8);
        }

        void U16(int v) { Span<byte> b = stackalloc byte[2]; if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); ms.Write(b); }

        ms.Write(littleEndian ? "II"u8 : "MM"u8);
        U16(42);
        U32(8);

        int ifd0Entries = (orientation != 0 ? 1 : 0) + (gpsIfd ? 1 : 0);
        uint gpsOffset = 8 + 2 + (uint)(ifd0Entries * 12) + 4;
        U16(ifd0Entries);
        if (orientation != 0)
        {
            U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        }

        if (gpsIfd)
        {
            U16(0x8825); U16(4); U32(1); U32(gpsOffset);
        }

        U32(0);
        if (!gpsIfd)
        {
            return ms.ToArray();
        }

        var tags = new List<(int Tag, bool Rational)>();
        if (latitude) { tags.Add((1, false)); tags.Add((2, true)); }
        if (longitude) { tags.Add((3, false)); tags.Add((4, true)); }
        if (destination) { tags.Add((0x13, false)); tags.Add((0x14, true)); tags.Add((0x15, false)); tags.Add((0x16, true)); }

        uint dataOffset = gpsOffset + 2 + (uint)(tags.Count * 12) + 4;
        U16(tags.Count);
        var rationalOffsets = new List<uint>();
        foreach (var (tag, rational) in tags)
        {
            U16(tag);
            if (rational)
            {
                U16(5); U32(3); U32(dataOffset);
                rationalOffsets.Add(dataOffset);
                dataOffset += 24;
            }
            else
            {
                U16(2); U32(2);
                ms.WriteByte((byte)(tag is 1 or 0x13 ? 'N' : 'W'));
                ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
            }
        }

        U32(0);
        foreach (var _ in rationalOffsets)
        {
            // 37/1, 46/1, 3000/100: the same value whatever the tag, which is enough to check the arithmetic.
            U32(37); U32(1); U32(46); U32(1); U32(3000); U32(100);
        }

        return ms.ToArray();
    }

    public static byte[] XmpPacket(bool gps) =>
        System.Text.Encoding.UTF8.GetBytes(
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\""
            + (gps ? " exif:GPSLatitude=\"37,46.5N\" exif:GPSLongitude=\"122,25.2W\"" : " dc:title=\"A photo\"")
            + "/></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");

    /// <summary>Raw IPTC-IIM records: an object name, plus (optionally) a value for each given dataset number.</summary>
    public static byte[] Iim(params (int DataSet, string Value)[] records)
    {
        using var ms = new MemoryStream();
        void Record(int dataSet, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            ms.WriteByte(0x1C); ms.WriteByte(2); ms.WriteByte((byte)dataSet);
            ms.WriteByte((byte)(bytes.Length >> 8)); ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        Record(5, "object name");
        foreach (var (dataSet, value) in records)
        {
            Record(dataSet, value);
        }

        return ms.ToArray();
    }

    /// <summary>A Photoshop APP13 payload: "Photoshop 3.0", then one 8BIM IPTC resource (0x0404).</summary>
    public static byte[] Photoshop(byte[] iim)
    {
        using var ms = new MemoryStream();
        ms.Write("Photoshop 3.0\0"u8);
        ms.Write("8BIM"u8);
        ms.WriteByte(0x04); ms.WriteByte(0x04);
        ms.WriteByte(0); ms.WriteByte(0); // empty pascal name, padded to even
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)iim.Length);
        ms.Write(size);
        ms.Write(iim);
        if ((iim.Length & 1) != 0)
        {
            ms.WriteByte(0);
        }

        return ms.ToArray();
    }

    public static byte[] BaseJpeg(int size = 8)
    {
        using var image = Image.Create(size, size, PixelFormat.Rgb24);
        using var ms = new MemoryStream();
        image.Save(ms, "jpeg");
        return ms.ToArray();
    }

    public static byte[] BasePng(int size = 8)
    {
        using var image = Image.Create(size, size, PixelFormat.Rgb24);
        using var ms = new MemoryStream();
        image.Save(ms, "png");
        return ms.ToArray();
    }

    /// <summary>Inserts marker segments right after the JPEG's SOI.</summary>
    public static byte[] JpegWith(byte[] jpeg, params (byte Marker, byte[] Payload)[] segments)
    {
        using var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        foreach (var (marker, payload) in segments)
        {
            ms.WriteByte(0xFF);
            ms.WriteByte(marker);
            int length = payload.Length + 2;
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)length);
            ms.Write(payload);
        }

        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    public static byte[] Concat(byte[] a, byte[] b) => [.. a, .. b];

    /// <summary>Inserts chunks right after the PNG's IHDR.</summary>
    public static byte[] PngWith(byte[] png, params (string Type, byte[] Data)[] chunks)
    {
        const int AfterIhdr = 8 + 12 + 13;
        using var ms = new MemoryStream();
        ms.Write(png, 0, AfterIhdr);
        foreach (var (type, data) in chunks)
        {
            PngTestFileBuilder.WriteChunk(ms, type, data);
        }

        ms.Write(png, AfterIhdr, png.Length - AfterIhdr);
        return ms.ToArray();
    }

    public static byte[] PngXmpChunk(byte[] packet) => [.. "XML:com.adobe.xmp\0\0\0\0\0"u8.ToArray(), .. packet];

    /// <summary>A WebP written by PeachImage's encoder carrying the given EXIF and/or XMP profiles.</summary>
    public static byte[] Webp(byte[]? exif = null, byte[]? xmp = null, byte[]? icc = null)
    {
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        if (exif is not null)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        }

        if (xmp is not null)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = xmp });
        }

        if (icc is not null)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = icc });
        }

        using var ms = new MemoryStream();
        image.Save(ms, "webp");
        return ms.ToArray();
    }

    /// <summary>A file written by PeachImage's own encoder for <paramref name="format"/> carrying the given EXIF and/or XMP.</summary>
    public static byte[] Encoded(string format, byte[]? exif = null, byte[]? xmp = null)
    {
        using var image = Image.Create(8, 8, PixelFormat.Rgb24);
        if (exif is not null)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        }

        if (xmp is not null)
        {
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = xmp });
        }

        using var ms = new MemoryStream();
        image.Save(ms, format);
        return ms.ToArray();
    }

    public static IEnumerable<(string Name, byte[] Bytes)> AllLocationFiles()
    {
        foreach (bool le in new[] { true, false })
        {
            string order = le ? "LE" : "BE";
            var exif = Exif(le, latitude: true, longitude: true, destination: true);
            yield return ($"jpeg-exif-{order}", JpegWith(BaseJpeg(), (0xE1, exif)));
            yield return ($"png-exif-{order}", PngWith(BasePng(), ("eXIf", exif[6..])));
            yield return ($"webp-exif-{order}", Webp(exif: exif));
        }

        yield return ("jpeg-xmp", JpegWith(BaseJpeg(), (0xE1, Concat(XmpHeader, XmpPacket(true)))));
        yield return ("png-xmp", PngWith(BasePng(), ("iTXt", PngXmpChunk(XmpPacket(true)))));
        yield return ("webp-xmp", Webp(xmp: XmpPacket(true)));
        yield return ("gif-xmp", Encoded("gif", xmp: XmpPacket(true)));
        yield return ("avif-xmp", Encoded("avif", xmp: XmpPacket(true)));
        yield return ("avif-exif", Encoded("avif", exif: Exif(true, latitude: true, orientation: 6, prefix: false)));
        yield return ("jxl-xmp", Encoded("jxl", xmp: XmpPacket(true)));
        yield return ("jxl-exif", Encoded("jxl", exif: Exif(false, longitude: true, prefix: false)));
        yield return ("jpeg-iptc", JpegWith(BaseJpeg(), (0xED, Photoshop(Iim((90, "San Francisco"))))));
    }
}
