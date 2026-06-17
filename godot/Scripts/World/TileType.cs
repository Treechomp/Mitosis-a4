namespace Mitosis.World;

/// <summary>
/// Types of terrain tiles in the world.
/// Moisture spectrum (dry→wet): Arid → Sand → Dirt → Shrubland → Grass → Forest → Wetland → Bog
/// Temperature spectrum: Ice/Tundra/Steppe (cold) → Taiga → temperate → Savanna/Jungle (hot)
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
    Lava = 14,
    Dirt = 15,
    Taiga = 16,
    Steppe = 17,
    Shrubland = 18,
    Bog = 19
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
    /// Check if a tile is water (deep or shallow or river).
    /// </summary>
    public static bool IsWater(this TileType tile)
    {
        return tile == TileType.DeepWater ||
               tile == TileType.ShallowWater ||
               tile == TileType.River;
    }

    /// <summary>Open/deep water — land creatures route around it and drown in it.</summary>
    public static bool IsDeepWater(this TileType tile) => tile == TileType.DeepWater;

    /// <summary>Wadeable water — shallow/river that land creatures cross and fish in safely.</summary>
    public static bool IsWadeableWater(this TileType tile)
        => tile == TileType.ShallowWater || tile == TileType.River;

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
               tile == TileType.Jungle ||
               tile == TileType.Dirt ||
               tile == TileType.Taiga ||
               tile == TileType.Steppe ||
               tile == TileType.Shrubland ||
               tile == TileType.Bog;
    }

    /// <summary>
    /// Check if a tile type can be grazed by herbivores.
    /// Requires some vegetation — bare earth, sand, and bogs are not grazeable.
    /// </summary>
    public static bool IsGrazeable(this TileType tile)
    {
        return tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Savanna ||
               tile == TileType.Jungle ||
               tile == TileType.Shrubland ||
               tile == TileType.Taiga ||
               tile == TileType.Steppe ||
               tile == TileType.Tundra ||
               tile == TileType.Arid;
    }

    /// <summary>
    /// Check if a tile is on the moisture spectrum and can be terraformed by faction species.
    /// Includes new biome tiles that participate in terraform chains.
    /// </summary>
    public static bool IsTerraformable(this TileType tile)
    {
        return tile == TileType.Arid ||
               tile == TileType.Sand ||
               tile == TileType.Dirt ||
               tile == TileType.Shrubland ||
               tile == TileType.Grass ||
               tile == TileType.Forest ||
               tile == TileType.Wetland ||
               tile == TileType.Bog ||
               tile == TileType.Tundra ||
               tile == TileType.Steppe ||
               tile == TileType.Taiga ||
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
            TileType.Dirt => BiomeType.Desert,
            TileType.Grass => BiomeType.Grassland,
            TileType.Shrubland => BiomeType.Grassland,
            TileType.Forest => BiomeType.Forest,
            TileType.Taiga => BiomeType.Forest,
            TileType.Mountain => BiomeType.Mountain,
            TileType.River => BiomeType.River,
            TileType.Wetland => BiomeType.Wetland,
            TileType.Bog => BiomeType.Wetland,
            TileType.Arid => BiomeType.Desert,
            TileType.Tundra => BiomeType.Arctic,
            TileType.Steppe => BiomeType.Arctic,
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
            TileType.Shrubland => 0.95f,     // Sparse bushes, nearly open
            TileType.Dirt => 0.9f,           // Packed earth, faster than sand
            TileType.Steppe => 0.9f,         // Open cold grassland
            TileType.Forest => 0.85f,
            TileType.Taiga => 0.8f,          // Cold dense forest
            TileType.Arid => 0.8f,
            TileType.Wetland => 0.75f,
            TileType.Sand => 0.7f,
            TileType.Jungle => 0.6f,         // Dense vegetation, very slow
            TileType.Bog => 0.55f,           // Squishy waterlogged ground
            TileType.Tundra => 0.5f,         // Frozen ground, very slow
            TileType.Ice => 0.45f,           // Slippery ice, very slow
            TileType.Reef => 0.35f,          // Shallow rocky water
            TileType.River => 0.35f,
            TileType.ShallowWater => 0.4f,
            TileType.DeepWater => 0.25f,
            TileType.Mountain => 0.35f,        // Rugged, slow but traversable
            TileType.Lava => 0.2f,            // Very slow, hostile surface
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
            TileType.Shrubland => 0f,         // Comfortable sparse terrain
            TileType.Dirt => 0.2f,            // Bare but passable
            TileType.Jungle => 0.3f,          // Dense but habitable
            TileType.Taiga => 0.4f,           // Cold forest
            TileType.Arid => 0.5f,
            TileType.Wetland => 0.5f,
            TileType.Bog => 0.8f,             // Waterlogged, unpleasant
            TileType.Steppe => 0.8f,          // Cold open grassland
            TileType.Sand => 1f,
            TileType.Tundra => 2.0f,          // Freezing cold
            TileType.Ice => 4.0f,             // Extremely cold
            TileType.Reef => 6f,              // Sharp coral, uncomfortable
            TileType.River => 8f,
            TileType.ShallowWater => 5f,
            TileType.DeepWater => 12f,
            TileType.Mountain => 15f,
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
            TileType.Shrubland => 0.02f,      // Nearly open
            TileType.Dirt => 0.05f,           // Bare earth
            TileType.Forest => 0.05f,
            TileType.Taiga => 0.1f,           // Cold forest
            TileType.Jungle => 0.1f,          // Dense but traversable
            TileType.Steppe => 0.12f,         // Cold grassland
            TileType.Wetland => 0.15f,
            TileType.Arid => 0.15f,
            TileType.Bog => 0.25f,            // Very wet, avoided
            TileType.Sand => 0.3f,
            TileType.Tundra => 0.4f,          // Cold, creatures avoid
            TileType.Ice => 0.6f,             // Very cold, strongly avoided
            TileType.Reef => 0.7f,            // Shallow rocky water
            TileType.River => 0.8f,
            TileType.ShallowWater => 0.75f,
            TileType.DeepWater => 0.95f,
            TileType.Mountain => 0.8f,         // Steep, strongly avoided
            TileType.Lava => 0.95f,           // Near-lethal, almost always avoided
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
            TileType.Taiga => 0.2f,           // Cold forest, same cover as forest
            TileType.Wetland => 0.15f,        // Some cover
            TileType.Bog => 0.15f,            // Reeds and muck
            TileType.Shrubland => 0.1f,       // Sparse bushes
            TileType.Savanna => 0.05f,        // Sparse, minimal cover
            _ => 0.0f
        };
    }

    /// <summary>
    /// Per-biome surface-ruggedness multiplier for the terrain detail noise: high for
    /// mountains/volcanic, moderate for forest/jungle, low for plains, zero for water (flat).
    /// Edit here like the other per-tile properties; see TerrainGenerator surface detail.
    /// </summary>
    public static float GetRuggedness(this TileType tile)
    {
        return tile switch
        {
            TileType.Mountain  => 1.6f,
            TileType.Lava      => 1.3f,
            TileType.Jungle    => 1.1f,
            TileType.Forest    => 0.9f,
            TileType.Taiga     => 0.9f,
            TileType.Dirt      => 0.7f,
            TileType.Arid      => 0.7f,
            TileType.Sand      => 0.6f,   // mild dunes
            TileType.Shrubland => 0.6f,
            TileType.Savanna   => 0.5f,
            TileType.Steppe    => 0.5f,
            TileType.Grass     => 0.4f,
            TileType.Bog       => 0.4f,
            TileType.Tundra    => 0.35f,
            TileType.Wetland   => 0.35f,
            TileType.Ice       => 0.25f,
            _ => 0.0f                     // water (DeepWater/ShallowWater/River/Reef): flat
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
            TileType.Sand => TileType.Dirt,
            TileType.Dirt => TileType.Shrubland,
            TileType.Shrubland => TileType.Grass,
            TileType.Grass => TileType.Forest,
            TileType.Forest => TileType.Wetland,
            TileType.Wetland => TileType.Bog,
            TileType.Tundra => TileType.Steppe,
            TileType.Steppe => TileType.Taiga,
            TileType.Taiga => TileType.Forest,
            TileType.Savanna => TileType.Jungle,
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
            TileType.Bog => TileType.Wetland,
            TileType.Wetland => TileType.Forest,
            TileType.Forest => TileType.Grass,
            TileType.Grass => TileType.Shrubland,
            TileType.Shrubland => TileType.Dirt,
            TileType.Dirt => TileType.Sand,
            TileType.Sand => TileType.Arid,
            TileType.Taiga => TileType.Steppe,
            TileType.Steppe => TileType.Tundra,
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
            TileType.Sand => TileType.Dirt,
            TileType.Dirt => TileType.Shrubland,
            TileType.Shrubland => TileType.Grass,
            TileType.Bog => TileType.Wetland,
            TileType.Wetland => TileType.Forest,
            TileType.Forest => TileType.Grass,
            TileType.Tundra => TileType.Steppe,
            TileType.Steppe => TileType.Grass,
            TileType.Taiga => TileType.Forest,
            TileType.Savanna => TileType.Grass,
            TileType.Jungle => TileType.Forest,
            _ => null
        };
    }
}
