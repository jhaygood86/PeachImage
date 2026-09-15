namespace PeachImage.Internal.Icc;

/// <summary>
/// A one-dimensional tone-reproduction curve — either a sampled table (<c>curv</c>, ICC.1:2010 §10.5) or a
/// parametric function (<c>para</c>, §10.16). Ported from Wacton/Unicolour's <c>Icc/Curve.cs</c> (MIT
/// license) — see THIRD-PARTY-LICENSES.md. <see cref="Lookup"/> is the per-pixel hot path and allocates
/// nothing; only <see cref="Inverse"/> (called once per profile, at setup time) allocates a table.
/// </summary>
internal abstract record IccCurve
{
    internal abstract double Lookup(double value);

    protected abstract double[] AsTable();

    internal static IccCurve FromStream(Stream stream)
    {
        string curveSignature = stream.ReadSignature();
        stream.Seek(-4, SeekOrigin.Current); // Revert so curve-specific reading is self-contained below.
        return curveSignature switch
        {
            IccSignatures.Curve => ReadCurve(stream),
            IccSignatures.ParametricCurve => ReadParametricCurve(stream),
            _ => throw new NotSupportedException($"Unsupported ICC curve type signature '{curveSignature}'."),
        };
    }

    private static IccCurve ReadCurve(Stream stream)
    {
        stream.ReadSignature();
        stream.ReadBytes(4); // reserved
        uint entries = stream.ReadUInt32();
        switch (entries)
        {
            case 0:
                return IccTableCurve.Identity;
            case 1:
            {
                double gamma = stream.ReadU8Fixed8();
                return new IccParametricCurve(x => Math.Pow(x, gamma), $"gamma {gamma}");
            }

            default:
            {
                var table = new double[entries];
                for (int i = 0; i < entries; i++)
                {
                    table[i] = stream.ReadUInt16() / 65535.0;
                }

                return new IccTableCurve(table);
            }
        }
    }

    private static IccParametricCurve ReadParametricCurve(Stream stream)
    {
        stream.ReadSignature();
        stream.ReadBytes(4); // reserved
        ushort functionType = stream.ReadUInt16();
        stream.ReadBytes(2); // reserved

        Func<double, double> function;
        switch (functionType)
        {
            case 0:
            {
                double g = stream.ReadS15Fixed16();
                function = x => Math.Pow(x, g);
                break;
            }

            case 1:
            {
                double g = stream.ReadS15Fixed16();
                double a = stream.ReadS15Fixed16();
                double b = stream.ReadS15Fixed16();
                function = x => x < -b / a ? 0 : Math.Pow((a * x) + b, g);
                break;
            }

            case 2:
            {
                double g = stream.ReadS15Fixed16();
                double a = stream.ReadS15Fixed16();
                double b = stream.ReadS15Fixed16();
                double c = stream.ReadS15Fixed16();
                function = x => x < -b / a ? c : Math.Pow((a * x) + b, g) + c;
                break;
            }

            case 3:
            {
                double g = stream.ReadS15Fixed16();
                double a = stream.ReadS15Fixed16();
                double b = stream.ReadS15Fixed16();
                double c = stream.ReadS15Fixed16();
                double d = stream.ReadS15Fixed16();
                function = x => x < d ? c * x : Math.Pow((a * x) + b, g);
                break;
            }

            default:
            {
                double g = stream.ReadS15Fixed16();
                double a = stream.ReadS15Fixed16();
                double b = stream.ReadS15Fixed16();
                double c = stream.ReadS15Fixed16();
                double d = stream.ReadS15Fixed16();
                double e = stream.ReadS15Fixed16();
                double f = stream.ReadS15Fixed16();
                function = x => x < d ? (c * x) + f : Math.Pow((a * x) + b, g) + e;
                break;
            }
        }

        return new IccParametricCurve(function, $"type {functionType}");
    }

    /// <summary>Builds the inverse of this curve as a 2048-entry table. Setup-time only — never called per pixel.</summary>
    internal IccCurve Inverse()
    {
        double[] tableToInvert = AsTable();
        const int length = 2048;
        var inverse = new double[length];
        for (int i = 0; i < length; i++)
        {
            double value = i / (double)(length - 1);
            inverse[i] = GetNormalizedIndex(tableToInvert, value);
        }

        return new IccTableCurve(inverse);
    }

    private static double GetNormalizedIndex(double[] table, double value)
    {
        int index = Array.BinarySearch(table, value);

        double exactIndex;
        if (index >= 0)
        {
            exactIndex = index;
        }
        else
        {
            // If the value isn't found, BinarySearch returns the bitwise complement of the first index larger than it.
            int upperIndex = ~index;
            int lowerIndex = upperIndex - 1;
            double valueDifference = value - table[lowerIndex];
            double totalDifference = table[upperIndex] - table[lowerIndex];
            double indexDistance = valueDifference / totalDifference;
            exactIndex = lowerIndex + indexDistance;
        }

        return exactIndex / (table.Length - 1);
    }
}

internal sealed record IccTableCurve(double[] Table) : IccCurve
{
    internal static IccTableCurve Identity { get; } = new([0.0, 1.0]);

    private bool IsIdentity => Table.Length == 2 && Table[0] == 0.0 && Table[1] == 1.0;

    internal override double Lookup(double value)
    {
        if (IsIdentity)
        {
            // Identity does not clamp its input, whereas a table lookup below would.
            return value;
        }

        var (lowerIndex, upperIndex, distance) = IccLut.Lookup(Table.Length, value);
        return Table[lowerIndex] + ((Table[upperIndex] - Table[lowerIndex]) * distance);
    }

    protected override double[] AsTable() => Table;
}

internal sealed record IccParametricCurve(Func<double, double> Function, string Name) : IccCurve
{
    /*
     * The ICC spec says curve values should be clamped to [0, 1], but this doesn't make sense for LAB
     * parametric curves (e.g. the sRGB v4 preference profile) which aren't in that range at all, and the
     * DemoIccMAX reference implementation does not clip here either -- so this doesn't clamp, matching that
     * reference behavior (ported from Unicolour's own comment making the same observation).
     */
    internal override double Lookup(double value) => Function(value);

    protected override double[] AsTable()
    {
        const int length = 2048;
        var table = new double[length];
        for (int i = 0; i < length; i++)
        {
            table[i] = Lookup(i / (double)(length - 1));
        }

        return table;
    }
}
