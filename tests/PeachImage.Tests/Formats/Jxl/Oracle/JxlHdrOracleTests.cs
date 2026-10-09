using System.Runtime.InteropServices;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>XYB-coded images in non-sRGB primaries and in the PQ and HLG transfer functions, against libjxl's own decode.</summary>
[Trait("Category", "Oracle")]
public class JxlHdrOracleTests
{
    [Theory]
    [InlineData("color_primaries=bt2020:color_trc=smpte2084")]
    [InlineData("color_primaries=bt2020:color_trc=arib-std-b67")]
    [InlineData("color_primaries=smpte432:color_trc=iec61966-2-1")]
    [InlineData("color_primaries=bt2020:color_trc=bt709")]
    [InlineData("color_primaries=bt709:color_trc=linear")]
    public void LossyImageInOtherColorSpaces_MatchesLibjxl(string colorOptions)
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] jxl = LibjxlOracle.Encode($"gradients=size=160x120,setparams={colorOptions}", "rgb48le", "-distance 1");
        byte[] reference = LibjxlOracle.Decode(jxl, "rgb48le");

        using var image = Image.Load(new MemoryStream(jxl), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgb48 });
        var ours = MemoryMarshal.Cast<byte, ushort>(image.GetPixelSpan());
        var theirs = MemoryMarshal.Cast<byte, ushort>(reference.AsSpan());
        Assert.Equal(theirs.Length, ours.Length);

        long total = 0;
        int max = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            int diff = Math.Abs(ours[i] - theirs[i]);
            total += diff;
            max = Math.Max(max, diff);
        }

        double mean = (double)total / ours.Length;
        Assert.True(mean <= 40 && max <= 600, $"{colorOptions}: mean abs difference {mean:F1}, max {max}.");
    }
}
