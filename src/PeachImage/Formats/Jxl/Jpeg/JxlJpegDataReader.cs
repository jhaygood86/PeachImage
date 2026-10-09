using System.IO.Compression;
using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Jpeg;

/// <summary>
/// Parses the contents of a <c>jbrd</c> box (JPEG bitstream reconstruction data, ISO/IEC 18181-2): a bit-packed description of
/// the JPEG's marker structure and tables followed by a Brotli stream with the JPEG's verbatim marker payloads.
/// </summary>
internal static class JxlJpegDataReader
{
    private const int MaxMarkers = 16384;
    private const long MaxDecompressedSize = 256L * 1024 * 1024;

    public static JxlJpegData Read(ReadOnlySpan<byte> jbrd)
    {
        var jpeg = new JxlJpegData();
        var br = new JxlBitReader(jbrd);
        int brotliStart = ReadStructure(ref br, jpeg);
        FillMarkerPayloads(jpeg, jbrd[brotliStart..]);
        return jpeg;
    }

    private static int ReadStructure(ref JxlBitReader br, JxlJpegData jpeg)
    {
        bool isGray = br.ReadBool();
        for (int i = 0; i < (isGray ? 1 : 3); i++)
        {
            jpeg.Components.Add(new JpegComponent());
        }

        // Marker order, up to and including EOI, and how many of each kind that implies.
        int numApp = 0;
        int numCom = 0;
        int numScans = 0;
        int numInterMarker = 0;
        bool hasDri = false;
        byte marker;
        do
        {
            marker = (byte)(br.ReadBits(6) + 0xC0);
            if ((marker & 0xF0) == 0xE0)
            {
                numApp++;
            }

            switch (marker)
            {
                case 0xFE:
                    numCom++;
                    break;
                case 0xDA:
                    numScans++;
                    break;
                case 0xFF:
                    numInterMarker++;
                    break;
                case 0xDD:
                    hasDri = true;
                    break;
            }

            jpeg.MarkerOrder.Add(marker);
            if (jpeg.MarkerOrder.Count > MaxMarkers)
            {
                throw new JxlDecodingException("The JPEG reconstruction data has too many markers.");
            }

            br.ThrowIfOverrun();
        }
        while (marker != 0xD9);

        if (numScans == 0)
        {
            throw new JxlDecodingException("The JPEG reconstruction data has no scans.");
        }

        for (int i = 0; i < numApp; i++)
        {
            uint type = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(1, 2), U32Dist.BitsOffset(2, 4));
            if (type > (uint)JpegAppMarkerType.Xmp)
            {
                throw new JxlDecodingException("Unknown JPEG application marker type.");
            }

            jpeg.AppMarkerTypes.Add((JpegAppMarkerType)type);
            int length = (int)br.ReadBits(16);
            if (length + 1 < 3)
            {
                throw new JxlDecodingException("An application marker is too small.");
            }

            jpeg.AppData.Add(new byte[length + 1]);
        }

        for (int i = 0; i < numCom; i++)
        {
            int length = (int)br.ReadBits(16);
            if (length + 1 < 3)
            {
                throw new JxlDecodingException("A comment marker is too small.");
            }

            jpeg.ComData.Add(new byte[length + 1]);
        }

