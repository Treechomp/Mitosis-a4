using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.World;

namespace Mitosis.SpeciesData;

/// <summary>
/// Registry of all species definitions.
/// Add new species here to make them available for spawning.
/// </summary>
public static class SpeciesRegistry
{
    private static readonly Dictionary<string, SpeciesDefinition> _species = new();
    private static readonly Dictionary<int, SpeciesDefinition> _speciesById = new();

    static SpeciesRegistry()
    {
        // Register all species on first access
        RegisterDefaultSpecies();
    }

    /// <summary>
    /// Get a species definition by name.
    /// </summary>
    public static SpeciesDefinition Get(string name)
    {
        if (_species.TryGetValue(name, out var species))
            return species;

        GD.PrintErr($"Species '{name}' not found in registry!");
        return _species["Deer"];  // Fallback
    }

    /// <summary>
    /// Get a species definition by ID (hash of name).
    /// </summary>
    public static SpeciesDefinition GetById(int id)
    {
        if (_speciesById.TryGetValue(id, out var species))
            return species;

        GD.PrintErr($"Species ID '{id}' not found in registry!");
        return _species["Deer"];  // Fallback
    }

    /// <summary>
    /// Get the ID for a species name.
    /// </summary>
    public static int GetId(string name) => name.GetHashCode();

    /// <summary>
    /// Get all registered species names.
    /// </summary>
    public static IEnumerable<string> GetAllNames() => _species.Keys;

    /// <summary>
    /// Get all herbivore species.
    /// </summary>
    public static IEnumerable<SpeciesDefinition> GetHerbivores()
    {
        foreach (var species in _species.Values)
            if (species.Diet == DietType.Herbivore)
                yield return species;
    }

    /// <summary>
    /// Get all predator species.
    /// </summary>
    public static IEnumerable<SpeciesDefinition> GetPredators()
    {
        foreach (var species in _species.Values)
            if (species.IsPredator)
                yield return species;
    }

    /// <summary>
    /// Register a new species.
    /// </summary>
    public static void Register(SpeciesDefinition species)
    {
        _species[species.Name] = species;
        _speciesById[species.Name.GetHashCode()] = species;
    }

