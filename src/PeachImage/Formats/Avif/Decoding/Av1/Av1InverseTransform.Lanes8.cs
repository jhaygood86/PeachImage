using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeachImage.Formats.Avif.Decoding.Av1;

/// <summary>
/// The eight-lane <see cref="int"/> flavour of the batched inverse transform (the same butterfly networks as
/// the four-lane <see cref="long"/> path in <c>Av1InverseTransform.cs</c>, with the exact 64-bit
/// intermediates replaced by 32-bit ones). For a conformant stream every intermediate fits comfortably: the
/// widest case is a 10-bit block, where a butterfly adds two products of an 18-bit value and a 12-bit
/// constant (under 2^31). Like dav1d's SIMD transforms, a stream that violates that bound may decode
/// differently from the exact path; it can never read or write out of bounds. The exact path stays available
/// as <see cref="Inverse2DExact"/> and is what the equivalence tests compare against.
/// </summary>
internal static partial class Av1InverseTransform
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> RoundBatch8(Vector256<int> x, int n) =>
        n == 0 ? x : Vector256.ShiftRightArithmetic(x + Vector256.Create(1 << (n - 1)), n);

    /// <summary>Batched <see cref="B"/> -- <paramref name="angle"/> is identical across all 4 lanes (broadcast, not gathered), since every lane is a different row/column undergoing the exact same butterfly step.</summary>
    private static void BBatch8(Span<Vector256<int>> t, int a, int b, int angle, bool flip, int r)
    {
        _ = r;
        var cos = Vector256.Create(Cos128(angle));
        var sin = Vector256.Create(Sin128(angle));
        Vector256<int> x = (t[a] * cos) - (t[b] * sin);
        Vector256<int> y = (t[a] * sin) + (t[b] * cos);
        Vector256<int> ra = RoundBatch8(x, 12);
        Vector256<int> rb = RoundBatch8(y, 12);
        if (flip)
        {
            (ra, rb) = (rb, ra);
        }

        t[a] = ra;
        t[b] = rb;
    }

    /// <summary>Batched <see cref="H"/>.</summary>
    private static void HBatch8(Span<Vector256<int>> t, int a, int b, bool flip, int r)
    {
        if (flip)
        {
            HBatch8(t, b, a, false, r);
            return;
        }

        Vector256<int> x = t[a];
        Vector256<int> y = t[b];
        int bound = 1 << (r - 1);
        var lo = Vector256.Create(-bound);
        var hi = Vector256.Create(bound - 1);
        t[a] = Vector256.Min(Vector256.Max(x + y, lo), hi);
        t[b] = Vector256.Min(Vector256.Max(x - y, lo), hi);
    }

    [ThreadStatic]
    private static Vector256<int>[]? _permuteScratchBatch8;

    private static Vector256<int>[] PermuteScratchBatch8() => _permuteScratchBatch8 ??= new Vector256<int>[64];

    private static void InverseDctPermuteBatch8(Span<Vector256<int>> t, int n)
    {
        var copy = PermuteScratchBatch8();
        int len = 1 << n;
        t[..len].CopyTo(copy);
        for (int i = 0; i < len; i++)
        {
            t[i] = copy[Brev(n, i)];
        }
    }

    /// <summary>Batched <see cref="InverseDct"/> -- a direct, mechanical transcription (every angle/index formula, every <c>if (n ...)</c> guard, in the same order) with <see cref="B"/>/<see cref="H"/> calls replaced by their batched equivalents, so its correctness rests on <see cref="BBatch8"/>/<see cref="HBatch8"/> alone, not on independently re-deriving the butterfly network.</summary>
    internal static void InverseDctBatch8(Span<Vector256<int>> t, int n, int r)
    {
        InverseDctPermuteBatch8(t, n);

        if (n == 6)
        {
            for (int i = 0; i <= 15; i++)
            {
                BBatch8(t, 32 + i, 63 - i, 63 - (4 * Brev(4, i)), false, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 7; i++)
            {
                BBatch8(t, 16 + i, 31 - i, 6 + (Brev(3, 7 - i) << 3), false, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 15; i++)
            {
                HBatch8(t, 32 + (i * 2), 33 + (i * 2), (i & 1) != 0, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 3; i++)
            {
                BBatch8(t, 8 + i, 15 - i, 12 + (Brev(2, 3 - i) << 4), false, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 7; i++)
            {
                HBatch8(t, 16 + (2 * i), 17 + (2 * i), (i & 1) != 0, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 3; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    BBatch8(t, 62 - (i * 4) - j, 33 + (i * 4) + j, 60 - (16 * Brev(2, i)) + (64 * j), true, r);
                }
            }
        }

        if (n >= 3)
        {
            for (int i = 0; i <= 1; i++)
            {
                BBatch8(t, 4 + i, 7 - i, 56 - (32 * i), false, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 3; i++)
            {
                HBatch8(t, 8 + (2 * i), 9 + (2 * i), (i & 1) != 0, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    BBatch8(t, 30 - (4 * i) - j, 17 + (4 * i) + j, 24 + (j << 6) + ((1 - i) << 5), true, r);
                }
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    HBatch8(t, 32 + (i * 4) + j, 35 + (i * 4) - j, (i & 1) != 0, r);
                }
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            BBatch8(t, 2 * i, (2 * i) + 1, 32 + (16 * i), (1 - i) != 0, r);
        }

        if (n >= 3)
        {
            for (int i = 0; i <= 1; i++)
            {
                HBatch8(t, 4 + (2 * i), 5 + (2 * i), i != 0, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 1; i++)
            {
                BBatch8(t, 14 - i, 9 + i, 48 + (64 * i), true, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 3; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    HBatch8(t, 16 + (4 * i) + j, 19 + (4 * i) - j, (i & 1) != 0, r);
                }
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 3; j++)
                {
                    BBatch8(t, 61 - (i * 8) - j, 34 + (i * 8) + j, 56 - (i * 32) + ((j >> 1) * 64), true, r);
                }
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            HBatch8(t, i, 3 - i, false, r);
        }

        if (n >= 3)
        {
            BBatch8(t, 6, 5, 32, true, r);
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    HBatch8(t, 8 + (4 * i) + j, 11 + (4 * i) - j, i != 0, r);
                }
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 3; i++)
            {
                BBatch8(t, 29 - i, 18 + i, 48 + ((i >> 1) * 64), true, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 3; i++)
            {
                for (int j = 0; j <= 3; j++)
                {
                    HBatch8(t, 32 + (8 * i) + j, 39 + (8 * i) - j, (i & 1) != 0, r);
                }
            }
        }

        if (n >= 3)
        {
            for (int i = 0; i <= 3; i++)
            {
                HBatch8(t, i, 7 - i, false, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 1; i++)
            {
                BBatch8(t, 13 - i, 10 + i, 32, true, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 3; j++)
                {
                    HBatch8(t, 16 + (i * 8) + j, 23 + (i * 8) - j, i != 0, r);
                }
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                BBatch8(t, 59 - i, 36 + i, i < 4 ? 48 : 112, true, r);
            }
        }

        if (n >= 4)
        {
            for (int i = 0; i <= 7; i++)
            {
                HBatch8(t, i, 15 - i, false, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 3; i++)
            {
                BBatch8(t, 27 - i, 20 + i, 32, true, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                HBatch8(t, 32 + i, 47 - i, false, r);
                HBatch8(t, 48 + i, 63 - i, true, r);
            }
        }

        if (n >= 5)
        {
            for (int i = 0; i <= 15; i++)
            {
                HBatch8(t, i, 31 - i, false, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 7; i++)
            {
                BBatch8(t, 55 - i, 40 + i, 32, true, r);
            }
        }

        if (n == 6)
        {
            for (int i = 0; i <= 31; i++)
            {
                HBatch8(t, i, 63 - i, false, r);
            }
        }
    }

    private static void AdstInputPermuteBatch8(Span<Vector256<int>> t, int n)
    {
        int n0 = 1 << n;
        var copy = PermuteScratchBatch8();
        t[..n0].CopyTo(copy);
        for (int i = 0; i < n0; i++)
        {
            int idx = (i & 1) != 0 ? i - 1 : n0 - i - 1;
            t[i] = copy[idx];
        }
    }

    private static void AdstOutputPermuteBatch8(Span<Vector256<int>> t, int n)
    {
        int n0 = 1 << n;
        var copy = PermuteScratchBatch8();
        t[..n0].CopyTo(copy);
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

    private static void InverseAdst4Batch8(Span<Vector256<int>> t, int r)
    {
        _ = r;
        var sinpi1 = Vector256.Create(Sinpi1_9);
        var sinpi2 = Vector256.Create(Sinpi2_9);
        var sinpi3 = Vector256.Create(Sinpi3_9);
        var sinpi4 = Vector256.Create(Sinpi4_9);

        Vector256<int> s0 = (sinpi1 * t[0]);
        Vector256<int> s1 = (sinpi2 * t[0]);
        Vector256<int> s2 = (sinpi3 * t[1]);
        Vector256<int> s3 = (sinpi4 * t[2]);
        Vector256<int> s4 = (sinpi1 * t[2]);
        Vector256<int> s5 = (sinpi2 * t[3]);
        Vector256<int> s6 = (sinpi4 * t[3]);
        Vector256<int> a7 = t[0] - t[2];
        Vector256<int> b7 = a7 + t[3];

        s0 += s3;
        s1 -= s4;
        s3 = s2;
        s2 = sinpi3 * b7;
        s0 += s5;
        s1 -= s6;

        Vector256<int> x0 = s0 + s3;
        Vector256<int> x1 = s1 + s3;
        Vector256<int> x2 = s2;
        Vector256<int> x3 = s0 + s1 - s3;

        t[0] = RoundBatch8(x0, 12);
        t[1] = RoundBatch8(x1, 12);
        t[2] = RoundBatch8(x2, 12);
        t[3] = RoundBatch8(x3, 12);
    }

    private static void InverseAdst8Batch8(Span<Vector256<int>> t, int r)
    {
        AdstInputPermuteBatch8(t, 3);
        for (int i = 0; i <= 3; i++)
        {
            BBatch8(t, 2 * i, (2 * i) + 1, 60 - (16 * i), true, r);
        }

        for (int i = 0; i <= 3; i++)
        {
            HBatch8(t, i, 4 + i, false, r);
        }

        for (int i = 0; i <= 1; i++)
        {
            BBatch8(t, 4 + (3 * i), 5 + i, 48 - (32 * i), true, r);
        }

        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                HBatch8(t, (4 * j) + i, 2 + (4 * j) + i, false, r);
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            BBatch8(t, 2 + (4 * i), 3 + (4 * i), 32, true, r);
        }

        AdstOutputPermuteBatch8(t, 3);
    }

    private static void InverseAdst16Batch8(Span<Vector256<int>> t, int r)
    {
        AdstInputPermuteBatch8(t, 4);
        for (int i = 0; i <= 7; i++)
        {
            BBatch8(t, 2 * i, (2 * i) + 1, 62 - (8 * i), true, r);
        }

        for (int i = 0; i <= 7; i++)
        {
            HBatch8(t, i, 8 + i, false, r);
        }

        for (int i = 0; i <= 1; i++)
        {
            BBatch8(t, 8 + (2 * i), 9 + (2 * i), 56 - (32 * i), true, r);
            BBatch8(t, 13 + (2 * i), 12 + (2 * i), 8 + (32 * i), true, r);
        }

        for (int i = 0; i <= 3; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                HBatch8(t, (8 * j) + i, 4 + (8 * j) + i, false, r);
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 1; j++)
            {
                BBatch8(t, 4 + (8 * j) + (3 * i), 5 + (8 * j) + i, 48 - (32 * i), true, r);
            }
        }

        for (int i = 0; i <= 1; i++)
        {
            for (int j = 0; j <= 3; j++)
            {
                HBatch8(t, (4 * j) + i, 2 + (4 * j) + i, false, r);
            }
        }

        for (int i = 0; i <= 3; i++)
        {
            BBatch8(t, 2 + (4 * i), 3 + (4 * i), 32, true, r);
        }

        AdstOutputPermuteBatch8(t, 4);
    }

    internal static void InverseAdstBatch8(Span<Vector256<int>> t, int n, int r)
    {
        switch (n)
        {
            case 2:
                InverseAdst4Batch8(t, r);
                break;
            case 3:
                InverseAdst8Batch8(t, r);
                break;
            default:
                InverseAdst16Batch8(t, r);
                break;
        }
    }

    private static void InverseWhtBatch8(Span<Vector256<int>> t, int shift)
    {
        Vector256<int> a = Vector256.ShiftRightArithmetic(t[0], shift);
        Vector256<int> c = Vector256.ShiftRightArithmetic(t[1], shift);
        Vector256<int> d = Vector256.ShiftRightArithmetic(t[2], shift);
        Vector256<int> b = Vector256.ShiftRightArithmetic(t[3], shift);

        a += c;
        d -= b;
        Vector256<int> e = Vector256.ShiftRightArithmetic(a - d, 1);
        b = e - b;
        c = e - c;
        a -= b;
        d += c;

        t[0] = a;
        t[1] = b;
        t[2] = c;
        t[3] = d;
    }

    internal static void InverseIdentityBatch8(Span<Vector256<int>> t, int n)
    {
        switch (n)
        {
            case 2:
                var scale2 = Vector256.Create(5793);
                for (int i = 0; i < 4; i++)
                {
                    t[i] = RoundBatch8((t[i] * scale2), 12);
                }

                break;
            case 3:
                for (int i = 0; i < 8; i++)
                {
                    t[i] = t[i] * 2;
                }

                break;
            case 4:
                var scale4 = Vector256.Create(11586);
                for (int i = 0; i < 16; i++)
                {
                    t[i] = RoundBatch8((t[i] * scale4), 12);
                }

                break;
            default:
                for (int i = 0; i < 32; i++)
                {
                    t[i] = t[i] * 4;
                }

                break;
        }
    }

}
