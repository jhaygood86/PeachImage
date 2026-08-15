namespace PeachImage.Formats.Webp.Decoding.Alpha;

/// <summary>How an <c>ALPH</c> chunk's payload bytes (after its 1-byte header) were compressed.</summary>
internal enum WebpAlphaCompressionMethod
{
    /// <summary>The payload is exactly <c>width*height</c> raw alpha samples, row-major.</summary>
    Uncompressed = 0,

    /// <summary>The payload is a VP8L bitstream with no 5-byte header (dimensions already known from the container); only its decoded green channel is meaningful.</summary>
    Lossless = 1,
}

/// <summary>Which row-prediction filter was applied to the decompressed alpha plane, to be reversed before use.</summary>
internal enum WebpAlphaFilterMethod
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Gradient = 3,
}

/// <summary>An <c>ALPH</c> chunk's 1-byte header: <c>Rsv(2) | Preprocessing(2) | Filter(2) | Compression(2)</c>, MSB first.</summary>
internal readonly record struct WebpAlphaHeader(WebpAlphaCompressionMethod CompressionMethod, WebpAlphaFilterMethod FilterMethod)
{
    public static WebpAlphaHeader Parse(byte headerByte)
    {
        int compression = headerByte & 0x03;
        int filter = (headerByte >> 2) & 0x03;

        // Preprocessing bits (headerByte >> 4 & 0x03) record an encoder-side level-reduction dithering hint —
        // informational only, nothing for the decoder to undo. Bits 6-7 are reserved and ignored.
        if (compression > 1)
        {
            throw new WebpDecodingException($"Unsupported WebP alpha compression method {compression}.");
        }

        return new WebpAlphaHeader((WebpAlphaCompressionMethod)compression, (WebpAlphaFilterMethod)filter);
    }
}
