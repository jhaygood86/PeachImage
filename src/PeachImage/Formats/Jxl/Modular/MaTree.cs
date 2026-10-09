using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Modular;

/// <summary>
/// A meta-adaptive context tree. Each inner node splits on one property (0 = channel index, 1 = stream id, then
/// per-pixel properties); a leaf selects a predictor, a prediction offset, a residual multiplier and an entropy context.
/// </summary>
internal sealed class MaTree
{
    /// <summary>Properties below this value are static: constant for a whole channel of a stream.</summary>
    public const int NumStaticProperties = 2;

    /// <summary>The number of per-pixel properties that don't refer to other channels.</summary>
    public const int NumNonRefProperties = NumStaticProperties + 13 + 1;

    /// <summary>The index of the weighted-predictor error property.</summary>
    public const int WeightedProperty = NumNonRefProperties - 1;

    /// <summary>Properties contributed by each earlier channel with matching geometry.</summary>
    public const int ExtraPropertiesPerChannel = 4;

    private const int MaxTreeSize = 1 << 22;
    private const int HeightLimit = 2048;

    private const int SplitValContext = 0;
    private const int PropertyContext = 1;
    private const int PredictorContext = 2;
    private const int OffsetContext = 3;
    private const int MultiplierLogContext = 4;
    private const int MultiplierBitsContext = 5;
    private const int NumTreeContexts = 6;

    private MaTree(int count)
    {
        Property = new int[count];
        SplitValue = new int[count];
        Left = new int[count];
        Right = new int[count];
        Predictor = new ModularPredictor[count];
        Offset = new int[count];
        Multiplier = new int[count];
    }

    public int Count => Property.Length;

    /// <summary>The split property, or -1 for a leaf.</summary>
    public int[] Property { get; }

    public int[] SplitValue { get; }

    /// <summary>For an inner node the child taken when the property is greater than the split value; for a leaf the leaf id (its context-map index).</summary>
    public int[] Left { get; }

    public int[] Right { get; }

    public ModularPredictor[] Predictor { get; }

    public int[] Offset { get; }

    public int[] Multiplier { get; }

    /// <summary>The number of properties a pixel needs, rounded as the format requires for the reference-channel properties.</summary>
    public int NumProperties { get; private set; }

    /// <summary>Whether any node refers to the weighted predictor (as a property or a predictor).</summary>
    public bool UsesWeightedPredictor { get; private set; }

    /// <summary>Reads a tree, rejecting trees with more than <paramref name="sizeLimit"/> nodes.</summary>
    public static MaTree Read(ref JxlBitReader br, int sizeLimit)
    {
        var code = JxlEntropyCode.Read(ref br, NumTreeContexts, out byte[] contextMap);
        if (code.DegenerateSymbols[contextMap[PropertyContext]] > 0)
        {
            throw new JxlDecodingException("Infinite MA tree.");
        }

        using var reader = new JxlSymbolReader(code, ref br);
        int limit = Math.Min(sizeLimit, MaxTreeSize);

        var property = new List<int>();
        var splitValue = new List<int>();
        var left = new List<int>();
        var right = new List<int>();
        var predictor = new List<ModularPredictor>();
        var offset = new List<int>();
        var multiplier = new List<int>();

        int leafId = 0;
        int toDecode = 1;
        while (toDecode > 0)
        {
            br.ThrowIfOverrun();
            if (property.Count > limit)
            {
                throw new JxlDecodingException($"The MA tree is too large: {property.Count} nodes versus {limit} allowed.");
            }

            toDecode--;
            uint prop1 = reader.ReadHybridUint(contextMap[PropertyContext], ref br);
            if (prop1 > 256)
            {
                throw new JxlDecodingException("Invalid MA tree property value.");
            }

            int prop = (int)prop1 - 1;
            if (prop == -1)
            {
                uint pred = reader.ReadHybridUint(contextMap[PredictorContext], ref br);
                if (pred > (uint)ModularPredictor.Average4)
                {
                    throw new JxlDecodingException("Invalid MA tree predictor.");
                }

                int predOffset = JxlFieldReader.UnpackSigned(reader.ReadHybridUint(contextMap[OffsetContext], ref br));
                uint mulLog = reader.ReadHybridUint(contextMap[MultiplierLogContext], ref br);
                if (mulLog >= 31)
                {
                    throw new JxlDecodingException("Invalid MA tree multiplier logarithm.");
                }

                uint mulBits = reader.ReadHybridUint(contextMap[MultiplierBitsContext], ref br);
                if (mulBits >= (1u << (int)(31 - mulLog)) - 1)
                {
                    throw new JxlDecodingException("Invalid MA tree multiplier.");
                }

                property.Add(-1);
                splitValue.Add(0);
                left.Add(leafId);
                right.Add(0);
                predictor.Add((ModularPredictor)pred);
                offset.Add(predOffset);
                multiplier.Add((int)((mulBits + 1) << (int)mulLog));
                leafId++;
                continue;
            }

            int split = JxlFieldReader.UnpackSigned(reader.ReadHybridUint(contextMap[SplitValContext], ref br));
            property.Add(prop);
            splitValue.Add(split);
            left.Add(property.Count + toDecode);
            right.Add(property.Count + toDecode + 1);
            predictor.Add(ModularPredictor.Zero);
            offset.Add(0);
            multiplier.Add(1);
            toDecode += 2;
        }

        br.ThrowIfOverrun();
        if (!reader.CheckFinalState())
        {
            throw new JxlDecodingException("ANS final state check failed while reading the MA tree.");
        }

        var tree = new MaTree(property.Count);
        property.CopyTo(tree.Property);
        splitValue.CopyTo(tree.SplitValue);
        left.CopyTo(tree.Left);
        right.CopyTo(tree.Right);
        predictor.CopyTo(tree.Predictor);
        offset.CopyTo(tree.Offset);
        multiplier.CopyTo(tree.Multiplier);
        tree.Validate();
        tree.Analyze();
        return tree;
    }

