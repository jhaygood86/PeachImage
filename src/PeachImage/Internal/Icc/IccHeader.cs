namespace PeachImage.Internal.Icc;

/// <summary>
/// The 128-byte ICC profile header (ICC.1:2010 §7.2). Adapted from Wacton/Unicolour's <c>Icc/Header.cs</c>
/// (MIT license) — see THIRD-PARTY-LICENSES.md. Unlike the upstream reference (a general-purpose library that
/// exposes every header field as public API), this only reads the fields the device↔PCS transform selection
/// and PCS/intent math actually consume — the header's own <c>PcsIlluminant</c> field is deliberately not
/// even parsed; see the "not header.PcsIlluminant!" remark preserved in the upstream <c>Transform.cs</c> this
/// file's own <c>IccTransform</c> counterpart carries forward.
/// </summary>
internal sealed class IccHeader
{
    internal Version ProfileVersion { get; }

    internal string ProfileClass { get; }

    /// <summary>The device color space on the "A" side of the transform (e.g. <see cref="IccSignatures.Cmyk"/>).</summary>
    internal string DataColorSpace { get; }

    /// <summary>The profile connection space on the "B" side of the transform (<see cref="IccSignatures.Xyz"/> or <see cref="IccSignatures.Lab"/>).</summary>
    internal string Pcs { get; }

    internal string ProfileFileSignature { get; }

    internal IccIntent Intent { get; }

    internal IccHeader(Stream stream)
    {
        stream.ReadBytes(8); // ProfileSize (4), PreferredCmmType (4) -- not consumed.
        ProfileVersion = stream.ReadVersion(); // bytes 8-11
        ProfileClass = stream.ReadSignature(); // bytes 12-15
        DataColorSpace = stream.ReadSignature(); // bytes 16-19
        Pcs = stream.ReadSignature(); // bytes 20-23
        stream.ReadBytes(12); // DateTime -- not consumed.
        ProfileFileSignature = stream.ReadSignature(); // bytes 36-39
        stream.ReadBytes(24); // PrimaryPlatform (4), ProfileFlags (4), DeviceManufacturer (4), DeviceModel (4), DeviceAttributes (8) -- not consumed.
        Intent = (IccIntent)stream.ReadUInt32(); // bytes 64-67
        // Bytes 68-127 (PcsIlluminant, ProfileCreator, ProfileId, reserved) are never consumed -- IccTags
        // seeks explicitly to byte 128 for its own tag table, so no further advancement is needed here.
    }

    public override string ToString() => $"v{ProfileVersion}, {ProfileClass}, {Intent}, {DataColorSpace} device, {Pcs} PCS";
}
