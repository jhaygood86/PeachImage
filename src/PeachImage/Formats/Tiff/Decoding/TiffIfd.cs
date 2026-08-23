using PeachImage.Formats.Tiff.Internal;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// A parsed IFD: its tag entries, plus typed accessors that resolve each entry's value(s) — inline or
/// offset-indirected, per TIFF 6.0's <c>TypeSize * Count &lt;= 4</c> rule — through the <see cref="TiffReader"/>
/// that read them. Every numeric accessor widens BYTE/SHORT/LONG values to <see cref="uint"/> uniformly, since
/// none of the tags this decoder reads need to distinguish those source widths once resolved.
/// </summary>
internal sealed class TiffIfd(TiffReader reader, IReadOnlyDictionary<ushort, TiffIfdEntry> entries)
{
    /// <summary>Whether the IFD has an entry for <paramref name="tag"/> at all.</summary>
    public bool HasTag(ushort tag) => entries.ContainsKey(tag);

    /// <summary>The first value of <paramref name="tag"/>, or <paramref name="defaultValue"/> if the tag is absent.</summary>
    public uint GetUInt32(ushort tag, uint defaultValue) =>
        entries.TryGetValue(tag, out var entry) ? ReadValues(entry)[0] : defaultValue;

    /// <summary>The first value of <paramref name="tag"/>, narrowed to <see cref="ushort"/>. Values legitimately outside the 16-bit range for a SHORT-typed tag can't occur; this is purely a convenience cast for tags this decoder treats as small enumerations/counts.</summary>
    public ushort GetUInt16(ushort tag, ushort defaultValue) => (ushort)GetUInt32(tag, defaultValue);

    /// <summary>The first value of <paramref name="tag"/>. Throws <see cref="TiffDecodingException"/> if the tag is absent.</summary>
    public uint RequireUInt32(ushort tag) =>
        entries.TryGetValue(tag, out var entry)
            ? ReadValues(entry)[0]
            : throw new TiffDecodingException($"Missing required TIFF tag {tag}.");

    /// <summary>Every value of <paramref name="tag"/>, or an empty array if the tag is absent.</summary>
    public uint[] GetUInt32Array(ushort tag) => entries.TryGetValue(tag, out var entry) ? ReadValues(entry) : [];

    /// <summary>Every value of <paramref name="tag"/>. Throws <see cref="TiffDecodingException"/> if the tag is absent.</summary>
    public uint[] RequireUInt32Array(ushort tag) =>
        entries.TryGetValue(tag, out var entry)
            ? ReadValues(entry)
            : throw new TiffDecodingException($"Missing required TIFF tag {tag}.");

    private uint[] ReadValues(TiffIfdEntry entry)
    {
        int typeSize = entry.Type.GetByteSize();
        if (typeSize == 0)
        {
            throw new TiffDecodingException($"Tag {entry.Tag} has an unrecognized value type ({(ushort)entry.Type}).");
        }

        if (entry.Count == 0)
        {
            throw new TiffDecodingException($"Tag {entry.Tag} declares zero values.");
        }

        if (entry.Count > TiffDecodingLimits.MaxArrayEntryCount)
        {
            throw new TiffDecodingException($"Tag {entry.Tag} declares {entry.Count} values, exceeding the supported maximum of {TiffDecodingLimits.MaxArrayEntryCount}.");
        }

        long totalBytes = (long)typeSize * entry.Count;
        long valuesOffsetLong = totalBytes <= 4 ? entry.ValueFieldOffset : reader.ReadUInt32(entry.ValueFieldOffset);
        if (valuesOffsetLong is < 0 or > int.MaxValue)
        {
            throw new TiffDecodingException($"Tag {entry.Tag} has an out-of-range value offset.");
        }

        int valuesOffset = (int)valuesOffsetLong;
        var result = new uint[entry.Count];

        for (int i = 0; i < entry.Count; i++)
        {
            int offset = valuesOffset + (i * typeSize);
            result[i] = entry.Type switch
            {
                TiffTagType.Byte or TiffTagType.SByte or TiffTagType.Undefined or TiffTagType.Ascii => reader.ReadByte(offset),
                TiffTagType.Short or TiffTagType.SShort => reader.ReadUInt16(offset),
                TiffTagType.Long or TiffTagType.SLong => reader.ReadUInt32(offset),
                _ => throw new TiffDecodingException($"Tag {entry.Tag} has a value type ({entry.Type}) this decoder cannot read numerically."),
            };
        }

        return result;
    }
}
