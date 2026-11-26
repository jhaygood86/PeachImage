using System.Diagnostics;
using System.Runtime.InteropServices;
using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>
/// Checks PeachImage's 10- and 12-bit AVIF decoder and encoder against an independent decoder (ffmpeg with libdav1d). Skipped where ffmpeg is not on PATH.
/// </summary>
public class AvifHighBitDepthFfmpegTests
{
    internal static bool Available => Probe("ffmpeg") && Probe("ffprobe");

    private static bool Probe(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            process!.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    internal static byte[] Run(string tool, params string[] args)
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

    private static string Temp(string extension) => Path.Combine(Path.GetTempPath(), $"peachimage-hbd-{Guid.NewGuid():N}{extension}");

    private static double Psnr16(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = (a[i] - b[i]) / 65535.0;
            sum += d * d;
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(1.0 / (sum / a.Length));
    }

    [Fact]
    public void TwelveBitCorpusFile_DecodesLikeLibdav1d()
    {
        Assert.SkipUnless(Available, "ffmpeg is not available.");
        Assert.SkipUnless(CorpusFixture.IsAvailable, "AVIF corpus is not available.");
        string path = Path.Combine(CorpusPaths.LibavifTestDataRoot, "weld_sato_12B_8B_q0.avif");
        Assert.SkipUnless(File.Exists(path), "The 12-bit corpus file is not present.");

        using var ours = Image.Load(path);
        Assert.Equal(PixelFormat.Rgb48, ours.PixelFormat);

        var reference = Run("ffmpeg", "-v", "error", "-i", path, "-pix_fmt", "rgb48le", "-f", "rawvideo", "-");
        Assert.Equal(ours.GetPixelSpan().Length, reference.Length);
        double psnr = Psnr16(MemoryMarshal.Cast<byte, ushort>(ours.GetPixelSpan()), MemoryMarshal.Cast<byte, ushort>(reference));
        Assert.True(psnr > 45, $"12-bit decode differs from libdav1d: {psnr:F1} dB");
    }

    private static Image Gradient(PixelFormat format, int bitDepth, int width, int height, bool smooth = false)
    {
        int channels = format.GetChannelCount();
        var image = Image.Create(width, height, format);
        var samples = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        int max = (1 << bitDepth) - 1;
        var random = new Random(bitDepth);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    int v = c switch { 0 => x * max / (width - 1), 1 => y * max / (height - 1), _ => smooth ? (max / 2) + ((x + y) * max / (4 * (width + height))) : ((x / 5) + (y / 7)) % 2 == 0 ? max * 3 / 4 : max / 6 };
                    v = Math.Clamp(v + (smooth ? 0 : random.Next(-9, 10)), 0, max);
                    samples[(((y * width) + x) * channels) + c] = (ushort)(((v * 65535L) + (max / 2)) / max);
                }
            }
        }

        return image;
    }

    [Theory]
    [InlineData(10, "gbrp10le")]
    [InlineData(12, "gbrp12le")]
    public void LosslessStream_DecodesToTheSameSamplesInLibdav1d(int bitDepth, string planarFormat)
    {
        Assert.SkipUnless(Available, "ffmpeg is not available.");
        const int width = 83, height = 47;
        using var source = Gradient(PixelFormat.Rgb48, bitDepth, width, height);
        string avif = Temp(".avif");
        try
        {
            source.Save(avif, new AvifEncoderOptions { Lossless = true, BitDepth = bitDepth, Effort = 0 });
            var raw = Run("ffmpeg", "-v", "error", "-i", avif, "-pix_fmt", planarFormat, "-f", "rawvideo", "-");

            // gbrp: three planes in the order G, B, R, each width*height 16-bit samples holding the bitDepth-bit value.
            var planes = MemoryMarshal.Cast<byte, ushort>(raw);
            var expected = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
            int max = (1 << bitDepth) - 1;
            int plane = width * height;
            Assert.Equal(3 * plane, planes.Length);
            for (int i = 0; i < plane; i++)
            {
                int r = (int)(((expected[i * 3] * (long)max) + 32767) / 65535);
                int g = (int)(((expected[(i * 3) + 1] * (long)max) + 32767) / 65535);
                int b = (int)(((expected[(i * 3) + 2] * (long)max) + 32767) / 65535);
                Assert.True(planes[i] == g && planes[plane + i] == b && planes[(2 * plane) + i] == r, $"pixel {i}: ffmpeg G{planes[i]} B{planes[plane + i]} R{planes[(2 * plane) + i]}, source R{r} G{g} B{b}");
            }
        }
        finally
        {
            File.Delete(avif);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void LossyStream_DecodesLikeLibdav1d(int bitDepth)
    {
        Assert.SkipUnless(Available, "ffmpeg is not available.");
        using var source = Gradient(PixelFormat.Rgb48, bitDepth, 128, 64, smooth: true);
        string avif = Temp(".avif");
        try
        {
            source.Save(avif, new AvifEncoderOptions { Quality = 85, BitDepth = bitDepth });
            using var ours = Image.Load(avif);
            var reference = Run("ffmpeg", "-v", "error", "-i", avif, "-pix_fmt", "rgb48le", "-f", "rawvideo", "-");
            Assert.Equal(ours.GetPixelSpan().Length, reference.Length); // a frame already a multiple of the 64-pixel coded size, so no cropping differences
            double psnr = Psnr16(MemoryMarshal.Cast<byte, ushort>(ours.GetPixelSpan()), MemoryMarshal.Cast<byte, ushort>(reference));
            Assert.True(psnr > 45, $"{bitDepth}-bit lossy decode differs from libdav1d: {psnr:F1} dB");
            Assert.True(Psnr16(MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan()), MemoryMarshal.Cast<byte, ushort>(reference)) > 35);
        }
        finally
        {
            File.Delete(avif);
        }
    }

    [Theory]
    [InlineData(10, 1)]
    [InlineData(12, 3)]
    public void TiledLossyStream_DecodesLikeLibdav1d(int bitDepth, int tileRowsLog2)
    {
        Assert.SkipUnless(Available, "ffmpeg is not available.");
        using var source = Gradient(PixelFormat.Rgb48, bitDepth, 128, 512, smooth: true);
        string avif = Temp(".avif");
        try
        {
            source.Save(avif, new AvifEncoderOptions { Quality = 85, BitDepth = bitDepth, TileRowsLog2 = tileRowsLog2 });
            using var ours = Image.Load(avif);
            var reference = Run("ffmpeg", "-v", "error", "-i", avif, "-pix_fmt", "rgb48le", "-f", "rawvideo", "-");
            Assert.Equal(ours.GetPixelSpan().Length, reference.Length);
            double psnr = Psnr16(MemoryMarshal.Cast<byte, ushort>(ours.GetPixelSpan()), MemoryMarshal.Cast<byte, ushort>(reference));
            Assert.True(psnr > 45, $"{bitDepth}-bit, {1 << tileRowsLog2} tile rows: differs from libdav1d by {psnr:F1} dB");
        }
        finally
        {
            File.Delete(avif);
        }
    }

    [Theory]
    [InlineData(PixelFormat.Rgba64, 10)]
    [InlineData(PixelFormat.Gray16, 12)]
    public void LosslessAlphaAndGray_DecodeLikeLibdav1d(PixelFormat format, int bitDepth)
    {
        Assert.SkipUnless(Available, "ffmpeg is not available.");
        using var source = Gradient(format, bitDepth, 64, 48);
        string avif = Temp(".avif");
        try
        {
            source.Save(avif, new AvifEncoderOptions { Lossless = true, BitDepth = bitDepth, Effort = 2 });
            if (format == PixelFormat.Gray16)
            {
                var gray = Run("ffmpeg", "-v", "error", "-i", avif, "-pix_fmt", $"gray{bitDepth}le", "-f", "rawvideo", "-");
                var planes = MemoryMarshal.Cast<byte, ushort>(gray);
                var expected = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
                int max = (1 << bitDepth) - 1;
                Assert.Equal(expected.Length, planes.Length);
                for (int i = 0; i < expected.Length; i++)
                {
                    Assert.Equal((int)(((expected[i] * (long)max) + 32767) / 65535), planes[i]);
                }
            }
            else
            {
                // The color item is plain lossless RGB; the alpha item is a second monochrome stream which this decoder and libdav1d must agree on.
                using var ours = Image.Load(avif);
                Assert.Equal(source.GetPixelSpan().ToArray(), ours.GetPixelSpan().ToArray());
                var color = Run("ffmpeg", "-v", "error", "-i", avif, "-pix_fmt", $"gbrp{bitDepth}le", "-f", "rawvideo", "-");
                var planes = MemoryMarshal.Cast<byte, ushort>(color);
                var expected = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
                int max = (1 << bitDepth) - 1;
                int plane = source.Width * source.Height;
                for (int i = 0; i < plane; i++)
                {
                    Assert.Equal((int)(((expected[(i * 4) + 1] * (long)max) + 32767) / 65535), planes[i]);
                }
            }
        }
        finally
        {
            File.Delete(avif);
        }
    }

    [Fact]
    public void HdrSignalling_IsVisibleToFfprobe()
    {
        Assert.SkipUnless(Available, "ffmpeg is not available.");
        using var source = Gradient(PixelFormat.Rgb48, 10, 32, 32);
        source.Metadata.Cicp = CicpCode.Of(NamedColorSpace.Rec2100Pq);
        string avif = Temp(".avif");
        try
        {
            source.Save(avif, new AvifEncoderOptions { Quality = 80, BitDepth = 10 });
            string info = System.Text.Encoding.UTF8.GetString(Run("ffprobe", "-v", "error", "-show_streams", avif));
            Assert.Contains("color_transfer=smpte2084", info.Replace("\r", string.Empty, StringComparison.Ordinal));
            Assert.Contains("color_primaries=bt2020", info.Replace("\r", string.Empty, StringComparison.Ordinal));
            Assert.Contains("10le", info, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(avif);
        }

    }
}

public class AvifHighBitDepthScreenContentFfmpegTests
{
    [Theory]
    [InlineData(10, "gbrp10le")]
    [InlineData(12, "gbrp12le")]
    public void LosslessScreenContent_DecodesToTheSameSamplesInLibdav1d(int bitDepth, string planarFormat)
    {
        Assert.SkipUnless(AvifHighBitDepthFfmpegTests.Available, "ffmpeg is not available.");
        using var screen = PeachImage.Tests.Formats.Avif.RoundTrip.HighBitDepthScreenContentTests.Screen(bitDepth);
        string path = Path.Combine(Path.GetTempPath(), $"peachimage-sc-{Guid.NewGuid():N}.avif");
        try
        {
            screen.Save(path, new AvifEncoderOptions { Lossless = true, BitDepth = bitDepth, Effort = 2 });
            var raw = AvifHighBitDepthFfmpegTests.Run("ffmpeg", "-v", "error", "-i", path, "-pix_fmt", planarFormat, "-f", "rawvideo", "-");

            var planes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(raw);
            var expected = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(screen.GetPixelSpan());
            int max = (1 << bitDepth) - 1;
            int plane = screen.Width * screen.Height;
            Assert.Equal(3 * plane, planes.Length);
            for (int i = 0; i < plane; i++)
            {
                int r = (int)(((expected[i * 3] * (long)max) + 32767) / 65535);
                int g = (int)(((expected[(i * 3) + 1] * (long)max) + 32767) / 65535);
                int b = (int)(((expected[(i * 3) + 2] * (long)max) + 32767) / 65535);
                Assert.True(planes[i] == g && planes[plane + i] == b && planes[(2 * plane) + i] == r, $"pixel {i}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
