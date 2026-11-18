using PeachImage.Formats.Jbig2;

namespace PeachImage.Tests.Formats.Jbig2.Unit;

public class Jbig2SymbolDictionaryTests
{
    // Byte 23 of refagg_t0.jb2 is in the symbol dictionary's exported-symbol count; 0x7E makes it far larger than the
    // dictionary's symbols. Before the count was checked against them, the decoder sized its export list from it (11.5 GB).
    [Fact]
    public void ExportedCountAboveAvailableSymbols_FailsWithoutALargeAllocation()
    {
        byte[] data = ReadAsset("refagg_t0.jb2");
        data[23] = 0x7E;

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<ImageFormatException>(() => Image.Load(data));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16 << 20, $"Rejecting the stream allocated {allocated:N0} bytes.");
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(Jbig2SymbolDictionaryTests).Assembly.GetManifestResourceStream($"PeachImage.Tests.Formats.Jbig2.Assets.{name}")
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
