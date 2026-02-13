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
    /// Returns a default if ID is 0 or not found (silently for ID 0).
    /// </summary>
    public static SpeciesDefinition GetById(int id)
    {
        // ID 0 means uninitialized - return default silently
        if (id == 0)
            return _species["Deer"];

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
    /// Get all terraformer (faction) species.
    /// </summary>
    public static IEnumerable<SpeciesDefinition> GetTerraformers()
    {
        foreach (var species in _species.Values)
            if (species.Diet == DietType.Terraformer)
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

            // Fear - standard herd animal, alert but not paranoid
            FearThreshold = 50f,
            FearMax = 100f,
            FearAccumulationRate = 6f,
            FearDecayRate = 1f,
            FearVigilanceDecay = 0.3f,
            FearVigilanceDuration = 120,
            DefaultFearResponse = FearResponse.Flee,

            // Survival
            MaxHunger = 240f,
            HungerDecayRate = 0.05f,
            MaxLifespan = 30000,
            MaturityAge = 2000,

            // Reproduction
            ReproHungerThreshold = 210f,
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

            // Separation
            SeparationRadius = 2.5f,
            SeparationStrength = 0.03f,

            // Social - herding params
            SocialRadius = 10f,
            LeaderInfluenceRadius = 7f,

            // Reproduction costs
            ReproHungerCost = 50f,
            ReproEnergyCost = 30f,

            // Trophic - medium herbivore
            BodyMass = 4.0f,

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

            // Fleeing - fast but catchable by foxes
            FleeRange = 8f,
            FleeSpeedMultiplier = 2.4f,  // Reduced from 2.8 - foxes hunt at 0.11, rabbits flee at 0.04*2.4=0.096

            // Fear - very nervous, prone to panic
            FearThreshold = 30f,           // Startles easily
            FearMax = 80f,
            FearAccumulationRate = 10f,    // Fear builds quickly
            FearDecayRate = 0.5f,          // Takes longer to calm down
            FearVigilanceDecay = 0.2f,
            FearVigilanceDuration = 200,   // Stays alert longer
            DefaultFearResponse = FearResponse.Panic,  // Erratic when scared

            // Survival - shorter lifespan, faster metabolism
            MaxHunger = 180f,
            HungerDecayRate = 0.08f,
            MaxLifespan = 15000,
            MaturityAge = 1000,

            // Reproduction - breeds moderately (controlled by predation pressure)
            ReproHungerThreshold = 180f,
            ReproEnergyThreshold = 70f,
            ReproCooldown = 600,   // 10 sec (was 5 sec — halved breeding rate)

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

            // Roaming - smaller territory, more frequent moves
            RoamDistance = 40f,
            RoamCooldown = 300,

            // Separation - small, cluster tighter
            SeparationRadius = 1.5f,
            SeparationStrength = 0.02f,

            // Reproduction costs - meaningful investment per litter
            ReproHungerCost = 45f,
            ReproEnergyCost = 30f,
            OffspringCount = 2,
            SpawnRadius = 2f,

            // Trophic - small herbivore
            BodyMass = 1.0f,

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

            // Survival - predators need lower decay to survive between hunts
            MaxHunger = 210f,
            HungerDecayRate = 0.04f,  // Half of previous (0.08) - gives ~1750 ticks to find food
            MaxLifespan = 24000,
            MaturityAge = 1500,

            // Reproduction - lower thresholds since hunts are risky
            ReproHungerThreshold = 195f,
            ReproEnergyThreshold = 75f,
            ReproCooldown = 1000,  // Longer cooldown

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 80f,
            PackCoordinationRadius = 10f,
            PackShareRadius = 12f,

            // Reproduction costs
            ReproHungerCost = 60f,
            ReproEnergyCost = 40f,
            EnergyRegenRate = 0.15f, // Predators regen slower

            // Social - tight pack hunters
            GroupAffinity = 0.7f,
            PreferredGroupSize = 3f,
            CohesionStrength = 0.04f,
            AlignmentStrength = 0.03f,
            PackHunterChance = 0.7f,

            // Social params
            SocialRadius = 12f,
            LeaderInfluenceRadius = 8f,
            MaxJoinDistance = 15f,

            // Pack role speeds
            LeaderSpeedMult = 1.0f,
            FlankerSpeedMult = 1.1f,
            ChaserSpeedMult = 1.15f,

            // Separation
            SeparationRadius = 2.5f,
            SeparationStrength = 0.03f,

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

            // Trophic - medium pack predator
            // Solo wolf (mass 3.5) vs Deer (mass 4.0): 4.0 > 3.5*1.2=4.2 — borderline, can solo
            // Solo wolf vs Rabbit (mass 1.0): easily
            // Pack of 3 wolves: effectiveMass = 3.5 * 3^0.7 ≈ 7.8 — can take deer comfortably
            BodyMass = 3.5f,
            SoloHuntMaxRatio = 1.2f,
            PackHuntMassExponent = 0.7f,

            // Prey preference - soft bias toward deer and rabbit
            PreferredPrey = new List<string> { "Deer", "Rabbit" },
            PreferredPreyBias = 0.5f,

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

            // Survival - solitary hunter, needs to sustain longer between kills
            MaxHunger = 180f,
            HungerDecayRate = 0.035f,  // Half of previous - gives ~1700 ticks
            MaxLifespan = 20000,
            MaturityAge = 1200,

            // Reproduction
            ReproHungerThreshold = 180f,
            ReproEnergyThreshold = 70f,
            ReproCooldown = 800,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 60f,

            // Roaming - foxes roam wide
            RoamDistance = 80f,

            // Reproduction costs
            ReproHungerCost = 35f,
            ReproEnergyCost = 25f,
            EnergyRegenRate = 0.15f, // Predators regen slower

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

            // Trophic - small solitary predator
            // Fox (mass 2.0) vs Rabbit (mass 1.0): 1.0 <= 2.0*1.0=2.0 — yes
            // Fox (mass 2.0) vs Deer (mass 4.0): 4.0 > 2.0*1.0=2.0 — no, too large
            // Fox (mass 2.0) vs Sectid (mass 0.5): 0.5 <= 2.0 — yes, opportunistic
            BodyMass = 2.0f,
            SoloHuntMaxRatio = 1.0f,  // Can only take prey up to own mass (small predator)

            // Prey preference - prefers small prey
            PreferredPrey = new List<string> { "Rabbit" },
            PreferredPreyBias = 0.4f,  // Strong preference for familiar prey

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
            SemiAquatic = true, // Crocs hunt through water freely

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
            MaxHunger = 300f,
            HungerDecayRate = 0.03f,  // Slow metabolism
            MaxLifespan = 50000,
            MaturityAge = 4000,

            // Hunting behavior - ambush hunter
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.6f,
            TrackingRange = 40f,

            // Roaming - stays near water
            RoamDistance = 30f,
            RoamCooldown = 800,

            // Reproduction
            ReproHungerThreshold = 240f,
            ReproEnergyThreshold = 90f,
            ReproCooldown = 1500,
            ReproHungerCost = 80f,
            ReproEnergyCost = 50f,

            // Survival - tough
            MaxEnergy = 150f,
            EnergyRegenRate = 0.1f, // Already very tanky

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

            // Trophic - large ambush predator
            // Croc (mass 8.0) vs Deer (mass 4.0): easily
            // Croc (mass 8.0) vs Rabbit (mass 1.0): easily
            // Croc is solitary so no pack bonuses, but high solo mass
            BodyMass = 8.0f,
            SoloHuntMaxRatio = 1.5f,  // Powerful ambush can take large prey

            // Grazing
            CanGraze = false,

            // Visuals - large, green
            BaseColor = new Color(0.3f, 0.5f, 0.3f),
            BaseSize = 14f,
            Shape = ShapeType.Triangle,

            StatVariation = 0.15f,
        });

        // ============================
        // FACTION SPECIES (Terraformers)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Shroomer",
            Diet = DietType.Terraformer,
            DefaultSocialType = SocialType.Herd,

            // Movement - slow, sedentary fungi
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.003f,

            // Fleeing - slow but durable
            FleeRange = 4f,
            FleeSpeedMultiplier = 1.4f,

            // Fear - calm, hard to startle
            FearThreshold = 70f,
            FearMax = 100f,
            FearAccumulationRate = 3f,
            FearDecayRate = 1.5f,
            FearVigilanceDecay = 0.5f,
            FearVigilanceDuration = 60,
            DefaultFearResponse = FearResponse.Freeze,

            // Survival - long-lived, effectively can't starve near water
            MaxHunger = 210f,
            HungerDecayRate = 0.04f,
            MaxLifespan = 50000,   // Very long lived — old shroomers become huge
            MaturityAge = 2500,

            // Survival extras
            MaxEnergy = 120f,
            EnergyRegenRate = 0.2f, // Passive defender
            StarvationDamage = 0.5f,

            // Reproduction — via spores only (SporeSystem handles this)
            SporeReproducer = true,
            ReproCooldown = 9999, // Effectively disabled in ReproductionSystem

            // Growth — Shroomers grow from tiny to hulking
            GrowthMaxScale = 4f,
            GrowthRate = 0.00008f,
            InitialScale = 1f,
            AoEMinScale = 1.5f,

            // Spore reproduction parameters
            SporeSpreadChance = 0.0003f,
            SporeSpreadRadius = 8f,
            SporesPerSpread = 2,
            SporeMoistureThreshold = 0.6f,
            SporeSpreadHungerCost = 0.15f,
            SporeTransformThreshold = 60f,
            SporeWitherRate = 2f,
            SporeMoistureGainRate = 0.5f,
            SporeEnergy = 40f,

            // Roaming - sedentary
            RoamDistance = 20f,
            RoamCooldown = 800,

            // Separation - larger for growing fungi
            SeparationRadius = 3f,
            SeparationStrength = 0.02f,

            // Social - loose clusters
            GroupAffinity = 0.4f,
            PreferredGroupSize = 4f,
            CohesionStrength = 0.01f,
            AlignmentStrength = 0.005f,

            // Terrain - thrives in wet areas
            DiscomfortThreshold = 60f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Wetland, 1.2f },
                { TileType.Forest, 1.1f },
                { TileType.Grass, 0.9f },
                { TileType.Sand, 0.6f },
                { TileType.Arid, 0.4f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Wetland, -3f },   // Loves wet terrain
                { TileType.Forest, -1f },
                { TileType.Grass, 0f },
                { TileType.Sand, 3f },       // Uncomfortable on dry
                { TileType.Arid, 6f },       // Very uncomfortable on arid
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Wetland, TileType.Forest },

            // Grazing - feeds from wet/forest tiles (near water = can't starve)
            CanGraze = false,
            FeedTiles = new List<TileType> { TileType.Wetland, TileType.Forest },
            FeedNutrition = 0.5f,  // Increased — effectively can't starve on wet tiles

            // Terraform - increases moisture
            TerraformDir = TerraformDirection.Wetter,
            TerraformRadius = 2.0f,
            TerraformStrength = 0.03f,
            TerraformCooldown = 8,

            // AoE attack — scales with growth (large Shroomers have huge AoE)
            HasAoEAttack = true,
            AoEAttackRadius = 3f,   // Base radius, scales with CurrentScale
            AoEAttackDamage = 8f,   // Base damage, scales with CurrentScale
            AoEAttackCooldown = 40,

            // Trophic - medium-small fungi (huntable, spores are edible)
            BodyMass = 2.5f,

            // Visuals - purple/violet fungi
            BaseColor = new Color(0.55f, 0.23f, 0.78f),
            BaseSize = 9f,
            Shape = ShapeType.Circle,

            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Sectid",
            Diet = DietType.Terraformer,
            DefaultSocialType = SocialType.Pack,  // Pack hunters — swarm tactics

            // Movement - quick, numerous insects
            BaseWanderSpeed = 0.06f,
            DirectionChangeChance = 0.015f,

            // Combat — Sectids are predators too (hunt small prey, spores)
            // Individually weak, but effective mass scales with pack size
            // Pack of 8: 0.5 * 8^0.7 = 2.38 — can hunt rabbits and spores
            // Pack of 15: 0.5 * 15^0.7 = 3.52 — can threaten Shroomers
            HuntRange = 8f,
            AttackRange = 0.5f,
            AttackPower = 12f,    // Swarm overwhelms through numbers
            AttackCooldown = 12,
            BaseHuntSpeed = 0.11f,  // Fast closing speed for swarm
            PackHunterChance = 0.9f,  // Almost always hunt in groups

            // Fleeing - fast
            FleeRange = 6f,
            FleeSpeedMultiplier = 2.0f,

            // Fear - nervous swarm insects
            FearThreshold = 40f,
            FearMax = 80f,
            FearAccumulationRate = 8f,
            FearDecayRate = 1.2f,
            FearVigilanceDecay = 0.4f,
            FearVigilanceDuration = 80,
            DefaultFearResponse = FearResponse.Panic,

            // Survival - moderate metabolism, moderate lifespan
            MaxHunger = 165f,
            HungerDecayRate = 0.05f,  // Slower starvation (no tile feeding backup)
            MaxLifespan = 25000,
            MaturityAge = 800,   // Mature quickly

            // Survival - fragile individually
            MaxEnergy = 60f,
            EnergyRegenRate = 0.4f, // Fragile but fast recovery

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 80f,
            PackCoordinationRadius = 12f,  // Wide swarm coordination
            PackShareRadius = 10f,

            // Reproduction — via nests only (NestSystem handles this)
            NestBreeder = true,
            MaxCarryFood = 5f,    // How much food one Sectid can carry to nest
            ReproCooldown = 9999, // Effectively disabled in ReproductionSystem

            // Nest breeding parameters
            NestColonyRadius = 40f,
            NestsForExpedition = 5,
            NestSearchRadius = 15f,
            ExpeditionDistance = 80f,
            NestFoodPerSpawn = 30f,
            NestSpawnDuration = 200f,
            NestEnergy = 200f,
            FoodDeliveryRange = 4f,
            CarryingSpeed = 0.11f,  // Faster than hunt speed — motivated to deliver

            // Separation - tiny, cluster tight
            SeparationRadius = 1.5f,
            SeparationStrength = 0.015f,

            // Social - tight swarm, large groups
            GroupAffinity = 0.8f,
            PreferredGroupSize = 8f,  // Larger swarms
            CohesionStrength = 0.035f,
            AlignmentStrength = 0.025f,

            // Social params
            SocialRadius = 10f,
            LeaderInfluenceRadius = 7f,
            MaxJoinDistance = 14f,

            // Pack role speeds
            LeaderSpeedMult = 1.0f,
            FlankerSpeedMult = 1.05f,
            ChaserSpeedMult = 1.1f,

            // Terrain - thrives in dry areas
            DiscomfortThreshold = 70f,
            DiscomfortDecayRate = 4f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Arid, 1.2f },
                { TileType.Sand, 1.1f },
                { TileType.Grass, 0.9f },
                { TileType.Forest, 0.6f },
                { TileType.Wetland, 0.4f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Arid, -3f },           // Loves dry terrain
                { TileType.Sand, -1f },
                { TileType.Grass, 0f },
                { TileType.Forest, 3f },           // Uncomfortable in wet
                { TileType.Wetland, 6f },          // Very uncomfortable in wetland
                { TileType.River, -6f },           // Tolerates small creeks (net 2f/tick)
                { TileType.ShallowWater, -2f },    // Slightly less bothered than default
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Arid, TileType.Sand },

            // Feeding — Sectids do NOT graze; they must hunt to survive
            // (ravenous swarming opportunists that eat anything that moves)
            CanGraze = false,

            // Terraform - decreases moisture
            TerraformDir = TerraformDirection.Drier,
            TerraformRadius = 1.5f,
            TerraformStrength = 0.04f,
            TerraformCooldown = 6,

            // Trophic - tiny insects, hunt in packs — eat anything that moves
            // No PreferredPrey: Sectids are pure opportunists, targeting nearest viable prey
            SwarmHunter = true,            // Colony-wide bravery, can target predators
            BodyMass = 0.5f,
            SoloHuntMaxRatio = 2.5f,       // Solo: prey up to mass 1.25 (rabbits)
            PackHuntMassExponent = 0.8f,    // Stronger pack scaling (colony bravery)
            // Swarm of 3: 0.5 × 3^0.8 × 2.5 = 3.0  → foxes
            // Swarm of 5: 0.5 × 5^0.8 × 2.5 = 4.5  → wolves (3.5), deer (4.0)
            // Swarm of 8: 0.5 × 8^0.8 × 2.5 = 6.6  → wolves comfortably
            // Swarm of 12: 0.5 × 12^0.8 × 2.5 = 9.2 → crocodiles (8.0)!

            // Visuals - orange insects
            BaseColor = new Color(0.86f, 0.55f, 0.16f),
            BaseSize = 5f,   // Smaller — numerous
            Shape = ShapeType.Triangle,

            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Faeling",
            Diet = DietType.Terraformer,
            DefaultSocialType = SocialType.Solitary,  // Lone guardians spawned from crystals

            // Movement - fast roamer, covers large territory seeking terraformed tiles
            BaseWanderSpeed = 0.07f,
            DirectionChangeChance = 0.003f,

            // No fleeing — Faelings fight, not flee (ranged attack)
            FleeRange = 0f,
            FleeSpeedMultiplier = 1.0f,

            // Fear - fearless guardian
            FearThreshold = 999f,  // Effectively never afraid
            FearMax = 999f,
            FearAccumulationRate = 0f,
            FearDecayRate = 10f,
            DefaultFearResponse = FearResponse.Defensive,

            // Survival - very long-lived, doesn't starve
            MaxHunger = 195f,
            HungerDecayRate = 0f,  // Never decays — Faelings don't starve
            ImmuneToStarvation = true,
            MaxLifespan = 60000,  // Very long lived
            MaturityAge = 1000,
            MaxEnergy = 150f,
            EnergyRegenRate = 0.1f, // Already immortal via crystals
            StarvationDamage = 0f,

            // Reproduction — via crystals only (CrystalSystem handles this)
            CrystalSpawned = true,
            UnhuntableByPredators = true, // Predators don't hunt Faelings
            ReproCooldown = 9999, // Effectively disabled

            // Crystal spawning parameters
            CrystalSpawnDelay = 500,
            RangedAttackRange = 8f,
            RangedAttackDamage = 10f,
            RangedAttackCooldown = 30,

            // Growth — slow power-based growth
            GrowthMaxScale = 2.5f,
            GrowthRate = 0.00003f,
            InitialScale = 1f,

            // Roaming - vast territory to find terraformed tiles
            RoamDistance = 120f,
            RoamCooldown = 300,

            // Separation
            SeparationRadius = 3f,
            SeparationStrength = 0.02f,

            // Social - solitary guardians
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,
            CohesionStrength = 0f,
            AlignmentStrength = 0f,

            // Terrain - prefers balanced grass, moves to find terraformed tiles
            DiscomfortThreshold = 50f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Grass, 1.2f },   // Fastest on balanced terrain
                { TileType.Forest, 0.9f },
                { TileType.Sand, 0.8f },
                { TileType.Wetland, 0.7f },
                { TileType.Arid, 0.7f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Grass, -2f },     // Loves balance
                { TileType.Forest, 1f },
                { TileType.Sand, 1f },
                { TileType.Wetland, 3f },    // Dislikes extremes
                { TileType.Arid, 3f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Grass },

            // Feeding - feeds from grass (but hunger doesn't decay, so this is just flavor)
            CanGraze = false,
            FeedTiles = new List<TileType> { TileType.Grass },
            FeedNutrition = 0.45f,

            // Terraform - restores balance (gains power per tile restored)
            TerraformDir = TerraformDirection.Balanced,
            TerraformRadius = 3.0f,       // Slightly larger than before
            TerraformStrength = 0.03f,     // Stronger restoration
            TerraformCooldown = 8,

            // Trophic - medium plant creature, NOT huntable by predators
            BodyMass = 3.0f,

            // Visuals - teal/cyan
            BaseColor = new Color(0.24f, 0.86f, 0.78f),
            BaseSize = 8f,
            Shape = ShapeType.Square,

            StatVariation = 0.15f,
        });
    }
}
