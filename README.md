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
- Other formats (PNG, WebP, GIF, ...) are not yet implemented. The public API
  (`Image`, `IImageDecoder`/`IImageEncoder`, `ImageFormatManager`) is designed to support them without
  breaking changes when they're added.

## Usage

```csharp
using PeachImage;
using PeachImage.Formats.Jpeg;

// Codecs are registered automatically — no setup call needed.
using var image = Image.Load("photo.jpg");

using var output = File.Create("resaved.jpg");
image.Save(output, "jpeg", new JpegEncoderOptions { Quality = 85 });
```

## Building & testing

```bash
dotnet build PeachImage.slnx
dotnet test PeachImage.slnx
```

The first `dotnet test` run automatically fetches JPEG and BMP test corpora (the Imazen `codec-corpus`
conformance sets, image-rs/jpeg-decoder's test assets, and — for BMP — the `bmp-conformance` subset of
`codec-corpus`, itself generated from Jason Summers' [bmpsuite](https://github.com/jsummers/bmpsuite)) into
the gitignored `tests/corpus/` directory — no separate script needed. Set `PEACHIMAGE_SKIP_CORPUS_FETCH=1` to
skip network access; corpus-driven tests report as skipped rather than failing.

## Benchmarking

```bash
dotnet run -c Release --project bench/PeachImage.Benchmarks
```

Compares PeachImage's JPEG decode/encode throughput against real libjpeg-turbo (via
`Quamotion.TurboJpegWrapper`, a dev-only dependency of the benchmark project only — never referenced by
the shipped library) and PeachImage's BMP decode throughput against SkiaSharp (also a dev-only dependency).
BMP encode has no real-world baseline here — SkiaSharp's encoder doesn't support BMP output — so
`BmpEncodeBenchmarks` tracks PeachImage's own throughput only.

## License

MIT — see [LICENSE](LICENSE). One algorithm's numerical structure (the AAN fast DCT/IDCT butterfly wiring)
was referenced from libjpeg-turbo during implementation; see [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
