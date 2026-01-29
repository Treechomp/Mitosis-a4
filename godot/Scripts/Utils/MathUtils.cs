using System;
using System.Runtime.CompilerServices;
using Godot;

namespace Mitosis.Utils;

/// <summary>
/// Optimized math utilities for performance-critical calculations.
/// </summary>
public static class MathUtils
{
    /// <summary>
    /// Calculate squared distance between two points (avoids sqrt).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DistanceSquared(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Calculate distance between two points.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(float x1, float y1, float x2, float y2)
    {
        return MathF.Sqrt(DistanceSquared(x1, y1, x2, y2));
    }

    /// <summary>
    /// Normalize a 2D vector. Returns zero vector if magnitude is near zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Normalize(float dx, float dy)
    {
        float magSq = dx * dx + dy * dy;
        if (magSq < 1e-10f)
            return Vector2.Zero;

        float invMag = 1f / MathF.Sqrt(magSq);
        return new Vector2(dx * invMag, dy * invMag);
    }

    /// <summary>
    /// Calculate flee direction away from multiple threats.
    /// </summary>
    public static (Vector2 direction, bool hasThreat) CalculateFleeVector(
        float preyX, float preyY,
        ReadOnlySpan<float> predatorXs,
        ReadOnlySpan<float> predatorYs,
        float fleeRangeSquared)
    {
        float fleeDx = 0f;
        float fleeDy = 0f;
        bool threatFound = false;

        for (int i = 0; i < predatorXs.Length; i++)
        {
            float dx = preyX - predatorXs[i];
            float dy = preyY - predatorYs[i];
            float distSq = dx * dx + dy * dy;

            if (distSq < fleeRangeSquared && distSq > 1e-10f)
            {
                // Weight by inverse distance for stronger flee from closer threats
                float invDist = 1f / MathF.Sqrt(distSq);
                fleeDx += dx * invDist;
                fleeDy += dy * invDist;
                threatFound = true;
            }
        }

        if (threatFound)
        {
            return (Normalize(fleeDx, fleeDy), true);
        }

        return (Vector2.Zero, false);
    }

    /// <summary>
    /// Find the nearest entity within range.
    /// </summary>
    public static (int entityId, float distanceSquared) FindNearestInRange(
        float sourceX, float sourceY,
        ReadOnlySpan<float> targetXs,
        ReadOnlySpan<float> targetYs,
        ReadOnlySpan<int> targetIds,
        float rangeSquared)
    {
        int nearestId = -1;
        float nearestDistSq = float.MaxValue;

        for (int i = 0; i < targetIds.Length; i++)
        {
            float dx = targetXs[i] - sourceX;
            float dy = targetYs[i] - sourceY;
            float distSq = dx * dx + dy * dy;

            if (distSq < rangeSquared && distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestId = targetIds[i];
            }
        }

        return (nearestId, nearestDistSq);
    }

    /// <summary>
    /// Fast random float between 0 and 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RandomFloat(Random rng)
    {
        return (float)rng.NextDouble();
    }

    /// <summary>
    /// Generate a random direction vector.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 RandomDirection(Random rng)
    {
        float angle = RandomFloat(rng) * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }
}
