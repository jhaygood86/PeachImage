namespace PeachImage.Formats.Shared.Orientation;

// Fixed-size pixel carriers so the orientation kernels can move odd-sized pixels with typed element copies.
internal struct Pixel3
{
    public byte A, B, C;
}

internal struct Pixel6
{
    public ushort A, B, C;
}

internal struct Pixel12
{
    public uint A, B, C;
}
