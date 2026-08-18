using System.Text;
using PeachImage.Formats.Webp;
using PeachImage.Formats.Webp.Decoding;

namespace PeachImage.Tests.Formats.Webp.Unit.Decoding;

public class WebpAnimationReaderTests
{
    [Fact]
    public void ReadHeader_ParsesLoopCountAndCanvasSize()
    {
        byte[] riff = BuildAnimatedRiff(
            canvasWidth: 8,
            canvasHeight: 8,
            loopCount: 5,
            BuildOpaqueFrame(0, 0, 8, 8, disposeToBackground: false, blend: false));

        var header = ReadHeader(riff, out _);

        Assert.Equal(8, header.CanvasWidth);
        Assert.Equal(8, header.CanvasHeight);
        Assert.Equal(5, header.LoopCount);
    }

    [Fact]
    public void ReadFrames_SingleFullCanvasFrame_MatchesSourcePixels()
    {
        using var sourceImage = SolidImage(6, 6, 200, 40, 40, 255);
        byte[] frameData = EncodeFrameData(sourceImage);
        byte[] riff = BuildAnimatedRiff(6, 6, loopCount: 0, BuildAnmf(0, 0, 6, 6, 50, disposeToBackground: false, blend: false, frameData));

        var frames = ReadFrames(riff);

        var frame = Assert.Single(frames);
        Assert.Equal(TimeSpan.FromMilliseconds(50), frame.Duration);
        Assert.Equal(FrameDisposalMethod.DoNotDispose, frame.Disposal);
        Assert.True(sourceImage.GetPixelSpan().SequenceEqual(frame.Image.GetPixelSpan()));
        frame.Image.Dispose();
    }

    [Fact]
    public void ReadFrames_DisposeToBackground_MapsToRestoreToBackground()
    {
        byte[] riff = BuildAnimatedRiff(4, 4, loopCount: 0, BuildOpaqueFrame(0, 0, 4, 4, disposeToBackground: true, blend: false));

        var frames = ReadFrames(riff);

        var frame = Assert.Single(frames);
        Assert.Equal(FrameDisposalMethod.RestoreToBackground, frame.Disposal);
        frame.Image.Dispose();
    }

    [Fact]
    public void ReadFrames_BlendFalse_OverwritesEvenWithTransparentSource()
    {
        // Frame 1: fully opaque red across the whole canvas, left in place. Frame 2: a fully transparent
        // (alpha=0) sub-rect with blend=false (overwrite) — the overwrite must punch the region to (0,0,0,0)
        // even though the source pixels are "transparent", proving blend=false ignores alpha entirely.
        using var opaque = SolidImage(4, 4, 255, 0, 0, 255);
        using var transparent = SolidImage(2, 2, 0, 0, 0, 0);

        // ANMF X/Y offsets are stored in 2-pixel units, so the sub-rect must sit on an even coordinate —
        // bottom-right 2x2 quadrant of the 4x4 canvas.
        byte[] riff = BuildAnimatedRiff(
            4, 4, loopCount: 0,
            BuildAnmf(0, 0, 4, 4, 10, disposeToBackground: false, blend: false, EncodeFrameData(opaque)),
            BuildAnmf(2, 2, 2, 2, 10, disposeToBackground: false, blend: false, EncodeFrameData(transparent)));

        var frames = ReadFrames(riff);

        Assert.Equal(2, frames.Count);
        var second = frames[1].Image;
        Assert.Equal((byte)0, second.GetRowSpan(2)[(2 * 4) + 3]);
        Assert.Equal((byte)0, second.GetRowSpan(2)[(2 * 4) + 0]);
        // Outside the overwritten rect, frame 1's opaque red is untouched.
        Assert.Equal((byte)255, second.GetRowSpan(0)[(0 * 4) + 0]);
        Assert.Equal((byte)255, second.GetRowSpan(0)[(0 * 4) + 3]);

        foreach (var frame in frames)
        {
            frame.Image.Dispose();
        }
    }

    [Fact]
    public void ReadFrames_BlendTrue_TransparentSourceLeavesPreviousPixelsUnchanged()
    {
        // Same setup as the overwrite test above, but blend=true this time: a fully transparent source
        // contributes nothing, so frame 1's opaque red must still show through everywhere in frame 2.
        using var opaque = SolidImage(4, 4, 255, 0, 0, 255);
        using var transparent = SolidImage(2, 2, 0, 0, 0, 0);

        byte[] riff = BuildAnimatedRiff(
            4, 4, loopCount: 0,
            BuildAnmf(0, 0, 4, 4, 10, disposeToBackground: false, blend: false, EncodeFrameData(opaque)),
            BuildAnmf(2, 2, 2, 2, 10, disposeToBackground: false, blend: true, EncodeFrameData(transparent)));

        var frames = ReadFrames(riff);

        var second = frames[1].Image;
        for (int y = 0; y < 4; y++)
        {
            var row = second.GetRowSpan(y);
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal((byte)255, row[(x * 4) + 0]);
                Assert.Equal((byte)255, row[(x * 4) + 3]);
            }
        }

