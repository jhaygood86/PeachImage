using PeachImage.Formats.Shared.Parallelism;
using System.Diagnostics.CodeAnalysis;
using PeachImage.Formats.Jxl.Encoder.Jpeg;

namespace PeachImage.Formats.Jxl;

/// <summary>
/// Losslessly recompresses a JPEG file into JPEG XL, the way <c>cjxl</c> does for JPEG input: the quantized DCT coefficients are
/// re-coded with JPEG XL's entropy coder (typically a fifth smaller than the JPEG) and the information needed to write the original
/// bytes back is kept in the file, so <see cref="JxlJpegReconstruction"/> gives the JPEG back unchanged. The result also decodes
/// like any JPEG XL image, to the pixels the JPEG decodes to.
/// </summary>
/// <remarks>
/// Baseline (sequential Huffman, 8-bit) JPEGs with one component or with Y, Cb and Cr components are supported, with 4:4:4, 4:2:2,
/// 4:4:0 or 4:2:0 sampling, restart intervals and any metadata markers. Progressive, arithmetic-coded, 12-bit and CMYK JPEGs are
/// not; use <see cref="Image.Load(Stream, DecoderOptions?)"/> and <see cref="Image.Save(Stream, string, EncoderOptions?)"/> to turn
/// those into (lossy or lossless) JPEG XL images instead. Recompression reads the whole file.
/// </remarks>
public static class JxlJpegRecompression
{
    /// <summary>Recompresses the JPEG file in <paramref name="stream"/> into a JPEG XL file.</summary>
    /// <param name="stream">A readable stream positioned at the start of the JPEG file. It is read to its end.</param>
    /// <returns>The bytes of the JPEG XL file.</returns>
    /// <exception cref="JxlUnsupportedFeatureException">The data is not a JPEG, or it is one that cannot be recompressed losslessly.</exception>
    public static byte[] RecompressJpeg(Stream stream) => RecompressJpeg(stream, -1);

    /// <summary>Recompresses the JPEG file in <paramref name="stream"/> into a JPEG XL file, bounding the threads the call may use.</summary>
    /// <param name="stream">A readable stream positioned at the start of the JPEG file. It is read to its end.</param>
    /// <param name="maxDegreeOfParallelism">The most threads this call may use: -1 leaves it to the library (up to <see cref="Environment.ProcessorCount"/>), 1 runs on the calling thread. Per call, not global. Does not change the output.</param>
    /// <param name="taskScheduler">An optional scheduler for the parallel work, to share one limit across concurrent calls. <see langword="null"/> uses the thread pool.</param>
    /// <returns>The bytes of the JPEG XL file.</returns>
    /// <exception cref="JxlUnsupportedFeatureException">The data is not a JPEG, or it is one that cannot be recompressed losslessly.</exception>
    public static byte[] RecompressJpeg(Stream stream, int maxDegreeOfParallelism, TaskScheduler? taskScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return JxlJpegRecompressor.Recompress(JxlDecoder.ReadAll(stream), new ParallelSettings(maxDegreeOfParallelism, taskScheduler));
    }

    /// <summary>Recompresses the JPEG file in <paramref name="source"/> and writes the JPEG XL file to <paramref name="destination"/>.</summary>
    /// <exception cref="JxlUnsupportedFeatureException">The data is not a JPEG, or it is one that cannot be recompressed losslessly.</exception>
    public static void RecompressJpeg(Stream source, Stream destination) => RecompressJpeg(source, destination, -1);

    /// <summary>Recompresses the JPEG file in <paramref name="source"/> and writes the JPEG XL file to <paramref name="destination"/>, bounding the threads the call may use.</summary>
    /// <param name="source">A readable stream positioned at the start of the file. It is read to its end.</param>
    /// <param name="destination">The stream the result is written to.</param>
    /// <param name="maxDegreeOfParallelism">The most threads this call may use: -1 leaves it to the library (up to <see cref="Environment.ProcessorCount"/>), 1 runs on the calling thread. Per call, not global. Does not change the output.</param>
    /// <param name="taskScheduler">An optional scheduler for the parallel work, to share one limit across concurrent calls. <see langword="null"/> uses the thread pool.</param>
    /// <exception cref="JxlUnsupportedFeatureException">The data is not a JPEG, or it is one that cannot be recompressed losslessly.</exception>
    public static void RecompressJpeg(Stream source, Stream destination, int maxDegreeOfParallelism, TaskScheduler? taskScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Write(RecompressJpeg(source, maxDegreeOfParallelism, taskScheduler));
    }

    /// <summary>
    /// Recompresses the JPEG file in <paramref name="stream"/> if it can be recompressed losslessly. Returns <see langword="false"/>
    /// (without throwing) for data that is not a JPEG or uses features that cannot be reproduced.
    /// </summary>
    public static bool TryRecompressJpeg(Stream stream, [NotNullWhen(true)] out byte[]? jxl) =>
        TryRecompressJpeg(stream, -1, null, out jxl);

    /// <summary>Like <see cref="TryRecompressJpeg(Stream, out byte[])"/>, bounding the threads the call may use.</summary>
    /// <param name="stream">A readable stream positioned at the start of the file. It is read to its end.</param>
    /// <param name="jxl">The JPEG XL file, when this returns <see langword="true"/>.</param>
    /// <param name="maxDegreeOfParallelism">The most threads this call may use: -1 leaves it to the library (up to <see cref="Environment.ProcessorCount"/>), 1 runs on the calling thread. Per call, not global. Does not change the output.</param>
    /// <param name="taskScheduler">An optional scheduler for the parallel work, to share one limit across concurrent calls. <see langword="null"/> uses the thread pool.</param>
    public static bool TryRecompressJpeg(Stream stream, int maxDegreeOfParallelism, TaskScheduler? taskScheduler, [NotNullWhen(true)] out byte[]? jxl)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] file = JxlDecoder.ReadAll(stream);
        try
        {
            jxl = JxlJpegRecompressor.Recompress(file, new ParallelSettings(maxDegreeOfParallelism, taskScheduler));
            return true;
        }
        catch (JxlUnsupportedFeatureException)
        {
            jxl = null;
            return false;
        }
    }
}
