using System.Buffers.Binary;
using System.IO.Compression;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Container;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlContainerTests
{
    private static readonly byte[] Signature = [0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] Ftyp = Box("ftyp", "jxl "u8.ToArray(), [0, 0, 0, 0], "jxl "u8.ToArray());

    private static byte[] Box(string type, params byte[][] parts)
    {
        int payload = parts.Sum(p => p.Length);
        var bytes = new byte[8 + payload];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        for (int i = 0; i < 4; i++)
        {
            bytes[4 + i] = (byte)type[i];
        }

        int at = 8;
        foreach (var part in parts)
        {
            part.CopyTo(bytes, at);
            at += part.Length;
        }

        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Partial(uint index, bool last, byte[] data)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, index | (last ? 0x80000000u : 0));
        return Box("jxlp", header, data);
    }

    [Fact]
    public void Jxlc_CodestreamIsReturnedAsIs()
    {
        byte[] codestream = [0xFF, 0x0A, 1, 2, 3];
        var container = JxlContainer.Parse(Concat(Signature, Ftyp, Box("jxlc", codestream)));

        Assert.Equal(codestream, container.Codestream.ToArray());
    }

    [Fact]
    public void Jxlp_PartsAreConcatenatedInOrder()
    {
        var container = JxlContainer.Parse(Concat(Signature, Ftyp, Partial(0, false, [0xFF, 0x0A]), Partial(1, false, [1, 2]), Partial(2, true, [3])));

        Assert.Equal(new byte[] { 0xFF, 0x0A, 1, 2, 3 }, container.Codestream.ToArray());
    }

    [Fact]
    public void Jxlp_OutOfOrder_Throws()
    {
        Assert.Throws<JxlDecodingException>(() =>
            JxlContainer.Parse(Concat(Signature, Ftyp, Partial(1, false, [0xFF, 0x0A]), Partial(0, true, [1]))));
    }

    [Fact]
    public void Jxlp_MissingLast_Throws()
    {
        Assert.Throws<JxlDecodingException>(() =>
            JxlContainer.Parse(Concat(Signature, Ftyp, Partial(0, false, [0xFF, 0x0A]))));
    }

    [Fact]
    public void MixedJxlcAndJxlp_Throws()
    {
        Assert.Throws<JxlDecodingException>(() =>
            JxlContainer.Parse(Concat(Signature, Ftyp, Box("jxlc", [0xFF, 0x0A]), Partial(0, true, [1]))));
    }

    [Fact]
    public void ExifBox_SkipsTiffHeaderOffsetPrefix()
    {
        byte[] exifBox = Box("Exif", [0, 0, 0, 2], [0xAA, 0xBB], [(byte)'I', (byte)'I', 42, 0]);
        var container = JxlContainer.Parse(Concat(Signature, Ftyp, exifBox, Box("jxlc", [0xFF, 0x0A])));

        Assert.Equal(new byte[] { (byte)'I', (byte)'I', 42, 0 }, container.Exif);
    }

    [Fact]
    public void XmlBox_IsCaptured()
    {
        byte[] xmp = "<x:xmpmeta/>"u8.ToArray();
        var container = JxlContainer.Parse(Concat(Signature, Ftyp, Box("xml ", xmp), Box("jxlc", [0xFF, 0x0A])));

        Assert.Equal(xmp, container.Xmp);
    }

    [Fact]
    public void BrotliCompressedXml_IsDecompressed()
    {
        byte[] xmp = "<x:xmpmeta>hello hello hello hello</x:xmpmeta>"u8.ToArray();
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(xmp);
        }

        var container = JxlContainer.Parse(Concat(Signature, Ftyp, Box("brob", "xml "u8.ToArray(), compressed.ToArray()), Box("jxlc", [0xFF, 0x0A])));

        Assert.Equal(xmp, container.Xmp);
    }

    [Fact]
    public void BoxWithOversizedLength_Throws()
    {
        byte[] bad = Concat(Signature, Ftyp, Box("jxlc", [0xFF, 0x0A]));
        BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(Signature.Length + Ftyp.Length), 9999);

        Assert.Throws<JxlDecodingException>(() => JxlContainer.Parse(bad));
    }

    [Fact]
    public void ExtendedSizeBox_IsSupported()
    {
        // size == 1 → 64-bit size follows the type.
        byte[] payload = [0xFF, 0x0A, 7];
        var box = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        "jxlc"u8.CopyTo(box.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        payload.CopyTo(box, 16);

        var container = JxlContainer.Parse(Concat(Signature, Ftyp, box));

        Assert.Equal(payload, container.Codestream.ToArray());
    }

    [Fact]
    public void MissingCodestream_Throws() =>
        Assert.Throws<JxlDecodingException>(() => JxlContainer.Parse(Concat(Signature, Ftyp)));
}
