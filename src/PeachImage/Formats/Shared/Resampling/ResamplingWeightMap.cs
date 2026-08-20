namespace PeachImage.Formats.Shared.Resampling;

/// <summary>
/// Precomputes, for one resize axis, each destination index's contributing source-pixel window: a starting
/// source index plus a normalized (weights sum to 1) weight per tap. Source positions use pixel-center
/// mapping (<c>(destIndex + 0.5) * scale - 0.5</c>); when downscaling, the kernel's radius is widened by the
/// scale factor (the standard "scaled filter" technique) so minification doesn't alias, and source indices
/// are clamped to the valid range (edge replication, no wraparound) rather than reading out of bounds.
/// </summary>
/// <remarks>
/// Every destination index's weights live in one shared, contiguous <see cref="Weights"/> array — not a
/// jagged <c>float[][]</c> — so building a map costs one allocation for the weights (plus <see cref="Starts"/>
/// and <see cref="WeightOffsets"/>) instead of one per destination index (up to thousands for a large
/// resize), and reading a window back via <see cref="GetWeights"/> stays cache-friendly.
/// </remarks>
internal sealed class ResamplingWeightMap
{
    public ResamplingWeightMap(int sourceSize, int destinationSize, IResamplingKernel kernel)
    {
        DestinationSize = destinationSize;
        Starts = new int[destinationSize];
        WeightOffsets = new int[destinationSize + 1];

        double scale = sourceSize / (double)destinationSize;
        double filterScale = scale > 1.0 ? scale : 1.0;
        double radius = kernel.Radius * filterScale;

        // First pass: every window's clamped [start, end] only, to size the flat Weights buffer exactly
        // up front — avoids either a resizable-list indirection or computing each kernel weight twice.
        var clampedStarts = new int[destinationSize];
        var clampedEnds = new int[destinationSize];
        int totalTaps = 0;
        for (int destIndex = 0; destIndex < destinationSize; destIndex++)
        {
            double center = ((destIndex + 0.5) * scale) - 0.5;
            int clampedStart = Math.Clamp((int)Math.Floor(center - radius), 0, sourceSize - 1);
            int clampedEnd = Math.Clamp((int)Math.Ceiling(center + radius), 0, sourceSize - 1);
            clampedStarts[destIndex] = clampedStart;
            clampedEnds[destIndex] = clampedEnd;
            totalTaps += clampedEnd - clampedStart + 1;
        }

        Weights = new float[totalTaps];

        int offset = 0;
        for (int destIndex = 0; destIndex < destinationSize; destIndex++)
        {
            double center = ((destIndex + 0.5) * scale) - 0.5;
            int start = (int)Math.Floor(center - radius);
            int end = (int)Math.Ceiling(center + radius);
            int clampedStart = clampedStarts[destIndex];
            int length = clampedEnds[destIndex] - clampedStart + 1;

            double sum = 0.0;
            for (int k = start; k <= end; k++)
            {
                double weight = kernel.GetWeight((k - center) / filterScale);
                if (weight == 0.0)
                {
                    continue;
                }

                int clampedK = Math.Clamp(k, 0, sourceSize - 1);
                Weights[offset + clampedK - clampedStart] += (float)weight;
                sum += weight;
            }

            if (sum != 0.0)
            {
                float inverseSum = (float)(1.0 / sum);
                for (int i = 0; i < length; i++)
                {
                    Weights[offset + i] *= inverseSum;
                }
            }

            Starts[destIndex] = clampedStart;
            WeightOffsets[destIndex] = offset;
            offset += length;
        }

        WeightOffsets[destinationSize] = offset;
    }

    /// <summary>The number of destination indices this map covers.</summary>
    public int DestinationSize { get; }

    /// <summary>Per destination index, the clamped source index its weight window starts at.</summary>
    public int[] Starts { get; }

    /// <summary>Per destination index, the offset into <see cref="Weights"/> its window starts at; has <see cref="DestinationSize"/> + 1 entries so a window's length is always <c>WeightOffsets[i + 1] - WeightOffsets[i]</c>.</summary>
    public int[] WeightOffsets { get; }

    /// <summary>Every destination index's normalized per-tap weights, concatenated — slice with <see cref="GetWeights"/> rather than indexing directly.</summary>
    public float[] Weights { get; }

    /// <summary>Gets destination index <paramref name="destIndex"/>'s normalized per-tap weights, parallel to (and the same length as) the window starting at its <see cref="Starts"/> entry.</summary>
    public ReadOnlySpan<float> GetWeights(int destIndex) =>
        Weights.AsSpan(WeightOffsets[destIndex], WeightOffsets[destIndex + 1] - WeightOffsets[destIndex]);
}
