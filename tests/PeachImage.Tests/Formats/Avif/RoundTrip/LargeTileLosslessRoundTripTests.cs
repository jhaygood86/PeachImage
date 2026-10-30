using PeachImage.Formats.Avif;

namespace PeachImage.Tests.Formats.Avif.RoundTrip;

/// <summary>
/// Tiles of at least 256x256 samples are decoded with parse and reconstruction on separate threads
/// (<c>Av1ReconPipeline</c>); smaller ones run inline, which is all the other round-trip tests reach. A
/// lossless round trip is exact, so any ordering or hand-off bug between the two threads shows up as a
/// pixel mismatch.
/// </summary>
public class LargeTileLosslessRoundTripTests
{
    [Theory]
    [InlineData(288, 288, 1)]
    [InlineData(333, 301, 2)]
    public void NoisyRgb24_RoundTripsExactly_WhenReconstructedOnWorkerThread(int width, int height, int seed)
    {
        var source = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = source.GetPixelSpan();
        var random = new Random(seed);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 3;
                int baseValue = (x / 3) + (y / 2);
                pixels[i] = (byte)(baseValue + random.Next(24));
                pixels[i + 1] = (byte)((baseValue * 2) + random.Next(24));
                pixels[i + 2] = (byte)((x ^ y) + random.Next(24));
            }
        }

        using var ms = new MemoryStream();
        source.Save(ms, "avif", new AvifEncoderOptions { Lossless = true });
        ms.Position = 0;
        var decoded = Image.Load(ms);

        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.True(source.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }
}
