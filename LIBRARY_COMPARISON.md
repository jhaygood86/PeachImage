# PeachImage vs. SkiaSharp

Performance comparison of PeachImage against [SkiaSharp](https://github.com/mono/SkiaSharp) (a
mature, real-world, native-backed image library) for JPEG, BMP, GIF, PNG, and WebP decode/encode
throughput. SkiaSharp is used as a single consistent baseline across all formats — it's also the
corpus tests' differential oracle (see the [README](README.md)).

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

The static (non-animated) GIF decode scenarios already present in `GifDecodeBenchmarks.cs`
(low-color graphic, dithered photographic) weren't run into this document — only the animated one
was, since that's what this measurement pass set out to fill in.

**Environment**: BenchmarkDotNet v0.15.8, Windows 11, Intel Core i9-14900K (24 physical / 32 logical
cores), .NET 10.0.11 (SDK 10.0.400), X64 RyuJIT x86-64-v3, AVX2-capable. PeachImage also targets
.NET 8.0 (see [README](README.md)), but these numbers were not re-measured there — .NET 8's JIT lacks
some of .NET 9/10's auto-vectorization and dynamic PGO refinements, so net8.0 consumers may see
somewhat different throughput than what's reported below.

**Noise floor**: even pinned, this machine shows several-percent between-run drift — a same-session,
back-to-back before/after comparison against code that wasn't touched (BMP, AVIF; see "Since the
last measurement" below) swung -6.7% to +4.5% and -7.5% to +3.4% respectively with zero code changes.
`--job short`'s 3 warmup + 3 iterations can't resolve deltas under that; the 20-iteration job above
can, but treat any single-digit-percent difference between two separate runs (as opposed to two
scenarios measured in the *same* run) with corresponding skepticism.

## Since the last measurement

The SIMD kernels across JPEG, PNG, and WebP were swept from bounds-checked `Vector128/256.Create(span)`
loads and `vector.CopyTo(span)` stores to unchecked `LoadUnsafe`/`StoreUnsafe` — the loop in every
touched call site already guarantees enough elements remain, so the bounds check `Create`/`CopyTo`
perform is redundant. Isolated (`bench/PeachImage.Benchmarks/VectorLoadBenchmarks.cs`, the same
pinned+long-job methodology as above), `LoadUnsafe` is a consistent 5–20% faster than `Create` across
byte/uint/float at both 128- and 256-bit width, on both .NET 8 and .NET 10.

At the full decode/encode pipeline level — measured as a controlled before/after in this same
session, same machine, same methodology (`git stash` on just the swept `src/` files, not the doc
numbers below) — the effect is real but small, and only clearly visible where the swept kernels are a
large fraction of total pipeline time:

- **PNG encode**: 1.1%–2.5% faster across all three scenarios (every scenario improved — the
  strongest signal, since PNG encode spends most of its time in exactly the row-filter kernels that
  were swept).
- **WebP decode**: 0.6%–1.1% faster on 4 of 6 scenarios (the two large lossy/lossless-with-alpha and
  photographic scenarios); flat to slightly slower on the lossless-photographic and small-image
  scenarios.
- **PNG decode**: small improvement on most scenarios (up to 4.2% on 48bpp), flat on interlaced.
- **JPEG decode/encode**: no change distinguishable from this machine's noise floor (see above) —
  entropy decode/Huffman coding, which wasn't touched, dominates JPEG's pipeline time far more than
  DCT/color-conversion/upsampling do.
- **BMP, AVIF**: untouched by this sweep; included above as a same-session noise-floor control.

## JPEG

Both libraries decode/encode from the same source files; JPEG encode uses explicit 4:2:0/4:4:4 chroma
subsampling on both sides (`JpegEncoderOptions.Subsampling` / `SKJpegEncoderOptions.Downsample`) at
quality 85.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 1080p, 4:2:0 | 12.78 ms | 9.68 ms | 1.32× |
| 1080p, 4:4:4 | 15.49 ms | 11.73 ms | 1.32× |
| 1080p, Grayscale | 7.04 ms | 6.08 ms | 1.16× |
| 12MP, 4:2:0 | 75.74 ms | 55.44 ms | 1.37× |

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
| 24bpp Truecolor | 18.31 ms | 16.89 ms | 1.08× |
| 32bpp RGBA | 24.59 ms | 21.99 ms | 1.12× |
| 48bpp (16-bit) Truecolor | 8.01 ms | 3.44 ms | 2.33× |
| 8bpp Grayscale | 10.31 ms | 9.34 ms | 1.10× |
| Interlaced (Adam7) Truecolor | 29.19 ms | 27.60 ms | 1.06× |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 122.08 ms | 148.98 ms | **0.82×** |
| 32bpp RGBA | 148.28 ms | 225.23 ms | **0.66×** |
| 8bpp Grayscale | 49.96 ms | 43.41 ms | 1.15× |
| Low-color graphic (16 colors), indexed | 6.08 ms | 4.09 ms | 1.49× |

