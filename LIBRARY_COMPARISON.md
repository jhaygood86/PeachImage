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
| Lossless, Photographic | 42.76 ms | 24.89 ms | **1.72×** | 2.58× | 10.9 MB |
| Lossy, Photographic | 29.15 ms | 13.71 ms | **2.13×** | 4.11× | 6.9 MB |
| Lossless, Graphic (flat color) | 0.66 ms | 0.35 ms | 1.87× | 2.08× | 0.96 MB |
| Lossy, Alpha | 32.48 ms | 19.13 ms | **1.70×** | 3.34× | 11.1 MB |
| Lossless, Alpha | 44.03 ms | 24.86 ms | **1.77×** | 2.63× | 13.0 MB |
| Small image (32×24) | 14.21 µs | 12.34 µs | **1.15×** | 1.30× | 44 KB |

The "Was" column is the same benchmark on the same job before the whole optimization pass described
below, not the `ShortRun` figures previously published here. Decode time fell 30–52% on the four
large-image scenarios. None of the six meet the 10% target on this particular run. `Small image` keeps
drifting between roughly 1.06× and 1.15× run to run with its allocation figure completely unchanged —
still a 32×24 image dominated by fixed setup cost that none of this pass's per-pixel work reaches, so
its number is noise rather than signal and is reported as such rather than read into.

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

The inverse DCT is now vectorized too — the first hardware-specific (`Sse2`-gated, not portable
`Vector128`) kernel in this pass. The scalar butterfly runs twice (once per column, once per row), and
both are lane-parallel by construction, but each pass's output has to become the next pass's lanes,
which needs a real cross-lane transpose that the portable `Vector128` API has no way to express (only
single-vector `Shuffle`, no two-vector interleave) — the same gap the loop filter's own transpose
already ran into and worked around with `Sse2.UnpackLow`/`UnpackHigh`. Every operation involved is
exact integer arithmetic, so this is provably bit-identical to the scalar form rather than approximately
equal, and is tested to exactly that standard. `Lossy-Photographic` improved 2.5% (well outside 2×
StdDev); `Lossy-Alpha` improved 1.25%, smaller and close enough to the noise floor to report as less
certain rather than as a clean win. Both are smaller than the profiled 7.5%+1.46% DCT budget would
suggest — a re-profile shows why: the kernel is small enough that the JIT inlines it fully into
`Vp8FrameDecoder.Decode`, so its own frame drops to 0.22% self-time with the rest absorbed into the
caller rather than staying separately attributable, consistent with a real but partial capture of that
budget once the double-transpose's own cost is netted out.

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

The post-change profile puts lossy decode at 39% coefficient/entropy decode, 21% upsample+convert,
17% loop filter; the inverse DCT no longer shows as a separately attributable bucket, since the
vectorized kernel is now small enough that the JIT inlines it into its caller (see above) — what's
left of it explicitly is 1.6% for the still-scalar DC-only fast path. Lossless is 69% pixel stream
(Huffman/LZ77/colour cache), 24% predictor transform. Entropy decode dominating is the expected end
state — it is the one part nobody, including libwebp, vectorizes. Three distinct reasons remain, in
decreasing size:

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
3. **Kernel coverage.** The loop filter is fully vectorized now, in both orientations and both widths,
   and so is the inverse DCT's full-butterfly path — both hardware-specific (`Sse2`-gated) rather than
   portable, since the transpose each needs (a real cross-lane matrix transpose, not a lane-wise op)
   has no equivalent in .NET's cross-platform `Vector128` API. Both are x86-only as a result; an
   `AdvSimd.Arm64.ZipLow`/`ZipHigh` path would be the direct Arm equivalent for either, and until one
   exists Arm falls back to the scalar form. Still scalar everywhere: VP8 intra prediction and
   `Vp8LColorTransform`.

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

## AVIF

Decode-only, and only for this phase's supported subset: a single (non-grid) or grid-composited item,
8- or 10-bit, with or without alpha, with the full deblock → CDEF → loop restoration filter chain
applied (see [README.md](README.md) for the exact scope boundary — animated AVIF, film grain,
gain maps, and 12-bit remain unimplemented). **There is no SkiaSharp baseline column**: this repo's
pinned SkiaSharp version (confirmed in the Phase 0 spike) doesn't decode AVIF at all. In its place,
the table below reports a supplementary `ffmpeg -c:v libdav1d`/`libaom` process-spawn timing as
context, not as a directly comparable BenchmarkDotNet row — the two measurement methodologies aren't
apples-to-apples (`ffmpeg`'s number includes process-startup overhead, and it's a mature,
hand-vectorized C decoder with over a decade of tuning behind it, not a peer to benchmark parity
against the way SkiaSharp is for the other formats here).

