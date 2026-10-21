namespace PeachImage.Formats.Shared.Ccitt;

/// <summary>
/// The ITU-T T.4 run-length code tables (white and black terminating, make-up and extended make-up codes),
/// expanded once into 13-bit direct-lookup tables. Each lookup entry is <c>(codeLength &lt;&lt; 16) | runLength</c>;
/// zero marks a bit pattern that is not a valid code.
/// </summary>
internal static class CcittCodeTables
{
    /// <summary>The number of bits the lookup tables are indexed by — the longest run-length code is 13 bits.</summary>
    public const int LookupBits = 13;

    private static readonly string[] WhiteTerminating =
    [
        "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111",
        "10011", "10100", "00111", "01000", "001000", "000011", "110100", "110101",
        "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100",
        "0101000", "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010",
        "00011011", "00010010", "00010011", "00010100", "00010101", "00010110", "00010111", "00101000",
        "00101001", "00101010", "00101011", "00101100", "00101101", "00000100", "00000101", "00001010",
        "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101", "01011000",
        "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
    ];

    private static readonly string[] WhiteMakeup =
    [
        "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101",
        "01101000", "01100111", "011001100", "011001101", "011010010", "011010011", "011010100", "011010101",
        "011010110", "011010111", "011011000", "011011001", "011011010", "011011011", "010011000", "010011001",
        "010011010", "011000", "010011011",
    ];

    private static readonly string[] BlackTerminating =
    [
        "0000110111", "010", "11", "10", "011", "0011", "0010", "00011",
        "000101", "000100", "0000100", "0000101", "0000111", "00000100", "00000111", "000011000",
        "0000010111", "0000011000", "0000001000", "00001100111", "00001101000", "00001101100", "00000110111", "00000101000",
        "00000010111", "00000011000", "000011001010", "000011001011", "000011001100", "000011001101", "000001101000", "000001101001",
        "000001101010", "000001101011", "000011010010", "000011010011", "000011010100", "000011010101", "000011010110", "000011010111",
        "000001101100", "000001101101", "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111",
        "000001100100", "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111",
        "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110", "000001100111",
    ];

    private static readonly string[] BlackMakeup =
    [
        "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101", "0000001101100",
        "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010", "0000001110011", "0000001110100",
        "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011", "0000001010100", "0000001010101", "0000001011010",
        "0000001011011", "0000001100100", "0000001100101",
    ];

    // Shared by both colors; run lengths 1792, 1856, ..., 2560.
    private static readonly string[] ExtendedMakeup =
    [
        "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
        "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
    ];

    /// <summary>Lookup for white runs, indexed by the next <see cref="LookupBits"/> bits.</summary>
    public static int[] White { get; } = Build(WhiteTerminating, WhiteMakeup);

    /// <summary>Lookup for black runs, indexed by the next <see cref="LookupBits"/> bits.</summary>
    public static int[] Black { get; } = Build(BlackTerminating, BlackMakeup);

    /// <summary>Exposes the code strings of one color as (code bits, run length) pairs so tests can verify they form a prefix-free set.</summary>
    internal static IEnumerable<(string Code, int Run)> EnumerateCodes(bool black)
    {
        string[] terminating = black ? BlackTerminating : WhiteTerminating;
        string[] makeup = black ? BlackMakeup : WhiteMakeup;
        for (int i = 0; i < terminating.Length; i++)
        {
            yield return (terminating[i], i);
        }

        for (int i = 0; i < makeup.Length; i++)
        {
            yield return (makeup[i], 64 * (i + 1));
        }

        for (int i = 0; i < ExtendedMakeup.Length; i++)
        {
            yield return (ExtendedMakeup[i], 1792 + (64 * i));
        }
    }

    private static int[] Build(string[] terminating, string[] makeup)
    {
        var table = new int[1 << LookupBits];
        for (int i = 0; i < terminating.Length; i++)
        {
            Fill(table, terminating[i], i);
        }

        for (int i = 0; i < makeup.Length; i++)
        {
            Fill(table, makeup[i], 64 * (i + 1));
        }

        for (int i = 0; i < ExtendedMakeup.Length; i++)
        {
            Fill(table, ExtendedMakeup[i], 1792 + (64 * i));
        }

        return table;
    }

    private static void Fill(int[] table, string code, int run)
    {
        int length = code.Length;
        int value = 0;
        foreach (char bit in code)
        {
            value = (value << 1) | (bit - '0');
        }

        int shift = LookupBits - length;
        int first = value << shift;
        int count = 1 << shift;
        int entry = (length << 16) | run;
        for (int i = 0; i < count; i++)
        {
            table[first + i] = entry;
        }
    }
}
