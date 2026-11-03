# PeachImage 0.7.2

**Release date:** November 3, 2026
**Full changelog:** [v0.7.1...v0.7.2](https://github.com/jhaygood86/PeachImage/compare/v0.7.1...v0.7.2)
**Release tag:** [v0.7.2](https://github.com/jhaygood86/PeachImage/releases/tag/v0.7.2)

Pixel density is now reported for JPEG, TIFF, BMP, JPEG 2000 and (through EXIF) WebP, AVIF and JPEG XL, in one documented unit. Nothing that worked before changes.

## Resolution metadata

| Member | What it does |
| --- | --- |
| `ImageMetadata.HorizontalResolution`, `VerticalResolution` | Now documented as **pixels per metre**, the unit PNG `pHYs` always used. JPEG and TIFF fill them too, with inch and centimetre densities converted. |
| `ImageMetadata.HorizontalDpi`, `VerticalDpi` (new) | The same values as pixels per inch, or `null` when the file records none. |

- **JPEG**: the JFIF density is used when its unit is dots per inch or dots per centimetre. A JFIF header that only gives a pixel aspect ratio (unit 0, which is what PeachImage itself writes) is not a resolution, so the EXIF `XResolution`/`YResolution`/`ResolutionUnit` of IFD0 is used instead. JFIF wins when both carry a density. Before this release JPEG always reported `null`.
- **TIFF**: `XResolution`, `YResolution` and `ResolutionUnit` of the first IFD; a missing unit means inch, as in the TIFF specification. Unit "none" and zero densities report `null`.
- **PNG** is unchanged (`pHYs`, pixels per metre).
- **BMP**: `biXPelsPerMeter`/`biYPelsPerMeter` of a Windows DIB header (zero means not recorded).
- **JPEG 2000**: the JP2 `res ` box, capture resolution (`resc`) before display resolution (`resd`).
- **PNG `eXIf`, WebP, AVIF, JPEG XL**: when the format has no density field of its own, the `XResolution`/`YResolution`/`ResolutionUnit` of the EXIF block (as for JPEG).
- GIF and the remaining formats record no absolute density and still report `null`.

## Behaviour notes

- A JPEG or TIFF that carries a density now has a non-null `HorizontalResolution` where it was `null` before. Code that treated `null` as "unknown" keeps working; code that copied the value into a PNG now writes a `pHYs` chunk.
