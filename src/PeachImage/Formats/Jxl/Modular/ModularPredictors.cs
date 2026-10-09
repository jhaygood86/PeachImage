using System.Runtime.CompilerServices;

namespace PeachImage.Formats.Jxl.Modular;

/// <summary>The fixed predictors a Modular MA-tree leaf can select (values 0..13 are decodable).</summary>
internal enum ModularPredictor : uint
{
    Zero = 0,
    Left = 1,
    Top = 2,
    Average0 = 3,
    Select = 4,
    Gradient = 5,
    Weighted = 6,
    TopRight = 7,
    TopLeft = 8,
    LeftLeft = 9,
    Average1 = 10,
    Average2 = 11,
    Average3 = 12,
    Average4 = 13,

    /// <summary>Encoder-only: best of Gradient and Weighted. Invalid in a stream.</summary>
    Best = 14,

    /// <summary>Encoder-only. Invalid in a stream.</summary>
    Variable = 15,
}

/// <summary>The neighbouring samples used to predict one pixel, with the format's edge-replication rules applied.</summary>
internal readonly struct ModularNeighbors
{
    public readonly long Left;
    public readonly long Top;
    public readonly long TopLeft;
    public readonly long TopRight;
    public readonly long LeftLeft;
    public readonly long TopTop;
    public readonly long TopRightRight;

    /// <summary>Gathers neighbours of (<paramref name="x"/>, <paramref name="y"/>) from a plane of the given width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ModularNeighbors(ReadOnlySpan<int> data, int width, int x, int y)
    {
        int pos = (y * width) + x;
        Left = x != 0 ? data[pos - 1] : y != 0 ? data[pos - width] : 0;
        Top = y != 0 ? data[pos - width] : Left;
        TopLeft = x != 0 && y != 0 ? data[pos - 1 - width] : Left;
        TopRight = x + 1 < width && y != 0 ? data[pos + 1 - width] : Top;
        LeftLeft = x > 1 ? data[pos - 2] : Left;
        TopTop = y > 1 ? data[pos - width - width] : Top;
        TopRightRight = x + 2 < width && y != 0 ? data[pos + 2 - width] : TopRight;
    }
}

internal static class ModularPredictors
{
    /// <summary>Clamps the gradient <c>n + w - l</c> to the range of <paramref name="n"/> and <paramref name="w"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ClampedGradient(int n, int w, int l)
    {
        int m = Math.Min(n, w);
        int max = Math.Max(n, w);

        // The intermediate may overflow, so it is computed unsigned and the range checked against the inputs.
        int grad = unchecked((int)((uint)n + (uint)w - (uint)l));
        int gradClampMax = l < m ? max : grad;
        return l > max ? m : gradClampMax;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Select(long a, long b, long c)
    {
        long p = a + b - c;
        long pa = Math.Abs(p - a);
        long pb = Math.Abs(p - b);
        return pa < pb ? a : b;
    }

    /// <summary>Evaluates a fixed predictor; <paramref name="weightedPrediction"/> is used for <see cref="ModularPredictor.Weighted"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Predict(ModularPredictor predictor, in ModularNeighbors n, long weightedPrediction) => predictor switch
    {
        ModularPredictor.Zero => 0,
        ModularPredictor.Left => n.Left,
        ModularPredictor.Top => n.Top,
        ModularPredictor.Select => Select(n.Left, n.Top, n.TopLeft),
        ModularPredictor.Weighted => weightedPrediction,
        ModularPredictor.Gradient => ClampedGradient((int)n.Left, (int)n.Top, (int)n.TopLeft),
        ModularPredictor.TopLeft => n.TopLeft,
        ModularPredictor.TopRight => n.TopRight,
        ModularPredictor.LeftLeft => n.LeftLeft,
        ModularPredictor.Average0 => (n.Left + n.Top) / 2,
        ModularPredictor.Average1 => (n.Left + n.TopLeft) / 2,
        ModularPredictor.Average2 => (n.TopLeft + n.Top) / 2,
        ModularPredictor.Average3 => (n.Top + n.TopRight) / 2,
        ModularPredictor.Average4 => ((6 * n.Top) - (2 * n.TopTop) + (7 * n.Left) + n.LeftLeft + n.TopRightRight + (3 * n.TopRight) + 8) / 16,
        _ => 0,
    };

    /// <summary>Predicts without the weighted predictor (used by the delta palette).</summary>
    public static long PredictSimple(ReadOnlySpan<int> data, int width, int x, int y, ModularPredictor predictor)
    {
        var neighbors = new ModularNeighbors(data, width, x, y);
        return Predict(predictor, neighbors, 0);
    }

    /// <summary>Predicts with the weighted predictor (used by the delta palette).</summary>
    public static long PredictWeighted(ReadOnlySpan<int> data, int width, int x, int y, WeightedPredictorState state)
    {
        var n = new ModularNeighbors(data, width, x, y);
        return state.Predict(x, y, width, n.Top, n.Left, n.TopRight, n.TopLeft, n.TopTop, out _);
    }
}
