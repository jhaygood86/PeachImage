using PeachImage.Formats.Avif.Container;

namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// <see cref="ImageInfo.Orientation"/> for AVIF, derived from the primary item's <c>irot</c> (anti-clockwise
/// quarter turns) and <c>imir</c> (axis 0 = vertical axis / left-right swap, axis 1 = horizontal axis /
/// top-bottom swap) properties applied in <c>ipma</c> order.
/// </summary>
public class AvifOrientationTests
{
    private static AvifTransform Rot(int quarterTurns) => new(IsMirror: false, quarterTurns);

    private static AvifTransform Mir(int axis) => new(IsMirror: true, axis);

    [Fact]
    public void NoTransforms_IsNormal() =>
        Assert.Equal(ImageOrientation.Normal, AvifTransformProperties.ToOrientation([]));

    [Theory]
    [InlineData(0, ImageOrientation.Normal)]
    [InlineData(1, ImageOrientation.Rotate270)] // 90 anti-clockwise == 270 clockwise
    [InlineData(2, ImageOrientation.Rotate180)]
    [InlineData(3, ImageOrientation.Rotate90)]
    public void Irot_MapsToExifRotation(int angle, ImageOrientation expected) =>
        Assert.Equal(expected, AvifTransformProperties.ToOrientation([Rot(angle)]));

    [Theory]
    [InlineData(0, ImageOrientation.MirrorHorizontal)] // vertical axis: left and right swap
    [InlineData(1, ImageOrientation.MirrorVertical)] // horizontal axis: top and bottom swap
    public void Imir_MapsToExifMirror(int axis, ImageOrientation expected) =>
        Assert.Equal(expected, AvifTransformProperties.ToOrientation([Mir(axis)]));

    [Fact]
    public void RotateThenMirror_OrderMatters()
    {
        Assert.Equal(ImageOrientation.Transverse, AvifTransformProperties.ToOrientation([Rot(1), Mir(0)]));
        Assert.Equal(ImageOrientation.Transpose, AvifTransformProperties.ToOrientation([Mir(0), Rot(1)]));
        Assert.Equal(ImageOrientation.Transpose, AvifTransformProperties.ToOrientation([Rot(3), Mir(0)]));
        Assert.Equal(ImageOrientation.Transverse, AvifTransformProperties.ToOrientation([Mir(0), Rot(3)]));
        Assert.Equal(ImageOrientation.Transpose, AvifTransformProperties.ToOrientation([Rot(1), Mir(1)]));
        Assert.Equal(ImageOrientation.Transverse, AvifTransformProperties.ToOrientation([Mir(1), Rot(1)]));
    }

    [Fact]
    public void AllEightExifValuesAreReachable()
    {
        var reached = new HashSet<ImageOrientation>();
        for (int angle = 0; angle < 4; angle++)
        {
            reached.Add(AvifTransformProperties.ToOrientation([Rot(angle)]));
            reached.Add(AvifTransformProperties.ToOrientation([Rot(angle), Mir(0)]));
        }

        Assert.Equal(8, reached.Count);
    }

    [Fact]
    public void TwoMirrorsOnDifferentAxes_IsRotate180() =>
        Assert.Equal(ImageOrientation.Rotate180, AvifTransformProperties.ToOrientation([Mir(0), Mir(1)]));

    public static TheoryData<(string FourCc, byte Value)[], ImageOrientation> ContainerCases => new()
    {
        { [], ImageOrientation.Normal },
        { [("irot", 1)], ImageOrientation.Rotate270 },
        { [("irot", 3)], ImageOrientation.Rotate90 },
        { [("imir", 0)], ImageOrientation.MirrorHorizontal },
        { [("imir", 1)], ImageOrientation.MirrorVertical },
        { [("irot", 1), ("imir", 0)], ImageOrientation.Transverse },
        { [("imir", 0), ("irot", 1)], ImageOrientation.Transpose },
        { [("irot", 0xFE)], ImageOrientation.Rotate180 }, // only the low 2 bits are the angle
    };

    [Theory]
    [MemberData(nameof(ContainerCases))]
    public void Identify_ReadsIrotAndImirFromPrimaryItem((string FourCc, byte Value)[] transforms, ImageOrientation expected)
    {
        byte[] file = AvifFixtureBuilder.BuildSingleItem(16, 8, transforms: transforms);

        var info = Image.Identify(new MemoryStream(file));

        Assert.Equal(expected, info.Orientation);
        Assert.Equal(16, info.Width);
        Assert.Equal(8, info.Height);
        Assert.False(info.HasPreview);
    }
}
