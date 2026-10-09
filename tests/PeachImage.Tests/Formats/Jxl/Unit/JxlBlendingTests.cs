using PeachImage.Formats.Jxl.Features;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlBlendingTests
{
    private static readonly IReadOnlyList<JxlExtraChannelInfo> NoExtras = [];

    private static float[][] Planes(float r, float g, float b, params float[] extras)
    {
        var planes = new float[3 + extras.Length][];
        planes[0] = [r, r];
        planes[1] = [g, g];
        planes[2] = [b, b];
        for (int i = 0; i < extras.Length; i++)
        {
            planes[3 + i] = [extras[i], extras[i]];
        }

        return planes;
    }

    [Fact]
    public void Add_SumsColourChannels()
    {
        var bg = Planes(0.25f, 0.5f, 0.125f);
        var fg = Planes(0.125f, 0.25f, 0.5f);

        JxlBlending.BlendRun(bg, 0, fg, 0, 2, new JxlPatchBlending(JxlPatchBlendMode.Add, 0, false), [], NoExtras);

        Assert.Equal([0.375f, 0.375f], bg[0]);
        Assert.Equal([0.75f, 0.75f], bg[1]);
        Assert.Equal([0.625f, 0.625f], bg[2]);
    }

    [Fact]
    public void Replace_CopiesForeground_AndNoneKeepsBackground()
    {
        var bg = Planes(0.25f, 0.5f, 0.125f);
        var fg = Planes(0.75f, 0.75f, 0.75f);

        JxlBlending.BlendRun(bg, 0, fg, 0, 1, new JxlPatchBlending(JxlPatchBlendMode.None, 0, false), [], NoExtras);
        Assert.Equal(0.25f, bg[0][0]);

        JxlBlending.BlendRun(bg, 0, fg, 0, 1, new JxlPatchBlending(JxlPatchBlendMode.Replace, 0, false), [], NoExtras);
        Assert.Equal(0.75f, bg[0][0]);
        Assert.Equal(0.25f, bg[0][1]); // Only the requested run changes.
    }

    [Fact]
    public void Multiply_ClampsForegroundWhenAsked()
    {
        var bg = Planes(0.5f, 0.5f, 0.5f);
        var fg = Planes(2f, 0.5f, 0.5f);

        JxlBlending.BlendRun(bg, 0, fg, 0, 2, new JxlPatchBlending(JxlPatchBlendMode.Mul, 0, true), [], NoExtras);

        Assert.Equal(0.5f, bg[0][0]);
        Assert.Equal(0.25f, bg[1][0]);
    }

    [Fact]
    public void BlendAbove_CompositesNonPremultipliedAlpha()
    {
        // Background: colour 1.0 at alpha 1.0. Foreground: colour 0.0 at alpha 0.5. Result: 0.5 at alpha 1.0.
        var extra = new[] { JxlExtraChannelInfo.CreateAlpha(false) };
        var bg = Planes(1f, 1f, 1f, 1f);
        var fg = Planes(0f, 0f, 0f, 0.5f);
        var color = new JxlPatchBlending(JxlPatchBlendMode.BlendAbove, 0, false);
        var alphaBlending = new[] { new JxlPatchBlending(JxlPatchBlendMode.BlendAbove, 0, false) };

        JxlBlending.BlendRun(bg, 0, fg, 0, 2, color, alphaBlending, extra);

        Assert.Equal(0.5f, bg[0][0], 5);
        Assert.Equal(1f, bg[3][0], 5);
    }

    [Fact]
    public void AlphaWeightedAddAbove_ScalesForegroundByItsAlpha()
    {
        var extra = new[] { JxlExtraChannelInfo.CreateAlpha(false) };
        var bg = Planes(0.25f, 0.25f, 0.25f, 1f);
        var fg = Planes(0.5f, 0.5f, 0.5f, 0.5f);
        var color = new JxlPatchBlending(JxlPatchBlendMode.AlphaWeightedAddAbove, 0, false);
        var alphaBlending = new[] { new JxlPatchBlending(JxlPatchBlendMode.None, 0, false) };

        JxlBlending.BlendRun(bg, 0, fg, 0, 2, color, alphaBlending, extra);

        Assert.Equal(0.5f, bg[0][0], 5);
        Assert.Equal(1f, bg[3][0], 5); // The alpha channel's own mode (None) leaves it alone.
    }
}
