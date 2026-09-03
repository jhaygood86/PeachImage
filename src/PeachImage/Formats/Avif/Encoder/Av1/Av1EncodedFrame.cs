namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>
/// The output of <see cref="Av1FrameEncoder"/>: the encoded AV1 OBU byte stream plus the metadata the
/// container writer's <c>av1C</c> box needs. <paramref name="Chroma444"/> is always <see langword="false"/>
/// when <paramref name="MonoChrome"/> is <see langword="true"/> (no chroma planes to subsample either way).
/// </summary>
internal sealed record Av1EncodedFrame(byte[] ObuBytes, int Width, int Height, bool MonoChrome, bool Chroma444);
