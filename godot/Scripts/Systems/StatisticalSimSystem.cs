using System;
using System.Collections.Generic;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Statistical simulation for distant chunks (LOD 2+).
/// Instead of processing individual entities, applies population-level
/// birth/death math. Handles three transitions:
///   1. Aggregation: entities -> population data (when chunk becomes distant)
///   2. Statistical tick: population-level birth/death rates
///   3. Materialization: population data -> entities (when chunk becomes nearby)
/// </summary>
public sealed class StatisticalSimSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly EntityFactory _entityFactory;
    private readonly SpatialHash _spatialHash;
    private readonly int _maxPopulation;
    private readonly int _chunkSize;
    private readonly Random _rng = new();

    // Chunk LOD tracking
    private readonly Dictionary<(int, int), ChunkPopulationData> _chunkPopulations = new(128);
    private readonly HashSet<(int, int)> _statisticalChunks = new(128);
    private readonly HashSet<(int, int)> _previousStatistical = new(128);

    // Reusable buffers
    private readonly List<int> _entityBuffer = new(256);
    private readonly List<int> _entitiesToDestroy = new(256);
    private readonly List<(int speciesId, int count)> _toMaterialize = new(32);
    private readonly Dictionary<int, (int count, float totalHungerRatio, float totalAgeRatio)> _speciesAccum = new(8);
    private readonly List<int> _keyBuffer = new(16);

    // Player position (set by GameManager)
    private float _playerX;
    private float _playerY;

    // Tick counter for periodic updates
    private int _tickCounter;

    /// <summary>Distance threshold in tiles. Chunks beyond this use statistical sim.</summary>
    private const float StatisticalDistanceThreshold = 100f; // matches LODLevel.Statistical

    /// <summary>How often (in ticks) to run the statistical population update.</summary>
    private const int StatisticalTickInterval = 30;

    /// <summary>Total statistical population across all chunks (for debug overlay).</summary>
    public int TotalStatisticalPopulation { get; private set; }

    /// <summary>Number of chunks currently running statistical simulation.</summary>
    public int StatisticalChunkCount => _statisticalChunks.Count;

    public StatisticalSimSystem(WorldManager worldManager, EntityFactory entityFactory,
                                 SpatialHash spatialHash, int maxPopulation, int chunkSize)
    {
        _worldManager = worldManager;
        _entityFactory = entityFactory;
        _spatialHash = spatialHash;
        _maxPopulation = maxPopulation;
        _chunkSize = chunkSize;
    }

    public void SetPlayerPosition(float x, float y)
    {
        _playerX = x;
        _playerY = y;
    }

    public void Process(EntityManager em)
    {
        _tickCounter++;

        // Determine which chunks should be statistical
        _previousStatistical.Clear();
        foreach (var key in _statisticalChunks)
            _previousStatistical.Add(key);
        _statisticalChunks.Clear();

        int worldChunks = _worldManager.WorldSizeChunks;
        float playerChunkX = _playerX / _chunkSize;
        float playerChunkY = _playerY / _chunkSize;

        for (int cx = 0; cx < worldChunks; cx++)
        {
            for (int cy = 0; cy < worldChunks; cy++)
            {
                float chunkCenterX = (cx + 0.5f) * _chunkSize;
                float chunkCenterY = (cy + 0.5f) * _chunkSize;
                float dist = MathUtils.Distance(_playerX, _playerY, chunkCenterX, chunkCenterY);

                if (dist >= StatisticalDistanceThreshold)
                    _statisticalChunks.Add((cx, cy));
            }
        }

        // Handle transitions
        foreach (var key in _statisticalChunks)
        {
            if (!_previousStatistical.Contains(key))
            {
                // Chunk just became distant — aggregate entities
                AggregateChunk(key.Item1, key.Item2, em);
            }
        }

        foreach (var key in _previousStatistical)
        {
            if (!_statisticalChunks.Contains(key))
            {
                // Chunk just became nearby — materialize entities
                MaterializeChunk(key.Item1, key.Item2, em);
            }
        }

        // Run statistical simulation periodically
        if (_tickCounter % StatisticalTickInterval == 0)
        {
            TotalStatisticalPopulation = 0;
            foreach (var key in _statisticalChunks)
            {
                if (_chunkPopulations.TryGetValue(key, out var popData) && popData.IsActive)
                {
                    UpdatePopulation(popData, key.Item1, key.Item2);
                    TotalStatisticalPopulation += popData.TotalCount;
                }
            }
        }
    }

    /// <summary>
    /// Aggregate all entities in a chunk into population data, then destroy the entities.
    /// </summary>
    private void AggregateChunk(int chunkX, int chunkY, EntityManager em)
    {
        if (!_chunkPopulations.TryGetValue((chunkX, chunkY), out var popData))
        {
            popData = new ChunkPopulationData();
            _chunkPopulations[(chunkX, chunkY)] = popData;
        }
        popData.Clear();

        // Find all entities in this chunk
        _entityBuffer.Clear();
        _entitiesToDestroy.Clear();

        // Scan entities by position
        float minX = chunkX * _chunkSize;
        float minY = chunkY * _chunkSize;
        float maxX = minX + _chunkSize;
        float maxY = minY + _chunkSize;

        // Use spatial hash to find entities in this chunk cell
        foreach (int entity in _spatialHash.GetEntitiesInCell(chunkX, chunkY))
        {
            if (!em.IsAlive(entity)) continue;

            // Skip player, structures (nests, crystals, spores)
            if (!em.HasComponents(entity, ComponentFlags.Species)) continue;
            if (em.HasComponents(entity, ComponentFlags.Nest)) continue;
            if (em.HasComponents(entity, ComponentFlags.Crystal)) continue;
            if (em.HasComponents(entity, ComponentFlags.Spore)) continue;

            _entityBuffer.Add(entity);
        }

        // Aggregate by species
        _speciesAccum.Clear();

        foreach (int entity in _entityBuffer)
        {
            ref var species = ref em.Species[entity];
            int sid = species.SpeciesId;

            float hungerRatio = 0.5f;
            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                hungerRatio = hunger.Max > 0 ? hunger.Current / hunger.Max : 0.5f;
            }

            float ageRatio = 0.5f;
            if (em.HasComponents(entity, ComponentFlags.Age))
            {
                ref var age = ref em.Ages[entity];
                ageRatio = age.MaxLifespan > 0 ? (float)age.Current / age.MaxLifespan : 0.5f;
            }

            if (_speciesAccum.TryGetValue(sid, out var acc))
                _speciesAccum[sid] = (acc.count + 1, acc.totalHungerRatio + hungerRatio, acc.totalAgeRatio + ageRatio);
            else
                _speciesAccum[sid] = (1, hungerRatio, ageRatio);

            _entitiesToDestroy.Add(entity);
        }

        // Store aggregated data
        foreach (var (sid, (count, totalHunger, totalAge)) in _speciesAccum)
        {
            popData.AddPopulation(sid, count, totalHunger / count, totalAge / count);
        }

        // Cache chunk nutrition
        var chunk = _worldManager.GetChunk(chunkX, chunkY);
        if (chunk != null)
        {
            float totalNutrition = 0;
            int grazeCount = 0;
            for (int ly = 0; ly < _chunkSize; ly++)
            {
                for (int lx = 0; lx < _chunkSize; lx++)
                {
                    if (chunk.GetTile(lx, ly).IsGrazeable())
                    {
                        totalNutrition += chunk.GetNutrition(lx, ly);
                        grazeCount++;
                    }
                }
            }
            popData.GrazeableTileCount = grazeCount;
            popData.AverageNutrition = grazeCount > 0 ? totalNutrition / grazeCount : 0;
        }

        popData.IsActive = true;

        // Destroy aggregated entities
        foreach (int entity in _entitiesToDestroy)
            _spatialHash.Remove(entity);
        em.DestroyEntities(_entitiesToDestroy);
    }

    /// <summary>
    /// Materialize entities from population data when a chunk becomes nearby.
    /// </summary>
    private void MaterializeChunk(int chunkX, int chunkY, EntityManager em)
    {
        if (!_chunkPopulations.TryGetValue((chunkX, chunkY), out var popData) || !popData.IsActive)
            return;

        var chunk = _worldManager.GetChunk(chunkX, chunkY);
        if (chunk == null)
        {
            popData.IsActive = false;
            return;
        }

        _toMaterialize.Clear();
        foreach (var (sid, pop) in popData.Populations)
        {
            if (pop.Count > 0)
                _toMaterialize.Add((sid, pop.Count));
        }

        foreach (var (speciesId, count) in _toMaterialize)
        {
            // Cap materialization to avoid exceeding max population
            int toSpawn = Math.Min(count, _maxPopulation - em.EntityCount);
            if (toSpawn <= 0) break;

            // Look up species definition
            var speciesDef = SpeciesRegistry.GetById(speciesId);
            if (speciesDef == null) continue;

            // Get valid spawn positions
            var positions = _worldManager.GetSpawnablePositionsForSpecies(chunk, toSpawn, _rng, speciesDef);

            foreach (var (x, y, _, _) in positions)
            {
                if (em.EntityCount >= _maxPopulation) break;
                _entityFactory.SpawnCreature(x, y, speciesDef);
            }
        }

        popData.IsActive = false;
        popData.Populations.Clear();
        popData.TotalCount = 0;
    }

    /// <summary>
    /// Apply population-level birth/death rates for one statistical tick interval.
    /// Uses simplified Lotka-Volterra dynamics.
    /// </summary>
    private void UpdatePopulation(ChunkPopulationData popData, int chunkX, int chunkY)
    {
        // Nutrition regeneration (simplified: average nutrition drifts toward 1.0)
        if (popData.GrazeableTileCount > 0)
        {
            // Regeneration: nutrition += RegenerationRate * TickInterval (but capped at 1.0)
            float regenPerInterval = Chunk.RegenerationRate * StatisticalTickInterval;
            popData.AverageNutrition = MathF.Min(1.0f, popData.AverageNutrition + regenPerInterval);
        }

        // Count herbivores and predators for interaction
        int totalHerbivores = 0;
        int totalPredators = 0;
        _keyBuffer.Clear();
        _keyBuffer.AddRange(popData.Populations.Keys);

        foreach (int sid in _keyBuffer)
        {
            var speciesDef = SpeciesRegistry.GetById(sid);
            if (speciesDef == null) continue;
            var pop = popData.Populations[sid];
            if (speciesDef.IsPredator) totalPredators += pop.Count;
            else totalHerbivores += pop.Count;
        }

        // Process each species
        foreach (int sid in _keyBuffer)
        {
            var speciesDef = SpeciesRegistry.GetById(sid);
            if (speciesDef == null) continue;

            var pop = popData.Populations[sid];
            if (pop.Count <= 0) continue;

            // --- Birth rate ---
            // Fraction of population that's mature
            float matureFraction = MathF.Max(0, 1f - (float)speciesDef.MaturityAge / speciesDef.MaxLifespan);

            // Fraction that's well-fed enough to reproduce
            float wellFedFraction;
            float hungerThresholdRatio = speciesDef.ReproHungerThreshold / speciesDef.MaxHunger;
            if (pop.AverageHungerRatio > hungerThresholdRatio)
                wellFedFraction = 0.8f; // Most mature individuals can breed
            else if (pop.AverageHungerRatio > hungerThresholdRatio * 0.8f)
                wellFedFraction = 0.3f; // Some can breed
            else
                wellFedFraction = 0.0f; // Too hungry

            // Births per tick per eligible individual: 1/cooldown * offspring
            float birthsPerTickPerIndividual = (float)speciesDef.OffspringCount / speciesDef.ReproCooldown;
            float birthRate = pop.Count * matureFraction * wellFedFraction * birthsPerTickPerIndividual;

            // Density suppression: reduce births as count approaches carrying capacity
            float carryingCapacity = EstimateCarryingCapacity(speciesDef, popData);
            if (carryingCapacity > 0 && pop.Count > carryingCapacity * 0.5f)
            {
                float densityFactor = MathF.Max(0, 1f - pop.Count / carryingCapacity);
                birthRate *= densityFactor;
            }

            // --- Death rate ---
            // Natural death: uniform age distribution = 1/maxLifespan per individual per tick
            float naturalDeathRate = (float)pop.Count / speciesDef.MaxLifespan;

            // Starvation deaths: depends on hunger level
            float starvationRate = 0;
            if (pop.AverageHungerRatio < 0.1f)
            {
                // Many individuals starving, high death rate
                float starvationFraction = MathF.Max(0, 0.1f - pop.AverageHungerRatio) / 0.1f;
                // Time to die from starvation: MaxEnergy / StarvationDamage ticks
                float starvationDeathsPerTick = starvationFraction * pop.Count /
                    (speciesDef.MaxEnergy / speciesDef.StarvationDamage);
                starvationRate = starvationDeathsPerTick;
            }

            // Predation deaths (herbivores only)
            float predationRate = 0;
            if (!speciesDef.IsPredator && totalPredators > 0 && totalHerbivores > 0)
            {
                // Simplified: predators kill at a rate proportional to predator/prey ratio
                // Each predator needs ~1 kill per (MaxHunger/EffectiveNutrition * HungerDecayRate^-1) ticks
                float killsPerPredatorPerTick = speciesDef.HungerDecayRate /
                    MathF.Max(1f, speciesDef.EffectiveNutrition * 0.5f);
                // Distribute predation across all herbivore species proportionally
                float preyShareFraction = totalHerbivores > 0 ? (float)pop.Count / totalHerbivores : 0;
                predationRate = totalPredators * killsPerPredatorPerTick * preyShareFraction;
            }

            float totalDeathRate = naturalDeathRate + starvationRate + predationRate;

            // Apply births and deaths over the tick interval
            float births = birthRate * StatisticalTickInterval + pop.FractionalBirths;
            float deaths = totalDeathRate * StatisticalTickInterval + pop.FractionalDeaths;

            int intBirths = (int)births;
            int intDeaths = (int)deaths;

            pop.FractionalBirths = births - intBirths;
            pop.FractionalDeaths = deaths - intDeaths;

            pop.Count = Math.Max(0, pop.Count + intBirths - intDeaths);

            // Update hunger based on food availability
            if (speciesDef.CanGraze && popData.GrazeableTileCount > 0)
            {
                // Grazing pressure: each herbivore consumes HungerDecayRate worth of nutrition
                float consumptionPerTick = pop.Count * speciesDef.HungerDecayRate / speciesDef.MaxHunger;
                float nutritionSupply = popData.AverageNutrition * popData.GrazeableTileCount * 0.01f;

                if (nutritionSupply > consumptionPerTick * StatisticalTickInterval)
                {
                    // Plenty of food — hunger drifts up
                    pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.02f);
                }
                else
                {
                    // Overgrazing — hunger drifts down, nutrition depletes
                    pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.03f);
                    popData.AverageNutrition *= 0.95f;
                }
            }
            else if (speciesDef.IsPredator)
            {
                // Predator hunger depends on prey availability
                if (totalHerbivores > totalPredators * 3)
                    pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.01f);
                else
                    pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.02f);
            }

            // Age drift (average age drifts toward middle of lifespan in steady state)
            pop.AverageAgeRatio = MathF.Min(0.9f, pop.AverageAgeRatio + 0.001f);
            // Births pull average age down
            if (intBirths > 0 && pop.Count > 0)
                pop.AverageAgeRatio *= (float)(pop.Count - intBirths) / pop.Count;

            popData.Populations[sid] = pop;
        }

        // Recalculate total
        popData.TotalCount = 0;
        foreach (var pop in popData.Populations.Values)
            popData.TotalCount += pop.Count;
    }

    /// <summary>
    /// Estimate carrying capacity for a species in a chunk.
    /// </summary>
    private float EstimateCarryingCapacity(SpeciesDefinition species, ChunkPopulationData popData)
    {
        if (species.CanGraze)
        {
            // Herbivore carrying capacity: based on nutrition supply vs consumption
            // Each tile regenerates RegenerationRate per tick
            // Each herbivore consumes HungerDecayRate per tick
            if (popData.GrazeableTileCount == 0) return 0;
            float supplyPerTick = popData.GrazeableTileCount * Chunk.RegenerationRate;
            float demandPerIndividual = species.HungerDecayRate / species.GrazeNutrition;
            return supplyPerTick / MathF.Max(0.001f, demandPerIndividual);
        }

        if (species.IsPredator)
        {
            // Predator capacity: roughly 1 predator per 5 prey
            return popData.TotalCount * 0.15f;
        }

        // Faction/other: small steady population
        return 10f;
    }
}
