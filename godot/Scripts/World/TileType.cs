namespace Mitosis.World;

/// <summary>
/// Types of terrain tiles in the world.
/// Moisture spectrum (dry→wet): Arid → Sand → Grass → Forest → Wetland
/// Temperature spectrum: Tundra/Ice (cold) → temperate → Savanna/Jungle (hot)
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
    Arid = 8,
    Tundra = 9,
    Ice = 10,
    Savanna = 11,
    Jungle = 12,
    Reef = 13,
    Lava = 14
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
    River,
    Arctic,
    Tropical,
    Volcanic
}

public static class TileTypeExtensions
{
    /// <summary>
    /// Check if a tile type is walkable by creatures.
    /// Water is passable but uncomfortable. Mountains, Reef, and Lava block movement.
    /// </summary>
    public static bool IsWalkable(this TileType tile)
    {
        return tile != TileType.Mountain &&
               tile != TileType.Reef &&
               tile != TileType.Lava;
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
    /// Excludes water, mountains, river, reef, lava, and ice tiles.
    /// </summary>
    public static bool IsSpawnable(this TileType tile)
    {
        return tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Sand ||
               tile == TileType.Wetland ||
               tile == TileType.Arid ||
               tile == TileType.Tundra ||
               tile == TileType.Savanna ||
               tile == TileType.Jungle;
    }

    /// <summary>
    /// Check if a tile type can be grazed by herbivores.
    /// Savanna is grazeable (sparse grass). Jungle is grazeable (dense vegetation).
    /// </summary>
    public static bool IsGrazeable(this TileType tile)
    {
        return tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Savanna ||
               tile == TileType.Jungle;
    }

    /// <summary>
    /// Check if a tile is on the moisture spectrum and can be terraformed by faction species.
    /// Includes new biome tiles that participate in terraform chains.
    /// </summary>
    public static bool IsTerraformable(this TileType tile)
    {
        return tile == TileType.Arid ||
               tile == TileType.Sand ||
               tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Wetland ||
               tile == TileType.Tundra ||
               tile == TileType.Savanna ||
               tile == TileType.Jungle;
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
            TileType.Tundra => BiomeType.Arctic,
            TileType.Ice => BiomeType.Arctic,
            TileType.Savanna => BiomeType.Tropical,
            TileType.Jungle => BiomeType.Tropical,
            TileType.Reef => BiomeType.Coast,
            TileType.Lava => BiomeType.Volcanic,
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
            TileType.Savanna => 1.05f,       // Sparse grass, slightly faster
            TileType.Forest => 0.85f,
            TileType.Sand => 0.7f,
            TileType.Wetland => 0.75f,
            TileType.Arid => 0.8f,
            TileType.Tundra => 0.5f,         // Frozen ground, very slow
            TileType.Ice => 0.45f,            // Slippery ice, very slow
            TileType.Jungle => 0.6f,          // Dense vegetation, very slow
            TileType.River => 0.35f,
            TileType.ShallowWater => 0.4f,
            TileType.DeepWater => 0.25f,
            TileType.Mountain => 0.05f,
            TileType.Reef => 0.05f,           // Impassable (not walkable)
            TileType.Lava => 0.05f,           // Impassable (not walkable)
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
            TileType.Savanna => 0f,           // Comfortable open terrain
            TileType.Jungle => 0.3f,          // Dense but habitable
            TileType.Wetland => 0.5f,
            TileType.Arid => 0.5f,
            TileType.Tundra => 2.0f,          // Freezing cold
            TileType.Ice => 4.0f,             // Extremely cold
            TileType.Sand => 1f,
            TileType.River => 8f,
            TileType.ShallowWater => 5f,
            TileType.DeepWater => 12f,
            TileType.Mountain => 15f,
            TileType.Reef => 15f,
            TileType.Lava => 20f,             // Extreme heat damage
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
            TileType.Savanna => 0f,           // Easy terrain
            TileType.Forest => 0.05f,
            TileType.Jungle => 0.1f,          // Dense but traversable
            TileType.Wetland => 0.15f,
            TileType.Arid => 0.15f,
            TileType.Tundra => 0.4f,          // Cold, creatures avoid
            TileType.Sand => 0.3f,
            TileType.Ice => 0.6f,             // Very cold, strongly avoided
            TileType.River => 0.8f,
            TileType.ShallowWater => 0.75f,
            TileType.DeepWater => 0.95f,
            TileType.Mountain => 1.0f,
            TileType.Reef => 1.0f,            // Impassable
            TileType.Lava => 1.0f,            // Impassable
            _ => 0.5f
        };
    }

    /// <summary>
    /// Get the stealth cover bonus for this tile (used by ambush hunters).
    /// Higher = better concealment.
    /// </summary>
    public static float GetCoverBonus(this TileType tile)
    {
        return tile switch
        {
            TileType.Jungle => 0.4f,          // Dense vegetation, excellent cover
            TileType.Forest => 0.2f,          // Good cover
            TileType.Wetland => 0.15f,        // Some cover
            TileType.Tundra => 0.0f,          // Barren, no cover
            TileType.Savanna => 0.05f,        // Sparse, minimal cover
            _ => 0.0f
        };
    }

    /// <summary>
    /// Shift a tile one step wetter on the moisture spectrum.
    /// Returns null if at the wet extreme or not terraformable.
    /// Tundra→Grass (thaw), Savanna→Jungle (tropical wet shift).
    /// </summary>
    public static TileType? ShiftWetter(this TileType tile)
    {
        return tile switch
        {
            TileType.Arid => TileType.Sand,
            TileType.Sand => TileType.Grass,
            TileType.Grass => TileType.Forest,
            TileType.Forest => TileType.Wetland,
            TileType.Tundra => TileType.Grass,
            TileType.Savanna => TileType.Jungle,
            _ => null
        };
    }

    /// <summary>
    /// Shift a tile one step drier on the moisture spectrum.
    /// Returns null if at the dry extreme or not terraformable.
    /// Jungle→Savanna (tropical dry shift).
    /// </summary>
    public static TileType? ShiftDrier(this TileType tile)
    {
        return tile switch
        {
            TileType.Wetland => TileType.Forest,
            TileType.Forest => TileType.Grass,
            TileType.Grass => TileType.Sand,
            TileType.Sand => TileType.Arid,
            TileType.Jungle => TileType.Savanna,
            TileType.Tundra => TileType.Arid,
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
            TileType.Tundra => TileType.Grass,
            TileType.Savanna => TileType.Grass,
            TileType.Jungle => TileType.Forest,
            _ => null
        };
    }
}
