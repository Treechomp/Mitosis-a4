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
/// Manages Shroomer spore lifecycle and Shroomer growth.
///
/// Spore lifecycle:
/// - Mature Shroomers spread spores when moisture is high
/// - Spores gain moisture on wet tiles, wither on dry tiles
/// - Once enough moisture accumulated, spore transforms into small Shroomer
/// - Spores are edible by Sectids and herbivores (they have Prey flag)
///
/// Shroomer growth:
/// - Shroomers grow continuously over their lifetime
/// - Old Shroomers become the largest entities on the map
/// - AoE attack radius and damage scale with growth
/// </summary>
public sealed class SporeSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly Random _rng = new();

    private readonly List<(float x, float y, int speciesId)> _pendingSpores = new(16);
    private readonly List<(float x, float y, int speciesId)> _pendingTransforms = new(8);
    private readonly List<int> _toKill = new(16);
    private readonly List<int> _nearbyBuffer = new(32);
    private readonly List<int> _crowdBuffer = new(32);
    private readonly int _maxPopulation;

    public SporeSystem(WorldManager worldManager, SpatialHash spatialHash, int maxPopulation)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
        _maxPopulation = maxPopulation;
    }

    public void Process(EntityManager em)
    {
        _pendingSpores.Clear();
        _pendingTransforms.Clear();
        _toKill.Clear();

        // === PROCESS EXISTING SPORES ===
        const ComponentFlags sporeRequired = ComponentFlags.Position | ComponentFlags.Spore |
                                              ComponentFlags.Energy;

        foreach (int entity in em.Query(sporeRequired))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: moisture/wither accumulate at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var spore = ref em.Spores[entity];
            ref var pos = ref em.Positions[entity];
            ref var energy = ref em.Energies[entity];

            var tile = _worldManager.GetTile(pos.X, pos.Y);
            float tileMoisture = GetTileMoisture(tile);

            // Get species definition for spore parameters
            var sporeDef = GetSpeciesDef(em, entity);
            float moistureThreshold = sporeDef?.SporeMoistureThreshold ?? 0.6f;

            if (tileMoisture >= moistureThreshold)
            {
                // Wet tile: accumulate moisture
                spore.MoistureAccumulated += spore.MoistureGainRate * tileMoisture * tickMult;
            }
            else
            {
                // Dry tile: wither
                energy.Current -= spore.WitherRate * tickMult;
            }

            // Check for transformation
            if (spore.IsReadyToTransform)
            {
                _pendingTransforms.Add((pos.X, pos.Y, spore.ParentSpeciesId));
                _toKill.Add(entity);
                continue;
            }

            // Check for death from withering
            if (energy.IsDead)
            {
                _toKill.Add(entity);
            }
        }

        // === MATURE SHROOMERS SPREAD SPORES ===
        const ComponentFlags shroomRequired = ComponentFlags.Position | ComponentFlags.Species |
                                               ComponentFlags.Age | ComponentFlags.Hunger;

        foreach (int entity in em.Query(shroomRequired))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var species = ref em.Species[entity];
            if (species.Type != SpeciesType.Shroomer) continue;

            // LOD tick multiplier: roll spread chance multiple times to compensate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            var shroomDef = SpeciesRegistry.GetById(species.SpeciesId);
            ref var pos = ref em.Positions[entity];
            var tile = _worldManager.GetTile(pos.X, pos.Y);
            float tileMoisture = GetTileMoisture(tile);
            bool droughted = tileMoisture < shroomDef.SporeMoistureThreshold;

            // === SELF-LIMITING: crowding competition + drought ===
            // Count same-species neighbours once (reused for attrition AND spread suppression).
            int neighbours = 0;
            if (shroomDef.CrowdingRadius > 0f)
            {
                _crowdBuffer.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, shroomDef.CrowdingRadius, _crowdBuffer);
                foreach (int other in _crowdBuffer)
                {
                    if (other == entity || !em.IsAlive(other)) continue;
                    if (em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Growth)
                        && em.Species[other].Type == SpeciesType.Shroomer)
                        neighbours++;
                }
            }

            // Attrition: a dense fungal mat competes with itself for substrate, and it cannot
            // hold ground that has dried out (faction terraforming toward dry biomes collapses a
            // bloom instead of merely stalling it). Both LOD-compensated so a distant bloom dies
            // at the same real-time rate.
            if (em.HasComponents(entity, ComponentFlags.Energy))
            {
                float attrition = 0f;
                if (shroomDef.CrowdingDamage > 0f && neighbours > shroomDef.CrowdingLimit)
                    attrition += shroomDef.CrowdingDamage * (neighbours - shroomDef.CrowdingLimit);
                if (shroomDef.DroughtDamage > 0f && droughted)
                    attrition += shroomDef.DroughtDamage;
                if (attrition > 0f)
                {
                    ref var energy = ref em.Energies[entity];
                    energy.Current -= attrition * tickMult;
                    if (energy.IsDead)
                    {
                        _toKill.Add(entity);
                        EcosystemLogger.Instance?.LogEnvironmentDeath(
                            species.SpeciesId, entity, pos.X, pos.Y, droughted ? "drought" : "crowding");
                        continue;
                    }
                }
            }

            // === SPREAD (mature, fed, wet ground, open space, global headroom) ===
            ref var age = ref em.Ages[entity];
            if (!age.IsMature) continue;

            ref var hunger = ref em.Hungers[entity];
            if (hunger.Percent < 0.5f) continue; // Need decent food to spread
            if (droughted) continue;             // no spreading on dry ground

            // Local saturation: a Shroomer ringed by kin has no open ground to colonise, so its
            // spread chance falls to zero as neighbours climb from CrowdingLimit → Saturation.
            float localFactor = 1f;
            if (shroomDef.CrowdingRadius > 0f && neighbours > shroomDef.CrowdingLimit)
            {
                int span = Math.Max(1, shroomDef.CrowdingSaturation - shroomDef.CrowdingLimit);
                localFactor = Math.Clamp(1f - (neighbours - shroomDef.CrowdingLimit) / (float)span, 0f, 1f);
            }
            // Global population pressure — a safety ceiling mirroring ReproductionSystem's ramp,
            // so Shroomers can never convert the whole shared cap even if the biological levers
            // above are mistuned. 1.0 until 50% of cap, linear to 0 at 100%.
            float popRatio = (float)em.EntityCount / _maxPopulation;
            float globalFactor = popRatio > 0.5f ? MathF.Max(0f, 2f * (1f - popRatio)) : 1f;

            float effChance = shroomDef.SporeSpreadChance * localFactor * globalFactor;
            if (effChance <= 0f) continue;

            // Random chance to spread — compensate for skipped ticks:
            // probability of at least one success in tickMult trials = 1 - (1-p)^tickMult
            double noSpreadProb = Math.Pow(1.0 - effChance, tickMult);
            if (_rng.NextDouble() >= 1.0 - noSpreadProb) continue;

            // Spread spores
            hunger.Current -= hunger.Max * shroomDef.SporeSpreadHungerCost;
            for (int i = 0; i < shroomDef.SporesPerSpread; i++)
            {
                float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                float dist = (float)(_rng.NextDouble() * shroomDef.SporeSpreadRadius) + 2f;
                float sx = pos.X + MathF.Cos(angle) * dist;
                float sy = pos.Y + MathF.Sin(angle) * dist;

                if (_worldManager.IsSpawnable(sx, sy))
                    _pendingSpores.Add((sx, sy, species.SpeciesId));
            }
        }

        // === GROWTH SYSTEM (Shroomers grow over time) ===
        const ComponentFlags growthRequired = ComponentFlags.Growth | ComponentFlags.Renderable;
        foreach (int entity in em.Query(growthRequired))
        {
            ref var growth = ref em.Growths[entity];
            if (growth.CurrentScale >= growth.MaxScale) continue;

            // LOD tick multiplier: growth rate compensated for skipped ticks
            int growthTickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            float prevScale = growth.CurrentScale;
            growth.CurrentScale = MathF.Min(growth.MaxScale, growth.CurrentScale + growth.GrowthRate * growthTickMult);

            // Update visual size and scale HP with growth
            ref var rend = ref em.Renderables[entity];
            var speciesDef = GetSpeciesDef(em, entity);
            if (speciesDef != null)
            {
                rend.Size = speciesDef.BaseSize * growth.CurrentScale;

                // Scale max HP with growth — growing adds HP but doesn't heal damage
                if (em.HasComponents(entity, ComponentFlags.Energy))
                {
                    ref var energy = ref em.Energies[entity];
                    float newMax = speciesDef.MaxEnergy * growth.CurrentScale;
                    float maxDelta = newMax - energy.Max;
                    energy.Max = newMax;
                    energy.Current += maxDelta; // Add new HP capacity, preserve damage taken
                }
            }
        }

        // === SHROOMER AOE ATTACK (scales with growth) ===
        ProcessShroomAoE(em);

        // === CLEANUP AND SPAWNING ===
        em.DestroyEntities(_toKill);

        foreach (var (x, y, speciesId) in _pendingSpores)
        {
            if (em.EntityCount >= _maxPopulation) break;
            SpawnSpore(em, x, y, speciesId);
            EcosystemLogger.Instance?.LogSporeCreated(x, y);
        }

        foreach (var (x, y, speciesId) in _pendingTransforms)
        {
            if (em.EntityCount >= _maxPopulation) break;
            SpawnShroomer(em, x, y, speciesId);
            EcosystemLogger.Instance?.LogSporeMatured(x, y);
            EcosystemLogger.Instance?.LogReproduction(speciesId, -1, x, y, 1);
        }
    }

    private float GetTileMoisture(TileType tile)
    {
        return tile switch
        {
            TileType.Bog => 1.0f,
            TileType.Wetland => 1.0f,
            TileType.ShallowWater => 1.0f,
            TileType.DeepWater => 1.0f,
            TileType.River => 0.95f,
            TileType.Reef => 0.9f,
            TileType.Jungle => 0.8f,
            TileType.Taiga => 0.7f,
            TileType.Forest => 0.7f,
            TileType.Ice => 0.5f,
            TileType.Grass => 0.4f,
            TileType.Shrubland => 0.35f,
            TileType.Steppe => 0.25f,
            TileType.Savanna => 0.25f,
            TileType.Dirt => 0.15f,
            TileType.Tundra => 0.2f,
            TileType.Sand => 0.1f,
            TileType.Arid => 0.0f,
            TileType.Lava => 0.0f,
            _ => 0.2f,
        };
    }

    private SpeciesDefinition? GetSpeciesDef(EntityManager em, int entity)
    {
        if (!em.HasComponents(entity, ComponentFlags.Species)) return null;
        ref var species = ref em.Species[entity];
        return SpeciesRegistry.GetById(species.SpeciesId);
    }

    private void SpawnSpore(EntityManager em, float x, float y, int parentSpeciesId)
    {
        var parentDef = SpeciesRegistry.GetById(parentSpeciesId);
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        // Spores don't move
        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.Spores[entity] = new Spore(
            transformThreshold: parentDef.SporeTransformThreshold,
            witherRate: parentDef.SporeWitherRate,
            moistureGainRate: parentDef.SporeMoistureGainRate,
            parentSpeciesId: parentSpeciesId);
        em.AddComponent(entity, ComponentFlags.Spore);

        // Spores have low energy (die easily)
        em.Energies[entity] = new Energy(parentDef.SporeEnergy, parentDef.SporeEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        // Spores are edible (prey for Sectids and herbivores)
        em.Preys[entity] = new Prey(0f, 0f); // Can't flee
        em.AddComponent(entity, ComponentFlags.Prey);

        // Small purple dot visual
        em.Renderables[entity] = new Renderable(
            new Color(0.7f, 0.3f, 0.9f), 3f, ShapeType.Circle);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Mark as Shroomer species for type checks
        em.Species[entity] = new Species(SpeciesType.Shroomer, 0, parentSpeciesId);
        em.AddComponent(entity, ComponentFlags.Species);

        _spatialHash.Update(entity, x, y);
    }

    private void SpawnShroomer(EntityManager em, float x, float y, int speciesId)
    {
        var speciesDef = SpeciesRegistry.GetById(speciesId);
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD(LODLevel.Full);
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        em.Species[entity] = new Species(SpeciesType.Shroomer, 0, speciesId);
        em.AddComponent(entity, ComponentFlags.Species);

        em.Ages[entity] = new Age(0, speciesDef.MaxLifespan, speciesDef.MaturityAge);
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        em.Hungers[entity] = new Hunger(speciesDef.MaxHunger * 0.5f, speciesDef.MaxHunger, speciesDef.HungerDecayRate,
            speciesDef.StarvationDamage);
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Wanders[entity] = new Wander(speciesDef.BaseWanderSpeed, speciesDef.DirectionChangeChance);
        em.AddComponent(entity, ComponentFlags.Wander);

        // Start small, grow over time
        em.Renderables[entity] = new Renderable(speciesDef.BaseColor, speciesDef.BaseSize * 0.5f, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Growth — Shroomers grow to become the largest entities on the map
        em.Growths[entity] = new Growth(maxScale: speciesDef.GrowthMaxScale, growthRate: speciesDef.GrowthRate);
        em.Growths[entity].CurrentScale = speciesDef.InitialScale; // Start small
        em.AddComponent(entity, ComponentFlags.Growth);

        // Shroomers are prey
        em.Preys[entity] = new Prey(speciesDef.FleeRange, speciesDef.FleeSpeedMultiplier);
        em.AddComponent(entity, ComponentFlags.Prey);

        em.Fears[entity] = new Fear(
            speciesDef.FearThreshold, speciesDef.FearMax, speciesDef.FearAccumulationRate,
            speciesDef.FearDecayRate, speciesDef.FearVigilanceDecay, speciesDef.DefaultFearResponse);
        em.AddComponent(entity, ComponentFlags.Fear);

        // Social
        em.Socials[entity] = new Social(SocialType.Herd, speciesDef.GroupAffinity,
            speciesDef.PreferredGroupSize, speciesDef.CohesionStrength, speciesDef.AlignmentStrength);
        em.AddComponent(entity, ComponentFlags.Social);

        // Terraform
        em.Terraforms[entity] = new Terraform(speciesDef.TerraformDir,
            speciesDef.TerraformRadius, speciesDef.TerraformStrength, speciesDef.TerraformCooldown);
        em.AddComponent(entity, ComponentFlags.Terraform);

        em.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            speciesDef.DiscomfortThreshold, speciesDef.DiscomfortDecayRate);
        em.AddComponent(entity, ComponentFlags.TerrainDiscomfort);
    }

    /// <summary>
    /// Shroomers with AoE attack damage nearby Faelings and Sectids.
    /// AoE radius and damage scale with Growth.CurrentScale.
    /// Uses Terraform cooldown as attack timer (shared resource).
    /// </summary>
    private void ProcessShroomAoE(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species |
                                         ComponentFlags.Growth | ComponentFlags.Terraform;
        _nearbyBuffer.Clear();

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var species = ref em.Species[entity];
            if (species.Type != SpeciesType.Shroomer) continue;

            var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
            if (!speciesDef.HasAoEAttack) continue;

            ref var growth = ref em.Growths[entity];
            if (growth.CurrentScale < speciesDef.AoEMinScale) continue; // Only mature shroomers attack

            // AoE pulse interval: slow passive pulses, faster reactive pulses when in combat
            bool inCombat = em.HasComponents(entity, ComponentFlags.Energy) && em.Energies[entity].RegenCooldown > 0;
            int aoeInterval = inCombat
                ? speciesDef.EffectiveAoECombatCooldown
                : speciesDef.EffectiveAoEPassiveCooldown;
            aoeInterval = Math.Max(1, aoeInterval);
            if (em.HasComponents(entity, ComponentFlags.Age))
            {
                ref var age = ref em.Ages[entity];
                if (age.Current % aoeInterval != 0) continue;
            }

            ref var pos = ref em.Positions[entity];
            // S-curve growth scaling: slow at birth → growth spurt mid-life → tapering at elder
            float growthFactor = speciesDef.GetGrowthScalingFactor(growth.CurrentScale);
            float aoeRadius = speciesDef.AoEAttackRadius * growthFactor;
            float aoeDamage = speciesDef.AoEAttackDamage * growthFactor;

            _spatialHash.QueryRadius(pos.X, pos.Y, aoeRadius, _nearbyBuffer);

            foreach (int other in _nearbyBuffer)
            {
                if (other == entity || !em.IsAlive(other)) continue;
                if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Energy)) continue;

                ref var otherSpecies = ref em.Species[other];
                // Only damage Faelings and Sectids (enemy terraformers)
                if (otherSpecies.Type != SpeciesType.Faeling && otherSpecies.Type != SpeciesType.Sectid)
                    continue;

                // Don't damage spores
                if (em.HasComponents(other, ComponentFlags.Spore)) continue;

                ref var otherEnergy = ref em.Energies[other];
                otherEnergy.Current -= aoeDamage;
                otherEnergy.RegenCooldown = 60; // 3s combat cooldown at 20 TPS

                if (EcosystemLogger.TrackedSpeciesId >= 0
                    && em.HasComponents(entity, ComponentFlags.Species))
                {
                    ref var otherPos = ref em.Positions[other];
                    EcosystemLogger.Instance?.LogCombatHit(
                        em.Species[entity].SpeciesId, entity,
                        otherSpecies.SpeciesId, other,
                        aoeDamage, otherPos.X, otherPos.Y, "aoe");
                }

                if (otherEnergy.IsDead)
                {
                    // Log the kill
                    if (em.HasComponents(entity, ComponentFlags.Species) &&
                        em.HasComponents(other, ComponentFlags.Species))
                    {
                        ref var killerSpecies = ref em.Species[entity];
                        ref var victimSpecies = ref em.Species[other];
                        ref var victimPos = ref em.Positions[other];
                        EcosystemLogger.Instance?.LogKill(
                            killerSpecies.SpeciesId, victimSpecies.SpeciesId,
                            entity, other, victimPos.X, victimPos.Y);
                    }

                    // Handle Faeling death → crystal power inheritance
                    if (em.HasComponents(other, ComponentFlags.FaelingPower))
                    {
                        ref var power = ref em.FaelingPowers[other];
                        int crystalId = power.LinkedCrystal;
                        if (crystalId >= 0 && em.IsAlive(crystalId) &&
                            em.HasComponents(crystalId, ComponentFlags.Crystal))
                        {
                            ref var crystal = ref em.Crystals[crystalId];
                            crystal.InheritedPower = power.Power * 0.5f;
                            crystal.LinkedFaeling = -1;
                            crystal.SpawnTimer = crystal.SpawnDelay;
                        }
                    }

                    em.DestroyEntity(other);
                }
            }
        }
    }
}
