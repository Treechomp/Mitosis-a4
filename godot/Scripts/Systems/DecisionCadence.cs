using System;
using Mitosis.Components;
using Mitosis.ECS;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Keeps steering behaviour identical whether a creature decides every tick or every twentieth.
///
/// LODSystem buys performance by thinning DECISIONS, not motion: a Minimal-tier creature keeps
/// moving every tick but only re-steers every 20th. Two quantities in the steering code are
/// per-tick rates that were being applied on that thinned cadence, and both silently collapsed
/// with distance:
///
///  - Turn authority. Velocity blending is an exponential approach — "close the remaining
///    angle by `rate` each tick". A Shark's rate is 0.06, so over 20 ticks in view it turns
///    1 - 0.94^20 = 71% of the way onto a new heading. Given one decision per 20 ticks instead,
///    the same 0.06 turns it 6%: a twelvefold loss of steering authority, purely from distance.
///
///  - Look-ahead. Terrain sampling used a fixed 1.5 tiles regardless of how far the creature
///    would travel before its next decision. A hunting Shark covers ~5.7 tiles between Minimal-
///    tier decisions, so it checked 1.5 tiles ahead and then sailed four tiles past whatever it
///    had checked.
///
/// Together these are why sharks that behave perfectly in view push onto the beach and suffocate
/// once they are a few screens away — they could neither see the shore coming nor turn away from
/// it in the one decision they got. The same failure applies to any species whose safe ground has
/// an edge: the correction belongs here, once, rather than in each steering system.
/// </summary>
public static class DecisionCadence
{
    /// <summary>
    /// Extra margin on the look-ahead, so terrain is spotted a little before the creature is
    /// committed to reaching it rather than exactly as it arrives.
    /// </summary>
    private const float HorizonMargin = 1.3f;

    /// <summary>Hard ceiling on the horizon, to bound the sampling cost of one decision.</summary>
    private const float HorizonMax = 12f;

    /// <summary>
    /// Ticks this entity will coast before its next decision (1 when it decides every tick).
    /// Forward-looking: TickInterval is the gap about to be traversed, whereas EffectiveInterval
    /// records the gap just ended.
    /// </summary>
    public static int Interval(EntityManager em, int entity)
    {
        if (!em.HasComponents(entity, ComponentFlags.SimulationLOD))
            return 1;
        int interval = em.SimulationLODs[entity].TickInterval;
        return interval > 0 ? interval : 1;
    }

    /// <summary>
    /// Convert a per-tick exponential approach rate into the equivalent rate for one decision
    /// covering `interval` ticks: applying the result once leaves the same fraction of the gap
    /// as applying the per-tick rate `interval` times.
    /// </summary>
    public static float BlendRate(float perTickRate, int interval)
    {
        if (interval <= 1) return perTickRate;
        float r = Math.Clamp(perTickRate, 0f, 1f);
        return 1f - MathF.Pow(1f - r, interval);
    }

    /// <summary>
    /// How far ahead this entity must look to react to terrain in time: the distance it will
    /// actually travel before its next decision, never less than the system's base look-ahead.
    /// Uses the cached terrain speed multiplier, so a creature that is fast in its element plans
    /// as far ahead as that speed carries it.
    /// </summary>
    public static float Horizon(EntityManager em, int entity, float baseDistance)
    {
        if (!em.HasComponents(entity, ComponentFlags.Velocity))
            return baseDistance;

        ref var vel = ref em.Velocities[entity];
        float speed = MathF.Sqrt(vel.Dx * vel.Dx + vel.Dy * vel.Dy);
        if (speed <= 0f) return baseDistance;

        float mult = vel.CachedSpeedMult > 0f ? vel.CachedSpeedMult : 1f;
        float travel = speed * mult * Interval(em, entity) * HorizonMargin;
        return Math.Clamp(MathF.Max(baseDistance, travel), baseDistance, MathF.Max(baseDistance, HorizonMax));
    }
}
