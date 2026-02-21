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
    private const float StatisticalDistanceThreshold = 50f; // ~1.5 chunks; 9/81 entity in 9x9 world

    /// <summary>How often (in ticks) to run the statistical population update.</summary>
    private const int StatisticalTickInterval = 30;

    /// <summary>Total statistical population across all chunks (for debug overlay).</summary>
    public int TotalStatisticalPopulation { get; private set; }

    /// <summary>Number of chunks currently running statistical simulation.</summary>
    public int StatisticalChunkCount => _statisticalChunks.Count;

    /// <summary>
    /// Get per-species population counts across all statistical chunks.
    /// Writes into the provided dictionary (additive — does not clear it).
    /// </summary>
    public void AccumulateSpeciesPopulations(Dictionary<int, int> target)
    {
        foreach (var popData in _chunkPopulations.Values)
        {
            if (!popData.IsActive) continue;
            foreach (var (sid, pop) in popData.Populations)
            {
                target.TryGetValue(sid, out int existing);
                target[sid] = existing + pop.Count;
            }
        }
    }

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

        // Sweep: catch entities that drifted into statistical chunks after initial aggregation.
        // Without this, entities that walk from entity-zone chunks into already-statistical chunks
        // persist as live rendered entities (the transition logic only fires once per chunk).
        SweepLeakedEntities(em);

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

            // Skip faction species (Sectids, Shroomers, Faelings) — their unique
            // reproduction systems (nests, spores, crystals) and feeding strategies
            // (hunting, tile-feeding) can't be reduced to statistical birth/death rates.
            // They stay as live entities; LOD gates already reduce processing cost.
            if (em.HasComponents(entity, ComponentFlags.Terraform)) continue;

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
    /// Catch entities that moved into already-statistical chunks (e.g. via movement or reproduction
    /// near chunk boundaries). Merges them into the chunk's population data and destroys them.
    /// Runs every tick so leaked entities never persist for more than one frame.
    /// Cost: one spatial hash lookup per statistical chunk (~72 dict lookups in 9x9 world).
    /// </summary>
    private void SweepLeakedEntities(EntityManager em)
    {
        _entitiesToDestroy.Clear();

        foreach (var key in _statisticalChunks)
        {
            _speciesAccum.Clear();
            bool found = false;

            foreach (int entity in _spatialHash.GetEntitiesInCell(key.Item1, key.Item2))
            {
                if (!em.IsAlive(entity)) continue;
                if (!em.HasComponents(entity, ComponentFlags.Species)) continue;
                if (em.HasComponents(entity, ComponentFlags.Nest)) continue;
                if (em.HasComponents(entity, ComponentFlags.Crystal)) continue;
                if (em.HasComponents(entity, ComponentFlags.Spore)) continue;
                if (em.HasComponents(entity, ComponentFlags.Terraform)) continue;

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
                found = true;
            }

            if (!found) continue;

            // Get or create population data for this chunk
            if (!_chunkPopulations.TryGetValue(key, out var popData))
            {
                popData = new ChunkPopulationData();
                _chunkPopulations[key] = popData;
            }

            // Merge leaked entities into existing population data
            foreach (var (sid, (count, totalHunger, totalAge)) in _speciesAccum)
            {
                if (popData.Populations.TryGetValue(sid, out var existing))
                {
                    // Weighted merge of hunger/age ratios
                    int newCount = existing.Count + count;
                    existing.AverageHungerRatio = (existing.AverageHungerRatio * existing.Count + totalHunger) / newCount;
                    existing.AverageAgeRatio = (existing.AverageAgeRatio * existing.Count + totalAge) / newCount;
                    existing.Count = newCount;
                    popData.Populations[sid] = existing;
                }
                else
                {
                    popData.AddPopulation(sid, count, totalHunger / count, totalAge / count);
                }
            }

            // Recalculate total (AddPopulation does this internally, but direct dict writes don't)
            popData.TotalCount = 0;
            foreach (var pop in popData.Populations.Values)
                popData.TotalCount += pop.Count;
            popData.IsActive = true;
        }

        // Destroy all leaked entities
        if (_entitiesToDestroy.Count > 0)
        {
            foreach (int entity in _entitiesToDestroy)
                _spatialHash.Remove(entity);
            em.DestroyEntities(_entitiesToDestroy);
        }
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
        float totalPredatorDemand = 0; // total hunger drain per tick across all predators
        _keyBuffer.Clear();
        _keyBuffer.AddRange(popData.Populations.Keys);

        foreach (int sid in _keyBuffer)
        {
            var speciesDef = SpeciesRegistry.GetById(sid);
            if (speciesDef == null) continue;
            var pop = popData.Populations[sid];
            if (speciesDef.IsPredator)
            {
                totalPredators += pop.Count;
                totalPredatorDemand += pop.Count * speciesDef.HungerDecayRate;
            }
            else if (speciesDef.Diet != DietType.Terraformer)
                totalHerbivores += pop.Count; // Don't count terraformers — they aren't typical prey
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
            float carryingCapacity = EstimateCarryingCapacity(speciesDef, popData, chunkX, chunkY);
            if (carryingCapacity <= 0)
            {
                // No food source in this chunk — species cannot sustain here at all
                birthRate = 0;
            }
            else if (pop.Count > carryingCapacity * 0.5f)
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
            if (!speciesDef.IsPredator && totalPredatorDemand > 0 && totalHerbivores > 0)
            {
                // Predators collectively need totalPredatorDemand nutrition per tick.
                // Each kill of this prey species provides EffectiveNutrition worth of food.
                // Distribute kills across prey species proportionally by count.
                float killsNeededPerTick = totalPredatorDemand /
                    MathF.Max(1f, speciesDef.EffectiveNutrition);
                float preyShareFraction = (float)pop.Count / totalHerbivores;
                predationRate = killsNeededPerTick * preyShareFraction;
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

            // Log aggregate events for auditing
            var logger = EcosystemLogger.Instance;
            if (logger != null && (intBirths > 0 || intDeaths > 0))
            {
                float cx = (chunkX + 0.5f) * _chunkSize;
                float cy = (chunkY + 0.5f) * _chunkSize;

                if (intBirths > 0)
                    logger.LogStatBirths(sid, intBirths, cx, cy);

                if (intDeaths > 0 && totalDeathRate > 0)
                {
                    // Split deaths proportionally by cause
                    float naturalFrac = naturalDeathRate / totalDeathRate;
                    float starvationFrac = starvationRate / totalDeathRate;
                    // predation gets the remainder to avoid rounding drift
                    int naturalDeaths = (int)(intDeaths * naturalFrac + 0.5f);
                    int starvationDeaths = (int)(intDeaths * starvationFrac + 0.5f);
                    int predationDeaths = intDeaths - naturalDeaths - starvationDeaths;

                    // Clamp: rounding can overshoot by 1
                    if (predationDeaths < 0) { predationDeaths = 0; naturalDeaths = intDeaths - starvationDeaths; }

                    logger.LogStatDeaths(sid, naturalDeaths, cx, cy, "age_death");
                    logger.LogStatDeaths(sid, starvationDeaths, cx, cy, "starvation");
                    logger.LogStatDeaths(sid, predationDeaths, cx, cy, "kill");
                }
            }

            // Hard per-chunk cap: no species should exceed 2x carrying capacity
            // (in entity sim, spatial density + food depletion enforce this naturally)
            if (carryingCapacity > 0)
            {
                int maxPerChunk = Math.Max(2, (int)(carryingCapacity * 2));
                pop.Count = Math.Min(pop.Count, maxPerChunk);
            }
            else if (pop.Count > 0 && birthRate <= 0)
            {
                // No carrying capacity and no births — cap at what we started with
                // (population can only shrink in a hostile chunk)
            }

            // Update hunger based on food availability
            if (speciesDef.CanGraze)
            {
                if (popData.GrazeableTileCount > 0)
                {
                    // Each herbivore consumes HungerDecayRate per tick; grazing restores GrazeNutrition
                    float demandPerTick = pop.Count * (speciesDef.HungerDecayRate / speciesDef.GrazeNutrition);
                    float supplyPerTick = popData.GrazeableTileCount * Chunk.RegenerationRate;

                    if (supplyPerTick > demandPerTick)
                    {
                        pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.01f);
                    }
                    else
                    {
                        float deficit = demandPerTick / MathF.Max(0.001f, supplyPerTick);
                        pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.02f * deficit);
                        popData.AverageNutrition *= 0.95f;
                    }
                }
                else
                {
                    // No grazeable tiles — herbivores starve rapidly
                    pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.1f);
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
            else if (speciesDef.FeedTiles != null && speciesDef.FeedTiles.Count > 0
                     && speciesDef.FeedNutrition > 0)
            {
                // FeedTile species (Fish, Terraformers, etc.): hunger based on feed tile availability.
                float feedTileCount = CountFeedTiles(speciesDef, chunkX, chunkY);
                float tilesPerCreature = feedTileCount / MathF.Max(1f, pop.Count);
                if (tilesPerCreature > 2f)
                    pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.01f);
                else if (tilesPerCreature > 0.5f)
                    pop.AverageHungerRatio = MathF.Min(0.6f, pop.AverageHungerRatio + 0.005f);
                else
                    pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.03f);
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

        // Statistical terraforming for faction species
        ApplyStatisticalTerraforming(popData, chunkX, chunkY);
    }

    /// <summary>
    /// Estimate carrying capacity for a species in a chunk.
    /// Computes capacity from all applicable food sources and returns the highest,
    /// so omnivores (e.g. Boar: CanGraze + IsPredator) benefit from multiple diets.
    /// </summary>
    private float EstimateCarryingCapacity(SpeciesDefinition species, ChunkPopulationData popData,
                                             int chunkX, int chunkY)
    {
        float grazerCap = 0;
        float feedTileCap = 0;
        float predatorCap = 0;

        // Grazer capacity: based on tile nutrition supply vs consumption rate
        if (species.CanGraze && popData.GrazeableTileCount > 0)
        {
            float supplyPerTick = popData.GrazeableTileCount * Chunk.RegenerationRate;
            float demandPerIndividual = species.HungerDecayRate / species.GrazeNutrition;
            grazerCap = supplyPerTick / MathF.Max(0.001f, demandPerIndividual);
        }

        // FeedTile capacity: for species that feed from specific tile types (Fish, Terraformers)
        if (species.FeedTiles != null && species.FeedTiles.Count > 0 && species.FeedNutrition > 0)
        {
            float feedTiles = CountFeedTiles(species, chunkX, chunkY);
            float feedRatio = species.HungerDecayRate > 0
                ? species.HungerDecayRate / species.FeedNutrition
                : 0.1f; // Near-zero decay (e.g. Faeling) still needs some tiles
            feedTileCap = feedTiles / MathF.Max(0.5f, feedRatio);
        }

        // Predator capacity: based on herbivore count only (not total population)
        if (species.IsPredator)
        {
            int herbivoreCount = 0;
            foreach (var (sid, pop) in popData.Populations)
            {
                var def = SpeciesRegistry.GetById(sid);
                if (def != null && !def.IsPredator && def.Diet != DietType.Terraformer)
                    herbivoreCount += pop.Count;
            }
            predatorCap = herbivoreCount * 0.2f; // ~1 predator per 5 herbivores
        }

        // Use the highest applicable capacity (omnivores benefit from multiple food sources)
        float cap = MathF.Max(grazerCap, MathF.Max(predatorCap, feedTileCap));
        return cap > 0 ? cap : 10f; // fallback for species with no recognized food source
    }

    /// <summary>
    /// Count tiles in a chunk that match a species' FeedTiles list.
    /// Called once per statistical tick per faction species — acceptable cost.
    /// </summary>
    private int CountFeedTiles(SpeciesDefinition species, int chunkX, int chunkY)
    {
        if (species.FeedTiles == null || species.FeedTiles.Count == 0)
            return 0;

        var chunk = _worldManager.GetChunk(chunkX, chunkY);
        if (chunk == null) return 0;

        int count = 0;
        for (int ly = 0; ly < _chunkSize; ly++)
        {
            for (int lx = 0; lx < _chunkSize; lx++)
            {
                if (species.FeedTiles.Contains(chunk.GetTile(lx, ly)))
                    count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Apply simplified terraforming for faction species in a statistical chunk.
    /// Each terraformer has: Strength chance per Cooldown ticks, within Radius.
    /// Statistical rate: count * (strength / cooldown) * tickInterval tile changes.
    /// </summary>
    private void ApplyStatisticalTerraforming(ChunkPopulationData popData, int chunkX, int chunkY)
    {
        var chunk = _worldManager.GetChunk(chunkX, chunkY);
        if (chunk == null) return;

        foreach (var (sid, pop) in popData.Populations)
        {
            if (pop.Count <= 0) continue;

            var speciesDef = SpeciesRegistry.GetById(sid);
            if (speciesDef.Diet != DietType.Terraformer) continue;
            if (speciesDef.TerraformCooldown <= 0) continue;

            // Expected tile changes this interval
            float changesPerTick = pop.Count * speciesDef.TerraformStrength / speciesDef.TerraformCooldown;
            float expectedChanges = changesPerTick * StatisticalTickInterval;
            int tileChanges = (int)expectedChanges;
            // Fractional part: random chance for one extra
            if (_rng.NextDouble() < (expectedChanges - tileChanges))
                tileChanges++;

            if (tileChanges <= 0) continue;

            // Determine shift function
            TerraformDirection dir = speciesDef.TerraformDir;

            for (int i = 0; i < tileChanges; i++)
            {
                // Pick a random tile in the chunk
                int lx = _rng.Next(_chunkSize);
                int ly = _rng.Next(_chunkSize);

                var currentTile = chunk.GetTile(lx, ly);
                if (!currentTile.IsTerraformable()) continue;

                TileType? newTile = dir switch
                {
                    TerraformDirection.Wetter => currentTile.ShiftWetter(),
                    TerraformDirection.Drier => currentTile.ShiftDrier(),
                    TerraformDirection.Balanced => currentTile.ShiftBalanced(),
                    _ => null
                };

                if (newTile.HasValue)
                {
                    chunk.SetTile(lx, ly, newTile.Value);
                    _worldManager.DirtyChunks.Add((chunkX, chunkY));
                }
            }
        }
    }
}
