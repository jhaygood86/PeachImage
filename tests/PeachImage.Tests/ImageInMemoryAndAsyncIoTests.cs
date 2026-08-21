namespace PeachImage.Tests;

/// <summary>
/// Covers the in-memory-buffer and async I/O overloads (<see cref="Image.Load(ReadOnlySpan{byte}, DecoderOptions?)"/>,
/// <see cref="Image.SaveAsync(Stream, string, EncoderOptions?, CancellationToken)"/>,
/// <see cref="Image.SaveAsync(string, EncoderOptions?, CancellationToken)"/>) — each is a thin wrapper around
/// the existing sync <see cref="Image.Load(Stream, DecoderOptions?)"/>/<see cref="Image.Save(Stream, string, EncoderOptions?)"/>
/// path, so these pin behavioral equivalence with that path rather than re-testing decode/encode correctness itself.
/// </summary>
public class ImageInMemoryAndAsyncIoTests
{
    [Fact]
    public void Load_FromSpan_ProducesSameImageAsLoadFromStream()
    {
        var source = CreateGradientRgbImage(12, 9);
        byte[] encoded = EncodeToBytes(source, "png");

        var viaSpan = Image.Load((ReadOnlySpan<byte>)encoded);
        using var stream = new MemoryStream(encoded);
        var viaStream = Image.Load(stream);

        Assert.Equal(viaStream.Width, viaSpan.Width);
        Assert.Equal(viaStream.Height, viaSpan.Height);
        Assert.Equal(viaStream.PixelFormat, viaSpan.PixelFormat);
        Assert.Equal(viaStream.GetPixelSpan().ToArray(), viaSpan.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Load_FromSpan_AcceptsAByteArrayImplicitly()
    {
        var source = CreateGradientRgbImage(6, 5);
        byte[] encoded = EncodeToBytes(source, "bmp");

        var decoded = Image.Load(encoded);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Fact]
    public void Load_FromSpan_HonorsDecoderOptions()
    {
        var source = CreateGradientRgbImage(8, 6);
        byte[] encoded = EncodeToBytes(source, "jpeg");

        var decoded = Image.Load((ReadOnlySpan<byte>)encoded, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
    }

    [Fact]
    public void Load_FromSpan_DoesNotReadPastTheGivenLength()
    {
        // A span that's a slice of a larger backing array must not let the decoder read trailing bytes it
        // doesn't own — this pins that the UnmanagedMemoryStream backing Load(ReadOnlySpan<byte>) is bounded
        // by the span's length, not the full backing array's.
        var source = CreateGradientRgbImage(5, 4);
        byte[] encoded = EncodeToBytes(source, "png");

        byte[] padded = new byte[encoded.Length + 64];
        encoded.CopyTo(padded, 0);
        new Random(1).NextBytes(padded.AsSpan(encoded.Length)); // trailing garbage, not part of the image

        var decoded = Image.Load(padded.AsSpan(0, encoded.Length));

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Fact]
    public void Load_FromSpan_WorksWithAStackallocBuffer()
    {
        var source = CreateGradientRgbImage(4, 3);
        byte[] encoded = EncodeToBytes(source, "bmp");

        Span<byte> stackBuffer = stackalloc byte[encoded.Length];
        encoded.CopyTo(stackBuffer);

        var decoded = Image.Load((ReadOnlySpan<byte>)stackBuffer);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("webp")]
    public async Task SaveAsync_ToStream_ProducesByteIdenticalOutputToSyncSave(string formatName)
    {
        var source = CreateGradientRgbImage(14, 10);

        using var syncStream = new MemoryStream();
        source.Save(syncStream, formatName);

        using var asyncStream = new MemoryStream();
        await source.SaveAsync(asyncStream, formatName, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(syncStream.ToArray(), asyncStream.ToArray());
    }

    [Fact]
    public async Task SaveAsync_ToPath_ProducesByteIdenticalOutputToSyncSave()
    {
        var source = CreateGradientRgbImage(11, 8);
        string syncPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
        string asyncPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");

        try
        {
            source.Save(syncPath);
            await source.SaveAsync(asyncPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(File.ReadAllBytes(syncPath), File.ReadAllBytes(asyncPath));
        }
        finally
        {
            File.Delete(syncPath);
            File.Delete(asyncPath);
        }
    }

    [Fact]
    public async Task SaveAsync_ToStream_ThrowsUnknownImageFormatException_ForUnknownFormat()
    {
        var source = CreateGradientRgbImage(4, 4);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<UnknownImageFormatException>(
            () => source.SaveAsync(stream, "not-a-real-format", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static byte[] EncodeToBytes(Image image, string formatName)
    {
        using var stream = new MemoryStream();
        image.Save(stream, formatName);
        return stream.ToArray();
    }

    private static Image CreateGradientRgbImage(int width, int height)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            var row = image.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                row[(x * 3) + 0] = (byte)((x * 255) / Math.Max(1, width - 1));
                row[(x * 3) + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                row[(x * 3) + 2] = (byte)((x + y) % 256);
            }
        }

        return image;
    }
}
