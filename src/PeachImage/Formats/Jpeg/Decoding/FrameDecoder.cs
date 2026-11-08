using PeachImage.Formats.Shared.Parallelism;
using PeachImage.Formats.Jpeg.Decoding.ColorSpace;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Internal;
using PeachImage.Formats.Jpeg.Markers;
using PeachImage.Formats.Jpeg.Markers.Segments;
using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Parses a full JPEG bitstream (SOI through EOI): marker segments, quantization/Huffman tables, and every
/// scan's entropy-coded data, producing a <see cref="DecodedFrame"/> with fully-populated coefficient buffers.
/// Pixel reconstruction (dequantize/IDCT/upsample/color-convert) is a separate step; see <see cref="FrameReconstructor"/>.
/// </summary>
internal static class FrameDecoder
{
    private static ReadOnlySpan<byte> XmpSignature => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static readonly byte[] ExifSignature = "Exif\0\0"u8.ToArray();
    private static readonly byte[] IccSignature = "ICC_PROFILE\0"u8.ToArray();

    /// <summary>
    /// Reads just enough of <paramref name="stream"/> to determine its frame header (dimensions, precision,
    /// component sampling) and resolved color space, stopping as soon as a SOF marker is parsed without
    /// decoding any scan data. Also reports the EXIF orientation of an APP1 segment seen before the SOF marker
    /// (the norm), or <see cref="ImageOrientation.Normal"/> when there is none.
    /// </summary>
    public static (JpegFrameHeader FrameHeader, JpegColorSpace ColorSpace, bool IsAdobeInverted, ImageOrientation Orientation) IdentifyFrameHeader(Stream stream)
    {
        long streamStart = stream.CanSeek ? stream.Position : -1;
        var source = new JpegByteSource(stream);
        var markerReader = new JpegMarkerReader(source);

        if (markerReader.NextMarker() != JpegMarker.Soi)
        {
            throw new JpegDecodingException("Not a JPEG file: missing SOI marker.");
        }

        JpegAdobeSegment? adobe = null;
        var orientation = ImageOrientation.Normal;
        bool sawOrientation = false;

        while (true)
        {
            var marker = markerReader.NextMarker()
                ?? throw new JpegDecodingException("Unexpected end of stream while identifying a JPEG frame header.");

            switch (marker)
            {
                case JpegMarker.Sof0 or JpegMarker.Sof1 or JpegMarker.Sof2:
                {
                    var frameHeader = JpegFrameHeader.Parse(ReadSegment(markerReader), isProgressive: marker == JpegMarker.Sof2);
                    ThrowIfDimensionsExceedLimits(frameHeader);
                    var (colorSpace, isAdobeInverted) = ColorSpaceResolver.Resolve(frameHeader.Components.Length, adobe);
                    return (frameHeader, colorSpace, isAdobeInverted, orientation);
                }

                case JpegMarker.Sof3 or JpegMarker.Sof5 or JpegMarker.Sof6 or JpegMarker.Sof7
                    or JpegMarker.Sof9 or JpegMarker.Sof10 or JpegMarker.Sof11 or JpegMarker.Sof13 or JpegMarker.Sof14 or JpegMarker.Sof15:
                    throw new JpegDecodingException($"Unsupported JPEG encoding mode (marker 0x{(byte)marker:X2}). Only baseline/extended sequential and progressive DCT with Huffman coding are supported.");

                case JpegMarker.Sos:
                    throw new JpegDecodingException("Reached scan data before a frame header (SOF) was found.");

                case JpegMarker.Eoi:
                    throw new JpegDecodingException("Reached end of image before a frame header (SOF) was found.");

                case JpegMarker.App1:
                {
                    var payload = ReadSegment(markerReader);
                    if (!sawOrientation && payload.AsSpan().StartsWith(ExifSignature))
                    {
                        sawOrientation = true;
                        orientation = ExifOrientationReader.Read(payload);
                    }

                    break;
                }

                case JpegMarker.App14:
                {
                    var payload = ReadSegment(markerReader);
                    if (JpegAdobeSegment.TryParse(payload, out var segment))
                    {
                        adobe = segment;
                    }

                    break;
                }

                default:
                    int length = markerReader.ReadSegmentLength();
                    markerReader.SkipSegment(length);
                    break;
            }
        }
    }

