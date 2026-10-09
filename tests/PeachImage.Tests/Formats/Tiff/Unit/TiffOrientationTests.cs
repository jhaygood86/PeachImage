namespace PeachImage.Tests.Formats.Tiff.Unit;

/// <summary><see cref="ImageInfo.Orientation"/> for TIFF: the Orientation tag (274) of the first IFD, which uses the same 1-8 values as EXIF.</summary>
public class TiffOrientationTests
{
    [Theory]
    [InlineData(true, 1, ImageOrientation.Normal)]
    [InlineData(true, 2, ImageOrientation.MirrorHorizontal)]
    [InlineData(true, 3, ImageOrientation.Rotate180)]
    [InlineData(true, 4, ImageOrientation.MirrorVertical)]
    [InlineData(true, 5, ImageOrientation.Transpose)]
    [InlineData(true, 6, ImageOrientation.Rotate90)]
    [InlineData(true, 7, ImageOrientation.Transverse)]
    [InlineData(true, 8, ImageOrientation.Rotate270)]
    [InlineData(false, 2, ImageOrientation.MirrorHorizontal)]
    [InlineData(false, 6, ImageOrientation.Rotate90)]
    [InlineData(false, 8, ImageOrientation.Rotate270)]
    [InlineData(true, 0, ImageOrientation.Normal)]
    [InlineData(false, 9, ImageOrientation.Normal)]
    public void Identify_ReportsOrientationTag(bool littleEndian, int value, ImageOrientation expected)
    {
        var info = Identify(Build(value, littleEndian));

        Assert.Equal(expected, info.Orientation);
        Assert.False(info.HasPreview);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Identify_WithoutOrientationTag_ReportsNormal(bool littleEndian)
    {
        Assert.Equal(ImageOrientation.Normal, Identify(Build(null, littleEndian)).Orientation);
    }

    [Fact]
    public void Decode_DoesNotApplyOrientation()
    {
        byte[] file = new TiffFixtureBuilder
        {
            Width = 3,
            Height = 2,
            Strips = [new byte[6]],
            Orientation = 6,
        }.Build();

        using var image = Image.Load(new MemoryStream(file));

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
    }

    private static ImageInfo Identify(byte[] file) => Image.Identify(new MemoryStream(file));

    private static byte[] Build(int? orientation, bool littleEndian) => new TiffFixtureBuilder
    {
        Width = 1,
        Height = 1,
        Strips = [[0]],
        LittleEndian = littleEndian,
        Orientation = orientation,
    }.Build();
}
