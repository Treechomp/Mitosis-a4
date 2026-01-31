using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.World;

namespace Mitosis.Species;

/// <summary>
/// Defines dietary type - what a species eats.
/// </summary>
public enum DietType : byte
{
    Herbivore = 0,   // Eats plants (grazes)
    Carnivore = 1,   // Eats other creatures
    Omnivore = 2     // Eats both (future)
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

    // === PREY PREFERENCES (for predators) ===
    /// <summary>
    /// List of species names this predator prefers to hunt (in order of preference).
    /// Empty = hunts any prey.
    /// </summary>
    public List<string>? PreferredPrey { get; init; }

    // === GRAZING ===
    public bool CanGraze { get; init; } = false;
    public float GrazeNutrition { get; init; } = 0.5f;

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
    public bool IsPrey => Diet == DietType.Herbivore || Diet == DietType.Omnivore;
}
