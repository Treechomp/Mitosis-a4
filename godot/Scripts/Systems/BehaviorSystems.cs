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
/// Includes terrain avoidance to steer away from dangerous tiles.
/// </summary>
public sealed class WanderSystem : ISystem
{
    private readonly Random _rng = new();
    private readonly WorldManager? _worldManager;
    private readonly float _lookAheadDistance;

    public WanderSystem(WorldManager? worldManager = null, float lookAheadDistance = 1.5f)
    {
        _worldManager = worldManager;
        _lookAheadDistance = lookAheadDistance;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Wander | ComponentFlags.Velocity | ComponentFlags.Position;

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
            ref var pos = ref em.Positions[entity];

            // Skip if fleeing
            if (em.HasComponents(entity, ComponentFlags.Prey) && em.Preys[entity].IsFleeing)
                continue;

            // Skip if hunting
            if (em.HasComponents(entity, ComponentFlags.Predator) && em.Predators[entity].HasTarget)
                continue;

            // Terrain danger avoidance
            if (_worldManager != null)
            {
                var avoidance = GetTerrainAvoidance(pos.X, pos.Y, wander.CurrentDirection);
                if (avoidance.LengthSquared() > 0.01f)
                {
                    // Blend avoidance with current direction
                    wander.CurrentDirection = (wander.CurrentDirection + avoidance * 2f).Normalized();
                }
            }

            // Random direction change
            if (MathUtils.RandomFloat(_rng) < wander.ChangeDirectionChance)
            {
                var newDir = MathUtils.RandomDirection(_rng);

                // If we have world manager, prefer safe directions
                if (_worldManager != null)
                {
                    float aheadX = pos.X + newDir.X * _lookAheadDistance;
                    float aheadY = pos.Y + newDir.Y * _lookAheadDistance;
                    var aheadTile = _worldManager.GetTile(aheadX, aheadY);

                    // If new direction leads to danger, try to pick a safer one
                    if (aheadTile.GetDangerLevel() > 0.3f)
                    {
                        // Try a few random directions and pick safest
                        float bestDanger = aheadTile.GetDangerLevel();
                        var bestDir = newDir;

                        for (int i = 0; i < 3; i++)
                        {
                            var testDir = MathUtils.RandomDirection(_rng);
                            float testX = pos.X + testDir.X * _lookAheadDistance;
                            float testY = pos.Y + testDir.Y * _lookAheadDistance;
                            float danger = _worldManager.GetTile(testX, testY).GetDangerLevel();

                            if (danger < bestDanger)
                            {
                                bestDanger = danger;
                                bestDir = testDir;
                            }
                        }
                        newDir = bestDir;
                    }
                }

                wander.CurrentDirection = newDir;
            }

            vel.Dx = wander.CurrentDirection.X * wander.Speed;
            vel.Dy = wander.CurrentDirection.Y * wander.Speed;
        }
    }

    /// <summary>
    /// Calculate avoidance vector based on nearby terrain danger.
    /// </summary>
    private Vector2 GetTerrainAvoidance(float x, float y, Vector2 currentDir)
    {
        if (_worldManager == null)
            return Vector2.Zero;

        float avoidX = 0f;
        float avoidY = 0f;

        // Sample tiles in the direction of movement
        float aheadX = x + currentDir.X * _lookAheadDistance;
        float aheadY = y + currentDir.Y * _lookAheadDistance;

        var aheadTile = _worldManager.GetTile(aheadX, aheadY);
        float aheadDanger = aheadTile.GetDangerLevel();

        if (aheadDanger > 0.2f)
        {
            // Push back from danger
            avoidX -= currentDir.X * aheadDanger;
            avoidY -= currentDir.Y * aheadDanger;
        }

        // Also check perpendicular directions for a better path
        float perpX = -currentDir.Y;
        float perpY = currentDir.X;

        float leftX = x + perpX * _lookAheadDistance;
        float leftY = y + perpY * _lookAheadDistance;
        float rightX = x - perpX * _lookAheadDistance;
        float rightY = y - perpY * _lookAheadDistance;

        float leftDanger = _worldManager.GetTile(leftX, leftY).GetDangerLevel();
        float rightDanger = _worldManager.GetTile(rightX, rightY).GetDangerLevel();

        // Steer toward less dangerous side
        if (leftDanger < rightDanger)
        {
            avoidX += perpX * (rightDanger - leftDanger);
            avoidY += perpY * (rightDanger - leftDanger);
        }
        else if (rightDanger < leftDanger)
        {
            avoidX -= perpX * (leftDanger - rightDanger);
            avoidY -= perpY * (leftDanger - rightDanger);
        }

        return new Vector2(avoidX, avoidY);
    }
}

