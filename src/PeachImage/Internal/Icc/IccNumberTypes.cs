namespace PeachImage.Internal.Icc;

/// <summary>
/// Big-endian binary number parsing for ICC profile streams. Ported from Wacton/Unicolour's
/// <c>Icc/NumberTypes.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal static class IccNumberTypes
{
    internal static byte ReadUInt8(this Stream stream) => (byte)stream.ReadByte();

    internal static ushort ReadUInt16(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactlyIcc(bytes);
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }

    internal static uint ReadUInt32(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactlyIcc(bytes);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    internal static ulong ReadUInt64(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactlyIcc(bytes);
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    /// <summary>An unsigned 8.8 fixed-point number (ICC.1:2010 §5.3.2).</summary>
    internal static double ReadU8Fixed8(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactlyIcc(bytes);
        return bytes[0] + (bytes[1] / 256.0);
    }

    /// <summary>A signed 15.16 fixed-point number (ICC.1:2010 §5.3.10).</summary>
    internal static double ReadS15Fixed16(this Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactlyIcc(bytes);
        short integer = (short)((bytes[0] << 8) | bytes[1]);
        double fraction = ((bytes[2] << 8) | bytes[3]) / 65536.0;
        return integer + fraction;
    }

    internal static byte[] ReadBytes(this Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactlyIcc(buffer);
        return buffer;
    }

    internal static void ReadExactlyIcc(this Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                throw new EndOfStreamException("Not enough bytes in stream to parse an ICC profile.");
            }

            totalRead += read;
        }
    }

    internal static double[] From8BitPrecision(ReadOnlySpan<byte> values)
    {
        var result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i] / 255.0;
        }

        return result;
    }

    internal static double[] From16BitPrecision(ReadOnlySpan<ushort> values)
    {
        var result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i] / 65535.0;
        }

        return result;
    }
}
