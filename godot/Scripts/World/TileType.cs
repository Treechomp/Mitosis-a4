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
}
