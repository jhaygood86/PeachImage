using System.Buffers;
using System.Runtime.CompilerServices;
using PeachImage.Formats.Jxl.Bitstream;

namespace PeachImage.Formats.Jxl.Entropy;

/// <summary>
/// Decodes integers from a <see cref="JxlEntropyCode"/>: rANS or prefix-coded tokens, expanded by the hybrid-integer
/// scheme and, when enabled, LZ77 back-references. Holds the mutable decoder state (ANS state, LZ77 window); the bit
/// reader is passed to each call so the same stream can interleave raw reads.
/// </summary>
internal sealed class JxlSymbolReader : IDisposable
{
    private const int WindowSize = 1 << 20;
    private const int WindowMask = WindowSize - 1;
    private const int NumSpecialDistances = 120;

    // Special distance codes (dx, dy) from WebP lossless.
    private static readonly sbyte[] SpecialDistanceTable =
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2,
        2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0,
        1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0,
        1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0,
        1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3,
        0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2,
        3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6,
        -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    private readonly JxlEntropyCode _code;
    private readonly AliasEntry[] _aliasTables;
    private readonly JxlHuffmanCode[] _huffman;
    private readonly HybridUintConfig[] _configs;
    private readonly bool _usePrefixCode;
    private readonly int _logAlphaSize;
    private readonly int _logEntrySize;
    private readonly int _entrySizeMinusOne;

    private uint _state;

    // LZ77 state.
    private readonly uint[]? _window;
    private readonly uint _lz77Threshold = 1u << 20;
    private readonly uint _lz77MinLength;
    private readonly int _lz77Context;
    private readonly HybridUintConfig _lz77LengthConfig;
    private readonly uint[]? _specialDistances;
    private uint _numDecoded;
    private uint _numToCopy;
    private uint _copyPosition;

    /// <summary>
    /// Creates a reader. For rANS this consumes the 32-bit initial state from <paramref name="br"/>.
    /// <paramref name="distanceMultiplier"/> is the image row stride used by the special LZ77 distance codes (0 for none).
    /// </summary>
    public JxlSymbolReader(JxlEntropyCode code, ref JxlBitReader br, int distanceMultiplier = 0)
    {
        _code = code;
        _aliasTables = code.AliasTables;
        _huffman = code.Huffman;
        _configs = code.UintConfigs;
        _usePrefixCode = code.UsePrefixCode;

        if (_usePrefixCode)
        {
            _state = JxlAliasTable.Signature << 16;
        }
        else
        {
            _state = br.ReadBits(32);
            _logAlphaSize = code.LogAlphaSize;
            _logEntrySize = JxlAliasTable.LogTableSize - code.LogAlphaSize;
            _entrySizeMinusOne = (1 << _logEntrySize) - 1;
        }

        var lz77 = code.Lz77;
        if (!lz77.Enabled)
        {
            return;
        }

        _window = ArrayPool<uint>.Shared.Rent(WindowSize);
        _lz77Context = lz77.DistanceContext;
        _lz77LengthConfig = lz77.LengthConfig;
        _lz77Threshold = lz77.MinSymbol;
        _lz77MinLength = lz77.MinLength;
        if (distanceMultiplier != 0)
        {
            _specialDistances = new uint[NumSpecialDistances];
            for (int i = 0; i < NumSpecialDistances; i++)
            {
                int distance = SpecialDistanceTable[i * 2] + (distanceMultiplier * SpecialDistanceTable[(i * 2) + 1]);
                _specialDistances[i] = (uint)(distance > 1 ? distance : 1);
            }
        }
    }

    /// <summary>Whether this stream uses LZ77.</summary>
    public bool UsesLz77 => _window is not null;

    public void Dispose()
    {
        if (_window is not null)
        {
            ArrayPool<uint>.Shared.Return(_window);
        }
    }

    /// <summary>For rANS streams, whether the final state equals the format's signature (a cheap integrity check). Always true for prefix codes.</summary>
    public bool CheckFinalState() => _state == JxlAliasTable.Signature << 16;

    /// <summary>Decodes one integer using the histogram that <paramref name="context"/> (already a clustered index) selects.</summary>
    public uint ReadHybridUint(int context, ref JxlBitReader br)
    {
        if (_window is null)
        {
            uint token = ReadToken(context, ref br);
            return _configs[context].Decode(token, ref br);
        }

        return ReadHybridUintLz77(context, ref br);
    }

    /// <summary>Decodes one integer for an unclustered <paramref name="context"/>, mapped through <paramref name="contextMap"/>.</summary>
    public uint ReadHybridUint(int context, ref JxlBitReader br, ReadOnlySpan<byte> contextMap) =>
        ReadHybridUint(contextMap[context], ref br);

    private uint ReadHybridUintLz77(int context, ref JxlBitReader br)
    {
        var window = _window!;
        if (_numToCopy > 0)
        {
            return CopyOne(window);
        }

        uint token = ReadToken(context, ref br);
        if (token >= _lz77Threshold)
        {
            _numToCopy = _lz77LengthConfig.Decode(token - _lz77Threshold, ref br) + _lz77MinLength;

            uint distanceToken = ReadToken(_lz77Context, ref br);
            uint distance = _configs[_lz77Context].Decode(distanceToken, ref br);
            if (_specialDistances is not null && distance < NumSpecialDistances)
            {
                distance = _specialDistances[distance];
            }
            else
            {
                distance = distance + 1 - (_specialDistances is null ? 0u : NumSpecialDistances);
            }

            if (distance > _numDecoded)
            {
                distance = _numDecoded;
            }

            if (distance > WindowSize)
            {
                distance = WindowSize;
            }

            _copyPosition = _numDecoded - distance;
            if (distance == 0)
            {
                // Nothing decoded yet: the copy source is a run of zeros.
                int toFill = (int)Math.Min(_numToCopy, (uint)WindowSize);
                Array.Clear(window, 0, toFill);
            }

            if (_numToCopy < _lz77MinLength)
            {
                // The length wrapped around: the stream is corrupt.
                _numToCopy = 0;
                throw new JxlDecodingException("Corrupt LZ77 run length.");
            }

            return CopyOne(window);
        }

        uint value = _configs[context].Decode(token, ref br);
        window[_numDecoded++ & WindowMask] = value;
        return value;
    }

    private uint CopyOne(uint[] window)
    {
        uint value = window[_copyPosition++ & WindowMask];
        _numToCopy--;
        window[_numDecoded++ & WindowMask] = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadToken(int context, ref JxlBitReader br) =>
        _usePrefixCode ? (uint)_huffman[context].ReadSymbol(ref br) : ReadAnsSymbol(context, ref br);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadAnsSymbol(int context, ref JxlBitReader br)
    {
        uint state = _state;
        int residue = (int)(state & (JxlAliasTable.TableSize - 1));
        var table = _aliasTables.AsSpan(context << _logAlphaSize, 1 << _logAlphaSize);
        var (symbol, offset, frequency) = JxlAliasTable.Lookup(table, residue, _logEntrySize, _entrySizeMinusOne);
        state = (uint)((frequency * (long)(state >> JxlAliasTable.LogTableSize)) + offset);
        if (state < (1u << 16))
        {
            state = (state << 16) | br.ReadBits(16);
        }

        _state = state;
        return (uint)symbol;
    }
}
