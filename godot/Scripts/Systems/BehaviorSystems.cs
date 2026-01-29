using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Processes wandering behavior for entities.
/// </summary>
public sealed class WanderSystem : ISystem
{
    private readonly Random _rng = new();

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Wander | ComponentFlags.Velocity;

        foreach (int entity in em.Query(required))
        {
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
