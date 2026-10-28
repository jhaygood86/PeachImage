using System.Runtime.InteropServices;

namespace PeachImage.Benchmarks.Native;

/// <summary>libjxl reference decoder/encoder through <c>LibraryImport</c>, run on the same number of threads PeachImage uses.</summary>
internal static unsafe partial class Libjxl
{
    private const string Lib = "jxl";
    private const string Threads = "jxl_threads";

    private const int EventBasicInfo = 0x40;
    private const int EventFullImage = 0x1000;
    private const int StatusSuccess = 0;
    private const int StatusNeedMoreOutput = 2;
    private const int StatusNeedImageOutBuffer = 5;

    private static readonly nint RunnerFunction = NativeLibs.GetExport(Threads, "JxlResizableParallelRunner");

    [LibraryImport(Lib)] private static partial nint JxlDecoderCreate(nint memoryManager);
    [LibraryImport(Lib)] private static partial void JxlDecoderDestroy(nint dec);
    [LibraryImport(Lib)] private static partial int JxlDecoderSubscribeEvents(nint dec, int events);
    [LibraryImport(Lib)] private static partial int JxlDecoderSetParallelRunner(nint dec, nint runner, nint opaque);
    [LibraryImport(Lib)] private static partial int JxlDecoderSetInput(nint dec, byte* data, nuint size);
    [LibraryImport(Lib)] private static partial void JxlDecoderCloseInput(nint dec);
    [LibraryImport(Lib)] private static partial int JxlDecoderProcessInput(nint dec);
    [LibraryImport(Lib)] private static partial int JxlDecoderGetBasicInfo(nint dec, byte* info);
    [LibraryImport(Lib)] private static partial int JxlDecoderImageOutBufferSize(nint dec, JxlPixelFormat* format, nuint* size);
    [LibraryImport(Lib)] private static partial int JxlDecoderSetImageOutBuffer(nint dec, JxlPixelFormat* format, void* buffer, nuint size);

    [LibraryImport(Lib)] private static partial nint JxlEncoderCreate(nint memoryManager);
    [LibraryImport(Lib)] private static partial void JxlEncoderDestroy(nint enc);
    [LibraryImport(Lib)] private static partial int JxlEncoderSetParallelRunner(nint enc, nint runner, nint opaque);
    [LibraryImport(Lib)] private static partial void JxlEncoderInitBasicInfo(byte* info);
    [LibraryImport(Lib)] private static partial int JxlEncoderSetBasicInfo(nint enc, byte* info);
    [LibraryImport(Lib)] private static partial void JxlColorEncodingSetToSRGB(byte* colorEncoding, int isGray);
    [LibraryImport(Lib)] private static partial int JxlEncoderSetColorEncoding(nint enc, byte* colorEncoding);
    [LibraryImport(Lib)] private static partial nint JxlEncoderFrameSettingsCreate(nint enc, nint source);
    [LibraryImport(Lib)] private static partial int JxlEncoderSetFrameLossless(nint settings, int lossless);
    [LibraryImport(Lib)] private static partial int JxlEncoderSetFrameDistance(nint settings, float distance);
    [LibraryImport(Lib)] private static partial float JxlEncoderDistanceFromQuality(float quality);
    [LibraryImport(Lib)] private static partial int JxlEncoderFrameSettingsSetOption(nint settings, int option, long value);
    [LibraryImport(Lib)] private static partial int JxlEncoderAddImageFrame(nint settings, JxlPixelFormat* format, void* pixels, nuint size);
    [LibraryImport(Lib)] private static partial void JxlEncoderCloseInput(nint enc);
    [LibraryImport(Lib)] private static partial int JxlEncoderProcessOutput(nint enc, byte** next, nuint* available);

    [LibraryImport(Threads)] private static partial nint JxlResizableParallelRunnerCreate(nint memoryManager);
    [LibraryImport(Threads)] private static partial void JxlResizableParallelRunnerSetThreads(nint runner, nuint threads);

    [StructLayout(LayoutKind.Sequential)]
    private struct JxlPixelFormat
    {
        public uint NumChannels;
        public int DataType; // JXL_TYPE_UINT8 = 2
        public int Endianness;
        public nuint Align;
    }

