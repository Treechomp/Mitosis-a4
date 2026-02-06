namespace Mitosis.World;

/// <summary>
/// Types of terrain tiles in the world.
/// Moisture spectrum (dry→wet): Arid → Sand → Grass → Forest → Wetland
/// </summary>
public enum TileType : byte
{
    DeepWater = 0,
    ShallowWater = 1,
    Sand = 2,
    Grass = 3,
    Forest = 4,
    Mountain = 5,
    River = 6,
    Wetland = 7,
    Arid = 8
}

/// <summary>
/// Broad biome classification derived from tile types.
/// </summary>
public enum BiomeType : byte
{
    Ocean,
    Coast,
    Grassland,
    Forest,
    Desert,
    Mountain,
    Wetland,
    River
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
    /// Check if a tile is water (deep or shallow or river).
    /// </summary>
    public static bool IsWater(this TileType tile)
    {
        return tile == TileType.DeepWater ||
               tile == TileType.ShallowWater ||
               tile == TileType.River;
    }

    /// <summary>
    /// Check if a tile is safe to spawn land creatures on.
    /// Excludes water, mountains, and river tiles.
    /// </summary>
    public static bool IsSpawnable(this TileType tile)
    {
        return tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Sand ||
               tile == TileType.Wetland ||
               tile == TileType.Arid;
    }

    /// <summary>
    /// Check if a tile type can be grazed by herbivores.
    /// </summary>
    public static bool IsGrazeable(this TileType tile)
    {
        return tile == TileType.Grass || tile == TileType.Forest;
    }

    /// <summary>
    /// Check if a tile is on the moisture spectrum and can be terraformed by faction species.
    /// </summary>
    public static bool IsTerraformable(this TileType tile)
    {
        return tile == TileType.Arid ||
               tile == TileType.Sand ||
               tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Wetland;
    }

    /// <summary>
    /// Get the typical biome for a tile type.
    /// </summary>
    public static BiomeType GetTypicalBiome(this TileType tile)
    {
        return tile switch
        {
            TileType.DeepWater => BiomeType.Ocean,
            TileType.ShallowWater => BiomeType.Coast,
            TileType.Sand => BiomeType.Desert,
            TileType.Grass => BiomeType.Grassland,
            TileType.Forest => BiomeType.Forest,
            TileType.Mountain => BiomeType.Mountain,
            TileType.River => BiomeType.River,
            TileType.Wetland => BiomeType.Wetland,
            TileType.Arid => BiomeType.Desert,
            _ => BiomeType.Grassland
        };
    }

    /// <summary>
    /// Get the movement speed multiplier for a tile (1.0 = normal, lower = slower).
    /// </summary>
    public static float GetSpeedMultiplier(this TileType tile)
    {
        return tile switch
        {
            TileType.Grass => 1.0f,
            TileType.Forest => 0.85f,
            TileType.Sand => 0.7f,
            TileType.Wetland => 0.75f,
            TileType.Arid => 0.8f,
            TileType.River => 0.35f,
            TileType.ShallowWater => 0.4f,
            TileType.DeepWater => 0.25f,
            TileType.Mountain => 0.05f,
            _ => 0.5f
        };
    }

    /// <summary>
    /// Get the discomfort accumulation rate per tick while on this tile.
    /// Higher = more uncomfortable, causes entities to want to leave.
    /// </summary>
    public static float GetDiscomfortRate(this TileType tile)
    {
        return tile switch
        {
            TileType.Grass => 0f,
            TileType.Forest => 0f,
            TileType.Wetland => 0.5f,
            TileType.Arid => 0.5f,
            TileType.Sand => 1f,
            TileType.River => 8f,
            TileType.ShallowWater => 5f,
            TileType.DeepWater => 12f,
            TileType.Mountain => 15f,
            _ => 2f
        };
    }

    /// <summary>
    /// Get how much to avoid this tile when pathfinding (used by wander).
    /// Higher values make creatures strongly prefer other directions.
    /// </summary>
    public static float GetAvoidanceWeight(this TileType tile)
    {
        return tile switch
        {
            TileType.Grass => 0f,
            TileType.Forest => 0.05f,
            TileType.Wetland => 0.15f,
            TileType.Arid => 0.15f,
            TileType.Sand => 0.3f,
            TileType.River => 0.8f,
            TileType.ShallowWater => 0.75f,
            TileType.DeepWater => 0.95f,
            TileType.Mountain => 1.0f,
            _ => 0.5f
        };
    }

    /// <summary>
    /// Shift a tile one step wetter on the moisture spectrum.
    /// Returns null if at the wet extreme or not terraformable.
    /// </summary>
    public static TileType? ShiftWetter(this TileType tile)
    {
        return tile switch
        {
            TileType.Arid => TileType.Sand,
            TileType.Sand => TileType.Grass,
            TileType.Grass => TileType.Forest,
            TileType.Forest => TileType.Wetland,
            _ => null
        };
    }

    /// <summary>
    /// Shift a tile one step drier on the moisture spectrum.
    /// Returns null if at the dry extreme or not terraformable.
    /// </summary>
    public static TileType? ShiftDrier(this TileType tile)
    {
        return tile switch
        {
            TileType.Wetland => TileType.Forest,
            TileType.Forest => TileType.Grass,
            TileType.Grass => TileType.Sand,
            TileType.Sand => TileType.Arid,
            _ => null
        };
    }

    /// <summary>
    /// Shift a tile one step toward balance (Grass).
    /// Returns null if already balanced or not terraformable.
    /// </summary>
    public static TileType? ShiftBalanced(this TileType tile)
    {
        return tile switch
        {
            TileType.Arid => TileType.Sand,
            TileType.Sand => TileType.Grass,
            TileType.Wetland => TileType.Forest,
            TileType.Forest => TileType.Grass,
            _ => null
        };
    }
}
