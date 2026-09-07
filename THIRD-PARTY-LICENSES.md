# Third-party attributions

PeachImage is original, from-scratch managed code with no native interop and no bundled or linked
third-party source. This file exists solely because one algorithm's exact numerical structure was used as
reference material during implementation, per the terms below.

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

## Alliance for Open Media (AOM) / libaom — AV1 trellis quantization rate-distortion calibration

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1TileEncoder.cs`'s `OptimizeCoeffTrellis` method (PeachImage's
post-quantization AV1 coefficient refinement, part of the AVIF encoder's rate-distortion optimization) uses
two specific numeric values sourced from libaom's own trellis implementation, `av1_optimize_txb` in
`av1/encoder/encodetxb.c` at <https://aomedia.googlesource.com/aom> (also mirrored on GitHub, e.g.
<https://github.com/GoogleChromeLabs/wasm-av1/blob/master/third_party/aom/av1/encoder/encodetxb.c>): the
per-plane trellis rd-multiplier table's intra row (`plane_rd_mult[0] = {17, 13}`, luma and chroma
respectively) and the `>> 2` divisor it's combined with. These were needed because a first attempt at this
method, using the same rate-distortion lambda this encoder's mode/tx_type/partition search already uses
(unscaled), measurably over-corrected — smaller output but disproportionately worse quality than simply
picking a different quantizer at the same size, on this project's own benchmark comparison. libaom's own
trellis pass deliberately uses a separate, smaller-granularity-calibrated multiplier rather than its
mode-decision rdmult directly; PeachImage's implementation adopted that same two real constants (17, 13, and
the shift-by-2) for the identical reason, rather than guessing a replacement scale factor with no reference
basis.

No source code from libaom/AOM was copied into PeachImage — `OptimizeCoeffTrellis` is original C#, using this
codebase's own existing quantization/entropy-coding types (`Av1Dequantizer`, `Av1CoefficientWriter`,
`Av1RdCost`) and its own coefficient-domain distortion formulation, verified against this project's own
benchmark image (real, non-interpolated same-size comparison points, not just this encoder's own internal
cost metric) rather than against libaom's output. What was referenced is the numeric calibration described
above, not literal code.

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

This notice applies only to the trellis rate-distortion calibration described above. It does not apply to any
other part of PeachImage, which remains covered solely by the [MIT license](LICENSE) in the repository root.

## Alliance for Open Media (AOM) / libaom — ALL_INTRA encoder speed-feature cascade

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1SpeedFeatures.cs` (`Av1SpeedFeatures.Compute`) is a field-by-field
port of the numeric structure of libaom's `set_allintra_speed_features_framesize_independent` function,
`av1/encoder/speed_features.c` (lines 345-616 as of commit `d565eec60f`) at
<https://aomedia.googlesource.com/aom>, the function libaom uses to configure its `--cpu-used` speed presets
(0-9) under AV1's ALL_INTRA usage mode. This project's `AvifEncoderOptions.Effort` option is intended to give
callers the same knob libaom exposes, so it needed to reproduce libaom's own per-level threshold values and
their cascading (each level a superset of the previous level's changes) structure, including two genuinely
non-monotonic exceptions libaom's own code contains (a chroma pruning threshold active only at exactly one
level, and a winner-mode candidate count that rises then falls across levels) — these are not independently
derivable from first principles or from the AV1 specification (which does not mandate any particular encoder
search strategy at all), only from reading libaom's own source values directly.

No source code from libaom/AOM was copied into PeachImage — `Av1SpeedFeatures.cs` is original C#, using this
codebase's own naming conventions and a `record`-based value type in place of libaom's C struct, with its own
doc comments citing the exact libaom function/line ranges each field's cascade point was read from (see that
file directly for the full per-field mapping). What was referenced is the numeric threshold structure
described above, not literal code.

