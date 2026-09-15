# Third-party attributions

PeachImage is original, from-scratch managed code with no native interop. With one disclosed exception — the
ICC color-management engine, adapted directly from a third-party MIT-licensed library, see the Wacton/Unicolour
section below — it bundles no third-party source code; every other attribution below exists only because an
algorithm's exact structure or numeric data (never literal code) was used as reference material during
implementation, per the terms below.

## Independent JPEG Group (IJG) / libjpeg-turbo — AAN fast DCT/IDCT butterfly wiring

`src/PeachImage/Formats/Jpeg/Dct/AanScalarForwardDct.cs` and
`src/PeachImage/Formats/Jpeg/Dct/AanScalarInverseDct.cs` implement the classical AAN (Arai-Agui-Nakajima)
fast DCT/IDCT algorithm. The specific odd-branch butterfly wiring and rotation constants — the part that
could not be safely re-derived from the DCT-II/DCT-III definitions alone (see
[issue #5](https://github.com/jhaygood86/PeachImage/issues/5)) — were sourced from libjpeg-turbo's
floating-point kernels, `jfdctflt.c` (`jpeg_fdct_float`) and `jidctflt.c` (`jpeg_idct_float`), at
<https://github.com/libjpeg-turbo/libjpeg-turbo>, cross-checked against the fixed-point kernels
`jfdctfst.c`/`jidctfst.c` in the same repository. Those files originate from the Independent JPEG Group's
libjpeg (algorithm and code by Thomas G. Lane, 1994-1998; later revisions by Guido Vollbeding, 2010) and are
maintained by the libjpeg-turbo project (D. R. Commander and contributors).

No source code from libjpeg-turbo or libjpeg was copied into PeachImage. PeachImage's C# implementation uses
its own variable names, method structure, and file organization (matching the rest of this codebase's
existing `Dct/` kernels), and was independently verified against PeachImage's own direct-definition
reference kernels (`ScalarForwardDct`/`ScalarInverseDct`) via matrix cross-check and impulse-response tests
— not against libjpeg-turbo's output — before being trusted. What was referenced is the mathematical
structure of the odd-branch butterfly network (which intermediate terms are shared between which outputs,
and in what order) and its constants, not literal code.

libjpeg/libjpeg-turbo are distributed under the IJG License, which requires the following notice to
accompany any software based in part on their work:

> This software is based in part on the work of the Independent JPEG Group.

The IJG License also requires reproducing its notice in full where source is referenced:

> The authors make NO WARRANTY or representation, either express or implied, with respect to this
> software, its quality, accuracy, merchantability, or fitness for a particular purpose. This software is
> provided "AS IS", and you, its user, assume the entire risk as to its quality and accuracy.
>
> This software is copyright (C) 1991-2020, Thomas G. Lane, Guido Vollbeding. All Rights Reserved except as
> specified below.
>
> Permission is hereby granted to use, copy, modify, and distribute this software (or portions thereof) for
> any purpose, without fee, subject to these conditions:
> (1) If any part of the source code for this software is distributed, then this README file must be
> included, with this copyright and no-warranty notice unaltered; and any additions, deletions, or changes
> to the original files must be clearly indicated in accompanying documentation.
> (2) If only executable code is distributed, then the accompanying documentation must state that "this
> software is based in part on the work of the Independent JPEG Group".
> (3) Permission for use of this software is granted only if the user accepts full responsibility for any
> undesirable consequences; the authors accept NO LIABILITY for damages of any kind.

This notice applies only to the AAN DCT/IDCT wiring described above. It does not apply to any other part of
PeachImage.

## Alliance for Open Media (AOM) / libaom

PeachImage's AVIF encoder and decoder leverage ported portions of libaom's algorithms and numeric tables
(structural/algorithmic ports and, in a few explicitly-noted cases, literal pre-trained data — never literal
source code) across:

- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1TileEncoder.cs` — the trellis quantization rate-distortion
  calibration (`av1_optimize_txb`, `av1/encoder/encodetxb.c`).
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1SpeedFeatures.cs` — the ALL_INTRA encoder speed-feature cascade
  (`set_allintra_speed_features_framesize_independent`, `av1/encoder/speed_features.c`).
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntrabcHashTable.cs` — the IntraBC whole-frame hash-table
  candidate search, including its CRC32C construction (`av1/encoder/hash.c`) and hierarchical hash pyramid
  (`av1/encoder/hash_motion.c`).
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntraHogPruner.cs` — the HOG-based directional intra-mode
  pruning model, including its pre-trained weights and thresholds reproduced verbatim
  (`intra_mode_search_utils.h`, `av1/encoder/intra_mode_search.c`).
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntraModelRdPruner.cs` — the SATD-based intra-mode shortlist
  (`intra_model_rd`/`prune_intra_y_mode`, `av1/encoder/intra_mode_search.c`).
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1SymbolEncoder.cs` — both the RD-search per-symbol bit-cost model
  (`av1_cost_symbol`, `av1/encoder/cost.c`/`cost.h`) and, unlike every other entry here, a literal port of
  libaom's real range-coder byte-emission path (`od_ec_enc_*`/`propagate_carry_bwd`, `aom_dsp/entenc.c`/
  `entenc.h`) — needed for genuine byte-for-byte output parity with `aomenc`, not just spec-correctness.
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1PaletteSearch.cs` — the multi-strategy palette-mode candidate
  search (`find_top_colors`/`av1_k_means`/the palette-size sweep/neighbor-color-cache mechanism,
  `av1/encoder/palette.c`, `k_means_template.h`).
- `src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntraCnnPartitionPruner.cs` — the CNN-based intra partition
  pruning model, including ~8,300 pre-trained weight values reproduced verbatim
  (`intra_mode_cnn_partition`/`partition_cnn_weights.h`, `av1/encoder/partition_strategy.c`).

Each file above carries its own doc comments citing the exact libaom file/function/line range each piece of
structure or data was read from, along with what was deliberately *not* ported and why, and (where applicable)
the verification evidence used to confirm the port's effect. That per-feature detail is not repeated here —
this section exists only to record the attribution once, in the same spirit that governs the rest of this
file: no literal libaom source was copied into any of these files (except where explicitly noted above), only
algorithmic structure and, in two cases, literal pre-trained numeric data that cannot be independently derived.

libaom is distributed under the following license (Alliance for Open Media, `LICENSE` file):

> Copyright (c) 2016, Alliance for Open Media. All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided
> that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
>    following disclaimer.
>
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and
>    the following disclaimer in the documentation and/or other materials provided with the distribution.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
> WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
> PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY
> DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
> PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
> HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
> NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
> POSSIBILITY OF SUCH DAMAGE.

This notice applies only to the AVIF encoder/decoder files listed above. It does not apply to any other part
of PeachImage.

## Wacton/Unicolour — ICC color-managed CMYK conversion

`src/PeachImage/Internal/Icc/` is a bounded, direct C# port of the ICC color-management engine from
[Wacton/Unicolour](https://github.com/waacton/Unicolour) (MIT license, © William Acton), used so a decoded
JPEG's CMYK output can be converted to RGBA32 using the image's own embedded ICC profile (its real device→PCS
transform) instead of a naive, non-colorimetric formula, wherever a caller requests that conversion via
`PixelFormatConverter`/`IccCmykConverter`. Unlike every other entry in this file, this **does** include
literally-adapted third-party source code, not just referenced algorithmic structure — per explicit instruction,
because a real ICC transform for CMYK is always LUT-based (multi-dimensional color lookup tables with curve and
matrix stages), which needs real, spec-careful binary tag parsing and N-dimensional interpolation that Unicolour
already has, tested and correct, rather than re-deriving one from scratch.

**Scope**: only Unicolour's `Icc/` subsystem is ported — its much larger general color-science library (CAM16,
Munsell, Oklab, spectral/blackbody machinery, its pluggable multi-white-point `Configuration`/`ChromaticAdaptor`
system, and every other color space it supports) is not, because PeachImage only ever needs one fixed pipeline:
ICC device values → profile connection space (XYZ or Lab, always D50) → Bradford-adapted XYZ (D65) → linear
sRGB → gamma-companded sRGB. Ported files are renamed to PeachImage's American "color" spelling throughout
(identifiers only — "Unicolour," the upstream project's own name, is never respelled) and given an `Icc` prefix:

| Unicolour source (`Icc/` unless noted) | PeachImage file | Notes |
|---|---|---|
| `Signatures.cs` | `IccSignatures.cs` | Trimmed to the signatures this port's supported transforms use. |
| `Header.cs` | `IccHeader.cs` | Trimmed to the fields transform selection and PCS/intent math consume. |
| `Tag.cs`, `Tags.cs` | `IccTag.cs`, `IccTags.cs` | Ported as-is (`Tag` dropped its unused `Offset`/`Size` fields). |
| `DataTypes.cs`, `NumberTypes.cs` | `IccDataTypes.cs`, `IccNumberTypes.cs` | Big-endian binary stream readers, ported as-is. |
| `Intent.cs` | `IccIntent.cs` | Enum, unchanged values. |
| `Curve.cs` | `IccCurve.cs` | `curv`/`para` (table/parametric) tone-curve parsing and evaluation. |
| `Clut.cs` | `IccClut.cs` | N-dimensional CLUT interpolation — rewritten against `Span<T>` (allocation-free per lookup; the upstream version allocates three arrays per call). |
| `Luts.cs` | `IccLuts.cs` | `mft1`/`mft2`/`mAB `/`mBA ` multi-function LUT tag parsing. |
| `Matrices.cs` + the relevant slice of `Matrix.cs` | `IccMatrices.cs`, `IccMatrix.cs` | Rewritten as purpose-built `readonly struct` value types (no heap allocation) instead of Unicolour's general array-backed `Matrix` class, since every matrix stage this port supports is exactly 3x3. Also carries new, PeachImage-original SIMD batch-multiply methods (`IccMatrix3x3.MultiplyBatch`) not present upstream. |
| `Transform.cs` | `IccTransform.cs` | Abstract base: the A/B/M curve-CLUT-matrix combinators and PCS intent-adjustment math — rewritten against `Span<T>` throughout (the per-pixel hot path; the upstream version allocates a fresh array at every stage). |
| `TransformAToB.cs`, `TransformTrcMatrix.cs`, `TransformTrcGrey.cs`, `TransformNone.cs`, `TransformDToB.cs` | `IccTransformAToB.cs`, `IccTransformTrcMatrix.cs`, `IccTransformTrcGrey.cs`, `IccTransformNone.cs`, `IccTransformDToB.cs` | The TRC-matrix/TRC-grey/none/D-to-B transforms are ported for completeness of the selection precedence below even though a real CMYK profile only ever resolves to `TransformAToB`. |
| `Profile.cs` | `IccProfile.cs` | Adapted to construct from `byte[]` only (no file-path convenience constructor), and to stay in the profile's own D50 PCS rather than taking an injected, pluggable `ChromaticAdaptor` — see below. |

**Not ported**: `Icc/Channels.cs` (Unicolour's own public-API glue to its general `Xyz`/`Rgb` types — replaced
by `IccCmykConverter`, a small integration point at the call site instead); Unicolour's general `Xyz.cs`,
`Lab.cs`, `WhitePoint.cs`, `XyzConfiguration.cs`, `ChromaticAdaptor.cs`, `ChromaticAdaptation.cs`, `Lut.cs`
(only its lower/upper-index lookup logic is ported, as `IccLut.cs`), `Interpolation.cs`, and `Matrix.cs`'s
general N×M machinery. New, PeachImage-original code fills the resulting gap: `IccColorMath.cs` (the standard
CIE Lab↔XYZ formulas — reimplemented against plain `IccVector3` values rather than importing Unicolour's
`Lab`/`Xyz`/`WhitePoint` types — plus the fixed Bradford D50→D65 adaptation and XYZ→linear-sRGB matrices, fused
into one matrix computed once rather than chained per pixel, and the sRGB gamma-companding step) and
`IccCmykConverter.cs` (the `PixelFormatConverter` integration point: parses the embedded profile, falls back
gracefully to the naive formula on any parse/support failure, and batches the fixed matrix stage across pixels
with `Vector128`/`Vector256` SIMD — the profile's own device→PCS evaluation stays scalar, since CLUT/curve
lookups are inherently data-dependent per pixel).

Unicolour is distributed under the MIT License:

> MIT License
>
> Copyright (c) 2022-2026 William Acton
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without
> limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
> Software, and to permit persons to whom the Software is furnished to do so, subject to the following
> conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
> TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
> THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
> CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
> DEALINGS IN THE SOFTWARE.

This notice applies only to `src/PeachImage/Internal/Icc/` as described above. It does not apply to any other
part of PeachImage.
