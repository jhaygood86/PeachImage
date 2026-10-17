using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Jpeg;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>The JPEG parser keeps everything the reconstruction writer needs: parsing a JPEG and writing it back gives the same bytes.</summary>
public class JxlJpegParserTests
{
    public static TheoryData<string, int, int, JpegChromaSubsampling, int, bool, bool> Cases()
    {
        var data = new TheoryData<string, int, int, JpegChromaSubsampling, int, bool, bool>();
        foreach (bool gray in new[] { false, true })
        {
            foreach (var subsampling in gray ? new[] { JpegChromaSubsampling.Yuv444 } : new[] { JpegChromaSubsampling.Yuv444, JpegChromaSubsampling.Yuv422, JpegChromaSubsampling.Yuv420 })
            {
                foreach (int restart in new[] { 0, 5 })
                {
                    foreach (bool optimize in new[] { false, true })
                    {
                        data.Add($"{(gray ? "gray" : subsampling.ToString())} restart {restart} optimize {optimize}", 203, 147, subsampling, restart, optimize, gray);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ParseThenWrite_GivesTheSameBytes(string name, int width, int height, JpegChromaSubsampling subsampling, int restart, bool optimize, bool gray)
    {
        _ = name;
        byte[] jpeg = JxlJpegTestImages.Encode(width, height, gray, new JpegEncoderOptions { Quality = 80, Subsampling = subsampling, RestartInterval = restart, OptimizeHuffmanTables = optimize });

        var parsed = JxlJpegParser.Parse(jpeg);

        Assert.Equal(jpeg, JxlJpegWriter.Write(parsed.Data));
        Assert.Equal(width, parsed.Data.Width);
        Assert.Equal(height, parsed.Data.Height);
    }

    [Fact]
    public void ProgressiveJpeg_IsRejected()
    {
        byte[] jpeg = JxlJpegTestImages.Encode(64, 48, gray: false, new JpegEncoderOptions { Progressive = true });

        Assert.Throws<JxlUnsupportedFeatureException>(() => JxlJpegParser.Parse(jpeg));
    }

    [Fact]
    public void NotAJpeg_IsRejected()
    {
        Assert.Throws<JxlUnsupportedFeatureException>(() => JxlJpegParser.Parse([1, 2, 3, 4, 5, 6]));
    }
}

/// <summary>Deterministic photograph-like test pictures encoded as JPEG.</summary>
internal static class JxlJpegTestImages
{
    public static byte[] Encode(int width, int height, bool gray, JpegEncoderOptions options)
    {
        using var image = Create(width, height, gray);
        using var stream = new MemoryStream();
        image.Save(stream, "jpeg", options);
        return stream.ToArray();
    }

    public static Image Create(int width, int height, bool gray)
    {
        var image = Image.Create(width, height, gray ? PixelFormat.Gray8 : PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        var random = new Random(12345);
        int channels = gray ? 1 : 3;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    // Smooth colour fields, a few hard edges and a little noise: a mix of flat, textured and busy blocks.
                    double value = 128 + (70 * Math.Sin((x + (c * 17)) / 23.0) * Math.Cos((y - (c * 11)) / 31.0));
                    if (((x / 37) + (y / 29)) % 3 == 0)
                    {
                        value += 40;
                    }

                    value += random.Next(-6, 7);
                    pixels[(((y * width) + x) * channels) + c] = (byte)Math.Clamp((int)value, 0, 255);
                }
            }
        }

        return image;
    }
}
