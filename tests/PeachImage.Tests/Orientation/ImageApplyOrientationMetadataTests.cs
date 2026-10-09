using PeachImage.Formats.Shared.Metadata;

namespace PeachImage.Tests.Orientation;

public class ImageApplyOrientationMetadataTests
{
    // A minimal TIFF: header, one IFD0 entry (orientation, SHORT, count 1), no next IFD.
    private static byte[] BuildExif(int orientation, bool littleEndian, bool withSignature)
    {
        var tiff = new List<byte>();
        void U16(int v)
        {
            if (littleEndian)
            {
                tiff.Add((byte)v);
                tiff.Add((byte)(v >> 8));
            }
            else
            {
                tiff.Add((byte)(v >> 8));
                tiff.Add((byte)v);
            }
        }

        void U32(uint v)
        {
            if (littleEndian)
            {
                tiff.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
            }
            else
            {
                tiff.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            }
        }

        tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(8);
        U16(1);
        U16(0x0112);
        U16(3);
        U32(1);
        U16(orientation);
        U16(0);
        U32(0);

        return withSignature ? [.. "Exif\0\0"u8.ToArray(), .. tiff] : [.. tiff];
    }

    private static Image CreateWithExif(byte[] exif, int width = 4, int height = 3)
    {
        var image = Image.Create(width, height, PixelFormat.Rgb24);
        image.GetPixelSpan().Fill(7);
        image.Metadata.Profiles.Add(new RawMetadataProfile { Kind = MetadataProfileKind.Exif, Data = exif });
        return image;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ExifOrientation_IsResetOnTheResult_AndTheSourceProfileIsUntouched(bool littleEndian, bool withSignature)
    {
        byte[] exif = BuildExif(6, littleEndian, withSignature);
        byte[] exifBefore = (byte[])exif.Clone();
        using var source = CreateWithExif(exif);
        var sourceProfile = source.Metadata.Profiles[0];

        using var result = source.ApplyOrientation(ImageOrientation.Rotate90);

        var resultProfile = Assert.Single(result.Metadata.Profiles);
        Assert.Equal(MetadataProfileKind.Exif, resultProfile.Kind);
        Assert.Equal(ImageOrientation.Normal, ExifOrientationReader.Read(resultProfile.Data));
        Assert.Equal(exifBefore.Length, resultProfile.Data.Length);

        // Exactly the two value bytes differ.
        int differing = 0;
        for (int i = 0; i < exifBefore.Length; i++)
        {
            differing += exifBefore[i] != resultProfile.Data[i] ? 1 : 0;
        }

        Assert.Equal(1, differing); // 6 -> 1 changes a single byte (the other byte of the SHORT is 0 either way).

        Assert.Same(sourceProfile, source.Metadata.Profiles[0]);
        Assert.Same(exif, sourceProfile.Data);
        Assert.Equal(exifBefore, sourceProfile.Data);
        Assert.Equal(ImageOrientation.Rotate90, ExifOrientationReader.Read(sourceProfile.Data));
    }

    [Fact]
    public void ExifWithNothingToReset_IsSharedRatherThanCopied()
    {
        using var source = CreateWithExif(BuildExif(1, littleEndian: true, withSignature: false));
        using var result = source.ApplyOrientation(ImageOrientation.MirrorHorizontal);
        Assert.Same(source.Metadata.Profiles[0], result.Metadata.Profiles[0]);
    }

    [Fact]
    public void MalformedExif_IsCarriedOverUnchanged()
    {
        byte[] garbage = [1, 2, 3, 4, 5];
        using var source = CreateWithExif(garbage);
        using var result = source.ApplyOrientation(ImageOrientation.Rotate180);
        Assert.Equal(garbage, Assert.Single(result.Metadata.Profiles).Data);
    }

    [Fact]
    public void OtherProfiles_AreCarriedOverAsTheyAre()
    {
        using var source = Image.Create(4, 3, PixelFormat.Rgb24);
        var icc = new RawMetadataProfile { Kind = MetadataProfileKind.Icc, Data = [1, 2, 3] };
        var xmp = new RawMetadataProfile { Kind = MetadataProfileKind.Xmp, Data = [4, 5] };
        source.Metadata.Profiles.Add(icc);
        source.Metadata.Profiles.Add(xmp);

        using var result = source.ApplyOrientation(ImageOrientation.Transpose);

        Assert.Equal(2, result.Metadata.Profiles.Count);
        Assert.Same(icc, result.Metadata.Profiles[0]);
        Assert.Same(xmp, result.Metadata.Profiles[1]);
    }

    [Theory]
    [InlineData(ImageOrientation.Transpose, true)]
    [InlineData(ImageOrientation.Rotate90, true)]
    [InlineData(ImageOrientation.Transverse, true)]
    [InlineData(ImageOrientation.Rotate270, true)]
    [InlineData(ImageOrientation.MirrorHorizontal, false)]
    [InlineData(ImageOrientation.Rotate180, false)]
    [InlineData(ImageOrientation.MirrorVertical, false)]
    public void Resolution_IsSwappedExactlyWhenTheDimensionsAre(ImageOrientation orientation, bool swaps)
    {
        using var source = Image.Create(4, 3, PixelFormat.Rgba32);
        source.Metadata.HorizontalResolution = 72;
        source.Metadata.VerticalResolution = 300;
        source.HasAlpha = true;

        using var result = source.ApplyOrientation(orientation);

        Assert.Equal(swaps ? 300 : 72, result.Metadata.HorizontalResolution);
        Assert.Equal(swaps ? 72 : 300, result.Metadata.VerticalResolution);
        Assert.True(result.HasAlpha);
        Assert.False(result.IsAnimated);
        Assert.Equal(72, source.Metadata.HorizontalResolution);
        Assert.Equal(300, source.Metadata.VerticalResolution);
    }

    [Fact]
    public void MissingResolution_StaysMissing()
    {
        using var source = Image.Create(4, 3, PixelFormat.Gray8);
        using var result = source.ApplyOrientation(ImageOrientation.Rotate90);
        Assert.Null(result.Metadata.HorizontalResolution);
        Assert.Null(result.Metadata.VerticalResolution);
    }

    [Fact]
    public void DestinationForm_LeavesDestinationMetadataAlone()
    {
        using var source = CreateWithExif(BuildExif(6, true, false));
        using var destination = Image.Create(3, 4, PixelFormat.Rgb24);
        destination.Metadata.HorizontalResolution = 123;

        source.ApplyOrientation(ImageOrientation.Rotate90, destination);

        Assert.Empty(destination.Metadata.Profiles);
        Assert.Equal(123, destination.Metadata.HorizontalResolution);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExifOrientationResetter_HandlesBothEndiansAndIsANoOpOnBadData(bool littleEndian)
    {
        byte[] exif = BuildExif(8, littleEndian, withSignature: true);
        Assert.True(ExifOrientationResetter.TryResetOrientation(exif));
        Assert.Equal(ImageOrientation.Normal, ExifOrientationReader.Read(exif));

        // Already 1: nothing to do, nothing touched.
        byte[] normal = BuildExif(1, littleEndian, withSignature: false);
        byte[] normalBefore = (byte[])normal.Clone();
        Assert.False(ExifOrientationResetter.TryResetOrientation(normal));
        Assert.Equal(normalBefore, normal);

        // Truncated anywhere before the end of the orientation entry (6 signature + 8 header + 2 count + 12 entry bytes):
        // never throws, never modifies, never claims a change.
        byte[] full = BuildExif(6, littleEndian, withSignature: true);
        for (int length = 0; length < 28; length++)
        {
            byte[] truncated = full[..length];
            byte[] snapshot = (byte[])truncated.Clone();
            Assert.False(ExifOrientationResetter.TryResetOrientation(truncated));
            Assert.Equal(snapshot, truncated);
        }

        // Wrong magic number and a non-SHORT type are left alone.
        byte[] badMagic = BuildExif(6, littleEndian, withSignature: false);
        badMagic[littleEndian ? 2 : 3] = 43;
        Assert.False(ExifOrientationResetter.TryResetOrientation(badMagic));

        byte[] badType = BuildExif(6, littleEndian, withSignature: false);
        badType[8 + 2 + 2 + (littleEndian ? 0 : 1)] = 4;
        Assert.False(ExifOrientationResetter.TryResetOrientation(badType));
    }
}
