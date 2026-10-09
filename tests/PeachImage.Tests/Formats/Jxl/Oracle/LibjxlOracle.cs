using System.Diagnostics;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>
/// Drives <c>ffmpeg</c> (built with libjxl) as an independent JPEG XL oracle: it can both encode synthetic test
/// images with libjxl and decode any .jxl with libjxl, so a decode can be checked against the reference
/// implementation without checked-in blobs. Unavailable (so dependent tests skip) when ffmpeg or its libjxl
/// codecs are not on the PATH.
/// </summary>
internal static class LibjxlOracle
{
    private static readonly Lazy<bool> AvailableLazy = new(Probe);

    public static bool IsAvailable => AvailableLazy.Value;

    private static bool Probe()
    {
        try
        {
            string encoders = Run("-hide_banner -encoders", out _).AsText();
            string decoders = Run("-hide_banner -decoders", out _).AsText();
            return encoders.Contains("libjxl", StringComparison.Ordinal) && decoders.Contains("libjxl", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>Encodes a synthetic lavfi source to a JPEG XL file with libjxl.</summary>
    /// <param name="lavfiSource">A lavfi source expression, e.g. <c>testsrc2=size=64x48</c>.</param>
    /// <param name="inputPixelFormat">The pix_fmt fed to the encoder (determines bit depth and channels).</param>
    /// <param name="encoderOptions">Extra libjxl options, e.g. <c>-distance 0 -effort 7</c>.</param>
    public static byte[] Encode(string lavfiSource, string inputPixelFormat, string encoderOptions)
    {
        string path = Path.Combine(Path.GetTempPath(), $"peach-jxl-{Guid.NewGuid():N}.jxl");
        try
        {
            var output = Run(
                $"-v error -y -f lavfi -i \"{lavfiSource}\" -frames:v 1 -vf format={inputPixelFormat} -c:v libjxl {encoderOptions} \"{path}\"",
                out int exitCode);
            if (exitCode != 0 || !File.Exists(path))
            {
                throw new InvalidOperationException($"ffmpeg failed to encode: {output.AsText()}");
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Decodes <paramref name="jxl"/> with libjxl, returning the raw pixels in <paramref name="outputPixelFormat"/>.</summary>
    public static byte[] Decode(byte[] jxl, string outputPixelFormat)
    {
        string path = Path.Combine(Path.GetTempPath(), $"peach-jxl-{Guid.NewGuid():N}.jxl");
        try
        {
            File.WriteAllBytes(path, jxl);
            var raw = Run($"-v error -i \"{path}\" -fps_mode passthrough -pix_fmt {outputPixelFormat} -f rawvideo -", out int exitCode);
            if (exitCode != 0)
            {
                throw new InvalidOperationException("ffmpeg failed to decode the file.");
            }

            return raw;
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static byte[] Run(string arguments, out int exitCode)
    {
        var info = new ProcessStartInfo("ffmpeg", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        using var stdout = new MemoryStream();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(stdout);
        process.WaitForExit();
        exitCode = process.ExitCode;
        string errors = errorTask.GetAwaiter().GetResult();

        // For probing commands the useful text is on stdout (encoder/decoder lists); for failures it is on stderr.
        return exitCode == 0 ? stdout.ToArray() : System.Text.Encoding.UTF8.GetBytes(errors);
    }

    private static string AsText(this byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);
}