Assets were encoded locally via `ffmpeg -c:v libaom-av1` from this repo's existing 1920×1080
benchmark source PNGs (no AVIF-specific source assets existed), at default `libaom` settings
(deblock/CDEF/restoration all encoder-enabled, matching real-world encoder output) — correctness
itself is verified independently by the AV1 spec's own bitstream-conformance check and a real-file
corpus (see [README.md](README.md) and `Av1HeaderCorpusTests`/`AvifCorpusTests`), not by this
benchmark.

| Scenario | PeachImage | `ffmpeg` (process-spawn, context only) | Allocated (PeachImage) |
|---|---:|---:|---:|
| Photographic, 8-bit 4:2:0 | 140.9 ms | 68.6 ms | 17.8 MB |
| Photographic, 8-bit 4:2:0 + alpha | 159.4 ms | — | 19.8 MB |
| Small image (32×24) | 113.4 µs | — | 213 KB |

PeachImage is roughly **2.05×** `ffmpeg`'s process-spawn-inclusive time on the 1080p scenario — down from
an initial 6.1× after six profile-guided passes (below), and now within the same range as this repo's
more mature codecs' own remaining gaps (JPEG 1.13×–1.30×, WebP 1.15×–2.13×). Per the project plan,
AV1/AVIF performance is an explicitly aspirational, long-term goal here, not a merge gate the way it is
for the more mature formats above — WebP's own optimization arc (4.11× → 2.13× on its worst scenario,
over several profile-guided passes) was the expected shape of this work, and AVIF has now followed the
same arc in fewer passes.

### What the profile actually showed

A `dotnet-trace` sampling profile of the 1080p scenario (`PeachImage.Benchmarks.exe avif-profile
photo420 60`, a bare decode-in-a-loop harness so the trace is entirely decoder frames — see
`AvifProfileHarness.cs`) contradicted the standing assumption that entropy/coefficient decode would
dominate, the way it does for WebP's VP8:

- **CDEF was 55% of total self-time**, not the secondary cost the plan's own risk assessment expected
  (it flagged SGRPROJ, not CDEF, as the filter chain's riskiest piece). The cause wasn't algorithmic —
  it was `cdef_get_at`'s per-tap `is_inside_filter_region` availability check: ~12 taps per sample,
  each paying a property write/read plus an MI-unit bounds translation, even though the overwhelming
  majority of 8×8 blocks in a 1920×1080 frame are nowhere near the edge and every one of their taps is
  always available. Fixed by computing, once per plane per 8×8 block (not once per tap), whether the
  block's fixed ±2-sample tap footprint stays fully inside frame bounds; interior blocks (the common
  case) skip the availability check entirely, and only genuinely edge-adjacent blocks fall back to the
  original bounds-checked path. This alone cut CDEF's cost by roughly 45%.
- **17% of total time was spent zeroing memory that could never be read stale.** `Reconstruct()`
  called `Array.Clear` on a fixed 64×64 scratch buffer before every transform block's dequantization,
  regardless of that block's actual size — a 4×4 transform's worth of real work paid for clearing 16×
  more memory than it used. Tracing `Av1Dequantizer.Dequantize`'s write bounds against
  `Av1InverseTransform.Inverse2D`'s read bounds (both derive from the same `txSz`, so they're always
  exactly matched) showed the clear was fully redundant: `Inverse2D` can never read a position that
  call's `Dequantize` didn't just write. Removed entirely.

Both changes are pure performance changes — verified bit-identical to the pre-change output via
`AvifDecodeHashTests`, the same regression harness `WebpDecodeHashTests` uses, before being accepted.
A third attempt (replacing CDEF's interior-path array reads with `Unsafe.Add` to skip the JIT's own
bounds checks) measured no change and was reverted — RyuJIT had already eliminated them, so the
`unsafe`-adjacent complexity bought nothing.

Re-profiling after those two fixes surfaced the inverse transform (`Av1InverseTransform.InverseDct`,
the scalar 31-step butterfly network) as the new largest single cost. Two more findings, again from
tracing rather than assumption:

