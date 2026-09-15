using System.Text;

namespace PeachImage.Internal.Icc;

/// <summary>
/// ICC header field parsing helpers. Ported from Wacton/Unicolour's <c>Icc/DataTypes.cs</c> (MIT license) —
/// see THIRD-PARTY-LICENSES.md.
/// </summary>
internal static class IccDataTypes
{
    internal static string ReadSignature(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactlyIcc(bytes);
        return Encoding.ASCII.GetString(bytes);
    }

    internal static Version ReadVersion(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactlyIcc(bytes);
        int major = bytes[0];
        int minor = (bytes[1] >> 4) & 0b1111;
        int bugFix = bytes[1] & 0b1111;
        return new Version(major, minor, bugFix);
    }

    internal static (double X, double Y, double Z) ReadXyzNumber(this Stream stream)
    {
        double x = stream.ReadS15Fixed16();
        double y = stream.ReadS15Fixed16();
        double z = stream.ReadS15Fixed16();
        return (x, y, z);
    }

    internal static IccXyzType ReadXyzType(this Stream stream)
    {
        stream.ReadSignature();
        stream.ReadBytes(4); // reserved
        var (x, y, z) = stream.ReadXyzNumber();
        return new IccXyzType(x, y, z);
    }
}

/// <summary>
/// The ICC "XYZType" tag payload (ICC.1:2010 §10.24). A reference type so a missing tag can be represented as
/// <see langword="null"/> rather than an ambiguous all-zero value.
/// </summary>
internal sealed record IccXyzType(double X, double Y, double Z);
