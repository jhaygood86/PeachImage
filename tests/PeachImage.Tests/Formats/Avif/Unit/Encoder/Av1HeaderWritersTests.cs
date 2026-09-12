using PeachImage.Formats.Avif;
using PeachImage.Formats.Avif.Decoding.Av1;
using PeachImage.Formats.Avif.Encoder.Av1;

namespace PeachImage.Tests.Formats.Avif.Unit.Encoder;

/// <summary>
/// Verifies <see cref="Av1SequenceHeaderWriter"/> and <see cref="Av1FrameHeaderWriter"/> by round-tripping
/// through the existing, already-correct <see cref="Av1SequenceHeader.Parse"/>/<see cref="Av1FrameHeader.Parse"/>.
/// </summary>
public class Av1HeaderWritersTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(64, 64, false)]
    [InlineData(255, 129, false)]
    [InlineData(256, 256, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(4096, 2304, false)]
    [InlineData(3, 5, true)]
    public void Write_RoundTripsThroughParse(int width, int height, bool monoChrome)
    {
        byte[] seqBytes = Av1SequenceHeaderWriter.Write(width, height, monoChrome);
        var seqReader = new Av1BitReader(seqBytes, 0, seqBytes.Length);
        var seq = Av1SequenceHeader.Parse(seqReader);

        Assert.Equal(Av1SequenceHeaderWriter.SeqProfile, seq.SeqProfile);
        Assert.Equal(width, seq.MaxFrameWidth);
        Assert.Equal(height, seq.MaxFrameHeight);
        Assert.False(seq.Use128x128Superblock);
        Assert.True(seq.EnableFilterIntra);
        Assert.Equal(Av1SequenceHeaderWriter.EnableIntraEdgeFilter, seq.EnableIntraEdgeFilter);
        Assert.False(seq.EnableSuperres);
        Assert.False(seq.EnableCdef);
        Assert.False(seq.EnableRestoration);
        Assert.Equal(8, seq.BitDepth);
        Assert.Equal(monoChrome, seq.MonoChrome);
        Assert.Equal(Av1SequenceHeaderWriter.MatrixCoefficients, seq.MatrixCoefficients);
        Assert.True(seq.ColorRange);
        Assert.False(seq.SeparateUvDeltaQ);
        Assert.False(seq.FilmGrainParamsPresent);

        if (!monoChrome)
        {
            Assert.True(seq.SubsamplingX);
            Assert.True(seq.SubsamplingY);
        }
    }

    /// <summary>
    /// Custom (non-default) colorPrimaries/transferCharacteristics must round-trip and, critically, must NOT
    /// trip the identity-matrix short-circuit branch just because chroma444/lossless happens to force
    /// MatrixCoefficients to MC_IDENTITY -- that branch requires colorPrimaries==CP_BT_709 &&
    /// transferCharacteristics==TC_SRGB too (see Av1SequenceHeaderWriter.WriteColorConfig's own remarks).
    /// Getting this wrong would desync a real decoder by skipping/adding color_range or chroma_sample_position
    /// bits it doesn't expect.
    /// </summary>
    [Theory]
    [InlineData(64, 64, false, false, 9, 16, 1)] // 4:2:0, custom BT.2020 primaries/PQ transfer, custom chroma siting
    [InlineData(64, 64, false, true, 12, 16, 0)] // chroma444/lossless, custom Display P3 primaries -- must NOT take the identity short-circuit
    [InlineData(64, 64, false, true, 1, 13, 0)] // chroma444/lossless, default primaries/transfer -- identity short-circuit still applies (no regression)
    public void Write_CustomColorTagging_RoundTripsThroughParse(int width, int height, bool monoChrome, bool chroma444, int colorPrimaries, int transferCharacteristics, int chromaSamplePosition)
    {
        byte[] seqBytes = Av1SequenceHeaderWriter.Write(width, height, monoChrome, chroma444, colorPrimaries: colorPrimaries, transferCharacteristics: transferCharacteristics, chromaSamplePosition: chromaSamplePosition);
        var seq = Av1SequenceHeader.Parse(new Av1BitReader(seqBytes, 0, seqBytes.Length));

        Assert.Equal(colorPrimaries, seq.ColorPrimaries);
        Assert.Equal(transferCharacteristics, seq.TransferCharacteristics);
        Assert.True(seq.ColorRange);
        Assert.False(seq.SeparateUvDeltaQ);

        // matrixCoefficients is derived from chroma444/monoChrome alone (Av1FrameEncoder never exposes it as
        // an independent option -- see Av1SequenceHeaderWriter's own remarks), regardless of what
        // colorPrimaries/transferCharacteristics the caller passes; the identity-branch *bitstream shortcut*
        // (color_range/chroma_sample_position both skipped) is the separate, three-way-gated condition this
        // test's own doc comment is really about, and is exercised implicitly by every case here still
        // round-tripping correctly through Parse rather than desyncing.
        Assert.Equal(chroma444 ? Av1SequenceHeaderWriter.MatrixCoefficientsIdentity : Av1SequenceHeaderWriter.MatrixCoefficients, seq.MatrixCoefficients);
        Assert.Equal(chroma444, !seq.SubsamplingX);
        Assert.Equal(chroma444, !seq.SubsamplingY);

        if (!chroma444)
        {
            Assert.Equal(chromaSamplePosition, seq.ChromaSamplePosition);
        }
    }

    [Theory]
    [InlineData(1, 1, false, 1)]
    [InlineData(1, 1, false, 255)]
    [InlineData(64, 64, true, 32)]
    [InlineData(1920, 1080, false, 63)]
    [InlineData(4096, 2304, false, 200)]
    public void Write_FrameHeader_RoundTripsThroughParse(int width, int height, bool monoChrome, int baseQIdx)
    {
        byte[] seqBytes = Av1SequenceHeaderWriter.Write(width, height, monoChrome);
        var seq = Av1SequenceHeader.Parse(new Av1BitReader(seqBytes, 0, seqBytes.Length));

        var frameWriter = new Av1BitWriter();
        var writtenHeader = Av1FrameHeaderWriter.Write(frameWriter, width, height, monoChrome, baseQIdx);
        byte[] frameBytes = frameWriter.ToArray();

        var frameReader = new Av1BitReader(frameBytes, 0, frameBytes.Length);
        var parsedHeader = Av1FrameHeader.Parse(frameReader, seq);

        Assert.Equal(width, parsedHeader.FrameWidth);
        Assert.Equal(height, parsedHeader.FrameHeight);
        Assert.Equal(baseQIdx, parsedHeader.BaseQIdx);
        Assert.False(parsedHeader.CodedLossless);
        Assert.False(parsedHeader.AllLossless);
        Assert.False(parsedHeader.UsingQMatrix);
        Assert.False(parsedHeader.Segmentation.Enabled);
        Assert.False(parsedHeader.DeltaQPresent);
        Assert.Equal([0, 0, 0, 0], parsedHeader.LoopFilter.Level);
        Assert.Equal(0, parsedHeader.Cdef.Bits);
        Assert.False(parsedHeader.LoopRestoration.UsesLr);
        // TxModeSelect, not TxModeLargest: project plan Phase 4's own transform-size RDO -- real aomenc
        // always signals tx_mode_select for non-lossless all-intra content at this project's own tested
        // settings (see Av1FrameHeaderWriter's own txModeSelect remarks), so this writer now does too,
        // unconditionally whenever !lossless.
        Assert.Equal(Av1FrameHeader.TxModeSelect, parsedHeader.TxMode);
        Assert.True(parsedHeader.ReducedTxSet);
        Assert.False(parsedHeader.DisableCdfUpdate);
        Assert.False(parsedHeader.AllowScreenContentTools);
        Assert.False(parsedHeader.AllowIntrabc);
        Assert.Equal(1, parsedHeader.TileInfo.TileCols);
        Assert.Equal(1, parsedHeader.TileInfo.TileRows);

        // Sanity-check the manually constructed return value against what parsing the same bytes produces.
        Assert.Equal(writtenHeader.MiCols, parsedHeader.MiCols);
        Assert.Equal(writtenHeader.MiRows, parsedHeader.MiRows);
        Assert.Equal(writtenHeader.BaseQIdx, parsedHeader.BaseQIdx);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(256)]
    public void Write_FrameHeader_RejectsOutOfRangeBaseQIdx(int baseQIdx)
    {
        var writer = new Av1BitWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => Av1FrameHeaderWriter.Write(writer, 64, 64, false, baseQIdx));
    }

    [Fact]
    public void Write_TileInfo_RejectsImageTooWideForSingleTile()
    {
        var writer = new Av1BitWriter();

        // A single 64x64 superblock tile is capped at 4096px per side; something well beyond that (e.g. a
        // multi-tile-forcing width) should fail fast rather than silently produce a multi-tile bitstream
        // this v1 encoder's tile encoder can't actually populate.
        Assert.Throws<AvifEncodingException>(() => Av1FrameHeaderWriter.Write(writer, 100_000, 64, false, 32));
    }

    /// <summary>
    /// Regression test for a real bug found by decoding encoder output through dav1d (via ffmpeg) rather
    /// than only this repo's own decoder: <c>sequence_header_obu()</c> must end with <c>trailing_bits()</c>
    /// (a mandatory "1" stop bit, then zero-padding to the next byte) per spec §5.5.1, not just whatever
    /// padding falls out of rounding up to a whole byte. For most dimensions the header's natural content
    /// already leaves spare bits in its last byte, so omitting the explicit stop bit is invisible -- both
    /// this repo's own (lenient) reader and, it turns out, real decoders tolerate it. But for dimensions
    /// where the natural content lands exactly on a byte boundary (no spare bits at all), a decoder that
    /// correctly looks for the mandatory stop bit has to read one bit past the declared OBU length --
    /// exactly the "overrun" dav1d reported for a 256x256 image before this was fixed. This test asserts
    /// the structural invariant directly (no ffmpeg dependency in the permanent suite): the written header
    /// is always longer than what the content alone would require when byte-aligned, proving a stop bit
    /// was actually appended rather than relying on already-present padding.
    /// </summary>
    [Theory]
    [InlineData(256, 256)] // reproduces the original dav1d failure: content lands exactly on a byte boundary
    [InlineData(129, 129)]
    [InlineData(192, 192)]
    [InlineData(255, 255)]
    public void Write_AlwaysAppendsTrailingStopBit_EvenWhenContentIsAlreadyByteAligned(int width, int height)
    {
        byte[] seqBytes = Av1SequenceHeaderWriter.Write(width, height, monoChrome: false);

        // Re-parsing tells us how many bits the *content* logically needs; BitsRead won't include the
        // trailing stop bit itself (the parser never reads it), so a byte-boundary case must show at least
        // one full spare byte beyond the content -- if trailing_bits() were skipped, there would be none.
        var reader = new Av1BitReader(seqBytes, 0, seqBytes.Length);
        Av1SequenceHeader.Parse(reader);

        int contentBits = reader.BitsRead;
        Assert.Equal(0, contentBits % 8); // this dimension pair is chosen specifically to land byte-exact
        int contentBytes = contentBits / 8;

        Assert.True(seqBytes.Length > contentBytes, $"{width}x{height}: expected an extra byte for the mandatory trailing stop bit (content used exactly {contentBytes} bytes), but the written header is only {seqBytes.Length} bytes.");
    }
}