    // Verifies no root-to-leaf path has an unsatisfiable condition (a property both > a and <= b with b <= a).
    private void Validate()
    {
        if (Count == 0)
        {
            return;
        }

        int numProperties = 0;
        foreach (int p in Property)
        {
            numProperties = Math.Max(numProperties, p + 1);
        }

        var lower = new int[numProperties];
        var upper = new int[numProperties];
        Array.Fill(lower, int.MinValue);
        Array.Fill(upper, int.MaxValue);

        // Iterative depth-first walk: 0 = check and go left, 1 = go right, 2 = pop.
        var stack = new List<(int Node, int OrigLower, int OrigUpper, int Action)> { (0, 0, 0, 0) };
        while (stack.Count > 0)
        {
            if (stack.Count >= HeightLimit)
            {
                throw new JxlDecodingException("The MA tree is too tall.");
            }

            var (node, origLower, origUpper, action) = stack[^1];
            switch (action)
            {
                case 0:
                {
                    int p = Property[node];
                    if (p == -1)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }

                    int split = SplitValue[node];
                    int l = lower[p];
                    int u = upper[p];
                    if (l > split || u <= split)
                    {
                        throw new JxlDecodingException("Invalid MA tree.");
                    }

                    stack[^1] = (node, l, u, 1);
                    lower[p] = split + 1;
                    stack.Add((Left[node], 0, 0, 0));
                    continue;
                }

                case 1:
                {
                    int p = Property[node];
                    stack[^1] = (node, origLower, origUpper, 2);
                    lower[p] = origLower;
                    upper[p] = SplitValue[node];
                    stack.Add((Right[node], 0, 0, 0));
                    continue;
                }

                default:
                {
                    int p = Property[node];
                    upper[p] = origUpper;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
            }
        }
    }

    private void Analyze()
    {
        int maxProperty = -1;
        bool usesWeighted = false;
        for (int i = 0; i < Count; i++)
        {
            if (Property[i] >= NumStaticProperties)
            {
                maxProperty = Math.Max(maxProperty, Property[i]);
                if (Property[i] == WeightedProperty)
                {
                    usesWeighted = true;
                }
            }
            else if (Property[i] == -1 && Predictor[i] == ModularPredictor.Weighted)
            {
                usesWeighted = true;
            }
        }

        int needed = maxProperty + 1;
        NumProperties = needed > NumNonRefProperties
            ? ((needed - NumNonRefProperties + ExtraPropertiesPerChannel - 1) / ExtraPropertiesPerChannel * ExtraPropertiesPerChannel) + NumNonRefProperties
            : NumNonRefProperties;
        UsesWeightedPredictor = usesWeighted;
    }
}
