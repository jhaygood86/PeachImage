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
