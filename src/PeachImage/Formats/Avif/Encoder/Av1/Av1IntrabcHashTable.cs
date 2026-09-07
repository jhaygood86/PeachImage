namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Faithful port of libaom's real IntraBC candidate hash table (<c>av1/encoder/hash_motion.c</c>,
/// <c>av1/encoder/hash.c</c>, read directly from the local checkout at <c>C:\Sources\GoogleSource\aom</c>)
/// -- replaces this encoder's own hand-rolled FNV-1a, leaf-size-restricted, incrementally-recorded exact-match
/// index with libaom's actual whole-frame, multi-size, precomputed construction. This is a genuine widening of
/// match coverage, not just a hash-function swap: the old index could only ever find a match against another
/// leaf whose own chosen partition size happened to equal the current leaf's, recorded only as leaves were
/// actually committed (in this encoder's own raster/quadtree order); this indexes every valid position at
/// every one of AV1's 6 square IntraBC block sizes (4, 8, 16, 32, 64, 128 -- <c>hash_block_size_to_index</c>,
/// <c>hash_motion.c</c>), built once up front from source pixels (this encoder's own source is already known
/// in full before any leaf is coded, exactly like libaom's own offline encode), so a match can be found across
/// a differently-partitioned region of an already-encoded area.
///
/// <para>Deliberately luma-only, matching libaom's own construction exactly (<c>av1_generate_block_2x2_hash_value</c>
/// only ever reads <c>picture-&gt;y_buffer</c>) -- the hash is purely an index to narrow candidates, never itself
/// the correctness check: every candidate this table returns still needs a real full-pixel comparison (luma
/// <em>and</em> chroma, for a lossless exact match) before use, exactly as this encoder's own prior
/// implementation already did. A 32-bit CRC collision only costs a wasted candidate check, never a wrong
/// match.</para>
///
/// <para><b>Deliberately not ported</b>: libaom's exact spatially-dispersed insertion-order state machine
/// (<c>av1_add_to_hash_map_by_row_with_precal_data</c>, only even-pixel-offset positions, a 4-phase
/// halving-step visitation) and its 256-entry-per-bucket cap with silent-drop-on-overflow. Both exist in
/// libaom purely to bound per-bucket memory/scan cost for pathologically repetitive content while keeping a
/// *spread* of candidates rather than a clump of near-duplicates -- they don't change which matches exist,
/// only which subset of an already-huge candidate set survives when a single bucket would otherwise hold more
/// than 256 entries (a rare edge case; per this project's own libaom source research, the exact eviction order
/// only affects byte-for-byte tie-breaking there, not real compression quality in the general case). This
/// implementation instead inserts every valid position into unbounded per-bucket lists (bounded in aggregate
/// by the frame's own pixel count, so total memory stays proportional to the source image regardless of
/// content) and caps the <em>query-time scan</em> instead, via the already-ported, effort-indexed
/// <see cref="Av1SpeedFeatures.PruneIntrabcCandidateBlockHashSearch"/> (see the query site in
/// <c>Av1TileEncoder.FindIntrabcMatch</c>) -- the same real speed feature libaom itself uses to bound the scan
/// (<c>AOMMIN(64, count)</c>, <c>mcomp.c</c>), just without needing the trickier dispersed-eviction machinery
/// to get the same practical bound.</para>
/// </summary>
internal sealed class Av1IntrabcHashTable
{
    /// <summary>The 6 real AV1 IntraBC hash block sizes (<c>hash_block_size_to_index</c>, <c>hash_motion.c</c>) -- every square size this encoder's own lossless leaves can ever be.</summary>
    public static readonly int[] BlockSizes = [4, 8, 16, 32, 64, 128];

    private readonly int _width;
    private readonly int _height;

    // hashBySize[i] is a full-frame array (row-major, stride _width, matching TileState.SourceY's own layout)
    // of each valid position's hash value at BlockSizes[i] -- only positions with (x + BlockSizes[i] <= width)
    // and (y + BlockSizes[i] <= height) are meaningful (mirrors av1_generate_block_hash_value's own x_end/y_end
    // bound); positions beyond that are never queried (FindIntrabcMatch never asks for a block hanging off the
    // padded buffer's own edge) so their array slots are simply left at their default 0 and never read.
    private readonly uint[][] _hashBySize;

