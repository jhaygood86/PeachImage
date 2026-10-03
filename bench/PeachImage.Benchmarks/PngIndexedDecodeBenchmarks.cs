using BenchmarkDotNet.Attributes;
using PeachImage.Formats.Png;

namespace PeachImage.Benchmarks;

/// <summary>
/// Decode throughput for indexed (PLTE) PNGs, which take the bit-unpack + palette-resolve path rather than the
/// direct-copy path truecolor/grayscale photos use. Sources are synthetic 1920x1080 images with 16 colors
/// (encoded at 4 bits per pixel) and 200 colors (8 bits per pixel).
/// </summary>
[MemoryDiagnoser]
public class PngIndexedDecodeBenchmarks
{
    private byte[] _png16 = null!;
    private byte[] _png200 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _png16 = Make(16);
        _png200 = Make(200);
    }

    private static byte[] Make(int colors)
    {
        const int width = 1920, height = 1080;
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.PixelMemory.Span;
        var rng = new Random(colors);
        var palette = new byte[colors * 3];
        rng.NextBytes(palette);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Blocky regions with some noise, so it compresses like graphics rather than pure noise.
                int c = (((x / 24) + (y / 24)) * 7 + (rng.Next(8) == 0 ? rng.Next(colors) : 0)) % colors;
                int o = ((y * width) + x) * 3;
                pixels[o] = palette[c * 3];
                pixels[o + 1] = palette[(c * 3) + 1];
                pixels[o + 2] = palette[(c * 3) + 2];
            }
        }

        using var stream = new MemoryStream();
        PngEncoder.Encode(image, stream);
        return stream.ToArray();
    }

    [Benchmark]
    public Image Decode_Indexed16Colors() => PngDecoder.Decode(new MemoryStream(_png16));

    [Benchmark]
    public Image Decode_Indexed200Colors() => PngDecoder.Decode(new MemoryStream(_png200));
}
