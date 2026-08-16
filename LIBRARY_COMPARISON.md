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

**Environment**: BenchmarkDotNet v0.15.8, Windows 11, Intel Core i9-14900K (24 physical / 32 logical
cores), .NET 10.0.11 (SDK 10.0.400), X64 RyuJIT x86-64-v3, AVX2-capable.

## JPEG

Both libraries decode/encode from the same source files; JPEG encode uses explicit 4:2:0/4:4:4 chroma
subsampling on both sides (`JpegEncoderOptions.Subsampling` / `SKJpegEncoderOptions.Downsample`) at
quality 85.

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

## BMP

SkiaSharp's encoder doesn't support BMP output, so encode has no SkiaSharp baseline.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio |
|---|---:|---:|---:|
| 24bpp Truecolor | 2.19 ms | 2.27 ms | **0.96×** |
| 32bpp Alpha | 3.14 ms | 7.94 ms | **0.40×** |
| 8bpp Indexed | 2.15 ms | 2.04 ms | 1.05× |
| 8bpp Indexed, RLE | 5.77 ms | 8.00 ms | **0.72×** |

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

## WebP

Decode covers both of WebP's bitstream codecs (VP8 lossy, VP8L lossless), with and without alpha,
plus a small-image scenario. Encode currently produces the lossless (VP8L) bitstream only — VP8
(lossy) encode and animation are not yet implemented.

### Decode

| Scenario | PeachImage | SkiaSharp | Ratio | Allocated |
|---|---:|---:|---:|---:|
| Lossless, Photographic | 42.76 ms | 24.89 ms | **1.72×** | 10.9 MB |
| Lossy, Photographic | 29.15 ms | 13.71 ms | **2.13×** | 6.9 MB |
| Lossless, Graphic (flat color) | 0.66 ms | 0.35 ms | 1.87× | 0.96 MB |
| Lossy, Alpha | 32.48 ms | 19.13 ms | **1.70×** | 11.1 MB |
| Lossless, Alpha | 44.03 ms | 24.86 ms | **1.77×** | 13.0 MB |
| Small image (32×24) | 14.21 µs | 12.34 µs | **1.15×** | 44 KB |

### Encode (lossless)

`WebpEncodeBenchmarks` covers the same photographic/alpha/graphic/small-image scenarios against
SkiaSharp's lossless WebP encoder; results haven't been captured here yet — run the benchmark
project locally (`dotnet run -c Release --project bench/PeachImage.Benchmarks --filter "*Webp*"`)
to populate this table.

## AVIF

Decode-only, for baseline still images (single or grid-composited item, 8/10-bit, with or without
alpha). **No SkiaSharp baseline**: this repo's pinned SkiaSharp version doesn't decode AVIF. In its
place, the table below reports `ffmpeg -c:v libdav1d`/`libaom` process-spawn timing as context, not a
directly comparable BenchmarkDotNet row — `ffmpeg`'s number includes process-startup overhead and is a
decade-tuned native decoder, not a peer to benchmark parity against the way SkiaSharp is elsewhere in
this document.

### Decode

| Scenario | PeachImage | `ffmpeg` (context only) | Allocated |
|---|---:|---:|---:|
| Photographic, 8-bit 4:2:0 | 140.9 ms | 68.6 ms | 17.8 MB |
| Photographic, 8-bit 4:2:0 + alpha | 159.4 ms | — | 19.8 MB |
| Small image (32×24) | 113.4 µs | — | 213 KB |

PeachImage is roughly **2.05×** `ffmpeg`'s process-spawn-inclusive time on the 1080p scenario.

## Summary

| Format | Decode | Encode |
|---|---|---|
| JPEG | 1.13×–1.30× | 1.20×–1.43× |
| BMP | 0.40×–1.05× | no baseline (PeachImage-only) |
| PNG | 1.08×–1.63× | 0.65×–1.27× |
| WebP | 1.15×–2.13× | not yet benchmarked (lossless-only encode implemented) |
| AVIF | ~2.05× vs. `ffmpeg` (no SkiaSharp baseline available) | not yet implemented |

BMP is fully within target and often faster. PNG meets or is close to target for every 8-bit scenario
and beats SkiaSharp outright on encode for truecolor/RGBA; its remaining gap is concentrated in the
16-bit decode path. JPEG has the largest gap on both sides among the mature formats. WebP and AVIF are
the newest formats and furthest from the 10% target on large images.
