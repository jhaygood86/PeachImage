using System.Runtime.InteropServices;
using PeachImage.Formats.Png;

namespace PeachImage.Tests.Color;

public class BitmapPipelineTests
{
    private static byte[] Png(Image image, EncoderOptions? options = null)
    {
        using var ms = new MemoryStream();
        image.Save(ms, "png", options);
        return ms.ToArray();
    }

    private static Image Pattern(PixelFormat format, int width, int height)
    {
        var image = Image.Create(width, height, format);
        int bpp = format.GetBytesPerPixel();
        var span = image.GetPixelSpan();
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (byte)((i / bpp * 37) + (i % bpp * 11));
        }

        if (format == PixelFormat.Rgba32)
        {
            for (int p = 0; p < width * height; p++)
            {
                span[(p * 4) + 3] = (byte)(p % 2 == 0 ? 255 : 128);
            }
        }

        return image;
    }

    [Fact]
    public void FromPixels_RemovesStridePadding_AndCopyPixelsToAddsIt()
    {
        var source = new byte[(2 * 16) + 12];
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                source[(y * 16) + x] = (byte)((y * 12) + x);
            }
        }

        using var image = Image.FromPixels(source, 4, 3, PixelFormat.Rgb24, strideBytes: 16);
        Assert.Equal(Enumerable.Range(0, 36).Select(i => (byte)i).ToArray(), image.GetPixelSpan().ToArray());

        var back = new byte[source.Length];
        image.CopyPixelsTo(back, strideBytes: 16);
        Assert.Equal(source[..12], back[..12]);
        Assert.Equal(source[16..28], back[16..28]);
        Assert.Equal(source[32..44], back[32..44]);

        Assert.Throws<ArgumentException>(() => Image.FromPixels(source.AsSpan(0, 20), 4, 3, PixelFormat.Rgb24, 16));
        Assert.Throws<ArgumentException>(() => Image.FromPixels(source, 4, 3, PixelFormat.Rgb24, 8));
    }

    [Fact]
    public void Wrap_SharesTheArray_AndDisposeLeavesItAlone()
    {
        var array = new byte[2 * 2 * 4];
        var image = Image.Wrap(array, 2, 2, PixelFormat.Rgba32);
        image.GetPixelSpan()[5] = 99;
        Assert.Equal(99, array[5]);
        image.Dispose();
        array[5] = 1; // still ours
        Assert.Throws<ArgumentException>(() => Image.Wrap(new byte[3], 2, 2, PixelFormat.Rgba32));
    }

    [Fact]
    public void Crop_ReturnsTheRegion()
    {
        using var source = Pattern(PixelFormat.Rgb24, 6, 5);
        using var cropped = source.Crop(new ImageRegion(2, 1, 3, 2));
        Assert.Equal(3, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(source.GetRowSpan(1).Slice(2 * 3, 3).ToArray(), cropped.GetRowSpan(0)[..3].ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Crop(new ImageRegion(4, 0, 3, 1)));
        Assert.Throws<ArgumentException>(() => source.Crop(new ImageRegion(0, 0, 0, 1)));
    }

    [Fact]
    public void EncodeFromPremultipliedBuffer_MatchesEncodingTheUnpremultipliedImage()
    {
        // straight (200, 100, 50) at alpha 128 -> premultiplied (100, 50, 25)
        var buffer = new byte[4 * 4];
        for (int i = 0; i < 4; i++)
        {
            buffer[(i * 4) + 0] = 100;
            buffer[(i * 4) + 1] = 50;
            buffer[(i * 4) + 2] = 25;
            buffer[(i * 4) + 3] = 128;
        }

        using var fromSpan = new MemoryStream();
        ImageEncoder.Encode(buffer, 4, 1, PixelFormat.PremultipliedRgba32, fromSpan, "png");
        using var fromMemory = new MemoryStream();
        ImageEncoder.Encode(new ReadOnlyMemory<byte>(buffer), 4, 1, PixelFormat.PremultipliedRgba32, fromMemory, "png");
        Assert.Equal(fromSpan.ToArray(), fromMemory.ToArray());

        using var decoded = Image.Load(fromSpan.ToArray());
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Assert.InRange(decoded.GetPixelSpan()[0], 198, 200);
        Assert.InRange(decoded.GetPixelSpan()[1], 99, 100);
        Assert.Equal(128, decoded.GetPixelSpan()[3]);
    }

    [Fact]
    public void EncodeFromStridedBuffer_AndDeclaredColorSpace()
    {
        var buffer = new byte[40 + 32];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan((y * 40) + (x * 8), 8))[0] = 40000;
                MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan((y * 40) + (x * 8), 8))[3] = 65535;
            }
        }

        using var ms = new MemoryStream();
        ImageEncoder.Encode(buffer, 4, 2, PixelFormat.Rgba64, ms, "png", strideBytes: 40, colorSpace: NamedColorSpace.DisplayP3);
        using var back = Image.Load(ms.ToArray());
        Assert.Equal(PixelFormat.Rgba64, back.PixelFormat);
        Assert.Equal(NamedColorSpace.DisplayP3, back.Metadata.GetNamedColorSpace());
        Assert.Equal(40000, MemoryMarshal.Cast<byte, ushort>(back.GetPixelSpan())[0]);
    }

    [Fact]
    public void LoadBitmap_DefaultsPremultiplyAndKeepsStraightWhenAsked()
    {
        using var source = Pattern(PixelFormat.Rgba32, 8, 4);
        var bytes = Png(source);

        using var premultiplied = Image.LoadBitmap(bytes);
        Assert.Equal(PixelFormat.PremultipliedRgba32, premultiplied.PixelFormat);

        using var straight = Image.LoadBitmap(bytes, new BitmapRequest { PremultiplyAlpha = BitmapPremultiply.None });
        Assert.Equal(PixelFormat.Rgba32, straight.PixelFormat);
        Assert.Equal(source.GetPixelSpan().ToArray(), straight.GetPixelSpan().ToArray());

        using var opaque = Image.LoadBitmap(Png(Pattern(PixelFormat.Rgb24, 4, 4)));
        Assert.Equal(PixelFormat.Rgb24, opaque.PixelFormat);
    }

    [Fact]
    public void LoadBitmap_FlipsVertically()
    {
        using var source = Pattern(PixelFormat.Rgb24, 4, 3);
        using var flipped = Image.LoadBitmap(Png(source), new BitmapRequest { Orientation = BitmapOrientation.FlipY });
        for (int y = 0; y < 3; y++)
        {
            Assert.Equal(source.GetRowSpan(2 - y).ToArray(), flipped.GetRowSpan(y).ToArray());
        }
    }

    [Fact]
    public void LoadBitmap_CropsThenResizes_WithTheRequestedQuality()
    {
        using var source = Pattern(PixelFormat.Rgb24, 20, 10);
        var bytes = Png(source);
        using var result = Image.LoadBitmap(bytes, new BitmapRequest { Crop = new ImageRegion(4, 2, 8, 4), ResizeWidth = 4, ResizeHeight = 2, ResizeQuality = ResizeQuality.Pixelated });
        Assert.Equal((4, 2), (result.Width, result.Height));

        using var onlyWidth = Image.LoadBitmap(bytes, new BitmapRequest { ResizeWidth = 10 });
        Assert.Equal((10, 5), (onlyWidth.Width, onlyWidth.Height));
        using var onlyHeight = Image.LoadBitmap(bytes, new BitmapRequest { ResizeHeight = 5 });
        Assert.Equal((10, 5), (onlyHeight.Width, onlyHeight.Height));

        Assert.Throws<ArgumentOutOfRangeException>(() => Image.LoadBitmap(bytes, new BitmapRequest { Crop = new ImageRegion(15, 0, 10, 2) }));
    }

    [Fact]
    public void LoadBitmap_ConvertsFromTheEmbeddedProfileToTheTarget()
    {
        using var p3 = Image.Create(1, 1, PixelFormat.Rgb48);
        MemoryMarshal.Cast<byte, ushort>(p3.GetPixelSpan())[0] = 0;
        MemoryMarshal.Cast<byte, ushort>(p3.GetPixelSpan())[1] = 65535;
        MemoryMarshal.Cast<byte, ushort>(p3.GetPixelSpan())[2] = 0;
        p3.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });
        var bytes = Png(p3);

        using var toSrgb = Image.LoadBitmap(bytes);
        Assert.Equal(PixelFormat.Rgb48, toSrgb.PixelFormat);
        Assert.Equal(NamedColorSpace.Srgb, toSrgb.Metadata.GetNamedColorSpace());
        Assert.Equal(0, MemoryMarshal.Cast<byte, ushort>(toSrgb.GetPixelSpan())[0]); // out of gamut: clipped

        using var unchanged = Image.LoadBitmap(bytes, new BitmapRequest { ColorSpaceConversion = BitmapColorSpaceConversion.None });
        Assert.Equal(65535, MemoryMarshal.Cast<byte, ushort>(unchanged.GetPixelSpan())[1]);
        Assert.Equal(0, MemoryMarshal.Cast<byte, ushort>(unchanged.GetPixelSpan())[0]);

        using var kept = Image.LoadBitmap(bytes, new BitmapRequest { TargetColorSpace = NamedColorSpace.DisplayP3 });
        Assert.Equal(NamedColorSpace.DisplayP3, kept.Metadata.GetNamedColorSpace());
        Assert.Equal(MemoryMarshal.Cast<byte, ushort>(p3.GetPixelSpan()).ToArray(), MemoryMarshal.Cast<byte, ushort>(kept.GetPixelSpan()).ToArray());
    }

    [Fact]
    public void LoadBitmap_ResamplesSixteenBitDataWithoutAnEightBitCopy()
    {
        // A 16-bit ramp whose adjacent values differ by less than one 8-bit step survives a resize only if the resampling stays 16-bit.
        using var source = Image.Create(64, 2, PixelFormat.Gray16);
        var samples = MemoryMarshal.Cast<byte, ushort>(source.GetPixelSpan());
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)(20000 + ((i % 64) * 3));
        }

        using var result = Image.LoadBitmap(Png(source), new BitmapRequest { ResizeWidth = 16, ResizeHeight = 2, ColorSpaceConversion = BitmapColorSpaceConversion.None });
        Assert.Equal(PixelFormat.Gray16, result.PixelFormat);
        var resized = MemoryMarshal.Cast<byte, ushort>(result.GetPixelSpan());
        Assert.NotEqual(resized[0] / 257, resized[1] / 257 + 1000); // sanity
        Assert.Contains(resized.ToArray(), v => v % 257 != 0);
    }

    [Fact]
    public void LoadBitmap_OutputFormatOverrides()
    {
        using var source = Pattern(PixelFormat.Rgba32, 4, 4);
        using var half = Image.LoadBitmap(Png(source), new BitmapRequest { OutputFormat = PixelFormat.PremultipliedRgbaF16 });
        Assert.Equal(PixelFormat.PremultipliedRgbaF16, half.PixelFormat);
        Assert.Throws<ArgumentException>(() => Image.LoadBitmap(Png(source), new BitmapRequest { OutputFormat = PixelFormat.PremultipliedRgba32, PremultiplyAlpha = BitmapPremultiply.None }));
    }

    [Fact]
    public void LoadBitmap_HonorsLimits_AndLoadsFromSpans()
    {
        var bytes = Png(Pattern(PixelFormat.Rgb24, 8, 8));
        Assert.ThrowsAny<Exception>(() => Image.LoadBitmap(bytes, new BitmapRequest { MaxPixels = 10 }));
        using var fromSpan = Image.LoadBitmap(bytes.AsSpan());
        Assert.Equal(8, fromSpan.Width);
        using var fromStream = Image.LoadBitmap(new MemoryStream(bytes));
        Assert.Equal(fromSpan.GetPixelSpan().ToArray(), fromStream.GetPixelSpan().ToArray());
    }
}

