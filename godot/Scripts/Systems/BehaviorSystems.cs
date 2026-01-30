using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Processes wandering behavior for entities.
/// LOD-aware: only processes entities due for update.
/// </summary>
public sealed class WanderSystem : ISystem
{
    private readonly Random _rng = new();

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Wander | ComponentFlags.Velocity;

        foreach (int entity in em.Query(required))
        {
            // Check LOD - skip if not due for update
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (!LODSystem.ShouldUpdate(in lod))
                    continue;
            }

            ref var wander = ref em.Wanders[entity];
            ref var vel = ref em.Velocities[entity];

            // Skip if fleeing
            if (em.HasComponents(entity, ComponentFlags.Prey) && em.Preys[entity].IsFleeing)
                continue;

            // Skip if hunting
            if (em.HasComponents(entity, ComponentFlags.Predator) && em.Predators[entity].HasTarget)
                continue;

            // Random direction change
            if (MathUtils.RandomFloat(_rng) < wander.ChangeDirectionChance)
            {
                wander.CurrentDirection = MathUtils.RandomDirection(_rng);
            }

            vel.Dx = wander.CurrentDirection.X * wander.Speed;
            vel.Dy = wander.CurrentDirection.Y * wander.Speed;
        }
    }
}

/// <summary>
/// Processes hunting behavior for predators using spatial hashing.
/// </summary>
public sealed class HuntingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly float _huntNutrition;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly List<int> _entitiesToKill = new(16);

    public HuntingSystem(SpatialHash spatialHash, float huntNutrition = 50f)
    {
        _spatialHash = spatialHash;
        _huntNutrition = huntNutrition;
    }

    public void Process(EntityManager em)
    {
        _entitiesToKill.Clear();

        // Update spatial hash for all prey
        const ComponentFlags preyRequired = ComponentFlags.Position | ComponentFlags.Prey;
        foreach (int entity in em.Query(preyRequired))
        {
            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Process predators
        const ComponentFlags predatorRequired = ComponentFlags.Position | ComponentFlags.Predator | ComponentFlags.Hunger;
        foreach (int entity in em.Query(predatorRequired))
        {
            ref var pos = ref em.Positions[entity];
            ref var predator = ref em.Predators[entity];
            ref var hunger = ref em.Hungers[entity];

            // Reduce cooldown
            if (predator.CurrentCooldown > 0)
                predator.CurrentCooldown--;

            // Clear target if it no longer exists
            if (predator.HasTarget && !em.IsAlive(predator.TargetEntity))
                predator.TargetEntity = -1;

            // Find nearest prey using spatial hash
            if (!predator.HasTarget)
            {
                float huntRangeSq = predator.HuntRange * predator.HuntRange;
                _spatialHash.QueryRadius(pos.X, pos.Y, predator.HuntRange, _nearbyEntities);

                float nearestDistSq = float.MaxValue;
                int nearestPrey = -1;

                foreach (int preyEntity in _nearbyEntities)
                {
                    if (!em.IsAlive(preyEntity) || !em.HasComponents(preyEntity, ComponentFlags.Prey))
                        continue;

                    ref var preyPos = ref em.Positions[preyEntity];
                    float distSq = MathUtils.DistanceSquared(pos.X, pos.Y, preyPos.X, preyPos.Y);

                    if (distSq < huntRangeSq && distSq < nearestDistSq)
                    {
                        nearestDistSq = distSq;
                        nearestPrey = preyEntity;
                    }
                }

                if (nearestPrey >= 0)
                    predator.TargetEntity = nearestPrey;
            }

            // Hunt the target
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity))
            {
                ref var preyPos = ref em.Positions[predator.TargetEntity];

                float dx = preyPos.X - pos.X;
                float dy = preyPos.Y - pos.Y;
                float distSq = dx * dx + dy * dy;

                // Close enough to attack?
                if (distSq < 1f && predator.CurrentCooldown == 0)
                {
                    if (em.HasComponents(predator.TargetEntity, ComponentFlags.Energy))
                    {
                        ref var preyEnergy = ref em.Energies[predator.TargetEntity];
                        preyEnergy.Current -= predator.AttackPower;

                        if (preyEnergy.IsDead)
                        {
                            _entitiesToKill.Add(predator.TargetEntity);
                            hunger.Current = MathF.Min(hunger.Max, hunger.Current + _huntNutrition);
                            predator.TargetEntity = -1;
                        }
                    }
                    predator.CurrentCooldown = predator.AttackCooldown;
                }
                else if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    // Move towards prey
                    ref var vel = ref em.Velocities[entity];
                    const float huntSpeed = 0.12f;
                    var dir = MathUtils.Normalize(dx, dy);
                    vel.Dx = dir.X * huntSpeed;
                    vel.Dy = dir.Y * huntSpeed;
                }
            }
        }

        // Kill dead prey
        foreach (int preyEntity in _entitiesToKill)
        {
            _spatialHash.Remove(preyEntity);
            em.DestroyEntity(preyEntity);
        }
    }
}

