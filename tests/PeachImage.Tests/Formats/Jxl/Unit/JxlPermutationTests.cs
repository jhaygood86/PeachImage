using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Tests.Formats.Jxl.Unit;

public class JxlPermutationTests
{
    [Fact]
    public void DecodeLehmerCode_KnownExample()
    {
        // From {0,1,2,3}: take the 2nd (index 2) → 2; from {0,1,3} index 0 → 0; from {1,3} index 1 → 3; then 1.
        Assert.Equal([2, 0, 3, 1], JxlPermutation.DecodeLehmerCode([2, 0, 1, 0]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(100)]
    [InlineData(1024)]
    public void DecodeLehmerCode_RoundTripsRandomPermutations(int n)
    {
        var random = new Random(n);
        int[] permutation = Enumerable.Range(0, n).OrderBy(_ => random.Next()).ToArray();

        // Straightforward O(n^2) Lehmer encoding: digit i is the rank of permutation[i] among the unused values.
        var used = new bool[n];
        var code = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint rank = 0;
            for (int v = 0; v < permutation[i]; v++)
            {
                if (!used[v])
                {
                    rank++;
                }
            }

            code[i] = rank;
            used[permutation[i]] = true;
        }

        Assert.Equal(permutation, JxlPermutation.DecodeLehmerCode(code));
    }

    [Fact]
    public void DecodeLehmerCode_AllZerosIsTheIdentity() =>
        Assert.Equal(Enumerable.Range(0, 64).ToArray(), JxlPermutation.DecodeLehmerCode(new uint[64]));
}
