namespace PeachImage.Tests;

/// <summary>
/// Covers the in-memory-buffer and async I/O overloads (<see cref="Image.Load(ReadOnlySpan{byte}, DecoderOptions?)"/>,
/// <see cref="Image.LoadAsync(ReadOnlyMemory{byte}, DecoderOptions?, CancellationToken)"/>,
/// <see cref="Image.SaveAsync(Stream, string, EncoderOptions?, CancellationToken)"/>,
/// <see cref="Image.SaveAsync(string, EncoderOptions?, CancellationToken)"/>, <see cref="Image.Encode"/>) —
/// each is a thin wrapper around the existing sync <see cref="Image.Load(Stream, DecoderOptions?)"/>/
/// <see cref="Image.Save(Stream, string, EncoderOptions?)"/> path, so these pin behavioral equivalence with
/// that path rather than re-testing decode/encode correctness itself.
/// </summary>
public class ImageInMemoryAndAsyncIoTests
{
    [Fact]
    public void Load_FromSpan_ProducesSameImageAsLoadFromStream()
    {
        var source = CreateGradientRgbImage(12, 9);
        byte[] encoded = source.Encode("png");

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
        byte[] encoded = source.Encode("bmp");

        var decoded = Image.Load(encoded);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }

    [Fact]
    public void Load_FromSpan_HonorsDecoderOptions()
    {
        var source = CreateGradientRgbImage(8, 6);
        byte[] encoded = source.Encode("jpeg");

        var decoded = Image.Load((ReadOnlySpan<byte>)encoded, new DecoderOptions { TargetPixelFormat = PixelFormat.Rgba32 });

        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
    }

    [Fact]
    public async Task LoadAsync_FromMemory_ProducesSameImageAsLoadFromSpan()
    {
        var source = CreateGradientRgbImage(10, 7);
        byte[] encoded = source.Encode("png");

        var viaMemory = await Image.LoadAsync((ReadOnlyMemory<byte>)encoded, cancellationToken: TestContext.Current.CancellationToken);
        var viaSpan = Image.Load((ReadOnlySpan<byte>)encoded);

        Assert.Equal(viaSpan.Width, viaMemory.Width);
        Assert.Equal(viaSpan.Height, viaMemory.Height);
        Assert.Equal(viaSpan.GetPixelSpan().ToArray(), viaMemory.GetPixelSpan().ToArray());
    }

    [Fact]
    public async Task LoadAsync_FromMemory_ThrowsIfCancellationAlreadyRequested()
    {
        var source = CreateGradientRgbImage(4, 4);
        byte[] encoded = source.Encode("png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Image.LoadAsync((ReadOnlyMemory<byte>)encoded, cancellationToken: cts.Token));
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

    [Fact]
    public void Encode_ProducesByteIdenticalOutputToSaveIntoMemoryStream()
    {
        var source = CreateGradientRgbImage(9, 7);

        using var stream = new MemoryStream();
        source.Save(stream, "webp");
        byte[] viaSave = stream.ToArray();

        byte[] viaEncode = source.Encode("webp");

        Assert.Equal(viaSave, viaEncode);
    }

    [Fact]
    public void Encode_RoundTrips_ThroughLoad()
    {
        var source = CreateGradientRgbImage(13, 11);

        byte[] encoded = source.Encode("png");
        var decoded = Image.Load(encoded);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
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
