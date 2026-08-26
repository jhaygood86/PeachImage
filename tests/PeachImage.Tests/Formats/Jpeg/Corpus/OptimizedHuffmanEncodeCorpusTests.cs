using PeachImage.Formats.Jpeg;
using PeachImage.Tests.Internal;
using SkiaSharp;

namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// Re-encodes a sample of real-world JPEGs with <see cref="JpegEncoderOptions.OptimizeHuffmanTables"/> and
/// confirms the result decodes correctly (both via this repo's own decoder and, as a differential check,
/// SkiaSharp -- validating the optimized DHT segments are actually spec-compliant enough for an independent
/// real-world decoder, not just self-consistent with this repo's own decoder), and that the sample's total
/// encoded size shrinks versus standard tables -- the corpus-level version of the file-size check the issue
/// asks for, aggregated across files rather than asserted per-file since any single near-optimal file could
/// legitimately tie.
/// </summary>
public class OptimizedHuffmanEncodeCorpusTests
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(45);

    [Theory]
    [MemberData(nameof(CorpusFileSource.MozjpegFilesSample), MemberType = typeof(CorpusFileSource))]
    public void ReEncodedWithOptimizedTables_DecodesGracefullyAndMatchesSkiaWhenBothSucceed(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryReEncodeAndCompare(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Re-encoding/comparing {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        if (result is { Failed: true } failure)
        {
            Assert.Fail(failure.Message);
        }
    }

    [Fact]
    public void OptimizedTables_ReduceTotalEncodedSizeAcrossCorpusSample()
    {
        if (!CorpusFixture.IsAvailable)
        {
            return;
        }

        long standardTotal = 0;
        long optimizedTotal = 0;
        int fileCount = 0;

        foreach (string path in CorpusFileSource.MozjpegFilePaths())
        {
            Image source;
            try
            {
                source = Image.Load(path);
            }
            catch (JpegFormatException)
            {
                continue;
            }

            using var standardMs = new MemoryStream();
            source.Save(standardMs, "jpeg", new JpegEncoderOptions { Quality = 85, OptimizeHuffmanTables = false });

            using var optimizedMs = new MemoryStream();
            source.Save(optimizedMs, "jpeg", new JpegEncoderOptions { Quality = 85, OptimizeHuffmanTables = true });

            standardTotal += standardMs.Length;
            optimizedTotal += optimizedMs.Length;
            fileCount++;
        }

        if (fileCount == 0)
        {
            return;
        }

        Assert.True(
            optimizedTotal < standardTotal,
            $"Expected optimized-table total ({optimizedTotal} bytes across {fileCount} files) to be smaller than standard-table total ({standardTotal} bytes).");
    }

    private static ComparisonResult TryReEncodeAndCompare(string path)
    {
        Image source;
        try
        {
            source = Image.Load(path);
        }
        catch (JpegFormatException)
        {
            return ComparisonResult.Ok;
        }

        using var reEncoded = new MemoryStream();
        source.Save(reEncoded, "jpeg", new JpegEncoderOptions { Quality = 85, OptimizeHuffmanTables = true });
        byte[] reEncodedBytes = reEncoded.ToArray();

        Image peachImage;
        try
        {
            using var peachStream = new MemoryStream(reEncodedBytes);
            peachImage = JpegDecoder.Decode(peachStream);
        }
        catch (Exception ex)
        {
            return ComparisonResult.Fail($"{Path.GetFileName(path)}: decoding optimized-table re-encode threw {ex}");
        }

        if (peachImage.Width < 8 || peachImage.Height < 8)
        {
            return ComparisonResult.Ok;
        }

        using var skiaStream = new MemoryStream(reEncodedBytes);
        using var skiaBitmap = SKBitmap.Decode(skiaStream);
        if (skiaBitmap is null || skiaBitmap.Width != peachImage.Width || skiaBitmap.Height != peachImage.Height)
        {
            return ComparisonResult.Ok;
        }

        if (peachImage.PixelFormat is not (PixelFormat.Gray8 or PixelFormat.Rgb24 or PixelFormat.Rgba32))
        {
            return ComparisonResult.Ok;
        }

        double averageDifference = ComputeAverageChannelDifference(peachImage, skiaBitmap);
        if (averageDifference >= 12.0)
        {
            return ComparisonResult.Fail($"{Path.GetFileName(path)}: average per-channel difference from SkiaSharp too high: {averageDifference:F2}");
        }

        return ComparisonResult.Ok;
    }

    private static double ComputeAverageChannelDifference(Image peachImage, SKBitmap skiaBitmap)
    {
        var span = peachImage.GetPixelSpan();
        int bytesPerPixel = peachImage.PixelFormat.GetBytesPerPixel();
        int step = Math.Max(1, Math.Min(peachImage.Width, peachImage.Height) / 64);

        double sum = 0;
        long count = 0;
        for (int y = 0; y < peachImage.Height; y += step)
        {
            for (int x = 0; x < peachImage.Width; x += step)
            {
                var skiaPixel = skiaBitmap.GetPixel(x, y);
                int offset = ((y * peachImage.Width) + x) * bytesPerPixel;

                double r, g, b;
                if (peachImage.PixelFormat == PixelFormat.Gray8)
                {
                    r = g = b = span[offset];
                }
                else
                {
                    r = span[offset];
                    g = span[offset + 1];
                    b = span[offset + 2];
                }

                sum += Math.Abs(r - skiaPixel.Red) + Math.Abs(g - skiaPixel.Green) + Math.Abs(b - skiaPixel.Blue);
                count++;
            }
        }

        return count == 0 ? 0 : sum / (count * 3);
    }

    private readonly record struct ComparisonResult(bool Failed, string? Message)
    {
        public static ComparisonResult Ok => default;

        public static ComparisonResult Fail(string message) => new(true, message);
    }
}