        foreach (var frame in frames)
        {
            frame.Image.Dispose();
        }
    }

    [Fact]
    public void ReadFrames_DisposalAppliedBeforeNextFrame_NotImmediately()
    {
        // Frame 1: opaque red in the top-left 2x2, dispose-to-background. Frame 2: opaque blue in a
        // non-overlapping 2x2 elsewhere, no disposal. Frame 1's own composited image must still show red
        // (disposal hasn't happened yet); frame 2's composited image must show frame 1's region cleared to
        // transparent (disposal applied right before frame 2 was drawn) plus the new blue region.
        using var red = SolidImage(2, 2, 255, 0, 0, 255);
        using var blue = SolidImage(2, 2, 0, 0, 255, 255);

        byte[] riff = BuildAnimatedRiff(
            4, 4, loopCount: 0,
            BuildAnmf(0, 0, 2, 2, 10, disposeToBackground: true, blend: false, EncodeFrameData(red)),
            BuildAnmf(2, 2, 2, 2, 10, disposeToBackground: false, blend: false, EncodeFrameData(blue)));

        var frames = ReadFrames(riff);

        var first = frames[0].Image;
        Assert.Equal((byte)255, first.GetRowSpan(0)[0]);

        var second = frames[1].Image;
        Assert.Equal((byte)0, second.GetRowSpan(0)[3]); // frame 1's region now cleared to transparent
        Assert.Equal((byte)255, second.GetRowSpan(2)[(2 * 4) + 2]); // frame 2's own blue region

        foreach (var frame in frames)
        {
            frame.Image.Dispose();
        }
    }

    [Fact]
    public void ReadFrames_TakeFirstFrame_DoesNotTouchLaterFrameBytes()
    {
        using var frame1 = SolidImage(4, 4, 10, 20, 30, 255);
        using var frame2 = SolidImage(4, 4, 40, 50, 60, 255);
        byte[] riff = BuildAnimatedRiff(
            4, 4, loopCount: 0,
            BuildAnmf(0, 0, 4, 4, 10, disposeToBackground: false, blend: false, EncodeFrameData(frame1)),
            BuildAnmf(0, 0, 4, 4, 10, disposeToBackground: false, blend: false, EncodeFrameData(frame2)));

        var header = ReadHeader(riff, out int consumedThroughAnim);
        using var stream = new PoisonedTailStream(riff, consumedThroughAnim + FirstAnmfByteLength(riff, consumedThroughAnim));
        stream.Position = consumedThroughAnim; // WebpAnimationReader.ReadFrames expects to start right after ANIM, same as ReadHeader left the real stream positioned
        var metadata = new ImageMetadata();

        var lazyFrames = WebpAnimationReader.ReadFrames(stream, metadata, header).Take(1).ToList();

        var only = Assert.Single(lazyFrames);
        Assert.True(frame1.GetPixelSpan().SequenceEqual(only.Image.GetPixelSpan()));
        only.Image.Dispose();
    }

    [Fact]
    public void AnimHeader_TooShort_Throws()
    {
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(width: 4, height: 4)),
            ("ANIM", [0, 0, 0, 0, 0]));

        Assert.Throws<WebpDecodingException>(() => ReadHeader(riff, out _));
    }

    [Fact]
    public void AnimChunk_Missing_Throws()
    {
        byte[] riff = BuildRiff(("VP8X", BuildVp8X(width: 4, height: 4)));

        Assert.Throws<WebpDecodingException>(() => ReadHeader(riff, out _));
    }

    [Fact]
    public void AnmfBeforeAnim_Throws()
    {
        byte[] riff = BuildRiff(
            ("VP8X", BuildVp8X(width: 4, height: 4)),
            ("ANMF", BuildOpaqueFrame(0, 0, 4, 4, disposeToBackground: false, blend: false)));

        Assert.Throws<WebpDecodingException>(() => ReadHeader(riff, out _));
    }

    [Fact]
    public void AnmfHeader_TooShort_Throws()
    {
        byte[] riff = BuildAnimatedRiff(4, 4, loopCount: 0);
        riff = AppendChunk(riff, "ANMF", new byte[10]);

        Assert.Throws<WebpDecodingException>(() => ReadFrames(riff));
    }

    [Fact]
    public void AnmfFrameRect_ExceedsCanvas_Throws()
    {
        using var image = SolidImage(4, 4, 1, 2, 3, 255);
        byte[] riff = BuildAnimatedRiff(4, 4, loopCount: 0, BuildAnmf(2, 2, 4, 4, 10, false, false, EncodeFrameData(image)));

        Assert.Throws<WebpDecodingException>(() => ReadFrames(riff));
    }

    [Fact]
    public void AnmfFrameData_NoImageChunk_Throws()
    {
        byte[] riff = BuildAnimatedRiff(4, 4, loopCount: 0, BuildAnmf(0, 0, 4, 4, 10, false, false, []));

        Assert.Throws<WebpDecodingException>(() => ReadFrames(riff));
    }

    [Fact]
    public void AnmfFrameData_TwoImageChunks_Throws()
    {
        using var image = SolidImage(4, 4, 1, 2, 3, 255);
        byte[] singleFrameData = EncodeFrameData(image);
        byte[] doubled = [.. singleFrameData, .. singleFrameData];
        byte[] riff = BuildAnimatedRiff(4, 4, loopCount: 0, BuildAnmf(0, 0, 4, 4, 10, false, false, doubled));

        Assert.Throws<WebpDecodingException>(() => ReadFrames(riff));
    }

    private static WebpAnimationHeader ReadHeader(byte[] riff, out int bytesConsumed)
    {
        using var stream = new MemoryStream(riff);
        var prelude = WebpContainerReader.ReadPrelude(stream, out _);
        var metadata = new ImageMetadata();
        var header = WebpAnimationReader.ReadHeader(stream, metadata, prelude);
        bytesConsumed = (int)stream.Position;
        return header;
    }

    private static List<AnimatedImageFrame> ReadFrames(byte[] riff)
    {
        using var stream = new MemoryStream(riff);
        var prelude = WebpContainerReader.ReadPrelude(stream, out _);
        var metadata = new ImageMetadata();
        var header = WebpAnimationReader.ReadHeader(stream, metadata, prelude);
        return WebpAnimationReader.ReadFrames(stream, metadata, header).ToList();
    }

    private static int FirstAnmfByteLength(byte[] riff, int offsetAfterAnim)
    {
        uint size = BitConverter.ToUInt32(riff, offsetAfterAnim + 4);
        return 8 + (int)size + ((int)size & 1);
    }

    /// <summary>A read-only stream over <paramref name="bytes"/> that throws if any byte at or past <paramref name="poisonOffset"/> is read — used to prove <see cref="WebpAnimationReader.ReadFrames"/> doesn't touch a later frame's bytes when the caller only pulls an earlier one.</summary>
    private sealed class PoisonedTailStream(byte[] bytes, int poisonOffset) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position + count > poisonOffset)
            {
                throw new InvalidOperationException("Read past the poisoned offset — a later frame's bytes were touched.");
            }

            return base.Read(buffer, offset, count);
        }

        public override int ReadByte()
        {
            if (Position + 1 > poisonOffset)
            {
                throw new InvalidOperationException("Read past the poisoned offset — a later frame's bytes were touched.");
            }

            return base.ReadByte();
        }
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

    private static byte[] BuildOpaqueFrame(int x, int y, int width, int height, bool disposeToBackground, bool blend)
    {
        using var image = SolidImage(width, height, 128, 64, 32, 255);
        return BuildAnmf(x, y, width, height, 20, disposeToBackground, blend, EncodeFrameData(image));
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

    private static byte[] BuildAnimatedRiff(int canvasWidth, int canvasHeight, int loopCount, params byte[][] anmfChunks)
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

    private static byte[] AppendChunk(byte[] riff, string fourCc, byte[] payload)
    {
        using var ms = new MemoryStream();
        ms.Write(riff);
        WriteChunk(ms, fourCc, payload);

        byte[] result = ms.ToArray();
        // Patch the RIFF-level size field (bytes 4..8) to reflect the appended chunk.
        uint newSize = (uint)(result.Length - 8);
        result[4] = (byte)(newSize & 0xFF);
        result[5] = (byte)((newSize >> 8) & 0xFF);
        result[6] = (byte)((newSize >> 16) & 0xFF);
        result[7] = (byte)((newSize >> 24) & 0xFF);
        return result;
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
