# PeachImage 0.7.1

Gaps found while replacing ImageSharp in a web API that decodes untrusted uploads. Every new option defaults to the previous behaviour, so existing callers are unaffected.

## 1. Bounded parallelism

| Option | Default | What it does |
| --- | --- | --- |
| `DecoderOptions.MaxDegreeOfParallelism`, `EncoderOptions.MaxDegreeOfParallelism`, `ResizeOptions.MaxDegreeOfParallelism` | `-1` (up to `Environment.ProcessorCount`, as before) | The most threads one call may use. `1` runs the whole call on the calling thread. Output is identical for every value. |
| `DecoderOptions.TaskScheduler`, `EncoderOptions.TaskScheduler`, `ResizeOptions.TaskScheduler` | `null` (thread pool) | Runs the call's parallel work on this scheduler, so a host can share one limit across concurrent calls. |
| `Image.ConvertTo(PixelFormat, int maxDegreeOfParallelism, TaskScheduler? = null)` | n/a | The same limit for pixel-format conversion, which has no options object. |

- The limit is **per call, not global**: ten concurrent calls with `MaxDegreeOfParallelism = 4` can use 40 threads. To cap the total, give every call the same scheduler, for example the `ConcurrentScheduler` of `new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxConcurrencyLevel: 4)`.
- Applies to JPEG decode (IDCT, chroma upsampling, colour conversion), resize, pixel conversion, JPEG XL, JPEG 2000 and AVIF decode and encode. `AvifEncoderOptions.MaxDegreeOfParallelism` moved to the `EncoderOptions` base class (source and binary compatible) and now also bounds the AVIF loop-filter searches.
- For a server: set `MaxDegreeOfParallelism` to a low value (1 to 2) on the decoder and resize options.

## 2. Scaled decode and upright decode

| Option | Default | What it does |
| --- | --- | --- |
| `DecoderOptions.ScaleDenominator` | `1` | Decode at 1/2, 1/4 or 1/8 size (`ceil(size / N)`). |
| `DecoderOptions.TargetSize` | `null` | Pick the largest reduction that still leaves an image at least this big (a 6000x4000 JPEG with `(600, 420)` decodes at 750x500). |
| `DecoderOptions.ApplyOrientation` | `false` | Return the image upright: the EXIF orientation is applied to the pixels (once, on the already reduced image) and reset in the EXIF profile. |

- JPEG scales in the DCT domain (baseline and progressive), so a reduced decode skips most of the IDCT, upsampling and colour-conversion work and never allocates the full-size planes. Formats without a native reduction decode at full size and are then box-averaged down, so the result size is the same for every format (no CPU saved there). JPEG 2000 and JPEG XL currently take that second path; native reduction for them is not in this release.
- `MaxPixels` and `MaxExpansionRatio` are checked against the size the header declares, before anything is allocated. `ImageInfo` reports the declared size.
- With `ApplyOrientation = true`, `TargetSize` is compared against the upright size. Without it, orientation is not applied and `TargetSize` compares against the stored size.
- A reduced decode is close to, but not bit-identical to, resizing the full decode.

## 3. CMYK and YCCK without a colour profile

- `Image.ConvertTo` now converts `Cmyk32` and `Ycck32` to `Rgb24`, `Rgba32` and `Gray8`, using `R = 255 x (1 - C) x (1 - K)` (likewise G and B; channels scaled to 0..1, 0 meaning no ink). It is an approximation of how the inks look, not a colour-managed conversion. Nothing converts into CMYK or YCCK.
- `Image.ConvertToSrgb()` falls back to the same formula when a CMYK image has no usable ICC profile (it used to return the CMYK image unchanged). The result is `Rgba32`, as it is with a profile. New overload `Image.ConvertToSrgb(IccColorProfile? fallbackCmykProfile)` and `DecoderOptions.FallbackCmykProfile` let a host supply its own CMYK profile for images that have none; nothing is bundled.
- The JPEG decoder already de-inverts Adobe CMYK (unless `KeepAdobeCmykInverted`) and converts YCCK to standard CMYK (unless `DecodeRawYcck`), so both reach `ConvertTo` / `ConvertToSrgb` as ordinary `Cmyk32`. With `DecodeRawYcck = true` the image is `Ycck32`, which `ConvertTo` and `ConvertToSrgb` also accept (they undo the YCbCr transform first).
- `DecoderOptions.TargetPixelFormat = Rgba32` on a profile-less CMYK JPEG or TIFF keeps its existing additive conversion (`R = 255 - min(255, C + K)`); use the decode followed by `ConvertTo` for the multiplicative one.

## 4. Decompression-bomb guard

- **The ratio is now defined precisely**: decoded pixel bytes (width x height x bytes per pixel of the decoded format) divided by compressed bytes (stream length from the image start). Realistic per-format guidance is in the `DecoderOptions.MaxExpansionRatio` documentation: photographic JPEG expands 3 to 30 times (a ceiling of 100 to 200 is generous), while flat lossless graphics reach tens of thousands (this library writes a flat 400x400 RGBA PNG at about 5,500x and a flat 2000x2000 one at about 27,000x).
- **New `DecoderOptions.MaxExpansionRatios`** (`IReadOnlyDictionary<string, double>`, keyed by format name such as `"jpeg"`, `"png"`, case-insensitive) overrides `MaxExpansionRatio` per format. A tight JPEG limit and a loose PNG limit can now coexist, so the global 20,000 workaround can go.
- **Cheap early refusal**: a baseline JPEG whose header claims more blocks than its remaining bytes could hold (an 808-byte file edited to say 8000x8000) throws `JpegDecodingException` (a `JpegFormatException`, the JPEG decoder's one failure type) before any coefficient buffer is allocated, even with no limits set (seekable streams). With a ratio set, the header check refuses it with `ImageTooLargeException` first. Truncated entropy-coded data now fails fast with `JpegDecodingException` instead of decoding zero blocks across the claimed canvas.
- `AnimatedImage.Load` now applies `MaxPixels` and the ratio limits too.
