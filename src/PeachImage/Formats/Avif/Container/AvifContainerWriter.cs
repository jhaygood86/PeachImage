using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Formats.Avif.Container;

/// <summary>
/// Writes a minimal, non-grid AVIF file -- the write-side inverse of <see cref="AvifContainerReader"/>,
/// restricted to exactly what this encoder ever produces: one <c>av01</c> color item, optionally a second
/// independent monochrome <c>av01</c> alpha item referenced via an <c>iref auxl</c> entry (spec-compliant,
/// not a PeachImage-only convention -- see <see cref="AvifItemAssembler.Assemble"/>'s exact parsing of this
/// same reference), no grid, no animation. Builds the box tree bottom-up via <see cref="AvifBoxWriter"/>
/// rather than streaming with backpatching (see its remarks). Includes an <c>hdlr</c> box even though
/// <see cref="AvifMetaBoxParser"/> tolerates its absence -- real-world decoders and browsers are not so
/// lenient, and this repo's own reader is the wrong bar for interop correctness on the write side.
/// Metadata (ICC/EXIF/XMP) re-emission is deferred in v1: no <c>colr</c> or metadata item is written even
/// if the source image carries profiles. Alpha is always straight (non-premultiplied), matching the
/// decoder's own assumption -- no <c>prem</c> signaling exists anywhere in this codebase.
/// </summary>
internal static class AvifContainerWriter
{
    private const uint ColorItemId = 1;
    private const uint AlphaItemId = 2;

