using System.IO.Compression;
using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Jpeg;

namespace PeachImage.Formats.Jxl.Encoder.Jpeg;

/// <summary>
/// Writes the contents of a <c>jbrd</c> box (JPEG bitstream reconstruction data, ISO/IEC 18181-2): a bit-packed description of
/// the JPEG's marker structure and tables followed by a Brotli stream with the verbatim marker payloads. The inverse of
/// <see cref="JxlJpegDataReader"/>.
/// </summary>
internal static class JxlJpegDataWriter
{
    public static byte[] Write(JxlJpegData jpeg)
    {
        var bw = new JxlBitWriter();
        bw.WriteBool(jpeg.Components.Count == 1);
        foreach (byte marker in jpeg.MarkerOrder)
        {
            bw.WriteBits((ulong)(marker - 0xC0), 6);
        }

        for (int i = 0; i < jpeg.AppData.Count; i++)
        {
            bw.WriteU32((uint)jpeg.AppMarkerTypes[i], U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(1, 2), U32Dist.BitsOffset(2, 4));
            bw.WriteBits((ulong)(jpeg.AppData[i].Length - 1), 16);
        }

        foreach (byte[] comment in jpeg.ComData)
        {
            bw.WriteBits((ulong)(comment.Length - 1), 16);
        }

        bw.WriteU32((uint)jpeg.Quant.Count, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4));
        foreach (var table in jpeg.Quant)
        {
            bw.WriteBits(table.Precision, 1);
            bw.WriteBits(table.Index, 2);
            bw.WriteBool(table.IsLast);
        }

        WriteComponents(bw, jpeg);

        bw.WriteU32((uint)jpeg.HuffmanCodes.Count, U32Dist.Val(4), U32Dist.BitsOffset(3, 2), U32Dist.BitsOffset(4, 10), U32Dist.BitsOffset(6, 26));
        foreach (var code in jpeg.HuffmanCodes)
        {
            bw.WriteBool((code.SlotId & 0x10) != 0);
            bw.WriteBits((ulong)(code.SlotId & 3), 2);
            bw.WriteBool(code.IsLast);
            long symbols = 0;
            foreach (uint count in code.Counts)
            {
                bw.WriteU32(count, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(3, 2), U32Dist.Bits(8));
                symbols += count;
            }

            for (int i = 0; i < symbols; i++)
            {
                bw.WriteU32(code.Values[i], U32Dist.Bits(2), U32Dist.BitsOffset(2, 4), U32Dist.BitsOffset(4, 8), U32Dist.BitsOffset(8, 1));
            }
        }

        foreach (var scan in jpeg.Scans)
        {
            bw.WriteU32(scan.NumComponents, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4));
            bw.WriteBits(scan.Ss, 6);
            bw.WriteBits(scan.Se, 6);
            bw.WriteBits(scan.Al, 4);
            bw.WriteBits(scan.Ah, 4);
            for (int i = 0; i < scan.NumComponents; i++)
            {
                bw.WriteBits(scan.Components[i].ComponentIndex, 2);
                bw.WriteBits(scan.Components[i].AcTableIndex, 2);
                bw.WriteBits(scan.Components[i].DcTableIndex, 2);
            }

            bw.WriteU32(scan.LastNeededPass, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.BitsOffset(3, 3));
        }

        if (jpeg.MarkerOrder.Contains(0xDD))
        {
            bw.WriteBits(jpeg.RestartInterval, 16);
        }

        foreach (var scan in jpeg.Scans)
        {
            bw.WriteU32((uint)scan.ResetPoints.Count, U32Dist.Val(0), U32Dist.BitsOffset(2, 1), U32Dist.BitsOffset(4, 4), U32Dist.BitsOffset(16, 20));
            long last = -1;
            foreach (uint point in scan.ResetPoints)
            {
                bw.WriteU32((uint)(point - last - 1), U32Dist.Val(0), U32Dist.BitsOffset(3, 1), U32Dist.BitsOffset(5, 9), U32Dist.BitsOffset(28, 41));
                last = point;
            }

            bw.WriteU32((uint)scan.ExtraZeroRuns.Count, U32Dist.Val(0), U32Dist.BitsOffset(2, 1), U32Dist.BitsOffset(4, 4), U32Dist.BitsOffset(16, 20));
            last = -1;
            foreach (var (blockIndex, runs) in scan.ExtraZeroRuns)
            {
                bw.WriteU32(runs, U32Dist.Val(1), U32Dist.BitsOffset(2, 2), U32Dist.BitsOffset(4, 5), U32Dist.BitsOffset(8, 20));
                bw.WriteU32((uint)(blockIndex - last - 1), U32Dist.Val(0), U32Dist.BitsOffset(3, 1), U32Dist.BitsOffset(5, 9), U32Dist.BitsOffset(28, 41));
                last = blockIndex;
            }
        }

        foreach (byte[] data in jpeg.InterMarkerData)
        {
            bw.WriteBits((ulong)data.Length, 16);
        }

        bw.WriteU32((uint)jpeg.TailData.Length, U32Dist.Val(0), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(16, 257), U32Dist.BitsOffset(22, 65793));

        bw.WriteBool(jpeg.HasZeroPaddingBit);
        if (jpeg.HasZeroPaddingBit)
        {
            bw.WriteBits((ulong)jpeg.PaddingBits.Count, 24);
            foreach (byte bit in jpeg.PaddingBits)
            {
                bw.WriteBits(bit, 1);
            }
        }

        bw.ZeroPadToByte();
        bw.WriteBytes(Compress(Payloads(jpeg)));
        return bw.ToArray();
    }

    private static void WriteComponents(JxlBitWriter bw, JxlJpegData jpeg)
    {
        var components = jpeg.Components;
        bool gray = components.Count == 1;
        if (gray && components[0].Id == 1)
        {
            bw.WriteBits(0, 2);
        }
        else if (!gray && components[0].Id == 1 && components[1].Id == 2 && components[2].Id == 3)
        {
            bw.WriteBits(1, 2);
        }
        else
        {
            bw.WriteBits(3, 2);
            bw.WriteU32((uint)components.Count, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4));
            foreach (var component in components)
            {
                bw.WriteBits(component.Id, 8);
            }
        }

        foreach (var component in components)
        {
            bw.WriteBits(component.QuantTableIndex, 2);
        }
    }

    // Brotli carries, in order: the unknown-type application markers, the comments, the inter-marker data and the tail.
    private static byte[] Payloads(JxlJpegData jpeg)
    {
        using var payload = new MemoryStream();
        for (int i = 0; i < jpeg.AppData.Count; i++)
        {
            if (jpeg.AppMarkerTypes[i] == JpegAppMarkerType.Unknown)
            {
                payload.Write(jpeg.AppData[i]);
            }
        }

        foreach (byte[] comment in jpeg.ComData)
        {
            payload.Write(comment);
        }

        foreach (byte[] data in jpeg.InterMarkerData)
        {
            payload.Write(data);
        }

        payload.Write(jpeg.TailData);
        return payload.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(data);
        }

        return output.ToArray();
    }
}
