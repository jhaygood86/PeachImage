# PeachImage vs. SkiaSharp

Performance comparison of PeachImage against [SkiaSharp](https://github.com/mono/SkiaSharp) (a
mature, real-world, native-backed image library) for JPEG, BMP, PNG, and WebP decode/encode
throughput. SkiaSharp is used as a single consistent baseline across all formats — it's also the
corpus tests' differential oracle (see the [README](README.md)).

**Target**: PeachImage's Mean within 10% of SkiaSharp's Mean (`Ratio` column ≤ ~1.10) for every
scenario. Ratios below 1.00 mean PeachImage is *faster* than SkiaSharp.

## How to reproduce

```bash
dotnet run -c Release --project bench/PeachImage.Benchmarks -- --filter "*" --job short
```

Results below use BenchmarkDotNet's `ShortRun` job (3 warmup + 3 measured iterations) for a quick,
repeatable snapshot rather than the (much slower) statistically-tighter default job — treat the
`Error`/`StdDev` columns in the full BenchmarkDotNet output as real when reading precision into these
numbers; a longer run will narrow them further without materially changing the story.

**Environment**: BenchmarkDotNet v0.15.8, Windows 11, Intel Core i9-14900K (24 physical / 32 logical
cores), .NET 10.0.11 (SDK 10.0.400), X64 RyuJIT x86-64-v3, AVX2-capable.

## JPEG

Both libraries decode/encode from the same source files; JPEG encode uses explicit 4:2:0/4:4:4 chroma
subsampling on both sides (`JpegEncoderOptions.Subsampling` / `SKJpegEncoderOptions.Downsample`) at
quality 85, for an apples-to-apples comparison.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 1080p, 4:2:0 | 12.50 ms | 9.66 ms | 1.29× |
| 1080p, 4:4:4 | 14.78 ms | 11.46 ms | 1.29× |
| 1080p, Grayscale | 6.72 ms | 5.95 ms | 1.13× |
| 12MP, 4:2:0 | 72.73 ms | 56.00 ms | 1.30× |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 1080p, 4:2:0 | 27.83 ms | 19.45 ms | 1.43× |
| 1080p, 4:4:4 | 32.88 ms | 27.42 ms | 1.20× |

JPEG is the furthest from the 10% target on both sides. PeachImage's IDCT/color-conversion kernels
are already SIMD-vectorized (`Vector128`/`Vector256`, runtime-dispatched), but the entropy coding
(Huffman decode/encode) and the general decode/encode orchestration are not — that's the most likely
remaining gap versus SkiaSharp's native, decades-optimized libjpeg-derived codec.

## BMP

BMP encode has no SkiaSharp baseline: SkiaSharp's encoder doesn't support BMP output at all (confirmed
empirically — it returns null), so `BmpEncodeBenchmarks` only tracks PeachImage's own throughput,
below.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 2.19 ms | 2.27 ms | **0.96×** |
| 32bpp Alpha | 3.14 ms | 7.94 ms | **0.40×** |
| 8bpp Indexed | 2.15 ms | 2.04 ms | 1.05× |
| 8bpp Indexed, RLE | 5.77 ms | 8.00 ms | **0.72×** |

BMP decode is PeachImage's strongest showing — it meets the target on every scenario and is
meaningfully *faster* than SkiaSharp on alpha-bearing and RLE-compressed images.

### Encode (PeachImage only — no SkiaSharp baseline)

| Scenario | PeachImage Mean |
|---|---:|
| 24bpp Truecolor | 3.84 ms |
| 32bpp Alpha | 4.79 ms |
| 8bpp Indexed | 0.46 ms |
| 8bpp Indexed, RLE | 3.88 ms |

## PNG

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 18.14 ms | 16.79 ms | 1.08× |
| 32bpp RGBA | 24.51 ms | 21.78 ms | 1.13× |
| 48bpp (16-bit) Truecolor | 5.51 ms | 3.37 ms | 1.63× |
| 8bpp Grayscale | 10.25 ms | 9.26 ms | 1.11× |
| Interlaced (Adam7) Truecolor | 29.34 ms | 26.64 ms | 1.10× |

### Encode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 122.51 ms | 149.46 ms | **0.82×** |
| 32bpp RGBA | 148.49 ms | 228.15 ms | **0.65×** |
| 8bpp Grayscale | 54.36 ms | 42.94 ms | 1.27× |

PNG decode is close to the target for the common 8-bit non-interlaced cases (8-13% over) via a
direct-copy fast path for grayscale/truecolor/truecolor+alpha at 8-bit depth (PNG's on-disk byte
layout already matches PeachImage's in-memory pixel layout exactly there, so no bit-unpacking or
per-sample scaling is needed). The 16-bit case is the outlier, since it still routes through the
slower general-purpose sample-resolution path. PNG encode already *beats* SkiaSharp on the two most
common real-world cases (truecolor, RGBA) — both filtering (all 5 PNG filter types) and the
direct-copy decode paths are SIMD-vectorized via `System.Numerics.Vector<byte>`.

