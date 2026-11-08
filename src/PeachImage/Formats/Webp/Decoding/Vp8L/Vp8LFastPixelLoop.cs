using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>Mutable decode position shared between <see cref="Vp8LFastPixelLoop"/> and the general loop that finishes the stream.</summary>
internal struct Vp8LPixelCursor
{
    public int Pos;
    public int Col;
    public int Row;

    /// <summary>First pixel not yet inserted into the color cache (deferred inserts; see <see cref="Vp8LColorCache.InsertRange"/>).</summary>
    public int CachedUpTo;

    /// <summary>Set when the stream is provably corrupt (out-of-range code or backward reference); the caller stops decoding.</summary>
    public bool Corrupt;
}

/// <summary>
/// The register-resident core of VP8L's per-pixel loop. The bit reader's shift register, bit count and byte
/// cursor live in locals for the whole run instead of being loaded and stored through the reader object on
/// every symbol, refills are a single compare per two symbols, and a group whose red/blue/alpha codes each
/// have exactly one symbol (so cost zero bits) decodes a literal pixel from the green code alone. The same
/// structure as libwebp's <c>DecodeImageData</c> hot loop (<c>src/dec/vp8l_dec.c</c>).
/// </summary>
/// <remarks>
/// It only runs while at least <see cref="SafeTailBytes"/> real bytes remain, which is enough for one more
/// pixel's worth of refills, so it never synthesizes zero padding and never goes over budget; the general
/// loop in <see cref="Vp8LPixelDecoder"/> finishes the stream, keeping truncation behavior unchanged.
/// </remarks>
internal static class Vp8LFastPixelLoop
{
    private const int NumLiteralCodes = 256;
    private const int LengthCodeLimit = NumLiteralCodes + 24;
    private const int RootMask = 0xFF;

    /// <summary>
    /// A backward reference refills at most four times (green+length, distance, distance extra bits, and the
    /// next pixel's first refill happens at its own loop head), 4 bytes each; 16 covers a full iteration.
    /// </summary>
    private const int SafeTailBytes = 16;

