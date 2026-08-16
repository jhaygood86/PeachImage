# PeachImage

Pure .NET image format readers and writers for commonly used image formats on the web.

Targets .NET 10. No native interop — every codec is managed code, using modern .NET APIs
(`System.Runtime.Intrinsics`, `Span<T>`/`ReadOnlySpan<T>`) for performance instead of P/Invoke.

## Status

- **JPEG**: decode (baseline sequential + progressive, grayscale/YCbCr/RGB/CMYK/YCCK, all standard
  chroma subsampling, restart markers) and encode (baseline sequential, grayscale/YCbCr) are implemented.
  SIMD-accelerated IDCT/FDCT and color conversion kernels are in place
  (`System.Runtime.Intrinsics.Vector128`/`Vector256`, dispatched at runtime by hardware support).
- **BMP**: decode (OS/2 1.x/2.x and Windows BITMAPINFOHEADER through BITMAPV5HEADER variants, 1/4/8bpp
  indexed color, 16/24/32bpp direct color, RLE4/RLE8 compression, arbitrary BI_BITFIELDS/BI_ALPHABITFIELDS
  masks) and encode (24bpp truecolor, 8bpp indexed grayscale with optional RLE8, 32bpp with an explicit
  alpha channel via BITMAPV4HEADER + BI_BITFIELDS) are implemented, including explicit alpha-channel
  support on both sides.
- **PNG**: decode and encode for all 5 color types (grayscale, truecolor, palette, grayscale+alpha,
  truecolor+alpha) at every valid bit depth (1/2/4/8/16 — including via new `Gray16`/`Rgb48`/`Rgba64`
  pixel formats), Adam7 interlacing, palette + `tRNS` transparency (both per-entry and single-color-key),
  optional opt-in gamma correction (`PngDecoderOptions.ScreenGamma`, mirroring libpng's
  `png_set_gamma`), and the common ancillary chunks (`gAMA`/`cHRM`/`sRGB`/`iCCP`/`pHYs`/`tEXt`/`zTXt`/`iTXt`/`tIME`/`bKGD`).
  Encoding doesn't yet build an indexed palette from an arbitrary truecolor source (no automatic
  quantization) — non-palette sources always encode as grayscale/truecolor(+alpha).
- **GIF**: decode (GIF87a/GIF89a, interlacing, transparency, multi-frame animation with per-frame
  disposal methods and the NETSCAPE2.0 loop count via `AnimatedImage.Load`) and encode
  (median-cut palette quantization, optional Floyd-Steinberg dithering, animation) are implemented.
- **WebP**: decode is implemented for both of WebP's bitstream codecs — VP8 (lossy) and VP8L
  (lossless) — including alpha (`ALPH` chunk / VP8L's own alpha) in the RIFF "simple" and "extended"
  (non-animated) container formats. Animated WebP and encode are not yet implemented. WebP decode is
  the furthest of any format here from the 10%-of-SkiaSharp target on large images (see
  `LIBRARY_COMPARISON.md`), though a profile-guided pass has closed roughly a third of that gap; what
  remains is concentrated in entropy decode, which is inherently sequential.
- **AVIF**: decode is implemented for baseline still images — intra-frame AV1 (partition tree, mode
  info, coefficient decode, dequantization, inverse transforms, intra prediction including CFL), the
  full in-loop filter chain (deblocking, CDEF, loop restoration including both Wiener and self-guided
  SGRPROJ), HEIF `grid` composite images, alpha via the auxiliary-item mechanism, and both 8-bit and
  10-bit depth (proportionally scaled to `Rgb48`/`Rgba64`/`Gray16`, not left-shift replication).
  Animated AVIF, film grain synthesis, gain maps, 12-bit depth, and palette/IntraBC mode remain
  unimplemented and throw a clear `AvifUnsupportedFeatureException` rather than a silently wrong
  result. Performance is an explicitly aspirational, longer-term goal here (see
  `LIBRARY_COMPARISON.md`) rather than a merge gate — only the YUV→RGB color conversion is vectorized
  so far (`Vector128<double>`), with entropy decode, the transforms, intra prediction, and the in-loop
  filters still scalar. Targeted correctness oracle is `ffmpeg` (test-only, never a shipped dependency)
  rather than SkiaSharp, whose AVIF decode support is inconsistent across builds — confirmed absent in
  this repo's pinned SkiaSharp version; correctness is instead verified via the AV1 spec's own
  bitstream-conformance requirement checked across a real `libavif` test corpus, plus a pixel-hash
  regression baseline that locks in known-good output before any future optimization pass.
- Other formats are not yet implemented. The public API (`Image`, `AnimatedImage` for multi-frame formats
  like GIF) is designed to support them without breaking changes when they're added. Codec selection is
  internal — there's no format-specific type or registration step in the public API.

## Usage

```csharp
using PeachImage;
using PeachImage.Formats.Jpeg;

// The format is auto-detected from the file's contents — no setup call needed.
using var image = Image.Load("photo.jpg");

using var output = File.Create("resaved.jpg");
image.Save(output, "jpeg", new JpegEncoderOptions { Quality = 85 });
```

Multi-frame formats (GIF today) use `AnimatedImage` instead, the same way across every format that
supports it:

```csharp
using PeachImage;
using PeachImage.Formats.Gif;

using var animation = AnimatedImage.Load("clip.gif");

using var output = File.Create("resaved.gif");
animation.Save(output, "gif", new GifEncoderOptions { MaxColors = 128 });
```

## Building & testing

```bash
dotnet build PeachImage.slnx
dotnet test PeachImage.slnx
```

The first `dotnet test` run automatically fetches JPEG, BMP, and PNG test corpora (the Imazen `codec-corpus`
conformance sets, image-rs/jpeg-decoder's test assets, and — for BMP — the `bmp-conformance` subset of
`codec-corpus`, itself generated from Jason Summers' [bmpsuite](https://github.com/jsummers/bmpsuite); for
PNG — the `pngsuite` subset of `codec-corpus`, a mirror of Willem van Schaik's classic PngSuite conformance
set) into the gitignored `tests/corpus/` directory — no separate script needed. Set
`PEACHIMAGE_SKIP_CORPUS_FETCH=1` to skip network access; corpus-driven tests report as skipped rather than
failing.

## Benchmarking

```bash
dotnet run -c Release --project bench/PeachImage.Benchmarks
```

Compares PeachImage's JPEG/BMP/PNG decode and encode throughput against SkiaSharp (a dev-only dependency
of the benchmark project only — never referenced by the shipped library), a single consistent baseline
across all three formats. BMP encode has no real-world baseline here — SkiaSharp's encoder doesn't support
BMP output — so `BmpEncodeBenchmarks` tracks PeachImage's own throughput only. See
[LIBRARY_COMPARISON.md](LIBRARY_COMPARISON.md) for a summary of the latest results.

## License

MIT — see [LICENSE](LICENSE). One algorithm's numerical structure (the AAN fast DCT/IDCT butterfly wiring)
was referenced from libjpeg-turbo during implementation; see [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
