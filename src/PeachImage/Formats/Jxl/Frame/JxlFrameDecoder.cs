using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Features;
using PeachImage.Formats.Jxl.Headers;
using PeachImage.Formats.Jxl.Jpeg;
using PeachImage.Formats.Jxl.Modular;
using PeachImage.Formats.Jxl.VarDct;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>
/// A decoded frame. Colour is either integer Modular channels (<see cref="ColorChannelCount"/> of them at the start of
/// <see cref="Image"/>) or, for XYB-coded frames, final RGB float planes in the output encoding (<see cref="ColorPlanes"/>);
/// extra channels (alpha and so on) follow in <see cref="Image"/>.
/// </summary>
internal sealed class JxlDecodedFrame : IDisposable
{
    public JxlDecodedFrame(JxlFrameHeader header, ModularImage image, int colorChannelCount)
    {
        Header = header;
        Image = image;
        ColorChannelCount = colorChannelCount;
    }

    public JxlFrameHeader Header { get; }

    public ModularImage Image { get; }

    /// <summary>Integer colour channels at the start of <see cref="Image"/>: 1 for gray, 3 for colour, 0 when <see cref="ColorPlanes"/> is used.</summary>
    public int ColorChannelCount { get; }

    /// <summary>Three float planes of display-referred RGB in [0, 1] (gray repeated), for XYB-coded frames.</summary>
    public float[][]? ColorPlanes { get; init; }

    public int ColorPlaneStride { get; init; }

    /// <summary>Total bytes of the codestream this frame occupied (header, TOC and all sections).</summary>
    public int ByteLength { get; init; }

    /// <summary>Set when this frame is a composited canvas that is larger than the coded frame (a cropped frame blended onto a background).</summary>
    public int? CanvasWidth { get; init; }

    public int? CanvasHeight { get; init; }

    public int Width => CanvasWidth ?? Header.Dimensions.XSizeUpsampled;

    public int Height => CanvasHeight ?? Header.Dimensions.YSizeUpsampled;

    /// <summary>Whether <see cref="ColorPlanes"/> were rented from the shared array pool and must be returned on disposal.</summary>
    public bool ColorPlanesPooled { get; init; }

    public void Dispose()
    {
        Image.Dispose();
        if (ColorPlanesPooled && ColorPlanes is { } planes)
        {
            for (int c = 0; c < planes.Length; c++)
            {
                bool shared = false;
                for (int p = 0; p < c; p++)
                {
                    shared |= ReferenceEquals(planes[p], planes[c]);
                }

                if (!shared)
                {
                    System.Buffers.ArrayPool<float>.Shared.Return(planes[c]);
                }
            }
        }
    }
}

/// <summary>
/// Decodes a frame: the frame header and table of contents, then the global, DC-group and AC-group sections. Modular
/// channels and (for VarDCT) the DCT coefficient data are decoded into per-frame state and combined at the end.
/// </summary>
internal static class JxlFrameDecoder
{
    private const int NumQuantTables = 17;

