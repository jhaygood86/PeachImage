using System.Runtime.InteropServices;

namespace PeachImage.Benchmarks.Native;

/// <summary>libavif (dav1d decoder, libyuv colour conversion) reference decoder through <c>LibraryImport</c>, single-threaded like PeachImage's decode.</summary>
internal static unsafe partial class Libavif
{
    private const string Lib = "avif";

    [LibraryImport(Lib)] private static partial nint avifDecoderCreate();
    [LibraryImport(Lib)] private static partial void avifDecoderDestroy(nint decoder);
    [LibraryImport(Lib)] private static partial nint avifImageCreateEmpty();
    [LibraryImport(Lib)] private static partial void avifImageDestroy(nint image);
    [LibraryImport(Lib)] private static partial int avifDecoderReadMemory(nint decoder, nint image, byte* data, nuint size);
    [LibraryImport(Lib)] private static partial void avifRGBImageSetDefaults(byte* rgb, nint image);
    [LibraryImport(Lib)] private static partial int avifRGBImageAllocatePixels(byte* rgb);
    [LibraryImport(Lib)] private static partial void avifRGBImageFreePixels(byte* rgb);
    [LibraryImport(Lib)] private static partial int avifImageYUVToRGB(nint image, byte* rgb);
    [LibraryImport(Lib)] private static partial nint avifImageCreate(uint width, uint height, uint depth, int yuvFormat);
    [LibraryImport(Lib)] private static partial int avifImageRGBToYUV(nint image, byte* rgb);
    [LibraryImport(Lib)] private static partial nint avifEncoderCreate();
    [LibraryImport(Lib)] private static partial void avifEncoderDestroy(nint encoder);
    [LibraryImport(Lib)] private static partial int avifEncoderWrite(nint encoder, nint image, byte* output);
    [LibraryImport(Lib)] private static partial void avifRWDataFree(byte* data);

    /// <summary>Decodes to 8-bit RGBA (parse, AV1 decode, YUV to RGB) and returns the image width.</summary>
    public static int Decode(PinnedBytes data)
    {
        nint decoder = avifDecoderCreate();
        nint image = avifImageCreateEmpty();
        byte* rgb = stackalloc byte[512];
        bool allocated = false;
        try
        {
            *(int*)((byte*)decoder + 4) = 1; // maxThreads
            Check(avifDecoderReadMemory(decoder, image, data.Ptr, (nuint)data.Data.Length));
            avifRGBImageSetDefaults(rgb, image);
            Check(avifRGBImageAllocatePixels(rgb));
            allocated = true;
            Check(avifImageYUVToRGB(image, rgb));
            return *(int*)(byte*)image;
        }
        finally
        {
            if (allocated)
            {
                avifRGBImageFreePixels(rgb);
            }

            avifImageDestroy(image);
            avifDecoderDestroy(decoder);
        }
    }

    /// <summary>
    /// Encodes 8-bit RGBA pixels with libaom (RGB to YUV, AV1 encode, container write), single-threaded, and returns
    /// the output size. <paramref name="lossless"/> uses 4:4:4, identity matrix coefficients, full range and quality 100;
    /// otherwise 4:2:0 at <paramref name="quality"/>.
    /// </summary>
    public static int Encode(PinnedBytes rgba, int width, int height, bool lossless, int quality, int speed)
    {
        // avifPixelFormat: 1 = YUV444, 3 = YUV420.
        nint image = avifImageCreate((uint)width, (uint)height, 8, lossless ? 1 : 3);
        nint encoder = avifEncoderCreate();
        byte* rgb = stackalloc byte[512];
        byte* output = stackalloc byte[16];
        output[0] = 0;
        new Span<byte>(output, 16).Clear();
        try
        {
            if (lossless)
            {
                *(int*)((byte*)image + 16) = 1;    // yuvRange = full
                *(int*)((byte*)image + 112) = 0;   // matrixCoefficients = identity
            }

            avifRGBImageSetDefaults(rgb, image);
            *(byte**)(rgb + 48) = rgba.Ptr;        // pixels
            *(uint*)(rgb + 56) = (uint)(width * 4); // rowBytes
            Check(avifImageRGBToYUV(image, rgb));

            *(int*)((byte*)encoder + 4) = 1;       // maxThreads
            *(int*)((byte*)encoder + 8) = speed;   // speed
            *(int*)((byte*)encoder + 32) = lossless ? 100 : quality; // quality
            *(int*)((byte*)encoder + 36) = lossless ? 100 : quality; // qualityAlpha
            Check(avifEncoderWrite(encoder, image, output));
            return (int)*(nuint*)(output + 8);
        }
        finally
        {
            avifRWDataFree(output);
            avifEncoderDestroy(encoder);
            avifImageDestroy(image);
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"libavif returned result {result}");
        }
    }
}
