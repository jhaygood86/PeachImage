namespace PeachImage.Internal.Icc;

/// <summary>
/// ICC rendering intent (ICC.1:2010 §7.2.15). Ported from Wacton/Unicolour's <c>Icc/Intent.cs</c> (MIT
/// license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal enum IccIntent
{
    Unspecified = -1,
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3,
}
