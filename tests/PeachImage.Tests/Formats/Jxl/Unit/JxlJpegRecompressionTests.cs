using System.Security.Cryptography;
using PeachImage.Formats.Jxl;
using PeachImage.Tests.Formats.Jxl.Oracle;

namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>
/// JPEG XL files made by <c>cjxl</c> (libjxl 0.12) from JPEG files that cover the shapes a JPEG can take: chroma subsampling,
/// grayscale, RGB without a colour transform, restart intervals, progressive scans and non-interleaved scans. Reconstruction
/// has to give back the original file, whose SHA-256 is listed here. The <c>generated_*</c> JPEGs were written by PeachImage's
/// own encoder from a synthetic 96x64 picture; the others come from the libjxl test data.
/// </summary>
public class JxlJpegRecompressionTests
{
    private static readonly (string Asset, string Sha256)[] Cases =
    [
        ("recompressed_generated_gray.jxl", "28437d7965628d75e81efcefb72005b55d49c31c6d3189c92cffca3e6b3956f6"),
        ("recompressed_generated_rgb444.jxl", "4e7014f647a93dbc4df32600aa843172db91e0fe429320900cf4cc1a4f5f76a8"),
        ("recompressed_generated_ycc420.jxl", "dd4e353029a198dbcd5cbeb34d4ad2b98d0c0adfdfbdb0295ef5ac1c7bde844a"),
        ("recompressed_generated_ycc420_restart.jxl", "bee47c7758a822cca4b4cd3cbc932e3fa91d27427c6cfa7123bdfb7886737c29"),
        ("recompressed_generated_ycc422.jxl", "2cb4768bdfa3aa7342a9d4c9ddd4515f4d7f6fbb081a31317a74bffb1ccefa1c"),
        ("recompressed_generated_ycc444.jxl", "9548ac7824c8e1ccd0f36fd9a16aa005ae51b44543f93bd64ad326c634a0ea74"),
        ("recompressed_generated_gray_progressive.jxl", "235e8ccb911bc66a35e8075d5fd5c88ce0e9ac1675859e0b627b96519468f1b9"),
        ("recompressed_generated_ycc420_progressive_optimized.jxl", "795e1682cf7b88b7b1c4edb2cf83803a671d71a272e9572e754eaff195e5f743"),
        ("recompressed_generated_ycc422_progressive_restart.jxl", "36cca9ccae320e9be3b594b243d80e8694b0fde6c621058d0fd6c6fdbc674326"),
        ("recompressed_generated_ycc444_progressive.jxl", "2feb5a2c6c2a8395e27c75b1be2c7f29b0cff13597ffba0c5e5b09c33306a422"),
        ("recompressed_flower_small_q85_420_non_interleaved.jxl", "9d58a21221df433e4e5d373b3178bb1bb073e810e5fe5988891bf6fbf580c493"),
        ("recompressed_flower_small_q85_420_partially_interleaved.jxl", "d9e29efba48dcc3171b6bc30eabb5ce9a54439fbe6087543e90da6c115469c32"),
        ("recompressed_flower_small_q85_444_non_interleaved.jxl", "bf8466234b80d37469627db92abfa00ffb04fa608fd4de4775311fb3c9572866"),
        ("recompressed_bicycles_restarts.jxl", "43aa279236a46c735f5eca9c5264274523f8ca11c202c8d8b277eb11157ac1d4"),
        ("recompressed_sideways_bench.jxl", "d1479d4643be8e857e5b91f70834aa6b37fa1594dced15933025babc0c7431cf"),
        ("recompressed_flower_png_im_q85_420_progr.jxl", "376ea09db11fad472ea164e5920c1c31746136b0e9db46bc3d527ee192b855e9"),
    ];

    public static TheoryData<string, string> Files()
    {
        var data = new TheoryData<string, string>();
        foreach (var (asset, hash) in Cases)
        {
            data.Add(asset, hash);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Reconstruction_GivesBackTheOriginalJpeg(string asset, string expectedSha256)
    {
        byte[] jpeg = JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(JxlTestAssets.Load(asset)));

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant());
        Assert.Equal("jpeg", Image.Identify(new MemoryStream(jpeg)).FormatName, ignoreCase: true);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RecompressedFile_AlsoDecodesLikeLibjxl(string asset, string expectedSha256)
    {
        _ = expectedSha256;
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");
        Assert.SkipWhen(asset.Contains("sideways", StringComparison.Ordinal), "The picture is rotated by its EXIF orientation, which the oracle output does not apply.");

        byte[] file = JxlTestAssets.Load(asset);
        using var ours = Image.Load(new MemoryStream(file));
        byte[] reference = LibjxlOracle.Decode(file, ours.PixelFormat == PixelFormat.Gray8 ? "gray" : "rgb24");

        var pixels = ours.GetPixelSpan();
        Assert.Equal(reference.Length, pixels.Length);
        long total = 0;
        int max = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            int difference = Math.Abs(pixels[i] - reference[i]);
            total += difference;
            max = Math.Max(max, difference);
        }

        Assert.True((double)total / pixels.Length < 0.6 && max <= 3, $"mean absolute difference {(double)total / pixels.Length:F3}, largest {max}");
    }

    [Fact]
    public void DamagedFiles_OnlyEverFailWithJpegXlExceptions()
    {
        byte[] file = JxlTestAssets.Load("recompressed_generated_ycc420_restart.jxl");
        var random = new Random(31);
        for (int attempt = 0; attempt < 600; attempt++)
        {
            byte[] damaged = (byte[])file.Clone();
            int flips = 1 + random.Next(3);
            for (int i = 0; i < flips; i++)
            {
                damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            }

            try
            {
                JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(damaged));
            }
            catch (JxlFormatException)
            {
            }
        }
    }

    [Fact]
    public void TruncatedFiles_OnlyEverFailWithJpegXlExceptions()
    {
        byte[] file = JxlTestAssets.Load("recompressed_generated_gray.jxl");
        for (int length = 0; length < file.Length; length += 7)
        {
            try
            {
                JxlJpegReconstruction.ReconstructJpeg(new MemoryStream(file, 0, length));
            }
            catch (JxlFormatException)
            {
            }
        }
    }
}
