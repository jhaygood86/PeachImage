using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8;

/// <summary>
/// Validates <see cref="Vp8IntraPredictionWholeBlock"/>'s DC/V/H/TM formulas against small, hand-computed 4x4
/// blocks, including the edge cases a real frame's top-left/top-row/left-column macroblocks hit: no above
/// neighbor, no left neighbor, and neither.
/// </summary>
public class Vp8IntraPredictionWholeBlockTests
{
    // A 5x5 buffer: row/col 0 is the "border" (above row / left column), rows/cols 1-4 are the 4x4 block being
    // predicted. Origin (the block's top-left pixel) is at (row=1, col=1) -> flat index stride+1.
    private const int Stride = 5;
    private const int Origin = Stride + 1;

    private static byte[] BuildBuffer(byte[] aboveRow, byte[] leftCol, byte corner)
    {
        var plane = new byte[Stride * Stride];
        for (int i = 0; i < 4; i++)
        {
            plane[Origin - Stride + i] = aboveRow[i];
            plane[Origin + (i * Stride) - 1] = leftCol[i];
        }

        plane[Origin - Stride - 1] = corner;
        return plane;
    }

    private static byte[,] ReadBlock(byte[] plane)
    {
        var block = new byte[4, 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                block[y, x] = plane[Origin + (y * Stride) + x];
            }
        }

        return block;
    }

    [Fact]
    public void PredictDc_BothNeighborsAvailable_AveragesAllEightSamples()
    {
        byte[] plane = BuildBuffer([10, 20, 30, 40], [5, 15, 25, 35], corner: 1);

        Vp8IntraPredictionWholeBlock.PredictDc(plane, Origin, Stride, 4, hasAbove: true, hasLeft: true);

        // sum = 10+20+30+40+5+15+25+35 = 180; dc = (180+4)>>3 = 23.
        byte[,] block = ReadBlock(plane);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(23, block[y, x]);
            }
        }
    }

    [Fact]
    public void PredictDc_OnlyAboveAvailable_AveragesAboveRowOnly()
    {
        byte[] plane = BuildBuffer([10, 20, 30, 40], [0, 0, 0, 0], corner: 0);

        Vp8IntraPredictionWholeBlock.PredictDc(plane, Origin, Stride, 4, hasAbove: true, hasLeft: false);

        // sum = 10+20+30+40 = 100; dc = (100+2)>>2 = 25.
        byte[,] block = ReadBlock(plane);
        Assert.Equal(25, block[0, 0]);
        Assert.Equal(25, block[3, 3]);
    }

    [Fact]
    public void PredictDc_OnlyLeftAvailable_AveragesLeftColumnOnly()
    {
        byte[] plane = BuildBuffer([0, 0, 0, 0], [5, 15, 25, 35], corner: 0);

        Vp8IntraPredictionWholeBlock.PredictDc(plane, Origin, Stride, 4, hasAbove: false, hasLeft: true);

        // sum = 5+15+25+35 = 80; dc = (80+2)>>2 = 20.
        byte[,] block = ReadBlock(plane);
        Assert.Equal(20, block[0, 0]);
        Assert.Equal(20, block[3, 3]);
    }

    [Fact]
    public void PredictDc_NoNeighbors_UsesMidGray128()
    {
        byte[] plane = BuildBuffer([0, 0, 0, 0], [0, 0, 0, 0], corner: 0);

        Vp8IntraPredictionWholeBlock.PredictDc(plane, Origin, Stride, 4, hasAbove: false, hasLeft: false);

        byte[,] block = ReadBlock(plane);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(128, block[y, x]);
            }
        }
    }

    [Fact]
    public void PredictVertical_ReplicatesAboveRowIntoEveryRow()
    {
        byte[] plane = BuildBuffer([10, 20, 30, 40], [0, 0, 0, 0], corner: 0);

        Vp8IntraPredictionWholeBlock.PredictVertical(plane, Origin, Stride, 4);

        byte[,] block = ReadBlock(plane);
        for (int y = 0; y < 4; y++)
        {
            Assert.Equal(10, block[y, 0]);
            Assert.Equal(20, block[y, 1]);
            Assert.Equal(30, block[y, 2]);
            Assert.Equal(40, block[y, 3]);
        }
    }

    [Fact]
    public void PredictHorizontal_ReplicatesLeftColumnAcrossEveryRow()
    {
        byte[] plane = BuildBuffer([0, 0, 0, 0], [5, 15, 25, 35], corner: 0);

        Vp8IntraPredictionWholeBlock.PredictHorizontal(plane, Origin, Stride, 4);

        byte[,] block = ReadBlock(plane);
        for (int x = 0; x < 4; x++)
        {
            Assert.Equal(5, block[0, x]);
            Assert.Equal(15, block[1, x]);
            Assert.Equal(25, block[2, x]);
            Assert.Equal(35, block[3, x]);
        }
    }

    [Fact]
    public void PredictTrueMotion_ComputesAboveLeftMinusCornerPerPixel()
    {
        byte[] plane = BuildBuffer([10, 20, 30, 40], [5, 15, 25, 35], corner: 1);

        Vp8IntraPredictionWholeBlock.PredictTrueMotion(plane, Origin, Stride, 4);

        byte[,] block = ReadBlock(plane);
        Assert.Equal(14, block[0, 0]); // 10 + 5 - 1
        Assert.Equal(44, block[0, 3]); // 40 + 5 - 1
        Assert.Equal(44, block[3, 0]); // 10 + 35 - 1
        Assert.Equal(74, block[3, 3]); // 40 + 35 - 1
    }

    [Fact]
    public void PredictTrueMotion_ClampsOutOfRangeResultsToByteBounds()
    {
        // above=250, left=250, corner=0 -> raw 500, must clamp to 255.
        byte[] plane = BuildBuffer([250, 250, 250, 250], [250, 250, 250, 250], corner: 0);

        Vp8IntraPredictionWholeBlock.PredictTrueMotion(plane, Origin, Stride, 4);

        byte[,] block = ReadBlock(plane);
        Assert.Equal(255, block[0, 0]);

        // above=0, left=0, corner=250 -> raw -250, must clamp to 0.
        byte[] plane2 = BuildBuffer([0, 0, 0, 0], [0, 0, 0, 0], corner: 250);
        Vp8IntraPredictionWholeBlock.PredictTrueMotion(plane2, Origin, Stride, 4);
        byte[,] block2 = ReadBlock(plane2);
        Assert.Equal(0, block2[0, 0]);
    }
}