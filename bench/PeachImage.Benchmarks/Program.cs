using System.Reflection;
using BenchmarkDotNet.Running;
using PeachImage.Benchmarks;

// `profile <scenario> [iterations]` runs a bare decode loop for attaching a sampling profiler (see
// WebpProfileHarness); anything else is handed straight to BenchmarkDotNet's own argument parsing.
if (args.Length > 0 && string.Equals(args[0], "profile", StringComparison.OrdinalIgnoreCase))
{
    return WebpProfileHarness.Run(args);
}

if (args.Length > 0 && string.Equals(args[0], "avif-profile", StringComparison.OrdinalIgnoreCase))
{
    return AvifProfileHarness.Run(args);
}

if (args.Length > 0 && string.Equals(args[0], "gif-profile", StringComparison.OrdinalIgnoreCase))
{
    return GifProfileHarness.Run(args);
}

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
return 0;
