using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PeachImage.Tests.Formats.Jxl.Oracle;

/// <summary>Every frame decoded by libjxl's <c>djxl</c> as 32-bit floats (the decoder's own output, no 8-bit or 16-bit rounding), interleaved.</summary>
internal sealed record FloatFrames(int FrameCount, int Width, int Height, int Channels, float[] Samples)
{
    public float Sample(int frame, int x, int y, int channel) =>
        Samples[(((((frame * Height) + y) * Width) + x) * Channels) + channel];
}

/// <summary>
/// Drives libjxl's reference decoder (<c>djxl</c>) as a pixel-exact oracle: it decodes any .jxl to float samples in the
/// file's own colour encoding, frame by frame, with no 8-bit or 16-bit rounding in between. Found through the
/// <c>PEACHIMAGE_DJXL</c> environment variable or the PATH; tests that use it skip when it is not installed (it is not
/// needed to build or run the rest of the suite).
/// </summary>
internal static class DjxlOracle
{
    private static readonly Lazy<string?> Executable = new(Find);

    public static bool IsAvailable => Executable.Value is not null;

    private static string? Find()
    {
        string? configured = Environment.GetEnvironmentVariable("PEACHIMAGE_DJXL");
        string[] candidates = string.IsNullOrWhiteSpace(configured) ? ["djxl"] : [configured, "djxl"];
        foreach (string candidate in candidates)
        {
            try
            {
                var info = new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var process = Process.Start(info);
                if (process is null)
                {
                    continue;
                }

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
            }
        }

        return null;
    }

    /// <summary>Decodes <paramref name="jxl"/> with djxl to float frames, or returns <see langword="null"/> (with the tool's message) when djxl cannot decode it.</summary>
    public static FloatFrames? Decode(byte[] jxl, out string message)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"peach-djxl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.jxl");
            string output = Path.Combine(directory, "out.npy");
            File.WriteAllBytes(input, jxl);
            var info = new ProcessStartInfo(Executable.Value!, $"\"{input}\" \"{output}\" --output_frames --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            message = process.StandardError.ReadToEnd().Trim();
            stdout.Wait();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                return null;
            }

            return ReadNpy(File.ReadAllBytes(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Decodes a single-frame <paramref name="jxl"/> with djxl to a 16-bit PNG and loads it. Unlike the float route this keeps the
    /// image in the file's own colour encoding, ICC profiles included, and returns straight (not premultiplied) alpha.
    /// </summary>
    public static Image? DecodeToPng16(byte[] jxl, out string message)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"peach-djxl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.jxl");
            string output = Path.Combine(directory, "out.png");
            File.WriteAllBytes(input, jxl);
            var info = new ProcessStartInfo(Executable.Value!, $"\"{input}\" \"{output}\" --bits_per_sample=16 --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            message = process.StandardError.ReadToEnd().Trim();
            stdout.Wait();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                return null;
            }

            return Image.Load(new MemoryStream(File.ReadAllBytes(output)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // NPY version 1: magic, 2 version bytes, a little-endian header length, then a Python dict naming dtype '<f4' and the shape (frames, height, width, channels).
    private static FloatFrames ReadNpy(byte[] data)
    {
        if (data.Length < 10 || data[0] != 0x93 || data[1] != (byte)'N')
        {
            throw new InvalidDataException("Not an NPY file.");
        }

        int headerLength = data[8] | (data[9] << 8);
        string header = Encoding.ASCII.GetString(data, 10, headerLength);
        if (!header.Contains("'<f4'", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Expected 32-bit float samples.");
        }

        int open = header.IndexOf("'shape': (", StringComparison.Ordinal) + "'shape': (".Length;
        string[] shape = header[open..header.IndexOf(')', open)].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (shape.Length != 4)
        {
            throw new InvalidDataException("Expected a (frames, height, width, channels) array.");
        }

        int[] dims = shape.Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var samples = new float[(data.Length - 10 - headerLength) / 4];
        Buffer.BlockCopy(data, 10 + headerLength, samples, 0, samples.Length * 4);
        return new FloatFrames(dims[0], dims[2], dims[1], dims[3], samples);
    }
}
