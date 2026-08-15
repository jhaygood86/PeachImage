namespace PeachImage.Formats.Png.Filtering;

/// <summary>PNG's 5 per-scanline filter types (spec §6.2), stored as the leading byte of each unfiltered scanline.</summary>
internal enum PngFilterType : byte
{
    None = 0,
    Sub = 1,
    Up = 2,
    Average = 3,
    Paeth = 4,
}
