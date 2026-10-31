using System.Buffers.Binary;
using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Tests.Metadata;

/// <summary>The location readers see attacker-controlled bytes: nothing may throw anything but an ImageFormatException, loop, or allocate from a declared size.</summary>
public class LocationHostileInputTests
{
    [Fact]
    public void Exif_IfdChainLoop_Terminates()
    {
        // IFD0 at offset 8 with zero entries whose "next IFD" pointer points back at itself.
        var tiff = new byte[8 + 2 + 4];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(10), 8);

        var result = ExifLocationReader.Read(tiff);

        Assert.False(result.HasLocation);
    }

    [Fact]
    public void Exif_HugeEntryCount_IsBoundedByTheData()
    {
        var tiff = new byte[8 + 2 + 12 + 4];
        "MM\0*"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 0xFFFF);

        Assert.False(ExifLocationReader.Read(tiff).HasLocation);
    }

    [Fact]
    public void Exif_GpsIfdOffsetPastTheEnd_IsIgnored()
    {
        var tiff = LocationFixtures.Exif(true, latitude: true, prefix: false);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(8 + 2 + 8), 0xFFFFFFF0);

        Assert.False(ExifLocationReader.Read(tiff).HasLocation);
    }

    [Fact]
    public void Exif_RationalOffsetPastTheEnd_StillReportsThePresentTag()
    {
        var tiff = LocationFixtures.Exif(true, latitude: true, prefix: false);
        int gpsIfd = 8 + 2 + 12 + 4;
        // Second GPS entry (GPSLatitude): point its value at an offset beyond the data.
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(gpsIfd + 2 + 12 + 8), 0x7FFFFFF0);

        var result = ExifLocationReader.Read(tiff);

        Assert.True(result.ExifGps);
        Assert.Null(result.Exif!.Value.Latitude);
    }

    [Fact]
    public void Exif_ZeroDenominator_GivesNoValueButStillReportsTheTag()
    {
        var tiff = LocationFixtures.Exif(true, longitude: true, prefix: false);
        int gpsIfd = 8 + 2 + 12 + 4;
        int rationalData = gpsIfd + 2 + (2 * 12) + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(rationalData + 4), 0);

        var result = ExifLocationReader.Read(tiff);

        Assert.True(result.ExifGps);
        Assert.Null(result.Exif!.Value.Longitude);
    }

    [Fact]
    public void XmpScan_OnlyLooksAtTheCap()
    {
        var packet = new byte[XmpLocationScanner.MaxScannedBytes + 100];
        "GPSLatitude"u8.CopyTo(packet.AsSpan(XmpLocationScanner.MaxScannedBytes + 10));

        Assert.False(XmpLocationScanner.NamesLocation(packet));
        "GPSLatitude"u8.CopyTo(packet.AsSpan(100));
        Assert.True(XmpLocationScanner.NamesLocation(packet));
    }

    [Fact]
    public void Iptc_ExtendedLengthAndTruncatedRecords_AreRejectedQuietly()
    {
        Assert.False(IptcLocationReader.ReadIim([0x1C, 2, 90, 0x80, 0x04, 1, 2, 3, 4]));
        Assert.False(IptcLocationReader.ReadIim([0x1C, 2, 90, 0xFF, 0xFE, 65]));
        Assert.False(IptcLocationReader.ReadPhotoshop("Photoshop 3.0\08BIM\x04\x04\0\0\xFF\xFF\xFF\xFF"u8));
    }

    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in LocationFixtures.AllLocationFiles())
        {
            data.Add(name);
        }

        return data;
    }

    private static string FormatOf(string name) => name[..name.IndexOf('-')];

    [Theory]
    [MemberData(nameof(FileNames))]
    public void EveryTruncation_IsHandledWithoutThrowing(string name)
    {
        var bytes = LocationFixtures.AllLocationFiles().First(f => f.Name == name).Bytes;

        for (int length = 0; length < bytes.Length; length++)
        {
            using var stream = new MemoryStream(bytes, 0, length);
            Exception? failure = Record.Exception(() => ContainerLocationScanner.Scan(stream, FormatOf(name)));
            Assert.True(failure is null, $"{name} truncated to {length}: {failure}");
        }
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void RandomMutations_NeverEscapeAsUnexpectedExceptions(string name)
    {
        var original = LocationFixtures.AllLocationFiles().First(f => f.Name == name).Bytes;
        var random = new Random(4242);

        for (int attempt = 0; attempt < 300; attempt++)
        {
            var mutated = (byte[])original.Clone();
            for (int edit = random.Next(1, 6); edit > 0; edit--)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            using var stream = new MemoryStream(mutated);
            Exception? failure = Record.Exception(() => ContainerLocationScanner.Scan(stream, FormatOf(name)));
            Assert.True(failure is null, $"{name} attempt {attempt}: {failure}");
        }
    }

    [Fact]
    public void PngChunkWithHugeDeclaredLength_IsNotAllocated()
    {
        var png = LocationFixtures.BasePng();
        var hostile = LocationFixtures.PngWith(png, ("eXIf", new byte[16]));
        // Declare 3.9 GB for the eXIf chunk, which sits right after IHDR (offset 33).
        BinaryPrimitives.WriteUInt32BigEndian(hostile.AsSpan(33), 0xF000_0000);

        using var stream = new MemoryStream(hostile);
        Exception? failure = Record.Exception(() => ContainerLocationScanner.Scan(stream, "png"));

        Assert.Null(failure);
    }
}