- **`cos128`/`sin128` recomputed a 3-branch case split on every call**, and `B()` (the butterfly
  rotation every one of `InverseDct`'s up to 31 steps calls) invokes both twice — up to 124 branchy
  calls per transform. Replaced with one precomputed 256-entry table covering the full `angle & 255`
  domain, turning both functions into an unconditional array lookup.
- **`brev` (bit-reversal) recomputed its own O(numBits) loop on every call**, both inside
  `InverseDctPermute`'s O(2^n) permutation loop and several of `InverseDct`'s own steps. Since every
  call site uses one of only five bit-widths (2 through 6), precomputed all five as lookup tables.
- **The row pass allocated a fresh array on every one of a block's `h` rows** (`t[..w].ToArray()`,
  needed because the transform functions take `int[]` rather than `Span<int>`). Replaced with one
  `w`-length array allocated once per `Inverse2D` call and reused/overwritten across all `h` rows — an
  h-fold reduction in both allocation count and bytes for that array. (A first attempt at this sized
  the reusable array to a fixed 64 regardless of the block's actual, often-smaller `w` — correct, but
  it *increased* measured allocation for the many blocks smaller than 64, since a right-sized array
  reused h times allocates less total memory than a max-sized array. Caught by comparing the
  benchmark's own `Allocated` column before/after, not just wall-clock time.)

All three verified bit-identical via `AvifDecodeHashTests` as before.

### Allocation-reduction pass

A third pass targeted allocation specifically, using the same `GC.GetAllocatedBytesForCurrentThread()`
delta-diagnostic technique (temporary, env-var-gated checkpoints bisecting the decode pipeline by
phase) to localize the largest contributors before touching any code, rather than guessing:

- **`Av1Cdef`'s direction search allocated ten small arrays (`cost[8]`, `partial[8][15]`) on every
  call** — once per non-skip 8×8 block, tens of thousands of times per 1080p frame. Replaced with
  reusable fields on the filter's per-frame `State`, cleared with `Array.Clear` instead of
  reallocated.
- **`Av1IntraPrediction.BuildEdges` allocated two `Av1EdgeArray` instances on every transform block.**
  Changed from a tuple-returning factory to an in-place filler over caller-owned, reused instances
  (mirroring `Av1TileDecoder`'s existing `_reconPred`/`_reconDequant`/`_reconResidual` reuse pattern).
  Four smaller, always-small, never-escaping local arrays elsewhere in the same file (`PredictRecursive`,
  `EdgeUpsample`, `EdgeFilter`, `PredictChromaFromLuma`) were converted to `stackalloc`.
- **The inverse transform's three permutation helpers (`InverseDctPermute`, `AdstInputPermute`,
  `AdstOutputPermute`) each `Clone()`d their input array on every call** — twice per row and twice per
  column of every transform block in the frame, the single largest allocation site found this pass.
  Replaced with one `[ThreadStatic]` 64-entry scratch buffer shared by all three (safe: permutation is
  always a leaf, non-reentrant operation within one thread's sequential tile decode), cutting this
  phase's own allocation from ~48 MB to ~4.4 MB per 1080p decode.
- **`Av1Cdef.Apply`'s per-plane write buffer was seeded with a full `Clone()` of the input it never
  reads back from.** Its own per-8×8-block loop unconditionally copies every input pixel into that
  buffer before any filter ever runs (identity-copied for skip blocks, overwritten by the filter
  otherwise) — the upfront clone's data was always overwritten before being read. Replaced with a
  same-sized empty allocation; correctness confirmed by the existing bit-exact corpus hash suite,
  since this is a genuine dead-work elimination rather than an approximation.
- **The pre-CDEF plane snapshot (`deblockedPlanes`) was always cloned, even though only loop
  restoration reads it, and loop restoration is a no-op — returning before touching it — whenever
  `UsesLr` is false.** Now skipped entirely in that (common) case.

Every change was verified bit-identical to the pre-change output via `AvifDecodeHashTests` before being
accepted, same as the prior two passes. Together they took the 1080p scenario's allocation from
175.5 MB to 54.4 MB (69%) and its time from 282.0 ms to 260.6 ms.

### Buffer pooling pass

What remained after the allocation-reduction pass was no longer hot-loop waste — it was a handful of
full-plane buffers (the reconstruction target, CDEF's write buffer, the tile compositor's output
canvas, the final YUV→RGB output) allocated once per decode. Each is large *because* it's a whole
plane's worth of samples, not because it's wasteful, so eliminating any of them outright wasn't an
option — spec-mandated separation between `CurrFrame`/`CdefFrame`/`LrFrame` means CDEF and loop
restoration genuinely need their own distinct buffer, not an in-place update. What they don't need is a
*fresh* buffer every decode: `ArrayPool<int>.Shared` was threaded through the whole pipeline (tile
decode → deblock → CDEF → loop restoration → grid compositing → the color and alpha composites in
`AvifDecoder.Decode`), with each stage renting a buffer, using it, and returning whatever it superseded
back to the pool once nothing else in the pipeline still needed it.

Getting the hand-off right across five files (`Av1FrameDecoder`, `Av1Cdef`, `Av1LoopRestoration`,
`Av1TileComposer`, `AvifDecoder`) took real care — a buffer can only be returned once the *last* reader
is done with it, which for the reconstruction target means "right after CDEF copies from it and swaps
in its own output," and for the pre-CDEF loop-restoration snapshot means "only after the whole
restoration pass finishes reading it." One genuine latent bug surfaced along the way in
`Av1TileComposer.CopyRegion`, which defensively clamps its copy region against each array's *actual*
length to survive a shared/reused grid tile item whose decoded dimensions don't match what the
destination expects (`color_grid_alpha_grid_tile_shared_in_dimg.avif` in the corpus, the adversarial
file this exists for) — a rented array can be larger than requested, so that clamp had to switch to the
caller-tracked logical width/height instead of `Length`, which the code already relied on elsewhere but
this one function didn't.

**A second, subtler bug took real bisection to pin down.** After wiring up the pool, the full test
suite failed on that same adversarial file — but non-deterministically, a different wrong hash on
every run, which ruled out the first suspects (a double-return, a stale reference read after return).
Disabling every `Return()` call while keeping every `Rent()` call still reproduced it, which eliminated
cross-decode buffer reuse as the cause entirely. The actual issue: unlike `new int[]`, which the CLR
always zero-initializes, `ArrayPool<T>.Rent` makes no such guarantee — and several buffers had a
padding region (the superblock-aligned canvas extends past a tile's true coded content, and a
mismatched/undersized tile's copy region can extend past its own true content within that canvas) that
was never explicitly written but had always been implicitly zero because `new int[]` provided it for
free. Once the backing array stopped being freshly OS-allocated (memory the pool had genuinely handed
back from an earlier, larger rental, containing whatever a previous decode had left there), that
padding surfaced as real garbage instead of harmless zero — for one specific adversarial file where the
copy region actually reaches into it. Fixed by explicitly `Array.Clear`-ing exactly the regions `new
int[]` used to zero for free (CDEF's write buffer and the tile compositor's output canvas), restoring
the same guarantee at the cost of a cheap linear clear instead of a fresh allocation. Confirmed by
running the full corpus suite three additional times after the fix (this class of bug is inherently
easy for a single green run to miss).

This pass didn't touch a single kernel's arithmetic — only where buffers come from — and dropped the
1080p scenario's allocation from 54.4 MB to **17.8 MB** (67% further, 90% cumulative from the session's
175.5 MB starting point), with Gen0/Gen1 GC collections during the benchmark dropping to effectively
zero once the pool warms up. Time held roughly flat (260.6 ms → 253.8 ms) as expected — this pass
targeted allocation, not CPU time.

What's left of allocation is now the per-mi-position neighbor-context arrays (mode/skip/segment/
delta-LF/CDEF-index, still freshly allocated every decode — not yet pooled) and the first-ever rental
of each buffer size the pool hasn't warmed up yet (irreducible for a one-off single decode, though
irrelevant for the repeated-decode/server-workload case this pass targeted). `int[]`-per-sample plane
storage throughout the pipeline (chosen for implementation simplicity across every intra-prediction and
reconstruction kernel while the format was being built out) rather than packed `byte`/`ushort` buffers
is the other standing simplification — it would shrink every pooled buffer by 2-4×, but touches nearly
every kernel in the decoder rather than just where its buffers come from, and is a materially larger,
riskier undertaking than this pass.

