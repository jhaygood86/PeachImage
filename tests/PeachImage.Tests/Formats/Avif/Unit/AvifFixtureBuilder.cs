using System.Buffers.Binary;
using System.Text;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Hand-builds minimal, valid AVIF (ISOBMFF/HEIF) byte sequences for unit tests, independent of any
/// external encoder or the corpus fetch. Follows the ISO/IEC 14496-12 / 23008-12 box layouts directly
/// (the same references the container parsers themselves were written against) rather than mirroring the
/// parser's own code, so these fixtures are a meaningful check rather than a tautology. AV1 tile payloads
/// are dummy bytes throughout -- Phase 1 never decodes pixel data, only container structure.
/// </summary>
internal static class AvifFixtureBuilder
{
    public static byte[] BuildSingleItem(
        int width,
        int height,
        bool highBitdepth = false,
        bool twelveBit = false,
        bool monochrome = false,
        bool subsamplingX = true,
        bool subsamplingY = true,
        bool includeAlpha = false,
        string majorBrand = "avif",
        string[]? compatibleBrands = null)
    {
        var items = new List<(uint Id, byte[] Data)> { (1, DummyAv1Bytes(16)) };

        var ipcoProps = new List<byte[]> { Ispe(width, height), Av1C(0, 0, false, highBitdepth, twelveBit, monochrome, subsamplingX, subsamplingY, 0) };
        var ipmaEntries = new List<byte[]> { IpmaEntry(1, 1, 2) };
        var infeEntries = new List<byte[]> { Infe(1, "av01") };
        var irefEntries = new List<byte[]>();

        if (includeAlpha)
        {
            items.Add((2, DummyAv1Bytes(8)));
            infeEntries.Add(Infe(2, "av01"));
            ipcoProps.Add(AuxC());
            // The alpha item is itself an av01 image item, so it needs its own av1C association too
            // (index 2, shared with the color item) in addition to auxC (index 3).
            ipmaEntries.Add(IpmaEntry(2, 2, 3));
            irefEntries.Add(IrefEntry("auxl", 1, 2));
        }

        byte[] ftyp = Ftyp(majorBrand, compatibleBrands ?? ["avif", "mif1", "miaf"]);

        byte[] BuildMeta(Func<uint, long> offsetOf)
        {
            var ilocItems = items.Select(it => (it.Id, (uint)offsetOf(it.Id), (uint)it.Data.Length)).ToArray();
            var children = new List<byte[]> { Pitm(1), Iloc(ilocItems), Iinf(infeEntries.ToArray()) };
            if (irefEntries.Count > 0)
            {
                children.Add(Iref(irefEntries.ToArray()));
            }

            children.Add(Iprp(Ipco(ipcoProps.ToArray()), Ipma(ipmaEntries.ToArray())));
            return Meta(children.ToArray());
        }

        return Assemble(ftyp, BuildMeta, items);
    }

    public static byte[] BuildGrid(int rows, int columns, int outputWidth, int outputHeight, bool includeItemIspe = false)
    {
        int tileCount = rows * columns;
        var tileIds = Enumerable.Range(2, tileCount).Select(i => (uint)i).ToArray();

        var items = new List<(uint Id, byte[] Data)> { (1, GridDescriptor(rows, columns, outputWidth, outputHeight)) };
        items.AddRange(tileIds.Select(id => (id, DummyAv1Bytes(16))));

        var ipcoProps = new List<byte[]> { Av1C(0, 0, false, highBitdepth: false, twelveBit: false, monochrome: false, subsamplingX: true, subsamplingY: true, 0) };
        int av1cIndex = 1;
        var ipmaEntries = tileIds.Select(id => IpmaEntry(id, av1cIndex)).ToList();

        if (includeItemIspe)
        {
            ipcoProps.Add(Ispe(outputWidth, outputHeight));
            ipmaEntries.Add(IpmaEntry(1, av1cIndex + 1));
        }

        var infeEntries = new List<byte[]> { Infe(1, "grid") };
        infeEntries.AddRange(tileIds.Select(id => Infe(id, "av01")));

        var irefEntries = new List<byte[]> { IrefEntry("dimg", 1, tileIds) };

        byte[] ftyp = Ftyp("avif", ["avif", "mif1", "miaf"]);

        byte[] BuildMeta(Func<uint, long> offsetOf)
        {
            var ilocItems = items.Select(it => (it.Id, (uint)offsetOf(it.Id), (uint)it.Data.Length)).ToArray();
            var children = new List<byte[]>
            {
                Pitm(1),
                Iloc(ilocItems),
                Iinf(infeEntries.ToArray()),
                Iref(irefEntries.ToArray()),
                Iprp(Ipco(ipcoProps.ToArray()), Ipma(ipmaEntries.ToArray())),
            };
            return Meta(children.ToArray());
        }

        return Assemble(ftyp, BuildMeta, items);
    }

