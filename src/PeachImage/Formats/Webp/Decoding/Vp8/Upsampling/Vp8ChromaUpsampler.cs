using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Webp.Decoding.Vp8.Upsampling;

/// <summary>
/// VP8's 4:2:0 chroma upsampler ("fancy"/bilinear upsampling, libwebp's <c>src/dsp/upsampling.c</c>): each
/// full-resolution output sample is a 9:3:3:1-weighted average of the 4 chroma samples nearest to it (diagonal
/// bilinear interpolation, not a straight nearest/box replication and not the same 9:3:3:1-adjacent but
/// differently-shaped filter this codebase's JPEG decoder uses for its own chroma upsampling).
/// </summary>
/// <remarks>
/// libwebp's actual C implementation computes this incrementally across a scanline pair (carrying forward
/// "top-left"/"left" chroma samples and two precomputed diagonal sums) purely for speed; the per-pixel closed
/// form implemented here - weight 9 on whichever of the 2x2 neighboring chroma samples is nearest the output
/// pixel in both dimensions, 3 on each adjacent sample, 1 on the diagonal-opposite sample, then <c>(sum+8)&gt;&gt;4</c>
/// - was derived by working through that incremental macro's arithmetic (see the file's own header comment,
/// which documents the same weights) rather than lifted from a ready-made per-pixel formula, so it is flagged
/// here as a best-effort transcription worth double-checking against a real libwebp build if exact bit-parity
/// ever matters. At the plane edges, the "far" neighbor coordinate clamps to the nearest valid chroma sample,
/// reproducing libwebp's edge-replication behavior.
/// </remarks>
internal static class Vp8ChromaUpsampler
{
    /// <summary>Computes the upsampled chroma sample at full-resolution output position (<paramref name="outX"/>, <paramref name="outY"/>).</summary>
    public static byte Sample(ReadOnlySpan<byte> chroma, int stride, int chromaWidth, int chromaHeight, int outX, int outY)
    {
        int nearCol = Clamp(outX / 2, chromaWidth - 1);
        int farCol = Clamp((outX / 2) + (((outX & 1) == 1) ? 1 : -1), chromaWidth - 1);
        int nearRow = Clamp(outY / 2, chromaHeight - 1);
        int farRow = Clamp((outY / 2) + (((outY & 1) == 1) ? 1 : -1), chromaHeight - 1);

        int nearNear = chroma[(nearRow * stride) + nearCol];
        int nearFar = chroma[(nearRow * stride) + farCol];
        int farNear = chroma[(farRow * stride) + nearCol];
        int farFar = chroma[(farRow * stride) + farCol];

        int sum = (9 * nearNear) + (3 * nearFar) + (3 * farNear) + farFar;
        return (byte)((sum + 8) >> 4);
    }

