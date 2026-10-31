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
/// An ICC profile is written as a <c>colr</c> (<c>prof</c>) property and EXIF as an <c>Exif</c> item described
/// by the color item through an <c>iref cdsc</c> entry, and XMP as a <c>mime</c> (<c>application/rdf+xml</c>) item likewise. Alpha is always straight (non-premultiplied), matching the
/// decoder's own assumption -- no <c>prem</c> signaling exists anywhere in this codebase.
/// </summary>
internal static class AvifContainerWriter
{
    private const uint ColorItemId = 1;
    private const uint AlphaItemId = 2;
    private const uint ExifItemId = 3;
    private const uint XmpItemId = 4;

    public static void Write(Stream stream, Av1EncodedFrame frame, Av1EncodedFrame? alphaFrame = null, byte[]? iccProfile = null, byte[]? exif = null, ImageOrientation orientation = ImageOrientation.Normal, byte[]? xmp = null)
    {
        // An Exif item is a 4-byte exif_tiff_header_offset (0: the TIFF header follows directly) and then the TIFF-structured block.
        byte[]? exifItem = exif is null ? null : [0, 0, 0, 0, .. exif];

        byte[] ftyp = BuildFtyp();
        byte[] meta = BuildMeta(frame, alphaFrame, iccProfile, exifItem, xmp, orientation, colorOffset: 0, alphaOffset: 0, exifOffset: 0, xmpOffset: 0);

        // iloc's extent-offset fields are fixed-size (4-byte) slots regardless of their value, so meta's
        // total length is already final -- rebuild once more with the real mdat offsets now that they're known.
        int mdatPayloadOffset = ftyp.Length + meta.Length + 8;
        int colorOffset = mdatPayloadOffset;
        int alphaOffset = mdatPayloadOffset + frame.ObuBytes.Length;
        int exifOffset = alphaOffset + (alphaFrame?.ObuBytes.Length ?? 0);
        int xmpOffset = exifOffset + (exifItem?.Length ?? 0);
        meta = BuildMeta(frame, alphaFrame, iccProfile, exifItem, xmp, orientation, colorOffset, alphaOffset, exifOffset, xmpOffset);

        stream.Write(ftyp);
        stream.Write(meta);

        int mdatPayloadLength = frame.ObuBytes.Length + (alphaFrame?.ObuBytes.Length ?? 0) + (exifItem?.Length ?? 0) + (xmp?.Length ?? 0);
        byte[] mdatHeader = new byte[8];
        AvifBoxWriter.WriteUInt32(mdatHeader, 0, (uint)(8 + mdatPayloadLength));
        AvifBoxWriter.WriteFourCc(mdatHeader, 4, "mdat");
        stream.Write(mdatHeader);
        stream.Write(frame.ObuBytes);
        if (alphaFrame is not null)
        {
            stream.Write(alphaFrame.ObuBytes);
        }

        if (exifItem is not null)
        {
            stream.Write(exifItem);
        }

        if (xmp is not null)
        {
            stream.Write(xmp);
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

    private static byte[] BuildMeta(Av1EncodedFrame frame, Av1EncodedFrame? alphaFrame, byte[]? iccProfile, byte[]? exifItem, byte[]? xmp, ImageOrientation orientation, int colorOffset, int alphaOffset, int exifOffset, int xmpOffset)
    {
        byte[] hdlr = BuildHdlr();
        byte[] pitm = BuildPitm();
        byte[] iinf = BuildIinf(alphaFrame is not null, exifItem is not null, xmp is not null);

        var extents = new List<(uint ItemId, int Offset, int Length)> { (ColorItemId, colorOffset, frame.ObuBytes.Length) };
        if (alphaFrame is not null)
        {
            extents.Add((AlphaItemId, alphaOffset, alphaFrame.ObuBytes.Length));
        }

        if (exifItem is not null)
        {
            extents.Add((ExifItemId, exifOffset, exifItem.Length));
        }

        if (xmp is not null)
        {
            extents.Add((XmpItemId, xmpOffset, xmp.Length));
        }

        byte[] iloc = BuildIloc(extents);
        byte[] iprp = BuildIprp(frame, alphaFrame, iccProfile, orientation);

        if (alphaFrame is null && exifItem is null && xmp is null)
        {
            return AvifBoxWriter.FullBox("meta", version: 0, flags: 0, AvifBoxWriter.Concat([hdlr, pitm, iinf, iloc, iprp]));
        }

        byte[] iref = BuildIref(alphaFrame is not null, exifItem is not null, xmp is not null);
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

    private static byte[] BuildIinf(bool hasAlpha, bool hasExif, bool hasXmp)
    {
        var infeEntries = new List<byte[]> { BuildInfe(ColorItemId, "av01") };
        if (hasAlpha)
        {
            infeEntries.Add(BuildInfe(AlphaItemId, "av01"));
        }

        if (hasExif)
        {
            infeEntries.Add(BuildInfe(ExifItemId, "Exif"));
        }

        if (hasXmp)
        {
            infeEntries.Add(BuildInfe(XmpItemId, "mime", "application/rdf+xml"));
        }

        byte[] entries = AvifBoxWriter.Concat([.. infeEntries]);
        var payload = new byte[2 + entries.Length];
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)infeEntries.Count); // entry_count
        Array.Copy(entries, 0, payload, 2, entries.Length);
        return AvifBoxWriter.FullBox("iinf", version: 0, flags: 0, payload);
    }

    private static byte[] BuildInfe(uint itemId, string itemType, string? contentType = null)
    {
        // item_id(2) + item_protection_index(2) + item_type(4) + item_name cstring (empty -> 1 zero byte),
        // then, for a 'mime' item, its content_type cstring.
        var payload = new byte[2 + 2 + 4 + 1 + (contentType is null ? 0 : contentType.Length + 1)];
        if (contentType is not null)
        {
            System.Text.Encoding.ASCII.GetBytes(contentType, 0, contentType.Length, payload, 9);
        }

        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)itemId);
        AvifBoxWriter.WriteUInt16(payload, 2, 0); // item_protection_index
        AvifBoxWriter.WriteFourCc(payload, 4, itemType);

