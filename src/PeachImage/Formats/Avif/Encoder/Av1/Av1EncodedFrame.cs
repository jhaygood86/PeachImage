namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// The output of <see cref="Av1FrameEncoder"/>: the encoded AV1 OBU byte stream plus the metadata the
/// container writer's <c>av1C</c> box needs. <paramref name="Chroma444"/> is always <see langword="false"/>
/// when <paramref name="MonoChrome"/> is <see langword="true"/> (no chroma planes to subsample either way).
/// <paramref name="SeqLevelIdx"/> is the same real, picture-size-computed level
/// (<see cref="Av1SequenceHeaderWriter.ComputeSeqLevelIdx"/>) the sequence header OBU actually wrote, so the
/// <c>av1C</c> box's own level field stays consistent with the real bitstream instead of a fixed constant.
/// </summary>
internal sealed record Av1EncodedFrame(byte[] ObuBytes, int Width, int Height, bool MonoChrome, bool Chroma444, int SeqLevelIdx);
