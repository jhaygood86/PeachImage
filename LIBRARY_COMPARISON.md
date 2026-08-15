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

Unlike the sections above, these numbers come from a **5 warmup + 20 measured iteration** job rather
than `ShortRun`, with the benchmark process pinned to P-cores. `ShortRun`'s Error bars on these
scenarios reached ±25 ms — wider than several of the individual improvements below — so it cannot
resolve them. Every figure here has a StdDev under 1.2%.

| Scenario | PeachImage | SkiaSharp | Ratio | Was | Allocated (PeachImage) |
|---|---:|---:|---:|---:|---:|
| Lossless, Photographic | 42.84 ms | 24.05 ms | **1.78×** | 2.58× | 10.9 MB |
| Lossy, Photographic | 29.90 ms | 14.00 ms | **2.14×** | 4.11× | 6.9 MB |
| Lossless, Graphic (flat color) | 0.68 ms | 0.35 ms | 1.93× | 2.08× | **0.96 MB** (was 2.2 MB) |
| Lossy, Alpha | 32.89 ms | 19.42 ms | **1.69×** | 3.34× | **11.1 MB** (was 17.3 MB) |
| Lossless, Alpha | 43.91 ms | 25.12 ms | **1.75×** | 2.63× | 13.0 MB |
| Small image (32×24) | 13.66 µs | 12.36 µs | **1.10×** | 1.30× | 44 KB |

The "Was" column is the same benchmark on the same job before the whole optimization pass described
below, not the `ShortRun` figures previously published here. Decode time fell 30–52% on the four
large-image scenarios. None of the six meet the 10% target on this particular run — `Small image` sits
right at the boundary and moves across it run to run, which the next paragraph addresses directly
rather than reading a story into it — and the remaining gap is characterized honestly at the end of
this section.

Two allocation sources were eliminated by reading the pipeline rather than profiling wall-clock time —
each was a buffer built, copied from once in full, and discarded. Lossy-with-alpha decode used to build
a whole RGB24 image and then a second whole-image pass to widen it into RGBA32; the RGB24 buffer now
never exists — each row is converted into a small pool-rented scratch row and spliced with that row's
alpha byte straight into the final buffer as it is produced. VP8L's color-indexing (palette) transform
always allocates a fresh, full-width buffer (the one place left that couldn't just reuse the existing
pooled buffer, since the transform changes both content and width) — it was `new uint[...]` regardless
of the caller's pooling intent, on every decode. Both are now pool-rented. `Lossless-Graphic` is a flat
370-byte file — exactly what triggers color-indexing in a real encoder — so it lost more than half its
allocation (2.19 MB → 0.96 MB); `Lossy-Alpha` lost the discarded RGB24 intermediate (17.3 MB → 11.1 MB,
-36%). Wall-clock moved too, as a side effect of less memory traffic rather than a targeted goal
(Lossless-Graphic -2.5%, Lossy-Alpha -2%). `Small image`'s allocation is unchanged byte-for-byte
(44,480 B both before and after) — this pass never touched its code path at all — so its 1.12× → 1.10×
move is noise, not a result, and is recorded as such rather than credited to work that didn't reach it.

A measurement caveat worth recording: `Lossless-Photographic` is the one scenario that occasionally
reports 15% high with a 8–10% StdDev instead of its usual ~1%. It allocates 10.9 MB per operation and
runs Gen2 collections throughout, so it is the most sensitive to whatever else the machine is doing.
The figures above are from a run where every scenario's StdDev was ≤1.2%, cross-checked against a
separate decode-loop harness (44.9–45.4 ms across 5 repeats as of the current code — this harness runs
without BenchmarkDotNet's per-iteration overhead calibration, so it reads consistently higher than the
BDN mean rather than matching it, which is expected and fine as a cross-check). Discard any single run
where that scenario's error bar blows out.

#### What the profile actually showed

The previous round of work had established, by elimination, that allocation was not the dominant cost
(pooling the VP8L ARGB buffer cut allocation 30–45% and moved wall-clock barely at all). This round
started by producing the sampling-profiler evidence that note called for — a `profile` mode on the
benchmarks executable that decodes one asset in a tight loop, traced with `dotnet-trace`. Two of the
seven findings contradicted the standing hypotheses:

- **The in-loop deblocking filter was the largest single bucket in lossy decode at 32%**, not the
  3–6% assumed. It had been treated as a secondary target behind entropy decode. Both of its edge
  orientations and both edge widths are now vectorized, which took it to 17%.
- **VP8L's predictor transform was 37% of lossless decode**, and instrumenting which of its 14 modes
  actually run showed that mode 11 (`Select`) accounted for **100%** of that work on the photographic
  asset. Sampling alone had hidden this, attributing most of it to the caller it partly inlines into.
- The VP8L bit reader's bulk-refill fast path was gated on `_bitCount == 0` *exactly*, a condition
  that essentially never holds after a Huffman code of arbitrary length is skipped — so it was close
  to dead code and nearly every refill ran a byte-at-a-time loop.
