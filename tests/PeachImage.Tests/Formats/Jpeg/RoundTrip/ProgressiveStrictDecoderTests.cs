using System.Diagnostics;
using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.RoundTrip;

/// <summary>
/// Progressive streams must be valid for strict decoders, not only for PeachImage's own decoder. Noisy content makes blocks with
/// long runs of zeros between significant coefficients, which is where the encoder once wrote zero-run symbols that no later
/// symbol followed: PeachImage and libjpeg-turbo read those streams, ffmpeg and libjxl rejected them. ffmpeg (when installed) is
/// the independent decoder here.
/// </summary>
public class ProgressiveStrictDecoderTests
{
    public static TheoryData<bool, JpegChromaSubsampling, int, int, int, bool, int> Cases() => new()
    {
        { true, JpegChromaSubsampling.Yuv420, 64, 64, 12, false, 0 },
        { true, JpegChromaSubsampling.Yuv444, 96, 64, 12, false, 0 },
        { true, JpegChromaSubsampling.Yuv422, 83, 61, 12, false, 2 },
        { true, JpegChromaSubsampling.Yuv420, 83, 61, 40, true, 0 },
        { false, JpegChromaSubsampling.Yuv420, 64, 64, 12, false, 0 },
        { false, JpegChromaSubsampling.Yuv420, 83, 61, 30, true, 3 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProgressiveOutput_IsAcceptedByAStrictDecoder_AndDecodesLikeBaseline(bool color, JpegChromaSubsampling subsampling, int width, int height, int noise, bool optimize, int restartInterval)
    {
        Assert.SkipUnless(FfmpegAvailable.Value, "ffmpeg is not available.");

        using var source = CreateNoisyImage(color, width, height, noise);
        var progressive = new JpegEncoderOptions { Quality = 90, Subsampling = subsampling, Progressive = true, OptimizeHuffmanTables = optimize, RestartInterval = restartInterval };
        var baseline = new JpegEncoderOptions { Quality = 90, Subsampling = subsampling, Progressive = false, RestartInterval = restartInterval };

        using var progressiveStream = new MemoryStream();
        source.Save(progressiveStream, "jpeg", progressive);
        using var baselineStream = new MemoryStream();
        source.Save(baselineStream, "jpeg", baseline);

        Assert.Equal(string.Empty, StrictDecodeErrors(progressiveStream.ToArray()));

        // The same quantized coefficients, so both decode to the very same pixels.
        progressiveStream.Position = 0;
        baselineStream.Position = 0;
        using var fromProgressive = Image.Load(progressiveStream);
        using var fromBaseline = Image.Load(baselineStream);
        Assert.True(fromProgressive.GetPixelSpan().SequenceEqual(fromBaseline.GetPixelSpan()));
    }

    private static readonly Lazy<bool> FfmpegAvailable = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            process!.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    });

    // What ffmpeg's strict JPEG decoder reports (stderr at the error level) while decoding the file; empty when it is happy.
    private static string StrictDecodeErrors(byte[] jpeg)
    {
        string path = Path.Combine(Path.GetTempPath(), $"peach-jpeg-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(path, jpeg);
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error -i \"{path}\" -f null -") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
            var output = process.StandardOutput.ReadToEndAsync();
            string errors = process.StandardError.ReadToEnd();
            output.Wait();
            process.WaitForExit();
            return errors.Trim();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Image CreateNoisyImage(bool color, int width, int height, int noise)
    {
        var image = Image.Create(width, height, color ? PixelFormat.Rgb24 : PixelFormat.Gray8);
        var random = new Random(5);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                double value = 128 + (60 * Math.Sin(x / 7.0)) + (50 * Math.Cos(y / 5.0));
                int n = random.Next(-noise, noise + 1);
                if (color)
                {
                    row[x * 3] = (byte)Math.Clamp((int)value + n, 0, 255);
                    row[(x * 3) + 1] = (byte)Math.Clamp((int)(255 - value) + n, 0, 255);
                    row[(x * 3) + 2] = (byte)Math.Clamp((x * 3) + n + (y % 17), 0, 255);
                }
                else
                {
                    row[x] = (byte)Math.Clamp((int)value + n, 0, 255);
                }
            }
        }

        return image;
    }
}
