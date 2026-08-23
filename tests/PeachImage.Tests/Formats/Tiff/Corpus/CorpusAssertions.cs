using PeachImage.Formats.Tiff;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Tiff.Corpus;

/// <summary>
/// Shared assertions for corpus-driven tests: decoding must never crash, hang, or throw anything other than
/// <see cref="TiffFormatException"/> (either <see cref="TiffDecodingException"/> for genuinely malformed
/// data, or <see cref="TiffUnsupportedFeatureException"/> for well-formed-but-out-of-scope files — the
/// corpus covers far more of TIFF's feature matrix than this decoder's declared baseline scope, so a large
/// fraction of "valid" corpus files legitimately hitting the latter is expected, not a failure). No
/// SkiaSharp-style differential pixel comparison here — SkiaSharp has no TIFF decoder at all; a real
/// external-oracle correctness check instead lives in <c>TiffFfmpegReferenceTests</c>, generated from
/// <c>ffmpeg</c>'s independent TIFF decoder (see the project plan).
/// </summary>
internal static class CorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Asserts that decoding <paramref name="path"/> either succeeds or throws <see cref="TiffFormatException"/> — never anything else, and never hangs.</summary>
    public static void AssertDecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not TiffFormatException)
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = TiffDecoder.Decode(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }
}