    // bucketsBySize[i][hash] lists every position at BlockSizes[i] whose hash equals `hash` -- see this
    // class's own remarks for why this is unbounded (unlike libaom's real 256-cap) and relies on the caller's
    // own query-time scan cap instead.
    private readonly Dictionary<uint, List<(int X, int Y)>>[] _bucketsBySize;

    /// <summary>
    /// Builds the whole-frame hash pyramid and bucket index once, from <paramref name="lumaY"/> (row-major,
    /// stride <paramref name="width"/>) -- mirrors libaom's own <c>encode_frame_internal</c> call site
    /// (<c>encodeframe.c</c>: build once per frame, before any block's own RD search runs), except this
    /// encoder builds it once per <em>tile</em> (this encoder is always single-tile) up front in
    /// <c>Av1TileEncoder.EncodeTile</c>, before its own superblock loop starts.
    /// </summary>
    /// <param name="lumaY">Row-major luma source samples, stride <paramref name="width"/>.</param>
    /// <param name="width">The luma plane's width in pixels.</param>
    /// <param name="height">The luma plane's height in pixels.</param>
    /// <param name="maxBlockSize">
    /// The largest size to hash (must be one of <see cref="BlockSizes"/>) -- mirrors libaom's own
    /// <c>sf.intra_sf.hash_max_8x8_intrabc_blocks</c> speed feature, which caps this to 8 at higher effort
    /// levels so sizes 16 and above are never even computed (a real, not just skipped-at-query-time, saving:
    /// the 4x4/8x8 levels are always needed as inputs to any larger level's own pyramid, but nothing above
    /// <paramref name="maxBlockSize"/> ever gets built or bucketed at all).
    /// </param>
    public Av1IntrabcHashTable(int[] lumaY, int width, int height, int maxBlockSize = 128)
    {
        _width = width;
        _height = height;

        int levelCount = Array.IndexOf(BlockSizes, maxBlockSize) + 1;
        _hashBySize = new uint[levelCount][];
        _bucketsBySize = new Dictionary<uint, List<(int X, int Y)>>[levelCount];

        uint[] level2x2 = ComputeLevel2x2(lumaY, width, height);
        uint[] previousLevel = level2x2;
        int previousBlockSize = 2;

        for (int levelIndex = 0; levelIndex < levelCount; levelIndex++)
        {
            int blockSize = BlockSizes[levelIndex];
            uint[] level = ComputeLevel(previousLevel, previousBlockSize, blockSize, width, height);
            _hashBySize[levelIndex] = level;
            _bucketsBySize[levelIndex] = BuildBuckets(level, blockSize, width, height);
            previousLevel = level;
            previousBlockSize = blockSize;
        }
    }

    /// <summary>
    /// The candidates (if any) sharing <paramref name="x"/>/<paramref name="y"/>'s own <paramref name="blockSize"/>-sized
    /// hash value, in insertion order (raster order over the whole frame -- this encoder's own deliberate
    /// simplification of libaom's dispersed order, see this class's own remarks). Returns <see langword="null"/>
    /// when <paramref name="blockSize"/> wasn't hashed at all (above this table's own <c>maxBlockSize</c>, see
    /// the constructor) or when there simply are no other same-hash positions.
    /// </summary>
    public List<(int X, int Y)>? GetCandidates(int blockSize, int x, int y)
    {
        int levelIndex = Array.IndexOf(BlockSizes, blockSize);
        if (levelIndex < 0 || levelIndex >= _bucketsBySize.Length)
        {
            return null;
        }

        uint hash = _hashBySize[levelIndex][(y * _width) + x];
        return _bucketsBySize[levelIndex].TryGetValue(hash, out var list) ? list : null;
    }

