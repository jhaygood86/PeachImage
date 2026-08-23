namespace PeachImage.Formats.Tiff.Decoding;

/// <summary>
/// One 12-byte IFD entry as read from the file, with its 4-byte value/offset field's own absolute position
/// preserved (<see cref="ValueFieldOffset"/>) rather than eagerly decoded into a plain integer — resolving it
/// into "inline value" vs. "offset to the real value" depends on <see cref="Type"/> and <see cref="Count"/>
/// together (<c>TypeSize * Count &lt;= 4</c>), which only <see cref="TiffIfd"/>'s accessors can determine.
/// </summary>
internal readonly record struct TiffIfdEntry(ushort Tag, TiffTagType Type, uint Count, int ValueFieldOffset);
