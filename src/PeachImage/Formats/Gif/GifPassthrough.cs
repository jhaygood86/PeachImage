using PeachImage.Formats.Gif.Decoding;
using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif;

/// <summary>
/// Reads a GIF's first frame's LZW-compressed image data verbatim, with no LZW decompress, for callers
/// that want to re-embed the original compressed pixel stream in another container — e.g. a PDF
/// <c>/LZWDecode</c> image stream — rather than decode it into an <see cref="Image"/> and re-encode it.
/// Only the first frame is read (mirroring <see cref="GifDecoder.Decode"/>'s "decode just the first
/// frame" semantics); see <see cref="GifPassthroughInfo.IsAnimated"/>.
/// </summary>
public static class GifPassthrough
{
    /// <summary>
    /// Attempts to read <paramref name="info"/> from <paramref name="stream"/>. Returns <see langword="false"/>
    /// (never throws) if the stream is not a GIF, or is structurally invalid, or has no image frames —
    /// matching <see cref="Image.TryLoad"/>'s contract. Succeeds for interlaced and animated GIFs alike;
    /// callers should check <see cref="GifPassthroughInfo.Interlaced"/> and
    /// <see cref="GifPassthroughInfo.MinCodeSize"/> themselves to decide whether the raw data is usable
    /// for their target format (see those members' doc comments for the specific compatibility
    /// conditions).
    /// </summary>
    public static bool TryRead(Stream stream, out GifPassthroughInfo info)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            info = Read(stream);
            return true;
        }
        catch (GifDecodingException)
        {
            info = default;
            return false;
        }
    }

    private static GifPassthroughInfo Read(Stream stream)
    {
        var header = GifImageDecoder.ReadHeader(stream);
        var prelude = GifImageDecoder.ReadPrelude(stream);
        var descriptor = GifImageDescriptorReader.Read(stream);
        byte minCodeSize = GifStreamHelpers.ReadByteOrThrow(stream);

        var pool = GifBufferPool.Shared;
        var (rentedImageData, imageDataLength) = GifSubBlocks.ReadAllImageData(stream);
        byte[] lzwData;
        try
        {
            lzwData = rentedImageData.AsSpan(0, imageDataLength).ToArray();
        }
        finally
        {
            pool.Return(rentedImageData);
        }

        byte[] palette = descriptor.LocalColorTable.Length > 0 ? descriptor.LocalColorTable : header.GlobalColorTable;
        if (palette.Length == 0)
        {
            throw new GifDecodingException("GIF frame has no color table (neither local nor global).");
        }

        var gce = prelude.FirstFrameGce ?? GifGraphicControlExtension.Default;

        bool isAnimated;
        try
        {
            isAnimated = GifAnimationScanner.HasAnotherFrame(stream);
        }
        catch (GifDecodingException)
        {
            // Frame 1 read successfully; trailing data beyond it is malformed/truncated. Don't fail an
            // otherwise-successful passthrough read just because we can't tell whether more frames follow.
            isAnimated = false;
        }

        return new GifPassthroughInfo(
            descriptor.Width,
            descriptor.Height,
            descriptor.Left,
            descriptor.Top,
            header.Width,
            header.Height,
            minCodeSize,
            descriptor.Interlaced,
            palette,
            gce.TransparentColorIndex,
            isAnimated,
            lzwData);
    }
}
