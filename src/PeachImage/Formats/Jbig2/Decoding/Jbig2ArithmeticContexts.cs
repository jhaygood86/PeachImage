using System.Buffers;
using PeachImage.Formats.Shared.Arithmetic;

namespace PeachImage.Formats.Jbig2.Decoding;

/// <summary>
/// The adaptive contexts of every arithmetic integer decoding procedure of T.88 Annex A, plus the symbol-ID contexts.
/// One instance backs a symbol dictionary or text region; the buffers are pooled and returned by <see cref="Dispose"/>.
/// </summary>
internal sealed class Jbig2ArithmeticContexts : IDisposable
{
    private const int IntegerContextSize = 512;
    private readonly byte[][] _integerContexts = new byte[13][];
    private byte[]? _symbolId;

    public Jbig2ArithmeticContexts(int symbolCodeLength)
    {
        for (int i = 0; i < _integerContexts.Length; i++)
        {
            _integerContexts[i] = Rent(IntegerContextSize);
        }

        if (symbolCodeLength > 30)
        {
            throw new Jbig2DecodingException("JBIG2 symbol code length is out of range.");
        }

        SymbolCodeLength = symbolCodeLength;
        _symbolId = Rent(1 << (symbolCodeLength + 1));
    }

    public int SymbolCodeLength { get; }

    public byte[] DeltaHeight => _integerContexts[0];

    public byte[] DeltaWidth => _integerContexts[1];

    public byte[] ExportRun => _integerContexts[2];

    public byte[] AggregateInstances => _integerContexts[3];

    public byte[] DeltaT => _integerContexts[4];

    public byte[] FirstS => _integerContexts[5];

    public byte[] DeltaS => _integerContexts[6];

    public byte[] CurrentT => _integerContexts[7];

    public byte[] RefinementIndicator => _integerContexts[8];

    public byte[] RefinementDeltaWidth => _integerContexts[9];

    public byte[] RefinementDeltaHeight => _integerContexts[10];

    public byte[] RefinementDeltaX => _integerContexts[11];

    public byte[] RefinementDeltaY => _integerContexts[12];

    /// <summary>Decodes one value with the integer procedure whose contexts are <paramref name="contexts"/>. Returns false for the out-of-band value.</summary>
    public static bool DecodeInteger(ref MqDecoder decoder, byte[] contexts, out int value)
    {
        int prev = 1;
        int sign = ReadBit(ref decoder, contexts, ref prev);
        long magnitude;
        if (ReadBit(ref decoder, contexts, ref prev) == 0)
        {
            magnitude = ReadBits(ref decoder, contexts, ref prev, 2);
        }
        else if (ReadBit(ref decoder, contexts, ref prev) == 0)
        {
            magnitude = ReadBits(ref decoder, contexts, ref prev, 4) + 4;
        }
        else if (ReadBit(ref decoder, contexts, ref prev) == 0)
        {
            magnitude = ReadBits(ref decoder, contexts, ref prev, 6) + 20;
        }
        else if (ReadBit(ref decoder, contexts, ref prev) == 0)
        {
            magnitude = ReadBits(ref decoder, contexts, ref prev, 8) + 84;
        }
        else if (ReadBit(ref decoder, contexts, ref prev) == 0)
        {
            magnitude = ReadBits(ref decoder, contexts, ref prev, 12) + 340;
        }
        else
        {
            magnitude = ReadBits(ref decoder, contexts, ref prev, 32) + 4436;
        }

        if (sign == 0)
        {
            value = (int)Math.Min(magnitude, int.MaxValue);
            return true;
        }

        if (magnitude > 0)
        {
            value = (int)-Math.Min(magnitude, int.MaxValue);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Decodes an integer that must not be out-of-band.</summary>
    public static int DecodeRequiredInteger(ref MqDecoder decoder, byte[] contexts)
    {
        if (!DecodeInteger(ref decoder, contexts, out int value))
        {
            throw new Jbig2DecodingException("JBIG2 data contains an unexpected out-of-band value.");
        }

        return value;
    }

    /// <summary>Decodes a symbol ID (the IAID procedure).</summary>
    public int DecodeSymbolId(ref MqDecoder decoder)
    {
        int prev = 1;
        for (int i = 0; i < SymbolCodeLength; i++)
        {
            int bit = decoder.Decode(ref _symbolId![prev]);
            prev = (prev << 1) | bit;
        }

        return prev - (1 << SymbolCodeLength);
    }

    public void Dispose()
    {
        foreach (var contexts in _integerContexts)
        {
            ArrayPool<byte>.Shared.Return(contexts);
        }

        if (_symbolId is not null)
        {
            ArrayPool<byte>.Shared.Return(_symbolId);
            _symbolId = null;
        }
    }

    /// <summary>Rents a zeroed context buffer.</summary>
    public static byte[] Rent(int size)
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(size);
        array.AsSpan(0, size).Clear();
        return array;
    }

    private static int ReadBit(ref MqDecoder decoder, byte[] contexts, ref int prev)
    {
        int bit = decoder.Decode(ref contexts[prev]);
        prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
        return bit;
    }

    private static long ReadBits(ref MqDecoder decoder, byte[] contexts, ref int prev, int count)
    {
        long value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)ReadBit(ref decoder, contexts, ref prev);
        }

        return value;
    }
}