        uint numQuant = JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4));
        if (numQuant == 4)
        {
            throw new JxlDecodingException("Invalid number of quantization tables.");
        }

        for (int i = 0; i < numQuant; i++)
        {
            var table = new JpegQuantTable { Precision = br.ReadBits(1), Index = br.ReadBits(2), IsLast = br.ReadBool() };
            jpeg.Quant.Add(table);
        }

        uint componentType = br.ReadBits(2);
        int numComponents;
        if (componentType == 0)
        {
            numComponents = 1;
        }
        else if (componentType != 3)
        {
            numComponents = 3;
        }
        else
        {
            numComponents = (int)JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4));
            if (numComponents != 1 && numComponents != 3)
            {
                throw new JxlDecodingException("Invalid number of JPEG components.");
            }
        }

        jpeg.Components.Clear();
        for (int i = 0; i < numComponents; i++)
        {
            jpeg.Components.Add(new JpegComponent());
        }

        if (componentType == 3)
        {
            foreach (var component in jpeg.Components)
            {
                component.Id = br.ReadBits(8);
            }
        }
        else if (componentType == 0)
        {
            jpeg.Components[0].Id = 1;
        }
        else if (componentType == 2)
        {
            jpeg.Components[0].Id = 'R';
            jpeg.Components[1].Id = 'G';
            jpeg.Components[2].Id = 'B';
        }
        else
        {
            jpeg.Components[0].Id = 1;
            jpeg.Components[1].Id = 2;
            jpeg.Components[2].Id = 3;
        }

        foreach (var component in jpeg.Components)
        {
            component.QuantTableIndex = br.ReadBits(2);
            if (component.QuantTableIndex >= jpeg.Quant.Count)
            {
                throw new JxlDecodingException("A JPEG component refers to a missing quantization table.");
            }
        }

        uint numHuffman = JxlFieldReader.ReadU32(ref br, U32Dist.Val(4), U32Dist.BitsOffset(3, 2), U32Dist.BitsOffset(4, 10), U32Dist.BitsOffset(6, 26));
        for (int h = 0; h < numHuffman; h++)
        {
            var code = new JpegHuffmanCode();
            bool isAc = br.ReadBool();
            uint id = br.ReadBits(2);
            code.SlotId = (isAc ? 0x10 : 0) | (int)id;
            code.IsLast = br.ReadBool();
            long numSymbols = 0;
            for (int i = 0; i <= JpegHuffmanCode.MaxBitLength; i++)
            {
                code.Counts[i] = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.BitsOffset(3, 2), U32Dist.Bits(8));
                numSymbols += code.Counts[i];
            }

            jpeg.HuffmanCodes.Add(code);
            br.ThrowIfOverrun();
            if (numSymbols == 0)
            {
                // An empty DHT marker.
                continue;
            }

            if (numSymbols > code.Values.Length)
            {
                throw new JxlDecodingException("A JPEG Huffman code is too large.");
            }

            var seen = new bool[JpegHuffmanCode.AlphabetSize + 1];
            for (int i = 0; i < numSymbols; i++)
            {
                uint value = JxlFieldReader.ReadU32(ref br, U32Dist.Bits(2), U32Dist.BitsOffset(2, 4), U32Dist.BitsOffset(4, 8), U32Dist.BitsOffset(8, 1));
                code.Values[i] = value;
                if (value > JpegHuffmanCode.AlphabetSize || seen[value])
                {
                    throw new JxlDecodingException("A JPEG Huffman code has a duplicate or invalid symbol.");
                }

                seen[value] = true;
            }

            if (code.Values[numSymbols - 1] != JpegHuffmanCode.AlphabetSize)
            {
                throw new JxlDecodingException("A JPEG Huffman code lacks its terminating symbol.");
            }

            if (!isAc)
            {
                for (int symbol = 12; symbol < JpegHuffmanCode.AlphabetSize; symbol++)
                {
                    if (seen[symbol])
                    {
                        throw new JxlDecodingException("A JPEG DC Huffman code has symbols out of range.");
                    }
                }
            }
        }

        for (int s = 0; s < numScans; s++)
        {
            var scan = new JpegScanInfo
            {
                NumComponents = JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.Val(2), U32Dist.Val(3), U32Dist.Val(4)),
            };
            if (scan.NumComponents >= 4)
            {
                throw new JxlDecodingException("Invalid number of components in a JPEG scan.");
            }

            scan.Ss = br.ReadBits(6);
            scan.Se = br.ReadBits(6);
            scan.Al = br.ReadBits(4);
            scan.Ah = br.ReadBits(4);
            for (int i = 0; i < scan.NumComponents; i++)
            {
                uint componentIndex = br.ReadBits(2);
                if (componentIndex >= jpeg.Components.Count)
                {
                    throw new JxlDecodingException("Invalid component index in a JPEG scan.");
                }

                scan.Components[i].ComponentIndex = componentIndex;
                scan.Components[i].AcTableIndex = br.ReadBits(2);
                scan.Components[i].DcTableIndex = br.ReadBits(2);
            }

            scan.LastNeededPass = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.Val(1), U32Dist.Val(2), U32Dist.BitsOffset(3, 3));
            jpeg.Scans.Add(scan);
            br.ThrowIfOverrun();
        }

        if (hasDri)
        {
            jpeg.RestartInterval = br.ReadBits(16);
        }

        foreach (var scan in jpeg.Scans)
        {
            uint numResetPoints = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(2, 1), U32Dist.BitsOffset(4, 4), U32Dist.BitsOffset(16, 20));
            long last = -1;
            for (uint i = 0; i < numResetPoints; i++)
            {
                uint delta = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(3, 1), U32Dist.BitsOffset(5, 9), U32Dist.BitsOffset(28, 41));
                long blockIndex = last + 1 + delta;
                if (blockIndex >= (3L << 26))
                {
                    throw new JxlDecodingException("Invalid JPEG block index.");
                }

                scan.ResetPoints.Add((uint)blockIndex);
                last = blockIndex;
                br.ThrowIfOverrun();
            }

            uint numExtraZeroRuns = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(2, 1), U32Dist.BitsOffset(4, 4), U32Dist.BitsOffset(16, 20));
            last = -1;
            for (uint i = 0; i < numExtraZeroRuns; i++)
            {
                uint runs = JxlFieldReader.ReadU32(ref br, U32Dist.Val(1), U32Dist.BitsOffset(2, 2), U32Dist.BitsOffset(4, 5), U32Dist.BitsOffset(8, 20));
                uint delta = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(3, 1), U32Dist.BitsOffset(5, 9), U32Dist.BitsOffset(28, 41));
                long blockIndex = last + 1 + delta;
                if (runs > 4 || blockIndex > (3L << 26))
                {
                    throw new JxlDecodingException("Invalid JPEG extra zero run.");
                }

                scan.ExtraZeroRuns.Add(((uint)blockIndex, runs));
                last = blockIndex;
                br.ThrowIfOverrun();
            }
        }

        var interMarkerSizes = new int[numInterMarker];
        for (int i = 0; i < numInterMarker; i++)
        {
            interMarkerSizes[i] = (int)br.ReadBits(16);
        }

        uint tailLength = JxlFieldReader.ReadU32(ref br, U32Dist.Val(0), U32Dist.BitsOffset(8, 1), U32Dist.BitsOffset(16, 257), U32Dist.BitsOffset(22, 65793));

        jpeg.HasZeroPaddingBit = br.ReadBool();
        if (jpeg.HasZeroPaddingBit)
        {
            uint bitCount = br.ReadBits(24);
            if (br.BitsRemaining < bitCount)
            {
                throw new JxlDecodingException("The JPEG padding bits extend past the reconstruction data.");
            }

            for (uint i = 0; i < bitCount; i++)
            {
                jpeg.PaddingBits.Add((byte)br.ReadBits(1));
            }
        }

        br.ThrowIfOverrun();
        ValidateTableUse(jpeg);

        foreach (int size in interMarkerSizes)
        {
            jpeg.InterMarkerData.Add(new byte[size]);
        }

        jpeg.TailData = new byte[tailLength];
        br.ZeroPadToByte();
        br.ThrowIfOverrun();
        return (int)(br.BitPosition >> 3);
    }

    // Every table a scan uses has to be defined by an earlier DHT marker.
    private static void ValidateTableUse(JxlJpegData jpeg)
    {
        int dhtIndex = 0;
        int scanIndex = 0;
        bool progressive = false;
        var acOk = new bool[JxlJpegData.MaxHuffmanTables];
        var dcOk = new bool[JxlJpegData.MaxHuffmanTables];
        foreach (byte marker in jpeg.MarkerOrder)
        {
            if (marker == 0xC2)
            {
                progressive = true;
            }
            else if (marker == 0xC4)
            {
                while (dhtIndex < jpeg.HuffmanCodes.Count)
                {
                    var huff = jpeg.HuffmanCodes[dhtIndex++];
                    int index = huff.SlotId;
                    if ((index & 0x10) != 0)
                    {
                        acOk[index - 0x10] = true;
                    }
                    else
                    {
                        dcOk[index] = true;
                    }

                    if (huff.IsLast)
                    {
                        break;
                    }
                }
            }
            else if (marker == 0xDA)
            {
                var scan = jpeg.Scans[scanIndex++];
                for (int i = 0; i < scan.NumComponents; i++)
                {
                    bool wantDc = !progressive || scan.Ss == 0;
                    bool wantAc = !progressive || scan.Ss != 0 || scan.Se != 0;
                    if (wantDc && !dcOk[scan.Components[i].DcTableIndex])
                    {
                        throw new JxlDecodingException("A JPEG DC Huffman table is used before it is defined.");
                    }

                    if (wantAc && !acOk[scan.Components[i].AcTableIndex])
                    {
                        throw new JxlDecodingException("A JPEG AC Huffman table is used before it is defined.");
                    }
                }
            }
        }
    }

    // Brotli carries, in order: the unknown-type application markers, the comments, the inter-marker data and the tail.
    private static void FillMarkerPayloads(JxlJpegData jpeg, ReadOnlySpan<byte> compressed)
    {
        byte[] data = Decompress(compressed);
        int position = 0;

        byte[] Take(int length)
        {
            if (length > data.Length - position)
            {
                throw new JxlDecodingException("Not enough decompressed JPEG reconstruction data.");
            }

            var result = data.AsSpan(position, length).ToArray();
            position += length;
            return result;
        }

        int iccCount = 0;
        for (int i = 0; i < jpeg.AppData.Count; i++)
        {
            var marker = jpeg.AppData[i];
            if (jpeg.AppMarkerTypes[i] != JpegAppMarkerType.Unknown)
            {
                // The payload comes from the codestream (ICC) or the Exif / XMP boxes; only the framing is known here.
                int sizeMinusOne = marker.Length - 1;
                marker[1] = (byte)(sizeMinusOne >> 8);
                marker[2] = (byte)(sizeMinusOne & 0xFF);
                if (jpeg.AppMarkerTypes[i] == JpegAppMarkerType.Icc)
                {
                    if (marker.Length < 17)
                    {
                        throw new JxlDecodingException("An ICC application marker is too small.");
                    }

                    marker[0] = 0xE2;
                    JxlJpegData.IccProfileTag.CopyTo(marker.AsSpan(3));
                    marker[15] = (byte)++iccCount;
                }
            }
            else
            {
                Take(marker.Length).CopyTo(marker, 0);
                if ((marker[1] * 256) + marker[2] + 1 != marker.Length)
                {
                    throw new JxlDecodingException("Incorrect application marker size.");
                }
            }
        }

        for (int i = 0; i < jpeg.AppData.Count; i++)
        {
            var marker = jpeg.AppData[i];
            switch (jpeg.AppMarkerTypes[i])
            {
                case JpegAppMarkerType.Icc:
                    marker[16] = (byte)iccCount;
                    break;
                case JpegAppMarkerType.Exif:
                    if (marker.Length < 3 + JxlJpegData.ExifTag.Length)
                    {
                        throw new JxlDecodingException("Incorrect Exif marker size.");
                    }

                    marker[0] = 0xE1;
                    JxlJpegData.ExifTag.CopyTo(marker.AsSpan(3));
                    break;
                case JpegAppMarkerType.Xmp:
                    if (marker.Length < 3 + JxlJpegData.XmpTag.Length)
                    {
                        throw new JxlDecodingException("Incorrect XMP marker size.");
                    }

                    marker[0] = 0xE1;
                    JxlJpegData.XmpTag.CopyTo(marker.AsSpan(3));
                    break;
            }
        }

        foreach (var marker in jpeg.ComData)
        {
            Take(marker.Length).CopyTo(marker, 0);
            if ((marker[1] * 256) + marker[2] + 1 != marker.Length)
            {
                throw new JxlDecodingException("Incorrect comment marker size.");
            }
        }

        for (int i = 0; i < jpeg.InterMarkerData.Count; i++)
        {
            jpeg.InterMarkerData[i] = Take(jpeg.InterMarkerData[i].Length);
        }

        jpeg.TailData = Take(jpeg.TailData.Length);
        if (position != data.Length)
        {
            throw new JxlDecodingException("Excess data in the JPEG reconstruction data.");
        }
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxDecompressedSize)
                {
                    throw new JxlDecodingException("The JPEG reconstruction data expands beyond the allowed size.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            throw new JxlDecodingException("The JPEG reconstruction data is corrupt.", ex);
        }
    }
}
