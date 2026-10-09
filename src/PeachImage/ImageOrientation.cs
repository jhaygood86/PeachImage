namespace PeachImage;

/// <summary>
/// The orientation recorded in an image's metadata (EXIF tag 0x0112 and its equivalents in other formats), which a viewer
/// applies to the decoded pixels to display the image upright. Decoders never apply it themselves: the pixels and
/// <see cref="ImageInfo.Width"/>/<see cref="ImageInfo.Height"/> are always as stored in the file.
/// </summary>
/// <remarks>The numeric values match the EXIF orientation values 1 to 8. Each name describes how the stored image is transformed to display it upright.</remarks>
public enum ImageOrientation
{
    /// <summary>The stored pixels are upright (EXIF 1). Also the value when the file records no orientation.</summary>
    Normal = 1,

    /// <summary>Mirror horizontally (EXIF 2).</summary>
    MirrorHorizontal = 2,

    /// <summary>Rotate 180 degrees (EXIF 3).</summary>
    Rotate180 = 3,

    /// <summary>Mirror vertically (EXIF 4).</summary>
    MirrorVertical = 4,

    /// <summary>Transpose: mirror across the main diagonal (EXIF 5).</summary>
    Transpose = 5,

    /// <summary>Rotate 90 degrees clockwise (EXIF 6).</summary>
    Rotate90 = 6,

    /// <summary>Transverse: mirror across the anti-diagonal (EXIF 7).</summary>
    Transverse = 7,

    /// <summary>Rotate 270 degrees clockwise, that is 90 counter-clockwise (EXIF 8).</summary>
    Rotate270 = 8,
}
