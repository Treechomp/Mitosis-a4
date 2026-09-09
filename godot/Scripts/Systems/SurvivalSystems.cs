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

    /// <summary>
    /// Global multiplier on every species' hunger decay. Below 1 it slows starvation across the
    /// board. Predators benefit most — they die almost entirely of starvation between kills
    /// (logs showed ~274 starvation vs ~11 predation deaths) — while continuously-grazing
    /// herbivores sit near full regardless, so this mainly raises the predator carrying capacity.
    ///
    /// Public because it is half of what an animal costs the world to keep alive, and
    /// <see cref="ECS.HabitatCapacity"/> has to read the same number the simulation uses rather
    /// than a copy of it.
    /// </summary>
    public const float HungerDecayScale = 0.3f;

    public void Process(EntityManager em)
    {
        _toKill.Clear();
        const ComponentFlags required = ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            // Skip structures — nests, crystals and mycelium hearts are buildings, not bodies.
            // They now carry no Energy at all (health lives on Structure), so this query could
            // never reach them anyway; the flag test stays as the explicit statement of intent,
            // and catches any future structure that does grow an Energy component.
            if (em.HasComponents(entity, ComponentFlags.Structure) ||
                em.HasComponents(entity, ComponentFlags.Nest) ||
                em.HasComponents(entity, ComponentFlags.Crystal))
                continue;

            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: compensate for skipped ticks so rates stay correct
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var hunger = ref em.Hungers[entity];

            // Decay hunger — hibernating Sectids run a low metabolism while dormant at their nest,
            // and sit-and-wait ambushers (e.g. Scorpion) idle their metabolism while lurking, so
            // they can wait out lean patches motionless.
            float decayRate = hunger.DecayRate * HungerDecayScale;
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier) && em.FoodCarriers[entity].IsHibernating)
                decayRate *= 0.1f;
            else if (em.HasComponents(entity, ComponentFlags.Predator) && em.Predators[entity].IsDormant)
                decayRate *= 0.2f;
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
            // Skip structures — nests, crystals and mycelium hearts are buildings, not bodies.
            // They now carry no Energy at all (health lives on Structure), so this query could
            // never reach them anyway; the flag test stays as the explicit statement of intent,
            // and catches any future structure that does grow an Energy component.
            if (em.HasComponents(entity, ComponentFlags.Structure) ||
                em.HasComponents(entity, ComponentFlags.Nest) ||
                em.HasComponents(entity, ComponentFlags.Crystal))
                continue;

            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: age at correct rate regardless of update frequency
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

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
    private readonly Mitosis.Utils.SpatialHash? _spatialHash;
    // Fungivore consumption: entities eaten this tick (destroyed after the main loop), and a
    // claim set so two fungivores don't both feed on the same spore before it's removed.
    private readonly List<int> _eaten = new(32);
    private readonly HashSet<int> _claimed = new();
    private readonly List<int> _fungivoreBuffer = new(16);

    public GrazingSystem(World.WorldManager worldManager, Mitosis.Utils.SpatialHash? spatialHash = null)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
    }

    /// <summary>
    /// What a tile at full fertility pays this species per tick. Derived, never authored: it is the
    /// animal's own hunger drain divided by the ground fullness at which it should just hold its
    /// condition. Authoring the payout separately is what let a species be fed by ground that was
    /// nearly bare — the two numbers drifted apart and hunger stopped meaning anything.
    ///
    /// Takes the entity's own decay rate rather than the definition's, so per-individual variation
    /// flows through: a hungrier animal needs richer ground, which is the same statement.
    /// </summary>
    private static float FullGroundPayout(SpeciesDefinition def, float decayRate)
        => decayRate * HungerSystem.HungerDecayScale / MathF.Max(0.01f, def.BreakEvenFullness);

    /// <summary>
    /// How full this tile is against its own biome cap, 0..1. Arid soil at its cap is full ground
    /// for whatever can live on it; the cap is what differs, not the meaning of "full".
    /// </summary>
    private static float Fullness(TileType tile, float nutrition)
    {
        float cap = tile.NutritionCap();
        return cap > 0f ? MathF.Min(1f, nutrition / cap) : 0f;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species | ComponentFlags.Hunger;
        // Nutrition stripped per grazing tick is per-species now (GrazeConsumeRate) — a rabbit
        // and a deer no longer press the same pasture at the same rate.
        _eaten.Clear();
        _claimed.Clear();

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // LOD tick multiplier: consume/gain food at correct rate
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var species = ref em.Species[entity];
            ref var pos = ref em.Positions[entity];
            ref var hunger = ref em.Hungers[entity];
            var tile = _worldManager.GetTile(pos.X, pos.Y);

            // Standard herbivore grazing — nutrition-dependent
            // Omnivores graze too, at whatever break-even their definition sets
            if (species.Type == SpeciesType.Herbivore || species.Type == SpeciesType.Omnivore)
            {
                var herbDef = SpeciesRegistry.GetById(species.SpeciesId);
                // A full animal stops. It used to keep stripping the ground it stood on and throw
                // the food away against the hunger clamp, which destroyed most of what grazers
                // took from the world and meant a herd wore out its pasture for nothing.
                bool hungry = hunger.Current < hunger.Max;
                if (hungry && herbDef.CanGraze && tile.IsGrazeable())
                {
                    // Check tile nutrition as THIS species values it; depleted tiles yield less
                    // food, and so does ground this species is poorly suited to feed on.
                    float grazeYield = TerrainProfile.ForageYield(herbDef, tile);
                    float rawNutrition = _worldManager.GetNutrition(pos.X, pos.Y);
                    if (rawNutrition * grazeYield > 0.05f)
                    {
                        float requested = herbDef.GrazeConsumeRate * tickMult;
                        _worldManager.ConsumeNutrition(pos.X, pos.Y, requested);
                        // What the mouthful is worth depends on how full the ground is, not on
                        // whether the mouthful was available: ground at a third of its cap feeds a
                        // third as well. The old ratio only fell once a tile held less than one
                        // bite, which is below the level every species is already steered off, so
                        // worn ground cost an animal nothing and hunger tracked nothing.
                        float fullness = Fullness(tile, rawNutrition);
                        // The yield scales the payout and not the draw, so poor forage means more
                        // ground stripped per unit of hunger — which is what makes a species need
                        // more of the pasture it is badly suited to than of the pasture it is not.
                        float foodGained = FullGroundPayout(herbDef, hunger.DecayRate) * fullness * grazeYield;
                        hunger.Current = MathF.Min(hunger.Max, hunger.Current + foodGained * tickMult);
                    }
                }
                // FeedTiles: species that feed from specific tiles (e.g. Fish from water). With a
                // FeedConsumeRate they strip that tile's fertility exactly as a grazer strips
                // pasture, and are paid in proportion to what they actually got — so a shoal eats
                // its patch of water down and has to move on. Without one they feed for free
                // (unchanged behaviour, still used as a subsistence floor elsewhere).
                else if (hungry && herbDef.FeedTiles != null && herbDef.FeedTiles.Contains(tile))
                {
                    // Same exchange-rate rule as grazing: a feeding tile is worth what this
                    // species can get out of it, so a shoal can prefer reef to open water.
                    float feedYield = TerrainProfile.ForageYield(herbDef, tile);
                    if (herbDef.FeedConsumeRate > 0f)
                    {
                        float rawNutrition = _worldManager.GetNutrition(pos.X, pos.Y);
                        float requested = herbDef.FeedConsumeRate * tickMult;
                        _worldManager.ConsumeNutrition(pos.X, pos.Y, requested);
                        // A shoal in water it has eaten down is fed in proportion to what is left,
                        // exactly as a herd on worn pasture is.
                        hunger.Current = MathF.Min(hunger.Max,
                            hunger.Current + FullGroundPayout(herbDef, hunger.DecayRate)
                                             * Fullness(tile, rawNutrition) * feedYield * tickMult);
                    }
                    else
                    {
                        hunger.Current = MathF.Min(hunger.Max,
                            hunger.Current + herbDef.FeedNutrition * feedYield * tickMult);
                    }
                }

                // Fungivory: eat nearby Shroomer spores / immature Shroomers (bloom control).
                // Independent of grazing, so a fungivore culls sprouts even off a grazeable tile.
                if (herbDef.IsFungivore && _spatialHash != null && hunger.Percent < 0.98f)
                    FungivoreFeed(em, entity, herbDef, ref hunger);
                continue;
            }

            // Faction species: feed from their preferred tiles
            if (species.Type == SpeciesType.Shroomer ||
                species.Type == SpeciesType.Sectid ||
                species.Type == SpeciesType.Faeling)
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);

                // Fertility feeding (Shroomers): growth fuel comes from tile nutrition, consumed
                // faster than herbivores and gained in proportion to what's left — so a bloom only
                // grows where there's fertility to strip, and depletes the land as it does. This
                // runs FIRST; the FeedTiles value below is only a subsistence floor.
                if (speciesDef.FertilityConsumeRate > 0f && tile.IsGrazeable())
                {
                    float nutrition = _worldManager.GetNutrition(pos.X, pos.Y);
                    if (nutrition > 0.05f)
                    {
                        // Appetite scales with body size: a hulking elder strips the ground far
                        // faster than a sprout, so a bloom's drain accelerates as it matures and
                        // it exhausts its patch — and must advance — sooner the longer it stands.
                        float sizeFactor = em.HasComponents(entity, ComponentFlags.Growth)
                            ? em.Growths[entity].CurrentScale : 1f;
                        float requested = speciesDef.FertilityConsumeRate * sizeFactor * tickMult;
                        float consumed = _worldManager.ConsumeNutrition(pos.X, pos.Y, requested);
                        float richness = requested > 0f ? consumed / requested : 0f;
                        hunger.Current = MathF.Min(hunger.Max,
                            hunger.Current + speciesDef.FertilityFeedNutrition * richness * sizeFactor * tickMult);
                    }
                }

                // Subsistence floor: FeedTiles (e.g. Shroomers on their own barren swamp) keep a
                // creature alive but — kept low for fertility feeders — can't fuel spore spread on
                // stripped ground, so fertility is what actually drives a bloom.
                if (speciesDef.FeedTiles != null && speciesDef.FeedTiles.Contains(tile))
                {
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + speciesDef.FeedNutrition * tickMult);
                }
            }
        }

        // Remove everything eaten this tick (deferred so we never destroy mid-query). The death
        // hook drops a corpse for immature Shroomers (scraps for scavengers); spores self-filter.
        if (_eaten.Count > 0)
            em.DestroyEntities(_eaten);
    }

    /// <summary>
    /// Consume the nearest unclaimed Shroomer spore or immature Shroomer within reach, restoring
    /// hunger. Spores are always edible; Shroomers only up to the species' FungivoreMaxScale
    /// (below the AoE/thorn danger band). Eating is grazing-adjacent — no attack, no thorns.
    /// </summary>
    private void FungivoreFeed(EntityManager em, int entity, SpeciesDefinition def,
        ref Hunger hunger)
    {
        ref var pos = ref em.Positions[entity];
        _fungivoreBuffer.Clear();
        _spatialHash!.QueryRadius(pos.X, pos.Y, def.FungivoreFeedRadius, _fungivoreBuffer);

        int best = -1;
        float bestDistSq = float.MaxValue;
        bool bestIsShroomer = false;
        foreach (int other in _fungivoreBuffer)
        {
            if (other == entity || _claimed.Contains(other) || !em.IsAlive(other)) continue;

            bool isSpore = em.HasComponents(other, ComponentFlags.Spore);
            bool isEdibleShroomer = false;
            if (!isSpore && def.FungivoreEatsSprouts
                && em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Growth))
            {
                if (em.Species[other].Type == SpeciesType.Shroomer &&
                    em.Growths[other].CurrentScale <= def.FungivoreMaxScale)
                    isEdibleShroomer = true;
            }
            if (!isSpore && !isEdibleShroomer) continue;

            ref var otherPos = ref em.Positions[other];
            float d = MathUtils.DistanceSquared(pos.X, pos.Y, otherPos.X, otherPos.Y);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = other;
                bestIsShroomer = isEdibleShroomer;
            }
        }

        if (best < 0) return;
        _claimed.Add(best);
        _eaten.Add(best);
        // Discrete meal (one item), so — unlike continuous grazing — it is NOT scaled by the
        // LOD tick multiplier. Spores are a smaller mouthful than a sprouted Shroomer.
        float food = def.FungivoreFeedAmount * (bestIsShroomer ? 1f : 0.5f);
        hunger.Current = MathF.Min(hunger.Max, hunger.Current + food);

        // Log immature-Shroomer consumption as a kill so bloom control is measurable in the
        // events CSV; spores are far too numerous to log individually.
        if (bestIsShroomer && em.HasComponents(entity, ComponentFlags.Species))
        {
            ref var eaterSp = ref em.Species[entity];
            ref var victimSp = ref em.Species[best];
            ref var victimPos = ref em.Positions[best];
            EcosystemLogger.Instance?.LogKill(
                eaterSp.SpeciesId, victimSp.SpeciesId, entity, best, victimPos.X, victimPos.Y);
        }
    }
}
