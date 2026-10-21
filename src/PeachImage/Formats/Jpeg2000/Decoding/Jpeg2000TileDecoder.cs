using System.Buffers;
using PeachImage.Formats.Jpeg2000.Internal;

namespace PeachImage.Formats.Jpeg2000.Decoding;

/// <summary>Decodes one tile of a JPEG 2000 codestream into component sample arrays (T.800 Annexes B, D, E, F, G).</summary>
internal sealed class J2kTileDecoder
{
    private readonly J2kCodestream _codestream;
    private readonly J2kSiz _siz;
    private readonly int _tileIndex;
    private readonly int _reduce;

    private J2kTileComponent[] _components = [];
    private J2kCod _cod = null!;
    private List<J2kProgressionChange>? _poc;

    public J2kTileDecoder(J2kCodestream codestream, int tileIndex, int reduce)
    {
        _codestream = codestream;
        _siz = codestream.Siz;
        _tileIndex = tileIndex;
        _reduce = reduce;
    }

    public int TileX0 { get; private set; }

    public int TileY0 { get; private set; }

    public int TileX1 { get; private set; }

    public int TileY1 { get; private set; }

    public J2kTileComponent[] Components => _components;

    public bool UsesMultipleComponentTransform => _cod.Mct;

    /// <summary>Whether the first three components use the reversible colour transform.</summary>
    public bool ReversibleTransform => _components.Length > 0 && _components[0].Coding.Reversible;

