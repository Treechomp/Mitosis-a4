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
    /// </summary>
    public static bool IsWalkable(this TileType tile)
    {
        return tile switch
        {
            TileType.Sand => true,
            TileType.Grass => true,
            TileType.Forest => true,
            _ => false
        };
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
            TileType.Sand => 0.7f,      // Sand slows movement
            TileType.Grass => 1.0f,     // Grass is normal
            TileType.Forest => 0.85f,   // Forest slightly slower
            TileType.ShallowWater => 0.3f,  // Very slow in water (if somehow in it)
            _ => 0.1f                   // Deep water/mountain = nearly impassable
        };
    }

    /// <summary>
    /// Get how dangerous/undesirable a tile is (0 = safe, higher = avoid).
    /// Used for pathfinding and avoidance behavior.
    /// </summary>
    public static float GetDangerLevel(this TileType tile)
    {
        return tile switch
        {
            TileType.DeepWater => 1.0f,     // Very dangerous - avoid
            TileType.ShallowWater => 0.8f,  // Dangerous
            TileType.Mountain => 0.6f,      // Avoid but less urgent
            TileType.Sand => 0.1f,          // Slight preference against
            TileType.Grass => 0.0f,         // Preferred
            TileType.Forest => 0.0f,        // Preferred
            _ => 0.5f
        };
    }
}
