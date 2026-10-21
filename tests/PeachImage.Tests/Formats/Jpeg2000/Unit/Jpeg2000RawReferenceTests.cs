namespace PeachImage.Tests.Formats.Jpeg2000.Unit;

/// <summary>
/// Files with unusual sample depths, signedness, component counts or sub-sampling, checked against opj_decompress's planar output
/// (each component at its own size; 1 byte per sample up to 8 bits, otherwise 2 bytes little endian; signed samples as two's complement).
/// Sub-sampled components are expected replicated up to the image grid.
/// </summary>
public class Jpeg2000RawReferenceTests
{
    private const string Prefix = "PeachImage.Tests.Formats.Jpeg2000.Assets.";

    public static TheoryData<string, int, int, bool, string> Cases() => new()
    {
        { "depth12_gray", 1, 12, false, "1x1" },
        { "depth12_rgb", 3, 12, false, "1x1,1x1,1x1" },
        { "depth16_gray", 1, 16, false, "1x1" },
        { "depth4_gray", 1, 4, false, "1x1" },
        { "depth1_gray", 1, 1, false, "1x1" },
        { "signed8_gray", 1, 8, true, "1x1" },
        { "signed12_gray_lossy", 1, 12, true, "1x1" },
        { "four_components", 4, 8, false, "1x1,1x1,1x1,1x1" },
        { "five_components", 5, 8, false, "1x1,1x1,1x1,1x1,1x1" },
        { "subsampled_420", 4, 8, false, "1x1,2x2,2x2,1x1" },
        { "subsampled_422_lossy", 4, 8, false, "1x1,2x1,2x1,1x1" },
        { "subsampled_odd", 4, 8, false, "1x1,3x2,2x3,1x1" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decode_MatchesOpenJpegPlanarOutput(string name, int components, int depth, bool hasSign, string subsampling)
    {
        byte[] reference = ReadAsset($"{name}.ref.raw");
        using var image = Image.Load(ReadAsset($"{name}.j2k"));

        int width = image.Width;
        int height = image.Height;
        int bytesPerSample = depth > 8 ? 2 : 1;
        string[] factors = subsampling.Split(',');
        var planeOffsets = new int[components];
        var planeWidths = new int[components];
        var steps = new (int X, int Y)[components];
        int total = 0;
        for (int c = 0; c < components; c++)
        {
            string[] parts = factors[c].Split('x');
            steps[c] = (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            planeWidths[c] = (width + steps[c].X - 1) / steps[c].X;
            planeOffsets[c] = total;
            total += planeWidths[c] * ((height + steps[c].Y - 1) / steps[c].Y) * bytesPerSample;
        }

        Assert.Equal(total, reference.Length);

        int colorChannels = components >= 3 ? 3 : 1;
        bool alpha = components >= 4;
        int outDepth = depth > 8 ? 16 : 8;
        int outMax = (1 << outDepth) - 1;
        int max = (1 << depth) - 1;
        int tolerance = name.Contains("lossy", StringComparison.Ordinal) ? (int)Math.Ceiling((double)outMax / max) : 0;

        int outSamples = image.PixelFormat switch
        {
            PixelFormat.Gray8 or PixelFormat.Gray16 => 1,
            PixelFormat.Rgb24 or PixelFormat.Rgb48 => 3,
            PixelFormat.Rgba32 or PixelFormat.Rgba64 => 4,
            _ => throw new InvalidOperationException($"Unexpected pixel format {image.PixelFormat}."),
        };
        Assert.Equal(colorChannels + (alpha ? 1 : 0), outSamples);

        int mismatches = 0;
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < outSamples; c++)
                {
                    // The last output channel is the alpha component when there is one.
                    int component = (alpha && c == outSamples - 1) ? 3 : c;
                    int offset = planeOffsets[component] + ((((y / steps[component].Y) * planeWidths[component]) + (x / steps[component].X)) * bytesPerSample);
                    int raw = bytesPerSample == 1 ? reference[offset] : reference[offset] | (reference[offset + 1] << 8);
                    if (hasSign)
                    {
                        raw ^= 1 << (depth - 1); // two's complement -> offset binary
                    }

                    raw &= max;
                    int expected = max == outMax ? raw : (int)(((raw * (long)outMax) + (max / 2)) / max);
                    int actual = outDepth == 8
                        ? row[(x * outSamples) + c]
                        : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row)[(x * outSamples) + c];
                    if (Math.Abs(expected - actual) > tolerance)
                    {
                        mismatches++;
                    }
                }
            }
        }

        Assert.True(mismatches == 0, $"{name}: {mismatches} samples differ from OpenJPEG's output.");
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(Jpeg2000RawReferenceTests).Assembly.GetManifestResourceStream($"{Prefix}{name}")
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
