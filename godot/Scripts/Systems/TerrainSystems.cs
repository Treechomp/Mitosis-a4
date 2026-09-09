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

    /// <summary>
    /// Ticks' worth of accrual a tile sustains: discomfort rises toward (rate × this) rather than
    /// piling up without limit, so every tile has a settling level and only ground whose level
    /// clears the escape threshold ever drives a creature off it.
    ///
    /// This is what makes "disliked but liveable" expressible at all. Under pure accumulation ANY
    /// net-positive rate reached the ceiling given enough time, so a mild dislike and lethal
    /// ground differed only in how many ticks they took to lock a creature into a permanent
    /// escape — the reported sharks-fleeing-their-own-feeding-grounds bug, and equally why grazers
    /// could not settle in a wetland. At scale 20: Wetland (0.5/tick) settles at 10 ≈ 0.2 of a
    /// typical threshold and is simply tolerated; Tundra (2) settles at 40 ≈ 0.8 and pushes a
    /// creature on; Mountain (15) pins to the ceiling within a few ticks, as before.
    /// </summary>
    private const float DiscomfortEquilibriumScale = 20f;

    /// <summary>
    /// Fraction of the remaining gap to the settling level closed per tick. 0.05 gives roughly the
    /// old time-to-escape on genuinely hostile ground (Mountain ~4 ticks, Ice ~20) while making
    /// the approach asymptotic instead of linear.
    /// </summary>
    private const float DiscomfortApproachRate = 0.05f;

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
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var pos = ref em.Positions[entity];
            ref var discomfort = ref em.TerrainDiscomforts[entity];

            // Get current tile. The accrual rate is resolved species-aware (TerrainProfile), so an
            // aquatic creature is at home in water instead of being punished by a tile table
            // written from a land animal's point of view.
            var tile = _worldManager.GetTile(pos.X, pos.Y);
            bool inWater = tile.IsWater();

            SpeciesDefinition? speciesDef = null;
            float tileDiscomfort;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                speciesDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                // Flying creatures ignore terrain discomfort entirely
                if (speciesDef != null && speciesDef.IsFlying)
                {
                    discomfort.Current = MathF.Max(0, discomfort.Current - discomfort.DecayRate);
                    discomfort.WrongElementTicks = 0;
                    continue;
                }
                tileDiscomfort = speciesDef != null
                    ? TerrainProfile.DiscomfortRate(speciesDef, tile)
                    : tile.GetDiscomfortRate();
            }
            else
            {
                tileDiscomfort = tile.GetDiscomfortRate();
            }

            // Add grazing pressure for hungry herbivores on non-grazeable or depleted terrain
            if (discomfort.GrazingPressure > 0 && em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                float hungerFactor = 1f - (hunger.Current / hunger.Max);

                // "Can I eat where I'm standing" is species-specific: a shoal feeds on the water
                // column, which the grazeable-tile test would call barren ground.
                bool feedsHere = speciesDef != null
                    ? ((speciesDef.CanGraze || speciesDef.FertilityConsumeRate > 0f) && tile.IsGrazeable()
                       || speciesDef.FeedConsumeRate > 0f && speciesDef.FeedTiles != null
                          && speciesDef.FeedTiles.Contains(tile))
                      // Ground this species cannot make a living on is not "food here", however
                      // edible the tile type is in general.
                      && TerrainProfile.ForageYield(speciesDef, tile) > 0f
                    : tile.IsGrazeable();

                if (!feedsHere)
                {
                    // Nothing here to eat at all — full pressure
                    tileDiscomfort += discomfort.GrazingPressure * hungerFactor;
                }
                else
                {
                    // Edible but possibly depleted — pressure scales with depletion, reaching
                    // full strength on ground that is stripped bare. To a hungry grazer that is
                    // exactly as useless as ground that never grew anything, and since discomfort
                    // now settles at a level instead of piling up, a discounted pressure would
                    // simply never reach the escape threshold — stranding herds on dead pasture.
                    // As this species values it, so the move-on pressure and the foraging
                    // steering agree about which ground is finished.
                    float nutrition = speciesDef != null
                        ? TerrainProfile.EffectiveNutrition(
                            speciesDef, tile, _worldManager.GetNutrition(pos.X, pos.Y))
                        : _worldManager.GetNutrition(pos.X, pos.Y);
                    if (nutrition < 0.3f)
                    {
                        float depletionFactor = 1f - (nutrition / 0.3f);
                        tileDiscomfort += discomfort.GrazingPressure * hungerFactor * depletionFactor;
                    }
                }
            }

            // The level this ground settles at, capped at the ceiling. Discomfort was unbounded,
            // so a creature that crossed genuinely hostile ground (Mountain accrues 15/tick, Lava
            // 20) banked a debt in the thousands — ratios of 4000%+ were observed — and then could
            // not pay it off: escape only clears below ratio 0.1, and HuntingSystem refuses to
            // hold a target while the ratio exceeds its tolerance. The result was a creature
            // permanently locked in "escape", never hunting again however hungry, though still
            // able to feed from carcasses (scavenging does not consult discomfort). Nothing needs
            // a ratio above the ceiling: every consumer asks "how far past threshold am I", and
            // the highest tolerance in play is ~2.1.
            float settlingLevel = MathF.Min(tileDiscomfort * DiscomfortEquilibriumScale,
                                            discomfort.Threshold * MaxDiscomfortRatio);

            // Approach it asymptotically while it is above us, shed it at the species' own decay
            // rate once we're on better ground. LOD-compensated so a distant creature builds and
            // sheds at the same real-time rate as one under the camera (the approach fraction is
            // clamped so a coarse LOD tier can't overshoot past the target).
            if (settlingLevel > discomfort.Current)
            {
                float approach = MathF.Min(1f, DiscomfortApproachRate * tickMult);
                discomfort.Current += (settlingLevel - discomfort.Current) * approach;
            }
            else
            {
                discomfort.Current = MathF.Max(settlingLevel,
                    discomfort.Current - discomfort.DecayRate * tickMult);
            }

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
                    isDrowning = tile.IsSubmerged();   // includes Reef — see TerrainProfile
                else
                    isDrowning = tile.IsDeepWater();
                // Reef counts as submerged: a shark chasing a fish over coral is still in the sea.
                bool isSuffocating = speciesDef.IsAquatic && !tile.IsSubmerged();

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
    private readonly Random _rng = SimRandom.Create();

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
                ? em.SimulationLODs[entity].EffectiveInterval : 1;

            ref var terraform = ref em.Terraforms[entity];

            // RATE-LIKE — compensate. The terraform cooldown measures out a NUMBER OF ACTS per
            // unit of world time, so a due tick that stands for 20 ticks owes 20 ticks' worth of
            // acts. The old code consumed the whole window in one countdown and then rolled once
            // regardless: at Cooldown 4 a Full-tier terraformer rolls five times per 20 ticks and
            // a Minimal-tier one rolled once, so how fast the world was reshaped depended on
            // where the player was standing. Measured on shroomer_bloom: 872 nudges at Full
            // against 157 at Minimal over 3,000 ticks.
            //
            // Spend the elapsed ticks as credit against the roll period and carry the remainder,
            // so the long-run rate is exact rather than merely closer. The period is Cooldown + 1,
            // not Cooldown: a roll costs its own tick on top of the countdown, which is what the
            // Full-tier path has always done. Using Cooldown here would quietly speed Full-tier
            // terraforming up by a fifth — the point is to bring Minimal up to Full, not to
            // retune the game.
            terraform.CurrentCooldown -= tickMult;
            if (terraform.CurrentCooldown > 0)
                continue;

            int period = Math.Max(1, terraform.Cooldown + 1);
            int overshoot = -terraform.CurrentCooldown;
            int rolls = 1 + overshoot / period;
            terraform.CurrentCooldown = period - overshoot % period;

            ref var pos = ref em.Positions[entity];

            // Larger individuals shift more ground per act, so a mature bloom actually opens new
            // habitat ahead of itself instead of only maintaining the tile it stands on.
            float sizeScale = em.HasComponents(entity, ComponentFlags.Growth)
                ? em.Growths[entity].CurrentScale : 1f;

            // Each owed act is rolled separately — its own strength check and its own target
            // choice. Batching them into one roll with a scaled probability would put the whole
            // window's worth of moisture on a single tile, and the 50/50 split below is what
            // spreads a bloom into a patch instead of a stripe.
            for (int roll = 0; roll < rolls; roll++)
            {
                // Roll against strength probability
                if ((float)_rng.NextDouble() > terraform.Strength)
                    continue;

                // Half the time, work the ground directly underfoot; otherwise a random tile in
                // radius. Purely random placement meant a terraformer could not reliably maintain
                // or convert the tile it was actually standing on: with radius 2 that is ~1 chance
                // in 20 per attempt, so a Shroomer at the edge of its swamp starved for substrate
                // long before it could turn the neighbouring grass into ground it could live on.
                // Working underfoot is what lets a slow frontier advance exist at all — the bloom
                // converts where it stands, then steps forward — while the random half still
                // spreads the influence outward into a patch rather than a single tile.
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
                // continuous colour follow from the new params (docs/archive/3d-terrain-plan-2026-06.md Phase 2b).
                _worldManager.Terraform(targetX, targetY, terraform.Direction, MoistureStep * sizeScale);
            }
        }
    }
}

