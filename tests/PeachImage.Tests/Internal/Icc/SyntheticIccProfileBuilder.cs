using System.Text;

namespace PeachImage.Tests.Internal.Icc;

/// <summary>
/// Hand-builds minimal, spec-valid ICC profiles (ICC.1:2010) byte-for-byte, for testing
/// <c>IccTransformTrcMatrix</c>/<c>IccTransformTrcGrey</c> — transform kinds no real-world corpus fixture
/// encountered during investigation exercises (the only real embedded profile found, on <c>ycck.jpg</c>, is
/// CMYK/AToB). Both profiles use a linear (gamma=1.0) TRC so their expected behavior is trivial to reason
/// about: device white (1.0) maps to the PCS white point, device black (0.0) maps to PCS (0,0,0).
/// </summary>
internal static class SyntheticIccProfileBuilder
{
    private const int HeaderSize = 128;

    /// <summary>
    /// A minimal RGB-matrix-TRC profile: linear rTRC/gTRC/bTRC curves plus the standard sRGB primaries
    /// (D50-adapted XYZ, the widely-published values used to derive the real sRGB ICC profile). Optionally
    /// declares an explicit <c>bkpt</c> tag (for black point compensation tests) -- otherwise the profile has
    /// none, and its black point must be estimated from device black instead.
    /// </summary>
    internal static byte[] BuildRgbTrcMatrixProfile(double? blackPointX = null, double? blackPointY = null, double? blackPointZ = null)
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("rTRC", BuildLinearCurve()),
            ("gTRC", BuildLinearCurve()),
            ("bTRC", BuildLinearCurve()),
            ("rXYZ", BuildXyzType(0.4360747, 0.2225045, 0.0139322)),
            ("gXYZ", BuildXyzType(0.3850649, 0.7168786, 0.0971045)),
            ("bXYZ", BuildXyzType(0.1430804, 0.0606169, 0.7141733)),
            ("wtpt", BuildXyzType(0.9642, 1.0000, 0.8249)),
        };

        if (blackPointX is { } bx && blackPointY is { } by && blackPointZ is { } bz)
        {
            tags.Add(("bkpt", BuildXyzType(bx, by, bz)));
        }

        return BuildProfile("RGB ", tags.ToArray());
    }

    /// <summary>
    /// A minimal single-curve grey profile: a linear kTRC curve. Optionally declares an explicit <c>bkpt</c>
    /// tag (for black point compensation tests) -- otherwise the profile has none, and its black point must
    /// be estimated from device black instead.
    /// </summary>
    internal static byte[] BuildGrayTrcProfile(double? blackPointX = null, double? blackPointY = null, double? blackPointZ = null)
    {
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("kTRC", BuildLinearCurve()),
            ("wtpt", BuildXyzType(0.9642, 1.0000, 0.8249)),
        };

        if (blackPointX is { } bx && blackPointY is { } by && blackPointZ is { } bz)
        {
            tags.Add(("bkpt", BuildXyzType(bx, by, bz)));
        }

        return BuildProfile("GRAY", tags.ToArray());
    }

    private static byte[] BuildProfile(string dataColorSpace, (string Signature, byte[] Data)[] tags)
    {
        int tagTableSize = 4 + (tags.Length * 12);
        int tagTableStart = HeaderSize;
        int tagDataStart = tagTableStart + tagTableSize;

        var offsets = new int[tags.Length];
        int cursor = tagDataStart;
        for (int i = 0; i < tags.Length; i++)
        {
            offsets[i] = cursor;
            cursor += tags[i].Data.Length;
        }

        int totalSize = cursor;
        var buffer = new byte[totalSize];

        // Header (ICC.1:2010 §7.2). Fields this test doesn't care about (CMM type, version detail, dates,
        // platform/flags/manufacturer/attributes, PCS illuminant, creator, profile id) are left zeroed --
        // PeachImage's own IccHeader never reads most of them (see its own remarks on why).
        WriteUInt32(buffer, 0, (uint)totalSize);
        WriteAscii4(buffer, 12, "mntr"); // ProfileClass: Display -- one of the four ErrorIfUnsupported accepts.
        WriteAscii4(buffer, 16, dataColorSpace);
        WriteAscii4(buffer, 20, "XYZ "); // Pcs
        WriteAscii4(buffer, 36, "acsp"); // ProfileFileSignature -- required.
        WriteUInt32(buffer, 64, 0); // Intent: Perceptual.

        // Tag table (§7.3).
        WriteUInt32(buffer, tagTableStart, (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            int entryOffset = tagTableStart + 4 + (i * 12);
            WriteAscii4(buffer, entryOffset, tags[i].Signature);
            WriteUInt32(buffer, entryOffset + 4, (uint)offsets[i]);
            WriteUInt32(buffer, entryOffset + 8, (uint)tags[i].Data.Length);
            Array.Copy(tags[i].Data, 0, buffer, offsets[i], tags[i].Data.Length);
        }

        return buffer;
    }

    /// <summary>A <c>curv</c> tag (§10.5) with a single gamma=1.0 entry -- a linear (identity) tone curve.</summary>
    private static byte[] BuildLinearCurve()
    {
        var data = new byte[14];
        WriteAscii4(data, 0, "curv");
        WriteUInt32(data, 8, 1); // entry count = 1 -> single gamma value follows.
        WriteU8Fixed8(data, 12, 1.0); // gamma = 1.0 (linear).
        return data;
    }

    /// <summary>An <c>XYZType</c> tag (§10.24).</summary>
    private static byte[] BuildXyzType(double x, double y, double z)
    {
        var data = new byte[20];
        WriteAscii4(data, 0, "XYZ ");
        WriteS15Fixed16(data, 8, x);
        WriteS15Fixed16(data, 12, y);
        WriteS15Fixed16(data, 16, z);
        return data;
    }

    private static void WriteAscii4(byte[] buffer, int offset, string signature)
    {
        var bytes = Encoding.ASCII.GetBytes(signature);
        Array.Copy(bytes, 0, buffer, offset, 4);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteU8Fixed8(byte[] buffer, int offset, double value)
    {
        byte integer = (byte)value;
        byte fraction = (byte)Math.Round((value - integer) * 256.0);
        buffer[offset] = integer;
        buffer[offset + 1] = fraction;
    }

    private static void WriteS15Fixed16(byte[] buffer, int offset, double value)
    {
        int fixedValue = (int)Math.Round(value * 65536.0);
        buffer[offset] = (byte)(fixedValue >> 24);
        buffer[offset + 1] = (byte)(fixedValue >> 16);
        buffer[offset + 2] = (byte)(fixedValue >> 8);
        buffer[offset + 3] = (byte)fixedValue;
    }
}
