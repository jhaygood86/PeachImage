namespace PeachImage.Internal.Icc;

/// <summary>
/// An ICC profile's tag table (ICC.1:2010 §7.3), with lazily-parsed accessors for the specific tags the
/// supported transforms need. Ported from Wacton/Unicolour's <c>Icc/Tags.cs</c> (MIT license) — see
/// THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed class IccTags
{
    private readonly List<IccTag> tags;

    internal Lazy<IccLuts?> AToB0 { get; }
    internal Lazy<IccLuts?> AToB1 { get; }
    internal Lazy<IccLuts?> AToB2 { get; }
    internal Lazy<IccLuts?> BToA0 { get; }
    internal Lazy<IccLuts?> BToA1 { get; }
    internal Lazy<IccLuts?> BToA2 { get; }

    internal Lazy<IccXyzType?> RedMatrixColumn { get; }
    internal Lazy<IccXyzType?> GreenMatrixColumn { get; }
    internal Lazy<IccXyzType?> BlueMatrixColumn { get; }
    internal Lazy<IccCurve?> RedTrc { get; }
    internal Lazy<IccCurve?> GreenTrc { get; }
    internal Lazy<IccCurve?> BlueTrc { get; }

    internal Lazy<IccCurve?> GreyTrc { get; }

    internal Lazy<IccXyzType?> MediaWhite { get; }

    internal Lazy<IccXyzType?> MediaBlack { get; }

    internal IccTags(Stream stream)
    {
        // Initialization just gathers the raw byte data for every tag; parsing happens lazily, on demand.
        stream.Seek(128, SeekOrigin.Begin); // The tag table begins at byte 128.
        uint tagCount = stream.ReadUInt32();
        tags = new List<IccTag>((int)tagCount);
        for (int i = 0; i < tagCount; i++)
        {
            string signature = stream.ReadSignature();
            uint offset = stream.ReadUInt32();
            uint size = stream.ReadUInt32();

            long streamPosition = stream.Position;
            stream.Seek(offset, SeekOrigin.Begin);
            byte[] data = stream.ReadBytes((int)size);
            stream.Seek(streamPosition, SeekOrigin.Begin);

            tags.Add(new IccTag(signature, data));
        }

        AToB0 = new Lazy<IccLuts?>(() => Read(IccSignatures.AToB0, IccLuts.AToBFromStream));
        AToB1 = new Lazy<IccLuts?>(() => Read(IccSignatures.AToB1, IccLuts.AToBFromStream));
        AToB2 = new Lazy<IccLuts?>(() => Read(IccSignatures.AToB2, IccLuts.AToBFromStream));
        BToA0 = new Lazy<IccLuts?>(() => Read(IccSignatures.BToA0, IccLuts.BToAFromStream));
        BToA1 = new Lazy<IccLuts?>(() => Read(IccSignatures.BToA1, IccLuts.BToAFromStream));
        BToA2 = new Lazy<IccLuts?>(() => Read(IccSignatures.BToA2, IccLuts.BToAFromStream));
        RedMatrixColumn = new Lazy<IccXyzType?>(() => Read(IccSignatures.RedMatrixColumn, IccDataTypes.ReadXyzType));
        GreenMatrixColumn = new Lazy<IccXyzType?>(() => Read(IccSignatures.GreenMatrixColumn, IccDataTypes.ReadXyzType));
        BlueMatrixColumn = new Lazy<IccXyzType?>(() => Read(IccSignatures.BlueMatrixColumn, IccDataTypes.ReadXyzType));
        RedTrc = new Lazy<IccCurve?>(() => Read(IccSignatures.RedTrc, IccCurve.FromStream));
        GreenTrc = new Lazy<IccCurve?>(() => Read(IccSignatures.GreenTrc, IccCurve.FromStream));
        BlueTrc = new Lazy<IccCurve?>(() => Read(IccSignatures.BlueTrc, IccCurve.FromStream));
        GreyTrc = new Lazy<IccCurve?>(() => Read(IccSignatures.GreyTrc, IccCurve.FromStream));
        MediaWhite = new Lazy<IccXyzType?>(() => Read(IccSignatures.MediaWhitePoint, IccDataTypes.ReadXyzType));
        MediaBlack = new Lazy<IccXyzType?>(() => Read(IccSignatures.MediaBlackPoint, IccDataTypes.ReadXyzType));
    }

    internal bool Has(string signature)
    {
        foreach (var tag in tags)
        {
            if (tag.Signature == signature)
            {
                return true;
            }
        }

        return false;
    }

    internal bool HasAny(params string[] signatures)
    {
        foreach (var signature in signatures)
        {
            if (Has(signature))
            {
                return true;
            }
        }

        return false;
    }

    internal bool HasAll(params string[] signatures)
    {
        foreach (var signature in signatures)
        {
            if (!Has(signature))
            {
                return false;
            }
        }

        return true;
    }

    private T? Read<T>(string signature, Func<Stream, T> read)
        where T : class
    {
        IccTag? tag = null;
        foreach (var candidate in tags)
        {
            if (candidate.Signature == signature)
            {
                tag = candidate;
                break;
            }
        }

        if (tag is null)
        {
            return null;
        }

        using var stream = new MemoryStream(tag.Data);
        return read(stream);
    }
}
