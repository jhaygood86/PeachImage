using System.IO.Compression;
using PeachImage.Formats.Png;
using PeachImage.Formats.Png.Decoding;

namespace PeachImage.Tests.Formats.Png.Unit.Chunks;

public class AncillaryChunkDecompressionTests
{
    [Fact]
    public void TryInflate_DecompressedOutputExceedsCap_ThrowsPngDecodingException()
    {
        // Highly compressible source (10,000 zero bytes) compresses down to a tiny chunk but inflates back
        // to 10,000 bytes — well past the 100-byte cap below. Ancillary chunks (iCCP/zTXt/iTXt) previously had
        // no cap at all on decompressed output, only on the compressed input size, so a small chunk of
        // highly-compressible data could inflate to a deflate-bomb-scale allocation.
        byte[] compressed = Compress(new byte[10_000]);

        Assert.Throws<PngDecodingException>(() => PngAncillaryChunkReader.TryInflate(compressed, maxOutputBytes: 100));
    }

    [Fact]
    public void TryInflate_DecompressedOutputWithinCap_ReturnsInflatedBytes()
    {
        byte[] source = "hello world"u8.ToArray();
        byte[] compressed = Compress(source);

        byte[]? result = PngAncillaryChunkReader.TryInflate(compressed, maxOutputBytes: 1024);

        Assert.Equal(source, result);
    }

    [Fact]
    public void TryInflate_NoCapSpecified_UsesProductionDefaultAndAcceptsSmallInput()
    {
        byte[] source = "profile"u8.ToArray();
        byte[] compressed = Compress(source);

        byte[]? result = PngAncillaryChunkReader.TryInflate(compressed);

        Assert.Equal(source, result);
    }

    private static byte[] Compress(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return compressed.ToArray();
    }
}