### CDEF vectorization pass

A fresh `dotnet-trace` profile after the pooling pass (the prior trace was two passes stale) found
CDEF's own filtering — `CdefFilter`, the primary/secondary directional tap accumulation, distinct from
`CdefDirection`'s direction search — at **45% of total self-time**, a far larger single hotspot than
anything else in the decoder. Two findings, in order:

- **`Constrain` (the per-tap rounding/clamp step) recomputed `FloorLog2(threshold)` on every one of up
  to 12 taps per pixel**, even though `threshold` (`priStr`/`secStr`) and `damping` are invariant for an
  entire `CdefFilter` call — only 2 distinct threshold values across up to 768 taps per 8×8 block.
  Hoisted to two precomputed `dampingAdj` values per call. This alone was a modest win (~3% faster):
  `FloorLog2`'s bit-loop is cheap enough that the redundant calls weren't the dominant cost.
- **The real cost was the sheer volume of scalar per-pixel, per-tap work**, and it turned out to be
  safely vectorizable: every CDEF block is exactly 8 samples wide (luma, or 4:4:4 chroma) or 4 samples
  wide (subsampled chroma) — matching `Vector256<int>`/`Vector128<int>` exactly — and every tap
  operation (subtract, abs, shift, compare, clamp) is plain integer arithmetic. Unlike the float-based
  YUV→RGB kernel vectorized earlier, integer SIMD ops are bit-for-bit identical to their scalar
  equivalents on any hardware, so this carries none of the cross-hardware precision risk a
  floating-point kernel would — a strictly safer vectorization target than it might first appear.
  Rewritten to process a full interior row per SIMD call (`CdefFilterRow256`/`CdefFilterRow128`),
  falling back to the untouched original scalar path for edge-adjacent blocks and non-x86 hardware.
  One correctness subtlety: scalar `Accum` always reads the tap sample and folds it into min/max even
  when `threshold == 0` (only the *sum* contribution is skipped) — the vectorized path reproduces this
  exactly without a branch, since a precomputed `dampingAdj` of 0 already makes
  `absDiff - (absDiff >> 0) == 0`, the same zero `Constrain` returns explicitly.

