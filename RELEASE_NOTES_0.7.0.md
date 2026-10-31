# PeachImage 0.7.0

Safe ingestion of untrusted uploads. Every new option defaults to the previous behaviour, so existing callers are unaffected.

## New options

| Option | Default | What it does |
| --- | --- | --- |
| `DecoderOptions.AllowedFormats` | `null` (all formats) | Names of the formats `Image.Load` / `Image.Identify` may accept. Anything else throws `UnsupportedFormatException` before its decoder runs. |
| `DecoderOptions.SkipMetadata` | `false` | Return no profiles (EXIF, XMP, IPTC, ICC, text) and no location; the PNG decoder does not parse them. |
| `DecoderOptions.MaxPixels` | `null` (format ceilings only) | Refuse an image whose header declares more pixels (`ImageTooLargeException`). |
| `DecoderOptions.MaxExpansionRatio` | `null` | Refuse an image that would decode to more than this many bytes per byte of file (`ImageTooLargeException`). |
| `EncoderOptions.IncludeIccProfile` | `null` (follows `IncludeMetadata`) | Write or drop the ICC profile independently of `IncludeMetadata`. `true` makes the JPEG encoder write `APP2` ICC segments (it wrote none before). |
| `ResizeOptions.AlwaysCopy` | `false` | `Resize` always returns a new, independently owned image, even when `ResizeMode.Max` would return the source. |
| `ResizeOptions.CopyMetadata` | `false` | The resized image carries the source's resolution and profiles (a resize previously dropped them, ICC included). |

## New members

- `ImageInfo.HasLocation`, `ImageInfo.ExifLocation` (trailing optional record parameters) and `ImageMetadata.HasLocation`, `ImageMetadata.ExifLocation`: whether EXIF GPS, XMP `GPSLatitude`/`GPSLongitude` or IPTC City/Sub-location/Province-State name a place. Read from JPEG, PNG, WebP, TIFF, GIF, AVIF and JPEG XL without decoding pixels.
- `ExifLocation`, `UnsupportedFormatException`, `ImageTooLargeException`.
- `MetadataProfileKind.Iptc`: a JPEG `APP13` Photoshop block (with its `Photoshop 3.0` signature) is now returned in `Image.Metadata.Profiles`. No encoder writes it, so it never reaches an output file.
- A PNG `eXIf` chunk is now returned as an `Exif` profile in `Image.Metadata.Profiles`. Consequence: converting such a PNG to JPEG XL (or saving it to WebP) with the default `IncludeMetadata = true` now carries its EXIF, GPS included, into the output; set `IncludeMetadata = false` to drop it.

## Behaviour notes

- **Encoders now write EXIF and ICC by default.** With the default `IncludeMetadata = true`, a JPEG encode writes the image's EXIF (`APP1`) and ICC profile (`APP2`), PNG writes `eXIf`, and AVIF writes an `Exif` item (with matching `irot`/`imir` transforms for the orientation) and a `colr` ICC property; WebP and JPEG XL already did. Previously JPEG and AVIF wrote neither. To get the old output, set `IncludeMetadata = false` (and `IncludeIccProfile = true` to keep just the colour profile). XMP is written too (below).
- **XMP** is now decoded and encoded wherever the format has a slot. It is returned as `MetadataProfileKind.Xmp` holding the bare packet from JPEG (`APP1`; previously filed under `Exif`), PNG (`iTXt` `XML:com.adobe.xmp`, compressed or not; previously a `Text` entry) and TIFF (tag 700), as well as WebP, AVIF and JPEG XL as before. It is written by JPEG, PNG, WebP, AVIF (a `mime` item) and JPEG XL, and dropped by `IncludeMetadata = false`. GIF is covered too: the `XMP DataXMP` application extension is read (before the first frame) and written (for a still image, after the colour table) with its magic trailer, so readers that do not know XMP skip it. JPEG extended XMP (packets over 64 KB) is neither read nor written.
- GIF, BMP and animated WebP write no metadata (no usable slot, or no source for it).
- `Image.ApplyOrientationInPlace` now replaces an EXIF profile in `Image.Metadata` with a copy whose orientation tag is 1, as `ApplyOrientation` already did.
- `Image.Load` has always decoded only the first frame of an animated GIF or WebP; this is now documented and tested. There is no `MaxFrames`; use `AnimatedImage.Load` for more frames.
- `Image.Identify` reads the whole stream of a non-seekable JPEG/PNG/WebP/TIFF into memory to scan its metadata.
