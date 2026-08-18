# PeachImage

Pure .NET image format readers and writers for commonly used image formats on the web.

Targets .NET 8.0 and .NET 10.0. No native interop — every codec is managed code, using modern .NET APIs
(`System.Runtime.Intrinsics`, `Span<T>`/`ReadOnlySpan<T>`) for performance instead of P/Invoke.

## Status

- **JPEG**: decode (baseline sequential + progressive, grayscale/YCbCr/RGB/CMYK/YCCK, all standard
  chroma subsampling, restart markers) and encode (baseline sequential, grayscale/YCbCr) are implemented.
- **BMP**: decode (OS/2 1.x/2.x and Windows BITMAPINFOHEADER through BITMAPV5HEADER variants, 1/4/8bpp
  indexed color, 16/24/32bpp direct color, RLE4/RLE8 compression, arbitrary BI_BITFIELDS/BI_ALPHABITFIELDS
  masks) and encode (24bpp truecolor, 8bpp indexed grayscale with optional RLE8, 32bpp with an explicit
  alpha channel via BITMAPV4HEADER + BI_BITFIELDS) are implemented, including explicit alpha-channel
  support on both sides.
- **PNG**: decode and encode for all 5 color types (grayscale, truecolor, palette, grayscale+alpha,
  truecolor+alpha) at every valid bit depth (1/2/4/8/16 — including via `Gray16`/`Rgb48`/`Rgba64`
  pixel formats), Adam7 interlacing, palette + `tRNS` transparency (both per-entry and single-color-key),
  optional opt-in gamma correction (`PngDecoderOptions.ScreenGamma`), and the common ancillary chunks
  (`gAMA`/`cHRM`/`sRGB`/`iCCP`/`pHYs`/`tEXt`/`zTXt`/`iTXt`/`tIME`/`bKGD`). Encoding doesn't yet build an
  indexed palette from an arbitrary truecolor source — non-palette sources always encode as
  grayscale/truecolor(+alpha).
- **GIF**: decode (GIF87a/GIF89a, interlacing, transparency, multi-frame animation with per-frame
  disposal methods and the NETSCAPE2.0 loop count via `AnimatedImage.Load`) and encode
  (median-cut palette quantization, optional Floyd-Steinberg dithering, animation) are implemented.
- **WebP**: decode is implemented for both of WebP's bitstream codecs — VP8 (lossy) and VP8L
  (lossless) — including alpha (`ALPH` chunk / VP8L's own alpha) in the RIFF "simple" and "extended"
  (non-animated) container formats. Encode currently produces the lossless (VP8L) bitstream only —
  predictor-transform selection, palette/color-indexing detection, subtract-green, and a color cache
  are all supported. Animated WebP and VP8 (lossy) encode are not yet implemented.
- **AVIF**: decode is implemented for baseline still images — intra-frame AV1, the full in-loop filter
  chain (deblocking, CDEF, loop restoration), HEIF `grid` composite images, alpha via the auxiliary-item
  mechanism, and both 8-bit and 10-bit depth. Animated AVIF, film grain synthesis, gain maps, 12-bit
  depth, and palette/IntraBC mode remain unimplemented and throw a clear
  `AvifUnsupportedFeatureException` rather than a silently wrong result. Encode is implemented for lossy,
  8-bit, 4:2:0, opaque still images only (a single `av01` item; no HEIF `grid`/`avis` animation on the
  output side even though decode supports reading them, and no partition-tree size search yet — every
  block is a fixed 8x8 with a real intra-mode decision among DC/vertical/horizontal/smooth/Paeth
  candidates). Alpha-bearing sources and higher bit depths are rejected with a clear exception rather than
  silently dropped or downsampled.
- Other formats are not yet implemented. The public API (`Image`, `AnimatedImage` for multi-frame formats
  like GIF) is designed to support them without breaking changes when they're added. Codec selection is
  internal — there's no format-specific type or registration step in the public API.

See [LIBRARY_COMPARISON.md](LIBRARY_COMPARISON.md) for performance numbers against SkiaSharp.

## Installing PeachImage

Install the PeachImage package from nuget.org

```
dotnet add package PeachImage
```

## Usage

### Single-frame images

The format is auto-detected from the file's contents for every operation below — no setup call needed.

```csharp
using PeachImage;
using PeachImage.Formats.Jpeg;

// Load, inspect, and convert between formats.
var image = Image.Load("photo.webp");
Console.WriteLine($"{image.Width}x{image.Height} {image.PixelFormat}");

using var output = File.Create("resaved.jpg");
image.Save(output, "jpeg", new JpegEncoderOptions { Quality = 85 });
```

```csharp
using PeachImage;

// Read dimensions/format without decoding pixel data.
using var stream = File.OpenRead("photo.avif");
ImageInfo info = Image.Identify(stream);
Console.WriteLine($"{info.Width}x{info.Height} {info.PixelFormat} ({info.FormatName})");
```

```csharp
using PeachImage;

// Zero-copy access to the decoded pixel buffer.
var image = Image.Load("photo.png");
Span<byte> pixels = image.GetPixelSpan();
Span<byte> firstRow = image.GetRowSpan(0);
```

### Animated images

Multi-frame formats (GIF today) use `AnimatedImage` instead, with the same load/save shape:

```csharp
using PeachImage;
using PeachImage.Formats.Gif;

var animation = AnimatedImage.Load("clip.gif");

foreach (AnimatedImageFrame frame in animation.Frames)
{
    Console.WriteLine($"{frame.Duration.TotalMilliseconds}ms, disposal={frame.Disposal}");
}

using var output = File.Create("resaved.gif");
animation.Save(output, "gif", new GifEncoderOptions { MaxColors = 128, Dither = true });
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

Compares PeachImage's decode/encode throughput against SkiaSharp (a dev-only dependency of the
benchmark project only — never referenced by the shipped library). See
[LIBRARY_COMPARISON.md](LIBRARY_COMPARISON.md) for the latest results.

## License

MIT — see [LICENSE](LICENSE). One algorithm's numerical structure (the AAN fast DCT/IDCT butterfly wiring)
was referenced from libjpeg-turbo during implementation; see [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