    /// <summary>Decodes the frame starting at <paramref name="offset"/> in <paramref name="codestream"/>.</summary>
    public static JxlDecodedFrame Decode(byte[] codestream, int offset, JxlCodestreamHeaders headers, JxlDecoderState state, bool isPreview = false, JxlJpegData? jpegTarget = null)
    {
        var metadata = headers.Metadata;
        var reader = new JxlBitReader(codestream.AsSpan(offset));
        var frame = JxlFrameHeader.Read(ref reader, metadata, isPreview ? metadata.PreviewSize!.Value : headers.Size, isPreview);

        // Noise seeds depend on how many visible and invisible frames precede this one.
        if (!isPreview && (frame.IsLast || frame.Duration > 0) && frame.FrameType is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
        {
            state.VisibleFrameIndex++;
            state.NonVisibleFrameIndex = 0;
        }
        else
        {
            state.NonVisibleFrameIndex++;
        }

        var dims = frame.Dimensions;
        int tocEntries = JxlToc.EntryCount(dims.NumGroups, dims.NumDcGroups, frame.NumPasses);
        var toc = JxlToc.Read(ref reader, tocEntries);
        int dataStart = offset + (int)(reader.BitPosition >> 3);

        long total = 0;
        foreach (uint size in toc.Sizes)
        {
            total += size;
        }

        if (dataStart + total > codestream.Length)
        {
            throw new JxlDecodingException("The frame's sections extend past the end of the codestream.");
        }

        // Sections in file order, then keyed by their logical id.
        var sectionStart = new int[tocEntries];
        var sectionLength = new int[tocEntries];
        int position = dataStart;
        for (int i = 0; i < tocEntries; i++)
        {
            int id = toc.Ids[i];
            sectionStart[id] = position;
            sectionLength[id] = (int)toc.Sizes[i];
            position += (int)toc.Sizes[i];
        }

        bool isGray = metadata.ColorEncoding.ColorSpace == JxlColorSpace.Gray;
        bool vardct = !frame.IsModular;

        // Grayscale frames without a colour transform carry a single colour channel; everything else carries three.
        int colorChannels = isGray && frame.ColorTransform == JxlColorTransform.None ? 1 : 3;
        int extraChannels = metadata.ExtraChannels.Count;
        int bitDepth = checked((int)metadata.BitDepth.BitsPerSample);

        var image = new ModularImage(dims.XSize, dims.YSize, bitDepth);
        var matrices = vardct ? null : new DequantMatrices();
        var vd = vardct ? new VarDctFrame(frame, headers) : null;
        matrices ??= vd!.Matrices;
        if (vd is not null && (frame.Flags & JxlFrameFlags.UseDcFrame) != 0)
        {
            if (frame.DcLevel >= 4 || state.DcFrames[frame.DcLevel] is not { } dcFrame)
            {
                throw new JxlDecodingException("The frame refers to a DC frame that was not decoded.");
            }

            vd.UseDcFrame(dcFrame);
        }

        if (jpegTarget is not null)
        {
            if (vd is null || (frame.Flags & JxlFrameFlags.UseDcFrame) != 0 || frame.ColorTransform == JxlColorTransform.Xyb)
            {
                throw new JxlUnsupportedFeatureException("The frame is not a recompressed JPEG.");
            }

            vd.EnableJpegCapture(jpegTarget);
        }

        ModularContextModel? globalModel = null;
        ModularGroupHeader? globalHeader = null;
        var features = new JxlFrameFeatures();
        try
        {
            bool singleSection = dims.NumGroups == 1 && frame.NumPasses == 1;
            var singleReader = singleSection ? new JxlBitReader(codestream.AsSpan(sectionStart[0], sectionLength[0])) : default;

            // DC global.
            {
                var br = singleSection ? singleReader : new JxlBitReader(codestream.AsSpan(sectionStart[0], sectionLength[0]));
                features = JxlFrameFeatures.Read(ref br, frame, metadata, state);
                matrices.ReadDc(ref br);
                vd?.ReadGlobal(ref br);
                (globalModel, globalHeader) = DecodeGlobalInfo(ref br, frame, metadata, image, colorChannels, vardct ? 0 : colorChannels, extraChannels);
                vd?.SetGlobalModel(globalModel);
                if (singleSection)
                {
                    singleReader = br;
                }
            }

            int groupDim = dims.GroupDimension;

            // DC groups.
            for (int g = 0; g < dims.NumDcGroups; g++)
            {
                var br = singleSection
                    ? singleReader
                    : new JxlBitReader(codestream.AsSpan(sectionStart[1 + g], sectionLength[1 + g]));
                if (vardct && (frame.Flags & JxlFrameFlags.UseDcFrame) == 0)
                {
                    vd!.DecodeDc(ref br, g, bitDepth);
                }

                int gx = g % dims.XSizeDcGroups;
                int gy = g / dims.XSizeDcGroups;
                DecodeGroup(
                    ref br,
                    image,
                    new GroupRect(gx * dims.DcGroupDimension, gy * dims.DcGroupDimension, dims.DcGroupDimension, dims.DcGroupDimension),
                    minShift: 3,
                    maxShift: 1000,
                    streamId: (uint)(1 + dims.NumDcGroups + g),
                    groupDim,
                    globalModel);
                vd?.DecodeAcMetadata(ref br, g, bitDepth);
                if (singleSection)
                {
                    singleReader = br;
                }
            }

            if (vardct && (frame.Flags & (JxlFrameFlags.SkipAdaptiveDcSmoothing | JxlFrameFlags.UseDcFrame)) == 0 && vd!.IsFullResolution && jpegTarget is null)
            {
                vd!.AdaptiveDcSmoothing();
            }

            // AC global.
            int acGlobalIndex = dims.NumDcGroups + 1;
            if (vardct)
            {
                var br = singleSection
                    ? singleReader
                    : new JxlBitReader(codestream.AsSpan(sectionStart[acGlobalIndex], sectionLength[acGlobalIndex]));
                vd!.ReadAcGlobal(ref br);
                if (singleSection)
                {
                    singleReader = br;
                }
            }

            // AC groups. Frames made of several sections have independent groups, which are decoded on all cores.
            if (!singleSection)
            {
                vd?.PrepareParallelDecode();
                JxlParallel.For(dims.NumGroups, g => DecodeIndependentAcGroup(g, codestream, sectionStart, sectionLength, frame, acGlobalIndex, vd, image, groupDim, globalModel));
            }
            else
            {
                var streams = new PassStream[frame.NumPasses];
                for (int g = 0; g < dims.NumGroups; g++)
                {
                    if (vardct)
                    {
                        for (int pass = 0; pass < frame.NumPasses; pass++)
                        {
                            streams[pass] = new PassStream
                            {
                                Data = codestream.AsMemory(sectionStart[0], sectionLength[0]),
                                BitPosition = singleReader.BitPosition,
                            };
                        }

                        vd!.DecodeAcGroup(g, streams);
                        singleReader = new JxlBitReader(codestream.AsSpan(sectionStart[0], sectionLength[0]), streams[0].BitPosition);
                    }

                    int gx = g % dims.XSizeGroups;
                    int gy = g / dims.XSizeGroups;
                    for (int pass = 0; pass < frame.NumPasses; pass++)
                    {
                        GetDownsamplingBracket(frame, pass, out int minShift, out int maxShift);
                        var br = singleReader;
                        DecodeGroup(
                            ref br,
                            image,
                            new GroupRect(gx * groupDim, gy * groupDim, groupDim, groupDim),
                            minShift,
                            maxShift,
                            streamId: (uint)(1 + (3 * dims.NumDcGroups) + NumQuantTables + (dims.NumGroups * pass) + g),
                            groupDim,
                            globalModel);
                        singleReader = br;
                    }
                }
            }

            // Undo the transforms that span the whole frame.
            image.UndoTransforms(globalHeader!.WeightedHeader);
            int expected = (vardct ? 0 : colorChannels) + extraChannels;
            if (image.Channels.Count != expected)
            {
                throw new JxlDecodingException("The Modular image has an unexpected number of channels after undoing its transforms.");
            }

            int byteLength = (int)(dataStart - offset + total);
            if (vardct && jpegTarget is not null)
            {
                // The coefficients were stored in the JPEG data; there is no image to build.
                return new JxlDecodedFrame(frame, image, 0) { ByteLength = byteLength };
            }

            if (vardct)
            {
                return BuildXybFrame(frame, headers, state, features, image, vd!, byteLength);
            }

            if (frame.ColorTransform == JxlColorTransform.Xyb)
            {
                return BuildXybFrameFromModular(frame, headers, state, features, image, matrices, colorChannels, byteLength);
            }

            if (FinishIntegerFrame(frame, headers, state, features, image, colorChannels) is { } kept)
            {
                // The colour went through the float pipeline (filters, features, upsampling, YCbCr): it stays in float, as in the
                // reference decoder, instead of being rounded back to integer samples before the output conversion.
                return new JxlDecodedFrame(frame, image, 0) { ColorPlanes = kept.Planes, ColorPlaneStride = kept.Stride, ColorPlanesPooled = kept.Pooled, ByteLength = byteLength };
            }

            return new JxlDecodedFrame(frame, image, colorChannels) { ByteLength = byteLength };
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    // One AC group of a frame whose sections are separate byte ranges: touches only its own section data and its own
    // rectangle of the shared output, so groups can run concurrently.
    private static void DecodeIndependentAcGroup(
        int g,
        byte[] codestream,
        int[] sectionStart,
        int[] sectionLength,
        JxlFrameHeader frame,
        int acGlobalIndex,
        VarDctFrame? vd,
        ModularImage image,
        int groupDim,
        ModularContextModel? globalModel)
    {
        var dims = frame.Dimensions;
        var streams = new PassStream[frame.NumPasses];
        if (vd is not null)
        {
            for (int pass = 0; pass < frame.NumPasses; pass++)
            {
                int sectionId = acGlobalIndex + 1 + (pass * dims.NumGroups) + g;
                streams[pass] = new PassStream { Data = codestream.AsMemory(sectionStart[sectionId], sectionLength[sectionId]), BitPosition = 0 };
            }

            vd.DecodeAcGroup(g, streams);
        }

        int gx = g % dims.XSizeGroups;
        int gy = g / dims.XSizeGroups;
        for (int pass = 0; pass < frame.NumPasses; pass++)
        {
            GetDownsamplingBracket(frame, pass, out int minShift, out int maxShift);
            int sectionId = acGlobalIndex + 1 + (pass * dims.NumGroups) + g;
            var br = new JxlBitReader(codestream.AsSpan(sectionStart[sectionId], sectionLength[sectionId]), vd is not null ? streams[pass].BitPosition : 0);
            DecodeGroup(
                ref br,
                image,
                new GroupRect(gx * groupDim, gy * groupDim, groupDim, groupDim),
                minShift,
                maxShift,
                streamId: (uint)(1 + (3 * dims.NumDcGroups) + NumQuantTables + (dims.NumGroups * pass) + g),
                groupDim,
                globalModel);
        }
    }

    private static JxlDecodedFrame BuildXybFrame(JxlFrameHeader frame, JxlCodestreamHeaders headers, JxlDecoderState state, JxlFrameFeatures features, ModularImage extras, VarDctFrame vd, int byteLength)
    {
        var metadata = headers.Metadata;
        var output = frame.ColorTransform == JxlColorTransform.Xyb ? SelectXybOutput(metadata) : metadata.ColorEncoding;
        bool gray = metadata.ColorEncoding.ColorSpace == JxlColorSpace.Gray;
        var dims = frame.Dimensions;
        float[][] planes = vd.FilterToXyb();
        var result = FinishXyb(frame, headers, state, features, extras, planes, dims.XSizePadded, dims.XSize, dims.YSize, vd.Correlation.YToXRatio(0), vd.Correlation.YToBRatio(0), output, gray, firstExtra: 0, planesPooled: true);
        return new JxlDecodedFrame(frame, extras, 0)
        {
            ColorPlanes = result.Planes,
            ColorPlaneStride = result.Stride,
            ColorPlanesPooled = result.Pooled,
            ByteLength = byteLength,
        };
    }

    private readonly record struct FinishedPlanes(float[][] Planes, int Stride, int Width, int Height, bool Pooled);

    private static int EcUpsampling(JxlFrameHeader frame, int index) =>
        index < frame.ExtraChannelUpsampling.Length ? frame.ExtraChannelUpsampling[index] : 1;

    private static float[] UpsamplingKernels(JxlCustomTransformData transform, int shift) => JxlUpsampling.BuildKernels(
        shift,
        (shift switch
        {
            1 => transform.Upsampling2Weights,
            2 => transform.Upsampling4Weights,
            _ => transform.Upsampling8Weights,
        }) ?? JxlUpsampling.DefaultWeights(shift));

    // Patches and splines at the coded resolution, then upsampling, then noise at the final resolution (the reference order).
    private static FinishedPlanes RunFeaturePipeline(
        JxlFrameHeader frame,
        JxlCodestreamHeaders headers,
        JxlDecoderState state,
        JxlFrameFeatures features,
        FrameExtraChannels extras,
        float[][] planes,
        int stride,
        int width,
        int height,
        float yToX,
        float yToB,
        bool planesPooled)
    {
        var transform = headers.TransformData;
        int ups = frame.Upsampling;
        bool lateExtraUpsampling = ups != 1;
        for (int i = 0; i < extras.Count; i++)
        {
            if (EcUpsampling(frame, i) != ups)
            {
                lateExtraUpsampling = false;
            }
        }

        if (!lateExtraUpsampling)
        {
            for (int i = 0; i < extras.Count; i++)
            {
                int ecUps = EcUpsampling(frame, i);
                if (ecUps != 1)
                {
                    extras.Upsample(i, CeilLog2(ecUps), UpsamplingKernels(transform, CeilLog2(ecUps)), stride);
                }
            }
        }

        features.ApplyBeforeUpsampling(headers.Metadata, state, planes, stride, width, height, extras, yToX, yToB);

        if (ups != 1)
        {
            int shift = CeilLog2(ups);
            var kernels = UpsamplingKernels(transform, shift);
            int newStride = width << shift;
            var upsampled = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                upsampled[c] = System.Buffers.ArrayPool<float>.Shared.Rent(newStride * (height << shift));
                JxlUpsampling.Upsample(planes[c], stride, width, height, shift, kernels, newStride, destination: upsampled[c]);
            }

            if (lateExtraUpsampling)
            {
                for (int i = 0; i < extras.Count; i++)
                {
                    extras.Upsample(i, shift, kernels, newStride);
                }
            }

            if (planesPooled)
            {
                foreach (float[] old in planes)
                {
                    System.Buffers.ArrayPool<float>.Shared.Return(old);
                }
            }

            planesPooled = true;
            planes = upsampled;
            stride = newStride;
            width = frame.Dimensions.XSizeUpsampled;
            height = frame.Dimensions.YSizeUpsampled;
        }

        features.ApplyNoise(frame, state, planes, stride, width, height, yToX, yToB);
        return new FinishedPlanes(planes, stride, width, height, planesPooled);
    }

    // Features, upsampling, reference saving and the XYB -> output colour conversion shared by VarDCT and lossy Modular frames.
    private static FinishedPlanes FinishXyb(
        JxlFrameHeader frame,
        JxlCodestreamHeaders headers,
        JxlDecoderState state,
        JxlFrameFeatures features,
        ModularImage extrasImage,
        float[][] planes,
        int stride,
        int width,
        int height,
        float yToX,
        float yToB,
        JxlColorEncoding output,
        bool gray,
        int firstExtra,
        bool planesPooled = false)
    {
        var metadata = headers.Metadata;
        var extras = new FrameExtraChannels(extrasImage, firstExtra, metadata.ExtraChannels, stride);
        var finished = RunFeaturePipeline(frame, headers, state, features, extras, planes, stride, width, height, yToX, yToB, planesPooled);
        planes = finished.Planes;
        stride = finished.Stride;
        width = finished.Width;
        height = finished.Height;

        if (frame.FrameType == JxlFrameType.Dc)
        {
            // A DC frame is the (XYB) low-frequency image of a later frame, not something to display.
            state.DcFrames[frame.DcLevel - 1] = JxlFrameFeatures.CopyFrame(planes, stride, width, height, null, inXyb: true);
            extras.Commit(width, height);
            return finished;
        }

        if (frame.CanBeReferenced && frame.SaveBeforeColorTransform)
        {
            state.References[(int)frame.SaveAsReference] = JxlFrameFeatures.CopyFrame(planes, stride, width, height, extras, inXyb: true);
        }

        if (frame.ColorTransform == JxlColorTransform.YCbCr)
        {
            // JPEG-style colour: the planes already hold display-encoded values, only the luma/chroma representation changes.
            ColorKernels.YCbCrToRgb(planes, stride, width, height);
        }
        else if (frame.ColorTransform == JxlColorTransform.None)
        {
            // Nothing to convert: the planes are the display-encoded colour channels.
        }
        else
        {
            float[]? gamutMatrix = !output.WantIcc && !gray && !JxlColorSpaceMath.IsSrgbGamut(output) ? JxlColorSpaceMath.SrgbToEncoding(output) : null;
            ColorKernels.XybToLinearRgb(planes, stride, width, height, headers.TransformData, metadata.ToneMapping.IntensityTarget, gray, srgbToEncoding: gamutMatrix);
            if (output.WantIcc)
            {
                var profile = JxlIccOutput.OpenProfile(headers.IccProfile ?? throw new JxlDecodingException("The ICC profile is missing."), gray);
                if (gray)
                {
                    JxlIccOutput.FromLinearGray(profile, planes, stride, width, height);
                }
                else
                {
                    JxlIccOutput.FromLinearSrgb(profile, planes, stride, width, height);
                }
            }
            else
            {
                ColorKernels.LinearToTransfer(planes, stride, width, height, output, intensityTarget: metadata.ToneMapping.IntensityTarget);
            }
        }

        extras.Commit(width, height);
        return finished;
    }

    // Integer (non-XYB) Modular frames only go through float planes when a feature, upsampling or reference saving needs them.
    private static FinishedPlanes? FinishIntegerFrame(JxlFrameHeader frame, JxlCodestreamHeaders headers, JxlDecoderState state, JxlFrameFeatures features, ModularImage image, int colorChannels)
    {
        bool needsExtraUpsampling = false;
        for (int i = 0; i < headers.Metadata.ExtraChannels.Count; i++)
        {
            needsExtraUpsampling |= EcUpsampling(frame, i) != 1;
        }

        bool savesBeforeTransform = frame.CanBeReferenced && frame.SaveBeforeColorTransform;
        var lf = frame.LoopFilter;
        bool filters = lf.Gab || lf.EpfIterations > 0;
        bool ycc = frame.ColorTransform == JxlColorTransform.YCbCr;
        if (!features.HasAny && !savesBeforeTransform && frame.Upsampling == 1 && !needsExtraUpsampling && frame.FrameType != JxlFrameType.Dc && !filters && !ycc)
        {
            return null;
        }

        var metadata = headers.Metadata;
        int width = frame.Dimensions.XSize;
        int height = frame.Dimensions.YSize;
        var depth = metadata.BitDepth;
        var pool = System.Buffers.ArrayPool<float>.Shared;

        // Chroma-subsampled planes are upsampled into planes whose stride and height are rounded up to even.
        int stride = ycc ? (width + 1) & ~1 : width;
        int rows = ycc ? (height + 1) & ~1 : height;
        var planes = new float[3][];
        for (int c = 0; c < colorChannels; c++)
        {
            planes[c] = JxlYCbCrModular.ToPlane(image.Channels[c], depth, stride, rows, width, height);
        }

        if (colorChannels == 1)
        {
            // Gray frames operate on three identical planes; only the first is written back.
            for (int c = 1; c < 3; c++)
            {
                planes[c] = pool.Rent(stride * rows);
                Array.Copy(planes[0], planes[c], stride * rows);
            }
        }

        if (filters)
        {
            // Gaborish and the edge-preserving filter run on the float samples (with a constant strength: there is no quantization field).
            int blocksX = (width + 7) / 8;
            float[]? sigma = lf.EpfIterations > 0 ? FramePostProcessing.ConstantSigma(lf, blocksX, (height + 7) / 8) : null;
            RestorationFilters.Apply(planes, stride, width, height, lf, sigma, blocksX);
        }

        var extras = new FrameExtraChannels(image, colorChannels, metadata.ExtraChannels, stride);
        var finished = RunFeaturePipeline(frame, headers, state, features, extras, planes, stride, width, height, 0f, 1f, planesPooled: true);
        if (savesBeforeTransform)
        {
            state.References[(int)frame.SaveAsReference] = JxlFrameFeatures.CopyFrame(finished.Planes, finished.Stride, finished.Width, finished.Height, extras, inXyb: true);
        }

        if (ycc)
        {
            ColorKernels.YCbCrToRgb(finished.Planes, finished.Stride, finished.Width, finished.Height);
        }

        if (depth.IsFloat || depth.BitsPerSample <= 16)
        {
            // Keep the float colour (the integer channels are no longer needed); only the extra channels stay in the image.
            extras.Commit(finished.Width, finished.Height);
            for (int c = 0; c < colorChannels; c++)
            {
                image.Channels[c].Dispose();
            }

            image.Channels.RemoveRange(0, colorChannels);
            return finished;
        }

        for (int c = 0; c < colorChannels; c++)
        {
            var channel = image.Channels[c];
            if (channel.Width != finished.Width || channel.Height != finished.Height)
            {
                var replacement = new ModularChannel(finished.Width, finished.Height);
                image.Channels[c] = replacement;
                channel.Dispose();
                channel = replacement;
            }

            JxlSampleConversion.ToIntChannel(finished.Planes[c], finished.Stride, channel.Array, finished.Width, finished.Height, depth);
        }

        extras.Commit(finished.Width, finished.Height);
        if (finished.Pooled)
        {
            foreach (float[] plane in finished.Planes)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(plane);
            }
        }

        return null;
    }

    // Lossy Modular: integer channels (Y, X, B-Y order) are scaled by the DC factors into XYB floats, then follow the same post-processing.
    private static JxlDecodedFrame BuildXybFrameFromModular(JxlFrameHeader frame, JxlCodestreamHeaders headers, JxlDecoderState state, JxlFrameFeatures features, ModularImage image, DequantMatrices matrices, int colorChannels, int byteLength)
    {
        var metadata = headers.Metadata;
        var output = SelectXybOutput(metadata);
        var dims = frame.Dimensions;
        int width = dims.XSize;
        int height = dims.YSize;
        var planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            planes[c] = System.Buffers.ArrayPool<float>.Shared.Rent(width * height);
        }

        var chY = image.Channels[0];
        var chX = image.Channels[1];
        var chB = image.Channels[2];
        float[] dcq = matrices.DcQuant;
        for (int y = 0; y < height; y++)
        {
            var rowY = chY.ReadOnlyRow(y);
            var rowX = chX.ReadOnlyRow(y);
            var rowB = chB.ReadOnlyRow(y);
            int dst = y * width;
            for (int x = 0; x < width; x++)
            {
                planes[0][dst + x] = rowX[x] * dcq[0];
                planes[1][dst + x] = rowY[x] * dcq[1];
                planes[2][dst + x] = (rowB[x] + rowY[x]) * dcq[2];
            }
        }

        var lf = frame.LoopFilter;
        int blocksX = (width + 7) / 8;
        int blocksY = (height + 7) / 8;
        float[]? sigma = lf.EpfIterations > 0 ? FramePostProcessing.ConstantSigma(lf, blocksX, blocksY) : null;
        RestorationFilters.Apply(planes, width, width, height, lf, sigma, blocksX);

        bool gray = metadata.ColorEncoding.ColorSpace == JxlColorSpace.Gray;

        // Only the extra channels stay in the integer image.
        for (int c = 0; c < colorChannels; c++)
        {
            image.Channels[c].Dispose();
        }

        image.Channels.RemoveRange(0, colorChannels);
        var finished = FinishXyb(frame, headers, state, features, image, planes, width, width, height, 0f, 1f, output, gray, firstExtra: 0, planesPooled: true);
        return new JxlDecodedFrame(frame, image, 0) { ColorPlanes = finished.Planes, ColorPlaneStride = finished.Stride, ColorPlanesPooled = finished.Pooled, ByteLength = byteLength };
    }

