using System.Collections.Generic;

namespace Mitosis.World;

/// <summary>
/// Per-species population entry for statistical simulation of distant chunks.
/// Tracks aggregate stats instead of individual entities.
/// </summary>
public struct SpeciesPopulation
{
    public int SpeciesId;
    public int Count;
    public float AverageHungerRatio;   // 0-1 (fraction of MaxHunger)
    public float AverageAgeRatio;      // 0-1 (fraction of MaxLifespan)
    public float FractionalBirths;     // Accumulated fractional births (< 1.0)
    public float FractionalDeaths;     // Accumulated fractional deaths (< 1.0)
}

/// <summary>
/// Stores population-level data for a chunk running statistical simulation.
/// Used when the chunk is far from the player (LOD 2+) to avoid per-entity overhead.
/// </summary>
public sealed class ChunkPopulationData
{
    /// <summary>Per-species population entries keyed by species ID.</summary>
    public readonly Dictionary<int, SpeciesPopulation> Populations = new(8);

    /// <summary>Total creature count across all species.</summary>
    public int TotalCount;

    /// <summary>Average tile nutrition across grazeable tiles in this chunk (0-1).</summary>
    public float AverageNutrition;

    /// <summary>Number of grazeable tiles in this chunk (cached at aggregation time).</summary>
    public int GrazeableTileCount;

    /// <summary>Whether this chunk is currently running statistical simulation.</summary>
    public bool IsActive;

    public void Clear()
    {
        Populations.Clear();
        TotalCount = 0;
        AverageNutrition = 0;
        IsActive = false;
    }

    /// <summary>
    /// Add or update a species population entry.
    /// </summary>
    public void AddPopulation(int speciesId, int count, float avgHungerRatio, float avgAgeRatio)
    {
        Populations[speciesId] = new SpeciesPopulation
        {
            SpeciesId = speciesId,
            Count = count,
            AverageHungerRatio = avgHungerRatio,
            AverageAgeRatio = avgAgeRatio,
        };
        RecalculateTotal();
    }

    private void RecalculateTotal()
    {
        TotalCount = 0;
        foreach (var pop in Populations.Values)
            TotalCount += pop.Count;
    }
}
