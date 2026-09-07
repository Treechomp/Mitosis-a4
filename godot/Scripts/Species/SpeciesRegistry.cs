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

            // Fleeing. A deer is the biggest thing a Sectid swarm can realistically pull down, so
            // it has to be genuinely hard to catch — the payoff for taking one is now large (see
            // the body-mass carcass curve), and the difficulty is what stops that payoff from
            // being free. Absolute flight speed is what matters, not the multiplier: at the old
            // 2.2× a deer fled at 0.03×2.2 = 0.066, SLOWER than a rabbit's 0.096 and half a
            // Sectid's 0.13 closing speed, so a lone Sectid simply ran one down. At 3.6× it makes
            // 0.108 — outpacing a rabbit as a long-legged runner should, still catchable by a
            // 0.14 wolf (its proper predator), but now a real chase for a swarm.
            FleeRange = 10f,             // Long sightlines on open ground; spots threats early
            FleeSpeedMultiplier = 3.6f,
            FleeFearThreshold = 0.4f,    // Commits to running sooner than the 0.5 default
            // Stamina: deep-chested distance runner. Drains slower and recovers faster than the
            // 0.005/0.0025 default, and stays fast when tired — a deer outlasts pursuit rather
            // than juking like a rabbit, so a relay of swarm members can no longer simply wear
            // one down to the tired floor.
            FleeStaminaDrain = 0.0028f,
            FleeStaminaRecovery = 0.004f,
            FleeTiredSpeedFloor = 0.62f,

            // Fear - standard herd animal, alert but not paranoid
            FearThreshold = 50f,
            FearMax = 100f,
            FearAccumulationRate = 9f,   // Bolts promptly once it does notice
            FearDecayRate = 1f,
            FearVigilanceDecay = 0.3f,
            FearVigilanceDuration = 120,
            // Directed Flee, not Panic: a panicking deer swerves randomly and runs into the
            // swarm. Committed straight-line flight is what actually makes it hard to bring down.
            DefaultFearResponse = FearResponse.Flee,

            // Survival
            MaxHunger = 240f,
            HungerDecayRate = 0.05f,
            MaxLifespan = 30000,
            MaturityAge = 2000,

            // Reproduction — deer consistently over-performed in wide-world runs, outgrowing what
            // predators could crop. Slower turnaround and a fuller belly required before breeding,
            // so herd growth now depends on actually finding good pasture rather than being
            // near-automatic. Works together with the higher graze draw above.
            ReproHungerThreshold = 228f,   // of MaxHunger 240 — must be genuinely well fed
            ReproEnergyThreshold = 80f,
            ReproCooldown = 900,

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

            // Grazing — a big browser takes a lot per mouthful and gives up on thin pasture
            // early, so a herd visibly strips a meadow and then has to move on. Roughly 3× a
            // rabbit's draw, and it abandons ground at 0.28 nutrition where a rabbit stays
            // down to 0.08 — the two species therefore migrate at different times rather than
            // sweeping the map as one front.
            CanGraze = true,
            GrazeConsumeRate = 0.035f,
            MinAcceptableNutrition = 0.28f,
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

            // Grazing — light feeder that can still get by on ground a deer has given up on,
            // so the two species stagger their migrations instead of moving as one mass.
            CanGraze = true,
            GrazeConsumeRate = 0.012f,
            MinAcceptableNutrition = 0.08f,
            GrazeNutrition = 0.3f,
            IsFungivore = true,  // nibbles Shroomer spores (not living sprouts — see
                                 // FungivoreEatsSprouts; adult Shroomers now repel grazers)

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

            // Diet: crocs are the resident predator of the rivers and swamps where inland fish
            // live, so fish and otters belong on the menu alongside the big game that comes down
            // to drink. Gives freshwater a second check on the shoal.
            PreferredPrey = new List<string> { "Fish", "Otter", "Tapir", "Deer" },
            PreferredPreyBias = 0.5f,

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
            // Fully coupled to the water they're in: a rich shelf breeds a shoal back after heavy
            // predation, exhausted water barely breeds at all. This is the brake that works where
            // there is no predator — an isolated pond fills until the plankton gives out and then
            // levels off, instead of solidifying with fish because nothing there eats them.
            BreedingNutritionSensitivity = 1.0f,

            // Social - tight schools
            GroupAffinity = 0.9f,
            PreferredGroupSize = 8f,
            CohesionStrength = 0.04f,
            AlignmentStrength = 0.03f,
            SocialRadius = 8f,
            LeaderInfluenceRadius = 5f,

            // Terrain - water only. GrazingPressure gives stripped water the same "move on" push
            // that bare pasture gives a grazer, so a shoal drifts to fresh feeding grounds.
            DiscomfortThreshold = 20f,
            DiscomfortDecayRate = 1f,
            GrazingPressure = 1.2f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, 1.1f },
                { TileType.DeepWater, 1.2f },
                { TileType.River, 1.0f },
            },
            // Only land is uncomfortable — water is the home element and costs nothing
            // (TerrainProfile.DiscomfortRate).
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Grass, 10f },
                { TileType.Sand, 10f },
            },
            // Shoaling fish hold to the sunlit shelf and the reef rather than the open deep. This
            // is what puts them where their predators can reach them: penguins can only dive so
            // far from the colony, and it gives sharks a reason to come in off the drop-off.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.ShallowWater, 0f },
                { TileType.Reef, 0f },
                { TileType.River, 0.1f },
                { TileType.DeepWater, 0.25f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.ShallowWater, TileType.DeepWater },
            PreferredBiomes = new List<BiomeType> { BiomeType.Ocean, BiomeType.Coast, BiomeType.River },

            // Feeding — the water column itself (plankton/algae), which is now a finite, per-tile
            // resource rather than an inexhaustible supply. A shoal strips its patch over a few
            // hundred ticks and the water regrows over ~1400, so the shoal has to keep moving;
            // the Reef and the sunlit shelf are worth far more than the open deep (NutritionCap).
            CanGraze = false,
            FeedTiles = new List<TileType>
            {
                TileType.ShallowWater, TileType.DeepWater, TileType.River, TileType.Reef,
            },
            FeedNutrition = 0.3f,
            FeedConsumeRate = 0.006f,
            MinAcceptableNutrition = 0.06f,  // thin water is worth leaving, not starving on

            // Separation
            SeparationRadius = 1.0f,
            SeparationStrength = 0.015f,

            // Trophic - small prey, but oily and calorie-dense: the generic mass curve rated a
            // fish at ~6 food, so filling a shark took ~47 of them and an otter ~25. That is a
            // treadmill no shoal survives — the predator isn't overpowered, its meal is just
            // worthless, so it must kill constantly to stay alive. Pricing a fish as a real meal
            // is what lets a predator be sated by a few and leave the rest of the shoal alone.
            BodyMass = 0.5f,
            NutritionValue = 18f,

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
            // Water carries no intrinsic discomfort for an aquatic species (TerrainProfile.
            // DiscomfortRate) — only land does, and these values pile onto that. A shark's
            // preference for the open ocean is a STEERING pull (below), not an intolerance:
            // expressing it as discomfort is what previously drove sharks out of the shallows
            // and, once the accumulated debt passed the escape threshold, out of hunting entirely.
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Grass, 15f },
                { TileType.Sand, 15f },
            },
            // Prefers deep water, patrols the reef, and hunts the shallows freely — the shelf is
            // where the fish are. River is cramped for a big shark, but not barred.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.DeepWater, 0f },
                { TileType.Reef, 0.1f },
                { TileType.ShallowWater, 0.3f },
                { TileType.River, 0.45f },
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
            // Proper game first. Fish are a famine ration: an apex predator that treats the shoal
            // as its staple crops the base of the food web flat, which is what happened before
            // this tier existed (Fish and Penguin scored identically, so the shark simply ate
            // whichever was nearer — and fish are always nearer).
            PreferredPrey = new List<string> { "Penguin", "Turtle" },
            PreferredPreyBias = 0.4f,
            FallbackPrey = new List<string> { "Fish" },
            FallbackPreyHunger = 0.45f,

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

            // Amphibian: at home in and out of the water, and never drowns in it.
            SemiAquatic = true,

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
            // Habitat steering: Wetland (0.15) and Bog (0.25) are penalised by the generic
            // weights, walking a marsh dweller out of its marsh.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Wetland, 0f },
                { TileType.Bog, 0.05f },
                { TileType.Jungle, 0.15f },
                { TileType.Forest, 0.2f },
                { TileType.Grass, 0.35f },
                { TileType.Shrubland, 0.5f },
                { TileType.Steppe, 0.6f },
                { TileType.Savanna, 0.6f },
                { TileType.Dirt, 0.7f },
                { TileType.Sand, 0.8f },
                { TileType.Arid, 0.9f },
                { TileType.Tundra, 0.8f },
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

        // The freshwater answer to fish. Sharks and penguins only patrol the sea, so a pond or a
        // river fish population had nothing above it at all — hawks skim the odd one, which never
        // came close to matching a shoal's breeding rate. The otter is a small semi-aquatic fish
        // specialist built on the same pattern as the penguin (hunts in the water, breeds on the
        // bank), and being an omnivore it is itself prey for wolves, foxes, bears and jaguars —
        // so it plugs into the land food web rather than sitting on top of an isolated one.
        Register(new SpeciesDefinition
        {
            Name = "Otter",
            MaxEnergy = 70f,
            Diet = DietType.Omnivore,      // predator AND prey, like the Penguin
            DefaultSocialType = SocialType.Territorial,   // holds a stretch of bank
            SemiAquatic = true,
            SpawnWeight = 0.8f,   // thinly spread by territory, but seeded often enough to persist

            // Movement - clumsy ashore, superb in the water
            BaseWanderSpeed = 0.03f,
            DirectionChangeChance = 0.01f,

            // Combat — agile pursuit hunter, small teeth but fish are fragile
            // Hunts only when actually hungry. The default opportunistic gate (0.8) means a
            // predator is hunting nearly all the time, killing far more than it can eat — with a
            // small fast prey animal in a confined pond that is the difference between an otter
            // that thins a shoal and one that empties it. Short reach for the same reason: an
            // otter should command a stretch of water, not all of it at once.
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 6f,
            AttackRange = 0.8f,
            AttackPower = 20f,
            AttackCooldown = 14,
            BaseHuntSpeed = 0.14f,
            HuntThreshold = 0.55f,
            TrackingHungerThreshold = 0.4f,
            TrackingRange = 40f,
            ForageHungerThreshold = 0.8f,
            // Forages the shallows and the river, not the open deep. That boundary matters for
            // more than flavour: deep water is the one place no land or bank-dwelling predator can
            // follow a fish, so it becomes the reservoir a cropped shoal recovers from. Without it
            // a lake has no refuge at all and the otters simply finish the fish off.
            HuntTerrain = new List<TileType>
            {
                TileType.River, TileType.ShallowWater, TileType.Reef,
            },
            ExclusivePrey = new List<string> { "Fish" },
            PreferredPrey = new List<string> { "Fish" },
            PreferredPreyBias = 0.4f,

            // Fleeing - quick and slippery
            FleeRange = 7f,
            FleeSpeedMultiplier = 2.2f,
            FearThreshold = 35f,
            FearMax = 80f,
            FearAccumulationRate = 9f,
            FearDecayRate = 1.5f,
            DefaultFearResponse = FearResponse.Flee,

            // Survival. Appetite is deliberately modest against the new fish value (18): about
            // twenty fish a lifetime-window, which a healthy shoal replaces from natural turnover.
            // A hungrier otter simply eats its own pond empty and starves with it.
            MaxHunger = 100f,
            HungerDecayRate = 0.03f,
            MaxLifespan = 16000,
            MaturityAge = 900,

            // Reproduction - on the bank, in a holt. Deliberately slow for a small animal: an
            // otter is meant to hold a shoal in check, not convert it into otters. It must be
            // near-full to breed and waits a long season between litters, so its numbers lag the
            // fish rather than tracking them — a fast-breeding specialist just eats the pond out
            // and then starves with it.
            // The wide SocialRadius above is what caps otter density, so the breeding CYCLE can
            // afford to be quick: it has to be, because an otter is small prey for every land
            // carnivore in the world and a slow-breeding sparse animal simply gets eaten out of
            // existence (which is what happened on its first outing).
            ReproHungerThreshold = 78f,
            ReproEnergyThreshold = 45f,
            ReproCooldown = 1500,
            ReproHungerCost = 35f,
            ReproEnergyCost = 20f,

            // Social — territorial, and this is the knob that actually decides how many otters a
            // stretch of water can hold. ReproductionSystem suppresses breeding above
            // max(6, PreferredGroupSize × 2) neighbours within 1.5 × SocialRadius, so a wide
            // social radius is what makes them SPARSE. Left at ordinary herd values (radius 7)
            // a pair on one pond bred to fifty and ate the shoal to extinction while never once
            // dropping below 80% fed — the runaway was fecundity, not appetite.
            GroupAffinity = 0.3f,
            PreferredGroupSize = 3f,
            CohesionStrength = 0.015f,
            AlignmentStrength = 0.01f,
            SocialRadius = 18f,
            LeaderInfluenceRadius = 8f,

            RoamDistance = 45f,
            RoamCooldown = 350,

            // Terrain - riverbank specialist; water costs nothing (semi-aquatic, see
            // TerrainProfile.DiscomfortRate), so these values only describe life ashore.
            DiscomfortThreshold = 45f,
            DiscomfortDecayRate = 2f,
            GrazingPressure = 0f,
            TerrainSpeedModifiers = new Dictionary<TileType, float>
            {
                { TileType.River, 1.7f },
                { TileType.ShallowWater, 1.5f },
                { TileType.DeepWater, 1.5f },
                { TileType.Wetland, 0.9f },
                { TileType.Grass, 0.7f },
                { TileType.Forest, 0.6f },
            },
            TerrainComfortModifiers = new Dictionary<TileType, float>
            {
                { TileType.Wetland, -3f },
                { TileType.Bog, -2f },
                { TileType.Forest, -1f },
                { TileType.Arid, 6f },
                { TileType.Sand, 4f },
            },
            // Home ground is the waterline: the generic weights treat wetland and bog as nearly
            // hostile, which would walk a riverbank animal away from the only place it can eat.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.River, 0f },
                { TileType.ShallowWater, 0f },
                { TileType.Wetland, 0f },
                { TileType.Bog, 0.05f },
                { TileType.Forest, 0.15f },
                { TileType.Grass, 0.25f },
                { TileType.Taiga, 0.3f },
                { TileType.Shrubland, 0.4f },
                { TileType.DeepWater, 0.65f },   // a bank animal, not a diver — see HuntTerrain
                { TileType.Savanna, 0.7f },
                { TileType.Steppe, 0.7f },
                { TileType.Sand, 0.85f },
                { TileType.Arid, 0.9f },
            },
            TerrainConcealment = new Dictionary<TileType, float>
            {
                { TileType.Wetland, 0.35f },
                { TileType.Bog, 0.3f },
            },
            AllowedSpawnTiles = new List<TileType>
            {
                TileType.Wetland, TileType.Bog, TileType.Grass, TileType.Forest,
            },
            // Comes out of the water to den and raise cubs, exactly as the penguin hauls out.
            BreedingTiles = new List<TileType>
            {
                TileType.Wetland, TileType.Bog, TileType.Grass, TileType.Forest,
                TileType.Shrubland, TileType.Taiga,
            },
            PreferredBiomes = new List<BiomeType>
            {
                BiomeType.River, BiomeType.Wetland, BiomeType.Forest, BiomeType.Grassland,
            },

            CanGraze = false,

            SeparationRadius = 1.2f,
            SeparationStrength = 0.02f,

            // Trophic - small mustelid; comfortably prey for any mid-size land carnivore
            BodyMass = 1.2f,
            SoloHuntMaxRatio = 1.0f,

            // Visuals - sleek dark brown
            BaseColor = new Color(0.42f, 0.29f, 0.18f),
            BaseSize = 7f,
            Shape = ShapeType.Teardrop,
            StatVariation = 0.2f,
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
            // Habitat steering: Reef is not classified as water, so it fell through to the
            // generic weight (0.7) and repelled the very species that forage there.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Reef, 0f },
                { TileType.Wetland, 0.05f },
                { TileType.Bog, 0.15f },
                { TileType.Sand, 0.2f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.4f },
                { TileType.Arid, 0.8f },
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
            // Rooting omnivore — the primary Shroomer-bloom control (widest reach, biggest bite).
            IsFungivore = true,
            FungivoreFeedRadius = 3.5f,
            FungivoreFeedAmount = 12f,

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
            // A lizard is a sprinter, not a marathoner: an explosive dash for cover, then
            // it is spent. The default stamina curve let it hold 2.6x speed for ~200 ticks
            // (26 tiles), outrunning a fox indefinitely and towing its pursuer clean across
            // the map — often into water the predator had no business entering.
            FleeSpeedMultiplier = 2.8f,
            FleeStaminaDrain = 0.022f,      // ~45 ticks of full sprint
            FleeStaminaRecovery = 0.004f,
            FleeTiredSpeedFloor = 0.35f,    // blown, and easily run down

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
            // Habitat steering: Sand (0.3) and Arid (0.15) read as unpleasant in the generic
            // table, pushing desert specialists off their niche toward greener ground they are
            // not adapted to. Inverted here.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0f },
                { TileType.Arid, 0f },
                { TileType.Dirt, 0.05f },
                { TileType.Shrubland, 0.1f },
                { TileType.Savanna, 0.15f },
                { TileType.Steppe, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.5f },
                { TileType.Jungle, 0.6f },
                { TileType.Wetland, 0.7f },
                { TileType.Bog, 0.8f },
                { TileType.Tundra, 0.7f },
                { TileType.Ice, 0.85f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Sand, TileType.Arid, TileType.Dirt },
            PreferredBiomes = new List<BiomeType> { BiomeType.Desert },

            CanGraze = true,
            GrazeNutrition = 0.25f,  // Can eat sparse desert scrub
            IsFungivore = true,      // nibbles spores/sprouts

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
            // Habitat steering: Sand (0.3) and Arid (0.15) read as unpleasant in the generic
            // table, pushing desert specialists off their niche toward greener ground they are
            // not adapted to. Inverted here.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0f },
                { TileType.Arid, 0f },
                { TileType.Dirt, 0.05f },
                { TileType.Shrubland, 0.1f },
                { TileType.Savanna, 0.15f },
                { TileType.Steppe, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.5f },
                { TileType.Jungle, 0.6f },
                { TileType.Wetland, 0.7f },
                { TileType.Bog, 0.8f },
                { TileType.Tundra, 0.7f },
                { TileType.Ice, 0.85f },
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
            // Habitat steering: Sand (0.3) and Arid (0.15) read as unpleasant in the generic
            // table, pushing desert specialists off their niche toward greener ground they are
            // not adapted to. Inverted here.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0f },
                { TileType.Arid, 0f },
                { TileType.Dirt, 0.05f },
                { TileType.Shrubland, 0.1f },
                { TileType.Savanna, 0.15f },
                { TileType.Steppe, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.5f },
                { TileType.Jungle, 0.6f },
                { TileType.Wetland, 0.7f },
                { TileType.Bog, 0.8f },
                { TileType.Tundra, 0.7f },
                { TileType.Ice, 0.85f },
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
            // Habitat steering: Sand (0.3) and Arid (0.15) read as unpleasant in the generic
            // table, pushing desert specialists off their niche toward greener ground they are
            // not adapted to. Inverted here.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Sand, 0f },
                { TileType.Arid, 0f },
                { TileType.Dirt, 0.05f },
                { TileType.Shrubland, 0.1f },
                { TileType.Savanna, 0.15f },
                { TileType.Steppe, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.5f },
                { TileType.Jungle, 0.6f },
                { TileType.Wetland, 0.7f },
                { TileType.Bog, 0.8f },
                { TileType.Tundra, 0.7f },
                { TileType.Ice, 0.85f },
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
            // Habitat steering: the generic land-animal weights treat Ice (0.6) and Tundra (0.4)
            // as near-hostile, which steers arctic specialists out of the only biome they are
            // built for. Home ground is home.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Reef, 0f },
                { TileType.Ice, 0f },
                { TileType.Tundra, 0f },
                { TileType.Steppe, 0.05f },
                { TileType.Taiga, 0.1f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.35f },
                { TileType.Shrubland, 0.4f },
                { TileType.Savanna, 0.6f },
                { TileType.Jungle, 0.7f },
                { TileType.Arid, 0.7f },
                { TileType.Sand, 0.7f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Ice, TileType.Tundra, TileType.Steppe },
            // Hunts at sea, breeds ashore. A fed adult that is off this ground gets a roam target
            // on the nearest patch of it (WanderSystem "seek_breeding_ground"), which is what makes
            // the colony's cycle — out to the shoals hungry, back onto the ice full — emerge on its
            // own rather than a penguin breeding wherever it happens to be floating.
            BreedingTiles = new List<TileType> { TileType.Ice, TileType.Tundra, TileType.Steppe },
            PreferredBiomes = new List<BiomeType> { BiomeType.Arctic },

            // Penguins feed FROM the water, not by chasing individual fish across the sea. Active
            // fish-hunting could never produce enough throughput to keep a colony fed (~1 fish per
            // penguin per run vs. constant hunger decay), so they starved despite hunting. Like the
            // seabirds they are, they forage the water column (krill/small fish) — a reliable food
            // source as long as they're IN the water, which the comfort fix now lets them stay in.
            // They remain Omnivore (can still opportunistically catch a Fish entity) and prey.
            CanGraze = false,
            // Water is where a penguin HUNTS, not a pasture it can live off. At the old 0.55/tick
            // simply floating in the sea fed it faster than its hunger decayed, so it never needed
            // to catch anything — which is why fish populations ran away with only sharks cropping
            // them. Cut to a subsistence floor (scraps of plankton and krill) that carries a bird
            // through a lean patch but leaves fish as the actual meal.
            FeedTiles = new List<TileType> { TileType.ShallowWater, TileType.DeepWater, TileType.Reef, TileType.River },
            // Must stay BELOW effective hunger decay (0.04 x the global 0.3 scale = 0.012/tick)
            // or the floor alone keeps the bird pinned at full, back above SatedHunger, and it
            // never opportunistically hunts at all — the exact failure being fixed here.
            FeedNutrition = 0.004f,

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
            // Habitat steering: the generic land-animal weights treat Ice (0.6) and Tundra (0.4)
            // as near-hostile, which steers arctic specialists out of the only biome they are
            // built for. Home ground is home.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, 0f },
                { TileType.Tundra, 0f },
                { TileType.Steppe, 0.05f },
                { TileType.Taiga, 0.1f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.35f },
                { TileType.Shrubland, 0.4f },
                { TileType.Savanna, 0.6f },
                { TileType.Jungle, 0.7f },
                { TileType.Arid, 0.7f },
                { TileType.Sand, 0.7f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Ice, TileType.Tundra, TileType.Steppe },
            PreferredBiomes = new List<BiomeType> { BiomeType.Arctic },

            CanGraze = false,

            SeparationRadius = 4f,
            SeparationStrength = 0.03f,

            // Trophic - arctic apex
            BodyMass = 14.0f,
            SoloHuntMaxRatio = 1.5f,
            // Seals-and-penguins first; fish only when the ice is lean (see Shark).
            PreferredPrey = new List<string> { "Penguin", "Musk Ox" },
            FallbackPrey = new List<string> { "Fish" },
            FallbackPreyHunger = 0.45f,
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
            SpawnWeight = 1.1f,            // was 0.8 — bigger founding population (died out ~every run)

            // Movement - quick
            BaseWanderSpeed = 0.05f,
            DirectionChangeChance = 0.012f,

            // Combat — solo arctic hunter
            HuntingTactic = HuntingTactic.Solo,
            HuntRange = 12f,               // was 8 — spot penguin rafts from the shore
            AttackRange = 0.6f,
            AttackPower = 18f,
            AttackCooldown = 15,
            BaseHuntSpeed = 0.12f,

            // Survival
            MaxHunger = 170f,
            HungerDecayRate = 0.03f,       // was 0.04 — leaner survival between arctic kills
            MaxLifespan = 18000,
            MaturityAge = 1000,
            EnergyRegenRate = 0.15f,

            // Hunting behavior
            HuntThreshold = 0.75f,
            TrackingHungerThreshold = 0.5f,
            TrackingRange = 60f,
            // ShallowWater added: its staple prey (Penguin) rafts on the shallow sea to feed —
            // the food-seek only steered foxes to tundra/ice, so they starved inland of a
            // penguin boom (45→347 in run 20260713_035932 while Arctic Fox went extinct).
            // Land hunters wade shallow water safely, so shoreline penguins are fair game.
            HuntTerrain = new List<TileType> { TileType.Tundra, TileType.Steppe, TileType.Ice,
                                               TileType.ShallowWater },

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
            // Habitat steering: the generic land-animal weights treat Ice (0.6) and Tundra (0.4)
            // as near-hostile, which steers arctic specialists out of the only biome they are
            // built for. Home ground is home.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, 0f },
                { TileType.Tundra, 0f },
                { TileType.Steppe, 0.05f },
                { TileType.Taiga, 0.1f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.35f },
                { TileType.Shrubland, 0.4f },
                { TileType.Savanna, 0.6f },
                { TileType.Jungle, 0.7f },
                { TileType.Arid, 0.7f },
                { TileType.Sand, 0.7f },
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
            // Habitat steering: the generic land-animal weights treat Ice (0.6) and Tundra (0.4)
            // as near-hostile, which steers arctic specialists out of the only biome they are
            // built for. Home ground is home.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Ice, 0f },
                { TileType.Tundra, 0f },
                { TileType.Steppe, 0.05f },
                { TileType.Taiga, 0.1f },
                { TileType.Grass, 0.3f },
                { TileType.Forest, 0.35f },
                { TileType.Shrubland, 0.4f },
                { TileType.Savanna, 0.6f },
                { TileType.Jungle, 0.7f },
                { TileType.Arid, 0.7f },
                { TileType.Sand, 0.7f },
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
            // Habitat steering: canopy species. Jungle (0.1) and Forest (0.05) both rank
            // worse than open Grass (0.0) in the generic table, so the generic weights slowly
            // walk rainforest animals out onto the plains.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 0f },
                { TileType.Forest, 0.05f },
                { TileType.Wetland, 0.2f },
                { TileType.Savanna, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Shrubland, 0.45f },
                { TileType.Taiga, 0.5f },
                { TileType.Steppe, 0.6f },
                { TileType.Dirt, 0.65f },
                { TileType.Sand, 0.75f },
                { TileType.Arid, 0.85f },
                { TileType.Tundra, 0.85f },
                { TileType.Ice, 0.95f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Jungle, TileType.Forest },
            PreferredBiomes = new List<BiomeType> { BiomeType.Tropical },

            CanGraze = true,
            GrazeNutrition = 0.4f,
            IsFungivore = true,  // forages fungus in the canopy floor

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
            // Habitat steering: canopy species. Jungle (0.1) and Forest (0.05) both rank
            // worse than open Grass (0.0) in the generic table, so the generic weights slowly
            // walk rainforest animals out onto the plains.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 0f },
                { TileType.Forest, 0.05f },
                { TileType.Wetland, 0.2f },
                { TileType.Savanna, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Shrubland, 0.45f },
                { TileType.Taiga, 0.5f },
                { TileType.Steppe, 0.6f },
                { TileType.Dirt, 0.65f },
                { TileType.Sand, 0.75f },
                { TileType.Arid, 0.85f },
                { TileType.Tundra, 0.85f },
                { TileType.Ice, 0.95f },
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
            // Habitat steering: canopy species. Jungle (0.1) and Forest (0.05) both rank
            // worse than open Grass (0.0) in the generic table, so the generic weights slowly
            // walk rainforest animals out onto the plains.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Jungle, 0f },
                { TileType.Forest, 0.05f },
                { TileType.Wetland, 0.2f },
                { TileType.Savanna, 0.25f },
                { TileType.Grass, 0.3f },
                { TileType.Shrubland, 0.45f },
                { TileType.Taiga, 0.5f },
                { TileType.Steppe, 0.6f },
                { TileType.Dirt, 0.65f },
                { TileType.Sand, 0.75f },
                { TileType.Arid, 0.85f },
                { TileType.Tundra, 0.85f },
                { TileType.Ice, 0.95f },
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

            // Self-limiting (SporeSystem): a dense mat competes with itself for substrate and
            // cannot spread into ground it already saturates; a bloom whose tile is dried out
            // (Sectid/Faeling terraforming) starves. These are the biological ceilings that let
            // the factions push a bloom back instead of it growing immortally.
            //
            // Retune 2 (run 20260710_221127): the first pass (Limit 8 / Damage 0.15) worked TOO
            // well — crowding triggered at any 8-in-radius cluster, so blooms could never form;
            // Shroomers held a flat ~90 scattered thin (no disruption) and the collapse of the
            // spore food base starved the Sectids to extinction. Relaxed so blooms CAN build to a
            // real local density and cycle (grow → feed Sectids → pushed back), while drought (the
            // faction weapon, kept strong-ish) + fungivores + Sectid predation still cap the total.
            // If Shroomers boom again, tighten Limit/Saturation; if Sectids still starve, relax more.
            // Mycelium territory. A heart is not a point to be poked but a claim on ground, and
            // it fails in the two ways that claim can fail: the substrate dries out, or the bodies
            // holding it die. Drying is the cheap attack a terraformer wins from range; killing
            // the Shroomers is the expensive one that needs bodies in the bloom.
            MyceliumRadius = 14f,
            MyceliumMoistureFloor = 0.35f,
            MyceliumShroomerFloor = 3,
            MyceliumHeartDrainRate = 0.35f,
            MyceliumHeartRegenRate = 0.1f,
            MyceliumHeartHealth = 300f,
            MyceliumFoundScale = 2f,
            MyceliumGrowthRate = 0.02f,
            MyceliumSpreadRadius = 4f,
            StructureDefenseRadius = 20f,  // the bloom closes on whatever is cutting at its heart

            // === SIEGE — the bloom answers a raider that parks on its ground ===
            //
            // READ THIS BEFORE TUNING: for a Shroomer, StructureIdleSeekRadius is the ONLY gate.
            // StructureAggression looks like the control and is INERT here. It is a ratio — the
            // structure's distance against the CURRENT PREY TARGET's — and a Shroomer is a
            // Terraformer with no hunt range that never holds a prey target, so SiegeSystem.Acquire
            // takes the no-prey branch every time: seek = StructureIdleSeekRadius, and "inside that
            // radius at all" is the whole test. StructureSeekRadius is likewise unreachable (it is
            // the has-prey radius). Both are set to sane values so the numbers do not lie about
            // what this species does, but 8 tiles is what actually decides anything.
            //
            // 8 is below MyceliumRadius (14) on purpose. A Shroomer away from its mycelium dries
            // out and dies, so the objective path must never give it a reason to leave: anything it
            // can reach is well inside the ground it depends on.
            StructureAggression = 0.3f,
            StructureSeekRadius = 8f,
            StructureIdleSeekRadius = 8f,  // THE gate — see above

            // Reach EQUALS the seek radius, which is the load-bearing choice. Anything a Shroomer
            // can see, it can hit without taking a step: SiegeSystem zeroes velocity the moment a
            // target is within reach, so the acquire tick is also the stop tick and the bloom never
            // walks. Without this the walk-in would run at BaseHuntSpeed — unset on Shroomer, so
            // the 0.10 default, FIVE TIMES its wander speed — and the mechanic would drag blooms
            // off their own mycelium, which is the one thing it must not do.
            StructureAttackRange = 8f,

            // A NUISANCE, NOT A DEMOLITION TOOL. 2 damage per 150 ticks is 0.013 dps — a Shroomer
            // alone needs 15,000 ticks to break a 200-HP nest, which is to say it cannot. Ten in
            // range take 1,500 ticks and thirty take 500; against a 1,600-HP crystal the same
            // groups need 12,000 and 4,000. So the threat scales with how far into a bloom the
            // raider has planted itself, and nothing else. Per individual this is ~1/18 of a Sectid
            // (6/25) and ~1/6 of a keeper (7/90): a Shroomer matters only in numbers, which is the
            // whole identity of the species.
            StructureAttackPower = 2f,
            StructureAttackCooldown = 150,

            CrowdingRadius = 6f,
            CrowdingLimit = 20,      // blooms may reach ~20 same-radius neighbours before self-thinning
            CrowdingSaturation = 40, // spread only fully stops in a very dense core
            CrowdingDamage = 0.08f,  // gentler thinning (was 0.15 — pinned them too flat)
            DroughtDamage = 0.35f,   // faction drying still collapses a bloom, slightly gentler

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
                { TileType.Bog, -3f },       // Home ground — the generic table calls bog unpleasant
                { TileType.Wetland, -3f },   // Loves wet terrain
                { TileType.Forest, -1f },
                { TileType.Grass, 0f },
                { TileType.Sand, 3f },       // Uncomfortable on dry
                { TileType.Arid, 6f },       // Very uncomfortable on arid
            },
            // Habitat steering. The generic land-animal weights rank Grass (0.0) as nicer than
            // Wetland (0.15) and Bog (0.25) — steering a Shroomer off the only substrate it can
            // survive on and onto grass, which is below its SporeMoistureThreshold and therefore
            // permanent drought. Inverted here: swamp is home, dry ground is what to avoid.
            // Hunger still overrides this, so a bloom that strips its own bog does move on — just
            // deliberately, at forage pace, instead of drifting out and dying well fed.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Bog, 0f },
                { TileType.Wetland, 0f },
                { TileType.Jungle, 0.05f },
                { TileType.Forest, 0.1f },   // wet enough to live on, but not home
                { TileType.Taiga, 0.15f },
                { TileType.Grass, 0.5f },    // below drought threshold — actively avoided
                { TileType.Shrubland, 0.6f },
                { TileType.Savanna, 0.65f },
                { TileType.Steppe, 0.7f },
                { TileType.Dirt, 0.75f },
                { TileType.Tundra, 0.8f },
                { TileType.Sand, 0.85f },
                { TileType.Arid, 0.9f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Wetland, TileType.Forest, TileType.Bog },

            // Feeding — Shroomers RELY ON and IMPACT land fertility, more than herbivores. Their
            // growth fuel is tile nutrition (FertilityConsumeRate/FertilityFeedNutrition below):
            // a bloom strips fertile ground (3× a grazer's depletion) and terraforms it to barren
            // swamp, so it must advance into fresh fertile land — an ecological, self-limiting,
            // pasture-destroying front. The wet-tile FeedNutrition is now only a subsistence FLOOR
            // (cut 0.5→0.06, ~= HungerDecayRate): a Shroomer survives on its own swamp but stays
            // too hungry to keep spreading there, so fertility — not free wet-tile food — drives
            // the bloom. (Previously 0.5 = "can't starve on wet tiles", which removed all fertility
            // reliance and let them grow immortally on self-made swamp.)
            CanGraze = false,
            FeedTiles = new List<TileType> { TileType.Wetland, TileType.Forest },
            FeedNutrition = 0.06f,          // subsistence floor only (was 0.5)
            FertilityConsumeRate = 0.06f,   // strips grazeable fertility 3× faster than a herbivore
            FertilityFeedNutrition = 0.8f,  // fertile ground fuels fast bloom growth

            // Terraform - increases moisture
            TerraformDir = TerraformDirection.Wetter,
            TerraformRadius = 2.0f,
            // Raised 0.03 -> 0.10 and the cooldown halved: at the old rate a bloom managed 3 tile
            // conversions across an entire test run, so it could never reach the fertile ground it
            // needs and was permanently confined to the patch it spawned on. Scaled by body size
            // in TerraformSystem, so sprouts still barely mark the ground.
            TerraformStrength = 0.10f,
            TerraformCooldown = 4,

            // AoE attack — S-curve growth scaling (smoothstep):
            //   Radius/damage = max_value * (minFactor + (1-minFactor) * smoothstep(t))
            //   where t = (scale - 1.0) / (4.0 - 1.0), smoothstep = t²(3-2t)
            // Scale 1.0 (birth):  factor=0.08  → radius 1.1, damage 2.4   (nearly harmless)
            // Scale 1.5:          factor=0.09  → radius 1.3, damage 2.7   (still weak)
            // Scale 2.5 (mid):    factor=0.50  → radius 7,   damage 15    (growth spurt)
            // Scale 3.5:          factor=0.91  → radius 12.7, damage 27.3 (powerful)
            // Scale 4.0 (elder):  factor=1.00  → radius 14,  damage 30    (max, tapers)
            HasAoEAttack = true,
            AoEAttackRadius = 14f,       // Max radius at full growth (was 25 — out-ranged every
                                         // counter on the map; 14 keeps elders dangerous up close
                                         // while letting Faeling bolts (range 12) trade into them)
            AoEAttackDamage = 30f,       // Max damage at full growth
            AoEAttackCooldown = 60,      // Base cooldown (used for combat pulses)
            AoEPassiveCooldown = 350,    // Slow passive pulses (every 17.5s at 20 TPS)
            AoECombatCooldown = 60,      // Reactive pulses when attacked (3s)
            AoEMinScaleFactor = 0.08f,   // 8% of max at birth → S-curve → 100% at max
            // Elder area denial: a near-fully-grown Shroomer that is attacked by ANYTHING floods
            // its surroundings for 10s with a bloom that kills nearly everything caught in it,
            // then spends 30s recharging. Nothing brings an elder down in a straight fight; the
            // recharge gap is the only way in, so it takes a swarm with the bodies to spare and
            // the patience to keep coming back (or, later, a strong enough Faeling).
            AoEEnrageScale = 3.2f,               // of GrowthMaxScale 4 — genuinely old
            AoEEnrageDuration = 200,             // 10s at 20 TPS
            AoEEnrageRecharge = 600,             // 30s vulnerable window afterwards
            AoEEnrageDamageMultiplier = 4f,      // 30 → 120 per pulse at full growth
            AoEEnrageRadiusMultiplier = 1.6f,    // 14 → 22 tiles

            // Thorn defense — S-curve growth-scaled counter-damage on melee attackers
            // Scale 1.0: 20 * 0.08 = 1.6 dmg/hit  (barely stings)
            // Scale 2.5: 20 * 0.50 = 10 dmg/hit   (serious deterrent)
            // Scale 4.0: 20 * 1.00 = 20 dmg/hit   (lethal to small attackers)
            ThornDamageBase = 35f,       // Max thorn damage at reference mass (S-curve + attacker mass scaled)

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
            // Colony shape. Nests used to sit ~15 tiles from their parent and expand after only 5
            // in a 40-tile area, so a colony read as an evenly-spaced lattice creeping outward
            // rather than a settlement. Now they pack tightly (≈4–9 tiles apart) and a colony
            // fills out to a dozen nests before sending an expedition, giving dense colony blobs
            // with open ground — and a frontline — between them.
            NestColonyRadius = 26f,
            NestsForExpedition = 12,
            NestSearchRadius = 7f,
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
            // Habitat steering: the dry ground a colony makes for itself is home, not something
            // to escape. Without this the generic weights rank Arid (0.15) and Sand (0.3) as worse
            // than the Grass they haven't dried yet, so a colony keeps fleeing its own territory —
            // 3,021 escape decisions and 2,573 hunts abandoned to discomfort in one test run.
            TerrainAversionModifiers = new Dictionary<TileType, float>
            {
                { TileType.Arid, 0f },
                { TileType.Sand, 0.05f },
                { TileType.Steppe, 0.05f },
                { TileType.Dirt, 0.05f },
                { TileType.Shrubland, 0.1f },
                { TileType.Savanna, 0.1f },
                { TileType.Grass, 0.2f },      // hunting ground, not home
                { TileType.Forest, 0.5f },
                { TileType.Jungle, 0.6f },
                { TileType.Wetland, 0.8f },    // enemy substrate
                { TileType.Bog, 0.9f },
            },
            AllowedSpawnTiles = new List<TileType> { TileType.Arid, TileType.Sand },

            // Feeding — Sectids do NOT graze; they must hunt to survive
            // (ravenous swarming opportunists that eat anything that moves)
            CanGraze = false,

            // Terraform - decreases moisture
            // Applied by the NEST on each hatch (NestSystem.TerraformAroundNest), not by roaming
            // Sectids. Radius widened 1.5→3: at 1.5 a colony's lifetime of hatches dried a ~7-tile
            // speck, leaving green corridors between nests instead of spreading desert (observed).
            TerraformDir = TerraformDirection.Drier,
            TerraformRadius = 3f,
            TerraformStrength = 0.04f,
            TerraformCooldown = 6,

            // Trophic - tiny insects, hunt in packs — eat anything that moves
            // No PreferredPrey: Sectids are pure opportunists, targeting nearest viable prey
            HuntingTactic = HuntingTactic.Swarm,  // Colony-wide rush, no retreat, count all same-species
            SwarmHunter = true,            // Colony-wide bravery, can target predators
            // Anti-bloom faction: strongly prefer Shroomer spores/immature Shroomers as targets,
            // eating a bloom out before it fortifies (grown Shroomers still fail the mass gate).
            SporeHuntBias = 0.85f,
            BodyMass = 0.5f,
            SoloHuntMaxRatio = 2.5f,       // Solo: prey up to mass 1.25 (rabbits)
            PackHuntMassExponent = 0.8f,    // Stronger pack scaling (colony bravery)
            // Swarm of 3: 0.5 × 3^0.8 × 2.5 = 3.0  → foxes
            // Swarm of 5: 0.5 × 5^0.8 × 2.5 = 4.5  → wolves (3.5), deer (4.0)
            // Swarm of 8: 0.5 × 8^0.8 × 2.5 = 6.6  → wolves comfortably
            // Swarm of 12: 0.5 × 12^0.8 × 2.5 = 9.2 → crocodiles (8.0)!

            // Siege — a colony breaks enemy structures, but ONLY when provoked. 0.15 means a
            // structure must be practically underfoot to distract a swarm from a meal; the
            // retaliation bias is where the character is — x8 while one of its own nests is being
            // broken turns that into 1.2, and a colony under siege stops foraging and goes to
            // break something back. That asymmetry is the whole point of expressing aggression as
            // a distance ratio: the same species reads as "busy eating" or "at war" depending
            // only on whether it has been attacked.
            //
            // It was 0.5, which was casual enough that swarms went crystal-breaking unprovoked:
            // 7 of 12 crystals destroyed across a 20,000-tick run, permanently shrinking a faction
            // whose population IS its crystal count. Combined with the keeper's own dominance
            // gate, 0.15 means the opening minutes of a run are quiet because nobody has yet been
            // given a reason to fight — which is the pacing the whole-game invariant asks for.
            StructureAggression = 0.15f,
            StructureRetaliationBias = 8f,
            // StructureIdleSeekRadius stays at the 0 default: a swarm with nothing to hunt goes
            // looking for food, not for a building. It sieges only when a structure is genuinely
            // nearer than the meal it had already picked — which is what the ratio above says.
            StructureIdleSeekRadius = 0f,
            StructureSeekRadius = 45f,
            StructureAttackPower = 6f,     // one Sectid is a nuisance; a swarm is a siege engine
            StructureAttackCooldown = 25,
            StructureAttackRange = 1.3f,   // long reach on a tiny body — it climbs the thing
            StructureDefenseRadius = 30f,  // nests call the colony home when struck

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

            // Terraform - restores balance (gains power per tile restored).
            // Strength is a PROBABILITY per cooldown roll, not an amount: at the old 0.03 a
            // keeper nudged one random tile every ~267 ticks — structurally invisible (observed:
            // restoration never left a mark). 0.25 ≈ one tile every ~32 ticks: a keeper parked on
            // a siege line visibly dries/restores its patch within a few minutes.
            TerraformDir = TerraformDirection.Restore,
            TerraformRadius = 4.0f,
            TerraformStrength = 0.25f,
            TerraformCooldown = 8,

            // Keeper of order — senses which rival faction is locally over-dominant (a Shroomer
            // bloom or a Sectid swarm), prefers it as a ranged target, and lays siege to its
            // hotspot from a standoff ring, drying/restoring the substrate the winner needs.
            KeeperSenseRadius = 40f,
            KeeperMinPresence = 5,

            // Siege — the keeper is a RAIDER, and this is what makes a handful of them a faction.
            // A dozen Faelings that kill individual Sectids are a rounding error against a colony
            // that hatches replacements; a dozen that break nests and mycelium hearts decide wars.
            // 4.0 says it will walk past a target four times nearer to reach a structure, which is
            // the behaviour "keeper of order" was always meant to describe — it suppresses whoever
            // is winning by taking their INFRASTRUCTURE, not by out-killing them.
            StructureAggression = 4f,
            StructureSeekRadius = 60f,     // paired with its 40-tile keeper sense
            // A keeper never holds a prey target — it does not eat — so without this it could
            // never acquire a siege at all once idle sieging became opt-in. Raiding IS its purpose,
            // so it looks just as far idle as it does otherwise.
            StructureIdleSeekRadius = 60f,
            StructureTargetsDominantOnly = true,   // check the winner, never the nearest
            // A RAID IS AN EVENT, NOT AN INSTANT. These were 22 damage on a 40-tick cooldown when
            // the world held 132 keepers, and that combination destroyed 44 of 46 Sectid nests in
            // 6,000 ticks — the first at t=951. Two things changed together: the faction is now 12
            // (FaelingCrystalCount) rather than 132, and one keeper alone can no longer break
            // anything on its own timescale. 7 damage on a 90-tick cooldown is 0.078 dps, so a
            // 200-HP nest takes ~2,570 ticks — over two minutes of sustained work by a lone
            // keeper, and the colony's own retaliation has that entire window to answer.
            // THESE NUMBERS ASSUMED KEEPERS COULD ASSEMBLE, AND THEY NO LONGER CAN. Concentration
            // was the intended counter to that slowness — three keepers relocating to one region
            // took a nest in ~860 ticks, so a raid was something the keepers gathered for. Travel
            // is deleted (keepers stay where they spawn and walk to their targets), so the only
            // rate that remains is the lone keeper's ~2,570 ticks. Measured: with travel still in,
            // keepers landed ZERO blows on any structure across two 20,000-tick runs, so the raid
            // path was already inert and removing travel did not slow it. Left unchanged here
            // deliberately: re-deriving raid damage against the invariant in
            // docs/archive/whole-game-invariants-2026-08.md is its own change with its own measurement.
            StructureAttackPower = 7f,
            StructureAttackCooldown = 90,
            StructureAttackRange = 8f,     // ranged: it dismantles from a standoff ring
            StructureDefenseRadius = 0f,   // a crystal is a lone outpost; nobody comes to help it

            // A crystal is the most permanent thing in the game. A nest is replaceable — a colony
            // founds new ones — but nothing creates a crystal, so every one lost shrinks the
            // Faeling faction forever, and the faction is twelve. At 400 HP a ten-Sectid swarm
            // took one in ~170 ticks and the run ended with 5 of 12 standing. 1,600 puts that at
            // ~670 ticks: still losable, and still lost to a determined siege, but a siege the
            // keepers have time to answer and a player would see happening.
            CrystalHealth = 1600f,

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