Verified bit-identical via `AvifDecodeHashTests` across the full corpus (which exercises both the 8-wide
and 4-wide paths, and both interior and edge blocks, across every subsampling mode). The vectorization
cut `CdefFilter`'s own self-time from 45% to under 10% of the total (roughly a 7.6× reduction in that
specific hotspot) and took the 1080p scenario from 253.8 ms to **145.1 ms** (43%) — allocation was
unchanged (17.8 MB), as expected for a pure CPU-time change.

Re-profiling afterward surfaced `Av1TileDecoder.Coeffs` (coefficient/entropy decode) as the new largest
single cost at 25% of self-time — expected, and not a good target: like WebP's own entropy decode, it's
an inherently sequential, adaptive-CDF-driven symbol stream, not parallelizable across lanes. Its one
apparent redundant-work candidate (`_quant`'s per-call `Array.Clear`) turned out, on inspection, not to
be one: unlike the earlier `_reconDequant` clear removed in an prior pass, `_quant` is read *during* the
same call for neighbor coefficient-magnitude context at positions that may not yet be written in the
current reverse-scan pass, so the clear is load-bearing, not redundant — removing it was not attempted
given the risk of silent, hard-to-detect corruption for a win that would need proving from spec details
rather than measurement.

### Sixth pass: smaller, lower-risk wins around the transform and entropy paths

With the two biggest levers (CDEF, allocation) spent, this pass swept for the same two patterns that had
worked before — redundant per-call computation, and flat elementwise loops safe to vectorize — without
attempting the still-deferred full transform-batching rewrite:

- **`GetCoeffBaseCtx` recomputed `ComputeTxType` on every coefficient for chroma planes**, even though
  `Coeffs()` had already computed the same value once and passed it to the sibling `GetCoeffBrCtx` call
  a few lines away — it just wasn't threaded through to `GetCoeffBaseCtx` too. Worse, the recomputed
  value was discarded unused entirely on the `isEob` path, which never reads it. Fixed by passing the
  already-computed `txType` in as a parameter instead of recomputing it internally.
- **`Av1TileDecoder.Reconstruct`'s final add-residual-then-clamp step** is a plain per-row contiguous
  operation for the (overwhelmingly common) non-FLIPADST case — vectorized with `Vector256<int>`,
  falling back to the original scalar loop only when a flip makes the destination a reversed-stride
  write that a single contiguous SIMD store can't express.
- **`Inverse2D`'s clamp between the row and column passes** turned out to have no per-row structure at
  all — `residual[(i*w)+j]` for `i` in `[0,h)`, `j` in `[0,w)` is just the flat range `[0, h*w)` in
  row-major order, so the nested loop collapsed to one unconditional elementwise clamp over the whole
  buffer, vectorized the same way.
- **The YUV→RGB converter's non-identity fast path was only 2-wide (`Vector128<double>`)** — widened to
  4-wide (`Vector256<double>`) first, using a runtime-indexed lane-extraction loop for the packed-pixel
  write; that regressed slightly (worse than not widening at all) because indexing a vector by a
  non-constant lane number doesn't get the same codegen as indexing by a compile-time constant. Unrolled
  back into four explicit `r[0]`/`r[1]`/`r[2]`/`r[3]`-style accesses, matching the already-proven 2-wide
  style, which fixed it.
- **`Av1TileComposer`'s output-canvas clear was unconditional**, even though it's only needed when a
  source tile doesn't fully cover the destination (the same mismatched-tile-size case the pooling pass's
  `Array.Clear` fixes exist for). For the common single-tile case, whether the tile fully covers the
  output is known before the copy runs, so the multi-megabyte clear is now skipped whenever it
  provably isn't needed — re-verified against the exact adversarial corpus file
  (`color_grid_alpha_grid_tile_shared_in_dimg.avif`) that exercises the case where it's still required.

