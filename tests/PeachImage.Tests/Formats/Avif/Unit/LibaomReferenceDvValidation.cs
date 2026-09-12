namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Independent, general (multi-tile-capable) transcription of libaom's own real <c>av1_is_dv_valid</c>
/// (<c>av1/common/mvref_common.h</c>, read directly line-by-line for this port -- not derived from
/// PeachImage's own <c>Av1TileEncoder.IsValidIntrabcSourcePixels</c>, which is a single-tile-only
/// specialization of the same rule) and its own <c>is_chroma_reference</c> helper
/// (<c>av1/common/av1_common_int.h</c>). Deliberately keeps every real parameter the C function takes
/// (absolute frame-relative <c>mi_row</c>/<c>mi_col</c>, the DV in 1/8-luma-sample units, block width/height
/// in pixels, <c>mib_size_log2</c>, and the tile's own <c>mi_row_start</c>/<c>mi_row_end</c>/
/// <c>mi_col_start</c>/<c>mi_col_end</c> plus chroma-reference/subsampling/plane-count inputs) instead of
/// specializing to a single-tile, zero-origin tile the way PeachImage's own production function does --
/// this needs to validate against libaom's own real <c>intrabc_test.cc</c> <c>kDvCases</c> table first,
/// which deliberately uses a non-zero tile origin to exercise real multi-tile behavior.
///
/// <para><b>Units</b>: <c>miRow</c>/<c>miCol</c> and every <c>tileMi*</c> bound are in 4-pixel mi units,
/// absolute (frame-relative, not tile-relative) -- matching the real function's own <c>mi_row</c>/
/// <c>mi_col</c>/<c>tile-&gt;mi_row_start</c> etc. exactly. <c>dvRow</c>/<c>dvCol</c> are in 1/8-luma-sample
/// units (the real <c>MV</c> struct's own scale) -- <em>not</em> pixels and <em>not</em> mi units.
/// <c>bw</c>/<c>bh</c> are in pixels (the real <c>block_size_wide</c>/<c>block_size_high</c> tables' own
/// units). All square block sizes actually used by the real <c>kDvCases</c> table have <c>bw == bh</c> and
/// mi-unit width/height <c>== bw / MiSize</c>, so this port never needs libaom's own separate
/// <c>mi_size_wide</c>/<c>mi_size_high</c> tables -- <see cref="IsChromaReference"/> derives them from
/// <c>bw</c>/<c>bh</c> directly.</para>
/// </summary>
internal static class LibaomReferenceDvValidation
{
    /// <summary><c>MI_SIZE</c> (<c>av1/common/enums.h</c>): pixels per mi unit.</summary>
    public const int MiSize = 4;

    /// <summary><c>MAX_SB_SIZE_LOG2</c>/<c>MAX_SB_SIZE</c> (<c>av1/common/enums.h</c>): the largest real AV1 superblock, in pixels.</summary>
    public const int MaxSbSizeLog2 = 7;
    public const int MaxSbSize = 1 << MaxSbSizeLog2;

    /// <summary><c>MAX_MIB_SIZE_LOG2</c>/<c>MAX_MIB_SIZE</c> (<c>av1/common/enums.h</c>): the largest real AV1 superblock, in mi units.</summary>
    public const int MaxMibSizeLog2 = MaxSbSizeLog2 - 2; // MI_SIZE_LOG2 == 2
    public const int MaxMibSize = 1 << MaxMibSizeLog2;

    /// <summary><c>INTRABC_DELAY_PIXELS</c>/<c>INTRABC_DELAY_SB64</c> (<c>av1/common/mvref_common.h</c>).</summary>
    public const int IntrabcDelayPixels = 256;
    public const int IntrabcDelaySb64 = IntrabcDelayPixels / 64;

    private const int ScalePxToMv = 8;

    /// <summary>
    /// Faithful transcription of <c>is_chroma_reference</c> (<c>av1/common/av1_common_int.h</c>), taking
    /// pixel-unit <paramref name="bw"/>/<paramref name="bh"/> instead of libaom's own separate
    /// <c>mi_size_wide</c>/<c>mi_size_high</c> tables (equivalent for the square block sizes this port is
    /// ever exercised with -- see this class's own remarks).
    /// </summary>
    public static bool IsChromaReference(int miRow, int miCol, int bw, int bh, int subsamplingX, int subsamplingY)
    {
        int miW = bw / MiSize;
        int miH = bh / MiSize;
        bool refPos = ((miRow & 1) != 0 || (miH & 1) == 0 || subsamplingY == 0)
            && ((miCol & 1) != 0 || (miW & 1) == 0 || subsamplingX == 0);
        return refPos;
    }

