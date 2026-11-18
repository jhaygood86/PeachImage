# PeachImage vs. SkiaSharp and reference codecs

Performance comparison of PeachImage against [SkiaSharp](https://github.com/mono/SkiaSharp) 4.154.0 (a
mature, real-world, native-backed image library) for JPEG, BMP, GIF, PNG, and WebP decode/encode
throughput, plus `Image.Resize`. SkiaSharp is used as the baseline for every format it supports — it's also
the corpus tests' differential oracle (see the [README](README.md)). For formats SkiaSharp has no codec for
(JPEG XL, JPEG 2000, TIFF, CCITT fax, AVIF), the baseline is the reference C/C++ library called through a
`LibraryImport` shim in `bench/PeachImage.Benchmarks/Native/`: libjxl, OpenJPEG, libtiff, libavif (dav1d
decoder, libyuv colour conversion). Those native libraries are built with vcpkg (`x64-windows`, dynamic) and
located through `PEACH_NATIVE_DIR`; run `native-smoke` (see below) to check they load and decode the same
workload as PeachImage.

**Target**: PeachImage's Mean within 10% of the baseline's Mean (`Ratio` column ≤ ~1.10) for every
scenario. Ratios below 1.00 mean PeachImage is *faster* than the baseline.

## How to reproduce

```bash
dotnet run -c Release --project bench/PeachImage.Benchmarks -- --filter "*" --job short
```

If that fails with `Found more than one matching project file for PeachImage.Benchmarks`, it's
because this repo accumulates per-session `.claude/worktrees/*/bench/PeachImage.Benchmarks.csproj`
copies that shadow the real one by name, confusing BenchmarkDotNet's default out-of-process
toolchain. Add `--inProcess` to run in the host process instead (sidesteps the per-job project
generation entirely) and `--affinity <mask>` to pin to a couple of P-core threads — see the
pinning note below. The numbers in this document were collected with:

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*JpegDecodeBenchmarks*" "*JpegEncodeBenchmarks*" "*BmpDecodeBenchmarks*" "*BmpEncodeBenchmarks*" "*PngDecodeBenchmarks*" "*PngEncodeBenchmarks*" "*WebpDecodeBenchmarks*" "*WebpEncodeBenchmarks*" "*GifEncodeBenchmarks*" "*JxlDecodeBenchmarks*" "*JxlEncodeBenchmarks*" "*AvifDecodeBenchmarks*" "*GifDecodeBenchmarks*" "*TiffDecodeBenchmarks*" "*Jpeg2000DecodeBenchmarks*" "*Jbig2DecodeBenchmarks*" "*CcittDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

The GIF section and the WebP/GIF "Animated" rows were collected separately (same methodology, same
session, same machine) with a filter scoped to just the `Animated-MultiFrame` category:

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*AnimatedAllFrames*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

