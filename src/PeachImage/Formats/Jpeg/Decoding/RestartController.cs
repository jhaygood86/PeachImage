using PeachImage.Formats.Jpeg.Entropy;

namespace PeachImage.Formats.Jpeg.Decoding;

/// <summary>
/// Tracks progress toward the next restart marker (DRI-defined interval) during scan decoding. Callers are
/// responsible for resetting their own per-component DC predictors and EOB run state immediately after
/// <see cref="ConsumeRestart"/> returns, since that state lives with the scan decoder, not this controller.
/// </summary>
internal sealed class RestartController(int restartInterval)
{
    private readonly int _restartInterval = restartInterval;
    private int _unitsUntilRestart = restartInterval;
    private int _nextExpectedIndex;

    /// <summary>Whether a restart interval is in effect (DRI with a non-zero interval was seen).</summary>
    public bool IsEnabled => _restartInterval > 0;

    /// <summary>Whether the next MCU/data-unit should be preceded by a restart marker.</summary>
    public bool ShouldRestart => IsEnabled && _unitsUntilRestart == 0;

    /// <summary>Records that one MCU (or, for non-interleaved scans, one data unit) has been decoded.</summary>
    public void NotifyUnitDecoded()
    {
        if (IsEnabled && _unitsUntilRestart > 0)
        {
            _unitsUntilRestart--;
        }
    }

    /// <summary>Byte-aligns, consumes and validates the expected RSTn marker, and rearms the interval. Does not reset any decoder-side prediction state.</summary>
    public void ConsumeRestart(JpegEntropyReader entropy)
    {
        entropy.AlignAndDiscardPartialByte();
        entropy.ConsumeRestartMarker(_nextExpectedIndex);
        entropy.ResetBitState();

        _nextExpectedIndex = (_nextExpectedIndex + 1) & 7;
        _unitsUntilRestart = _restartInterval;
    }
}