## WebP

Decode-only (see the [WebP codec plan](https://github.com/jhaygood86/PeachImage) — encode and
animation are deferred to a later phase). Covers both of WebP's unrelated bitstream codecs (VP8
lossy, VP8L lossless), with and without alpha (`ALPH` chunk / VP8L's own alpha), plus a small-image
scenario. Assets were generated locally via SkiaSharp's own WebP encoder (`SKWebpEncoderOptions`),
since no `cwebp`-produced fixtures were available in this environment — correctness itself is
independently verified separately by `WebpCorpusTests`, which decodes 131 real libwebp-project test
files and diffs pixel output (including an *unpremultiplied* alpha comparison) against SkiaSharp.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated (PeachImage) |
|---|---:|---:|---:|---:|
| Lossless, Photographic | 65.25 ms | 25.52 ms | 2.56× | 10.9 MB |
| Lossy, Photographic | 65.24 ms | 14.26 ms | 4.58× | 6.9 MB |
| Lossless, Graphic (flat color) | 0.83 ms | 0.35 ms | 2.36× | 2.2 MB |
| Lossy, Alpha | 70.18 ms | 19.33 ms | 3.63× | 17.3 MB |
| Lossless, Alpha | 70.13 ms | 26.07 ms | 2.69× | 13.0 MB |
| Small image (32×24) | 15.46 µs | 12.63 µs | 1.22× | 44 KB |

**WebP decode does not meet the 10% target** — it is the furthest from it of any format in this
repo, by a wide margin. Two contributing factors were investigated, in likely order of impact:

1. **Allocation/GC pressure** — addressed, partially. The VP8L ARGB working buffer (the dominant
   large-object-heap allocation: ~8 MB for a 1080p image) is now rented from a dedicated
   `WebpBufferPool.SharedUInt32` pool instead of freshly allocated on every decode, for both the main
   image path and the `ALPH` chunk's lossless-alpha substream path; a redundant defensive copy of the
   whole VP8 chunk was also removed. This measurably cut allocation by 30–45% and Gen2 collections by
   roughly half across every scenario (e.g. Lossless-Photographic: 19.2 MB → 10.9 MB allocated, 500 →
   0 Gen2 collections per 1000 ops) — but **wall-clock time barely moved** (Ratio changes are within
   run-to-run noise on the `ShortRun` job used here). This confirms allocation wasn't actually the
   dominant cost; it was a real, worthwhile fix (less GC pressure is good on its own merits, and a
   longer/production benchmark run would likely show a small but real time improvement from it), but
   it was not the lever that closes this gap.
2. **Scalar-only hot loops** — not yet addressed, and now confirmed (by elimination) to be the actual
   dominant cost. The VP8 boolean-arithmetic decoder and VP8L's Huffman/LZ77 decode are both
   implemented as straightforward scalar loops (matching this codebase's existing precedent — GIF's
   LZW decoder and JPEG's Huffman decode are similarly scalar, since bit-level entropy decode is
   inherently sequential). SkiaSharp's libwebp backend runs the equivalent loops through decades of
   hand-tuned, SIMD-assisted C. Only a few of VP8L's transform steps (subtract-green, the predictor's
   pure-Top mode) are currently vectorized; VP8's loop filter (a genuinely SIMD-friendly, non-bit-level
   pass) is also still scalar.

Closing the remaining gap needs `dotnet-trace`/sampling-profiler evidence of exactly which loop
dominates (the bool decoder and the coefficient/Huffman token-tree walks are the leading suspects) before
investing in either a wider bit-reader refill (libwebp's own technique) or hand-vectorizing the loop
filter — the same measure-before-optimizing discipline already applied to JPEG's/GIF's entropy coding
in this codebase. This is substantially more involved than the allocation pass above and hasn't been
started.

## Summary

| Format | Decode | Encode |
|---|---|---|
| JPEG | 1.13×–1.30× | 1.20×–1.43× |
| BMP | 0.40×–1.05× | no baseline (PeachImage-only) |
| PNG | 1.08×–1.63× | 0.65×–1.27× |
| WebP | 1.22×–4.58× | not yet implemented |

BMP is fully within target and often faster. PNG meets or is close to target for every 8-bit scenario
and beats SkiaSharp outright on encode for truecolor/RGBA; its remaining gap is concentrated in the
16-bit decode path. JPEG has the largest gap on both sides and is the best next target for further
optimization work among the mature formats (entropy coding is the most likely place to start). WebP
is newest and furthest from target overall — correctness is solid (see `WebpCorpusTests`), but
performance work (allocation reduction first, then targeted SIMD) hasn't started yet.