The Resize section's numbers were collected separately, with a reduced iteration count (3 warmup + 8
iterations rather than the 20-iteration job above) given how many scenarios `ResizeBenchmarks.cs`
covers — treat them with correspondingly more skepticism than the rest of this document per the noise
floor note below:

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*ResizeBenchmarks*" --warmupCount 3 --iterationCount 8 --inProcess --affinity 15
```

**Environment**: BenchmarkDotNet v0.15.8, Windows 11, Intel Core i9-14900K (24 physical / 32 logical
cores), .NET 10.0.15 and .NET 11.0.0 (both built with SDK 11.0.100), X64 RyuJIT x86-64-v3, AVX2-capable.
PeachImage targets both runtimes (see [README](README.md)); every suite above was run once per runtime, back to
back on the same build, with `-f net11.0` and then `-f net10.0`. The PeachImage columns give each runtime's
Mean; the baseline column is the .NET 11 run's Mean (the native and SkiaSharp code is the same in both runs,
and differed between them by no more than the noise floor below), and each Ratio is PeachImage against the
baseline measured in the same run.

SkiaSharp 4.154.0. Reference codecs (vcpkg, MSVC, x64-windows dynamic): libjxl 0.12.0, OpenJPEG 2.5.4,
libtiff 4.7.2, libavif 1.4.2 with dav1d 1.5.4 and libyuv. The native baselines run on the same thread count
PeachImage does (libjxl and OpenJPEG are given `Environment.ProcessorCount` threads, libavif is single-threaded
like PeachImage's AV1 decode). Native decoders write into their own native layouts (see each section's notes),
so a few rows compare slightly different output work; those are called out where they apply.

`dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- native-smoke` decodes each native-baseline
asset once through PeachImage and the reference library and prints the dimensions side by side, to confirm both
sides are timing the same workload.

**Noise floor**: even pinned, this machine shows several-percent between-run drift — a same-session,
back-to-back before/after comparison against unchanged code has swung several percent in both
directions with zero code changes. `--job short`'s 3 warmup + 3 iterations can't resolve deltas under
that; the 20-iteration job above can, but treat any single-digit-percent difference between two
separate runs (as opposed to two scenarios measured in the *same* run) with corresponding skepticism.

## JPEG

Both libraries decode/encode from the same source files; JPEG encode uses explicit 4:2:0/4:4:4 chroma
subsampling on both sides (`JpegEncoderOptions.Subsampling` / `SKJpegEncoderOptions.Downsample`) at
quality 85.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 |
|---|---:|---:|---:|---:|---:|
| 1080p, 4:2:0 | 5.17 ms | 4.40 ms | 9.54 ms | **0.55×** | **0.46×** |
| 1080p, 4:4:4 | 5.90 ms | 5.34 ms | 11.49 ms | **0.52×** | **0.47×** |
| 1080p, Grayscale | 3.93 ms | 3.62 ms | 5.90 ms | **0.67×** | **0.61×** |
| 12MP, 4:2:0 | 29.66 ms | 26.12 ms | 55.46 ms | **0.54×** | **0.47×** |

### Encode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 |
|---|---:|---:|---:|---:|---:|
| 1080p, 4:2:0 | 26.41 ms | 26.03 ms | 19.59 ms | 1.36× | 1.33× |
| 1080p, 4:4:4 | 32.18 ms | 34.25 ms | 28.02 ms | 1.17× | 1.22× |

## BMP

SkiaSharp's encoder doesn't support BMP output, and there is no maintained reference C library to
substitute, so encode has no baseline.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 |
|---|---:|---:|---:|---:|---:|
| 24bpp Truecolor | 2.27 ms | 2.12 ms | 2.14 ms | 1.03× | **0.99×** |
| 32bpp Alpha | 3.22 ms | 3.15 ms | 7.86 ms | **0.42×** | **0.40×** |
| 8bpp Indexed | 2.22 ms | 2.16 ms | 2.08 ms | 1.08× | 1.04× |
| 8bpp Indexed, RLE | 5.53 ms | 5.54 ms | 8.05 ms | **0.71×** | **0.69×** |

### Encode (PeachImage only — no baseline)

| Scenario | .NET 10 | .NET 11 |
|---|---:|---:|
| 24bpp Truecolor | 3.34 ms | 3.23 ms |
| 32bpp Alpha | 4.34 ms | 4.33 ms |
| 8bpp Indexed | 398 µs | 439 µs |
| 8bpp Indexed, RLE | 3.42 ms | 3.57 ms |

## PNG

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 |
|---|---:|---:|---:|---:|---:|
| 24bpp Truecolor | 16.96 ms | 16.81 ms | 16.67 ms | 1.03× | 1.01× |
| 32bpp RGBA | 22.66 ms | 22.09 ms | 21.23 ms | 1.07× | 1.04× |
| 48bpp (16-bit) Truecolor | 4.31 ms | 4.34 ms | 3.38 ms | 1.29× | 1.29× |
| 8bpp Grayscale | 8.39 ms | 8.29 ms | 9.21 ms | **0.91×** | **0.90×** |
| Interlaced (Adam7) Truecolor | 25.60 ms | 24.64 ms | 26.63 ms | **0.98×** | **0.93×** |

### Encode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 |
|---|---:|---:|---:|---:|---:|
| 24bpp Truecolor | 76.16 ms | 77.36 ms | 142.9 ms | **0.54×** | **0.54×** |
| 32bpp RGBA | 98.91 ms | 101.7 ms | 220.5 ms | **0.45×** | **0.46×** |
| 8bpp Grayscale | 29.29 ms | 30.43 ms | 41.66 ms | **0.71×** | **0.73×** |
| Low-color graphic (16 colors), indexed | 5.88 ms | 5.93 ms | 4.03 ms | 1.46× | 1.47× |

Indexed-color (PLTE) encoding is not held to the 10% throughput target the other rows track — its
point is file size, not speed. On the low-color scenario above (640×480, 16 distinct colors),
`PngEncoderOptions.ColorMode`'s default `Auto` mode produces a **1,187-byte** file, vs. **2,243
bytes** for truecolor (`ColorMode = Truecolor`) and **2,229 bytes** from SkiaSharp's `SKBitmap.Encode`
(SkiaSharp doesn't itself select indexed color for this source), so the file-size comparison is
PeachImage-only.

## GIF

Decode covers a low-color graphic and a dithered photographic still, plus an animated scenario
(all 24 frames of a 320×240 animation, decoded through both libraries — PeachImage via
`AnimatedImage.Load`, SkiaSharp via frame-indexed `SKCodec` decode — not just the first frame,
since that's GIF's defining use case). Encode has no SkiaSharp baseline: `SKPixmap.Encode` does
not support GIF output (it returns null), so encode throughput is tracked for PeachImage alone.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Low-color graphic, single frame (640×480) | 947 µs | 961 µs | 261 µs | 3.72× | 3.68× | 1.0 MB |
| Photographic dithered, single frame (1920×1080) | 11.53 ms | 11.07 ms | 5.23 ms | 2.26× | 2.12× | 8.0 MB |
| Animated, all frames (24×, 320×240) | 4.23 ms | 3.95 ms | 538 µs | 8.04× | 7.34× | 697 KB |

This is the largest gap measured against SkiaSharp in this document.

### Encode (PeachImage only — no baseline)

| Scenario | .NET 10 | .NET 11 |
|---|---:|---:|
| Low-color graphic (640×480) | 7.36 ms | 7.41 ms |
| Photographic dithered (1920×1080) | 111.7 ms | 113.6 ms |
| Photographic, no dither (1920×1080) | 96.77 ms | 98.79 ms |
| Animated, all frames (24×, 320×240) | 14.72 ms | 14.42 ms |

## WebP

Decode covers both of WebP's bitstream codecs (VP8 lossy, VP8L lossless), with and without alpha,
a small-image scenario, and an animated scenario (all 24 frames of the same source content/dimensions
as GIF's animated scenario above, transcoded to WebP via ffmpeg's `libwebp_anim` muxer, so the two are
directly comparable). Encode covers both the lossless (VP8L, default) and lossy (VP8,
`WebpEncoderOptions.Lossless = false`) bitstreams; animated encoding is not implemented (decode-only).
SkiaSharp's WebP codec is libwebp itself, so it also serves as the reference-codec baseline here.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Lossless, Photographic | 17.73 ms | 17.36 ms | 23.27 ms | **0.77×** | **0.75×** | 8.0 MB |
| Lossy, Photographic | 15.35 ms | 15.10 ms | 13.76 ms | 1.12× | 1.10× | 8.0 MB |
| Lossless, Graphic (flat color) | 471 µs | 461 µs | 328 µs | 1.45× | 1.41× | 1.0 MB |
| Lossy, Alpha | 15.87 ms | 15.78 ms | 18.71 ms | **0.85×** | **0.84×** | 8.0 MB |
| Lossless, Alpha | 21.68 ms | 21.33 ms | 24.17 ms | **0.90×** | **0.88×** | 8.0 MB |
| Small image (32×24) | 10.86 µs | 10.88 µs | 12.00 µs | **0.92×** | **0.91×** | 6.1 KB |
| Animated, all frames (24×, 320×240) | 723 µs | 749 µs | 4.07 ms | **0.18×** | **0.18×** | 349 KB |

The animated scenario is one of the strongest results in this document: PeachImage decodes it roughly
**5.5× faster** than SkiaSharp. SkiaSharp pays fixed per-call native marshaling overhead on every one of the 24
`GetPixels` calls, while PeachImage's decode is pure managed code with lower fixed per-frame cost
(`WebpFrameCompositor`/`GifFrameCompositor` reuse one persistent canvas in place rather than
allocating a fresh one per frame). GIF's animated decode remains far slower in relative terms (see
the GIF section above) because its own LZW decode loop, not frame compositing, dominates its
per-frame cost.

### Encode (lossless)

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Photographic | 226.6 ms | 202.1 ms | 216.6 ms | 1.05× | **0.93×** | 14.3 MB |
| Graphic (flat color, palette) | 2.14 ms | 2.01 ms | 1.43 ms | 1.50× | 1.40× | 650 KB |
| Alpha | 226.0 ms | 209.9 ms | 237.5 ms | **0.95×** | **0.88×** | 9.5 MB |
| Small image (32×24) | 35.46 µs | 34.67 µs | 216 µs | **0.17×** | **0.16×** | 77 KB |

Photographic and alpha are at or slightly ahead of parity with SkiaSharp (both are dominated by the same
LZ77-style backward-reference search either way); the small-image case is where SkiaSharp's fixed per-call
native marshaling overhead dominates instead, and PeachImage's pure-managed path is ~6× faster.

### Encode (lossy)

`Vp8ImageEncoder` uses SAD-only mode decision (no rate-distortion search), a linear
quality→quantizer mapping, and no coefficient-probability adaptation, yet already beats SkiaSharp's
libwebp encoder on throughput at the same quality setting:

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Photographic (quality 75) | 31.96 ms | 31.60 ms | 107.6 ms | **0.30×** | **0.29×** | 7.8 MB |

The remaining allocation gap (7.8 MB vs. SkiaSharp's ~2 KB) comes from one-time per-image buffers
(RGB-to-YUV conversion, final bitstream chunk assembly); libwebp's encoder works in unmanaged memory
throughout, so its managed allocation is near zero regardless of image size.

## Resize

`Image.Resize` vs. `SKBitmap.Resize`, for every `ResamplingFilter` that has a same-algorithm SkiaSharp
equivalent — the cubic filters via `SKSamplingOptions(new SKCubicResampler(B, C))` with the exact same
`(B, C)` constants `CubicBcKernel` uses, plus Nearest and Linear. Both directions are measured on the same
1920×1080 photographic source: a 4× downscale (480×270, thumbnail generation) and a 2× upscale (3840×2160).
Box, Lanczos2/3/5/8, and Welch have no SkiaSharp equivalent (Skia's public resize API exposes no
windowed-sinc or box option) and no reference C library with the same kernels, so those are PeachImage-only
rows. This section used the reduced 3-warmup, 8-iteration job shown under "How to reproduce".

### Downscale (1920×1080 → 480×270)

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| NearestNeighbor | 329 µs | 327 µs | 265 µs | 1.24× | 1.23× | 512 KB |
| Bilinear | 2.85 ms | 2.54 ms | 1.13 ms | 2.52× | 2.25× | 569 KB |
| Bicubic | 4.02 ms | 3.76 ms | 3.57 ms | 1.13× | 1.05× | 592 KB |
| MitchellNetravali | 3.96 ms | 3.71 ms | 3.61 ms | 1.10× | 1.03× | 592 KB |
| Hermite | 3.93 ms | 3.66 ms | 3.56 ms | 1.10× | 1.03× | 592 KB |
| Spline | 3.97 ms | 3.70 ms | 3.50 ms | 1.13× | 1.06× | 592 KB |
| Robidoux | 3.97 ms | 3.69 ms | 3.52 ms | 1.13× | 1.05× | 592 KB |
| RobidouxSharp | 3.92 ms | 3.67 ms | 3.50 ms | 1.12× | 1.05× | 592 KB |

### Upscale (1920×1080 → 3840×2160)

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | SkiaSharp | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| NearestNeighbor | 20.76 ms | 20.54 ms | 14.00 ms | 1.51× | 1.47× | 32.0 MB |
| Bilinear | 26.11 ms | 25.21 ms | 67.64 ms | **0.39×** | **0.37×** | 32.2 MB |
| Bicubic | 30.10 ms | 29.19 ms | 222.0 ms | **0.14×** | **0.13×** | 32.2 MB |
| MitchellNetravali | 30.32 ms | 29.41 ms | 222.0 ms | **0.14×** | **0.13×** | 32.2 MB |
| Hermite | 30.23 ms | 29.36 ms | 221.8 ms | **0.14×** | **0.13×** | 32.2 MB |
| Spline | 30.49 ms | 29.19 ms | 221.9 ms | **0.14×** | **0.13×** | 32.2 MB |
| Robidoux | 30.11 ms | 29.07 ms | 222.0 ms | **0.13×** | **0.13×** | 32.2 MB |
| RobidouxSharp | 30.30 ms | 29.22 ms | 221.7 ms | **0.14×** | **0.13×** | 32.2 MB |

Downscale and upscale pull in opposite directions. On downscale, PeachImage widens the filter's radius by
the scale factor before convolving (the standard "scaled filter" anti-aliasing technique — see
`ResamplingWeightMap`), which is real extra work SkiaSharp's default (non-mipmapped) `SKBitmap.Resize` does
not do; the more expensive that widened window gets per destination pixel (cubic family) or the cheaper
SkiaSharp's own base case already is (Bilinear, radius 1 either way), the wider the gap. On upscale, that
widening never triggers (the window is just the kernel's native radius), so PeachImage's throughput for the
cubic family beats SkiaSharp outright — SkiaSharp appears to pay a comparatively larger fixed cost per
output pixel there. NearestNeighbor, doing no windowed convolution either way, stays closest to parity in
both directions.

The convolution passes run through `ResamplingParallel.For`, which caps `ParallelOptions.MaxDegreeOfParallelism` at
`Environment.ProcessorCount` so the `--affinity <mask>` benchmarking convention (see "How to reproduce") doesn't
oversubscribe threads.

### PeachImage only — no baseline

| Scenario | Downscale .NET 10 | Downscale .NET 11 | Upscale .NET 10 | Upscale .NET 11 |
|---|---:|---:|---:|---:|
| Box | 2.38 ms | 2.13 ms | 23.90 ms | 23.42 ms |
| Welch | 2.89 ms | 2.84 ms | 26.29 ms | 25.32 ms |
| Lanczos2 | 4.05 ms | 3.92 ms | 30.17 ms | 29.33 ms |
| Lanczos3 | 5.16 ms | 4.94 ms | 34.73 ms | 33.09 ms |
| Lanczos5 | 7.69 ms | 7.41 ms | 43.80 ms | 40.73 ms |
| Lanczos8 | 11.26 ms | 11.10 ms | 56.12 ms | 53.11 ms |

### SIMD convolver tier (Vector128 vs. Vector256)

Isolated per-pass comparison, bypassing `ResamplingConvolverSelector` and `Image.Resize` entirely, on the same
downscale's horizontal and vertical passes (both parallelized):

| Pass | Vector128 .NET 10 | Vector256 .NET 10 | Ratio | Vector128 .NET 11 | Vector256 .NET 11 | Ratio |
|---|---:|---:|---:|---:|---:|---:|
| Horizontal | 2.24 ms | 2.24 ms | 1.00× | 2.06 ms | 2.06 ms | 1.00× |
| Vertical | 657 µs | 423 µs | **0.64×** | 652 µs | 345 µs | **0.53×** |

Matches the design intent: the vertical pass's genuine 8-lane width is 36% faster on .NET 10 and
47% faster on .NET 11; the horizontal pass (which delegates straight to the Vector128 tier — see
`Vector256ResamplingConvolver`'s remarks) shows no consistent difference, run-to-run noise aside.

## AVIF

Decode baseline: libavif 1.4.2 with the dav1d 1.5.4 AV1 decoder and libyuv YUV-to-RGB conversion, single-threaded,
producing 8-bit RGBA. PeachImage's AV1 decode is multi-threaded: tiles of 256x256 samples or more reconstruct pixels
on a second thread while the first parses, and deblocking, CDEF and YUV-to-RGB run in parallel row bands. SkiaSharp 4.154.0 does not decode AVIF.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | libavif | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Photographic, 8-bit 4:2:0 | 40.09 ms | 38.25 ms | 18.94 ms | 2.16× | 2.02× | 19.3 MB |
| Photographic, 8-bit 4:2:0 + alpha | 45.95 ms | 45.70 ms | 21.86 ms | 2.16× | 2.09× | 19.4 MB |
| Photographic, lossless (4:4:4) | 250.4 ms | 245.3 ms | 133.9 ms | 1.89× | 1.83× | 35.4 MB |
| Photographic, 8-bit 4:2:0 + loop restoration | 61.76 ms | 58.97 ms | 22.97 ms | 2.76× | 2.57× | 20.1 MB |
| Photographic, 10-bit 4:2:0 + loop restoration | 63.64 ms | 58.49 ms | 40.80 ms | 1.60× | 1.43× | 28.0 MB |
| Small image (32×24) | 66.31 µs | 69.74 µs | 13.06 µs | 5.10× | 5.34× | 254 KB |

AV1 decode is the format where PeachImage is furthest from a hand-tuned native decoder: dav1d is SIMD-assembly
throughout. Per-image managed allocation is a PeachImage-only concern (libavif's buffers are unmanaged).

### Encode (time)

Baseline: libavif 1.4.2 with libaom, single-threaded (`maxThreads = 1`, speed 6), from 8-bit RGBA including the RGB-to-YUV
conversion. Lossy is 4:2:0 at quality 75 for both; lossless is 4:4:4 (libavif: quality 100, identity matrix, full range).
The two encoders make different rate/quality trade-offs and produce different output sizes, so these are throughput
comparisons, not equal-output comparisons.

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | libavif | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Photographic 1080p, lossy | 1,725 ms | 1,678 ms | 483.5 ms | 3.46× | 3.47× | 892.7 MB |
| Photographic 1080p, lossless | 28.7 s | 28.5 s | 956.1 ms | 29.86× | 29.80× | 10.9 GB |
| Small image (32×24), lossy | 6.48 ms | 6.28 ms | 719 µs | 9.16× | 8.74× | 1.5 MB |
| Small image (32×24), lossless | 4.83 ms | 4.88 ms | 1.23 ms | 3.94× | 3.95× | 3.4 MB |

### Encode (lossless size)

`AvifEncoderOptions.Lossless = true` compared against this repo's own PNG encoder on the same pixels, plus
`ffmpeg`'s lossless AV1 encode (`-c:v libaom-av1 -crf 0 -still-picture 1`) as external context. Reproduce
with the deterministic 128×128 multi-octave fractal-noise fixture in `LosslessSizeRegressionTests`
(`dotnet test --filter FractalNoiseImage_LosslessAvif_IsSmallerThanSourcePng`):

| Scenario | PeachImage PNG | PeachImage lossless AVIF | `ffmpeg` lossless AVIF (context only) |
|---|---:|---:|---:|
| 128×128 photo-like (fractal noise) | 28,128 bytes | 27,386 bytes | 26,154 bytes |

## TIFF

Decode baseline: libtiff 4.7.2 (`TIFFClientOpen` over memory, `TIFFReadScanline` into one raster; no codec-level threading).
SkiaSharp has no TIFF codec at all.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | libtiff | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| 1080p, Uncompressed | 5.42 ms | 5.22 ms | 647 µs | 7.74× | 8.07× | 39.9 MB |
| 1080p, LZW | 23.39 ms | 22.96 ms | 13.01 ms | 1.81× | 1.76× | 41.7 MB |
| 1080p, PackBits | 5.47 ms | 5.87 ms | 766 µs | 7.71× | 7.67× | 40.0 MB |

Uncompressed and PackBits are close to memory-copy speed for libtiff, which leaves PeachImage's pixel-format reshape and
managed allocation as the whole gap; LZW is dominated by the dictionary decode in both implementations, so the ratio is
much smaller.

## JPEG XL

Baseline: libjxl 0.12.0 through `LibraryImport` (`JxlDecoder*`/`JxlEncoder*`, with libjxl's resizable parallel runner
given the same thread count as PeachImage), 8-bit interleaved output. SkiaSharp has no JPEG XL codec. The decode inputs are
libjxl conformance files (a large lossy VarDCT image with a DC frame, patches, the edge-preserving filter and ICC output; a
lossless Modular image with patches; a lossy image with synthesized noise; a lossless image whose splines dominate).
Collected with the `--affinity 15` methodology above (four logical cores); decode and encode are parallel across groups and
rows in both libraries.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | libjxl | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Lossy 4064x2704 (11 MP) | 270.8 ms | 269.7 ms | 262.6 ms | 1.04× | 1.03× | 64.4 MB |
| Lossless 1600x1096 with patches | 66.70 ms | 74.62 ms | 44.38 ms | 1.53× | 1.68× | 16.9 MB |
| Lossless 2048x2048 with splines | 210.8 ms | 208.6 ms | 154.5 ms | 1.32× | 1.35× | 23.2 MB |
| Lossy 500x606 with noise | 9.54 ms | 9.52 ms | 5.77 ms | 1.67× | 1.65× | 2.7 MB |

The lossless-with-patches row moves between processes more than the noise floor: run as separate benchmark processes, its Mean
ranged 66.6–81.9 ms on .NET 10 and 61.7–79.1 ms on .NET 11, with each process steady within itself. The spread comes from its
parallel Modular group decode; single-threaded (`--affinity 1`) the same decode is steady, at 117–124 ms on .NET 10 and
106–111 ms on .NET 11.

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*JxlDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

### Encode

Photographic 1920x1080 sources, `Quality` 75 (libjxl: `JxlEncoderDistanceFromQuality(75)`). PeachImage runs at its default
`Effort` 5; libjxl is shown at effort 5 for a like-for-like effort number and at effort 7 (its own default).

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | libjxl effort 5 | Ratio .NET 10 | Ratio .NET 11 | libjxl effort 7 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Lossless RGB | 60.68 ms | 58.98 ms | 284.7 ms | **0.21×** | **0.21×** | 798.4 ms | **0.07×** | 111.0 MB |
| Lossless gray | 25.96 ms | 25.50 ms | 84.51 ms | **0.31×** | **0.30×** | — | — | 41.2 MB |
| Lossy RGB | 213.9 ms | 217.6 ms | 170.4 ms | 1.27× | 1.28× | 316.8 ms | **0.69×** | 151.0 MB |
| Lossy gray | 217.8 ms | 219.0 ms | 191.1 ms | 1.17× | 1.15× | — | — | 134.7 MB |
| Lossless RGBA | 78.04 ms | 80.13 ms | — | — | — | — | — | 121.0 MB |

Lossless RGBA has no libjxl row (the shim doesn't set up the extra alpha channel). Effort-for-effort PeachImage's lossless
Modular encode is several times faster than libjxl's at a larger file (see the size table below); the lossy VarDCT encode is
within roughly 15–28% of libjxl at the same effort.

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*JxlEncodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

### Encode (JPEG XL lossy size)

File size in bytes needed to reach a given SSIMULACRA2 score (higher is better quality; about 70 is "good", 80 "high", 88 close to
visually lossless), PeachImage at its default effort against libjxl 0.x through `ffmpeg -c:v libjxl -effort 7`. Each cell is
PeachImage / libjxl, interpolated along a sweep of `Quality` values and libjxl distances; scores were computed with the `ssimulacra2`
Python package on the decoded 8-bit RGB output. Images are libjxl conformance photographs.

| Image | Score 70 | Score 80 | Score 88 |
|---|---:|---:|---:|
| bicycles (1024x631) | 70,540 / 72,514 | 107,995 / 101,526 | 170,658 / 148,057 |
| sunset_logo | 24,476 / 20,526 | 36,250 / 31,348 | 62,877 / 56,105 |
| noise | 106,188 / 122,303 | 138,299 / 154,573 | 191,161 / 201,247 |
| opsin_inverse | 83,915 / 95,675 | 121,251 / 135,382 | 179,922 / 208,674 |
| lz77_flower | 33,734 / 36,834 | 57,489 / 54,216 | not reached |
| Geometric mean of the ratio | 0.96 | 1.01 | 1.01 |

### Encode (JPEG XL lossless size)

Output size of the 1920x1080 lossless encodes against libjxl (`ffmpeg -c:v libjxl -distance 0`) at efforts 1, 3 and 7.

| Source | PeachImage | libjxl effort 1 | libjxl effort 3 | libjxl effort 7 |
|---|---:|---:|---:|---:|
| RGB photo (24bpp PNG source 3,494,915 B) | 2,658,373 B | 2,812,020 B | 2,600,027 B | 2,465,977 B |
| Gray photo (8bpp PNG source 1,230,130 B) | 1,199,781 B | 1,240,850 B | 1,178,235 B | 1,175,269 B |

## JPEG 2000

Baseline: OpenJPEG 2.5.4 (`opj_decode` over a memory stream, `opj_codec_set_threads` at the same thread count). OpenJPEG
produces planar 32-bit components, PeachImage 8-bit interleaved pixels. SkiaSharp has no JPEG 2000 codec. Two 1280x960 RGB
files written by OpenJPEG: lossless (reversible 5/3 wavelet with the reversible colour transform, 64x64 code-blocks, five
decomposition levels) and lossy (irreversible 9/7, 12:1). Collected with the `--affinity 15` methodology above.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | OpenJPEG | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Lossless 1280x960 | 93.75 ms | 82.90 ms | 49.35 ms | 1.96× | 1.68× | 4.5 MB |
| Lossless 1280x960, half size (`ReduceResolutionLevels = 1`) | 30.58 ms | 25.00 ms | 17.67 ms | 1.84× | 1.41× | 1.4 MB |
| Lossy 1280x960 | 41.58 ms | 38.89 ms | 24.55 ms | 1.81× | 1.58× | 4.5 MB |

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*Jpeg2000DecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

## JBIG2

**No baseline in this run**: SkiaSharp has no JBIG2 codec, and vcpkg's `jbig2dec` builds only as a static library, so there is
no DLL for the `LibraryImport` shim to load yet.

| Scenario | .NET 10 | .NET 11 | Allocated |
|---|---:|---:|---:|
| Arithmetic-coded generic region 850x1100 | 5.02 ms | 5.17 ms | 1.0 MB |
| MMR-coded generic region 850x1100 | 2.45 ms | 2.35 ms | 1.1 MB |

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*Jbig2DecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

## CCITT fax

Baseline: libtiff 4.7.2 for the two TIFF-wrapped inputs (libtiff has no entry point for a bare CCITT stream, so the raw Group 4
row is PeachImage-only). libtiff returns packed 1-bit scanlines, PeachImage 8-bit gray, so PeachImage does about 8× the output
work and allocation. SkiaSharp has no TIFF or fax codec. A 1700x2200 page-like bilevel image.

### Decode

| Scenario | PeachImage .NET 10 | PeachImage .NET 11 | libtiff | Ratio .NET 10 | Ratio .NET 11 | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Group 4 TIFF 1700x2200 | 17.01 ms | 16.80 ms | 6.98 ms | 2.48× | 2.41× | 5.7 MB |
| Group 3 TIFF 1700x2200 | 12.54 ms | 12.54 ms | 3.44 ms | 3.72× | 3.65× | 5.7 MB |
| Raw Group 4 stream 1700x2200 | 9.09 ms | 8.78 ms | — | — | — | 4.4 MB |

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*CcittDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

## SimdCodecs

Decode comparison against [Sdcb.SimdCodecs](https://github.com/sdcb/SimdCodecs) 1.4.0 (pure-managed, MIT, net10.0
only, single-threaded by default). Here the baseline is SimdCodecs, so `Ratio` below 1.00 means PeachImage is *faster*.
SimdCodecs decodes into one caller-owned RGBA8 buffer allocated once. PeachImage is measured two ways: `Decode(Stream)`
with the result disposed so the pooled pixel buffer is recycled (*Load*), and `ImageDecoder.DecodeInto` into one
caller-owned buffer with `SkipMetadata` (*Into*). `Ratio` is Load against SimdCodecs; PeachImage writes its native pixel
format (RGB24 for opaque images) where SimdCodecs always writes RGBA8.

```bash
dotnet run -c Release -f net11.0 --project bench/PeachImage.Benchmarks -- --filter "*SimdCodecsDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

