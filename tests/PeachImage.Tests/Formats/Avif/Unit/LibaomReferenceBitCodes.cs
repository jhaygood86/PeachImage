namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent, fresh transcription of libaom's own <c>aom_dsp/bitwriter_buffer.c</c> /
/// <c>aom_dsp/bitreader_buffer.c</c> -- the plain MSB-first bit buffer pair AV1's uncompressed header syntax
/// (spec §4.10) uses, mirrored in this project by <see cref="PeachImage.Formats.Avif.Encoder.Av1.Av1BitWriter"/>
/// and <see cref="PeachImage.Formats.Avif.Decoding.Av1.Av1BitReader"/>. Deliberately does NOT call either of
/// those types, or <c>Av1CdfAdaptation.FloorLog2</c>, so a test comparing this class's output against theirs is
/// a genuine check against libaom's own real algorithm -- not merely a check that PeachImage's own
/// writer/reader pair are each other's inverse, which a self-consistently-wrong pair could still pass (the
/// same "genuine second source" discipline as <c>LibaomReferenceWht</c>/<c>LibaomReferenceCfl</c> elsewhere in
/// this directory).
///
/// <para><see cref="LibaomReferenceBitWriter"/> covers <c>aom_wb_write_bit</c>, <c>aom_wb_write_literal</c>,
/// <c>aom_wb_write_unsigned_literal</c>, <c>aom_wb_write_uvlc</c>, and the static <c>wb_write_primitive_quniform</c>
/// helper (the real algorithm behind AV1 spec's <c>ns(n)</c> non-symmetric encoding, per
/// <c>aom_wb_write_signed_primitive_refsubexpfin</c>'s own call chain). <see cref="LibaomReferenceBitReader"/>
/// is its read-side mirror, covering <c>aom_rb_read_bit</c>, <c>aom_rb_read_literal</c>,
/// <c>aom_rb_read_unsigned_literal</c>, <c>aom_rb_read_uvlc</c>, and <c>aom_rb_read_primitive_quniform</c>.</para>
///
/// <para>Note libaom's own <c>aom_wb_write_inv_signed_literal</c>/<c>aom_rb_read_inv_signed_literal</c> --
/// despite the name -- are NOT the AV1 spec's <c>su(n)</c> (they write/read <c>bits + 1</c> bits of raw
/// two's-complement, one more than <c>n</c>). The spec's actual <c>su(n)</c> (§4.10.6) reads exactly <paramref
/// name="n"/> bits total as an <c>f(n)</c>, then reinterprets the top bit as a sign -- which is algebraically
/// just "the raw two's-complement encoding of the value in <paramref name="n"/> bits", i.e. plain
/// <c>WriteLiteral(value, n)</c>/<c>ReadLiteral(n)</c> with C#'s own arithmetic (sign-extending) right shift on
/// <see langword="long"/> doing the sign reinterpretation for free -- matching both this file's
/// <see cref="LibaomReferenceBitWriter.WriteSuLiteral"/>/<see cref="LibaomReferenceBitReader.ReadSuLiteral"/>
/// and the spec pseudocode directly, independent of how <c>Av1BitWriter.WriteSu</c>/<c>Av1BitReader.ReadSu</c>
/// happen to compute it (they add/subtract <c>2 * signMask</c> explicitly instead of relying on shift
/// semantics).</para>
/// </summary>
internal sealed class LibaomReferenceBitWriter
{
    private readonly List<bool> _bits = new();

    /// <summary>Total bits written so far -- matches <c>aom_write_bit_buffer.bit_offset</c>.</summary>
    public int BitOffset => _bits.Count;

    /// <summary><c>aom_wb_write_bit</c>.</summary>
    public void WriteBit(int bit) => _bits.Add(bit != 0);