Several fields are now wired into real encoding decisions in `Av1TileEncoder.cs`, each its own literal port of
the libaom behavior the field name refers to (not just the cascade value): `PruneIntrabcCandidateBlockHashSearch`/
`HashMax8x8IntrabcBlocks` gate the IntraBC hash-table search (see this file's own IntraBC section below);
`IntraPruningWithHog` drives the HOG-based directional-mode pruner (see this file's own HOG section below);
`DisableSmoothIntra`/`PruneFilterIntraLevel` gate SMOOTH_PRED/SMOOTH_V_PRED/SMOOTH_H_PRED and the filter_intra
sub-mode search in `Av1TileEncoder.ComputeLumaPruning`/`FilterIntraModeUsedFlag` -- the latter a literal,
verbatim port of libaom's own 13-entry `av1_derived_filter_intra_mode_used_flag` bitmask table
(`av1/encoder/intra_mode_search.c:60-74`), and the former a port of a genuinely non-obvious interaction
between the two speed features confirmed by directly reading libaom's own real mode-search loop (SMOOTH_PRED
itself survives `disable_smooth_intra` unless filter_intra is *also* being pruned, per libaom's own comment
that "the functionality of filter intra modes and smooth prediction overlap"), not derivable from the field
names or the AV1 specification alone. `ChromaIntraPruningWithHog` (effort ≥ 3) drives the same HOG pruner as
luma's `IntraPruningWithHog`, applied to `SearchUvMode` reading only the U plane (confirmed by reading
`av1_rd_pick_intra_sbuv_mode` directly: chroma's HOG call never reads V), a real, distinct call site from
luma's own -- not a copy of the same evaluation, since it consults its own separate speed-feature level and
runs against chroma's own source pixels. `TopIntraModelCountAllowed` drives the SATD-based shortlist (see
this file's own SATD section below). `PruneChromaModesUsingLumaWinner` (effort ≥ 4) drives
`Av1TileEncoder.ChromaModeUsedFlagByLumaWinner`, a literal, verbatim port of libaom's own 13-entry
`av1_derived_chroma_intra_mode_used_flag` bitmask table (`av1/encoder/intra_mode_search.c:87-101`), applied
inside `SearchUvMode`'s own candidate loop exactly where libaom applies it (`av1_rd_pick_intra_sbuv_mode`,
`intra_mode_search.c:938-941` -- a plain `continue` inside the existing loop, not a different iteration
order). Confirmed by direct reading that every row unconditionally keeps DC_PRED, SMOOTH_PRED, and
UV_CFL_PRED available regardless of the luma winner, restricting only the directional and
SMOOTH_V/SMOOTH_H/Paeth candidates to the one mode matching the luma winner itself -- not derivable from the
speed-feature's own name or the AV1 specification, only from decoding libaom's own literal table values.
Also confirmed this project's own existing `ChromaIntraPruningWithHog` override (already wired in an earlier
pass, see this section's own non-monotonic-cascade remarks above) already correctly disables that HOG-based
pruner whenever `PruneChromaModesUsingLumaWinner` is active, mirroring libaom's own real override
(`speed_features.c:608-615`: "the HOG computation ... does not seem to help ... hence disable ... when
prune_chroma_modes_using_luma_winner is enabled") -- so the two mechanisms are never simultaneously live in
this project's own cascade either, matching libaom's real behavior exactly rather than by coincidence.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — IntraBC whole-frame hash-table candidate search

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntrabcHashTable.cs` ports the algorithmic structure of libaom's
own IntraBC exact-match candidate index, from the local checkout at `C:\Sources\GoogleSource\aom` (upstream:
<https://aomedia.googlesource.com/aom>):

- The real CRC32C (Castagnoli, polynomial `0x82f63b78` reversed) construction from `av1/encoder/hash.c`
  (`av1_crc32c_calculator_init`/`av1_get_crc32c_value_c`) — a single-table, byte-at-a-time reference
  implementation of the same algorithm libaom's own faster 8-way-sliced variant (same file) also computes;
  this project has no need for that speed, so only the reference form was ported.
- The whole-frame, hierarchical hash-pyramid construction from `av1/encoder/hash_motion.c`
  (`av1_generate_block_2x2_hash_value`/`av1_generate_block_hash_value`): a luma-only, per-pixel-position array
  computed once per frame at each of AV1's 6 square IntraBC block sizes (4, 8, 16, 32, 64, 128), each level
  built by CRC32C-combining 4 non-overlapping sub-block hashes from the previous, half-sized level, bottomed
  out by a 2x2 base level that simply byte-packs its 4 luma samples (`get_identity_hash_value`'s own
  `(a<<24)+(b<<16)+(c<<8)+d` packing, reproduced exactly).

`Av1TileEncoder.FindIntrabcMatch` was rewritten around this table: previously PeachImage indexed only
already-committed leaves' own top-left corners (an FNV-1a hash keyed by that leaf's own chosen size), which
could only ever find a match against a *same-size* leaf. The libaom-structured whole-frame table indexes
every valid pixel offset at every one of the 6 sizes up front, so a match can be found against a
differently-partitioned region of an already-encoded area too — a genuine widening of match coverage, not
just a hash-function swap. Verified via this project's own `GraphicContentImage_LosslessAvif` regression
fixture: 512x512 improved from 56,702 to 48,457 bytes (-14.5%) with this table wired in versus disabled,
confirmed round-trip-correct via the real decoder both before and after.

**Deliberately not ported**: libaom's exact spatially-dispersed insertion-order state machine
(`av1_add_to_hash_map_by_row_with_precal_data`) and its 256-entry-per-bucket cap with silent-drop-on-overflow
— both exist in libaom to bound per-bucket memory/scan cost for pathologically repetitive content while
keeping a *spread* of candidates, not to change which matches exist. This project instead inserts every valid
position into unbounded per-bucket lists and caps the *query-time* scan via the already-ported, effort-indexed
`Av1SpeedFeatures.PruneIntrabcCandidateBlockHashSearch` (`AOMMIN(64, count)`, mirroring libaom's own real
`mcomp.c` query-time bound) — the same practical bound libaom achieves, without the dispersed-eviction
machinery. `Av1SpeedFeatures.HashMax8x8IntrabcBlocks` (effort ≥ 4) is also wired in, capping the table's own
largest hashed size to 8 so sizes 16 and above are never even computed, matching libaom's own real
construction-time saving under that speed feature.

No literal libaom source was copied — `Av1IntrabcHashTable.cs` is original C# using this project's own naming
and structure throughout, with its own doc comments citing the exact libaom file/function each piece of
algorithmic structure was read from. What was referenced is libaom's real construction and insertion-order
*structure* (needed because which exact candidate wins a tie depends on that structure, not just on whether a
match exists, and this is not derivable from the AV1 specification, which doesn't mandate any particular
encoder search strategy), not literal code.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — HOG-based directional intra-mode pruning model

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntraHogPruner.cs` ports libaom's own pre-trained linear model for
pruning directional intra modes before either the real RD search or the cheaper SATD-based shortlist ever runs
(`prune_intra_mode_with_hog`, `av1/encoder/intra_mode_search_utils.h`, from the local checkout at
`C:\Sources\GoogleSource\aom`, upstream: <https://aomedia.googlesource.com/aom>), reproduced from the
`Av1SpeedFeatures.IntraPruningWithHog` speed feature already ported and cited elsewhere in this file. Unlike
this file's other two libaom attributions above (structural/algorithmic ports with independently-derived C#
implementations), this one carries over real, literal pre-trained numeric data verbatim:

- `av1_intra_hog_model_weights`/`av1_intra_hog_model_bias` (256 + 8 floats, `intra_mode_search_utils.h:41-90`)
  — libaom's own pre-trained linear model coefficients (confirmed by reading `av1/encoder/ml.c`'s
  `av1_nn_predict_c` directly: with `num_hidden_layers = 0`, the model has no hidden layer or activation at
  all, just `scores = weights·histogram + bias`), reproduced as-is in `Av1IntraHogPruner.Weights`/`.Bias` —
  this data can only come from libaom's own training process, not be independently derived or read off the AV1
  specification (which does not define or require this pruning heuristic at all).
