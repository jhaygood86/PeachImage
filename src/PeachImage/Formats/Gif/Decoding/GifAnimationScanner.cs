using PeachImage.Formats.Gif.Internal;

namespace PeachImage.Formats.Gif.Decoding;

/// <summary>
/// Determines whether a GIF has more than one frame, without decoding any pixel data — used to populate
/// <see cref="Image.IsAnimated"/>/<see cref="ImageInfo.IsAnimated"/> cheaply. Nothing in a GIF's header
/// declares frame count (unlike WebP's VP8X animation flag), so the only way to know is to walk forward
/// looking for a second Image Separator (0x2C) before the Trailer (0x3B). Extension blocks and each frame's
/// LZW sub-blocks are self-length-prefixed, so this never requires interpreting pixel data.
/// </summary>
internal static class GifAnimationScanner
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    /// <summary>
    /// Scans from the stream's current position (right after the header) for frame 1's transparency and
    /// whether a second frame follows — the two pieces of frame-shape info <see cref="GifDecoder.Identify"/>
    /// needs, combined into one forward pass since the stream can't be rewound. Tolerant of a frame-less
    /// stream (returns <c>(false, false)</c> rather than throwing), matching <see cref="GifDecoder.Identify"/>'s
    /// existing "header-level info only" leniency.
    /// </summary>
    public static (bool HasTransparency, bool IsAnimated) ScanForTransparencyAndAnimation(Stream stream)
    {
        GifGraphicControlExtension? pendingGce = null;

        while (true)
        {
            if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
            {
                return (false, false);
            }

            if (blockType == ExtensionIntroducer)
            {
                var (gce, _) = GifExtensionReader.Read(stream);
                pendingGce = gce ?? pendingGce;
                continue;
            }

            if (blockType != ImageSeparator)
            {
                // Reached image data (or an unrecognized block) without seeing a Graphic Control Extension.
                return (false, false);
            }

            bool hasTransparency = pendingGce?.TransparentColorIndex.HasValue ?? false;
            SkipFrame(stream);
            return (hasTransparency, HasAnotherFrame(stream));
        }
    }

    /// <summary>
    /// From the stream's current position — anywhere between two frames' data — returns <see langword="true"/>
    /// as soon as another Image Separator is found before the Trailer/end of stream.
    /// </summary>
    public static bool HasAnotherFrame(Stream stream)
    {
        while (true)
        {
            if (!GifStreamHelpers.TryReadByte(stream, out byte blockType) || blockType == Trailer)
            {
                return false;
            }

            if (blockType == ExtensionIntroducer)
            {
                GifExtensionReader.Read(stream);
                continue;
            }

            return blockType == ImageSeparator;
        }
    }

    /// <summary>
    /// Skips one frame's Image Descriptor (+ Local Color Table), LZW minimum-code-size byte, and LZW
    /// sub-blocks structurally — no LZW decode, no pixel data ever touched. The frame's Image Separator (0x2C)
    /// must already be consumed, matching <see cref="GifImageDescriptorReader.Read"/>'s own contract.
    /// </summary>
    private static void SkipFrame(Stream stream)
    {
        GifImageDescriptorReader.Read(stream);
        GifStreamHelpers.ReadByteOrThrow(stream);
        var (imageData, _) = GifSubBlocks.ReadAllImageData(stream);
        GifBufferPool.Shared.Return(imageData);
    }
}
