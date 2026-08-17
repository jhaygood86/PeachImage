using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Formats.Avif.Container;

/// <summary>
/// Writes a minimal, single-item, non-grid, opaque AVIF file -- the write-side inverse of
/// <see cref="AvifContainerReader"/>, restricted to exactly what this encoder ever produces: one <c>av01</c>
/// color item, no alpha, no grid, no animation. Builds the box tree bottom-up via <see cref="AvifBoxWriter"/>
/// rather than streaming with backpatching (see its remarks). Includes an <c>hdlr</c> box even though
/// <see cref="AvifMetaBoxParser"/> tolerates its absence -- real-world decoders and browsers are not so
/// lenient, and this repo's own reader is the wrong bar for interop correctness on the write side.
/// Metadata (ICC/EXIF/XMP) re-emission is deferred in v1: no <c>colr</c> or metadata item is written even
/// if the source image carries profiles.
/// </summary>
internal static class AvifContainerWriter
{
    private const uint ItemId = 1;

    public static void Write(Stream stream, Av1EncodedFrame frame)
    {
        byte[] ftyp = BuildFtyp();
        byte[] meta = BuildMeta(frame, mdatPayloadOffset: 0);

        // iloc's extent-offset field is a fixed-size (4-byte) slot regardless of its value, so meta's total
        // length is already final -- rebuild once more with the real mdat offset now that it's known.
        int mdatPayloadOffset = ftyp.Length + meta.Length + 8;
        meta = BuildMeta(frame, mdatPayloadOffset);

        stream.Write(ftyp);
        stream.Write(meta);

        byte[] mdatHeader = new byte[8];
        AvifBoxWriter.WriteUInt32(mdatHeader, 0, (uint)(8 + frame.ObuBytes.Length));
        AvifBoxWriter.WriteFourCc(mdatHeader, 4, "mdat");
        stream.Write(mdatHeader);
        stream.Write(frame.ObuBytes);
    }

    private static byte[] BuildFtyp()
    {
        var payload = new byte[8 + (4 * 3)];
        AvifBoxWriter.WriteFourCc(payload, 0, "avif"); // major_brand
        AvifBoxWriter.WriteUInt32(payload, 4, 0); // minor_version
        AvifBoxWriter.WriteFourCc(payload, 8, "avif");
        AvifBoxWriter.WriteFourCc(payload, 12, "mif1");
        AvifBoxWriter.WriteFourCc(payload, 16, "miaf");
        return AvifBoxWriter.Box("ftyp", payload);
    }

    private static byte[] BuildMeta(Av1EncodedFrame frame, int mdatPayloadOffset)
    {
        byte[] hdlr = BuildHdlr();
        byte[] pitm = BuildPitm();
        byte[] iinf = BuildIinf();
        byte[] iloc = BuildIloc(mdatPayloadOffset, frame.ObuBytes.Length);
        byte[] iprp = BuildIprp(frame);

        return AvifBoxWriter.FullBox("meta", version: 0, flags: 0, AvifBoxWriter.Concat([hdlr, pitm, iinf, iloc, iprp]));
    }

    private static byte[] BuildHdlr()
    {
        var payload = new byte[20 + 1];
        AvifBoxWriter.WriteUInt32(payload, 0, 0); // pre_defined
        AvifBoxWriter.WriteFourCc(payload, 4, "pict"); // handler_type
        // reserved[3] (12 bytes) already zero; name cstring (1 zero byte) already zero.
        return AvifBoxWriter.FullBox("hdlr", version: 0, flags: 0, payload);
    }

