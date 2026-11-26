using System.Security.Cryptography;
using System.Text;
using PeachImage.Formats.Avif;
using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jxl;
using PeachImage.Formats.Png;
using PeachImage.Formats.Webp;

namespace PeachImage.Tests.Regression;

/// <summary>
/// Pins the exact bytes the existing 8-bit encode paths write, and the exact pixels the decoders and resizer produce from them, so
/// the color-managed and high-precision work cannot shift an 8-bit result. The hashes were captured before that work began; a
/// change here is a behavior change, not a refactor. Set <c>PEACH_PIN_PRINT=1</c> to print the current hashes instead of asserting.
/// </summary>
public class EightBitPinTests
{
    private static Image Source(PixelFormat format, int width = 67, int height = 41)
    {
        var image = Image.Create(width, height, format);
        int channels = format.GetChannelCount();
        var span = image.GetPixelSpan();
        uint state = 0x9E3779B9u;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    state = state * 1664525u + 1013904223u;
                    int noise = (int)(state >> 29);
                    int value = c switch
                    {
                        0 => x * 255 / (width - 1),
                        1 => y * 255 / (height - 1),
                        2 => ((x / 8) + (y / 8)) % 2 == 0 ? 220 : 40,
                        _ => 255 - (x + y) * 255 / (width + height),
                    };
                    span[((y * width) + x) * channels + c] = (byte)Math.Clamp(value + noise, 0, 255);
                }
            }
        }

        return image;
    }

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data))[..24];

    private static byte[] Encode(Image image, string format, EncoderOptions? options)
    {
        using var ms = new MemoryStream();
        image.Save(ms, format, options);
        return ms.ToArray();
    }

    private static IEnumerable<(string Name, Func<string> Compute)> Cases()
    {
        static string EncodeHash(PixelFormat pf, string fmt, EncoderOptions? o)
        {
            using var img = Source(pf);
            return Hash(Encode(img, fmt, o));
        }

        static string DecodeHash(PixelFormat pf, string fmt, EncoderOptions? o)
        {
            using var img = Source(pf);
            using var back = Image.Load(Encode(img, fmt, o));
            return Hash(back.GetPixelSpan()) + ":" + back.PixelFormat;
        }

        static string ResizeHash(PixelFormat pf, ResamplingFilter filter)
        {
            using var img = Source(pf);
            using var resized = img.Resize(31, 23, new ResizeOptions { Filter = filter });
            return Hash(resized.GetPixelSpan());
        }

        yield return ("png.rgb24", () => EncodeHash(PixelFormat.Rgb24, "png", null));
        yield return ("png.rgba32", () => EncodeHash(PixelFormat.Rgba32, "png", null));
        yield return ("png.gray8", () => EncodeHash(PixelFormat.Gray8, "png", null));
        yield return ("png.rgb24.interlaced", () => EncodeHash(PixelFormat.Rgb24, "png", new PngEncoderOptions { Interlace = true }));
        yield return ("jpeg.rgb24.420", () => EncodeHash(PixelFormat.Rgb24, "jpeg", null));
        yield return ("jpeg.rgb24.444.q90", () => EncodeHash(PixelFormat.Rgb24, "jpeg", new JpegEncoderOptions { Quality = 90, Subsampling = JpegChromaSubsampling.Yuv444 }));
        yield return ("jpeg.gray8", () => EncodeHash(PixelFormat.Gray8, "jpeg", null));
        yield return ("webp.lossless.rgba32", () => EncodeHash(PixelFormat.Rgba32, "webp", null));
        yield return ("webp.lossy.rgb24", () => EncodeHash(PixelFormat.Rgb24, "webp", new WebpEncoderOptions { Lossless = false, Quality = 80 }));
        yield return ("avif.lossless.rgb24", () => EncodeHash(PixelFormat.Rgb24, "avif", new AvifEncoderOptions { Lossless = true }));
        yield return ("avif.lossy.rgb24", () => EncodeHash(PixelFormat.Rgb24, "avif", new AvifEncoderOptions { Quality = 70 }));
        yield return ("jxl.lossless.rgb24", () => EncodeHash(PixelFormat.Rgb24, "jxl", new JxlEncoderOptions { Lossless = true }));
        yield return ("jxl.lossy.rgb24", () => EncodeHash(PixelFormat.Rgb24, "jxl", new JxlEncoderOptions { Quality = 80 }));
        yield return ("decode.png.rgba32", () => DecodeHash(PixelFormat.Rgba32, "png", null));
        yield return ("decode.jpeg.rgb24", () => DecodeHash(PixelFormat.Rgb24, "jpeg", null));
        yield return ("decode.webp.lossy", () => DecodeHash(PixelFormat.Rgb24, "webp", new WebpEncoderOptions { Lossless = false, Quality = 80 }));
        yield return ("decode.avif.lossy", () => DecodeHash(PixelFormat.Rgb24, "avif", new AvifEncoderOptions { Quality = 70 }));
        yield return ("decode.jxl.lossy", () => DecodeHash(PixelFormat.Rgb24, "jxl", new JxlEncoderOptions { Quality = 80 }));
        yield return ("resize.rgb24.bicubic", () => ResizeHash(PixelFormat.Rgb24, ResamplingFilter.Bicubic));
        yield return ("resize.rgba32.lanczos3", () => ResizeHash(PixelFormat.Rgba32, ResamplingFilter.Lanczos3));
        yield return ("resize.rgb24.nearest", () => ResizeHash(PixelFormat.Rgb24, ResamplingFilter.NearestNeighbor));
        yield return ("convert.rgb24.to.rgba32", () =>
        {
            using var img = Source(PixelFormat.Rgb24);
            using var c = img.ConvertTo(PixelFormat.Rgba32);
            return Hash(c.GetPixelSpan());
        });
    }

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["png.rgb24"] = "278995B5ECED9129CE0788A3",
        ["png.rgba32"] = "8B07247E891A0066295EC18F",
        ["png.gray8"] = "4004A351B912F8F37ABB5F7A",
        ["png.rgb24.interlaced"] = "ED7C04ECDC5676ACFFFB6761",
        ["jpeg.rgb24.420"] = "174669546511B5FD4301BACB",
        ["jpeg.rgb24.444.q90"] = "96448A4DB4C1C98C4F71ACB3",
        ["jpeg.gray8"] = "BE122D6C931070EE8E64DB46",
        ["webp.lossless.rgba32"] = "D486EA63353B315A55745A4D",
        ["webp.lossy.rgb24"] = "D389C7A31701C328D9833031",
        ["avif.lossless.rgb24"] = "41FB00FEC67863710C676D4E",
        ["avif.lossy.rgb24"] = "B9705AC9DC2E8B6100D718BC",
        ["jxl.lossless.rgb24"] = "BDEF3E396848C9DE3E4954A7",
        ["jxl.lossy.rgb24"] = "EEC8132CDD206E2A590DA1BD",
        ["decode.png.rgba32"] = "0A9E2648CCF057A20383B4DE:Rgba32",
        ["decode.jpeg.rgb24"] = "3C222EBD7895CAF62B1836DB:Rgb24",
        ["decode.webp.lossy"] = "2A1D45D7A8EDE4BE1070A835:Rgb24",
        ["decode.avif.lossy"] = "7E6048D09B383C0B7682FE36:Rgb24",
        ["decode.jxl.lossy"] = "E01566897347ACA31AA4D3CC:Rgb24",
        ["resize.rgb24.bicubic"] = "E7662CD92FE0B68360B6892C",
        ["resize.rgba32.lanczos3"] = "6DAD432D1F9719A937C6C4E6",
        ["resize.rgb24.nearest"] = "61DC505C657FFB58E38F03D1",
        ["convert.rgb24.to.rgba32"] = "D13E3032CBB79A0B633C72E8",
    };

    [Fact]
    public void ExistingEightBitOutputs_AreUnchanged()
    {
        var actual = Cases().ToDictionary(c => c.Name, c => c.Compute());

        if (Environment.GetEnvironmentVariable("PEACH_PIN_PRINT") == "1")
        {
            var sb = new StringBuilder();
            foreach (var (name, hash) in actual)
            {
                sb.Append("        [\"").Append(name).Append("\"] = \"").Append(hash).AppendLine("\",");
            }

            File.WriteAllText(Path.Combine(Path.GetTempPath(), "peach-pins.txt"), sb.ToString());
            return;
        }

        foreach (var (name, hash) in actual)
        {
            Assert.True(Expected.TryGetValue(name, out var expected), $"No pinned hash for {name} (actual {hash}).");
            Assert.True(expected == hash, $"{name}: pinned {expected}, actual {hash}.");
        }
    }
}