    /// <summary><c>av1_generate_block_2x2_hash_value</c>'s 8-bit path (<c>hash_motion.c</c>) -- packs each 2x2 block's 4 luma samples (top-left, top-right, bottom-left, bottom-right) into one 32-bit value via <c>get_identity_hash_value</c>'s exact <c>(a&lt;&lt;24)+(b&lt;&lt;16)+(c&lt;&lt;8)+d</c> packing (deliberately not a real hash at this level -- libaom's own comment notes 4 8-bit values already fit losslessly in 32 bits, so there's nothing to gain from hashing yet).</summary>
    private static uint[] ComputeLevel2x2(int[] lumaY, int width, int height)
    {
        var result = new uint[width * height];
        int xEnd = width - 1;
        int yEnd = height - 1;
        for (int y = 0; y < yEnd; y++)
        {
            int rowBase = y * width;
            int nextRowBase = (y + 1) * width;
            for (int x = 0; x < xEnd; x++)
            {
                uint a = (uint)lumaY[rowBase + x];
                uint b = (uint)lumaY[rowBase + x + 1];
                uint c = (uint)lumaY[nextRowBase + x];
                uint d = (uint)lumaY[nextRowBase + x + 1];
                result[rowBase + x] = (a << 24) + (b << 16) + (c << 8) + d;
            }
        }

        return result;
    }

    /// <summary><c>av1_generate_block_hash_value</c> (<c>hash_motion.c</c>): combines 4 non-overlapping <paramref name="previousBlockSize"/>-sized sub-block hashes (top-left, top-right, bottom-left, bottom-right, each <c>previousBlockSize</c> apart) into a real CRC32C over their packed 16-byte (4 x uint32, little-endian -- matching libaom's own raw pointer cast on its little-endian reference hardware, see <see cref="Av1IntrabcCrc32C"/>'s own remarks) representation.</summary>
    private static uint[] ComputeLevel(uint[] previousLevel, int previousBlockSize, int blockSize, int width, int height)
    {
        var result = new uint[width * height];
        int srcSize = previousBlockSize;
        int xEnd = width - blockSize + 1;
        int yEnd = height - blockSize + 1;
        Span<byte> packed = stackalloc byte[16];

        for (int y = 0; y < yEnd; y++)
        {
            int rowBase = y * width;
            int nextRowBase = (y + srcSize) * width;
            for (int x = 0; x < xEnd; x++)
            {
                WriteUInt32LittleEndian(packed[..4], previousLevel[rowBase + x]);
                WriteUInt32LittleEndian(packed.Slice(4, 4), previousLevel[rowBase + x + srcSize]);
                WriteUInt32LittleEndian(packed.Slice(8, 4), previousLevel[nextRowBase + x]);
                WriteUInt32LittleEndian(packed.Slice(12, 4), previousLevel[nextRowBase + x + srcSize]);
                result[rowBase + x] = Av1IntrabcCrc32C.Compute(packed);
            }
        }

        return result;
    }

    private static void WriteUInt32LittleEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
    }

    private static Dictionary<uint, List<(int X, int Y)>> BuildBuckets(uint[] level, int blockSize, int width, int height)
    {
        var buckets = new Dictionary<uint, List<(int X, int Y)>>();
        int xEnd = width - blockSize + 1;
        int yEnd = height - blockSize + 1;
        for (int y = 0; y < yEnd; y++)
        {
            int rowBase = y * width;
            for (int x = 0; x < xEnd; x++)
            {
                uint hash = level[rowBase + x];
                if (!buckets.TryGetValue(hash, out var list))
                {
                    list = [];
                    buckets[hash] = list;
                }

                list.Add((x, y));
            }
        }

        return buckets;
    }
}

/// <summary>
/// Real CRC32C (Castagnoli, polynomial <c>0x82f63b78</c> reversed), ported from libaom's own
/// <c>av1_crc32c_calculator_init</c>/<c>av1_get_crc32c_value_c</c> (<c>av1/encoder/hash.c</c>) -- the
/// single-table, byte-at-a-time reference form (libaom's own 8-way-sliced fast path, also in that file,
/// produces bit-identical results, just faster; this project has no need for that speed). Used only by
/// <see cref="Av1IntrabcHashTable"/> to combine sub-block hashes -- not a general-purpose CRC utility.
/// </summary>
internal static class Av1IntrabcCrc32C
{
    private const uint Polynomial = 0x82f63b78u;

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint crc = n;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[n] = crc;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
