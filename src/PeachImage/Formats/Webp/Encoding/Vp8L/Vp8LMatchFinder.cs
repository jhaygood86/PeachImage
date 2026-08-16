using System.Numerics;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>
/// Finds profitable LZ77-style backward references over a flat ARGB pixel array, using a zlib/deflate-style
/// hash-chain match finder adapted to 3-pixel hash keys instead of 3-byte ones. Only ever compares against
/// the original, already-fully-available pixel array (never a partially-reconstructed output buffer, unlike
/// decode's <see cref="Decoding.Vp8L.Vp8LBackwardCopier"/>), so a flat-color run's overlapping-copy case
/// (distance &lt; length) needs no special handling here — it falls out of plain equality comparison.
/// </summary>
/// <remarks>
/// <see cref="_head"/>/<see cref="_prev"/> are rented from <see cref="WebpBufferPool.SharedInt32"/> rather
/// than freshly allocated: for a 1080p image <c>_prev</c> alone is ~8 MiB, comfortably past the
/// large-object-heap threshold, and this class is constructed and discarded once per image encode. Callers
/// must dispose an instance once done to return the buffers.
/// </remarks>
internal sealed class Vp8LMatchFinder : IDisposable
{
    private readonly uint[] _pixels;
    private readonly int _pixelCount;
    private readonly int _hashShift;
    private readonly int _hashSize;
    private readonly int _maxChainLength;
    private readonly int _giveUpAfterFruitlessProbes;
    private readonly int[] _head;
    private readonly int[] _prev;

    public Vp8LMatchFinder(uint[] pixels, int pixelCount, int hashBits, int maxChainLength, int giveUpAfterFruitlessProbes)
    {
        _pixels = pixels;
        _pixelCount = pixelCount;
        _hashShift = 32 - hashBits;
        _hashSize = 1 << hashBits;
        _maxChainLength = maxChainLength;
        _giveUpAfterFruitlessProbes = giveUpAfterFruitlessProbes;

        // Rented arrays may come back larger than requested (an ArrayPool contract) and are not guaranteed
        // zeroed. _head's sentinel value (-1, meaning "no chain yet") must be established explicitly, bounded
        // to _hashSize since every index derived from Hash() is always < _hashSize regardless of the rented
        // array's real length. _prev needs no such fill: a slot _prev[p] is only ever read via a chain walk
        // that reached position p through _head or an earlier _prev link, both of which only ever point at
        // positions Insert has already run for -- and Insert always writes _prev[pos] the same call it makes
        // pos reachable at all -- so no slot can be read before Insert has written it, regardless of what
        // stale contents a rented (not freshly zeroed) array might otherwise hold there.
        _head = WebpBufferPool.SharedInt32.Rent(_hashSize);
        _head.AsSpan(0, _hashSize).Fill(-1);
        _prev = WebpBufferPool.SharedInt32.Rent(Math.Max(pixelCount, 1));
    }

    /// <summary>Returns the rented hash-table and hash-chain buffers to <see cref="WebpBufferPool.SharedInt32"/>.</summary>
    public void Dispose()
    {
        WebpBufferPool.SharedInt32.Return(_head);
        WebpBufferPool.SharedInt32.Return(_prev);
    }

    /// <summary>A reasonable hash-table width for an image of <paramref name="pixelCount"/> pixels: wide enough to avoid excessive collisions, without paying setup cost far beyond what the image needs.</summary>
    public static int ChooseHashBits(int pixelCount)
    {
        int bits = pixelCount <= 1 ? 1 : BitOperations.Log2((uint)pixelCount) + 1;
        return Math.Clamp(bits, 10, 16);
    }

    /// <summary>
    /// Finds the longest backward reference available at <paramref name="pos"/>, up to the chain-search
    /// budget and the format's own length/distance ceilings. Returns <see langword="false"/> if no match of
    /// at least 3 pixels was found.
    /// </summary>
    /// <remarks>
    /// Bails out of the chain walk once <see cref="_giveUpAfterFruitlessProbes"/> consecutive candidates in a
    /// row have produced no match at all (<c>bestLength</c> still 0) — measured on real photographic content,
    /// 99.96% of positions walk the *entire* configured chain length and find nothing whatsoever, so for a
    /// position that hasn't found anything within the first handful of candidates, continuing to the full
    /// depth is overwhelmingly likely to be wasted work. This only shortens the search when nothing has been
    /// found yet; once any match (even a short one) is found, the walk continues to the full chain budget
    /// looking for something better, same as before.
    /// </remarks>
    public bool TryFindMatch(int pos, out int distance, out int length)
    {
        distance = 0;
        length = 0;

        if (pos + 3 > _pixelCount)
        {
            return false;
        }

        int maxPossible = Math.Min(Vp8LEncodingLimits.MaxBackwardReferenceLength, _pixelCount - pos);
        int candidate = _head[Hash(pos)];
        int chain = 0;
        int bestLength = 0;
        int bestDistance = 0;
        int fruitlessProbes = 0;

        while (candidate >= 0 && chain < _maxChainLength)
        {
            int dist = pos - candidate;
            if (dist > Vp8LEncodingLimits.MaxBackwardReferenceDistance)
            {
                // The chain walks from most-recently-inserted to oldest, so distance only grows from here.
                break;
            }

            int matchLength = MeasureMatch(candidate, pos, maxPossible);
            if (matchLength > bestLength)
            {
                bestLength = matchLength;
                bestDistance = dist;
                fruitlessProbes = 0;
                if (matchLength >= maxPossible)
                {
                    break;
                }
            }
            else
            {
                fruitlessProbes++;
                if (bestLength == 0 && fruitlessProbes >= _giveUpAfterFruitlessProbes)
                {
                    break;
                }
            }

            candidate = _prev[candidate];
            chain++;
        }

        if (bestLength < 3)
        {
            return false;
        }

        distance = bestDistance;
        length = bestLength;
        return true;
    }

    /// <summary>Records <paramref name="pos"/> in the hash chain. Must be called for every position consumed while tokenizing, in order — including every position inside an accepted match, not just its first pixel — so later matches can reference into the middle of an earlier run.</summary>
    public void Insert(int pos)
    {
        if (pos + 3 > _pixelCount)
        {
            return;
        }

        int hash = Hash(pos);
        _prev[pos] = _head[hash];
        _head[hash] = pos;
    }

    private int MeasureMatch(int candidate, int pos, int maxLength)
    {
        int len = 0;
        while (len < maxLength && _pixels[candidate + len] == _pixels[pos + len])
        {
            len++;
        }

        return len;
    }

    private int Hash(int pos)
    {
        uint p0 = _pixels[pos];
        uint p1 = _pixels[pos + 1];
        uint p2 = _pixels[pos + 2];
        uint mixed = (p0 * 0x9E3779B1u) ^ (p1 * 0x85EBCA6Bu) ^ (p2 * 0xC2B2AE3Du);
        return (int)(mixed >> _hashShift);
    }
}
