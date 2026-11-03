namespace PeachImage.Formats.Shared.Metadata;

/// <summary>Converts the density units the formats record to the pixels per metre <see cref="ImageMetadata.HorizontalResolution"/> uses.</summary>
internal static class ResolutionUnits
{
    private const double PerInch = 100.0 / 2.54;
    private const double PerCentimetre = 100.0;

    /// <summary>Pixels per metre in one pixel per unit for a TIFF/EXIF <c>ResolutionUnit</c> (2 = inch, 3 = centimetre), or <see langword="null"/> for "no absolute unit" and anything unknown.</summary>
    public static double? FromTiffUnit(int unit) => unit switch { 2 => PerInch, 3 => PerCentimetre, _ => null };

    /// <summary>As <see cref="FromTiffUnit"/> for a JFIF density unit (1 = dots per inch, 2 = dots per centimetre; 0 is only an aspect ratio).</summary>
    public static double? FromJfifUnit(int unit) => unit switch { 1 => PerInch, 2 => PerCentimetre, _ => null };
}
