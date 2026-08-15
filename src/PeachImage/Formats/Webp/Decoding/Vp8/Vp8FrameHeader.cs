using PeachImage.Formats.Webp.Internal;

namespace PeachImage.Formats.Webp.Decoding.Vp8;

/// <summary>
/// VP8's uncompressed frame tag (RFC 6386 section 9.1) plus, for a keyframe, the fixed keyframe start code and
/// 14-bit width/height fields (section 9.2). This is pure byte-level parsing that happens before any bool
/// decoder is constructed.
/// </summary>
internal sealed class Vp8FrameHeader
{
    public required bool IsKeyFrame { get; init; }

    public required int Version { get; init; }

    public required bool ShowFrame { get; init; }

    /// <summary>Length, in bytes, of partition 0 (the header + per-macroblock mode/segment data partition).</summary>
    public required int FirstPartitionLength { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Number of bytes consumed by the uncompressed tag + keyframe start code + dimensions, i.e. where partition 0 begins.</summary>
    public required int HeaderByteLength { get; init; }

    public static Vp8FrameHeader Parse(ReadOnlySpan<byte> data)
    {
        // 3-byte uncompressed frame tag + 3-byte start code + 2x 2-byte dimension fields.
        const int UncompressedHeaderLength = 10;
        if (data.Length < UncompressedHeaderLength)
        {
            throw new WebpDecodingException("VP8 chunk is too small to contain a frame header.");
        }

        uint tag = data[0] | ((uint)data[1] << 8) | ((uint)data[2] << 16);
        bool isKeyFrame = (tag & 1) == 0;
        int version = (int)((tag >> 1) & 7);
        bool showFrame = ((tag >> 4) & 1) != 0;
        int firstPartitionLength = (int)(tag >> 5);

        // WebP's `VP8 ` chunk always contains exactly one VP8 keyframe; an inter frame here would mean the
        // bitstream is either corrupt or relies on multi-frame VP8 video semantics this decoder does not support.
        if (!isKeyFrame)
        {
            throw new WebpDecodingException("VP8 bitstream is not a keyframe; only keyframe VP8 data is supported inside a WebP container.");
        }

        if (data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a)
        {
            throw new WebpDecodingException("VP8 keyframe start code is invalid.");
        }

        int width = ((data[7] << 8) | data[6]) & 0x3FFF;
        int height = ((data[9] << 8) | data[8]) & 0x3FFF;

        if (width == 0 || height == 0)
        {
            throw new WebpDecodingException("VP8 frame has a zero width or height.");
        }

        if (width > WebpDecodingLimits.MaxDimension || height > WebpDecodingLimits.MaxDimension)
        {
            throw new WebpDecodingException($"VP8 frame dimensions {width}x{height} exceed the maximum supported dimension of {WebpDecodingLimits.MaxDimension}.");
        }

        if ((long)width * height > WebpDecodingLimits.MaxPixelCount)
        {
            throw new WebpDecodingException($"VP8 frame pixel count {(long)width * height} exceeds the maximum supported pixel count of {WebpDecodingLimits.MaxPixelCount}.");
        }

        if (UncompressedHeaderLength + firstPartitionLength > data.Length)
        {
            throw new WebpDecodingException("VP8 first partition length exceeds the available chunk data.");
        }

        return new Vp8FrameHeader
        {
            IsKeyFrame = isKeyFrame,
            Version = version,
            ShowFrame = showFrame,
            FirstPartitionLength = firstPartitionLength,
            Width = width,
            Height = height,
            HeaderByteLength = UncompressedHeaderLength,
        };
    }
}