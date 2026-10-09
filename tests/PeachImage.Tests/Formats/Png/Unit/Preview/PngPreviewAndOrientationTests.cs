using PeachImage.Formats.Png;

namespace PeachImage.Tests.Formats.Png.Unit.Preview;

public class PngPreviewAndOrientationTests
{
    private const int IhdrEnd = 8 + 4 + 4 + 13 + 4;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Identify_HasPreview_MatchesInterlacing(bool interlace)
    {
        byte[] png = Encode(CreateGradient(21, 17), interlace);

        var info = PngDecoder.Identify(new MemoryStream(png));

        Assert.Equal(interlace, info.HasPreview);
        Assert.Equal(ImageOrientation.Normal, info.Orientation);
    }

    [Fact]
    public void Decode_Interlaced_InvokesCallbackOncePerPassExceptLast_WithFinalFormatAndSize()
    {
        byte[] png = Encode(CreateGradient(37, 29), interlace: true);
        var previews = new List<Image>();

        var final = PngDecoder.Decode(new MemoryStream(png), new DecoderOptions { PreviewAvailable = previews.Add });

        Assert.Equal(6, previews.Count);
        foreach (var preview in previews)
        {
            Assert.Equal(final.PixelFormat, preview.PixelFormat);
            Assert.Equal(final.Width, preview.Width);
            Assert.Equal(final.Height, preview.Height);
        }

        double previous = double.MaxValue;
        foreach (var preview in previews)
        {
            double error = MeanAbsoluteError(preview, final);
            Assert.True(error <= previous, "Previews must not get further from the final image.");
            previous = error;
        }

        Assert.True(MeanAbsoluteError(previews[^1], final) < MeanAbsoluteError(previews[0], final));

        // No black holes: the first preview replicates pixel (0,0) over the whole first lattice cell.
        Assert.Equal(final.GetRowSpan(0).Slice(0, 3).ToArray(), previews[0].GetRowSpan(5).Slice(7 * 3, 3).ToArray());

        var withoutCallback = PngDecoder.Decode(new MemoryStream(png));
        Assert.True(withoutCallback.GetPixelSpan().SequenceEqual(final.GetPixelSpan()));
    }

    [Fact]
    public void Decode_Interlaced_HonorsTargetPixelFormat()
    {
        byte[] png = Encode(CreateGradient(16, 16), interlace: true);
        var previews = new List<Image>();

        var final = PngDecoder.Decode(new MemoryStream(png), new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32, PreviewAvailable = previews.Add });

        Assert.Equal(PixelFormat.Rgba32, final.PixelFormat);
        Assert.Equal(6, previews.Count);
        Assert.All(previews, p => Assert.Equal(PixelFormat.Rgba32, p.PixelFormat));
    }

    [Fact]
    public void Decode_NonInterlaced_NeverInvokesCallback()
    {
        byte[] png = Encode(CreateGradient(16, 16), interlace: false);
        int calls = 0;

        PngDecoder.Decode(new MemoryStream(png), new DecoderOptions { PreviewAvailable = p => { calls++; p.Dispose(); } });

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Decode_OnePixelInterlaced_InvokesNoCallback()
    {
        byte[] png = Encode(CreateGradient(1, 1), interlace: true);
        int calls = 0;

        PngDecoder.Decode(new MemoryStream(png), new DecoderOptions { PreviewAvailable = p => { calls++; p.Dispose(); } });

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(6, false)]
    [InlineData(8, false)]
    [InlineData(5, true)]
    public void Identify_ReadsOrientationFromExifChunk(int orientation, bool littleEndian)
    {
        byte[] png = InsertChunk(Encode(CreateGradient(8, 8), interlace: false), "eXIf", BuildTiff(orientation, littleEndian));

        var info = PngDecoder.Identify(new MemoryStream(png));

        Assert.Equal((ImageOrientation)orientation, info.Orientation);
    }

    [Fact]
    public void Identify_ExifChunkWithExifPrefix_IsAccepted()
    {
        byte[] data = [.. "Exif\0\0"u8.ToArray(), .. BuildTiff(6, littleEndian: false)];
        byte[] png = InsertChunk(Encode(CreateGradient(8, 8), interlace: false), "eXIf", data);

        Assert.Equal((ImageOrientation)6, PngDecoder.Identify(new MemoryStream(png)).Orientation);
    }

    [Fact]
    public void Identify_ExifChunkAfterIdat_IsNotConsulted()
    {
        byte[] png = Encode(CreateGradient(8, 8), interlace: false);
        byte[] withTrailing = InsertChunk(png, "eXIf", BuildTiff(6, true), png.Length - 12);

        Assert.Equal(ImageOrientation.Normal, PngDecoder.Identify(new MemoryStream(withTrailing)).Orientation);
    }

    private static byte[] BuildTiff(int orientation, bool littleEndian)
    {
        var ms = new MemoryStream();

        void U16(int v)
        {
            if (littleEndian)
            {
                ms.WriteByte((byte)v);
                ms.WriteByte((byte)(v >> 8));
            }
            else
            {
                ms.WriteByte((byte)(v >> 8));
                ms.WriteByte((byte)v);
            }
        }

        void U32(int v)
        {
            if (littleEndian)
            {
                U16(v & 0xFFFF);
                U16(v >> 16);
            }
            else
            {
                U16(v >> 16);
                U16(v & 0xFFFF);
            }
        }

        ms.Write(littleEndian ? "II"u8 : "MM"u8);
        U16(42);
        U32(8);
        U16(1);
        U16(0x0112);
        U16(3);
        U32(1);
        U16(orientation);
        U16(0);
        U32(0);
        return ms.ToArray();
    }

    /// <summary>Inserts a chunk at <paramref name="offset"/> (default: right after IHDR).</summary>
    private static byte[] InsertChunk(byte[] png, string type, byte[] data, int offset = IhdrEnd)
    {
        using var chunk = new MemoryStream();
        PngTestFileBuilder.WriteChunk(chunk, type, data);
        var result = new List<byte>(png);
        result.InsertRange(offset, chunk.ToArray());
        return [.. result];
    }

    private static byte[] Encode(Image image, bool interlace)
    {
        using var ms = new MemoryStream();
        PngEncoder.Encode(image, ms, new PngEncoderOptions { Interlace = interlace });
        return ms.ToArray();
    }

    private static Image CreateGradient(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        var span = image.GetPixelSpan();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 3;
                span[i] = (byte)(x * 255 / Math.Max(1, width - 1));
                span[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                span[i + 2] = (byte)((x * 7) + (y * 13));
            }
        }

        return image;
    }

    private static double MeanAbsoluteError(Image a, Image b)
    {
        var x = a.GetPixelSpan();
        var y = b.GetPixelSpan();
        long sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += Math.Abs(x[i] - y[i]);
        }

        return (double)sum / x.Length;
    }
}
