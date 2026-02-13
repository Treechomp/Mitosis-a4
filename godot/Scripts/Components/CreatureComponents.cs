using System.Runtime.InteropServices;

namespace Mitosis.Components;

/// <summary>
/// Types of species in the ecosystem.
/// </summary>
public enum SpeciesType : byte
{
    Herbivore = 0,
    Carnivore = 1,
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
/// LOD level for simulation fidelity based on distance from player.
/// </summary>
public enum LODLevel : byte
{
    Full = 0,       // Every tick, full AI
    Reduced = 1,    // Every 5 ticks, simplified AI
    Statistical = 2, // Every 30 ticks, statistical updates
    Aggregate = 3   // Every 60 ticks, population-level only
}

/// <summary>
/// Simulation Level of Detail - determines update frequency based on distance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SimulationLOD
{
    public LODLevel Level;
    public int TicksUntilUpdate;
    public float DistanceToPlayer;

    public SimulationLOD(LODLevel level = LODLevel.Full)
    {
        Level = level;
        TicksUntilUpdate = 0;
        DistanceToPlayer = 0f;
    }

    /// <summary>
    /// Get the tick interval for this LOD level.
    /// </summary>
    public static int GetTickInterval(LODLevel level)
    {
        return level switch
        {
            LODLevel.Full => 1,
            LODLevel.Reduced => 5,
            LODLevel.Statistical => 30,
            LODLevel.Aggregate => 60,
            _ => 1
        };
    }

    /// <summary>
    /// Determine LOD level based on distance (in tiles).
    /// </summary>
    public static LODLevel GetLevelForDistance(float distance)
    {
        if (distance < 50f) return LODLevel.Full;        // ~1.5 chunks
        if (distance < 100f) return LODLevel.Reduced;    // ~3 chunks
        if (distance < 200f) return LODLevel.Statistical; // ~6 chunks
        return LODLevel.Aggregate;
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
