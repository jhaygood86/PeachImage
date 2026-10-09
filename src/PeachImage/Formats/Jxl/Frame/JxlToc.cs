using PeachImage.Formats.Jxl.Bitstream;
using PeachImage.Formats.Jxl.Entropy;

namespace PeachImage.Formats.Jxl.Frame;

/// <summary>
/// A frame's table of contents: the byte size and (after the optional permutation) the logical section id of each
/// section. Sections are, in order: the global DC section, one per DC group, the global AC section, then one per
/// AC group per pass.
/// </summary>
internal sealed class JxlToc
{
    private const int MaxEntries = 65536;
    private const int PermutationContexts = 8;

    private JxlToc(uint[] sizes, int[] ids)
    {
        Sizes = sizes;
        Ids = ids;
    }

    /// <summary>The byte size of each section in file order.</summary>
    public uint[] Sizes { get; }

    /// <summary>The logical section id (0 = DC global, ...) of each section in file order.</summary>
    public int[] Ids { get; }

    /// <summary>The number of sections a frame with these dimensions has.</summary>
    public static int EntryCount(int numGroups, int numDcGroups, int numPasses) =>
        numGroups == 1 && numPasses == 1 ? 1 : 2 + numDcGroups + (numGroups * numPasses);

    /// <summary>Reads the table of contents, leaving <paramref name="br"/> at the byte boundary where section data begins.</summary>
    public static JxlToc Read(ref JxlBitReader br, int entries)
    {
        if (entries > MaxEntries)
        {
            throw new JxlDecodingException("Too many TOC entries.");
        }

        int[]? permutation = null;
        if (br.ReadBool())
        {
            CheckBitBudget(in br, entries);
            permutation = DecodePermutation(ref br, entries);
        }

        br.ZeroPadToByte();
        CheckBitBudget(in br, entries);
        var sizes = new uint[entries];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = JxlFieldReader.ReadU32(ref br, U32Dist.Bits(10), U32Dist.BitsOffset(14, 1024), U32Dist.BitsOffset(22, 17408), U32Dist.BitsOffset(30, 4211712));
        }

        br.ZeroPadToByte();
        br.ThrowIfOverrun();

        // `sizes[i]` is the i-th section in the file. With a permutation, the file's i-th section carries logical id
        // permutation-inverse: toc[permutation[i]].id = i.
        var ids = new int[entries];
        for (int i = 0; i < ids.Length; i++)
        {
            int index = permutation is null ? i : permutation[i];
            ids[index] = i;
        }

        return new JxlToc(sizes, ids);
    }

    private static void CheckBitBudget(in JxlBitReader br, int entries)
    {
        // Each entry costs at least a 2-bit selector plus 10 bits.
        if (br.IsOverrun || br.BitsRemaining < (long)entries * 12)
        {
            throw new JxlDecodingException("Not enough bytes for the TOC.");
        }
    }

    /// <summary>Decodes an entropy-coded permutation of <paramref name="size"/> elements (a Lehmer code).</summary>
    internal static int[] DecodePermutation(ref JxlBitReader br, int size)
    {
        var code = JxlEntropyCode.Read(ref br, JxlPermutation.NumContexts, out byte[] contextMap);
        using var reader = new JxlSymbolReader(code, ref br);
        int[] permutation = JxlPermutation.Read(ref br, reader, contextMap, skip: 0, size);
        br.ThrowIfOverrun();
        if (!reader.CheckFinalState())
        {
            throw new JxlDecodingException("Invalid ANS stream in the TOC permutation.");
        }

        return permutation;
    }
}
