using PeachImage.Formats.Jpeg2000.Decoding;

namespace PeachImage.Tests.Formats.Jpeg2000.Unit;

/// <summary>
/// Rewrites ordinary codestreams so their packet headers live in PPT (tile-part header) or PPM (main header) marker segments,
/// then checks the result decodes to the same pixels.
/// </summary>
public class PackedPacketHeaderTests
{
    private const string Prefix = "PeachImage.Tests.Formats.Jpeg2000.Assets.";

    [Theory]
    [InlineData("sop_eph_tiled.j2k")]
    [InlineData("eph_layers_precincts.j2k")]
    [InlineData("rgb_layers.jp2")]
    [InlineData("rgb_prog_pcrl_tiles.jp2")]
    public void PptHeaders_DecodeLikeInlineHeaders(string file)
    {
        byte[] codestream = ExtractCodestream(ReadAsset(file));
        byte[] rewritten = Rewrite(codestream, usePpm: false);
        AssertSameImage(codestream, rewritten);
    }

    [Theory]
    [InlineData("sop_eph_tiled.j2k")]
    [InlineData("eph_layers_precincts.j2k")]
    [InlineData("rgb_layers.jp2")]
    [InlineData("rgb_prog_pcrl_tiles.jp2")]
    public void PpmHeaders_DecodeLikeInlineHeaders(string file)
    {
        byte[] codestream = ExtractCodestream(ReadAsset(file));
        byte[] rewritten = Rewrite(codestream, usePpm: true);
        AssertSameImage(codestream, rewritten);
    }

    private static void AssertSameImage(byte[] expectedStream, byte[] actualStream)
    {
        using var expected = Image.Load(expectedStream);
        using var actual = Image.Load(actualStream);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.GetRowSpan(y).SequenceEqual(actual.GetRowSpan(y)), $"row {y} differs");
        }
    }

    internal static byte[] Rewrite(byte[] codestream, bool usePpm)
    {
        var parsed = J2kCodestream.Parse(codestream);
        int pos = 2;
        while (!(codestream[pos] == 0xFF && codestream[pos + 1] == 0x90))
        {
            pos += 2 + ((codestream[pos + 2] << 8) | codestream[pos + 3]);
        }

        var main = codestream.AsSpan(0, pos).ToArray();
        var output = new List<byte>(codestream.Length + 4096);
        var ppmHeaders = new List<byte[]>();
        var tileParts = new List<byte[]>();

        // Trace the packet boundaries of every tile (these streams have a single tile-part per tile).
        var traces = new Dictionary<int, List<(int PacketStart, int HeaderStart, int HeaderEnd, int PacketEnd)>>();
        foreach (var group in parsed.TileParts.GroupBy(p => p.TileIndex))
        {
            Assert.Single(group);
            var decoder = new J2kTileDecoder(parsed, group.Key, 0) { PacketTrace = [] };
            try
            {
                decoder.Decode(group.ToList());
            }
            finally
            {
                decoder.Release();
            }

            traces[group.Key] = decoder.PacketTrace!;
        }

        // Walk the original tile-parts, rebuilding each.
        while (pos + 12 <= codestream.Length && codestream[pos] == 0xFF && codestream[pos + 1] == 0x90)
        {
            int sotStart = pos;
            int tileIndex = (codestream[pos + 4] << 8) | codestream[pos + 5];
            long psot = ((long)codestream[pos + 6] << 24) | ((long)codestream[pos + 7] << 16) | ((long)codestream[pos + 8] << 8) | codestream[pos + 9];
            int end = psot == 0 ? codestream.Length - 2 : (int)(sotStart + psot);
            int cursor = pos + 12;
            var headerSegments = new List<byte>();
            while (!(codestream[cursor] == 0xFF && codestream[cursor + 1] == 0x93))
            {
                int length = 2 + ((codestream[cursor + 2] << 8) | codestream[cursor + 3]);
                headerSegments.AddRange(codestream.AsSpan(cursor, length).ToArray());
                cursor += length;
            }

            cursor += 2; // SOD
            var data = codestream.AsSpan(cursor, end - cursor);
            var trace = traces[tileIndex];

            var bodies = new List<byte>();
            var headers = new List<byte>();
            foreach (var (packetStart, headerStart, headerEnd, packetEnd) in trace)
            {
                bodies.AddRange(data.Slice(packetStart, headerStart - packetStart).ToArray()); // SOP marker, if any
                headers.AddRange(data.Slice(headerStart, headerEnd - headerStart).ToArray());
                bodies.AddRange(data.Slice(headerEnd, packetEnd - headerEnd).ToArray());
            }

            var part = new List<byte>();
            part.AddRange(codestream.AsSpan(sotStart, 12).ToArray());
            part.AddRange(headerSegments);
            if (usePpm)
            {
                ppmHeaders.Add(headers.ToArray());
            }
            else
            {
                int index = 0;
                for (int offset = 0; offset < headers.Count; offset += 700, index++)
                {
                    int chunk = Math.Min(700, headers.Count - offset);
                    part.Add(0xFF);
                    part.Add(0x61);
                    part.Add((byte)((chunk + 3) >> 8));
                    part.Add((byte)(chunk + 3));
                    part.Add((byte)index);
                    part.AddRange(headers.GetRange(offset, chunk));
                }
            }

            part.Add(0xFF);
            part.Add(0x93);
            part.AddRange(bodies);
            int newLength = part.Count;
            part[6] = (byte)(newLength >> 24);
            part[7] = (byte)(newLength >> 16);
            part[8] = (byte)(newLength >> 8);
            part[9] = (byte)newLength;
            tileParts.Add(part.ToArray());
            pos = end;
        }

        output.AddRange(main);
        if (usePpm)
        {
            // Nppm + Ippm for every tile-part, cut into marker segments that split values arbitrarily.
            var stream = new List<byte>();
            foreach (var header in ppmHeaders)
            {
                stream.Add((byte)(header.Length >> 24));
                stream.Add((byte)(header.Length >> 16));
                stream.Add((byte)(header.Length >> 8));
                stream.Add((byte)header.Length);
                stream.AddRange(header);
            }

            int index = 0;
            for (int offset = 0; offset < stream.Count; offset += 501, index++)
            {
                int chunk = Math.Min(501, stream.Count - offset);
                output.Add(0xFF);
                output.Add(0x60);
                output.Add((byte)((chunk + 3) >> 8));
                output.Add((byte)(chunk + 3));
                output.Add((byte)index);
                output.AddRange(stream.GetRange(offset, chunk));
            }
        }

        foreach (var part in tileParts)
        {
            output.AddRange(part);
        }

        output.Add(0xFF);
        output.Add(0xD9);
        return [.. output];
    }

    internal static byte[] ExtractCodestream(byte[] file)
    {
        if (file.Length > 4 && file[0] == 0xFF && file[1] == 0x4F)
        {
            return file;
        }

        int pos = 0;
        while (pos + 8 <= file.Length)
        {
            long length = ((long)file[pos] << 24) | ((long)file[pos + 1] << 16) | ((long)file[pos + 2] << 8) | file[pos + 3];
            if (file[pos + 4] == 0x6A && file[pos + 5] == 0x70 && file[pos + 6] == 0x32 && file[pos + 7] == 0x63)
            {
                return file.AsSpan(pos + 8, (int)(length == 0 ? file.Length - pos - 8 : length - 8)).ToArray();
            }

            pos += (int)length;
        }

        throw new InvalidOperationException("No codestream box.");
    }

    private static byte[] ReadAsset(string name)
    {
        using var stream = typeof(PackedPacketHeaderTests).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"Missing test asset {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