    /// <summary>Decodes to 8-bit interleaved samples (RGB, RGBA or gray as the file dictates) and returns the pixel buffer.</summary>
    public static byte[] Decode(byte[] data)
    {
        nint dec = JxlDecoderCreate(0);
        nint runner = JxlResizableParallelRunnerCreate(0);
        JxlResizableParallelRunnerSetThreads(runner, (nuint)Environment.ProcessorCount);
        try
        {
            Check(JxlDecoderSetParallelRunner(dec, RunnerFunction, runner));
            Check(JxlDecoderSubscribeEvents(dec, EventBasicInfo | EventFullImage));
            byte[] pixels = [];
            var format = new JxlPixelFormat { DataType = 2 };
            byte* info = stackalloc byte[512];
            fixed (byte* input = data)
            {
                Check(JxlDecoderSetInput(dec, input, (nuint)data.Length));
                JxlDecoderCloseInput(dec);
                while (true)
                {
                    int status = JxlDecoderProcessInput(dec);
                    if (status == EventBasicInfo)
                    {
                        Check(JxlDecoderGetBasicInfo(dec, info));
                        uint color = *(uint*)(info + 52);
                        uint alphaBits = *(uint*)(info + 60);
                        format.NumChannels = color + (alphaBits > 0 ? 1u : 0u);
                    }
                    else if (status == StatusNeedImageOutBuffer)
                    {
                        nuint size;
                        Check(JxlDecoderImageOutBufferSize(dec, &format, &size));
                        pixels = new byte[size];
                        fixed (byte* output = pixels)
                        {
                            Check(JxlDecoderSetImageOutBuffer(dec, &format, output, size));
                            DrainFullImage(dec);
                        }

                        return pixels;
                    }
                    else if (status == StatusSuccess)
                    {
                        return pixels;
                    }
                    else if (status != EventFullImage)
                    {
                        throw new InvalidOperationException($"libjxl decode status {status}");
                    }
                }
            }
        }
        finally
        {
            JxlDecoderDestroy(dec);
            JxlResizableParallelRunnerDestroy(runner);
        }
    }

    [LibraryImport(Threads)] private static partial void JxlResizableParallelRunnerDestroy(nint runner);

    private static void DrainFullImage(nint dec)
    {
        while (true)
        {
            int status = JxlDecoderProcessInput(dec);
            if (status == EventFullImage || status == StatusSuccess)
            {
                return;
            }

            if (status != 0x400 /* FRAME */)
            {
                throw new InvalidOperationException($"libjxl decode status {status}");
            }
        }
    }

    /// <summary>Encodes 8-bit interleaved gray (1) or RGB (3) samples; <paramref name="distance"/> &lt;= 0 selects lossless.</summary>
    public static MemoryStream Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, bool lossless, float quality, int effort)
    {
        nint enc = JxlEncoderCreate(0);
        nint runner = JxlResizableParallelRunnerCreate(0);
        JxlResizableParallelRunnerSetThreads(runner, (nuint)Environment.ProcessorCount);
        try
        {
            Check(JxlEncoderSetParallelRunner(enc, RunnerFunction, runner));
            byte* info = stackalloc byte[512];
            JxlEncoderInitBasicInfo(info);
            *(uint*)(info + 4) = (uint)width;
            *(uint*)(info + 8) = (uint)height;
            *(uint*)(info + 12) = 8;
            *(uint*)(info + 16) = 0;
            *(int*)(info + 36) = lossless ? 1 : 0; // uses_original_profile
            *(uint*)(info + 52) = (uint)channels;
            Check(JxlEncoderSetBasicInfo(enc, info));
            byte* color = stackalloc byte[256];
            JxlColorEncodingSetToSRGB(color, channels == 1 ? 1 : 0);
            Check(JxlEncoderSetColorEncoding(enc, color));

            nint settings = JxlEncoderFrameSettingsCreate(enc, 0);
            Check(JxlEncoderFrameSettingsSetOption(settings, 0 /* EFFORT */, effort));
            if (lossless)
            {
                Check(JxlEncoderSetFrameLossless(settings, 1));
            }
            else
            {
                Check(JxlEncoderSetFrameDistance(settings, JxlEncoderDistanceFromQuality(quality)));
            }

            var format = new JxlPixelFormat { NumChannels = (uint)channels, DataType = 2 };
            fixed (byte* p = pixels)
            {
                Check(JxlEncoderAddImageFrame(settings, &format, p, (nuint)pixels.Length));
            }

            JxlEncoderCloseInput(enc);

            var output = new MemoryStream();
            byte[] chunk = new byte[1 << 16];
            int status;
            do
            {
                fixed (byte* c = chunk)
                {
                    byte* next = c;
                    nuint available = (nuint)chunk.Length;
                    status = JxlEncoderProcessOutput(enc, &next, &available);
                    output.Write(chunk, 0, chunk.Length - (int)available);
                }
            }
            while (status == StatusNeedMoreOutput);

            Check(status);
            return output;
        }
        finally
        {
            JxlEncoderDestroy(enc);
            JxlResizableParallelRunnerDestroy(runner);
        }
    }

    private static void Check(int status)
    {
        if (status != StatusSuccess)
        {
            throw new InvalidOperationException($"libjxl returned status {status}");
        }
    }
}
