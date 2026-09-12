using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Two-pass tile-encoder architecture, Stage 1b-ii's own explicit verification bar (project plan's "build as
/// a parallel, switchable path first" discipline): asserts <see cref="Av1TileEncoder.EncodeTileTwoPass"/>
/// (the new <c>DecideTile</c>-then-<c>EmitTile</c> path) produces byte-for-byte identical output to the
/// existing, already-tested fused <see cref="Av1TileEncoder.EncodeTile"/>, for the exact same inputs, across
/// content patterns deliberately chosen to exercise every leaf kind the two-pass split has to reproduce
/// exactly: solid color (all-DC, all-skip leaves), a smooth gradient (directional mode search), a repeated
/// tiled pattern (real IntraBC/palette candidates), and pseudo-random noise (stresses tx-type/trellis
/// search breadth). All dimensions here are already exact superblock multiples (128 for lossless, 64 for
/// non-lossless) specifically so this test can call <see cref="Av1TileEncoder.EncodeTile"/>/
/// <see cref="Av1TileEncoder.EncodeTileTwoPass"/> directly with unpadded planes, without needing to
/// replicate <c>Av1FrameEncoder</c>'s own edge-padding logic just for this comparison.
/// </summary>
public class Av1TwoPassTileEncoderParityTests
{
    private static int[] SolidPlane(int width, int height, int value)
    {
        var plane = new int[width * height];
        Array.Fill(plane, value);
        return plane;
    }

