using System.Text;
using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding;

namespace PeachImage.Tests.Formats.Webp.Unit.Decoding;

public class WebpContainerReaderTests
{
    [Fact]
    public void SimpleLossless_ParsesImageChunkWithNoCanvasSize()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] riff = BuildRiff(("VP8L", payload));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var container = WebpContainerReader.Read(stream, metadata);

        Assert.Equal(WebpBitstreamFormat.Lossless, container.Format);
        Assert.Equal(payload, container.BitstreamData);
        Assert.Null(container.CanvasWidth);
        Assert.Null(container.CanvasHeight);
        Assert.Null(container.AlphaData);
    }

    [Fact]
    public void SimpleLossy_ParsesImageChunk()
    {
        byte[] payload = [9, 8, 7, 6];
        byte[] riff = BuildRiff(("VP8 ", payload));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var container = WebpContainerReader.Read(stream, metadata);

        Assert.Equal(WebpBitstreamFormat.Lossy, container.Format);
        Assert.Equal(payload, container.BitstreamData);
    }

    [Fact]
    public void ExtendedWithAlphaFlag_MissingAlphChunk_Throws()
    {
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(hasAlpha: true, hasAnimation: false, width: 10, height: 20)),
            ("VP8 ", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    [Fact]
    public void ExtendedWithAlphaFlag_AlphPresent_PopulatesAlphaData()
    {
        byte[] alpha = [42, 43, 44];
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(hasAlpha: true, hasAnimation: false, width: 10, height: 20)),
            ("ALPH", alpha),
            ("VP8 ", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var container = WebpContainerReader.Read(stream, metadata);

        Assert.Equal(10, container.CanvasWidth);
        Assert.Equal(20, container.CanvasHeight);
        Assert.Equal(alpha, container.AlphaData);
    }

    [Fact]
    public void ExtendedLossless_StrayAlphChunk_IsIgnoredNotError()
    {
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(hasAlpha: true, hasAnimation: false, width: 4, height: 4)),
            ("ALPH", [1, 2, 3]),
            ("VP8L", [9, 9, 9, 9, 9]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var container = WebpContainerReader.Read(stream, metadata);

        Assert.Equal(WebpBitstreamFormat.Lossless, container.Format);
        Assert.Null(container.AlphaData);
    }

    [Fact]
    public void MultipleImageChunks_Throws()
    {
        byte[] riff = BuildRiff(("VP8L", [1, 2, 3, 4, 5]), ("VP8 ", [1, 2, 3, 4]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    [Fact]
    public void NoImageChunk_Throws()
    {
        byte[] riff = BuildRiff(("ICCP", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    [Fact]
    public void AnimationFlag_Throws()
    {
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(hasAlpha: false, hasAnimation: true, width: 4, height: 4)),
            ("ANIM", [0, 0, 0, 0, 0, 0]),
            ("VP8 ", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var ex = Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
        Assert.Contains("Animated", ex.Message);
    }

    [Fact]
    public void MetadataChunks_PopulateProfiles()
    {
        byte[] icc = [1, 2, 3];
        byte[] exif = [4, 5, 6];
        byte[] xmp = [7, 8, 9];
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(hasAlpha: false, hasAnimation: false, width: 2, height: 2)),
            ("ICCP", icc),
            ("EXIF", exif),
            ("XMP ", xmp),
            ("VP8 ", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        WebpContainerReader.Read(stream, metadata);

        Assert.Equal(3, metadata.Profiles.Count);
        Assert.Contains(metadata.Profiles, p => p.Kind == MetadataProfileKind.Icc && p.Data.SequenceEqual(icc));
        Assert.Contains(metadata.Profiles, p => p.Kind == MetadataProfileKind.Exif && p.Data.SequenceEqual(exif));
        Assert.Contains(metadata.Profiles, p => p.Kind == MetadataProfileKind.Xmp && p.Data.SequenceEqual(xmp));
    }

    [Fact]
    public void UnrecognizedChunk_IsSkippedWithoutError()
    {
        byte[] riff = BuildRiff(("FOOB", [1, 2, 3]), ("VP8 ", [4, 5, 6]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var container = WebpContainerReader.Read(stream, metadata);

        Assert.Equal(WebpBitstreamFormat.Lossy, container.Format);
    }

    [Fact]
    public void OddSizedChunk_PaddingByteIsConsumed()
    {
        // Odd-length chunk followed by another chunk — if the pad byte weren't consumed, the next
        // chunk header would be misaligned by one byte and fail to parse.
        byte[] riff = BuildRiff(("ICCP", [1, 2, 3]), ("VP8 ", [4, 5, 6, 7]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        var container = WebpContainerReader.Read(stream, metadata);

        Assert.Equal(WebpBitstreamFormat.Lossy, container.Format);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, container.BitstreamData);
    }

    [Fact]
    public void NotRiff_Throws()
    {
        byte[] bytes = "NOTARIFFFILE"u8.ToArray();
        using var stream = new MemoryStream(bytes);
        var metadata = new ImageMetadata();

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    [Fact]
    public void NotWebp_Throws()
    {
        using var body = new MemoryStream();
        body.Write("RIFF"u8);
        WriteUInt32Le(body, 4);
        body.Write("AVI "u8);
        var metadata = new ImageMetadata();
        using var stream = new MemoryStream(body.ToArray());

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    [Fact]
    public void Vp8XWrongSize_Throws()
    {
        byte[] riff = BuildRiff(("VP8X", new byte[8]), ("VP8 ", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    [Fact]
    public void Vp8XCanvasExceedingMaxDimension_Throws()
    {
        // 24-bit-minus-one field maxed out (0xFFFFFF + 1 == 16,777,216) far exceeds the 16384 cap.
        byte[] vp8X = BuildVp8X(hasAlpha: false, hasAnimation: false, width: 1, height: 1);
        vp8X[4] = 0xFF;
        vp8X[5] = 0xFF;
        vp8X[6] = 0xFF;
        byte[] riff = BuildRiff(("VP8X", vp8X), ("VP8 ", [1, 2, 3]));
        using var stream = new MemoryStream(riff);
        var metadata = new ImageMetadata();

        Assert.Throws<WebpDecodingException>(() => WebpContainerReader.Read(stream, metadata));
    }

    private static byte[] BuildVp8X(bool hasAlpha, bool hasAnimation, int width, int height)
    {
        byte flags = 0;
        if (hasAlpha)
        {
            flags |= 0x10;
        }

        if (hasAnimation)
        {
            flags |= 0x02;
        }

        byte[] data = new byte[10];
        data[0] = flags;

        int w = width - 1;
        int h = height - 1;
        data[4] = (byte)(w & 0xFF);
        data[5] = (byte)((w >> 8) & 0xFF);
        data[6] = (byte)((w >> 16) & 0xFF);
        data[7] = (byte)(h & 0xFF);
        data[8] = (byte)((h >> 8) & 0xFF);
        data[9] = (byte)((h >> 16) & 0xFF);
        return data;
    }

    private static byte[] BuildRiff(params (string FourCc, byte[] Payload)[] chunks)
    {
        using var body = new MemoryStream();
        foreach (var (fourCc, payload) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(fourCc));
            WriteUInt32Le(body, (uint)payload.Length);
            body.Write(payload);
            if ((payload.Length & 1) != 0)
            {
                body.WriteByte(0);
            }
        }

        using var riff = new MemoryStream();
        riff.Write("RIFF"u8);
        WriteUInt32Le(riff, (uint)(4 + body.Length));
        riff.Write("WEBP"u8);
        body.Position = 0;
        body.CopyTo(riff);
        return riff.ToArray();
    }

    private static void WriteUInt32Le(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }
}
