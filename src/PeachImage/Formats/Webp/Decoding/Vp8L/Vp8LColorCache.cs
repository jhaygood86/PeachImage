namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// VP8L's optional color cache: a small hash table of recently-seen ARGB pixel values, letting the encoder
/// reference a repeated color by a short cache-index code instead of re-encoding the literal. Mirrors
/// libwebp's <c>color_cache_utils.c</c> exactly — <see cref="HashMultiplier"/> and the shift are load-bearing
/// constants shared verbatim between encoder and decoder, not tunable.
/// </summary>
internal sealed class Vp8LColorCache
{
    // Verified against libwebp's src/utils/color_cache_utils.h (kHashMul / VP8LHashPix): hash = (argb *
    // 0x1e35a7bd) >> (32 - hash_bits). Any other multiplier would still build *a* hash table, just not one
    // that agrees with what real encoders assume, silently corrupting every cache-index reference.
    private const uint HashMultiplier = 0x1e35a7bdu;

    private readonly uint[] _entries;
    private readonly int _hashShift;

    public Vp8LColorCache(int cacheBits)
    {
        _entries = new uint[1 << cacheBits];
        _hashShift = 32 - cacheBits;
    }

    /// <summary>Records <paramref name="argb"/> at its hash slot (overwriting whatever was there before — a plain, non-chaining cache).</summary>
    public void Insert(uint argb) => _entries[Hash(argb)] = argb;

    /// <summary>Returns the ARGB value stored at cache index <paramref name="index"/> (already decoded from the bitstream — not a hash lookup).</summary>
    public uint Lookup(int index) => _entries[index];

    private int Hash(uint argb) => (int)((argb * HashMultiplier) >> _hashShift);
}
