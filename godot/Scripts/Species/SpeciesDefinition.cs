using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.World;

namespace Mitosis.SpeciesData;

/// <summary>
/// Defines dietary type - what a species eats.
/// </summary>
public enum DietType : byte
{
    Herbivore = 0,    // Eats plants (grazes)
    Carnivore = 1,    // Eats other creatures
    Omnivore = 2,     // Eats both (future)
    Terraformer = 3   // Feeds from terrain it shapes (faction species)
}

/// <summary>
/// Comprehensive species definition containing all stats and behaviors.
/// This allows easy addition of new species without code changes.
/// </summary>
public sealed class SpeciesDefinition
{
    // === IDENTITY ===
    public string Name { get; init; } = "Unknown";
    public DietType Diet { get; init; } = DietType.Herbivore;
    public SocialType DefaultSocialType { get; init; } = SocialType.Solitary;

    // === MOVEMENT ===
    public float BaseWanderSpeed { get; init; } = 0.03f;
    public float DirectionChangeChance { get; init; } = 0.005f;

    // === COMBAT (Predators) ===
    public float HuntRange { get; init; } = 12f;
    public float AttackRange { get; init; } = 0.8f;
    public float AttackPower { get; init; } = 30f;
    public int AttackCooldown { get; init; } = 20;
    public float BaseHuntSpeed { get; init; } = 0.10f;

    // === FLEEING (Prey) ===
    public float FleeRange { get; init; } = 6f;
    public float FleeSpeedMultiplier { get; init; } = 2f;

    // === FEAR RESPONSE ===
    public float FearThreshold { get; init; } = 50f;         // Fear level that triggers response
    public float FearMax { get; init; } = 100f;              // Max fear (panic level)
    public float FearAccumulationRate { get; init; } = 5f;   // How fast fear builds up per tick near threat
    public float FearDecayRate { get; init; } = 1f;          // How fast fear decays when safe
    public float FearVigilanceDecay { get; init; } = 0.3f;   // Slower decay when recently threatened
    public int FearVigilanceDuration { get; init; } = 100;   // Ticks to stay vigilant after threat
    public FearResponse DefaultFearResponse { get; init; } = FearResponse.Flee;

    // === SURVIVAL ===
    public float MaxHunger { get; init; } = 80f;
    public float HungerDecayRate { get; init; } = 0.05f;
    public int MaxLifespan { get; init; } = 30000;
    public int MaturityAge { get; init; } = 2000;

    // === REPRODUCTION ===
    public float ReproHungerThreshold { get; init; } = 70f;
    public float ReproEnergyThreshold { get; init; } = 80f;
    public int ReproCooldown { get; init; } = 600;

    // === SOCIAL ===
    public float GroupAffinity { get; init; } = 0.5f;
    public float PreferredGroupSize { get; init; } = 5f;
    public float CohesionStrength { get; init; } = 0.02f;
    public float AlignmentStrength { get; init; } = 0.01f;
    public float PackHunterChance { get; init; } = 0.6f;  // Chance to be pack vs solitary (predators)

    // === PACK ROLE MODIFIERS (speed multipliers per role) ===
    public float LeaderSpeedMult { get; init; } = 1.0f;
    public float FlankerSpeedMult { get; init; } = 1.1f;   // Flankers slightly faster to get in position
    public float ChaserSpeedMult { get; init; } = 1.15f;   // Chasers fastest to cut off escape

    // === TERRAIN ===
    public float DiscomfortThreshold { get; init; } = 50f;
    public float DiscomfortDecayRate { get; init; } = 2f;
    public float GrazingPressure { get; init; } = 0f;      // Extra discomfort when hungry on non-grazeable

    /// <summary>
    /// Per-terrain speed modifiers. If not specified, uses default terrain speeds.
    /// Values multiply the base terrain speed (1.0 = normal, >1 = faster, <1 = slower).
    /// Example: A fish might have Water = 2.0f (fast in water), Grass = 0.3f (slow on land).
    /// </summary>
    public Dictionary<TileType, float>? TerrainSpeedModifiers { get; init; }

    /// <summary>
    /// Per-terrain comfort overrides. Negative = comfortable, Positive = uncomfortable.
    /// Example: A hippo might have ShallowWater = -1f (likes water).
    /// </summary>
    public Dictionary<TileType, float>? TerrainComfortModifiers { get; init; }

    /// <summary>
    /// Biomes where this species can spawn. Empty/null = spawn in any biome.
    /// </summary>
    public List<BiomeType>? PreferredBiomes { get; init; }

    /// <summary>
    /// Whether this species is aquatic (spawns in water instead of on land).
    /// </summary>
    public bool IsAquatic { get; init; } = false;

    /// <summary>
    /// Specific tile types where this species can spawn. If null, uses default logic
    /// (aquatic = water tiles, land = non-water spawnable tiles).
    /// </summary>
    public List<TileType>? AllowedSpawnTiles { get; init; }

