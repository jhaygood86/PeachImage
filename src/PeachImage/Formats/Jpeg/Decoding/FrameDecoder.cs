using PeachImage.Formats.Jpeg.Decoding.ColorSpace;
using PeachImage.Formats.Jpeg.Entropy;
using PeachImage.Formats.Jpeg.Internal;
using PeachImage.Formats.Jpeg.Markers;
using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Parses a full JPEG bitstream (SOI through EOI): marker segments, quantization/Huffman tables, and every
/// scan's entropy-coded data, producing a <see cref="DecodedFrame"/> with fully-populated coefficient buffers.
/// Pixel reconstruction (dequantize/IDCT/upsample/color-convert) is a separate step; see <see cref="FrameReconstructor"/>.
/// </summary>
internal static class FrameDecoder
{
    private static readonly byte[] IccSignature = "ICC_PROFILE\0"u8.ToArray();

    /// <summary>
    /// Reads just enough of <paramref name="stream"/> to determine its frame header (dimensions, precision,
    /// component sampling), stopping as soon as a SOF marker is parsed without decoding any scan data.
    /// </summary>
    public static JpegFrameHeader IdentifyFrameHeader(Stream stream)
    {
        var source = new JpegByteSource(stream);
        var markerReader = new JpegMarkerReader(source);

        if (markerReader.NextMarker() != JpegMarker.Soi)
        {
            throw new JpegDecodingException("Not a JPEG file: missing SOI marker.");
        }

        while (true)
        {
            var marker = markerReader.NextMarker()
                ?? throw new JpegDecodingException("Unexpected end of stream while identifying a JPEG frame header.");

            switch (marker)
            {
                case JpegMarker.Sof0 or JpegMarker.Sof2:
                {
                    var frameHeader = JpegFrameHeader.Parse(ReadSegment(markerReader), isProgressive: marker == JpegMarker.Sof2);
                    ThrowIfDimensionsExceedLimits(frameHeader);
                    return frameHeader;
                }

                case JpegMarker.Sof1 or JpegMarker.Sof3 or JpegMarker.Sof5 or JpegMarker.Sof6 or JpegMarker.Sof7
                    or JpegMarker.Sof9 or JpegMarker.Sof10 or JpegMarker.Sof11 or JpegMarker.Sof13 or JpegMarker.Sof14 or JpegMarker.Sof15:
                    throw new JpegDecodingException($"Unsupported JPEG encoding mode (marker 0x{(byte)marker:X2}). Only baseline sequential and progressive DCT with Huffman coding are supported.");

                case JpegMarker.Sos:
                    throw new JpegDecodingException("Reached scan data before a frame header (SOF) was found.");

                case JpegMarker.Eoi:
                    throw new JpegDecodingException("Reached end of image before a frame header (SOF) was found.");

                default:
                    int length = markerReader.ReadSegmentLength();
                    markerReader.SkipSegment(length);
                    break;
            }
        }
    }