        // infe version 2 so AvifItemInfoBox.Parse's modern branch (with an explicit item_type field) applies.
        return AvifBoxWriter.FullBox("infe", version: 2, flags: 0, payload);
    }

    private static byte[] BuildIloc(List<(uint ItemId, int Offset, int Length)> extents)
    {
        // offsetSize=4, lengthSize=4, baseOffsetSize=0, indexSize=0 (reserved, unused at version 0).
        var payload = new byte[1 + 1 + 2 + (extents.Count * (2 + 2 + 2 + 4 + 4))];
        int i = 0;
        payload[i++] = 0x44; // (offsetSize=4 << 4) | lengthSize=4
        payload[i++] = 0x00; // (baseOffsetSize=0 << 4) | indexSize=0
        AvifBoxWriter.WriteUInt16(payload, i, (ushort)extents.Count); // item_count
        i += 2;

        foreach (var (itemId, offset, length) in extents)
        {
            WriteIlocItem(payload, ref i, itemId, offset, length);
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

    private static byte[] BuildIprp(Av1EncodedFrame frame, Av1EncodedFrame? alphaFrame, byte[]? iccProfile, ImageOrientation orientation)
    {
        // ipco properties are numbered from 1 in declaration order; the top bit of an association marks it essential.
        var properties = new List<byte[]>
        {
            BuildIspe(frame.Width, frame.Height),
            BuildAv1Config(frame.MonoChrome, frame.Chroma444, frame.SeqLevelIdx),
            BuildPixi(frame.MonoChrome),
        };
        var colorAssociations = new List<byte> { 0x80 | 1, 0x80 | 2, 3 };
        var entries = new List<(uint ItemId, byte[] Associations)>();

        byte[]? alphaAssociations = null;
        if (alphaFrame is not null)
        {
            // Alpha carries no ispe of its own -- AvifItemAssembler.ResolveDimensions falls back to (0,0) for the
            // alpha item and always takes the assembled image's width/height from the color item, so an alpha
            // ispe would be redundant (this mirrors this repo's own decoder tolerance, not a spec requirement
            // this encoder needs to lean on for interop -- real decoders are expected to do the same fallback).
            properties.Add(BuildAv1Config(monoChrome: true, chroma444: false, alphaFrame.SeqLevelIdx));
            properties.Add(BuildPixi(monoChrome: true));
            properties.Add(AvifAuxCBox.Build());
            alphaAssociations = [0x80 | 4, 5, 6];
        }

        if (iccProfile is not null)
        {
            properties.Add(AvifBoxWriter.Box("colr", [.. "prof"u8.ToArray(), .. iccProfile]));
            colorAssociations.Add((byte)properties.Count);
        }

        // MIAF has irot/imir override any Exif orientation, so the Exif orientation is mirrored into them (rotation first, then mirror).
        foreach (var (isMirror, value) in ToTransforms(orientation))
        {
            properties.Add(AvifBoxWriter.Box(isMirror ? "imir" : "irot", [(byte)value]));
            colorAssociations.Add((byte)(0x80 | properties.Count));
        }

        entries.Add((ColorItemId, [.. colorAssociations]));
        if (alphaAssociations is not null)
        {
            entries.Add((AlphaItemId, alphaAssociations));
        }

        byte[] ipco = AvifBoxWriter.Box("ipco", properties.ToArray());
        return AvifBoxWriter.Box("iprp", ipco, BuildIpma(entries));
    }

    /// <summary>The <c>irot</c>/<c>imir</c> sequence whose fold (see <see cref="AvifTransformProperties.ToOrientation"/>) is <paramref name="orientation"/>.</summary>
    private static (bool IsMirror, int Value)[] ToTransforms(ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.MirrorHorizontal => [(true, 0)],
        ImageOrientation.Rotate180 => [(false, 2)],
        ImageOrientation.MirrorVertical => [(true, 1)],
        ImageOrientation.Transpose => [(false, 1), (true, 1)],
        ImageOrientation.Rotate90 => [(false, 3)],
        ImageOrientation.Transverse => [(false, 1), (true, 0)],
        ImageOrientation.Rotate270 => [(false, 1)],
        _ => [],
    };

    /// <summary>
    /// Writes this encoder's <c>iref</c> entries: an <c>auxl</c> reference from the color item to the alpha item (exactly the
    /// direction <see cref="AvifItemAssembler.Assemble"/> parses) and a <c>cdsc</c> reference from the EXIF item to the color item.
    /// </summary>
    private static byte[] BuildIref(bool hasAlpha, bool hasExif, bool hasXmp)
    {
        var references = new List<byte[]>();
        if (hasAlpha)
        {
            references.Add(BuildReference("auxl", ColorItemId, AlphaItemId));
        }

        if (hasExif)
        {
            references.Add(BuildReference("cdsc", ExifItemId, ColorItemId));
        }

        if (hasXmp)
        {
            references.Add(BuildReference("cdsc", XmpItemId, ColorItemId));
        }

        return AvifBoxWriter.FullBox("iref", version: 0, flags: 0, AvifBoxWriter.Concat([.. references]));
    }

    private static byte[] BuildReference(string type, uint fromItem, uint toItem)
    {
        var payload = new byte[2 + 2 + 2]; // from_item_ID(2) + reference_count(2) + to_item_ID(2)
        AvifBoxWriter.WriteUInt16(payload, 0, (ushort)fromItem);
        AvifBoxWriter.WriteUInt16(payload, 2, 1); // reference_count
        AvifBoxWriter.WriteUInt16(payload, 4, (ushort)toItem);
        return AvifBoxWriter.Box(type, payload);
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
    private static byte[] BuildAv1Config(bool monoChrome, bool chroma444, int seqLevelIdx)
    {
        int seqProfile = chroma444 ? Av1SequenceHeaderWriter.SeqProfileChroma444 : Av1SequenceHeaderWriter.SeqProfile;
        var payload = new byte[4];
        payload[0] = (byte)(0x80 | 1); // marker=1, version=1
        payload[1] = (byte)(((seqProfile & 0x7) << 5) | (seqLevelIdx & 0x1F));
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
    private static byte[] BuildIpma(List<(uint ItemId, byte[] Associations)> entries)
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
