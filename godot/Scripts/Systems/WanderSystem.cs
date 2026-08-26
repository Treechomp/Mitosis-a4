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
/// Processes wandering behavior for entities.
/// LOD-aware: only processes entities due for update.
/// Includes terrain avoidance and discomfort-driven escape behavior.
/// Uses angular interpolation for smooth turning (mass-based turn rates).
/// </summary>
public sealed class WanderSystem : ISystem
{
    private readonly Random _rng = SimRandom.Create();
    private readonly WorldManager? _worldManager;
    private readonly SpatialHash? _spatialHash;
    private readonly float _lookAheadDistance;
    private readonly float _roamArrivalDist;
    private readonly List<int> _nearbyBuffer = new(32);

    // Turn rate constants: turnRate = clamp(TurnRateScale / bodyMass, TurnRateMin, TurnRateMax)
    // Light creatures (~0.3 mass) turn quickly, heavy creatures (~14 mass) turn slowly.
    private const float TurnRateScale = 0.4f;
    private const float TurnRateMin = 0.06f;
    private const float TurnRateMax = 0.3f;

    // === Restoration search (TerraformDirection.Restore) ===
    /// <summary>Nearest sample distance as a fraction of roam distance, and the sampling step.</summary>
    private const float RestoreSeekNearFraction = 0.1f;
    /// <summary>Farthest sample, as a fraction of roam distance. Deliberately short — a keeper
    /// repairs what it can walk to; crossing the world is what crystal travel is for.</summary>
    private const float RestoreSeekFarFraction = 0.6f;
    /// <summary>
    /// Minimum damage score worth walking to. GetTerrainDamageScore is |current - pristine| x 4,
    /// so 0.2 is a moisture displacement of 0.05 — about a third of a biome band. Below that the
    /// ground is essentially as worldgen left it and a keeper has nothing to do there.
    /// </summary>
    private const float RestoreSeekMinScore = 0.2f;

