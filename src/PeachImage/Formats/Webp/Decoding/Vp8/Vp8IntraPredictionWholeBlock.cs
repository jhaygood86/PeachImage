using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// Whole-block intra prediction (RFC 6386 section 12.2): VP8's 16x16 luma modes and 8x8 chroma modes, both
/// sharing the same four-mode set (DC/V/H/TM) and pixel formulas, just at different block sizes.
/// </summary>
/// <remarks>
/// All methods operate directly on a macroblock-aligned plane buffer that has been padded with one extra border
/// row above (filled with 127) and one extra border column to the left (filled with 129) before decoding starts
/// - see <see cref="Vp8FrameDecoder"/> for the padding setup. This lets <see cref="PredictVertical(Span{byte}, int, int, int)"/>,
/// <see cref="PredictHorizontal(Span{byte}, int, int, int)"/>, and <see cref="PredictTrueMotion(Span{byte}, int, int, int)"/> simply read whatever is physically above
/// or to the left of <c>origin</c> without any conditional logic: at the frame's real top row /
/// left column, that memory already holds the correct synthetic border value. Only <see cref="PredictDc(Span{byte}, int, int, int, bool, bool)"/> needs
/// explicit availability flags, because unlike the other three modes its formula (not just its inputs) changes
/// when a neighbor is missing (RFC 6386 section 12.2, libwebp's <c>DC16_C</c>/<c>DC16NoTop_C</c>/
/// <c>DC16NoLeft_C</c>/<c>DC16NoTopLeft_C</c> family).
/// </remarks>
internal static class Vp8IntraPredictionWholeBlock
{
    // The Span entry points check once that every byte a prediction reads or writes is inside the plane, then run the kernels below
    // on raw references; per-pixel span indexing costs more than the prediction itself.

    public static void PredictDc(Span<byte> plane, int origin, int stride, int size, bool hasAbove, bool hasLeft)
    {
        CheckBlock(plane, origin, stride, size, hasAbove, hasLeft);
        PredictDc(ref MemoryMarshal.GetReference(plane), origin, stride, size, hasAbove, hasLeft);
    }

    public static void PredictVertical(Span<byte> plane, int origin, int stride, int size)
    {
        CheckBlock(plane, origin, stride, size, readsAbove: true, readsLeft: false);
        PredictVertical(ref MemoryMarshal.GetReference(plane), origin, stride, size);
    }

    public static void PredictHorizontal(Span<byte> plane, int origin, int stride, int size)
    {
        CheckBlock(plane, origin, stride, size, readsAbove: false, readsLeft: true);
        PredictHorizontal(ref MemoryMarshal.GetReference(plane), origin, stride, size);
    }

    public static void PredictTrueMotion(Span<byte> plane, int origin, int stride, int size)
    {
        CheckBlock(plane, origin, stride, size, readsAbove: true, readsLeft: true);
        PredictTrueMotion(ref MemoryMarshal.GetReference(plane), origin, stride, size);
    }

    /// <summary>Dispatches to the right whole-block prediction routine for <paramref name="mode"/> (DC_PRED/V_PRED/H_PRED/TM_PRED).</summary>
    public static void PredictModeWholeBlock(int mode, Span<byte> plane, int origin, int stride, int size, bool hasAbove, bool hasLeft)
    {
        switch (mode)
        {
            case Vp8PredictionModes.DcPred:
                PredictDc(plane, origin, stride, size, hasAbove, hasLeft);
                break;
            case Vp8PredictionModes.VPred:
                PredictVertical(plane, origin, stride, size);
                break;
            case Vp8PredictionModes.HPred:
                PredictHorizontal(plane, origin, stride, size);
                break;
            case Vp8PredictionModes.TmPred:
                PredictTrueMotion(plane, origin, stride, size);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown VP8 whole-block prediction mode.");
        }
    }

    /// <summary>Throws unless the <paramref name="size"/> x <paramref name="size"/> block at <paramref name="origin"/>, plus the neighbours the prediction reads, lie inside <paramref name="plane"/>.</summary>
    private static void CheckBlock(Span<byte> plane, int origin, int stride, int size, bool readsAbove, bool readsLeft)
    {
        long lowest = origin - (readsAbove ? stride : 0) - (readsLeft ? 1 : 0);
        long end = origin + ((size - 1L) * stride) + size;
        if (stride < 0 || size <= 0 || lowest < 0 || end > plane.Length)
        {
            throw new ArgumentException("The block lies outside its plane.");
        }
    }

