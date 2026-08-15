
namespace PeachImage.Formats.Png;

/// <summary>
/// A structured view over a <see cref="MetadataProfileKind.Text"/> <see cref="RawMetadataProfile"/>,
/// covering PNG's <c>tEXt</c>/<c>zTXt</c>/<c>iTXt</c> chunks. All three are captured under the single
/// <see cref="MetadataProfileKind.Text"/> kind with <see cref="RawMetadataProfile.Data"/> holding
/// UTF-8 bytes of <c>keyword\0languageTag\0translatedKeyword\0text</c> (language/translated-keyword
/// empty for plain <c>tEXt</c>/<c>zTXt</c>; Latin-1 source text is re-encoded to UTF-8 for a single
/// consistent representation regardless of which chunk type produced it).
/// </summary>
public sealed class PngTextEntry
{
    /// <summary>The chunk's keyword (e.g. "Title", "Author", "Description").</summary>
    public required string Keyword { get; init; }

    /// <summary>The text content.</summary>
    public required string Text { get; init; }

    /// <summary>The RFC 3066 language tag, if this came from an <c>iTXt</c> chunk with one set. Empty otherwise.</summary>
    public string LanguageTag { get; init; } = string.Empty;

    /// <summary>The keyword translated into <see cref="LanguageTag"/>, if this came from an <c>iTXt</c> chunk with one set. Empty otherwise.</summary>
    public string TranslatedKeyword { get; init; } = string.Empty;

    /// <summary>Whether the source chunk stored its text zlib-compressed (<c>zTXt</c>, or compressed <c>iTXt</c>).</summary>
    public bool WasCompressed { get; init; }

    /// <summary>Attempts to parse <paramref name="profile"/> as a PNG text entry.</summary>
    public static bool TryParse(RawMetadataProfile profile, out PngTextEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Kind != MetadataProfileKind.Text)
        {
            entry = null;
            return false;
        }

        string joined = System.Text.Encoding.UTF8.GetString(profile.Data);
        string[] parts = joined.Split('\0', 4);
        if (parts.Length != 4)
        {
            entry = null;
            return false;
        }

        entry = new PngTextEntry
        {
            Keyword = parts[0],
            LanguageTag = parts[1],
            TranslatedKeyword = parts[2],
            Text = parts[3],
        };
        return true;
    }

    /// <summary>Builds the <see cref="RawMetadataProfile"/> representation of this entry.</summary>
    internal RawMetadataProfile ToProfile()
    {
        string joined = string.Concat(Keyword, "\0", LanguageTag, "\0", TranslatedKeyword, "\0", Text);
        return new RawMetadataProfile { Kind = MetadataProfileKind.Text, Data = System.Text.Encoding.UTF8.GetBytes(joined) };
    }
}
