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
/// Manages Sectid nests: food accumulation, larvae spawning, nest growth,
/// colony tracking, and expedition founding.
///
/// Nest lifecycle:
/// - Sectids deliver food → nest stores it
/// - When food >= threshold, start growing larvae in available slot
/// - After spawn duration, new Sectid hatches
/// - Every 3 spawns, nest upgrades (more larvae slots)
/// - Max stage nest (3 slots) → try founding new nest nearby
/// - 5+ nests in proximity = colony → triggers long-distance expedition
/// </summary>
public sealed class NestSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly Random _rng = new();
    private readonly List<int> _nearbyBuffer = new(64);

    // Spawn tracking
    private readonly List<(float x, float y, int colonyId)> _pendingSpawns = new(16);
    private readonly List<(float x, float y, int colonyId)> _pendingNests = new(4);
    private int _nextColonyId = 1;
    private readonly int _maxPopulation;

    public NestSystem(WorldManager worldManager, SpatialHash spatialHash, int maxPopulation)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
        _maxPopulation = maxPopulation;
    }

    public void Process(EntityManager em)
    {
        _pendingSpawns.Clear();
        _pendingNests.Clear();

        // Get Sectid species def for nest parameters
        var sectidDef = SpeciesRegistry.Get("Sectid");

        const ComponentFlags nestRequired = ComponentFlags.Position | ComponentFlags.Nest |
                                            ComponentFlags.Energy | ComponentFlags.Renderable;

        foreach (int entity in em.Query(nestRequired))
        {
            ref var nest = ref em.Nests[entity];
            ref var energy = ref em.Energies[entity];
            ref var pos = ref em.Positions[entity];

            // === LARVAE PROCESSING ===
            // Check each larvae slot (up to nest.Stage slots active)
            ProcessLarvaeSlot(ref nest.SpawnTimer0, ref nest, ref pos, em, entity);
            if (nest.Stage >= 2)
                ProcessLarvaeSlot(ref nest.SpawnTimer1, ref nest, ref pos, em, entity);
            if (nest.Stage >= 3)
                ProcessLarvaeSlot(ref nest.SpawnTimer2, ref nest, ref pos, em, entity);

            // === FOOD → LARVAE CONVERSION ===
            // Try to fill empty larvae slots with food
            if (nest.FoodStored >= nest.FoodPerSpawn)
            {
                if (TryStartLarvae(ref nest))
                    nest.FoodStored -= nest.FoodPerSpawn;
            }

            // === STAGE ADVANCEMENT ===
            if (nest.SpawnsThisStage >= 3 && !nest.IsMaxStage)
            {
                nest.Stage++;
                nest.SpawnsThisStage = 0;

                // Visual growth
                ref var rend = ref em.Renderables[entity];
                rend.Size = 6f + nest.Stage * 3f; // Grows visually with stage
            }

            // === MAX STAGE: TRY FOUNDING NEW NEST ===
            if (nest.IsMaxStage && nest.FoodStored >= nest.FoodPerSpawn * 2f)
            {
                // Count nearby nests for colony check
                int nearbyNests = CountNearbyNests(em, pos.X, pos.Y, sectidDef.NestColonyRadius, entity);

                if (nearbyNests < sectidDef.NestsForExpedition)
                {
                    // Found new nest nearby
                    TryFoundNest(pos.X, pos.Y, sectidDef.NestSearchRadius, nest.ColonyId);
                }
                else
                {
                    // Colony full — expedition to found distant colony
                    TryFoundNest(pos.X, pos.Y, sectidDef.ExpeditionDistance, _nextColonyId++);
                }

                if (_pendingNests.Count > 0)
                    nest.FoodStored -= nest.FoodPerSpawn * 2f;
            }
        }

        // === SECTID LIFE (food delivery + hibernation/waking) ===
        ProcessSectids(em);

        // === SPAWN PENDING SECTIDS (respect population cap) ===
        var sectidSpeciesId = SpeciesRegistry.GetId("Sectid");
        foreach (var (x, y, colonyId) in _pendingSpawns)
        {
            if (em.EntityCount >= _maxPopulation) break;
            SpawnSectid(em, x, y, colonyId);
            EcosystemLogger.Instance?.LogReproduction(sectidSpeciesId, -1, x, y, 1);
        }

        // === SPAWN PENDING NESTS (respect population cap) ===
        foreach (var (x, y, colonyId) in _pendingNests)
        {
            if (em.EntityCount >= _maxPopulation) break;
            SpawnNest(em, x, y, colonyId);
        }
    }

    private void ProcessLarvaeSlot(ref float timer, ref Nest nest, ref Position pos,
                                    EntityManager em, int nestEntity)
    {
        if (timer < 0f) return; // Empty slot

        timer--;
        if (timer <= 0f)
        {
            // Larvae ready — spawn Sectid
            float offsetX = ((float)_rng.NextDouble() * 2f - 1f) * 3f;
            float offsetY = ((float)_rng.NextDouble() * 2f - 1f) * 3f;
            float spawnX = pos.X + offsetX;
            float spawnY = pos.Y + offsetY;

            if (_worldManager.IsSpawnable(spawnX, spawnY))
            {
                _pendingSpawns.Add((spawnX, spawnY, nest.ColonyId));
                nest.SpawnsThisStage++;
            }
            timer = -1f; // Clear slot
        }
    }

    private bool TryStartLarvae(ref Nest nest)
    {
        // Fill first empty slot
        if (nest.SpawnTimer0 < 0f) { nest.SpawnTimer0 = nest.SpawnDuration; return true; }
        if (nest.Stage >= 2 && nest.SpawnTimer1 < 0f) { nest.SpawnTimer1 = nest.SpawnDuration; return true; }
        if (nest.Stage >= 3 && nest.SpawnTimer2 < 0f) { nest.SpawnTimer2 = nest.SpawnDuration; return true; }
        return false;
    }

    private int CountNearbyNests(EntityManager em, float x, float y, float radius, int excludeEntity)
    {
        int count = 0;
        _spatialHash.QueryRadius(x, y, radius, _nearbyBuffer);
        foreach (int other in _nearbyBuffer)
        {
            if (other == excludeEntity || !em.IsAlive(other)) continue;
            if (em.HasComponents(other, ComponentFlags.Nest))
                count++;
        }
        return count;
    }

    private void TryFoundNest(float originX, float originY, float distance, int colonyId)
    {
        // Try several random positions at roughly the given distance
        for (int attempt = 0; attempt < 10; attempt++)
        {
            float angle = (float)(_rng.NextDouble() * Math.PI * 2);
            float dist = distance * (0.7f + (float)_rng.NextDouble() * 0.6f);
            float x = originX + MathF.Cos(angle) * dist;
            float y = originY + MathF.Sin(angle) * dist;

            var tile = _worldManager.GetTile(x, y);
            // Nests only on dry land tiles, away from water (no beach nests)
            if (tile == TileType.Arid || tile == TileType.Sand || tile == TileType.Grass ||
                tile == TileType.Savanna || tile == TileType.Tundra)
            {
                if (_worldManager.HasWaterNearby(x, y, 3))
                    continue; // Too close to water — likely a beach
                _pendingNests.Add((x, y, colonyId));
                return;
            }
        }
    }

    // === Hibernation tuning ===
    // A hungry Sectid that detects no prey within ForageDetectRadius for HibernateNoFoodTicks
    // consecutive ticks retreats to its nest and goes dormant (low metabolism). It wakes when
    // huntable prey strays within WakeRadius. This keeps a minimal viable colony alive through
    // prey troughs instead of the swarm wandering off and starving en masse.
    private const float HibernateHungerRatio = 0.35f; // Only consider dormancy when quite hungry
    private const int HibernateNoFoodTicks = 600;     // ~30s at 20 TPS of no prey before dormancy
    private const float ForageDetectRadius = 30f;     // Scanned for prey while deciding to hibernate
    private const float WakeRadius = 14f;             // Prey within this wakes a dormant Sectid

    private void ProcessSectids(EntityManager em)
    {
        var sectidDef = SpeciesRegistry.Get("Sectid");
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity |
                                        ComponentFlags.FoodCarrier | ComponentFlags.Species |
                                        ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var carrier = ref em.FoodCarriers[entity];
            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];

            // === Carrying food: deliver to nest (takes priority, cancels dormancy) ===
            if (carrier.IsCarrying)
            {
                carrier.IsHibernating = false;
                carrier.NoFoodTicks = 0;
                DeliverFoodToNest(em, entity, ref carrier, ref pos, ref vel, sectidDef);
                continue;
            }

            // === Dormant: idle at nest, wake when prey approaches ===
            if (carrier.IsHibernating)
            {
                if (HuntablePreyNearby(em, entity, pos.X, pos.Y, WakeRadius))
                {
                    carrier.IsHibernating = false; // Prey in reach — rejoin the hunt
                    continue;
                }

                int nest = FindNearestNest(em, pos.X, pos.Y);
                if (nest < 0)
                {
                    carrier.IsHibernating = false; // No refuge left — resume normal behavior
                    continue;
                }

                // Drift toward the nest and settle motionless once there.
                ref var nestPos = ref em.Positions[nest];
                float dx = nestPos.X - pos.X;
                float dy = nestPos.Y - pos.Y;
                float distSq = dx * dx + dy * dy;
                float settleRange = sectidDef.FoodDeliveryRange;
                if (distSq <= settleRange * settleRange)
                {
                    vel.Dx = 0f;
                    vel.Dy = 0f;
                }
                else
                {
                    float dist = MathF.Sqrt(distSq);
                    float speed = sectidDef.CarryingSpeed;
                    vel.Dx = (dx / dist) * speed;
                    vel.Dy = (dy / dist) * speed;
                }
                continue;
            }

            // === Active and hungry with no prey around: count down toward dormancy ===
            ref var hunger = ref em.Hungers[entity];
            if (hunger.Current / hunger.Max < HibernateHungerRatio)
            {
                if (HuntablePreyNearby(em, entity, pos.X, pos.Y, ForageDetectRadius))
                {
                    carrier.NoFoodTicks = 0; // Food is around — keep hunting
                }
                else
                {
                    carrier.NoFoodTicks += tickMult;
                    if (carrier.NoFoodTicks >= HibernateNoFoodTicks &&
                        FindNearestNest(em, pos.X, pos.Y) >= 0)
                    {
                        carrier.IsHibernating = true;
                        carrier.NoFoodTicks = 0;
                    }
                }
            }
            else
            {
                carrier.NoFoodTicks = 0;
            }
        }
    }

    /// <summary>
    /// Carry-and-deliver logic: steer a food-laden Sectid to its nearest nest, deposit the
    /// food on arrival, and feed the carrier a little from the delivery.
    /// </summary>
    private void DeliverFoodToNest(EntityManager em, int entity, ref FoodCarrier carrier,
        ref Position pos, ref Velocity vel, SpeciesDefinition sectidDef)
    {
        // Find nearest nest if no target or target dead
        if (carrier.TargetNest < 0 || !em.IsAlive(carrier.TargetNest) ||
            !em.HasComponents(carrier.TargetNest, ComponentFlags.Nest))
        {
            carrier.TargetNest = FindNearestNest(em, pos.X, pos.Y);
            if (carrier.TargetNest < 0) return; // No nest found
        }

        ref var nestPos = ref em.Positions[carrier.TargetNest];
        float dx = nestPos.X - pos.X;
        float dy = nestPos.Y - pos.Y;
        float distSq = dx * dx + dy * dy;

        // Arrived at nest — deliver food
        float deliveryRange = sectidDef.FoodDeliveryRange;
        if (distSq < deliveryRange * deliveryRange)
        {
            ref var nest = ref em.Nests[carrier.TargetNest];
            nest.FoodStored += carrier.FoodCarried;
            carrier.FoodCarried = 0f;
            carrier.TargetNest = -1;

            // Also feed self a bit from the delivery
            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                hunger.Current = MathF.Min(hunger.Max, hunger.Current + 3f);
            }
        }
        else
        {
            // Move toward nest (override wander velocity)
            float dist = MathF.Sqrt(distSq);
            float speed = sectidDef.CarryingSpeed;
            vel.Dx = (dx / dist) * speed;
            vel.Dy = (dy / dist) * speed;
        }
    }

    /// <summary>
    /// True if a huntable prey entity (anything with Prey that isn't another Sectid) is within
    /// the given radius. Shroomer spores and herbivores count as food; same-faction Sectids do not.
    /// </summary>
    private bool HuntablePreyNearby(EntityManager em, int self, float x, float y, float radius)
    {
        _nearbyBuffer.Clear();
        _spatialHash.QueryRadius(x, y, radius, _nearbyBuffer);
        foreach (int other in _nearbyBuffer)
        {
            if (other == self || !em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Prey)) continue;
            if (em.HasComponents(other, ComponentFlags.Species) &&
                em.Species[other].Type == SpeciesType.Sectid)
                continue; // Don't count our own kind as food
            return true;
        }
        return false;
    }

    private int FindNearestNest(EntityManager em, float x, float y)
    {
        int best = -1;
        float bestDistSq = float.MaxValue;

        _spatialHash.QueryRadius(x, y, 60f, _nearbyBuffer);
        foreach (int other in _nearbyBuffer)
        {
            if (!em.IsAlive(other) || !em.HasComponents(other, ComponentFlags.Nest | ComponentFlags.Position))
                continue;

            ref var otherPos = ref em.Positions[other];
            float distSq = MathUtils.DistanceSquared(x, y, otherPos.X, otherPos.Y);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = other;
            }
        }

        return best;
    }

    private void SpawnSectid(EntityManager em, float x, float y, int colonyId)
    {
        var speciesDef = SpeciesRegistry.Get("Sectid");
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD();
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        em.Species[entity] = new Species(SpeciesType.Sectid, 0, SpeciesRegistry.GetId("Sectid"));
        em.AddComponent(entity, ComponentFlags.Species);

        em.Ages[entity] = new Age(0, speciesDef.MaxLifespan, speciesDef.MaturityAge);
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        em.Hungers[entity] = new Hunger(speciesDef.MaxHunger * 0.7f, speciesDef.MaxHunger, speciesDef.HungerDecayRate,
            speciesDef.StarvationDamage);
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Wanders[entity] = new Wander(speciesDef.BaseWanderSpeed, speciesDef.DirectionChangeChance);
        em.AddComponent(entity, ComponentFlags.Wander);

        em.Renderables[entity] = new Renderable(speciesDef.BaseColor, speciesDef.BaseSize, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Sectids are prey (can be eaten by predators)
        em.Preys[entity] = new Prey(speciesDef.FleeRange, speciesDef.FleeSpeedMultiplier);
        em.AddComponent(entity, ComponentFlags.Prey);

        em.Fears[entity] = new Fear(
            speciesDef.FearThreshold, speciesDef.FearMax, speciesDef.FearAccumulationRate,
            speciesDef.FearDecayRate, speciesDef.FearVigilanceDecay, speciesDef.DefaultFearResponse);
        em.AddComponent(entity, ComponentFlags.Fear);

        // Sectids are also predators (hunt small prey and spores)
        em.Predators[entity] = new Predator(
            speciesDef.HuntRange, speciesDef.AttackRange, speciesDef.AttackPower, speciesDef.AttackCooldown);
        em.AddComponent(entity, ComponentFlags.Predator);

        // Food carrier — brings food back to nest
        em.FoodCarriers[entity] = new FoodCarrier(speciesDef.MaxCarryFood);
        em.AddComponent(entity, ComponentFlags.FoodCarrier);

        // Social — pack behavior for group hunting
        var social = new Social(SocialType.Pack, speciesDef.GroupAffinity,
            speciesDef.PreferredGroupSize, speciesDef.CohesionStrength, speciesDef.AlignmentStrength);
        social.GroupId = colonyId; // Colony ID doubles as initial group
        em.Socials[entity] = social;
        em.AddComponent(entity, ComponentFlags.Social);

        // Terraform
        em.Terraforms[entity] = new Terraform(speciesDef.TerraformDir,
            speciesDef.TerraformRadius, speciesDef.TerraformStrength, speciesDef.TerraformCooldown);
        em.AddComponent(entity, ComponentFlags.Terraform);

        em.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            speciesDef.DiscomfortThreshold, speciesDef.DiscomfortDecayRate);
        em.AddComponent(entity, ComponentFlags.TerrainDiscomfort);
    }

    public int SpawnNest(EntityManager em, float x, float y, int colonyId)
    {
        var nestDef = SpeciesRegistry.Get("Sectid");
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        // Nests don't move
        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.Nests[entity] = new Nest(colonyId, foodPerSpawn: nestDef.NestFoodPerSpawn,
            spawnDuration: nestDef.NestSpawnDuration);
        em.AddComponent(entity, ComponentFlags.Nest);

        // Nests have energy (health) — can be destroyed if unattended
        em.Energies[entity] = new Energy(nestDef.NestEnergy, nestDef.NestEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        // Visual: small brown square that grows with stage
        em.Renderables[entity] = new Renderable(
            new Color(0.6f, 0.4f, 0.2f), 9f, ShapeType.Square);
        em.AddComponent(entity, ComponentFlags.Renderable);

        _spatialHash.Update(entity, x, y);

        return entity;
    }
}
