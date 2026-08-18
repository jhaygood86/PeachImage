namespace PeachImage.Tests.Internal;

/// <summary>
/// A single skipped <see cref="TheoryDataRow{T}"/> reporting corpus unavailability to a corpus-driven
/// <c>[Theory]</c> test as a genuine skip. Plain <c>yield break</c>-ing an empty <c>MemberData</c> source
/// (this codebase's original approach) means the theory has zero data rows -- xUnit v3 reports that as a
/// hard "No data found for X" failure, not a skip, so a transient/rate-limited corpus fetch failure (see
/// <c>GitHubAuth.cs</c>) looked identical to a real test regression in CI. Yielding this sentinel instead
/// gives the theory exactly one row whose execution is skipped via xUnit v3's per-row <c>Skip</c> support,
/// matching what <c>CorpusAvailabilityTests</c> already does for its own plain <c>[Fact]</c>.
/// </summary>
internal static class CorpusSkip
{
    public static TheoryDataRow<string> Row(string reason) => new(string.Empty) { Skip = reason };
}
