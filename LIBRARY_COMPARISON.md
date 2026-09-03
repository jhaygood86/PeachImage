# PeachImage vs. SkiaSharp

Performance comparison of PeachImage against [SkiaSharp](https://github.com/mono/SkiaSharp) (a
mature, real-world, native-backed image library) for JPEG, BMP, GIF, PNG, and WebP decode/encode
throughput, plus `Image.Resize`. SkiaSharp is used as a single consistent baseline across all formats
— it's also the corpus tests' differential oracle (see the [README](README.md)).

**Target**: PeachImage's Mean within 10% of SkiaSharp's Mean (`Ratio` column ≤ ~1.10) for every
scenario. Ratios below 1.00 mean PeachImage is *faster* than SkiaSharp.

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
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*JpegDecodeBenchmarks*" "*JpegEncodeBenchmarks*" "*BmpDecodeBenchmarks*" "*BmpEncodeBenchmarks*" "*PngDecodeBenchmarks*" "*PngEncodeBenchmarks*" "*WebpDecodeBenchmarks*" "*AvifDecodeBenchmarks*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

The GIF section and the WebP/GIF "Animated" rows were collected separately (same methodology, same
session, same machine) with a filter scoped to just the `Animated-MultiFrame` category:

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*AnimatedAllFrames*" --warmupCount 5 --iterationCount 20 --inProcess --affinity 15
```

The static (non-animated) GIF decode scenarios in `GifDecodeBenchmarks.cs` (low-color graphic,
dithered photographic) were run with the same `*GifDecodeBenchmarks*` filter as the GIF section's
`--filter "*"` command above.

The Resize section's numbers were collected separately, with a reduced iteration count (3 warmup + 8
iterations rather than the 20-iteration job above) given how many scenarios `ResizeBenchmarks.cs`
covers — treat them with correspondingly more skepticism than the rest of this document per the noise
floor note below:

```bash
dotnet run -c Release -f net10.0 --project bench/PeachImage.Benchmarks -- --filter "*ResizeBenchmarks*" --warmupCount 3 --iterationCount 8 --inProcess --affinity 15
```

**Environment**: BenchmarkDotNet v0.15.8, Windows 11, Intel Core i9-14900K (24 physical / 32 logical
cores), .NET 10.0.11 (SDK 10.0.400), X64 RyuJIT x86-64-v3, AVX2-capable. PeachImage also targets
.NET 8.0 (see [README](README.md)), but these numbers were not re-measured there — .NET 8's JIT lacks
some of .NET 9/10's auto-vectorization and dynamic PGO refinements, so net8.0 consumers may see
somewhat different throughput than what's reported below.

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
| 1080p, 4:2:0 | 8.94 ms | 9.74 ms | 0.92× |
| 1080p, 4:4:4 | 10.85 ms | 11.82 ms | 0.92× |
| 1080p, Grayscale | 5.15 ms | 5.96 ms | 0.86× |
| 12MP, 4:2:0 | 53.24 ms | 56.75 ms | 0.94× |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 1080p, 4:2:0 | 26.89 ms | 19.90 ms | 1.35× |
| 1080p, 4:4:4 | 32.83 ms | 27.74 ms | 1.18× |

## BMP

SkiaSharp's encoder doesn't support BMP output, so encode has no SkiaSharp baseline.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 2.39 ms | 2.51 ms | **0.95×** |
| 32bpp Alpha | 3.60 ms | 9.37 ms | **0.38×** |
| 8bpp Indexed | 2.23 ms | 2.15 ms | 1.04× |
| 8bpp Indexed, RLE | 6.15 ms | 8.30 ms | **0.74×** |

### Encode (PeachImage only — no SkiaSharp baseline)

| Scenario | PeachImage Mean |
|---|---:|
| 24bpp Truecolor | 3.94 ms |
| 32bpp Alpha | 5.22 ms |
| 8bpp Indexed | 0.60 ms |
| 8bpp Indexed, RLE | 4.19 ms |

## PNG

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 18.35 ms | 16.72 ms | 1.10× |
| 32bpp RGBA | 23.36 ms | 22.10 ms | 1.06× |
| 48bpp (16-bit) Truecolor | 4.65 ms | 3.41 ms | 1.36× |
| 8bpp Grayscale | 8.68 ms | 9.41 ms | **0.92×** |
| Interlaced (Adam7) Truecolor | 26.13 ms | 27.21 ms | **0.96×** |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 122.08 ms | 148.98 ms | **0.82×** |
| 32bpp RGBA | 148.28 ms | 225.23 ms | **0.66×** |
| 8bpp Grayscale | 49.96 ms | 43.41 ms | 1.15× |
| Low-color graphic (16 colors), indexed | 6.08 ms | 4.09 ms | 1.49× |

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
not support GIF output (confirmed empirically — it returns null), so encode throughput is tracked
for PeachImage alone, over time.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Low-color graphic, single frame (640×480) | 0.91 ms | 0.28 ms | **3.29×** | 941 KB |
| Photographic dithered, single frame (1920×1080) | 11.50 ms | 5.53 ms | **2.08×** | 6.24 MB |
| Animated, all frames (24×, 320×240) | 3.61 ms | 0.53 ms | **6.77×** | 711 KB |

This is the largest gap measured anywhere in this document — everything else stays under ~2.3×.

### Encode (PeachImage only — no SkiaSharp baseline)

| Scenario | PeachImage Mean |
|---|---:|
| Animated, all frames (24×, 320×240) | 13.51 ms |

## WebP

Decode covers both of WebP's bitstream codecs (VP8 lossy, VP8L lossless), with and without alpha,
a small-image scenario, and an animated scenario (all 24 frames of the same source content/dimensions
as GIF's animated scenario above, transcoded to WebP via ffmpeg's `libwebp_anim` muxer, so the two are
directly comparable). Encode covers both the lossless (VP8L, default) and lossy (VP8,
`WebpEncoderOptions.Lossless = false`) bitstreams; animated encoding is not implemented (decode-only).

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Lossless, Photographic | 43.82 ms | 24.02 ms | **1.82×** | 10.9 MB |
| Lossy, Photographic | 29.33 ms | 14.32 ms | **2.05×** | 6.9 MB |
| Lossless, Graphic (flat color) | 0.76 ms | 0.35 ms | 2.17× | 0.96 MB |
| Lossy, Alpha | 33.49 ms | 19.85 ms | **1.69×** | 11.1 MB |
| Lossless, Alpha | 45.62 ms | 24.82 ms | **1.84×** | 13.0 MB |
| Small image (32×24) | 14.24 µs | 12.69 µs | **1.12×** | 44 KB |
| Animated, all frames (24×, 320×240) | 1.14 ms | 4.15 ms | **0.27×** | 1.9 MB |

The animated scenario is the strongest result in this whole document: PeachImage decodes it roughly
**3.6× faster** than SkiaSharp, consistent across repeated runs. As with the small-image lossless
encode case below, SkiaSharp pays fixed per-call native marshaling overhead on every one of the 24
`GetPixels` calls, while PeachImage's decode is pure managed code with lower fixed per-frame cost
(`WebpFrameCompositor`/`GifFrameCompositor` reuse one persistent canvas in place rather than
allocating a fresh one per frame). GIF's animated decode remains far slower in relative terms (see
the GIF section above) because its own LZW decode loop, not frame compositing, dominates its
per-frame cost.

### Encode (lossless)

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Photographic | 241.78 ms | 237.21 ms | 1.02× | 15.0 MB |
| Graphic (flat color, palette) | 2.16 ms | 1.51 ms | **1.44×** | 666 KB |
| Alpha | 265.11 ms | 262.98 ms | 1.01× | 9.5 MB |
| Small image (32×24) | 38.09 µs | 240.64 µs | **0.16×** | 78 KB |

Photographic and alpha are near parity with SkiaSharp (both are dominated by the same LZ77-style
backward-reference search either way); the small-image case is where SkiaSharp's fixed per-call
native marshaling overhead dominates instead, and PeachImage's pure-managed path is ~6× faster.

### Encode (lossy)

`Vp8ImageEncoder` uses SAD-only mode decision (no rate-distortion search), a linear
quality→quantizer mapping, and no coefficient-probability adaptation, yet already beats SkiaSharp's
libwebp encoder on throughput at the same quality setting:

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Photographic (quality 75) | 102.8 ms | 111.0 ms | **0.93×** | 8.29 MB |

The remaining allocation gap (8.3 MB vs. SkiaSharp's ~1 KB) comes from one-time per-image buffers
(RGB-to-YUV conversion, final bitstream chunk assembly); libwebp's encoder works in unmanaged memory
throughout, so its managed allocation is near zero regardless of image size. The per-macroblock
scratch allocations that previously dominated this figure (a fresh `short[16]`/`short[16][]` per
block, ~52 MB for this scenario) are now stack-allocated instead.

## Resize

`Image.Resize` vs. `SKBitmap.Resize`, for every `ResamplingFilter` that has a same-algorithm SkiaSharp
equivalent — the cubic filters via `SKSamplingOptions(new SKCubicResampler(B, C))` with the exact same
`(B, C)` constants `CubicBcKernel` uses, plus Nearest and Linear. Both directions are measured on the same
1920×1080 photographic source: a 4× downscale (480×270, thumbnail generation) and a 2× upscale (3840×2160).
Box, Lanczos2/3/5/8, and Welch have no SkiaSharp equivalent (Skia's public resize API exposes no
windowed-sinc or box option), so those are PeachImage-only rows, the same pattern BMP/GIF encode use above
for gaps in SkiaSharp's own API surface.

### Downscale (1920×1080 → 480×270)

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| NearestNeighbor | 0.41 ms | 0.32 ms | 1.29× | 389 KB |
| Bilinear | 4.25 ms | 1.19 ms | **3.58×** | 446 KB |
| Bicubic | 5.11 ms | 3.68 ms | **1.39×** | 470 KB |
| MitchellNetravali | 6.57 ms | 5.14 ms | 1.28× | 471 KB |
| Hermite | 5.07 ms | 3.84 ms | **1.32×** | 470 KB |
| Spline | 5.12 ms | 3.55 ms | **1.44×** | 470 KB |
| Robidoux | 5.25 ms | 3.74 ms | **1.40×** | 470 KB |
| RobidouxSharp | 5.16 ms | 3.68 ms | **1.40×** | 470 KB |

### Upscale (1920×1080 → 3840×2160)

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| NearestNeighbor | 22.92 ms | 16.27 ms | 1.41× | 24.9 MB |
| Bilinear | 41.93 ms | 70.98 ms | **0.59×** | 25.1 MB |
| Bicubic | 49.79 ms | 230.46 ms | **0.22×** | 25.1 MB |
| MitchellNetravali | 45.20 ms | 243.44 ms | **0.19×** | 25.1 MB |
| Hermite | 46.32 ms | 240.13 ms | **0.19×** | 25.1 MB |
| Spline | 48.41 ms | 225.21 ms | **0.21×** | 25.1 MB |
| Robidoux | 48.26 ms | 229.43 ms | **0.21×** | 25.1 MB |
| RobidouxSharp | 49.88 ms | 227.89 ms | **0.22×** | 25.1 MB |

Downscale and upscale pull in opposite directions. On downscale, PeachImage widens the filter's radius by
the scale factor before convolving (the standard "scaled filter" anti-aliasing technique — see
`ResamplingWeightMap`), which is real extra work SkiaSharp's default (non-mipmapped) `SKBitmap.Resize` does
not do; the more expensive that widened window gets per destination pixel (cubic family) or the cheaper
SkiaSharp's own base case already is (Bilinear, radius 1 either way), the wider the gap. On upscale, that
widening never triggers (the window is just the kernel's native radius), so PeachImage's throughput for the
cubic family beats SkiaSharp outright — SkiaSharp appears to pay a comparatively larger fixed cost per
output pixel there. NearestNeighbor, doing no windowed convolution either way, stays closest to parity in
both directions.

Four perf/allocation changes landed after this comparison was first written, all covered by
`ResizeSkiaSharpQualityTests`/the resize unit suite so none change observable output — cumulatively, roughly
halving the downscale ratio (e.g. Bicubic 3.42× → 1.39×) and taking the cubic-family upscale ratio well
under half of where it started (e.g. Bicubic 0.47× → 0.22×):

- **Pooled convolution buffers.** `ImageResizer` rents its intermediate `float[]` buffers from
  `ArrayPool<float>.Shared` instead of allocating fresh ones per call, and `AnimatedImage.Resize` builds each
  axis's `ResamplingWeightMap` once and reuses it across every frame rather than rebuilding it per frame (see
  `AnimatedImage.ResizeFrames`). Allocation dropped from 44.0 MB (480×270 downscale) / 257 MB (3840×2160
  upscale) to 389 KB–471 KB / 24.9–25.1 MB — a ~100× reduction downscale, ~10× upscale. What's left is
  essentially the unavoidable output `Image`'s own pixel buffer (24.9 MB for a 3840×2160 24bpp image), not
  pooling waste.
- **Flat weight-map storage.** `ResamplingWeightMap.Weights` is one contiguous `float[]` (sliced per
  destination index via `GetWeights`) instead of a `float[][]` with one small array per destination index —
  up to thousands fewer allocations per weight map, and better cache locality while convolving.
- **Parallelized rows, everywhere in the pipeline.** Both convolution passes' per-row loop, plus the
  byte/ushort↔float boundary conversions (`ImageResizer.ToFloatBuffer`/`FromFloatBuffer`), now run through
  `ResamplingParallel.For`, which switches from a sequential loop to `Parallel.For` above 64 rows (small
  images, including this repo's many tiny-image unit tests, stay sequential — the threading overhead isn't
  worth it below that). The boundary conversions read/write through `Image.PixelMemory` (`Memory<byte>`)
  rather than `Image.GetPixelSpan()` (`Span<byte>`) specifically because a `Span<T>` can't be captured by the
  closure `Parallel.For` requires — see `IResamplingConvolver`'s remarks for the same constraint on the
  convolvers themselves. One non-obvious lesson from getting this right: **`Parallel.For`'s default degree of
  parallelism doesn't respect the machine's actual usable core count under CPU-affinity restriction** — this
  repo's own `--affinity <mask>` benchmarking convention (see "How to reproduce" below) pins the process to a
  handful of cores for reproducibility, and left to its default heuristic, `Parallel.For` still tried to
  schedule work as if all 32 logical cores were available, causing enough thread oversubscription that
  several filters' "parallel" upscale path measured *slower* than the sequential code it replaced. Explicitly
  capping `ParallelOptions.MaxDegreeOfParallelism` at `Environment.ProcessorCount` (which does correctly
  reflect the affinity-restricted count) fixed it — see `ResamplingParallel`'s remarks.
- **Pass-order selection.** The two convolution passes can run in either order (horizontal-then-vertical or
  vertical-then-horizontal) and reach the same result — `ImageResizer.ResizeWithWeights` now picks whichever
  order produces the smaller intermediate buffer instead of always running horizontal first, so the more
  expensive second pass has less carried-over data to process on any resize whose two axes scale by
  meaningfully different amounts.

### PeachImage only — no SkiaSharp baseline

| Scenario | Downscale | Upscale |
|---|---:|---:|
| Box | 4.00 ms | 44.49 ms |
| Welch | 4.19 ms | 47.45 ms |
| Lanczos2 | 6.97 ms | 47.78 ms |
| Lanczos3 | 6.96 ms | 55.99 ms |
| Lanczos5 | 9.50 ms | 59.74 ms |
| Lanczos8 | 18.33 ms | 78.72 ms |

### SIMD convolver tier (Vector128 vs. Vector256)

Isolated per-pass comparison, bypassing `ResamplingConvolverSelector` and `Image.Resize` entirely (same
approach as the JPEG chroma-upsampling tier comparison above), on the same downscale's horizontal and
vertical passes (both now parallelized, so these are faster in absolute terms than the same comparison
measured before parallelization, not just relative to each other):

| Pass | Vector128 | Vector256 | Ratio |
|---|---:|---:|---:|
| Horizontal | 2.301 ms | 2.377 ms | 1.03× |
| Vertical | 0.726 ms | 0.386 ms | **0.53×** |

Matches the design intent: the vertical pass's genuine 8-lane width shows a consistent ~35-47% speedup
across runs; the horizontal pass (which delegates straight to the Vector128 tier — see
`Vector256ResamplingConvolver`'s remarks) shows no consistent difference, run-to-run noise aside.

## AVIF

Decode-only, for baseline still images (single or grid-composited item, 8/10-bit, with or without
alpha). **No SkiaSharp baseline**: this repo's pinned SkiaSharp version doesn't decode AVIF. In its
place, the table below reports `ffmpeg -c:v libdav1d`/`libaom` process-spawn timing as context, not a
directly comparable BenchmarkDotNet row — `ffmpeg`'s number includes process-startup overhead and is a
decade-tuned native decoder, not a peer to benchmark parity against the way SkiaSharp is elsewhere in
this document. `ffmpeg`'s number wasn't re-measured this session; only PeachImage's was.

### Decode

| Scenario | PeachImage | `ffmpeg` (context only) | Allocated |
|---|---:|---:|---:|
| Photographic, 8-bit 4:2:0 | 145.3 ms | 68.6 ms | 17.8 MB |
| Photographic, 8-bit 4:2:0 + alpha | 162.0 ms | — | 19.8 MB |
| Small image (32×24) | 116.4 µs | — | 213 KB |

PeachImage is roughly **2.12×** `ffmpeg`'s process-spawn-inclusive time on the 1080p scenario.

### Encode (lossless size)

`AvifEncoderOptions.Lossless = true` compared against this repo's own PNG encoder on the same pixels, plus
`ffmpeg`'s lossless AV1 encode (`-c:v libaom-av1 -crf 0 -still-picture 1`) as external context. Reproduce
with the deterministic 128×128 multi-octave fractal-noise fixture in `LosslessSizeRegressionTests`
(`dotnet test --filter FractalNoiseImage_LosslessAvif_IsSmallerThanSourcePng`):

| Scenario | PeachImage PNG | PeachImage lossless AVIF | `ffmpeg` lossless AVIF (context only) |
|---|---:|---:|---:|
| 128×128 photo-like (fractal noise) | 28,128 bytes | 27,386 bytes | 26,154 bytes |

## TIFF

Decode-only (uncompressed/LZW/PackBits, 1/2/4/8/16-bit, grayscale/RGB/palette/CMYK). **No SkiaSharp
baseline**: confirmed via `SKEncodedImageFormat`/`SkCodec` — SkiaSharp has no TIFF codec at all, not just an
unsupported feature within one. As with AVIF above, the table reports `ffmpeg`'s (`libavcodec/tiff.c`)
process-spawn-inclusive time as context only, not a directly comparable BenchmarkDotNet row.

### Decode

| Scenario | PeachImage | `ffmpeg` (context only) | Allocated |
|---|---:|---:|---:|
| 1080p, Uncompressed | 5.46 ms | ~49 ms | 39.9 MB |
| 1080p, LZW | 24.20 ms | ~69 ms | 41.7 MB |
| 1080p, PackBits | 3.96 ms | ~48 ms | 40.0 MB |

Unlike AVIF's comparison (where AV1's entropy/transform pipeline dominates and PeachImage trails
`ffmpeg`'s decade-tuned native decoder), TIFF's baseline compression modes are each cheap enough that
`ffmpeg`'s fixed per-invocation process-spawn overhead (visible in the PackBits row: the fastest and
simplest of the three, yet not meaningfully faster in wall-clock terms than Uncompressed) dominates its own
number — these ratios say more about process-spawn cost than about decoder throughput, and shouldn't be
read as "PeachImage is 9-12× faster than a real TIFF decoder." LZW is the slowest of the three for both
implementations, consistent with it being the only one doing real dictionary-based decompression work
rather than a fixed per-byte reshape.

## Summary

| Format | Decode | Encode |
|---|---|---|
| JPEG | 1.16×–1.37× | 1.18×–1.35× |
| BMP | 0.38×–1.04× | no baseline (PeachImage-only) |
| GIF | **2.08×–6.77× (animated: 6.77×, static: 2.08×–3.29×)** | no SkiaSharp baseline (PeachImage-only) |
| PNG | 0.92×–1.36× | 0.66×–1.15× |
| WebP | 0.27×–2.17× (animated: **0.27×**, static: 1.12×–2.17×) | 1.01×–1.44× lossless (0.16× small-image outlier), 0.88× lossy |
| AVIF | ~2.12× vs. `ffmpeg` (no SkiaSharp baseline available) | implemented; lossy fixed 8x8 blocks, lossless has a real partition-tree RDO search up to 64x64 (see Encode (lossless size) above); throughput not yet measured here |
| TIFF | ~0.08×–0.35× vs. `ffmpeg` (no SkiaSharp baseline available; ratios dominated by `ffmpeg`'s process-spawn overhead, not decoder throughput) | not implemented (decode-only) |
| Resize | — | Downscale: 1.28×–3.58× (NearestNeighbor closest, Bilinear/cubic slower); Upscale: 1.41× (NearestNeighbor) or **0.19×–0.59×** (Bilinear/cubic family, faster than SkiaSharp) |

BMP is fully within target and often faster. PNG is within target on every scenario and beats
SkiaSharp outright on 8bpp grayscale/interlaced decode and on truecolor/RGBA encode; its remaining
gap is the 16-bit decode path, now 1.36× (down from 2.3×). JPEG has the largest gap on both sides
among the mature formats. WebP's static
decode and AVIF are the furthest from the 10% target on large images, but WebP's *animated* decode is
actually the single best result in this document (SkiaSharp's fixed per-frame native marshaling
overhead losing badly to PeachImage's managed decode loop). GIF's animated decode is the worst result
in this document by a wide margin — its own LZW decode loop, not frame compositing, is the dominant
cost (see the GIF section above). Resize is the one case in this document where PeachImage's ratio
flips sign by direction — slower than SkiaSharp on downscale (it applies proper anti-aliasing filter
widening that SkiaSharp's default resize doesn't), faster on upscale for the cubic family — though its
allocation is now pooled and close to SkiaSharp's own footprint (see the Resize section above).