    private static void RegisterDefaultSpecies()
    {
        // ============================
        // HERBIVORES
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Deer",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,

            // Movement
            BaseWanderSpeed = 0.03f,
            DirectionChangeChance = 0.005f,

            // Fleeing
            FleeRange = 6f,
            FleeSpeedMultiplier = 2.2f,  // Fast runners

            // Survival
            MaxHunger = 80f,
            HungerDecayRate = 0.05f,
            MaxLifespan = 30000,
            MaturityAge = 2000,

            // Reproduction
            ReproHungerThreshold = 70f,
            ReproEnergyThreshold = 80f,
            ReproCooldown = 600,

            // Social
            GroupAffinity = 0.7f,
            PreferredGroupSize = 6f,
            CohesionStrength = 0.025f,
            AlignmentStrength = 0.015f,

            // Terrain
            DiscomfortThreshold = 50f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 1.5f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 0.75f },  // Slower in dense forest
                { TileType.Grass, 1.1f },    // Slightly faster on open grass
            },

            // Grazing
            CanGraze = true,
            GrazeNutrition = 0.5f,

            // Visuals
            BaseColor = new Color(0.4f, 1f, 0.4f),
            BaseSize = 8f,
            Shape = ShapeType.Circle,

            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Rabbit",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,

            // Movement - fast and erratic
            BaseWanderSpeed = 0.04f,
            DirectionChangeChance = 0.015f,  // Changes direction often

            // Fleeing - very fast, alert
            FleeRange = 8f,
            FleeSpeedMultiplier = 2.8f,

            // Survival - shorter lifespan, faster metabolism
            MaxHunger = 60f,
            HungerDecayRate = 0.08f,
            MaxLifespan = 15000,
            MaturityAge = 1000,

            // Reproduction - breeds quickly
            ReproHungerThreshold = 60f,
            ReproEnergyThreshold = 70f,
            ReproCooldown = 300,

            // Social - loose groups
            GroupAffinity = 0.4f,
            PreferredGroupSize = 4f,
            CohesionStrength = 0.015f,
            AlignmentStrength = 0.008f,

            // Terrain - good in underbrush
            DiscomfortThreshold = 40f,
            DiscomfortDecayRate = 3f,
            GrazingPressure = 2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 1.2f },   // Fast in underbrush (small)
                { TileType.Grass, 1.0f },
            },

            // Grazing
            CanGraze = true,
            GrazeNutrition = 0.3f,

            // Visuals - small
            BaseColor = new Color(0.6f, 0.5f, 0.4f),
            BaseSize = 5f,
            Shape = ShapeType.Circle,

            StatVariation = 0.25f,
        });

        // ============================
        // PREDATORS
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Wolf",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Pack,

            // Movement
            BaseWanderSpeed = 0.06f,
            DirectionChangeChance = 0.01f,

            // Combat
            HuntRange = 12f,
            AttackRange = 0.8f,
            AttackPower = 30f,
            AttackCooldown = 20,
            BaseHuntSpeed = 0.12f,

            // Survival
            MaxHunger = 70f,
            HungerDecayRate = 0.08f,
            MaxLifespan = 24000,
            MaturityAge = 1500,

            // Reproduction
            ReproHungerThreshold = 75f,
            ReproEnergyThreshold = 85f,
            ReproCooldown = 800,

            // Social - pack hunters
            GroupAffinity = 0.5f,
            PreferredGroupSize = 3f,
            CohesionStrength = 0.015f,
            AlignmentStrength = 0.01f,
            PackHunterChance = 0.7f,

            // Pack role speeds
            LeaderSpeedMult = 1.0f,
            FlankerSpeedMult = 1.1f,
            ChaserSpeedMult = 1.15f,

            // Terrain
            DiscomfortThreshold = 60f,
            DiscomfortDecayRate = 2.5f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 0.9f },
                { TileType.Grass, 1.0f },
                { TileType.Sand, 0.85f },
            },

            // Prey preference
            PreferredPrey = new List<string> { "Deer", "Rabbit" },

            // Grazing
            CanGraze = false,

            // Visuals
            BaseColor = new Color(0.6f, 0.6f, 0.6f),
            BaseSize = 10f,
            Shape = ShapeType.Triangle,

            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Fox",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,

            // Movement - quick and agile
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.012f,

            // Combat - smaller, faster attacks
            HuntRange = 8f,
            AttackRange = 0.6f,
            AttackPower = 20f,
            AttackCooldown = 15,
            BaseHuntSpeed = 0.11f,

            // Survival
            MaxHunger = 60f,
            HungerDecayRate = 0.07f,
            MaxLifespan = 20000,
            MaturityAge = 1200,

            // Reproduction
            ReproHungerThreshold = 70f,
            ReproEnergyThreshold = 80f,
            ReproCooldown = 700,

            // Social - mostly solitary
            GroupAffinity = 0.2f,
            PreferredGroupSize = 1f,
            CohesionStrength = 0.005f,
            AlignmentStrength = 0.003f,
            PackHunterChance = 0.1f,

            // Terrain - good in varied terrain
            DiscomfortThreshold = 55f,
            DiscomfortDecayRate = 3f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 1.1f },   // Agile in forest
                { TileType.Grass, 1.0f },
            },

            // Prey preference - prefers small prey
            PreferredPrey = new List<string> { "Rabbit" },

            // Grazing
            CanGraze = false,

            // Visuals - smaller, orange
            BaseColor = new Color(1f, 0.5f, 0.2f),
            BaseSize = 7f,
            Shape = ShapeType.Triangle,

            StatVariation = 0.2f,
        });

        // ============================
        // SEMI-AQUATIC (Example of terrain preference)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Crocodile",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,

            // Movement - slow on land, ambush predator
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.003f,

            // Combat - powerful bite, slow attack
            HuntRange = 6f,
            AttackRange = 1.2f,
            AttackPower = 50f,
            AttackCooldown = 40,
            BaseHuntSpeed = 0.08f,

            // Survival - long lived
            MaxHunger = 100f,
            HungerDecayRate = 0.03f,  // Slow metabolism
            MaxLifespan = 50000,
            MaturityAge = 4000,

            // Reproduction
            ReproHungerThreshold = 80f,
            ReproEnergyThreshold = 90f,
            ReproCooldown = 1500,

            // Social - territorial loners
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,
            CohesionStrength = 0f,
            AlignmentStrength = 0f,
            PackHunterChance = 0f,

            // Terrain - LOVES water
            DiscomfortThreshold = 80f,
            DiscomfortDecayRate = 1f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, 1.5f },  // Fast in shallow water
                { TileType.DeepWater, 1.8f },     // Very fast in deep water
                { TileType.Grass, 0.4f },         // Slow on land
                { TileType.Sand, 0.5f },          // Slow on sand
                { TileType.Forest, 0.3f },        // Very slow in forest
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, -5f },   // Comfortable in water (negative = likes it)
                { TileType.DeepWater, -3f },
                { TileType.Grass, 2f },           // Uncomfortable on land
                { TileType.Sand, 0f },            // Neutral on sand (sunbathing)
            },

            // Grazing
            CanGraze = false,

            // Visuals - large, green
            BaseColor = new Color(0.3f, 0.5f, 0.3f),
            BaseSize = 14f,
            Shape = ShapeType.Triangle,

            StatVariation = 0.15f,
        });
    }
}
