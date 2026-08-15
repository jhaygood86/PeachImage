using PeachImage.Formats.Webp.Decoding.Vp8L;
using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8L;

public class Vp8LColorIndexingTransformTests
{
    /// <summary>
    /// <c>rentFromPool: true</c> must produce exactly the same pixels as the unpooled path, and this is also
    /// the fixture most likely to catch a bounds mistake: 3 pixels is far smaller than any real
    /// <see cref="WebpBufferPool.SharedUInt32"/> bucket, so the returned array is guaranteed to come back
    /// larger than requested here — precisely the case a loop mistakenly bounded by the array's own
    /// <c>.Length</c> instead of the real pixel count would either read garbage from or throw on.
    /// </summary>
    [Fact]
    public void ApplyInverse_RentFromPool_MatchesTheUnpooledResult_Unpacked()
    {
        uint[] palette = [Pack(255, 10, 10, 10), Pack(255, 20, 20, 20), Pack(255, 30, 30, 30)];
        var transform = MakeTransform(width: 3, height: 1, bits: 0, palette);
        uint[] src = [GreenOnly(2), GreenOnly(0), GreenOnly(1)];

        uint[] expected = Vp8LColorIndexingTransform.ApplyInverse(src, transform);
        uint[] pooled = Vp8LColorIndexingTransform.ApplyInverse(src, transform, rentFromPool: true);

        try
        {
            Assert.Equal(expected, pooled.AsSpan(0, expected.Length).ToArray());
        }
        finally
        {
            WebpBufferPool.SharedUInt32.Return(pooled);
        }
    }

    /// <summary>Same equivalence check on the sub-byte-packed path, which was already dimension-bounded rather than <c>.Length</c>-bounded but is worth confirming end to end with a real pooled buffer.</summary>
    [Fact]
    public void ApplyInverse_RentFromPool_MatchesTheUnpooledResult_Packed()
    {
        uint[] palette = [Pack(1, 0, 0, 0), Pack(2, 0, 0, 0), Pack(3, 0, 0, 0), Pack(4, 0, 0, 0), Pack(5, 0, 0, 0)];
        var transform = MakeTransform(width: 4, height: 1, bits: 1, palette);
        uint[] src = [GreenOnly(19), GreenOnly(4)];

        uint[] expected = Vp8LColorIndexingTransform.ApplyInverse(src, transform);
        uint[] pooled = Vp8LColorIndexingTransform.ApplyInverse(src, transform, rentFromPool: true);

        try
        {
            Assert.Equal(expected, pooled.AsSpan(0, expected.Length).ToArray());
        }
        finally
        {
            WebpBufferPool.SharedUInt32.Return(pooled);
        }
    }


    [Fact]
    public void ExpandPalette_AccumulatesDeltasPerChannel_WithWraparound()
    {
        // bits=0 (numColors > 16 path -- no packing, final table size is a full 256).
        uint[] deltas =
        [
            Pack(255, 10, 20, 30),
            Pack(0, 250, 0, 5), // red wraps: 10+250=260 mod 256 = 4.
            Pack(1, 0, 0, 0),
        ];

        uint[] palette = Vp8LColorIndexingTransform.ExpandPalette(deltas, numColors: 3, bits: 0);

        Assert.Equal(256, palette.Length);
        Assert.Equal(Pack(255, 10, 20, 30), palette[0]);
        Assert.Equal(Pack(255, 4, 20, 35), palette[1]);
        Assert.Equal(Pack(0, 4, 20, 35), palette[2]);
    }

    [Fact]
    public void ExpandPalette_PadsUnusedEntriesWithZero()
    {
        // numColors=2 -> bits=3 -> final table size = 1 << (8 >> 3) = 2, i.e. no padding needed here; use a
        // slightly larger bits-1 case (numColors=3 -> bits=2 -> final size 4) to actually see padding.
        uint[] deltas = [Pack(255, 1, 1, 1), Pack(0, 1, 1, 1), Pack(0, 1, 1, 1)];
        uint[] palette = Vp8LColorIndexingTransform.ExpandPalette(deltas, numColors: 3, bits: 2);

        Assert.Equal(4, palette.Length);
        Assert.Equal(0u, palette[3]); // padding entry, never written by the cumulative-sum loop.
    }

    [Fact]
    public void ApplyInverse_Unpacked_OneIndexPerPixel_MapsDirectly()
    {
        uint[] palette = [Pack(255, 10, 10, 10), Pack(255, 20, 20, 20), Pack(255, 30, 30, 30)];
        var transform = MakeTransform(width: 3, height: 1, bits: 0, palette);

        uint[] src = [GreenOnly(2), GreenOnly(0), GreenOnly(1)];
        uint[] dst = Vp8LColorIndexingTransform.ApplyInverse(src, transform);

        Assert.Equal([palette[2], palette[0], palette[1]], dst);
    }

    [Fact]
    public void ApplyInverse_SmallPalette_UnpacksSubBytePackedIndices_AndExpandsToFullWidth()
    {
        // numColors=5 -> bits=1 -> 4 bits/index, 2 indices packed per source byte. Full (expanded) width=4,
        // so the packed source stream is only ceil(4/2)=2 "pixels" wide.
        uint[] palette = [Pack(1, 0, 0, 0), Pack(2, 0, 0, 0), Pack(3, 0, 0, 0), Pack(4, 0, 0, 0), Pack(5, 0, 0, 0)];
        var transform = MakeTransform(width: 4, height: 1, bits: 1, palette);

        // Logical indices in reading order: 3, 1, 4, 0.
        // Packed byte 0 (pixels 0,1): low nibble=3, high nibble=1 -> 3 | (1<<4) = 0x13 = 19.
        // Packed byte 1 (pixels 2,3): low nibble=4, high nibble=0 -> 4 | (0<<4) = 0x04 = 4.
        uint[] src = [GreenOnly(19), GreenOnly(4)];

        uint[] dst = Vp8LColorIndexingTransform.ApplyInverse(src, transform);

        Assert.Equal(4, dst.Length);
        Assert.Equal(palette[3], dst[0]);
        Assert.Equal(palette[1], dst[1]);
        Assert.Equal(palette[4], dst[2]);
        Assert.Equal(palette[0], dst[3]);
    }

    [Fact]
    public void ApplyInverse_SmallPalette_TwoRows_EachRowReadsItsOwnPackedBytes()
    {
        // Same packing as above (bits=1, 2 indices/byte), but two rows -- verifies the packed source row
        // stride (srcWidth = ceil(fullWidth / pixelsPerByte)) is computed independently per row, not just
        // flattened linearly across the whole image.
        uint[] palette = [Pack(1, 0, 0, 0), Pack(2, 0, 0, 0), Pack(3, 0, 0, 0)];
        var transform = MakeTransform(width: 2, height: 2, bits: 1, palette);

        // Row 0 indices: [0, 1] -> packed byte = 0 | (1<<4) = 16.
        // Row 1 indices: [2, 0] -> packed byte = 2 | (0<<4) = 2.
        uint[] src = [GreenOnly(16), GreenOnly(2)];

        uint[] dst = Vp8LColorIndexingTransform.ApplyInverse(src, transform);

        Assert.Equal([palette[0], palette[1], palette[2], palette[0]], dst);
    }

    private static uint Pack(int a, int r, int g, int b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    private static uint GreenOnly(int green) => (uint)green << 8;

    private static Vp8LTransform MakeTransform(int width, int height, int bits, uint[] palette) =>
        new()
        {
            Type = Vp8LTransformType.ColorIndexing,
            Xsize = width,
            Ysize = height,
            Bits = bits,
            Data = palette,
        };
}
