using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.Unit;

public class JpegPreviewAndOrientationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Identify_ReportsHasPreview_OnlyForProgressive(bool progressive, bool expected)
    {
        var jpeg = EncodeGradient(64, 48, progressive);

        var info = JpegDecoder.Identify(new MemoryStream(jpeg));

        Assert.Equal(expected, info.HasPreview);
        Assert.Equal(ImageOrientation.Normal, info.Orientation);
    }

    [Theory]
    [InlineData(JpegChromaSubsampling.Yuv444)]
    [InlineData(JpegChromaSubsampling.Yuv420)]
    public void ProgressiveDecode_DeliversPreviews_AndFinalImageIsUnaffected(JpegChromaSubsampling subsampling)
    {
        var jpeg = EncodeGradient(67, 45, progressive: true, subsampling);
        using var expected = JpegDecoder.Decode(new MemoryStream(jpeg));

        var previews = new List<Image>();
        using var actual = JpegDecoder.Decode(new MemoryStream(jpeg), new DecoderOptions { PreviewAvailable = previews.Add });

        try
        {
            Assert.NotEmpty(previews);
            foreach (var preview in previews)
            {
                Assert.Equal(expected.Width, preview.Width);
                Assert.Equal(expected.Height, preview.Height);
                Assert.Equal(expected.PixelFormat, preview.PixelFormat);
            }

            Assert.Equal(expected.GetPixelSpan().ToArray(), actual.GetPixelSpan().ToArray());

            // Later previews carry more detail, so the last one is at least as close to the final image as the first.
            Assert.True(Distance(previews[^1], expected) <= Distance(previews[0], expected));
        }
        finally
        {
            previews.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void ProgressiveDecode_HonorsTargetPixelFormat_ForPreviews()
    {
        var jpeg = EncodeGradient(32, 32, progressive: true);
        var previews = new List<Image>();

        using var final = JpegDecoder.Decode(new MemoryStream(jpeg), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32, PreviewAvailable = previews.Add });

        try
        {
            Assert.Equal(PixelFormat.Rgba32, final.PixelFormat);
            Assert.NotEmpty(previews);
            Assert.All(previews, p => Assert.Equal(PixelFormat.Rgba32, p.PixelFormat));
        }
        finally
        {
            previews.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void ProgressiveDecode_WorksThroughImageLoad()
    {
        var jpeg = EncodeGradient(32, 32, progressive: true);
        int count = 0;

        using var image = Image.Load(new MemoryStream(jpeg), new DecoderOptions { PreviewAvailable = p => { count++; p.Dispose(); } });

        Assert.True(count > 0);
        Assert.Equal(32, image.Width);
    }

    [Fact]
    public void BaselineDecode_NeverInvokesPreviewCallback()
    {
        var jpeg = EncodeGradient(32, 32, progressive: false);
        int count = 0;

        using var image = JpegDecoder.Decode(new MemoryStream(jpeg), new DecoderOptions { PreviewAvailable = p => { count++; p.Dispose(); } });

        Assert.Equal(0, count);
    }

    [Fact]
    public void ProgressiveDecode_CallbackException_Propagates()
    {
        var jpeg = EncodeGradient(32, 32, progressive: true);

        Assert.Throws<InvalidOperationException>(() =>
            JpegDecoder.Decode(new MemoryStream(jpeg), new DecoderOptions { PreviewAvailable = _ => throw new InvalidOperationException() }));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(true, 7)]
    [InlineData(true, 8)]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(false, 6)]
    [InlineData(false, 8)]
    public void Identify_ReadsExifOrientation(bool littleEndian, int value)
    {
        var jpeg = InsertApp1(EncodeGradient(16, 16, progressive: false), BuildExif(littleEndian, (ushort)value));

        var info = JpegDecoder.Identify(new MemoryStream(jpeg));

        Assert.Equal((ImageOrientation)value, info.Orientation);
    }

    [Fact]
    public void Identify_OrientationIsNormal_WhenNoExif()
    {
        var info = JpegDecoder.Identify(new MemoryStream(EncodeGradient(16, 16, progressive: false)));

        Assert.Equal(ImageOrientation.Normal, info.Orientation);
    }

    [Fact]
    public void Identify_OrientationIsNormal_ForOtherApp1Payload()
    {
        var xmp = "http://ns.adobe.com/xap/1.0/\0<x/>"u8.ToArray();
        var jpeg = InsertApp1(EncodeGradient(16, 16, progressive: false), xmp);

        Assert.Equal(ImageOrientation.Normal, JpegDecoder.Identify(new MemoryStream(jpeg)).Orientation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(0xFFFF)]
    public void Identify_OrientationIsNormal_ForOutOfRangeValue(int value)
    {
        var jpeg = InsertApp1(EncodeGradient(16, 16, progressive: false), BuildExif(true, (ushort)value));

        Assert.Equal(ImageOrientation.Normal, JpegDecoder.Identify(new MemoryStream(jpeg)).Orientation);
    }

    [Fact]
    public void Identify_OrientationIsNormal_ForGarbageExif()
    {
        byte[] garbage = [.. "Exif\0\0"u8.ToArray(), 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];
        var jpeg = InsertApp1(EncodeGradient(16, 16, progressive: true), garbage);

        var info = JpegDecoder.Identify(new MemoryStream(jpeg));

        Assert.Equal(ImageOrientation.Normal, info.Orientation);
        Assert.True(info.HasPreview);
    }

    [Fact]
    public void Identify_OrientationIsNormal_ForTruncatedExif()
    {
        var exif = BuildExif(true, 6)[..16];
        var jpeg = InsertApp1(EncodeGradient(16, 16, progressive: false), exif);

        Assert.Equal(ImageOrientation.Normal, JpegDecoder.Identify(new MemoryStream(jpeg)).Orientation);
    }

    private static byte[] EncodeGradient(int width, int height, bool progressive, JpegChromaSubsampling subsampling = JpegChromaSubsampling.Yuv420)
    {
        using var image = Image.Create(width, height, PixelFormat.Rgb24);
        var pixels = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 3;
                pixels[offset] = (byte)(x * 255 / Math.Max(width - 1, 1));
                pixels[offset + 1] = (byte)(y * 255 / Math.Max(height - 1, 1));
                pixels[offset + 2] = (byte)(((x * 7) ^ (y * 13)) & 0xFF);
            }
        }

        using var ms = new MemoryStream();
        JpegEncoder.Encode(image, ms, new JpegEncoderOptions { Quality = 90, Subsampling = subsampling, Progressive = progressive });
        return ms.ToArray();
    }

    /// <summary>Builds an APP1 payload ("Exif\0\0" + TIFF header + IFD0 holding only the orientation tag).</summary>
    private static byte[] BuildExif(bool littleEndian, ushort orientation)
    {
        var bytes = new List<byte>("Exif\0\0"u8.ToArray());

        void U16(int v)
        {
            if (littleEndian)
            {
                bytes.Add((byte)v);
                bytes.Add((byte)(v >> 8));
            }
            else
            {
                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)v);
            }
        }

        void U32(int v)
        {
            if (littleEndian)
            {
                U16(v & 0xFFFF);
                U16(v >>> 16);
            }
            else
            {
                U16(v >>> 16);
                U16(v & 0xFFFF);
            }
        }

        bytes.Add((byte)(littleEndian ? 'I' : 'M'));
        bytes.Add((byte)(littleEndian ? 'I' : 'M'));
        U16(42);
        U32(8);
        U16(1);
        U16(0x0112);
        U16(3);
        U32(1);
        U16(orientation);
        U16(0);
        U32(0);
        return [.. bytes];
    }

    private static byte[] InsertApp1(byte[] jpeg, byte[] payload)
    {
        int length = payload.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    private static long Distance(Image a, Image b)
    {
        var x = a.GetPixelSpan();
        var y = b.GetPixelSpan();
        long total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            total += Math.Abs(x[i] - y[i]);
        }

        return total;
    }
}
