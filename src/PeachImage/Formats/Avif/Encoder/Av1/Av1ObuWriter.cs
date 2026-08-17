using PeachImage.Formats.Avif.Decoding.Av1;

namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// Writes AV1 Open Bitstream Units (spec §5.3 <c>open_bitstream_unit()</c>) -- the write-side mirror of
/// <see cref="Av1ObuReader"/>. Byte-level framing only; payload bytes are supplied pre-built by the
/// header/tile-group writers. Always writes <c>obu_has_size_field=1</c> and never an extension header,
/// matching what real AVIF item data (and this codebase's own reader) expects.
/// </summary>
internal static class Av1ObuWriter
{
    /// <summary>Appends an OBU (header + leb128 size + payload) for <paramref name="obuType"/> to <paramref name="output"/>.</summary>
    public static void WriteObu(List<byte> output, int obuType, ReadOnlySpan<byte> payload)
    {
        // bit7 obu_forbidden_bit=0, bits6-3 obu_type, bit2 obu_extension_flag=0, bit1 obu_has_size_field=1, bit0 reserved=0.
        byte headerByte = (byte)(((obuType & 0xF) << 3) | 0x02);
        output.Add(headerByte);
        WriteLeb128(output, (ulong)payload.Length);
        output.AddRange(payload.ToArray());
    }

    /// <summary>Writes <paramref name="value"/> as a <c>leb128()</c> value (spec §4.10.5), inverse of the private <c>ReadLeb128</c> in <see cref="Av1ObuReader"/>.</summary>
    public static void WriteLeb128(List<byte> output, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }

            output.Add(b);
        }
        while (value != 0);
    }
}
