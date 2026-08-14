using PeachImage.Formats.Jpeg.Markers.Segments;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Per-component geometry and working state for decoding a single frame: sampling factors, the MCU-padded
/// block grid, the quantization table to dequantize with, and the coefficient buffer scans write into.
/// </summary>
internal sealed class ComponentDecodeState
{
    /// <summary>The frame header's component descriptor (id, sampling factors, quant table id).</summary>
    public required JpegFrameComponent Frame { get; init; }

    /// <summary>Blocks contributed per MCU horizontally (equals the component's horizontal sampling factor).</summary>
    public required int BlocksPerMcuHorizontal { get; init; }

    /// <summary>Blocks contributed per MCU vertically (equals the component's vertical sampling factor).</summary>
    public required int BlocksPerMcuVertical { get; init; }

    /// <summary>This component's sample width before any MCU padding (ceil(frameWidth * H / Hmax)).</summary>
    public required int ActualWidthInSamples { get; init; }

    /// <summary>This component's sample height before any MCU padding (ceil(frameHeight * V / Vmax)).</summary>
    public required int ActualHeightInSamples { get; init; }

    /// <summary>The dequantization table (natural order, 64 values) this component uses.</summary>
    public required JpegQuantizationTable QuantizationTable { get; set; }

    /// <summary>The coefficient storage for every block in this component's MCU-padded grid.</summary>
    public required JpegCoefficientBuffer Coefficients { get; init; }

    /// <summary>Per-scan working DC predictor (reset at the start of each scan and on every restart).</summary>
    public int DcPredictor { get; set; }
}