/// <summary>
/// Processes hunting behavior for predators using spatial hashing.
/// Hunger-driven: predators only hunt when hungry, with stats scaling based on hunger level.
/// </summary>
public sealed class HuntingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly float _huntNutrition;
    private readonly float _huntThreshold;      // Start hunting below this hunger %
    private readonly float _baseHuntSpeed;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly List<int> _entitiesToKill = new(16);

    public HuntingSystem(SpatialHash spatialHash, float huntNutrition = 50f,
                         float huntThreshold = 0.7f, float baseHuntSpeed = 0.10f)
    {
        _spatialHash = spatialHash;
        _huntNutrition = huntNutrition;
        _huntThreshold = huntThreshold;  // Hunt when hunger falls below 70%
        _baseHuntSpeed = baseHuntSpeed;
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

            // Calculate hunger ratio (0 = starving, 1 = full)
            float hungerRatio = hunger.Current / hunger.Max;

            // Only hunt when hungry enough (below threshold)
            // Also abandon hunt if we become satiated
            if (hungerRatio >= _huntThreshold)
            {
                predator.TargetEntity = -1;  // Stop hunting, we're full
                continue;
            }

            // Calculate hunger-based modifiers
            // urgency: 0 at threshold, 1 at starving
            float urgency = 1f - (hungerRatio / _huntThreshold);

            // Hunt range increases with hunger (up to 1.5x when desperate)
            float rangeMultiplier = 1f + (urgency * 0.5f);

            // Speed scales with hunger:
            // - Moderate hunger (0.3-0.7): faster (adrenaline)
            // - Very low hunger (<0.15): slower (exhaustion)
            float speedMultiplier;
            if (hungerRatio < 0.15f)
            {
                // Exhaustion: speed drops sharply
                speedMultiplier = 0.5f + hungerRatio * 2f;  // 0.5 to 0.8
            }
            else
            {
                // Hungry but not exhausted: speed boost
                speedMultiplier = 1f + (urgency * 0.4f);  // 1.0 to 1.4
            }

            // Find nearest prey using spatial hash
            if (!predator.HasTarget)
            {
                float effectiveRange = predator.HuntRange * rangeMultiplier;
                float huntRangeSq = effectiveRange * effectiveRange;
                _spatialHash.QueryRadius(pos.X, pos.Y, effectiveRange, _nearbyEntities);

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
                float attackRangeSq = predator.AttackRange * predator.AttackRange;
                if (distSq < attackRangeSq && predator.CurrentCooldown == 0)
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
                    // Move towards prey with hunger-scaled speed
                    ref var vel = ref em.Velocities[entity];
                    float huntSpeed = _baseHuntSpeed * speedMultiplier;
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
/// Applies separation forces to prevent entities from clustering.
/// </summary>
public sealed class SeparationSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly float _separationRadius;
    private readonly float _separationStrength;

    public SeparationSystem(SpatialHash spatialHash, float separationRadius = 2f, float separationStrength = 0.02f)
    {
        _spatialHash = spatialHash;
        _separationRadius = separationRadius;
        _separationStrength = separationStrength;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity | ComponentFlags.Species;

        // First pass: update spatial hash positions for all creatures
        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Second pass: apply separation forces
        foreach (int entity in em.Query(required))
        {
            // Check LOD - skip if not due for update
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (!LODSystem.ShouldUpdate(in lod))
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];
            ref var species = ref em.Species[entity];

            // Query nearby entities
            _spatialHash.QueryRadius(pos.X, pos.Y, _separationRadius, _nearbyEntities);

            float separationX = 0f;
            float separationY = 0f;
            int neighborCount = 0;

            foreach (int other in _nearbyEntities)
            {
                if (other == entity || !em.IsAlive(other))
                    continue;

                // Only separate from same species
                if (!em.HasComponents(other, ComponentFlags.Species))
                    continue;

                ref var otherSpecies = ref em.Species[other];
                if (otherSpecies.Type != species.Type)
                    continue;

                ref var otherPos = ref em.Positions[other];
                float dx = pos.X - otherPos.X;
                float dy = pos.Y - otherPos.Y;
                float distSq = dx * dx + dy * dy;

                if (distSq > 0.001f && distSq < _separationRadius * _separationRadius)
                {
                    float dist = MathF.Sqrt(distSq);
                    float factor = 1f - (dist / _separationRadius); // Stronger when closer
                    separationX += (dx / dist) * factor;
                    separationY += (dy / dist) * factor;
                    neighborCount++;
                }
            }

            // Apply separation force
            if (neighborCount > 0)
            {
                separationX /= neighborCount;
                separationY /= neighborCount;

                vel.Dx += separationX * _separationStrength;
                vel.Dy += separationY * _separationStrength;
            }
        }
    }
}

