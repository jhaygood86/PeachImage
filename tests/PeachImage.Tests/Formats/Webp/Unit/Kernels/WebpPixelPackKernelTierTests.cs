using PeachImage.Formats.Webp.Kernels;

namespace PeachImage.Tests.Formats.Webp.Unit.Kernels;

/// <summary>
/// Pins the vectorized <see cref="IWebpPixelPackKernel"/> tiers against the scalar one they have to
/// reproduce exactly. Tiers are selected by name rather than passed as <see cref="IWebpPixelPackKernel"/>
/// theory parameters, since that type is internal and a public [Theory] method's parameters can't be less
/// accessible than the method itself.
/// </summary>
public class WebpPixelPackKernelTierTests
{
    private static readonly int[] PixelCounts = [0, 1, 3, 4, 5, 7, 8, 9, 15, 16, 17, 33];
    private static readonly string[] TierNames = ["Vector128", "Vector256"];

    public static IEnumerable<object[]> VectorTiers()
    {
        foreach (string name in TierNames)
        {
            yield return [name];
        }
    }

    public static IEnumerable<object[]> VectorTiersAndPixelCounts()
    {
        foreach (string name in TierNames)
        {
            foreach (int pixelCount in PixelCounts)
            {
                yield return [name, pixelCount];
            }
        }
    }

    [Theory]
    [MemberData(nameof(VectorTiers))]
    public void GatherRgba32_MatchesTheScalarTier_AllOpaqueRandomColors(string tierName)
    {
        var vector = CreateTier(tierName);
        var random = new Random(1);
        const int PixelCount = 97; // Not a multiple of either vector width, exercising both scalar tails.
        byte[] rgba = new byte[PixelCount * 4];
        random.NextBytes(rgba);
        for (int i = 0; i < PixelCount; i++)
        {
            rgba[(i * 4) + 3] = 0xFF; // Force opaque so the two tiers' hasAlpha=false path is also checked.
        }

        AssertGatherMatches(vector, rgba, PixelCount);
    }

    [Theory]
    [MemberData(nameof(VectorTiers))]
    public void GatherRgba32_MatchesTheScalarTier_RandomAlpha(string tierName)
    {
        var vector = CreateTier(tierName);
        var random = new Random(2);
        const int PixelCount = 97;
        byte[] rgba = new byte[PixelCount * 4];
        random.NextBytes(rgba);

        AssertGatherMatches(vector, rgba, PixelCount);
    }

    [Theory]
    [MemberData(nameof(VectorTiers))]
    public void GatherRgba32_MatchesTheScalarTier_SingleNonOpaquePixelAtEveryPosition(string tierName)
    {
        var vector = CreateTier(tierName);

        // The alpha reduction accumulates across the whole vector loop before being inspected once at the
        // end, so a single non-opaque pixel anywhere (not just the last one processed) must still flip the
        // result -- this walks that pixel through every position across two vector widths' worth of pixels.
        const int PixelCount = 16;
        for (int nonOpaqueIndex = 0; nonOpaqueIndex < PixelCount; nonOpaqueIndex++)
        {
            byte[] rgba = new byte[PixelCount * 4];
            for (int i = 0; i < PixelCount; i++)
            {
                rgba[(i * 4) + 0] = (byte)(i * 7);
                rgba[(i * 4) + 1] = (byte)(i * 11);
                rgba[(i * 4) + 2] = (byte)(i * 13);
                rgba[(i * 4) + 3] = (byte)(i == nonOpaqueIndex ? 128 : 0xFF);
            }

            AssertGatherMatches(vector, rgba, PixelCount);
        }
    }

    [Theory]
    [MemberData(nameof(VectorTiersAndPixelCounts))]
    public void GatherRgba32_MatchesTheScalarTier_AtEveryPixelCount(string tierName, int pixelCount)
    {
        var vector = CreateTier(tierName);
        var random = new Random(pixelCount + 1);
        byte[] rgba = new byte[Math.Max(pixelCount, 1) * 4];
        random.NextBytes(rgba);

        AssertGatherMatches(vector, rgba, pixelCount);
    }

    [Theory]
    [MemberData(nameof(VectorTiers))]
    public void GatherRgba32_DoesNotWritePastTheEndOfTheDestination(string tierName)
    {
        var vector = CreateTier(tierName);
        const int PixelCount = 5;
        const uint Sentinel = 0xDEADBEEF;

        var random = new Random(3);
        byte[] rgba = new byte[PixelCount * 4];
        random.NextBytes(rgba);

        uint[] destination = new uint[PixelCount + 8];
        Array.Fill(destination, Sentinel);

        vector.GatherRgba32(rgba, destination.AsSpan(0, PixelCount));

        for (int i = PixelCount; i < destination.Length; i++)
        {
            Assert.Equal(Sentinel, destination[i]);
        }
    }

    [Theory]
    [MemberData(nameof(VectorTiersAndPixelCounts))]
    public void ExtractRgb_MatchesTheScalarTier_AtEveryPixelCount(string tierName, int pixelCount)
    {
        var vector = CreateTier(tierName);
        var random = new Random(pixelCount + 100);
        uint[] argb = new uint[Math.Max(pixelCount, 1)];
        for (int i = 0; i < pixelCount; i++)
        {
            argb[i] = (uint)random.Next();
        }

        byte[] fromVector = new byte[Math.Max(pixelCount * 3, 1)];
        byte[] fromScalar = new byte[Math.Max(pixelCount * 3, 1)];

        vector.ExtractRgb(argb.AsSpan(0, pixelCount), fromVector);
        new ScalarWebpPixelPackKernel().ExtractRgb(argb.AsSpan(0, pixelCount), fromScalar);

        Assert.Equal(fromScalar, fromVector);
    }

    [Theory]
    [MemberData(nameof(VectorTiers))]
    public void ExtractRgb_DoesNotWritePastTheEndOfTheDestination(string tierName)
    {
        var vector = CreateTier(tierName);
        const int PixelCount = 13;
        const byte Sentinel = 0xAB;

        var random = new Random(4);
        uint[] argb = new uint[PixelCount];
        for (int i = 0; i < PixelCount; i++)
        {
            argb[i] = (uint)random.Next();
        }

        byte[] destination = new byte[(PixelCount * 3) + 8];
        Array.Fill(destination, Sentinel);

        vector.ExtractRgb(argb, destination.AsSpan(0, PixelCount * 3));

        for (int i = PixelCount * 3; i < destination.Length; i++)
        {
            Assert.Equal(Sentinel, destination[i]);
        }
    }

    private static IWebpPixelPackKernel CreateTier(string name) => name switch
    {
        "Vector128" => new Vector128WebpPixelPackKernel(),
        "Vector256" => new Vector256WebpPixelPackKernel(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown tier."),
    };

    private static void AssertGatherMatches(IWebpPixelPackKernel vector, byte[] rgba, int pixelCount)
    {
        uint[] fromVector = new uint[Math.Max(pixelCount, 1)];
        uint[] fromScalar = new uint[Math.Max(pixelCount, 1)];

        bool vectorHasAlpha = vector.GatherRgba32(rgba, fromVector.AsSpan(0, pixelCount));
        bool scalarHasAlpha = new ScalarWebpPixelPackKernel().GatherRgba32(rgba, fromScalar.AsSpan(0, pixelCount));

        Assert.Equal(scalarHasAlpha, vectorHasAlpha);
        Assert.Equal(fromScalar, fromVector);
    }
}
