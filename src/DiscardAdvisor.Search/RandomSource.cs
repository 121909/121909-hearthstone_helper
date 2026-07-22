using System;

namespace DiscardAdvisor.Search;

public interface IRandomSource
{
    int Next(int exclusiveMaximum);
}

public sealed class SeededRandomSource : IRandomSource
{
    private ulong _state;

    public SeededRandomSource(int seed)
    {
        _state = unchecked((uint)seed);
    }

    public int Next(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        var bound = (ulong)exclusiveMaximum;
        var rejectionLimit = ulong.MaxValue - ulong.MaxValue % bound;
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= rejectionLimit);

        return (int)(value % bound);
    }

    private ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var value = _state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

internal sealed class SystemRandomSource : IRandomSource
{
    private readonly Random _random;

    public SystemRandomSource(Random random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public int Next(int exclusiveMaximum) => _random.Next(exclusiveMaximum);
}
