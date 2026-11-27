namespace PeachImage.Tests.Formats.Jpeg;

/// <summary>A minimal CMYK output profile (v2, <c>mft1</c> A2B0 with a two-point grid, Lab PCS) where only white paper and pure cyan are meaningful.</summary>
internal static class TinyCmykProfile
{
    public static byte[] Build()
    {
        var lut = new List<byte>();
        void U32(List<byte> l, uint v) { l.Add((byte)(v >> 24)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 8)); l.Add((byte)v); }
        void S15(List<byte> l, double v) => U32(l, (uint)(int)Math.Round(v * 65536));

        U32(lut, 0x6D667431); // 'mft1'
        U32(lut, 0);
        lut.AddRange([4, 3, 2, 0]); // 4 in, 3 out, 2 grid points, pad
        double[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        foreach (double v in identity)
        {
            S15(lut, v);
        }

        for (int channel = 0; channel < 4; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                lut.Add((byte)i);
            }
        }

        // CLUT, C slowest to K fastest. Lab8: L = value / 255 * 100, a/b = value - 128.
        for (int index = 0; index < 16; index++)
        {
            int c = (index >> 3) & 1, m = (index >> 2) & 1, y = (index >> 1) & 1, k = index & 1;
            byte[] lab = (c, m, y, k) switch
            {
                (0, 0, 0, 0) => [255, 128, 128], // white paper
                (1, 0, 0, 0) => [140, 91, 78],   // cyan: L 55, a -37, b -50
                _ => [51, 128, 128],             // everything else: dark grey
            };
            lut.AddRange(lab);
        }

        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = 0; i < 256; i++)
            {
                lut.Add((byte)i);
            }
        }

        while (lut.Count % 4 != 0)
        {
            lut.Add(0);
        }

        const int tagCount = 1;
        int dataOffset = 128 + 4 + (12 * tagCount);
        var profile = new List<byte>();
        U32(profile, (uint)(dataOffset + lut.Count));
        U32(profile, 0);
        U32(profile, 0x02100000); // v2.1
        U32(profile, 0x70727472); // 'prtr'
        U32(profile, 0x434D594B); // 'CMYK'
        U32(profile, 0x4C616220); // 'Lab '
        profile.AddRange(new byte[12]); // date
        U32(profile, 0x61637370); // 'acsp'
        profile.AddRange(new byte[4 + 4 + 4 + 4 + 8 + 4]); // platform, flags, manufacturer, model, attributes, intent
        S15(profile, 0.9642);
        S15(profile, 1.0);
        S15(profile, 0.8249);
        while (profile.Count < 128)
        {
            profile.Add(0);
        }

        U32(profile, tagCount);
        U32(profile, 0x41324230); // 'A2B0'
        U32(profile, (uint)dataOffset);
        U32(profile, (uint)lut.Count);
        profile.AddRange(lut);
        return [.. profile];
    }
}
