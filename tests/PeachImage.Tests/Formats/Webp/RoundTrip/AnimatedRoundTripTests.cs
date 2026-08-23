using System.Text;
using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding;

namespace PeachImage.Tests.Formats.Webp.RoundTrip;

public class AnimatedRoundTripTests
{
    [Fact]
    public void DecodeAnimation_ReportsDimensionsLoopCountAndFrames()
    {
        byte[] webp = BuildAnimatedWebp(
            canvasWidth: 6, canvasHeight: 6, loopCount: 3,
            SolidFrame(6, 6, 200, 40, 40, durationMs: 40),
            SolidFrame(6, 6, 40, 200, 40, durationMs: 60));

        using var ms = new MemoryStream(webp);
        var animated = WebpDecoder.DecodeAnimation(ms);

        Assert.Equal(6, animated.Width);
        Assert.Equal(6, animated.Height);
        Assert.Equal(3, animated.LoopCount);

        var frames = animated.Frames.ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(40), frames[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(60), frames[1].Duration);
    }

    [Fact]
    public void Decode_AnimatedFile_ReturnsOnlyFirstFrame_MarkedIsAnimated()
    {
        var frame1 = SolidImage(4, 4, 10, 20, 30, 255);
        byte[] webp = BuildAnimatedWebp(
            4, 4, loopCount: 0,
            SolidFrame(4, 4, 10, 20, 30, durationMs: 25),
            SolidFrame(4, 4, 90, 80, 70, durationMs: 25));

        using var ms = new MemoryStream(webp);
        var decoded = WebpDecoder.Decode(ms);

        Assert.True(decoded.IsAnimated);
        Assert.True(frame1.GetPixelSpan().SequenceEqual(decoded.GetPixelSpan()));
    }

    [Fact]
    public void Identify_AnimatedFile_ReportsIsAnimated()
    {
        byte[] webp = BuildAnimatedWebp(8, 5, loopCount: 0, SolidFrame(8, 5, 1, 2, 3, durationMs: 10));

        using var ms = new MemoryStream(webp);
        var info = WebpDecoder.Identify(ms);

        Assert.Equal(8, info.Width);
        Assert.Equal(5, info.Height);
        Assert.True(info.IsAnimated);
    }