    /// <summary><c>aom_wb_write_literal</c>: writes <paramref name="bits"/> bits of <paramref name="data"/>, MSB first.</summary>
    public void WriteLiteral(long data, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            WriteBit((int)((data >> bit) & 1));
        }
    }

    /// <summary><c>aom_wb_write_unsigned_literal</c>: same bit-packing as <see cref="WriteLiteral"/>, unsigned source, up to 32 bits.</summary>
    public void WriteUnsignedLiteral(uint data, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            WriteBit((int)((data >> bit) & 1));
        }
    }

    /// <summary>
    /// AV1 spec §4.10.6 <c>su(n)</c>, transcribed straight from the spec pseudocode rather than from
    /// <c>Av1BitWriter.WriteSu</c>'s explicit sign-mask arithmetic: <paramref name="value"/>'s raw
    /// two's-complement bit pattern in <paramref name="n"/> bits IS the <c>su(n)</c> wire encoding, because
    /// the spec's own decode step ("if the top bit is set, subtract 2 * signMask") is exactly what
    /// reinterpreting an n-bit two's-complement pattern as signed does.
    /// </summary>
    public void WriteSuLiteral(int value, int n) => WriteLiteral(value, n);

    /// <summary>
    /// Fresh loop-based floor(log2) (libaom's own portable <c>get_msb</c>, <c>aom_ports/bitops.h</c>) --
    /// deliberately not <c>Av1CdfAdaptation.FloorLog2</c>, which is covered by its own independent exhaustive
    /// port in <c>Av1Log2Tests</c> and would otherwise make this class's uvlc/quniform checks partly
    /// self-referential.
    /// </summary>
    public static int GetMsb(uint value)
    {
        int msb = -1;
        while (value != 0)
        {
            value >>= 1;
            msb++;
        }

        return msb;
    }

    /// <summary><c>aom_wb_write_uvlc</c>: Exp-Golomb-style variable length unsigned code.</summary>
    public void WriteUvlc(uint value)
    {
        uint v = value + 1;
        int leadingZeroes = GetMsb(v);
        WriteLiteral(0, leadingZeroes);
        WriteUnsignedLiteral(v, leadingZeroes + 1);
    }

    /// <summary><c>wb_write_primitive_quniform</c>: the real algorithm behind spec's <c>ns(n)</c> for a value in <c>[0, n)</c>.</summary>
    public void WriteQuniform(int n, int v)
    {
        if (n <= 1)
        {
            return;
        }

        int l = GetMsb((uint)n) + 1;
        int m = (1 << l) - n;
        if (v < m)
        {
            WriteLiteral(v, l - 1);
        }
        else
        {
            WriteLiteral(m + ((v - m) >> 1), l - 1);
            WriteBit((v - m) & 1);
        }
    }

    /// <summary>Packs the written bits MSB-first into bytes, zero-padding the final byte -- matches <c>Av1BitWriter.ToArray</c>'s own contract for byte-exact comparison.</summary>
    public byte[] ToByteArray()
    {
        int byteLength = (_bits.Count + 7) >> 3;
        var result = new byte[byteLength];
        for (int i = 0; i < _bits.Count; i++)
        {
            if (_bits[i])
            {
                result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        return result;
    }
}

/// <summary>Read-side mirror of <see cref="LibaomReferenceBitWriter"/> -- see its remarks.</summary>
internal sealed class LibaomReferenceBitReader
{
    private readonly byte[] _data;
    private int _bitOffset;

    public LibaomReferenceBitReader(byte[] data) => _data = data;

    /// <summary>Total bits consumed so far -- matches <c>aom_read_bit_buffer.bit_offset</c>.</summary>
    public int BitOffset => _bitOffset;

    /// <summary><c>aom_rb_read_bit</c>.</summary>
    public int ReadBit()
    {
        int p = _bitOffset >> 3;
        int q = 7 - (_bitOffset & 7);
        int bit = (_data[p] >> q) & 1;
        _bitOffset++;
        return bit;
    }

    /// <summary><c>aom_rb_read_literal</c>.</summary>
    public int ReadLiteral(int bits)
    {
        int value = 0;
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            value |= ReadBit() << bit;
        }

        return value;
    }

    /// <summary><c>aom_rb_read_unsigned_literal</c>.</summary>
    public uint ReadUnsignedLiteral(int bits)
    {
        uint value = 0;
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            value |= (uint)ReadBit() << bit;
        }

        return value;
    }

    /// <summary>See <see cref="LibaomReferenceBitWriter.WriteSuLiteral"/>: reads the raw n-bit two's-complement pattern directly via a sign-extending shift, the spec-literal form of <c>su(n)</c>.</summary>
    public int ReadSuLiteral(int n)
    {
        uint raw = ReadUnsignedLiteral(n);
        long shifted = (long)raw << (64 - n);
        return (int)(shifted >> (64 - n));
    }

    /// <summary><c>aom_rb_read_uvlc</c>.</summary>
    public uint ReadUvlc()
    {
        int leadingZeros = 0;
        while (leadingZeros < 32 && ReadBit() == 0)
        {
            leadingZeros++;
        }

        if (leadingZeros == 32)
        {
            return uint.MaxValue;
        }

        uint baseValue = (1u << leadingZeros) - 1;
        uint value = ReadUnsignedLiteral(leadingZeros);
        return baseValue + value;
    }

    /// <summary><c>aom_rb_read_primitive_quniform</c>.</summary>
    public int ReadQuniform(int n)
    {
        if (n <= 1)
        {
            return 0;
        }

        int l = LibaomReferenceBitWriter.GetMsb((uint)n) + 1;
        int m = (1 << l) - n;
        int v = ReadLiteral(l - 1);
        return v < m ? v : ((v << 1) - m + ReadBit());
    }
}
