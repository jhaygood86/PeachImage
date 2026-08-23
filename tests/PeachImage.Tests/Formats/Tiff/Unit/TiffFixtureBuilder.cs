using System.Buffers.Binary;

namespace PeachImage.Tests.Formats.Tiff.Unit;

/// <summary>
/// Hand-rolled minimal single-IFD TIFF file builder for unit tests — writes exactly the tags this decoder
/// reads (plus whichever optional ones a test opts into), in either byte order, without pulling in a full
/// TIFF-writing library. Lays the file out in three sections: header, IFD (fixed-size entries + a zero
/// next-IFD offset), then "external" tag data (arrays too large to fit inline) followed immediately by strip
/// pixel data — offsets into the external section are computed as it's built, so callers never need to
/// pre-compute byte positions themselves.
/// </summary>
internal sealed class TiffFixtureBuilder
{
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    public required int Width { get; init; }

    public required int Height { get; init; }

    public int BitsPerSample { get; init; } = 8;

    public int SamplesPerPixel { get; init; } = 1;

    public int Compression { get; init; } = 1;

    public int Photometric { get; init; } = 1;

    public int Predictor { get; init; } = 1;

    public int? RowsPerStrip { get; init; }

    public int? PlanarConfiguration { get; init; }

    public int? FillOrder { get; init; }

    public int? SampleFormat { get; init; }

    public int[]? ExtraSamples { get; init; }

    public int? InkSet { get; init; }

    public ushort[]? ColorMap { get; init; }

    public bool LittleEndian { get; init; } = true;

    public bool OmitStripByteCounts { get; init; }

    public bool OmitStripOffsets { get; init; }

    public bool WriteTileTagInstead { get; init; }

    public uint? OverrideCompressionValue { get; init; }

    public uint? OverridePhotometricValue { get; init; }

    /// <summary>Pre-encoded strip payloads (already compressed per <see cref="Compression"/>), one per strip, in row order.</summary>
    public required byte[][] Strips { get; init; }