/// <summary>
/// Regenerates tile fertility (pasture and, since water gained a nutrition cap, the water column)
/// across the loaded world. The per-tile rate is very slow, so depleted areas take many ticks to
/// recover — which is what drives migration rather than a static carrying capacity.
///
/// Cost is managed in two ways, because a full pass is a 1024-tile scan per chunk and the number
/// of chunks holding depleted ground went up sharply once shoals started stripping water:
///  1. Chunks that are fully topped up carry a <c>_hasDepleted</c> flag and skip the scan entirely.
///  2. The remaining work is spread round-robin over <see cref="RegenSweepPasses"/> passes, so any
///     single tick touches only a slice of the world.
/// Neither changes the net rate: the step is multiplied by exactly the time between visits, so a
/// tile still gains the same fertility per tick on average — it just arrives in fewer, larger
/// increments, which is invisible against a variable that takes ~2000 ticks to refill.
/// </summary>
public sealed class TileRegenerationSystem : ISystem
{
    private readonly WorldManager _worldManager;
    private int _tickCounter;
    private int _sweepCursor;

    /// <summary>Ticks between regeneration passes.</summary>
    private const int RegenInterval = 4;

    /// <summary>
    /// How many passes one full sweep of the world is spread across. Combined with RegenInterval,
    /// a given chunk is visited every 32 ticks and steps forward by 32× the base rate.
    /// </summary>
    private const int RegenSweepPasses = 8;

    public TileRegenerationSystem(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        _tickCounter++;
        if (_tickCounter % RegenInterval != 0)
            return;

        int total = _worldManager.LoadedChunkCount;
        if (total == 0)
            return;

        // Slice bounds for this pass. The cursor walks the chunk collection in its natural order;
        // that order only shifts if chunks are added, and a chunk visited twice (or skipped once)
        // in that rare case is harmless for a variable this slow.
        int sliceSize = (total + RegenSweepPasses - 1) / RegenSweepPasses;
        int start = _sweepCursor * sliceSize;
        if (start >= total)
        {
            _sweepCursor = 0;
            start = 0;
        }
        int end = Math.Min(start + sliceSize, total);
        _sweepCursor = (_sweepCursor + 1) % RegenSweepPasses;

        const int stepTicks = RegenInterval * RegenSweepPasses;
        float regenerated = 0f;
        int index = 0;
        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            if (index >= end) break;
            if (index++ < start) continue;
            regenerated += chunk.RegenerateNutrition(stepTicks);
        }
        if (regenerated > 0f)
            EcosystemLogger.Instance?.CountNutritionRegen(regenerated);
    }
}
