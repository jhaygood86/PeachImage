using System.Buffers.Binary;
using PeachImage.Formats.Jxl.Encoder.Bitstream;
using PeachImage.Formats.Jxl.Encoder.Entropy;
using PeachImage.Formats.Jxl.Headers;

namespace PeachImage.Formats.Jxl.Encoder.Headers;

/// <summary>
/// Writes an ICC profile as a codestream's ICC stream, the inverse of <c>JxlIccDecoder.Read</c>. The profile is stored
/// as header residuals against the format's prototype header followed by the rest of the bytes inserted verbatim (no
/// tag-table or linear prediction), entropy coded with the format's byte-class context model.
/// </summary>
internal static class JxlIccEncoder
{
    public static void Write(JxlBitWriter writer, byte[] icc)
    {
        byte[] encoded = Predict(icc);
        writer.WriteU64((ulong)encoded.Length);

        var tokens = new JxlTokenList();
        for (int i = 0; i < encoded.Length; i++)
        {
            byte b1 = i > 0 ? encoded[i - 1] : (byte)0;
            byte b2 = i > 1 ? encoded[i - 2] : (byte)0;
            tokens.Add(JxlIccDecoder.Context(i, b1, b2), encoded[i]);
        }

        var plan = JxlEntropyPlan.Create(JxlIccDecoder.NumContexts, tokens);
        plan.WriteHeader(writer);
        plan.WriteStream(writer, tokens);
    }

    /// <summary>Builds the byte string the decoder's <c>Unpredict</c> expands back into <paramref name="icc"/>.</summary>
    internal static byte[] Predict(byte[] icc)
    {
        int size = icc.Length;
        int headerBytes = Math.Min(JxlIccDecoder.HeaderSize, size);

        var commands = new List<byte>();
        if (size > JxlIccDecoder.HeaderSize)
        {
            AddVarInt(commands, 0); // no tag-table commands: the table is inserted as plain bytes
            commands.Add(1);        // insert
            AddVarInt(commands, (ulong)(size - JxlIccDecoder.HeaderSize));
        }

        var result = new List<byte>(size + 16);
        AddVarInt(result, (ulong)size);
        AddVarInt(result, (ulong)commands.Count);
        result.AddRange(commands);

        byte[] header = (byte[])JxlIccDecoder.InitialHeaderPrediction.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
        for (int i = 0; i < headerBytes; i++)
        {
            JxlIccDecoder.PredictHeader(icc.AsSpan(0, i), header, i);
            result.Add((byte)(icc[i] - header[i]));
        }

        for (int i = headerBytes; i < size; i++)
        {
            result.Add(icc[i]);
        }

        return [.. result];
    }

    private static void AddVarInt(List<byte> destination, ulong value)
    {
        while (value >= 0x80)
        {
            destination.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        destination.Add((byte)value);
    }
}
