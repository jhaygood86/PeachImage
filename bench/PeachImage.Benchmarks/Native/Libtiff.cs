using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeachImage.Benchmarks.Native;

/// <summary>libtiff reference decoder through <c>LibraryImport</c>, reading from memory and decoding strip-by-strip into one raster.</summary>
internal static unsafe partial class Libtiff
{
    private const string Lib = "tiff";
    private const uint TagImageWidth = 256;
    private const uint TagImageLength = 257;

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint TIFFClientOpen(string name, string mode, void* handle, nint read, nint write, nint seek, nint close, nint size, nint map, nint unmap);
    [LibraryImport(Lib)] private static partial void TIFFClose(nint tif);
    [LibraryImport(Lib)] private static partial int TIFFGetField(nint tif, uint tag, uint* value);
    [LibraryImport(Lib)] private static partial nint TIFFScanlineSize(nint tif);
    [LibraryImport(Lib)] private static partial int TIFFReadScanline(nint tif, void* buffer, uint row, ushort sample);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Read(void* handle, void* buffer, nint count)
    {
        nuint n = ((MemSource*)handle)->Read(buffer, (nuint)count);
        return n == nuint.MaxValue ? 0 : (nint)n;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Write(void* handle, void* buffer, nint count) => -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong Seek(void* handle, ulong offset, int whence)
    {
        var s = (MemSource*)handle;
        long target = whence switch { 0 => (long)offset, 1 => s->Pos + (long)offset, _ => s->Length + (long)offset };
        return s->Seek(target) ? (ulong)target : ulong.MaxValue;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Close(void* handle) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong Size(void* handle) => (ulong)((MemSource*)handle)->Length;

    /// <summary>Decodes every scanline of the first image into one buffer (native sample layout) and returns it.</summary>
    public static byte[] Decode(PinnedBytes data)
    {
        MemSource source = data.Source();
        nint tif = TIFFClientOpen(
            "memory",
            "rm",
            &source,
            (nint)(delegate* unmanaged[Cdecl]<void*, void*, nint, nint>)&Read,
            (nint)(delegate* unmanaged[Cdecl]<void*, void*, nint, nint>)&Write,
            (nint)(delegate* unmanaged[Cdecl]<void*, ulong, int, ulong>)&Seek,
            (nint)(delegate* unmanaged[Cdecl]<void*, int>)&Close,
            (nint)(delegate* unmanaged[Cdecl]<void*, ulong>)&Size,
            0,
            0);
        if (tif == 0)
        {
            throw new InvalidOperationException("TIFFClientOpen failed");
        }

        try
        {
            uint width, height;
            if (TIFFGetField(tif, TagImageWidth, &width) == 0 || TIFFGetField(tif, TagImageLength, &height) == 0)
            {
                throw new InvalidOperationException("TIFF is missing its dimensions");
            }

            int stride = (int)TIFFScanlineSize(tif);
            byte[] raster = new byte[(long)stride * height];
            fixed (byte* p = raster)
            {
                for (uint row = 0; row < height; row++)
                {
                    if (TIFFReadScanline(tif, p + (long)row * stride, row, 0) < 0)
                    {
                        throw new InvalidOperationException($"TIFFReadScanline failed at row {row}");
                    }
                }
            }

            return raster;
        }
        finally
        {
            TIFFClose(tif);
        }
    }
}
