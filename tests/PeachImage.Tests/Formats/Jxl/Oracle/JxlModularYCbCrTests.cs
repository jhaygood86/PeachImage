using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// libjxl never writes a Modular frame with a YCbCr colour transform, so one is made by editing the frame header of a
/// lossless libjxl encode: <c>do_YCbCr</c> is switched on (with unsubsampled chroma) and the table of contents re-aligned. The
/// three decoded channels are then interpreted as (Cb, Y, Cr).
/// </summary>
[Trait("Category", "Oracle")]
public class JxlModularYCbCrTests
{
    [Fact]
    public void ModularYCbCrFrame_DecodesThroughTheYCbCrTransform()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] file = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] edited = EnableYCbCr(file);

        using var original = Image.Load(file);
        using var decoded = Image.Load(edited);
        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.PixelFormat, decoded.PixelFormat);

        var source = original.GetPixelSpan();
        var ours = decoded.GetPixelSpan();
        int bytesPerPixel = original.PixelFormat.GetBytesPerPixel();
        Assert.True(bytesPerPixel >= 3);
        int worst = 0;
        for (int i = 0; i + bytesPerPixel <= source.Length; i += bytesPerPixel)
        {
            double cb = source[i] / 255.0;
            double y = (source[i + 1] / 255.0) + (128.0 / 255.0);
            double cr = source[i + 2] / 255.0;
            double[] expected =
            [
                y + (1.402 * cr),
                y + (-0.114 * 1.772 / 0.587 * cb) + (-0.299 * 1.402 / 0.587 * cr),
                y + (1.772 * cb),
            ];
            for (int c = 0; c < 3; c++)
            {
                int want = (int)Math.Clamp((expected[c] * 255.0) + 0.5, 0, 255);
                worst = Math.Max(worst, Math.Abs(want - ours[i + c]));
            }
        }

        Assert.True(worst <= 1, $"The largest deviation from the YCbCr formula is {worst}.");
    }

    private static byte[] EnableYCbCr(byte[] file)
    {
        byte[] codestream = JxlContainer.Parse(file).Codestream.ToArray();
        var headers = JxlCodestreamHeaders.Read(codestream);
        int frameStart = headers.FrameOffset;
        ReadOnlySpan<byte> frame = codestream.AsSpan(frameStart);

        // Position of the do_YCbCr bit: after all_default, frame_type, encoding and flags.
        var reader = new JxlBitReader(frame);
        Assert.False(reader.ReadBool(), "The frame header is expected to be explicit.");
        reader.ReadBits(2);
        reader.ReadBits(1);
        JxlFieldReader.ReadU64(ref reader);
        long doYCbCr = reader.BitPosition;
        Assert.Equal(0u, reader.ReadBits(1));

        var full = new JxlBitReader(frame);
        var header = JxlFrameHeader.Read(ref full, headers.Metadata, headers.Size);
        Assert.True(header.IsModular);
        long headerEnd = full.BitPosition;
        Assert.Equal(0u, full.ReadBits(1)); // no TOC permutation
        long sizesStart = (headerEnd + 1 + 7) & ~7L;

        var bits = new List<bool>();
        for (long i = 0; i < doYCbCr; i++)
        {
            bits.Add(((frame[(int)(i >> 3)] >> (int)(i & 7)) & 1) != 0);
        }

        bits.Add(true);
        for (int i = 0; i < 6; i++)
        {
            bits.Add(false); // chroma subsampling modes: none
        }

        for (long i = doYCbCr + 1; i < headerEnd; i++)
        {
            bits.Add(((frame[(int)(i >> 3)] >> (int)(i & 7)) & 1) != 0);
        }

        bits.Add(false); // no TOC permutation
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var result = new List<byte>(codestream.AsSpan(0, frameStart).ToArray());
        for (int i = 0; i < bits.Count; i += 8)
        {
            int value = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bits[i + b])
                {
                    value |= 1 << b;
                }
            }

            result.Add((byte)value);
        }

        result.AddRange(frame[(int)(sizesStart >> 3)..].ToArray());
        return [.. result];
    }
}
