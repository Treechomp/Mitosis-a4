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
            MaxEnergy = 105f,           // HP scaled to body mass (previously the default 100)
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            SpawnWeight = 1.5f,
            WrongElementGraceTicks = 80,    // Can swim rivers
            WrongElementDamageRate = 1.8f,

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
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Forest, 0.4f },
                { TileType.Taiga, 0.3f },
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
            MaxEnergy = 40f,            // Frail: a small rabbit drops in ~2 hits (Fox/Wolf) once caught
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            SpawnWeight = 1.5f,
            WrongElementGraceTicks = 40,    // Small, panics in water
            WrongElementDamageRate = 3.0f,

            // Movement - fast and erratic
            BaseWanderSpeed = 0.04f,
            DirectionChangeChance = 0.015f,  // Changes direction often

            // Fleeing - fast but catchable by foxes
            FleeRange = 8f,
            FleeSpeedMultiplier = 2.4f,  // Reduced from 2.8 - foxes hunt at 0.11, rabbits flee at 0.04*2.4=0.096
            FleeFearThreshold = 0.35f,     // frail & jumpy — bolts early

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

            // Reproduction - breeds easily (energy values fit the frail 40 HP budget: a 70
            // threshold against MaxEnergy 40 made reproduction impossible).
            ReproHungerThreshold = 180f,
            ReproEnergyThreshold = 25f,
            ReproCooldown = 600,   // Breeds a bit faster to offset higher predation losses

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
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Forest, 0.5f },
                { TileType.Shrubland, 0.35f },
                { TileType.Grass, 0.1f },
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

            // Reproduction costs - hunger is the real investment; energy cost kept below the
            // 25 threshold so breeding never pushes a 40 HP rabbit into starvation/death.
            ReproHungerCost = 55f,
            ReproEnergyCost = 15f,
            OffspringCount = 2,
            SpawnRadius = 2f,

            // Trophic - small herbivore
            BodyMass = 1.0f,

            // Visuals - small
            BaseColor = new Color(0.6f, 0.5f, 0.4f),
            BaseSize = 5f,
            Shape = ShapeType.Teardrop,

            StatVariation = 0.25f,
        });

        // ============================
        // PREDATORS
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Wolf",
            MaxEnergy = 115f,           // HP scaled to body mass (previously the default 100)
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Pack,
            SpawnWeight = 1.5f,
            WrongElementGraceTicks = 90,    // Decent swimmer
            WrongElementDamageRate = 1.5f,

            // Movement
            BaseWanderSpeed = 0.06f,
            DirectionChangeChance = 0.01f,

            // Combat — coordinated pack hunting with leader/flanker/disruptor roles
            HuntingTactic = HuntingTactic.PackCoordinated,
            HuntRange = 12f,
            AttackRange = 0.8f,
            AttackPower = 38f,
            AttackCooldown = 20,
            BaseHuntSpeed = 0.14f,

            // Survival - predators need lower decay to survive between hunts
            MaxHunger = 210f,
            HungerDecayRate = 0.04f,  // Half of previous (0.08) - gives ~1750 ticks to find food
            MaxLifespan = 24000,
            MaturityAge = 1500,

            // Reproduction - lower thresholds since hunts are risky
            ReproHungerThreshold = 148f,   // ~79%: was 195 (93%) — the one predator never cut in step 4, so it bred far less than peers and dwindled
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
            PreferredGroupSize = 4f,
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
            MaxEnergy = 90f,            // HP scaled to body mass (previously the default 100)
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 1.2f,

            // Movement - quick and agile
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.012f,

            // Combat — opportunistic ambusher/scavenger (no burrow to model: stalks, pounces on
            // small prey, and leans on carrion between kills).
            HuntingTactic = HuntingTactic.Ambush,
            HuntRange = 8f,
            AttackRange = 0.6f,
            AttackPower = 20f,
            AttackCooldown = 15,
            BaseHuntSpeed = 0.11f,

            // Ambush — quick, low-commitment pounce on small prey
            AmbushStealthGain = 0.012f,
            AmbushStealthDecay = 0.05f,
            AmbushSpeedThreshold = 0.5f,
            PounceRange = 2.5f,
            PounceSpeedMult = 3.5f,
            PounceAttackMult = 2.0f,
            PounceDuration = 10,
            PounceStealthThreshold = 0.55f,

            // Survival - solitary hunter, needs to sustain longer between kills
            MaxHunger = 180f,
            HungerDecayRate = 0.035f,  // Half of previous - gives ~1700 ticks
            MaxLifespan = 20000,
            MaturityAge = 1200,

            // Reproduction
            ReproHungerThreshold = 120f,
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

            // Terrain - good in varied terrain
            DiscomfortThreshold = 55f,
            DiscomfortDecayRate = 3f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 1.1f },   // Agile in forest
                { TileType.Grass, 1.0f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Forest, 0.4f },
                { TileType.Shrubland, 0.3f },
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
            SpawnWeight = 0.4f,
            SemiAquatic = true, // Crocs hunt through water freely

            // Movement - slow on land, ambush predator
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.003f,

            // Combat — ambush predator: stealth + explosive pounce
            HuntingTactic = HuntingTactic.Ambush,
            HuntRange = 6f,
            AttackRange = 1.2f,
            AttackPower = 50f,
            AttackCooldown = 26,   // was 40 (2s between bites) — far too slow, dragged out the kill
            BaseHuntSpeed = 0.10f,

            // Survival - long lived
            MaxHunger = 300f,
            HungerDecayRate = 0.03f,  // Slow metabolism
            MaxLifespan = 50000,
            MaturityAge = 4000,

            // Hunting behavior - ambush hunter
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.6f,
            TrackingRange = 22f,   // Nerf: was 40 — ambush at the water's edge, don't track the map
            HuntTerrain = new List<TileType> { TileType.ShallowWater, TileType.River, TileType.Wetland, TileType.Reef },

            // Ambush tactics — lurk in water, build stealth, pounce with burst speed
            AmbushStealthGain = 0.008f,    // ~125 ticks (6s) to full stealth when still
            AmbushStealthDecay = 0.04f,    // Stealth drops fast when chasing openly
            AmbushSpeedThreshold = 0.5f,   // Stealth builds when moving at ≤50% hunt speed
            PounceRange = 2.5f,            // Must be close — short explosive lunge
            PounceSpeedMult = 3.5f,        // Massive burst — 0.08 × 3.5 = 0.28 (faster than any prey flee)
            PounceAttackMult = 2.5f,       // 50 × 2.5 = 125 damage on pounce hit
            PounceDuration = 15,           // ~0.75s burst — prey can escape if it reacts fast
            PounceStealthThreshold = 0.65f, // Doesn't need perfect stealth to strike
            WaterStealthBonus = 0.012f,    // Extra stealth on water tiles (total 0.02/tick on water)

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
                { TileType.ShallowWater, 1.4f },  // Fast in shallow water
                { TileType.DeepWater, 1.6f },     // Very fast in deep water
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
            SoloHuntMaxRatio = 1.2f,  // Nerf: was 1.5 — can't gorge on the very largest prey (Musk Ox)

            // Grazing
            CanGraze = false,

            // Visuals - large, green
            BaseColor = new Color(0.3f, 0.5f, 0.3f),
            BaseSize = 14f,
            Shape = ShapeType.Fangs,

            StatVariation = 0.15f,
        });

        // ============================
        // AQUATIC SPECIES (Phase 3.1)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Fish",
            MaxEnergy = 45f,            // small/fragile (previously the default 100)
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            SpawnWeight = 0.8f,
            WrongElementGraceTicks = 40,    // Pure gill breather
            WrongElementDamageRate = 2.5f,
            IsAquatic = true,

            // Movement - fast in water, school behavior
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.02f,

            // Fleeing - scatter on predator
            FleeRange = 7f,
            FleeSpeedMultiplier = 2.5f,

            // Fear - skittish schooling fish
            FearThreshold = 25f,
            FearMax = 60f,
            FearAccumulationRate = 12f,
            FearDecayRate = 2f,
            FearVigilanceDecay = 0.5f,
            FearVigilanceDuration = 80,
            DefaultFearResponse = FearResponse.Panic,

            // Survival - short-lived, fast metabolism
            MaxHunger = 120f,
            HungerDecayRate = 0.06f,
            MaxLifespan = 10000,
            MaturityAge = 600,

            // Reproduction - prolific r-strategist: the base of the cold/aquatic food web
            // (Shark, Polar Bear, Penguin all feed on fish), so it must recover fast from heavy
            // predation. Short cooldown; the existing local-density + global-pressure brakes in
            // ReproductionSystem cap the standing population so this can't run away.
            ReproHungerThreshold = 100f,
            ReproEnergyThreshold = 32f,  // lowered to fit the smaller MaxEnergy (45)
            ReproCooldown = 300,
            ReproHungerCost = 45f,
            ReproEnergyCost = 18f,       // scaled to the smaller MaxEnergy (45)
            OffspringCount = 2,

            // Social - tight schools
            GroupAffinity = 0.9f,
            PreferredGroupSize = 8f,
            CohesionStrength = 0.04f,
            AlignmentStrength = 0.03f,
            SocialRadius = 8f,
            LeaderInfluenceRadius = 5f,

            // Terrain - water only
            DiscomfortThreshold = 20f,
            DiscomfortDecayRate = 1f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, 1.1f },
                { TileType.DeepWater, 1.2f },
                { TileType.River, 1.0f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, -3f },
                { TileType.DeepWater, -5f },
                { TileType.River, -2f },
                { TileType.Grass, 10f },
                { TileType.Sand, 10f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.ShallowWater, TileType.DeepWater },
            PreferredBiomes = new List<BiomeType> { BiomeType.Ocean, BiomeType.Coast, BiomeType.River },

            // Grazing - fish graze on aquatic vegetation (shallow water tiles)
            CanGraze = false,
            FeedTiles = new List<TileType> { TileType.ShallowWater, TileType.DeepWater, TileType.River },
            FeedNutrition = 0.3f,  // Feed from water (plankton/algae)

            // Separation
            SeparationRadius = 1.0f,
            SeparationStrength = 0.015f,

            // Trophic - small prey
            BodyMass = 0.5f,

            // Visuals - small blue
            BaseColor = new Color(0.3f, 0.6f, 0.9f),
            BaseSize = 4f,
            Shape = ShapeType.FishShape,
            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Shark",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 0.4f,
            WrongElementGraceTicks = 30,    // Needs constant water flow over gills
            WrongElementDamageRate = 3.0f,
            IsAquatic = true,

            // Movement - fast aquatic predator
            BaseWanderSpeed = 0.04f,
            DirectionChangeChance = 0.005f,

            // Combat — apex aquatic predator: explosive pursuit + long-range sensing. Very high
            // hunt speed (with the strong DeepWater swim modifier below it outruns fleeing fish
            // and penguins), and a huge tracking/hunt radius — sharks detect prey from far off.
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 24f,
            AttackRange = 1.2f,
            AttackPower = 45f,
            AttackCooldown = 30,
            BaseHuntSpeed = 0.22f,

            // Survival - long lived apex
            MaxHunger = 280f,
            HungerDecayRate = 0.025f,
            MaxLifespan = 40000,
            MaturityAge = 3000,
            MaxEnergy = 140f,
            EnergyRegenRate = 0.12f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 160f,
            HuntTerrain = new List<TileType> { TileType.DeepWater, TileType.ShallowWater, TileType.Reef },

            // Reproduction - slow
            ReproHungerThreshold = 200f,   // ~71% (predator viability; pairs with the water fix)
            ReproEnergyThreshold = 90f,
            ReproCooldown = 2000,
            ReproHungerCost = 80f,
            ReproEnergyCost = 50f,

            // Roaming - wide ocean patrol
            RoamDistance = 100f,
            RoamCooldown = 400,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - water only
            DiscomfortThreshold = 30f,
            DiscomfortDecayRate = 1f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.DeepWater, 1.7f },
                { TileType.ShallowWater, 1.3f },
                { TileType.River, 0.6f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.DeepWater, -5f },
                { TileType.ShallowWater, -2f },
                { TileType.Grass, 15f },
                { TileType.Sand, 15f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.DeepWater },
            PreferredBiomes = new List<BiomeType> { BiomeType.Ocean },

            CanGraze = false,

            // Separation
            SeparationRadius = 3f,
            SeparationStrength = 0.02f,

            // Trophic - large aquatic apex
            BodyMass = 10.0f,
            SoloHuntMaxRatio = 1.5f,
            PreferredPrey = new List<string> { "Fish", "Penguin", "Turtle" },
            PreferredPreyBias = 0.4f,

            // Visuals - large dark blue triangle
            BaseColor = new Color(0.2f, 0.3f, 0.5f),
            BaseSize = 14f,
            Shape = ShapeType.Fin,
            StatVariation = 0.15f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Frog",
            MaxEnergy = 40f,            // small/fragile (previously the default 100)
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 0.7f,
            WrongElementGraceTicks = 200,   // Nearly amphibious
            WrongElementDamageRate = 0.5f,

            // Movement - small hops
            BaseWanderSpeed = 0.03f,
            DirectionChangeChance = 0.02f,

            // Fleeing
            FleeRange = 5f,
            FleeSpeedMultiplier = 2.0f,

            // Fear - jumpy
            FearThreshold = 25f,
            FearMax = 70f,
            FearAccumulationRate = 10f,
            FearDecayRate = 1.5f,
            DefaultFearResponse = FearResponse.Panic,

            // Survival - short-lived
            MaxHunger = 140f,
            HungerDecayRate = 0.06f,
            MaxLifespan = 12000,
            MaturityAge = 800,

            // Reproduction - controlled breeding
            ReproHungerThreshold = 120f,
            ReproEnergyThreshold = 30f,  // lowered to fit the smaller MaxEnergy (Frog 40 / Lizard 45)
            ReproCooldown = 750,
            ReproHungerCost = 40f,
            ReproEnergyCost = 15f,       // scaled to the smaller MaxEnergy
            OffspringCount = 2,

            // Social - solitary
            GroupAffinity = 0.1f,
            PreferredGroupSize = 1f,

            // Terrain - river/wetland specialist
            DiscomfortThreshold = 35f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 1.0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Wetland, 1.2f },
                { TileType.Bog, 1.1f },
                { TileType.ShallowWater, 0.8f },
                { TileType.Grass, 0.9f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Wetland, 0.4f },
                { TileType.Bog, 0.4f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Wetland, -3f },
                { TileType.Bog, -2f },
                { TileType.ShallowWater, -1f },
                { TileType.Sand, 4f },
                { TileType.Arid, 6f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Wetland, TileType.Bog },
            PreferredBiomes = new List<BiomeType> { BiomeType.Wetland, BiomeType.River },

            CanGraze = true,
            GrazeNutrition = 0.3f,

            SeparationRadius = 1.5f,
            SeparationStrength = 0.02f,

            RoamDistance = 25f,
            RoamCooldown = 400,

            // Trophic - very small
            BodyMass = 0.3f,

            // Visuals - small green
            BaseColor = new Color(0.2f, 0.8f, 0.3f),
            BaseSize = 4f,
            Shape = ShapeType.Circle,
            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Turtle",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Solitary,
            SemiAquatic = true,
            SpawnWeight = 0.5f,

            // Movement - very slow
            BaseWanderSpeed = 0.015f,
            DirectionChangeChance = 0.003f,

            // Fleeing - barely flees
            FleeRange = 3f,
            FleeSpeedMultiplier = 1.3f,

            // Fear - calm, retreats into shell
            FearThreshold = 80f,
            FearMax = 120f,
            FearAccumulationRate = 2f,
            FearDecayRate = 1f,
            DefaultFearResponse = FearResponse.Freeze,

            // Survival - very long-lived, slow metabolism
            MaxHunger = 300f,
            HungerDecayRate = 0.02f,
            MaxLifespan = 60000,
            MaturityAge = 5000,
            MaxEnergy = 180f,
            EnergyRegenRate = 0.3f,

            // Reproduction - slow
            ReproHungerThreshold = 260f,
            ReproEnergyThreshold = 90f,
            ReproCooldown = 2000,
            ReproHungerCost = 60f,
            ReproEnergyCost = 40f,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - likes water edges
            DiscomfortThreshold = 70f,
            DiscomfortDecayRate = 1.5f,
            GrazingPressure = 0.5f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, 1.3f },
                { TileType.Grass, 0.7f },
                { TileType.Sand, 0.6f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, -3f },
                { TileType.Wetland, -2f },
                { TileType.Sand, 1f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Grass, TileType.Wetland, TileType.ShallowWater },
            PreferredBiomes = new List<BiomeType> { BiomeType.Coast, BiomeType.Wetland, BiomeType.River },

            CanGraze = true,
            GrazeNutrition = 0.3f,

            SeparationRadius = 2f,
            SeparationStrength = 0.02f,

            RoamDistance = 20f,
            RoamCooldown = 600,

            // Trophic - high mass (shell protection)
            BodyMass = 6.0f,

            // Visuals - dark green
            BaseColor = new Color(0.25f, 0.45f, 0.2f),
            BaseSize = 8f,
            Shape = ShapeType.Diamond,
            StatVariation = 0.15f,
        });

        // ============================
        // TEMPERATE SPECIES (Phase 3.2)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Elk",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            SpawnWeight = 1.2f,
            WrongElementGraceTicks = 100,   // Large, can wade through rivers
            WrongElementDamageRate = 1.2f,

            // Movement - large, steady
            BaseWanderSpeed = 0.025f,
            DirectionChangeChance = 0.004f,

            // Fleeing
            FleeRange = 7f,
            FleeSpeedMultiplier = 2.0f,

            // Fear - alert herd animal
            FearThreshold = 55f,
            FearMax = 100f,
            FearAccumulationRate = 5f,
            FearDecayRate = 1f,
            FearVigilanceDecay = 0.3f,
            FearVigilanceDuration = 120,
            DefaultFearResponse = FearResponse.Flee,

            // Survival - large, long-lived
            MaxHunger = 300f,
            HungerDecayRate = 0.05f,
            MaxLifespan = 35000,
            MaturityAge = 2500,
            MaxEnergy = 120f,

            // Reproduction
            ReproHungerThreshold = 260f,
            ReproEnergyThreshold = 85f,
            ReproCooldown = 800,
            ReproHungerCost = 60f,
            ReproEnergyCost = 35f,

            // Social - large herds
            GroupAffinity = 0.8f,
            PreferredGroupSize = 8f,
            CohesionStrength = 0.03f,
            AlignmentStrength = 0.02f,
            SocialRadius = 12f,
            LeaderInfluenceRadius = 8f,

            // Terrain - grassland specialist
            DiscomfortThreshold = 50f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 1.5f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Grass, 1.1f },
                { TileType.Forest, 0.7f },
                { TileType.Steppe, 1.0f },
            },
            PreferredBiomes = new List<BiomeType> { BiomeType.Grassland },

            CanGraze = true,
            GrazeNutrition = 0.6f,

            SeparationRadius = 3f,
            SeparationStrength = 0.03f,

            RoamDistance = 70f,
            RoamCooldown = 500,

            // Trophic - large herbivore, hard to solo hunt
            BodyMass = 7.0f,

            // Visuals - large brown
            BaseColor = new Color(0.55f, 0.4f, 0.25f),
            BaseSize = 11f,
            Shape = ShapeType.Circle,
            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Boar",
            Diet = DietType.Omnivore,
            DefaultSocialType = SocialType.Pack,
            WrongElementGraceTicks = 90,    // Pigs can swim
            WrongElementDamageRate = 1.5f,

            // Movement
            BaseWanderSpeed = 0.035f,
            DirectionChangeChance = 0.008f,

            // Combat — pack-coordinated omnivore
            HuntingTactic = HuntingTactic.PackCoordinated,
            HuntRange = 6f,
            AttackRange = 0.7f,
            AttackPower = 18f,
            AttackCooldown = 18,
            BaseHuntSpeed = 0.09f,

            // Fleeing - fights rather than flees
            FleeRange = 4f,
            FleeSpeedMultiplier = 1.6f,

            // Fear - defensive, fights back
            FearThreshold = 60f,
            FearMax = 100f,
            FearAccumulationRate = 4f,
            FearDecayRate = 1.5f,
            DefaultFearResponse = FearResponse.Defensive,

            // Survival
            MaxHunger = 250f,
            HungerDecayRate = 0.06f,
            MaxLifespan = 22000,
            MaturityAge = 1500,
            MaxEnergy = 110f,

            // Hunting behavior
            HuntThreshold = 0.6f,
            TrackingHungerThreshold = 0.4f,
            TrackingRange = 40f,
            PackCoordinationRadius = 8f,
            PackShareRadius = 8f,

            // Reproduction - slower breeding for omnivore
            ReproHungerThreshold = 220f,
            ReproEnergyThreshold = 75f,
            ReproCooldown = 900,
            ReproHungerCost = 60f,
            ReproEnergyCost = 35f,
            OffspringCount = 2,
            EnergyRegenRate = 0.2f,

            // Social - small packs
            GroupAffinity = 0.6f,
            PreferredGroupSize = 4f,
            CohesionStrength = 0.025f,
            AlignmentStrength = 0.015f,
            PackHunterChance = 0.5f,
            SocialRadius = 8f,

            // Terrain - forest specialist
            DiscomfortThreshold = 55f,
            DiscomfortDecayRate = 2.5f,
            GrazingPressure = 1.0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 1.1f },
                { TileType.Grass, 1.0f },
                { TileType.Shrubland, 1.05f },
            },
            PreferredBiomes = new List<BiomeType> { BiomeType.Forest, BiomeType.Grassland },

            // Grazing - omnivore: grazes at reduced efficiency, also hunts small prey
            CanGraze = true,
            GrazeNutrition = 0.3f,  // Lower than pure herbivores

            SeparationRadius = 2f,
            SeparationStrength = 0.025f,

            // Trophic - medium, tough
            BodyMass = 4.5f,
            SoloHuntMaxRatio = 0.8f,  // Only hunts small prey solo
            PreferredPrey = new List<string> { "Rabbit", "Frog" },
            PreferredPreyBias = 0.3f,

            // Visuals - dark brown
            BaseColor = new Color(0.4f, 0.3f, 0.2f),
            BaseSize = 9f,
            Shape = ShapeType.Diamond,
            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Bear",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 0.3f,
            WrongElementGraceTicks = 150,   // Excellent swimmer
            WrongElementDamageRate = 0.8f,

            // Movement - slow but powerful
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.004f,

            // Combat — solo apex ambusher: lumbers slowly, then explodes into a short high-speed
            // charge. Also wades shallows/rivers to bat fish (see M1 water model).
            HuntingTactic = HuntingTactic.Ambush,
            HuntRange = 10f,
            AttackRange = 1.5f,
            AttackPower = 60f,
            AttackCooldown = 35,
            BaseHuntSpeed = 0.11f,

            // Ambush — rare but explosive burst (high PounceSpeedMult), longer charge range
            AmbushStealthGain = 0.01f,
            AmbushStealthDecay = 0.04f,
            AmbushSpeedThreshold = 0.5f,
            PounceRange = 3.5f,
            PounceSpeedMult = 4.0f,
            PounceAttackMult = 1.8f,
            PounceDuration = 14,
            PounceStealthThreshold = 0.5f,

            // Survival - large, long-lived
            MaxHunger = 350f,
            HungerDecayRate = 0.04f,
            MaxLifespan = 40000,
            MaturityAge = 3000,
            MaxEnergy = 200f,
            EnergyRegenRate = 0.15f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 60f,

            // Reproduction - slow
            ReproHungerThreshold = 245f,
            ReproEnergyThreshold = 90f,
            ReproCooldown = 1500,
            ReproHungerCost = 90f,
            ReproEnergyCost = 60f,

            // Roaming
            RoamDistance = 80f,
            RoamCooldown = 600,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - forest specialist
            DiscomfortThreshold = 70f,
            DiscomfortDecayRate = 2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Forest, 1.0f },
                { TileType.Grass, 0.9f },
                { TileType.Taiga, 1.0f },
            },
            PreferredBiomes = new List<BiomeType> { BiomeType.Forest },

            CanGraze = false,

            SeparationRadius = 4f,
            SeparationStrength = 0.03f,

            // Trophic - apex predator, very high mass
            BodyMass = 12.0f,
            SoloHuntMaxRatio = 1.5f,
            PreferredPrey = new List<string> { "Deer", "Elk", "Boar", "Fish" },
            PreferredPreyBias = 0.4f,

            // Visuals - large dark brown
            BaseColor = new Color(0.35f, 0.2f, 0.1f),
            BaseSize = 15f,
            Shape = ShapeType.Fangs,
            StatVariation = 0.15f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Hawk",
            MaxEnergy = 88f,            // HP scaled to body mass (previously the default 100)
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            IsFlying = true,
            SpawnWeight = 0.7f,

            // Movement - fast aerial hunter
            BaseWanderSpeed = 0.06f,
            DirectionChangeChance = 0.008f,

            // Combat — solo aerial hunter, dive strikes
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 15f,
            AttackRange = 0.8f,
            AttackPower = 22f,
            AttackCooldown = 20,
            BaseHuntSpeed = 0.15f,

            // Survival
            MaxHunger = 160f,
            HungerDecayRate = 0.05f,   // Reverted: Hawk feasts on abundant carrion, doesn't starve
            MaxLifespan = 20000,
            MaturityAge = 1200,
            EnergyRegenRate = 0.15f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 80f,

            // Reproduction
            ReproHungerThreshold = 140f,   // Reverted from 112: unlimited small-prey/carrion let it boom to 160+
            ReproEnergyThreshold = 75f,
            ReproCooldown = 1000,
            ReproHungerCost = 40f,
            ReproEnergyCost = 30f,

            // Roaming - wide aerial patrol
            RoamDistance = 120f,
            RoamCooldown = 300,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - flying ignores terrain (handled by system)
            DiscomfortThreshold = 100f,
            DiscomfortDecayRate = 5f,
            PreferredBiomes = new List<BiomeType> { BiomeType.Grassland, BiomeType.Forest },

            CanGraze = false,

            SeparationRadius = 3f,
            SeparationStrength = 0.02f,

            // Trophic - small but effective aerial hunter
            BodyMass = 1.5f,
            SoloHuntMaxRatio = 1.0f,
            PreferredPrey = new List<string> { "Rabbit", "Frog", "Lizard" },
            PreferredPreyBias = 0.5f,

            // Visuals - brown triangle
            BaseColor = new Color(0.5f, 0.35f, 0.15f),
            BaseSize = 7f,
            Shape = ShapeType.Chevron,
            StatVariation = 0.2f,
        });

        // ============================
        // DESERT SPECIES (Phase 3.3)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Lizard",
            MaxEnergy = 45f,            // small/fragile (previously the default 100)
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 0.7f,
            WrongElementGraceTicks = 40,    // Cold-blooded, tires quickly
            WrongElementDamageRate = 2.5f,

            // Movement - fast on hot terrain
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.015f,

            // Fleeing - very fast
            FleeRange = 6f,
            FleeSpeedMultiplier = 2.6f,

            // Fear - skittish
            FearThreshold = 30f,
            FearMax = 70f,
            FearAccumulationRate = 10f,
            FearDecayRate = 1.5f,
            DefaultFearResponse = FearResponse.Flee,

            // Survival - short-lived, adapted to heat
            MaxHunger = 140f,
            HungerDecayRate = 0.04f,
            MaxLifespan = 12000,
            MaturityAge = 800,

            // Reproduction - controlled breeding
            ReproHungerThreshold = 120f,
            ReproEnergyThreshold = 30f,  // lowered to fit the smaller MaxEnergy (Frog 40 / Lizard 45)
            ReproCooldown = 750,
            ReproHungerCost = 40f,
            ReproEnergyCost = 15f,       // scaled to the smaller MaxEnergy
            OffspringCount = 2,

            // Social - solitary
            GroupAffinity = 0.1f,
            PreferredGroupSize = 1f,

            // Terrain - desert specialist, fast on sand
            DiscomfortThreshold = 40f,
            DiscomfortDecayRate = 3f,
            GrazingPressure = 1.0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 1.3f },
                { TileType.Arid, 1.2f },
                { TileType.Dirt, 1.1f },
                { TileType.Grass, 0.8f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0.5f },
                { TileType.Arid, 0.45f },
                { TileType.Dirt, 0.3f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, -3f },
                { TileType.Arid, -2f },
                { TileType.Dirt, -1f },
                { TileType.Forest, 3f },
                { TileType.Wetland, 5f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Sand, TileType.Arid, TileType.Dirt },
            PreferredBiomes = new List<BiomeType> { BiomeType.Desert },

            CanGraze = true,
            GrazeNutrition = 0.25f,  // Can eat sparse desert scrub

            SeparationRadius = 1.5f,
            SeparationStrength = 0.02f,

            RoamDistance = 40f,
            RoamCooldown = 300,

            // Trophic - very small
            BodyMass = 0.4f,

            // Visuals - sandy yellow
            BaseColor = new Color(0.85f, 0.75f, 0.4f),
            BaseSize = 4f,
            Shape = ShapeType.Diamond,
            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Scorpion",
            MaxEnergy = 82f,            // HP scaled to body mass (previously the default 100)
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 0.6f,
            WrongElementGraceTicks = 25,    // Cannot swim at all
            WrongElementDamageRate = 4.0f,
            AvoidsWater = true,             // Routes around (and drowns in) any water, even shallow

            // Movement - slow, ambush
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.005f,

            // Combat — ambush predator with venom
            HuntingTactic = HuntingTactic.Ambush,
            HuntRange = 5f,
            AttackRange = 0.6f,
            AttackPower = 15f,
            AttackCooldown = 25,
            BaseHuntSpeed = 0.09f,   // was 0.07 (too slow even to reposition vs wandering prey)

            // Ambush tactics — a venom ambusher only needs to land ONE bite (DOT finishes the prey),
            // so the pounce must actually connect. Was structurally broken: 2.5× from 1.5 tiles
            // couldn't close on Lizard (flees 0.13) → literally 0 kills in every run.
            AmbushDormant = true,      // sit-and-wait: lurk motionless (≈invisible, idle metabolism), pounce on proximity, envenomate, trail
            AmbushStealthGain = 0.02f, // builds the lurk's stealth faster while still
            AmbushStealthDecay = 0.05f,
            AmbushSpeedThreshold = 0.5f,
            PounceRange = 2.5f,
            PounceSpeedMult = 4.5f,   // the lunge outruns fleeing desert prey to land the venom bite
            PounceAttackMult = 2.0f,
            PounceDuration = 10,
            PounceStealthThreshold = 0.6f,

            // Venom
            VenomDamagePerTick = 1.5f,
            VenomDurationTicks = 100,  // 5 seconds of poison at 20 TPS

            // Survival
            MaxHunger = 200f,
            HungerDecayRate = 0.025f,  // Very slow metabolism
            MaxLifespan = 25000,
            MaturityAge = 1500,
            EnergyRegenRate = 0.12f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 30f,
            HuntTerrain = new List<TileType> { TileType.Sand, TileType.Arid, TileType.Dirt },

            // Reproduction
            ReproHungerThreshold = 140f,   // ~70% (predator viability)
            ReproEnergyThreshold = 70f,
            ReproCooldown = 900,
            ReproHungerCost = 40f,
            ReproEnergyCost = 25f,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - desert only
            DiscomfortThreshold = 50f,
            DiscomfortDecayRate = 2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 1.2f },
                { TileType.Arid, 1.1f },
                { TileType.Dirt, 1.0f },
                { TileType.Grass, 0.7f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0.6f },
                { TileType.Arid, 0.5f },
                { TileType.Dirt, 0.3f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, -3f },
                { TileType.Arid, -2f },
                { TileType.Wetland, 6f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Sand, TileType.Arid },
            PreferredBiomes = new List<BiomeType> { BiomeType.Desert },

            CanGraze = false,

            SeparationRadius = 2f,
            SeparationStrength = 0.02f,

            RoamDistance = 25f,
            RoamCooldown = 500,

            // Trophic - small venomous predator
            BodyMass = 0.8f,
            SoloHuntMaxRatio = 1.2f,
            PreferredPrey = new List<string> { "Lizard", "Rabbit" },
            PreferredPreyBias = 0.4f,

            // Visuals - dark red
            BaseColor = new Color(0.6f, 0.15f, 0.1f),
            BaseSize = 5f,
            Shape = ShapeType.Crescent,
            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Camel",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            WrongElementGraceTicks = 40,    // Desert animal, poor swimmer
            WrongElementDamageRate = 2.5f,
            SpawnWeight = 0.6f,

            // Movement - steady desert traveler
            BaseWanderSpeed = 0.025f,
            DirectionChangeChance = 0.004f,

            // Fleeing
            FleeRange = 5f,
            FleeSpeedMultiplier = 1.8f,

            // Fear - calm, hard to startle
            FearThreshold = 65f,
            FearMax = 100f,
            FearAccumulationRate = 3f,
            FearDecayRate = 1.2f,
            DefaultFearResponse = FearResponse.Flee,

            // Survival - desert adapted, very slow hunger
            MaxHunger = 400f,
            HungerDecayRate = 0.02f,  // Extremely slow - desert adapted
            MaxLifespan = 35000,
            MaturityAge = 3000,
            MaxEnergy = 130f,

            // Reproduction
            ReproHungerThreshold = 350f,
            ReproEnergyThreshold = 85f,
            ReproCooldown = 1000,
            ReproHungerCost = 70f,
            ReproEnergyCost = 40f,

            // Social - small herds
            GroupAffinity = 0.6f,
            PreferredGroupSize = 5f,
            CohesionStrength = 0.02f,
            AlignmentStrength = 0.015f,
            SocialRadius = 10f,

            // Terrain - desert specialist
            DiscomfortThreshold = 60f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 0.8f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 1.1f },
                { TileType.Arid, 1.15f },
                { TileType.Dirt, 1.0f },
                { TileType.Grass, 0.85f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0.35f },
                { TileType.Arid, 0.35f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, -2f },
                { TileType.Arid, -3f },
                { TileType.Forest, 3f },
                { TileType.Wetland, 5f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Sand, TileType.Arid, TileType.Dirt },
            PreferredBiomes = new List<BiomeType> { BiomeType.Desert },

            CanGraze = true,
            GrazeNutrition = 0.4f,

            SeparationRadius = 3f,
            SeparationStrength = 0.025f,

            RoamDistance = 80f,
            RoamCooldown = 400,

            // Trophic - large desert herbivore
            BodyMass = 8.0f,

            // Visuals - tan
            BaseColor = new Color(0.8f, 0.7f, 0.5f),
            BaseSize = 12f,
            Shape = ShapeType.Circle,
            StatVariation = 0.15f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Snake",
            MaxEnergy = 85f,            // HP scaled to body mass (previously the default 100)
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            SpawnWeight = 0.6f,
            WrongElementGraceTicks = 100,   // Snakes swim well
            WrongElementDamageRate = 1.0f,

            // Movement - slithering
            BaseWanderSpeed = 0.03f,
            DirectionChangeChance = 0.008f,

            // Combat — ambush stealth hunter with venom
            HuntingTactic = HuntingTactic.Ambush,
            HuntRange = 6f,
            AttackRange = 0.8f,
            AttackPower = 18f,
            AttackCooldown = 18,
            BaseHuntSpeed = 0.10f,

            // Ambush tactics - stalking predator
            AmbushStealthGain = 0.012f,
            AmbushStealthDecay = 0.06f,
            AmbushSpeedThreshold = 0.4f,
            PounceRange = 2.0f,
            PounceSpeedMult = 3.0f,
            PounceAttackMult = 2.0f,
            PounceDuration = 8,
            PounceStealthThreshold = 0.6f,

            // Venom - milder than scorpion
            VenomDamagePerTick = 1.0f,
            VenomDurationTicks = 80,

            // Survival
            MaxHunger = 220f,
            HungerDecayRate = 0.025f,
            MaxLifespan = 20000,
            MaturityAge = 1200,
            EnergyRegenRate = 0.12f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 40f,
            HuntTerrain = new List<TileType> { TileType.Sand, TileType.Shrubland, TileType.Arid, TileType.Grass },

            // Reproduction
            ReproHungerThreshold = 155f,   // ~70% (predator viability)
            ReproEnergyThreshold = 70f,
            ReproCooldown = 800,
            ReproHungerCost = 45f,
            ReproEnergyCost = 30f,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - desert and grassland
            DiscomfortThreshold = 55f,
            DiscomfortDecayRate = 2.5f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 1.1f },
                { TileType.Arid, 1.0f },
                { TileType.Grass, 1.0f },
                { TileType.Shrubland, 1.1f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0.4f },
                { TileType.Shrubland, 0.4f },
                { TileType.Arid, 0.35f },
                { TileType.Grass, 0.2f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, -2f },
                { TileType.Arid, -1f },
                { TileType.Shrubland, -1f },
                { TileType.Wetland, 3f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Sand, TileType.Arid, TileType.Dirt, TileType.Shrubland },
            PreferredBiomes = new List<BiomeType> { BiomeType.Desert, BiomeType.Grassland },

            CanGraze = false,

            SeparationRadius = 2f,
            SeparationStrength = 0.02f,

            RoamDistance = 40f,
            RoamCooldown = 400,

            // Trophic - small predator
            BodyMass = 1.2f,
            SoloHuntMaxRatio = 1.0f,
            PreferredPrey = new List<string> { "Lizard", "Frog" },
            PreferredPreyBias = 0.4f,

            // Visuals - olive green
            BaseColor = new Color(0.45f, 0.5f, 0.2f),
            BaseSize = 6f,
            Shape = ShapeType.Serpent,
            StatVariation = 0.2f,
        });

        // ============================
        // ARCTIC SPECIES (Phase 3.4)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Penguin",
            MaxEnergy = 90f,            // HP scaled to body mass (previously the default 100)
            // Omnivore so it is BOTH a predator (hunts fish) and prey (Arctic Fox / Polar Bear /
            // Shark eat it). SemiAquatic gives AvoidsOpenWater = false, so it can dive into open
            // water to chase fish. FleeingSystem runs after HuntingSystem, so a hunting penguin
            // still breaks off to flee when a predator closes in.
            Diet = DietType.Omnivore,
            DefaultSocialType = SocialType.Herd,
            SemiAquatic = true,
            SpawnWeight = 1.2f,

            // Movement - waddle on land, faster in water
            BaseWanderSpeed = 0.025f,
            DirectionChangeChance = 0.008f,

            // Combat — agile underwater pursuit hunter; feeds exclusively on fish
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 9f,
            AttackRange = 0.8f,
            AttackPower = 22f,
            AttackCooldown = 16,
            BaseHuntSpeed = 0.13f,
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 32f,
            ForageHungerThreshold = 0.8f,   // stay coupled to fish — disperse/forage early
            HuntTerrain = new List<TileType> { TileType.ShallowWater, TileType.DeepWater, TileType.Reef, TileType.River },
            ExclusivePrey = new List<string> { "Fish" },
            PreferredPrey = new List<string> { "Fish" },
            PreferredPreyBias = 0.4f,

            // Fleeing
            FleeRange = 6f,
            FleeSpeedMultiplier = 2.0f,

            // Fear - alert in colony
            FearThreshold = 40f,
            FearMax = 80f,
            FearAccumulationRate = 7f,
            FearDecayRate = 1.2f,
            DefaultFearResponse = FearResponse.Flee,

            // Survival
            MaxHunger = 200f,
            HungerDecayRate = 0.04f,
            MaxLifespan = 20000,
            MaturityAge = 1000,

            // Reproduction
            ReproHungerThreshold = 125f,
            ReproEnergyThreshold = 70f,
            ReproCooldown = 600,
            ReproHungerCost = 40f,
            ReproEnergyCost = 25f,

            // Social - tight huddle colonies
            GroupAffinity = 0.9f,
            PreferredGroupSize = 14f,
            CohesionStrength = 0.05f,   // Very tight huddling
            AlignmentStrength = 0.02f,
            SocialRadius = 8f,
            LeaderInfluenceRadius = 6f,

            // Terrain - ice/tundra, can swim
            DiscomfortThreshold = 40f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 1.2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, 1.0f },
                { TileType.Tundra, 0.9f },
                { TileType.Steppe, 0.85f },
                // Strong swimmer (REPLACE values = absolute tile speed): fast in water (faster
                // than a swimming Polar Bear ~0.8, so it can sometimes escape bears/foxes by
                // fleeing to sea, and fast enough to run fish down) but the slow land base keeps
                // it easy prey ashore. Still slower than a hunting Shark.
                { TileType.ShallowWater, 1.6f },
                { TileType.DeepWater, 1.9f },
            },
            // Seabird: comfortable BOTH on the cold coast (huddle/breed) AND in the water it feeds
            // in. Water is intrinsically high-discomfort (raw DeepWater 12/tick, ShallowWater 5),
            // so WITHOUT strong negative modifiers the discomfort system aborted a penguin's hunt
            // and drove it straight back ashore the instant it entered the water — the colony then
            // starved offshore of its own food (0% ever hunting; slow starvation in place). Values
            // net each home tile slightly comfortable (combined discomfort <= 0) so a penguin can
            // stay in the water to catch fish; hunger drives it to sea, satiety + the land-only
            // breeding cycle pulls it back to the colony.
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, -6f },
                { TileType.Tundra, -4f },
                { TileType.Steppe, -2f },
                { TileType.ShallowWater, -8f },
                { TileType.DeepWater, -14f },
                { TileType.Reef, -8f },
                { TileType.River, -10f },
                { TileType.Sand, 5f },
                { TileType.Arid, 8f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Ice, TileType.Tundra, TileType.Steppe },
            PreferredBiomes = new List<BiomeType> { BiomeType.Arctic },

            // Fish-exclusive: penguins no longer graze land at all — they live or starve by the
            // local fish supply, tightly coupling the colony to the aquatic food web.
            CanGraze = false,

            SeparationRadius = 1.0f,   // Huddle close
            SeparationStrength = 0.01f,

            RoamDistance = 30f,
            RoamCooldown = 400,

            // Trophic - small-medium
            BodyMass = 1.5f,

            // Visuals - black/white
            BaseColor = new Color(0.15f, 0.15f, 0.2f),
            BaseSize = 6f,
            Shape = ShapeType.Teardrop,
            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Polar Bear",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            SemiAquatic = true,
            SpawnWeight = 0.3f,

            // Movement - powerful but slow
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.004f,

            // Combat — solo apex predator
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 12f,
            AttackRange = 1.5f,
            AttackPower = 55f,
            AttackCooldown = 35,
            BaseHuntSpeed = 0.10f,

            // Survival - large, long-lived
            MaxHunger = 380f,
            HungerDecayRate = 0.035f,
            MaxLifespan = 40000,
            MaturityAge = 3500,
            MaxEnergy = 200f,
            EnergyRegenRate = 0.15f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 80f,
            HuntTerrain = new List<TileType> { TileType.ShallowWater, TileType.DeepWater, TileType.Ice },

            // Reproduction - slow
            ReproHungerThreshold = 270f,   // ~71% (predator viability)
            ReproEnergyThreshold = 90f,
            ReproCooldown = 1800,
            ReproHungerCost = 100f,
            ReproEnergyCost = 60f,

            // Roaming - wide arctic patrol
            RoamDistance = 100f,
            RoamCooldown = 500,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - ice/tundra, swims
            DiscomfortThreshold = 80f,
            DiscomfortDecayRate = 2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, 1.1f },
                { TileType.Tundra, 1.0f },
                { TileType.Steppe, 0.9f },
                { TileType.ShallowWater, 1.0f },
                { TileType.DeepWater, 0.8f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, -3f },
                { TileType.Tundra, -2f },
                { TileType.ShallowWater, -1f },
                { TileType.Sand, 6f },
                { TileType.Arid, 8f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Ice, TileType.Tundra, TileType.Steppe },
            PreferredBiomes = new List<BiomeType> { BiomeType.Arctic },

            CanGraze = false,

            SeparationRadius = 4f,
            SeparationStrength = 0.03f,

            // Trophic - arctic apex
            BodyMass = 14.0f,
            SoloHuntMaxRatio = 1.5f,
            PreferredPrey = new List<string> { "Penguin", "Fish", "Musk Ox" },
            PreferredPreyBias = 0.4f,

            // Visuals - white
            BaseColor = new Color(0.9f, 0.9f, 0.85f),
            BaseSize = 16f,
            Shape = ShapeType.Fangs,
            StatVariation = 0.15f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Arctic Fox",
            MaxEnergy = 92f,            // HP scaled to body mass (previously the default 100)
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            WrongElementGraceTicks = 50,    // Small, freezing water
            WrongElementDamageRate = 2.2f,
            SpawnWeight = 0.8f,

            // Movement - quick
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.012f,

            // Combat — solo arctic hunter
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 8f,
            AttackRange = 0.6f,
            AttackPower = 18f,
            AttackCooldown = 15,
            BaseHuntSpeed = 0.12f,

            // Survival
            MaxHunger = 170f,
            HungerDecayRate = 0.04f,
            MaxLifespan = 18000,
            MaturityAge = 1000,
            EnergyRegenRate = 0.15f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 60f,
            HuntTerrain = new List<TileType> { TileType.Tundra, TileType.Steppe, TileType.Ice },

            // Reproduction
            ReproHungerThreshold = 120f,   // ~70% (predator viability)
            ReproEnergyThreshold = 70f,
            ReproCooldown = 700,
            ReproHungerCost = 35f,
            ReproEnergyCost = 25f,

            // Roaming - wide range
            RoamDistance = 70f,
            RoamCooldown = 350,

            // Social - solitary
            GroupAffinity = 0.1f,
            PreferredGroupSize = 1f,

            // Terrain - tundra/grassland edge
            DiscomfortThreshold = 50f,
            DiscomfortDecayRate = 3f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Tundra, 1.1f },
                { TileType.Steppe, 1.1f },
                { TileType.Ice, 0.9f },
                { TileType.Grass, 1.0f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Ice, 0.6f },
                { TileType.Tundra, 0.5f },
                { TileType.Steppe, 0.3f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Tundra, -2f },
                { TileType.Steppe, -1f },
                { TileType.Sand, 4f },
                { TileType.Arid, 6f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Tundra, TileType.Steppe },
            PreferredBiomes = new List<BiomeType> { BiomeType.Arctic },

            CanGraze = false,

            SeparationRadius = 2f,
            SeparationStrength = 0.02f,

            // Trophic - small arctic predator
            BodyMass = 1.8f,
            SoloHuntMaxRatio = 1.0f,
            PreferredPrey = new List<string> { "Penguin", "Rabbit" },
            PreferredPreyBias = 0.4f,

            // Visuals - white/grey
            BaseColor = new Color(0.8f, 0.8f, 0.85f),
            BaseSize = 6f,
            Shape = ShapeType.Triangle,
            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Musk Ox",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            SpawnWeight = 0.8f,
            WrongElementGraceTicks = 100,   // Large, can ford water
            WrongElementDamageRate = 1.5f,

            // Movement - slow, steady
            BaseWanderSpeed = 0.02f,
            DirectionChangeChance = 0.004f,

            // Fleeing - barely flees, stands ground
            FleeRange = 4f,
            FleeSpeedMultiplier = 1.4f,
            FleeFearThreshold = 0.7f,      // bold defensive herd — holds ground longer

            // Fear - defensive herd formation
            FearThreshold = 70f,
            FearMax = 120f,
            FearAccumulationRate = 3f,
            FearDecayRate = 1f,
            DefaultFearResponse = FearResponse.Defensive,

            // Survival - tough, long-lived
            MaxHunger = 320f,
            HungerDecayRate = 0.04f,
            MaxLifespan = 30000,
            MaturityAge = 2500,
            MaxEnergy = 160f,
            EnergyRegenRate = 0.25f,

            // Reproduction
            ReproHungerThreshold = 280f,
            ReproEnergyThreshold = 85f,
            ReproCooldown = 900,
            ReproHungerCost = 65f,
            ReproEnergyCost = 40f,

            // Social - defensive herds
            GroupAffinity = 0.8f,
            PreferredGroupSize = 6f,
            CohesionStrength = 0.04f,
            AlignmentStrength = 0.02f,
            SocialRadius = 10f,
            LeaderInfluenceRadius = 7f,

            // Terrain - tundra/steppe
            DiscomfortThreshold = 55f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 1.2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Tundra, 1.0f },
                { TileType.Steppe, 1.1f },
                { TileType.Ice, 0.7f },
                { TileType.Grass, 0.9f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Tundra, 0.3f },
                { TileType.Ice, 0.2f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Tundra, -3f },
                { TileType.Steppe, -2f },
                { TileType.Sand, 5f },
                { TileType.Arid, 7f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Tundra, TileType.Steppe },
            PreferredBiomes = new List<BiomeType> { BiomeType.Arctic },

            CanGraze = true,
            GrazeNutrition = 0.5f,

            SeparationRadius = 2.5f,
            SeparationStrength = 0.025f,

            RoamDistance = 50f,
            RoamCooldown = 500,

            // Trophic - very high mass, tough
            BodyMass = 10.0f,

            // Visuals - dark brown
            BaseColor = new Color(0.3f, 0.25f, 0.15f),
            BaseSize = 13f,
            Shape = ShapeType.Diamond,
            StatVariation = 0.15f,
        });

        // ============================
        // TROPICAL SPECIES (Phase 3.5)
        // ============================

        Register(new SpeciesDefinition
        {
            Name = "Monkey",
            MaxEnergy = 84f,            // HP scaled to body mass (previously the default 100)
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            WrongElementGraceTicks = 80,    // Some monkeys swim
            WrongElementDamageRate = 1.5f,
            SpawnWeight = 0.8f,

            // Movement - fast, erratic
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.025f,  // Very erratic - hard to predict

            // Fleeing - very fast, unpredictable
            FleeRange = 7f,
            FleeSpeedMultiplier = 2.5f,

            // Fear - alert but not panicky
            FearThreshold = 35f,
            FearMax = 80f,
            FearAccumulationRate = 8f,
            FearDecayRate = 1.5f,
            DefaultFearResponse = FearResponse.Flee,

            // Survival
            MaxHunger = 180f,
            HungerDecayRate = 0.06f,
            MaxLifespan = 20000,
            MaturityAge = 1200,

            // Reproduction
            ReproHungerThreshold = 155f,
            ReproEnergyThreshold = 70f,
            ReproCooldown = 600,
            ReproHungerCost = 40f,
            ReproEnergyCost = 25f,

            // Social - troops
            GroupAffinity = 0.7f,
            PreferredGroupSize = 6f,
            CohesionStrength = 0.025f,
            AlignmentStrength = 0.015f,
            SocialRadius = 10f,
            LeaderInfluenceRadius = 7f,

            // Terrain - jungle/forest specialist
            DiscomfortThreshold = 45f,
            DiscomfortDecayRate = 2.5f,
            GrazingPressure = 1.5f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 1.3f },
                { TileType.Forest, 1.2f },
                { TileType.Grass, 0.8f },
                { TileType.Savanna, 0.85f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 0.5f },
                { TileType.Forest, 0.4f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, -3f },
                { TileType.Forest, -2f },
                { TileType.Sand, 4f },
                { TileType.Arid, 6f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Jungle, TileType.Forest },
            PreferredBiomes = new List<BiomeType> { BiomeType.Tropical },

            CanGraze = true,
            GrazeNutrition = 0.4f,

            SeparationRadius = 1.5f,
            SeparationStrength = 0.02f,

            RoamDistance = 40f,
            RoamCooldown = 300,

            // Trophic - small, agile
            BodyMass = 1.2f,

            // Visuals - brown
            BaseColor = new Color(0.6f, 0.4f, 0.2f),
            BaseSize = 6f,
            Shape = ShapeType.Circle,
            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Parrot",
            MaxEnergy = 42f,            // small/fragile (previously the default 100)
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            IsFlying = true,
            SpawnWeight = 0.5f,

            // Movement - aerial
            BaseWanderSpeed = 0.055f,
            DirectionChangeChance = 0.015f,

            // Fleeing
            FleeRange = 8f,
            FleeSpeedMultiplier = 2.3f,

            // Fear - alert flocking
            FearThreshold = 30f,
            FearMax = 70f,
            FearAccumulationRate = 8f,
            FearDecayRate = 1.5f,
            DefaultFearResponse = FearResponse.Flee,

            // Survival
            MaxHunger = 130f,
            HungerDecayRate = 0.06f,
            MaxLifespan = 18000,
            MaturityAge = 1000,

            // Reproduction
            ReproHungerThreshold = 110f,
            ReproEnergyThreshold = 30f,  // lowered to fit the smaller MaxEnergy (42)
            ReproCooldown = 550,
            ReproHungerCost = 30f,
            ReproEnergyCost = 14f,       // scaled to the smaller MaxEnergy (42)

            // Social - flocks
            GroupAffinity = 0.8f,
            PreferredGroupSize = 6f,
            CohesionStrength = 0.03f,
            AlignmentStrength = 0.025f,
            SocialRadius = 10f,
            LeaderInfluenceRadius = 7f,

            // Terrain - flying ignores terrain, prefers tropical
            DiscomfortThreshold = 80f,
            DiscomfortDecayRate = 3f,
            GrazingPressure = 1.0f,
            PreferredBiomes = new List<BiomeType> { BiomeType.Tropical, BiomeType.Forest },
            AllowedSpawnTiles = new List<TileType> { TileType.Jungle, TileType.Forest, TileType.Savanna },

            CanGraze = true,
            GrazeNutrition = 0.3f,

            SeparationRadius = 1.5f,
            SeparationStrength = 0.02f,

            RoamDistance = 60f,
            RoamCooldown = 300,

            // Trophic - small
            BodyMass = 0.4f,

            // Visuals - bright green
            BaseColor = new Color(0.2f, 0.9f, 0.3f),
            BaseSize = 5f,
            Shape = ShapeType.Chevron,
            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Jaguar",
            Diet = DietType.Carnivore,
            DefaultSocialType = SocialType.Solitary,
            WrongElementGraceTicks = 120,   // Jaguars swim well
            WrongElementDamageRate = 1.0f,
            SpawnWeight = 0.4f,

            // Movement
            BaseWanderSpeed = 0.04f,
            DirectionChangeChance = 0.006f,

            // Combat — ambush predator with jungle stealth
            HuntingTactic = HuntingTactic.Ambush,
            HuntRange = 10f,
            AttackRange = 1.0f,
            AttackPower = 45f,
            AttackCooldown = 25,
            BaseHuntSpeed = 0.12f,

            // Ambush - jungle specialist
            AmbushStealthGain = 0.01f,
            AmbushStealthDecay = 0.04f,
            AmbushSpeedThreshold = 0.5f,
            PounceRange = 3.0f,
            PounceSpeedMult = 3.5f,
            PounceAttackMult = 2.5f,
            PounceDuration = 12,
            PounceStealthThreshold = 0.6f,

            // Survival - large jungle cat
            MaxHunger = 260f,
            HungerDecayRate = 0.035f,
            MaxLifespan = 30000,
            MaturityAge = 2000,
            MaxEnergy = 140f,
            EnergyRegenRate = 0.15f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 70f,

            // Reproduction
            ReproHungerThreshold = 185f,   // ~71% (predator viability)
            ReproEnergyThreshold = 85f,
            ReproCooldown = 1200,
            ReproHungerCost = 70f,
            ReproEnergyCost = 45f,

            // Roaming
            RoamDistance = 80f,
            RoamCooldown = 500,

            // Social - solitary
            GroupAffinity = 0f,
            PreferredGroupSize = 0f,

            // Terrain - jungle specialist
            DiscomfortThreshold = 60f,
            DiscomfortDecayRate = 2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 1.2f },
                { TileType.Forest, 1.0f },
                { TileType.Savanna, 0.9f },
                { TileType.Grass, 0.85f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, -3f },
                { TileType.Forest, -1f },
                { TileType.Sand, 4f },
                { TileType.Arid, 6f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Jungle, TileType.Forest },
            PreferredBiomes = new List<BiomeType> { BiomeType.Tropical },

            CanGraze = false,

            SeparationRadius = 3f,
            SeparationStrength = 0.025f,

            // Trophic - large jungle apex
            BodyMass = 7.0f,
            SoloHuntMaxRatio = 1.5f,
            PreferredPrey = new List<string> { "Monkey", "Tapir", "Deer" },
            PreferredPreyBias = 0.4f,

            // Visuals - spotted gold
            BaseColor = new Color(0.85f, 0.65f, 0.2f),
            BaseSize = 12f,
            Shape = ShapeType.Fangs,
            StatVariation = 0.15f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Tapir",
            Diet = DietType.Herbivore,
            DefaultSocialType = SocialType.Herd,
            SpawnWeight = 0.7f,
            WrongElementGraceTicks = 120,   // Tapirs love water, strong swimmers
            WrongElementDamageRate = 0.8f,

            // Movement - medium pace
            BaseWanderSpeed = 0.025f,
            DirectionChangeChance = 0.005f,

            // Fleeing - shy, low threshold
            FleeRange = 8f,
            FleeSpeedMultiplier = 2.0f,

            // Fear - very shy, panics easily
            FearThreshold = 25f,
            FearMax = 70f,
            FearAccumulationRate = 10f,
            FearDecayRate = 0.8f,
            FearVigilanceDecay = 0.2f,
            FearVigilanceDuration = 200,
            DefaultFearResponse = FearResponse.Flee,

            // Survival
            MaxHunger = 270f,
            HungerDecayRate = 0.05f,
            MaxLifespan = 28000,
            MaturityAge = 2000,
            MaxEnergy = 110f,

            // Reproduction
            ReproHungerThreshold = 235f,
            ReproEnergyThreshold = 80f,
            ReproCooldown = 700,
            ReproHungerCost = 55f,
            ReproEnergyCost = 35f,

            // Social - small groups
            GroupAffinity = 0.5f,
            PreferredGroupSize = 4f,
            CohesionStrength = 0.02f,
            AlignmentStrength = 0.015f,
            SocialRadius = 8f,

            // Terrain - jungle grazer
            DiscomfortThreshold = 45f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 1.5f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 1.0f },
                { TileType.Forest, 0.95f },
                { TileType.Savanna, 0.9f },
                { TileType.Grass, 0.85f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 0.45f },
                { TileType.Forest, 0.35f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, -3f },
                { TileType.Forest, -1f },
                { TileType.Sand, 4f },
                { TileType.Arid, 6f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Jungle, TileType.Forest },
            PreferredBiomes = new List<BiomeType> { BiomeType.Tropical },

            CanGraze = true,
            GrazeNutrition = 0.5f,

            SeparationRadius = 2.5f,
            SeparationStrength = 0.025f,

            RoamDistance = 40f,
            RoamCooldown = 400,

            // Trophic - medium
            BodyMass = 5.0f,

            // Visuals - dark grey
            BaseColor = new Color(0.35f, 0.3f, 0.3f),
            BaseSize = 10f,
            Shape = ShapeType.Teardrop,
            StatVariation = 0.2f,
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
            AoEMinScale = 1.0f,   // Defend from birth (weak at small scale, devastating when mature)

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

            // AoE attack — S-curve growth scaling (smoothstep):
            //   Radius/damage = max_value * (minFactor + (1-minFactor) * smoothstep(t))
            //   where t = (scale - 1.0) / (4.0 - 1.0), smoothstep = t²(3-2t)
            // Scale 1.0 (birth):  factor=0.08  → radius 2,   damage 2.4   (nearly harmless)
            // Scale 1.5:          factor=0.09  → radius 2.3,  damage 2.7  (still weak)
            // Scale 2.5 (mid):    factor=0.50  → radius 12.5, damage 15   (growth spurt)
            // Scale 3.5:          factor=0.91  → radius 22.8, damage 27.3 (powerful)
            // Scale 4.0 (elder):  factor=1.00  → radius 25,   damage 30   (max, tapers)
            HasAoEAttack = true,
            AoEAttackRadius = 25f,       // Max radius at full growth
            AoEAttackDamage = 30f,       // Max damage at full growth
            AoEAttackCooldown = 60,      // Base cooldown (used for combat pulses)
            AoEPassiveCooldown = 350,    // Slow passive pulses (every 17.5s at 20 TPS)
            AoECombatCooldown = 60,      // Reactive pulses when attacked (3s)
            AoEMinScaleFactor = 0.08f,   // 8% of max at birth → S-curve → 100% at max

            // Thorn defense — S-curve growth-scaled counter-damage on melee attackers
            // Scale 1.0: 20 * 0.08 = 1.6 dmg/hit  (barely stings)
            // Scale 2.5: 20 * 0.50 = 10 dmg/hit   (serious deterrent)
            // Scale 4.0: 20 * 1.00 = 20 dmg/hit   (lethal to small attackers)
            ThornDamageBase = 20f,       // Max thorn damage (scaled by same S-curve)

            // Trophic - medium-small fungi (huntable, spores are edible)
            BodyMass = 2.5f,

            // Visuals - purple/violet fungi
            BaseColor = new Color(0.55f, 0.23f, 0.78f),
            BaseSize = 9f,
            Shape = ShapeType.Mushroom,

            StatVariation = 0.2f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Sectid",
            Diet = DietType.Terraformer,
            DefaultSocialType = SocialType.Pack,  // Pack hunters — swarm tactics
            WrongElementGraceTicks = 40,    // Insects drown quickly
            WrongElementDamageRate = 2.5f,
            AvoidsWater = true,             // Routes around (and drowns in) any water, even shallow
            CarrionChopRate = 6f,           // Strips carcasses fast into carrier sacks (was hardcoded)

            // Movement - quick, numerous insects
            BaseWanderSpeed = 0.06f,
            DirectionChangeChance = 0.015f,

            // Combat — Sectids are predators too (hunt small prey, spores)
            // Individually weak, but effective mass scales with pack size
            // Pack of 8: 0.5 * 8^0.8 = 3.03 — can hunt rabbits and foxes
            // Pack of 15: 0.5 * 15^0.8 = 5.18 — can threaten wolves and Shroomers
            HuntRange = 12f,       // Wider detection range for swarming
            AttackRange = 1.3f,    // Wide enough that a whole swarm dogpiles a target at once
                                   // (must exceed SeparationRadius or only one bites at a time)
            AttackPower = 16f,     // Swarm overwhelms through numbers
            AttackCooldown = 10,   // Faster bite cycle
            BaseHuntSpeed = 0.13f, // Fast closing speed for swarm
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
            // Lone Sectids flee cleanly instead of jittering in place; grouped Sectids rally and
            // mob the attacker instead (DEFENSIVE RALLY in HuntingSystem).
            DefaultFearResponse = FearResponse.Flee,

            // Survival - moderate metabolism, moderate lifespan
            MaxHunger = 165f,
            HungerDecayRate = 0.05f,  // Slower starvation (no tile feeding backup)
            MaxLifespan = 25000,
            MaturityAge = 800,   // Mature quickly

            // Survival - fragile individually (but not one-shot; 60 starved/died too fast)
            MaxEnergy = 90f,
            EnergyRegenRate = 0.4f, // Fragile but fast recovery

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 80f,
            PackCoordinationRadius = 16f,  // Wide swarm coordination
            PackShareRadius = 10f,

            // Reproduction — via nests only (NestSystem handles this)
            NestBreeder = true,
            MaxCarryFood = 12f,   // Larger loads so big kills aren't wasted at the carry cap
            ReproCooldown = 9999, // Effectively disabled in ReproductionSystem

            // Nest breeding parameters
            NestColonyRadius = 40f,
            NestsForExpedition = 5,
            NestSearchRadius = 15f,
            ExpeditionDistance = 80f,
            NestFoodPerSpawn = 18f,  // Cheaper larva so a collapsing prey base can still fund the swarm
            NestSpawnDuration = 200f,
            NestEnergy = 200f,
            FoodDeliveryRange = 4f,
            CarryingSpeed = 0.11f,  // Faster than hunt speed — motivated to deliver

            // Separation - tiny, cluster tight (below AttackRange so the swarm can pile onto prey)
            SeparationRadius = 1.0f,
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

            // Terrain - thrives in dry areas, tolerant when hunting
            DiscomfortThreshold = 120f,   // High tolerance — relentless swarm pushes into enemy terrain
            DiscomfortDecayRate = 5f,
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
            HuntingTactic = HuntingTactic.Swarm,  // Colony-wide rush, no retreat, count all same-species
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
            Shape = ShapeType.Star,

            StatVariation = 0.25f,
        });

        Register(new SpeciesDefinition
        {
            Name = "Faeling",
            Diet = DietType.Terraformer,
            DefaultSocialType = SocialType.Solitary,  // Lone guardians spawned from crystals

            // Movement - fast roamer, patrols territory seeking damaged terrain to restore
            BaseWanderSpeed = 0.09f,   // Faster patrol speed
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
            RangedAttackRange = 12f,  // Wide engagement range — intercepts terraformers early
            RangedAttackDamage = 12f, // Stronger bolts
            RangedAttackCooldown = 20, // Faster attack rate

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

            // Terrain - immune to discomfort (guardians go INTO damaged terrain to fix it)
            DiscomfortThreshold = 999f,  // Never triggers escape — terrain-seeking AI handles pathing
            DiscomfortDecayRate = 10f,
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
