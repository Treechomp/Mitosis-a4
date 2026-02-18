using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Systems;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis;

/// <summary>
/// Handles initial world population — distributes creatures and structures across the map.
/// </summary>
public sealed class WorldSpawner
{
    private readonly WorldManager _worldManager;
    private readonly EntityFactory _entityFactory;
    private readonly Random _rng;
    private int _nextSpawnGroupId = 1;

    public WorldSpawner(WorldManager worldManager, EntityFactory entityFactory, Random rng)
    {
        _worldManager = worldManager;
        _entityFactory = entityFactory;
        _rng = rng;
    }

    public int SpawnCreatures(int initialPopulation, float herbivoreRatio)
    {
        int spawned = 0;

        // Collect all species by category
        // Sectids spawn from nests, Faelings from crystals — only Shroomers use normal spawning
        var herbivoreSpecies = new List<SpeciesDefinition>(SpeciesRegistry.GetHerbivores());
        var predatorSpecies = new List<SpeciesDefinition>(SpeciesRegistry.GetPredators());
        var terraformerSpecies = new List<SpeciesDefinition>();
        foreach (var tf in SpeciesRegistry.GetTerraformers())
        {
            if (!tf.NestBreeder && !tf.CrystalSpawned)
                terraformerSpecies.Add(tf); // Only Shroomers
        }

        // Budget all categories from InitialPopulation (not additive)
        float terraformerShare = terraformerSpecies.Count > 0 ? 0.12f : 0f;
        float predatorShare = predatorSpecies.Count > 0 ? (1f - herbivoreRatio) * (1f - terraformerShare) : 0f;
        float herbivoreShare = 1f - predatorShare - terraformerShare;

        int targetHerbivores = (int)(initialPopulation * herbivoreShare);
        int targetPredators = (int)(initialPopulation * predatorShare);
        int targetTerraformers = initialPopulation - targetHerbivores - targetPredators;

        GD.Print($"Spawn targets: {targetHerbivores} herbivores, {targetPredators} predators, {targetTerraformers} terraformers (total {initialPopulation})");

        // Get all chunks and shuffle
        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);

        // Scale position sampling based on target size
        int baseSamples = Math.Max(6, initialPopulation / allChunks.Count + 2);

        // Track which chunks have prey spawned in them (for predator placement)
        var preyChunks = new List<Chunk>();

        // === Phase 1: Spawn herbivores spread across the world ===
        // Different herbivore species can share territory — this is natural.
        spawned += SpawnCategory(herbivoreSpecies, targetHerbivores, allChunks, preyChunks, baseSamples);

        // === Phase 2: Spawn terraformers in their preferred biomes ===
        ShuffleList(allChunks);
        spawned += SpawnCategory(terraformerSpecies, targetTerraformers, allChunks, preyChunks, baseSamples);

