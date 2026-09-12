using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Port of libaom's own <c>screen_content_detection_mode_2_test.cc</c> (<c>ScreenContentDetectionMode2.
/// FindDominantValue</c>/<c>DilateBlock</c>): the exact real <c>kSource</c>/<c>kExpected</c> fixture,
/// transcribed verbatim.
///
/// <para>The real test's own comparison loop indexes <c>kExpected</c> with <c>r * kHeight + c</c>
/// (<c>kHeight</c>, not <c>kWidth</c>, as the row stride) -- a real bug in libaom's own test code, evidently
/// harmless there only because the fixture's own dilated output is uniform enough along the affected
/// positions to still match. Hand-verified independently (row-by-row, off this project's own port of the
/// real algorithm) that <c>kExpected</c>'s own literal 54 values are themselves correct for
/// <c>kWidth</c> = 9, <c>kHeight</c> = 6 when indexed correctly (<c>r * kWidth + c</c>) -- so this port
/// reuses the same real expected data, just compared with the stride libaom's own doc comment and
/// dimensions actually describe.</para>
/// </summary>
public class Av1ScreenContentDilationTests
{
    private const int Width = 9;
    private const int Height = 6;

    private static readonly byte[] Source =
    [
        0, 0, 1, 2, 255, 3, 4, 0, 0,
        0, 5, 6, 255, 255, 255, 7, 8, 0,
        0, 255, 255, 255, 255, 255, 255, 255, 0,
        0, 255, 255, 255, 255, 255, 255, 255, 0,
        0, 9, 10, 255, 255, 255, 11, 12, 0,
        0, 0, 13, 14, 255, 15, 16, 0, 0,
    ];

    private static readonly byte[] Expected =
    [
        0, 0, 255, 255, 255, 255, 255, 0, 0,
        255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255,
        0, 0, 255, 255, 255, 255, 255, 0, 0,
    ];

    [Fact]
    public void FindDominantValue_MatchesLibaomFixture()
    {
        // 255 appears 22 times, in contrast to 0's 16 times.
        Assert.Equal(255, Av1ScreenContentDilation.FindDominantValue(Source, Width, Height, Width));
    }

    [Fact]
    public void DilateBlock_MatchesLibaomFixture()
    {
        var dilated = new byte[Width * Height];
        Av1ScreenContentDilation.DilateBlock(Source, Width, dilated, Width, Height, Width);

        for (int r = 0; r < Height; r++)
        {
            for (int c = 0; c < Width; c++)
            {
                int i = (r * Width) + c;
                Assert.True(Expected[i] == dilated[i], $"({r},{c}): expected {Expected[i]}, actual {dilated[i]}");
            }
        }
    }
}
