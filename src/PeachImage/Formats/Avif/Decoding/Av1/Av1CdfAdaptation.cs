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
        int count = cdf[n];
        int rate = 3 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0) + Math.Min(FloorLog2((uint)n), 2);

        int tmp = 0;
        for (int i = 0; i < n - 1; i++)
        {
            tmp = i == symbol ? 1 << 15 : tmp;
            if (tmp < cdf[i])
            {
                cdf[i] -= (ushort)((cdf[i] - tmp) >> rate);
            }
            else
            {
                cdf[i] += (ushort)((tmp - cdf[i]) >> rate);
            }
        }

        if (count < 32)
        {
            cdf[n] = (ushort)(count + 1);
        }
    }

    public static int FloorLog2(uint x)
    {
        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }

        return s - 1;
    }
}