/// <summary>
/// Processes fleeing behavior for prey.
/// </summary>
public sealed class FleeingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;

    // Pre-allocated arrays for predator positions
    private float[] _predatorXs = new float[128];
    private float[] _predatorYs = new float[128];
    private int _predatorCount;

    public FleeingSystem(SpatialHash spatialHash)
    {
        _spatialHash = spatialHash;
    }

    public void Process(EntityManager em)
    {
        // Collect predator positions
        const ComponentFlags predatorRequired = ComponentFlags.Position | ComponentFlags.Predator;
        _predatorCount = 0;

        foreach (int entity in em.Query(predatorRequired))
        {
            // Resize arrays if needed
            if (_predatorCount >= _predatorXs.Length)
            {
                int newSize = _predatorXs.Length * 2;
                Array.Resize(ref _predatorXs, newSize);
                Array.Resize(ref _predatorYs, newSize);
            }

            ref var pos = ref em.Positions[entity];
            _predatorXs[_predatorCount] = pos.X;
            _predatorYs[_predatorCount] = pos.Y;
            _predatorCount++;

            // Update spatial hash for predators
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Process prey
        const ComponentFlags preyRequired = ComponentFlags.Position | ComponentFlags.Prey | ComponentFlags.Velocity | ComponentFlags.Wander;

        var predXSpan = _predatorXs.AsSpan(0, _predatorCount);
        var predYSpan = _predatorYs.AsSpan(0, _predatorCount);

        foreach (int entity in em.Query(preyRequired))
        {
            ref var pos = ref em.Positions[entity];
            ref var prey = ref em.Preys[entity];
            ref var vel = ref em.Velocities[entity];
            ref var wander = ref em.Wanders[entity];

            float fleeRangeSq = prey.FleeRange * prey.FleeRange;

            var (fleeDir, hasThreat) = MathUtils.CalculateFleeVector(
                pos.X, pos.Y,
                predXSpan, predYSpan,
                fleeRangeSq);

            if (hasThreat)
            {
                prey.IsFleeing = true;
                float fleeSpeed = wander.Speed * prey.FleeSpeedMultiplier;
                vel.Dx = fleeDir.X * fleeSpeed;
                vel.Dy = fleeDir.Y * fleeSpeed;
            }
            else
            {
                prey.IsFleeing = false;
            }
        }
    }
}

/// <summary>
/// Processes hunger decay and starvation damage.
/// </summary>
public sealed class HungerSystem : ISystem
{
    private readonly List<int> _toKill = new(32);

    public void Process(EntityManager em)
    {
        _toKill.Clear();
        const ComponentFlags required = ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            ref var hunger = ref em.Hungers[entity];

            // Decay hunger
            hunger.Current -= hunger.DecayRate;

            // Starvation damage
            if (hunger.IsStarving && em.HasComponents(entity, ComponentFlags.Energy))
            {
                ref var energy = ref em.Energies[entity];
                energy.Current -= hunger.StarvationDamage;

                if (energy.IsDead)
                    _toKill.Add(entity);
            }
        }

        foreach (int entity in _toKill)
        {
            em.DestroyEntity(entity);
        }
    }
}

/// <summary>
/// Processes aging and natural death.
/// </summary>
public sealed class AgingSystem : ISystem
{
    private readonly List<int> _toKill = new(32);

    public void Process(EntityManager em)
    {
        _toKill.Clear();
        const ComponentFlags required = ComponentFlags.Age;

        foreach (int entity in em.Query(required))
        {
            ref var age = ref em.Ages[entity];
            age.Current++;

            // Natural death from old age
            if (age.Current >= age.MaxLifespan)
                _toKill.Add(entity);
        }

        foreach (int entity in _toKill)
        {
            em.DestroyEntity(entity);
        }
    }
}

/// <summary>
/// Herbivores graze on grass/forest tiles to restore hunger.
/// </summary>
public sealed class GrazingSystem : ISystem
{
    private readonly World.WorldManager _worldManager;
    private readonly float _grazeRate;

    public GrazingSystem(World.WorldManager worldManager, float grazeRate = 0.5f)
    {
        _worldManager = worldManager;
        _grazeRate = grazeRate;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species | ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            ref var species = ref em.Species[entity];

            // Only herbivores graze
            if (species.Type != SpeciesType.Herbivore)
                continue;

            ref var pos = ref em.Positions[entity];
            ref var hunger = ref em.Hungers[entity];

            // Check tile type at entity position
            var tile = _worldManager.GetTile(pos.X, pos.Y);
            if (tile.IsGrazeable())
            {
                // Restore hunger (capped at max)
                hunger.Current = MathF.Min(hunger.Max, hunger.Current + _grazeRate);
            }
        }
    }
}