Indexed-color (PLTE) encoding (issue #23) is a new capability, not yet held to the 10% throughput
target the other rows track — its point is file size, not speed. On the low-color scenario above
(640x480, 16 distinct colors), `PngEncoderOptions.ColorMode`'s default `Auto` mode automatically
quantizes to a palette and produces a **1,187-byte** file, vs. **2,243 bytes** encoding the same
source as truecolor (this encoder, `ColorMode = Truecolor`) and **2,229 bytes** from SkiaSharp's
`SKBitmap.Encode` — confirmed empirically that SkiaSharp's PNG encoder does not itself select indexed
color for this source (its output stays color type 2/truecolor regardless of the source's color
count), so the file-size comparison is PeachImage-only.

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
| Animated, all frames (24×, 320×240) | 4.63 ms | 0.54 ms | **8.59×** | 8.1 MB |

This is the largest gap measured anywhere in this document — everything else stays under ~2.3×.
It was already present in the benchmark suite before animated WebP decode was added in the same
session that first ran and published this number, so there's no earlier recorded baseline to
confirm whether it's a longstanding cost or a regression from later GIF decode changes; it's flagged
here as a real gap worth a dedicated investigation, not something to read past. The static (non-animated)
GIF decode scenarios in `GifDecodeBenchmarks.cs` (low-color graphic, dithered photographic) haven't
been run into this document yet either.

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
| Animated, all frames (24×, 320×240) | 1.48 ms | 4.07 ms | **0.36×** | 9.1 MB |

The animated scenario is the strongest result in this whole document: PeachImage decodes it roughly
**2.8× faster** than SkiaSharp, consistent across repeated runs. Same explanation as the lossless
small-image encode case below — SkiaSharp pays fixed per-call native marshaling overhead on every one
of the 24 `GetPixels` calls, while PeachImage's decode is pure managed code with lower fixed
per-frame cost. Both libraries' per-frame allocation strategy differs sharply (9.1 MB vs SkiaSharp's
~2.4 KB): `WebpFrameCompositor`/GIF's `GifFrameCompositor` each allocate a fresh full-canvas `Image`
per frame rather than reusing one buffer in place the way SkiaSharp's benchmark loop does — a real,
shared design cost across both animated formats, though evidently not what's driving GIF's much
larger 8.59× gap above, since WebP has the same allocation pattern and still comes out ahead.

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

`Vp8ImageEncoder` (issue #21) is a v1 encoder: SAD-only mode decision (no rate-distortion search),
a linear quality→quantizer mapping, and no coefficient-probability adaptation — see that PR's
description for the full list of deferred refinements. Despite that, it already beats SkiaSharp's
real-libwebp encoder on throughput at the same quality setting:

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Photographic (quality 75) | 99.10 ms | 112.57 ms | **0.88×** | 51.9 MB |

The allocation gap (52 MB vs SkiaSharp's ~1 KB) is the encoder's own unoptimized per-macroblock
scratch allocations (a fresh `short[16]`/`short[16][]` per block, per macroblock) rather than
anything inherent to the algorithm — libwebp's encoder works in unmanaged memory throughout, so its
managed allocation is near zero regardless of image size. Pooling those scratch buffers is a real
follow-up, not attempted here since it wasn't necessary to beat SkiaSharp on wall-clock time.

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

## Summary

| Format | Decode | Encode |
|---|---|---|
| JPEG | 1.16×–1.37× | 1.18×–1.35× |
| BMP | 0.38×–1.04× | no baseline (PeachImage-only) |
| GIF | **8.59× (animated only measured so far)** | no SkiaSharp baseline (PeachImage-only) |
| PNG | 1.06×–2.33× | 0.66×–1.15× |
| WebP | 0.36×–2.17× (animated: **0.36×**, static: 1.12×–2.17×) | 1.01×–1.44× lossless (0.16× small-image outlier), 0.88× lossy |
| AVIF | ~2.12× vs. `ffmpeg` (no SkiaSharp baseline available) | implemented (fixed 8x8 blocks, no partition-tree RDO yet); throughput not yet measured here |

BMP is fully within target and often faster. PNG meets or is close to target for every 8-bit scenario
and beats SkiaSharp outright on encode for truecolor/RGBA; its remaining gap is concentrated in the
16-bit decode path. JPEG has the largest gap on both sides among the mature formats. WebP's static
decode and AVIF are the furthest from the 10% target on large images, but WebP's *animated* decode is
actually the single best result in this document (SkiaSharp's fixed per-frame native marshaling
overhead losing badly to PeachImage's managed decode loop). GIF's animated decode is the worst result
in this document by a wide margin and is flagged in its own section above as needing dedicated
investigation, not yet explained.
