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
/// Accumulates terrain discomfort when on uncomfortable tiles, decays on comfortable ones.
/// Also applies grazing pressure for hungry herbivores on non-grazeable terrain.
/// Handles drowning (land creatures in water) and suffocation (aquatic creatures on land).
/// </summary>
public sealed class TerrainDiscomfortSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private readonly List<int> _toKill = new(16);

    public TerrainDiscomfortSystem(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        _toKill.Clear();
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.TerrainDiscomfort;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var pos = ref em.Positions[entity];
            ref var discomfort = ref em.TerrainDiscomforts[entity];

            // Get current tile and apply species-specific comfort modifier
            var tile = _worldManager.GetTile(pos.X, pos.Y);
            float tileDiscomfort = tile.GetDiscomfortRate();
            bool inWater = tile.IsWater();

            SpeciesDefinition? speciesDef = null;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                speciesDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                if (speciesDef != null)
                {
                    // Flying creatures ignore terrain discomfort entirely
                    if (speciesDef.IsFlying)
                    {
                        discomfort.Current = MathF.Max(0, discomfort.Current - discomfort.DecayRate);
                        discomfort.WrongElementTicks = 0;
                        continue;
                    }
                    tileDiscomfort += speciesDef.GetTerrainComfortModifier(tile);
                }
            }

            // Clamp so negative comfort can't cause negative discomfort accumulation
            tileDiscomfort = MathF.Max(0, tileDiscomfort);

            // Add grazing pressure for hungry herbivores on non-grazeable or depleted terrain
            if (discomfort.GrazingPressure > 0 && em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                float hungerFactor = 1f - (hunger.Current / hunger.Max);

                if (!tile.IsGrazeable())
                {
                    // Not grazeable at all — full pressure
                    tileDiscomfort += discomfort.GrazingPressure * hungerFactor;
                }
                else
                {
                    // Grazeable but possibly depleted — pressure scales with depletion
                    float nutrition = _worldManager.GetNutrition(pos.X, pos.Y);
                    if (nutrition < 0.3f)
                    {
                        float depletionFactor = 1f - (nutrition / 0.3f);
                        tileDiscomfort += discomfort.GrazingPressure * hungerFactor * depletionFactor * 0.5f;
                    }
                }
            }

            // Accumulate or decay discomfort
            if (tileDiscomfort > 0)
            {
                // Accumulate discomfort
                discomfort.Current += tileDiscomfort;
            }
            else
            {
                // Decay discomfort on comfortable terrain
                discomfort.Current = MathF.Max(0, discomfort.Current - discomfort.DecayRate);
            }

            // === Drowning / Suffocation ===
            if (speciesDef != null && em.HasComponents(entity, ComponentFlags.Energy))
            {
                bool isDrowning = inWater && !speciesDef.IsAquatic && !speciesDef.SemiAquatic;
                bool isSuffocating = !inWater && speciesDef.IsAquatic;

                if (isDrowning || isSuffocating)
                {
                    discomfort.WrongElementTicks++;

                    if (discomfort.WrongElementTicks > speciesDef.WrongElementGraceTicks)
                    {
                        ref var energy = ref em.Energies[entity];
                        // Healthier entities resist longer; damage accelerates as energy drops
                        float energyFactor = 1f - (energy.Percent * 0.7f);
                        energy.Current -= speciesDef.WrongElementDamageRate * energyFactor;
                        energy.RegenCooldown = 40; // Suppress regen while drowning/suffocating

                        if (energy.IsDead)
                        {
                            _toKill.Add(entity);
                            if (em.HasComponents(entity, ComponentFlags.Species))
                            {
                                ref var sp = ref em.Species[entity];
                                string cause = isDrowning ? "drowning" : "suffocation";
                                EcosystemLogger.Instance?.LogEnvironmentDeath(
                                    sp.SpeciesId, entity, pos.X, pos.Y, cause);
                            }
                        }
                    }
                }
                else
                {
                    // Back in correct element — reset counter
                    discomfort.WrongElementTicks = 0;
                }
            }
        }

        // Kill drowned/suffocated entities (leaving scavengeable remains)
        foreach (int entity in _toKill)
            CarrionSystem.SpawnCorpse(em, entity);
        em.DestroyEntities(_toKill);
    }
}

/// <summary>
/// Faction species gradually modify terrain tiles based on their terraform direction.
/// Shroomers push tiles wetter, Sectids push drier, Faelings push toward balance.
/// </summary>
public sealed class TerraformSystem : ISystem
{
    private readonly World.WorldManager _worldManager;
    private readonly Random _rng = new();

    // Moisture nudged per successful terraform event (continuous; classification + colour
    // follow). Roughly one discrete biome step's worth of moisture. Tunable.
    private const float MoistureStep = 0.05f;

    public TerraformSystem(World.WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Terraform;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // Dormant Sectids are inactive — they don't reshape terrain while hibernating
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier) && em.FoodCarriers[entity].IsHibernating)
                continue;

            // LOD tick multiplier: terraform cooldowns count down at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var terraform = ref em.Terraforms[entity];

            // Cooldown
            if (terraform.CurrentCooldown > 0)
            {
                terraform.CurrentCooldown -= tickMult;
                continue;
            }

            terraform.CurrentCooldown = terraform.Cooldown;

            // Roll against strength probability
            if ((float)_rng.NextDouble() > terraform.Strength)
                continue;

            ref var pos = ref em.Positions[entity];

            // Pick a random tile within influence radius
            float offsetX = ((float)_rng.NextDouble() * 2f - 1f) * terraform.Radius;
            float offsetY = ((float)_rng.NextDouble() * 2f - 1f) * terraform.Radius;
            float targetX = pos.X + offsetX;
            float targetY = pos.Y + offsetY;

            // Terraform nudges the moisture parameter; the tile's classification and its
            // continuous colour follow from the new params (docs/3d-terrain-plan.md Phase 2b).
            _worldManager.Terraform(targetX, targetY, terraform.Direction, MoistureStep);
        }
    }
}

/// <summary>
/// Regenerates nutrition on grazeable tiles across all loaded chunks.
/// Runs every tick but the regeneration rate per tile is very slow, so
/// depleted areas take many ticks to recover — driving migration patterns.
/// </summary>
public sealed class TileRegenerationSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private int _tickCounter;

    /// <summary>
    /// Only regenerate every N ticks; multiply rate by N to keep net regeneration identical.
    /// At RegenerationRate 0.0005/tick, tiles take 2000 ticks to fully recover —
    /// a 4-tick gap is invisible but cuts this system's cost by ~75%.
    /// </summary>
    private const int RegenInterval = 4;

    public TileRegenerationSystem(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        _tickCounter++;
        if (_tickCounter % RegenInterval != 0)
            return;

        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            chunk.RegenerateNutrition(RegenInterval);
        }
    }
}
