namespace PeachImage.Formats.Jxl.Jpeg;

/// <summary>A JPEG quantization table as stored in a DQT marker.</summary>
internal sealed class JpegQuantTable
{
    /// <summary>The 64 values in natural (row-major) order.</summary>
    public int[] Values { get; } = new int[64];

    /// <summary>0 for 8-bit values, 1 for 16-bit values.</summary>
    public uint Precision { get; set; }

    /// <summary>The table's index in its DQT marker (0 to 3).</summary>
    public uint Index { get; set; }

    /// <summary>Whether this is the last table of its marker segment.</summary>
    public bool IsLast { get; set; } = true;
}

/// <summary>A Huffman code in the form of a DHT marker: a bit-length histogram plus the symbols in code order.</summary>
internal sealed class JpegHuffmanCode
{
    public const int MaxBitLength = 16;
    public const int AlphabetSize = 256;

    /// <summary>Number of codes of each bit length (index 0 is unused). The count of the longest length includes one extra, virtual code.</summary>
    public uint[] Counts { get; } = new uint[MaxBitLength + 1];

    /// <summary>The symbols sorted by increasing code length; the last one is the virtual symbol 256.</summary>
    public uint[] Values { get; } = new uint[AlphabetSize + 1];

    /// <summary>The table slot; 0x10 is added for AC tables.</summary>
    public int SlotId { get; set; }

    /// <summary>Whether this is the last code of its marker segment.</summary>
    public bool IsLast { get; set; } = true;
}

internal struct JpegComponentScanInfo
{
    public uint ComponentIndex;
    public uint DcTableIndex;
    public uint AcTableIndex;
}

/// <summary>One scan of the JPEG, with the extra information needed to reproduce an encoder's exact bit stream.</summary>
internal sealed class JpegScanInfo
{
    public uint Ss;
    public uint Se;
    public uint Ah;
    public uint Al;
    public uint NumComponents;
    public JpegComponentScanInfo[] Components { get; } = new JpegComponentScanInfo[4];

    public uint LastNeededPass;

    /// <summary>Block indices where the encoder flushed its end-of-block runs and refinement bits.</summary>
    public List<uint> ResetPoints { get; } = [];

    /// <summary>Blocks that end with extra 0xF0 zero-run symbols, with the number of those symbols.</summary>
    public List<(uint BlockIndex, uint ExtraZeroRuns)> ExtraZeroRuns { get; } = [];
}

/// <summary>One colour component of the JPEG and its quantized DCT coefficients.</summary>
internal sealed class JpegComponent
{
    public uint Id;
    public int HorizontalSamplingFactor = 1;
    public int VerticalSamplingFactor = 1;
    public uint QuantTableIndex;
    public uint WidthInBlocks;
    public uint HeightInBlocks;

    /// <summary>The coefficients block by block (64 per block, natural order), as stored in the file (divided by the quantization table).</summary>
    public short[] Coefficients = [];
}

internal enum JpegAppMarkerType : uint
{
    Unknown = 0,
    Icc = 1,
    Exif = 2,
    Xmp = 3,
}

/// <summary>Everything in a JPEG file besides its pixels: the bits <c>jbrd</c> stores, plus the coefficients the codestream holds.</summary>
internal sealed class JxlJpegData
{
    public const int MaxComponents = 4;
    public const int MaxHuffmanTables = 4;
    public const int DctBlockSize = 64;

    public static ReadOnlySpan<byte> IccProfileTag => "ICC_PROFILE\0"u8;

    public static ReadOnlySpan<byte> ExifTag => "Exif\0\0"u8;

    public static ReadOnlySpan<byte> XmpTag => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>Zigzag position to natural (row-major) coefficient index.</summary>
    public static readonly uint[] NaturalOrder =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63,
        63, 63, 63, 63, 63, 63, 63, 63,
    ];

    public int Width;
    public int Height;
    public uint RestartInterval;
    public List<byte[]> AppData { get; } = [];
    public List<JpegAppMarkerType> AppMarkerTypes { get; } = [];
    public List<byte[]> ComData { get; } = [];
    public List<JpegQuantTable> Quant { get; } = [];
    public List<JpegHuffmanCode> HuffmanCodes { get; } = [];
    public List<JpegComponent> Components { get; } = [];
    public List<JpegScanInfo> Scans { get; } = [];
    public List<byte> MarkerOrder { get; } = [];
    public List<byte[]> InterMarkerData { get; } = [];
    public byte[] TailData = [];
    public bool HasZeroPaddingBit;
    public List<byte> PaddingBits { get; } = [];

    /// <summary>Computes the number of MCUs per row and the number of MCU rows of <paramref name="scan"/>.</summary>
    public void CalculateMcuSize(JpegScanInfo scan, out int mcusPerRow, out int mcuRows)
    {
        bool interleaved = scan.NumComponents > 1;
        var baseComponent = Components[(int)scan.Components[0].ComponentIndex];
        int hGroup = interleaved ? 1 : baseComponent.HorizontalSamplingFactor;
        int vGroup = interleaved ? 1 : baseComponent.VerticalSamplingFactor;
        int maxH = 1;
        int maxV = 1;
        foreach (var component in Components)
        {
            maxH = Math.Max(maxH, component.HorizontalSamplingFactor);
            maxV = Math.Max(maxV, component.VerticalSamplingFactor);
        }

        mcusPerRow = ((Width * hGroup) + (8 * maxH) - 1) / (8 * maxH);
        mcuRows = ((Height * vGroup) + (8 * maxV) - 1) / (8 * maxV);
    }
}
