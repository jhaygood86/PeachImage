namespace PeachImage;

/// <summary>
/// A raw, un-parsed metadata segment captured verbatim during decode so it can be inspected or
/// re-emitted on encode without the core library needing to understand its internal structure.
/// </summary>
public sealed class RawMetadataProfile
{
    /// <summary>The kind of metadata this profile carries.</summary>
    public required MetadataProfileKind Kind { get; init; }

    /// <summary>The raw bytes of the profile, in whatever encoding its kind natively uses.</summary>
    public required byte[] Data { get; init; }
}