    /// <summary>
    /// Faithful, literal transcription of <c>av1_is_dv_valid</c> (<c>av1/common/mvref_common.h</c>, lines
    /// 279-338 as read for this port), generalized to take the tile's own bounds explicitly rather than
    /// assuming a zero-origin, whole-frame tile.
    /// </summary>
    public static bool IsDvValid(
        int dvRow, int dvCol,
        int miRow, int miCol,
        int bw, int bh,
        int mibSizeLog2,
        int tileMiRowStart, int tileMiRowEnd, int tileMiColStart, int tileMiColEnd,
        bool isChromaRef, int subsamplingX, int subsamplingY, int numPlanes)
    {
        // Disallow subpixel for now. SUBPEL_MASK is not the correct scale.
        if ((dvRow & (ScalePxToMv - 1)) != 0 || (dvCol & (ScalePxToMv - 1)) != 0)
        {
            return false;
        }

        // Is the source top-left inside the current tile?
        int srcTopEdge = (miRow * MiSize * ScalePxToMv) + dvRow;
        int tileTopEdge = tileMiRowStart * MiSize * ScalePxToMv;
        if (srcTopEdge < tileTopEdge)
        {
            return false;
        }

        int srcLeftEdge = (miCol * MiSize * ScalePxToMv) + dvCol;
        int tileLeftEdge = tileMiColStart * MiSize * ScalePxToMv;
        if (srcLeftEdge < tileLeftEdge)
        {
            return false;
        }

        // Is the bottom right inside the current tile?
        int srcBottomEdge = (((miRow * MiSize) + bh) * ScalePxToMv) + dvRow;
        int tileBottomEdge = tileMiRowEnd * MiSize * ScalePxToMv;
        if (srcBottomEdge > tileBottomEdge)
        {
            return false;
        }

        int srcRightEdge = (((miCol * MiSize) + bw) * ScalePxToMv) + dvCol;
        int tileRightEdge = tileMiColEnd * MiSize * ScalePxToMv;
        if (srcRightEdge > tileRightEdge)
        {
            return false;
        }

        // Special case for sub 8x8 chroma cases, to prevent referring to chroma pixels outside current tile.
        if (isChromaRef && numPlanes > 1)
        {
            if (bw < 8 && subsamplingX != 0 && srcLeftEdge < tileLeftEdge + (4 * ScalePxToMv))
            {
                return false;
            }

            if (bh < 8 && subsamplingY != 0 && srcTopEdge < tileTopEdge + (4 * ScalePxToMv))
            {
                return false;
            }
        }

        // Is the bottom right within an already coded SB? Also consider additional constraints to
        // facilitate HW decoder.
        int maxMibSize = 1 << mibSizeLog2;
        int activeSbRow = miRow >> mibSizeLog2;
        int activeSb64Col = (miCol * MiSize) >> 6;
        int sbSize = maxMibSize * MiSize;
        int srcSbRow = ((srcBottomEdge >> 3) - 1) / sbSize;
        int srcSb64Col = ((srcRightEdge >> 3) - 1) >> 6;
        int totalSb64PerRow = ((tileMiColEnd - tileMiColStart - 1) >> 4) + 1;
        int activeSb64 = (activeSbRow * totalSb64PerRow) + activeSb64Col;
        int srcSb64 = (srcSbRow * totalSb64PerRow) + srcSb64Col;
        if (srcSb64 >= activeSb64 - IntrabcDelaySb64)
        {
            return false;
        }

        // Wavefront constraint: use only top left area of frame for reference.
        int gradient = 1 + IntrabcDelaySb64 + (sbSize > 64 ? 1 : 0);
        int wfOffset = gradient * (activeSbRow - srcSbRow);
        if (srcSbRow > activeSbRow || srcSb64Col >= activeSb64Col - IntrabcDelaySb64 + wfOffset)
        {
            return false;
        }

        return true;
    }
}
