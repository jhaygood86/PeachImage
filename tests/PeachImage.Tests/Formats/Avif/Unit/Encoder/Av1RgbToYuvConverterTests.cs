using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1RgbToYuvConverter"/> by round-tripping through the existing, already-correct
/// <see cref="Av1YuvToRgbConverter"/> (the decode-direction converter this mirrors).
/// </summary>
public class Av1RgbToYuvConverterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(37)]
    public void ConvertMonoChrome_PassesThroughGrayValuesExactly(byte gray)
    {
        byte[] source = new byte[16];
        Array.Fill(source, gray);

        int[] y = Av1RgbToYuvConverter.ConvertMonoChrome(source, 4, 4);

        Assert.All(y, value => Assert.Equal(gray, value));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(255, 0, 0)]
    [InlineData(0, 255, 0)]
    [InlineData(0, 0, 255)]
    [InlineData(128, 128, 128)]
    [InlineData(200, 100, 50)]
    public void Convert_SolidColor_RoundTripsThroughDecoderWithinRoundingTolerance(byte r, byte g, byte b)
    {
        const int width = 8;
        const int height = 8;
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            rgb[(i * 3) + 0] = r;
            rgb[(i * 3) + 1] = g;
            rgb[(i * 3) + 2] = b;
        }

        var (y, u, v, chromaWidth, chromaHeight) = Av1RgbToYuvConverter.Convert(rgb, width, height);

        byte[] decoded = Av1YuvToRgbConverter.Convert(
            colorPlanes: [y, u, v],
            colorWidths: [width, chromaWidth, chromaHeight],
            monoChrome: false,
            subsamplingX: true,
            subsamplingY: true,
            matrixCoefficients: Av1RgbToYuvConverter_TestMatrixCoefficients,
            colorRangeFull: true,
            bitDepth: 8,
            alphaPlane: null,
            alphaWidth: 0,
            outWidth: width,
            outHeight: height);

        for (int i = 0; i < width * height; i++)
        {
            Assert.InRange(decoded[(i * 3) + 0], Math.Max(0, r - 2), Math.Min(255, r + 2));
            Assert.InRange(decoded[(i * 3) + 1], Math.Max(0, g - 2), Math.Min(255, g + 2));
            Assert.InRange(decoded[(i * 3) + 2], Math.Max(0, b - 2), Math.Min(255, b + 2));
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 5)]
    [InlineData(5, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 9)]
    public void Convert_OddDimensions_ProducesCeilHalfChromaPlanes(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        new Random(42).NextBytes(rgb);

        var (y, u, v, chromaWidth, chromaHeight) = Av1RgbToYuvConverter.Convert(rgb, width, height);

        Assert.Equal(width * height, y.Length);
        Assert.Equal((width + 1) / 2, chromaWidth);
        Assert.Equal((height + 1) / 2, chromaHeight);
        Assert.Equal(chromaWidth * chromaHeight, u.Length);
        Assert.Equal(chromaWidth * chromaHeight, v.Length);
    }

    [Fact]
    public void Convert_SmoothGradient_RoundTripsWithinLooseTolerance()
    {
        const int width = 32;
        const int height = 32;
        byte[] rgb = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                rgb[idx + 0] = (byte)(col * 255 / (width - 1));
                rgb[idx + 1] = (byte)(row * 255 / (height - 1));
                rgb[idx + 2] = 128;
            }
        }

        var (y, u, v, chromaWidth, chromaHeight) = Av1RgbToYuvConverter.Convert(rgb, width, height);

        byte[] decoded = Av1YuvToRgbConverter.Convert(
            colorPlanes: [y, u, v],
            colorWidths: [width, chromaWidth, chromaHeight],
            monoChrome: false,
            subsamplingX: true,
            subsamplingY: true,
            matrixCoefficients: Av1RgbToYuvConverter_TestMatrixCoefficients,
            colorRangeFull: true,
            bitDepth: 8,
            alphaPlane: null,
            alphaWidth: 0,
            outWidth: width,
            outHeight: height);

        long sumSquaredError = 0;
        for (int i = 0; i < width * height * 3; i++)
        {
            int diff = decoded[i] - rgb[i];
            sumSquaredError += diff * diff;
        }

        double rmse = Math.Sqrt(sumSquaredError / (double)(width * height * 3));
        Assert.True(rmse < 5.0, $"RMSE {rmse} too high for a smooth gradient (chroma subsampling loss should be small here).");
    }

    // Matches Av1SequenceHeaderWriter.MatrixCoefficients (6, BT.601/SMPTE170M) -- duplicated as a literal
    // here rather than referenced, since this test is specifically pinning Av1RgbToYuvConverter's own
    // hard-coded Kr/Kb constants against the decoder's GetCoefficients(6) branch, not re-deriving them from
    // whatever the sequence header writer happens to select.
    private const int Av1RgbToYuvConverter_TestMatrixCoefficients = 6;
}
