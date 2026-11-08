using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// VP8L's core per-pixel decode loop: reads the (optional) color cache declaration and Huffman code
/// definitions, then walks the pixel stream. Recursively reusable for the meta-Huffman entropy sub-image and
/// every transform's parameter sub-image (<c>allowRecursion: false</c> for those — a color cache and a
/// further nested meta-Huffman image are only meaningful/permitted at the top level).
/// </summary>
internal static class Vp8LPixelDecoder
{
    /// <summary>
    /// Decodes one VP8L pixel stream in full. <paramref name="rentFromPool"/> should only ever be set by the single
    /// top-level (<paramref name="allowRecursion"/><c>: true</c>) caller for the image's real, potentially
    /// large pixel buffer — <see cref="WebpBufferPool.SharedUInt32"/> exists precisely because that one buffer
    /// is the dominant source of large-object-heap churn in VP8L decode; every recursive sub-image call here
    /// (meta-Huffman entropy image, predictor/color-transform tile parameters, the color-indexing palette) is
    /// always small, so pooling them would add rent/return bookkeeping for no measurable benefit. When
    /// <paramref name="rentFromPool"/> is <see langword="true"/>, the returned array may be larger than
    /// <c>width*height</c> (an <see cref="System.Buffers.ArrayPool{T}"/> contract) — callers MUST bound every
    /// use of it to exactly <c>width*height</c> elements (e.g. via <c>.AsSpan(0, width*height)</c>), never rely
    /// on the array's own <c>.Length</c>, and are responsible for returning it to the pool once done.
    /// </summary>
    public static uint[] DecodeImageStream(Vp8LBitReader reader, int width, int height, bool allowRecursion, bool rentFromPool = false)
    {
        var stream = new Vp8LPixelStream(reader, width, height, allowRecursion, rentFromPool);
        stream.DecodeUntil(stream.PixelCount);
        return stream.Pixels;
    }
}

