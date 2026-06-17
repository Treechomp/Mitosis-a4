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
/// Processes hunger decay and starvation damage.
/// </summary>
public sealed class HungerSystem : ISystem
{
    private readonly List<int> _toKill = new(32);

    // Global multiplier on every species' hunger decay. Below 1 it slows starvation across the
    // board. Predators benefit most — they die almost entirely of starvation between kills
    // (logs showed ~274 starvation vs ~11 predation deaths) — while continuously-grazing
    // herbivores sit near full regardless, so this mainly raises the predator carrying capacity.
    private const float HungerDecayScale = 0.3f;

    public void Process(EntityManager em)
    {
        _toKill.Clear();
        const ComponentFlags required = ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            // Skip structures (nests, crystals) — they don't eat
            if (em.HasComponents(entity, ComponentFlags.Nest) ||
                em.HasComponents(entity, ComponentFlags.Crystal))
                continue;

            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: compensate for skipped ticks so rates stay correct
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var hunger = ref em.Hungers[entity];

            // Decay hunger — hibernating Sectids run a low metabolism while dormant at their nest
            float decayRate = hunger.DecayRate * HungerDecayScale;
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier) && em.FoodCarriers[entity].IsHibernating)
                decayRate *= 0.1f;
            hunger.Current -= decayRate * tickMult;

            // Starvation damage
            if (hunger.IsStarving && em.HasComponents(entity, ComponentFlags.Energy))
            {
                ref var energy = ref em.Energies[entity];
                energy.Current -= hunger.StarvationDamage * tickMult;

                if (energy.IsDead)
                {
                    _toKill.Add(entity);
                    if (em.HasComponents(entity, ComponentFlags.Species | ComponentFlags.Position))
                    {
                        ref var sp = ref em.Species[entity];
                        ref var p = ref em.Positions[entity];
                        EcosystemLogger.Instance?.LogStarvation(sp.SpeciesId, entity, p.X, p.Y);
                    }
                }
            }

            // Conditional energy regen: only when not starving and out of combat
            if (!hunger.IsStarving && em.HasComponents(entity, ComponentFlags.Energy | ComponentFlags.Species))
            {
                ref var energy = ref em.Energies[entity];
                if (energy.RegenCooldown > 0)
                {
                    energy.RegenCooldown -= tickMult;
                }
                else if (energy.Current < energy.Max)
                {
                    ref var species = ref em.Species[entity];
                    var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                    energy.Current = MathF.Min(energy.Max, energy.Current + speciesDef.EnergyRegenRate * tickMult);
                }
            }

            // Venom DOT: apply damage and decrement timer
            if (em.HasComponents(entity, ComponentFlags.VenomEffect | ComponentFlags.Energy))
            {
                ref var venom = ref em.VenomEffects[entity];
                ref var energy = ref em.Energies[entity];
                energy.Current -= venom.DamagePerTick * tickMult;
                venom.RemainingTicks -= tickMult;
                if (venom.RemainingTicks <= 0)
                    em.RemoveComponent(entity, ComponentFlags.VenomEffect);
                if (energy.IsDead)
                    _toKill.Add(entity);
            }
        }

        foreach (int entity in _toKill)
        {
            // Corpse is left automatically via the ECS death hook (CarrionSystem).
            // Faeling death from starvation: pass power to crystal
            if (em.HasComponents(entity, ComponentFlags.FaelingPower))
            {
                ref var power = ref em.FaelingPowers[entity];
                int crystalId = power.LinkedCrystal;
                if (crystalId >= 0 && em.IsAlive(crystalId) && em.HasComponents(crystalId, ComponentFlags.Crystal))
                {
                    ref var crystal = ref em.Crystals[crystalId];
                    crystal.InheritedPower = power.Power * 0.5f;
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = crystal.SpawnDelay;
                }
            }

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
            // Skip structures (nests, crystals don't age)
            if (em.HasComponents(entity, ComponentFlags.Nest) ||
                em.HasComponents(entity, ComponentFlags.Crystal))
                continue;

            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: age at correct rate regardless of update frequency
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var age = ref em.Ages[entity];
            age.Current += tickMult;

            // Natural death from old age
            if (age.Current >= age.MaxLifespan)
            {
                _toKill.Add(entity);
                if (em.HasComponents(entity, ComponentFlags.Species | ComponentFlags.Position))
                {
                    ref var sp = ref em.Species[entity];
                    ref var p = ref em.Positions[entity];
                    EcosystemLogger.Instance?.LogAgeDeath(sp.SpeciesId, entity, p.X, p.Y);
                }
            }
        }

        foreach (int entity in _toKill)
        {
            // Corpse is left automatically via the ECS death hook (CarrionSystem).
            // Faeling death: pass power to crystal for next spawn
            if (em.HasComponents(entity, ComponentFlags.FaelingPower))
            {
                ref var power = ref em.FaelingPowers[entity];
                int crystalId = power.LinkedCrystal;
                if (crystalId >= 0 && em.IsAlive(crystalId) && em.HasComponents(crystalId, ComponentFlags.Crystal))
                {
                    ref var crystal = ref em.Crystals[crystalId];
                    crystal.InheritedPower = power.Power * 0.5f; // Half power inheritance
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = crystal.SpawnDelay;
                }
            }

            em.DestroyEntity(entity);
        }
    }
}

