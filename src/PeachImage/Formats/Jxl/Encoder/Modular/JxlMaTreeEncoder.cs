using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Modular;

namespace PeachImage.Formats.Jxl.Encoder.Modular;

/// <summary>A node of the encoder's meta-adaptive context tree: either a split on one property or a leaf with a predictor.</summary>
internal sealed class JxlMaTreeNode
{
    /// <summary>The property compared against <see cref="Split"/>, or -1 for a leaf.</summary>
    public int Property = -1;

    public int Split;

    /// <summary>The child taken when the property is greater than <see cref="Split"/>.</summary>
    public JxlMaTreeNode? Greater;

    /// <summary>The child taken otherwise.</summary>
    public JxlMaTreeNode? LessOrEqual;

    public ModularPredictor Predictor = ModularPredictor.Gradient;

    public int Offset;

    /// <summary>For a leaf, its index in breadth-first order: the context its residuals are coded under.</summary>
    public int LeafId = -1;

    public static JxlMaTreeNode Leaf(ModularPredictor predictor, int offset = 0) => new() { Predictor = predictor, Offset = offset };

    public static JxlMaTreeNode Inner(int property, int split, JxlMaTreeNode greater, JxlMaTreeNode lessOrEqual) =>
        new() { Property = property, Split = split, Greater = greater, LessOrEqual = lessOrEqual };
}

/// <summary>
/// An MA tree ready to be written: nodes in the breadth-first order the decoder reads them in, with leaves numbered
/// by that order. A tree that splits only on the channel index (property 0) resolves to one leaf per channel.
/// </summary>
internal sealed class JxlMaTreeEncoder
{
    // Entropy contexts of the tree's own symbol stream, mirroring the decoder's.
    private const int SplitValContext = 0;
    private const int PropertyContext = 1;
    private const int PredictorContext = 2;
    private const int OffsetContext = 3;
    private const int MultiplierLogContext = 4;
    private const int MultiplierBitsContext = 5;

    /// <summary>The number of entropy contexts the tree's own symbol stream uses.</summary>
    public const int NumTreeContexts = 6;

    private readonly List<JxlMaTreeNode> _nodes = [];
    private readonly List<JxlMaTreeNode> _leaves = [];

    public JxlMaTreeEncoder(JxlMaTreeNode root)
    {
        Root = root;
        _nodes.Add(root);
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Property < 0)
            {
                node.LeafId = _leaves.Count;
                _leaves.Add(node);
                continue;
            }

            _nodes.Add(node.Greater!);
            _nodes.Add(node.LessOrEqual!);
        }
    }

    public JxlMaTreeNode Root { get; }

    public int NodeCount => _nodes.Count;

    public int LeafCount => _leaves.Count;

    public IReadOnlyList<JxlMaTreeNode> Leaves => _leaves;

    /// <summary>The highest property index any split refers to, or -1 when no split uses a per-pixel property.</summary>
    public int MaxProperty
    {
        get
        {
            int max = -1;
            foreach (var node in _nodes)
            {
                max = Math.Max(max, node.Property);
            }

            return max;
        }
    }

    /// <summary>A tree that splits only on the channel index, giving each of <paramref name="predictors"/>' channels its own leaf.</summary>
    public static JxlMaTreeEncoder PerChannel(IReadOnlyList<ModularPredictor> predictors)
    {
        JxlMaTreeNode Build(int lo, int hi)
        {
            if (lo == hi)
            {
                return JxlMaTreeNode.Leaf(predictors[lo]);
            }

            int mid = (lo + hi) / 2;
            return JxlMaTreeNode.Inner(0, mid, Build(mid + 1, hi), Build(lo, mid));
        }

        return new JxlMaTreeEncoder(Build(0, predictors.Count - 1));
    }

    /// <summary>
    /// Follows splits on the two static properties (the channel index and the stream id) and returns the leaf, which
    /// must not depend on any per-pixel property.
    /// </summary>
    public JxlMaTreeNode LeafFor(int streamId, int channel)
    {
        var node = Root;
        while (node.Property >= 0)
        {
            int value = node.Property switch
            {
                0 => channel,
                1 => streamId,
                _ => throw new InvalidOperationException("The tree splits on a per-pixel property."),
            };
            node = value > node.Split ? node.Greater! : node.LessOrEqual!;
        }

        return node;
    }

    /// <summary>The leaf for <paramref name="channel"/> of a stream whose id the tree does not split on.</summary>
    public JxlMaTreeNode LeafForChannel(int channel) => LeafFor(0, channel);

    /// <summary>Appends the tree's serialization (the decoder's <c>MaTree.Read</c> input) to <paramref name="tokens"/>.</summary>
    public void WriteTokens(JxlTokenList tokens)
    {
        foreach (var node in _nodes)
        {
            if (node.Property >= 0)
            {
                tokens.Add(PropertyContext, (uint)node.Property + 1);
                tokens.Add(SplitValContext, PackSigned(node.Split));
                continue;
            }

            tokens.Add(PropertyContext, 0);
            tokens.Add(PredictorContext, (uint)node.Predictor);
            tokens.Add(OffsetContext, PackSigned(node.Offset));
            tokens.Add(MultiplierLogContext, 0);
            tokens.Add(MultiplierBitsContext, 0); // multiplier (0 + 1) << 0 = 1
        }
    }

    internal static uint PackSigned(int value) => (uint)((value << 1) ^ (value >> 31));
}
