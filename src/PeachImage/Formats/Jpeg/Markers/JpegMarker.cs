namespace PeachImage.Formats.Jpeg.Markers;

/// <summary>JPEG marker codes (the byte following a <c>0xFF</c> marker prefix), per ITU-T.81 Table B.1.</summary>
internal enum JpegMarker : byte
{
    /// <summary>Temporary marker, reserved.</summary>
    Tem = 0x01,

    /// <summary>Baseline sequential DCT frame.</summary>
    Sof0 = 0xC0,

    /// <summary>Extended sequential DCT frame.</summary>
    Sof1 = 0xC1,

    /// <summary>Progressive DCT frame.</summary>
    Sof2 = 0xC2,

    /// <summary>Lossless (sequential) frame.</summary>
    Sof3 = 0xC3,

    /// <summary>Define Huffman table(s).</summary>
    Dht = 0xC4,

    /// <summary>Differential sequential DCT frame.</summary>
    Sof5 = 0xC5,

    /// <summary>Differential progressive DCT frame.</summary>
    Sof6 = 0xC6,

    /// <summary>Differential lossless frame.</summary>
    Sof7 = 0xC7,

    /// <summary>Extension marker.</summary>
    Jpg = 0xC8,

    /// <summary>Extended sequential DCT, arithmetic coding.</summary>
    Sof9 = 0xC9,

    /// <summary>Progressive DCT, arithmetic coding.</summary>
    Sof10 = 0xCA,

    /// <summary>Lossless, arithmetic coding.</summary>
    Sof11 = 0xCB,

    /// <summary>Define arithmetic coding conditioning(s).</summary>
    Dac = 0xCC,

    /// <summary>Differential sequential DCT, arithmetic coding.</summary>
    Sof13 = 0xCD,

    /// <summary>Differential progressive DCT, arithmetic coding.</summary>
    Sof14 = 0xCE,

    /// <summary>Differential lossless, arithmetic coding.</summary>
    Sof15 = 0xCF,

    /// <summary>Restart marker 0.</summary>
    Rst0 = 0xD0,

    /// <summary>Restart marker 7 (restart markers cycle Rst0..Rst7).</summary>
    Rst7 = 0xD7,

    /// <summary>Start of image.</summary>
    Soi = 0xD8,

    /// <summary>End of image.</summary>
    Eoi = 0xD9,

    /// <summary>Start of scan.</summary>
    Sos = 0xDA,

    /// <summary>Define quantization table(s).</summary>
    Dqt = 0xDB,

    /// <summary>Define number of lines.</summary>
    Dnl = 0xDC,

    /// <summary>Define restart interval.</summary>
    Dri = 0xDD,

    /// <summary>Define hierarchical progression.</summary>
    Dhp = 0xDE,

    /// <summary>Expand reference component(s).</summary>
    Exp = 0xDF,

    /// <summary>Application-specific segment 0 (commonly JFIF).</summary>
    App0 = 0xE0,

    /// <summary>Application-specific segment 1 (commonly EXIF).</summary>
    App1 = 0xE1,

    /// <summary>Application-specific segment 2 (commonly an embedded ICC profile).</summary>
    App2 = 0xE2,

    /// <summary>Application-specific segment 14 (commonly the Adobe marker).</summary>
    App14 = 0xEE,

    /// <summary>Application-specific segment 15.</summary>
    App15 = 0xEF,

    /// <summary>Comment segment.</summary>
    Com = 0xFE,
}
