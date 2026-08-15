using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Chunks;

public class ChunkFramingTests
{
    [Fact]
    public void ValidFile_Decodes()
    {
        byte[] file = BuildMinimalGrayscaleFile();

        using var image = new PngDecoder().Decode(new MemoryStream(file));

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public void CorruptedCrcOnCriticalChunk_Throws()
    {
        byte[] file = BuildMinimalGrayscaleFile();

        // The IHDR chunk's CRC is the last 4 bytes before the next chunk header (8 sig + 8 header + 13 data = byte 29..33).
        int crcOffset = 8 + 8 + 13;
        file[crcOffset] ^= 0xFF;

        Assert.Throws<PngDecodingException>(() => new PngDecoder().Decode(new MemoryStream(file)));
    }

    [Fact]
    public void TruncatedStream_ThrowsPngDecodingException_NotRawIoException()
    {
        byte[] file = BuildMinimalGrayscaleFile();
        byte[] truncated = file[..(file.Length - 10)];

        Assert.Throws<PngDecodingException>(() => new PngDecoder().Decode(new MemoryStream(truncated)));
    }

    [Fact]
    public void UnrecognizedCriticalChunk_Throws()
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        PngTestFileBuilder.WriteChunk(ms, "IHDR", BuildIhdr(1, 1, 8, 0));
        PngTestFileBuilder.WriteChunk(ms, "XxXx", []); // uppercase first letter = critical, per spec bit-5 convention.

        Assert.Throws<PngDecodingException>(() => new PngDecoder().Decode(new MemoryStream(ms.ToArray())));
    }

    [Fact]
    public void UnrecognizedAncillaryChunk_IsSkippedGracefully()
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        PngTestFileBuilder.WriteChunk(ms, "IHDR", BuildIhdr(1, 1, 8, 0));
        PngTestFileBuilder.WriteChunk(ms, "xxXx", [1, 2, 3]); // lowercase first letter = ancillary; safely skippable.

        byte[] onePixelIdat = BuildGrayscaleIdat([[0, 128]]);
        PngTestFileBuilder.WriteChunk(ms, "IDAT", onePixelIdat);
        PngTestFileBuilder.WriteChunk(ms, "IEND", []);

        using var image = new PngDecoder().Decode(new MemoryStream(ms.ToArray()));
        Assert.Equal(128, image.GetPixelSpan()[0]);
    }

    [Fact]
    public void NotStartingWithPngSignature_Throws()
    {
        byte[] notPng = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Throws<PngDecodingException>(() => new PngDecoder().Decode(new MemoryStream(notPng)));
    }

    private static byte[] BuildMinimalGrayscaleFile() =>
        PngTestFileBuilder.Build(2, 2, 8, colorType: 0, palette: null, trns: null,
            scanlines: [[0, 10, 20], [0, 30, 40]]);

    private static byte[] BuildIhdr(int width, int height, byte bitDepth, byte colorType)
    {
        var data = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), (uint)height);
        data[8] = bitDepth;
        data[9] = colorType;
        return data;
    }

    private static byte[] BuildGrayscaleIdat(byte[][] scanlines)
    {
        using var raw = new MemoryStream();
        foreach (var s in scanlines)
        {
            raw.Write(s);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(raw.ToArray());
        }

        return compressed.ToArray();
    }
}