    /// <summary>Two-pass assembly: box field widths (hence total sizes) never depend on an offset's numeric value, only its declared byte width -- so a placeholder pass with offset 0 yields the same `meta` length as the real pass, letting mdat's true start be computed from it without a fixed-point/iterative layout.</summary>
    private static byte[] Assemble(byte[] ftyp, Func<Func<uint, long>, byte[]> buildMeta, List<(uint Id, byte[] Data)> items)
    {
        byte[] metaPlaceholder = buildMeta(_ => 0);
        long mdatPayloadStart = ftyp.Length + metaPlaceholder.Length + 8;

        long cursor = mdatPayloadStart;
        var offsets = new Dictionary<uint, long>();
        var mdatPayload = new List<byte>();
        foreach (var item in items)
        {
            offsets[item.Id] = cursor;
            mdatPayload.AddRange(item.Data);
            cursor += item.Data.Length;
        }

        byte[] meta = buildMeta(id => offsets[id]);
        byte[] mdat = Box("mdat", mdatPayload.ToArray());
        return Concat(ftyp, meta, mdat);
    }

    private static byte[] DummyAv1Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(0xA0 + (i % 16))).ToArray();

    private static byte[] Ftyp(string majorBrand, IEnumerable<string> compatibleBrands)
    {
        var payload = new List<byte>();
        payload.AddRange(Encoding.ASCII.GetBytes(majorBrand));
        payload.AddRange(BEUInt32(0));
        foreach (var brand in compatibleBrands)
        {
            payload.AddRange(Encoding.ASCII.GetBytes(brand));
        }

        return Box("ftyp", payload.ToArray());
    }

    private static byte[] Av1C(int seqProfile, int seqLevel, bool tier, bool highBitdepth, bool twelveBit, bool monochrome, bool subsamplingX, bool subsamplingY, int chromaSamplePosition)
    {
        byte b0 = 0x80 | 1;
        byte b1 = (byte)(((seqProfile & 0x7) << 5) | (seqLevel & 0x1F));
        byte b2 = (byte)((tier ? 0x80 : 0) | (highBitdepth ? 0x40 : 0) | (twelveBit ? 0x20 : 0) | (monochrome ? 0x10 : 0) | (subsamplingX ? 0x08 : 0) | (subsamplingY ? 0x04 : 0) | (chromaSamplePosition & 0x3));
        byte b3 = 0;
        return Box("av1C", [b0, b1, b2, b3]);
    }

    private static byte[] Ispe(int width, int height) => Box("ispe", FullBoxPayload(0, 0, Concat(BEUInt32((uint)width), BEUInt32((uint)height))));

    private static byte[] AuxC(string urn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha")
    {
        var rest = new List<byte>();
        rest.AddRange(Encoding.ASCII.GetBytes(urn));
        rest.Add(0);
        return Box("auxC", FullBoxPayload(0, 0, rest.ToArray()));
    }

    private static byte[] Pitm(uint itemId) => Box("pitm", FullBoxPayload(0, 0, BEUInt16((int)itemId)));

    private static byte[] Infe(uint itemId, string itemType, string itemName = "")
    {
        var rest = new List<byte>();
        rest.AddRange(BEUInt16((int)itemId));
        rest.AddRange(BEUInt16(0));
        rest.AddRange(Encoding.ASCII.GetBytes(itemType));
        rest.AddRange(Encoding.UTF8.GetBytes(itemName));
        rest.Add(0);
        return Box("infe", FullBoxPayload(2, 0, rest.ToArray()));
    }

    private static byte[] Iinf(params byte[][] entries)
    {
        var rest = new List<byte>();
        rest.AddRange(BEUInt16(entries.Length));
        foreach (var entry in entries)
        {
            rest.AddRange(entry);
        }

        return Box("iinf", FullBoxPayload(0, 0, rest.ToArray()));
    }

    /// <summary>Version 0 <c>iloc</c>: offset_size=4, length_size=4, base_offset_size=0, one extent per item, construction_method implicitly 0 (file offset).</summary>
    private static byte[] Iloc(params (uint ItemId, uint Offset, uint Length)[] items)
    {
        var rest = new List<byte> { 0x44, 0x00 };
        rest.AddRange(BEUInt16(items.Length));
        foreach (var item in items)
        {
            rest.AddRange(BEUInt16((int)item.ItemId));
            rest.AddRange(BEUInt16(0)); // data_reference_index
            rest.AddRange(BEUInt16(1)); // extent_count
            rest.AddRange(BEUInt32(item.Offset));
            rest.AddRange(BEUInt32(item.Length));
        }

        return Box("iloc", FullBoxPayload(0, 0, rest.ToArray()));
    }

    private static byte[] IrefEntry(string referenceType, uint fromItemId, params uint[] toItemIds)
    {
        var rest = new List<byte>();
        rest.AddRange(BEUInt16((int)fromItemId));
        rest.AddRange(BEUInt16(toItemIds.Length));
        foreach (var id in toItemIds)
        {
            rest.AddRange(BEUInt16((int)id));
        }

        return Box(referenceType, rest.ToArray());
    }

    private static byte[] Iref(params byte[][] entries) => Box("iref", FullBoxPayload(0, 0, Concat(entries)));

    private static byte[] Ipco(params byte[][] properties) => Box("ipco", Concat(properties));

    /// <summary>Non-essential, small (7-bit) property-index form -- matches <c>ipma</c>'s <c>flags == 0</c>.</summary>
    private static byte[] IpmaEntry(uint itemId, params int[] propertyIndices)
    {
        var rest = new List<byte>();
        rest.AddRange(BEUInt16((int)itemId));
        rest.Add((byte)propertyIndices.Length);
        foreach (int index in propertyIndices)
        {
            rest.Add((byte)(index & 0x7F));
        }

        return rest.ToArray();
    }

    private static byte[] Ipma(params byte[][] entries)
    {
        var rest = new List<byte>();
        rest.AddRange(BEUInt32((uint)entries.Length));
        foreach (var entry in entries)
        {
            rest.AddRange(entry);
        }

        return Box("ipma", FullBoxPayload(0, 0, rest.ToArray()));
    }

    private static byte[] Iprp(byte[] ipco, params byte[][] ipmaBoxes) => Box("iprp", Concat([ipco, .. ipmaBoxes]));

    private static byte[] Meta(params byte[][] children) => Box("meta", FullBoxPayload(0, 0, Concat(children)));

    private static byte[] GridDescriptor(int rows, int columns, int outputWidth, int outputHeight)
    {
        bool large = outputWidth > 0xFFFF || outputHeight > 0xFFFF;
        var bytes = new List<byte> { 0, (byte)(large ? 1 : 0), (byte)(rows - 1), (byte)(columns - 1) };
        if (large)
        {
            bytes.AddRange(BEUInt32((uint)outputWidth));
            bytes.AddRange(BEUInt32((uint)outputHeight));
        }
        else
        {
            bytes.AddRange(BEUInt16(outputWidth));
            bytes.AddRange(BEUInt16(outputHeight));
        }

        return bytes.ToArray();
    }

    private static byte[] Box(string fourCc, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(fourCc, 0, 4, result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] FullBoxPayload(byte version, uint flags, byte[] rest)
    {
        var result = new byte[4 + rest.Length];
        result[0] = version;
        result[1] = (byte)(flags >> 16);
        result[2] = (byte)(flags >> 8);
        result[3] = (byte)flags;
        rest.CopyTo(result, 4);
        return result;
    }

    private static byte[] BEUInt16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] BEUInt32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
