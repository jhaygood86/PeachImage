namespace PeachImage;

/// <summary>The bit depth an encoder writes; see <see cref="EncoderOptions.Precision"/>.</summary>
public enum EncodePrecision
{
    /// <summary>Keep the precision the source has, where the format can carry it.</summary>
    Auto,

    /// <summary>Write 8 bits per sample, rounding higher-precision data to nearest.</summary>
    Eight,

    /// <summary>Write 16 bits per sample where the format has them (PNG, JPEG XL); formats limited to 8 bits ignore this.</summary>
    Sixteen,
}
