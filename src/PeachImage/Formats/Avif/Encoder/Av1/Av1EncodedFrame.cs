namespace PeachImage.Formats.Avif.Encoder.Av1;

/// <summary>The output of <see cref="Av1FrameEncoder"/>: the encoded AV1 OBU byte stream plus the metadata the container writer's <c>av1C</c> box needs.</summary>
internal sealed record Av1EncodedFrame(byte[] ObuBytes, int Width, int Height, bool MonoChrome);
