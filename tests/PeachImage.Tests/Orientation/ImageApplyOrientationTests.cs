namespace PeachImage.Tests.Orientation;

public class ImageApplyOrientationTests
{
    private static readonly PixelFormat[] AllFormats = Enum.GetValues<PixelFormat>();

    private static readonly ImageOrientation[] AllOrientations = Enum.GetValues<ImageOrientation>();

    // 1x1, single rows and columns, odd sizes, sizes straddling the tile (32) and vector (4 / 16 byte) widths, and a few larger ones.
    private static readonly (int Width, int Height)[] Sizes =
    [
        (1, 1), (1, 7), (7, 1), (2, 3), (3, 2), (3, 5), (5, 3), (4, 4), (8, 8), (17, 33), (33, 17),
        (31, 32), (32, 33), (100, 61), (61, 100), (37, 37), (64, 64), (1, 300), (300, 1), (130, 65),
    ];

    public static TheoryData<PixelFormat, ImageOrientation, int, int> Cases()
    {
        var data = new TheoryData<PixelFormat, ImageOrientation, int, int>();
        foreach (var format in AllFormats)
        {
            foreach (var orientation in AllOrientations)
            {
                foreach (var (width, height) in Sizes)
                {
                    data.Add(format, orientation, width, height);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutOfPlace_MatchesPerPixelReference(PixelFormat format, ImageOrientation orientation, int width, int height)
    {
        using var source = OrientationTestHelpers.CreateRandomImage(width, height, format, seed: 1);
        byte[] original = source.GetPixelSpan().ToArray();

        using var result = source.ApplyOrientation(orientation);

        byte[] expected = OrientationTestHelpers.ReferenceApply(original, width, height, format.GetBytesPerPixel(), orientation, out int expectedWidth, out int expectedHeight);
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
        Assert.Equal(format, result.PixelFormat);
        Assert.True(expected.AsSpan().SequenceEqual(result.GetPixelSpan()));
        Assert.True(original.AsSpan().SequenceEqual(source.GetPixelSpan()), "The source must not be modified.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IntoDestination_MatchesPerPixelReference(PixelFormat format, ImageOrientation orientation, int width, int height)
    {
        using var source = OrientationTestHelpers.CreateRandomImage(width, height, format, seed: 2);
        byte[] original = source.GetPixelSpan().ToArray();
        bool swaps = OrientationTestHelpers.SwapsDimensions(orientation);
        using var destination = Image.Create(swaps ? height : width, swaps ? width : height, format);

        // Stale content in the destination must be fully overwritten.
        destination.GetPixelSpan().Fill(0xA5);
        source.ApplyOrientation(orientation, destination);

        byte[] expected = OrientationTestHelpers.ReferenceApply(original, width, height, format.GetBytesPerPixel(), orientation, out _, out _);
        Assert.True(expected.AsSpan().SequenceEqual(destination.GetPixelSpan()));
        Assert.True(original.AsSpan().SequenceEqual(source.GetPixelSpan()), "The source must not be modified.");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void InPlace_MatchesOutOfPlaceWhenDimensionsAreKept(PixelFormat format, ImageOrientation orientation, int width, int height)
    {
        using var image = OrientationTestHelpers.CreateRandomImage(width, height, format, seed: 3);
        byte[] original = image.GetPixelSpan().ToArray();

        if (OrientationTestHelpers.SwapsDimensions(orientation) && width != height)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => image.ApplyOrientationInPlace(orientation));
            Assert.Contains("ApplyOrientation", ex.Message);
            Assert.True(original.AsSpan().SequenceEqual(image.GetPixelSpan()), "A rejected call must leave the pixels alone.");
            return;
        }

        image.ApplyOrientationInPlace(orientation);

        byte[] expected = OrientationTestHelpers.ReferenceApply(original, width, height, format.GetBytesPerPixel(), orientation, out int expectedWidth, out int expectedHeight);
        Assert.Equal(width, expectedWidth);
        Assert.Equal(height, expectedHeight);
        Assert.True(expected.AsSpan().SequenceEqual(image.GetPixelSpan()));
    }

    [Theory]
    [InlineData(PixelFormat.Gray8)]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Rgb48)]
    [InlineData(PixelFormat.RgbF32)]
    [InlineData(PixelFormat.RgbaF32)]
    public void Identities_RoundTripToTheOriginal(PixelFormat format)
    {
        using var source = OrientationTestHelpers.CreateRandomImage(45, 29, format, seed: 4);
        byte[] original = source.GetPixelSpan().ToArray();

        (ImageOrientation First, ImageOrientation Second)[] pairs =
        [
            (ImageOrientation.Rotate90, ImageOrientation.Rotate270),
            (ImageOrientation.Rotate270, ImageOrientation.Rotate90),
            (ImageOrientation.Rotate180, ImageOrientation.Rotate180),
            (ImageOrientation.MirrorHorizontal, ImageOrientation.MirrorHorizontal),
            (ImageOrientation.MirrorVertical, ImageOrientation.MirrorVertical),
            (ImageOrientation.Transpose, ImageOrientation.Transpose),
            (ImageOrientation.Transverse, ImageOrientation.Transverse),
        ];

        foreach (var (first, second) in pairs)
        {
            using var once = source.ApplyOrientation(first);
            using var twice = once.ApplyOrientation(second);
            Assert.Equal(source.Width, twice.Width);
            Assert.Equal(source.Height, twice.Height);
            Assert.True(original.AsSpan().SequenceEqual(twice.GetPixelSpan()), $"{first} then {second}");
        }

        // Four clockwise quarter turns.
        var current = source.ApplyOrientation(ImageOrientation.Rotate90);
        for (int i = 0; i < 3; i++)
        {
            var next = current.ApplyOrientation(ImageOrientation.Rotate90);
            current.Dispose();
            current = next;
        }

        using (current)
        {
            Assert.True(original.AsSpan().SequenceEqual(current.GetPixelSpan()));
        }
    }

    [Fact]
    public void Normal_ReturnsTheSameInstance()
    {
        using var image = Image.Create(3, 2, PixelFormat.Rgb24);
        Assert.Same(image, image.ApplyOrientation(ImageOrientation.Normal));
    }

    [Fact]
    public void Normal_IntoDestination_CopiesAndInPlaceIsANoOp()
    {
        using var source = OrientationTestHelpers.CreateRandomImage(5, 4, PixelFormat.Rgba32, seed: 5);
        using var destination = Image.Create(5, 4, PixelFormat.Rgba32);
        source.ApplyOrientation(ImageOrientation.Normal, destination);
        Assert.True(source.GetPixelSpan().SequenceEqual(destination.GetPixelSpan()));

        byte[] before = source.GetPixelSpan().ToArray();
        source.ApplyOrientationInPlace(ImageOrientation.Normal);
        Assert.True(before.AsSpan().SequenceEqual(source.GetPixelSpan()));
    }

    [Fact]
    public void UndefinedOrientation_Throws()
    {
        using var image = Image.Create(3, 2, PixelFormat.Gray8);
        using var destination = Image.Create(3, 2, PixelFormat.Gray8);
        var bad = (ImageOrientation)9;
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ApplyOrientation(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ApplyOrientation(bad, destination));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ApplyOrientationInPlace(bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ApplyOrientationInPlace((ImageOrientation)0));
    }

    [Fact]
    public void Destination_ThatIsTheSourceOrSharesItsBuffer_Throws()
    {
        using var image = Image.Create(4, 4, PixelFormat.Rgb24);
        foreach (var orientation in AllOrientations)
        {
            Assert.Throws<ArgumentException>(() => image.ApplyOrientation(orientation, image));
        }

        // Two distinct Image instances over the same buffer (as animated-frame images alias their decoder's canvas) are aliasing too.
        var shared = new byte[4 * 4 * 3];
        using var a = OrientationTestHelpers.WrapBuffer(4, 4, PixelFormat.Rgb24, shared);
        using var b = OrientationTestHelpers.WrapBuffer(4, 4, PixelFormat.Rgb24, shared);
        Assert.Throws<ArgumentException>(() => a.ApplyOrientation(ImageOrientation.Rotate90, b));
    }

    [Fact]
    public void Destination_WithWrongFormatOrDimensions_Throws()
    {
        using var image = Image.Create(5, 3, PixelFormat.Rgb24);

        using var wrongFormat = Image.Create(5, 3, PixelFormat.Rgba32);
        Assert.Throws<ArgumentException>(() => image.ApplyOrientation(ImageOrientation.MirrorHorizontal, wrongFormat));

        // A swapping orientation needs the swapped size, a non-swapping one the original size.
        using var unswapped = Image.Create(5, 3, PixelFormat.Rgb24);
        using var swapped = Image.Create(3, 5, PixelFormat.Rgb24);
        Assert.Throws<ArgumentException>(() => image.ApplyOrientation(ImageOrientation.Rotate90, unswapped));
        Assert.Throws<ArgumentException>(() => image.ApplyOrientation(ImageOrientation.Rotate180, swapped));
        image.ApplyOrientation(ImageOrientation.Rotate90, swapped);
        image.ApplyOrientation(ImageOrientation.Rotate180, unswapped);

        // Same pixel count but different shape is still a mismatch.
        using var reshaped = Image.Create(15, 1, PixelFormat.Rgb24);
        Assert.Throws<ArgumentException>(() => image.ApplyOrientation(ImageOrientation.Rotate180, reshaped));

        Assert.Throws<ArgumentNullException>(() => image.ApplyOrientation(ImageOrientation.Rotate180, null!));
    }

    [Fact]
    public void DisposedImages_Throw()
    {
        var image = Image.Create(3, 2, PixelFormat.Gray8);
        using var destination = Image.Create(3, 2, PixelFormat.Gray8);
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.ApplyOrientation(ImageOrientation.Rotate180));
        Assert.Throws<ObjectDisposedException>(() => image.ApplyOrientation(ImageOrientation.Rotate180, destination));
        Assert.Throws<ObjectDisposedException>(() => image.ApplyOrientationInPlace(ImageOrientation.Rotate180));
    }

    [Fact]
    public void DestinationAndInPlaceForms_DoNotAllocate()
    {
        using var source = OrientationTestHelpers.CreateRandomImage(97, 97, PixelFormat.Rgb24, seed: 6);
        using var destination = Image.Create(97, 97, PixelFormat.Rgb24);

        foreach (var orientation in AllOrientations)
        {
            // Warm up so JIT and any one-time static initialisation are excluded.
            source.ApplyOrientation(orientation, destination);
            source.ApplyOrientationInPlace(orientation);

            long before = GC.GetAllocatedBytesForCurrentThread();
            source.ApplyOrientation(orientation, destination);
            source.ApplyOrientationInPlace(orientation);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"{orientation} allocated {allocated} bytes.");
        }
    }

    [Fact]
    public void TinyImage_EveryOrientationMatchesTheHandWrittenResult()
    {
        // Stored 2 wide x 3 tall:   1 2
        //                           3 4
        //                           5 6
        byte[] stored = [1, 2, 3, 4, 5, 6];

        (ImageOrientation Orientation, int Width, int Height, byte[] Expected)[] cases =
        [
            (ImageOrientation.Normal, 2, 3, [1, 2, 3, 4, 5, 6]),
            (ImageOrientation.MirrorHorizontal, 2, 3, [2, 1, 4, 3, 6, 5]),
            (ImageOrientation.Rotate180, 2, 3, [6, 5, 4, 3, 2, 1]),
            (ImageOrientation.MirrorVertical, 2, 3, [5, 6, 3, 4, 1, 2]),

            // Transpose: the main diagonal stays put.   1 3 5 / 2 4 6
            (ImageOrientation.Transpose, 3, 2, [1, 3, 5, 2, 4, 6]),

            // Rotate90 is clockwise: the left column (1 3 5) becomes the top row, read right to left.   5 3 1 / 6 4 2
            (ImageOrientation.Rotate90, 3, 2, [5, 3, 1, 6, 4, 2]),

            // Transverse: the anti-diagonal stays put.   6 4 2 / 5 3 1
            (ImageOrientation.Transverse, 3, 2, [6, 4, 2, 5, 3, 1]),

            // Rotate270 is counter-clockwise: the right column (2 4 6) becomes the top row, read left to right.   2 4 6 / 1 3 5
            (ImageOrientation.Rotate270, 3, 2, [2, 4, 6, 1, 3, 5]),
        ];

        foreach (var (orientation, width, height, expected) in cases)
        {
            using var source = Image.Create(2, 3, PixelFormat.Gray8);
            stored.CopyTo(source.GetPixelSpan());

            using var result = source.ApplyOrientation(orientation);
            Assert.Equal(width, result.Width);
            Assert.Equal(height, result.Height);
            Assert.Equal(expected, result.GetPixelSpan().ToArray());

            using var destination = Image.Create(width, height, PixelFormat.Gray8);
            source.ApplyOrientation(orientation, destination);
            Assert.Equal(expected, destination.GetPixelSpan().ToArray());
        }
    }

    [Fact]
    public void ThreeByteTinyImage_KeepsEachPixelIntact()
    {
        // Pixels (r,g,b) = (10,11,12) (20,21,22) / (30,31,32) (40,41,42): no channel may be reordered by any orientation.
        using var source = Image.Create(2, 2, PixelFormat.Rgb24);
        byte[] stored = [10, 11, 12, 20, 21, 22, 30, 31, 32, 40, 41, 42];
        stored.CopyTo(source.GetPixelSpan());

        using var rotated = source.ApplyOrientation(ImageOrientation.Rotate90);
        Assert.Equal(new byte[] { 30, 31, 32, 10, 11, 12, 40, 41, 42, 20, 21, 22 }, rotated.GetPixelSpan().ToArray());

        using var mirrored = source.ApplyOrientation(ImageOrientation.MirrorHorizontal);
        Assert.Equal(new byte[] { 20, 21, 22, 10, 11, 12, 40, 41, 42, 30, 31, 32 }, mirrored.GetPixelSpan().ToArray());
    }
}
