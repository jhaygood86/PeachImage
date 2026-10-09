using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Features;

/// <summary>
/// A patch dictionary: rectangles cut from earlier (reference) frames that are blended onto the frame at listed positions,
/// which lets repeated content such as text glyphs be stored once.
/// </summary>
internal sealed class JxlPatches
{
    private const int NumContexts = 10;
    private const int NumRefPatchContext = 0;
    private const int ReferenceFrameContext = 1;
    private const int PatchSizeContext = 2;
    private const int PatchReferencePositionContext = 3;
    private const int PatchPositionContext = 4;
    private const int PatchBlendModeContext = 5;
    private const int PatchOffsetContext = 6;
    private const int PatchCountContext = 7;
    private const int PatchAlphaChannelContext = 8;
    private const int PatchClampContext = 9;

    private readonly record struct RefPosition(int Ref, int X0, int Y0, int Width, int Height);

    private readonly record struct Position(int X, int Y, int RefIndex);

    private readonly List<RefPosition> _refPositions = [];
    private readonly List<Position> _positions = [];
    private readonly List<JxlPatchBlending> _blendings = [];
    private int _blendingStride;
    private int[][] _patchesPerRow = [];

    public bool HasAny => _positions.Count > 0;

    /// <summary>Reads a dictionary for a frame whose padded size is <paramref name="xsize"/> x <paramref name="ysize"/>.</summary>
    public static JxlPatches Read(ref JxlBitReader br, int xsize, int ysize, int numExtraChannels, JxlDecoderState state)
    {
        var code = JxlEntropyCode.Read(ref br, NumContexts, out byte[] contextMap);
        using var reader = new JxlSymbolReader(code, ref br);
        var result = new JxlPatches { _blendingStride = numExtraChannels + 1 };

        long numRefPatches = ReadNum(reader, ref br, contextMap, NumRefPatchContext);
        long numPixels = (long)xsize * ysize;
        long maxRefPatches = 1024 + (numPixels / 4);
        long maxPatches = maxRefPatches * 4;
        long maxBlendingInfos = maxPatches * 4;
        if (numRefPatches > maxRefPatches)
        {
            throw new JxlDecodingException("Too many patches in dictionary.");
        }

        long totalPatches = 0;
        long nextSize = 1;
        bool chooseAlpha = numExtraChannels > 1;
        for (long id = 0; id < numRefPatches; id++)
        {
            int refIndex = checked((int)ReadNum(reader, ref br, contextMap, ReferenceFrameContext));
            if (refIndex >= JxlDecoderState.ReferenceSlots || state.References[refIndex] is not { } reference)
            {
                throw new JxlDecodingException("Invalid reference frame ID.");
            }

            if (!reference.IsInXyb)
            {
                throw new JxlDecodingException("Patches cannot use frames saved after the colour transform.");
            }

            long x0 = ReadNum(reader, ref br, contextMap, PatchReferencePositionContext);
            long y0 = ReadNum(reader, ref br, contextMap, PatchReferencePositionContext);
            long refW = (long)ReadNum(reader, ref br, contextMap, PatchSizeContext) + 1;
            long refH = (long)ReadNum(reader, ref br, contextMap, PatchSizeContext) + 1;
            if (x0 + refW > reference.Width || y0 + refH > reference.Height)
            {
                throw new JxlDecodingException("Invalid position specified in reference frame.");
            }

            long idCount = ReadNum(reader, ref br, contextMap, PatchCountContext);
            if (idCount > maxPatches)
            {
                throw new JxlDecodingException("Too many patches in dictionary.");
            }

            idCount++;
            totalPatches += idCount;
            if (totalPatches > maxPatches)
            {
                throw new JxlDecodingException("Too many patches in dictionary.");
            }

            if (nextSize < totalPatches)
            {
                nextSize = Math.Min(nextSize * 2, maxPatches);
            }

            if (nextSize * result._blendingStride > maxBlendingInfos)
            {
                throw new JxlDecodingException("Too many patches in dictionary.");
            }

            int refPositionIndex = result._refPositions.Count;
            for (long i = 0; i < idCount; i++)
            {
                long x;
                long y;
                if (i == 0)
                {
                    x = ReadNum(reader, ref br, contextMap, PatchPositionContext);
                    y = ReadNum(reader, ref br, contextMap, PatchPositionContext);
                }
                else
                {
                    var previous = result._positions[^1];
                    long deltaX = JxlFieldReader.UnpackSigned(ReadNum(reader, ref br, contextMap, PatchOffsetContext));
                    if (deltaX < 0 && -deltaX > previous.X)
                    {
                        throw new JxlDecodingException("Invalid patch: negative x coordinate.");
                    }

                    x = previous.X + deltaX;
                    long deltaY = JxlFieldReader.UnpackSigned(ReadNum(reader, ref br, contextMap, PatchOffsetContext));
                    if (deltaY < 0 && -deltaY > previous.Y)
                    {
                        throw new JxlDecodingException("Invalid patch: negative y coordinate.");
                    }

                    y = previous.Y + deltaY;
                }

                if (x + refW > xsize || y + refH > ysize)
                {
                    throw new JxlDecodingException("A patch extends beyond the frame.");
                }

                for (int j = 0; j < result._blendingStride; j++)
                {
                    uint mode = ReadNum(reader, ref br, contextMap, PatchBlendModeContext);
                    if (mode >= JxlPatchBlending.ModeCount)
                    {
                        throw new JxlDecodingException($"Invalid patch blend mode: {mode}.");
                    }

                    var blendMode = (JxlPatchBlendMode)mode;
                    var probe = new JxlPatchBlending(blendMode, 0, false);
                    int alphaChannel = 0;
                    if (probe.UsesAlpha && chooseAlpha)
                    {
                        alphaChannel = checked((int)ReadNum(reader, ref br, contextMap, PatchAlphaChannelContext));
                        if (alphaChannel >= numExtraChannels)
                        {
                            throw new JxlDecodingException("Invalid alpha channel for patch blending.");
                        }
                    }

                    bool clamp = probe.UsesClamp && ReadNum(reader, ref br, contextMap, PatchClampContext) != 0;
                    result._blendings.Add(new JxlPatchBlending(blendMode, alphaChannel, clamp));
                }

                result._positions.Add(new Position((int)x, (int)y, refPositionIndex));
            }

            result._refPositions.Add(new RefPosition(refIndex, (int)x0, (int)y0, (int)refW, (int)refH));
            br.ThrowIfOverrun();
        }

        if (!reader.CheckFinalState())
        {
            throw new JxlDecodingException("ANS final state check failed while decoding patches.");
        }

        result.IndexRows(ysize);
        return result;
    }