    // The encoding XYB output is rendered in. Only sRGB primaries with a white point of D65 can be produced without colour management.
    private static JxlColorEncoding SelectXybOutput(JxlImageMetadata metadata)
    {
        var encoding = metadata.ColorEncoding;
        if (encoding.WantIcc)
        {
            // Rendered to linear sRGB first, then converted to the embedded profile.
            return encoding;
        }

        return encoding;
    }

    private static (ModularContextModel? Model, ModularGroupHeader Header) DecodeGlobalInfo(
        ref JxlBitReader br,
        JxlFrameHeader frame,
        JxlImageMetadata metadata,
        ModularImage image,
        int treeColorChannels,
        int colorChannels,
        int extraChannels)
    {
        var dims = frame.Dimensions;
        bool hasTree = br.ReadBool();
        ModularContextModel? model = null;
        if (hasTree)
        {
            long limit = Math.Min(1 << 22, 1024 + ((long)dims.XSize * dims.YSize * (treeColorChannels + extraChannels) / 16));
            model = ModularContextModel.Read(ref br, (int)limit);
        }

        if (metadata.BitDepth.BitsPerSample >= 32 && !metadata.BitDepth.IsFloat && colorChannels > 0 && frame.ColorTransform != JxlColorTransform.Xyb)
        {
            throw new JxlUnsupportedFeatureException("32-bit integer samples are not supported.");
        }

        if (frame.ColorTransform == JxlColorTransform.YCbCr)
        {
            // JPEG-style colour planes may be chroma-subsampled; each is stored at its own (shifted) size.
            for (int c = 0; c < colorChannels; c++)
            {
                int hShift = JxlYCbCrModular.HorizontalShift(frame.ChromaSubsamplingModes[c]);
                int vShift = JxlYCbCrModular.VerticalShift(frame.ChromaSubsamplingModes[c]);
                image.Channels.Add(new ModularChannel(
                    (dims.XSize + (1 << hShift) - 1) >> hShift,
                    (dims.YSize + (1 << vShift) - 1) >> vShift,
                    hShift,
                    vShift));
            }
        }
        else
        {
            image.AddChannels(colorChannels);
        }

        for (int ec = 0; ec < extraChannels; ec++)
        {
            int upsampling = frame.ExtraChannelUpsampling.Length > ec ? frame.ExtraChannelUpsampling[ec] : 1;
            var channel = new ModularChannel(
                (dims.XSizeUpsampled + upsampling - 1) / upsampling,
                (dims.YSizeUpsampled + upsampling - 1) / upsampling);
            int shift = CeilLog2(upsampling) - CeilLog2(frame.Upsampling);
            channel.HShift = shift;
            channel.VShift = shift;
            image.Channels.Add(channel);
        }

        var header = ModularStreamDecoder.Decode(
            ref br,
            image,
            streamId: 0,
            maxChannelSize: dims.GroupDimension,
            groupDim: dims.GroupDimension,
            model,
            undoTransforms: false);
        return (model, header);
    }

