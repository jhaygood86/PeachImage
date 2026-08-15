namespace PeachImage.Formats.Webp.Decoding.Vp8.Upsampling;

/// <summary>
/// VP8's 4:2:0 chroma upsampler ("fancy"/bilinear upsampling, libwebp's <c>src/dsp/upsampling.c</c>): each
/// full-resolution output sample is a 9:3:3:1-weighted average of the 4 chroma samples nearest to it (diagonal
/// bilinear interpolation, not a straight nearest/box replication and not the same 9:3:3:1-adjacent but
/// differently-shaped filter this codebase's JPEG decoder uses for its own chroma upsampling).
/// </summary>
/// <remarks>
/// libwebp's actual C implementation computes this incrementally across a scanline pair (carrying forward
/// "top-left"/"left" chroma samples and two precomputed diagonal sums) purely for speed; the per-pixel closed
/// form implemented here - weight 9 on whichever of the 2x2 neighboring chroma samples is nearest the output
/// pixel in both dimensions, 3 on each adjacent sample, 1 on the diagonal-opposite sample, then <c>(sum+8)&gt;&gt;4</c>
/// - was derived by working through that incremental macro's arithmetic (see the file's own header comment,
/// which documents the same weights) rather than lifted from a ready-made per-pixel formula, so it is flagged
/// here as a best-effort transcription worth double-checking against a real libwebp build if exact bit-parity
/// ever matters. At the plane edges, the "far" neighbor coordinate clamps to the nearest valid chroma sample,
/// reproducing libwebp's edge-replication behavior.
/// </remarks>
internal static class Vp8ChromaUpsampler
{
    /// <summary>Computes the upsampled chroma sample at full-resolution output position (<paramref name="outX"/>, <paramref name="outY"/>).</summary>
    public static byte Sample(ReadOnlySpan<byte> chroma, int stride, int chromaWidth, int chromaHeight, int outX, int outY)
    {
        int nearCol = Clamp(outX / 2, chromaWidth - 1);
        int farCol = Clamp((outX / 2) + (((outX & 1) == 1) ? 1 : -1), chromaWidth - 1);
        int nearRow = Clamp(outY / 2, chromaHeight - 1);
        int farRow = Clamp((outY / 2) + (((outY & 1) == 1) ? 1 : -1), chromaHeight - 1);

        int nearNear = chroma[(nearRow * stride) + nearCol];
        int nearFar = chroma[(nearRow * stride) + farCol];
        int farNear = chroma[(farRow * stride) + nearCol];
        int farFar = chroma[(farRow * stride) + farCol];

        int sum = (9 * nearNear) + (3 * nearFar) + (3 * farNear) + farFar;
        return (byte)((sum + 8) >> 4);
    }

    private static int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;
}