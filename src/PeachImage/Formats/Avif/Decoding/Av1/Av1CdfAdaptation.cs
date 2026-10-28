using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// CDF adaptation (spec §8.2.6) and <c>FloorLog2</c> (spec §4.7), shared between <see cref="Av1SymbolDecoder"/>
/// and the AV1 forward-encode core's <c>Av1SymbolEncoder</c>. Extracted so both sides call the exact same
/// implementation rather than two independently-maintained copies that could silently drift apart --
/// decoder and encoder CDF state must evolve identically, symbol for symbol, for either side's bitstream
/// to be meaningful to the other.
/// </summary>
internal static class Av1CdfAdaptation
{
    /// <summary>CDF adaptation (spec §8.2.6, the process following a symbol decode/encode when <c>disable_cdf_update == 0</c>).</summary>
    public static void AdaptCdf(Span<ushort> cdf, int n, int symbol)
    {
        if ((uint)n >= (uint)cdf.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(n));
        }

        ref ushort c = ref MemoryMarshal.GetReference(cdf);
        int count = Unsafe.Add(ref c, n);
        int rate = 3 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0) + Math.Min(FloorLog2((uint)n), 2);

        // The spec's loop moves each entry towards 0 while i < symbol and towards 1 << 15 from symbol on.
        int last = n - 1;
        int split = Math.Min(symbol, last);
        int i = 0;
        for (; i < split; i++)
        {
            ref ushort e = ref Unsafe.Add(ref c, i);
            e -= (ushort)(e >> rate);
        }

        for (; i < last; i++)
        {
            ref ushort e = ref Unsafe.Add(ref c, i);
            e += (ushort)(((1 << 15) - e) >> rate);
        }

        if (count < 32)
        {
            Unsafe.Add(ref c, n) = (ushort)(count + 1);
        }
    }

    public static int FloorLog2(uint x) => x == 0 ? -1 : BitOperations.Log2(x);
}
