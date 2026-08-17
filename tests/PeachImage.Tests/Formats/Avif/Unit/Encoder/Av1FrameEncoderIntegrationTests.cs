using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// The v1 acceptance gate: encodes a real image end to end through <see cref="Av1FrameEncoder"/>, then
/// decodes the resulting OBU stream through the existing, <em>unmodified</em>
/// <see cref="Av1FrameDecoder"/>/<see cref="Av1YuvToRgbConverter"/> pipeline -- the strongest available
/// correctness signal, since this repo's own AV1 decoder is already validated independently of anything
/// built for encoding.
/// </summary>
public class Av1FrameEncoderIntegrationTests
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(16, 16)]
    [InlineData(50, 40)]
    [InlineData(8, 8)]
    public void Encode_SolidColorImage_DecodesBackViaRealDecoder(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            rgb[(i * 3) + 0] = 180;
            rgb[(i * 3) + 1] = 90;
            rgb[(i * 3) + 2] = 40;
        }

        var encoded = Av1FrameEncoder.Encode(rgb, width, height, monoChrome: false, quality: 80);

        Assert.Equal(width, encoded.Width);
        Assert.Equal(height, encoded.Height);
        Assert.False(encoded.MonoChrome);

        var decoded = Av1FrameDecoder.Decode(encoded.ObuBytes);
        Assert.True(decoded.BlocksDecoded > 0);

        byte[] decodedRgb = Av1YuvToRgbConverter.Convert(
            decoded.Planes, decoded.PlaneWidths, monoChrome: false,
            subsamplingX: true, subsamplingY: true,
            matrixCoefficients: Av1SequenceHeaderWriter.MatrixCoefficients,
            colorRangeFull: Av1SequenceHeaderWriter.ColorRangeFull,
            bitDepth: 8, alphaPlane: null, alphaWidth: 0,
            outWidth: width, outHeight: height);

        AssertPsnrAtLeast(rgb, decodedRgb, width, height, 3, minPsnrDb: 30.0);
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(50, 40)]
    public void Encode_GradientImage_DecodesBackViaRealDecoderWithinReasonableFidelity(int width, int height)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                rgb[idx + 0] = (byte)(width <= 1 ? 0 : col * 255 / (width - 1));
                rgb[idx + 1] = (byte)(height <= 1 ? 0 : row * 255 / (height - 1));
                rgb[idx + 2] = 128;
            }
        }

        var encoded = Av1FrameEncoder.Encode(rgb, width, height, monoChrome: false, quality: 90);
        var decoded = Av1FrameDecoder.Decode(encoded.ObuBytes);

        byte[] decodedRgb = Av1YuvToRgbConverter.Convert(
            decoded.Planes, decoded.PlaneWidths, monoChrome: false,
            subsamplingX: true, subsamplingY: true,
            matrixCoefficients: Av1SequenceHeaderWriter.MatrixCoefficients,
            colorRangeFull: Av1SequenceHeaderWriter.ColorRangeFull,
            bitDepth: 8, alphaPlane: null, alphaWidth: 0,
            outWidth: width, outHeight: height);

        AssertPsnrAtLeast(rgb, decodedRgb, width, height, 3, minPsnrDb: 20.0);
    }

    [Fact]
    public void Encode_MonoChromeImage_DecodesBackViaRealDecoder()
    {
        const int width = 32;
        const int height = 32;
        byte[] gray = new byte[width * height];
        for (int i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)((i * 7) % 256);
        }

        var encoded = Av1FrameEncoder.Encode(gray, width, height, monoChrome: true, quality: 85);
        Assert.True(encoded.MonoChrome);

        var decoded = Av1FrameDecoder.Decode(encoded.ObuBytes);
        Assert.True(decoded.Sequence.MonoChrome);
        Assert.True(decoded.BlocksDecoded > 0);

        int[] yPlane = decoded.Planes[0];
        int stride = decoded.PlaneWidths[0];

        long sumSquaredError = 0;
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int diff = yPlane[(row * stride) + col] - gray[(row * width) + col];
                sumSquaredError += diff * diff;
            }
        }

        double mse = sumSquaredError / (double)(width * height);
        double psnr = mse <= 0 ? 100.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);
        Assert.True(psnr > 25.0, $"Monochrome PSNR {psnr:F2} dB too low.");
    }

    [Fact]
    public void Encode_DifferentQualityLevels_ProduceDifferentSizes()
    {
        const int width = 64;
        const int height = 64;
        var random = new Random(42);
        byte[] rgb = new byte[width * height * 3];
        random.NextBytes(rgb);

        var high = Av1FrameEncoder.Encode(rgb, width, height, monoChrome: false, quality: 95);
        var low = Av1FrameEncoder.Encode(rgb, width, height, monoChrome: false, quality: 10);

        Assert.NotEqual(high.ObuBytes.Length, low.ObuBytes.Length);
    }

    private static void AssertPsnrAtLeast(byte[] expectedRgb, byte[] actualRgb, int width, int height, int channels, double minPsnrDb)
    {
        Assert.Equal(expectedRgb.Length, actualRgb.Length);

        long sumSquaredError = 0;
        for (int i = 0; i < width * height * channels; i++)
        {
            int diff = expectedRgb[i] - actualRgb[i];
            sumSquaredError += diff * diff;
        }

        double mse = sumSquaredError / (double)(width * height * channels);
        double psnr = mse <= 0 ? 100.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);

        Assert.True(psnr >= minPsnrDb, $"PSNR {psnr:F2} dB below required {minPsnrDb} dB (MSE {mse:F2}).");
    }
}
