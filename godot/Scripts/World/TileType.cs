namespace Mitosis.World;

/// <summary>
/// Types of terrain tiles in the world.
/// </summary>
public enum TileType : byte
{
    DeepWater = 0,
    ShallowWater = 1,
    Sand = 2,
    Grass = 3,
    Forest = 4,
    Mountain = 5
}

public static class TileTypeExtensions
{
    /// <summary>
    /// Check if a tile type is walkable by creatures.
    /// Water is passable but uncomfortable. Only mountains block movement.
    /// </summary>
    public static bool IsWalkable(this TileType tile)
    {
        return tile != TileType.Mountain;
    }

    /// <summary>
    /// Check if a tile type can be grazed by herbivores.
    /// </summary>
    public static bool IsGrazeable(this TileType tile)
    {
        return tile == TileType.Grass || tile == TileType.Forest;
    }

    /// <summary>
    /// Get the movement speed multiplier for a tile (1.0 = normal, lower = slower).
    /// </summary>
    public static float GetSpeedMultiplier(this TileType tile)
    {
        return tile switch
        {
            TileType.Grass => 1.0f,         // Grass is normal
            TileType.Forest => 0.85f,       // Forest slightly slower
            TileType.Sand => 0.7f,          // Sand slows movement
            TileType.ShallowWater => 0.4f,  // Wading through shallow water
            TileType.DeepWater => 0.25f,    // Swimming is slow
            TileType.Mountain => 0.05f,     // Nearly impassable
            _ => 0.5f
        };
    }

    /// <summary>
    /// Get the discomfort accumulation rate per tick while on this tile.
    /// Higher = more uncomfortable, causes entities to want to leave.
    /// Values tuned so discomfort threshold (~50) is reached in reasonable time:
    /// - Shallow water: ~10 ticks to start feeling uncomfortable
    /// - Deep water: ~4 ticks to become urgent
    /// </summary>
    public static float GetDiscomfortRate(this TileType tile)
    {
        return tile switch
        {
            TileType.Grass => 0f,           // Comfortable
            TileType.Forest => 0f,          // Comfortable
            TileType.Sand => 1f,            // Mild discomfort (hot/dry)
            TileType.ShallowWater => 5f,    // Uncomfortable, want to get out quickly
            TileType.DeepWater => 12f,      // Very uncomfortable, urgent to escape
            TileType.Mountain => 15f,       // Extremely uncomfortable
            _ => 2f
        };
    }

    /// <summary>
    /// Get how much to avoid this tile when pathfinding (used by wander).
    /// Different from discomfort - this is for proactive avoidance.
    /// Higher values make creatures strongly prefer other directions.
    /// </summary>
    public static float GetAvoidanceWeight(this TileType tile)
    {
        return tile switch
        {
            TileType.Grass => 0f,           // Preferred
            TileType.Forest => 0.05f,       // Slightly less preferred (visibility)
            TileType.Sand => 0.3f,          // Moderate avoidance
            TileType.ShallowWater => 0.75f, // Strong avoidance
            TileType.DeepWater => 0.95f,    // Very strong avoidance
            TileType.Mountain => 1.0f,      // Complete avoidance
            _ => 0.5f
        };
    }
}