        // === Phase 3: Spawn predators NEAR existing prey populations ===
        // Predators need food — place them in or adjacent to chunks with prey.
        if (predatorSpecies.Count > 0 && preyChunks.Count > 0)
        {
            // Build a list of chunks near prey: the prey chunks themselves + their neighbors
            var predatorCandidateChunks = new List<Chunk>();
            var addedChunks = new HashSet<(int, int)>();

            foreach (var preyChunk in preyChunks)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int cx = preyChunk.ChunkX + dx;
                        int cy = preyChunk.ChunkY + dy;
                        if (addedChunks.Add((cx, cy)))
                        {
                            var neighbor = _worldManager.GetChunk(cx, cy);
                            if (neighbor != null)
                                predatorCandidateChunks.Add(neighbor);
                        }
                    }
                }
            }

            var predatorTargets = ComputeWeightedTargets(predatorSpecies, targetPredators);

            for (int si = 0; si < predatorSpecies.Count; si++)
            {
                var species = predatorSpecies[si];
                int target = predatorTargets[si];
                int speciesSpawned = 0;

                // Round-robin: one group/solo per chunk per pass
                const int maxPasses = 20;
                for (int pass = 0; pass < maxPasses && speciesSpawned < target; pass++)
                {
                    ShuffleList(predatorCandidateChunks);
                    bool spawnedAnyThisPass = false;

                    foreach (var chunk in predatorCandidateChunks)
                    {
                        if (speciesSpawned >= target) break;

                        var positions = _worldManager.GetSpawnablePositionsForSpecies(
                            chunk, baseSamples, _rng, species);
                        if (positions.Count == 0) continue;

                        var (x, y, _, _) = positions[0];

                        bool isPack = _rng.NextDouble() < species.PackHunterChance
                                      && species.DefaultSocialType == SocialType.Pack;

                        if (!isPack)
                        {
                            _entityFactory.SpawnCreature(x, y, species, -1, true);
                            speciesSpawned++;
                            spawned++;
                        }
                        else
                        {
                            int packSize = (int)species.PreferredGroupSize + _rng.Next(-1, 2);
                            packSize = Math.Max(2, packSize);
                            packSize = Math.Min(packSize, target - speciesSpawned);

                            int posIndex = 0;
                            int gid = _nextSpawnGroupId++;
                            int gs = SpawnGroup(x, y, species, packSize, gid, ref posIndex);
                            speciesSpawned += gs;
                            spawned += gs;
                        }

                        spawnedAnyThisPass = true;
                    }

                    if (!spawnedAnyThisPass) break;
                }

                GD.Print($"  {species.Name}: spawned {speciesSpawned}/{target}");
            }
        }

        return spawned;
    }

    /// <summary>
    /// Spawns a category of species spread across chunks using round-robin distribution.
    /// Places at most one group per chunk per pass, then re-shuffles and cycles again.
    /// This ensures entities are spread across the entire world, not clumped in early chunks.
    /// </summary>
    public int SpawnCategory(List<SpeciesDefinition> speciesList, int totalTarget,
                               List<Chunk> chunks, List<Chunk> preyChunks, int samplesPerChunk)
    {
        if (speciesList.Count == 0 || totalTarget <= 0) return 0;

        int spawned = 0;

        // Weighted allocation: each species gets budget proportional to its SpawnWeight
        var speciesTargets = ComputeWeightedTargets(speciesList, totalTarget);

        for (int i = 0; i < speciesList.Count; i++)
        {
            var species = speciesList[i];
            int target = speciesTargets[i];
            int speciesSpawned = 0;
            var preyChunkSet = new HashSet<(int, int)>();

            // Round-robin: keep cycling through shuffled chunks, one group per chunk per pass
            const int maxPasses = 20;  // Safety limit
            for (int pass = 0; pass < maxPasses && speciesSpawned < target; pass++)
            {
                ShuffleList(chunks);
                bool spawnedAnyThisPass = false;

                foreach (var chunk in chunks)
                {
                    if (speciesSpawned >= target) break;

                    var positions = _worldManager.GetSpawnablePositionsForSpecies(
                        chunk, samplesPerChunk, _rng, species);
                    if (positions.Count == 0) continue;

                    // One group per chunk per pass
                    var (x, y, _, _) = positions[0];

                    int groupSize = (int)species.PreferredGroupSize + _rng.Next(-2, 3);
                    groupSize = Math.Max(2, groupSize);
                    groupSize = Math.Min(groupSize, target - speciesSpawned);

                    int posIndex = 0;
                    int gid = _nextSpawnGroupId++;
                    int gs = SpawnGroup(x, y, species, groupSize, gid, ref posIndex);
                    speciesSpawned += gs;
                    spawned += gs;
                    spawnedAnyThisPass |= gs > 0;

                    // Track prey chunks (deduplicated)
                    if (gs > 0 && species.IsPrey && preyChunkSet.Add((chunk.ChunkX, chunk.ChunkY)))
                        preyChunks.Add(chunk);
                }

                if (!spawnedAnyThisPass) break;  // No valid chunks left for this species
            }

            GD.Print($"  {species.Name}: spawned {speciesSpawned}/{target}");
        }

        return spawned;
    }

    /// <summary>
    /// Fisher-Yates shuffle for any list.
    /// </summary>
    private void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Computes per-species spawn targets proportional to SpawnWeight.
    /// Uses largest-remainder method to distribute the total exactly.
    /// </summary>
    private static int[] ComputeWeightedTargets(List<SpeciesDefinition> speciesList, int totalTarget)
    {
        float totalWeight = 0f;
        foreach (var species in speciesList)
            totalWeight += species.SpawnWeight;

        var targets = new int[speciesList.Count];
        var fractional = new float[speciesList.Count];
        int allocated = 0;

        for (int i = 0; i < speciesList.Count; i++)
        {
            float ideal = totalTarget * speciesList[i].SpawnWeight / totalWeight;
            targets[i] = Math.Max(2, (int)ideal);  // At least 2 (one group) per species
            fractional[i] = ideal - targets[i];
            allocated += targets[i];
        }

        // Distribute remaining budget to species with highest fractional remainders
        int remaining = totalTarget - allocated;
        while (remaining > 0)
        {
            float bestFrac = -1f;
            int bestIdx = 0;
            for (int i = 0; i < speciesList.Count; i++)
            {
                if (fractional[i] > bestFrac)
                {
                    bestFrac = fractional[i];
                    bestIdx = i;
                }
            }
            targets[bestIdx]++;
            fractional[bestIdx] -= 1f;
            remaining--;
        }

        return targets;
    }

    /// <summary>
    /// Spawns a group of creatures around a central position.
    /// </summary>
    public int SpawnGroup(float centerX, float centerY, SpeciesDefinition species, int groupSize,
                           int groupId, ref int posIndex)
    {
        int spawned = 0;
        float groupRadius = 3f;  // Spawn within this radius of center

        // Increment posIndex for the center position we're using
        posIndex++;

        for (int i = 0; i < groupSize; i++)
        {
            float x = centerX, y = centerY;  // Default to center position

            // First member (alpha) spawns at the given position
            if (i == 0)
            {
                x = centerX;
                y = centerY;
            }
            else
            {
                // Other members spawn nearby - try to find a spawnable position for this species
                bool found = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = (float)(_rng.NextDouble() * groupRadius);
                    x = centerX + MathF.Cos(angle) * dist;
                    y = centerY + MathF.Sin(angle) * dist;

                    if (_worldManager.IsSpawnableForSpecies(x, y, species))
                    {
                        found = true;
                        break;
                    }
                }

                // If we couldn't find a spawnable spot, use center
                if (!found)
                {
                    x = centerX;
                    y = centerY;
                }
            }

            // Spawn with group ID (alpha = first member, higher leadership)
            bool isAlpha = (i == 0);
            _entityFactory.SpawnCreature(x, y, species, groupId, false, isAlpha);
            spawned++;
        }

        return spawned;
    }

    /// <summary>
    /// Spawns Faeling crystals spread across the world on grass tiles.
    /// Crystal count is derived from population budget: each crystal eventually
    /// sustains ~8-12 Faelings, so crystalCount = budget / 10.
    /// </summary>
    public void SpawnCrystals(CrystalSystem crystalSystem, EntityManager entityManager, int populationBudget)
    {
        if (crystalSystem == null) return;

        // Each crystal sustains a small group of Faelings
        int crystalCount = Math.Max(1, populationBudget / 10);

        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);

        int spawned = 0;
        int chunkIndex = 0;

        while (spawned < crystalCount && chunkIndex < allChunks.Count)
        {
            var chunk = allChunks[chunkIndex++];
            var positions = _worldManager.GetSpawnablePositionsForSpecies(
                chunk, 4, _rng, SpeciesRegistry.Get("Faeling"));
            if (positions.Count == 0) continue;

            var (x, y, _, _) = positions[0];
            crystalSystem.SpawnCrystal(entityManager, x, y);
            spawned++;
        }

        GD.Print($"  Faeling crystals: {spawned}/{crystalCount} (from budget {populationBudget})");
    }

    /// <summary>
    /// Spawns initial Sectid nests on dry tiles with starter Sectids around each.
    /// Colony and nest counts are derived from population budget.
    /// Each nest gets ~5 starter Sectids, so nestCount = budget / 5.
    /// Colonies = ceil(nestCount / 2) to spread nests geographically.
    /// </summary>
    public void SpawnInitialNests(NestSystem nestSystem, EntityManager entityManager,
                                   int populationBudget)
    {
        if (nestSystem == null) return;

        // Derive counts from budget
        const int sectidsPerNest = 5;
        int totalNestTarget = Math.Max(1, populationBudget / sectidsPerNest);
        int colonyCount = Math.Max(1, (totalNestTarget + 1) / 2);
        int nestsPerColony = Math.Max(1, totalNestTarget / colonyCount);
        int sectidBudgetRemaining = populationBudget;

        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);
        var sectidDef = SpeciesRegistry.Get("Sectid");

        int totalNests = 0;
        int totalSectids = 0;
        int chunkIndex = 0;

        for (int colony = 0; colony < colonyCount; colony++)
        {
            int colonyId = colony + 1;
            int nestsThisColony = 0;

            while (nestsThisColony < nestsPerColony && chunkIndex < allChunks.Count)
            {
                var chunk = allChunks[chunkIndex++];
                var positions = _worldManager.GetSpawnablePositionsForSpecies(
                    chunk, 4, _rng, sectidDef);
                if (positions.Count == 0) continue;

                // Find a position away from water (no beach nests)
                int posIdx = -1;
                for (int p = 0; p < positions.Count; p++)
                {
                    if (!_worldManager.HasWaterNearby(positions[p].x, positions[p].y, 3))
                    {
                        posIdx = p;
                        break;
                    }
                }
                if (posIdx < 0) continue;

                var (x, y, _, _) = positions[posIdx];
                int nestEntity = nestSystem.SpawnNest(entityManager, x, y, colonyId);
                nestsThisColony++;
                totalNests++;

                // Spawn starter Sectids around each nest, respecting remaining budget
                int starterCount = Math.Min(sectidsPerNest, sectidBudgetRemaining);
                for (int i = 0; i < starterCount; i++)
                {
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = 2f + (float)(_rng.NextDouble() * 4f);
                    float sx = x + MathF.Cos(angle) * dist;
                    float sy = y + MathF.Sin(angle) * dist;

                    if (_worldManager.IsWalkable(sx, sy))
                    {
                        _entityFactory.SpawnCreature(sx, sy, sectidDef, colonyId);
                        totalSectids++;
                        sectidBudgetRemaining--;
                    }
                }
            }

            GD.Print($"  Colony {colonyId}: {nestsThisColony} nests");
        }

        GD.Print($"  Sectid nests: {totalNests}, starter Sectids: {totalSectids} (from budget {populationBudget})");
    }
}
