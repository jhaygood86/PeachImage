namespace PeachImage.Formats.Webp.Decoding.Vp8L;

/// <summary>
/// Performs VP8L's LZ77-style backward-copy: copies a run of pixels starting some distance behind the
/// current write position, into and past it.
/// </summary>
internal static class Vp8LBackwardCopier
{
    /// <summary>
    /// When <paramref name="distance"/> &lt; <paramref name="length"/> the source and destination ranges
    /// overlap (the classic LZ77 "repeat the last N pixels" flat-color/run-length case) — the copy must
    /// observe already-written pixels as it goes, so a single bulk <c>Array.Copy</c>/<c>Span.CopyTo</c>
    /// (which reads the *original*, pre-copy source snapshot) would corrupt the result. This copies one
    /// pixel at a time in that case; for a non-overlapping copy (the common case for anything but flat runs)
    /// it copies in a single bulk operation.
    /// </summary>
    public static void CopyPixels(Span<uint> argb, int destPos, int distance, int length)
    {
        int sourcePos = destPos - distance;

        if (distance >= length)
        {
            argb.Slice(sourcePos, length).CopyTo(argb.Slice(destPos, length));
            return;
        }

        for (int i = 0; i < length; i++)
        {
            argb[destPos + i] = argb[sourcePos + i];
        }
    }
}