    private static byte[] BuildPitm()
    {
        var payload = new byte[2];
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)ItemId);
        return AvifBoxWriter.FullBox("pitm", version: 0, flags: 0, payload);
    }

    private static byte[] BuildIinf()
    {
        byte[] infe = BuildInfe();
        var payload = new byte[2 + infe.Length];
        AvifBoxWriter.WriteUInt16(payload, 0, 1); // entry_count
        Array.Copy(infe, 0, payload, 2, infe.Length);
        return AvifBoxWriter.FullBox("iinf", version: 0, flags: 0, payload);
    }

    private static byte[] BuildInfe()
    {
        // item_id(2) + item_protection_index(2) + item_type(4) + item_name cstring (empty -> 1 zero byte).
        var payload = new byte[2 + 2 + 4 + 1];
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)ItemId);
        AvifBoxWriter.WriteUInt16(payload, 2, 0); // item_protection_index
        AvifBoxWriter.WriteFourCc(payload, 4, "av01");

        // infe version 2 so AvifItemInfoBox.Parse's modern branch (with an explicit item_type field) applies.
        return AvifBoxWriter.FullBox("infe", version: 2, flags: 0, payload);
    }

    private static byte[] BuildIloc(int mdatPayloadOffset, int length)
    {
        // offsetSize=4, lengthSize=4, baseOffsetSize=0, indexSize=0 (reserved, unused at version 0).
        var payload = new byte[1 + 1 + 2 + 2 + 2 + 2 + 4 + 4];
        int i = 0;
        payload[i++] = 0x44; // (offsetSize=4 << 4) | lengthSize=4
        payload[i++] = 0x00; // (baseOffsetSize=0 << 4) | indexSize=0
        AvifBoxWriter.WriteUInt16(payload, i, 1); // item_count
        i += 2;
        AvifBoxWriter.WriteUInt16(payload, i, (ushort)ItemId);
        i += 2;
        AvifBoxWriter.WriteUInt16(payload, i, 0); // data_reference_index
        i += 2;
        // base_offset omitted (baseOffsetSize == 0)
        AvifBoxWriter.WriteUInt16(payload, i, 1); // extent_count
        i += 2;
        AvifBoxWriter.WriteUInt32(payload, i, (uint)mdatPayloadOffset);
        i += 4;
        AvifBoxWriter.WriteUInt32(payload, i, (uint)length);

        return AvifBoxWriter.FullBox("iloc", version: 0, flags: 0, payload);
    }

    private static byte[] BuildIprp(Av1EncodedFrame frame)
    {
        byte[] ispe = BuildIspe(frame.Width, frame.Height);
        byte[] av1C = BuildAv1Config(frame.MonoChrome);
        byte[] pixi = BuildPixi(frame.MonoChrome);
        byte[] ipco = AvifBoxWriter.Box("ipco", ispe, av1C, pixi);
        byte[] ipma = BuildIpma();

        return AvifBoxWriter.Box("iprp", ipco, ipma);
    }

    private static byte[] BuildIspe(int width, int height)
    {
        var payload = new byte[8];
        AvifBoxWriter.WriteUInt32(payload, 0, (uint)width);
        AvifBoxWriter.WriteUInt32(payload, 4, (uint)height);
        return AvifBoxWriter.FullBox("ispe", version: 0, flags: 0, payload);
    }

    private static byte[] BuildAv1Config(bool monoChrome)
    {
        var payload = new byte[4];
        payload[0] = (byte)(0x80 | 1); // marker=1, version=1
        payload[1] = (byte)(((Av1SequenceHeaderWriter.SeqProfile & 0x7) << 5) | (Av1SequenceHeaderWriter.SeqLevelIdx0 & 0x1F));
        int b2 = 0; // seq_tier0=0, high_bitdepth=0 (8-bit), twelve_bit=0
        if (monoChrome)
        {
            b2 |= 0x10;
        }
        else
        {
            b2 |= 0x08; // chroma_subsampling_x
            b2 |= 0x04; // chroma_subsampling_y
        }

        payload[2] = (byte)b2; // chroma_sample_position = 0 (CSP_UNKNOWN)
        payload[3] = 0;
        return AvifBoxWriter.Box("av1C", payload);
    }

    private static byte[] BuildPixi(bool monoChrome)
    {
        int channels = monoChrome ? 1 : 3;
        var payload = new byte[1 + channels];
        payload[0] = (byte)channels;
        for (int i = 0; i < channels; i++)
        {
            payload[1 + i] = 8;
        }

        return AvifBoxWriter.FullBox("pixi", version: 0, flags: 0, payload);
    }

    private static byte[] BuildIpma()
    {
        // ispe=1, av1C=2, pixi=3 (ipco declaration order) -- ispe and av1C marked essential (top bit set),
        // pixi is not (matches AvifItemPropertiesBox.ParseIpma's "essential" bit convention; this decoder
        // ignores the flag either way, but real decoders may enforce it for unrecognized essential properties).
        var payload = new byte[4 + 2 + 1 + 3];
        int i = 0;
        AvifBoxWriter.WriteUInt32(payload, i, 1); // entry_count
        i += 4;
        AvifBoxWriter.WriteUInt16(payload, i, (ushort)ItemId);
        i += 2;
        payload[i++] = 3; // association_count
        payload[i++] = 0x80 | 1; // ispe, essential
        payload[i++] = 0x80 | 2; // av1C, essential
        payload[i] = 3; // pixi, not essential

        return AvifBoxWriter.FullBox("ipma", version: 0, flags: 0, payload);
    }
}
