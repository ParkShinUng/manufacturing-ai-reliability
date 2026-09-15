namespace Mair.EquipmentSimulator;

/// <summary>
/// xoshiro256** seeded by SplitMix64.
/// <para>
/// <see cref="System.Random"/> is deliberately NOT used: its algorithm is an
/// implementation detail that has already changed between .NET versions, so a seeded
/// run is reproducible only on one runtime. NFR-010 requires bit-identical telemetry
/// for a given seed, which means the generator has to be part of this repository.
/// </para>
/// </summary>
internal sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRandom(ulong seed)
    {
        var state = seed;
        _s0 = SplitMix64(ref state);
        _s1 = SplitMix64(ref state);
        _s2 = SplitMix64(ref state);
        _s3 = SplitMix64(ref state);
    }

    private static ulong SplitMix64(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>Uniform in [0, 1) using the top 53 bits, which is the full mantissa of a double.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>
    /// Gaussian with the given standard deviation, by Box-Muller. The second variate is
    /// discarded rather than cached: caching would make the draw sequence depend on how
    /// many Gaussians a tick happened to need, which is exactly the coupling PROP-03
    /// must not have.
    /// </summary>
    public double NextGaussian(double sigma)
    {
        if (sigma == 0)
        {
            return 0;
        }

        // u1 must be strictly positive for the logarithm.
        var u1 = ((NextUInt64() >> 11) + 1) * (1.0 / 9007199254740993.0);
        var u2 = NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
