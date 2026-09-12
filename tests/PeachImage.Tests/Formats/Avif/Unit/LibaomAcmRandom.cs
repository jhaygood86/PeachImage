namespace PeachImage.Tests.Formats.Avif.Unit;

/// <summary>
/// Bit-exact C# port of libaom's own <c>libaom_test::ACMRandom</c> (<c>test/acm_random.h</c>), itself a thin
/// wrapper around GoogleTest's own <c>testing::internal::Random</c> (a glibc-<c>rand(3)</c>-style linear
/// congruential generator, <c>third_party/googletest/src/googletest/src/gtest.cc</c>'s own
/// <c>Random::Generate</c>). Exists so a ported libaom unit test can generate the *exact same* pseudo-random
/// test vectors the real libaom test does for the same seed -- not merely "some random data", but the
/// identical sequence a real run of the real C++ test would feed its own reference/tested functions, making
/// a byte-for-byte comparison against a hand-transcribed reference implementation a genuine, faithful port
/// rather than a similarly-spirited reinvention. <see cref="DeterministicSeed"/> matches libaom's own
/// <c>0xbaba</c> exactly.
/// </summary>
internal sealed class LibaomAcmRandom
{
    private uint _state;

    public LibaomAcmRandom(int seed) => _state = unchecked((uint)seed);

    public static int DeterministicSeed => 0xbaba;

    /// <summary>
    /// <c>Random::Generate</c>'s own exact LCG step (glibc's own <c>rand(3)</c> constants), followed by the
    /// same <c>% range</c> reduction -- <see langword="ulong"/> intermediates match the real implementation's
    /// own "wider than necessary" comment preventing unsigned overflow.
    /// </summary>
    private uint Generate(uint range)
    {
        _state = (uint)((1103515245UL * _state) + 12345UL) % KMaxRange;
        return _state % range;
    }

    private const uint KMaxRange = 1u << 31;

    /// <summary>Matches <c>ACMRandom::Rand31</c>: a random 31-bit unsigned integer from [0, 2^31).</summary>
    public uint Rand31() => Generate(KMaxRange);

    /// <summary>Matches <c>ACMRandom::Rand16</c> exactly, including its own "more entropy in the upper bits" shift.</summary>
    public ushort Rand16()
    {
        uint value = Generate(KMaxRange);
        return (ushort)((value >> 15) & 0xffff);
    }

    /// <summary>Matches <c>ACMRandom::Rand16Signed</c>.</summary>
    public short Rand16Signed() => unchecked((short)Rand16());

    /// <summary>Matches <c>ACMRandom::Rand8</c>.</summary>
    public byte Rand8()
    {
        uint value = Generate(KMaxRange);
        return (byte)((value >> 23) & 0xff);
    }

    /// <summary>Matches <c>ACMRandom::PseudoUniform</c> (and thus <c>operator()</c>).</summary>
    public int PseudoUniform(int range) => (int)Generate((uint)range);
}
