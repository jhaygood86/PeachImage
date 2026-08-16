namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// The inverse transform process (spec §7.13): the butterfly primitives (<c>B</c>/<c>H</c>/<c>brev</c>/
/// <c>cos128</c>/<c>sin128</c>), a single generic inverse-DCT implementation covering every size from
/// 4 to 64 points via the spec's own size-parameterized 31-step butterfly network (rather than five
/// separate hard-coded networks -- the spec itself defines it this way, so this is a direct transcription,
/// not an independent simplification), the inverse ADST4/8/16, inverse WHT (lossless only), inverse
/// identity transforms, and the 2D transform block process tying them together with the row/column shift
/// and clamping rules.
/// </summary>
internal static class Av1InverseTransform
{
    /// <summary><c>Cos128_Lookup</c> (spec §7.13.2.1), extracted directly from the specification text.</summary>
    private static readonly int[] Cos128Lookup =
    [
        4096, 4095, 4091, 4085, 4076, 4065, 4052, 4036,
        4017, 3996, 3973, 3948, 3920, 3889, 3857, 3822,
        3784, 3745, 3703, 3659, 3612, 3564, 3513, 3461,
        3406, 3349, 3290, 3229, 3166, 3102, 3035, 2967,
        2896, 2824, 2751, 2675, 2598, 2520, 2440, 2359,
        2276, 2191, 2106, 2019, 1931, 1842, 1751, 1660,
        1567, 1474, 1380, 1285, 1189, 1092, 995, 897,
        799, 700, 601, 501, 401, 301, 201, 101, 0,
    ];

    /// <summary><c>Transform_Row_Shift</c> (spec §7.13.3), extracted directly from the specification text.</summary>
    private static readonly int[] TransformRowShift = [0, 1, 2, 2, 2, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2];

    private const int Sinpi1_9 = 1321;
    private const int Sinpi2_9 = 2482;
    private const int Sinpi3_9 = 3344;
    private const int Sinpi4_9 = 3803;

    private static int Cos128(int angle)
    {
        int angle2 = angle & 255;
        if (angle2 <= 64)
        {
            return Cos128Lookup[angle2];
        }

        if (angle2 <= 128)
        {
            return -Cos128Lookup[128 - angle2];
        }

        if (angle2 <= 192)
        {
            return -Cos128Lookup[angle2 - 128];
        }

        return Cos128Lookup[256 - angle2];
    }

    private static int Sin128(int angle) => Cos128(angle - 64);

    /// <summary><c>Round2(x, n)</c> (spec §4.7): arithmetic right shift with round-to-nearest, safe for negative <paramref name="x"/> since C#'s <c>&gt;&gt;</c> on a signed integer is already arithmetic (sign-extending).</summary>
    private static int Round2(long x, int n) => n == 0 ? (int)x : (int)((x + (1L << (n - 1))) >> n);

    private static int Brev(int numBits, int x)
    {
        int t = 0;
        for (int i = 0; i < numBits; i++)
        {
            int bit = (x >> i) & 1;
            t += bit << (numBits - 1 - i);
        }

        return t;
    }

    /// <summary><c>B(a, b, angle, flip, r)</c> butterfly rotation (spec §7.13.2.1).</summary>
    private static void B(int[] t, int a, int b, int angle, bool flip, int r)
    {
        _ = r; // bitstream-conformance precision bound only; not enforced here (see the class remarks)
        long x = ((long)t[a] * Cos128(angle)) - ((long)t[b] * Sin128(angle));
        long y = ((long)t[a] * Sin128(angle)) + ((long)t[b] * Cos128(angle));
        t[a] = Round2(x, 12);
        t[b] = Round2(y, 12);
        if (flip)
        {
            (t[a], t[b]) = (t[b], t[a]);
        }
    }

    /// <summary><c>H(a, b, flip, r)</c> Hadamard rotation (spec §7.13.2.1).</summary>
    private static void H(int[] t, int a, int b, bool flip, int r)
    {
        if (flip)
        {
            H(t, b, a, false, r);
            return;
        }

        int x = t[a];
        int y = t[b];
        int bound = 1 << (r - 1);
        t[a] = Math.Clamp(x + y, -bound, bound - 1);
        t[b] = Math.Clamp(x - y, -bound, bound - 1);
    }

    /// <summary><c>Inverse DCT array permutation process</c> (spec §7.13.2.2).</summary>
    private static void InverseDctPermute(int[] t, int n)
    {
        var copy = (int[])t.Clone();
        int len = 1 << n;
        for (int i = 0; i < len; i++)
        {
            t[i] = copy[Brev(n, i)];
        }
    }

