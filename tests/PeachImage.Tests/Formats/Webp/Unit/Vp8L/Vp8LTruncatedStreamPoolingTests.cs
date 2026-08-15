using PeachImage.Formats.Webp.Decoding.Vp8L;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

/// <summary>
/// Guards against a truncated or corrupt VP8L stream surfacing pixels left behind by a <em>previous</em>
/// decode.
/// </summary>
/// <remarks>
/// <para>
/// The pixel buffer is rented from <c>WebpBufferPool.SharedUInt32</c>, and <c>ArrayPool</c> clears arrays
/// neither on rent nor on return. When the pixel loop stops early — on a truncated stream, a backward
/// reference pointing outside the buffer, or an out-of-range code — every pixel it never reached still holds
/// whatever the last decode wrote there. Without an explicit clear, decoding a malformed file in a process
/// that previously decoded a different image hands back that other image's content, which is an
/// information leak rather than merely a rendering artifact.
/// </para>
/// <para>
/// Both decodes below use the same dimensions deliberately: identical pixel counts land in the same
/// <c>ArrayPool</c> bucket, which is what makes the second decode actually receive the first one's array and
/// so makes the leak reproducible rather than incidental.
/// </para>
/// </remarks>
public class Vp8LTruncatedStreamPoolingTests
{
    private const int Width = 64;
    private const int Height = 64;

    [Fact]
    public void Decode_TruncatedStream_LeavesUndecodedPixelsBlackRatherThanThePreviousDecodesPixels()
    {
        // 1. A complete stream whose every pixel is a distinctive non-black value. This is the "previous
        //    image" whose pixels must not resurface; decoding it returns its buffer to the pool, uncleared.
        using (var poisoning = Vp8LDecoder.Decode(BuildStream(pixelBits: Width * Height)))
        {
            var poisoned = poisoning.GetPixelSpan();
            Assert.Equal(0xAB, poisoned[0]);
            Assert.Equal(0xCD, poisoned[1]);
            Assert.Equal(0xEF, poisoned[2]);
        }

        // 2. The same dimensions, but the pixel stream runs out after a quarter of the pixels. The decoder
        //    stops early by design; what matters is what it leaves behind in the rest of the buffer.
        int decodedPixels = (Width * Height) / 4;
        using var truncated = Vp8LDecoder.Decode(BuildStream(pixelBits: decodedPixels));

        Assert.Equal(Width, truncated.Width);
        Assert.Equal(Height, truncated.Height);

        var pixels = truncated.GetPixelSpan();

        // Allow a small margin past the truncation point: the bit reader only reports over-budget once
        // consumption passes the buffer's end, so a few extra pixels legitimately decode from the zero
        // padding. Everything beyond that must be black.
        const int Margin = 64;
        for (int i = decodedPixels + Margin; i < Width * Height; i++)
        {
            int offset = i * 3;
            Assert.True(
                pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0,
                $"Pixel {i} is ({pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]}), not black — the undecoded "
                + "tail of a truncated stream is exposing a previous decode's pixels.");
        }
    }

    /// <summary>
    /// Builds a <see cref="Width"/>x<see cref="Height"/> VP8L chunk carrying exactly
    /// <paramref name="pixelBits"/> pixels' worth of pixel-stream bits. Supplying fewer than
    /// <c>Width * Height</c> produces a stream that is well-formed up to the point it simply stops — the
    /// truncation case, without needing a corpus file to cut down.
    /// </summary>
    private static byte[] BuildStream(int pixelBits)
    {
        var writer = new BitWriter();

        writer.WriteBits(Width - 1, 14);
        writer.WriteBits(Height - 1, 14);
        writer.WriteBits(0, 1); // alpha_is_used = false -> Rgb24
        writer.WriteBits(0, 3); // version_number

        writer.WriteBits(0, 1); // transform_present = false
        writer.WriteBits(0, 1); // color_cache_present = false
        writer.WriteBits(0, 1); // use_meta_huffman = false

        // Green is a two-symbol tree so each pixel consumes exactly one bit, which is what lets the caller
        // control how far the pixel stream reaches. Red/Blue/Alpha/Distance are single-symbol (zero-bit) trees.
        WriteSimpleTwoSymbolCode(writer, 0xCD, 0xCE);
        WriteSimpleOneSymbolCode(writer, 0xAB, use8Bit: true);  // RED
        WriteSimpleOneSymbolCode(writer, 0xEF, use8Bit: true);  // BLUE
        WriteSimpleOneSymbolCode(writer, 0xFF, use8Bit: true);  // ALPHA
        WriteSimpleOneSymbolCode(writer, 0, use8Bit: false);    // DISTANCE (declared but never used)

        for (int i = 0; i < pixelBits; i++)
        {
            writer.WriteBits(0, 1); // green = 0xCD
        }

        byte[] body = writer.ToBytes();
        byte[] chunk = new byte[1 + body.Length];
        chunk[0] = 0x2F;
        body.CopyTo(chunk, 1);
        return chunk;
    }

    private static void WriteSimpleTwoSymbolCode(BitWriter writer, int symbol1, int symbol2)
    {
        writer.WriteBits(1, 1); // simple_code = true
        writer.WriteBits(1, 1); // num_symbols - 1 = 1 -> 2 symbols
        writer.WriteBits(1, 1); // first symbol uses an 8-bit code
        writer.WriteBits((uint)symbol1, 8);
        writer.WriteBits((uint)symbol2, 8);
    }

    private static void WriteSimpleOneSymbolCode(BitWriter writer, int symbol, bool use8Bit)
    {
        writer.WriteBits(1, 1); // simple_code = true
        writer.WriteBits(0, 1); // num_symbols - 1 = 0 -> 1 symbol
        writer.WriteBits(use8Bit ? 1u : 0u, 1);
        writer.WriteBits((uint)symbol, use8Bit ? 8 : 1);
    }

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
            byte[] bytes = new byte[(_bits.Count + 7) / 8];
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
