using System.Diagnostics;
using System.Runtime.InteropServices;
using PeachImage.Formats.Jxl;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>Checks that the color encodings and half-float samples PeachImage writes mean the same thing to libjxl (through ffmpeg). Skipped without ffmpeg built with libjxl.</summary>
public class JxlHdrSignallingOracleTests
{
    private static bool Available
    {
        get
        {
            try
            {
                string decoders = System.Text.Encoding.UTF8.GetString(Run("ffmpeg", "-hide_banner", "-decoders"));
                return decoders.Contains("libjxl", StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static byte[] Run(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        string error = process.StandardError.ReadToEnd();
        copy.Wait();
        process.WaitForExit(30_000);
        Assert.True(process.ExitCode == 0, $"{tool} failed: {error}");
        return output.ToArray();
    }

    [Theory]
    [InlineData(NamedColorSpace.Rec2100Pq, "smpte2084", "bt2020")]
    [InlineData(NamedColorSpace.Rec2100Hlg, "arib-std-b67", "bt2020")]
    [InlineData(NamedColorSpace.DisplayP3, "iec61966-2-1", "smpte432")]
    [InlineData(NamedColorSpace.LinearSrgb, "linear", "bt709")]
    public void ColorEncoding_IsUnderstoodByLibjxl(NamedColorSpace space, string transfer, string primaries)
    {
        Assert.SkipUnless(Available, "ffmpeg with libjxl is not available.");
        using var image = Image.Create(16, 16, PixelFormat.Rgb48);
        image.Metadata.Cicp = CicpCode.Of(space);
        string path = Path.Combine(Path.GetTempPath(), $"peachimage-jxl-{Guid.NewGuid():N}.jxl");
        try
        {
            image.Save(path, new JxlEncoderOptions { Lossless = true });
            string probe = System.Text.Encoding.UTF8.GetString(Run("ffprobe", "-v", "error", "-show_streams", path)).Replace("\r", string.Empty, StringComparison.Ordinal);
            Assert.Contains($"color_transfer={transfer}", probe, StringComparison.Ordinal);
            Assert.Contains($"color_primaries={primaries}", probe, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(NamedColorSpace.DisplayP3)]
    [InlineData(NamedColorSpace.Rec2020)]
    [InlineData(NamedColorSpace.Rec2100Pq)]
    [InlineData(NamedColorSpace.Rec2100Hlg)]
    public void LossyWideGamutAndHdr_DecodesTheSameInLibjxl(NamedColorSpace space)
    {
        Assert.SkipUnless(Available, "ffmpeg with libjxl is not available.");
        const int width = 64, height = 48;
        using var scene = Image.Create(width, height, PixelFormat.RgbF32);
        var f = MemoryMarshal.Cast<byte, float>(scene.GetPixelSpan());
        for (int i = 0; i < width * height; i++)
        {
            float t = (i % width) / (float)(width - 1);
            float u = (i / width) / (float)(height - 1);
            f[i * 3] = 0.03f + (0.25f * t * u);
            f[(i * 3) + 1] = 0.3f + (0.6f * (1 - t));
            f[(i * 3) + 2] = 0.04f + (0.3f * u);
        }

        // Linear Rec. 2020 light (up to SDR white for the SDR spaces; the HDR ones are given highlights above it) in the space under test, as 16-bit.
        bool hdr = space is NamedColorSpace.Rec2100Pq or NamedColorSpace.Rec2100Hlg;
        if (hdr)
        {
            for (int i = 0; i < f.Length; i++)
            {
                f[i] *= 3f;
            }
        }

        scene.Metadata.Cicp = CicpCode.Of(NamedColorSpace.LinearRec2020);
        using var encoded = scene.ConvertColorSpace(space, new ColorConversionOptions { TargetPixelFormat = PixelFormat.Rgb48 });

        string path = Path.Combine(Path.GetTempPath(), $"peachimage-jxl-{Guid.NewGuid():N}.jxl");
        try
        {
            encoded.Save(path, new JxlEncoderOptions { Quality = 96 });
            using var ours = Image.Load(path);
            var raw = Run("ffmpeg", "-v", "error", "-i", path, "-pix_fmt", "rgb48le", "-f", "rawvideo", "-");
            var reference = MemoryMarshal.Cast<byte, ushort>(raw);
            var mine = MemoryMarshal.Cast<byte, ushort>(ours.GetPixelSpan());
            Assert.Equal(mine.Length, reference.Length);
            double sum = 0;
            for (int i = 0; i < mine.Length; i++)
            {
                double d = (mine[i] - reference[i]) / 65535.0;
                sum += d * d;
            }

            double psnr = 10 * Math.Log10(1.0 / (sum / mine.Length));
            Assert.True(psnr > 40, $"{space}: PeachImage and libjxl decode the same lossy file {psnr:F1} dB apart");

            // And the file is faithful to the source in the declared space, not just self-consistent.
            var source = MemoryMarshal.Cast<byte, ushort>(encoded.GetPixelSpan());
            double faithful = 0;
            for (int i = 0; i < source.Length; i++)
            {
                double d = (source[i] - reference[i]) / 65535.0;
                faithful += d * d;
            }

            Assert.True(10 * Math.Log10(1.0 / (faithful / source.Length)) > 30, $"{space}: libjxl's decode is not close to the source");
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Theory]
    [InlineData(NamedColorSpace.Rec2100Pq, "smpte2084")]
    [InlineData(NamedColorSpace.Rec2100Hlg, "arib-std-b67")]
    [InlineData(NamedColorSpace.LinearSrgb, "linear")]
    public void GrayTransferFunction_IsUnderstoodByLibjxl_LosslessAndLossy(NamedColorSpace space, string transfer)
    {
        Assert.SkipUnless(Available, "ffmpeg with libjxl is not available.");
        using var image = Image.Create(32, 32, PixelFormat.GrayF32);
        var f = MemoryMarshal.Cast<byte, float>(image.GetPixelSpan());
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = 0.1f + ((i % 32) / 64f);
        }

        image.Metadata.Cicp = CicpCode.Of(space);
        foreach (bool lossless in new[] { true, false })
        {
            string path = Path.Combine(Path.GetTempPath(), $"peachimage-jxl-{Guid.NewGuid():N}.jxl");
            try
            {
                image.Save(path, new JxlEncoderOptions { Lossless = lossless, Quality = 95 });
                string probe = System.Text.Encoding.UTF8.GetString(Run("ffprobe", "-v", "error", "-show_streams", path)).Replace("\r", string.Empty, StringComparison.Ordinal);
                Assert.Contains($"color_transfer={transfer}", probe, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }


    [Fact]
    public void HalfFloatLossless_DecodesToTheSameValuesInLibjxl()
    {
        Assert.SkipUnless(Available, "ffmpeg with libjxl is not available.");
        const int width = 20, height = 12;
        using var image = Image.Create(width, height, PixelFormat.RgbaF16);
        var h = MemoryMarshal.Cast<byte, Half>(image.GetPixelSpan());
        for (int i = 0; i < width * height; i++)
        {
            h[i * 4] = (Half)(i * 5f / (width * height));
            h[(i * 4) + 1] = (Half)(0.5f);
            h[(i * 4) + 2] = (Half)(0.003f * (i % 31));
            h[(i * 4) + 3] = (Half)1f;
        }

        string path = Path.Combine(Path.GetTempPath(), $"peachimage-jxl-{Guid.NewGuid():N}.jxl");
        try
        {
            image.Save(path, new JxlEncoderOptions { Lossless = true });
            var raw = Run("ffmpeg", "-v", "error", "-i", path, "-pix_fmt", "rgbf32le", "-f", "rawvideo", "-");
            var reference = MemoryMarshal.Cast<byte, float>(raw);
            Assert.Equal(width * height * 3, reference.Length); // the opaque alpha is not stored, so libjxl reports RGB
            for (int i = 0; i < reference.Length; i++)
            {
                Assert.True(reference[i] == (float)h[((i / 3) * 4) + (i % 3)], $"sample {i}: libjxl {reference[i]}, source {(float)h[((i / 3) * 4) + (i % 3)]}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