- `ProduceRgbFrame` was 23%, spending it on ~2 million interface dispatches and ~4 million per-pixel
  upsampler calls per frame.
- The coefficient probability table was a `byte[4,8,3,11]`, indexed two or three times per decoded
  coefficient bit — a CLR multidimensional array cannot be spanned and costs a bounds check per rank.
- `Vp8BoolDecoder`, underneath every bit of every lossy file, was still RFC 6386's reference decoder:
  renormalization doubled the range one bit at a time in a loop, refilling one byte every eighth
  iteration. libwebp does the same work with a single count-leading-zeros shift and a 56-bit bulk
  refill.

Each of those was fixed, in that order of measured cost, re-measuring after each change. The
commit history carries the per-change numbers.

#### Remaining gap

The post-change profile puts lossy decode at 41% coefficient/entropy decode, 20% upsample+convert,
17% loop filter, 7% inverse DCT; and lossless at 69% pixel stream (Huffman/LZ77/colour cache), 24%
predictor transform. Entropy decode dominating is the expected end state — it is the one part nobody,
including libwebp, vectorizes. Three distinct reasons remain, in decreasing size:

1. **Entropy decode is inherently sequential**, and it is now the dominant cost on both codecs. The
   VP8 boolean decoder and VP8L's Huffman/LZ77 walk cannot be vectorized — libwebp does not vectorize
   them either, and the VP8 side now runs libwebp's own algorithm. What remains is that C gets bounds
   checks off every table lookup for free and keeps the bit-reader registers in registers. Two of the
   three managed equivalents identified for this are done: `Vp8LHuffmanTable`'s per-pixel decode now
   hardcodes its root width as a compile-time constant instead of reading an instance field, letting
   RyuJIT prove the root-table index stays in range; and `Vp8LBitReader.SkipBits` — the single hottest
   call in VP8L decode — no longer writes a running counter on every call, deriving the same
   truncation check from state `Refill` already updates roughly 8× less often. Together these cut
   2.7–3.6% off all three lossless scenarios. Still open: flattening a `Vp8LHuffmanGroup`'s five
   tables into one array so a symbol decode is not three dependent pointer loads deep before it starts.
2. **Pipeline architecture, not kernel quality.** `Vp8FrameDecoder` makes three full-frame passes
   (reconstruct → loop filter → upsample+convert). libwebp runs a macroblock-row-band pipeline where
   all three happen while the band is still in L2. Converting is a much larger restructuring, and it
   would cost the "filters the whole frame in raster order, exactly reproducing the reference
   ordering" property that makes the current shape verifiable.
3. **Kernel coverage.** The loop filter is fully vectorized now, in both orientations and both widths.
   Still scalar: the inverse DCT's full-butterfly path (7%), VP8 intra prediction, and
   `Vp8LColorTransform`. The strided orientation's transpose is x86-only — an
   `AdvSimd.Arm64.ZipLow`/`ZipHigh` path would be the direct Arm equivalent, and until it exists Arm
   falls back to the scalar filter for that orientation and width.

`Lossless-Graphic` and `Small image` are not kernel-bound at all, which is why neither moved on
wall-clock time by much across the CPU-focused work above: profiling the 640×480 flat-colour case
shows ~82% of it in buffer management (`Buffer.MemmoveInternal` alone is 13%) against ~15% actually
decoding pixels, and every technique above targets a per-pixel loop that a 640×480-or-smaller image
barely spends time in. That CPU-time shape is still accurate — the copy work inside the color-indexing
loop is unchanged, only where its destination array comes from changed — which is why pooling that
array (see above) cut `Lossless-Graphic`'s *allocation* by more than half while its *time* moved only
2.5%: allocation and CPU time are different axes here, and this pass mostly moved the former for this
scenario. `PackPixels`' output remains un-pooled, but deliberately — it becomes the real final `Image`
buffer the caller keeps, not an intermediate, so there is nothing to pool it against.

## Summary

| Format | Decode | Encode |
|---|---|---|
| JPEG | 1.13×–1.30× | 1.20×–1.43× |
| BMP | 0.40×–1.05× | no baseline (PeachImage-only) |
| PNG | 1.08×–1.63× | 0.65×–1.27× |
| WebP | 1.10×–2.14× | not yet implemented |

BMP is fully within target and often faster. PNG meets or is close to target for every 8-bit scenario
and beats SkiaSharp outright on encode for truecolor/RGBA; its remaining gap is concentrated in the
16-bit decode path. JPEG has the largest gap on both sides among the mature formats and is the best
next target for further optimization work there (entropy coding is the most likely place to start).
WebP is newest and still furthest from target on large images, but a profile-guided pass plus a
follow-up allocation pass have together closed roughly half the gap on the lossy scenarios and closer
to half on the lossless ones (lossy 4.11× → 2.14×, lossy+alpha 3.34× → 1.69×, lossless 2.58× → 1.78×),
and the small-image case sits right at the boundary; what is left is concentrated in entropy decode, which is
inherently sequential.
