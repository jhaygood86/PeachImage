namespace PeachImage.Internal.Icc;

/// <summary>
/// An N-dimensional color lookup table (ICC.1:2010 §10.8's CLUT structure), e.g. a 4-input (CMYK), 3-output
/// (Lab/XYZ) grid for a typical CMYK printer profile. Ported from Wacton/Unicolour's <c>Icc/Clut.cs</c> (MIT
/// license) — see THIRD-PARTY-LICENSES.md, with <see cref="Lookup"/> (the per-pixel hot path) rewritten
/// against <see cref="Span{T}"/> — the upstream version allocates three arrays per call; this allocates
/// nothing, using <c>stackalloc</c> scratch sized by <see cref="InputChannels"/> (bounded and small — at most
/// a handful of ink channels in any real ICC profile) instead.
/// </summary>
internal sealed class IccClut
{
    private readonly double[] values;
    private readonly int[][] inputBinaryVectors;

    internal int InputChannels { get; }

    internal int GridPoints { get; }

    internal int OutputChannels { get; }

    /// <summary>The number of <paramref name="values"/> must equal <c>gridPoints ^ inputChannels * outputChannels</c>.</summary>
    internal IccClut(double[] values, int inputChannels, int gridPoints, int outputChannels)
    {
        this.values = values;
        InputChannels = inputChannels;
        GridPoints = gridPoints;
        OutputChannels = outputChannels;
        inputBinaryVectors = GenerateVectorsOfBaseTwo(inputChannels);
    }

    /// <summary>
    /// Finds the grid section bounded by each input channel's lower/upper grid index, then interpolates
    /// within it (quadrilinear for a 4-input CMYK table). Writes <see cref="OutputChannels"/> values into
    /// <paramref name="output"/>; allocates nothing.
    /// </summary>
    internal void Lookup(ReadOnlySpan<double> clutInputs, Span<double> output)
    {
        Span<int> lowerIndex = stackalloc int[InputChannels];
        Span<int> upperIndex = stackalloc int[InputChannels];
        Span<double> distanceToLower = stackalloc double[InputChannels];
        Span<double> distanceToUpper = stackalloc double[InputChannels];
        for (int i = 0; i < InputChannels; i++)
        {
            var (lower, upper, distanceUp) = IccLut.Lookup(GridPoints, clutInputs[i]);
            lowerIndex[i] = lower;
            upperIndex[i] = upper;
            distanceToUpper[i] = distanceUp;
            distanceToLower[i] = 1 - distanceUp;
        }

        output.Clear();
        Span<int> cornerIndexes = stackalloc int[InputChannels];
        foreach (var binaryVector in inputBinaryVectors)
        {
            double distance = 1.0;
            for (int i = 0; i < InputChannels; i++)
            {
                bool useUpper = binaryVector[i] != 0;
                cornerIndexes[i] = useUpper ? upperIndex[i] : lowerIndex[i];
                distance *= useUpper ? distanceToUpper[i] : distanceToLower[i];
            }

            if (distance == 0.0)
            {
                continue;
            }

            int outputOffset = GetOutputIndex(cornerIndexes);
            for (int i = 0; i < OutputChannels; i++)
            {
                output[i] += values[outputOffset + i] * distance;
            }
        }
    }

    private int GetOutputIndex(ReadOnlySpan<int> gridIndexes)
    {
        int outputIndex = 0;
        int stride = OutputChannels;
        for (int i = gridIndexes.Length - 1; i >= 0; i--)
        {
            outputIndex += gridIndexes[i] * stride;
            stride *= GridPoints;
        }

        return outputIndex;
    }

    private static int[][] GenerateVectorsOfBaseTwo(int dimensions)
    {
        int totalVectors = 1 << dimensions;
        var vectors = new int[totalVectors][];
        for (int i = 0; i < totalVectors; i++)
        {
            var vector = new int[dimensions];
            for (int bit = 0; bit < dimensions; bit++)
            {
                vector[dimensions - 1 - bit] = (i >> bit) & 1;
            }

            vectors[i] = vector;
        }

        return vectors;
    }
}
