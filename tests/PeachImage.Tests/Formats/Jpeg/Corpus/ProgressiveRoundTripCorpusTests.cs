using PeachImage.Formats.Jpeg;

namespace PeachImage.Tests.Formats.Jpeg.Corpus;

/// <summary>
/// Re-encodes a small sample of real-world JPEGs as progressive and confirms the result decodes without
/// throwing and preserves dimensions — a modest sanity check that the progressive encoder holds up on
/// corpus-sourced images, not a full differential-fidelity sweep (see <see cref="MozjpegCorpusTests"/> for
/// that, on the decode side).
/// </summary>
public class ProgressiveRoundTripCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFileSource.MozjpegFilesSample), MemberType = typeof(CorpusFileSource))]
    public void ReencodesAsProgressiveAndDecodesBack(string path)
    {
        Image source;
        try
        {
            source = Image.Load(path);
        }
        catch (JpegFormatException)
        {
            return;
        }

        using var ms = new MemoryStream();
        source.Save(ms, "jpeg", new JpegEncoderOptions { Quality = 85, Progressive = true });

        ms.Position = 0;
        var decoded = Image.Load(ms);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
    }
}
