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

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"libavif returned result {result}");
        }
    }
}