/// <summary>
/// Herbivores graze on grass/forest tiles to restore hunger.
/// Faction species (Shroomer, Sectid, Faeling) feed from their preferred tile types.
/// </summary>
public sealed class GrazingSystem : ISystem
{
    private readonly World.WorldManager _worldManager;

    public GrazingSystem(World.WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species | ComponentFlags.Hunger;
        // Amount of nutrition consumed from a tile per grazing tick
        const float nutritionConsumeRate = 0.02f;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: consume/gain food at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

            ref var species = ref em.Species[entity];
            ref var pos = ref em.Positions[entity];
            ref var hunger = ref em.Hungers[entity];
            var tile = _worldManager.GetTile(pos.X, pos.Y);

            // Standard herbivore grazing — nutrition-dependent
            // Omnivores also graze but at whatever GrazeNutrition their definition sets
            if (species.Type == SpeciesType.Herbivore || species.Type == SpeciesType.Omnivore)
            {
                var herbDef = SpeciesRegistry.GetById(species.SpeciesId);
                if (herbDef.CanGraze && tile.IsGrazeable())
                {
                    // Check tile nutrition; depleted tiles yield less food
                    float nutrition = _worldManager.GetNutrition(pos.X, pos.Y);
                    if (nutrition > 0.05f)
                    {
                        float requested = nutritionConsumeRate * tickMult;
                        float consumed = _worldManager.ConsumeNutrition(pos.X, pos.Y, requested);
                        // Food gained scales with tile nutrition level. Guard the ratio:
                        // requested can only be 0 if tickMult is 0, which would make this 0/0 = NaN.
                        float richness = requested > 0f ? consumed / requested : 0f;
                        float foodGained = herbDef.GrazeNutrition * richness;
                        hunger.Current = MathF.Min(hunger.Max, hunger.Current + foodGained * tickMult);
                    }
                }
                // FeedTiles fallback: species that feed from specific tiles (e.g. Fish from water)
                else if (herbDef.FeedTiles != null && herbDef.FeedTiles.Contains(tile))
                {
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + herbDef.FeedNutrition * tickMult);
                }
                continue;
            }

            // Faction species: feed from their preferred tiles
            if (species.Type == SpeciesType.Shroomer ||
                species.Type == SpeciesType.Sectid ||
                species.Type == SpeciesType.Faeling)
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                if (speciesDef.FeedTiles != null && speciesDef.FeedTiles.Contains(tile))
                {
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + speciesDef.FeedNutrition * tickMult);
                }
            }
        }
    }
}
