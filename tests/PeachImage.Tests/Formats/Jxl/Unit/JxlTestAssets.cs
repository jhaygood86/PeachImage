namespace PeachImage.Tests.Formats.Jxl.Unit;

internal static class JxlTestAssets
{
    public static byte[] Load(string fileName)
    {
        using var stream = typeof(JxlTestAssets).Assembly.GetManifestResourceStream($"PeachImage.Tests.Formats.Jxl.Assets.{fileName}")
            ?? throw new InvalidOperationException($"Missing embedded JXL asset '{fileName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
