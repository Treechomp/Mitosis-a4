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
/// Manages Faeling crystals and their linked Faelings.
///
/// Crystal lifecycle:
/// - Crystals are indestructible structures placed during world generation
/// - Each crystal spawns and links to one Faeling
/// - When the linked Faeling dies, crystal begins spawning a replacement
///   with half the deceased Faeling's power
/// - Faelings don't starve, grow slowly, gain power from kills and tile restoration
/// - Ranged attack allows kiting Sectid groups and fighting large Shroomers
/// </summary>
public sealed class CrystalSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly Random _rng = new();

    private readonly List<(float x, float y, int crystalEntity, float inheritedPower)> _pendingFaelings = new(4);
    private readonly List<int> _nearbyBuffer = new(32);
    private readonly int _maxPopulation;

    public CrystalSystem(WorldManager worldManager, SpatialHash spatialHash, int maxPopulation)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
        _maxPopulation = maxPopulation;
    }

    public void Process(EntityManager em)
    {
        _pendingFaelings.Clear();

        // === CRYSTAL PROCESSING ===
        const ComponentFlags crystalRequired = ComponentFlags.Position | ComponentFlags.Crystal;

        foreach (int entity in em.Query(crystalRequired))
        {
            ref var crystal = ref em.Crystals[entity];

            // Check if linked Faeling is still alive
            if (crystal.HasFaeling)
            {
                if (!em.IsAlive(crystal.LinkedFaeling))
                {
                    // Faeling died — start spawning replacement
                    // Try to inherit power from the dead faeling
                    // (power was stored in InheritedPower when faeling died, via FactionCombatSystem)
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = crystal.SpawnDelay;
                }
                continue;
            }

            // Spawning countdown
            if (crystal.SpawnTimer > 0)
            {
                crystal.SpawnTimer--;
                if (crystal.SpawnTimer <= 0)
                {
                    ref var pos = ref em.Positions[entity];
                    _pendingFaelings.Add((pos.X, pos.Y, entity, crystal.InheritedPower));
                }
            }
        }

        // === FAELING POWER TRACKING ===
        // Faelings gain power from tile restoration (tracked here since TerraformSystem runs separately)
        const ComponentFlags faelingRequired = ComponentFlags.Position | ComponentFlags.FaelingPower |
                                                ComponentFlags.Terraform;

        foreach (int entity in em.Query(faelingRequired))
        {
            ref var power = ref em.FaelingPowers[entity];
            ref var growth = ref em.Growths[entity];

            // Power-based growth boost
            if (em.HasComponents(entity, ComponentFlags.Growth) &&
                em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var faeSpecies = ref em.Species[entity];
                var faeDef = SpeciesRegistry.GetById(faeSpecies.SpeciesId);
                // Faelings grow faster with more power
                float powerBoost = 1f + power.Power * 0.01f;
                growth.GrowthRate = faeDef.GrowthRate * powerBoost;
            }

            // Power affects ranged attack damage
            if (em.HasComponents(entity, ComponentFlags.RangedAttack) &&
                em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var faeSpecies = ref em.Species[entity];
                var faeDef = SpeciesRegistry.GetById(faeSpecies.SpeciesId);
                ref var ranged = ref em.RangedAttacks[entity];
                ranged.BaseDamage = faeDef.RangedAttackDamage + power.Power * 0.5f;
            }
        }

        // === RANGED ATTACK PROCESSING ===
        ProcessRangedAttacks(em);

        // === SPAWN PENDING FAELINGS (respect population cap) ===
        foreach (var (x, y, crystalEntity, inheritedPower) in _pendingFaelings)
        {
            if (em.EntityCount >= _maxPopulation) break;
            int faeling = SpawnFaeling(em, x, y, crystalEntity, inheritedPower);
            if (faeling >= 0)
            {
                ref var crystal = ref em.Crystals[crystalEntity];
                crystal.LinkedFaeling = faeling;
                crystal.InheritedPower = 0f;
            }
        }
    }

    private void ProcessRangedAttacks(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.RangedAttack |
                                         ComponentFlags.FaelingPower;
        _nearbyBuffer.Clear();

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip ranged attacks for distant Faelings (Reduced+)
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Reduced)
                    continue;
            }

            ref var ranged = ref em.RangedAttacks[entity];
            ref var pos = ref em.Positions[entity];

            // Cooldown
            if (ranged.CurrentCooldown > 0)
            {
                ranged.CurrentCooldown--;
                continue;
            }

            // Find terraformer target (Sectids and Shroomers — NOT other Faelings)
            _spatialHash.QueryRadius(pos.X, pos.Y, ranged.Range, _nearbyBuffer);

            int bestTarget = -1;
            float bestDistSq = ranged.Range * ranged.Range;

            foreach (int other in _nearbyBuffer)
            {
                if (other == entity || !em.IsAlive(other)) continue;
                if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Energy)) continue;

                ref var otherSpecies = ref em.Species[other];
                // Target only Sectids and Shroomers (not Faelings)
                if (otherSpecies.Type != SpeciesType.Sectid && otherSpecies.Type != SpeciesType.Shroomer)
                    continue;

                // Don't target spores (they have low priority)
                if (em.HasComponents(other, ComponentFlags.Spore)) continue;

                ref var otherPos = ref em.Positions[other];
                float distSq = MathUtils.DistanceSquared(pos.X, pos.Y, otherPos.X, otherPos.Y);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestTarget = other;
                }
            }

            if (bestTarget >= 0)
            {
                // Attack!
                ref var targetEnergy = ref em.Energies[bestTarget];
                targetEnergy.Current -= ranged.BaseDamage;
                targetEnergy.RegenCooldown = 60; // 3s combat cooldown at 20 TPS
                ranged.CurrentCooldown = ranged.Cooldown;
                ranged.TargetEntity = bestTarget;

                // Check if kill — award power
                if (targetEnergy.IsDead)
                {
                    ref var power = ref em.FaelingPowers[entity];
                    power.Power += power.PowerPerKill;
                    power.KillCount++;
                    em.DestroyEntity(bestTarget);
                }
            }
            else
            {
                ranged.TargetEntity = -1;
            }
        }
    }

    private int SpawnFaeling(EntityManager em, float x, float y, int crystalEntity, float inheritedPower)
    {
        var speciesDef = SpeciesRegistry.Get("Faeling");

        // Offset spawn slightly from crystal
        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
        float spawnX = x + MathF.Cos(angle) * 2f;
        float spawnY = y + MathF.Sin(angle) * 2f;

        if (!_worldManager.IsWalkable(spawnX, spawnY))
        {
            spawnX = x;
            spawnY = y;
        }

        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(spawnX, spawnY);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD();
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        em.Species[entity] = new Species(SpeciesType.Faeling, 0, SpeciesRegistry.GetId("Faeling"));
        em.AddComponent(entity, ComponentFlags.Species);

        // Faelings live very long
        em.Ages[entity] = new Age(0, speciesDef.MaxLifespan, speciesDef.MaturityAge);
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        // Faelings don't starve — keep hunger always high
        em.Hungers[entity] = new Hunger(speciesDef.MaxHunger, speciesDef.MaxHunger, speciesDef.HungerDecayRate,
            speciesDef.StarvationDamage);
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Wanders[entity] = new Wander(speciesDef.BaseWanderSpeed, speciesDef.DirectionChangeChance);
        em.AddComponent(entity, ComponentFlags.Wander);

        em.Renderables[entity] = new Renderable(speciesDef.BaseColor, speciesDef.BaseSize, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Faelings are NOT prey (predators don't hunt them)
        // No Prey component, no Fear component

        // Terraform — restore balance
        em.Terraforms[entity] = new Terraform(speciesDef.TerraformDir,
            speciesDef.TerraformRadius, speciesDef.TerraformStrength, speciesDef.TerraformCooldown);
        em.AddComponent(entity, ComponentFlags.Terraform);

        // Growth — slow, power-boosted
        em.Growths[entity] = new Growth(maxScale: speciesDef.GrowthMaxScale, growthRate: speciesDef.GrowthRate);
        em.AddComponent(entity, ComponentFlags.Growth);

        // Power system
        em.FaelingPowers[entity] = new FaelingPower(crystalEntity, powerPerKill: 5f, powerPerTile: 0.2f);
        em.FaelingPowers[entity].Power = inheritedPower; // Inherit half of predecessor's power
        em.AddComponent(entity, ComponentFlags.FaelingPower);

        // Ranged attack
        em.RangedAttacks[entity] = new RangedAttack(
            range: speciesDef.RangedAttackRange,
            baseDamage: speciesDef.RangedAttackDamage + inheritedPower * 0.5f,
            cooldown: speciesDef.RangedAttackCooldown);
        em.AddComponent(entity, ComponentFlags.RangedAttack);

        // Social — solitary guardians
        em.Socials[entity] = new Social(SocialType.Solitary);
        em.AddComponent(entity, ComponentFlags.Social);

        em.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            speciesDef.DiscomfortThreshold, speciesDef.DiscomfortDecayRate);
        em.AddComponent(entity, ComponentFlags.TerrainDiscomfort);

        return entity;
    }

    /// <summary>
    /// Spawns a crystal at the given position. Called during world generation.
    /// </summary>
    public int SpawnCrystal(EntityManager em, float x, float y)
    {
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        var faelingDef = SpeciesRegistry.Get("Faeling");
        em.Crystals[entity] = new Crystal(spawnDelay: faelingDef.CrystalSpawnDelay);
        em.AddComponent(entity, ComponentFlags.Crystal);

        // Indestructible — very high energy, no aging
        em.Energies[entity] = new Energy(999999f, 999999f);
        em.AddComponent(entity, ComponentFlags.Energy);

        // Teal diamond visual
        em.Renderables[entity] = new Renderable(
            new Color(0.1f, 1f, 0.9f), 7f, ShapeType.Square);
        em.AddComponent(entity, ComponentFlags.Renderable);

        _spatialHash.Update(entity, x, y);

        return entity;
    }
}
