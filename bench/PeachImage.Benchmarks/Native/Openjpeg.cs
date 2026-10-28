using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeachImage.Benchmarks.Native;

/// <summary>OpenJPEG reference decoder through <c>LibraryImport</c>, reading from memory like PeachImage does.</summary>
internal static unsafe partial class Openjpeg
{
    private const string Lib = "openjp2";
    private const int CodecJp2 = 2;

    [LibraryImport(Lib)] private static partial nint opj_stream_create(nuint bufferSize, int isInput);
    [LibraryImport(Lib)] private static partial void opj_stream_destroy(nint stream);
    [LibraryImport(Lib)] private static partial void opj_stream_set_user_data(nint stream, void* data, nint freeFunction);
    [LibraryImport(Lib)] private static partial void opj_stream_set_user_data_length(nint stream, ulong length);
    [LibraryImport(Lib)] private static partial void opj_stream_set_read_function(nint stream, nint function);
    [LibraryImport(Lib)] private static partial void opj_stream_set_skip_function(nint stream, nint function);
    [LibraryImport(Lib)] private static partial void opj_stream_set_seek_function(nint stream, nint function);
    [LibraryImport(Lib)] private static partial nint opj_create_decompress(int format);
    [LibraryImport(Lib)] private static partial void opj_destroy_codec(nint codec);
    [LibraryImport(Lib)] private static partial void opj_set_default_decoder_parameters(byte* parameters);
    [LibraryImport(Lib)] private static partial int opj_setup_decoder(nint codec, byte* parameters);
    [LibraryImport(Lib)] private static partial int opj_codec_set_threads(nint codec, int threads);
    [LibraryImport(Lib)] private static partial int opj_read_header(nint stream, nint codec, nint* image);
    [LibraryImport(Lib)] private static partial int opj_decode(nint codec, nint stream, nint image);
    [LibraryImport(Lib)] private static partial int opj_end_decompress(nint codec, nint stream);
    [LibraryImport(Lib)] private static partial void opj_image_destroy(nint image);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Read(void* buffer, nuint count, void* user) => ((MemSource*)user)->Read(buffer, count);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long Skip(long count, void* user) => ((MemSource*)user)->Skip(count);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Seek(long position, void* user) => ((MemSource*)user)->Seek(position) ? 1 : 0;

    /// <summary>Decodes a JP2 file to planar 32-bit components (OpenJPEG's native output); returns the first component's width.</summary>
    public static int Decode(PinnedBytes data, int reduceResolutionLevels = 0)
    {
        MemSource source = data.Source();
        nint stream = opj_stream_create(1 << 16, 1);
        opj_stream_set_user_data(stream, &source, 0);
        opj_stream_set_user_data_length(stream, (ulong)source.Length);
        opj_stream_set_read_function(stream, (nint)(delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint>)&Read);
        opj_stream_set_skip_function(stream, (nint)(delegate* unmanaged[Cdecl]<long, void*, long>)&Skip);
        opj_stream_set_seek_function(stream, (nint)(delegate* unmanaged[Cdecl]<long, void*, int>)&Seek);

        nint codec = opj_create_decompress(CodecJp2);
        byte* parameters = stackalloc byte[32768]; // opj_dparameters_t embeds two 4 KB path buffers
        opj_set_default_decoder_parameters(parameters);
        *(uint*)parameters = (uint)reduceResolutionLevels; // cp_reduce is the first field of opj_dparameters_t
        nint image = 0;
        try
        {
            Check(opj_setup_decoder(codec, parameters));
            Check(opj_codec_set_threads(codec, Environment.ProcessorCount));
            Check(opj_read_header(stream, codec, &image));
            Check(opj_decode(codec, stream, image));
            Check(opj_end_decompress(codec, stream));
            nint components = *(nint*)(image + 24);
            return (int)*(uint*)(components + 8); // opj_image_comp_t.w
        }
        finally
        {
            if (image != 0)
            {
                opj_image_destroy(image);
            }

            opj_destroy_codec(codec);
            opj_stream_destroy(stream);
        }
    }

    private static void Check(int ok)
    {
        if (ok == 0)
        {
            throw new InvalidOperationException("OpenJPEG call failed");
        }
    }
}
