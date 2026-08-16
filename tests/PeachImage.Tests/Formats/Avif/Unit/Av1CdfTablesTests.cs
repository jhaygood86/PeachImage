using System.Reflection;
using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Structural validation of every extracted <see cref="Av1CdfTables"/> array, applied automatically
/// (via reflection, so newly added tables are covered without editing this file) to every leaf CDF row
/// found anywhere in the nested jagged arrays. Per the AV1 spec's own documented invariant (§8.2.6's
/// note: "cdf[N-1] will be equal to 1 &lt;&lt; 15"), every leaf row of length N+1 must have
/// <c>row[N-1] == 32768</c> and <c>row[N] == 0</c> (the adaptation counter, always zero for a freshly
/// extracted default table), and the cumulative values at indices <c>[0, N-1]</c> must be
/// non-decreasing. A real transcription error (a swapped or mistyped digit) would very likely violate
/// one of these across a table this large, making this a strong, cheap cross-check independent of
/// re-deriving the numbers by hand.
/// </summary>
public class Av1CdfTablesTests
{
    public static IEnumerable<object[]> AllTableFields() =>
        typeof(Av1CdfTables)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => new object[] { f.Name, f.GetValue(null)! });

    [Theory]
    [MemberData(nameof(AllTableFields))]
    public void EveryLeafRow_SatisfiesCdfInvariants(string fieldName, object table)
    {
        int rowsChecked = 0;
        foreach (ushort[] row in EnumerateLeafRows(table))
        {
            rowsChecked++;
            int n = row.Length - 1; // row has N+1 entries: cumulative[0..N-1] then the counter at [N]

            Assert.True(n >= 1, $"{fieldName}: leaf row too short ({row.Length} entries).");
            Assert.True(row[n - 1] == 32768, $"{fieldName}: row[{n - 1}] expected 32768 (cdf[N-1]), was {row[n - 1]}. Row: [{string.Join(", ", row)}]");
            Assert.True(row[n] == 0, $"{fieldName}: row[{n}] (adaptation counter) expected 0, was {row[n]}. Row: [{string.Join(", ", row)}]");

            for (int i = 1; i < n; i++)
            {
                Assert.True(row[i] >= row[i - 1], $"{fieldName}: row is not non-decreasing at index {i} ({row[i - 1]} -> {row[i]}). Row: [{string.Join(", ", row)}]");
            }

            Assert.True(row[0] >= 0 && row[0] <= 32768, $"{fieldName}: row[0] out of range: {row[0]}.");
        }

        Assert.True(rowsChecked > 0, $"{fieldName}: no leaf rows found (unexpected array shape).");
    }

    private static IEnumerable<ushort[]> EnumerateLeafRows(object value)
    {
        if (value is ushort[] leaf)
        {
            yield return leaf;
            yield break;
        }

        if (value is Array array)
        {
            foreach (object? item in array)
            {
                if (item is null)
                {
                    continue;
                }

                foreach (var row in EnumerateLeafRows(item))
                {
                    yield return row;
                }
            }
        }
    }
}
