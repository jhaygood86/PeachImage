using PeachImage.Formats.Jxl;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Tests.Formats.Jxl.Unit;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Files with an embedded preview frame. libjxl's encoder tools do not produce one here, so the file is assembled from two libjxl
/// encodes: the header of the main image gains a preview size, and the small image's frame is spliced in before the main frame.
/// </summary>
[Trait("Category", "Oracle")]
public class JxlPreviewTests
{
    [Fact]
    public void PreviewFrame_IsSkipped_AndTheMainImageDecodes()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] withPreview = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8);

        using var expected = Image.Load(main);
        using var actual = Image.Load(withPreview);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.PixelFormat, actual.PixelFormat);
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "The decoded pixels differ from the file without a preview.");

        // The preview size is reported by the header parser, so the splice really produced a preview.
        var headers = JxlCodestreamHeaders.Read(JxlContainer.Parse(withPreview).Codestream.ToArray());
        Assert.Equal(new JxlSize(16, 8), headers.Metadata.PreviewSize);

        // Identify steps over the preview to inspect the main frame.
        using var stream = new MemoryStream(withPreview);
        Assert.True(Image.Identify(stream).IsLosslessEncoding);
    }

    [Fact]
    public void PreviewFrame_DoesNotCountAsAnAnimationFrame()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] withPreview = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8);

        using var stream = new MemoryStream(withPreview);
        var animation = AnimatedImage.Load(stream);
        Assert.Single(animation.Frames);
        Assert.Equal(40, animation.Width);
    }

    [Fact]
    public void Identify_ReportsThePreview_AndItsAbsence()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] withPreview = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8);

        Assert.True(Image.Identify(new MemoryStream(withPreview)).HasPreview);
        Assert.False(Image.Identify(new MemoryStream(main)).HasPreview);
    }

    [Fact]
    public void Identify_ReportsTheOrientation_WithoutApplyingIt()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] rotated = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8, orientation: 6);

        var info = Image.Identify(new MemoryStream(rotated));
        Assert.Equal(ImageOrientation.Rotate90, info.Orientation);
        Assert.Equal(ImageOrientation.Normal, Image.Identify(new MemoryStream(main)).Orientation);

        // The size and the pixels stay as stored; the upright view is the caller's to compute.
        using var expected = Image.Load(main);
        using var actual = Image.Load(rotated);
        Assert.Equal(40, info.Width);
        Assert.Equal(32, info.Height);
        Assert.Equal(expected.Width, actual.Width);
        Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()));
    }

    [Fact]
    public void PreviewAvailable_ReceivesThePreview_BeforeTheMainImage()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] withPreview = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8);

        var previews = new List<Image>();
        try
        {
            using var actual = Image.Load(new MemoryStream(withPreview), new DecoderOptions { PreviewAvailable = previews.Add });
            using var expected = Image.Load(main);
            using var expectedPreview = Image.Load(thumbnail);

            var preview = Assert.Single(previews);
            Assert.Equal(16, preview.Width);
            Assert.Equal(8, preview.Height);
            Assert.Equal(actual.PixelFormat, preview.PixelFormat);
            Assert.True(expectedPreview.GetPixelSpan().SequenceEqual(preview.GetPixelSpan()), "The preview differs from the thumbnail it was built from.");
            Assert.True(expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan()), "The main image changed when a preview callback was set.");
        }
        finally
        {
            previews.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void PreviewAvailable_HonoursTheTargetPixelFormat()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] withPreview = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8);

        PixelFormat? seen = null;
        var options = new DecoderOptions
        {
            TargetPixelFormat = PixelFormat.Rgba32,
            PreviewAvailable = p =>
            {
                seen = p.PixelFormat;
                p.Dispose();
            },
        };
        using var actual = Image.Load(new MemoryStream(withPreview), options);

        Assert.Equal(PixelFormat.Rgba32, seen);
        Assert.Equal(PixelFormat.Rgba32, actual.PixelFormat);
    }

    [Fact]
    public void PreviewAvailable_ThrowingAbortsTheDecode()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");
        byte[] thumbnail = LibjxlOracle.Encode("testsrc2=size=16x8", "rgb24", "-distance 0");
        byte[] withPreview = AddPreview(main, thumbnail, previewWidth: 16, previewHeight: 8);

        var options = new DecoderOptions { PreviewAvailable = p => { p.Dispose(); throw new InvalidOperationException("stop"); } };
        Assert.Throws<InvalidOperationException>(() => Image.Load(new MemoryStream(withPreview), options));
    }

    [Fact]
    public void PreviewAvailable_IsNotCalled_WithoutAPreview()
    {
        Assert.SkipUnless(LibjxlOracle.IsAvailable, "ffmpeg with libjxl is not available.");

        byte[] main = LibjxlOracle.Encode("testsrc2=size=40x32", "rgb24", "-distance 0");

        int calls = 0;
        using var image = Image.Load(new MemoryStream(main), new DecoderOptions { PreviewAvailable = p => { calls++; p.Dispose(); } });

        Assert.Equal(0, calls);
    }

    private static byte[] AddPreview(byte[] mainFile, byte[] thumbnailFile, int previewWidth, int previewHeight, int orientation = 1)
    {
        byte[] main = JxlContainer.Parse(mainFile).Codestream.ToArray();
        byte[] thumbnail = JxlContainer.Parse(thumbnailFile).Codestream.ToArray();
        var mainHeaders = JxlCodestreamHeaders.Read(main);
        var thumbnailHeaders = JxlCodestreamHeaders.Read(thumbnail);

        // Bit positions in the main header: after the signature and size header the metadata starts.
        var reader = new JxlBitReader(main, 16);
        JxlSize.ReadSizeHeader(ref reader);
        long metadataStart = reader.BitPosition;
        JxlImageMetadata.Read(ref reader);
        long metadataEnd = reader.BitPosition;
        JxlCustomTransformData.Read(ref reader, false);
        long headerEnd = reader.BitPosition;

        var bits = new List<bool>();
        void CopyBits(byte[] source, long from, long to)
        {
            for (long i = from; i < to; i++)
            {
                bits.Add(((source[i >> 3] >> (int)(i & 7)) & 1) != 0);
            }
        }

        void Write(ulong value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        // Signature and size header, unchanged.
        CopyBits(main, 0, metadataStart);

        bool allDefault = ((main[metadataStart >> 3] >> (int)(metadataStart & 7)) & 1) != 0;
        Assert.False(allDefault, "The main image's metadata is expected to be explicit.");
        bool extraFields = ((main[(metadataStart + 1) >> 3] >> (int)((metadataStart + 1) & 7)) & 1) != 0;
        Assert.False(extraFields, "The main image is expected to have no extra fields yet.");

        Write(0, 1); // all_default = false
        Write(1, 1); // extra_fields = true
        Write((ulong)(orientation - 1), 3); // orientation - 1
        Write(0, 1); // have_intrinsic_size
        Write(1, 1); // have_preview
        WritePreviewHeader(Write, previewWidth, previewHeight);
        Write(0, 1); // have_animation

        // The rest of the metadata, up to its trailing (empty) extensions field, then the tone mapping that extra_fields brings in.
        CopyBits(main, metadataStart + 2, metadataEnd - 2);
        Write(1, 1); // tone mapping: all_default
        CopyBits(main, metadataEnd - 2, headerEnd);

        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }

        var result = new List<byte>();
        for (int i = 0; i < bits.Count; i += 8)
        {
            int value = 0;
            for (int b = 0; b < 8; b++)
            {
                if (bits[i + b])
                {
                    value |= 1 << b;
                }
            }

            result.Add((byte)value);
        }

        result.AddRange(thumbnail.AsSpan(thumbnailHeaders.FrameOffset).ToArray());
        result.AddRange(main.AsSpan(mainHeaders.FrameOffset).ToArray());
        return [.. result];
    }

    private static void WritePreviewHeader(Action<ulong, int> write, int width, int height)
    {
        Assert.True(width % 8 == 0 && height % 8 == 0 && width / 8 is >= 1 and <= 32 && height / 8 is >= 1 and <= 32);
        write(1, 1); // div8
        WriteDiv8(write, height / 8);
        write(0, 3); // explicit width
        WriteDiv8(write, width / 8);
    }

    // U32(Val(16), Val(32), BitsOffset(5, 1), BitsOffset(9, 33)) for a value of 1..32.
    private static void WriteDiv8(Action<ulong, int> write, int value)
    {
        write(2, 2);
        write((ulong)(value - 1), 5);
    }
}