Individually each of these was a fraction of a percent to ~1%; together they took the 1080p scenario
from 145.1 ms to **140.9 ms**. Verified bit-identical via `AvifDecodeHashTests`, with extra repeated runs
of the corpus suite specifically for the two changes touching the same buffer-coverage logic the
pooling pass's zero-init bug lived in.

#### Remaining gap

The inverse transform's butterfly network (`InverseDct`/`Inverse2D`/`InverseAdst8` and friends, ~22%
combined) remains the largest real CPU-time target, and the reasoning for deferring it hasn't changed:
a genuine win needs batching multiple independent row/column transforms across SIMD lanes (the row pass
transforms `h` independent rows through the identical butterfly network — a real, structurally-available
batching axis, not a hypothetical one) rather than vectorizing one transform's inherently-sequential
stage chain. What makes this harder than the CDEF pass isn't the vectorization itself (integer SIMD is
still bit-exact) but the sheer length and intricacy of the network being transcribed: `InverseDct` alone
is a 31-step, size-parameterized sequence of `B()`/`H()` calls with a different index/angle pair at
nearly every step, and `B()`'s intermediate products are wide enough that a faithful vectorized version
needs 4-lane `Vector256<long>` (matching the existing scalar `long` arithmetic's overflow safety) rather
than 8-lane `Vector256<int>`, for less parallelism per instruction than CDEF's kernel got. Attempting
this without being able to verify every one of those steps with high confidence risks exactly the kind
of silent, hard-to-detect corruption the rest of this work has been careful to avoid — still deferred,
not attempted, pending a pass with time budgeted specifically for that verification burden.

`CdefDirection` (the direction search preceding `CdefFilter`, ~5% of self-time) remains a smaller,
structurally similar vectorization candidate not yet attempted — its cost-accumulation pattern indexes
diagonally rather than row-wise (some of its 8 running sums *are* contiguous-in-column for a fixed row
and would vectorize cleanly, others index by `j/2` or reduce to a single scalar per row), making it a
partial, more intricate fit for the row-at-a-time approach used in `CdefFilter` rather than a direct
port. `Av1DeblockingFilter`'s per-edge sample filter (~6% combined) was inspected and not pursued: its
mask/filter-size selection is heavily data-dependent per edge-crossing line, the kind of control-flow
divergence that SIMD lanes handle poorly without a larger restructuring.

## Summary

| Format | Decode | Encode |
|---|---|---|
| JPEG | 1.13×–1.30× | 1.20×–1.43× |
| BMP | 0.40×–1.05× | no baseline (PeachImage-only) |
| PNG | 1.08×–1.63× | 0.65×–1.27× |
| WebP | 1.15×–2.13× | not yet implemented |
| AVIF | ~2.05× vs. `ffmpeg` (no SkiaSharp baseline available) | not yet implemented |

BMP is fully within target and often faster. PNG meets or is close to target for every 8-bit scenario
and beats SkiaSharp outright on encode for truecolor/RGBA; its remaining gap is concentrated in the
16-bit decode path. JPEG has the largest gap on both sides among the mature formats and is the best
next target for further optimization work there (entropy coding is the most likely place to start).
WebP is newest and still furthest from target on large images, but a profile-guided pass, a follow-up
allocation pass, and a hardware-specific DCT kernel have together closed roughly half the gap on the
lossy scenarios and closer to half on the lossless ones (lossy 4.11× → 2.13×, lossy+alpha 3.34× → 1.70×,
lossless 2.58× → 1.72×); what is left is concentrated in entropy decode, which is
inherently sequential.
