using System.Runtime.InteropServices;

namespace Mitosis.Components;

/// <summary>
/// Types of species in the ecosystem.
/// </summary>
public enum SpeciesType : byte
{
    Herbivore = 0,
    Carnivore = 1,
    Omnivore = 5,  // Eats both plants and creatures
    Shroomer = 2,  // Fungi-like, spreads via spores
    Sectid = 3,    // Insect-like, multiplies quickly
    Faeling = 4    // Plant-like, grows crystals
}

/// <summary>
/// Defines what type of creature this entity is.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Species
{
    public SpeciesType Type;
    public int Generation;

    /// <summary>
    /// Index into SpeciesRegistry to get the full species definition.
    /// This is the hash of the species name for fast lookup.
    /// </summary>
    public int SpeciesId;

    public Species(SpeciesType type, int generation = 0, int speciesId = 0)
    {
        Type = type;
        Generation = generation;
        SpeciesId = speciesId;
    }
}

/// <summary>
/// Hunger/food need for an entity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Hunger
{
    public float Current;
    public float Max;
    public float DecayRate;
    public float StarvationDamage;

    public Hunger(float current, float max = 100f, float decayRate = 0.1f, float starvationDamage = 1f)
    {
        Current = current;
        Max = max;
        DecayRate = decayRate;
        StarvationDamage = starvationDamage;
    }

    public readonly float Percent => Current / Max;
    public readonly bool IsStarving => Current <= 0;
}

/// <summary>
/// Energy/health for an entity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Energy
{
    public float Current;
    public float Max;
    public int RegenCooldown; // Ticks until regen resumes after taking damage

    public Energy(float current, float max = 100f)
    {
        Current = current;
        Max = max;
        RegenCooldown = 0;
    }

    public readonly float Percent => Current / Max;
    public readonly bool IsDead => Current <= 0;
}

/// <summary>
/// Age tracking for natural death and maturity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Age
{
    public int Current;
    public int MaxLifespan;
    public int MaturityAge;

    public Age(int current = 0, int maxLifespan = 10000, int maturityAge = 1000)
    {
        Current = current;
        MaxLifespan = maxLifespan;
        MaturityAge = maturityAge;
    }

    public readonly bool IsMature => Current >= MaturityAge;
    public readonly bool IsElderly => Current >= MaxLifespan * 0.8f;
}

/// <summary>
/// Reproduction capability for an entity.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Reproduction
{
    // Thresholds for reproduction
    public float HungerThreshold;
    public float EnergyThreshold;

    // Costs of reproduction
    public float HungerCost;
    public float EnergyCost;

    // Timing
    public int Cooldown;
    public int CurrentCooldown;

    // Offspring settings
    public int OffspringCount;
    public float SpawnRadius;

    public Reproduction(
        float hungerThreshold = 70f,
        float energyThreshold = 80f,
        float hungerCost = 40f,
        float energyCost = 30f,
        int cooldown = 500,
        int offspringCount = 1,
        float spawnRadius = 3f)
    {
        HungerThreshold = hungerThreshold;
        EnergyThreshold = energyThreshold;
        HungerCost = hungerCost;
        EnergyCost = energyCost;
        Cooldown = cooldown;
        CurrentCooldown = 0;
        OffspringCount = offspringCount;
        SpawnRadius = spawnRadius;
    }
}

/// <summary>
/// LOD level for simulation tick rate based on distance from player.
/// At 20 TPS base rate: Full=20, High=10, Medium=5, Low=2, Minimal=1 TPS.
/// </summary>
public enum LODLevel : byte
{
    Full = 0,       // Every tick (20 TPS) — near player
    High = 1,       // Every 2 ticks (10 TPS)
    Medium = 2,     // Every 4 ticks (5 TPS)
    Low = 3,        // Every 10 ticks (2 TPS)
    Minimal = 4     // Every 20 ticks (1 TPS)
}