    public WanderSystem(WorldManager? worldManager = null, float lookAheadDistance = 1.5f,
                        float roamArrivalDist = 5f, SpatialHash? spatialHash = null)
    {
        _worldManager = worldManager;
        _spatialHash = spatialHash;
        _lookAheadDistance = lookAheadDistance;
        _roamArrivalDist = roamArrivalDist;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Wander | ComponentFlags.Velocity | ComponentFlags.Position;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            // Hibernating Sectids stay dormant near their nest (movement handled by NestSystem)
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier) && em.FoodCarriers[entity].IsHibernating)
                continue;

            ref var wander = ref em.Wanders[entity];
            ref var vel = ref em.Velocities[entity];
            ref var pos = ref em.Positions[entity];

            // Get species definition for roaming parameters and turn rate
            SpeciesDefinition? wanderSpeciesDef = null;
            int speciesId = -1;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var sp = ref em.Species[entity];
                speciesId = sp.SpeciesId;
                wanderSpeciesDef = SpeciesRegistry.GetById(speciesId);
            }
            float roamDistance = wanderSpeciesDef?.RoamDistance ?? 60f;
            int roamCooldownBase = wanderSpeciesDef?.RoamCooldown ?? 500;
            float bodyMass = wanderSpeciesDef?.BodyMass ?? 1f;

            // Turn rate is a PER-TICK approach rate, but this system only runs on the entity's
            // decision cadence, so a distant creature was getting one 6%-of-the-way turn where a
            // nearby one got twenty. Compensating restores the same total turn per unit of world
            // time at every LOD tier — see DecisionCadence.
            int decisionInterval = DecisionCadence.Interval(em, entity);
            float turnRate = DecisionCadence.BlendRate(
                Math.Clamp(TurnRateScale / bodyMass, TurnRateMin, TurnRateMax), decisionInterval);

            // Terrain sampling reaches as far as this creature will actually travel before it
            // decides again, so it never coasts past ground it "checked".
            float lookAhead = DecisionCadence.Horizon(em, entity, _lookAheadDistance);

            // Skip if fleeing
            if (em.HasComponents(entity, ComponentFlags.Prey) && em.Preys[entity].IsFleeing)
                continue;

            // Skip if hunting (has active target)
            if (em.HasComponents(entity, ComponentFlags.Predator) && em.Predators[entity].HasTarget)
            {
                // Cancel roam if we just acquired a target
                if (wander.IsRoaming)
                {
                    wander.RoamTargetX = 0f;
                    wander.RoamTargetY = 0f;
                }
                continue;
            }

            // === Roaming logic ===
            // RATE-LIKE — compensate. RoamCooldown is a countdown in ticks ("how long before this
            // creature may pick a new destination"), so it must count the ticks that actually
            // passed. One decrement per due tick turned a 500-tick cooldown into 10,000 ticks at
            // Minimal: a distant herd that grazed its patch bare simply stood on it, because the
            // seek_food roam it was waiting on was twenty times further away than intended.
            // Clamped at 0 rather than allowed to overshoot negative — see the same guard in
            // HuntingSystem, where a stuck negative cooldown made prey unhittable.
            if (wander.RoamCooldown > 0)
                wander.RoamCooldown = Math.Max(0, wander.RoamCooldown - DecisionCadence.Elapsed(em, entity));

            // Determine hunger urgency for roaming behavior
            float hungerUrgency = 0f;
            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                float ratio = hunger.Current / hunger.Max;
                hungerUrgency = Math.Clamp(1f - ratio, 0f, 1f);  // 0 = full, 1 = starving
            }

            // Standing on ground that actively damages us (fungal drought) overrides both the
            // roam cooldown and every "am I hungry enough to bother" test. Being well fed is the
            // trap, not the safeguard: dry ground is the FERTILE ground, so a Shroomer that drifts
            // onto grass eats its fill, reports no hunger, and is therefore never eligible to
            // roam again — it dies at 100% hunger with its energy ground down by drought.
            bool onLethalGround = _worldManager != null && wanderSpeciesDef != null
                && IsLethalSubstrate(wanderSpeciesDef, _worldManager.GetTile(pos.X, pos.Y));

            // Ready to breed but standing somewhere this species cannot (a penguin at sea, which
            // must haul out onto the ice). Heading for that ground outranks the roam cooldown the
            // same way lethal ground does — otherwise a fed adult drifts offshore for hundreds of
            // ticks with nothing to do, and the colony only breeds when the sea happens to wash it
            // back onto land.
            bool needsBreedingGround = _worldManager != null && wanderSpeciesDef?.BreedingTiles != null
                && !wanderSpeciesDef.CanBreedOnTile(_worldManager.GetTile(pos.X, pos.Y))
                && IsBreedingReady(em, entity);

            // === Terrain escape state (hysteresis) ===
            // Decided HERE, before the roaming block, because roaming used to win outright: the
            // escape check sat after an early `continue`, so a creature that walked into a lake
            // while roaming never evaluated it at all and just kept swimming toward the target it
            // had picked on dry land. That is the reported "locked into the direction it already
            // had" — the escape urge was real, it simply never got a say in where to go.
            bool needsEscape = false;
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];

                // Wide gap prevents oscillation at terrain edges. Enter escape late (0.6) so
                // minor discomfort doesn't trigger; exit early (0.1) so the creature commits to
                // reaching safe terrain.
                if (!discomfort.IsEscaping && discomfort.Ratio > 0.6f)
                {
                    discomfort.IsEscaping = true;
                    if (_worldManager != null && EcosystemLogger.DecisionLoggingFor(speciesId))
                        EcosystemLogger.Instance!.LogDecision(speciesId, entity, pos.X, pos.Y,
                            "wander", "escape_start", FormattableString.Invariant(
                                $"tile={_worldManager.GetTile(pos.X, pos.Y)};discomfort={discomfort.Ratio:F2}"));
                }
                else if (discomfort.IsEscaping && discomfort.Ratio < 0.1f)
                {
                    discomfort.IsEscaping = false;
                    if (_worldManager != null && EcosystemLogger.DecisionLoggingFor(speciesId))
                        EcosystemLogger.Instance!.LogDecision(speciesId, entity, pos.X, pos.Y,
                            "wander", "escape_end", FormattableString.Invariant(
                                $"tile={_worldManager.GetTile(pos.X, pos.Y)}"));
                }
                needsEscape = discomfort.IsEscaping;
            }

            // Abandon a roam that is carrying us further through the ground we're fleeing. A
            // target on comfortable ground is kept — that one is already the way out.
            if (needsEscape && wander.IsRoaming && _worldManager != null && wanderSpeciesDef != null
                && !IsComfortableGround(wanderSpeciesDef,
                       _worldManager.GetTile(wander.RoamTargetX, wander.RoamTargetY)))
            {
                wander.RoamTargetX = 0f;
                wander.RoamTargetY = 0f;
            }

            // Check if we should start roaming
            if (!wander.IsRoaming
                && (wander.RoamCooldown <= 0 || onLethalGround || needsBreedingGround || needsEscape))
            {
                bool shouldRoam = onLethalGround || needsBreedingGround || needsEscape
                    || ShouldStartRoaming(entity, em, ref pos);
                if (shouldRoam)
                {
                    float targetX, targetY;
                    string roamReason;

                    // Retreat to survivable substrate. Takes priority over everything else —
                    // a creature being damaged by the ground it stands on has no more urgent
                    // business than getting off it.
                    if (onLethalGround
                        && TryFindSafeSubstrateTarget(pos.X, pos.Y, roamDistance, wanderSpeciesDef!,
                               out targetX, out targetY))
                    {
                        roamReason = "escape_substrate";
                    }
                    // Get off intolerable ground, heading for the NEAREST comfortable tile rather
                    // than nudging the current heading a little. The old per-tick steering looked
                    // only 1.5 tiles ahead, so anything more than a step from shore saw nothing
                    // but more of the same water and had no gradient to follow at all.
                    else if (needsEscape && wanderSpeciesDef != null
                        && TryFindComfortableTarget(pos.X, pos.Y, roamDistance, wanderSpeciesDef,
                               out targetX, out targetY))
                    {
                        roamReason = "escape_terrain";
                    }
                    // Haul out to breed: head for the nearest ground this species can raise young
                    // on. Only reached by a fed, mature adult off its breeding ground, so it never
                    // competes with feeding — hunger keeps a penguin at sea until it is full.
                    else if (needsBreedingGround
                        && TryFindTileTarget(pos.X, pos.Y, roamDistance, wanderSpeciesDef!.BreedingTiles!,
                               out targetX, out targetY))
                    {
                        roamReason = "seek_breeding_ground";
                    }
                    // Keepers (Faelings with an active dominance reading): besiege the locally
                    // dominant faction — roam to a standoff ring around its sensed hotspot,
                    // from which their balanced terraform dries/restores the substrate the
                    // winner depends on. Containment, not a suicide charge into elder AoE.
                    else if (wanderSpeciesDef != null
                        && wanderSpeciesDef.TerraformDir == TerraformDirection.Restore
                        && em.HasComponents(entity, ComponentFlags.FaelingPower)
                        && em.FaelingPowers[entity].KeeperFaction != 0
                        && TrySiegeTarget(ref em.FaelingPowers[entity], pos.X, pos.Y,
                               out targetX, out targetY))
                    {
                        // Target already set toward the siege line
                        roamReason = "keeper_siege";
                    }
                    // Restorers seek ground that has actually been moved off its pristine state
                    else if (wanderSpeciesDef != null
                        && wanderSpeciesDef.TerraformDir == TerraformDirection.Restore
                        && _worldManager != null
                        && TryFindDamagedTerrainTarget(pos.X, pos.Y, roamDistance, out targetX, out targetY))
                    {
                        // Target already set toward damaged terrain
                        roamReason = "restore_terrain";
                    }
                    // Hungry foragers steer toward the best nearby food instead of wandering blind:
                    // grazers/FeedTile species toward food tiles, and predators with a HuntTerrain
                    // (Penguin, Shark, Crocodile, Scorpion…) toward their hunting grounds when no
                    // prey is in range. The core fix for starving in place while food exists
                    // elsewhere — e.g. an inland penguin migrating to the coast to fish.
                    else if (wanderSpeciesDef != null
                        && _worldManager != null
                        && hungerUrgency > (1f - wanderSpeciesDef.ForageHungerThreshold)
                        && (wanderSpeciesDef.CanGraze || wanderSpeciesDef.FeedTiles != null
                            || wanderSpeciesDef.HuntTerrain != null)
                        && TryFindFoodTarget(pos.X, pos.Y, roamDistance, wanderSpeciesDef, out targetX, out targetY))
                    {
                        // Target already set toward food
                        roamReason = "seek_food";
                    }
                    else
                    {
                        // Default: random direction — but never deliberately set off toward ground
                        // that damages us. A specialist whose surroundings are all lethal finds no
                        // food target at all and falls through to here, so an unguarded random
                        // roam is exactly how a Shroomer that correctly refuses to FORAGE onto dry
                        // land still ends up walking out into it and dying of drought.
                        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                        float dist = roamDistance * (0.5f + (float)_rng.NextDouble() * 0.5f);
                        if (_worldManager != null && wanderSpeciesDef != null
                            && wanderSpeciesDef.DroughtDamage > 0f)
                        {
                            for (int attempt = 0; attempt < 8; attempt++)
                            {
                                float tx = pos.X + MathF.Cos(angle) * dist;
                                float ty = pos.Y + MathF.Sin(angle) * dist;
                                if (!IsLethalSubstrate(wanderSpeciesDef, _worldManager.GetTile(tx, ty)))
                                    break;
                                angle = (float)(_rng.NextDouble() * Math.PI * 2);
                                dist *= 0.75f; // pull the search in closer to safe ground
                            }
                        }
                        targetX = pos.X + MathF.Cos(angle) * dist;
                        targetY = pos.Y + MathF.Sin(angle) * dist;
                        roamReason = "random";
                    }

                    // Clamp to world bounds
                    int worldSize = _worldManager?.WorldSizeTiles ?? 512;
                    targetX = Math.Clamp(targetX, 2f, worldSize - 2f);
                    targetY = Math.Clamp(targetY, 2f, worldSize - 2f);

                    wander.RoamTargetX = targetX;
                    wander.RoamTargetY = targetY;

                    if (EcosystemLogger.DecisionLoggingFor(speciesId))
                        EcosystemLogger.Instance!.LogDecision(speciesId, entity, pos.X, pos.Y,
                            "wander", "roam_start", FormattableString.Invariant(
                                $"reason={roamReason};target={targetX:F0}:{targetY:F0};hunger_urgency={hungerUrgency:F2}"));
                }
            }

            // If roaming, move toward target
            if (wander.IsRoaming)
            {
                float dx = wander.RoamTargetX - pos.X;
                float dy = wander.RoamTargetY - pos.Y;
                float distSq = dx * dx + dy * dy;

                if (distSq < _roamArrivalDist * _roamArrivalDist)
                {
                    // Arrived — stop roaming, cooldown scales with urgency
                    wander.RoamTargetX = 0f;
                    wander.RoamTargetY = 0f;
                    // Hungry creatures re-roam faster (min 100 ticks when starving)
                    int cooldown = (int)(roamCooldownBase * (1f - hungerUrgency * 0.8f));
                    wander.RoamCooldown = cooldown + _rng.Next(cooldown / 3);

                    if (EcosystemLogger.DecisionLoggingFor(speciesId))
                        EcosystemLogger.Instance!.LogDecision(speciesId, entity, pos.X, pos.Y,
                            "wander", "roam_end", FormattableString.Invariant(
                                $"cooldown={wander.RoamCooldown};hunger_urgency={hungerUrgency:F2}"));
                }
                else
                {
                    float dist = MathF.Sqrt(distSq);
                    // Roam speed scales with hunger: base 1.5x up to 3x when starving
                    float roamMult = wander.RoamSpeedMultiplier + hungerUrgency * 1.5f;
                    float roamSpeed = wander.Speed * roamMult;

                    // Blend roam direction with terrain avoidance — but avoidance may only steer
                    // the route, never veto the destination.
                    var roamDir = new Vector2(dx / dist, dy / dist);
                    if (_worldManager != null)
                    {
                        var avoidance = GetTerrainAvoidance(pos.X, pos.Y, roamDir, wanderSpeciesDef, lookAhead);
                        if (avoidance.LengthSquared() > 0.01f)
                            roamDir = SteerWithoutReversing(roamDir, avoidance);
                    }

                    wander.CurrentDirection = roamDir;
                    BlendVelocitySmooth(ref vel, roamDir.X, roamDir.Y, roamSpeed, turnRate);
                    continue;  // Skip normal wander while roaming
                }
            }

            // === Normal wander logic ===

            // Escaping without a roam target (nothing comfortable within roam range): steer by the
            // local gradient instead. The blend weight is CLAMPED — it used to be the raw
            // discomfort ratio, which the 300% cap lets reach 3.0, giving the current heading a
            // weight of (1 - 3) = -2 and flipping it backwards instead of turning away from it.
            if (needsEscape)
            {
                var escapeDir = FindEscapeDirection(pos.X, pos.Y, wanderSpeciesDef, lookAhead);
                if (escapeDir.LengthSquared() > 0.01f)
                {
                    float escapeUrgency = Math.Clamp(
                        em.HasComponents(entity, ComponentFlags.TerrainDiscomfort)
                            ? em.TerrainDiscomforts[entity].Ratio : 1f, 0f, 1f);
                    wander.CurrentDirection = (wander.CurrentDirection * (1 - escapeUrgency) +
                                               escapeDir * escapeUrgency).Normalized();
                }
            }

            // Terrain avoidance (proactive - avoid entering bad terrain)
            if (_worldManager != null && !needsEscape)
            {
                var avoidance = GetTerrainAvoidance(pos.X, pos.Y, wander.CurrentDirection, wanderSpeciesDef, lookAhead);
                if (avoidance.LengthSquared() > 0.01f)
                {
                    wander.CurrentDirection = (wander.CurrentDirection + avoidance * 2f).Normalized();
                }
            }

            // Random direction change (less likely if escaping)
            float directionChangeChance = needsEscape ? wander.ChangeDirectionChance * 0.3f : wander.ChangeDirectionChance;
            if (MathUtils.RandomFloat(_rng) < directionChangeChance)
            {
                var newDir = MathUtils.RandomDirection(_rng);

                // If we have world manager, prefer safe directions
                if (_worldManager != null)
                {
                    float aheadAversion = WorstAversionAlong(wanderSpeciesDef, pos.X, pos.Y,
                                                             newDir.X, newDir.Y, lookAhead);

                    // If new direction leads to bad terrain, try to pick a safer one
                    if (aheadAversion > 0.3f)
                    {
                        float bestAvoidance = aheadAversion;
                        var bestDir = newDir;

                        for (int i = 0; i < 4; i++)
                        {
                            var testDir = MathUtils.RandomDirection(_rng);
                            float avoidWeight = WorstAversionAlong(wanderSpeciesDef, pos.X, pos.Y,
                                                                  testDir.X, testDir.Y, lookAhead);

                            if (avoidWeight < bestAvoidance)
                            {
                                bestAvoidance = avoidWeight;
                                bestDir = testDir;
                            }
                        }
                        newDir = bestDir;
                    }
                }

                wander.CurrentDirection = newDir;
            }

            BlendVelocitySmooth(ref vel, wander.CurrentDirection.X, wander.CurrentDirection.Y,
                                wander.Speed, turnRate);
        }
    }

    /// <summary>
    /// Determines if an entity should start a long-distance roam.
    /// Driven by food scarcity: predators roam when no prey is nearby,
    /// herbivores/terraformers roam when too many competitors share limited food.
    /// </summary>
    private bool ShouldStartRoaming(int entity, EntityManager em, ref Position pos)
    {
        if (_spatialHash == null) return false;

        // Faelings: always patrol — they're balance guardians that seek damaged terrain
        if (em.HasComponents(entity, ComponentFlags.Terraform | ComponentFlags.Species))
        {
            ref var sp = ref em.Species[entity];
            if (sp.Type == SpeciesType.Faeling)
                return _rng.NextDouble() < 0.04f; // ~25 tick average wait between patrols
        }

        // Predators: roam when hungry and no prey nearby
        if (em.HasComponents(entity, ComponentFlags.Predator | ComponentFlags.Hunger))
        {
            ref var hunger = ref em.Hungers[entity];
            float hungerRatio = hunger.Current / hunger.Max;

            // Only consider migrating once hungry enough to forage (per-species; a specialist that
            // must travel to its food forages sooner). Was a hardcoded 0.7.
            float forageThreshold = em.HasComponents(entity, ComponentFlags.Species)
                ? SpeciesRegistry.GetById(em.Species[entity].SpeciesId).ForageHungerThreshold : 0.7f;
            if (hungerRatio >= forageThreshold) return false;

            // Check if any eligible prey exists within hunt range
            ref var predator = ref em.Predators[entity];
            _nearbyBuffer.Clear();
            _spatialHash.QueryRadius(pos.X, pos.Y, predator.HuntRange, _nearbyBuffer);

            bool preyNearby = false;
            foreach (int other in _nearbyBuffer)
            {
                if (other != entity && em.IsAlive(other)
                    && em.HasComponents(other, ComponentFlags.Prey))
                {
                    preyNearby = true;
                    break;
                }
            }

            // No prey in hunt range — time to migrate
            if (!preyNearby)
            {
                // Scales with hunger: 3% at 70%, 8% at 30%, 15% near starvation
                float roamChance = 0.03f + (1f - hungerRatio) * 0.12f;
                return _rng.NextDouble() < roamChance;
            }
            return false;
        }

        // Herbivores / grazing factions: seek food when hungry, spread out when crowded.
        if (em.HasComponents(entity, ComponentFlags.Species | ComponentFlags.Hunger))
        {
            ref var hunger = ref em.Hungers[entity];
            float hungerRatio = hunger.Current / hunger.Max;

            ref var mySpecies = ref em.Species[entity];
            var myDef = SpeciesRegistry.GetById(mySpecies.SpeciesId);

            // Well-fed creatures stay put and graze locally (per-species forage threshold).
            if (hungerRatio >= myDef.ForageHungerThreshold) return false;

            // Primary driver: if there's no food where we're standing, migrate to find some.
            // Without this, hungry grazers random-walk and starve on depleted/barren tiles
            // even when grazing exists nearby. Eagerness scales with hunger.
            if (!HasFoodAt(pos.X, pos.Y, myDef))
            {
                float urgency = 1f - hungerRatio;            // 0.3 .. 1
                float roamChance = 0.03f + urgency * 0.12f;  // ~0.07 .. 0.15
                return _rng.NextDouble() < roamChance;
            }

            // Food is here, but the patch may be overcrowded — occasionally spread out to
            // less contested grazing so a herd doesn't strip a single spot bare.
            if (hungerRatio < 0.5f)
            {
                float checkRadius = 12f;
                _nearbyBuffer.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, checkRadius, _nearbyBuffer);

                // Count creatures competing for the same food source
                int competitorCount = 0;
                foreach (int other in _nearbyBuffer)
                {
                    if (other == entity || !em.IsAlive(other)) continue;
                    if (!em.HasComponents(other, ComponentFlags.Species)) continue;

                    ref var otherSpecies = ref em.Species[other];
                    var otherDef = SpeciesRegistry.GetById(otherSpecies.SpeciesId);

                    // Same diet = competing for same food
                    if (otherDef.Diet == myDef.Diet)
                        competitorCount++;
                }

                float competitionPressure = competitorCount / Math.Max(1f,
                    em.HasComponents(entity, ComponentFlags.Social)
                        ? em.Socials[entity].PreferredGroupSize * 2f
                        : 6f);

                if (competitionPressure > 1.0f)
                    return _rng.NextDouble() < 0.01f;
            }
        }

        return false;
    }

    /// <summary>
    /// True if the tile at the given world position currently offers food for this species:
    /// a grazeable tile with remaining nutrition, or one of the species' FeedTiles.
    /// </summary>
    private bool HasFoodAt(float x, float y, SpeciesDefinition def)
    {
        if (_worldManager == null) return true; // No world to evaluate — assume fed, don't roam
        var tile = _worldManager.GetTile(x, y);
        // Ground that would kill this species is never "food here", however rich it looks —
        // otherwise a fertility feeder sits on lethal substrate eating happily until it dies
        // (a well-fed creature never triggers the food-seeking roam that would carry it home).
        if (IsLethalSubstrate(def, tile)) return false;
        // Anything that draws fertility from the ground it stands on — grazers, Shroomers, and
        // water-feeding shoals — sees a stripped tile as no food at all, whatever its type.
        if (DrawsFertility(def, tile))
            return _worldManager.GetNutrition(x, y) > def.MinAcceptableNutrition;
        if (def.FeedTiles != null && def.FeedTiles.Contains(tile))
            return true;
        return false;
    }

    /// <summary>
    /// True when this species eats by stripping the tile's own fertility here — grazing pasture,
    /// a Shroomer drawing growth fuel, or a shoal feeding on the water column. Such a tile is only
    /// worth anything while it still holds nutrition, which is what turns local depletion into
    /// migration rather than starvation in place.
    /// </summary>
    private static bool DrawsFertility(SpeciesDefinition def, TileType tile)
        => ((def.CanGraze || def.FertilityConsumeRate > 0f) && tile.IsGrazeable())
           || (def.FeedConsumeRate > 0f && def.FeedTiles != null && def.FeedTiles.Contains(tile));

    /// <summary>
    /// True when standing on this tile does sustained damage to the species — currently the
    /// fungal drought rule (substrate below SporeMoistureThreshold). Foraging must treat such
    /// ground as worthless no matter how fertile it is.
    /// </summary>
    private static bool IsLethalSubstrate(SpeciesDefinition def, TileType tile)
        => def.DroughtDamage > 0f && tile.SubstrateMoisture() < def.SporeMoistureThreshold;

    /// <summary>
    /// Find the nearest direction back to survivable ground for a species being damaged by the
    /// substrate it is standing on. Samples the 8 compass directions and takes the one whose
    /// closest safe tile is nearest, so a stranded creature heads for the edge of the dry patch
    /// rather than picking a scenic route across it.
    /// </summary>
    private bool TryFindSafeSubstrateTarget(float x, float y, float roamDistance,
        SpeciesDefinition def, out float targetX, out float targetY)
    {
        targetX = x;
        targetY = y;
        if (_worldManager == null) return false;

        float bestDist = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            for (float t = 0.15f; t <= 1.0f; t += 0.15f)
            {
                float d = roamDistance * t;
                float sx = x + dx * d;
                float sy = y + dy * d;
                if (IsLethalSubstrate(def, _worldManager.GetTile(sx, sy))) continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    // Aim a little past the boundary so it settles inside the safe ground,
                    // not on the very edge where the next random step drifts back out.
                    targetX = x + dx * (d + 4f);
                    targetY = y + dy * (d + 4f);
                }
                break; // nearest safe sample along this ray is all that matters
            }
        }

        return bestDist < float.MaxValue;
    }

    /// <summary>
    /// True when this creature is mature, off cooldown and well enough fed/rested to reproduce —
    /// i.e. everything ReproductionSystem checks except where it is standing.
    /// </summary>
    private static bool IsBreedingReady(EntityManager em, int entity)
    {
        const ComponentFlags needed = ComponentFlags.Reproduction | ComponentFlags.Age
                                    | ComponentFlags.Hunger | ComponentFlags.Energy;
        if (!em.HasComponents(entity, needed)) return false;
        ref var repro = ref em.Reproductions[entity];
        if (repro.CurrentCooldown > 0) return false;
        if (!em.Ages[entity].IsMature) return false;
        return em.Hungers[entity].Current >= repro.HungerThreshold
            && em.Energies[entity].Current >= repro.EnergyThreshold;
    }

    /// <summary>
    /// Nearest point on one of the given tile types, searched along the 8 compass directions.
    /// Shares the shape of TryFindSafeSubstrateTarget: closest hit wins, and the target is placed
    /// a little past the boundary so the creature settles inside rather than on the rim.
    /// </summary>
    private bool TryFindTileTarget(float x, float y, float roamDistance, List<TileType> wanted,
        out float targetX, out float targetY)
    {
        targetX = x;
        targetY = y;
        if (_worldManager == null) return false;

        float bestDist = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            for (float t = 0.1f; t <= 1.0f; t += 0.1f)
            {
                float d = roamDistance * t;
                float sx = x + dx * d;
                float sy = y + dy * d;
                if (!_worldManager.IsInBounds(sx, sy)) break;
                if (!wanted.Contains(_worldManager.GetTile(sx, sy))) continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    targetX = x + dx * (d + 3f);
                    targetY = y + dy * (d + 3f);
                }
                break; // nearest hit along this ray is all that matters
            }
        }

        return bestDist < float.MaxValue;
    }

    /// <summary>
    /// Find a roam target toward the best nearby food for a grazing species.
    /// Samples 8 compass directions out to roamDistance, scoring each by food availability
    /// (tile nutrition for grazers, presence for FeedTile species), with a mild bias toward
    /// closer food. Mirrors TryFindDamagedTerrainTarget but for hunger-driven foraging.
    /// </summary>
    private bool TryFindFoodTarget(float x, float y, float roamDistance, SpeciesDefinition def,
        out float targetX, out float targetY)
    {
        targetX = x;
        targetY = y;
        float bestScore = 0f;

        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            // Sample points along this direction; nearer food is weighted slightly higher.
            float dirScore = 0f;
            for (float t = 0.2f; t <= 1.0f; t += 0.2f)
            {
                float sampleX = x + dx * roamDistance * t;
                float sampleY = y + dy * roamDistance * t;
                dirScore += GetFoodScore(sampleX, sampleY, def) * (1.2f - t * 0.4f);
            }

            if (dirScore > bestScore)
            {
                bestScore = dirScore;
                float dist = roamDistance * (0.5f + (float)_rng.NextDouble() * 0.5f);
                targetX = x + dx * dist;
                targetY = y + dy * dist;
            }
        }

        return bestScore > 0.2f; // Only commit if meaningful food was detected
    }

    /// <summary>
    /// Score a tile as a food source for the given species. Grazers score by remaining
    /// nutrition (0-1); FeedTile species score a flat amount. Hostile terrain scores 0 so
    /// foraging never steers a creature into water/lava/mountains.
    /// </summary>
    private float GetFoodScore(float x, float y, SpeciesDefinition def)
    {
        // Never forage off the edge of the map (out of bounds reads as DeepWater, which is food
        // to an aquatic species).
        if (!_worldManager!.IsInBounds(x, y)) return 0f;
        var tile = _worldManager.GetTile(x, y);
        // Species-aware: hostile terrain scores 0 so foraging never steers a creature off its
        // element — but for an aquatic this means LAND, not water (fish forage IN water).
        if (TerrainProfile.SteerAversion(def, tile) > 0.6f) return 0f;
        // Never forage toward ground that damages us (fungal drought), however fertile it is.
        if (IsLethalSubstrate(def, tile)) return 0f;
        if (DrawsFertility(def, tile))
        {
            // Ground already below what this species will settle for scores nothing, so a picky
            // browser routes past thin pasture a small generalist would happily stop on — and a
            // shoal steers toward richer water instead of milling over the patch it just stripped.
            float n = _worldManager.GetNutrition(x, y);
            return n > def.MinAcceptableNutrition ? n : 0f;
        }
        if (def.FeedTiles != null && def.FeedTiles.Contains(tile))
            return 1f;
        // Predator hunting grounds: a hungry predator with no prey in range scores its
        // HuntTerrain so it migrates toward where its prey lives (Penguin → water, Scorpion →
        // desert) instead of wandering blind off into hostile terrain.
        if (def.HuntTerrain != null && def.HuntTerrain.Contains(tile))
            return 1f;
        return 0f;
    }

    /// <summary>Distance a creature scans along each compass ray when looking for a way out.</summary>
    private const float EscapeScanDistance = 24f;

    /// <summary>
    /// Apply a terrain-avoidance nudge to a direction the creature has already committed to,
    /// without ever turning it back on itself.
    ///
    /// The plain blend (<c>(dir + avoidance * 2).Normalized()</c>) reverses whenever the ground
    /// ahead scores above 0.5, because the push-back term then outweighs the direction itself.
    /// In UNIFORM bad ground that is every tick and in every direction: a creature crossing a lake
    /// (shallow water aversion 0.75) had its heading flipped on the spot, tick after tick, so it
    /// oscillated in place a few tiles from where it started while its roam target sat on the
    /// shore it had correctly chosen. Avoidance is for routing around an obstacle; when there is
    /// no way around, the answer is to keep going, not to turn back.
    ///
    /// The forward component is preserved and only the sideways part of the nudge is applied.
    /// </summary>
    private static Vector2 SteerWithoutReversing(Vector2 desired, Vector2 avoidance)
    {
        var blended = desired + avoidance * 2f;
        if (blended.LengthSquared() < 0.0001f)
            return desired;

        var candidate = blended.Normalized();
        if (candidate.Dot(desired) > 0.1f)
            return candidate;

        // The nudge would reverse (or stall) us: strip its backward component and keep the
        // sideways steer, so we skirt the obstacle instead of bouncing off it.
        var sideways = avoidance - desired * avoidance.Dot(desired);
        if (sideways.LengthSquared() < 0.0001f)
            return desired;
        return (desired + sideways.Normalized() * 0.6f).Normalized();
    }

    /// <summary>
    /// True when standing on this tile costs the species nothing — the definition of "far enough
    /// out of the water", and the target an escaping creature is actually looking for.
    /// </summary>
    private static bool IsComfortableGround(SpeciesDefinition sp, TileType tile)
        => TerrainProfile.DiscomfortRate(sp, tile) <= 0f;

    /// <summary>
    /// Nearest genuinely comfortable ground, searched along the 8 compass rays out to roam range.
    /// Same shape as TryFindSafeSubstrateTarget: closest hit wins and the target sits a little
    /// past the boundary so the creature settles inside the safe ground rather than on its rim.
    /// </summary>
    private bool TryFindComfortableTarget(float x, float y, float roamDistance,
        SpeciesDefinition def, out float targetX, out float targetY)
    {
        targetX = x;
        targetY = y;
        if (_worldManager == null) return false;

        float range = MathF.Max(roamDistance, EscapeScanDistance);
        float step = MathF.Max(1f, range / 24f);
        float bestDist = float.MaxValue;

        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            for (float d = step; d <= range; d += step)
            {
                float sx = x + dx * d;
                float sy = y + dy * d;
                if (!_worldManager.IsInBounds(sx, sy)) break;
                if (!IsComfortableGround(def, _worldManager.GetTile(sx, sy))) continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    targetX = x + dx * (d + 3f);
                    targetY = y + dy * (d + 3f);
                }
                break; // nearest safe sample along this ray is all that matters
            }
        }

        return bestDist < float.MaxValue;
    }

    /// <summary>
    /// Fallback escape heading when nothing comfortable is in range: the ray whose terrain
    /// improves soonest, else the least-bad one overall.
    ///
    /// This used to sample a single point 1.5 tiles along each ray, which fails in exactly the
    /// case it exists for — a creature more than a step into open water sees nothing but water in
    /// all eight directions. Worse, the ties were broken by iteration order (strictly-less-than
    /// against a running best), so every animal in the middle of a lake picked due EAST. Now each
    /// ray is walked outward, and the scan starts at a random compass point so genuine ties
    /// scatter instead of pointing the whole map the same way.
    /// </summary>
    private Vector2 FindEscapeDirection(float x, float y, SpeciesDefinition? sp, float lookAhead)
    {
        if (_worldManager == null)
            return Vector2.Zero;

        float bestScore = float.MaxValue;
        Vector2 bestDir = Vector2.Zero;
        int startRay = _rng.Next(8);

        for (int k = 0; k < 8; k++)
        {
            int i = (k + startRay) % 8;
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            // Walk outward: score by how far we must travel before the ground improves, so the
            // shortest way out wins. Rays that never improve are ranked behind every ray that
            // does, by their average unpleasantness.
            float firstImprovement = float.MaxValue;
            float sum = 0f;
            int samples = 0;
            float here = AversionAt(sp, x, y);

            // Stepped at the BASE look-ahead, not the LOD-scaled one: this ray is measuring how
            // far the bad ground extends, so its resolution must stay fine even when a distant
            // creature is planning ten tiles out. Only the first sample is pushed outward, so a
            // fast creature doesn't rate a way out it will have overshot before it can take it.
            for (float d = MathF.Max(_lookAheadDistance, lookAhead * 0.5f);
                 d <= EscapeScanDistance; d += _lookAheadDistance)
            {
                float testX = x + dx * d;
                float testY = y + dy * d;
                float avoidance = AversionAt(sp, testX, testY);
                sum += avoidance;
                samples++;
                if (avoidance < here - 0.05f)
                {
                    firstImprovement = d;
                    break;
                }
            }

            float score = firstImprovement < float.MaxValue
                ? firstImprovement
                : EscapeScanDistance + (samples > 0 ? sum / samples : 1f) * EscapeScanDistance;

            if (score < bestScore)
            {
                bestScore = score;
                bestDir = new Vector2(dx, dy);
            }
        }

        return bestDir;
    }

    /// <summary>
    /// Calculate avoidance vector based on nearby terrain.
    /// </summary>
    private Vector2 GetTerrainAvoidance(float x, float y, Vector2 currentDir, SpeciesDefinition? sp,
                                        float lookAhead)
    {
        if (_worldManager == null)
            return Vector2.Zero;

        float avoidX = 0f;
        float avoidY = 0f;

        // Sample the whole path we're about to cover, not one point at a fixed distance: the
        // question is "does anything bad lie between here and my next decision", and a single
        // distant probe steps clean over a shoreline it should have refused.
        float aheadAvoidance = WorstAversionAlong(sp, x, y, currentDir.X, currentDir.Y, lookAhead);

        if (aheadAvoidance > 0.2f)
        {
            // Push back from bad terrain
            avoidX -= currentDir.X * aheadAvoidance;
            avoidY -= currentDir.Y * aheadAvoidance;
        }

        // Also check perpendicular directions for a better path
        float perpX = -currentDir.Y;
        float perpY = currentDir.X;

        float leftAvoidance = WorstAversionAlong(sp, x, y, perpX, perpY, lookAhead);
        float rightAvoidance = WorstAversionAlong(sp, x, y, -perpX, -perpY, lookAhead);

        // Steer toward better side
        if (leftAvoidance < rightAvoidance)
        {
            avoidX += perpX * (rightAvoidance - leftAvoidance);
            avoidY += perpY * (rightAvoidance - leftAvoidance);
        }
        else if (rightAvoidance < leftAvoidance)
        {
            avoidX -= perpX * (leftAvoidance - rightAvoidance);
            avoidY -= perpY * (leftAvoidance - rightAvoidance);
        }

        return new Vector2(avoidX, avoidY);
    }

    /// <summary>
    /// Species-aware steering aversion, via the shared TerrainProfile resolver (aquatic invert,
    /// semi-aquatic water-OK, insects water-barred, land animals standard). Null species (e.g.
    /// the player) falls back to the raw tile weight.
    /// </summary>
    private static float Aversion(SpeciesDefinition? sp, TileType tile)
        => sp != null ? TerrainProfile.SteerAversion(sp, tile) : tile.GetAvoidanceWeight();

    /// <summary>
    /// Steering aversion for a sampled POSITION: the tile's own aversion, or the world border's,
    /// whichever is stronger. Sampling by tile alone made the map edge invisible to steering —
    /// out there GetTile answers DeepWater, so land animals happened to avoid it while the aquatic
    /// species that live in deep water were actively drawn over the boundary and stuck there.
    /// </summary>
    private float AversionAt(SpeciesDefinition? sp, float x, float y)
    {
        if (_worldManager == null) return 0f;
        float edge = _worldManager.EdgeAversion(x, y);
        if (edge >= 1f) return 1f;
        return MathF.Max(edge, Aversion(sp, _worldManager.GetTile(x, y)));
    }

    /// <summary>
    /// Worst ground anywhere along the next `lookAhead` tiles of travel in a direction — a swept
    /// test rather than a point probe.
    ///
    /// The distinction only matters once a creature moves further between decisions than it looks
    /// ahead, which is exactly what LOD tiers create: a Minimal-tier shark covers ~5.7 tiles per
    /// decision, so probing a single point 5.7 tiles out reports whatever happens to be there and
    /// says nothing about the beach at 3. Stepping the ray at the base look-ahead keeps the
    /// resolution a nearby creature has, and the sample count scales with the interval — so the
    /// cost per unit of world time is unchanged, just spent in fewer, larger decisions.
    /// </summary>
    private float WorstAversionAlong(SpeciesDefinition? sp, float x, float y,
                                     float dirX, float dirY, float lookAhead)
    {
        float worst = 0f;
        for (float d = _lookAheadDistance; ; d += _lookAheadDistance)
        {
            if (d > lookAhead) d = lookAhead;
            float a = AversionAt(sp, x + dirX * d, y + dirY * d);
            if (a > worst) worst = a;
            if (worst >= 1f || d >= lookAhead) break;
        }
        return worst;
    }

    // Siege standoff in tiles: just outside a full-grown Shroomer's max AoE reach (14), so a
    // keeper besieging a bloom bombards/terraforms from the rim instead of dying inside it.
    private const float KeeperSiegeStandoff = 16f;

    /// <summary>
    /// Roam target for a keeper with an active dominance reading: a point on the standoff
    /// ring around the sensed hotspot, on the keeper's side. Works from both directions —
    /// approaching keepers stop at the ring, a keeper caught inside it retreats out to it.
    /// Returns false when already on station (fall through to local restoration patrol,
    /// which the damaged substrate around a bloom naturally attracts).
    /// </summary>
    private static bool TrySiegeTarget(ref FaelingPower power, float x, float y,
        out float targetX, out float targetY)
    {
        targetX = x;
        targetY = y;
        float dx = x - power.KeeperHotspotX;
        float dy = y - power.KeeperHotspotY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        // On station: within a band around the ring — hold and restore locally.
        if (dist > KeeperSiegeStandoff - 6f && dist < KeeperSiegeStandoff + 14f)
            return false;

        if (dist < 0.001f)
        {
            // Standing exactly on the hotspot: pick an arbitrary retreat direction.
            dx = 1f;
            dy = 0f;
            dist = 1f;
        }

        targetX = power.KeeperHotspotX + dx / dist * KeeperSiegeStandoff;
        targetY = power.KeeperHotspotY + dy / dist * KeeperSiegeStandoff;
        return true;
    }

    /// <summary>
    /// Find a roam target toward the most damaged (non-balanced) terrain.
    /// Samples 8 compass directions and picks the one with highest damage score.
    /// Used by Faelings to patrol toward areas needing restoration.
    /// </summary>
    private bool TryFindDamagedTerrainTarget(float x, float y, float roamDistance,
        out float targetX, out float targetY)
    {
        targetX = x;
        targetY = y;
        float best = 0f;

        // Aim at the most damaged POINT found, not at a random distance along the best direction.
        // Direction scoring plus a random travel distance used to overshoot: a keeper would
        // correctly identify the direction of a converted patch and then walk past it.
        //
        // The near field is what matters and is what the old sampling missed entirely. It sampled
        // t = 0.2..1.0 of a 60-tile roam distance, so the first probe was already 12 tiles out and
        // damage under the keeper's feet was invisible; a keeper five tiles from a fifteen-tile
        // patch got at most one sample inside it and never cleared the threshold. Long-range
        // redeployment is the crystal-travel mechanism's job (CrystalSystem), not this one's —
        // here a keeper is looking for work within walking distance.
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            for (float t = RestoreSeekNearFraction; t <= RestoreSeekFarFraction; t += RestoreSeekNearFraction)
            {
                float sampleX = x + dx * roamDistance * t;
                float sampleY = y + dy * roamDistance * t;
                float score = GetTerrainDamageScore(sampleX, sampleY);
                if (score > best)
                {
                    best = score;
                    targetX = sampleX;
                    targetY = sampleY;
                }
            }
        }

        return best >= RestoreSeekMinScore;
    }

    /// <summary>
    /// Score a tile by how far it has actually been MOVED from what worldgen made it. Higher =
    /// more damaged.
    ///
    /// This was a hardcoded table scoring Arid 4, Bog 4, Wetland 3 … Grass 0 — that is, it
    /// asserted that grass is the correct state of the world and sent keepers off to convert
    /// natural deserts and jungles into it. It was the search half of the same monoculture the
    /// `Balanced` terraform direction was the acting half of. Deviation from pristine has no such
    /// opinion: an untouched desert scores zero and a swamped grassland scores high, so a keeper
    /// walks toward damage rather than toward disagreement.
    ///
    /// Scaled to keep the caller's "significant damage" threshold meaningful: 0.25 of full-scale
    /// moisture displacement — roughly one biome band — reads as 1.0.
    /// </summary>
    private float GetTerrainDamageScore(float x, float y)
    {
        if (_worldManager == null) return 0f;
        var tile = _worldManager.GetTile(x, y);
        if (!tile.IsTerraformable()) return 0f;   // water, mountain: nothing to restore
        return _worldManager.DeviationAt(x, y) * 4f;
    }

    /// <summary>
    /// Smoothly rotate velocity toward a target direction using angular interpolation.
    /// Maintains constant speed while the direction converges exponentially.
    /// Avoids the instant-flip jitter of direct velocity assignment.
    /// </summary>
    private static void BlendVelocitySmooth(ref Velocity vel, float targetDirX, float targetDirY,
                                             float speed, float turnRate)
    {
        float targetAngle = MathF.Atan2(targetDirY, targetDirX);
        float speedSq = vel.Dx * vel.Dx + vel.Dy * vel.Dy;

        float newAngle;
        if (speedSq > 0.0001f)
        {
            float curAngle = MathF.Atan2(vel.Dy, vel.Dx);
            float angleDiff = targetAngle - curAngle;

            // Normalize to [-PI, PI] so we always take the shortest arc
            if (angleDiff > MathF.PI) angleDiff -= 2f * MathF.PI;
            else if (angleDiff < -MathF.PI) angleDiff += 2f * MathF.PI;

            newAngle = curAngle + angleDiff * turnRate;
        }
        else
        {
            // No current velocity — snap to target direction (first tick after spawn)
            newAngle = targetAngle;
        }

        vel.Dx = MathF.Cos(newAngle) * speed;
        vel.Dy = MathF.Sin(newAngle) * speed;
    }
}