    /// <summary>
    /// Produces a whole upsampled output row from the two chroma rows it interpolates between —
    /// <paramref name="nearRow"/> (the one the output row sits closest to) and <paramref name="farRow"/> —
    /// computing exactly what <see cref="Sample"/> would return for every position on that row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same arithmetic, restructured to hoist everything <see cref="Sample"/> recomputed per pixel. The row
    /// coordinates and their clamps are resolved once by the caller; within the row, two adjacent output pixels
    /// share a chroma column, so one iteration produces both. Writing it per output pixel pair also removes the
    /// two integer divisions and four clamps <see cref="Sample"/> performed on every single output sample —
    /// against 2 million output pixels per 1080p plane, twice over for U and V.
    /// </para>
    /// <para>
    /// The column clamp only ever binds at the two ends of the row, so it is peeled out: the first and last
    /// pixel pairs are handled separately and the interior loop indexes unconditionally.
    /// </para>
    /// </remarks>
    public static void UpsampleRow(
        ReadOnlySpan<byte> nearRow,
        ReadOnlySpan<byte> farRow,
        Span<byte> destination,
        int width,
        int chromaWidth)
    {
        int lastColumn = chromaWidth - 1;
        int pairCount = (width + 1) / 2;
        int c = 0;

        if (Sse2.IsSupported && nearRow.Length >= chromaWidth && farRow.Length >= chromaWidth && destination.Length >= width)
        {
            // Column 0 clamps its left neighbour, so it is peeled; the vector loop then handles eight chroma
            // columns (sixteen output pixels) per step wherever every neighbour read and every output byte is in range.
            if (pairCount > 1)
            {
                UpsamplePair(nearRow, farRow, destination, width, lastColumn, 0);
                c = 1;
            }

            ref byte nearRef = ref MemoryMarshal.GetReference(nearRow);
            ref byte farRef = ref MemoryMarshal.GetReference(farRow);
            ref byte dstRef = ref MemoryMarshal.GetReference(destination);
            var zero = Vector128<byte>.Zero;
            var eight = Vector128.Create((ushort)8);

            while (c + 8 <= lastColumn && (2 * (c + 8)) <= width)
            {
                var nearPrevious = Widen8(ref nearRef, c - 1, zero);
                var nearCurrent = Widen8(ref nearRef, c, zero);
                var nearNext = Widen8(ref nearRef, c + 1, zero);
                var farPrevious = Widen8(ref farRef, c - 1, zero);
                var farCurrent = Widen8(ref farRef, c, zero);
                var farNext = Widen8(ref farRef, c + 1, zero);

                // 9 * near + 3 * far + 8, shared by the even and odd output of each column.
                var shared = Sse2.Add(
                    Sse2.Add(Sse2.ShiftLeftLogical(nearCurrent, 3), nearCurrent),
                    Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(farCurrent, 1), farCurrent), eight));

                var even = Sse2.ShiftRightLogical(
                    Sse2.Add(Sse2.Add(shared, Sse2.Add(Sse2.ShiftLeftLogical(nearPrevious, 1), nearPrevious)), farPrevious), 4);
                var odd = Sse2.ShiftRightLogical(
                    Sse2.Add(Sse2.Add(shared, Sse2.Add(Sse2.ShiftLeftLogical(nearNext, 1), nearNext)), farNext), 4);

                // Every lane is at most 255, so the saturating pack is exact; interleave even/odd back into pixel order.
                var evenBytes = Sse2.PackUnsignedSaturate(even.AsInt16(), even.AsInt16());
                var oddBytes = Sse2.PackUnsignedSaturate(odd.AsInt16(), odd.AsInt16());
                Sse2.UnpackLow(evenBytes, oddBytes).StoreUnsafe(ref dstRef, (nuint)(2 * c));

                c += 8;
            }
        }

        for (; c < pairCount; c++)
        {
            UpsamplePair(nearRow, farRow, destination, width, lastColumn, c);
        }
    }

    /// <summary>Loads eight consecutive samples starting at <paramref name="index"/> and widens them to 16-bit lanes.</summary>
    private static Vector128<ushort> Widen8(ref byte row, int index, Vector128<byte> zero) =>
        Sse2.UnpackLow(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref row, index))).AsByte(), zero).AsUInt16();

    /// <summary>The two output pixels (even then odd) sharing chroma column <paramref name="c"/>, with edge clamping.</summary>
    private static void UpsamplePair(ReadOnlySpan<byte> nearRow, ReadOnlySpan<byte> farRow, Span<byte> destination, int width, int lastColumn, int c)
    {
        int previous = c > 0 ? c - 1 : 0;
        int next = c < lastColumn ? c + 1 : lastColumn;

        int near = nearRow[c];
        int far = farRow[c];
        int nearFar9 = (9 * near) + (3 * far) + 8;

        destination[2 * c] = (byte)((nearFar9 + (3 * nearRow[previous]) + farRow[previous]) >> 4);

        int odd = (2 * c) + 1;
        if (odd < width)
        {
            destination[odd] = (byte)((nearFar9 + (3 * nearRow[next]) + farRow[next]) >> 4);
        }
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;
}
