using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Container;

namespace PeachImage.Tests.Formats.Avif.Unit.Container;

/// <summary>
/// Direct byte-level checks of <see cref="AvifContainerWriter"/>'s <c>av1C</c> box output. This exists
/// specifically because <see cref="AvifContainerWriter.BuildAv1Config"/>'s own remarks note this repo's
/// <see cref="Avif.AvifDecoder"/> never actually reads <c>av1C</c>'s chroma-subsampling bits (it re-derives
/// subsampling from the AV1 bitstream's own sequence header instead) -- so a pixel-level round-trip test
/// could never catch a regression here, even though the bits still matter for spec conformance and any
/// other tool that trusts <c>av1C</c> for fast subsampling probing without a full bitstream parse.
/// </summary>
public class AvifContainerWriterTests
{
    [Fact]
    public void Lossless_ColorItem_Av1Config_SignalsChroma444()
    {
        var config = EncodeAndParseColorAv1Config(CreateGradientImage(48, 32), new AvifEncoderOptions { Lossless = true });

        Assert.False(config.Monochrome);
        Assert.False(config.ChromaSubsamplingX);
        Assert.False(config.ChromaSubsamplingY);
    }

    [Fact]
    public void Lossy_ColorItem_Av1Config_StillSignals420()
    {
        var config = EncodeAndParseColorAv1Config(CreateGradientImage(48, 32), new AvifEncoderOptions { Quality = 80 });

        Assert.False(config.Monochrome);
        Assert.True(config.ChromaSubsamplingX);
        Assert.True(config.ChromaSubsamplingY);
    }

    private static AvifAv1Config EncodeAndParseColorAv1Config(Image source, AvifEncoderOptions options)
    {
        using var ms = new MemoryStream();
        source.Save(ms, "avif", options);
        byte[] data = ms.ToArray();

        int av1CIndex = IndexOfFourCc(data, "av1C");
        Assert.True(av1CIndex >= 0, "no 'av1C' box found in the encoded output");

        // av1CIndex points at the fourCC; the box's size field is the 4 bytes immediately before it, and the
        // payload starts immediately after it (av1C is a plain Box, not a FullBox -- no version/flags prefix).
        int payloadOffset = av1CIndex + 4;
        int size = (data[av1CIndex - 4] << 24) | (data[av1CIndex - 3] << 16) | (data[av1CIndex - 2] << 8) | data[av1CIndex - 1];
        int payloadLength = size - 8;

        var box = new AvifBox("av1C", payloadOffset, payloadLength);
        return AvifAv1ConfigBox.Parse(data, box);
    }

    private static int IndexOfFourCc(byte[] data, string fourCc)
    {
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(fourCc);
        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static Image CreateGradientImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                pixels[idx + 0] = (byte)(width <= 1 ? 0 : col * 255 / (width - 1));
                pixels[idx + 1] = (byte)(height <= 1 ? 0 : row * 255 / (height - 1));
                pixels[idx + 2] = 128;
            }
        }

        return image;
    }
}