public class ZeroCopyEncodeTests
{
    private static IImageCodec Png => Image.FindCodecByFormatName("png")!;

    [Fact]
    public void TightWholeArray_InAFormatTheEncoderTakes_IsWrappedNotCopied()
    {
        var array = new byte[16 * 8 * 4];
        using var prepared = PeachImage.Internal.Color.EncodePreparation.PrepareBuffer(array, default, useMemory: true, 16, 8, PixelFormat.Rgba32, 0, new ImageMetadata(), Png, null);
        Assert.Equal(PixelFormat.Rgba32, prepared.Image.PixelFormat);
        array[5] = 99;
        Assert.Equal(99, prepared.Image.GetPixelSpan()[5]);
    }

    [Fact]
    public void StridedPremultipliedBuffer_IsConvertedStraightIntoTheCarrier()
    {
        const int width = 5, height = 3, stride = 48;
        var buffer = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buffer.AsSpan((y * stride) + (x * 8), 8));
                pixel[0] = 20000;
                pixel[1] = 10000;
                pixel[2] = 5000;
                pixel[3] = 40000; // premultiplied color under alpha 40000
            }
        }

        using var prepared = PeachImage.Internal.Color.EncodePreparation.PrepareBuffer(buffer, default, useMemory: true, width, height, PixelFormat.PremultipliedRgba64, stride, new ImageMetadata(), Png, null);
        Assert.Equal(PixelFormat.Rgba64, prepared.Image.PixelFormat);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(prepared.Image.GetPixelSpan());
        Assert.InRange(samples[0], 32760, 32776); // 20000 / (40000 / 65535)
        Assert.Equal(40000, samples[3]);
        Assert.Equal(width * height * 8, prepared.Image.GetPixelSpan().Length);
    }

    [Fact]
    public void SpanAndMemoryOverloads_WriteIdenticalFiles_ForEveryPath()
    {
        const int width = 37, height = 21;
        foreach (var (format, bytes, stride) in new[] { (PixelFormat.Rgba32, 4, 0), (PixelFormat.PremultipliedRgbaF16, 8, 0), (PixelFormat.Rgb48, 6, 6 * width + 10) })
        {
            int rowBytes = width * bytes;
            int actualStride = stride == 0 ? rowBytes : stride;
            var buffer = new byte[(actualStride * (height - 1)) + rowBytes];
            new Random(5).NextBytes(buffer);
            if (format == PixelFormat.PremultipliedRgbaF16)
            {
                var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(buffer.AsSpan());
                for (int i = 0; i < halves.Length; i++)
                {
                    halves[i] = (Half)(((i * 37) % 100) / 100f);
                }
            }

            using var fromMemory = new MemoryStream();
            ImageEncoder.Encode(buffer.AsMemory(), width, height, format, fromMemory, "png", strideBytes: stride, colorSpace: NamedColorSpace.DisplayP3);
            using var fromSpan = new MemoryStream();
            ImageEncoder.Encode(buffer.AsSpan(), width, height, format, fromSpan, "png", strideBytes: stride, colorSpace: NamedColorSpace.DisplayP3);
            Assert.Equal(fromSpan.ToArray(), fromMemory.ToArray());

            // And it equals going through an Image the long way.
            using var image = Image.FromPixels(buffer, width, height, format, stride);
            image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = ColorProfiles.GetBytes(NamedColorSpace.DisplayP3).ToArray() });
            image.Metadata.Cicp = CicpCode.Of(NamedColorSpace.DisplayP3);
            using var viaImage = new MemoryStream();
            image.Save(viaImage, "png");
            Assert.Equal(viaImage.ToArray(), fromMemory.ToArray());
        }
    }

    [Fact]
    public void UnmanagedFormats_StillEncodeFromBuffers()
    {
        var buffer = new byte[(8 * 3 * 4) + 8];
        using var ms = new MemoryStream();
        ImageEncoder.Encode(buffer.AsMemory(), 8, 4, PixelFormat.Rgb24, ms, "bmp", strideBytes: 8 * 3 + 2);
        using var back = Image.Load(ms.ToArray());
        Assert.Equal((8, 4), (back.Width, back.Height));
    }
}