    /// <summary>Parses <paramref name="stream"/> as a JPEG bitstream.</summary>
    public static DecodedFrame Decode(Stream stream)
    {
        var source = new JpegByteSource(stream);
        var markerReader = new JpegMarkerReader(source);

        if (markerReader.NextMarker() != JpegMarker.Soi)
        {
            throw new JpegDecodingException("Not a JPEG file: missing SOI marker.");
        }

        var quantTables = new Dictionary<byte, JpegQuantizationTable>();
        var dcHuffmanSpecs = new Dictionary<byte, JpegHuffmanTableSpec>();
        var acHuffmanSpecs = new Dictionary<byte, JpegHuffmanTableSpec>();
        var metadata = new List<RawMetadataProfile>();
        var iccChunks = new SortedDictionary<byte, byte[]>();
        int iccChunkCount = 0;
        JpegAdobeSegment? adobe = null;

        JpegFrameHeader? frameHeader = null;
        ComponentDecodeState[]? components = null;
        int mcusAcross = 0;
        int mcusDown = 0;
        int restartInterval = 0;

        while (true)
        {
            var marker = markerReader.NextMarker()
                ?? throw new JpegDecodingException("Unexpected end of stream: missing EOI marker.");

            if (marker == JpegMarker.Eoi)
            {
                break;
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
                case JpegMarker.Sof2:
                {
                    if (frameHeader is not null)
                    {
                        throw new JpegDecodingException("Multiple SOF markers are not supported.");
                    }

                    frameHeader = JpegFrameHeader.Parse(ReadSegment(markerReader), isProgressive: marker == JpegMarker.Sof2);
                    ThrowIfDimensionsExceedLimits(frameHeader);
                    (components, mcusAcross, mcusDown) = BuildComponentPlans(frameHeader);
                    break;
                }

                case JpegMarker.Sof1 or JpegMarker.Sof3 or JpegMarker.Sof5 or JpegMarker.Sof6 or JpegMarker.Sof7
                    or JpegMarker.Sof9 or JpegMarker.Sof10 or JpegMarker.Sof11 or JpegMarker.Sof13 or JpegMarker.Sof14 or JpegMarker.Sof15:
                    throw new JpegDecodingException($"Unsupported JPEG encoding mode (marker 0x{(byte)marker:X2}). Only baseline sequential and progressive DCT with Huffman coding are supported.");

                case JpegMarker.App0:
                    metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Jfif, Data = ReadSegment(markerReader) });
                    break;

                case JpegMarker.App1:
                    metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = ReadSegment(markerReader) });
                    break;

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

                    DecodeScan(markerReader, source, frameHeader, components, dcHuffmanSpecs, acHuffmanSpecs, restartInterval, mcusAcross, mcusDown);
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

        foreach (var component in components)
        {
            if (!quantTables.TryGetValue(component.Frame.QuantizationTableId, out var table))
            {
                throw new JpegDecodingException($"Component references undefined quantization table {component.Frame.QuantizationTableId}.");
            }

            component.QuantizationTable = table;
        }

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

            metadata.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = [.. assembled] });
        }

        var (colorSpace, isAdobeInverted) = ColorSpaceResolver.Resolve(frameHeader.Components.Length, adobe);

        return new DecodedFrame
        {
            FrameHeader = frameHeader,
            Components = components,
            ColorSpace = colorSpace,
            IsAdobeInverted = isAdobeInverted,
            Metadata = metadata,
        };
    }

    private static void DecodeScan(
        JpegMarkerReader markerReader,
        JpegByteSource source,
        JpegFrameHeader frameHeader,
        ComponentDecodeState[] components,
        Dictionary<byte, JpegHuffmanTableSpec> dcHuffmanSpecs,
        Dictionary<byte, JpegHuffmanTableSpec> acHuffmanSpecs,
        int restartInterval,
        int mcusAcross,
        int mcusDown)
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
                if (!dcHuffmanSpecs.TryGetValue(scanComponent.DcTableSelector, out var dcSpec))
                {
                    throw new JpegDecodingException($"Scan references undefined DC Huffman table {scanComponent.DcTableSelector}.");
                }

                dcTables[i] = HuffmanDecodingTable.Build(dcSpec);
            }

            if (needsAc)
            {
                if (!acHuffmanSpecs.TryGetValue(scanComponent.AcTableSelector, out var acSpec))
                {
                    throw new JpegDecodingException($"Scan references undefined AC Huffman table {scanComponent.AcTableSelector}.");
                }

                acTables[i] = HuffmanDecodingTable.Build(acSpec);
            }
        }

        var entropy = new JpegEntropyReader(source);
        var restart = new RestartController(restartInterval);

        if (!frameHeader.IsProgressive)
        {
            BaselineScanDecoder.DecodeScan(entropy, scanComponents, dcTables, acTables, restart, mcusAcross, mcusDown);
        }
        else
        {
            ProgressiveScanDecoder.DecodeScan(entropy, scanHeader, scanComponents, dcTables, acTables, restart, mcusAcross, mcusDown);
        }
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

    private static (ComponentDecodeState[] Components, int McusAcross, int McusDown) BuildComponentPlans(JpegFrameHeader frameHeader)
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
                Coefficients = new JpegCoefficientBuffer(blocksWide, blocksHigh),
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
