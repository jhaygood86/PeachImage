namespace PeachImage.Internal.Icc;

internal enum IccLutType
{
    Lut8,
    Lut16,
    LutAB,
    LutBA,
}

internal enum IccLutElements
{
    AB,
    BA,
    AMB,
    BMA,
    MB,
    BM,
    B,
}

/// <summary>
/// A parsed <c>mft1</c>/<c>mft2</c>/<c>mAB </c>/<c>mBA </c> multi-function LUT tag (ICC.1:2010 §10.9-10.13):
/// up to three curve stages (A/M/B) around an optional matrix and an optional N-dimensional CLUT. Ported from
/// Wacton/Unicolour's <c>Icc/Luts.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed class IccLuts
{
    internal IccCurve[]? ACurves { get; }

    internal IccClut? Clut { get; }

    internal IccCurve[]? MCurves { get; }

    internal IccMatrices? Matrices { get; }

    internal IccCurve[] BCurves { get; }

    internal IccLutType Type { get; }

    internal bool IsDeviceToPcs { get; }

    internal IccLutElements Elements { get; }

    private IccLuts(IccCurve[]? aCurves, IccClut? clut, IccCurve[]? mCurves, IccMatrices? matrices, IccCurve[] bCurves, IccLutType type, bool isDeviceToPcs)
    {
        ACurves = aCurves;
        Clut = clut;
        MCurves = mCurves;
        Matrices = matrices;
        BCurves = bCurves;
        Type = type;
        IsDeviceToPcs = isDeviceToPcs;

        bool hasA = ACurves != null;
        bool hasM = MCurves != null && Matrices != null;
        Elements = IsDeviceToPcs switch
        {
            true when hasA => hasM ? IccLutElements.AMB : IccLutElements.AB,
            true => hasM ? IccLutElements.MB : IccLutElements.B,
            false when hasM => hasA ? IccLutElements.BMA : IccLutElements.BM,
            false => hasA ? IccLutElements.BA : IccLutElements.B,
        };
    }

    internal static IccLuts AToBFromStream(Stream stream) => FromStream(stream, isDeviceToPcs: true);

    internal static IccLuts BToAFromStream(Stream stream) => FromStream(stream, isDeviceToPcs: false);

    private static IccLuts FromStream(Stream stream, bool isDeviceToPcs)
    {
        string lutSignature = stream.ReadSignature();
        stream.Seek(-4, SeekOrigin.Current);
        return lutSignature switch
        {
            IccSignatures.MultiFunctionTable1Byte => ReadTables(stream, isDeviceToPcs, is8Bit: true),
            IccSignatures.MultiFunctionTable2Byte => ReadTables(stream, isDeviceToPcs, is8Bit: false),
            IccSignatures.MultiFunctionAToB => ReadTablesAb(stream, isDeviceToPcs),
            IccSignatures.MultiFunctionBToA => ReadTablesAb(stream, isDeviceToPcs),
            _ => throw new NotSupportedException($"Unsupported ICC LUT type signature '{lutSignature}'."),
        };
    }

    private static IccLuts ReadTables(Stream stream, bool isDeviceToPcs, bool is8Bit)
    {
        stream.ReadSignature();
        stream.ReadBytes(4); // reserved
        byte inputChannels = stream.ReadUInt8();
        byte outputChannels = stream.ReadUInt8();
        byte clutGridPoints = stream.ReadUInt8();
        stream.ReadUInt8(); // padding

        // These 9 e1-e9 matrix values only apply to output ("prtr") profiles with an XYZ PCS, and must be the
        // identity matrix otherwise (ICC.1:2010 §10.10/§10.11) -- CMYK profiles are never RGB-matrix-eligible,
        // so like Unicolour, this doesn't attempt to apply them; they're read only to advance the stream.
        stream.ReadBytes(4 * 9);

        return is8Bit
            ? Read8BitTables(stream, inputChannels, clutGridPoints, outputChannels, isDeviceToPcs)
            : Read16BitTables(stream, inputChannels, clutGridPoints, outputChannels, isDeviceToPcs);
    }

    private static IccLuts ReadTablesAb(Stream stream, bool isDeviceToPcs)
    {
        stream.ReadSignature();
        stream.ReadBytes(4); // reserved
        byte inputChannels = stream.ReadUInt8();
        byte outputChannels = stream.ReadUInt8();
        stream.ReadBytes(2); // padding
        uint bCurvesOffset = stream.ReadUInt32();
        uint matrixOffset = stream.ReadUInt32();
        uint mCurvesOffset = stream.ReadUInt32();
        uint clutOffset = stream.ReadUInt32();
        uint aCurvesOffset = stream.ReadUInt32();

        int bCurvesCount = isDeviceToPcs ? outputChannels : inputChannels;
        int mCurvesCount = isDeviceToPcs ? outputChannels : inputChannels;
        int aCurvesCount = isDeviceToPcs ? inputChannels : outputChannels;

        var bCurves = ReadCurves(stream, bCurvesOffset, bCurvesCount)!;
        var matrices = ReadMatrices(stream, matrixOffset);
        var mCurves = ReadCurves(stream, mCurvesOffset, mCurvesCount);
        var clut = ReadClut(stream, clutOffset, inputChannels, outputChannels);
        var aCurves = ReadCurves(stream, aCurvesOffset, aCurvesCount);

        var lutType = isDeviceToPcs ? IccLutType.LutAB : IccLutType.LutBA;
        return new IccLuts(aCurves, clut, mCurves, matrices, bCurves, lutType, isDeviceToPcs);
    }

    private static IccLuts Read8BitTables(Stream stream, byte inputChannels, byte clutGridPoints, byte outputChannels, bool isDeviceToPcs)
    {
        const int tableEntries = 256;

        var inputCurves = new IccCurve[inputChannels];
        for (int i = 0; i < inputChannels; i++)
        {
            inputCurves[i] = new IccTableCurve(ReadTableAs01(stream, tableEntries, is16Bit: false));
        }

        int clutValuesSize = (int)Math.Pow(clutGridPoints, inputChannels) * outputChannels;
        var clutValues = ReadTableAs01(stream, clutValuesSize, is16Bit: false);
        var clut = new IccClut(clutValues, inputChannels, clutGridPoints, outputChannels);

        var outputCurves = new IccCurve[outputChannels];
        for (int i = 0; i < outputChannels; i++)
        {
            outputCurves[i] = new IccTableCurve(ReadTableAs01(stream, tableEntries, is16Bit: false));
        }

        var aCurves = isDeviceToPcs ? inputCurves : outputCurves;
        var bCurves = isDeviceToPcs ? outputCurves : inputCurves;
        return new IccLuts(aCurves, clut, mCurves: null, matrices: null, bCurves, IccLutType.Lut8, isDeviceToPcs);
    }

    private static IccLuts Read16BitTables(Stream stream, byte inputChannels, byte clutGridPoints, byte outputChannels, bool isDeviceToPcs)
    {
        int inputTableEntries = stream.ReadUInt16();
        int outputTableEntries = stream.ReadUInt16();

        var inputCurves = new IccCurve[inputChannels];
        for (int i = 0; i < inputChannels; i++)
        {
            inputCurves[i] = new IccTableCurve(ReadTableAs01(stream, inputTableEntries, is16Bit: true));
        }

        int clutValuesSize = (int)Math.Pow(clutGridPoints, inputChannels) * outputChannels;
        var clutValues = ReadTableAs01(stream, clutValuesSize, is16Bit: true);
        var clut = new IccClut(clutValues, inputChannels, clutGridPoints, outputChannels);

        var outputCurves = new IccCurve[outputChannels];
        for (int i = 0; i < outputChannels; i++)
        {
            outputCurves[i] = new IccTableCurve(ReadTableAs01(stream, outputTableEntries, is16Bit: true));
        }

        var aCurves = isDeviceToPcs ? inputCurves : outputCurves;
        var bCurves = isDeviceToPcs ? outputCurves : inputCurves;
        return new IccLuts(aCurves, clut, mCurves: null, matrices: null, bCurves, IccLutType.Lut16, isDeviceToPcs);
    }

    private static double[] ReadTableAs01(Stream stream, int entries, bool is16Bit)
    {
        var result = new double[entries];
        for (int i = 0; i < entries; i++)
        {
            result[i] = is16Bit ? stream.ReadUInt16() / 65535.0 : stream.ReadUInt8() / 255.0;
        }

        return result;
    }

    private static IccCurve[]? ReadCurves(Stream stream, uint offset, int count)
    {
        if (offset == 0)
        {
            return null;
        }

        var curves = new IccCurve[count];
        stream.Seek(offset, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            curves[i] = IccCurve.FromStream(stream);
            MoveToFourByteBoundary(stream, offset);
        }

        return curves;
    }

    private static IccMatrices? ReadMatrices(Stream stream, uint offset)
    {
        if (offset == 0)
        {
            return null;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        Span<double> e = stackalloc double[12];
        for (int i = 0; i < 12; i++)
        {
            e[i] = stream.ReadS15Fixed16();
        }

        MoveToFourByteBoundary(stream, offset);

        var multiply = new IccMatrix3x3(
            e[0], e[1], e[2],
            e[3], e[4], e[5],
            e[6], e[7], e[8]);
        var offsetVector = new IccVector3(e[9], e[10], e[11]);
        return new IccMatrices(multiply, offsetVector);
    }

    private static void MoveToFourByteBoundary(Stream stream, long startPosition)
    {
        long endPosition = stream.Position;
        int distanceIntoBoundary = (int)((endPosition - startPosition) % 4);
        if (distanceIntoBoundary <= 0)
        {
            return;
        }

        stream.ReadBytes(4 - distanceIntoBoundary);
    }

    private static IccClut? ReadClut(Stream stream, uint offset, byte inputChannels, byte outputChannels)
    {
        if (offset == 0)
        {
            return null;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        Span<byte> allGridPoints = stackalloc byte[16];
        stream.ReadExactlyIcc(allGridPoints);
        byte precision = stream.ReadUInt8();
        stream.ReadBytes(3); // reserved

        bool is8Bit = precision == 1;
        int clutValuesSize = outputChannels;
        for (int i = 0; i < inputChannels; i++)
        {
            clutValuesSize *= allGridPoints[i];
        }

        var clutValues = ReadTableAs01(stream, clutValuesSize, is16Bit: !is8Bit);

        // Every dimension is assumed to share the same grid-point count -- no real-world profile encountered
        // during Unicolour's own development (or this port's) violates this, matching its own note.
        var clut = new IccClut(clutValues, inputChannels, allGridPoints[0], outputChannels);
        MoveToFourByteBoundary(stream, offset);
        return clut;
    }
}
