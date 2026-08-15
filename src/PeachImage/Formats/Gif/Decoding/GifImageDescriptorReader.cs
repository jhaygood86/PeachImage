using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>Reads an Image Descriptor (the 0x2C introducer has already been consumed) and its Local Color Table, if any.</summary>
internal static class GifImageDescriptorReader
{
    public static GifImageDescriptor Read(Stream stream)
    {
        Span<byte> data = stackalloc byte[9];
        GifStreamHelpers.ReadExactlyOrThrow(stream, data);

        int left = data[0] | (data[1] << 8);
        int top = data[2] | (data[3] << 8);
        int width = data[4] | (data[5] << 8);
        int height = data[6] | (data[7] << 8);
        byte packed = data[8];

        bool hasLocalColorTable = (packed & 0x80) != 0;
        bool interlaced = (packed & 0x40) != 0;
        int localColorTableSize = 2 << (packed & 0x07);

        if (width <= 0 || height <= 0)
        {
            throw new GifDecodingException($"Invalid GIF image descriptor dimensions {width}x{height}.");
        }

        if ((long)width * height > GifDecodingLimits.MaxPixelCount)
        {
            throw new GifDecodingException($"GIF frame {width}x{height} exceeds the maximum supported pixel count.");
        }

        byte[] localColorTable = hasLocalColorTable ? GifColorTable.Read(stream, localColorTableSize) : [];

        return new GifImageDescriptor
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Interlaced = interlaced,
            LocalColorTable = localColorTable,
        };
    }
}
