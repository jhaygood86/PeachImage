using PeachImage.Internal.Color;

namespace PeachImage;

/// <summary>
/// Built-in ICC profiles for the <see cref="NamedColorSpace"/> values, so callers never need profile bytes of their own for sRGB, Display P3,
/// Rec. 2020 or their linear variants. Each is a compact (about 450 byte) ICC v4 matrix/TRC display profile generated from the space's
/// published primaries, D50-adapted with Bradford.
/// </summary>
public static class ColorProfiles
{
    private static readonly byte[]?[] Bytes = new byte[]?[Enum.GetValues<NamedColorSpace>().Length];
    private static readonly IccColorProfile?[] Parsed = new IccColorProfile?[Bytes.Length];

    /// <summary>The profile bytes for <paramref name="space"/>, ready to embed (the same array on every call; do not modify it).</summary>
    /// <exception cref="NotSupportedException"><paramref name="space"/> is PQ or HLG, which no matrix/TRC profile can describe; use <see cref="CicpCode.Of(NamedColorSpace)"/>.</exception>
    public static ReadOnlyMemory<byte> GetBytes(NamedColorSpace space) => BytesOf(space);

    /// <summary>The parsed profile for <paramref name="space"/>.</summary>
    /// <exception cref="NotSupportedException"><paramref name="space"/> is PQ or HLG.</exception>
    public static IccColorProfile Get(NamedColorSpace space)
    {
        int index = Index(space);
        return Volatile.Read(ref Parsed[index]) ?? (Parsed[index] = new IccColorProfile(BytesOf(space)));
    }

    /// <summary>Whether an ICC profile can describe <paramref name="space"/> (false for <see cref="NamedColorSpace.Rec2100Pq"/> and <see cref="NamedColorSpace.Rec2100Hlg"/>).</summary>
    public static bool HasProfile(NamedColorSpace space) => NamedSpaces.Transfer(space) is not (TransferFunction.Pq or TransferFunction.Hlg);

    private static int Index(NamedColorSpace space) =>
        (uint)space < (uint)Bytes.Length ? (int)space : throw new ArgumentOutOfRangeException(nameof(space), space, message: null);

    private static byte[] BytesOf(NamedColorSpace space)
    {
        int index = Index(space);
        return Volatile.Read(ref Bytes[index]) ?? (Bytes[index] = IccProfileWriter.Write(space));
    }
}
