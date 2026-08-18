using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// End-to-end tests against hand-built, minimal-but-valid VP8L bitstreams — the strongest available
/// correctness signal in the absence of a real libwebp encoder to diff against (see the task notes this
/// codebase was built against). Every bit is assembled by hand via <see cref="BitWriter"/>, using only
/// "simple" Huffman codes (1-2 symbols, no canonical-code machinery) so the expected bit sequence can be
/// worked out by hand rather than depending on the very Huffman table builder under test elsewhere.
/// </summary>
public class Vp8LDecoderTests
{
    [Fact]
    public void Decode_HandBuiltLiteralOnlyBitstream_ReproducesExpectedPixels()
    {
        // 2x2 image, no transforms, no color cache, a single (non-meta) Huffman group, pure literals (no
        // backward references). Red/Blue are constant across every pixel (trivial, zero-bit "simple code,
        // 1 symbol" trees); Green alternates between two values (a trivial "simple code, 2 symbols" tree,
        // whose canonical code is worked out by hand below); Alpha is constant and unused in the Rgb24 output.
        var writer = new BitWriter();

        writer.WriteBits(1, 14); // width - 1 = 1  -> width = 2
        writer.WriteBits(1, 14); // height - 1 = 1 -> height = 2
        writer.WriteBits(0, 1); // alpha_is_used = false -> Rgb24 output
        writer.WriteBits(0, 3); // version_number

        writer.WriteBits(0, 1); // transform_present = false
        writer.WriteBits(0, 1); // color_cache_present = false
        writer.WriteBits(0, 1); // use_meta_huffman = false

        // GREEN: two symbols (20, 40). A 2-symbol/length-1 canonical tree assigns "0" to the first symbol in
        // ascending value order and "1" to the second (see Vp8LHuffmanTableBuilderTests for the general
        // proof of this construction) -- so green=20 decodes from bit 0, green=40 from bit 1.
        WriteSimpleTwoSymbolCode(writer, 20, 40);

        WriteSimpleOneSymbolCode(writer, 10, use8Bit: true); // RED: constant 10.
        WriteSimpleOneSymbolCode(writer, 30, use8Bit: true); // BLUE: constant 30.
        WriteSimpleOneSymbolCode(writer, 0, use8Bit: false); // ALPHA: constant 0 (unused by Rgb24 output).
        WriteSimpleOneSymbolCode(writer, 0, use8Bit: false); // DISTANCE: never referenced by this stream, but every stream must still declare a valid tree.

        // Pixel stream: only GREEN actually consumes bits (Red/Blue/Alpha are all zero-bit trivial tables).
        writer.WriteBits(0, 1); // pixel 0: green = 20
        writer.WriteBits(1, 1); // pixel 1: green = 40
        writer.WriteBits(0, 1); // pixel 2: green = 20
        writer.WriteBits(1, 1); // pixel 3: green = 40

        byte[] chunk = BuildChunk(writer);

        var image = Vp8LDecoder.Decode(chunk);

        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);

        var pixels = image.GetPixelSpan();
        AssertRgbPixel(pixels, index: 0, r: 10, g: 20, b: 30);
        AssertRgbPixel(pixels, index: 1, r: 10, g: 40, b: 30);
        AssertRgbPixel(pixels, index: 2, r: 10, g: 20, b: 30);
        AssertRgbPixel(pixels, index: 3, r: 10, g: 40, b: 30);
    }

    [Fact]
    public void Decode_SinglePixelWithAllConstantChannels_NeedsNoPixelStreamBitsAtAll()
    {
        // 1x1 image where every one of the five trees is a trivial single-symbol ("simple code, 1 symbol")
        // table -- every such table consumes zero bits per decode, so the entire pixel is determined purely
        // by the tree declarations, with nothing left in the bitstream for the pixel loop itself to read.
        var writer = new BitWriter();

        writer.WriteBits(0, 14); // width - 1 = 0 -> width = 1
        writer.WriteBits(0, 14); // height - 1 = 0 -> height = 1
        writer.WriteBits(1, 1); // alpha_is_used = true -> Rgba32 output
        writer.WriteBits(0, 3); // version_number

        writer.WriteBits(0, 1); // transform_present
        writer.WriteBits(0, 1); // color_cache_present
        writer.WriteBits(0, 1); // use_meta_huffman

        WriteSimpleOneSymbolCode(writer, 200, use8Bit: true); // GREEN
        WriteSimpleOneSymbolCode(writer, 50, use8Bit: true); // RED
        WriteSimpleOneSymbolCode(writer, 150, use8Bit: true); // BLUE
        WriteSimpleOneSymbolCode(writer, 128, use8Bit: true); // ALPHA
        WriteSimpleOneSymbolCode(writer, 0, use8Bit: false); // DISTANCE

        byte[] chunk = BuildChunk(writer);

        var image = Vp8LDecoder.Decode(chunk);

        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);

        var pixels = image.GetPixelSpan();
        Assert.Equal(50, pixels[0]);  // red
        Assert.Equal(200, pixels[1]); // green
        Assert.Equal(150, pixels[2]); // blue
        Assert.Equal(128, pixels[3]); // alpha
    }

    private static void WriteSimpleTwoSymbolCode(BitWriter writer, int symbol1, int symbol2)
    {
        writer.WriteBits(1, 1); // simple_code = true
        writer.WriteBits(1, 1); // num_symbols - 1 = 1 -> 2 symbols
        writer.WriteBits(1, 1); // first symbol uses an 8-bit code (both our symbols exceed 1 bit)
        writer.WriteBits((uint)symbol1, 8);
        writer.WriteBits((uint)symbol2, 8); // the second symbol is always 8 bits, per the format.
    }

    private static void WriteSimpleOneSymbolCode(BitWriter writer, int symbol, bool use8Bit)
    {
        writer.WriteBits(1, 1); // simple_code = true
        writer.WriteBits(0, 1); // num_symbols - 1 = 0 -> 1 symbol
        writer.WriteBits(use8Bit ? 1u : 0u, 1);
        writer.WriteBits((uint)symbol, use8Bit ? 8 : 1);
    }

    private static byte[] BuildChunk(BitWriter writer)
    {
        byte[] body = writer.ToBytes();
        byte[] chunk = new byte[1 + body.Length];
        chunk[0] = 0x2F;
        body.CopyTo(chunk, 1);
        return chunk;
    }

    private static void AssertRgbPixel(ReadOnlySpan<byte> pixels, int index, byte r, byte g, byte b)
    {
        int offset = index * 3;
        Assert.Equal(r, pixels[offset]);
        Assert.Equal(g, pixels[offset + 1]);
        Assert.Equal(b, pixels[offset + 2]);
    }

    /// <summary>Assembles a bit sequence least-significant-bit-first, matching <see cref="Vp8LBitReader"/>'s convention, then packs it into bytes.</summary>
    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        public void WriteBits(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _bits.Add(((value >> i) & 1) != 0);
            }
        }

        public byte[] ToBytes()
        {
            int byteCount = (_bits.Count + 7) / 8;
            var bytes = new byte[byteCount];
            for (int i = 0; i < _bits.Count; i++)
            {
                if (_bits[i])
                {
                    bytes[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            return bytes;
        }
    }
}