/// <summary>
/// Simulation Level of Detail — determines update frequency based on distance.
/// LODSystem runs first each tick, counting down TicksUntilUpdate.
/// All other systems skip the entity when TicksUntilUpdate != 0.
/// TickInterval is cached to avoid repeated switch lookups in downstream systems.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SimulationLOD
{
    public LODLevel Level;
    public int TicksUntilUpdate;
    public int TickInterval;      // Cached interval for this LOD level (avoids switch per system)
    public float DistanceToPlayer;

    /// <summary>Ticks elapsed since this entity was last due (maintained by LODSystem).</summary>
    public int TicksSinceUpdate;

    /// <summary>
    /// Ticks that ACTUALLY elapsed since the previous due tick — what a rate-compensating system
    /// must multiply by. Distinct from <see cref="TickInterval"/>, which is only the nominal
    /// interval of the current tier: when an entity changes tier the countdown is reset, so the
    /// real gap is whatever the old tier and the reset left behind. Multiplying by the nominal
    /// interval then over- or under-counts every time the player moves past a boundary.
    /// </summary>
    public int EffectiveInterval;

    public SimulationLOD(LODLevel level = LODLevel.Full)
    {
        Level = level;
        TicksUntilUpdate = 0;
        TickInterval = GetTickInterval(level);
        DistanceToPlayer = 0f;
        TicksSinceUpdate = 0;
        EffectiveInterval = 1;
    }

    /// <summary>
    /// Get the tick interval (in simulation ticks) for this LOD level.
    /// </summary>
    public static int GetTickInterval(LODLevel level)
    {
        return level switch
        {
            LODLevel.Full => 1,      // 20 TPS
            LODLevel.High => 2,      // 10 TPS
            LODLevel.Medium => 4,    // 5 TPS
            LODLevel.Low => 10,      // 2 TPS
            LODLevel.Minimal => 20,  // 1 TPS
            _ => 1
        };
    }

    /// <summary>
    /// Determine LOD level based on distance (in tiles) and camera visible radius.
    /// Full tier covers everything within visible range + 15% buffer for player movement.
    /// Other tiers are spaced as multiples of the visible radius.
    /// Hysteresis: to downgrade (lower tick rate), distance must exceed threshold by 10%.
    /// To upgrade (higher tick rate), the normal threshold applies. This prevents
    /// entities near boundaries from oscillating between tiers every few ticks.
    /// </summary>
    public static LODLevel GetLevelForDistance(float distance, float visibleRadius, LODLevel currentLevel)
    {
        float fullRange = visibleRadius * 1.15f;             // visible + 15% buffer
        const float hysteresis = 1.1f;                        // 10% buffer to resist downgrade

        // Thresholds for upgrading (moving to higher tick rate — use exact boundary)
        // Thresholds for downgrading (moving to lower tick rate — require 10% past boundary)
        float t0 = fullRange;           // Full ↔ High boundary
        float t1 = fullRange * 2f;      // High ↔ Medium boundary
        float t2 = fullRange * 3f;      // Medium ↔ Low boundary
        float t3 = fullRange * 5f;      // Low ↔ Minimal boundary

        // Use widened threshold when entity would downgrade (move farther from player)
        float b0 = currentLevel <= LODLevel.Full    ? t0 * hysteresis : t0;
        float b1 = currentLevel <= LODLevel.High    ? t1 * hysteresis : t1;
        float b2 = currentLevel <= LODLevel.Medium  ? t2 * hysteresis : t2;
        float b3 = currentLevel <= LODLevel.Low     ? t3 * hysteresis : t3;

        if (distance < b0) return LODLevel.Full;
        if (distance < b1) return LODLevel.High;
        if (distance < b2) return LODLevel.Medium;
        if (distance < b3) return LODLevel.Low;
        return LODLevel.Minimal;
    }
}

/// <summary>
/// Tracks accumulated terrain discomfort - influences behavior decisions.
/// Accumulates on uncomfortable terrain, decays on comfortable terrain.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainDiscomfort
{
    public float Current;           // Current accumulated discomfort (0 = comfortable)
    public float Threshold;         // Discomfort level that triggers behavior change
    public float DecayRate;         // How fast discomfort decays on comfortable terrain
    public float GrazingPressure;   // Extra discomfort when hungry and not on grazeable terrain (herbivores)
    public bool IsEscaping;         // Hysteresis flag: true while actively escaping discomfort
    public int WrongElementTicks;   // Ticks spent in wrong element (water for land, land for aquatic)

    public TerrainDiscomfort(
        float threshold = 50f,
        float decayRate = 2f,
        float grazingPressure = 0f)
    {
        Current = 0f;
        Threshold = threshold;
        DecayRate = decayRate;
        GrazingPressure = grazingPressure;
        IsEscaping = false;
        WrongElementTicks = 0;
    }

    public readonly bool IsUncomfortable => Current > 0;
    public readonly bool ExceedsThreshold => Current >= Threshold;
    public readonly float Ratio => Current / Threshold;  // 0-1+ how close to threshold
}