| Scenario | Load .NET 10 | Load .NET 11 | Into .NET 10 | Into .NET 11 | SimdCodecs | Ratio .NET 10 | Ratio .NET 11 | Load alloc | Into alloc | SimdCodecs alloc |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| JPEG 1080p, 4:2:0 | 5.34 ms | 4.70 ms | 4.79 ms | 4.67 ms | 4.16 ms | 1.27× | 1.13× | 6.0 KB | 6.3 KB | 1.2 KB |
| JPEG 1080p, 4:4:4 | 5.18 ms | 5.95 ms | 5.34 ms | 5.79 ms | 5.33 ms | **0.96×** | 1.12× | 6.0 KB | 6.3 KB | 1.2 KB |
| JPEG 12MP, 4:2:0 | 23.04 ms | 22.89 ms | 25.51 ms | 22.69 ms | 24.13 ms | **0.94×** | **0.95×** | 6.2 KB | 6.5 KB | 1.3 KB |
| JPEG 1080p, grayscale | 3.72 ms | 3.73 ms | 3.68 ms | 4.32 ms | 3.08 ms | 1.19× | 1.21× | 4.3 KB | 8.6 KB | 609 B |
| PNG 24bpp | 16.84 ms | 16.09 ms | 16.90 ms | 16.00 ms | 15.77 ms | 1.01× | 1.02× | 15 KB | 15 KB | 8.9 KB |
| PNG 32bpp RGBA | 22.15 ms | 21.86 ms | 22.39 ms | 21.86 ms | 19.95 ms | 1.06× | 1.10× | 17 KB | 17 KB | 16 KB |
| WebP lossless | 15.62 ms | 15.15 ms | 15.43 ms | 15.17 ms | 18.54 ms | **0.82×** | **0.82×** | 43 KB | 44 KB | 219 B |
| WebP lossless + alpha | 19.34 ms | 18.90 ms | 19.24 ms | 18.82 ms | 19.29 ms | **0.97×** | **0.98×** | 42 KB | 43 KB | 210 B |
| WebP lossy | 14.88 ms | 14.84 ms | 14.89 ms | 14.83 ms | 13.77 ms | 1.07× | 1.08× | 6.2 KB | 6.9 KB | 115 B |
| WebP lossy + alpha | 16.08 ms | 15.50 ms | 16.06 ms | 15.55 ms | 13.95 ms | 1.12× | 1.11× | 13 KB | 14 KB | 110 B |