    public void Decode(IReadOnlyList<J2kTilePart> parts)
    {
        int tilesWide = _siz.TilesWide;
        int p = _tileIndex % tilesWide;
        int q = _tileIndex / tilesWide;
        TileX0 = Math.Max(_siz.XTOsiz + (p * _siz.XTsiz), _siz.XOsiz);
        TileY0 = Math.Max(_siz.YTOsiz + (q * _siz.YTsiz), _siz.YOsiz);
        TileX1 = (int)Math.Min((long)_siz.XTOsiz + ((p + 1L) * _siz.XTsiz), _siz.Xsiz);
        TileY1 = (int)Math.Min((long)_siz.YTOsiz + ((q + 1L) * _siz.YTsiz), _siz.Ysiz);

        ResolveParameters(parts);
        int minimumLevels = _components.Min(c => c.Coding.Levels);
        int reduce = Math.Min(_reduce, minimumLevels);

        byte[]? rented = null;
        try
        {
            ReadOnlyMemory<byte> data = AssembleTileData(parts, ref rented);
            _tileMemory = data;
            ReadOnlyMemory<byte> packedHeaders = AssemblePackedHeaders(parts);
            ReadPackets(data.Span, packedHeaders.Span, packedHeaders.Length > 0);
            DecodeBlocksAndReconstruct(reduce);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private void ResolveParameters(IReadOnlyList<J2kTilePart> parts)
    {
        var main = _codestream.Main;
        var tileSets = parts.Select(part => part.Header).ToList();
        J2kCod? tileCod = tileSets.Select(s => s.Cod).FirstOrDefault(c => c is not null);
        J2kQuantization? tileQcd = tileSets.Select(s => s.Qcd).FirstOrDefault(c => c is not null);
        _cod = tileCod ?? main.Cod!;
        _poc = tileSets.Select(s => s.Poc).FirstOrDefault(c => c is not null) ?? main.Poc;

        int count = _siz.Components.Length;
        _components = new J2kTileComponent[count];
        for (int c = 0; c < count; c++)
        {
            var coding = tileSets.Select(s => s.Coc[c]).FirstOrDefault(x => x is not null)
                ?? (tileCod?.Coding)
                ?? main.Coc[c]
                ?? main.Cod!.Coding;
            var quantization = tileSets.Select(s => s.Qcc[c]).FirstOrDefault(x => x is not null)
                ?? tileQcd
                ?? main.Qcc[c]
                ?? main.Qcd!;
            int roi = tileSets.Select(s => s.Rgn[c]).FirstOrDefault(x => x is not null) ?? main.Rgn[c] ?? 0;
            _components[c] = J2kTileComponent.Build(_siz, c, TileX0, TileY0, TileX1, TileY1, coding, quantization, roi);
        }
    }

    private static ReadOnlyMemory<byte> AssembleTileData(IReadOnlyList<J2kTilePart> parts, ref byte[]? rented)
    {
        if (parts.Count == 1)
        {
            return parts[0].Data;
        }

        long total = parts.Sum(p => (long)p.Data.Length);
        rented = ArrayPool<byte>.Shared.Rent((int)total);
        int offset = 0;
        foreach (var part in parts)
        {
            part.Data.Span.CopyTo(rented.AsSpan(offset));
            offset += part.Data.Length;
        }

        return rented.AsMemory(0, (int)total);
    }

    private static ReadOnlyMemory<byte> AssemblePackedHeaders(IReadOnlyList<J2kTilePart> parts)
    {
        var pieces = new List<byte[]>();
        foreach (var part in parts)
        {
            if (!part.PpmHeaders.IsEmpty)
            {
                pieces.Add(part.PpmHeaders.ToArray());
            }

            foreach (var segment in part.PptSegments.OrderBy(s => s.Index))
            {
                pieces.Add(segment.Data);
            }
        }

        return pieces.Count == 0 ? ReadOnlyMemory<byte>.Empty : pieces.SelectMany(p => p).ToArray();
    }

    // ------------------------------------------------------------------ packet sequencing

    private readonly record struct PacketId(int Layer, int Resolution, int Component, int Precinct);

    private IEnumerable<PacketId> EnumeratePackets()
    {
        var progressions = new List<J2kProgressionChange>();
        int maxResolutions = _components.Max(c => c.ResolutionCount);
        if (_poc is { Count: > 0 })
        {
            progressions.AddRange(_poc);
        }
        else
        {
            progressions.Add(new J2kProgressionChange(0, 0, _cod.Layers, maxResolutions, _components.Length, _cod.Progression));
        }

        foreach (var change in progressions)
        {
            int layerEnd = Math.Min(change.LayerEnd, _cod.Layers);
            int resEnd = Math.Min(change.ResolutionEnd, maxResolutions);
            int compEnd = Math.Min(change.ComponentEnd, _components.Length);
            foreach (var packet in EnumerateOrder(change.Order, change.ResolutionStart, resEnd, change.ComponentStart, compEnd, layerEnd))
            {
                yield return packet;
            }
        }
    }

    private IEnumerable<PacketId> EnumerateOrder(int order, int resStart, int resEnd, int compStart, int compEnd, int layerEnd)
    {
        switch (order)
        {
            case 0: // LRCP
                for (int l = 0; l < layerEnd; l++)
                {
                    for (int r = resStart; r < resEnd; r++)
                    {
                        for (int c = compStart; c < compEnd; c++)
                        {
                            if (r >= _components[c].ResolutionCount)
                            {
                                continue;
                            }

                            int count = _components[c].Resolutions[r].PrecinctsWide * _components[c].Resolutions[r].PrecinctsHigh;
                            for (int p = 0; p < count; p++)
                            {
                                yield return new PacketId(l, r, c, p);
                            }
                        }
                    }
                }

                break;

            case 1: // RLCP
                for (int r = resStart; r < resEnd; r++)
                {
                    for (int l = 0; l < layerEnd; l++)
                    {
                        for (int c = compStart; c < compEnd; c++)
                        {
                            if (r >= _components[c].ResolutionCount)
                            {
                                continue;
                            }

                            int count = _components[c].Resolutions[r].PrecinctsWide * _components[c].Resolutions[r].PrecinctsHigh;
                            for (int p = 0; p < count; p++)
                            {
                                yield return new PacketId(l, r, c, p);
                            }
                        }
                    }
                }

                break;

            case 2: // RPCL
                for (int r = resStart; r < resEnd; r++)
                {
                    long dx = 0;
                    long dy = 0;
                    MinimumSteps(r, r + 1, compStart, compEnd, ref dx, ref dy);
                    if (dx == 0 || dy == 0)
                    {
                        continue;
                    }

                    for (long y = TileY0; y < TileY1; y += dy - (y % dy))
                    {
                        for (long x = TileX0; x < TileX1; x += dx - (x % dx))
                        {
                            for (int c = compStart; c < compEnd; c++)
                            {
                                if (TryPrecinctAt(c, r, x, y, out int precinct))
                                {
                                    for (int l = 0; l < layerEnd; l++)
                                    {
                                        yield return new PacketId(l, r, c, precinct);
                                    }
                                }
                            }
                        }
                    }
                }

                break;

            case 3: // PCRL
            {
                long dx = 0;
                long dy = 0;
                MinimumSteps(resStart, resEnd, compStart, compEnd, ref dx, ref dy);
                if (dx == 0 || dy == 0)
                {
                    break;
                }

                for (long y = TileY0; y < TileY1; y += dy - (y % dy))
                {
                    for (long x = TileX0; x < TileX1; x += dx - (x % dx))
                    {
                        for (int c = compStart; c < compEnd; c++)
                        {
                            for (int r = resStart; r < Math.Min(resEnd, _components[c].ResolutionCount); r++)
                            {
                                if (TryPrecinctAt(c, r, x, y, out int precinct))
                                {
                                    for (int l = 0; l < layerEnd; l++)
                                    {
                                        yield return new PacketId(l, r, c, precinct);
                                    }
                                }
                            }
                        }
                    }
                }

                break;
            }

            default: // CPRL
                for (int c = compStart; c < compEnd; c++)
                {
                    long dx = 0;
                    long dy = 0;
                    MinimumSteps(resStart, resEnd, c, c + 1, ref dx, ref dy);
                    if (dx == 0 || dy == 0)
                    {
                        continue;
                    }

                    for (long y = TileY0; y < TileY1; y += dy - (y % dy))
                    {
                        for (long x = TileX0; x < TileX1; x += dx - (x % dx))
                        {
                            for (int r = resStart; r < Math.Min(resEnd, _components[c].ResolutionCount); r++)
                            {
                                if (TryPrecinctAt(c, r, x, y, out int precinct))
                                {
                                    for (int l = 0; l < layerEnd; l++)
                                    {
                                        yield return new PacketId(l, r, c, precinct);
                                    }
                                }
                            }
                        }
                    }
                }

                break;
        }
    }

    private void MinimumSteps(int resStart, int resEnd, int compStart, int compEnd, ref long dx, ref long dy)
    {
        for (int c = compStart; c < compEnd; c++)
        {
            var info = _siz.Components[c];
            var component = _components[c];
            for (int r = resStart; r < Math.Min(resEnd, component.ResolutionCount); r++)
            {
                var res = component.Resolutions[r];
                int levelNo = component.ResolutionCount - 1 - r;
                long stepX = (long)info.XRsiz << (res.PrecinctWidthExponent + levelNo);
                long stepY = (long)info.YRsiz << (res.PrecinctHeightExponent + levelNo);
                dx = dx == 0 ? stepX : Math.Min(dx, stepX);
                dy = dy == 0 ? stepY : Math.Min(dy, stepY);
            }
        }
    }

    private bool TryPrecinctAt(int c, int r, long x, long y, out int precinct)
    {
        precinct = 0;
        var component = _components[c];
        if (r >= component.ResolutionCount)
        {
            return false;
        }

        var info = _siz.Components[c];
        var res = component.Resolutions[r];
        if (res.PrecinctsWide == 0 || res.PrecinctsHigh == 0 || res.X1 == res.X0 || res.Y1 == res.Y0)
        {
            return false;
        }

        int levelNo = component.ResolutionCount - 1 - r;
        int ppx = res.PrecinctWidthExponent;
        int ppy = res.PrecinctHeightExponent;
        long rpx = ppx + levelNo;
        long rpy = ppy + levelNo;
        bool yOk = (y % ((long)info.YRsiz << (int)rpy) == 0) || (y == TileY0 && (((long)res.Y0 << levelNo) % (1L << (int)rpy)) != 0);
        bool xOk = (x % ((long)info.XRsiz << (int)rpx) == 0) || (x == TileX0 && (((long)res.X0 << levelNo) % (1L << (int)rpx)) != 0);
        if (!yOk || !xOk)
        {
            return false;
        }

        long px = (CeilDivLong(x, (long)info.XRsiz << levelNo) >> ppx) - (res.X0 >> ppx);
        long py = (CeilDivLong(y, (long)info.YRsiz << levelNo) >> ppy) - (res.Y0 >> ppy);
        if (px < 0 || py < 0 || px >= res.PrecinctsWide || py >= res.PrecinctsHigh)
        {
            return false;
        }

        precinct = (int)(px + (py * res.PrecinctsWide));
        return true;
    }

    private static long CeilDivLong(long a, long b) => (a + b - 1) / b;

    // ------------------------------------------------------------------ packets

    private void ReadPackets(ReadOnlySpan<byte> data, ReadOnlySpan<byte> packedHeaders, bool packed)
    {
        int bodyPosition = 0;
        int headerPosition = 0;
        try
        {
            foreach (var id in EnumeratePackets())
            {
                var component = _components[id.Component];
                var res = component.Resolutions[id.Resolution];
                if (id.Layer < res.NextLayer[id.Precinct])
                {
                    continue; // Already read through an earlier progression change.
                }

                res.NextLayer[id.Precinct] = id.Layer + 1;
                ReadPacket(data, packedHeaders, packed, ref bodyPosition, ref headerPosition, component, res, id);
            }
        }
        catch (J2kTruncatedException)
        {
            // Decode whatever was read.
        }
    }

    private void ReadPacket(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> packedHeaders,
        bool packed,
        ref int bodyPosition,
        ref int headerPosition,
        J2kTileComponent component,
        J2kResolution res,
        PacketId id)
    {
        int packetStart = bodyPosition;
        if (_cod.Sop && bodyPosition + 6 <= data.Length && data[bodyPosition] == 0xFF && data[bodyPosition + 1] == 0x91)
        {
            bodyPosition += 6;
        }

        var headerSource = packed ? packedHeaders : data;
        int start = packed ? headerPosition : bodyPosition;
        var reader = new J2kPacketBitReader(headerSource, start);
        _pending.Clear();

        if (reader.ReadBit() != 0)
        {
            foreach (var band in res.Bands)
            {
                var precinctBand = band.Precincts[id.Precinct];
                for (int index = 0; index < precinctBand.Blocks.Length; index++)
                {
                    var block = precinctBand.Blocks[index];
                    int bx = index % precinctBand.BlocksWide;
                    int by = index / precinctBand.BlocksWide;
                    bool isIncluded;
                    if (!block.Included)
                    {
                        isIncluded = precinctBand.Inclusion!.Decode(ref reader, bx, by, id.Layer + 1);
                    }
                    else
                    {
                        isIncluded = reader.ReadBit() != 0;
                    }

                    if (!isIncluded)
                    {
                        continue;
                    }

                    if (!block.Included)
                    {
                        int zero = 1;
                        while (!precinctBand.ZeroBitPlanes!.Decode(ref reader, bx, by, zero))
                        {
                            zero++;
                            if (zero > 100)
                            {
                                throw new Jpeg2000DecodingException("JPEG 2000 packet header has an invalid zero bit-plane count.");
                            }
                        }

                        block.BitPlanes = band.BitPlanes + 1 - zero;
                        block.Included = true;
                    }

                    int newPasses = ReadPassCount(ref reader);
                    while (reader.ReadBit() != 0)
                    {
                        block.LengthBits++;
                        if (block.LengthBits > 32)
                        {
                            throw new Jpeg2000DecodingException("JPEG 2000 packet header has an invalid length indicator.");
                        }
                    }

                    int style = component.Coding.CodeBlockStyle;
                    var segment = CurrentSegment(block, style);
                    int remaining = newPasses;
                    while (remaining > 0)
                    {
                        if (segment.Passes >= segment.MaxPasses)
                        {
                            segment = NewSegment(block, style);
                        }

                        int take = Math.Min(segment.MaxPasses - segment.Passes, remaining);
                        int bits = block.LengthBits + FloorLog2(take);
                        int length = reader.ReadBits(Math.Min(bits, 31));
                        _pending.Add((segment, length));
                        segment.Passes += take;
                        remaining -= take;
                    }

                }
            }
        }

        reader.Align();
        int end = reader.Position;
        int headerEndWithEph = end;
        if (packed)
        {
            headerPosition = end;
            if (_cod.Eph && headerPosition + 2 <= packedHeaders.Length && packedHeaders[headerPosition] == 0xFF && packedHeaders[headerPosition + 1] == 0x92)
            {
                headerPosition += 2;
            }
        }
        else
        {
            bodyPosition = end;
            if (_cod.Eph && bodyPosition + 2 <= data.Length && data[bodyPosition] == 0xFF && data[bodyPosition + 1] == 0x92)
            {
                bodyPosition += 2;
            }
            headerEndWithEph = bodyPosition;
        }

        foreach (var (segment, length) in _pending)
        {
                if (length > data.Length - bodyPosition)
                {
                    throw new J2kTruncatedException();
                }

                if (length > 0)
                {
                    segment.Chunks.Add(MemoryOfData(data, bodyPosition, length));
                    segment.Length += length;
                }

                bodyPosition += length;
        }

        PacketTrace?.Add((packetStart, packed ? -1 : start, packed ? -1 : headerEndWithEph, bodyPosition));
    }

    // The packet data lives in a span; chunks need Memory, so remember where the tile buffer is.
    private ReadOnlyMemory<byte> _tileMemory;

    /// <summary>When set, receives (packet start, header start, header end including EPH, packet end) for every packet read from unpacked tile data. Test support.</summary>
    internal List<(int PacketStart, int HeaderStart, int HeaderEnd, int PacketEnd)>? PacketTrace { get; set; }
    private readonly List<(J2kSegment Segment, int Length)> _pending = [];

    private ReadOnlyMemory<byte> MemoryOfData(ReadOnlySpan<byte> data, int offset, int length) =>
        _tileMemory.Slice(offset, length);

    private static J2kSegment CurrentSegment(J2kCodeBlock block, int style)
    {
        if (block.Segments.Count == 0)
        {
            return NewSegment(block, style);
        }

        return block.Segments[^1];
    }

    private static J2kSegment NewSegment(J2kCodeBlock block, int style)
    {
        int max;
        if ((style & J2kBlockDecoder.StyleTermAll) != 0)
        {
            max = 1;
        }
        else if ((style & J2kBlockDecoder.StyleBypass) != 0)
        {
            if (block.Segments.Count == 0)
            {
                max = 10;
            }
            else
            {
                int previous = block.Segments[^1].MaxPasses;
                max = previous is 1 or 10 ? 2 : 1;
            }
        }
        else
        {
            max = 109;
        }

        var segment = new J2kSegment { MaxPasses = max };
        block.Segments.Add(segment);
        return segment;
    }

    private static int ReadPassCount(ref J2kPacketBitReader reader)
    {
        if (reader.ReadBit() == 0)
        {
            return 1;
        }

        if (reader.ReadBit() == 0)
        {
            return 2;
        }

        int n = reader.ReadBits(2);
        if (n != 3)
        {
            return 3 + n;
        }

        n = reader.ReadBits(5);
        if (n != 31)
        {
            return 6 + n;
        }

        return 37 + reader.ReadBits(7);
    }

    private static int FloorLog2(int value)
    {
        int result = 0;
        while (value > 1)
        {
            value >>= 1;
            result++;
        }

        return result;
    }

    // ------------------------------------------------------------------ reconstruction

    private void DecodeBlocksAndReconstruct(int reduce)
    {
        foreach (var component in _components)
        {
            int keepResolutions = component.ResolutionCount - reduce;
            var top = component.Resolutions[keepResolutions - 1];
            long pixels = (long)top.Width * top.Height;
            if (pixels > Jpeg2000DecodingLimits.MaxPixelCount)
            {
                throw new Jpeg2000DecodingException("JPEG 2000 tile is too large.");
            }

            if (component.Coding.Reversible)
            {
                component.IntegerData = ArrayPool<int>.Shared.Rent(Math.Max(1, (int)pixels));
                Array.Clear(component.IntegerData, 0, (int)pixels);
            }
            else
            {
                component.FloatData = ArrayPool<float>.Shared.Rent(Math.Max(1, (int)pixels));
                Array.Clear(component.FloatData, 0, (int)pixels);
            }
        }

        // Gather every code-block that has data, across components.
        var work = new List<(J2kTileComponent Component, J2kBand Band, J2kCodeBlock Block, int Stride)>();
        foreach (var component in _components)
        {
            int keepResolutions = component.ResolutionCount - reduce;
            int stride = component.Resolutions[keepResolutions - 1].Width;
            for (int r = 0; r < keepResolutions; r++)
            {
                foreach (var band in component.Resolutions[r].Bands)
                {
                    foreach (var precinct in band.Precincts)
                    {
                        foreach (var block in precinct.Blocks)
                        {
                            if (block.Included && block.Width > 0 && block.Height > 0)
                            {
                                work.Add((component, band, block, stride));
                            }
                        }
                    }
                }
            }
        }

        var scratchPool = new System.Collections.Concurrent.ConcurrentBag<J2kBlockScratch>();
        Parallel.For(
            0,
            work.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => scratchPool.TryTake(out var s) ? s : new J2kBlockScratch(),
            (index, _, scratch) =>
            {
                var (component, band, block, stride) = work[index];
                DecodeBlock(component, band, block, stride, scratch);
                return scratch;
            },
            scratch => scratchPool.Add(scratch));

        foreach (var component in _components)
        {
            int keepResolutions = component.ResolutionCount - reduce;
            var sizes = new J2kResolutionSize[keepResolutions];
            for (int r = 0; r < keepResolutions; r++)
            {
                var res = component.Resolutions[r];
                sizes[r] = new J2kResolutionSize(res.Width, res.Height, res.X0, res.Y0);
            }

            int stride = sizes[^1].Width;
            if (component.IntegerData is { } integers)
            {
                J2kDwt.Inverse53(integers, stride, sizes);
            }
            else
            {
                J2kDwt.Inverse97(component.FloatData!, stride, sizes);
            }
        }
    }

    private static void DecodeBlock(J2kTileComponent component, J2kBand band, J2kCodeBlock block, int stride, J2kBlockScratch scratch)
    {
        int segmentCount = block.Segments.Count;
        if (scratch.SegmentBuffer.Length < segmentCount)
        {
            scratch.SegmentBuffer = new J2kSegmentData[segmentCount];
            scratch.Rented = new byte[segmentCount][];
        }

        var segments = scratch.SegmentBuffer.AsSpan(0, segmentCount);
        var rented = scratch.Rented;
        Array.Clear(rented, 0, segmentCount);
        try
        {
            for (int i = 0; i < segmentCount; i++)
            {
                var segment = block.Segments[i];
                ReadOnlyMemory<byte> memory;
                if (segment.Chunks.Count == 1)
                {
                    memory = segment.Chunks[0];
                }
                else if (segment.Chunks.Count == 0)
                {
                    memory = ReadOnlyMemory<byte>.Empty;
                }
                else
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(segment.Length);
                    rented[i] = buffer;
                    int offset = 0;
                    foreach (var chunk in segment.Chunks)
                    {
                        chunk.Span.CopyTo(buffer.AsSpan(offset));
                        offset += chunk.Length;
                    }

                    memory = buffer.AsMemory(0, segment.Length);
                }

                segments[i] = new J2kSegmentData(memory, segment.Passes);
            }

            var decoder = new J2kBlockDecoder(scratch);
            decoder.Decode(block.Width, block.Height, band.Orientation, component.Coding.CodeBlockStyle, block.BitPlanes, segments);
            var values = decoder.Data;

            int roiShift = component.RoiShift;
            int originX = band.OffsetX + (block.X0 - band.X0);
            int originY = band.OffsetY + (block.Y0 - band.Y0);
            int width = block.Width;
            int height = block.Height;

            if (component.IntegerData is { } integers)
            {
                for (int y = 0; y < height; y++)
                {
                    var destination = integers.AsSpan(((originY + y) * stride) + originX, width);
                    var source = values.Slice(y * width, width);
                    if (roiShift == 0)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            destination[x] = source[x] / 2;
                        }
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            destination[x] = UnshiftRoi(source[x], roiShift) / 2;
                        }
                    }
                }
            }
            else
            {
                float scale = band.StepSize * 0.5f;
                var floats = component.FloatData!;
                for (int y = 0; y < height; y++)
                {
                    var destination = floats.AsSpan(((originY + y) * stride) + originX, width);
                    var source = values.Slice(y * width, width);
                    if (roiShift == 0)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            destination[x] = source[x] * scale;
                        }
                    }
                    else
                    {
                        for (int x = 0; x < width; x++)
                        {
                            destination[x] = UnshiftRoi(source[x], roiShift) * scale;
                        }
                    }
                }
            }
        }
        finally
        {
            for (int i = 0; i < segmentCount; i++)
            {
                if (rented[i] is { } buffer)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    rented[i] = null!;
                }
            }
        }
    }

    // Region-of-interest maxshift (T.800 H.1): coefficients at or above 2^shift belong to the ROI and are shifted back down.
    private static int UnshiftRoi(int value, int shift)
    {
        int magnitude = Math.Abs(value);
        if (magnitude >= (2 << shift))
        {
            magnitude >>= shift;
        }

        return value < 0 ? -magnitude : magnitude;
    }

    /// <summary>Hands the tile's packet buffer to the decoder so block chunks can refer to it.</summary>
    internal void SetTileMemory(ReadOnlyMemory<byte> memory) => _tileMemory = memory;

    /// <summary>Returns the pooled sample buffers of every component.</summary>
    public void Release()
    {
        foreach (var component in _components)
        {
            if (component.IntegerData is not null)
            {
                ArrayPool<int>.Shared.Return(component.IntegerData);
                component.IntegerData = null;
            }

            if (component.FloatData is not null)
            {
                ArrayPool<float>.Shared.Return(component.FloatData);
                component.FloatData = null;
            }
        }
    }
}
