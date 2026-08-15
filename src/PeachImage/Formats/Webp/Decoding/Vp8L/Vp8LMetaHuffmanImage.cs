using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// VP8L's optional entropy sub-image: splits the image into fixed-size tiles, each tagged with which
/// <see cref="Vp8LHuffmanGroup"/> (of possibly several) should be used to decode pixels in that tile — letting
/// an image with visually distinct regions (e.g. photo + flat-color UI chrome) use differently-tuned Huffman
/// trees per region instead of one compromise tree for the whole image. Absent, every pixel uses a single
/// implicit group.
/// </summary>
internal sealed class Vp8LMetaHuffmanImage
{
    private readonly int[] _groupIndexPerTile;
    private readonly int _tileXSize;
    private readonly int _subsampleBits;

    private Vp8LMetaHuffmanImage(int[] groupIndexPerTile, int tileXSize, int subsampleBits)
    {
        _groupIndexPerTile = groupIndexPerTile;
        _tileXSize = tileXSize;
        _subsampleBits = subsampleBits;
    }

    /// <summary>Which <see cref="Vp8LHuffmanGroup"/> index applies to the tile containing pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public int GetGroupIndex(int x, int y) => _groupIndexPerTile[(_tileXSize * (y >> _subsampleBits)) + (x >> _subsampleBits)];

    /// <summary>
    /// Reads this image stream's Huffman code definitions: an optional meta-Huffman entropy sub-image (only
    /// permitted when <paramref name="allowRecursion"/> — VP8L never nests a second one inside a sub-image),
    /// then one <see cref="Vp8LHuffmanGroup"/> per distinct group index the entropy sub-image selects (or
    /// exactly one group, if there's no entropy sub-image at all).
    /// </summary>
    public static Vp8LHuffmanGroup[] ReadGroups(Vp8LBitReader reader, int xsize, int ysize, int colorCacheBits, bool allowRecursion, out Vp8LMetaHuffmanImage? metaImage)
    {
        if (allowRecursion && reader.ReadBits(1) != 0)
        {
            // MIN_HUFFMAN_BITS + NUM_HUFFMAN_BITS-wide field, verified against libwebp's format_constants.h
            // (2 + ReadBits(3)) -- the same shape as the predictor/color transforms' own tile-size field.
            int subsampleBits = 2 + (int)reader.ReadBits(3);
            int tileXSize = SubSampleSize(xsize, subsampleBits);
            int tileYSize = SubSampleSize(ysize, subsampleBits);

            uint[] tilePixels = Vp8LPixelDecoder.DecodeImageStream(reader, tileXSize, tileYSize, allowRecursion: false);

            int tileCount = tileXSize * tileYSize;
            var groupIndexPerTile = new int[tileCount];
            int maxGroupIndex = -1;
            for (int i = 0; i < tileCount; i++)
            {
                // The group index is packed into the red+green bytes of the decoded entropy-image pixel.
                int group = (int)((tilePixels[i] >> 8) & 0xFFFF);
                groupIndexPerTile[i] = group;
                if (group > maxGroupIndex)
                {
                    maxGroupIndex = group;
                }
            }

            int groupCount = maxGroupIndex + 1;
            if (groupCount > WebpDecodingLimits.MaxMetaHuffmanGroups)
            {
                throw new WebpDecodingException($"VP8L meta-Huffman image declares {groupCount} groups, exceeding the maximum of {WebpDecodingLimits.MaxMetaHuffmanGroups}.");
            }

            var groups = new Vp8LHuffmanGroup[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                groups[i] = Vp8LHuffmanGroup.Read(reader, colorCacheBits);
            }

            metaImage = new Vp8LMetaHuffmanImage(groupIndexPerTile, tileXSize, subsampleBits);
            return groups;
        }

        metaImage = null;
        return [Vp8LHuffmanGroup.Read(reader, colorCacheBits)];
    }

    /// <summary>Ceiling-divides <paramref name="size"/> by <c>1 &lt;&lt; sampleBits</c> — VP8L's <c>VP8LSubSampleSize</c>, shared by tile-grid subsampling (predictor/color transforms, the meta-Huffman image) and color-indexing's byte packing.</summary>
    public static int SubSampleSize(int size, int sampleBits) => (size + (1 << sampleBits) - 1) >> sampleBits;
}
