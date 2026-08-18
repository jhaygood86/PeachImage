using PeachImage.Formats.Webp.Decoding.Vp8;
using PeachImage.Formats.Webp.Encoding.Vp8;

namespace PeachImage.Tests.Formats.Webp.Unit.Vp8Encoding;

/// <summary>
/// Validates <see cref="Vp8ModeWriter"/> by round-tripping through the real, unmodified <see cref="Vp8ModeDecoder"/>
/// across a full row of macroblocks -- covering both whole-block (16x16/UV) and per-subblock (B_PRED) mode
/// cascades, and the above/left context bookkeeping the B_PRED submode cascade depends on.
/// </summary>
public class Vp8ModeWriterTests
{
    private const int MbCols = 4;

    private static Vp8SegmentHeader NoSegmentHeader()
    {
        var bw = new Vp8BoolEncoder();
        bw.PutFlag(false); // segmentation_enabled = false.
        byte[] encoded = bw.Finish();
        var br = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        return Vp8SegmentHeader.Parse(br);
    }

    [Fact]
    public void WriteMacroblockModes_MixOfWholeBlockAndBPredMacroblocks_RoundTrips()
    {
        var writer = new Vp8ModeWriter(MbCols);
        var bw = new Vp8BoolEncoder();
        writer.StartRow();

        bool[] skips = [false, true, false, false];
        bool[] isI4x4s = [false, false, true, true];
        int[] yModes = [Vp8PredictionModes.DcPred, Vp8PredictionModes.VPred, 0, 0];
        int[] uvModes = [Vp8PredictionModes.DcPred, Vp8PredictionModes.HPred, Vp8PredictionModes.TmPred, Vp8PredictionModes.VPred];
        int[][] subModesPerMb =
        [
            [],
            [],
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 1, 2, 3, 4, 5, 6],
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9],
        ];

        for (int mbX = 0; mbX < MbCols; mbX++)
        {
            writer.WriteMacroblockModes(bw, mbX, skips[mbX], useSkipProbability: true, skipFalseProbability: 200, isI4x4s[mbX], yModes[mbX], isI4x4s[mbX] ? subModesPerMb[mbX] : null, uvModes[mbX]);
        }

        byte[] encoded = bw.Finish();

        var decoder = new Vp8ModeDecoder(MbCols, NoSegmentHeader(), useSkipProbability: true, skipFalseProbability: 200);
        var br = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        decoder.StartRow();

        var modes = new Vp8MacroblockModes();
        for (int mbX = 0; mbX < MbCols; mbX++)
        {
            decoder.DecodeMacroblock(br, mbX, modes);

            Assert.Equal(skips[mbX], modes.Skip);
            Assert.Equal(isI4x4s[mbX], modes.IsI4x4);
            Assert.Equal(uvModes[mbX], modes.UvMode);

            if (isI4x4s[mbX])
            {
                Assert.Equal(Vp8PredictionModes.BPred, modes.YMode);
                for (int i = 0; i < 16; i++)
                {
                    Assert.Equal(subModesPerMb[mbX][i], modes.SubModes[i]);
                }
            }
            else
            {
                Assert.Equal(yModes[mbX], modes.YMode);
            }
        }
    }

    [Theory]
    [InlineData(Vp8PredictionModes.DcPred)]
    [InlineData(Vp8PredictionModes.VPred)]
    [InlineData(Vp8PredictionModes.HPred)]
    [InlineData(Vp8PredictionModes.TmPred)]
    public void WriteMacroblockModes_EveryWholeBlockYMode_RoundTrips(int yMode)
    {
        var writer = new Vp8ModeWriter(1);
        var bw = new Vp8BoolEncoder();
        writer.StartRow();
        writer.WriteMacroblockModes(bw, 0, skip: false, useSkipProbability: false, skipFalseProbability: 0, isI4x4: false, yMode, subModes: null, Vp8PredictionModes.DcPred);
        byte[] encoded = bw.Finish();

        var decoder = new Vp8ModeDecoder(1, NoSegmentHeader(), useSkipProbability: false, skipFalseProbability: 0);
        var br = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        decoder.StartRow();
        var modes = new Vp8MacroblockModes();
        decoder.DecodeMacroblock(br, 0, modes);

        Assert.Equal(yMode, modes.YMode);
    }

    [Theory]
    [InlineData(Vp8PredictionModes.DcPred)]
    [InlineData(Vp8PredictionModes.VPred)]
    [InlineData(Vp8PredictionModes.HPred)]
    [InlineData(Vp8PredictionModes.TmPred)]
    public void WriteMacroblockModes_EveryUvMode_RoundTrips(int uvMode)
    {
        var writer = new Vp8ModeWriter(1);
        var bw = new Vp8BoolEncoder();
        writer.StartRow();
        writer.WriteMacroblockModes(bw, 0, skip: false, useSkipProbability: false, skipFalseProbability: 0, isI4x4: false, Vp8PredictionModes.DcPred, subModes: null, uvMode);
        byte[] encoded = bw.Finish();

        var decoder = new Vp8ModeDecoder(1, NoSegmentHeader(), useSkipProbability: false, skipFalseProbability: 0);
        var br = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        decoder.StartRow();
        var modes = new Vp8MacroblockModes();
        decoder.DecodeMacroblock(br, 0, modes);

        Assert.Equal(uvMode, modes.UvMode);
    }

    /// <summary>Sweeps every one of the 10 B_PRED submodes as the sole subblock (all others DC), which exercises every branch of the submode cascade.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void WriteMacroblockModes_EveryBPredSubmode_RoundTrips(int subMode)
    {
        var writer = new Vp8ModeWriter(1);
        var bw = new Vp8BoolEncoder();
        writer.StartRow();

        int[] subModes = new int[16];
        subModes[5] = subMode;

        writer.WriteMacroblockModes(bw, 0, skip: false, useSkipProbability: false, skipFalseProbability: 0, isI4x4: true, yMode: 0, subModes, Vp8PredictionModes.DcPred);
        byte[] encoded = bw.Finish();

        var decoder = new Vp8ModeDecoder(1, NoSegmentHeader(), useSkipProbability: false, skipFalseProbability: 0);
        var br = new Vp8BoolDecoder(encoded, 0, encoded.Length);
        decoder.StartRow();
        var modes = new Vp8MacroblockModes();
        decoder.DecodeMacroblock(br, 0, modes);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(subModes[i], modes.SubModes[i]);
        }
    }
}
