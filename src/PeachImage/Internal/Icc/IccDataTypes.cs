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

    /// <summary>
    /// Reads a human-readable text tag payload in any of the three forms a profile's <c>desc</c> tag takes:
    /// v4's <c>multiLocalizedUnicodeType</c> (<c>mluc</c>, ICC.1:2010 §10.15, UTF-16BE records -- the first
    /// <c>en</c> record wins, else the first record), v2's <c>textDescriptionType</c> (<c>desc</c>, ICC v2 §6.5.17,
    /// a NUL-terminated ASCII string), or plain <c>textType</c> (<c>text</c>, §10.24). Returns
    /// <see langword="null"/> for an unrecognized or malformed payload instead of throwing, since a description
    /// is purely diagnostic.
    /// </summary>
    internal static string? ReadTextDescription(byte[] data)
    {
        if (data.Length < 12)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            string type = stream.ReadSignature();
            stream.ReadBytes(4); // reserved
            switch (type)
            {
                case "desc":
                {
                    uint count = stream.ReadUInt32(); // includes the trailing NUL
                    return DecodeAscii(data.AsSpan(12, (int)Math.Min(count, (uint)(data.Length - 12))));
                }

                case "text":
                    return DecodeAscii(data.AsSpan(8));

                case "mluc":
                    return ReadMultiLocalizedUnicode(data, stream);

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static string? ReadMultiLocalizedUnicode(byte[] data, Stream stream)
    {
        uint recordCount = stream.ReadUInt32();
        uint recordSize = stream.ReadUInt32();
        if (recordCount == 0 || recordSize < 12)
        {
            return null;
        }

        string? first = null;
        for (uint i = 0; i < recordCount; i++)
        {
            string language = Encoding.ASCII.GetString(stream.ReadBytes(2));
            stream.ReadBytes(2); // country
            uint length = stream.ReadUInt32();
            uint offset = stream.ReadUInt32();
            stream.ReadBytes((int)recordSize - 12);

            if (offset > data.Length || length > data.Length - offset)
            {
                continue;
            }

            string text = Encoding.BigEndianUnicode.GetString(data, (int)offset, (int)length).TrimEnd('\0');
            if (language == "en")
            {
                return text;
            }

            first ??= text;
        }

        return first;
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? bytes[..nul] : bytes);
    }
}

/// <summary>
/// The ICC "XYZType" tag payload (ICC.1:2010 §10.24). A reference type so a missing tag can be represented as
/// <see langword="null"/> rather than an ambiguous all-zero value.
/// </summary>
internal sealed record IccXyzType(double X, double Y, double Z);
