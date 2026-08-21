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
  (`gAMA`/`cHRM`/`sRGB`/`iCCP`/`pHYs`/`tEXt`/`zTXt`/`iTXt`/`tIME`/`bKGD`). Encoding can build an indexed
  palette automatically (`PngEncoderOptions.ColorMode`, default `Auto`): lossless whenever the source has
  at most `MaxColors` (default 256) distinct opaque colors and binary alpha, otherwise falling back to
  grayscale/truecolor(+alpha) unless `ColorMode = Indexed` forces palette output via median-cut
  quantization with optional Floyd-Steinberg dithering (`Dither`) — the same quantizer GIF encoding uses.
- **GIF**: decode (GIF87a/GIF89a, interlacing, transparency, multi-frame animation with per-frame
  disposal methods and the NETSCAPE2.0 loop count via `AnimatedImage.Load`) and encode
  (median-cut palette quantization, optional Floyd-Steinberg dithering, animation) are implemented.
- **WebP**: decode is implemented for both of WebP's bitstream codecs — VP8 (lossy) and VP8L
  (lossless) — including alpha (`ALPH` chunk / VP8L's own alpha) and animation (via `AnimatedImage.Load`,
  including the loop count) in the RIFF "simple" and "extended" container formats. Encode supports both
  bitstreams: lossless (VP8L, the default) with predictor-transform selection, palette/color-indexing
  detection, subtract-green, and a color cache; and lossy (VP8, opt in via `WebpEncoderOptions { Lossless
  = false }`) with quality-driven quantization. Alpha-bearing sources always encode as VP8L regardless of
  `Lossless`, since lossy WebP's alpha channel isn't implemented yet. Animated WebP encode is not yet
  implemented (decode-only for animation).
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

Bytes already in memory (e.g. a buffered upload) load directly — no need to wrap them in a `MemoryStream`
first; a `byte[]` converts implicitly to `ReadOnlySpan<byte>`, and decoding reads straight out of that
memory with no intermediate copy:

```csharp
using PeachImage;

byte[] uploadedBytes = await ReadUploadIntoMemoryAsync();
var image = Image.Load(uploadedBytes);
```

`SaveAsync` exists for async I/O call paths. Encoding itself is CPU-bound, not I/O-bound, so only the
actual stream/file write is awaited — same as `LoadAsync` otherwise:

```csharp
using PeachImage;

using var output = File.Create("resaved.jpg");
await image.SaveAsync(output, "jpeg", new JpegEncoderOptions { Quality = 85 });
```

### Animated images

Multi-frame formats (GIF, and WebP for decode — animated WebP encode isn't implemented yet) use
`AnimatedImage` instead, with the same load/save shape:

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

### Resizing

`Image.Resize`/`AnimatedImage.Resize` support 15 resampling filters via `ResamplingFilter` — `Bicubic`
is the default; also available: `Box`, `CatmullRom`, `Hermite`, `Lanczos2`/`Lanczos3`/`Lanczos5`/`Lanczos8`,
`MitchellNetravali`, `NearestNeighbor`, `Robidoux`, `RobidouxSharp`, `Spline`, `Bilinear`, and `Welch`.

```csharp
using PeachImage;

var image = Image.Load("photo.jpg");

// Bicubic by default.
var thumbnail = image.Resize(200, 150);

// Or pick a specific filter.
var sharpened = image.Resize(200, 150, new ResizeOptions { Filter = ResamplingFilter.Lanczos3 });
```

`AnimatedImage.Resize` resizes every frame — lazily, as `Frames` is enumerated — preserving each frame's
duration and disposal method:

```csharp
using PeachImage;

var animation = AnimatedImage.Load("clip.gif");
var resized = animation.Resize(160, 120, new ResizeOptions { Filter = ResamplingFilter.MitchellNetravali });

using var output = File.Create("resized.gif");
resized.Save(output, "gif");
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