    /// <summary>
    /// Check if this species can spawn in the given biome.
    /// </summary>
    public bool CanSpawnInBiome(BiomeType biome)
    {
        if (PreferredBiomes == null || PreferredBiomes.Count == 0)
            return true;  // No preference = spawn anywhere
        return PreferredBiomes.Contains(biome);
    }

    /// <summary>
    /// Check if this species can spawn on the given tile type.
    /// </summary>
    public bool CanSpawnOnTile(TileType tile)
    {
        // If specific tiles are defined, use those
        if (AllowedSpawnTiles != null && AllowedSpawnTiles.Count > 0)
            return AllowedSpawnTiles.Contains(tile);

        // Otherwise use default logic based on aquatic flag
        if (IsAquatic)
            return tile.IsWater();

        // Land creatures use the spawnable check (excludes water, cliffs, etc)
        return tile.IsSpawnable();
    }

    // === TROPHIC INTERACTIONS ===
    /// <summary>
    /// Relative body mass for size-based hunting eligibility.
    /// Predators can only solo-hunt prey with BodyMass &lt;= own BodyMass * SoloHuntMaxRatio.
    /// Pack effective mass = leader BodyMass * packSize^PackHuntMassExponent.
    /// </summary>
    public float BodyMass { get; init; } = 1.0f;

    /// <summary>
    /// Maximum prey-to-predator mass ratio for solo hunting.
    /// E.g. 1.2 means a solo predator can hunt prey up to 1.2x its own mass.
    /// </summary>
    public float SoloHuntMaxRatio { get; init; } = 1.2f;

    /// <summary>
    /// Exponent for pack effective mass: effectiveMass = leaderMass * packSize^exponent.
    /// Sub-linear (0.7) means diminishing returns from larger packs.
    /// </summary>
    public float PackHuntMassExponent { get; init; } = 0.7f;

    /// <summary>
    /// List of species names this predator prefers to hunt (soft bias, not a hard gate).
    /// Preferred prey get a scoring bonus during target selection.
    /// Empty = no preference bias.
    /// </summary>
    public List<string>? PreferredPrey { get; init; }

    /// <summary>
    /// Scoring bonus multiplier for preferred prey (lower = more preferred).
    /// Applied as: score *= PreferredPreyBias for preferred species.
    /// E.g. 0.5 means preferred prey scores 50% better (closer effective distance).
    /// </summary>
    public float PreferredPreyBias { get; init; } = 0.5f;

    // === GRAZING ===
    public bool CanGraze { get; init; } = false;
    public float GrazeNutrition { get; init; } = 0.5f;

    // === TERRAFORM (faction species) ===
    public TerraformDirection TerraformDir { get; init; } = TerraformDirection.Balanced;
    public float TerraformRadius { get; init; } = 2f;
    public float TerraformStrength { get; init; } = 0.02f;
    public int TerraformCooldown { get; init; } = 10;

    /// <summary>
    /// Tile types that this species feeds from (faction species).
    /// If null or empty, uses standard grazing logic.
    /// </summary>
    public List<TileType>? FeedTiles { get; init; }
    public float FeedNutrition { get; init; } = 0.4f;

    // === VISUALS ===
    public Color BaseColor { get; init; } = new(0.5f, 0.5f, 0.5f);
    public float BaseSize { get; init; } = 8f;
    public ShapeType Shape { get; init; } = ShapeType.Circle;

    // === STAT VARIATION ===
    /// <summary>
    /// How much stats vary between individuals (0.0 = identical, 0.3 = ±30%).
    /// </summary>
    public float StatVariation { get; init; } = 0.2f;

    /// <summary>
    /// Get speed modifier for a specific terrain type.
    /// </summary>
    public float GetTerrainSpeedModifier(TileType tile)
    {
        if (TerrainSpeedModifiers != null && TerrainSpeedModifiers.TryGetValue(tile, out float mod))
            return mod;
        return 1.0f;  // Default: no modification
    }

    /// <summary>
    /// Get comfort modifier for a specific terrain type.
    /// </summary>
    public float GetTerrainComfortModifier(TileType tile)
    {
        if (TerrainComfortModifiers != null && TerrainComfortModifiers.TryGetValue(tile, out float mod))
            return mod;
        return 0f;  // Default: no modification
    }

    /// <summary>
    /// Check if this species is a predator (hunts other creatures).
    /// </summary>
    public bool IsPredator => Diet == DietType.Carnivore || Diet == DietType.Omnivore;

    /// <summary>
    /// Check if this species can be prey (can be hunted).
    /// </summary>
    public bool IsPrey => Diet == DietType.Herbivore || Diet == DietType.Omnivore || Diet == DietType.Terraformer;
}
