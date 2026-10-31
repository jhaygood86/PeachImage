namespace PeachImage;

/// <summary>
/// The raw GPS location tags found in an image's EXIF metadata (the GPS IFD, tags <c>GPSLatitude</c>, <c>GPSLongitude</c>,
/// <c>GPSDestLatitude</c> and <c>GPSDestLongitude</c> and their <c>Ref</c> companions), exactly as recorded: nothing is
/// geocoded, rounded or signed. Each coordinate is the unsigned decimal-degree value of the file's degrees/minutes/seconds
/// rationals; the hemisphere is in the matching <c>Ref</c> member (<c>'N'</c>/<c>'S'</c>, <c>'E'</c>/<c>'W'</c>), so a caller
/// wanting a signed value negates for <c>'S'</c> and <c>'W'</c>.
/// </summary>
/// <remarks>
/// A member is <see langword="null"/> when its tag is absent <em>or</em> present but malformed (a zero denominator, a value
/// that runs past the end of the EXIF block, the wrong type). The presence of a malformed tag is still reported by
/// <see cref="ImageMetadata.HasLocation"/>, since the location question is about what the metadata names, not whether
/// this reader could make sense of it.
/// </remarks>
/// <param name="Latitude">The <c>GPSLatitude</c> value in unsigned decimal degrees, if present and well-formed.</param>
/// <param name="LatitudeRef">The <c>GPSLatitudeRef</c> character (<c>'N'</c> or <c>'S'</c>), if present.</param>
/// <param name="Longitude">The <c>GPSLongitude</c> value in unsigned decimal degrees, if present and well-formed.</param>
/// <param name="LongitudeRef">The <c>GPSLongitudeRef</c> character (<c>'E'</c> or <c>'W'</c>), if present.</param>
/// <param name="DestLatitude">The <c>GPSDestLatitude</c> value in unsigned decimal degrees, if present and well-formed.</param>
/// <param name="DestLatitudeRef">The <c>GPSDestLatitudeRef</c> character, if present.</param>
/// <param name="DestLongitude">The <c>GPSDestLongitude</c> value in unsigned decimal degrees, if present and well-formed.</param>
/// <param name="DestLongitudeRef">The <c>GPSDestLongitudeRef</c> character, if present.</param>
public readonly record struct ExifLocation(
    double? Latitude = null,
    char? LatitudeRef = null,
    double? Longitude = null,
    char? LongitudeRef = null,
    double? DestLatitude = null,
    char? DestLatitudeRef = null,
    double? DestLongitude = null,
    char? DestLongitudeRef = null);
