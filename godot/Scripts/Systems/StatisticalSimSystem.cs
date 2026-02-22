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
    private readonly NestSystem _nestSystem;
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
    private readonly Dictionary<int, (int count, float totalHungerRatio, float totalAgeRatio,
        float totalGrowthScale, float totalPower)> _speciesAccum = new(8);
    private readonly List<int> _keyBuffer = new(16);
    private readonly List<int> _structureBuffer = new(32);

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
                                 SpatialHash spatialHash, NestSystem nestSystem,
                                 int maxPopulation, int chunkSize)
    {
        _worldManager = worldManager;
        _entityFactory = entityFactory;
        _spatialHash = spatialHash;
        _nestSystem = nestSystem;
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
    /// Terraformers (Shroomer, Sectid, Faeling) are now aggregated alongside regular species.
    /// Structures (nests, spores) are destroyed; crystals stay alive with a sentinel.
    /// </summary>
    private void AggregateChunk(int chunkX, int chunkY, EntityManager em)
    {
        if (!_chunkPopulations.TryGetValue((chunkX, chunkY), out var popData))
        {
            popData = new ChunkPopulationData();
            _chunkPopulations[(chunkX, chunkY)] = popData;
        }
        popData.Clear();

        _entityBuffer.Clear();
        _entitiesToDestroy.Clear();
        _structureBuffer.Clear();

        // Use spatial hash to find entities in this chunk cell
        foreach (int entity in _spatialHash.GetEntitiesInCell(chunkX, chunkY))
        {
            if (!em.IsAlive(entity)) continue;

            // Skip player and non-species (crystals have no Species component, skipped naturally)
            if (!em.HasComponents(entity, ComponentFlags.Species)) continue;

            // Spores: destroy but don't count as population (modeled in Shroomer birth rate)
            if (em.HasComponents(entity, ComponentFlags.Spore))
            {
                _entitiesToDestroy.Add(entity);
                continue;
            }

            // Nest entities: collect separately (destroyed, count tracked in popData)
            if (em.HasComponents(entity, ComponentFlags.Nest))
            {
                _structureBuffer.Add(entity);
                continue;
            }

            // All creatures (regular + terraformers) get aggregated
            _entityBuffer.Add(entity);
        }

        // Aggregate creatures by species
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

            // Track growth scale for Shroomer/Faeling
            float growthScale = 1f;
            if (em.HasComponents(entity, ComponentFlags.Growth))
                growthScale = em.Growths[entity].CurrentScale;

            // Track power for Faelings
            float power = 0f;
            if (em.HasComponents(entity, ComponentFlags.FaelingPower))
            {
                power = em.FaelingPowers[entity].Power;

                // Update crystal: mark linked Faeling as aggregated (not dead)
                int crystalId = em.FaelingPowers[entity].LinkedCrystal;
                if (crystalId >= 0 && em.IsAlive(crystalId) &&
                    em.HasComponents(crystalId, ComponentFlags.Crystal))
                {
                    ref var crystal = ref em.Crystals[crystalId];
                    crystal.LinkedFaeling = Crystal.FAELING_AGGREGATED;
                    crystal.InheritedPower = power;
                }
            }

            if (_speciesAccum.TryGetValue(sid, out var acc))
                _speciesAccum[sid] = (acc.count + 1, acc.totalHungerRatio + hungerRatio,
                    acc.totalAgeRatio + ageRatio, acc.totalGrowthScale + growthScale,
                    acc.totalPower + power);
            else
                _speciesAccum[sid] = (1, hungerRatio, ageRatio, growthScale, power);

            _entitiesToDestroy.Add(entity);
        }

        // Store aggregated data
        foreach (var (sid, (count, totalHunger, totalAge, totalGrowth, totalPower)) in _speciesAccum)
        {
            var pop = new SpeciesPopulation
            {
                SpeciesId = sid,
                Count = count,
                AverageHungerRatio = totalHunger / count,
                AverageAgeRatio = totalAge / count,
                AverageGrowthScale = totalGrowth / count,
                AveragePower = totalPower / count,
            };
            popData.Populations[sid] = pop;
        }

        // Count and destroy nest structures
        popData.NestCount = _structureBuffer.Count;
        float totalNestFood = 0;
        foreach (int nestEntity in _structureBuffer)
        {
            if (em.HasComponents(nestEntity, ComponentFlags.Nest))
                totalNestFood += em.Nests[nestEntity].FoodStored;
            _spatialHash.Remove(nestEntity);
            _entitiesToDestroy.Add(nestEntity);
        }
        popData.NestFoodStored = popData.NestCount > 0 ? totalNestFood / popData.NestCount : 0;

        // Count crystals in chunk (they stay alive — just count for Faeling birth rate)
        popData.CrystalCount = 0;
        foreach (int entity in _spatialHash.GetEntitiesInCell(chunkX, chunkY))
        {
            if (em.IsAlive(entity) && em.HasComponents(entity, ComponentFlags.Crystal))
                popData.CrystalCount++;
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

        // Recalculate total
        popData.TotalCount = 0;
        foreach (var pop in popData.Populations.Values)
            popData.TotalCount += pop.Count;

        popData.IsActive = true;

        // Destroy aggregated entities (creatures + spores + nests)
        foreach (int entity in _entitiesToDestroy)
            _spatialHash.Remove(entity);
        em.DestroyEntities(_entitiesToDestroy);
    }

    /// <summary>
    /// Catch entities that moved into already-statistical chunks (e.g. via movement or reproduction
    /// near chunk boundaries). Merges them into the chunk's population data and destroys them.
    /// Runs every tick so leaked entities never persist for more than one frame.
    /// Now also sweeps terraformers and spores.
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
                // Skip crystals (permanent structures that stay alive)
                if (em.HasComponents(entity, ComponentFlags.Crystal)) continue;

                // Spores: destroy but don't count as population
                if (em.HasComponents(entity, ComponentFlags.Spore))
                {
                    _entitiesToDestroy.Add(entity);
                    found = true;
                    continue;
                }

                // Nests: destroy but don't count as population (already tracked in popData)
                if (em.HasComponents(entity, ComponentFlags.Nest))
                {
                    _entitiesToDestroy.Add(entity);
                    found = true;
                    continue;
                }

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

                float growthScale = 1f;
                if (em.HasComponents(entity, ComponentFlags.Growth))
                    growthScale = em.Growths[entity].CurrentScale;

                float power = 0f;
                if (em.HasComponents(entity, ComponentFlags.FaelingPower))
                {
                    power = em.FaelingPowers[entity].Power;
                    // Update crystal sentinel
                    int crystalId = em.FaelingPowers[entity].LinkedCrystal;
                    if (crystalId >= 0 && em.IsAlive(crystalId) &&
                        em.HasComponents(crystalId, ComponentFlags.Crystal))
                    {
                        ref var crystal = ref em.Crystals[crystalId];
                        crystal.LinkedFaeling = Crystal.FAELING_AGGREGATED;
                        crystal.InheritedPower = power;
                    }
                }

                if (_speciesAccum.TryGetValue(sid, out var acc))
                    _speciesAccum[sid] = (acc.count + 1, acc.totalHungerRatio + hungerRatio,
                        acc.totalAgeRatio + ageRatio, acc.totalGrowthScale + growthScale,
                        acc.totalPower + power);
                else
                    _speciesAccum[sid] = (1, hungerRatio, ageRatio, growthScale, power);

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
            foreach (var (sid, (count, totalHunger, totalAge, totalGrowth, totalPower)) in _speciesAccum)
            {
                if (popData.Populations.TryGetValue(sid, out var existing))
                {
                    int newCount = existing.Count + count;
                    existing.AverageHungerRatio = (existing.AverageHungerRatio * existing.Count + totalHunger) / newCount;
                    existing.AverageAgeRatio = (existing.AverageAgeRatio * existing.Count + totalAge) / newCount;
                    existing.AverageGrowthScale = (existing.AverageGrowthScale * existing.Count + totalGrowth) / newCount;
                    existing.AveragePower = (existing.AveragePower * existing.Count + totalPower) / newCount;
                    existing.Count = newCount;
                    popData.Populations[sid] = existing;
                }
                else
                {
                    popData.AddPopulation(sid, count, totalHunger / count, totalAge / count);
                    // Set extra fields on newly added population
                    var newPop = popData.Populations[sid];
                    newPop.AverageGrowthScale = totalGrowth / count;
                    newPop.AveragePower = totalPower / count;
                    popData.Populations[sid] = newPop;
                }
            }

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
    /// Handles terraformer-specific spawning: growth scale, nests, crystal re-linking.
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

        // Collect Faeling crystals in this chunk for re-linking
        _structureBuffer.Clear();
        foreach (int entity in _spatialHash.GetEntitiesInCell(chunkX, chunkY))
        {
            if (em.IsAlive(entity) && em.HasComponents(entity, ComponentFlags.Crystal))
                _structureBuffer.Add(entity);
        }
        int crystalIndex = 0;

        _toMaterialize.Clear();
        foreach (var (sid, pop) in popData.Populations)
        {
            if (pop.Count > 0)
                _toMaterialize.Add((sid, pop.Count));
        }

        foreach (var (speciesId, count) in _toMaterialize)
        {
            int toSpawn = Math.Min(count, _maxPopulation - em.EntityCount);
            if (toSpawn <= 0) break;

            var speciesDef = SpeciesRegistry.GetById(speciesId);
            if (speciesDef == null) continue;

            // Faelings: re-link to crystals instead of spawning via EntityFactory
            // (EntityFactory.SpawnCreature would incorrectly add Prey/Fear to Faelings)
            if (speciesDef.CrystalSpawned)
            {
                int faelingCount = toSpawn;
                var pop = popData.Populations[speciesId];
                while (faelingCount > 0 && crystalIndex < _structureBuffer.Count)
                {
                    int crystalEntity = _structureBuffer[crystalIndex++];
                    if (!em.IsAlive(crystalEntity)) continue;
                    ref var crystal = ref em.Crystals[crystalEntity];
                    // Set crystal to immediately spawn a Faeling (1 tick delay)
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = 1;
                    crystal.InheritedPower = pop.AveragePower;
                    faelingCount--;
                }
                // Any remaining crystals without Faelings: start normal respawn
                while (crystalIndex < _structureBuffer.Count)
                {
                    int crystalEntity = _structureBuffer[crystalIndex++];
                    if (!em.IsAlive(crystalEntity)) continue;
                    ref var crystal = ref em.Crystals[crystalEntity];
                    if (crystal.IsFaelingAggregated)
                    {
                        crystal.LinkedFaeling = -1;
                        crystal.SpawnTimer = crystal.SpawnDelay;
                    }
                }
                continue;
            }

            // Regular species + Shroomer + Sectid: spawn via EntityFactory
            var positions = _worldManager.GetSpawnablePositionsForSpecies(chunk, toSpawn, _rng, speciesDef);
            var popForSpecies = popData.Populations[speciesId];

            foreach (var (x, y, _, _) in positions)
            {
                if (em.EntityCount >= _maxPopulation) break;
                _entityFactory.SpawnCreature(x, y, speciesDef);

                // Post-fix growth scale for terraformers with Growth component
                if (speciesDef.HasGrowth && popForSpecies.AverageGrowthScale > 1f)
                {
                    // Find the just-spawned entity (it's the most recent)
                    int spawned = em.EntityCount - 1;
                    if (spawned >= 0 && em.IsAlive(spawned) &&
                        em.HasComponents(spawned, ComponentFlags.Growth))
                    {
                        ref var growth = ref em.Growths[spawned];
                        growth.CurrentScale = popForSpecies.AverageGrowthScale;
                        // Scale renderable size to match
                        if (em.HasComponents(spawned, ComponentFlags.Renderable))
                        {
                            ref var rend = ref em.Renderables[spawned];
                            rend.Size = speciesDef.BaseSize * growth.CurrentScale;
                        }
                        // Scale max energy with growth
                        if (em.HasComponents(spawned, ComponentFlags.Energy))
                        {
                            ref var energy = ref em.Energies[spawned];
                            energy.Max = speciesDef.MaxEnergy * growth.CurrentScale;
                            energy.Current = energy.Max;
                        }
                    }
                }
            }

            // Sectid: also recreate nests (~1 nest per 5 Sectids, minimum 1 if any Sectids)
            if (speciesDef.NestBreeder && toSpawn > 0)
            {
                int nestsToSpawn = Math.Max(1, toSpawn / 5);
                for (int i = 0; i < nestsToSpawn; i++)
                {
                    if (em.EntityCount >= _maxPopulation) break;
                    // Find a valid position for the nest (dry tiles)
                    float nx = (chunkX + 0.1f + (float)_rng.NextDouble() * 0.8f) * _chunkSize;
                    float ny = (chunkY + 0.1f + (float)_rng.NextDouble() * 0.8f) * _chunkSize;
                    _nestSystem.SpawnNest(em, nx, ny, _rng.Next(1, 100));
                }
            }
        }

        popData.IsActive = false;
        popData.Populations.Clear();
        popData.TotalCount = 0;
    }

    /// <summary>
    /// Apply population-level birth/death rates for one statistical tick interval.
    /// Uses simplified Lotka-Volterra dynamics for regular species.
    /// Terraformers use species-specific models (spore/nest/crystal).
    /// </summary>
    private void UpdatePopulation(ChunkPopulationData popData, int chunkX, int chunkY)
    {
        // Nutrition regeneration
        if (popData.GrazeableTileCount > 0)
        {
            float regenPerInterval = Chunk.RegenerationRate * StatisticalTickInterval;
            popData.AverageNutrition = MathF.Min(1.0f, popData.AverageNutrition + regenPerInterval);
        }

        // Count herbivores and predators for interaction.
        // Sectid counts as predator here (it hunts) despite IsPredator being false.
        int totalHerbivores = 0;
        int totalPredators = 0;
        float totalPredatorDemand = 0;

        // Count terraformer populations for inter-faction combat
        int totalShroomers = 0, totalSectids = 0, totalFaelings = 0;

        _keyBuffer.Clear();
        _keyBuffer.AddRange(popData.Populations.Keys);

        foreach (int sid in _keyBuffer)
        {
            var speciesDef = SpeciesRegistry.GetById(sid);
            if (speciesDef == null) continue;
            var pop = popData.Populations[sid];
            if (pop.Count <= 0) continue;

            if (speciesDef.IsPredator || speciesDef.NestBreeder)
            {
                // IsPredator covers Carnivore/Omnivore. NestBreeder adds Sectid.
                totalPredators += pop.Count;
                totalPredatorDemand += pop.Count * speciesDef.HungerDecayRate;
            }
            else if (speciesDef.Diet != DietType.Terraformer)
            {
                totalHerbivores += pop.Count;
            }

            // Track terraformer totals for inter-faction combat
            if (speciesDef.SporeReproducer) totalShroomers += pop.Count;
            else if (speciesDef.NestBreeder) totalSectids += pop.Count;
            else if (speciesDef.CrystalSpawned) totalFaelings += pop.Count;
        }

        // Process each species
        foreach (int sid in _keyBuffer)
        {
            var speciesDef = SpeciesRegistry.GetById(sid);
            if (speciesDef == null) continue;

            var pop = popData.Populations[sid];
            if (pop.Count <= 0) continue;

            float birthRate, naturalDeathRate, starvationRate, predationRate;
            float carryingCapacity;

            if (speciesDef.Diet == DietType.Terraformer)
            {
                // === TERRAFORMER-SPECIFIC MODELS ===
                CalculateTerraformerRates(speciesDef, ref pop, popData, chunkX, chunkY,
                    totalShroomers, totalSectids, totalFaelings,
                    totalHerbivores, totalPredators,
                    out birthRate, out naturalDeathRate, out starvationRate, out predationRate,
                    out carryingCapacity);
            }
            else
            {
                // === REGULAR SPECIES (unchanged logic) ===
                float matureFraction = MathF.Max(0, 1f - (float)speciesDef.MaturityAge / speciesDef.MaxLifespan);
                float wellFedFraction;
                float hungerThresholdRatio = speciesDef.ReproHungerThreshold / speciesDef.MaxHunger;
                if (pop.AverageHungerRatio > hungerThresholdRatio)
                    wellFedFraction = 0.8f;
                else if (pop.AverageHungerRatio > hungerThresholdRatio * 0.8f)
                    wellFedFraction = 0.3f;
                else
                    wellFedFraction = 0.0f;

                float birthsPerTickPerIndividual = (float)speciesDef.OffspringCount / speciesDef.ReproCooldown;
                birthRate = pop.Count * matureFraction * wellFedFraction * birthsPerTickPerIndividual;

                carryingCapacity = EstimateCarryingCapacity(speciesDef, popData, chunkX, chunkY);
                if (carryingCapacity <= 0)
                    birthRate = 0;
                else if (pop.Count > carryingCapacity * 0.5f)
                    birthRate *= MathF.Max(0, 1f - pop.Count / carryingCapacity);

                naturalDeathRate = (float)pop.Count / speciesDef.MaxLifespan;
                starvationRate = 0;
                if (pop.AverageHungerRatio < 0.1f)
                {
                    float starvationFraction = MathF.Max(0, 0.1f - pop.AverageHungerRatio) / 0.1f;
                    starvationRate = starvationFraction * pop.Count /
                        (speciesDef.MaxEnergy / speciesDef.StarvationDamage);
                }

                predationRate = 0;
                if (!speciesDef.IsPredator && totalPredatorDemand > 0 && totalHerbivores > 0)
                {
                    float killsNeededPerTick = totalPredatorDemand /
                        MathF.Max(1f, speciesDef.EffectiveNutrition);
                    float preyShareFraction = (float)pop.Count / totalHerbivores;
                    predationRate = killsNeededPerTick * preyShareFraction;
                }
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
                    float naturalFrac = naturalDeathRate / totalDeathRate;
                    float starvationFrac = starvationRate / totalDeathRate;
                    int naturalDeaths = (int)(intDeaths * naturalFrac + 0.5f);
                    int starvationDeaths = (int)(intDeaths * starvationFrac + 0.5f);
                    int predationDeaths = intDeaths - naturalDeaths - starvationDeaths;
                    if (predationDeaths < 0) { predationDeaths = 0; naturalDeaths = intDeaths - starvationDeaths; }

                    logger.LogStatDeaths(sid, naturalDeaths, cx, cy, "age_death");
                    logger.LogStatDeaths(sid, starvationDeaths, cx, cy, "starvation");
                    logger.LogStatDeaths(sid, predationDeaths, cx, cy, "kill");
                }
            }

            // Hard per-chunk cap
            if (carryingCapacity > 0)
            {
                int maxPerChunk = Math.Max(2, (int)(carryingCapacity * 2));
                pop.Count = Math.Min(pop.Count, maxPerChunk);
            }

            // Update hunger based on food availability
            UpdateHunger(speciesDef, ref pop, popData, chunkX, chunkY,
                totalHerbivores, totalPredators);

            // Age drift
            pop.AverageAgeRatio = MathF.Min(0.9f, pop.AverageAgeRatio + 0.001f);
            if (intBirths > 0 && pop.Count > 0)
                pop.AverageAgeRatio *= (float)(pop.Count - intBirths) / pop.Count;

            // Growth drift for terraformers
            if (speciesDef.HasGrowth && pop.AverageGrowthScale < speciesDef.GrowthMaxScale)
                pop.AverageGrowthScale = MathF.Min(speciesDef.GrowthMaxScale,
                    pop.AverageGrowthScale + speciesDef.GrowthRate * StatisticalTickInterval);

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
    /// Calculate birth/death rates for terraformer species using species-specific models.
    /// Shroomer: spore-based reproduction, FeedTile starvation, predation by wolves etc.
    /// Sectid: nest-based reproduction, predator hunger model, predation by Faelings.
    /// Faeling: crystal-based respawn, immune to starvation, killed by inter-faction combat.
    /// </summary>
    private void CalculateTerraformerRates(
        SpeciesDefinition speciesDef, ref SpeciesPopulation pop,
        ChunkPopulationData popData, int chunkX, int chunkY,
        int totalShroomers, int totalSectids, int totalFaelings,
        int totalHerbivores, int totalPredators,
        out float birthRate, out float naturalDeathRate,
        out float starvationRate, out float predationRate,
        out float carryingCapacity)
    {
        naturalDeathRate = (float)pop.Count / speciesDef.MaxLifespan;
        starvationRate = 0;
        predationRate = 0;

        if (speciesDef.SporeReproducer)
        {
            // === SHROOMER ===
            // Birth rate: population × sporeSpreadChance × sporesPerSpread × maturationRate × eligibleFraction
            float matureFraction = MathF.Max(0, 1f - (float)speciesDef.MaturityAge / speciesDef.MaxLifespan);
            float wellFedFraction = pop.AverageHungerRatio > 0.5f ? 0.8f : (pop.AverageHungerRatio > 0.3f ? 0.3f : 0f);
            float feedTiles = CountFeedTiles(speciesDef, chunkX, chunkY);
            float totalTiles = _chunkSize * _chunkSize;
            float moistTileFraction = feedTiles / MathF.Max(1f, totalTiles);

            // ~77% maturation rate from entity sim data
            const float maturationRate = 0.77f;
            birthRate = pop.Count * matureFraction * wellFedFraction
                * speciesDef.SporeSpreadChance * speciesDef.SporesPerSpread * maturationRate
                * moistTileFraction;

            // Carrying capacity: based on feed tiles (Wetland/Forest)
            carryingCapacity = feedTiles > 0 ? feedTiles * 0.3f : 0f;
            if (carryingCapacity > 0 && pop.Count > carryingCapacity * 0.5f)
                birthRate *= MathF.Max(0, 1f - pop.Count / carryingCapacity);

            // Starvation: Shroomer feeds on Wetland/Forest tiles
            if (pop.AverageHungerRatio < 0.1f && speciesDef.StarvationDamage > 0)
            {
                float starvFrac = MathF.Max(0, 0.1f - pop.AverageHungerRatio) / 0.1f;
                starvationRate = starvFrac * pop.Count / (speciesDef.MaxEnergy / speciesDef.StarvationDamage);
            }

            // Predation: wolves and other real predators hunt Shroomer (IsPrey is true)
            // Use same Lotka-Volterra but include Shroomer in the prey pool
            int preyPool = totalHerbivores + totalShroomers;
            if (totalPredators > 0 && preyPool > 0)
            {
                float predatorDemand = totalPredators * 0.05f; // approximate hunger demand
                float killsNeeded = predatorDemand / MathF.Max(1f, speciesDef.EffectiveNutrition);
                predationRate = killsNeeded * ((float)pop.Count / preyPool);
            }

            // Inter-faction combat: Faelings kill Shroomers
            if (totalFaelings > 0)
            {
                // Faeling ranged attacks: ~1 kill per 20 ticks per Faeling vs Shroomers
                float faelingKillRate = totalFaelings * 0.05f / MathF.Max(1f, speciesDef.MaxEnergy);
                predationRate += faelingKillRate * ((float)pop.Count / MathF.Max(1, totalShroomers + totalSectids));
            }
        }
        else if (speciesDef.NestBreeder)
        {
            // === SECTID ===
            // Birth rate from nests: nestCount × avgSlots × (1/spawnDuration) × foodAvailability
            int nestCount = Math.Max(popData.NestCount, pop.Count > 0 ? 1 : 0);
            float avgSlots = 2f; // average across stages 1-3
            // Food availability: Sectids hunt prey, so depends on prey count
            int preyForSectid = totalHerbivores + totalShroomers;
            float foodAvailability = preyForSectid > 0
                ? MathF.Min(1f, (float)preyForSectid / (pop.Count * 2f + 1f))
                : 0f;
            float nestSpawnDuration = speciesDef.NestSpawnDuration > 0 ? speciesDef.NestSpawnDuration : 200f;
            birthRate = nestCount * avgSlots * (1f / nestSpawnDuration) * foodAvailability;

            // Carrying capacity: predator-like (needs prey)
            carryingCapacity = preyForSectid * 0.2f;
            if (carryingCapacity > 0 && pop.Count > carryingCapacity * 0.5f)
                birthRate *= MathF.Max(0, 1f - pop.Count / carryingCapacity);

            // Nest count adjusts over time based on Sectid population
            if (pop.Count > nestCount * 5)
                popData.NestCount = Math.Min(nestCount + 1, pop.Count / 3);
            else if (pop.Count < nestCount * 2 && nestCount > 1)
                popData.NestCount = nestCount - 1;

            // Starvation: Sectids hunt to survive, so hunger depends on prey availability
            if (pop.AverageHungerRatio < 0.1f && speciesDef.StarvationDamage > 0)
            {
                float starvFrac = MathF.Max(0, 0.1f - pop.AverageHungerRatio) / 0.1f;
                starvationRate = starvFrac * pop.Count / (speciesDef.MaxEnergy / speciesDef.StarvationDamage);
            }

            // Inter-faction: Faelings and Shroomer AoE kill Sectids
            if (totalFaelings > 0)
            {
                float faelingKillRate = totalFaelings * 0.05f / MathF.Max(1f, speciesDef.MaxEnergy);
                predationRate += faelingKillRate * ((float)pop.Count / MathF.Max(1, totalShroomers + totalSectids));
            }
            if (totalShroomers > 0)
            {
                // Shroomer AoE: damage scales with average growth. Larger Shroomers kill more Sectids.
                var shroomDef = SpeciesRegistry.Get("Shroomer");
                float avgScale = 1f;
                int shroomSid = SpeciesRegistry.GetId("Shroomer");
                if (popData.Populations.TryGetValue(shroomSid, out var shroomPop))
                    avgScale = shroomPop.AverageGrowthScale;
                float aoeDamageRate = totalShroomers * 0.02f * avgScale / MathF.Max(1f, speciesDef.MaxEnergy);
                predationRate += aoeDamageRate;
            }
        }
        else if (speciesDef.CrystalSpawned)
        {
            // === FAELING ===
            // Birth rate: unlinked crystals respawn Faelings
            int unlinked = Math.Max(0, popData.CrystalCount - pop.Count);
            float spawnDelay = speciesDef.CrystalSpawnDelay > 0 ? speciesDef.CrystalSpawnDelay : 500f;
            birthRate = (float)unlinked / spawnDelay;

            // Carrying capacity = crystal count (hard cap: 1 Faeling per crystal)
            carryingCapacity = popData.CrystalCount;
            pop.Count = Math.Min(pop.Count, popData.CrystalCount);

            // Faelings are immune to starvation
            starvationRate = 0;

            // Inter-faction: killed by Shroomer AoE and Sectid swarms
            if (totalShroomers > 0)
            {
                var shroomDef = SpeciesRegistry.Get("Shroomer");
                float avgScale = 1f;
                int shroomSid = SpeciesRegistry.GetId("Shroomer");
                if (popData.Populations.TryGetValue(shroomSid, out var shroomPop))
                    avgScale = shroomPop.AverageGrowthScale;
                // Large Shroomers are devastating to Faelings
                float aoeDamageRate = totalShroomers * 0.01f * avgScale / MathF.Max(1f, speciesDef.MaxEnergy);
                predationRate += aoeDamageRate;
            }
            if (totalSectids > 0)
            {
                // Sectid swarms can overwhelm Faelings
                float swarmRate = totalSectids * 0.005f / MathF.Max(1f, speciesDef.MaxEnergy);
                predationRate += swarmRate;
            }
        }
        else
        {
            // Unknown terraformer — use generic rates
            birthRate = 0;
            carryingCapacity = 10f;
        }
    }

    /// <summary>
    /// Update hunger ratio for a species based on food availability.
    /// Handles all diet types including terraformer-specific feeding.
    /// </summary>
    private void UpdateHunger(SpeciesDefinition speciesDef, ref SpeciesPopulation pop,
        ChunkPopulationData popData, int chunkX, int chunkY,
        int totalHerbivores, int totalPredators)
    {
        if (speciesDef.ImmuneToStarvation)
        {
            // Faelings: always well-fed
            pop.AverageHungerRatio = 1f;
        }
        else if (speciesDef.CanGraze)
        {
            if (popData.GrazeableTileCount > 0)
            {
                float demandPerTick = pop.Count * (speciesDef.HungerDecayRate / speciesDef.GrazeNutrition);
                float supplyPerTick = popData.GrazeableTileCount * Chunk.RegenerationRate;
                if (supplyPerTick > demandPerTick)
                    pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.01f);
                else
                {
                    float deficit = demandPerTick / MathF.Max(0.001f, supplyPerTick);
                    pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.02f * deficit);
                    popData.AverageNutrition *= 0.95f;
                }
            }
            else
                pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.1f);
        }
        else if (speciesDef.IsPredator || speciesDef.NestBreeder)
        {
            // Predator/Sectid hunger depends on prey availability
            int preyCount = totalHerbivores;
            if (speciesDef.NestBreeder)
            {
                // Sectids also hunt Shroomers (spores)
                int shroomSid = SpeciesRegistry.GetId("Shroomer");
                if (popData.Populations.TryGetValue(shroomSid, out var shroomPop))
                    preyCount += shroomPop.Count;
            }
            if (preyCount > totalPredators * 3)
                pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.01f);
            else
                pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.02f);
        }
        else if (speciesDef.FeedTiles != null && speciesDef.FeedTiles.Count > 0
                 && speciesDef.FeedNutrition > 0)
        {
            // FeedTile species (Fish, Shroomer, etc.)
            float feedTileCount = CountFeedTiles(speciesDef, chunkX, chunkY);
            float tilesPerCreature = feedTileCount / MathF.Max(1f, pop.Count);
            if (tilesPerCreature > 2f)
                pop.AverageHungerRatio = MathF.Min(1f, pop.AverageHungerRatio + 0.01f);
            else if (tilesPerCreature > 0.5f)
                pop.AverageHungerRatio = MathF.Min(0.6f, pop.AverageHungerRatio + 0.005f);
            else
                pop.AverageHungerRatio = MathF.Max(0f, pop.AverageHungerRatio - 0.03f);
        }
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

        // Predator capacity: based on herbivore count (includes Sectid which hunts)
        if (species.IsPredator || species.NestBreeder)
        {
            int herbivoreCount = 0;
            foreach (var (sid, pop) in popData.Populations)
            {
                var def = SpeciesRegistry.GetById(sid);
                if (def != null && !def.IsPredator && !def.NestBreeder && def.Diet != DietType.Terraformer)
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
