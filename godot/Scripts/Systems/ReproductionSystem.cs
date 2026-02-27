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
    private readonly List<(float x, float y, SpeciesType speciesType, int speciesId)> _toSpawn = new(32);
    private readonly List<int> _nearbyBuffer = new(64);

    public ReproductionSystem(World.WorldManager worldManager, int maxPopulation,
                               SpatialHash spatialHash)
    {
        _worldManager = worldManager;
        _maxPopulation = maxPopulation;
        _spatialHash = spatialHash;
    }

    public void Process(EntityManager em)
    {
        _toSpawn.Clear();

        // Don't reproduce if at population cap
        if (em.EntityCount >= _maxPopulation)
            return;

        // Global population pressure: as population approaches cap, reproduction becomes
        // increasingly unlikely. This prevents local pockets from ignoring the global limit.
        float populationRatio = (float)em.EntityCount / _maxPopulation;
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

            // Skip faction species that reproduce via special systems
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                if (speciesDef.NestBreeder || speciesDef.SporeReproducer || speciesDef.CrystalSpawned)
                    continue;
            }

            ref var hunger = ref em.Hungers[entity];
            ref var energy = ref em.Energies[entity];

            // Check thresholds
            if (hunger.Current < reproduction.HungerThreshold ||
                energy.Current < reproduction.EnergyThreshold)
                continue;

            // Check population cap again
            if (em.EntityCount + _toSpawn.Count >= _maxPopulation)
                break;

            ref var pos = ref em.Positions[entity];

            // Local density suppression — skip if too many same-species nearby
            // Prevents exponential population explosions in well-fed areas
            if (_spatialHash != null)
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
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
                _toSpawn.Add((spawnX, spawnY, species.Type, species.SpeciesId));
            }
        }

        // Spawn offspring
        foreach (var (x, y, speciesType, speciesId) in _toSpawn)
        {
            if (em.EntityCount >= _maxPopulation) break;
            SpawnCreature(em, x, y, speciesType, speciesId);
        }
    }

    private void SpawnCreature(EntityManager em, float x, float y, SpeciesType speciesType, int speciesId)
    {
        if (em.EntityCount >= _maxPopulation) return;
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD();
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        // Use SpeciesRegistry to inherit parent stats where possible
        var speciesDef = SpeciesRegistry.GetById(speciesId);

        em.Species[entity] = new Species(speciesType, 0, speciesId);
        em.AddComponent(entity, ComponentFlags.Species);

        em.Ages[entity] = new Age(
            current: 0,
            maxLifespan: (int)speciesDef.MaxLifespan,
            maturityAge: (int)speciesDef.MaturityAge
        );
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        em.Hungers[entity] = new Hunger(
            current: speciesDef.MaxHunger * 0.6f,
            max: speciesDef.MaxHunger,
            decayRate: speciesDef.HungerDecayRate
        );
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Reproductions[entity] = new Reproduction(
            hungerThreshold: speciesDef.ReproHungerThreshold,
            energyThreshold: speciesDef.ReproEnergyThreshold,
            hungerCost: speciesDef.ReproHungerCost,
            energyCost: speciesDef.ReproEnergyCost,
            cooldown: (int)speciesDef.ReproCooldown,
            offspringCount: speciesDef.OffspringCount,
            spawnRadius: speciesDef.SpawnRadius
        );
        em.AddComponent(entity, ComponentFlags.Reproduction);

        em.Wanders[entity] = new Wander(
            speed: speciesDef.BaseWanderSpeed,
            changeDirectionChance: speciesDef.DirectionChangeChance
        );
        em.AddComponent(entity, ComponentFlags.Wander);

        em.Renderables[entity] = new Renderable(
            speciesDef.BaseColor, speciesDef.BaseSize, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Prey behavior (herbivores and faction species are prey)
        if (speciesDef.IsPrey)
        {
            em.Preys[entity] = new Prey(speciesDef.FleeRange, speciesDef.FleeSpeedMultiplier);
            em.AddComponent(entity, ComponentFlags.Prey);
        }

        // Predator behavior
        if (speciesDef.IsPredator)
        {
            em.Predators[entity] = new Predator(
                speciesDef.HuntRange, speciesDef.AttackRange, speciesDef.AttackPower,
                (int)speciesDef.AttackCooldown);
            em.AddComponent(entity, ComponentFlags.Predator);
        }

        // Terraform for faction species
        if (speciesDef.Diet == DietType.Terraformer)
        {
            em.Terraforms[entity] = new Terraform(
                direction: speciesDef.TerraformDir,
                radius: speciesDef.TerraformRadius,
                strength: speciesDef.TerraformStrength,
                cooldown: speciesDef.TerraformCooldown
            );
            em.AddComponent(entity, ComponentFlags.Terraform);
        }
    }
}