/// <summary>
/// A VP8L pixel stream that can be decoded incrementally: <see cref="DecodeUntil"/> advances the decode to at
/// least a given pixel position and can be called again to continue. That is what lets the top-level decoder run
/// the inverse transforms over a few rows at a time while they are still in cache, instead of making a pass over
/// the whole image per transform.
/// </summary>
internal sealed class Vp8LPixelStream
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int LengthCodeLimit = NumLiteralCodes + NumLengthCodes;

    private readonly Vp8LBitReader _reader;
    private readonly int _width;
    private readonly Vp8LHuffmanGroup[] _groups;
    private readonly Vp8LFastPixelLoop.GroupView[]? _views;
    private readonly Vp8LMetaHuffmanImage? _metaImage;
    private readonly Vp8LColorCache? _colorCache;
    private readonly int _colorCacheLimit;

    private Vp8LPixelCursor _cursor;
    private bool _finished;

    /// <summary>Reads the stream's color-cache declaration and Huffman codes and rents/allocates the pixel buffer. Pixels are decoded by <see cref="DecodeUntil"/>.</summary>
    public Vp8LPixelStream(Vp8LBitReader reader, int width, int height, bool allowRecursion, bool rentFromPool)
    {
        if (width <= 0 || height <= 0)
        {
            throw new WebpDecodingException($"Invalid VP8L image stream dimensions {width}x{height}.");
        }

        long pixelCount = (long)width * height;
        if (pixelCount > WebpDecodingLimits.MaxPixelCount)
        {
            throw new WebpDecodingException($"VP8L image stream {width}x{height} exceeds the maximum supported pixel count.");
        }

        int colorCacheBits = 0;
        if (reader.ReadBits(1) != 0)
        {
            colorCacheBits = (int)reader.ReadBits(4);
            if (colorCacheBits is < 1 or > WebpDecodingLimits.MaxColorCacheBits)
            {
                throw new WebpDecodingException($"Invalid VP8L color cache bits {colorCacheBits}; must be in [1, {WebpDecodingLimits.MaxColorCacheBits}].");
            }
        }

        _reader = reader;
        _width = width;
        _groups = Vp8LMetaHuffmanImage.ReadGroups(reader, width, height, colorCacheBits, allowRecursion, out _metaImage);
        _views = Vp8LFastPixelLoop.CreateViews(_groups);
        _colorCache = colorCacheBits > 0 ? new Vp8LColorCache(colorCacheBits) : null;
        _colorCacheLimit = LengthCodeLimit + (colorCacheBits > 0 ? 1 << colorCacheBits : 0);

        PixelCount = (int)pixelCount;
        Pixels = rentFromPool ? WebpBufferPool.SharedUInt32.Rent(PixelCount) : new uint[pixelCount];
    }

    /// <summary>The decoded ARGB pixels. When rented from the pool the array may be longer than <see cref="PixelCount"/>.</summary>
    public uint[] Pixels { get; }

    public int PixelCount { get; }

    /// <summary>Pixels decoded so far (always <see cref="PixelCount"/> once the stream has ended, however it ended).</summary>
    public int Position => _cursor.Pos;

    /// <summary>
    /// Decodes until at least <paramref name="limit"/> pixels exist (a backward reference can overshoot it), or
    /// the stream ends. A truncated or corrupt stream stops early; the undecoded remainder is then zeroed and the
    /// position moves to the end, so callers never see another decode's pool-recycled pixels.
    /// </summary>
    public void DecodeUntil(int limit)
    {
        if (_finished || _cursor.Pos >= limit)
        {
            return;
        }

        var reader = _reader;
        var pixels = Pixels;
        var colorCache = _colorCache;
        var metaImage = _metaImage;
        var groups = _groups;
        int width = _width;
        int pixelCount = PixelCount;

        // The register-resident fast loop decodes everything except the last few bytes of the stream; the
        // general loop below then finishes (and handles truncation) exactly as it always did.
        if (_views is not null)
        {
            Vp8LFastPixelLoop.Run(reader, pixels, width, pixelCount, limit, _views, metaImage, colorCache, _colorCacheLimit, ref _cursor);
        }

        int col = _cursor.Col;
        int row = _cursor.Row;
        int pos = _cursor.Pos;
        bool corrupt = _cursor.Corrupt;
        var activeGroup = groups[0];
        if (metaImage != null && pos < pixelCount)
        {
            activeGroup = groups[metaImage.GetGroupIndex(col, row)];
        }

        bool stoppedEarly = corrupt;
        while (!corrupt && pos < limit)
        {
            if (reader.IsOverBudget)
            {
                // Truncated stream: stop decoding, mirroring GifLzwDecoder's "stop early on truncation rather
                // than throw" convention. The undecoded remainder is zeroed below.
                stoppedEarly = true;
                break;
            }

            if (metaImage != null)
            {
                activeGroup = groups[metaImage.GetGroupIndex(col, row)];
            }

            int code = activeGroup.Green.DecodeMain(reader);

            if (code < NumLiteralCodes)
            {
                int red = activeGroup.Red.DecodeMain(reader);
                int blue = activeGroup.Blue.DecodeMain(reader);
                int alpha = activeGroup.Alpha.DecodeMain(reader);
                uint argb = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;

                pixels[pos] = argb;
                colorCache?.Insert(argb);

                pos++;
                col++;
                if (col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            else if (code < LengthCodeLimit)
            {
                int lengthSymbol = code - NumLiteralCodes;
                int length = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(lengthSymbol, reader);
                int distanceSymbol = activeGroup.Distance.DecodeMain(reader);
                int distanceCode = Vp8LBackwardReferenceTables.DecodePrefixCodeValue(distanceSymbol, reader);
                int distance = Vp8LBackwardReferenceTables.PlaneCodeToDistance(width, distanceCode);

                if (distance > pos || length > pixelCount - pos)
                {
                    // A backward reference reaching before the start of the buffer, or past its end, can
                    // only come from a corrupt/truncated stream -- stop early rather than throw, same
                    // leniency as the truncation case above.
                    stoppedEarly = true;
                    break;
                }

                Vp8LBackwardCopier.CopyPixels(pixels, pos, distance, length);

                if (colorCache != null)
                {
                    for (int i = 0; i < length; i++)
                    {
                        colorCache.Insert(pixels[pos + i]);
                    }
                }

                pos += length;
                col += length;
                while (col >= width)
                {
                    col -= width;
                    row++;
                }
            }
            else if (code < _colorCacheLimit)
            {
                int key = code - LengthCodeLimit;
                pixels[pos] = colorCache!.Lookup(key);

                pos++;
                col++;
                if (col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            else
            {
                // Code outside every valid range for this stream's configuration -- corrupt data.
                stoppedEarly = true;
                break;
            }
        }

        if (stoppedEarly)
        {
            // Pixels [pos, pixelCount) were never written. They have to be zeroed explicitly, because `pixels`
            // may be a pool-rented array still holding the *previous* decode's image: without this, a
            // truncated or corrupt file would surface another image's pixels as its own.
            if (pos < pixelCount)
            {
                pixels.AsSpan(pos, pixelCount - pos).Clear();
            }

            pos = pixelCount;
            _finished = true;
        }

        _cursor.Pos = pos;
        _cursor.Col = col;
        _cursor.Row = row;
        _cursor.Corrupt = false;
        _cursor.CachedUpTo = pos;
        if (pos >= pixelCount)
        {
            _finished = true;
        }
    }
}
