using PeachImage.Formats.Shared.IO;

namespace PeachImage.Tests.Formats.Robustness;

public class BoundedPayloadReaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryRead_DeclaredSizeBeyondStream_ReturnsFalseWithoutHugeAllocation(bool seekable)
    {
        using var stream = Wrap(new byte[10], seekable);
        long before = GC.GetAllocatedBytesForCurrentThread();

        bool ok = BoundedPayloadReader.TryRead(stream, 1_500_000_000, out _);

        Assert.False(ok);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryRead_ExactSize_ReturnsPayload(bool seekable)
    {
        byte[] data = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        using var stream = Wrap(data, seekable);

        Assert.True(BoundedPayloadReader.TryRead(stream, data.Length, out byte[] result));
        Assert.Equal(data, result);
    }

    private static Stream Wrap(byte[] data, bool seekable) =>
        seekable ? new MemoryStream(data) : new NonSeekableStream(data);

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
