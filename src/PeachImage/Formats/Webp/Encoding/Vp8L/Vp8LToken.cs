namespace PeachImage.Formats.Webp.Encoding.Vp8L;

/// <summary>The three shapes a decoded VP8L pixel-stream symbol can take, mirroring <see cref="Decoding.Vp8L.Vp8LPixelDecoder"/>'s three decode branches.</summary>
internal enum Vp8LTokenKind
{
    Literal,
    BackwardReference,
    CacheIndex,
}

/// <summary>One emitted VP8L pixel-stream token, produced by <see cref="Vp8LTokenizer"/> and consumed by <see cref="Vp8LTokenWriter"/>.</summary>
internal readonly struct Vp8LToken
{
    public required Vp8LTokenKind Kind { get; init; }

    /// <summary>The packed ARGB pixel value. Only meaningful for <see cref="Vp8LTokenKind.Literal"/>.</summary>
    public uint Argb { get; init; }

    /// <summary>The backward reference's copy length. Only meaningful for <see cref="Vp8LTokenKind.BackwardReference"/>.</summary>
    public int Length { get; init; }

    /// <summary>
    /// The backward reference's already-mapped plane code (the value <see cref="Vp8LPrefixCodeEncoder.EncodePrefixCodeValue"/>
    /// is applied to, i.e. the output of <see cref="Vp8LDistanceMapper.DistanceToPlaneCode"/> — not a raw
    /// pixel distance). Only meaningful for <see cref="Vp8LTokenKind.BackwardReference"/>.
    /// </summary>
    public int PlaneCode { get; init; }

    /// <summary>The color cache slot index. Only meaningful for <see cref="Vp8LTokenKind.CacheIndex"/>.</summary>
    public int CacheIndex { get; init; }
}

/// <summary>Per-alphabet symbol-frequency histograms accumulated alongside token emission, feeding <see cref="Vp8LHuffmanCodeBuilder.BuildCodeLengths"/> for each of the five alphabets.</summary>
internal sealed class Vp8LTokenHistograms
{
    public required int[] Green { get; init; }

    public required int[] Red { get; init; }

    public required int[] Blue { get; init; }

    public required int[] Alpha { get; init; }

    public required int[] Distance { get; init; }
}

/// <summary>A built canonical Huffman code for one alphabet: one length and one LSB-first code per symbol, parallel-indexed.</summary>
internal readonly struct Vp8LBuiltHuffmanCode
{
    public required int[] Lengths { get; init; }

    public required uint[] Codes { get; init; }
}
