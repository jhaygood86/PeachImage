namespace PeachImage.Tests.Formats.Jxl.Unit;

/// <summary>An LSB-first bit writer, the inverse of the decoder's bit reader, for building synthetic streams.</summary>
internal sealed class JxlBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitCount;

    public void WriteBits(ulong value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (_bitCount % 8 == 0)
            {
                _bytes.Add(0);
            }

            if (((value >> i) & 1) != 0)
            {
                _bytes[^1] |= (byte)(1 << (_bitCount % 8));
            }

            _bitCount++;
        }
    }

    /// <summary>Writes a canonical prefix code MSB-first (the order a prefix-code decoder consumes bits).</summary>
    public void WriteCodeMsbFirst(uint code, int length)
    {
        for (int i = length - 1; i >= 0; i--)
        {
            WriteBits((code >> i) & 1, 1);
        }
    }

    public byte[] ToArray() => [.. _bytes];
}
