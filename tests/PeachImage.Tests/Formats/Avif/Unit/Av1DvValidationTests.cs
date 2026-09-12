using System.Reflection;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Port of libaom's own <c>test/intrabc_test.cc</c> <c>IntrabcTest.DvValidation</c>.
///
/// <para><b>Step 1 (<see cref="KnownVectors_MatchLibaomReference"/>)</b>: the real test's own exact
/// <c>kDvCases</c> table, transcribed verbatim (same dv/mi_row_offset/mi_col_offset/bsize/expected-valid
/// values, same non-zero tile origin the real test itself uses -- <c>xd.tile.mi_row_start = 8 *
/// MAX_MIB_SIZE</c>, <c>xd.tile.mi_col_start = 24 * MAX_MIB_SIZE</c>), run against
/// <see cref="LibaomReferenceDvValidation"/>'s own independent, general transcription of
/// <c>av1_is_dv_valid</c>. This is a warm-up sanity check on the reference port itself (the same role
/// <c>LibaomReferenceWht</c>'s own known-vector diagnostic played for the WHT port) -- it does not touch
/// PeachImage's production code at all.</para>
///
/// <para><b>Step 2 (<see cref="MatchesPeachImageProduction"/>)</b>: the same general reference, specialized
/// to the zero-tile-origin, single-tile, <c>mib_size_log2 == 5</c> (128x128 superblock) case that
/// <c>Av1TileEncoder.IsValidIntrabcSourcePixels</c> is deliberately hard-coded for (see that method's own
/// doc comment), compared directly against that real production method via reflection (it and its
/// <c>TileState</c> parameter type are both private) -- no hand-duplicated re-transcription of its logic,
/// so a real disagreement here can only be a genuine behavioral difference between the reference and
/// production code, not a bug in a second copy of the same arithmetic. Two of the real <c>kDvCases</c>
/// scenarios (the subpixel-rejection ones) are intentionally not reproduced here: <c>IsValidIntrabcSourcePixels</c>
/// takes raw whole-pixel <c>srcX</c>/<c>srcY</c> coordinates, not a 1/8-pel <c>dv</c>, so there is no way to
/// even represent a subpel offset through its real signature -- see that method's own doc comment ("every
/// call site must pass srcX/srcY as received... there's no further requirement that the referenced position
/// be a multiple of 4 pixels") confirming this is real, deliberate API scope, not an oversight.</para>
/// </summary>
public class Av1DvValidationTests
{
    private const int KSubPelScale = 8;
    private const int KTileMaxMibWidth = 8;
    private const int MaxSbSize = LibaomReferenceDvValidation.MaxSbSize; // 128
    private const int MiSize = LibaomReferenceDvValidation.MiSize; // 4
    private const int MaxMibSize = LibaomReferenceDvValidation.MaxMibSize; // 32
    private const int MaxMibSizeLog2 = LibaomReferenceDvValidation.MaxMibSizeLog2; // 5

    // BLOCK_SIZE pixel widths/heights (all square block sizes actually used by kDvCases).
    private const int Block4x4 = 4;
    private const int Block8x8 = 8;
    private const int Block16x16 = 16;
    private const int Block32x32 = 32;
    private const int Block64x64 = 64;
    private const int Block128x128 = 128; // == BLOCK_LARGEST

