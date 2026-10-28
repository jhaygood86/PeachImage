using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeachImage.Benchmarks.Native;

/// <summary>
/// Locates the reference C/C++ codec DLLs (libjxl, OpenJPEG, libtiff, libavif) used as comparison baselines where SkiaSharp has no
/// codec. The directory comes from <c>PEACH_NATIVE_DIR</c>; see <c>Native/README.md</c> for how the DLLs are built.
/// </summary>
internal static partial class NativeLibs
{
    public static readonly string Directory =
        Environment.GetEnvironmentVariable("PEACH_NATIVE_DIR") ?? @"C:\Sources\GoogleSource\vcpkg-native\x64-windows\bin";

    [ModuleInitializer]
    internal static void Init()
    {
        if (OperatingSystem.IsWindows() && System.IO.Directory.Exists(Directory))
        {
            SetDllDirectoryW(Directory);
        }
    }

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string path);

    public static nint GetExport(string library, string symbol) =>
        NativeLibrary.GetExport(NativeLibrary.Load(library), symbol);
}

/// <summary>A pinned byte array exposed as a read-only native memory source for codec stream callbacks.</summary>
internal unsafe struct MemSource
{
    public byte* Data;
    public long Length;
    public long Pos;

    public nuint Read(void* destination, nuint count)
    {
        long remaining = Length - Pos;
        if (remaining <= 0)
        {
            return nuint.MaxValue;
        }

        long n = Math.Min((long)count, remaining);
        Buffer.MemoryCopy(Data + Pos, destination, n, n);
        Pos += n;
        return (nuint)n;
    }

    public long Skip(long offset)
    {
        long next = Math.Clamp(Pos + offset, 0, Length);
        long delta = next - Pos;
        Pos = next;
        return delta;
    }

    public bool Seek(long position)
    {
        if (position < 0 || position > Length)
        {
            return false;
        }

        Pos = position;
        return true;
    }
}

internal sealed unsafe class PinnedBytes : IDisposable
{
    private GCHandle _handle;

    public PinnedBytes(byte[] data)
    {
        Data = data;
        _handle = GCHandle.Alloc(data, GCHandleType.Pinned);
    }

    public byte[] Data { get; }

    public byte* Ptr => (byte*)_handle.AddrOfPinnedObject();

    public MemSource Source() => new() { Data = Ptr, Length = Data.Length };

    public void Dispose() => _handle.Free();
}