/// <summary>
/// Hard collision resolution to prevent entities from overlapping.
/// Runs after movement to resolve any overlaps.
/// </summary>
public sealed class CollisionSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly float _collisionRadiusScale;
    private readonly float _tileSize;

    public CollisionSystem(SpatialHash spatialHash, float collisionRadiusScale = 0.5f, float tileSize = 16f)
    {
        _spatialHash = spatialHash;
        _collisionRadiusScale = collisionRadiusScale;
        _tileSize = tileSize; // Convert pixel sizes to tile/world units
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

        // Resolve collisions - multiple passes for better resolution
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (int entity in em.Query(required))
            {
                ref var pos = ref em.Positions[entity];
                ref var rend = ref em.Renderables[entity];
                // Convert pixel size to tile units
                float radius = (rend.Size * _collisionRadiusScale) / _tileSize;

                // Update spatial hash position
                _spatialHash.Update(entity, pos.X, pos.Y);

                // Query nearby entities (search radius in tile units)
                _spatialHash.QueryRadius(pos.X, pos.Y, radius * 4f, _nearbyEntities);

                foreach (int other in _nearbyEntities)
                {
                    if (other <= entity || !em.IsAlive(other))
                        continue;

                    if (!em.HasComponents(other, ComponentFlags.Position | ComponentFlags.Renderable))
                        continue;

                    ref var otherPos = ref em.Positions[other];
                    ref var otherRend = ref em.Renderables[other];
                    float otherRadius = (otherRend.Size * _collisionRadiusScale) / _tileSize;

                    float dx = pos.X - otherPos.X;
                    float dy = pos.Y - otherPos.Y;
                    float distSq = dx * dx + dy * dy;
                    float minDist = radius + otherRadius;
                    float minDistSq = minDist * minDist;

                    // Check for overlap
                    if (distSq < minDistSq && distSq > 0.0001f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        float overlap = minDist - dist;

                        // Normalize direction
                        float nx = dx / dist;
                        float ny = dy / dist;

                        // Push both entities apart (half the overlap each)
                        float push = overlap * 0.5f;
                        pos.X += nx * push;
                        pos.Y += ny * push;
                        otherPos.X -= nx * push;
                        otherPos.Y -= ny * push;
                    }
                }
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

/// <summary>
/// Applies herding/pack cohesion and alignment behaviors to social creatures.
/// Uses spatial hash for efficient neighbor queries.
/// </summary>
public sealed class HerdingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly float _socialRadius;
    private readonly List<int> _nearbyEntities = new(64);
    private int _nextGroupId = 1;

    public HerdingSystem(SpatialHash spatialHash, float socialRadius = 8f)
    {
        _spatialHash = spatialHash;
        _socialRadius = socialRadius;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity |
                                        ComponentFlags.Species | ComponentFlags.Social;

        // First pass: Update spatial hash and assign groups
        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Second pass: Apply social behaviors
        foreach (int entity in em.Query(required))
        {
            // Check LOD - skip if not due for update
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (!LODSystem.ShouldUpdate(in lod))
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];
            ref var species = ref em.Species[entity];
            ref var social = ref em.Socials[entity];

            // Skip non-social types
            if (!social.IsSocial)
                continue;

            // Query nearby entities
            _spatialHash.QueryRadius(pos.X, pos.Y, _socialRadius, _nearbyEntities);

            // Calculate group center and average velocity
            float centerX = 0f, centerY = 0f;
            float avgVelX = 0f, avgVelY = 0f;
            int neighborCount = 0;
            int groupId = social.GroupId;

            foreach (int other in _nearbyEntities)
            {
                if (other == entity || !em.IsAlive(other))
                    continue;

                // Must be same species and social
                if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Social))
                    continue;

                ref var otherSpecies = ref em.Species[other];
                ref var otherSocial = ref em.Socials[other];

                if (otherSpecies.Type != species.Type || !otherSocial.IsSocial)
                    continue;

                ref var otherPos = ref em.Positions[other];
                ref var otherVel = ref em.Velocities[other];

                centerX += otherPos.X;
                centerY += otherPos.Y;
                avgVelX += otherVel.Dx;
                avgVelY += otherVel.Dy;
                neighborCount++;

                // Adopt group ID from neighbors if we don't have one
                if (groupId < 0 && otherSocial.GroupId >= 0)
                    groupId = otherSocial.GroupId;
            }

            // Assign new group if we found neighbors but no group exists
            if (neighborCount > 0 && groupId < 0)
            {
                groupId = _nextGroupId++;
            }
            social.GroupId = groupId;

            // Apply social forces if we have neighbors
            if (neighborCount > 0)
            {
                centerX /= neighborCount;
                centerY /= neighborCount;
                avgVelX /= neighborCount;
                avgVelY /= neighborCount;

                // Cohesion: move toward group center
                float cohesionX = (centerX - pos.X) * social.CohesionStrength * social.GroupAffinity;
                float cohesionY = (centerY - pos.Y) * social.CohesionStrength * social.GroupAffinity;

                // Alignment: match group velocity
                float alignX = (avgVelX - vel.Dx) * social.AlignmentStrength * social.GroupAffinity;
                float alignY = (avgVelY - vel.Dy) * social.AlignmentStrength * social.GroupAffinity;

                // Apply forces
                vel.Dx += cohesionX + alignX;
                vel.Dy += cohesionY + alignY;
            }
            else
            {
                // No neighbors - clear group if we're alone for too long
                // (simplified: just clear immediately for now)
                social.GroupId = -1;
            }

            // Update leadership score based on age (if available)
            if (em.HasComponents(entity, ComponentFlags.Age))
            {
                ref var age = ref em.Ages[entity];
                social.LeadershipScore = (float)age.Current / age.MaxLifespan;
            }
        }
    }
}
