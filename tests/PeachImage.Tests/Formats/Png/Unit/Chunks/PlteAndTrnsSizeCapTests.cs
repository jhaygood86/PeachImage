using System.Buffers.Binary;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Chunks;

public class PlteAndTrnsSizeCapTests
{
    [Fact]
    public void Decode_PlteDeclaresHugeLength_ThrowsBeforeAttemptingToReadIt()
    {
        // PLTE can never legitimately exceed 768 bytes (256 entries x 3), but ProcessMetadataChunk used to
        // call ReadDataAndValidateCrc (which allocates `new byte[header.Length]`) with no cap of its own —
        // only the PNG spec's own ~2GB chunk-length ceiling applied. A chunk header declaring 100,000,000
        // bytes, with none actually following it, must be rejected by a length check alone, before any read
        // is attempted (which is why this test can omit the declared bytes entirely).
        byte[] file = BuildFileWithBareChunkHeader("PLTE", declaredLength: 100_000_000, colorType: 3);

        var ex = Assert.Throws<PngDecodingException>(() => PngDecoder.Decode(new MemoryStream(file)));
        Assert.Contains("PLTE", ex.Message);
    }

    [Fact]
    public void Decode_TrnsDeclaresHugeLength_ThrowsBeforeAttemptingToReadIt()
    {
        // tRNS is capped at 256 bytes even in its largest legal form (a full palette's worth of alpha
        // entries); grayscale/truecolor need only 2/6 bytes. Same gap as PLTE above.
        byte[] file = BuildFileWithBareChunkHeader("tRNS", declaredLength: 100_000_000, colorType: 0);

        var ex = Assert.Throws<PngDecodingException>(() => PngDecoder.Decode(new MemoryStream(file)));
        Assert.Contains("tRNS", ex.Message);
    }

    [Fact]
    public void Decode_NormalSizedPlteAndTrns_StillDecodesNormally()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0];
        byte[] trns = [0, 128];

        byte[] file = PngTestFileBuilder.Build(2, 2, bitDepth: 8, colorType: 3, palette, trns,
            scanlines: [[0, 0, 1], [0, 2, 3]]);

        using var image = PngDecoder.Decode(new MemoryStream(file));

        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
    }

    /// <summary>
    /// A valid signature + IHDR, followed by a single bare 8-byte chunk header (length + type, no payload,
    /// no CRC) — enough to exercise a check that must fire purely from the declared length, without the
    /// stream containing anywhere near that many actual bytes.
    /// </summary>
    private static byte[] BuildFileWithBareChunkHeader(string chunkType, uint declaredLength, byte colorType)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        PngTestFileBuilder.WriteChunk(ms, "IHDR", BuildIhdr(2, 2, bitDepth: 8, colorType: colorType));

        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], declaredLength);
        header[4] = (byte)chunkType[0];
        header[5] = (byte)chunkType[1];
        header[6] = (byte)chunkType[2];
        header[7] = (byte)chunkType[3];
        ms.Write(header);

        return ms.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height, byte bitDepth, byte colorType)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), (uint)height);
        data[8] = bitDepth;
        data[9] = colorType;
        return data;
    }
}
