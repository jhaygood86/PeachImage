namespace PeachImage.Formats.Avif.Container;

/// <summary>Parses the <c>ftyp</c> box: <c>major_brand</c>/<c>minor_version</c>/<c>compatible_brands[]</c>. A plain <c>Box</c>, not a <c>FullBox</c> — no version/flags prefix.</summary>
internal readonly struct AvifFtypBox
{
    public required string MajorBrand { get; init; }

    public required IReadOnlyList<string> CompatibleBrands { get; init; }

    /// <summary>Whether <paramref name="brand"/> is the major brand or appears among the compatible brands. Real encoders sometimes set <c>major_brand = "mif1"</c> and list <c>avif</c> only among compatible brands, so both must be checked.</summary>
    public bool HasBrand(string brand) => MajorBrand == brand || CompatibleBrands.Contains(brand);

    public static AvifFtypBox Parse(byte[] data, AvifBox box)
    {
        if (box.PayloadLength < 8)
        {
            throw new AvifDecodingException("Truncated 'ftyp' box.");
        }

        string majorBrand = AvifBinaryReader.ReadFourCc(data, box.PayloadOffset);

        var compatible = new List<string>();
        int offset = box.PayloadOffset + 8; // skip major_brand(4) + minor_version(4)
        int end = box.PayloadOffset + box.PayloadLength;
        while (offset + 4 <= end)
        {
            compatible.Add(AvifBinaryReader.ReadFourCc(data, offset));
            offset += 4;
        }

        return new AvifFtypBox { MajorBrand = majorBrand, CompatibleBrands = compatible };
    }
}
