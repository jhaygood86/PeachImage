using System.Buffers;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Holds every DCT coefficient block for a single frame component, across the full MCU-padded block grid,
/// as a flat natural-order array. Used for both baseline (a single scan fills every block) and progressive
/// (successive scans fill in subsets of coefficients/bits) decoding, so the reconstruction pass
/// (dequantize/IDCT/upsample/color-convert) is identical for both.
/// </summary>
/// <remarks>
/// The backing array is rented from <see cref="ArrayPool{T}"/> (this buffer is large — often several MB —
/// and short-lived relative to a decoder that processes many images) and must be released via
/// <see cref="Return"/> once the frame has been fully reconstructed into pixels. Unlike a freshly-allocated
/// array, a rented one is not guaranteed to be zeroed, and progressive decode's DC-first scan only ever
/// writes coefficient 0 of each block (relying on the other 63 already being zero, to be filled in by later
/// AC scans) — so the buffer is explicitly cleared once up front here.
/// </remarks>
internal sealed class JpegCoefficientBuffer
{
    private short[]? _coefficients;

    public JpegCoefficientBuffer(int blocksWide, int blocksHigh)
    {
        BlocksWide = blocksWide;
        BlocksHigh = blocksHigh;

        // Computed in `long` and bounds-checked explicitly here rather than trusting the frame-header-level
        // pixel-count check alone — that check and this allocation are two separate call sites, and relying
        // on an invariant that only holds because of how they currently happen to combine is fragile to a
        // future change in either one.
        long length = (long)blocksWide * blocksHigh * 64;
        if (length > int.MaxValue)
        {
            throw new JpegDecodingException($"JPEG component block grid ({blocksWide}x{blocksHigh}) is too large to decode.");
        }

        _coefficients = ArrayPool<short>.Shared.Rent((int)length);
        Array.Clear(_coefficients, 0, (int)length);
    }

    /// <summary>The block grid width, padded up to a whole number of MCUs.</summary>
    public int BlocksWide { get; }

    /// <summary>The block grid height, padded up to a whole number of MCUs.</summary>
    public int BlocksHigh { get; }

    /// <summary>Gets the 64-coefficient (natural order) span for the block at grid position <paramref name="blockX"/>, <paramref name="blockY"/>.</summary>
    public Span<short> Block(int blockX, int blockY)
    {
        var coefficients = _coefficients ?? throw new ObjectDisposedException(nameof(JpegCoefficientBuffer));
        return coefficients.AsSpan((((blockY * BlocksWide) + blockX) * 64), 64);
    }

    /// <summary>Returns the backing array to the shared pool. The buffer must not be used afterward.</summary>
    public void Return()
    {
        if (_coefficients is { } buffer)
        {
            ArrayPool<short>.Shared.Return(buffer);
            _coefficients = null;
        }
    }
}
