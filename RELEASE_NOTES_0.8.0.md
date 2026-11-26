# PeachImage 0.8.0

**Release date:** unreleased
**Full changelog:** [v0.7.4...v0.8.0](https://github.com/jhaygood86/PeachImage/compare/v0.7.4...v0.8.0)
**Release tag:** [v0.8.0](https://github.com/jhaygood86/PeachImage/releases/tag/v0.8.0)

Color-managed, high-precision imaging for canvas-style hosts, on top of the move to .NET 10 and 11: named color spaces with built-in ICC profiles, float color space conversion, half-float and premultiplied pixel formats, HDR (PQ/HLG) decode, tone mapping and signalling, 10- and 12-bit AVIF encode and decode, and a `createImageBitmap`-style load. Existing 8-bit encode and decode output is byte-identical to 0.7.4 (pinned by `EightBitPinTests`).

## New

- **Named color spaces and built-in profiles.** `NamedColorSpace` (sRGB, Display P3, Rec. 2020, their linear variants, Rec. 2100 PQ and HLG), `ColorProfiles.Get`/`GetBytes` (compact ICC v4 profiles), `ColorSpaceDescription` (primaries, white point, transfer function), `CicpCode` (ITU-T H.273), `IccColorProfile.Describe()`/`Identify()`, and `ImageMetadata.GetColorSpace()`, `GetNamedColorSpace()`, `Cicp`, `IccProfileBytes`, `IsHdr`.
- **`Image.ConvertColorSpace`** to a named space or an ICC profile, in float, vectorized, with `GamutHandling.Clip` or `Preserve` (unclipped values in a float destination). Matrix/TRC profiles convert exactly; a LUT-based RGB profile is accepted as a source (slow path) and rejected as a destination with a descriptive `IccProfileException`. sRGB (5, 250, 128) converts to Display P3 (0.4504, 0.9660, 0.5524).
- **Pixel formats.** `RgbaF16` and the premultiplied `PremultipliedRgba32`, `PremultipliedRgba64`, `PremultipliedRgbaF16` and `PremultipliedRgbaF32` load, convert, resize and save. `Image.ConvertTo(format, PixelConversionOptions)` adds `EncodeSrgb`/`DecodeSrgb`. Float to integer always rounds to nearest, clips and maps NaN to 0.
- **Encoders take any pixel format** (PNG, JPEG, WebP, AVIF, JPEG XL) and pick the precision the data needs: 16-bit PNG and 10-bit AVIF for 16-bit and float input; linear float keeps its linear profile in PNG, JPEG XL and 10-bit AVIF. `EncoderOptions.Precision`, `TargetColorSpace` and `TargetColorProfile` convert and embed in one pass. Premultiplied sources are unpremultiplied in that same pass. `QualityFraction` (0 to 1) on the JPEG, WebP, AVIF and JPEG XL options maps like `canvas.toBlob`.
- **PNG `cICP`** is read and written. AVIF `nclx` is read, written and signalled in the AV1 sequence header; JPEG XL color encodings are read as `Cicp` and written for the named spaces.
- **AVIF 10- and 12-bit.** Decode accepts 12-bit; the encoder writes 10- and 12-bit, lossy and lossless, with palette mode and IntraBC for screen content (`AvifEncoderOptions.BitDepth`). Lossless streams decode to identical samples in libdav1d; the real 12-bit corpus file decodes like libdav1d.
- **JPEG XL.** Lossless `RgbaF16` is stored as true 16-bit floats. Lossless files declare the named spaces with enums (no profile bytes). Lossy 16-bit and float images with a profile are converted to sRGB instead of having the profile dropped; linear sRGB float is declared linear and decodes to linear light.
- **HDR.** PQ and HLG (BT.2100 system gamma) decode into float with the headroom kept (1.0 is 203 cd/m2); `HdrToneMapping.ToSdr` applies extended Reinhard on luminance.
- **Spans.** `ImageEncoder.Encode` (strided, premultiplied; PNG, JPEG, WebP, AVIF and JPEG XL are produced straight from the rows with no intermediate copy, and a tightly packed array already in the codec format is encoded in place), `Image.FromPixels`, `Image.Wrap`, `Image.CopyPixelsTo`, `Image.Crop(ImageRegion)`.
- **`Image.LoadBitmap(BitmapRequest)`**: orientation, crop, color space conversion, resize (pixelated, low, medium, high) and premultiplication in one call, resampling in the image's own sample type.
- **`ImageInfo`** reports `ColorSpace`, `Cicp`, `BitsPerSample`, `IsHighBitDepth` and `IsHdr` from the container, without decoding.
- A public API snapshot test (`PublicApi.approved.txt`) now guards the exported surface.

## Behavior notes

- `Image.ConvertToSrgb` no longer leaves the old ICC profile (or cICP, gAMA, cHRM, sRGB) on the Rgba32 it returns: those described the pixels it replaced, so saving the result embedded a wrong profile. A file with none means sRGB. Calls that return the image unchanged keep its metadata.
- Lossy JPEG XL keeps Display P3, Rec. 2020, PQ and HLG (and linear variants): the colors go to XYB as sRGB-primaries light, outside sRGB included, and the header declares the space (with an intensity target of 10,000 cd/m2 for PQ and 1,000 for HLG); libjxl decodes those files to the same pixels. A profile that is not one of those is converted to sRGB; 0.7.4 dropped it and mislabeled the pixels. Gray8/Rgb24 with a profile are unchanged.
- Decoding an AVIF with an `nclx` box, or a PNG with `cICP`, now adds a `Cicp` entry to `ImageMetadata.Profiles`.
- PNG, JPEG, WebP, AVIF and JPEG XL no longer throw on 16-bit, float or premultiplied input; BMP, GIF and the other codecs still do.
- HLG and PQ cannot be described by an ICC profile; `ColorProfiles.Get` throws `NotSupportedException` for them and they are signalled with `CicpCode`.
- Vector kernels use `Vector<T>` (and portable `Vector128`/`Vector256` shuffles for the interleaved matrix and row transposes), so the same code runs 256-bit on AVX2 and 128-bit on ARM NEON; run the color tests with `DOTNET_EnableAVX2=0` to exercise the 128-bit width.
- **PNG.** The encoder no longer writes an `sRGB` chunk next to an `iCCP` profile (the specification asks for at most one); with `IncludeIccProfile = false` the `sRGB` chunk stays. Decoding 8-bit gray, RGB or RGBA with `PngDecoderOptions.ScreenGamma` set now applies the documented gamma correction: the 8-bit fast path used to skip it, so only 16-bit and palette images were corrected.
- **Decoding straight to the new pixel formats.** `DecoderOptions.TargetPixelFormat` accepts `RgbaF16` and the premultiplied formats for every codec (the decoded image is converted after decode; a half-float JPEG XL comes back as the exact halves).
- **Resizing** `RgbaF16` and the premultiplied formats runs on their own samples: a premultiplied source is convolved as it is (no unpremultiply and premultiply round trip), a half-float one is premultiplied once for the convolution like any straight-alpha format, and every resize mode (including `Pad` and `Crop`) works for them.
- **Gray JPEG XL** declares its transfer function (sRGB, linear, Rec. 709, PQ, HLG), lossless or lossy, and decodes with it as `Cicp` (primaries unspecified). Lossy gray images in linear, PQ or HLG keep their light the way color images do.
