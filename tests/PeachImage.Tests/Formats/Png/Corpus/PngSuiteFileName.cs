namespace PeachImage.Tests.Formats.Png.Corpus;

public enum PngSuiteBucket
{
    /// <summary>A well-formed PngSuite test file matching the standard 8-character naming convention.</summary>
    Valid,

    /// <summary>A deliberately corrupt/malformed file (filename starts with <c>x</c>) — a conformant decoder must reject it gracefully, never hang or crash.</summary>
    Invalid,

    /// <summary>Doesn't match PngSuite's naming convention at all (e.g. the suite's own <c>PngSuite.png</c> illustration) — not a test case, excluded from every bucket.</summary>
    Excluded,
}

/// <summary>
/// Parses PngSuite's filename convention. Unlike Bmp/Jpeg's corpora — pre-bucketed by the source repo
/// into valid/non-conformant/invalid subdirectories — PngSuite ships as a flat file set whose filenames
/// self-describe the test: <c>{test}{interlace}{colorTypeDigit}{colorTypeLetter}{depth}</c>, e.g.
/// <c>basn0g01</c> (basic, non-interlaced, grayscale, 1-bit) or <c>xhdn0g08</c> (deliberately corrupt
/// IHDR). The leading <c>{test}</c> segment is usually 3 characters but occasionally 4 (e.g. the EXIF
/// test file <c>exif2c08</c>), so the <c>{interlace}{colorTypeDigit}{colorTypeLetter}{depth}</c> suffix
/// is located by scanning rather than assumed to start at a fixed offset.
/// </summary>
internal static class PngSuiteFileName
{
    public static PngSuiteBucket Classify(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);

        if (name.Length is not (8 or 9))
        {
            return PngSuiteBucket.Excluded;
        }

        if (name[0] == 'x')
        {
            return PngSuiteBucket.Invalid;
        }

        // The {test} prefix is usually 3 characters (occasionally 4, e.g. "exif"), and the interlace
        // flag is usually present (occasionally absent, e.g. "exif2c08" has no 'n'/'i' at all).
        foreach (int testIdLength in (ReadOnlySpan<int>)[3, 4])
        {
            foreach (bool hasInterlaceFlag in (ReadOnlySpan<bool>)[true, false])
            {
                int suffixLength = hasInterlaceFlag ? 5 : 4;
                if (name.Length - testIdLength != suffixLength)
                {
                    continue;
                }

                int p = testIdLength;
                if (hasInterlaceFlag)
                {
                    if (name[p] is not ('n' or 'i'))
                    {
                        continue;
                    }

                    p++;
                }

                char colorTypeDigit = name[p];
                char colorTypeLetter = name[p + 1];
                char depthTens = name[p + 2];
                char depthOnes = name[p + 3];

                if (colorTypeDigit is not ('0' or '2' or '3' or '4' or '6'))
                {
                    continue;
                }

                if (colorTypeLetter is not ('g' or 'c' or 'p' or 'a'))
                {
                    continue;
                }

                if (!char.IsAsciiDigit(depthTens) || !char.IsAsciiDigit(depthOnes))
                {
                    continue;
                }

                return PngSuiteBucket.Valid;
            }
        }

        return PngSuiteBucket.Excluded;
    }
}