    private static int[] GradientPlane(int width, int height)
    {
        var plane = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                plane[(y * width) + x] = ((x * 255) / Math.Max(1, width - 1) + (y * 255) / Math.Max(1, height - 1)) / 2;
            }
        }

        return plane;
    }

    /// <summary>16x16 tile repeated across the whole plane -- real, exact-match IntraBC/palette candidates
    /// once enough of the frame has already been committed to reference back into.</summary>
    private static int[] RepeatedTilePlane(int width, int height)
    {
        var plane = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int tileX = x % 16;
                int tileY = y % 16;
                plane[(y * width) + x] = ((tileX + tileY) % 2 == 0) ? 40 : (200 - ((tileX * 5) % 60));
            }
        }

        return plane;
    }

    private static int[] NoisePlane(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var plane = new int[width * height];
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = rng.Next(0, 256);
        }

        return plane;
    }

    private static void AssertTwoPassMatchesFused(
        int[] yPlane, int width, int height,
        int[]? uPlane, int[]? vPlane, int chromaWidth, int chromaHeight,
        bool monoChrome, int baseQIdx, bool lossless, bool chroma444,
        bool allowScreenContentTools = false, bool allowIntrabc = false)
    {
        int[] fusedReconY = (int[])yPlane.Clone();
        int[]? fusedReconU = uPlane is null ? null : (int[])uPlane.Clone();
        int[]? fusedReconV = vPlane is null ? null : (int[])vPlane.Clone();
        byte[] fusedBytes = Av1TileEncoder.EncodeTile(
            yPlane, width, height, uPlane, vPlane, chromaWidth, chromaHeight,
            fusedReconY, fusedReconU, fusedReconV,
            monoChrome, baseQIdx, lossless, chroma444, effort: 2, allowScreenContentTools, allowIntrabc);

        int[] twoPassReconY = (int[])yPlane.Clone();
        int[]? twoPassReconU = uPlane is null ? null : (int[])uPlane.Clone();
        int[]? twoPassReconV = vPlane is null ? null : (int[])vPlane.Clone();
        byte[] twoPassBytes = Av1TileEncoder.EncodeTileTwoPass(
            yPlane, width, height, uPlane, vPlane, chromaWidth, chromaHeight,
            twoPassReconY, twoPassReconU, twoPassReconV,
            monoChrome, baseQIdx, lossless, chroma444, effort: 2, allowScreenContentTools, allowIntrabc);

        Assert.Equal(fusedBytes, twoPassBytes);
        Assert.Equal(fusedReconY, twoPassReconY);
        if (fusedReconU is not null)
        {
            Assert.Equal(fusedReconU, twoPassReconU);
            Assert.Equal(fusedReconV, twoPassReconV);
        }
    }

    public static IEnumerable<object[]> LosslessContentPatterns()
    {
        yield return ["solid"];
        yield return ["gradient"];
        yield return ["repeated"];
        yield return ["noise"];
    }

    [Theory]
    [MemberData(nameof(LosslessContentPatterns))]
    public void Lossless_SingleSuperblock_TwoPassMatchesFused(string pattern)
    {
        const int size = 128; // one 128x128 lossless superblock
        int[] y = MakePlane(pattern, size, size, seed: 1);
        int[] u = MakePlane(pattern, size, size, seed: 2);
        int[] v = MakePlane(pattern, size, size, seed: 3);

        AssertTwoPassMatchesFused(y, size, size, u, v, size, size, monoChrome: false, baseQIdx: 0, lossless: true, chroma444: true);
    }

    [Theory]
    [MemberData(nameof(LosslessContentPatterns))]
    public void Lossless_MultiSuperblock_TwoPassMatchesFused(string pattern)
    {
        const int width = 256; // 2x1 128x128 superblocks
        const int height = 128;
        int[] y = MakePlane(pattern, width, height, seed: 4);
        int[] u = MakePlane(pattern, width, height, seed: 5);
        int[] v = MakePlane(pattern, width, height, seed: 6);

        AssertTwoPassMatchesFused(y, width, height, u, v, width, height, monoChrome: false, baseQIdx: 0, lossless: true, chroma444: true);
    }

    [Fact]
    public void Lossless_ScreenContentAndIntrabc_TwoPassMatchesFused()
    {
        const int size = 128;
        int[] y = RepeatedTilePlane(size, size);
        int[] u = RepeatedTilePlane(size, size);
        int[] v = RepeatedTilePlane(size, size);

        AssertTwoPassMatchesFused(y, size, size, u, v, size, size, monoChrome: false, baseQIdx: 0, lossless: true, chroma444: true, allowScreenContentTools: true, allowIntrabc: true);
    }

    [Theory]
    [InlineData("solid")]
    [InlineData("gradient")]
    [InlineData("noise")]
    public void Lossy_SingleSuperblock_TwoPassMatchesFused(string pattern)
    {
        const int size = 64; // one 64x64 non-lossless superblock
        int[] y = MakePlane(pattern, size, size, seed: 7);
        int[] u = MakePlane(pattern, size / 2, size / 2, seed: 8);
        int[] v = MakePlane(pattern, size / 2, size / 2, seed: 9);

        AssertTwoPassMatchesFused(y, size, size, u, v, size / 2, size / 2, monoChrome: false, baseQIdx: 64, lossless: false, chroma444: false);
    }

    [Theory]
    [InlineData("solid")]
    [InlineData("gradient")]
    [InlineData("noise")]
    public void Lossy_MultiSuperblock_TwoPassMatchesFused(string pattern)
    {
        const int width = 128; // 2x1 64x64 superblocks
        const int height = 64;
        int[] y = MakePlane(pattern, width, height, seed: 10);
        int[] u = MakePlane(pattern, width / 2, height / 2, seed: 11);
        int[] v = MakePlane(pattern, width / 2, height / 2, seed: 12);

        AssertTwoPassMatchesFused(y, width, height, u, v, width / 2, height / 2, monoChrome: false, baseQIdx: 96, lossless: false, chroma444: false);
    }

    [Fact]
    public void Lossy_ScreenContentAndIntrabc_TwoPassMatchesFused()
    {
        const int size = 64;
        int[] y = RepeatedTilePlane(size, size);
        int[] u = RepeatedTilePlane(size / 2, size / 2);
        int[] v = RepeatedTilePlane(size / 2, size / 2);

        AssertTwoPassMatchesFused(y, size, size, u, v, size / 2, size / 2, monoChrome: false, baseQIdx: 64, lossless: false, chroma444: false, allowScreenContentTools: true, allowIntrabc: true);
    }

    [Fact]
    public void MonoChrome_TwoPassMatchesFused()
    {
        const int size = 128;
        int[] y = GradientPlane(size, size);

        AssertTwoPassMatchesFused(y, size, size, null, null, 0, 0, monoChrome: true, baseQIdx: 0, lossless: true, chroma444: false);
    }

    private static int[] MakePlane(string pattern, int width, int height, int seed) => pattern switch
    {
        "solid" => SolidPlane(width, height, 128),
        "gradient" => GradientPlane(width, height),
        "repeated" => RepeatedTilePlane(width, height),
        "noise" => NoisePlane(width, height, seed),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
    };
}