## Summary

Ratios are the .NET 11 run's; the sections above give the .NET 10 ratio beside each one.

| Format | Baseline | Decode | Encode |
|---|---|---|---|
| JPEG | SkiaSharp | 0.46×–0.61× | 1.22×–1.33× |
| BMP | SkiaSharp | 0.40×–1.04× | no baseline (PeachImage-only) |
| GIF | SkiaSharp | **2.12×–7.34×** (animated 7.34×, static 2.12×–3.68×) | no baseline (PeachImage-only) |
| PNG | SkiaSharp | 0.90×–1.29× | 0.46×–1.47× |
| WebP | SkiaSharp (libwebp) | 0.18×–1.41× (animated **0.18×**, static 0.75×–1.41×) | 0.88×–1.40× lossless (0.16× small-image outlier), 0.29× lossy |
| AVIF | libavif / dav1d | **1.43×–5.34×** | lossy 3.47×–8.74×, lossless 3.95×–29.8× (libaom, single-threaded) |
| TIFF | libtiff | 1.76× (LZW), 7.7×–8.1× (uncompressed/PackBits) | not implemented (decode-only) |
| JPEG 2000 | OpenJPEG | 1.41×–1.68× | not implemented (decode-only) |
| JBIG2 | none (no shared build of jbig2dec) | 5.17 ms arithmetic / 2.35 ms MMR for 850x1100 | not implemented (decode-only) |
| CCITT fax | libtiff | 2.41× (G4 TIFF), 3.65× (G3 TIFF) | not implemented (decode-only) |
| JPEG XL | libjxl | 1.03×–1.68× | lossless 0.07×–0.30× (faster); lossy 1.15×–1.28× at effort 5, 0.69× vs. effort 7; lossless size within 2% of libjxl effort 3; lossy files within a few percent of libjxl effort 7 at equal SSIMULACRA2 |
| Sdcb.SimdCodecs (managed) | SimdCodecs | JPEG 0.95×–1.21×; PNG 1.02×–1.10×; WebP 0.82×–1.11× | not measured |
| Resize | SkiaSharp | — | Downscale: 1.03×–2.25×; Upscale: 1.47× (NearestNeighbor) or **0.13×–0.37×** (Bilinear/cubic family, faster than SkiaSharp) |

