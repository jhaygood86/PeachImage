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
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*JpegDecodeBenchmarks*" "*JpegEncodeBenchmarks*" "*BmpDecodeBenchmarks*" "*BmpEncodeBenchmarks*" "*PngDecodeBenchmarks*" "*PngEncodeBenchmarks*" "*WebpDecodeBenchmarks*" "*WebpEncodeBenchmarks*" "*GifEncodeBenchmarks*" "*JxlDecodeBenchmarks*" "*JxlEncodeBenchmarks*" "*AvifDecodeBenchmarks*" "*GifDecodeBenchmarks*" "*TiffDecodeBenchmarks*" "*Jpeg2000DecodeBenchmarks*" "*Jbig2DecodeBenchmarks*" "*CcittDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

The GIF section and the WebP/GIF "Animated" rows were collected separately (same methodology, same
session, same machine) with a filter scoped to just the `Animated-MultiFrame` category:

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*AnimatedAllFrames*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

The Resize section's numbers were collected separately, with a reduced iteration count (3 warmup + 8
iterations rather than the 20-iteration job above) given how many scenarios `ResizeBenchmarks.cs`
covers — treat them with correspondingly more skepticism than the rest of this document per the noise
floor note below:

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*ResizeBenchmarks*" --warmupCount 3 --iterationCount 8 --inProcess --affinity 15
```

**Environment**: BenchmarkDotNet v0.15.8, Windows 11, Intel Core i9-14900K (24 physical / 32 logical
cores), .NET 10.0 (SDK 10.0.403), X64 RyuJIT x86-64-v3, AVX2-capable. PeachImage also targets
.NET 8.0 (see [README](README.md)), but these numbers were not re-measured there — .NET 8's JIT lacks
some of .NET 9/10's auto-vectorization and dynamic PGO refinements, so net8.0 consumers may see
somewhat different throughput than what's reported below.

SkiaSharp 4.154.0. Reference codecs (vcpkg, MSVC, x64-windows dynamic): libjxl 0.12.0, OpenJPEG 2.5.4,
libtiff 4.7.2, libavif 1.4.2 with dav1d 1.5.4 and libyuv. The native baselines run on the same thread count
PeachImage does (libjxl and OpenJPEG are given `Environment.ProcessorCount` threads, libavif is single-threaded
like PeachImage's AV1 decode). Native decoders write into their own native layouts (see each section's notes),
so a few rows compare slightly different output work; those are called out where they apply.

`dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- native-smoke` decodes each native-baseline
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

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 1080p, 4:2:0 | 8.21 ms | 9.47 ms | 0.87× |
| 1080p, 4:4:4 | 9.69 ms | 11.51 ms | 0.84× |
| 1080p, Grayscale | 4.71 ms | 6.01 ms | 0.78× |
| 12MP, 4:2:0 | 48.50 ms | 56.56 ms | 0.86× |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 1080p, 4:2:0 | 27.65 ms | 19.63 ms | 1.41× |
| 1080p, 4:4:4 | 34.03 ms | 28.36 ms | 1.20× |

## BMP

SkiaSharp's encoder doesn't support BMP output, and there is no maintained reference C library to
substitute, so encode has no baseline.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 2.73 ms | 2.44 ms | 1.12× |
| 32bpp Alpha | 3.79 ms | 9.17 ms | **0.41×** |
| 8bpp Indexed | 2.54 ms | 2.26 ms | 1.12× |
| 8bpp Indexed, RLE | 6.43 ms | 8.01 ms | **0.80×** |

### Encode (PeachImage only — no baseline)

| Scenario | PeachImage Mean |
|---|---:|
| 24bpp Truecolor | 3.63 ms |
| 32bpp Alpha | 4.72 ms |
| 8bpp Indexed | 0.53 ms |
| 8bpp Indexed, RLE | 4.12 ms |

## PNG

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 17.72 ms | 16.87 ms | 1.05× |
| 32bpp RGBA | 23.32 ms | 21.84 ms | 1.07× |
| 48bpp (16-bit) Truecolor | 4.64 ms | 3.43 ms | 1.35× |
| 8bpp Grayscale | 8.56 ms | 9.40 ms | **0.91×** |
| Interlaced (Adam7) Truecolor | 26.25 ms | 26.91 ms | **0.98×** |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 77.00 ms | 144.67 ms | **0.53×** |
| 32bpp RGBA | 100.80 ms | 222.97 ms | **0.45×** |
| 8bpp Grayscale | 30.28 ms | 42.23 ms | **0.72×** |
| Low-color graphic (16 colors), indexed | 5.74 ms | 4.05 ms | 1.42× |

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

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Low-color graphic, single frame (640×480) | 1.05 ms | 0.26 ms | **3.99×** | 1,043 KB |
| Photographic dithered, single frame (1920×1080) | 12.37 ms | 5.35 ms | **2.31×** | 8.02 MB |
| Animated, all frames (24×, 320×240) | 4.01 ms | 0.64 ms | **6.28×** | 695 KB |

This is the largest gap measured against SkiaSharp in this document.

### Encode (PeachImage only — no baseline)

| Scenario | PeachImage Mean |
|---|---:|
| Low-color graphic (640×480) | 7.57 ms |
| Photographic dithered (1920×1080) | 114.38 ms |
| Photographic, no dither (1920×1080) | 100.71 ms |
| Animated, all frames (24×, 320×240) | 14.54 ms |

## WebP

Decode covers both of WebP's bitstream codecs (VP8 lossy, VP8L lossless), with and without alpha,
a small-image scenario, and an animated scenario (all 24 frames of the same source content/dimensions
as GIF's animated scenario above, transcoded to WebP via ffmpeg's `libwebp_anim` muxer, so the two are
directly comparable). Encode covers both the lossless (VP8L, default) and lossy (VP8,
`WebpEncoderOptions.Lossless = false`) bitstreams; animated encoding is not implemented (decode-only).
SkiaSharp's WebP codec is libwebp itself, so it also serves as the reference-codec baseline here.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Lossless, Photographic | 42.43 ms | 23.79 ms | **1.78×** | 12.5 MB |
| Lossy, Photographic | 28.72 ms | 14.25 ms | **2.02×** | 14.6 MB |
| Lossless, Graphic (flat color) | 0.69 ms | 0.34 ms | **2.00×** | 1.04 MB |
| Lossy, Alpha | 31.28 ms | 19.65 ms | **1.59×** | 18.5 MB |
| Lossless, Alpha | 43.02 ms | 25.53 ms | **1.68×** | 12.5 MB |
| Small image (32×24) | 14.05 µs | 11.86 µs | 1.18× | 45 KB |
| Animated, all frames (24×, 320×240) | 0.81 ms | 4.17 ms | **0.19×** | 1.22 MB |

The animated scenario is the strongest result in this whole document: PeachImage decodes it roughly
**5.2× faster** than SkiaSharp. SkiaSharp pays fixed per-call native marshaling overhead on every one of the 24
`GetPixels` calls, while PeachImage's decode is pure managed code with lower fixed per-frame cost
(`WebpFrameCompositor`/`GifFrameCompositor` reuse one persistent canvas in place rather than
allocating a fresh one per frame). GIF's animated decode remains far slower in relative terms (see
the GIF section above) because its own LZW decode loop, not frame compositing, dominates its
per-frame cost.

### Encode (lossless)

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Photographic | 247.43 ms | 222.26 ms | 1.11× | 14.3 MB |
| Graphic (flat color, palette) | 2.15 ms | 1.60 ms | **1.35×** | 650 KB |
| Alpha | 257.11 ms | 248.06 ms | 1.04× | 9.5 MB |
| Small image (32×24) | 36.78 µs | 241.91 µs | **0.15×** | 77 KB |

Photographic and alpha are near parity with SkiaSharp (both are dominated by the same LZ77-style
backward-reference search either way); the small-image case is where SkiaSharp's fixed per-call
native marshaling overhead dominates instead, and PeachImage's pure-managed path is ~6.6× faster.

### Encode (lossy)

`Vp8ImageEncoder` uses SAD-only mode decision (no rate-distortion search), a linear
quality→quantizer mapping, and no coefficient-probability adaptation, yet already beats SkiaSharp's
libwebp encoder on throughput at the same quality setting:

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Photographic (quality 75) | 35.08 ms | 110.61 ms | **0.32×** | 7.90 MB |

The remaining allocation gap (7.9 MB vs. SkiaSharp's ~2 KB) comes from one-time per-image buffers
(RGB-to-YUV conversion, final bitstream chunk assembly); libwebp's encoder works in unmanaged memory
throughout, so its managed allocation is near zero regardless of image size.

## Resize

`Image.Resize` vs. `SKBitmap.Resize`, for every `ResamplingFilter` that has a same-algorithm SkiaSharp
equivalent — the cubic filters via `SKSamplingOptions(new SKCubicResampler(B, C))` with the exact same
`(B, C)` constants `CubicBcKernel` uses, plus Nearest and Linear. Both directions are measured on the same
1920×1080 photographic source: a 4× downscale (480×270, thumbnail generation) and a 2× upscale (3840×2160).
Box, Lanczos2/3/5/8, and Welch have no SkiaSharp equivalent (Skia's public resize API exposes no
windowed-sinc or box option) and no reference C library with the same kernels, so those are PeachImage-only
rows. This run used the 20-iteration job (5 warmup) like the other sections.

### Downscale (1920×1080 → 480×270)

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| NearestNeighbor | 0.36 ms | 0.27 ms | 1.34× | 512 KB |
| Bilinear | 3.48 ms | 1.16 ms | **2.99×** | 568 KB |
| Bicubic | 4.55 ms | 3.66 ms | 1.24× | 591 KB |
| MitchellNetravali | 4.45 ms | 3.59 ms | 1.24× | 591 KB |
| Hermite | 4.53 ms | 3.64 ms | 1.25× | 591 KB |
| Spline | 4.57 ms | 3.58 ms | 1.28× | 592 KB |
| Robidoux | 4.49 ms | 3.63 ms | 1.24× | 591 KB |
| RobidouxSharp | 4.59 ms | 3.68 ms | 1.25× | 591 KB |

### Upscale (1920×1080 → 3840×2160)

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| NearestNeighbor | 24.15 ms | 14.48 ms | 1.67× | 32.0 MB |
| Bilinear | 55.26 ms | 68.64 ms | **0.81×** | 32.2 MB |
| Bicubic | 56.83 ms | 225.67 ms | **0.25×** | 32.2 MB |
| MitchellNetravali | 57.70 ms | 225.30 ms | **0.26×** | 32.2 MB |
| Hermite | 57.55 ms | 228.36 ms | **0.25×** | 32.2 MB |
| Spline | 60.37 ms | 224.40 ms | **0.27×** | 32.2 MB |
| Robidoux | 53.47 ms | 231.47 ms | **0.23×** | 32.2 MB |
| RobidouxSharp | 56.70 ms | 228.55 ms | **0.25×** | 32.2 MB |

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

| Scenario | Downscale | Upscale |
|---|---:|---:|
| Box | 3.12 ms | 51.54 ms |
| Welch | 3.56 ms | 52.39 ms |
| Lanczos2 | 4.57 ms | 56.07 ms |
| Lanczos3 | 5.68 ms | 62.03 ms |
| Lanczos5 | 7.82 ms | 65.90 ms |
| Lanczos8 | 10.86 ms | 79.08 ms |

### SIMD convolver tier (Vector128 vs. Vector256, not re-measured)

Isolated per-pass comparison, bypassing `ResamplingConvolverSelector` and `Image.Resize` entirely (same
approach as the JPEG chroma-upsampling tier comparison above), on the same downscale's horizontal and
vertical passes (both now parallelized, so these are faster in absolute terms than the same comparison
measured before parallelization, not just relative to each other):

| Pass | Vector128 | Vector256 | Ratio |
|---|---:|---:|---:|
| Horizontal | 2.199 ms | 2.186 ms | 0.99× |
| Vertical | 0.693 ms | 0.400 ms | **0.58×** |

Matches the design intent: the vertical pass's genuine 8-lane width shows a consistent ~35-47% speedup
across runs; the horizontal pass (which delegates straight to the Vector128 tier — see
`Vector256ResamplingConvolver`'s remarks) shows no consistent difference, run-to-run noise aside.

## AVIF

Decode baseline: libavif 1.4.2 with the dav1d 1.5.4 AV1 decoder and libyuv YUV-to-RGB conversion, single-threaded
(PeachImage's AV1 decode is single-threaded too), producing 8-bit RGBA. SkiaSharp 4.154.0 does not decode AVIF.

### Decode

| Scenario | PeachImage | libavif | Ratio | PeachImage allocated |
|---|---:|---:|---:|---:|
| Photographic, 8-bit 4:2:0 | 127.4 ms | 19.6 ms | **6.50×** | 33.0 MB |
| Photographic, 8-bit 4:2:0 + alpha | 145.7 ms | 21.8 ms | **6.70×** | 34.5 MB |
| Photographic, lossless (4:4:4) | 569.7 ms | 134.5 ms | **4.24×** | 48.5 MB |
| Small image (32×24) | 132.9 µs | 13.1 µs | **10.2×** | 260 KB |

AV1 decode is the format where PeachImage is furthest from a hand-tuned native decoder: dav1d is SIMD-assembly
throughout. Per-image managed allocation is a PeachImage-only concern (libavif's buffers are unmanaged).

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

| Scenario | PeachImage | libtiff | Ratio | PeachImage allocated |
|---|---:|---:|---:|---:|
| 1080p, Uncompressed | 4.96 ms | 0.72 ms | **6.90×** | 39.9 MB |
| 1080p, LZW | 23.55 ms | 13.35 ms | **1.76×** | 41.7 MB |
| 1080p, PackBits | 5.37 ms | 0.78 ms | **6.89×** | 40.0 MB |

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

| Scenario | PeachImage | libjxl | Ratio | PeachImage allocated |
|---|---:|---:|---:|---:|
| Lossy 4064x2704 (11 MP) | 272.2 ms | 247.9 ms | 1.10× | 64.3 MB |
| Lossless 1600x1096 with patches | 76.4 ms | 45.1 ms | **1.69×** | 16.9 MB |
| Lossless 2048x2048 with splines | 213.3 ms | 166.3 ms | **1.28×** | 21.8 MB |
| Lossy 500x606 with noise | 9.84 ms | 5.75 ms | **1.71×** | 2.7 MB |

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*JxlDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

### Encode

Photographic 1920x1080 sources, `Quality` 75 (libjxl: `JxlEncoderDistanceFromQuality(75)`). PeachImage runs at its default
`Effort` 5; libjxl is shown at effort 5 for a like-for-like effort number and at effort 7 (its own default).

| Scenario | PeachImage (effort 5) | libjxl effort 5 | Ratio | libjxl effort 7 | Ratio | PeachImage allocated |
|---|---:|---:|---:|---:|---:|---:|
| Lossless RGB | 59.7 ms | 294.1 ms | **0.20×** | 803.2 ms | **0.07×** | 111.0 MB |
| Lossless gray | 26.4 ms | 83.9 ms | **0.31×** | — | — | 41.2 MB |
| Lossy RGB | 222.6 ms | 173.8 ms | **1.28×** | 321.0 ms | **0.69×** | 151.0 MB |
| Lossy gray | 219.6 ms | 192.5 ms | 1.14× | — | — | 134.6 MB |
| Lossless RGBA | 83.0 ms | — | — | — | — | 121.0 MB |

Lossless RGBA has no libjxl row (the shim doesn't set up the extra alpha channel). Effort-for-effort PeachImage's lossless
Modular encode is several times faster than libjxl's at a larger file (see the size table below); the lossy VarDCT encode is
within roughly 14–28% of libjxl at the same effort.

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*JxlEncodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
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

| Scenario | PeachImage | OpenJPEG | Ratio | PeachImage allocated |
|---|---:|---:|---:|---:|
| Lossless 1280x960 | 91.7 ms | 50.3 ms | **1.82×** | 4.4 MB |
| Lossless 1280x960, half size (`ReduceResolutionLevels = 1`) | 28.8 ms | 17.8 ms | **1.62×** | 1.3 MB |
| Lossy 1280x960 | 40.3 ms | 24.4 ms | **1.65×** | 4.4 MB |

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*Jpeg2000DecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

## JBIG2

**No baseline in this run**: SkiaSharp has no JBIG2 codec, and vcpkg's `jbig2dec` builds only as a static library, so there is
no DLL for the `LibraryImport` shim to load yet. PeachImage-only numbers from the previous measurement: 5.92 ms for the
arithmetic-coded 850x1100 generic region and 3.04 ms for the MMR-coded one.

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*Jbig2DecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

## CCITT fax

Baseline: libtiff 4.7.2 for the two TIFF-wrapped inputs (libtiff has no entry point for a bare CCITT stream, so the raw Group 4
row is PeachImage-only). libtiff returns packed 1-bit scanlines, PeachImage 8-bit gray, so PeachImage does about 8× the output
work and allocation. SkiaSharp has no TIFF or fax codec. A 1700x2200 page-like bilevel image.

### Decode

| Scenario | PeachImage | libtiff | Ratio | PeachImage allocated |
|---|---:|---:|---:|---:|
| Group 4 TIFF 1700x2200 | 17.46 ms | 7.07 ms | **2.47×** | 5.4 MB |
| Group 3 TIFF 1700x2200 | 12.53 ms | 3.42 ms | **3.66×** | 5.4 MB |
| Raw Group 4 stream 1700x2200 | 9.24 ms | — | — | 4.4 MB |

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*CcittDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

## Summary

| Format | Baseline | Decode | Encode |
|---|---|---|---|
| JPEG | SkiaSharp | 0.78×–0.87× | 1.20×–1.41× |
| BMP | SkiaSharp | 0.41×–1.12× | no baseline (PeachImage-only) |
| GIF | SkiaSharp | **2.31×–6.28×** (animated 6.28×, static 2.31×–3.99×) | no baseline (PeachImage-only) |
| PNG | SkiaSharp | 0.91×–1.35× | 0.45×–1.42× |
| WebP | SkiaSharp (libwebp) | 0.19×–2.02× (animated **0.19×**, static 1.18×–2.02×) | 1.04×–1.35× lossless (0.15× small-image outlier), 0.32× lossy |
| AVIF | libavif / dav1d | **4.24×–10.2×** | implemented, throughput not measured here |
| TIFF | libtiff | 1.76× (LZW), 6.9× (uncompressed/PackBits) | not implemented (decode-only) |
| JPEG 2000 | OpenJPEG | 1.62×–1.82× | not implemented (decode-only) |
| JBIG2 | none (no shared build of jbig2dec) | 5.92 ms arithmetic / 3.04 ms MMR for 850x1100 | not implemented (decode-only) |
| CCITT fax | libtiff | 2.47× (G4 TIFF), 3.66× (G3 TIFF) | not implemented (decode-only) |
| JPEG XL | libjxl | 1.10×–1.71× | lossless 0.07×–0.31× (faster); lossy 1.14×–1.28× at effort 5, 0.69× vs. effort 7; lossless size within 2% of libjxl effort 3; lossy files within a few percent of libjxl effort 7 at equal SSIMULACRA2 |
| Resize | SkiaSharp | — | Downscale: 1.24×–2.99×; Upscale: 1.67× (NearestNeighbor) or **0.23×–0.81×** (Bilinear/cubic family, faster than SkiaSharp) |

Against SkiaSharp, JPEG decode beats it on every scenario, BMP and PNG are within or near target on most scenarios, and PNG
truecolor/RGBA encode and BMP 32bpp decode are well ahead. WebP static decode and GIF decode are the furthest from the 10%
target; WebP's *animated* decode is the best result in this document (SkiaSharp's fixed per-frame native marshaling overhead
losing to PeachImage's managed loop), while GIF's animated decode is the worst (its LZW loop dominates). Against the
reference native codecs, PeachImage is 1.1×–2× slower on JPEG XL, JPEG 2000 and LZW TIFF decode, much further behind on AV1
and on the memory-bound uncompressed/PackBits TIFF paths, but faster than libjxl at equal effort on lossless JPEG XL encode.
Resize flips by direction — slower than SkiaSharp on downscale (PeachImage applies proper anti-aliasing filter widening that
SkiaSharp's default resize doesn't), faster on upscale for the cubic family.