- The `get_hist_bin_idx` bin-boundary table (32 `int32` thresholds, `intra_mode_search_utils.h:110-115`) and
  the `thresh[4]` per-effort-level pruning thresholds (`{ -1.2, -1.2, -0.6, 0.4 }`, `intra_mode_search.c:1506`
  for luma; the intraframe row of chroma's own `thresh[2][4]` at `intra_mode_search.c:961-964` is numerically
  identical for this encoder's always-intra-frame use, so one shared table serves both), both reproduced
  verbatim in `Av1IntraHogPruner.Thresholds`/`.Thresh`.
- The Sobel gradient formula, bin-angle assignment, and histogram normalization (`lowbd_generate_hog`/
  `get_hist_bin_idx`/`normalize_hog`, `intra_mode_search_utils.h:106-181`) are a literal algorithmic port
  (integer arithmetic and truncating-division semantics reproduced exactly, including the intentionally-lossy
  `temp / 2` integer split for the `dx == 0` case) — needed because, unlike the RD-cost formulas ported
  elsewhere in this project, this specific pruning behavior is entirely libaom's own trained heuristic with no
  independent derivation possible.

Deliberately ported the plain C reference implementation (`av1_nn_predict_c`) rather than libaom's
SIMD-dispatched variants (`av1_nn_predict_sse3`/`_avx2`/neon, selected at runtime via `av1_rtcd_defs.pl`'s
`specialize` directive on real hardware) — confirmed those use a different floating-point summation order
(a SIMD horizontal-add tree vs. this port's sequential accumulation) that can differ from the C reference by
up to one ULP before the final 1/512-precision quantization step usually (not always) absorbs it. This
project's own Phase 0 harness reference (`aomenc.exe`, `build_ninja2`) is built with
`-DAOM_TARGET_CPU=generic` (no SIMD, pure C), so it always executes `av1_nn_predict_c` itself — porting a SIMD
variant would target behavior this project's own comparison harness never exercises.

No other source code from libaom/AOM was copied — `Av1IntraHogPruner.cs` is original C# using this project's
own naming, structure, and doc comments throughout; only the numeric model data and the specific algorithmic
steps it depends on (gradient computation, binning, normalization, quantization) were reproduced, each cited
to its exact libaom file/line range above.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — SATD-based intra-mode shortlist

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntraModelRdPruner.cs` ports libaom's own cheap SATD-based
pre-filter that runs after HOG pruning (above) but before this project's own real, far more expensive
WHT+quantize+entropy-cost estimate for each (mode, angle_delta) candidate (`intra_model_rd`/`prune_intra_y_mode`,
`av1/encoder/intra_mode_search.c`/`intra_mode_search_utils.h`, local checkout `C:\Sources\GoogleSource\aom`,
upstream: <https://aomedia.googlesource.com/aom>):

- `aom_hadamard_4x4_c`/`hadamard_col4` (`aom_dsp/avg.c`) — a real, distinct transform from this project's own
  lossless `Av1ForwardWht` (confirmed by reading `av1_quick_txfm`/`wht_fwd_txfm` directly: libaom's own SATD
  estimate deliberately uses a *different* Hadamard construction than its real lossless coding transform, not
  the same one reused for a cheaper purpose), reproduced with the exact same two-pass column/cross-column
  index arithmetic and intermediate right-shift-by-1 as the original, plus its own final transpose ("to match
  SSE2 behavior" per libaom's own comment) -- ported literally rather than simplified via abstract
  separable-transform reasoning, to avoid an off-by-one/transpose-direction error in a construction this
  indirect. Verified against an independently hand-traced 4x4 test vector, not just internal consistency.
- `aom_satd_c` (`aom_dsp/avg.c`) — sum of absolute transform-domain coefficients, no entropy modeling.
- `prune_intra_y_mode`'s own two-threshold decision (`thresh_top = 1.00`, `thresh_best = 1.50`,
  `intra_mode_search.c:471-472`) and its stateful top-K insertion logic, reproduced exactly including
  updating the top-K list even for a candidate the same call is about to prune.

**Deliberate scope limitation, still disclosed**: `get_model_rd_index_for_pruning`'s neighbor-adaptive
refinement (`Av1SpeedFeatures.AdaptTopModelRdCountUsingNeighbors`, effort ≥ 6 only) is not ported -- this port
always checks the true `Count`-th-best slot, matching libaom's own behavior at every effort level below 6
(this project's own default is 2).

**A previously-disclosed gap, since closed**: `prune_intra_y_mode`'s top-K state is genuinely *order-dependent*
(which candidates survive depends on evaluation order for a given leaf); this project's own `CandidateModes`
array (`Av1TileEncoder.cs`) now iterates in libaom's own real `intra_rd_search_mode_order`
(`intra_mode_search.c:38-42`: DC, H, V, SMOOTH, PAETH, SMOOTH_V, SMOOTH_H, D135, D203, D157, D67, D113, D45),
confirmed by direct source reading rather than the `Av1IntraMode` enum's own declaration order this project
used before -- see `CandidateModes`'s own doc comment for the full citation, including its own disclosed
scope limit (libaom's separate, third ordering for the angle-delta *refinement* phase specifically, a larger,
genuinely different two-phase search architecture this project's own single nested mode/angle-delta loop
doesn't reproduce). This same array also now correctly orders the 13 non-CFL candidates for chroma
(`SearchUvMode`/`EstimateLosslessChromaCost`): libaom's own real chroma order, `uv_rd_search_mode_order`
(`intra_mode_search.c:44-49`), is identical to the luma order once its own leading `UV_CFL_PRED` entry is
removed -- confirmed by direct comparison, not assumed -- and CFL is already evaluated via this project's own
separate `TryCflCandidate` code path (its own cost computation -- least-squares alpha estimation plus a real
trial window -- is structurally unlike every other candidate in this array), so no second, chroma-specific
array was needed.

No other source code from libaom/AOM was copied — `Av1IntraModelRdPruner.cs` is original C# using this
project's own naming and structure throughout, with its own doc comments citing the exact libaom file/line
range each piece was read from.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — RD-search per-symbol bit-cost model

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1SymbolEncoder.cs`'s `EstimateSymbolCost` (the RD-search cost
estimate every partition/mode/palette/coefficient candidate comparison in `Av1TileEncoder.cs` is built on --
never the real, bit-exact bitstream write path, which is untouched) ports libaom's own `av1_cost_symbol`
function (`av1/encoder/cost.h`) and its backing `av1_prob_cost[128]` table (`av1/encoder/cost.c`, `round(-log2(i
/ 256.0) * (1 << AV1_PROB_COST_SHIFT))` for `i = 128..255`) from the local checkout at
`C:\Sources\GoogleSource\aom` (upstream: <https://aomedia.googlesource.com/aom>). Unlike this file's other two
libaom attributions above (which reference a small number of independently-applied tuning constants), this is
a closer structural port: `av1_prob_cost`'s 128 values are reproduced verbatim in
`Av1SymbolEncoder.Av1ProbCostTable`, and `Av1SymbolEncoder.Av1ProbCostSymbol` reimplements
`av1_cost_symbol`'s own normalization-shift-then-table-lookup algorithm (translating libaom's `get_msb`/
`get_prob` C helpers and `CDF_PROB_BITS`/`CDF_PROB_TOP`/`AV1_PROB_COST_SHIFT` constants, read directly from
`aom_dsp/entcode.h`/`aom_dsp/prob.h`, into equivalent C# using this project's own existing
`Av1CdfAdaptation.FloorLog2` in place of `get_msb`) rather than only its two output constants. This was needed
because PeachImage's own prior formula (`15 - FloorLog2(newRange)`, a real-bitstream renormalization-step
count reused as a cost proxy) rounds every symbol's cost to a whole bit, which is exact for the real bitstream
write path but a poor RD-search proxy for a near-certain, frequently-repeated symbol -- confirmed via this
project's own libaom byte-exact comparison harness (a 128x128 checkerboard test image: PeachImage's own
estimate for a single 64x64 candidate already exceeded aomenc's entire 4-quadrant frame total under the old
formula) and independently confirmed by reading libaom's own source, which does not use its bitstream's
renormalization schedule as an RD proxy either, for the same reason.

