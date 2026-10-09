using PeachImage.Formats.Jxl.Container;
using PeachImage.Formats.Jxl.Features;
using PeachImage.Formats.Jxl.Frame;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Jpeg;

/// <summary>Rebuilds the original JPEG file from a JPEG XL file that was made by losslessly recompressing one.</summary>
internal static class JxlJpegReconstructor
{
    public static bool HasReconstructionData(byte[] file) => JxlContainer.Parse(file).JpegReconstruction is not null;

    public static byte[] Reconstruct(byte[] file)
    {
        var container = JxlContainer.Parse(file);
        if (container.JpegReconstruction is not { } reconstruction)
        {
            throw new JxlUnsupportedFeatureException("The file does not contain JPEG reconstruction data (it was not made from a JPEG).");
        }

        var jpeg = JxlJpegDataReader.Read(reconstruction);
        byte[] codestream = container.Codestream.ToArray();
        var headers = JxlCodestreamHeaders.Read(codestream);

        var state = new JxlDecoderState();
        int offset = headers.FrameOffset;
        if (headers.Metadata.PreviewSize is not null)
        {
            using var preview = JxlFrameDecoder.Decode(codestream, offset, headers, state, isPreview: true);
            offset += preview.ByteLength;
        }

        using (var frame = JxlFrameDecoder.Decode(codestream, offset, headers, state, jpegTarget: jpeg))
        {
            if (!frame.Header.IsLast)
            {
                throw new JxlUnsupportedFeatureException("A recompressed JPEG has exactly one frame.");
            }
        }

        InsertMetadata(jpeg, headers.IccProfile, container);
        return JxlJpegWriter.Write(jpeg);
    }

    // The ICC profile lives in the codestream and the Exif / XMP in their boxes; the JPEG markers only keep their framing.
    private static void InsertMetadata(JxlJpegData jpeg, byte[]? icc, JxlContainer container)
    {
        int iccPosition = 0;
        int exifMarkers = 0;
        int xmpMarkers = 0;
        for (int i = 0; i < jpeg.AppData.Count; i++)
        {
            var marker = jpeg.AppData[i];
            switch (jpeg.AppMarkerTypes[i])
            {
                case JpegAppMarkerType.Icc:
                {
                    if (marker.Length < 17)
                    {
                        throw new JxlDecodingException("An ICC application marker is too small.");
                    }

                    int length = marker.Length - 17;
                    if (icc is null || iccPosition + length > icc.Length)
                    {
                        throw new JxlDecodingException("The ICC profile is shorter than the JPEG ICC markers.");
                    }

                    icc.AsSpan(iccPosition, length).CopyTo(marker.AsSpan(17));
                    iccPosition += length;
                    break;
                }

                case JpegAppMarkerType.Exif:
                {
                    exifMarkers++;
                    if (container.ExifBox is not { } exif || marker.Length != exif.Length + 3 + JxlJpegData.ExifTag.Length - 4)
                    {
                        throw new JxlDecodingException("The Exif box does not match the JPEG Exif marker.");
                    }

                    marker[0] = 0xE1;
                    JxlJpegData.ExifTag.CopyTo(marker.AsSpan(3));

                    // The first four bytes are the TIFF header offset of the box, which the JPEG marker does not carry.
                    exif.AsSpan(4).CopyTo(marker.AsSpan(3 + JxlJpegData.ExifTag.Length));
                    break;
                }

                case JpegAppMarkerType.Xmp:
                {
                    xmpMarkers++;
                    if (container.Xmp is not { } xmp || marker.Length != xmp.Length + 3 + JxlJpegData.XmpTag.Length)
                    {
                        throw new JxlDecodingException("The XMP box does not match the JPEG XMP marker.");
                    }

                    marker[0] = 0xE1;
                    JxlJpegData.XmpTag.CopyTo(marker.AsSpan(3));
                    xmp.CopyTo(marker.AsSpan(3 + JxlJpegData.XmpTag.Length));
                    break;
                }
            }
        }

        if (exifMarkers > 1 || xmpMarkers > 1)
        {
            throw new JxlUnsupportedFeatureException("Multiple Exif or XMP markers are not supported for JPEG reconstruction.");
        }

        if (icc is not null && iccPosition != icc.Length && iccPosition != 0)
        {
            throw new JxlDecodingException("The ICC profile is longer than the JPEG ICC markers.");
        }
    }
}
