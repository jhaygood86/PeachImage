# PeachImage 0.7.3

**Release date:** November 3, 2026
**Full changelog:** [v0.7.2...v0.7.3](https://github.com/jhaygood86/PeachImage/compare/v0.7.2...v0.7.3)
**Release tag:** [v0.7.3](https://github.com/jhaygood86/PeachImage/releases/tag/v0.7.3)

A fix for 0.7.2's pixel density.

## Fixes

- **Density survives `DecoderOptions.TargetPixelFormat`.** `ImageMetadata.HorizontalResolution` and `VerticalResolution` (pixels per metre) were read by the decoders but lost when a target pixel format made the decoder build a new image, which is what a caller that always asks for `Rgba32` gets. BMP, TIFF, WebP, AVIF, JPEG XL, GIF and PNG now keep it through the conversion, including the density a WebP, AVIF, JPEG XL or PNG carries only in its EXIF block.

## Behaviour notes

- Only the density is carried across that conversion. Profiles (EXIF, ICC, XMP) are still not attached to an image the converter had to rebuild; that is unchanged.
