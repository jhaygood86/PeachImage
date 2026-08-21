using PeachImage.Formats.Webp.Decoding.Vp8;

namespace PeachImage.Formats.Webp.Encoding.Vp8;

/// <summary>
/// Writes per-macroblock mode data (skip flag, whole-block/B_PRED luma mode, chroma mode) to partition 0 — the
/// write-side mirror of <see cref="Vp8ModeDecoder.DecodeMacroblock"/>, including its context-adaptive 4x4
/// submode bookkeeping. Reuses <see cref="Vp8BModeProbabilities.Table"/> and <see cref="Vp8PredictionModes"/>'s
/// constants as-is (the same spec-fixed probabilities and mode cascade serve both directions). v1 never enables
/// segmentation, so unlike <see cref="Vp8ModeDecoder"/> this never reads/writes a segment id.
/// </summary>
internal sealed class Vp8ModeWriter
{
    private readonly int[] _aboveSubblockModes;
    private readonly int[] _leftSubblockModes = new int[4];

    public Vp8ModeWriter(int macroblockCols)
    {
        _aboveSubblockModes = new int[macroblockCols * 4];
        Array.Fill(_aboveSubblockModes, Vp8PredictionModes.BDcPred);
    }

    /// <summary>Resets the left-neighbor subblock-mode context; call at the start of each macroblock row.</summary>
    public void StartRow() => Array.Fill(_leftSubblockModes, Vp8PredictionModes.BDcPred);

    public void WriteMacroblockModes(Vp8BoolEncoder bw, int mbX, bool skip, bool useSkipProbability, int skipFalseProbability, bool isI4x4, int yMode, ReadOnlySpan<int> subModes, int uvMode)
    {
        if (useSkipProbability)
        {
            bw.PutBit(skip ? 1 : 0, skipFalseProbability);
        }

        bw.PutBit(isI4x4 ? 0 : 1, 145); // Vp8ModeDecoder: IsI4x4 = GetBit(145) == 0.

        int baseCol = mbX * 4;
        if (!isI4x4)
        {
            WriteWholeBlockYMode(bw, yMode);
            for (int i = 0; i < 4; i++)
            {
                _aboveSubblockModes[baseCol + i] = yMode;
                _leftSubblockModes[i] = yMode;
            }
        }
        else
        {
            for (int y = 0; y < 4; y++)
            {
                int leftMode = _leftSubblockModes[y];
                for (int x = 0; x < 4; x++)
                {
                    int aboveMode = _aboveSubblockModes[baseCol + x];
                    int mode = subModes[(y * 4) + x];
                    WriteSubblockMode(bw, aboveMode, leftMode, mode);
                    _aboveSubblockModes[baseCol + x] = mode;
                    leftMode = mode;
                }

                _leftSubblockModes[y] = leftMode;
            }
        }

        WriteUvMode(bw, uvMode);
    }

    /// <summary>Mirrors <c>Vp8ModeDecoder.DecodeMacroblock</c>'s <c>ymode</c> cascade: <c>GetBit(156)!=0 ? (GetBit(128)!=0?TM:H) : (GetBit(163)!=0?V:DC)</c>.</summary>
    private static void WriteWholeBlockYMode(Vp8BoolEncoder bw, int mode)
    {
        switch (mode)
        {
            case Vp8PredictionModes.DcPred:
                bw.PutBit(0, 156);
                bw.PutBit(0, 163);
                break;
            case Vp8PredictionModes.VPred:
                bw.PutBit(0, 156);
                bw.PutBit(1, 163);
                break;
            case Vp8PredictionModes.HPred:
                bw.PutBit(1, 156);
                bw.PutBit(0, 128);
                break;
            case Vp8PredictionModes.TmPred:
                bw.PutBit(1, 156);
                bw.PutBit(1, 128);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown VP8 whole-block prediction mode.");
        }
    }

    /// <summary>Mirrors <c>Vp8ModeDecoder.DecodeMacroblock</c>'s <c>info.UvMode</c> cascade: <c>GetBit(142)==0 ? DC : GetBit(114)==0 ? V : GetBit(183)!=0 ? TM : H</c>.</summary>
    private static void WriteUvMode(Vp8BoolEncoder bw, int mode)
    {
        switch (mode)
        {
            case Vp8PredictionModes.DcPred:
                bw.PutBit(0, 142);
                break;
            case Vp8PredictionModes.VPred:
                bw.PutBit(1, 142);
                bw.PutBit(0, 114);
                break;
            case Vp8PredictionModes.HPred:
                bw.PutBit(1, 142);
                bw.PutBit(1, 114);
                bw.PutBit(0, 183);
                break;
            case Vp8PredictionModes.TmPred:
                bw.PutBit(1, 142);
                bw.PutBit(1, 114);
                bw.PutBit(1, 183);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown VP8 whole-block prediction mode.");
        }
    }

    /// <summary>Mirrors <c>Vp8ModeDecoder.DecodeSubblockMode</c>'s nested cascade exactly (same branch order, same probability-table indices).</summary>
    private static void WriteSubblockMode(Vp8BoolEncoder bw, int above, int left, int mode)
    {
        byte P(int k) => Vp8BModeProbabilities.Table[above, left, k];

        switch (mode)
        {
            case Vp8PredictionModes.BDcPred:
                bw.PutBit(0, P(0));
                return;
            case Vp8PredictionModes.BTmPred:
                bw.PutBit(1, P(0));
                bw.PutBit(0, P(1));
                return;
            case Vp8PredictionModes.BVePred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(0, P(2));
                return;
            case Vp8PredictionModes.BHePred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(0, P(3));
                bw.PutBit(0, P(4));
                return;
            case Vp8PredictionModes.BRdPred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(0, P(3));
                bw.PutBit(1, P(4));
                bw.PutBit(0, P(5));
                return;
            case Vp8PredictionModes.BVrPred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(0, P(3));
                bw.PutBit(1, P(4));
                bw.PutBit(1, P(5));
                return;
            case Vp8PredictionModes.BLdPred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(1, P(3));
                bw.PutBit(0, P(6));
                return;
            case Vp8PredictionModes.BVlPred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(1, P(3));
                bw.PutBit(1, P(6));
                bw.PutBit(0, P(7));
                return;
            case Vp8PredictionModes.BHdPred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(1, P(3));
                bw.PutBit(1, P(6));
                bw.PutBit(1, P(7));
                bw.PutBit(0, P(8));
                return;
            case Vp8PredictionModes.BHuPred:
                bw.PutBit(1, P(0));
                bw.PutBit(1, P(1));
                bw.PutBit(1, P(2));
                bw.PutBit(1, P(3));
                bw.PutBit(1, P(6));
                bw.PutBit(1, P(7));
                bw.PutBit(1, P(8));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown VP8 4x4 subblock prediction mode.");
        }
    }
}
