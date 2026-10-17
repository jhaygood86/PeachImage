using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Container;
using PeachImage.Formats.Jxl.Encoder.Headers;
using PeachImage.Formats.Jxl.Internal;
using PeachImage.Formats.Jxl.Jpeg;
using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Formats.Jxl.Encoder.Jpeg;

/// <summary>Turns a JPEG file into a JPEG XL file from which that exact JPEG file can be reconstructed, without decoding any pixels.</summary>
internal static class JxlJpegRecompressor
{
    public static byte[] Recompress(byte[] jpeg)
    {
        var parsed = JxlJpegParser.Parse(jpeg);
        var data = parsed.Data;
        if ((long)data.Width * data.Height > JxlDecodingLimits.MaxPixelCount)
        {
            throw new JxlUnsupportedFeatureException("This JPEG cannot be recompressed: the image is too large for JPEG XL.");
        }

        int orientation = parsed.Exif is null ? 1 : (int)ExifOrientationReader.ReadTiff(parsed.Exif);
        var info = new JxlEncodedImageInfo((uint)data.Width, (uint)data.Height, 8, IsGray: data.Components.Count == 1, HasAlpha: false, XybEncoded: false, orientation, parsed.IccProfile);
        var codestream = new JxlBitWriter(Math.Max(1024, jpeg.Length));
        JxlCodestreamHeaderWriter.Write(codestream, info);
        codestream.WriteBytes(JxlJpegFrameEncoder.Encode(data));

        using var output = new MemoryStream(jpeg.Length);
        JxlContainerWriter.Write(output, codestream.AsSpan(), parsed.Exif, parsed.Xmp, JxlJpegDataWriter.Write(data));
        byte[] file = output.ToArray();

        // A JPEG that quirks beyond what the reconstruction data can describe would silently come back different: prove it does not.
        byte[] rebuilt;
        try
        {
            rebuilt = JxlJpegReconstructor.Reconstruct(file);
        }
        catch (JxlFormatException ex)
        {
            throw new JxlUnsupportedFeatureException("This JPEG cannot be recompressed: it cannot be reconstructed from its JPEG XL form.", ex);
        }

        if (!rebuilt.AsSpan().SequenceEqual(jpeg))
        {
            throw new JxlUnsupportedFeatureException("This JPEG cannot be recompressed: it does not come back byte for byte from its JPEG XL form.");
        }

        return file;
    }
}