    internal readonly struct GroupView
    {
        public readonly Vp8LHuffmanCode[] Green;
        public readonly Vp8LHuffmanCode[] Red;
        public readonly Vp8LHuffmanCode[] Blue;
        public readonly Vp8LHuffmanCode[] Alpha;
        public readonly Vp8LHuffmanCode[] Distance;
        public readonly bool Trivial;
        public readonly bool AlphaIsConstant;
        public readonly uint ConstantAlpha;
        public readonly uint TrivialArgb;

        public GroupView(Vp8LHuffmanGroup group)
        {
            Green = group.Green.Entries;
            Red = group.Red.Entries;
            Blue = group.Blue.Entries;
            Alpha = group.Alpha.Entries;
            Distance = group.Distance.Entries;

            // A one-symbol code is built as a full root table of zero-length entries, so entry 0 says it all.
            Trivial = Red[0].Bits == 0 && Blue[0].Bits == 0 && Alpha[0].Bits == 0;
            AlphaIsConstant = Alpha[0].Bits == 0;
            ConstantAlpha = (uint)Alpha[0].Value << 24;
            TrivialArgb = Trivial ? ((uint)Alpha[0].Value << 24) | ((uint)Red[0].Value << 16) | Blue[0].Value : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deconstruct(
            out Vp8LHuffmanCode[] green, out Vp8LHuffmanCode[] red, out Vp8LHuffmanCode[] blue, out Vp8LHuffmanCode[] alpha,
            out Vp8LHuffmanCode[] distance, out bool trivial, out uint trivialArgb, out bool alphaIsConstant, out uint constantAlpha)
        {
            green = Green;
            red = Red;
            blue = Blue;
            alpha = Alpha;
            distance = Distance;
            trivial = Trivial;
            trivialArgb = TrivialArgb;
            alphaIsConstant = AlphaIsConstant;
            constantAlpha = ConstantAlpha;
        }

        /// <summary>The unchecked root lookup below needs every root level to be a full 256-entry table.</summary>
        public bool IsWellFormed =>
            Green.Length >= 256 && Red.Length >= 256 && Blue.Length >= 256 && Alpha.Length >= 256 && Distance.Length >= 256;
    }

    private struct Bits
    {
        public ulong Buffer;
        public int Count;
        public int Pos;
    }

    /// <summary>
    /// Builds the per-group table views once per stream; <see langword="null"/> when any group has a root level
    /// that is not a full 256-entry table (the unchecked root lookup would be unsafe), in which case the caller
    /// uses the general loop for the whole stream.
    /// </summary>
    public static GroupView[]? CreateViews(Vp8LHuffmanGroup[] groups)
    {
        var views = new GroupView[groups.Length];
        for (int i = 0; i < views.Length; i++)
        {
            views[i] = new GroupView(groups[i]);
            if (!views[i].IsWellFormed)
            {
                return null;
            }
        }

        return views;
    }

    /// <summary>
    /// Decodes pixels until <paramref name="limit"/> (or the stream's last safe byte, whichever comes first);
    /// returns with <paramref name="cursor"/> advanced as far as it could safely go. A backward reference can
    /// carry the position past <paramref name="limit"/>.
    /// </summary>
    public static void Run(
        Vp8LBitReader reader,
        uint[] pixels,
        int width,
        int pixelCount,
        int limit,
        GroupView[] views,
        Vp8LMetaHuffmanImage? metaImage,
        Vp8LColorCache? colorCache,
        int colorCacheLimit,
        ref Vp8LPixelCursor cursor)
    {
        byte[] data = reader.Data;
        int end = reader.EndOffset;
        int safeEnd = end - SafeTailBytes;

        var bits = new Bits();
        reader.Export(out bits.Buffer, out bits.Count, out bits.Pos);

        int pos = cursor.Pos;
        int col = cursor.Col;
        int row = cursor.Row;
        int cachedUpTo = cursor.CachedUpTo;
        bool corrupt = false;

        bool hasMeta = metaImage is not null;
        int tileMask = hasMeta ? metaImage!.TileMask : 0;
        ref uint pixelBase = ref MemoryMarshal.GetArrayDataReference(pixels);

        // The active group's tables live in locals: GroupView has more fields than the JIT will promote to
        // registers, so reading them through a struct would be a memory load per symbol.
        Vp8LHuffmanCode[] greenTable, redTable, blueTable, alphaTable, distanceTable;
        bool trivial;
        uint trivialArgb;
        bool alphaIsConstant;
        uint constantAlpha;
        views[hasMeta ? metaImage!.GetGroupIndex(col, row) : 0].Deconstruct(out greenTable, out redTable, out blueTable, out alphaTable, out distanceTable, out trivial, out trivialArgb, out alphaIsConstant, out constantAlpha);

        while (pos < limit && bits.Pos <= safeEnd)
        {
            if (hasMeta && (col & tileMask) == 0)
            {
                views[metaImage!.GetGroupIndex(col, row)].Deconstruct(out greenTable, out redTable, out blueTable, out alphaTable, out distanceTable, out trivial, out trivialArgb, out alphaIsConstant, out constantAlpha);
            }

            Refill(ref bits, data);
            int code = ReadSymbol(greenTable, ref bits);

            if (code < NumLiteralCodes)
            {
                uint argb;
                if (trivial)
                {
                    argb = trivialArgb | ((uint)code << 8);
                }
                else
                {
                    int red = ReadSymbol(redTable, ref bits);
                    Refill(ref bits, data);
                    int blue = ReadSymbol(blueTable, ref bits);

                    // Photographs are usually opaque: a one-symbol alpha code costs no bits, but reading it
                    // would still put a table load and a shift on the bit-state dependency chain.
                    uint alphaBits = alphaIsConstant ? constantAlpha : (uint)ReadSymbol(alphaTable, ref bits) << 24;
                    argb = alphaBits | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;
                }

                Unsafe.Add(ref pixelBase, pos++) = argb;
                if (hasMeta && ++col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            else if (code < LengthCodeLimit)
            {
                int length = ReadPrefixValue(code - NumLiteralCodes, ref bits);
                Refill(ref bits, data);
                int distanceSymbol = ReadSymbol(distanceTable, ref bits);
                Refill(ref bits, data);
                int distanceCode = ReadPrefixValue(distanceSymbol, ref bits);
                int distance = Vp8LBackwardReferenceTables.PlaneCodeToDistance(width, distanceCode);

                if (distance > pos || length > pixelCount - pos)
                {
                    corrupt = true;
                    break;
                }

                CopyPixels(pixels, pos, distance, length);
                pos += length;

                if (hasMeta)
                {
                    col += length;
                    while (col >= width)
                    {
                        col -= width;
                        row++;
                    }

                    views[metaImage!.GetGroupIndex(col, row)].Deconstruct(out greenTable, out redTable, out blueTable, out alphaTable, out distanceTable, out trivial, out trivialArgb, out alphaIsConstant, out constantAlpha);
                }
            }
            else if (code < colorCacheLimit)
            {
                // Only a lookup can observe the cache, so the pixels written since the last lookup are
                // inserted now, in order, instead of one at a time as they were decoded.
                colorCache!.InsertRange(pixels, cachedUpTo, pos);
                cachedUpTo = pos;
                Unsafe.Add(ref pixelBase, pos++) = colorCache.Lookup(code - LengthCodeLimit);
                if (hasMeta && ++col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            else
            {
                corrupt = true;
                break;
            }
        }

        // Hand the cache back exactly as the general loop (which inserts eagerly) expects it.
        colorCache?.InsertRange(pixels, cachedUpTo, pos);

        reader.Import(bits.Buffer, bits.Count, bits.Pos);

        if (!hasMeta)
        {
            // Column and row are only tracked when a meta-Huffman image needs them; recover them for the general loop.
            col = pos % width;
            row = pos / width;
        }

        cursor.Pos = pos;
        cursor.Col = col;
        cursor.Row = row;
        cursor.CachedUpTo = pos;
        cursor.Corrupt = corrupt;
    }

    /// <summary>Tops the register up to at least 32 bits with one unaligned 4-byte load. Only called while <c>Pos &lt;= end - SafeTailBytes</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Refill(ref Bits bits, byte[] data)
    {
        if (bits.Count < 32)
        {
            uint word = BinaryPrimitives.ReadUInt32LittleEndian(
                MemoryMarshal.CreateReadOnlySpan(ref data[bits.Pos], 4));
            bits.Buffer |= (ulong)word << bits.Count;
            bits.Pos += 4;
            bits.Count += 32;
        }
    }

    /// <summary>One Huffman symbol (at most 15 bits). The root level is a full 256-entry table (checked up front), so the root index needs no bounds check.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSymbol(Vp8LHuffmanCode[] entries, ref Bits bits)
    {
        uint window = (uint)bits.Buffer;
        int low = (int)(window & RootMask);
        Vp8LHuffmanCode root = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), low);

        if (root.Bits <= Vp8LHuffmanTableBuilder.MainRootBits)
        {
            bits.Buffer >>= root.Bits;
            bits.Count -= root.Bits;
            return root.Value;
        }

        int extraBits = root.Bits - Vp8LHuffmanTableBuilder.MainRootBits;
        uint secondaryOffset = (window >> Vp8LHuffmanTableBuilder.MainRootBits) & ((1u << extraBits) - 1);
        Vp8LHuffmanCode second = entries[low + root.Value + (int)secondaryOffset];
        int used = Vp8LHuffmanTableBuilder.MainRootBits + second.Bits;
        bits.Buffer >>= used;
        bits.Count -= used;
        return second.Value;
    }

    /// <summary><see cref="Vp8LBackwardReferenceTables.DecodePrefixCodeValue"/> against the register-resident bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadPrefixValue(int symbol, ref Bits bits)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        int extra = (int)(bits.Buffer & ((1UL << extraBits) - 1));
        bits.Buffer >>= extraBits;
        bits.Count -= extraBits;
        return offset + extra + 1;
    }

    /// <summary>
    /// Copies a backward reference. Most are short, where a call into <c>Array.Copy</c> costs more than the
    /// copy; so short runs are copied with four-pixel vector moves (forward order, which stays correct for an
    /// overlapping source as long as the distance is at least the vector width), and only long non-overlapping
    /// runs use the bulk copy.
    /// </summary>
    private static void CopyPixels(uint[] pixels, int destPos, int distance, int length)
    {
        if (distance == 1)
        {
            pixels.AsSpan(destPos, length).Fill(pixels[destPos - 1]);
            return;
        }

        if (distance >= length && length >= 64)
        {
            Array.Copy(pixels, destPos - distance, pixels, destPos, length);
            return;
        }

        // Bounds: destPos - distance >= 0 and destPos + length <= pixels.Length were established by the caller.
        ref uint destination = ref pixels[destPos];
        ref uint source = ref pixels[destPos - distance];
        int i = 0;

        if (distance >= 4 && Vector128.IsHardwareAccelerated)
        {
            for (; i + 4 <= length; i += 4)
            {
                Vector128.LoadUnsafe(ref source, (nuint)i).StoreUnsafe(ref destination, (nuint)i);
            }
        }

        for (; i < length; i++)
        {
            Unsafe.Add(ref destination, i) = Unsafe.Add(ref source, i);
        }
    }
}