    public byte[] Build()
    {
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] InlineOrPointerBytes, byte[]? ExternalData)>();

        void AddShort(ushort tag, int value) => entries.Add((tag, TypeShort, 1, Pack(TypeShort, [(uint)value]), null));
        void AddLong(ushort tag, uint value) => entries.Add((tag, TypeLong, 1, Pack(TypeLong, [value]), null));

        void AddShortArray(ushort tag, int[] values)
        {
            uint[] widened = Array.ConvertAll(values, v => (uint)v);
            byte[] packed = Pack(TypeShort, widened);
            entries.Add(packed.Length <= 4
                ? (tag, TypeShort, (uint)values.Length, packed, null)
                : (tag, TypeShort, (uint)values.Length, [], packed));
        }

        void AddLongArray(ushort tag, uint[] values)
        {
            byte[] packed = Pack(TypeLong, values);
            entries.Add(packed.Length <= 4
                ? (tag, TypeLong, (uint)values.Length, packed, null)
                : (tag, TypeLong, (uint)values.Length, [], packed));
        }

        AddLong(256, (uint)Width); // ImageWidth
        AddLong(257, (uint)Height); // ImageLength

        if (SamplesPerPixel == 1)
        {
            AddShort(258, BitsPerSample); // BitsPerSample
        }
        else
        {
            AddShortArray(258, Enumerable.Repeat(BitsPerSample, SamplesPerPixel).ToArray());
        }

        entries.Add(OverrideCompressionValue is { } cv
            ? (259, TypeShort, 1, Pack(TypeShort, [cv]), null)
            : (259, TypeShort, 1, Pack(TypeShort, [(uint)Compression]), null));

        if (FillOrder is { } fillOrder)
        {
            AddShort(266, fillOrder);
        }

        entries.Add(OverridePhotometricValue is { } pv
            ? (262, TypeShort, 1, Pack(TypeShort, [pv]), null)
            : (262, TypeShort, 1, Pack(TypeShort, [(uint)Photometric]), null));

        if (WriteTileTagInstead)
        {
            AddShort(322, 16); // TileWidth
        }

        if (!OmitStripOffsets)
        {
            // Placeholder — real offsets are patched in after external data + strip layout is known.
            AddLongArray(273, new uint[Strips.Length]);
        }

        AddShort(277, SamplesPerPixel); // SamplesPerPixel

        if (RowsPerStrip is { } rps)
        {
            AddLong(278, (uint)rps);
        }

        if (!OmitStripByteCounts)
        {
            AddLongArray(279, Array.ConvertAll(Strips, s => (uint)s.Length));
        }

        if (PlanarConfiguration is { } pc)
        {
            AddShort(284, pc);
        }

        if (Predictor != 1)
        {
            AddShort(317, Predictor);
        }

        if (ColorMap is { } colorMap)
        {
            AddShortArray(320, Array.ConvertAll(colorMap, v => (int)v));
        }

        if (InkSet is { } inkSet)
        {
            AddShort(332, inkSet);
        }

        if (ExtraSamples is { } extraSamples)
        {
            AddShortArray(338, extraSamples);
        }

        if (SampleFormat is { } sampleFormat)
        {
            AddShort(339, sampleFormat);
        }

        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        const int headerSize = 8;
        int ifdSize = 2 + (entries.Count * 12) + 4;
        int externalStart = headerSize + ifdSize;

        var external = new List<byte>();
        var resolvedEntries = new List<(ushort Tag, ushort Type, uint Count, byte[] ValueField)>();
        int stripOffsetsEntryIndex = -1;

        foreach (var entry in entries)
        {
            if (entry.ExternalData is null)
            {
                resolvedEntries.Add((entry.Tag, entry.Type, entry.Count, entry.InlineOrPointerBytes));
            }
            else
            {
                uint offset = (uint)(externalStart + external.Count);
                external.AddRange(entry.ExternalData);
                resolvedEntries.Add((entry.Tag, entry.Type, entry.Count, Pack(TypeLong, [offset])));
            }

            if (entry.Tag == 273 && !OmitStripOffsets)
            {
                stripOffsetsEntryIndex = resolvedEntries.Count - 1;
            }
        }

        int stripDataStart = externalStart + external.Count;
        var stripOffsets = new uint[Strips.Length];
        int cursor = stripDataStart;
        for (int i = 0; i < Strips.Length; i++)
        {
            stripOffsets[i] = (uint)cursor;
            cursor += Strips[i].Length;
        }

        if (stripOffsetsEntryIndex >= 0)
        {
            var stripOffsetsEntry = resolvedEntries[stripOffsetsEntryIndex];
            byte[] packedOffsets = Pack(TypeLong, stripOffsets);
            if (packedOffsets.Length <= 4)
            {
                resolvedEntries[stripOffsetsEntryIndex] = (stripOffsetsEntry.Tag, stripOffsetsEntry.Type, stripOffsetsEntry.Count, packedOffsets);
            }
            else
            {
                // The StripOffsets array itself needs external storage too; append it and repoint.
                uint offset = (uint)(externalStart + external.Count);
                external.AddRange(packedOffsets);
                resolvedEntries[stripOffsetsEntryIndex] = (stripOffsetsEntry.Tag, stripOffsetsEntry.Type, stripOffsetsEntry.Count, Pack(TypeLong, [offset]));

                // Re-lay-out strip data after the (now larger) external section.
                stripDataStart = externalStart + external.Count;
                cursor = stripDataStart;
                for (int i = 0; i < Strips.Length; i++)
                {
                    stripOffsets[i] = (uint)cursor;
                    cursor += Strips[i].Length;
                }

                byte[] finalPackedOffsets = Pack(TypeLong, stripOffsets);
                external.RemoveRange(external.Count - packedOffsets.Length, packedOffsets.Length);
                external.AddRange(finalPackedOffsets);
            }
        }

        var output = new List<byte>();
        WriteHeader(output);
        WriteUInt16(output, (ushort)resolvedEntries.Count);
        foreach (var entry in resolvedEntries)
        {
            WriteUInt16(output, entry.Tag);
            WriteUInt16(output, entry.Type);
            WriteUInt32(output, entry.Count);
            var valueField = entry.ValueField.Length == 4 ? entry.ValueField : Pad4(entry.ValueField);
            output.AddRange(valueField);
        }

        WriteUInt32(output, 0); // Next IFD offset: none — only the first IFD is ever read.
        output.AddRange(external);

        foreach (var strip in Strips)
        {
            output.AddRange(strip);
        }

        return output.ToArray();
    }

    private static byte[] Pad4(byte[] value)
    {
        var padded = new byte[4];
        value.CopyTo(padded, 0);
        return padded;
    }

    private byte[] Pack(ushort type, uint[] values)
    {
        int size = type == TypeShort ? 2 : 4;
        var bytes = new byte[values.Length * size];
        for (int i = 0; i < values.Length; i++)
        {
            if (type == TypeShort)
            {
                WriteUInt16(bytes.AsSpan(i * 2, 2), (ushort)values[i]);
            }
            else
            {
                WriteUInt32(bytes.AsSpan(i * 4, 4), values[i]);
            }
        }

        return bytes;
    }

    private void WriteHeader(List<byte> output)
    {
        if (LittleEndian)
        {
            output.Add((byte)'I');
            output.Add((byte)'I');
        }
        else
        {
            output.Add((byte)'M');
            output.Add((byte)'M');
        }

        WriteUInt16(output, 42);
        WriteUInt32(output, 8);
    }

    private void WriteUInt16(List<byte> output, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        WriteUInt16(buffer, value);
        output.AddRange(buffer.ToArray());
    }

    private void WriteUInt32(List<byte> output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        WriteUInt32(buffer, value);
        output.AddRange(buffer.ToArray());
    }

    private void WriteUInt16(Span<byte> destination, ushort value)
    {
        if (LittleEndian)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        }
    }

    private void WriteUInt32(Span<byte> destination, uint value)
    {
        if (LittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        }
    }
}
