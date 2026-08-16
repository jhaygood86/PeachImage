using PeachImage.Formats.Gif;
using PeachImage.Formats.Gif.Decoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

public class GifImageDecoderCumulativeLimitTests
{
    [Fact]
    public void Decode_CumulativeCanvasBytesExceedsCap_ThrowsBeforeAllocatingFurtherFrames()
    {
        // Every decoded frame is composited onto, and copied out of, the *full* logical-screen canvas
        // regardless of that frame's own size — and every copy is retained for the whole animation. Neither
        // MaxPixelCount (bounds one canvas) nor MaxFrameCount (bounds frame count) alone stops a modest
        // canvas times many frames from multiplying into a huge total allocation. 8x8 RGBA = 256 bytes per
        // frame; a cap of 512 bytes allows only 2 frames, so the 5-frame animation below must be rejected
        // partway through rather than decoding (and retaining) all 5.
        byte[] gif = EncodeAnimation(canvasWidth: 8, canvasHeight: 8, frameCount: 5);

        Assert.Throws<GifDecodingException>(() =>
            GifImageDecoder.Decode(new MemoryStream(gif), maxFrames: 100, maxCumulativeCanvasBytes: 512));
    }

    [Fact]
    public void Decode_CumulativeCanvasBytesWithinCap_DecodesAllFrames()
    {
        byte[] gif = EncodeAnimation(canvasWidth: 8, canvasHeight: 8, frameCount: 5);

        var (frames, _) = GifImageDecoder.Decode(new MemoryStream(gif), maxFrames: 100, maxCumulativeCanvasBytes: 8 * 8 * 4 * 5);

        Assert.Equal(5, frames.Count);
        foreach (var frame in frames)
        {
            frame.Dispose();
        }
    }

    [Fact]
    public void Decode_OrdinaryAnimation_UsesProductionDefaultAndDecodesNormally()
    {
        byte[] gif = EncodeAnimation(canvasWidth: 8, canvasHeight: 8, frameCount: 3);

        var (frames, _) = GifImageDecoder.Decode(new MemoryStream(gif), maxFrames: 100);

        Assert.Equal(3, frames.Count);
        foreach (var frame in frames)
        {
            frame.Dispose();
        }
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

        using var source = new AnimatedImage(frames, loopCount: 0);
        using var ms = new MemoryStream();
        source.Save(ms, "gif", new GifEncoderOptions());
        return ms.ToArray();
    }
}
