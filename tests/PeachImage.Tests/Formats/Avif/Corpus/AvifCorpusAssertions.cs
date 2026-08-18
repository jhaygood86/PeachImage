using PeachImage.Formats.Avif;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Avif.Corpus;

/// <summary>Shared assertions for AVIF corpus-driven tests, mirroring Webp's <c>CorpusAssertions</c>. Kept as a plain helper (not itself a <c>[Fact]</c>/<c>[Theory]</c> method) so its blocking <c>Thread.Join</c> hang-guard doesn't trip xunit's analyzer rules against blocking directly inside a test method.</summary>
internal static class AvifCorpusAssertions
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Asserts that identifying <paramref name="path"/> either succeeds or throws <see cref="AvifDecodingException"/>/<see cref="AvifUnsupportedFeatureException"/> -- never anything else, and never hangs.</summary>
    public static void AssertIdentifiesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryIdentify(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Identifying {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not (AvifDecodingException or AvifUnsupportedFeatureException))
        {
            Assert.Fail($"Identifying {Path.GetFileName(path)} threw {exception}");
        }
    }

    private static (bool Succeeded, Exception? Exception) TryIdentify(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            _ = AvifDecoder.Identify(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    /// <summary>
    /// Asserts that fully decoding <paramref name="path"/> (container parsing through AV1 pixel
    /// reconstruction and YUV-&gt;RGB conversion) either succeeds or throws
    /// <see cref="AvifDecodingException"/>/<see cref="AvifUnsupportedFeatureException"/> -- never anything
    /// else, and never hangs. This phase's decode is scoped to a single (non-grid) 8-bit item without
    /// alpha and without in-loop filters, so most real corpus files legitimately throw
    /// <see cref="AvifUnsupportedFeatureException"/> today; that's a scope fact, not a failure.
    /// </summary>
    public static void AssertDecodesGracefully(string path)
    {
        if (!CorpusHangGuard.TryRun(() => TryDecode(path), PerFileTimeout, out var result))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} did not complete within {PerFileTimeout.TotalSeconds:F0}s (possible hang).");
        }

        var (succeeded, exception) = result;
        if (!succeeded && exception is not (AvifDecodingException or AvifUnsupportedFeatureException))
        {
            Assert.Fail($"Decoding {Path.GetFileName(path)} threw {exception}");
        }
    }

    private static (bool Succeeded, Exception? Exception) TryDecode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = AvifDecoder.Decode(stream);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }
}
