using PeachImage.Benchmarks.Native;

namespace PeachImage.Benchmarks;

/// <summary>
/// <c>native-smoke</c>: decodes each benchmark asset once through PeachImage and the matching reference codec shim and prints
/// the sizes side by side, so a broken P/Invoke signature or a mismatched workload shows up before a long benchmark run.
/// </summary>
internal static class NativeSmoke
{
    public static int Run()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        foreach (string name in new[] { "jp2_lossless_1280x960.jp2", "jp2_lossy_1280x960.jp2" })
        {
            using var pinned = new PinnedBytes(File.ReadAllBytes(Path.Combine(assets, name)));
            using Image peach = Image.Load(pinned.Data);
            Console.WriteLine($"{name}: PeachImage {peach.Width}x{peach.Height}; OpenJPEG width {Openjpeg.Decode(pinned)}");
            Console.WriteLine($"{name}: OpenJPEG reduce 1 width {Openjpeg.Decode(pinned, 1)}");
        }

        foreach (string name in new[] { "photo_1920x1080_lzw.tif", "fax_page_1700x2200_g4.tif" })
        {
            using var pinned = new PinnedBytes(File.ReadAllBytes(Path.Combine(assets, name)));
            using Image peach = Image.Load(pinned.Data);
            Console.WriteLine($"{name}: PeachImage {peach.Width}x{peach.Height} {peach.PixelFormat}; libtiff {Libtiff.Decode(pinned).Length} bytes");
        }

        foreach (string name in new[] { "jxl_lossy_noise_500x606.jxl", "jxl_lossless_patches_1600x1096.jxl" })
        {
            byte[] data = File.ReadAllBytes(Path.Combine(assets, name));
            using Image peach = Image.Load(data);
            Console.WriteLine($"{name}: PeachImage {peach.Width}x{peach.Height} {peach.PixelFormat}; libjxl {Libjxl.Decode(data).Length} bytes");
        }

        foreach (string name in new[] { "photo_1920x1080_420.avif", "small_32x24.avif" })
        {
            using var pinned = new PinnedBytes(File.ReadAllBytes(Path.Combine(assets, name)));
            using Image peach = Image.Load(pinned.Data);
            Console.WriteLine($"{name}: PeachImage {peach.Width}x{peach.Height}; libavif width {Libavif.Decode(pinned)}");
        }

        using Image rgb = Image.Load(Path.Combine(assets, "photo_1920x1080_24bpp.png"));
        using MemoryStream jxl = Libjxl.Encode(rgb.GetPixelSpan(), rgb.Width, rgb.Height, 3, lossless: true, 75, 5);
        Console.WriteLine($"libjxl lossless RGB encode: {jxl.Length} bytes");
        using MemoryStream lossy = Libjxl.Encode(rgb.GetPixelSpan(), rgb.Width, rgb.Height, 3, lossless: false, 75, 5);
        Console.WriteLine($"libjxl lossy RGB encode: {lossy.Length} bytes");
        return 0;
    }
}
