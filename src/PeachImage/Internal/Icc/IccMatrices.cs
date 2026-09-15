namespace PeachImage.Internal.Icc;

/// <summary>
/// The matrix stage of an <c>mAB </c>/<c>mBA </c> multi-function LUT, or a RGB-matrix-TRC transform's fixed
/// RGB↔XYZ matrix: a 3x3 multiply followed by an additive 3-vector offset. Ported from Wacton/Unicolour's
/// <c>Icc/Matrices.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal readonly record struct IccMatrices(IccMatrix3x3 Multiply, IccVector3 Offset)
{
    internal static IccVector3 ZeroOffset => default;

    // NOTE: the ICC spec says the matrix output should apparently be clipped to 0-1 before being used
    // downstream, but no evidence of that clipping was found in the DemoIccMAX reference implementation
    // either (same observation Unicolour's own comment makes), so this doesn't clip.
    internal IccVector3 Apply(IccVector3 input) => Multiply.Multiply(input) + Offset;
}
