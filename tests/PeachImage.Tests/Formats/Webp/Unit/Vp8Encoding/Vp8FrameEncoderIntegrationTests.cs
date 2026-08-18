using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding;
using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Encodes synthetic images directly via <see cref="Vp8ImageEncoder"/>, decodes the result with the real,
/// unmodified <see cref="Vp8FrameDecoder"/>, and asserts PSNR against the source -- validating that the full
/// mode-decision/forward-transform/quantize/reconstruct/entropy-code pipeline actually reproduces image content,
/// not just that it produces a bitstream the decoder can open without throwing. Thresholds are calibrated
/// against this encoder's own actual current output (run once, observe, set a safety margin below it), the same
/// discipline the AVIF lossy round-trip tests use -- not guessed up front.
/// </summary>
public class Vp8FrameEncoderIntegrationTests
{
    private static double AssertPsnrAtLeast(byte[] sourceRgb, int width, int height, double minPsnrDb)
    {
        byte[] vp8Bytes = Vp8ImageEncoder.Encode(sourceRgb, width, height, new WebpEncoderOptions { Lossless = false });
        Vp8DecodedFrame frame = Vp8FrameDecoder.Instance.Decode(vp8Bytes, null);

        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);

        double psnr = ComputePsnr(sourceRgb, frame.Pixels);
        Assert.True(psnr >= minPsnrDb, $"Expected PSNR >= {minPsnrDb} dB, got {psnr:F2} dB.");
        return psnr;
    }

    private static double ComputePsnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        double sumSquaredError = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int diff = a[i] - b[i];
            sumSquaredError += diff * diff;
        }

        if (sumSquaredError == 0)
        {
            return double.PositiveInfinity;
        }

        double mse = sumSquaredError / a.Length;
        return 10 * Math.Log10((255.0 * 255.0) / mse);
    }

    [Fact]
    public void Encode_SolidColorBlock_ReconstructsAtHighFidelity()
    {
        const int width = 32;
        const int height = 32;
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            rgb[(i * 3) + 0] = 180;
            rgb[(i * 3) + 1] = 90;
            rgb[(i * 3) + 2] = 40;
        }

        AssertPsnrAtLeast(rgb, width, height, 30.0);
    }

    [Fact]
    public void Encode_SmoothGradient_ReconstructsWithReasonableFidelity()
    {
        const int width = 64;
        const int height = 48;
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                rgb[(i * 3) + 0] = (byte)((x * 255) / width);
                rgb[(i * 3) + 1] = (byte)((y * 255) / height);
                rgb[(i * 3) + 2] = 128;
            }
        }

        AssertPsnrAtLeast(rgb, width, height, 25.0);
    }

    [Fact]
    public void Encode_Checkerboard_ReconstructsWithSomeFidelity()
    {
        const int width = 32;
        const int height = 32;
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                byte v = ((x / 4) + (y / 4)) % 2 == 0 ? (byte)230 : (byte)20;
                rgb[(i * 3) + 0] = v;
                rgb[(i * 3) + 1] = v;
                rgb[(i * 3) + 2] = v;
            }
        }

        AssertPsnrAtLeast(rgb, width, height, 15.0);
    }

    [Fact]
    public void Encode_Grayscale_ReconstructsWithReasonableFidelity()
    {
        const int width = 32;
        const int height = 32;
        byte[] rgb = new byte[width * height * 3];
        var random = new Random(7);
        for (int i = 0; i < width * height; i++)
        {
            byte v = (byte)(128 + random.Next(-20, 20));
            rgb[(i * 3) + 0] = v;
            rgb[(i * 3) + 1] = v;
            rgb[(i * 3) + 2] = v;
        }

        // Measured ~27.7 dB at this quant level; asserted with a small margin below that, not guessed up front.
        AssertPsnrAtLeast(rgb, width, height, 25.0);
    }

    [Fact]
    public void Encode_NonMacroblockAlignedDimensions_StillReconstructsReasonably()
    {
        const int width = 37;
        const int height = 21;
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                rgb[(i * 3) + 0] = (byte)(50 + x);
                rgb[(i * 3) + 1] = (byte)(50 + y);
                rgb[(i * 3) + 2] = 100;
            }
        }

        AssertPsnrAtLeast(rgb, width, height, 22.0);
    }
}
