namespace PeachImage.Formats.Webp.Decoding;

/// <summary>The result of parsing a WebP file's RIFF container: which inner bitstream it carries, plus any container-level extras.</summary>
internal sealed class WebpContainerInfo
{
    /// <summary>Whether the image chunk is VP8 (lossy) or VP8L (lossless).</summary>
    public required WebpBitstreamFormat Format { get; init; }

    /// <summary>The raw payload of the <c>VP8 </c>/<c>VP8L</c> chunk, untouched — handed to the matching bitstream decoder.</summary>
    public required byte[] BitstreamData { get; init; }

    /// <summary>
    /// How many bytes of <see cref="BitstreamData"/> are the payload: the whole array unless it was rented from a pool (and so may be longer).
    /// </summary>
    public int BitstreamLength { get; init; } = -1;

    /// <summary>Whether <see cref="BitstreamData"/> was rented from <see cref="Internal.WebpBufferPool"/> and has to go back through <see cref="Release"/>.</summary>
    public bool BitstreamIsPooled { get; init; }

    /// <summary>The payload itself, exactly: <see cref="BitstreamData"/> trimmed to <see cref="BitstreamLength"/>.</summary>
    public ReadOnlyMemory<byte> Bitstream => BitstreamData.AsMemory(0, BitstreamLength < 0 ? BitstreamData.Length : BitstreamLength);

    /// <summary>Returns a rented bitstream buffer to its pool. The payload must not be read afterward.</summary>
    public void Release()
    {
        if (BitstreamIsPooled)
        {
            Internal.WebpBufferPool.Shared.Return(BitstreamData);
        }
    }

    /// <summary>The raw payload of the <c>ALPH</c> chunk, if the file is extended-format lossy-with-alpha. Always <see langword="null"/> for lossless images (VP8L carries its own alpha).</summary>
    public byte[]? AlphaData { get; init; }

    /// <summary>The VP8X chunk's declared canvas width, or <see langword="null"/> for simple-format files (no VP8X chunk).</summary>
    public int? CanvasWidth { get; init; }

    /// <summary>The VP8X chunk's declared canvas height, or <see langword="null"/> for simple-format files (no VP8X chunk).</summary>
    public int? CanvasHeight { get; init; }
}