    internal static void PredictDc(ref byte plane, int origin, int stride, int size, bool hasAbove, bool hasLeft)
    {
        int dc;
        if (hasAbove && hasLeft)
        {
            int sum = 0;
            for (int i = 0; i < size; i++)
            {
                sum += Unsafe.Add(ref plane, origin - stride + i) + Unsafe.Add(ref plane, origin + (i * stride) - 1);
            }

            int shift = BitOperations.Log2((uint)size) + 1;
            dc = (sum + size) >> shift;
        }
        else if (hasAbove)
        {
            int sum = 0;
            for (int i = 0; i < size; i++)
            {
                sum += Unsafe.Add(ref plane, origin - stride + i);
            }

            int shift = BitOperations.Log2((uint)size);
            dc = (sum + (size >> 1)) >> shift;
        }
        else if (hasLeft)
        {
            int sum = 0;
            for (int i = 0; i < size; i++)
            {
                sum += Unsafe.Add(ref plane, origin + (i * stride) - 1);
            }

            int shift = BitOperations.Log2((uint)size);
            dc = (sum + (size >> 1)) >> shift;
        }
        else
        {
            dc = 128;
        }

        byte value = (byte)dc;
        for (int y = 0; y < size; y++)
        {
            FillRow(ref Unsafe.Add(ref plane, origin + (y * stride)), value, size);
        }
    }

    internal static void PredictVertical(ref byte plane, int origin, int stride, int size)
    {
        ref byte above = ref Unsafe.Add(ref plane, origin - stride);
        for (int y = 0; y < size; y++)
        {
            CopyRow(ref Unsafe.Add(ref plane, origin + (y * stride)), ref above, size);
        }
    }

    internal static void PredictHorizontal(ref byte plane, int origin, int stride, int size)
    {
        for (int y = 0; y < size; y++)
        {
            byte value = Unsafe.Add(ref plane, origin + (y * stride) - 1);
            FillRow(ref Unsafe.Add(ref plane, origin + (y * stride)), value, size);
        }
    }

    internal static void PredictTrueMotion(ref byte plane, int origin, int stride, int size)
    {
        int corner = Unsafe.Add(ref plane, origin - stride - 1);

        // Each pixel is clip(above + left - corner); 16-bit lanes hold the widest intermediate (-255..510)
        // exactly, so clamping then narrowing matches ClipByte lane for lane.
        if (Vector128.IsHardwareAccelerated && size is 16 or 8)
        {
            ref byte aboveRow = ref Unsafe.Add(ref plane, origin - stride);
            var aboveBytes = size == 16
                ? Vector128.LoadUnsafe(ref aboveRow)
                : Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref aboveRow)).AsByte();
            var aboveLow = Vector128.WidenLower(aboveBytes).AsInt16();
            var aboveHigh = Vector128.WidenUpper(aboveBytes).AsInt16();
            var zero = Vector128<short>.Zero;
            var max = Vector128.Create((short)255);

            for (int y = 0; y < size; y++)
            {
                int rowOrigin = origin + (y * stride);
                var delta = Vector128.Create((short)(Unsafe.Add(ref plane, rowOrigin - 1) - corner));
                var low = Vector128.Min(Vector128.Max(aboveLow + delta, zero), max).AsUInt16();
                if (size == 16)
                {
                    var high = Vector128.Min(Vector128.Max(aboveHigh + delta, zero), max).AsUInt16();
                    Vector128.Narrow(low, high).StoreUnsafe(ref Unsafe.Add(ref plane, rowOrigin));
                }
                else
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref plane, rowOrigin), Vector128.Narrow(low, low).AsUInt64().ToScalar());
                }
            }

            return;
        }

        for (int y = 0; y < size; y++)
        {
            int left = Unsafe.Add(ref plane, origin + (y * stride) - 1);
            int rowOrigin = origin + (y * stride);
            for (int x = 0; x < size; x++)
            {
                int above = Unsafe.Add(ref plane, origin - stride + x);
                Unsafe.Add(ref plane, rowOrigin + x) = ClipByte(above + left - corner);
            }
        }
    }

    /// <summary>Writes <paramref name="size"/> copies of <paramref name="value"/>. The sizes a prediction uses (4, 8, 16) are single stores; a block-fill call costs more than the bytes it writes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FillRow(ref byte row, byte value, int size)
    {
        switch (size)
        {
            case 16:
                Vector128.Create(value).StoreUnsafe(ref row);
                break;
            case 8:
                Unsafe.WriteUnaligned(ref row, value * 0x0101010101010101UL);
                break;
            case 4:
                Unsafe.WriteUnaligned(ref row, value * 0x01010101U);
                break;
            default:
                for (int i = 0; i < size; i++)
                {
                    Unsafe.Add(ref row, i) = value;
                }

                break;
        }
    }

    /// <summary>Copies <paramref name="size"/> bytes from <paramref name="source"/> to <paramref name="row"/>; see <see cref="FillRow"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyRow(ref byte row, ref byte source, int size)
    {
        switch (size)
        {
            case 16:
                Vector128.LoadUnsafe(ref source).StoreUnsafe(ref row);
                break;
            case 8:
                Unsafe.WriteUnaligned(ref row, Unsafe.ReadUnaligned<ulong>(ref source));
                break;
            case 4:
                Unsafe.WriteUnaligned(ref row, Unsafe.ReadUnaligned<uint>(ref source));
                break;
            default:
                for (int i = 0; i < size; i++)
                {
                    Unsafe.Add(ref row, i) = Unsafe.Add(ref source, i);
                }

                break;
        }
    }

    internal static byte ClipByte(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
