using PeachImage.Formats.Gif.Decoding;

namespace PeachImage.Tests.Formats.Gif.Unit.Decoding;

public class GifInterlacerTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(1)]
    [InlineData(2)]
    public void Deinterlace_RestoresRowOrder(int height)
    {
        const int width = 4;

        // Storage order: pass 1 (0, 8, 16, ...), pass 2 (4, 12, ...), pass 3 (2, 6, 10, ...), pass 4 (1, 3, 5, ...).
        var storageRowOrder = new List<int>();
        for (int r = 0; r < height; r += 8) storageRowOrder.Add(r);
        for (int r = 4; r < height; r += 8) storageRowOrder.Add(r);
        for (int r = 2; r < height; r += 4) storageRowOrder.Add(r);
        for (int r = 1; r < height; r += 2) storageRowOrder.Add(r);

        Assert.Equal(height, storageRowOrder.Count);

        byte[] source = new byte[width * height];
        for (int i = 0; i < storageRowOrder.Count; i++)
        {
            int destRow = storageRowOrder[i];
            Array.Fill(source, (byte)destRow, i * width, width);
        }

        byte[] result = GifInterlacer.Deinterlace(source, width, height);

        for (int row = 0; row < height; row++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal((byte)row, result[(row * width) + x]);
            }
        }
    }
}