    /// <summary>
    /// Verbatim transcription of <c>intrabc_test.cc</c>'s own <c>kDvCases</c> array (26 entries):
    /// (dvRow, dvCol, miRowOffset, miColOffset, bsize, expectedValid).
    /// </summary>
    private static readonly (int DvRow, int DvCol, int MiRowOffset, int MiColOffset, int Bsize, bool Valid)[] KDvCases =
    [
        (0, 0, 0, 0, Block128x128, false),
        (0, 0, 0, 0, Block64x64, false),
        (0, 0, 0, 0, Block32x32, false),
        (0, 0, 0, 0, Block16x16, false),
        (0, 0, 0, 0, Block8x8, false),
        (0, 0, 0, 0, Block4x4, false),
        (-MaxSbSize * KSubPelScale, -MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block16x16, true),
        (0, -MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block16x16, false),
        (-MaxSbSize * KSubPelScale, 0, MaxSbSize / MiSize, MaxSbSize / MiSize, Block16x16, true),
        (MaxSbSize * KSubPelScale, 0, MaxSbSize / MiSize, MaxSbSize / MiSize, Block16x16, false),
        (0, MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block16x16, false),
        (-32 * KSubPelScale, -32 * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block32x32, true),
        (-32 * KSubPelScale, -32 * KSubPelScale, 32 / MiSize, 32 / MiSize, Block32x32, false),
        (-32 * KSubPelScale - (KSubPelScale / 2), -32 * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block32x32, false),
        (-33 * KSubPelScale, -32 * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block32x32, true),
        (-32 * KSubPelScale, -32 * KSubPelScale - (KSubPelScale / 2), MaxSbSize / MiSize, MaxSbSize / MiSize, Block32x32, false),
        (-32 * KSubPelScale, -33 * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block32x32, true),
        (-MaxSbSize * KSubPelScale, -MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, true),
        (-(MaxSbSize + 1) * KSubPelScale, -MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
        (-MaxSbSize * KSubPelScale, -(MaxSbSize + 1) * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
        (-(MaxSbSize - 1) * KSubPelScale, -MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
        (-MaxSbSize * KSubPelScale, -(MaxSbSize - 1) * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, true),
        (-(MaxSbSize - 1) * KSubPelScale, -(MaxSbSize - 1) * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
        (-MaxSbSize * KSubPelScale, MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
        (-MaxSbSize * KSubPelScale, (KTileMaxMibWidth - 2) * MaxSbSize * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
        (-MaxSbSize * KSubPelScale, (((KTileMaxMibWidth - 2) * MaxSbSize) + 1) * KSubPelScale, MaxSbSize / MiSize, MaxSbSize / MiSize, Block128x128, false),
    ];

    public static TheoryData<int> CaseIndices()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < KDvCases.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIndices))]
    public void KnownVectors_MatchLibaomReference(int index)
    {
        // xd.tile.mi_row_start = 8 * MAX_MIB_SIZE; xd.tile.mi_row_end = 16 * MAX_MIB_SIZE;
        // xd.tile.mi_col_start = 24 * MAX_MIB_SIZE; xd.tile.mi_col_end = mi_col_start + kTileMaxMibWidth * MAX_MIB_SIZE;
        const int tileMiRowStart = 8 * MaxMibSize;
        const int tileMiRowEnd = 16 * MaxMibSize;
        const int tileMiColStart = 24 * MaxMibSize;
        const int tileMiColEnd = tileMiColStart + (KTileMaxMibWidth * MaxMibSize);
        const int subsamplingX = 1;
        const int subsamplingY = 1;
        const int numPlanes = 3; // seq_params.monochrome == 0 (zero-initialized) -> av1_num_planes == MAX_MB_PLANE.

        var c = KDvCases[index];
        int miRow = tileMiRowStart + c.MiRowOffset;
        int miCol = tileMiColStart + c.MiColOffset;
        bool isChromaRef = LibaomReferenceDvValidation.IsChromaReference(miRow, miCol, c.Bsize, c.Bsize, subsamplingX, subsamplingY);

        bool actual = LibaomReferenceDvValidation.IsDvValid(
            c.DvRow, c.DvCol,
            miRow, miCol,
            c.Bsize, c.Bsize,
            MaxMibSizeLog2,
            tileMiRowStart, tileMiRowEnd, tileMiColStart, tileMiColEnd,
            isChromaRef, subsamplingX, subsamplingY, numPlanes);

        Assert.True(c.Valid == actual, $"kDvCases[{index}]: expected {c.Valid}, got {actual}");
    }

    /// <summary>
    /// Representative boundary cases covering the same kinds of behavior <c>kDvCases</c> exercises --
    /// self-reference rejection, exact-SB-back acceptance, SB64 delay, wavefront gradient (including the
    /// 128x128-superblock +1 term), and frame-edge out-of-bounds rejection -- reinterpreted for a
    /// zero-origin, single-tile, whole-frame tile (<c>Av1TileEncoder.IsValidIntrabcSourcePixels</c>'s own
    /// only real scenario) instead of reusing <c>kDvCases</c>' own non-zero tile origin numbers verbatim.
    /// Frame is 256x256 mi units (1024x1024 px, an 8x8 grid of 128px superblocks) -- deliberately the same
    /// scale as <c>kDvCases</c>' own tile (<c>mi_row_end - mi_row_start == mi_col_end - mi_col_start ==
    /// 256</c> mi there too), so the same offsets exercise equivalent boundary conditions.
    /// (dvRow, dvCol, miRow, miCol, bsize) -- all in the reference's own absolute-mi/1-8-pel units. The two
    /// real subpel-rejection kDvCases entries are intentionally not reproduced (see class remarks).
    /// </summary>
    private static readonly (int DvRow, int DvCol, int MiRow, int MiCol, int Bsize)[] SpecializationCases =
    [
        // Self-reference: must always be rejected (SB64 delay).
        (0, 0, 32, 32, Block128x128),
        (0, 0, 32, 32, Block64x64),
        (0, 0, 32, 32, Block16x16),
        (0, 0, 32, 32, Block8x8),

        // Exactly one MAX_SB_SIZE back diagonally: valid. Row-only/col-only variants: invalid (matches
        // kDvCases entries 7-11's own row/col-only pattern).
        (-MaxSbSize * KSubPelScale, -MaxSbSize * KSubPelScale, 32, 32, Block16x16),
        (0, -MaxSbSize * KSubPelScale, 32, 32, Block16x16),
        (-MaxSbSize * KSubPelScale, 0, 32, 32, Block16x16),
        (MaxSbSize * KSubPelScale, 0, 32, 32, Block16x16),
        (0, MaxSbSize * KSubPelScale, 32, 32, Block16x16),

        // One-SB-back for BLOCK_32X32, at both a superblock-aligned and a non-superblock-aligned active
        // position (kDvCases entries 12/13's own not-yet-reached-far-enough contrast).
        (-32 * KSubPelScale, -32 * KSubPelScale, 32, 32, Block32x32),
        (-32 * KSubPelScale, -32 * KSubPelScale, 8, 8, Block32x32),

        // One-pixel-short-of-a-full-SB-back vs exactly one-pixel-further: SB64 delay boundary
        // (kDvCases entries 15/17's own +/-1 pixel pattern).
        (-33 * KSubPelScale, -32 * KSubPelScale, 32, 32, Block32x32),
        (-32 * KSubPelScale, -33 * KSubPelScale, 32, 32, Block32x32),

        // BLOCK_128X128 (BLOCK_LARGEST): exact one-SB-back valid, one-pixel-over/short invalid in each axis,
        // exercising the sb_size > 64 -> +1 gradient term (kDvCases entries 18-23).
        (-MaxSbSize * KSubPelScale, -MaxSbSize * KSubPelScale, 32, 32, Block128x128),
        (-(MaxSbSize + 1) * KSubPelScale, -MaxSbSize * KSubPelScale, 32, 32, Block128x128),
        (-MaxSbSize * KSubPelScale, -(MaxSbSize + 1) * KSubPelScale, 32, 32, Block128x128),
        (-(MaxSbSize - 1) * KSubPelScale, -MaxSbSize * KSubPelScale, 32, 32, Block128x128),
        (-MaxSbSize * KSubPelScale, -(MaxSbSize - 1) * KSubPelScale, 32, 32, Block128x128),
        (-(MaxSbSize - 1) * KSubPelScale, -(MaxSbSize - 1) * KSubPelScale, 32, 32, Block128x128),

        // Positive (forward) DV: always rejected regardless of magnitude (kDvCases entry 24).
        (-MaxSbSize * KSubPelScale, MaxSbSize * KSubPelScale, 32, 32, Block128x128),

        // Wavefront gradient reach within tile width, near and just past the frame's own right edge
        // (kDvCases entries 25/26's own kTileMaxMibWidth-relative pattern, reinterpreted against this
        // frame's own 256-mi/8-SB64 width).
        (-MaxSbSize * KSubPelScale, (KTileMaxMibWidth - 2) * MaxSbSize * KSubPelScale, 32, 32, Block128x128),
        (-MaxSbSize * KSubPelScale, (((KTileMaxMibWidth - 2) * MaxSbSize) + 1) * KSubPelScale, 32, 32, Block128x128),

        // Out-of-frame source (negative pixel position): rejected by the tile/frame bound check itself.
        (-2000, 0, 4, 4, Block16x16),
        (0, -2000, 4, 4, Block16x16),

        // Source overhanging the bottom/right frame edge: rejected by the tile/frame bound check.
        (2000, 0, 32, 32, Block16x16),
        (0, 2000, 32, 32, Block16x16),
    ];

    public static TheoryData<int> SpecializationCaseIndices()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < SpecializationCases.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SpecializationCaseIndices))]
    public void MatchesPeachImageProduction(int index)
    {
        // Zero-origin, single-tile, whole-frame tile -- Av1TileEncoder.IsValidIntrabcSourcePixels's own only
        // real scenario (see its own doc comment).
        const int frameMi = 256; // 1024x1024 px, an 8x8 grid of 128px superblocks -- matches kDvCases' own tile scale.
        const int subsamplingX = 0;
        const int subsamplingY = 0;
        const int numPlanes = 1; // never taken (every case here has bw/bh >= 8) -- see class remarks.

        var c = SpecializationCases[index];
        bool reference = LibaomReferenceDvValidation.IsDvValid(
            c.DvRow, c.DvCol,
            c.MiRow, c.MiCol,
            c.Bsize, c.Bsize,
            MaxMibSizeLog2,
            tileMiRowStart: 0, tileMiRowEnd: frameMi, tileMiColStart: 0, tileMiColEnd: frameMi,
            isChromaRef: false, subsamplingX, subsamplingY, numPlanes);

        int x = c.MiCol * MiSize;
        int y = c.MiRow * MiSize;
        int srcX = x + (c.DvCol / 8);
        int srcY = y + (c.DvRow / 8);

        bool production = InvokeIsValidIntrabcSourcePixels(frameMi, frameMi, x, y, c.Bsize, srcX, srcY);

        Assert.True(reference == production,
            $"SpecializationCases[{index}]: reference={reference}, production={production} " +
            $"(x={x}, y={y}, sizePixels={c.Bsize}, srcX={srcX}, srcY={srcY})");
    }

