using System.Buffers.Binary;
using System.Text;
using PeachImage.Internal.Icc;

namespace PeachImage.Internal.Color;

/// <summary>Writes the compact ICC v4 matrix/TRC display profiles behind <see cref="ColorProfiles"/> (about 450 bytes each).</summary>
internal static class IccProfileWriter
{
    public static byte[] Write(NamedColorSpace space)
    {
        var transfer = NamedSpaces.Transfer(space);
        if (transfer is TransferFunction.Pq or TransferFunction.Hlg)
        {
            throw new NotSupportedException($"{space} is an HDR color space that an ICC matrix/TRC profile cannot describe; signal it with CicpCode.Of({space}) instead.");
        }

        var matrix = NamedSpaces.ToXyzD50(space).ToArray();
        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", MultiLocalized(Description(space))),
            ("wtpt", Xyz(NamedSpaces.D50White.X, NamedSpaces.D50White.Y, NamedSpaces.D50White.Z)),
            ("rXYZ", Xyz(matrix[0], matrix[3], matrix[6])),
            ("gXYZ", Xyz(matrix[1], matrix[4], matrix[7])),
            ("bXYZ", Xyz(matrix[2], matrix[5], matrix[8])),
            ("cprt", MultiLocalized("No copyright, use freely")),
        };

        byte[] trc = Curve(transfer);
        // The three channels share one curve; ICC allows tags to point at the same data.
        int tagCount = tags.Count + 3;
        int offset = 128 + 4 + (tagCount * 12);
        var table = new byte[4 + (tagCount * 12)];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tagCount);

        using var data = new MemoryStream();
        int slot = 0;
        void AddTag(string signature, int dataOffset, int length)
        {
            int p = 4 + (slot++ * 12);
            Encoding.ASCII.GetBytes(signature, table.AsSpan(p, 4));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(p + 4), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(p + 8), (uint)length);
        }

        foreach (var (signature, bytes) in tags)
        {
            AddTag(signature, offset + (int)data.Length, bytes.Length);
            data.Write(bytes);
            Pad(data);
        }

        int trcOffset = offset + (int)data.Length;
        data.Write(trc);
        Pad(data);
        AddTag("rTRC", trcOffset, trc.Length);
        AddTag("gTRC", trcOffset, trc.Length);
        AddTag("bTRC", trcOffset, trc.Length);

        var profile = new byte[offset + (int)data.Length];
        WriteHeader(profile);
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        table.CopyTo(profile, 128);
        data.ToArray().CopyTo(profile, offset);
        return profile;
    }

    private static void WriteHeader(Span<byte> h)
    {
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 0x04200000); // version 4.2
        Encoding.ASCII.GetBytes("mntr", h[12..16]);
        Encoding.ASCII.GetBytes("RGB ", h[16..20]);
        Encoding.ASCII.GetBytes("XYZ ", h[20..24]);
        BinaryPrimitives.WriteUInt16BigEndian(h[24..], 2024); // a fixed date keeps the bytes reproducible
        BinaryPrimitives.WriteUInt16BigEndian(h[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(h[28..], 1);
        Encoding.ASCII.GetBytes("acsp", h[36..40]);
        BinaryPrimitives.WriteUInt32BigEndian(h[64..], 0); // perceptual
        BinaryPrimitives.WriteInt32BigEndian(h[68..], S15(NamedSpaces.D50White.X));
        BinaryPrimitives.WriteInt32BigEndian(h[72..], S15(NamedSpaces.D50White.Y));
        BinaryPrimitives.WriteInt32BigEndian(h[76..], S15(NamedSpaces.D50White.Z));
    }

    private static string Description(NamedColorSpace space) => space switch
    {
        NamedColorSpace.Srgb => "sRGB",
        NamedColorSpace.LinearSrgb => "Linear sRGB",
        NamedColorSpace.DisplayP3 => "Display P3",
        NamedColorSpace.LinearDisplayP3 => "Linear Display P3",
        NamedColorSpace.Rec2020 => "Rec. 2020",
        NamedColorSpace.LinearRec2020 => "Linear Rec. 2020",
        _ => space.ToString(),
    };

    private static int S15(double value) => (int)Math.Round(value * 65536.0);

    private static byte[] Xyz(double x, double y, double z)
    {
        var b = new byte[20];
        Encoding.ASCII.GetBytes("XYZ ", b.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), S15(x));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), S15(y));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), S15(z));
        return b;
    }

    private static byte[] MultiLocalized(string text)
    {
        int length = text.Length * 2;
        var b = new byte[28 + length];
        Encoding.ASCII.GetBytes("mluc", b.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), 12);
        Encoding.ASCII.GetBytes("enUS", b.AsSpan(16, 4));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)length);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(24), 28);
        Encoding.BigEndianUnicode.GetBytes(text, b.AsSpan(28));
        return b;
    }

    private static byte[] Curve(TransferFunction transfer)
    {
        if (transfer == TransferFunction.Linear)
        {
            var identity = new byte[12];
            Encoding.ASCII.GetBytes("curv", identity.AsSpan(0, 4)); // zero entries is the identity curve
            return identity;
        }

        // Parametric type 3: Y = (aX + b)^g for X >= d, cX below.
        (double g, double a, double b, double c, double d) = transfer == TransferFunction.Srgb
            ? (2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)
            : (1 / 0.45, 1 / 1.099, 0.099 / 1.099, 1 / 4.5, 0.081);
        var para = new byte[32];
        Encoding.ASCII.GetBytes("para", para.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16BigEndian(para.AsSpan(8), 3);
        BinaryPrimitives.WriteInt32BigEndian(para.AsSpan(12), S15(g));
        BinaryPrimitives.WriteInt32BigEndian(para.AsSpan(16), S15(a));
        BinaryPrimitives.WriteInt32BigEndian(para.AsSpan(20), S15(b));
        BinaryPrimitives.WriteInt32BigEndian(para.AsSpan(24), S15(c));
        BinaryPrimitives.WriteInt32BigEndian(para.AsSpan(28), S15(d));
        return para;
    }

    private static void Pad(MemoryStream stream)
    {
        while ((stream.Length & 3) != 0)
        {
            stream.WriteByte(0);
        }
    }
}
