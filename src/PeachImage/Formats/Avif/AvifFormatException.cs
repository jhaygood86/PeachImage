namespace PeachImage.Formats.Avif;

/// <summary>Base type for exceptions specific to the AVIF codec.</summary>
public class AvifFormatException : ImageFormatException
{
    /// <summary>Initializes a new instance of <see cref="AvifFormatException"/>.</summary>
    public AvifFormatException(string message)
        : base(message, "avif")
    {
    }

    /// <summary>Initializes a new instance of <see cref="AvifFormatException"/> with an inner exception.</summary>
    public AvifFormatException(string message, Exception innerException)
        : base(message, "avif", innerException)
    {
    }
}

/// <summary>Thrown when an AVIF file is malformed or its AV1 bitstream cannot be decoded due to a genuine defect.</summary>
public sealed class AvifDecodingException : AvifFormatException
{
    /// <summary>Initializes a new instance of <see cref="AvifDecodingException"/>.</summary>
    public AvifDecodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="AvifDecodingException"/> with an inner exception.</summary>
    public AvifDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an AVIF file uses a feature that is deliberately out of scope for this decoder (animated
/// AVIF, film grain synthesis, 12-bit depth, gain maps, palette/IntraBC mode, super-resolution) rather
/// than one that is malformed. Kept as a sibling of <see cref="AvifDecodingException"/> (not a subclass)
/// so corpus/differential tests can distinguish "this file deliberately isn't supported, skip it" from
/// "this is a real decoding bug" by catching this narrower type first.
///
/// <para>Also used on the encode side for source <em>images</em> whose content is deliberately out of scope
/// for this encoder rather than malformed -- specifically, an <see cref="Image"/> with a non-opaque alpha
/// channel, since this encoder only produces opaque AVIF still images in this version.</para>
/// </summary>
public sealed class AvifUnsupportedFeatureException : AvifFormatException
{
    /// <summary>Initializes a new instance of <see cref="AvifUnsupportedFeatureException"/>.</summary>
    public AvifUnsupportedFeatureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="AvifUnsupportedFeatureException"/> with an inner exception.</summary>
    public AvifUnsupportedFeatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when an <see cref="Image"/> cannot be encoded as AVIF, either because its pixel format is
/// unsupported by this encoder or its dimensions exceed what it can represent. Alpha-bearing source images
/// are rejected via <see cref="AvifUnsupportedFeatureException"/> instead -- see its own remarks.
/// </summary>
public sealed class AvifEncodingException : AvifFormatException
{
    /// <summary>Initializes a new instance of <see cref="AvifEncodingException"/>.</summary>
    public AvifEncodingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of <see cref="AvifEncodingException"/> with an inner exception.</summary>
    public AvifEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
