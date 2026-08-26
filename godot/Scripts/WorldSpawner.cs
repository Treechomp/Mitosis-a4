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

    /// <summary>
    /// Seed the world with explicit per-class targets.
    ///
    /// The caller derives these from the class BUDGETS (see PopulationBudget.SplitSeed) rather
    /// than from a separate composition ratio. This used to take one `herbivoreRatio` of 0.85 and
    /// carve a terraformer share out of what was left, which put the starting composition and the
    /// composition the world could actually sustain in disagreement — a disagreement that the
    /// global birth ramp then resolved by selecting for whichever species bred fastest.
    ///
    /// Classification here (herbivore / predator / terraformer) is the SpeciesRegistry's diet
    /// split, which is not quite PopulationBudget's: the budget charges Boar and Penguin to the
    /// prey base and only Otter to the hunters. The seed being slightly off the budget's own line
    /// is harmless — it is a starting point, and the budget is what holds thereafter.
    /// </summary>
    public int SpawnCreatures(int herbivoreTarget, int predatorTarget, int terraformerTarget)
    {
        int spawned = 0;

        // Collect all species by category, honouring the per-species enable/disable toggles.
        // Sectids spawn from nests, Faelings from crystals — only Shroomers use normal spawning.
        var herbivoreSpecies = new List<SpeciesDefinition>();
        foreach (var s in SpeciesRegistry.GetHerbivores())
            if (SpeciesToggle.IsEnabled(s)) herbivoreSpecies.Add(s);
        var predatorSpecies = new List<SpeciesDefinition>();
        foreach (var s in SpeciesRegistry.GetPredators())
            if (SpeciesToggle.IsEnabled(s)) predatorSpecies.Add(s);
        var terraformerSpecies = new List<SpeciesDefinition>();
        foreach (var tf in SpeciesRegistry.GetTerraformers())
        {
            if (!tf.NestBreeder && !tf.CrystalSpawned && SpeciesToggle.IsEnabled(tf))
                terraformerSpecies.Add(tf); // Only Shroomers
        }

        int targetHerbivores = Math.Max(0, herbivoreTarget);
        int targetPredators = predatorSpecies.Count > 0 ? Math.Max(0, predatorTarget) : 0;
        int targetTerraformers = terraformerSpecies.Count > 0 ? Math.Max(0, terraformerTarget) : 0;
        int initialPopulation = targetHerbivores + targetPredators + targetTerraformers;

        GD.Print($"Spawn targets: {targetHerbivores} herbivores, {targetPredators} predators, {targetTerraformers} terraformers (total {initialPopulation})");

        // Get all chunks and shuffle
        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);

        // Scale position sampling based on target size
        int baseSamples = Math.Max(6, initialPopulation / Math.Max(1, allChunks.Count) + 2);

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
    /// Seed one mycelium heart per initial Shroomer cluster, so the faction starts a run with
    /// territory to defend rather than waiting for a Shroomer to reach founding scale — which,
    /// at a growth rate of 8e-5 per tick, is several thousand ticks of a world where one faction
    /// has nothing that can be taken from it.
    ///
    /// Placement follows the Shroomers that were actually spawned: a heart is the centre of a
    /// bloom, so it goes where the bodies are. Spacing is the heart's own MyceliumRadius, matching
    /// the one-per-radius rule MyceliumSystem enforces when a bloom founds its own.
    /// </summary>
    public void SpawnMyceliumHearts(EntityManager entityManager)
    {
        var shroomerDef = SpeciesRegistry.Get("Shroomer");
        if (shroomerDef == null || shroomerDef.MyceliumRadius <= 0f) return;
        int shroomerId = SpeciesRegistry.GetId("Shroomer");
        if (!SpeciesToggle.IsEnabled(shroomerId))
        {
            GD.Print("  Mycelium hearts: disabled (species toggle)");
            return;
        }

        // Collect the Shroomers once, then seed only where a heart could actually LIVE.
        //
        // Seeding one per cluster without checking was wrong, and the whole-game invariant is what
        // caught it: a heart needs more than MyceliumShroomerFloor Shroomers inside its radius to
        // survive, the initial spawn scatters Shroomers thinly, and 76 of 85 seeded hearts duly
        // bled out by t=819 with cause `environment`. Seeding an anchor that dies on its own in
        // 800 ticks is worse than seeding none: it looks like the faction is being destroyed.
        var positions = new List<(float x, float y)>();
        foreach (int entity in entityManager.Query(ComponentFlags.Position | ComponentFlags.Species))
        {
            if (entityManager.HasComponents(entity, ComponentFlags.Spore)) continue;
            if (entityManager.HasComponents(entity, ComponentFlags.Structure)) continue;
            if (entityManager.Species[entity].SpeciesId != shroomerId) continue;
            ref var p = ref entityManager.Positions[entity];
            positions.Add((p.X, p.Y));
        }

        var placed = new List<(float x, float y)>();
        float spacingSq = shroomerDef.MyceliumRadius * shroomerDef.MyceliumRadius;
        int hearts = 0;

        foreach (var (x, y) in positions)
        {
            bool tooClose = false;
            foreach (var (px, py) in placed)
            {
                float dx = px - x, dy = py - y;
                if (dx * dx + dy * dy < spacingSq) { tooClose = true; break; }
            }
            if (tooClose) continue;

            // Both floors, via the same test MyceliumSystem applies when a bloom founds its own.
            // The moisture floor is the binding one at worldgen: a heart needs 35% of its
            // territory above the fungal threshold, and natural grassland is below it — so the
            // faction starts anchored only in genuinely swampy country and earns the rest by
            // converting ground, which is what a fungal faction should have to do.
            if (!Systems.MyceliumSystem.TerritorySupportsHeart(
                    _worldManager, _worldManager.SpatialHash, entityManager,
                    x, y, shroomerDef, shroomerId))
                continue;

            if (Systems.MyceliumSystem.SpawnHeart(entityManager, x, y, shroomerId) >= 0)
            {
                placed.Add((x, y));
                hearts++;
            }
        }

        GD.Print($"  Mycelium hearts: {hearts} of {positions.Count} Shroomers " +
                 $"(one per {shroomerDef.MyceliumRadius:F0}-tile territory holding " +
                 $"more than {shroomerDef.MyceliumShroomerFloor})");
    }

    /// <summary>
    /// Spawns Faeling crystals spread across the world on grass tiles.
    ///
    /// Crystal count is an EXPLICIT DESIGNED CONSTANT, because a crystal holds exactly ONE
    /// Faeling: <c>CrystalSystem</c> links one to each and respawns it when it dies, so the
    /// crystal count IS the Faeling population.
    ///
    /// It has been wrong in both directions. The original formula assumed one crystal sustained
    /// 8-12 Faelings and divided by ten a number that should have been multiplied by one, giving
    /// EIGHT crystals for a 1152x1152 world — the reason the faction never functioned. The fix
    /// then derived it from SENSE COVERAGE (f*A/(pi*r^2), half the map at r=40 → 132 crystals),
    /// which solved sensing by adding presence — and presence is also FORCE. Every
    /// structure-combat term scaled 16x as a side effect: 132 immortal raiders with
    /// StructureAttackPower 22 and StructureAggression 4.0 converged on far fewer than 132
    /// structures from t=0, and a 6,000-tick run lost 44 of 46 Sectid nests and 21 colonies, the
    /// first at t=951.
    ///
    /// The lesson is that a derived count silently converts a perception fix into an army. A
    /// designed faction size is a number someone chose; a formula output is a number nobody owns.
    /// Reach — getting a keeper to the problem — is now a MOBILITY question (CrystalSystem's
    /// crystal-to-crystal travel) and awareness is a CENSUS question (FactionCensus), neither of
    /// which needs more bodies.
    /// </summary>
    public void SpawnCrystals(CrystalSystem crystalSystem, EntityManager entityManager,
                               bool faelingEnabled, PopulationBudget? budget, int crystalCount)
    {
        if (crystalSystem == null) return;
        if (!faelingEnabled || !SpeciesToggle.IsEnabled(SpeciesRegistry.GetId("Faeling")))
        {
            GD.Print("  Faeling crystals: disabled (species toggle)");
            return;
        }

        var faelingDef = SpeciesRegistry.Get("Faeling");
        float senseRadius = faelingDef.KeeperSenseRadius > 0f ? faelingDef.KeeperSenseRadius : 40f;
        double worldTiles = (double)_worldManager.WorldSizeTiles * _worldManager.WorldSizeTiles;
        double perCrystal = Math.PI * senseRadius * senseRadius;
        crystalCount = Math.Max(1, crystalCount);

        // Never allocate more Faeling slots than the faction class can hold — a crystal whose
        // Faeling can never spawn is a structure that does nothing.
        if (budget != null)
            crystalCount = Math.Min(crystalCount, Math.Max(1, budget.BudgetFor(PopClass.Faction)));

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

        // Coverage is reported, not targeted: it is the number that says how much the keepers
        // must cover by TRAVELLING rather than by standing there.
        double coverage = spawned * perCrystal / worldTiles;
        GD.Print($"  Faeling crystals: {spawned}/{crystalCount} " +
                 $"(static sense coverage {coverage:P0} at r={senseRadius:F0}; the rest is travel)");
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
        if (!SpeciesToggle.IsEnabled(SpeciesRegistry.GetId("Sectid")))
        {
            GD.Print("  Sectid nests: disabled (species toggle)");
            return;
        }

        // Derive counts from budget. Denser starter swarms (matching PreferredGroupSize) so a
        // colony can immediately field a kill-capable swarm instead of scattered individuals.
        const int sectidsPerNest = 8;
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

                    if (_worldManager.IsSpawnable(sx, sy))
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