    [Fact]
    public void Identify_NonAnimatedFile_ReportsIsAnimatedFalse()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);
        using var ms = new MemoryStream();
        source.Save(ms, "webp");
        ms.Position = 0;

        var info = WebpDecoder.Identify(ms);

        Assert.False(info.IsAnimated);
    }

    [Fact]
    public void Decode_NonAnimatedFile_ReportsIsAnimatedFalse()
    {
        var source = Image.Create(4, 4, PixelFormat.Rgb24);
        using var ms = new MemoryStream();
        source.Save(ms, "webp");
        ms.Position = 0;

        var decoded = WebpDecoder.Decode(ms);

        Assert.False(decoded.IsAnimated);
    }

    [Fact]
    public void AnimatedOpaqueFrames_RoundTrip_Exactly()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(CreateTiledImageRgba(32, 24, colorCount: 8, colorOffset: 0), TimeSpan.FromMilliseconds(50), FrameDisposalMethod.DoNotDispose),
            new(CreateTiledImageRgba(32, 24, colorCount: 8, colorOffset: 3), TimeSpan.FromMilliseconds(100), FrameDisposalMethod.RestoreToBackground),
            new(CreateTiledImageRgba(32, 24, colorCount: 8, colorOffset: 5), TimeSpan.FromMilliseconds(150), FrameDisposalMethod.None),
        };
        var source = new AnimatedImage(frames, width: 32, height: 24, loopCount: 7);

        using var ms = new MemoryStream();
        source.Save(ms, "webp", new WebpEncoderOptions());
        ms.Position = 0;

        var decodedAnimation = AnimatedImage.Load(ms);
        // Each pulled frame's Image aliases the decoder's persistent compositor canvas and is invalidated
        // once the next frame is pulled, so every frame must be cloned as it's pulled (not just ToList()'d)
        // to compare all of them afterward.
        var decoded = decodedAnimation.Frames.Select(frame => frame.Clone()).ToList();

        Assert.Equal(3, decoded.Count);
        Assert.Equal(7, decodedAnimation.LoopCount);

        // WebP's ANMF disposal is a single dispose-to-background bit: RestoreToBackground round-trips as
        // itself, but every other FrameDisposalMethod value (None, DoNotDispose) collapses to DoNotDispose.
        Assert.Equal(FrameDisposalMethod.DoNotDispose, decoded[0].Disposal);
        Assert.Equal(FrameDisposalMethod.RestoreToBackground, decoded[1].Disposal);
        Assert.Equal(FrameDisposalMethod.DoNotDispose, decoded[2].Disposal);

        for (int i = 0; i < frames.Count; i++)
        {
            // WebP's ANMF duration field is millisecond-precision (no GIF-style 10ms tick rounding).
            Assert.Equal(frames[i].Duration, decoded[i].Duration);
            Assert.True(frames[i].Image.GetPixelSpan().SequenceEqual(decoded[i].Image.GetPixelSpan()));
        }
    }

    [Fact]
    public void AnimatedTransparencyWithRestoreToBackground_RoundTrips_Exactly()
    {
        var opaqueFrame = Image.Create(20, 20, PixelFormat.Rgba32);
        FillSolid(opaqueFrame, 200, 40, 40);

        var holeFrame = Image.Create(20, 20, PixelFormat.Rgba32);
        FillSolid(holeFrame, 40, 40, 200);
        for (int y = 5; y < 15; y++)
        {
            var row = holeFrame.GetRowSpan(y);
            row[(5 * 4)..(15 * 4)].Clear();
        }

        var frames = new List<AnimatedImageFrame>
        {
            new(opaqueFrame, TimeSpan.FromMilliseconds(30), FrameDisposalMethod.RestoreToBackground),
            new(holeFrame, TimeSpan.FromMilliseconds(30), FrameDisposalMethod.None),
        };
        var source = new AnimatedImage(frames, width: 20, height: 20, loopCount: 0);

        using var ms = new MemoryStream();
        source.Save(ms, "webp", new WebpEncoderOptions());
        ms.Position = 0;

        var decodedAnimation = AnimatedImage.Load(ms);
        var decoded = decodedAnimation.Frames.Select(frame => frame.Clone()).ToList();

        Assert.Equal(2, decoded.Count);
        Assert.True(frames[0].Image.GetPixelSpan().SequenceEqual(decoded[0].Image.GetPixelSpan()));
        Assert.True(frames[1].Image.GetPixelSpan().SequenceEqual(decoded[1].Image.GetPixelSpan()));
    }

    [Fact]
    public void AnimatedImage_LossyOptions_RoundTrips_WithMatchingFrameCountAndTiming()
    {
        var frames = new List<AnimatedImageFrame>
        {
            new(CreateTiledImageRgba(16, 16, colorCount: 4, colorOffset: 0), TimeSpan.FromMilliseconds(40), FrameDisposalMethod.DoNotDispose),
            new(CreateTiledImageRgba(16, 16, colorCount: 4, colorOffset: 2), TimeSpan.FromMilliseconds(80), FrameDisposalMethod.DoNotDispose),
        };
        var source = new AnimatedImage(frames, width: 16, height: 16, loopCount: 2);

        using var ms = new MemoryStream();
        source.Save(ms, "webp", new WebpEncoderOptions { Lossless = false });
        ms.Position = 0;

        var decodedAnimation = AnimatedImage.Load(ms);
        var decoded = decodedAnimation.Frames.Select(frame => frame.Clone()).ToList();

        Assert.Equal(2, decoded.Count);
        Assert.Equal(2, decodedAnimation.LoopCount);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i].Duration, decoded[i].Duration);
        }
    }

    private static Image CreateTiledImageRgba(int width, int height, int colorCount, int colorOffset)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int tile = ((x / 3) + (y / 3) + colorOffset) % colorCount;
                (byte r, byte g, byte b) = PaletteColor(tile);
                row[(x * 4) + 0] = r;
                row[(x * 4) + 1] = g;
                row[(x * 4) + 2] = b;
                row[(x * 4) + 3] = 255;
            }
        }

        return image;
    }

    private static (byte R, byte G, byte B) PaletteColor(int index)
    {
        byte r = (byte)((index * 53) % 256);
        byte g = (byte)((index * 97) % 256);
        byte b = (byte)((index * 181) % 256);
        return (r, g, b);
    }

    private static void FillSolid(Image image, byte r, byte g, byte b)
    {
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
    }

    [Fact]
    public void AnimatedImageLoad_DispatchesWebpFilesToWebpCodec()
    {
        byte[] webp = BuildAnimatedWebp(4, 4, loopCount: 0, SolidFrame(4, 4, 5, 6, 7, durationMs: 15));

        using var ms = new MemoryStream(webp);
        var animated = AnimatedImage.Load(ms);
        var frames = animated.Frames.ToList();
        Assert.Equal(4, animated.Width);
        Assert.Single(frames);
    }

    private static byte[] SolidFrame(int width, int height, byte r, byte g, byte b, int durationMs)
    {
        var image = SolidImage(width, height, r, g, b, 255);
        return BuildAnmf(0, 0, width, height, durationMs, disposeToBackground: false, blend: false, EncodeFrameData(image));
    }

    private static Image SolidImage(int width, int height, byte r, byte g, byte b, byte a)
    {
        var image = Image.Create(width, height, PixelFormat.Rgba32);
        var pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return image;
    }

    /// <summary>Encodes <paramref name="image"/> as WebP and extracts just its VP8/VP8L (+ optional ALPH) chunk bytes — the shape of an ANMF chunk's Frame Data.</summary>
    private static byte[] EncodeFrameData(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, "webp");
        ms.Position = 0;
        var metadata = new ImageMetadata();
        var container = WebpContainerReader.Read(ms, metadata);

        using var frameData = new MemoryStream();
        if (container.AlphaData is { } alpha)
        {
            WriteChunk(frameData, "ALPH", alpha);
        }

        string fourCc = container.Format == WebpBitstreamFormat.Lossless ? "VP8L" : "VP8 ";
        WriteChunk(frameData, fourCc, container.BitstreamData);
        return frameData.ToArray();
    }

    private static byte[] BuildAnmf(int x, int y, int width, int height, int durationMs, bool disposeToBackground, bool blend, byte[] frameData)
    {
        byte[] header = new byte[16];
        WriteUInt24Le(header, 0, x / 2);
        WriteUInt24Le(header, 3, y / 2);
        WriteUInt24Le(header, 6, width - 1);
        WriteUInt24Le(header, 9, height - 1);
        WriteUInt24Le(header, 12, durationMs);

        byte flags = 0;
        if (disposeToBackground)
        {
            flags |= 0x01;
        }

        if (!blend)
        {
            flags |= 0x02;
        }

        header[15] = flags;

        return [.. header, .. frameData];
    }

    private static byte[] BuildAnimatedWebp(int canvasWidth, int canvasHeight, int loopCount, params byte[][] anmfChunks)
    {
        var chunks = new List<(string FourCc, byte[] Payload)>
        {
            ("VP8X", BuildVp8X(canvasWidth, canvasHeight)),
            ("ANIM", BuildAnim(loopCount)),
        };
        foreach (var anmf in anmfChunks)
        {
            chunks.Add(("ANMF", anmf));
        }

        return BuildRiff([.. chunks]);
    }

    private static byte[] BuildAnim(int loopCount)
    {
        byte[] data = new byte[6];
        data[4] = (byte)(loopCount & 0xFF);
        data[5] = (byte)((loopCount >> 8) & 0xFF);
        return data;
    }

    private static byte[] BuildVp8X(int width, int height)
    {
        byte[] data = new byte[10];
        data[0] = 0x02; // animation bit
        WriteUInt24Le(data, 4, width - 1);
        WriteUInt24Le(data, 7, height - 1);
        return data;
    }

    private static void WriteUInt24Le(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    private static byte[] BuildRiff(params (string FourCc, byte[] Payload)[] chunks)
    {
        using var body = new MemoryStream();
        foreach (var (fourCc, payload) in chunks)
        {
            WriteChunk(body, fourCc, payload);
        }

        using var riff = new MemoryStream();
        riff.Write("RIFF"u8);
        WriteUInt32Le(riff, (uint)(4 + body.Length));
        riff.Write("WEBP"u8);
        body.Position = 0;
        body.CopyTo(riff);
        return riff.ToArray();
    }

    private static void WriteChunk(Stream stream, string fourCc, byte[] payload)
    {
        stream.Write(Encoding.ASCII.GetBytes(fourCc));
        WriteUInt32Le(stream, (uint)payload.Length);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteUInt32Le(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 24) & 0xFF));
    }
}