No other source code from libaom/AOM was copied -- `Av1SymbolEncoder.cs`'s own bit-exact bitstream write path
(`WriteSymbolCore`/`CurValue`) and every downstream consumer of `EstimateSymbolCost` remain this project's own
original C#, and this port's result is deliberately converted back to whole, rounded bits before returning
(preserving `EstimateSymbolCost`'s existing `int` "whole bits" contract for every one of its call sites)
rather than propagating libaom's own 1/512-bit fixed-point unit any further into this codebase.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — forward, incremental range-coder byte emission

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1SymbolEncoder.cs`'s real bitstream write path (`WriteSymbolCore`'s
`low`/`rng`/`cnt` state machine, `Normalize`, `EmitBytes`, `PropagateCarryBackward`, and `Flush`) is a direct,
literal port of libaom's own real range encoder, `od_ec_enc_normalize`/`od_ec_encode_q15`/`od_ec_enc_done`
(`aom_dsp/entenc.c`) and `propagate_carry_bwd`/`write_enc_data_to_out_buf`/the `od_ec_enc` struct layout
(`aom_dsp/entenc.h`), from the local checkout at `C:\Sources\GoogleSource\aom` (upstream:
<https://aomedia.googlesource.com/aom>). This is needed for genuine byte-for-byte output parity with libaom
(this project's own explicit goal, see the AVIF lossless-parity plan): the AV1 range decoder accepts more than
one bit-exact encoding of a given symbol sequence, so an independently-designed (but still spec-correct)
encoder is not enough -- only replicating libaom's own specific encoding choice produces its exact bytes.

- `od_ec_enc_reset`'s initial state (`low = 0`, `rng = 0x8000`, `cnt = -9`) is reproduced exactly, including
  the `-9` starting value libaom's own comment explains as chosen "so that it crosses zero after we've
  accumulated one byte + one carry bit".
- `od_ec_encode_q15`'s per-symbol `low`/`rng` update (`Av1SymbolEncoder.WriteSymbolCore`) is reproduced using
  this project's own pre-existing `CurValue` helper (already an exact, independently-verified port of
  `Av1SymbolDecoder.ReadSymbolCore`'s own `cur()`) in place of libaom's own inverted-icdf `u`/`v` terms --
  confirmed algebraically that the two conventions' differing constant-offset terms cancel out identically in
  the `u - v`/`prev - cur` subtraction both use, so no separate icdf-format translation was needed.
- `od_ec_enc_normalize`'s renormalization-and-conditional-flush logic (`Av1SymbolEncoder.Normalize`) is
  reproduced exactly, including the `s >= 40` flush threshold and the `numBytesReady`/`carry`-mask arithmetic
  that extracts the newly-final top bytes of `low`.
- `write_enc_data_to_out_buf` (`Av1SymbolEncoder.EmitBytes`) is reproduced with equivalent (not literal) byte
  emission: libaom writes a full 8-byte register via `memcpy` as a performance optimization and advances its
  own offset by only the meaningful byte count; this port simply appends the meaningful bytes one at a time,
  which is functionally identical and avoids needing unsafe/raw-memory code for a micro-optimization this
  project has no equivalent performance requirement for.
- `propagate_carry_bwd` (`Av1SymbolEncoder.PropagateCarryBackward`) and `od_ec_enc_done`
  (`Av1SymbolEncoder.Flush`) are reproduced verbatim/near-verbatim, including `od_ec_enc_done`'s own choice of
  final committed value (`e = ((l + m) & ~m) | (m + 1)`, the smallest `0x4000`-boundary-congruent value at or
  above the true `low` plus a half-step margin) and its own per-byte extraction loop.

An earlier version of this encoder (before this port) instead recorded every symbol's own renormalization step
during encoding without emitting any bytes, then algebraically solved backward from an arbitrary
always-achievable final state for *some* valid bit sequence -- spec-correct and round-trip-verified against
this project's own decoder, but not libaom's own specific byte sequence, confirmed via this project's own
libaom byte-exact comparison harness (`tools/PeachImage.LibaomParity/`). That harness confirms this port closes
the gap: the synthetic solid-color and gradient test images, previously 2 and 1 bytes larger than real `aomenc`
output respectively despite already being structurally identical (same partition, same mode, same skip
decisions), are now byte-for-byte identical to `aomenc`'s own real output, including the full muxed AVIF file.

No other source code from libaom/AOM was copied -- `Av1SymbolEncoder.cs` is original C# throughout, using this
project's own naming and doc-comment conventions; only the range-coder algorithm itself (state machine,
renormalization/flush thresholds, and carry-propagation logic) was reproduced, cited to its exact libaom
file/function above. `EstimateSymbolCost`/`EstimateSymbolCostPrecise512ths`/`Av1ProbCostSymbol`/
`Av1ProbCostTable`/`EstimateNsCost` (the RD-search cost-estimation path, attributed separately above) were not
touched by this change at all.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — multi-strategy palette-mode candidate search

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1PaletteSearch.cs` ports libaom's own real palette candidate
generation and size-sweep algorithms (`av1/encoder/palette.c`, `av1/encoder/k_means_template.h`,
`av1/encoder/random.h`, read directly from the local checkout at `C:\Sources\GoogleSource\aom`, upstream:
<https://aomedia.googlesource.com/aom>), replacing this project's own earlier exact-match-or-single-k-means
palette construction (`TryBuildYPalette`/`TryBuildApproximateYPalette` and their UV counterparts, all removed)
with the real, faithful port:

- `find_top_colors` (`palette.c:502-538`) -- the top-N-by-frequency candidate strategy, an incrementally
  maintained bounded top-N list (not a full sort), ties broken toward the lower color value
  (`color_count_comp`). Real libaom runs this *and* k-means for every candidate size, not one or the other --
  this port does the same.
- `av1_k_means` (dim 1 and 2, `k_means_template.h`) -- Lloyd's algorithm with libaom's own specific,
  deterministic-but-data-dependent quirks reproduced exactly: range-bisection centroid seeding (not k-means++
  or arbitrary), a monotonic-non-increase early-termination rule (rolls back to the previous iteration's
  centroids/assignment the instant total distortion would increase, rather than iterating to strict
  convergence), and a pseudo-random empty-cluster reseed via a specific LCG (`lcg_next`/`lcg_rand16`,
  `random.h`, freshly seeded from the first data point on every centroid-update call). `av1_calc_indices`'s
  own strict-less-than nearest-centroid tie-break (lowest centroid index wins) is reproduced exactly too.
- `perform_top_color_palette_search`/`perform_k_means_palette_search`/`set_stage2_params` (`palette.c`) --
  the real palette-size sweep, gated by `prune_palette_search_level` (`Av1SpeedFeatures.PrunePaletteSearchLevel`,
  already ported and unit-tested in an earlier session, wired into a real consumer here for the first time):
  a structured jump-search coarse pass (libaom's own `start_n_lookup_table`/`step_size_lookup_table`,
  reproduced verbatim as `Av1TileEncoder.PaletteStartNLookup`/`PaletteStepSizeLookup`) plus a +-1 refine of
  the winner at level 1; a full ascending-then-descending sweep at level 0; a greedy hill-climb that stops
  the moment a size fails to improve at level 2. The `colors == PALETTE_MIN_SIZE` special case (the block's
  true min/max pixel values become the centroids directly, evaluated unconditionally with header-cost gating
  off regardless of speed level) is reproduced exactly (`palette.c:718-725`).
- `prune_luma_palette_size_search_level`'s header-cost-only lower bound (`palette.c:271-286`) -- a real,
  zero-distortion cost estimate (color values + size + the real, locally-adapted color-map entropy cost, via
  this project's own pre-existing `EstimateColorMapBits`/`EstimatePaletteColorBitsY`) that, when it alone
  can't beat the running best, skips that candidate's real residual evaluation and aborts the rest of that
  sweep direction -- reproduced with libaom's own two-tier shift (level 1: a 2x-headroom relaxed threshold;
  level 2: strict).
- Chroma's own real, simpler search (`av1_rd_pick_palette_intra_sbuv`, `palette.c:762-936`) -- confirmed by
  direct source reading to have no top-color strategy at all, just a plain ascending k-means (2D, jointly
  clustering (U, V) pairs) size sweep, broken early by `early_term_chroma_palette_size_search`
  (`Av1SpeedFeatures.EarlyTermChromaPaletteSizeSearch`, unconditionally true at every effort level) the
  moment a size's own header cost alone can no longer beat the running best -- and, unlike luma, *never*
  deduplicates centroids after k-means (confirmed by direct reading: `remove_duplicates` is never called on
  this path -- a real asymmetry in libaom's own source, reproduced as-is, not smoothed over). The post-k-means
  centroid relabeling (`palette.c:872-885`, re-sorting (U, V) pairs ascending by U and recomputing indices
  against the new label order -- a pure canonicalization, not a distortion/entropy trade-off) is reproduced
  exactly as `Av1TileEncoder.SortChromaCentroidsByU`.

**`optimize_palette_colors`/`av1_index_color_cache` (`palette.c`) -- now implemented** (`Av1PaletteSearch.
OptimizePaletteColors`/`IndexColorCache`, `Av1TileEncoder.GetPaletteCacheColors`/`WriteCacheAwareColors`/
`EstimateCacheAwareColorBits`): a literal port of libaom's own neighbor-color-cache mechanism, previously
deferred (see the prior revision of this note). `OptimizePaletteColors` snaps each raw candidate centroid
(before dedup) to the nearest neighbor-cache color when within `4 << (bit_depth-8)` (== 4 at 8-bit depth)
codeword units, trading a tiny reconstruction error for reusing an already-cheap cached color -- always safe
for correctness, since the encoder's own real, RD-gated non-exact-residual path (already ported separately,
see the "approximate palette with RD-gated residual coding" work) prices and corrects whatever error the snap
introduces, exactly like any other approximate/k-means candidate. `IndexColorCache` is a literal port of
`av1_index_color_cache`'s own early-stop protocol (walk the cache in ascending order, stop the instant every
chosen color has a match), used identically for real bit-cost estimation (`EstimateCacheAwareColorBits`) and
real bitstream writing (`WriteCacheAwareColors`) -- both now genuinely cache-hit-aware, replacing the prior,
always-pessimistic "every color is not in cache" cost/write path this note previously described. Verified via
the project's own harness: the synthetic 128x128 solid/gradient/checkerboard/noise corpus shows zero byte
change (no leaf in that corpus has a palette-using neighbor to snap against), while the `GraphicContentImage`
regression fixture (which does exercise real neighbor-cache adjacency across many leaves) shows a small,
mixed real effect -- 512x512 improves (45923 -> 45868 bytes), 256x256 regresses slightly (32412 -> 32547
bytes), 128x128 unchanged -- consistent with this being a faithful port of a real, narrowly-scoped libaom
mechanism rather than an independently-tuned heuristic, not a uniform win by construction. Also not ported:
the real Y+UV *joint* independence libaom's own architecture has (this
project's own pre-existing "palette is all-or-nothing across Y and UV" invariant, needed for its own
`skip`-flag handling, means each plane's search is gated against its own plane's real non-palette cost
independently rather than truly jointly -- a leaf where Y alone doesn't quite beat its own baseline, but Y+UV
jointly would have, is not found by this combination; a small, disclosed limitation of preserving that
pre-existing invariant, not a regression from any prior session's own behavior).

No other source code from libaom/AOM was copied -- `Av1PaletteSearch.cs` and the new orchestration methods in
`Av1TileEncoder.cs` (`SearchLosslessYPalette`/`SearchLosslessUvPalette`/`PaletteRdY`/`PaletteRdUv`/
`PerformTopColorPaletteSearch`/`PerformKMeansPaletteSearch`/`SetStage2Params`) are original C# using this
project's own naming, structure, and doc comments throughout, with each piece cited to its exact libaom
file/function/line range above.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.

## Alliance for Open Media (AOM) / libaom — CNN-based intra partition pruning model and weights

`src/PeachImage/Formats/Avif/Encoder/Av1/Av1IntraCnnPartitionPruner.cs` ports libaom's real
`intra_cnn_based_part_prune_level` speed feature (wired here as `Av1SpeedFeatures.IntraCnnBasedPartPruneLevel`,
active from effort 1 onward for non-screen-content lossless images) -- `intra_mode_cnn_partition`
(`av1/encoder/partition_strategy.c`) and its own real, pre-trained weight tables
(`av1/encoder/partition_cnn_weights.h`), read directly from the local checkout at
`C:\Sources\GoogleSource\aom` (upstream: <https://aomedia.googlesource.com/aom>). This is the largest single
weight-table port in this project's own history (~8,300 floating-point values across 34 arrays) and, unlike
every other speed-feature port in this file, a genuine small trained model (a 5-layer convolutional trunk
plus 4 branch classifier MLPs), not a hand-tunable heuristic -- confirmed by direct source reading before
porting that libaom's own architecture already tolerates ordinary float32 rounding differences here (its own
test suite, `test/cnn_test.cc`, cross-validates SIMD vs. scalar C implementations for numerical *equivalence*
rather than bit-identical results, and the one explicit precision step in the real inference path,
`av1_nn_output_prec_reduce`, exists specifically "to prevent mismatches between C and SIMD implementations"
per libaom's own comment), so no attempt was made to replicate C's exact summation order -- ordinary C#
`float` math throughout, plus that one quantization step, is faithful to libaom's own real tolerance.

Ported verbatim, with every value read mechanically from the real header file (not hand-transcribed, reducing
transcription-error risk for a table this large):
- The 5 conv layer weight/bias arrays (`av1_intra_mode_cnn_partition_cnn_layer_{0-4}_kernel`/`_bias`,
  `partition_cnn_weights.h:90-1004` approx.) and the `av1_intra_mode_cnn_partition_cnn_config` layer shape
  metadata (in/out channels, kernel size, stride, VALID padding, ReLU activation, and which of the last 4
  layers' own outputs are tapped as the 4 pyramid feature maps) it implies.
- The 4 branch classifier MLP weight/bias arrays (`av1_intra_mode_cnn_partition_branch_{0-3}_dnn_layer_{0,1}_kernel`/`_bias`
  and `_logits_kernel`/`_bias`, `partition_cnn_weights.h:1005-1961`) and their shared `NN_CONFIG` shape (features
  -> 16 (ReLU) -> 24 (ReLU) -> 1 linear logit).
- The resolution-tiered split/no-split threshold tables (`av1_intra_mode_cnn_partition_{no_,}split_thresh_{hdres,midres,lowres}`,
  `partition_cnn_weights.h:2092-2115`) and the `log_q` normalization constants (`av1_intra_mode_cnn_partition_mean`/`_std`,
  `:2116-2122`).
- The `quad_to_linear_1`/`_2`/`_3` quad-tree-position-to-spatial-index lookup tables (`:2124-2133`), copied
  verbatim rather than derived -- confirmed via the actual encoder's own real recursive traversal that this
  project's own `(r, c)`-based child ordering already matches libaom's own `idx` convention exactly (no
  reordering needed), but the tables themselves encode a real, non-obvious quadrant-to-raster-scan
  permutation not worth re-deriving independently.

**Real algorithmic structure ported, not just numeric constants**: the 5-layer conv trunk's own forward-pass
math (`Av1IntraCnnPartitionPruner.Conv2DValidRelu`, a literal port of `av1_cnn_convolve_no_maxpool_padding_valid_c`
fused with `av1_cnn_activate_c`'s `RELU` case, `av1/encoder/cnn.c`, including its own specific
bias-first/in-channel-outer/filter-row/filter-col-inner summation order and flat weight-array indexing
convention) and the branch MLP forward pass (`PredictMlp`, a literal port of `av1_nn_predict_c`,
`av1/encoder/ml.c:31-70`, including `av1_nn_output_prec_reduce`'s own 1/512-precision quantization on the
final logit). The per-node feature assembly (which slice of which pyramid tap feeds which branch classifier,
depending on the node's own quad-tree position) and the split/no-split decision logic (including the real,
non-obvious "level == 1 keeps NONE alive" screen-content carve-out) are also real, faithful ports of
`intra_mode_cnn_partition`'s own control flow (`partition_strategy.c:142-344`), not independently re-derived.

**A real, disclosed adaptation, not a literal copy**: libaom's own 65x65 input-patch extraction reads one
row/column of border padding unconditionally, relying on its own driver-allocated border-padded source
buffers -- this project's own tighter buffers don't have that padding, so `RunCnn` clamps those reads to the
frame's own valid extent instead, matching this project's own established edge-replication convention used
elsewhere (`Av1IntraPrediction.BuildEdges`). This is a correctness necessity, not a behavioral choice, and
only affects the single row/column nearest the frame's own top-left edge.

**Integration into `Av1TileEncoder.ComputeDecidePartition`** mirrors libaom's own per-64x64-region cache
lifecycle (`MACROBLOCK.part_search_info`: reset once per 64x64 region on entry, the CNN run lazily exactly
once per region on first real need, `quad_tree_idx` computed as `4*parent+childIndex+1` with save/restore
around each recursive descent) as an ordinary recursive parameter rather than mutable per-thread state --
simpler here since a recursive parameter needs no manual save/restore, the call stack does it implicitly.
Applied as a pure, minimal-diff gate on which candidate is *allowed to win* the existing real cost comparison
(never skipping any real cost computation, unlike libaom's own genuine pre-filter, which exists for real
compute savings this project's own byte-parity-focused priority doesn't need) -- see that class's own remarks
for why the two possible outcomes (force-split-only vs. disable-split) are mutually exclusive, so this project
needed no equivalent of libaom's own `must_find_valid_partition` safety-valve retry mechanism.

libaom is distributed under the license reproduced in full above (Alliance for Open Media, `LICENSE` file);
that same license text applies to this attribution.
