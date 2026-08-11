using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Handles reproduction for mature, well-fed entities.
/// </summary>
public sealed class ReproductionSystem : ISystem
{
    private readonly World.WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly int _maxPopulation;
    private readonly Random _rng = new();
    private readonly List<(float x, float y, SpeciesType speciesType, int speciesId, int groupId)> _toSpawn = new(32);
    private readonly List<int> _nearbyBuffer = new(64);

    private readonly EntityFactory _entityFactory;

    public ReproductionSystem(World.WorldManager worldManager, int maxPopulation,
                               SpatialHash spatialHash, EntityFactory entityFactory)
    {
        _worldManager = worldManager;
        _maxPopulation = maxPopulation;
        _spatialHash = spatialHash;
        _entityFactory = entityFactory;
    }

    public void Process(EntityManager em)
    {
        _toSpawn.Clear();

        // Don't reproduce if at population cap
        if (em.CreatureCount >= _maxPopulation)
            return;

        // Global population pressure: as population approaches cap, reproduction becomes
        // increasingly unlikely. This prevents local pockets from ignoring the global limit.
        float populationRatio = (float)em.CreatureCount / _maxPopulation;
        float globalPressure = 1f; // 1.0 = no suppression
        if (populationRatio > 0.5f)
        {
            // Linear ramp from 1.0 at 50% to 0.0 at 100%
            globalPressure = MathF.Max(0f, 2f * (1f - populationRatio));
        }

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species |
                                         ComponentFlags.Hunger | ComponentFlags.Energy |
                                         ComponentFlags.Age | ComponentFlags.Reproduction;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: cooldowns count down at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var reproduction = ref em.Reproductions[entity];

            // Reduce cooldown
            if (reproduction.CurrentCooldown > 0)
            {
                reproduction.CurrentCooldown -= tickMult;
                continue;
            }

            ref var age = ref em.Ages[entity];
            if (!age.IsMature)
                continue;

            ref var species = ref em.Species[entity];
            var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);

            // Skip faction species that reproduce via special systems
            if (speciesDef.NestBreeder || speciesDef.SporeReproducer || speciesDef.CrystalSpawned)
                continue;

            ref var hunger = ref em.Hungers[entity];
            ref var energy = ref em.Energies[entity];

            // Check thresholds
            if (hunger.Current < reproduction.HungerThreshold ||
                energy.Current < reproduction.EnergyThreshold)
                continue;

            // Check population cap again
            if (em.CreatureCount + _toSpawn.Count >= _maxPopulation)
                break;

            ref var pos = ref em.Positions[entity];

            // Species that must come ashore to breed (penguins hauling out onto the ice) wait
            // until they are standing on that ground. WanderSystem walks them there — see
            // SpeciesDefinition.BreedingTiles.
            if (!speciesDef.CanBreedOnTile(_worldManager.GetTile(pos.X, pos.Y)))
                continue;

            // Local density suppression — skip if too many same-species nearby
            // Prevents exponential population explosions in well-fed areas
            if (_spatialHash != null)
            {
                float densityRadius = speciesDef.SocialRadius > 0 ? speciesDef.SocialRadius * 1.5f : 15f;
                _nearbyBuffer.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, densityRadius, _nearbyBuffer);
                int sameSpeciesCount = 0;
                foreach (int other in _nearbyBuffer)
                {
                    if (other == entity || !em.IsAlive(other)) continue;
                    if (!em.HasComponents(other, ComponentFlags.Species)) continue;
                    if (em.Species[other].SpeciesId == species.SpeciesId)
                        sameSpeciesCount++;
                }
                // Suppress reproduction when local density exceeds 2× preferred group size
                float maxLocal = MathF.Max(6f, speciesDef.PreferredGroupSize * 2f);
                if (sameSpeciesCount >= (int)maxLocal)
                    continue;
            }

            // Global population pressure: randomly skip reproduction based on global fullness
            if (globalPressure < 1f && (float)_rng.NextDouble() > globalPressure)
                continue;

            // Find spawn position
            float spawnX = pos.X + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;
            float spawnY = pos.Y + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;

            // Only spawn on tiles valid for this species (aquatic spawn in water, etc.)
            if (!speciesDef.CanSpawnOnTile(_worldManager.GetTile(spawnX, spawnY)))
                continue;

            // Pay reproduction cost
            hunger.Current -= reproduction.HungerCost;
            energy.Current -= reproduction.EnergyCost;
            reproduction.CurrentCooldown = reproduction.Cooldown;

            EcosystemLogger.Instance?.LogReproduction(
                species.SpeciesId, entity, pos.X, pos.Y, reproduction.OffspringCount);

            // Queue offspring for spawning (inherit parent species)
            for (int i = 0; i < reproduction.OffspringCount; i++)
            {
                _toSpawn.Add((spawnX, spawnY, species.Type, species.SpeciesId,
                    em.HasComponents(entity, ComponentFlags.Social) ? em.Socials[entity].GroupId : -1));
            }
        }

        // Spawn offspring
        foreach (var (x, y, speciesType, speciesId, groupId) in _toSpawn)
        {
            if (em.CreatureCount >= _maxPopulation) break;
            SpawnCreature(em, x, y, speciesType, speciesId, groupId);
        }
    }

    /// <summary>
    /// Bring one offspring into the world. Delegates to EntityFactory so a creature born here is
    /// assembled exactly like one spawned at worldgen. This used to be a parallel copy of the
    /// assembly code, and it had drifted: newborns were created without TerrainDiscomfort, Fear,
    /// Social or Growth. The consequences were invisible in short tests but severe over a long
    /// run — born creatures could not drown, ignored terrain and grazing pressure, never built
    /// fear, and pack species could not be seen by pack coordination at all (it keys on Social),
    /// so an ageing wolf population lost the ability to hunt together as its founders died off.
    /// </summary>
    private void SpawnCreature(EntityManager em, float x, float y, SpeciesType speciesType,
        int speciesId, int groupId)
    {
        if (em.CreatureCount >= _maxPopulation) return;
        // Disabled species never spawn, even via reproduction (belt-and-suspenders: with no
        // initial population a disabled species can't reproduce anyway, but this keeps the
        // toggle a hard guarantee regardless of how offspring are queued).
        if (!SpeciesToggle.IsEnabled(speciesId)) return;

        var speciesDef = SpeciesRegistry.GetById(speciesId);
        if (speciesDef == null) return;

        // Offspring start at age 0 and inherit the parent's group so herds and packs stay whole
        // across generations instead of every newborn being an unaffiliated loner.
        _entityFactory.SpawnCreature(x, y, speciesDef, groupId, forceSolitary: false,
            isAlpha: false, startAge: 0);
    }
}
