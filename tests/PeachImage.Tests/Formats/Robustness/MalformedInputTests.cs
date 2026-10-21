using PeachImage.Formats.Ccitt;
using PeachImage.Formats.Jbig2;

namespace PeachImage.Tests.Formats.Robustness;

/// <summary>Corrupts valid JPEG 2000, JBIG2 and CCITT data and checks the decoders either succeed or fail with an <see cref="ImageFormatException"/> — never another exception, never a hang.</summary>
public class MalformedInputTests
{
    [Theory]
    [InlineData("Jpeg2000", "rgb_lossless.jp2")]
    [InlineData("Jpeg2000", "rgb_lossy_layers.jp2")]
    [InlineData("Jpeg2000", "rgb_tiles.jp2")]
    [InlineData("Jpeg2000", "mode_all_rgb.j2k")]
    [InlineData("Jpeg2000", "box_palette.jp2")]
    [InlineData("Jbig2", "text_refined_t0.jb2")]
    [InlineData("Jbig2", "halftone_arith.jb2")]
    [InlineData("Jbig2", "huffman_basic.jb2")]
    [InlineData("Jbig2", "refagg_t0.jb2")]
    [InlineData("Jbig2", "generic_t0_tpgd.jb2")]
    public void CorruptedFiles_FailCleanly(string format, string file)
    {
        byte[] original = Read($"PeachImage.Tests.Formats.{format}.Assets.{file}");
        var random = new Random(12345);
        for (int attempt = 0; attempt < 150; attempt++)
        {
            byte[] data = (byte[])original.Clone();
            int edits = 1 + random.Next(4);
            for (int e = 0; e < edits; e++)
            {
                int index = random.Next(data.Length);
                data[index] = (byte)random.Next(256);
            }

            if (attempt % 5 == 0)
            {
                data = data.AsSpan(0, random.Next(data.Length)).ToArray();
            }

            try
            {
                using var image = Image.Load(data);
                Assert.True(image.Width > 0);
            }
            catch (ImageFormatException)
            {
            }
        }
    }

    [Fact]
    public void CorruptedFaxStreams_FailCleanly()
    {
        byte[] original = Read("PeachImage.Tests.Formats.Tiff.Assets.Ccitt.similar_g4.raw");
        var random = new Random(777);
        for (int attempt = 0; attempt < 300; attempt++)
        {
            byte[] data = (byte[])original.Clone();
            for (int e = 0; e < 3; e++)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            try
            {
                using var image = Image.Load(new MemoryStream(data), new CcittFaxDecoderOptions { K = -1, Columns = 1000, Rows = 0, DamagedRowsBeforeError = attempt % 3 });
                Assert.True(image.Width > 0);
            }
            catch (CcittFaxFormatException)
            {
            }
        }
    }

    [Fact]
    public void EmbeddedJbig2_WithGarbageGlobals_FailsCleanly()
    {
        byte[] page = Read("PeachImage.Tests.Formats.Jbig2.Assets.text_topleft.seg");
        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(new MemoryStream(page), new Jbig2DecoderOptions { Embedded = true, Globals = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 } }));
    }

    private static byte[] Read(string name)
    {
        using var stream = typeof(MalformedInputTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
