using PeachImage.Formats.Gif;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

/// <summary>
/// Verifies the frame-validity contract documented on <see cref="AnimatedImage.Frames"/>: a pulled frame's
/// <see cref="Image"/> aliases the decoder's persistent compositor canvas and is invalidated once the next
/// frame is pulled from the same enumeration, unless the caller clones it first. Exercised via GIF (the
/// simpler of the two animated formats to build a fixture for) — the contract itself is codec-agnostic, since
/// it's enforced by <see cref="Image"/>'s own invalidation guard, not by GIF-specific code.
/// </summary>
public class AnimatedFrameInvalidationTests
{
    [Fact]
    public void Frames_ImageReadAfterNextFramePulled_Throws()
    {
        byte[] gif = EncodeTwoFrameAnimation();

        using var enumerator = AnimatedImage.Load(new MemoryStream(gif)).Frames.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var first = enumerator.Current;

        Assert.True(enumerator.MoveNext());

        Assert.Throws<InvalidOperationException>(() => first.Image.GetPixelSpan());
    }

    [Fact]
    public void Frames_ClonedBeforeNextFramePulled_RemainsValidAndIndependent()
    {
        byte[] gif = EncodeTwoFrameAnimation();

        using var enumerator = AnimatedImage.Load(new MemoryStream(gif)).Frames.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var firstClone = enumerator.Current.Clone();

        Assert.True(enumerator.MoveNext());
        var second = enumerator.Current;

        // The clone must still be readable (no invalidation) and must reflect frame 1's pixels, not frame 2's.
        var firstClonePixels = firstClone.Image.GetPixelSpan();
        Assert.False(firstClonePixels.SequenceEqual(second.Image.GetPixelSpan()));
    }

    private static byte[] EncodeTwoFrameAnimation()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(SolidImage(8, 8, 200, 40, 40), TimeSpan.FromMilliseconds(20), FrameDisposalMethod.DoNotDispose),
            new(SolidImage(8, 8, 40, 200, 40), TimeSpan.FromMilliseconds(20), FrameDisposalMethod.DoNotDispose),
        };
        var source = new AnimatedImage(frames, width: 8, height: 8, loopCount: 0);

        using var ms = new MemoryStream();
        source.Save(ms, "gif", new GifEncoderOptions());
        return ms.ToArray();
    }

    private static Image SolidImage(int width, int height, byte r, byte g, byte b)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return image;
    }
}
