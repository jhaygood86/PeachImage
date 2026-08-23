using PeachImage.Formats.Tiff.Internal;

namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// Reads a single IFD's tag entries — just the entries, not the next-IFD chain. This decoder only ever
/// decodes the first IFD (single-frame <see cref="Image"/> output, no multi-page API), so the 4-byte
/// next-IFD offset that follows an IFD's entries is read past and discarded rather than followed; TIFF's
/// "circular next-IFD chain" malformation category is therefore not reachable by this decoder at all, not
/// merely guarded against.
/// </summary>
internal static class TiffIfdReader
{
    private const int EntrySize = 12;

    public static TiffIfd Read(TiffReader reader, uint ifdOffset)
    {
        if (ifdOffset > int.MaxValue)
        {
            throw new TiffDecodingException("TIFF first-IFD offset is out of range.");
        }

        int offset = (int)ifdOffset;
        ushort entryCount = reader.ReadUInt16(offset);
        if (entryCount > TiffDecodingLimits.MaxIfdEntryCount)
        {
            throw new TiffDecodingException($"TIFF IFD declares {entryCount} entries, exceeding the supported maximum of {TiffDecodingLimits.MaxIfdEntryCount}.");
        }

        var entries = new Dictionary<ushort, TiffIfdEntry>(entryCount);
        int entryOffset = offset + 2;

        for (int i = 0; i < entryCount; i++)
        {
            ushort tag = reader.ReadUInt16(entryOffset);
            ushort rawType = reader.ReadUInt16(entryOffset + 2);
            uint count = reader.ReadUInt32(entryOffset + 4);
            int valueFieldOffset = entryOffset + 8;

            // Duplicate tags shouldn't appear in a well-formed IFD; keep the first occurrence rather than
            // treating a second one as fatal, matching this codebase's convention of tolerating oddities
            // that aren't this decoder's actual concern.
            entries.TryAdd(tag, new TiffIfdEntry(tag, (TiffTagType)rawType, count, valueFieldOffset));
            entryOffset += EntrySize;
        }

        return new TiffIfd(reader, entries);
    }
}
