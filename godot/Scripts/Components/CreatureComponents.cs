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

    public Species(SpeciesType type, int generation = 0)
    {
        Type = type;
        Generation = generation;
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

    public Energy(float current, float max = 100f)
    {
        Current = current;
        Max = max;
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
