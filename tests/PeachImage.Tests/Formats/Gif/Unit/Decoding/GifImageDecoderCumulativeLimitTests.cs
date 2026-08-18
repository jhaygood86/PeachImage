using PeachImage.Formats.Gif;
using PeachImage.Formats.Gif.Decoding;
using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

public class GifImageDecoderCumulativeLimitTests
{
    [Fact]
    public void Decode_CumulativeCanvasBytesExceedsCap_ThrowsBeforeAllocatingFurtherFrames()
    {
        // Every decoded frame is composited onto, and copied out of, the *full* logical-screen canvas
        // regardless of that frame's own size. Neither MaxPixelCount (bounds one canvas) nor MaxFrameCount
        // (bounds frame count) alone stops a modest canvas times many frames from costing a huge amount of
        // total compositor work. 8x8 RGBA = 256 bytes per frame; a cap of 512 bytes allows only 2 frames, so
        // decoding the 5-frame animation below to completion must throw partway through.
        byte[] gif = EncodeAnimation(canvasWidth: 8, canvasHeight: 8, frameCount: 5);

        Assert.Throws<GifDecodingException>(() => DecodeAll(new MemoryStream(gif), maxFrames: 100, maxCumulativeCanvasBytes: 512));
    }

    [Fact]
    public void Decode_CumulativeCanvasBytesWithinCap_DecodesAllFrames()
    {
        byte[] gif = EncodeAnimation(canvasWidth: 8, canvasHeight: 8, frameCount: 5);

        var (frames, _) = DecodeAll(new MemoryStream(gif), maxFrames: 100, maxCumulativeCanvasBytes: 8 * 8 * 4 * 5);

        Assert.Equal(5, frames.Count);
    }

    [Fact]
    public void Decode_OrdinaryAnimation_UsesProductionDefaultAndDecodesNormally()
    {
        byte[] gif = EncodeAnimation(canvasWidth: 8, canvasHeight: 8, frameCount: 3);

        var (frames, _) = DecodeAll(new MemoryStream(gif), maxFrames: 100);

        Assert.Equal(3, frames.Count);
    }

    /// <summary>Forces full enumeration of the lazy <see cref="GifImageDecoder.DecodeFrames"/> sequence into a list, so a cap-exceeded throw (which only happens as later frames are pulled) surfaces here rather than lazily at the caller's own enumeration.</summary>
    private static (List<GifFrame> Frames, int LoopCount) DecodeAll(Stream stream, int maxFrames, long maxCumulativeCanvasBytes = GifDecodingLimits.MaxCumulativeCanvasBytes)
    {
        var header = GifImageDecoder.ReadHeader(stream);
        var prelude = GifImageDecoder.ReadPrelude(stream);
        var frames = GifImageDecoder.DecodeFrames(stream, header, prelude, maxFrames, maxCumulativeCanvasBytes).ToList();
        return (frames, prelude.LoopCount);
    }

    private static byte[] EncodeAnimation(int canvasWidth, int canvasHeight, int frameCount)
    {
        var frames = new List<AnimatedImageFrame>();
        for (int i = 0; i < frameCount; i++)
        {
            var image = Image.Create(canvasWidth, canvasHeight, PixelFormat.Rgb24);
            byte shade = (byte)(i * 30);
            var pixels = image.GetPixelSpan();
            for (int p = 0; p < pixels.Length; p += 3)
            {
                pixels[p] = shade;
                pixels[p + 1] = shade;
                pixels[p + 2] = shade;
            }

            frames.Add(new AnimatedImageFrame(image, TimeSpan.FromMilliseconds(50), FrameDisposalMethod.None));
        }

        var source = new AnimatedImage(frames, width: canvasWidth, height: canvasHeight, loopCount: 0);
        using var ms = new MemoryStream();
        source.Save(ms, "gif", new GifEncoderOptions());

        return ms.ToArray();
    }
}