/// <summary>
/// Handles reproduction for mature, well-fed entities.
/// </summary>
public sealed class ReproductionSystem : ISystem
{
    private readonly World.WorldManager _worldManager;
    private readonly int _maxPopulation;
    private readonly Random _rng = new();
    private readonly List<(float x, float y, bool isHerbivore)> _toSpawn = new(32);

    public ReproductionSystem(World.WorldManager worldManager, int maxPopulation = 500)
    {
        _worldManager = worldManager;
        _maxPopulation = maxPopulation;
    }

    public void Process(EntityManager em)
    {
        _toSpawn.Clear();

        // Don't reproduce if at population cap
        if (em.EntityCount >= _maxPopulation)
            return;

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species |
                                         ComponentFlags.Hunger | ComponentFlags.Energy |
                                         ComponentFlags.Age | ComponentFlags.Reproduction;

        foreach (int entity in em.Query(required))
        {
            ref var reproduction = ref em.Reproductions[entity];

            // Reduce cooldown
            if (reproduction.CurrentCooldown > 0)
            {
                reproduction.CurrentCooldown--;
                continue;
            }

            ref var age = ref em.Ages[entity];
            if (!age.IsMature)
                continue;

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
            ref var species = ref em.Species[entity];

            // Find spawn position
            float spawnX = pos.X + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;
            float spawnY = pos.Y + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;

            // Only spawn on walkable tiles
            if (!_worldManager.IsWalkable(spawnX, spawnY))
                continue;

            // Pay reproduction cost
            hunger.Current -= reproduction.HungerCost;
            energy.Current -= reproduction.EnergyCost;
            reproduction.CurrentCooldown = reproduction.Cooldown;

            // Queue offspring for spawning
            bool isHerbivore = species.Type == SpeciesType.Herbivore;
            for (int i = 0; i < reproduction.OffspringCount; i++)
            {
                _toSpawn.Add((spawnX, spawnY, isHerbivore));
            }
        }

        // Spawn offspring
        foreach (var (x, y, isHerbivore) in _toSpawn)
        {
            SpawnCreature(em, x, y, isHerbivore);
        }
    }

    private void SpawnCreature(EntityManager em, float x, float y, bool isHerbivore)
    {
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.Ages[entity] = new Age(
            current: 0,
            maxLifespan: isHerbivore ? 30000 : 24000,
            maturityAge: isHerbivore ? 2000 : 1500
        );
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(80f);
        em.AddComponent(entity, ComponentFlags.Energy);

        em.Reproductions[entity] = new Reproduction(
            hungerThreshold: isHerbivore ? 70f : 75f,
            energyThreshold: isHerbivore ? 80f : 85f,
            cooldown: isHerbivore ? 600 : 800
        );
        em.AddComponent(entity, ComponentFlags.Reproduction);

        em.SimulationLODs[entity] = new SimulationLOD();
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        if (isHerbivore)
        {
            em.Species[entity] = new Species(SpeciesType.Herbivore);
            em.AddComponent(entity, ComponentFlags.Species);

            em.Hungers[entity] = new Hunger(60f, decayRate: 0.05f);
            em.AddComponent(entity, ComponentFlags.Hunger);

            em.Wanders[entity] = new Wander(0.03f, 0.005f);
            em.AddComponent(entity, ComponentFlags.Wander);

            em.Preys[entity] = new Prey(6f, 2f);
            em.AddComponent(entity, ComponentFlags.Prey);

            em.Renderables[entity] = new Renderable(
                new Color(0.4f, 1f, 0.4f), 8f, ShapeType.Circle);
            em.AddComponent(entity, ComponentFlags.Renderable);
        }
        else
        {
            em.Species[entity] = new Species(SpeciesType.Carnivore);
            em.AddComponent(entity, ComponentFlags.Species);

            em.Hungers[entity] = new Hunger(50f, decayRate: 0.08f);
            em.AddComponent(entity, ComponentFlags.Hunger);

            em.Wanders[entity] = new Wander(0.06f, 0.01f);
            em.AddComponent(entity, ComponentFlags.Wander);

            em.Predators[entity] = new Predator(12f, 30f);
            em.AddComponent(entity, ComponentFlags.Predator);

            em.Renderables[entity] = new Renderable(
                new Color(1f, 0.4f, 0.4f), 10f, ShapeType.Triangle);
            em.AddComponent(entity, ComponentFlags.Renderable);
        }
    }
}
