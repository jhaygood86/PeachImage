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
PeachImage, which remains covered solely by the [MIT license](LICENSE) in the repository root.
