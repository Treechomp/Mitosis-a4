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

    /// <summary>
    /// Ceiling on accumulated discomfort, as a multiple of the species' threshold. Sits above the
    /// largest tolerance any consumer applies (a committed swarm hunter tolerates ~2.1) so the
    /// "this ground is intolerable" signal still discriminates, while keeping recovery bounded:
    /// at a typical threshold 50 and decay 2/tick a creature clears the cap in about 70 ticks.
    /// </summary>
    private const float MaxDiscomfortRatio = 3f;

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

            // LOD tick multiplier: drowning/suffocation must accrue at the correct RATE regardless
            // of LOD, or a beached aquatic at a coarse tier barely takes damage and roams the land
            // near-immortally instead of suffocating.
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].TickInterval : 1;

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

            // Accumulate or decay discomfort, LOD-compensated so a distant creature builds and
            // sheds it at the same real-time rate as one under the camera.
            if (tileDiscomfort > 0)
                discomfort.Current += tileDiscomfort * tickMult;
            else
                discomfort.Current = MathF.Max(0, discomfort.Current - discomfort.DecayRate * tickMult);

            // Cap the accumulation. Discomfort was unbounded, so a creature that crossed genuinely
            // hostile ground (Mountain accrues 15/tick, Lava 20) banked a debt in the thousands —
            // ratios of 4000%+ were observed — and then could not pay it off: escape only clears
            // below ratio 0.1, and HuntingSystem refuses to hold a target while the ratio exceeds
            // its tolerance. The result was a creature permanently locked in "escape", never
            // hunting again however hungry, though still able to feed from carcasses (scavenging
            // does not consult discomfort). Nothing needs a ratio above the cap: every consumer
            // asks "how far past threshold am I", and the highest tolerance in play is ~2.1.
            float maxDiscomfort = discomfort.Threshold * MaxDiscomfortRatio;
            if (discomfort.Current > maxDiscomfort)
                discomfort.Current = maxDiscomfort;

            // === Drowning / Suffocation ===
            if (speciesDef != null && em.HasComponents(entity, ComponentFlags.Energy))
            {
                // Depth-aware: aquatic/semi-aquatic never drown; non-swimmers (AvoidsWater, e.g.
                // insects) drown in any water; ordinary land creatures wade shallow/river safely
                // and only drown in deep water. Aquatic species suffocate out of any water.
                bool isDrowning;
                if (speciesDef.IsAquatic || speciesDef.SemiAquatic)
                    isDrowning = false;
                else if (speciesDef.AvoidsWater)
                    isDrowning = inWater;
                else
                    isDrowning = tile.IsDeepWater();
                bool isSuffocating = !inWater && speciesDef.IsAquatic;

                if (isDrowning || isSuffocating)
                {
                    discomfort.WrongElementTicks += tickMult;

                    if (discomfort.WrongElementTicks > speciesDef.WrongElementGraceTicks)
                    {
                        ref var energy = ref em.Energies[entity];
                        // Health barely helps against drowning/suffocation — you can't out-HP a lack
                        // of air (floor 0.7× damage even at full health, ramping to 1.0× as energy
                        // drops). LOD-compensated so the rate is the same at every tier.
                        float energyFactor = 1f - (energy.Percent * 0.3f);
                        energy.Current -= speciesDef.WrongElementDamageRate * energyFactor * tickMult;
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

        // Kill drowned/suffocated entities (corpses left automatically via the ECS death hook)
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

            // Nest-breeders (Sectids) no longer terraform while roaming — they move too fast to
            // leave a meaningful imprint. Their colonies reshape the land from the (stationary)
            // nest on each hatch instead (see NestSystem). Dormant carriers are inactive regardless.
            if (em.HasComponents(entity, ComponentFlags.Species)
                && SpeciesRegistry.GetById(em.Species[entity].SpeciesId).NestBreeder)
                continue;
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

            // Half the time, work the ground directly underfoot; otherwise a random tile in
            // radius. Purely random placement meant a terraformer could not reliably maintain or
            // convert the tile it was actually standing on: with radius 2 that is ~1 chance in 20
            // per attempt, so a Shroomer at the edge of its swamp starved for substrate long
            // before it could turn the neighbouring grass into ground it could live on. Working
            // underfoot is what lets a slow frontier advance exist at all — the bloom converts
            // where it stands, then steps forward — while the random half still spreads the
            // influence outward into a patch rather than a single tile.
            float targetX, targetY;
            if (_rng.NextDouble() < 0.5)
            {
                targetX = pos.X;
                targetY = pos.Y;
            }
            else
            {
                float offsetX = ((float)_rng.NextDouble() * 2f - 1f) * terraform.Radius;
                float offsetY = ((float)_rng.NextDouble() * 2f - 1f) * terraform.Radius;
                targetX = pos.X + offsetX;
                targetY = pos.Y + offsetY;
            }

            // Terraform nudges the moisture parameter; the tile's classification and its
            // continuous colour follow from the new params (docs/3d-terrain-plan.md Phase 2b).
            // Larger individuals shift more ground per act, so a mature bloom actually opens new
            // habitat ahead of itself instead of only maintaining the tile it stands on.
            float sizeScale = em.HasComponents(entity, ComponentFlags.Growth)
                ? em.Growths[entity].CurrentScale : 1f;
            _worldManager.Terraform(targetX, targetY, terraform.Direction, MoistureStep * sizeScale);
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

        float regenerated = 0f;
        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            regenerated += chunk.RegenerateNutrition(RegenInterval);
        }
        if (regenerated > 0f)
            EcosystemLogger.Instance?.CountNutritionRegen(regenerated);
    }
}