/// <summary>
/// Fear response types - how a creature reacts when fear threshold is exceeded.
/// </summary>
public enum FearResponse : byte
{
    Flee = 0,       // Run away (most common)
    Freeze = 1,     // Stop moving, hope predator doesn't notice
    Defensive = 2,  // Form defensive group (requires herd)
    Panic = 3       // Erratic movement, ignores terrain danger
}

/// <summary>
/// Tracks accumulated fear from threats - influences behavior decisions.
/// Fear builds up when threats are nearby, decays when safe.
/// Different from instant flee reaction - allows for vigilance, panic, etc.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Fear
{
    public float Current;           // Current fear level (0 = calm)
    public float Max;               // Maximum fear before panic
    public float Threshold;         // Fear level that triggers response behavior
    public float AccumulationRate;  // How fast fear builds when threatened
    public float DecayRate;         // How fast fear decays when safe
    public float VigilanceDecay;    // Slower decay rate when recently threatened
    public int VigilanceTicks;      // Ticks remaining in vigilant state
    public FearResponse Response;   // How this creature responds to fear

    public Fear(
        float threshold = 50f,
        float max = 100f,
        float accumulationRate = 5f,
        float decayRate = 1f,
        float vigilanceDecay = 0.3f,
        FearResponse response = FearResponse.Flee)
    {
        Current = 0f;
        Max = max;
        Threshold = threshold;
        AccumulationRate = accumulationRate;
        DecayRate = decayRate;
        VigilanceDecay = vigilanceDecay;
        VigilanceTicks = 0;
        Response = response;
    }

    public readonly bool IsAfraid => Current > 0;
    public readonly bool ExceedsThreshold => Current >= Threshold;
    public readonly bool IsPanicking => Current >= Max * 0.9f;
    public readonly bool IsVigilant => VigilanceTicks > 0;
    public readonly float Ratio => Current / Threshold;  // 0-1+ how close to threshold
}

/// <summary>
/// Damage over time effect from venomous attacks.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VenomEffect
{
    public float DamagePerTick;
    public int RemainingTicks;

    public VenomEffect(float damagePerTick, int durationTicks)
    {
        DamagePerTick = damagePerTick;
        RemainingTicks = durationTicks;
    }

    public readonly bool IsActive => RemainingTicks > 0;
}

/// <summary>
/// A corpse left behind when a creature dies. Holds an edible nutrient pool that predators,
/// omnivores and Sectids consume over time (carrion scavenging). Stays fresh for a grace
/// period, then slowly rots away on its own.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Carrion
{
    public float Nutrition;       // Remaining edible nutrients
    public float MaxNutrition;    // Initial pool (for decay scaling / visuals)
    public int GraceTicks;        // Ticks before self-rot begins (a fresh carcass)
    public float DecayPerTick;    // Nutrition lost per tick once rotting
    public int SourceSpeciesId;   // Species the corpse came from (visuals / future use)

    public Carrion(float nutrition, int graceTicks, float decayPerTick, int sourceSpeciesId)
    {
        Nutrition = nutrition;
        MaxNutrition = nutrition;
        GraceTicks = graceTicks;
        DecayPerTick = decayPerTick;
        SourceSpeciesId = sourceSpeciesId;
    }

    // Treat a non-finite pool as depleted: a NaN nutrition value makes `<= 0f` false,
    // which would otherwise leave an immortal corpse that never rots and becomes a
    // permanent scavenger magnet (predators pile on it and never re-hunt).
    public readonly bool IsDepleted => !(Nutrition > 0f);
    public readonly float Freshness => MaxNutrition > 0f ? Nutrition / MaxNutrition : 0f;
}
