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
/// </summary>
public sealed class TerrainDiscomfortSystem : ISystem
{
    private readonly WorldManager _worldManager;

    public TerrainDiscomfortSystem(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.TerrainDiscomfort;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip discomfort for distant entities (Reduced+)
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Reduced)
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var discomfort = ref em.TerrainDiscomforts[entity];

            // Get current tile and apply species-specific comfort modifier
            var tile = _worldManager.GetTile(pos.X, pos.Y);
            float tileDiscomfort = tile.GetDiscomfortRate();

            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                var speciesDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                if (speciesDef != null)
                    tileDiscomfort += speciesDef.GetTerrainComfortModifier(tile);
            }

            // Clamp so negative comfort can't cause negative discomfort accumulation
            tileDiscomfort = MathF.Max(0, tileDiscomfort);

            // Add grazing pressure for hungry herbivores on non-grazeable terrain
            if (discomfort.GrazingPressure > 0 && !tile.IsGrazeable())
            {
                // Check hunger level
                if (em.HasComponents(entity, ComponentFlags.Hunger))
                {
                    ref var hunger = ref em.Hungers[entity];
                    // Grazing pressure scales with hunger (hungrier = more pressure)
                    float hungerFactor = 1f - (hunger.Current / hunger.Max);
                    tileDiscomfort += discomfort.GrazingPressure * hungerFactor;
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
        }
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

    public TerraformSystem(World.WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Terraform;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip terraform for distant entities (Statistical+)
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Statistical)
                    continue;
            }

            ref var terraform = ref em.Terraforms[entity];

            // Cooldown
            if (terraform.CurrentCooldown > 0)
            {
                terraform.CurrentCooldown--;
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

            var currentTile = _worldManager.GetTile(targetX, targetY);
            if (!currentTile.IsTerraformable())
                continue;

            // Determine transformation based on direction
            TileType? newTile = terraform.Direction switch
            {
                TerraformDirection.Wetter => currentTile.ShiftWetter(),
                TerraformDirection.Drier => currentTile.ShiftDrier(),
                TerraformDirection.Balanced => currentTile.ShiftBalanced(),
                _ => null
            };

            if (newTile.HasValue)
                _worldManager.SetTile(targetX, targetY, newTile.Value);
        }
    }
}