    /// <summary>
    /// <c>Inverse DCT process</c> (spec §7.13.2.3): a single generic implementation, parameterized on
    /// <paramref name="n"/> (log2 of the transform length, 2 through 6), of the spec's own 31 ordered
    /// steps -- each step's own "if n is ..." guard is transcribed as a direct <c>if</c>, exactly as
    /// written, rather than being unrolled per size.
    /// </summary>
    public static void InverseDct(int[] t, int n, int r)
    {
        InverseDctPermute(t, n);

        if (n == 6)
        {
            for (int i = 0; i <= 15; i++)
            {
                B(t, 32 + i, 63 - i, 63 - (4 * Brev(4, i)), false, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 7; i++)
            {
                B(t, 16 + i, 31 - i, 6 + (Brev(3, 7 - i) << 3), false, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 15; i++)
            {
                H(t, 32 + (i * 2), 33 + (i * 2), (i & 1) != 0, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 3; i++)
            {
                B(t, 8 + i, 15 - i, 12 + (Brev(2, 3 - i) << 4), false, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 7; i++)
            {
                H(t, 16 + (2 * i), 17 + (2 * i), (i & 1) != 0, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 3; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    B(t, 62 - (i * 4) - j, 33 + (i * 4) + j, 60 - (16 * Brev(2, i)) + (64 * j), true, r);
                }
            }
        }

        if (n >= 3)
        {
            for (int i = 0; i <= 1; i++)
            {
                B(t, 4 + i, 7 - i, 56 - (32 * i), false, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 3; i++)
            {
                H(t, 8 + (2 * i), 9 + (2 * i), (i & 1) != 0, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    B(t, 30 - (4 * i) - j, 17 + (4 * i) + j, 24 + (j << 6) + ((1 - i) << 5), true, r);
                }
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    H(t, 32 + (i * 4) + j, 35 + (i * 4) - j, (i & 1) != 0, r);
                }
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            B(t, 2 * i, (2 * i) + 1, 32 + (16 * i), (1 - i) != 0, r);
        }

        if (n >= 3)
        {
            for (int i = 0; i <= 1; i++)
            {
                H(t, 4 + (2 * i), 5 + (2 * i), i != 0, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 1; i++)
            {
                B(t, 14 - i, 9 + i, 48 + (64 * i), true, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 3; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    H(t, 16 + (4 * i) + j, 19 + (4 * i) - j, (i & 1) != 0, r);
                }
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 3; j++)
                {
                    B(t, 61 - (i * 8) - j, 34 + (i * 8) + j, 56 - (i * 32) + ((j >> 1) * 64), true, r);
                }
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            H(t, i, 3 - i, false, r);
        }

        if (n >= 3)
        {
            B(t, 6, 5, 32, true, r);
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    H(t, 8 + (4 * i) + j, 11 + (4 * i) - j, i != 0, r);
                }
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 3; i++)
            {
                B(t, 29 - i, 18 + i, 48 + ((i >> 1) * 64), true, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 3; i++)
            {
                for (int j = 0; j <= 3; j++)
                {
                    H(t, 32 + (8 * i) + j, 39 + (8 * i) - j, (i & 1) != 0, r);
                }
            }
        }

        if (n >= 3)
        {
            for (int i = 0; i <= 3; i++)
            {
                H(t, i, 7 - i, false, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 1; i++)
            {
                B(t, 13 - i, 10 + i, 32, true, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 3; j++)
                {
                    H(t, 16 + (i * 8) + j, 23 + (i * 8) - j, i != 0, r);
                }
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                B(t, 59 - i, 36 + i, i < 4 ? 48 : 112, true, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 7; i++)
            {
                H(t, i, 15 - i, false, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 3; i++)
            {
                B(t, 27 - i, 20 + i, 32, true, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                H(t, 32 + i, 47 - i, false, r);
                H(t, 48 + i, 63 - i, true, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 15; i++)
            {
                H(t, i, 31 - i, false, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                B(t, 55 - i, 40 + i, 32, true, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 31; i++)
            {
                H(t, i, 63 - i, false, r);
            }
        }
    }

    /// <summary><c>Inverse ADST4 process</c> (spec §7.13.2.6).</summary>
    private static void InverseAdst4(int[] t, int r)
    {
        _ = r;

        long s0 = (long)Sinpi1_9 * t[0];
        long s1 = (long)Sinpi2_9 * t[0];
        long s2 = (long)Sinpi3_9 * t[1];
        long s3 = (long)Sinpi4_9 * t[2];
        long s4 = (long)Sinpi1_9 * t[2];
        long s5 = (long)Sinpi2_9 * t[3];
        long s6 = (long)Sinpi4_9 * t[3];
        long a7 = t[0] - t[2];
        long b7 = a7 + t[3];

        s0 += s3;
        s1 -= s4;
        s3 = s2;
        s2 = (long)Sinpi3_9 * b7;
        s0 += s5;
        s1 -= s6;

        long x0 = s0 + s3;
        long x1 = s1 + s3;
        long x2 = s2;
        long x3 = s0 + s1 - s3;

        t[0] = Round2(x0, 12);
        t[1] = Round2(x1, 12);
        t[2] = Round2(x2, 12);
        t[3] = Round2(x3, 12);
    }

    /// <summary><c>Inverse ADST input array permutation process</c> (spec §7.13.2.4).</summary>
    private static void AdstInputPermute(int[] t, int n)
    {
        int n0 = 1 << n;
        var copy = (int[])t.Clone();
        for (int i = 0; i < n0; i++)
        {
            int idx = (i & 1) != 0 ? i - 1 : n0 - i - 1;
            t[i] = copy[idx];
        }
    }

    /// <summary><c>Inverse ADST output array permutation process</c> (spec §7.13.2.5).</summary>
    private static void AdstOutputPermute(int[] t, int n)
    {
        int n0 = 1 << n;
        var copy = (int[])t.Clone();
        for (int i = 0; i < n0; i++)
        {
            int a = (i >> 3) & 1;
            int b = ((i >> 2) & 1) ^ ((i >> 3) & 1);
            int c = ((i >> 1) & 1) ^ ((i >> 2) & 1);
            int d = (i & 1) ^ ((i >> 1) & 1);
            int idx = ((d << 3) | (c << 2) | (b << 1) | a) >> (4 - n);
            t[i] = (i & 1) != 0 ? -copy[idx] : copy[idx];
        }
    }

    /// <summary><c>Inverse ADST8 process</c> (spec §7.13.2.7).</summary>
    private static void InverseAdst8(int[] t, int r)
    {
        AdstInputPermute(t, 3);
        for (int i = 0; i <= 3; i++)
        {
            B(t, 2 * i, (2 * i) + 1, 60 - (16 * i), true, r);
        }

        for (int i = 0; i <= 3; i++)
        {
            H(t, i, 4 + i, false, r);
        }

        for (int i = 0; i <= 1; i++)
        {
            B(t, 4 + (3 * i), 5 + i, 48 - (32 * i), true, r);
        }

        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                H(t, (4 * j) + i, 2 + (4 * j) + i, false, r);
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            B(t, 2 + (4 * i), 3 + (4 * i), 32, true, r);
        }

        AdstOutputPermute(t, 3);
    }

    /// <summary><c>Inverse ADST16 process</c> (spec §7.13.2.8).</summary>
    private static void InverseAdst16(int[] t, int r)
    {
        AdstInputPermute(t, 4);
        for (int i = 0; i <= 7; i++)
        {
            B(t, 2 * i, (2 * i) + 1, 62 - (8 * i), true, r);
        }

        for (int i = 0; i <= 7; i++)
        {
            H(t, i, 8 + i, false, r);
        }

        for (int i = 0; i <= 1; i++)
        {
            B(t, 8 + (2 * i), 9 + (2 * i), 56 - (32 * i), true, r);
            B(t, 13 + (2 * i), 12 + (2 * i), 8 + (32 * i), true, r);
        }

        for (int i = 0; i <= 3; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                H(t, (8 * j) + i, 4 + (8 * j) + i, false, r);
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                B(t, 4 + (8 * j) + (3 * i), 5 + (8 * j) + i, 48 - (32 * i), true, r);
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 3; j++)
            {
                H(t, (4 * j) + i, 2 + (4 * j) + i, false, r);
            }
        }

        for (int i = 0; i <= 3; i++)
        {
            B(t, 2 + (4 * i), 3 + (4 * i), 32, true, r);
        }

        AdstOutputPermute(t, 4);
    }

    /// <summary><c>Inverse ADST process</c> (spec §7.13.2.9): dispatches by size.</summary>
    private static void InverseAdst(int[] t, int n, int r)
    {
        switch (n)
        {
            case 2:
                InverseAdst4(t, r);
                break;
            case 3:
                InverseAdst8(t, r);
                break;
            default:
                InverseAdst16(t, r);
                break;
        }
    }

    /// <summary><c>Inverse Walsh-Hadamard transform process</c> (spec §7.13.2.10) -- lossless only.</summary>
    private static void InverseWht(int[] t, int shift)
    {
        int a = t[0] >> shift;
        int c = t[1] >> shift;
        int d = t[2] >> shift;
        int b = t[3] >> shift;

        a += c;
        d -= b;
        int e = (a - d) >> 1;
        b = e - b;
        c = e - c;
        a -= b;
        d += c;

        t[0] = a;
        t[1] = b;
        t[2] = c;
        t[3] = d;
    }

    /// <summary><c>Inverse identity transform process</c> (spec §7.13.2.11-§7.13.2.15): dispatches by size, each with its own fixed scale factor.</summary>
    private static void InverseIdentity(int[] t, int n)
    {
        switch (n)
        {
            case 2:
                for (int i = 0; i < 4; i++)
                {
                    t[i] = Round2((long)t[i] * 5793, 12);
                }

                break;
            case 3:
                for (int i = 0; i < 8; i++)
                {
                    t[i] *= 2;
                }

                break;
            case 4:
                for (int i = 0; i < 16; i++)
                {
                    t[i] = Round2((long)t[i] * 11586, 12);
                }

                break;
            default:
                for (int i = 0; i < 32; i++)
                {
                    t[i] *= 4;
                }

                break;
        }
    }

    /// <summary>
    /// <c>2D inverse transform process</c> (spec §7.13.3): transforms <paramref name="dequant"/> (a flat
    /// <c>64x64</c> row-major buffer, as written by <see cref="Av1Dequantizer.Dequantize"/>) into
    /// <paramref name="residual"/> (a flat <c>w x h</c> row-major buffer).
    /// </summary>
    public static void Inverse2D(int[] dequant, int[] residual, int txSz, int planeTxType, bool lossless, int bitDepth)
    {
        int log2W = Av1TxDimensions.WidthLog2[txSz];
        int log2H = Av1TxDimensions.HeightLog2[txSz];
        int w = 1 << log2W;
        int h = 1 << log2H;

        int rowShift = lossless ? 0 : TransformRowShift[txSz];
        int colShift = lossless ? 0 : 4;
        int rowClampRange = bitDepth + 8;
        int colClampRange = Math.Max(bitDepth + 6, 16);

        Span<int> t = stackalloc int[64];

        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                t[j] = i < 32 && j < 32 ? dequant[(i * 64) + j] : 0;
            }

            if (Math.Abs(log2W - log2H) == 1)
            {
                for (int j = 0; j < w; j++)
                {
                    t[j] = Round2((long)t[j] * 2896, 12);
                }
            }

            var tRow = t[..w].ToArray();
            if (lossless)
            {
                InverseWht(tRow, 2);
            }
            else if (planeTxType is Av1TxType.DctDct or Av1TxType.AdstDct or Av1TxType.FlipadstDct or Av1TxType.HDct)
            {
                InverseDct(tRow, log2W, rowClampRange);
            }
            else if (planeTxType is Av1TxType.DctAdst or Av1TxType.AdstAdst or Av1TxType.DctFlipadst or Av1TxType.FlipadstFlipadst or Av1TxType.AdstFlipadst or Av1TxType.FlipadstAdst or Av1TxType.HAdst or Av1TxType.HFlipadst)
            {
                InverseAdst(tRow, log2W, rowClampRange);
            }
            else
            {
                InverseIdentity(tRow, log2W);
            }

            for (int j = 0; j < w; j++)
            {
                residual[(i * w) + j] = Round2(tRow[j], rowShift);
            }
        }

        int colBound = 1 << (colClampRange - 1);
        for (int i = 0; i < h; i++)
        {
            for (int j = 0; j < w; j++)
            {
                residual[(i * w) + j] = Math.Clamp(residual[(i * w) + j], -colBound, colBound - 1);
            }
        }

        var tCol = new int[h];
        for (int j = 0; j < w; j++)
        {
            for (int i = 0; i < h; i++)
            {
                tCol[i] = residual[(i * w) + j];
            }

            if (lossless)
            {
                InverseWht(tCol, 0);
            }
            else if (planeTxType is Av1TxType.DctDct or Av1TxType.DctAdst or Av1TxType.DctFlipadst or Av1TxType.VDct)
            {
                InverseDct(tCol, log2H, colClampRange);
            }
            else if (planeTxType is Av1TxType.AdstDct or Av1TxType.AdstAdst or Av1TxType.FlipadstDct or Av1TxType.FlipadstFlipadst or Av1TxType.AdstFlipadst or Av1TxType.FlipadstAdst or Av1TxType.VAdst or Av1TxType.VFlipadst)
            {
                InverseAdst(tCol, log2H, colClampRange);
            }
            else
            {
                InverseIdentity(tCol, log2H);
            }

            for (int i = 0; i < h; i++)
            {
                residual[(i * w) + j] = Round2(tCol[i], colShift);
            }
        }
    }
}