Against SkiaSharp, JPEG decode beats it on every scenario, BMP and PNG are within or near target on most scenarios, and PNG
truecolor/RGBA encode and BMP 32bpp decode are well ahead. GIF decode is the furthest from the 10% target, and WebP static
decode beats SkiaSharp (libwebp) on most scenarios, with lossy photographic (1.10×) and the tiny flat-color graphic (1.41×) the
exceptions; WebP's *animated* decode is among the best results in this document (SkiaSharp's fixed per-frame native marshaling overhead
losing to PeachImage's managed loop), while GIF's animated decode is the worst (its LZW loop dominates). Against the
reference native codecs, PeachImage is 1.1×–2× slower on JPEG XL, JPEG 2000 and LZW TIFF decode, much further behind on AV1
and on the memory-bound uncompressed/PackBits TIFF paths, but faster than libjxl at equal effort on lossless JPEG XL encode.
Resize flips by direction — slower than SkiaSharp on downscale (PeachImage applies proper anti-aliasing filter widening that
SkiaSharp's default resize doesn't), faster on upscale for the cubic family.

Between the two runtimes most scenarios are within the noise floor. The clearest .NET 11 gains are JPEG decode (8–15%),
JPEG 2000 decode (6–18%), WebP lossless photographic and alpha encode (7–11%) and filtered resize downscale (1–11%). The one row
where .NET 11 reads slower, JPEG XL lossless decode with patches, is within that scenario's spread between processes (see the JPEG
XL section).