    /// <summary>
    /// Parses <paramref name="stream"/> as a JPEG bitstream. <paramref name="maxScanCount"/> defaults to the
    /// production <see cref="JpegDecodingLimits.MaxScanCount"/> cap and is exposed as a parameter only so
    /// tests can exercise the cap without needing to construct a file with hundreds of scans.
    /// </summary>
    /// <param name="stream">The JPEG bitstream.</param>
    /// <param name="maxScanCount">The maximum number of scans to accept.</param>
    /// <param name="streamBaseline">
    /// When set, a baseline frame whose first scan carries every component is decoded in streaming mode: coefficients are
    /// inverse-transformed batch by batch as they are decoded, using these parallelism settings, and only the resulting planes
    /// are kept (<see cref="DecodedFrame.StreamedPlanes"/>). Anything else decodes as usual. Only valid when the frame will be
    /// reconstructed at full size.
    /// </param>
    /// <param name="progressFrameAvailable">
    /// When set and the frame is progressive, called with a snapshot of the frame as accumulated so far after every scan
    /// except the last, starting once DC has been decoded for every component (earlier scans carry no usable picture).
    /// The snapshot shares the live coefficient buffers, so the callback may read them (for example to reconstruct pixels)
    /// but must neither modify nor return them, and must not retain the frame past the call. Because the last scan is only
    /// known once EOI is reached, a snapshot is delivered when the next marker after its scan is seen, so quantization
    /// tables redefined between scans have not yet been applied to it. Baseline frames never invoke it.
    /// </param>
    public static DecodedFrame Decode(Stream stream, int maxScanCount = JpegDecodingLimits.MaxScanCount, Action<DecodedFrame>? progressFrameAvailable = null, ParallelSettings? streamBaseline = null)
    {
        var streaming = new StreamingState { Requested = streamBaseline is not null, Parallel = streamBaseline ?? default };
        long streamStart = stream.CanSeek ? stream.Position : -1;
        var source = new JpegByteSource(stream);
        var markerReader = new JpegMarkerReader(source);

        if (markerReader.NextMarker() != JpegMarker.Soi)
        {
            throw new JpegDecodingException("Not a JPEG file: missing SOI marker.");
        }

        var quantTables = new Dictionary<byte, JpegQuantizationTable>();
        var dcHuffmanSpecs = new Dictionary<byte, JpegHuffmanTableSpec>();
        var acHuffmanSpecs = new Dictionary<byte, JpegHuffmanTableSpec>();

        // Built tables are cached across every scan in the frame, not just within one — Cb/Cr (almost always)
        // share table selectors both within a scan and across scans, so without this a multi-scan progressive
        // file rebuilds the same 1024-entry fast-lookahead table over and over for no reason. A DHT segment
        // that redefines a table id invalidates any cached entry for that id (see the Dht case below).
        var dcTableCache = new Dictionary<byte, HuffmanDecodingTable>();
        var acTableCache = new Dictionary<byte, HuffmanDecodingTable>();

        var metadata = new List<RawMetadataProfile>();
        var iccChunks = new SortedDictionary<byte, byte[]>();
        int iccChunkCount = 0;
        JpegAdobeSegment? adobe = null;

        JpegFrameHeader? frameHeader = null;
        ComponentDecodeState[]? components = null;
        int mcusAcross = 0;
        int mcusDown = 0;
        int restartInterval = 0;
        int scanCount = 0;
        int componentsWithDc = 0;
        bool previewPending = false;

        while (true)
        {
            var marker = markerReader.NextMarker()
                ?? throw new JpegDecodingException("Unexpected end of stream: missing EOI marker.");

            if (marker == JpegMarker.Eoi)
            {
                break;
            }

            if (previewPending)
            {
                // A marker other than EOI follows the previous scan, so that scan was not the last one.
                previewPending = false;
                BindQuantizationTables(components!, quantTables);
                var (previewColorSpace, previewInverted) = ColorSpaceResolver.Resolve(frameHeader!.Components.Length, adobe);
                progressFrameAvailable!(new DecodedFrame
                {
                    FrameHeader = frameHeader,
                    Components = components!,
                    ColorSpace = previewColorSpace,
                    IsAdobeInverted = previewInverted,
                    Metadata = BuildMetadata(metadata, iccChunks, iccChunkCount),
                });
            }

            switch (marker)
            {
                case JpegMarker.Dqt:
                    foreach (var table in JpegQuantizationTable.ParseAll(ReadSegment(markerReader)))
                    {
                        quantTables[table.Id] = table;
                    }

                    break;

                case JpegMarker.Dht:
                    foreach (var spec in JpegHuffmanTableSpec.ParseAll(ReadSegment(markerReader)))
                    {
                        (spec.TableClass == 0 ? dcHuffmanSpecs : acHuffmanSpecs)[spec.Id] = spec;

                        // A redefined table id invalidates whatever was already built and cached for it.
                        (spec.TableClass == 0 ? dcTableCache : acTableCache).Remove(spec.Id);
                    }

                    break;

                case JpegMarker.Dri:
                {
                    var payload = ReadSegment(markerReader);
                    if (payload.Length != 2)
                    {
                        throw new JpegDecodingException("Invalid DRI segment length.");
                    }

                    restartInterval = (payload[0] << 8) | payload[1];
                    break;
                }

                case JpegMarker.Sof0:
                case JpegMarker.Sof1:
                case JpegMarker.Sof2:
                {
                    if (frameHeader is not null)
                    {
                        throw new JpegDecodingException("Multiple SOF markers are not supported.");
                    }

                    frameHeader = JpegFrameHeader.Parse(ReadSegment(markerReader), isProgressive: marker == JpegMarker.Sof2);
                    ThrowIfDimensionsExceedLimits(frameHeader);
                    ThrowIfStreamCannotHoldBaselineFrame(frameHeader, stream, streamStart, source.BytePosition);
                    // Streaming is for baseline frames only; a progressive frame keeps full coefficient buffers from the start.
                    streaming.Requested &= !frameHeader.IsProgressive;
                    (components, mcusAcross, mcusDown) = BuildComponentPlans(frameHeader, deferCoefficients: streaming.Requested);
                    break;
                }

                case JpegMarker.Sof3 or JpegMarker.Sof5 or JpegMarker.Sof6 or JpegMarker.Sof7
                    or JpegMarker.Sof9 or JpegMarker.Sof10 or JpegMarker.Sof11 or JpegMarker.Sof13 or JpegMarker.Sof14 or JpegMarker.Sof15:
                    throw new JpegDecodingException($"Unsupported JPEG encoding mode (marker 0x{(byte)marker:X2}). Only baseline/extended sequential and progressive DCT with Huffman coding are supported.");

                case JpegMarker.App0:
                    metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Jfif, Data = ReadSegment(markerReader) });
                    break;

                case JpegMarker.App1:
                {
                    var payload = ReadSegment(markerReader);
                    if (payload.AsSpan().StartsWith(XmpSignature))
                    {
                        // An XMP packet shares APP1 with EXIF; the profile holds just the packet, as WebP and JPEG XL report it.
                        metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = payload[XmpSignature.Length..] });
                    }
                    else
                    {
                        metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = payload });
                    }

                    break;
                }

                case JpegMarker.App2:
                {
                    var payload = ReadSegment(markerReader);
                    if (payload.Length > 14 && payload.AsSpan(0, 12).SequenceEqual(IccSignature))
                    {
                        byte sequence = payload[12];
                        iccChunkCount = payload[13];
                        iccChunks[sequence] = payload[14..];
                    }
                    else
                    {
                        metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Unknown, Data = payload });
                    }

                    break;
                }

                case JpegMarker.App13:
                {
                    // Only Photoshop image-resource blocks (which carry IPTC) are kept; any other APP13 payload is skipped as before.
                    var payload = ReadSegment(markerReader);
                    if (payload.AsSpan().StartsWith("Photoshop 3.0\0"u8))
                    {
                        metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Iptc, Data = payload });
                    }

                    break;
                }

                case JpegMarker.App14:
                {
                    var payload = ReadSegment(markerReader);
                    if (JpegAdobeSegment.TryParse(payload, out var segment))
                    {
                        adobe = segment;
                    }

                    metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.AdobeApp14, Data = payload });
                    break;
                }

                case JpegMarker.Sos:
                {
                    if (frameHeader is null || components is null)
                    {
                        throw new JpegDecodingException("SOS marker encountered before a frame header (SOF).");
                    }

                    scanCount++;
                    if (scanCount > maxScanCount)
                    {
                        throw new JpegDecodingException($"JPEG frame contains more than {maxScanCount} scans, exceeding the maximum supported scan count.");
                    }

                    streaming.AssumedColorSpace = ColorSpaceResolver.Resolve(frameHeader.Components.Length, adobe).ColorSpace;
                    var scanHeader = DecodeScan(markerReader, source, frameHeader, components, dcHuffmanSpecs, acHuffmanSpecs, dcTableCache, acTableCache, restartInterval, mcusAcross, mcusDown, quantTables, streaming);
                    if (progressFrameAvailable is not null && frameHeader.IsProgressive)
                    {
                        if (scanHeader.SpectralStart == 0 && scanHeader.SuccessiveApproximationHigh == 0)
                        {
                            componentsWithDc += scanHeader.Components.Length;
                        }

                        previewPending = componentsWithDc >= components.Length;
                    }

                    break;
                }

                default:
                {
                    int length = markerReader.ReadSegmentLength();
                    markerReader.SkipSegment(length);
                    break;
                }
            }
        }

        if (frameHeader is null || components is null)
        {
            throw new JpegDecodingException("JPEG stream ended without a frame header.");
        }

        if (streaming.Requested && !streaming.BuffersAllocated)
        {
            // No scan ever ran: keep the usual all-zero coefficient buffers.
            AllocateCoefficients(components, mcusAcross, mcusDown);
            streaming.BuffersAllocated = true;
        }

        BindQuantizationTables(components, quantTables);

        var (colorSpace, isAdobeInverted) = ColorSpaceResolver.Resolve(frameHeader.Components.Length, adobe);

        return new DecodedFrame
        {
            FrameHeader = frameHeader,
            Components = components,
            ColorSpace = colorSpace,
            IsAdobeInverted = isAdobeInverted,
            Metadata = BuildMetadata(metadata, iccChunks, iccChunkCount),
            StreamedPlanes = streaming.Planes,
            StreamedOutput = streaming.Output,
            StreamedOutputColorSpace = streaming.OutputColorSpace,
        };
    }

    private static void BindQuantizationTables(ComponentDecodeState[] components, Dictionary<byte, JpegQuantizationTable> quantTables)
    {
        foreach (var component in components)
        {
            if (!quantTables.TryGetValue(component.Frame.QuantizationTableId, out var table))
            {
                throw new JpegDecodingException($"Component references undefined quantization table {component.Frame.QuantizationTableId}.");
            }

            component.QuantizationTable = table;
        }
    }

    /// <summary>The profiles captured so far plus, when ICC chunks were seen, the ICC profile assembled from them. Does not modify its inputs.</summary>
    private static List<RawMetadataProfile> BuildMetadata(List<RawMetadataProfile> metadata, SortedDictionary<byte, byte[]> iccChunks, int iccChunkCount)
    {
        var result = new List<RawMetadataProfile>(metadata);
        if (iccChunkCount > 0)
        {
            var assembled = new List<byte>();
            for (int sequence = 1; sequence <= iccChunkCount; sequence++)
            {
                if (iccChunks.TryGetValue((byte)sequence, out var chunk))
                {
                    assembled.AddRange(chunk);
                }
            }

            result.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = [.. assembled] });
        }

        return result;
    }

    private static JpegScanHeader DecodeScan(
        JpegMarkerReader markerReader,
        JpegByteSource source,
        JpegFrameHeader frameHeader,
        ComponentDecodeState[] components,
        Dictionary<byte, JpegHuffmanTableSpec> dcHuffmanSpecs,
        Dictionary<byte, JpegHuffmanTableSpec> acHuffmanSpecs,
        Dictionary<byte, HuffmanDecodingTable> dcTableCache,
        Dictionary<byte, HuffmanDecodingTable> acTableCache,
        int restartInterval,
        int mcusAcross,
        int mcusDown,
        Dictionary<byte, JpegQuantizationTable> quantTables,
        StreamingState streaming)
    {
        var scanHeader = JpegScanHeader.Parse(ReadSegment(markerReader));

        var scanComponents = new ComponentDecodeState[scanHeader.Components.Length];
        var dcTables = new HuffmanDecodingTable[scanHeader.Components.Length];
        var acTables = new HuffmanDecodingTable[scanHeader.Components.Length];

        bool needsDc = !frameHeader.IsProgressive || scanHeader.SpectralStart == 0;
        bool needsAc = !frameHeader.IsProgressive || scanHeader.SpectralStart > 0;

        for (int i = 0; i < scanHeader.Components.Length; i++)
        {
            var scanComponent = scanHeader.Components[i];
            scanComponents[i] = Array.Find(components, c => c.Frame.Id == scanComponent.ComponentSelector)
                ?? throw new JpegDecodingException($"Scan references undefined component id {scanComponent.ComponentSelector}.");

            if (needsDc)
            {
                if (!dcTableCache.TryGetValue(scanComponent.DcTableSelector, out var dcTable))
                {
                    if (!dcHuffmanSpecs.TryGetValue(scanComponent.DcTableSelector, out var dcSpec))
                    {
                        throw new JpegDecodingException($"Scan references undefined DC Huffman table {scanComponent.DcTableSelector}.");
                    }

                    dcTable = HuffmanDecodingTable.Build(dcSpec);
                    dcTableCache[scanComponent.DcTableSelector] = dcTable;
                }

                dcTables[i] = dcTable;
            }

            if (needsAc)
            {
                if (!acTableCache.TryGetValue(scanComponent.AcTableSelector, out var acTable))
                {
                    if (!acHuffmanSpecs.TryGetValue(scanComponent.AcTableSelector, out var acSpec))
                    {
                        throw new JpegDecodingException($"Scan references undefined AC Huffman table {scanComponent.AcTableSelector}.");
                    }

                    acTable = HuffmanDecodingTable.Build(acSpec);
                    acTableCache[scanComponent.AcTableSelector] = acTable;
                }

                acTables[i] = acTable;
            }
        }

        var entropy = new JpegEntropyReader(source);
        var restart = new RestartController(restartInterval);

        if (!frameHeader.IsProgressive)
        {
            if (streaming.Planes is not null)
            {
                throw new JpegDecodingException("A baseline frame already decoded by its first scan has a further scan.");
            }

            if (streaming.Requested && !streaming.BuffersAllocated)
            {
                if (CanStream(components, scanComponents, quantTables))
                {
                    BindQuantizationTables(components, quantTables);
                    streaming.Planes = StreamingBaselineScan.Decode(
                        entropy, components, scanComponents, dcTables, acTables, restart, mcusAcross, mcusDown, streaming.Parallel,
                        streaming.AssumedColorSpace, frameHeader.Width, frameHeader.Height, out var converted);
                    streaming.Output = converted;
                    streaming.OutputColorSpace = streaming.AssumedColorSpace;
                    streaming.BuffersAllocated = true;
                    return scanHeader;
                }

                AllocateCoefficients(components, mcusAcross, mcusDown);
                streaming.BuffersAllocated = true;
            }

            BaselineScanDecoder.DecodeScan(entropy, scanComponents, dcTables, acTables, restart, mcusAcross, mcusDown);
        }
        else
        {
            ProgressiveScanDecoder.DecodeScan(entropy, scanHeader, scanComponents, dcTables, acTables, restart, mcusAcross, mcusDown);
        }

        return scanHeader;
    }

    private static void ThrowIfDimensionsExceedLimits(JpegFrameHeader frameHeader)
    {
        long pixelCount = (long)frameHeader.Width * frameHeader.Height;
        if (pixelCount > JpegDecodingLimits.MaxPixelCount)
        {
            throw new JpegDecodingException(
                $"JPEG frame dimensions {frameHeader.Width}x{frameHeader.Height} ({pixelCount:N0} pixels) exceed the maximum supported pixel count ({JpegDecodingLimits.MaxPixelCount:N0}).");
        }
    }

    // A baseline block costs at least two bits (a DC code and an end-of-block code, one bit each at the very least), so a seekable stream
    // too short to hold the claimed block grid at one bit per block cannot be genuine. Refused before the coefficient buffers (about
    // 400 MB for a header claiming 8000x8000) are allocated. Progressive frames are exempt: an end-of-band run makes a block nearly free,
    // so they rely on MaxExpansionRatio instead.
    private static void ThrowIfStreamCannotHoldBaselineFrame(JpegFrameHeader frameHeader, Stream stream, long streamStart, long consumed)
    {
        if (frameHeader.IsProgressive || streamStart < 0)
        {
            return;
        }

        int hMax = 1;
        int vMax = 1;
        foreach (var c in frameHeader.Components)
        {
            hMax = Math.Max(hMax, c.HorizontalSamplingFactor);
            vMax = Math.Max(vMax, c.VerticalSamplingFactor);
        }

        long mcusAcross = ((long)frameHeader.Width + (8 * hMax) - 1) / (8 * hMax);
        long mcusDown = ((long)frameHeader.Height + (8 * vMax) - 1) / (8 * vMax);
        long blocks = 0;
        foreach (var c in frameHeader.Components)
        {
            blocks += mcusAcross * c.HorizontalSamplingFactor * mcusDown * c.VerticalSamplingFactor;
        }

        long remainingBytes = stream.Length - streamStart - consumed;
        if (blocks / 8 > remainingBytes)
        {
            throw new JpegDecodingException(
                $"JPEG frame {frameHeader.Width}x{frameHeader.Height} declares {blocks:N0} blocks, more than the {remainingBytes:N0} remaining bytes can hold; the header is inconsistent with the data.");
        }
    }

    /// <summary>Per-frame state for the optional streaming decode of a baseline frame.</summary>
    private sealed class StreamingState
    {
        public bool Requested { get; set; }

        public ParallelSettings Parallel { get; init; }

        /// <summary>Whether the components have their full-size coefficient buffers (or streaming took over, which needs none).</summary>
        public bool BuffersAllocated { get; set; }

        public byte[][]? Planes { get; set; }

        /// <summary>The finished image bytes when rows were converted during the scan, and the color space they were converted as.</summary>
        public byte[]? Output { get; set; }

        public JpegColorSpace OutputColorSpace { get; set; }

        /// <summary>The color space the frame resolves to given the markers seen when its scan starts.</summary>
        public JpegColorSpace AssumedColorSpace { get; set; }
    }

    /// <summary>True when one scan covers every component exactly once and every quantization table it needs is already known.</summary>
    private static bool CanStream(ComponentDecodeState[] components, ComponentDecodeState[] scanComponents, Dictionary<byte, JpegQuantizationTable> quantTables)
    {
        if (scanComponents.Length != components.Length)
        {
            return false;
        }

        foreach (var component in components)
        {
            if (!Array.Exists(scanComponents, c => ReferenceEquals(c, component)) || !quantTables.ContainsKey(component.Frame.QuantizationTableId))
            {
                return false;
            }
        }

        return true;
    }

    private static void AllocateCoefficients(ComponentDecodeState[] components, int mcusAcross, int mcusDown)
    {
        foreach (var component in components)
        {
            component.Coefficients = new JpegCoefficientBuffer(mcusAcross * component.BlocksPerMcuHorizontal, mcusDown * component.BlocksPerMcuVertical);
        }
    }

    private static (ComponentDecodeState[] Components, int McusAcross, int McusDown) BuildComponentPlans(JpegFrameHeader frameHeader, bool deferCoefficients)
    {
        int hMax = 1;
        int vMax = 1;
        foreach (var c in frameHeader.Components)
        {
            hMax = Math.Max(hMax, c.HorizontalSamplingFactor);
            vMax = Math.Max(vMax, c.VerticalSamplingFactor);
        }

        int mcuWidth = 8 * hMax;
        int mcuHeight = 8 * vMax;
        int mcusAcross = (frameHeader.Width + mcuWidth - 1) / mcuWidth;
        int mcusDown = (frameHeader.Height + mcuHeight - 1) / mcuHeight;

        var components = new ComponentDecodeState[frameHeader.Components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            var fc = frameHeader.Components[i];
            int blocksWide = mcusAcross * fc.HorizontalSamplingFactor;
            int blocksHigh = mcusDown * fc.VerticalSamplingFactor;

            int actualWidth = (int)Math.Ceiling(frameHeader.Width * (double)fc.HorizontalSamplingFactor / hMax);
            int actualHeight = (int)Math.Ceiling(frameHeader.Height * (double)fc.VerticalSamplingFactor / vMax);

            components[i] = new ComponentDecodeState
            {
                Frame = fc,
                BlocksPerMcuHorizontal = fc.HorizontalSamplingFactor,
                BlocksPerMcuVertical = fc.VerticalSamplingFactor,
                ActualWidthInSamples = actualWidth,
                ActualHeightInSamples = actualHeight,
                QuantizationTable = UnresolvedQuantizationTable,
                Coefficients = deferCoefficients ? new JpegCoefficientBuffer(0, 0) : new JpegCoefficientBuffer(blocksWide, blocksHigh),
            };
        }

        return (components, mcusAcross, mcusDown);
    }

    // Placeholder assigned at construction time and always overwritten once every DQT segment in the
    // stream has been seen (immediately after the marker-parsing loop above finishes); never used as-is.
    private static readonly JpegQuantizationTable UnresolvedQuantizationTable = new() { Id = 0, Values = new ushort[64] };

    private static byte[] ReadSegment(JpegMarkerReader markerReader)
    {
        int length = markerReader.ReadSegmentLength();
        var payload = new byte[length];
        markerReader.ReadSegmentBytes(payload);
        return payload;
    }
}
