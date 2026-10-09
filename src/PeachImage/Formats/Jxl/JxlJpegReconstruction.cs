using System.Diagnostics.CodeAnalysis;
using PeachImage.Formats.Jxl.Jpeg;

namespace PeachImage.Formats.Jxl;

/// <summary>
/// Recovers the original JPEG file from a JPEG XL image that was made by losslessly recompressing one (as <c>cjxl</c> does
/// for JPEG input). The recovered bytes are identical to the JPEG that was recompressed, so they can be embedded or served
/// as-is, without decoding and re-encoding the pixels.
/// </summary>
/// <remarks>
/// Only files that carry JPEG reconstruction data (a <c>jbrd</c> box) qualify; any other JPEG XL image has to be decoded
/// with <see cref="Image.Load(Stream, DecoderOptions?)"/> instead. Reconstruction reads the whole file.
/// </remarks>
public static class JxlJpegReconstruction
{
    /// <summary>Whether the JPEG XL file in <paramref name="stream"/> carries JPEG reconstruction data. Returns <see langword="false"/> for files that are not JPEG XL.</summary>
    /// <param name="stream">A readable stream positioned at the start of the file. It is read to its end.</param>
    public static bool HasJpegReconstructionData(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] file = JxlDecoder.ReadAll(stream);
        try
        {
            return JxlJpegReconstructor.HasReconstructionData(file);
        }
        catch (JxlFormatException)
        {
            return false;
        }
    }

    /// <summary>Reconstructs the original JPEG file from the JPEG XL file in <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream positioned at the start of the file. It is read to its end.</param>
    /// <returns>The bytes of the original JPEG file.</returns>
    /// <exception cref="JxlUnsupportedFeatureException">The file has no JPEG reconstruction data.</exception>
    /// <exception cref="JxlFormatException">The file is not valid JPEG XL, or its reconstruction data is damaged.</exception>
    public static byte[] ReconstructJpeg(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return JxlJpegReconstructor.Reconstruct(JxlDecoder.ReadAll(stream));
    }

    /// <summary>Reconstructs the original JPEG file from the JPEG XL file in <paramref name="source"/> and writes it to <paramref name="destination"/>.</summary>
    /// <exception cref="JxlUnsupportedFeatureException">The file has no JPEG reconstruction data.</exception>
    /// <exception cref="JxlFormatException">The file is not valid JPEG XL, or its reconstruction data is damaged.</exception>
    public static void ReconstructJpeg(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Write(ReconstructJpeg(source));
    }

    /// <summary>
    /// Reconstructs the original JPEG file if the JPEG XL file in <paramref name="stream"/> carries reconstruction data.
    /// Returns <see langword="false"/> (without throwing) for files that are not JPEG XL or were not made from a JPEG.
    /// </summary>
    /// <exception cref="JxlFormatException">The file claims to hold a JPEG but its data is damaged.</exception>
    public static bool TryReconstructJpeg(Stream stream, [NotNullWhen(true)] out byte[]? jpeg)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] file = JxlDecoder.ReadAll(stream);
        jpeg = null;
        try
        {
            if (!JxlJpegReconstructor.HasReconstructionData(file))
            {
                return false;
            }
        }
        catch (JxlFormatException)
        {
            return false;
        }

        jpeg = JxlJpegReconstructor.Reconstruct(file);
        return true;
    }
}