    public static void Write(Stream stream, Av1EncodedFrame frame, Av1EncodedFrame? alphaFrame = null)
    {
        byte[] ftyp = BuildFtyp();
        byte[] meta = BuildMeta(frame, alphaFrame, colorOffset: 0, alphaOffset: 0);

        // iloc's extent-offset fields are fixed-size (4-byte) slots regardless of their value, so meta's
        // total length is already final -- rebuild once more with the real mdat offsets now that they're known.
        int mdatPayloadOffset = ftyp.Length + meta.Length + 8;
        int colorOffset = mdatPayloadOffset;
        int alphaOffset = mdatPayloadOffset + frame.ObuBytes.Length;
        meta = BuildMeta(frame, alphaFrame, colorOffset, alphaOffset);

        stream.Write(ftyp);
        stream.Write(meta);

        int mdatPayloadLength = frame.ObuBytes.Length + (alphaFrame?.ObuBytes.Length ?? 0);
        byte[] mdatHeader = new byte[8];
        AvifBoxWriter.WriteUInt32(mdatHeader, 0, (uint)(8 + mdatPayloadLength));
        AvifBoxWriter.WriteFourCc(mdatHeader, 4, "mdat");
        stream.Write(mdatHeader);
        stream.Write(frame.ObuBytes);
        if (alphaFrame is not null)
        {
            stream.Write(alphaFrame.ObuBytes);
        }
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

    private static byte[] BuildMeta(Av1EncodedFrame frame, Av1EncodedFrame? alphaFrame, int colorOffset, int alphaOffset)
    {
        byte[] hdlr = BuildHdlr();
        byte[] pitm = BuildPitm();
        byte[] iinf = BuildIinf(alphaFrame is not null);
        byte[] iloc = BuildIloc(colorOffset, frame.ObuBytes.Length, alphaFrame is null ? null : alphaOffset, alphaFrame?.ObuBytes.Length);
        byte[] iprp = BuildIprp(frame, alphaFrame);

        if (alphaFrame is null)
        {
            return AvifBoxWriter.FullBox("meta", version: 0, flags: 0, AvifBoxWriter.Concat([hdlr, pitm, iinf, iloc, iprp]));
        }

        byte[] iref = BuildIref();
        return AvifBoxWriter.FullBox("meta", version: 0, flags: 0, AvifBoxWriter.Concat([hdlr, pitm, iinf, iloc, iprp, iref]));
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
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)ColorItemId);
        return AvifBoxWriter.FullBox("pitm", version: 0, flags: 0, payload);
    }

    private static byte[] BuildIinf(bool hasAlpha)
    {
        byte[][] infeEntries = hasAlpha ? [BuildInfe(ColorItemId), BuildInfe(AlphaItemId)] : [BuildInfe(ColorItemId)];
        byte[] entries = AvifBoxWriter.Concat(infeEntries);
        var payload = new byte[2 + entries.Length];
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)infeEntries.Length); // entry_count
        Array.Copy(entries, 0, payload, 2, entries.Length);
        return AvifBoxWriter.FullBox("iinf", version: 0, flags: 0, payload);
    }

    private static byte[] BuildInfe(uint itemId)
    {
        // item_id(2) + item_protection_index(2) + item_type(4) + item_name cstring (empty -> 1 zero byte).
        var payload = new byte[2 + 2 + 4 + 1];
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)itemId);
        AvifBoxWriter.WriteUInt16(payload, 2, 0); // item_protection_index
        AvifBoxWriter.WriteFourCc(payload, 4, "av01");

        // infe version 2 so AvifItemInfoBox.Parse's modern branch (with an explicit item_type field) applies.
        return AvifBoxWriter.FullBox("infe", version: 2, flags: 0, payload);
    }

    private static byte[] BuildIloc(int colorOffset, int colorLength, int? alphaOffset, int? alphaLength)
    {
        bool hasAlpha = alphaOffset is not null;
        int itemCount = hasAlpha ? 2 : 1;

        // offsetSize=4, lengthSize=4, baseOffsetSize=0, indexSize=0 (reserved, unused at version 0).
        var payload = new byte[1 + 1 + 2 + (itemCount * (2 + 2 + 2 + 4 + 4))];
        int i = 0;
        payload[i++] = 0x44; // (offsetSize=4 << 4) | lengthSize=4
        payload[i++] = 0x00; // (baseOffsetSize=0 << 4) | indexSize=0
        AvifBoxWriter.WriteUInt16(payload, i, (ushort)itemCount); // item_count
        i += 2;

        WriteIlocItem(payload, ref i, ColorItemId, colorOffset, colorLength);
        if (hasAlpha)
        {
            WriteIlocItem(payload, ref i, AlphaItemId, alphaOffset!.Value, alphaLength!.Value);
        }

        return AvifBoxWriter.FullBox("iloc", version: 0, flags: 0, payload);
    }

    private static void WriteIlocItem(byte[] payload, ref int i, uint itemId, int offset, int length)
    {
        AvifBoxWriter.WriteUInt16(payload, i, (ushort)itemId);
        i += 2;
        AvifBoxWriter.WriteUInt16(payload, i, 0); // data_reference_index
        i += 2;
        // base_offset omitted (baseOffsetSize == 0)
        AvifBoxWriter.WriteUInt16(payload, i, 1); // extent_count
        i += 2;
        AvifBoxWriter.WriteUInt32(payload, i, (uint)offset);
        i += 4;
        AvifBoxWriter.WriteUInt32(payload, i, (uint)length);
        i += 4;
    }

    private static byte[] BuildIprp(Av1EncodedFrame frame, Av1EncodedFrame? alphaFrame)
    {
        byte[] ispe = BuildIspe(frame.Width, frame.Height);
        byte[] av1C = BuildAv1Config(frame.MonoChrome, frame.Chroma444);
        byte[] pixi = BuildPixi(frame.MonoChrome);

        if (alphaFrame is null)
        {
            byte[] ipco = AvifBoxWriter.Box("ipco", ispe, av1C, pixi);
            byte[] ipma = BuildIpma([(ColorItemId, [0x80 | 1, 0x80 | 2, 3])]);
            return AvifBoxWriter.Box("iprp", ipco, ipma);
        }

        // Alpha carries no ispe of its own -- AvifItemAssembler.ResolveDimensions falls back to (0,0) for the
        // alpha item and always takes the assembled image's width/height from the color item, so an alpha
        // ispe would be redundant (this mirrors this repo's own decoder tolerance, not a spec requirement
        // this encoder needs to lean on for interop -- real decoders are expected to do the same fallback).
        byte[] alphaAv1C = BuildAv1Config(monoChrome: true, chroma444: false);
        byte[] alphaPixi = BuildPixi(monoChrome: true);
        byte[] auxC = AvifAuxCBox.Build();
        byte[] ipcoWithAlpha = AvifBoxWriter.Box("ipco", ispe, av1C, pixi, alphaAv1C, alphaPixi, auxC);
        byte[] ipmaWithAlpha = BuildIpma(
        [
            (ColorItemId, [0x80 | 1, 0x80 | 2, 3]),
            (AlphaItemId, [0x80 | 4, 5, 6]),
        ]);
        return AvifBoxWriter.Box("iprp", ipcoWithAlpha, ipmaWithAlpha);
    }

    /// <summary>Writes the one <c>iref</c> entry this encoder ever produces: an <c>auxl</c> reference from the color item to the alpha item, exactly the direction <see cref="AvifItemAssembler.Assemble"/> parses.</summary>
    private static byte[] BuildIref()
    {
        var auxlPayload = new byte[2 + 2 + 2]; // from_item_ID(2) + reference_count(2) + to_item_ID(2)
        AvifBoxWriter.WriteUInt16(auxlPayload, 0, (ushort)ColorItemId);
        AvifBoxWriter.WriteUInt16(auxlPayload, 2, 1); // reference_count
        AvifBoxWriter.WriteUInt16(auxlPayload, 4, (ushort)AlphaItemId);
        byte[] auxl = AvifBoxWriter.Box("auxl", auxlPayload);

        return AvifBoxWriter.FullBox("iref", version: 0, flags: 0, auxl);
    }

    private static byte[] BuildIspe(int width, int height)
    {
        var payload = new byte[8];
        AvifBoxWriter.WriteUInt32(payload, 0, (uint)width);
        AvifBoxWriter.WriteUInt32(payload, 4, (uint)height);
        return AvifBoxWriter.FullBox("ispe", version: 0, flags: 0, payload);
    }

    /// <summary>
    /// <paramref name="chroma444"/> is only meaningful (and only ever <see langword="true"/>) when
    /// <paramref name="monoChrome"/> is <see langword="false"/> -- see <see cref="Av1FrameEncoder.Encode"/>'s
    /// <c>chroma444</c> gate. This repo's own <see cref="AvifDecoder"/> never actually reads these bits (it
    /// re-derives subsampling from the AV1 bitstream's own sequence header instead), so getting this wrong
    /// wouldn't be caught by this repo's round-trip tests -- it still matters for spec conformance and for
    /// any other tool that trusts <c>av1C</c> for fast subsampling probing without a full bitstream parse.
    /// </summary>
    private static byte[] BuildAv1Config(bool monoChrome, bool chroma444)
    {
        int seqProfile = chroma444 ? Av1SequenceHeaderWriter.SeqProfileChroma444 : Av1SequenceHeaderWriter.SeqProfile;
        var payload = new byte[4];
        payload[0] = (byte)(0x80 | 1); // marker=1, version=1
        payload[1] = (byte)(((seqProfile & 0x7) << 5) | (Av1SequenceHeaderWriter.SeqLevelIdx0 & 0x1F));
        int b2 = 0; // seq_tier0=0, high_bitdepth=0 (8-bit), twelve_bit=0
        if (monoChrome)
        {
            b2 |= 0x10;
        }
        else if (!chroma444)
        {
            b2 |= 0x08; // chroma_subsampling_x
            b2 |= 0x04; // chroma_subsampling_y
        }

        // chroma444: both chroma_subsampling_x/y bits stay 0.
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

    /// <summary>
    /// Writes one <c>ipma</c> entry per <paramref name="entries"/> item, each associating that item with a
    /// set of <c>ipco</c> property indices (1-based, in <c>ipco</c> declaration order; the top bit of each
    /// association byte marks it essential -- matches <see cref="AvifItemPropertiesBox.ParseIpma"/>'s
    /// "essential" bit convention; this repo's own decoder ignores the flag either way, but real decoders may
    /// enforce it for unrecognized essential properties).
    /// </summary>
    private static byte[] BuildIpma(IReadOnlyList<(uint ItemId, byte[] Associations)> entries)
    {
        int payloadLength = 4;
        foreach (var entry in entries)
        {
            payloadLength += 2 + 1 + entry.Associations.Length;
        }

        var payload = new byte[payloadLength];
        int i = 0;
        AvifBoxWriter.WriteUInt32(payload, i, (uint)entries.Count); // entry_count
        i += 4;

        foreach (var entry in entries)
        {
            AvifBoxWriter.WriteUInt16(payload, i, (ushort)entry.ItemId);
            i += 2;
            payload[i++] = (byte)entry.Associations.Length; // association_count
            foreach (byte association in entry.Associations)
            {
                payload[i++] = association;
            }
        }

        return AvifBoxWriter.FullBox("ipma", version: 0, flags: 0, payload);
    }
}
