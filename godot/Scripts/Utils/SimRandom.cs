using System;

namespace Mitosis.Utils;

/// <summary>
/// Source of the per-system random streams that drive simulation behaviour (wander headings,
/// reproduction rolls, terraform placement…).
///
/// Every system used to construct its own time-seeded <c>new Random()</c>, which made a run
/// impossible to repeat: the same world seed and the same scenario file produced a different
/// simulation every time. That is bad for the game (a seed should mean something) and worse for
/// the test scenes this branch exists to provide — two runs of one scenario could not be compared,
/// so any measured difference might be the change under test or might be the dice.
///
/// Call <see cref="SetSeed"/> once before the systems are constructed. Each subsequent
/// <see cref="Create"/> returns a distinct but deterministic stream, so systems stay independent
/// while the run as a whole replays exactly. Seed 0 restores the old time-seeded behaviour.
///
/// Determinism depends on systems being CONSTRUCTED in a fixed order (they are — GameManager
/// builds the stack in one place); it does not depend on the order they run or how often they
/// draw, because each holds its own stream.
/// </summary>
public static class SimRandom
{
    private static int _seed;
    private static int _streamCount;

    /// <summary>
    /// Fix the simulation's random streams to a seed, or pass 0 for a fresh nondeterministic run.
    /// Resets the stream counter, so this must be called before constructing the system stack.
    /// </summary>
    public static void SetSeed(int seed)
    {
        _seed = seed;
        _streamCount = 0;
    }

    /// <summary>The seed in force, or 0 when runs are nondeterministic.</summary>
    public static int Seed => _seed;

    /// <summary>
    /// A new random stream. Deterministic and distinct per call while a seed is set; time-seeded
    /// otherwise. The odd multiplier just spreads consecutive streams apart so systems created
    /// back to back don't start on neighbouring seeds.
    /// </summary>
    public static Random Create()
        => _seed == 0 ? new Random() : new Random(unchecked(_seed + ++_streamCount * 7919));
}
