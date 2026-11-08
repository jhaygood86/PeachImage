using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// Locates and builds the boolean decoders for VP8's coefficient-data partitions (RFC 6386 section 9.5).
/// Partition 0 (header + per-macroblock mode data) is handled separately by the caller since it is consumed
/// continuously across the whole frame, not just up front; this type only deals with the 1/2/4/8 coefficient
/// partitions that follow it, each of which is selected per macroblock row via <c>mbRow % partitionCount</c>.
/// </summary>
internal static class Vp8Partitions
{
    /// <summary>
    /// Builds the coefficient-partition decoders. <paramref name="data"/> holds the full VP8 chunk in its first
    /// <paramref name="dataLength"/> bytes (the array may be longer: it can be rented from a pool);
    /// <paramref name="partition0End"/> is the byte offset immediately after partition 0 (where the optional
    /// 3-byte-little-endian partition size table, if any, begins).
    /// </summary>
    public static Vp8BoolDecoder[] Build(byte[] data, int dataLength, int partition0End, int partitionCount)
    {
        if (partitionCount < 1 || partitionCount > WebpDecodingLimits.MaxVp8Partitions)
        {
            throw new WebpDecodingException($"VP8 coefficient partition count {partitionCount} is out of range.");
        }

        int sizeTableBytes = 3 * (partitionCount - 1);
        if (partition0End + sizeTableBytes > dataLength)
        {
            throw new WebpDecodingException("VP8 coefficient partition size table exceeds the available chunk data.");
        }

        var partitions = new Vp8BoolDecoder[partitionCount];
        int sizeTablePos = partition0End;
        int dataPos = partition0End + sizeTableBytes;

        for (int p = 0; p < partitionCount - 1; p++)
        {
            int size = data[sizeTablePos] | (data[sizeTablePos + 1] << 8) | (data[sizeTablePos + 2] << 16);
            sizeTablePos += 3;

            if (dataPos + size > dataLength)
            {
                throw new WebpDecodingException("VP8 coefficient partition size exceeds the available chunk data.");
            }

            partitions[p] = new Vp8BoolDecoder(data, dataPos, size);
            dataPos += size;
        }

        // The last partition consumes whatever bytes remain in the chunk.
        int lastSize = dataLength - dataPos;
        partitions[partitionCount - 1] = new Vp8BoolDecoder(data, dataPos, lastSize);

        return partitions;
    }
}