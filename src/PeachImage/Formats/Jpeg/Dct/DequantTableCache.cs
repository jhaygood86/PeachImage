namespace PeachImage.Formats.Jpeg.Dct;

/// <summary>
/// The selected inverse DCT kernel's dequantization tables (<see cref="IInverseDctKernel.PrepareDequantTable"/>) for full-size decodes,
/// shared between decodes: nearly every JPEG carries one of a few quantization tables, and a prepared table is 64 floats that nothing
/// modifies, so a decode that meets a table it has seen before reuses the prepared one instead of building another.
/// </summary>
internal static class DequantTableCache
{
    private const int Slots = 16;

    // Direct-mapped by the table's contents; a collision simply replaces the older entry, which bounds the memory at 16 tables
    // however many different tables a hostile input offers.
    private static readonly Entry?[] Cache = new Entry?[Slots];

    private sealed record Entry(ushort[] Key, float[] Table);

    /// <summary>The prepared form of <paramref name="quantization"/> (64 values, natural order) for <see cref="DctKernelSelector.Inverse"/>. Callers must not modify it.</summary>
    public static float[] Get(ReadOnlySpan<ushort> quantization)
    {
        uint hash = 2166136261;
        foreach (ushort value in quantization)
        {
            hash = (hash ^ value) * 16777619;
        }

        ref var slot = ref Cache[(int)((hash ^ (hash >> 15)) % Slots)];
        var cached = Volatile.Read(ref slot);
        if (cached is not null && cached.Key.AsSpan().SequenceEqual(quantization))
        {
            return cached.Table;
        }

        var table = DctKernelSelector.Inverse.PrepareDequantTable(quantization);
        Volatile.Write(ref slot, new Entry(quantization.ToArray(), table));
        return table;
    }
}