    private static int CeilLog2(int x) => x <= 1 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(x - 1));

    private readonly record struct GroupRect(int X0, int Y0, int Width, int Height);

    private static void DecodeGroup(
        ref JxlBitReader br,
        ModularImage full,
        GroupRect rect,
        int minShift,
        int maxShift,
        uint streamId,
        int groupDim,
        ModularContextModel? globalModel)
    {
        // Start at the first non-meta channel that is larger than a group: smaller ones were in the global stream.
        int c = full.MetaChannelCount;
        for (; c < full.Channels.Count; c++)
        {
            var fc = full.Channels[c];
            if (fc.Width > groupDim || fc.Height > groupDim)
            {
                break;
            }
        }

        var group = new ModularImage(rect.Width, rect.Height, full.BitDepth);
        var rects = new List<(int Channel, int X0, int Y0, int W, int H)>();
        try
        {
            for (; c < full.Channels.Count; c++)
            {
                var fc = full.Channels[c];
                int shift = Math.Min(fc.HShift, fc.VShift);
                if (shift > maxShift || shift < minShift)
                {
                    continue;
                }

                int x0 = rect.X0 >> fc.HShift;
                int y0 = rect.Y0 >> fc.VShift;
                int w = Math.Min(rect.Width >> fc.HShift, fc.Width - x0);
                int h = Math.Min(rect.Height >> fc.VShift, fc.Height - y0);
                if (w <= 0 || h <= 0)
                {
                    continue;
                }

                group.Channels.Add(new ModularChannel(w, h, fc.HShift, fc.VShift));
                rects.Add((c, x0, y0, w, h));
            }

            if (group.Channels.Count == 0)
            {
                return;
            }

            ModularStreamDecoder.Decode(
                ref br,
                group,
                streamId,
                maxChannelSize: 0xFFFFFF,
                groupDim: 0x1FFFFFFF,
                globalModel,
                undoTransforms: true);

            for (int i = 0; i < rects.Count; i++)
            {
                var (channel, x0, y0, w, h) = rects[i];
                var target = full.Channels[channel];
                var source = group.Channels[i];
                for (int y = 0; y < h; y++)
                {
                    source.ReadOnlyRow(y)[..w].CopyTo(target.Row(y0 + y).Slice(x0, w));
                }
            }
        }
        finally
        {
            group.Dispose();
        }
    }

    // The range of squeeze levels (downsampling shifts) that pass `pass` of a progressive frame carries.
    private static void GetDownsamplingBracket(JxlFrameHeader frame, int pass, out int minShift, out int maxShift)
    {
        maxShift = 2;
        minShift = 3;
        for (int i = 0; ; i++)
        {
            for (int j = 0; j < frame.PassDownsample.Length; j++)
            {
                if (i == frame.PassLastPass[j])
                {
                    minShift = frame.PassDownsample[j] switch { 8 => 3, 4 => 2, 2 => 1, _ => 0 };
                }
            }

            if (i == frame.NumPasses - 1)
            {
                minShift = 0;
            }

            if (i == pass)
            {
                return;
            }

            maxShift = minShift - 1;
        }
    }
}
