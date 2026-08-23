namespace PeachImage.Tests.Formats.Tiff.Unit.Decoding;

/// <summary>
/// Test-only TIFF LZW encoder (MSB-first bit packing, "early change" code-width growth — the same rules
/// <c>TiffLzwDecoder</c> implements) used solely to generate correct, round-trippable fixtures for
/// <see cref="TiffLzwDecoderTests"/> without hand-computing every bitstream. Not shipped, and deliberately
/// not optimized — a plain dictionary-based encoder is enough to prove round-trip correctness; the decoder's
/// actual spec-conformance is separately cross-checked against a genuinely hand-computed case (see
/// <see cref="TiffLzwDecoderTests.Decode_HandComputedThreeRepeatedBytes_MatchesOriginal"/>) and, at the
/// corpus-test level, against real libtiff/image-tiff-produced files via the <c>ffmpeg</c> reference
/// baseline (see the project plan) — this encoder's own correctness isn't itself the thing being verified.
/// </summary>
/// <remarks>
/// The encoder's own code-width bump check is deliberately <em>one code later</em> than the decoder's
/// (<c>nextCode &gt; bumpThreshold</c> here, vs. <c>nextCode &gt;= bumpThreshold</c> in <c>TiffLzwDecoder</c>,
/// both confirmed against libtiff's real <c>tif_lzw.c</c> decoder source). This isn't an inconsistency —
/// it's LZW's inherent "decoder lags the encoder's table by exactly one code" property: a decoder only
/// learns table entry N's full content when it sees entry N's *first character* while decoding the
/// *following* code, one step behind the encoder, which already knows entry N's content the moment it adds
/// it. An encoder that switched width using the same raw threshold the decoder uses would switch one code
/// too early for a real decoder to follow; empirically confirmed here by round-tripping data long enough to
/// cross the 511/1023/2047 thresholds (short inputs never reach them, so this asymmetry is invisible to
/// short fixtures and only shows up under real stress — a lesson worth keeping visible, not just fixing).
/// </remarks>
internal static class TiffLzwTestEncoder
{
    private const int ClearCode = 256;
    private const int EndCode = 257;
    private const int FirstFreeCode = 258;
    private const int MinCodeWidth = 9;
    private const int MaxCodeWidth = 12;
    private const int MaxCodeTableSize = 4096;

    public static byte[] Encode(byte[] input)
    {
        var output = new List<byte>();
        var writer = new BitWriter(output);
        var table = new Dictionary<(int Prefix, byte Suffix), int>();
        int nextCode = FirstFreeCode;
        int codeWidth = MinCodeWidth;
        int bumpThreshold = (1 << codeWidth) - 1;

        writer.WriteCode(ClearCode, codeWidth);

        if (input.Length == 0)
        {
            writer.WriteCode(EndCode, codeWidth);
            writer.Flush();
            return output.ToArray();
        }

        int currentPrefix = input[0];

        for (int i = 1; i < input.Length; i++)
        {
            byte symbol = input[i];
            if (table.TryGetValue((currentPrefix, symbol), out int existingCode))
            {
                currentPrefix = existingCode;
                continue;
            }

            writer.WriteCode(currentPrefix, codeWidth);

            if (nextCode < MaxCodeTableSize)
            {
                table[(currentPrefix, symbol)] = nextCode;
                nextCode++;
                if (nextCode > bumpThreshold && codeWidth < MaxCodeWidth)
                {
                    codeWidth++;
                    bumpThreshold = (1 << codeWidth) - 1;
                }
            }
            else
            {
                writer.WriteCode(ClearCode, codeWidth);
                table.Clear();
                nextCode = FirstFreeCode;
                codeWidth = MinCodeWidth;
                bumpThreshold = (1 << codeWidth) - 1;
            }

            currentPrefix = symbol;
        }

        writer.WriteCode(currentPrefix, codeWidth);
        writer.WriteCode(EndCode, codeWidth);
        writer.Flush();

        return output.ToArray();
    }

    private sealed class BitWriter(List<byte> output)
    {
        private ulong _buffer;
        private int _bitCount;

        public void WriteCode(int code, int bits)
        {
            _buffer = (_buffer << bits) | (uint)code;
            _bitCount += bits;

            while (_bitCount >= 8)
            {
                _bitCount -= 8;
                output.Add((byte)(_buffer >> _bitCount));
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                output.Add((byte)(_buffer << (8 - _bitCount)));
                _bitCount = 0;
            }
        }
    }
}
