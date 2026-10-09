using PeachImage.Formats.Webp;
using System.Buffers.Binary;
using System.Text;

namespace PeachImage.Tests.Formats.Webp.Unit;

/// <summary><see cref="ImageInfo.Orientation"/> for WebP: read from the <c>EXIF</c> chunk, for still and animated files alike.</summary>
public class WebpOrientationTests
{
    [Theory]
    [InlineData(1, ImageOrientation.Normal)]
    [InlineData(2, ImageOrientation.MirrorHorizontal)]
    [InlineData(3, ImageOrientation.Rotate180)]
    [InlineData(4, ImageOrientation.MirrorVertical)]
    [InlineData(5, ImageOrientation.Transpose)]
    [InlineData(6, ImageOrientation.Rotate90)]
    [InlineData(7, ImageOrientation.Transverse)]
    [InlineData(8, ImageOrientation.Rotate270)]
    public void Identify_EncodedStill_ReportsExifOrientation(int value, ImageOrientation expected)
    {
        foreach (bool prefix in new[] { false, true })
        {
            Assert.Equal(expected, IdentifyEncoded(ExifBlob(value, littleEndian: true, prefix)).Orientation);
        }
    }

    [Fact]
    public void Identify_BigEndianExif_IsRead() =>
        Assert.Equal(ImageOrientation.Rotate90, IdentifyEncoded(ExifBlob(6, littleEndian: false, prefix: false)).Orientation);

    [Fact]
    public void Identify_WithoutExif_ReportsNormal()
    {
        var info = IdentifyEncoded(null);

        Assert.Equal(ImageOrientation.Normal, info.Orientation);
        Assert.False(info.HasPreview);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(3, false)]
    public void Identify_Animated_ReportsExifOrientationAfterFrames(int value, bool prefix)
    {
        byte[] file = BuildAnimatedFile(ExifBlob(value, littleEndian: true, prefix), setExifFlag: true);

        var info = Image.Identify(new MemoryStream(file));

        Assert.True(info.IsAnimated);
        Assert.Equal((ImageOrientation)value, info.Orientation);
    }

    [Fact]
    public void Identify_Animated_WithoutExifFlag_ReportsNormal()
    {
        byte[] file = BuildAnimatedFile(ExifBlob(6, littleEndian: true, prefix: false), setExifFlag: false);

        Assert.Equal(ImageOrientation.Normal, Image.Identify(new MemoryStream(file)).Orientation);
    }

    [Fact]
    public void Identify_Animated_TruncatedAfterFlag_ReportsNormal()
    {
        byte[] file = BuildAnimatedFile(ExifBlob(6, littleEndian: true, prefix: false), setExifFlag: true);

        Assert.Equal(ImageOrientation.Normal, Image.Identify(new MemoryStream(file[..(file.Length - 10)])).Orientation);
    }

    private static ImageInfo IdentifyEncoded(byte[]? exif)
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);
        if (exif is not null)
        {
            source.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        }

        using var ms = new MemoryStream();
        source.Save(ms, "webp", new WebpEncoderOptions());
        ms.Position = 0;
        return Image.Identify(ms);
    }

    private static byte[] ExifBlob(int orientation, bool littleEndian, bool prefix)
    {
        var bytes = new List<byte>();
        if (prefix)
        {
            bytes.AddRange("Exif\0\0"u8.ToArray());
        }

        void U16(int v) => bytes.AddRange(littleEndian ? new[] { (byte)v, (byte)(v >> 8) } : new[] { (byte)(v >> 8), (byte)v });
        void U32(uint v) => bytes.AddRange(littleEndian
            ? new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }
            : new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

        bytes.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(8);
        U16(1); // one IFD0 entry
        U16(0x0112);
        U16(3); // SHORT
        U32(1);
        U16(orientation);
        U16(0);
        U32(0); // no next IFD
        return [.. bytes];
    }

    private static byte[] Chunk(string fourCc, byte[] payload)
    {
        var chunk = new List<byte>(Encoding.ASCII.GetBytes(fourCc));
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        chunk.AddRange(size);
        chunk.AddRange(payload);
        if ((payload.Length & 1) != 0)
        {
            chunk.Add(0);
        }

        return [.. chunk];
    }

    /// <summary>A VP8X animated file whose frame (ANMF) chunk is opaque filler: Identify must skip it without decoding and still find the trailing EXIF chunk.</summary>
    private static byte[] BuildAnimatedFile(byte[] exif, bool setExifFlag)
    {
        byte[] vp8x = new byte[10];
        vp8x[0] = (byte)(0x02 | (setExifFlag ? 0x08 : 0));
        vp8x[4] = 7; // canvas width - 1
        vp8x[7] = 3; // canvas height - 1

        var body = new List<byte>(Encoding.ASCII.GetBytes("WEBP"));
        body.AddRange(Chunk("VP8X", vp8x));
        body.AddRange(Chunk("ANIM", new byte[6]));
        body.AddRange(Chunk("ANMF", new byte[33]));
        body.AddRange(Chunk("EXIF", exif));

        var file = new List<byte>(Encoding.ASCII.GetBytes("RIFF"));
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Count);
        file.AddRange(size);
        file.AddRange(body);
        return [.. file];
    }
}
