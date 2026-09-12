using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Regression test for a decoder-only bug found while bisecting an unrelated Phase 4 chroma-search change
/// via this project's own structural-decision-log self-round-trip check: <c>Av1TileDecoder</c>'s per-leaf
/// mode-info reader unconditionally copied its <c>_mvRow</c>/<c>_mvCol</c> instance fields into the per-mi
/// <c>MvRowsGrid</c>/<c>MvColsGrid</c> arrays (the encoder's own analogous write already correctly zeroes
/// these via <c>usedIntrabc ? mv : 0</c>) -- but the non-IntraBC mode-info branch never reset those two
/// fields back to 0 the way it already resets <c>_angleDeltaY</c>/<c>_angleDeltaUv</c>/<c>_cflAlphaU</c>/
/// <c>_cflAlphaV</c>. A non-IntraBC leaf decoded right after an IntraBC leaf would silently carry that
/// leaf's stale MV into its own grid entry. Confirmed (see <c>compare-avif-encoding-to-lucky-clover.md</c>'s
/// own round log) inert for real pixel decode/MV-stack prediction, since every real read of
/// <c>MvRowsGrid</c>/<c>MvColsGrid</c> already gates on <c>IsInters</c> first -- but it corrupted the grid
/// as an exposed structural invariant, which is what this test asserts directly rather than relying on a
/// pixel-level round-trip (which the bug never affected and so would never catch a regression here).
/// A 256x256 sharp checkerboard at low effort reliably produces both an IntraBC leaf and adjacent non-
/// IntraBC leaves in the same tile, which is what's needed to expose the staleness.
/// </summary>
public class Av1TileDecoderMvGridResetTests
{
    [Fact]
    public void DecodedNonIntrabcLeaves_HaveZeroMv_EvenAfterAnIntrabcLeaf()
    {
        var image = CreateCheckerboardImage(width: 256, height: 256, blockSize: 8);
        byte[] rgb = image.GetPixelSpan().ToArray();

        var encodedFrame = Av1FrameEncoder.Encode(
            rgb, image.Width, image.Height, monoChrome: false, quality: 100, lossless: true, effort: 2);

        var decoded = Av1FrameDecoder.Decode(encodedFrame.ObuBytes);

        bool sawAnyIntrabcLeaf = false;
        for (int idx = 0; idx < decoded.MiSizes.Length; idx++)
        {
            if (decoded.IsInters[idx])
            {
                sawAnyIntrabcLeaf = true;
                continue;
            }

            Assert.True(decoded.MvRowsGrid[idx] == 0 && decoded.MvColsGrid[idx] == 0,
                $"mi position {idx} is not IntraBC but decoded MvRow={decoded.MvRowsGrid[idx]}, MvCol={decoded.MvColsGrid[idx]} (expected 0, 0) -- stale MV leaked from a prior IntraBC leaf.");
        }

        Assert.True(sawAnyIntrabcLeaf, "fixture didn't reach an IntraBC leaf -- test wouldn't exercise the staleness path.");
    }

    private static Image CreateCheckerboardImage(int width, int height, int blockSize)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int idx = ((row * width) + col) * 3;
                byte v = ((col / blockSize) + (row / blockSize)) % 2 == 0 ? (byte)255 : (byte)0;
                pixels[idx + 0] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
            }
        }

        return image;
    }
}
