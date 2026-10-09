using PeachImage.Formats.Jxl;
using PeachImage.Tests.Internal;

namespace PeachImage.Tests.Formats.Jxl.Corpus;

/// <summary>
/// Damaged real-world files: truncations and bit flips of the smaller corpus files must be rejected with a JPEG XL exception (or
/// still decode), never crash, hang or exhaust memory.
/// </summary>
[Trait("Category", "Corpus")]
public class JxlCorpusRobustnessTests
{
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(60);

    public static IEnumerable<TheoryDataRow<string>> SmallFiles()
    {
        if (!CorpusFixture.IsAvailable || !Directory.Exists(CorpusPaths.ConformanceRoot))
        {
            yield return CorpusSkip.Row("External JPEG XL test corpus is not available (no network, or PEACHIMAGE_SKIP_CORPUS_FETCH is set).");
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(CorpusPaths.ConformanceRoot, "input.jxl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            long length = new FileInfo(file).Length;
            if (length is > 200 and < 130_000)
            {
                yield return new TheoryDataRow<string>(file);
            }
        }
    }

    [Theory]
    [MemberData(nameof(SmallFiles))]
    public void DamagedFile_FailsCleanly(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        var random = new Random(Path.GetFileName(Path.GetDirectoryName(path))!.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)));
        for (int trial = 0; trial < 12; trial++)
        {
            byte[] damaged;
            if (trial % 2 == 0)
            {
                damaged = original.AsSpan(0, random.Next(original.Length)).ToArray();
            }
            else
            {
                damaged = (byte[])original.Clone();
                int flips = 1 + random.Next(3);
                for (int i = 0; i < flips; i++)
                {
                    // Concentrate on the headers and the global sections, where a flipped bit changes the most.
                    int position = random.Next(2) == 0 ? random.Next(Math.Min(damaged.Length, 400)) : random.Next(damaged.Length);
                    damaged[position] ^= (byte)(1 << random.Next(8));
                }
            }

            if (!CorpusHangGuard.TryRun(() => TryDecode(damaged), PerFileTimeout, out var failure))
            {
                Assert.Fail($"{Path.GetFileName(Path.GetDirectoryName(path))}: a damaged variant did not finish within {PerFileTimeout.TotalSeconds:F0}s.");
            }

            if (failure is not null && failure is not JxlFormatException and not UnknownImageFormatException)
            {
                Assert.Fail($"{Path.GetFileName(Path.GetDirectoryName(path))} (trial {trial}): a damaged variant threw {Describe(failure)}");
            }
        }
    }

    private static Exception? TryDecode(byte[] data)
    {
        try
        {
            using var image = Image.Load(new MemoryStream(data));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Describe(Exception ex)
    {
        var inner = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : ex;
        return inner.GetType().Name + ": " + inner.Message + Environment.NewLine + inner.StackTrace;
    }
}
