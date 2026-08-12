using System.Runtime.InteropServices;

namespace Mitosis.Components;

/// <summary>
/// World position in tile coordinates.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Position
{
    public float X;
    public float Y;

    public Position(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// Movement velocity in tiles per tick.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Velocity
{
    public float Dx;
    public float Dy;

    /// <summary>
    /// Terrain speed multiplier resolved on this entity's last DUE tick (0 = not yet resolved).
    ///
    /// Movement runs every tick for everyone so the world advances at one speed everywhere, but
    /// resolving terrain per entity per tick is expensive: a species lookup, a TerrainProfile
    /// dictionary probe and an elevation sample for the slope. None of that changes appreciably
    /// between one decision and the next, so it is computed on the decision cadence and reused in
    /// between — the motion stays exact while the lookups stay LOD-gated.
    /// </summary>
    public float CachedSpeedMult;

    public Velocity(float dx = 0f, float dy = 0f)
    {
        Dx = dx;
        Dy = dy;
        CachedSpeedMult = 0f;
    }
}

/// <summary>
/// Cached chunk coordinates for spatial queries.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ChunkPosition
{
    public int ChunkX;
    public int ChunkY;

    public ChunkPosition(int chunkX = 0, int chunkY = 0)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
    }

    /// <summary>
    /// Update chunk position from world position. Returns true if changed.
    /// </summary>
    public bool Update(in Position pos, int chunkSize)
    {
        int newX = (int)(pos.X / chunkSize);
        int newY = (int)(pos.Y / chunkSize);

        if (newX != ChunkX || newY != ChunkY)
        {
            ChunkX = newX;
            ChunkY = newY;
            return true;
        }
        return false;
    }
}