    private static readonly Type TileStateType = typeof(Av1TileEncoder)
        .GetNestedType("TileState", BindingFlags.NonPublic)!;

    private static readonly MethodInfo IsValidIntrabcSourcePixelsMethod = typeof(Av1TileEncoder)
        .GetMethod("IsValidIntrabcSourcePixels", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Constructs a minimal <c>TileState</c> via reflection (its constructor and every field are private/
    /// <c>required</c>-but-CLR-unenforced) and invokes the real, private
    /// <c>IsValidIntrabcSourcePixels(TileState, int x, int y, int sizePixels, int srcX, int srcY)</c>
    /// directly -- confirmed by reading that method's own body (above) that it only ever reads
    /// <c>s.MiRows</c>/<c>s.MiCols</c>/<c>s.Lossless</c> off the state, so those are the only three fields
    /// this helper needs to populate.
    /// </summary>
    private static bool InvokeIsValidIntrabcSourcePixels(int miRows, int miCols, int x, int y, int sizePixels, int srcX, int srcY)
    {
        object state = Activator.CreateInstance(TileStateType, nonPublic: true)!;
        TileStateType.GetField("MiRows")!.SetValue(state, miRows);
        TileStateType.GetField("MiCols")!.SetValue(state, miCols);
        TileStateType.GetField("Lossless")!.SetValue(state, true); // lossless always uses 128x128 superblocks -- see IsValidIntrabcSourcePixels's own doc comment.

        object? result = IsValidIntrabcSourcePixelsMethod.Invoke(null, [state, x, y, sizePixels, srcX, srcY]);
        return (bool)result!;
    }
}
