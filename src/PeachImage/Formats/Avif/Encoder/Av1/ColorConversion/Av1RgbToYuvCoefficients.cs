namespace PeachImage.Formats.Avif.Encoder.Av1.ColorConversion;

/// <summary>
/// Shared BT.601 full-range coefficients for <see cref="ScalarAv1RgbToYuvKernel"/>,
/// <see cref="Vector128Av1RgbToYuvKernel"/>, and <see cref="Vector256Av1RgbToYuvKernel"/> -- a
/// float-precision restatement of <see cref="Av1RgbToYuvConverter"/>'s original <c>Kr</c>/<c>Kb</c> double
/// constants, algebraically simplified to drop the original per-pixel <c>/255.0</c> normalize-then-<c>*255</c>
/// round trip and its two per-pixel divisions.
/// </summary>
/// <remarks>
/// The original computes (all in [0,1]-normalized space) <c>Yn = Kr*Rn + Kg*Gn + Kb*Bn</c>, then
/// <c>Crn = (Rn - Yn) / (2 * (1 - Kr))</c>, then scales back up by 255 and offsets by 128. Substituting
/// <c>Rn = R/255</c>, <c>Yn = Y/255</c> (unnormalized 8-bit R/Y) makes every <c>/255</c> cancel: the
/// unnormalized Y is just <c>Kr*R + Kg*G + Kb*B</c> directly, and <c>Cr = 128 + (R - Y) * KrInvHalf</c> where
/// <see cref="KrInvHalf"/> is exactly the original's <c>1 / (2 * (1 - Kr))</c> divisor precomputed once --
/// one multiply per pixel instead of a normalize-divide, a subtract, and a range-divide.
/// </remarks>
internal static class Av1RgbToYuvCoefficients
{
    private const double KrDouble = 0.299;
    private const double KbDouble = 0.114;

    /// <summary>BT.601 red coefficient, as a float.</summary>
    public const float Kr = (float)KrDouble;

    /// <summary>BT.601 green coefficient (<c>1 - Kr - Kb</c>), as a float.</summary>
    public const float Kg = (float)(1.0 - KrDouble - KbDouble);

    /// <summary>BT.601 blue coefficient, as a float.</summary>
    public const float Kb = (float)KbDouble;

    /// <summary>See the type-level remarks -- <c>1 / (2 * (1 - Kr))</c>, the Cr-channel scale factor.</summary>
    public static readonly float KrInvHalf = (float)(1.0 / (2.0 * (1.0 - KrDouble)));

    /// <summary>See the type-level remarks -- <c>1 / (2 * (1 - Kb))</c>, the Cb-channel scale factor.</summary>
    public static readonly float KbInvHalf = (float)(1.0 / (2.0 * (1.0 - KbDouble)));
}
