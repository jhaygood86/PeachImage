namespace PeachImage.Internal.Icc;

/// <summary>
/// A single entry from an ICC profile's tag table (ICC.1:2010 §7.3), with its data already read verbatim.
/// Ported from Wacton/Unicolour's <c>Icc/Tag.cs</c> (MIT license) — see THIRD-PARTY-LICENSES.md.
/// </summary>
internal sealed record IccTag(string Signature, byte[] Data);