    private static uint ReadNum(JxlSymbolReader reader, ref JxlBitReader br, byte[] contextMap, int context) =>
        reader.ReadHybridUint(context, ref br, contextMap);

    // Per image row, the indices of the patches covering it, in dictionary order (order matters for non-additive blends).
    private void IndexRows(int height)
    {
        var counts = new int[height];
        foreach (var position in _positions)
        {
            int h = _refPositions[position.RefIndex].Height;
            for (int y = position.Y; y < Math.Min(height, position.Y + h); y++)
            {
                counts[y]++;
            }
        }

        _patchesPerRow = new int[height][];
        for (int y = 0; y < height; y++)
        {
            _patchesPerRow[y] = counts[y] == 0 ? [] : new int[counts[y]];
            counts[y] = 0;
        }

        for (int i = 0; i < _positions.Count; i++)
        {
            var position = _positions[i];
            int h = _refPositions[position.RefIndex].Height;
            for (int y = position.Y; y < Math.Min(height, position.Y + h); y++)
            {
                _patchesPerRow[y][counts[y]++] = i;
            }
        }
    }

    /// <summary>
    /// Blends the patches onto <paramref name="planes"/> (three colour planes followed by the extra channels, all of
    /// <paramref name="stride"/> samples per row), in place.
    /// </summary>
    public void Apply(float[][] planes, int stride, int width, int height, IReadOnlyList<JxlExtraChannelInfo> extraChannels, JxlDecoderState state)
    {
        for (int y = 0; y < Math.Min(height, _patchesPerRow.Length); y++)
        {
            foreach (int index in _patchesPerRow[y])
            {
                var position = _positions[index];
                var reference = _refPositions[position.RefIndex];
                if (position.X >= width)
                {
                    continue;
                }

                int x0 = position.X;
                int x1 = Math.Min(position.X + reference.Width, width);
                var source = state.References[reference.Ref]!;
                int iy = y - position.Y;
                int fgOffset = ((reference.Y0 + iy) * source.Width) + reference.X0;
                int blendingIndex = index * _blendingStride;
                JxlBlending.BlendRun(
                    planes,
                    (y * stride) + x0,
                    source.Planes,
                    fgOffset,
                    x1 - x0,
                    _blendings[blendingIndex],
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_blendings).Slice(blendingIndex + 1, _blendingStride - 1),
                    extraChannels);
            }
        }
    }
}
