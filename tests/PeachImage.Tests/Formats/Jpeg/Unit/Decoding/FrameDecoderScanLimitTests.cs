using PeachImage.Formats.Jpeg;
using PeachImage.Formats.Jpeg.Decoding;

namespace PeachImage.Tests.Formats.Jpeg.Unit.Decoding;

public class FrameDecoderScanLimitTests
{
    [Fact]
    public void Decode_ScanCountExceedsLimit_ThrowsBeforeDecodingFurtherScans()
    {
        // A tiny, validly-encoded single-scan baseline JPEG whose one SOS-through-entropy-data segment is
        // then repeated several times in the same stream (nothing in the decode loop previously limited how
        // many SOS markers a frame could contain — each one triggered a full decode pass over every block).
        // maxScanCount: 2 lets this test trip the limit with just a handful of repeats instead of needing to
        // construct hundreds/thousands of scans to reach the real production cap.
        byte[] jpeg = EncodeMinimalJpeg();
        byte[] repeated = RepeatFirstScan(jpeg, repeatCount: 4);

        var ex = Assert.Throws<JpegDecodingException>(() => FrameDecoder.Decode(new MemoryStream(repeated), maxScanCount: 2));
        Assert.Contains("scan", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_ScanCountWithinLimit_DecodesNormally()
    {
        byte[] jpeg = EncodeMinimalJpeg();
        byte[] repeated = RepeatFirstScan(jpeg, repeatCount: 2);

        var frame = FrameDecoder.Decode(new MemoryStream(repeated), maxScanCount: 5);

        Assert.NotNull(frame);
    }

    [Fact]
    public void Decode_OrdinarySingleScanFile_UsesProductionDefaultAndDecodesNormally()
    {
        byte[] jpeg = EncodeMinimalJpeg();

        var frame = FrameDecoder.Decode(new MemoryStream(jpeg));

        Assert.NotNull(frame);
    }

    private static byte[] EncodeMinimalJpeg()
    {
        using var source = Image.Create(16, 16, PixelFormat.Rgb24);
        for (int y = 0; y < 16; y++)
        {
            var row = source.GetRowSpan(y);
            for (int x = 0; x < 16; x++)
            {
                row[(x * 3) + 0] = (byte)(x * 16);
                row[(x * 3) + 1] = (byte)(y * 16);
                row[(x * 3) + 2] = 128;
            }
        }

        using var ms = new MemoryStream();
        new JpegEncoder().Encode(source, ms, new JpegEncoderOptions { Quality = 90 });
        return ms.ToArray();
    }

    /// <summary>
    /// Splices <paramref name="repeatCount"/> copies of a single-scan JPEG's SOS-marker-through-entropy-data
    /// segment back to back, ahead of the trailing EOI marker. The decoder does not (and, for this test's
    /// purpose, should not) care that every repeat encodes the same content — each is independently
    /// well-formed, exactly like a real multi-scan progressive file's later scans would be.
    /// </summary>
    private static byte[] RepeatFirstScan(byte[] jpeg, int repeatCount)
    {
        int sosIndex = FindMarker(jpeg, 0xDA, 0);
        int entropyEnd = FindNextRealMarker(jpeg, sosIndex + 2);

        byte[] header = jpeg[..sosIndex];
        byte[] scanBlock = jpeg[sosIndex..entropyEnd];
        byte[] trailer = jpeg[entropyEnd..];

        using var ms = new MemoryStream();
        ms.Write(header);
        for (int i = 0; i < repeatCount; i++)
        {
            ms.Write(scanBlock);
        }

        ms.Write(trailer);
        return ms.ToArray();
    }

    private static int FindMarker(byte[] data, byte markerByte, int startIndex)
    {
        for (int i = startIndex; i < data.Length - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == markerByte)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Marker 0xFF{markerByte:X2} not found.");
    }

    /// <summary>Scans entropy-coded data (skipping 0xFF 0x00 byte-stuffed pairs) for the next genuine marker.</summary>
    private static int FindNextRealMarker(byte[] data, int startIndex)
    {
        int i = startIndex;
        while (i < data.Length - 1)
        {
            if (data[i] == 0xFF && data[i + 1] != 0x00)
            {
                return i;
            }

            i++;
        }

        throw new InvalidOperationException("No terminating marker found after scan entropy data.");
    }
}
