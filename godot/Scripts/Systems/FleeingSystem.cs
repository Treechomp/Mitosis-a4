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
/// Processes fleeing behavior for prey.
/// Uses accumulated fear to determine response type and intensity.
/// Balances fear against terrain discomfort.
/// </summary>
public sealed class FleeingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly WorldManager? _worldManager;
    private readonly Random _rng = SimRandom.Create();

    // Reused buffer for the per-prey nearby-predator spatial query.
    private readonly List<int> _nearbyPredators = new(64);

    // Predator-only index when a world supplies one (WorldManager.PredatorHash); falls back to
    // the full index so the system still works standalone.
    private readonly SpatialHash _threatHash;

    /// <summary>
    /// How much of its flee radius a fully desperate (starving) animal gives up. At 0.65 a prey
    /// animal on the edge of starvation reacts only inside ~35% of its usual reaction distance —
    /// enough to keep feeding in contested ground, not enough to walk into a predator's jaws.
    /// </summary>
    private const float DesperationFleeRangeCut = 0.65f;

    /// <summary>How far a fleeing semi-aquatic animal will look for water to escape into.</summary>
    private const float RefugeSearchRadius = 10f;

    /// <summary>How strongly the refuge direction pulls on the flee vector (0 = ignored, 1 = only refuge).</summary>
    private const float RefugePull = 0.55f;

    public FleeingSystem(SpatialHash spatialHash, WorldManager? worldManager = null)
    {
        _spatialHash = spatialHash;
        _worldManager = worldManager;
        _threatHash = worldManager?.PredatorHash ?? spatialHash;
    }

    public void Process(EntityManager em)
    {
        // Each prey scans only its own neighbourhood for predators via the spatial hash, instead
        // of every prey checking every predator. Was O(prey × all-predators); now O(prey ×
        // predators within FleeRange) — a large win in big, sparse worlds where most prey have no
        // predator anywhere near them.
        const ComponentFlags preyRequired = ComponentFlags.Position | ComponentFlags.Prey | ComponentFlags.Velocity | ComponentFlags.Wander;

        foreach (int entity in em.Query(preyRequired))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var pos = ref em.Positions[entity];
            ref var prey = ref em.Preys[entity];
            ref var vel = ref em.Velocities[entity];
            ref var wander = ref em.Wanders[entity];

            var speciesDef = em.HasComponents(entity, ComponentFlags.Species)
                ? SpeciesRegistry.GetById(em.Species[entity].SpeciesId) : null;

            // Desperation: a starving animal cannot afford to be careful. Its effective flee
            // radius shrinks, so it tolerates a predator it can see and only bolts when one is
            // genuinely on top of it. Without this, prey holds a safe position until it dies of
            // hunger — a penguin refused to enter the water for as long as a shark was anywhere
            // in it, and starved on the ice while its food swam past. Risk beats certainty.
            float desperation = 0f;
            if (speciesDef != null && speciesDef.DesperationHunger > 0f
                && em.HasComponents(entity, ComponentFlags.Hunger))
            {
                float hungerRatio = em.Hungers[entity].Percent;
                if (hungerRatio < speciesDef.DesperationHunger)
                    desperation = 1f - hungerRatio / speciesDef.DesperationHunger;
            }

            float effectiveFleeRange = prey.FleeRange * (1f - desperation * DesperationFleeRangeCut);
            float fleeRangeSq = effectiveFleeRange * effectiveFleeRange;
            // For decision logging: detect flee start/stop transitions this tick.
            bool wasFleeing = prey.IsFleeing;

            // Get this prey's species ID to filter out same-species "threats"
            int mySpeciesId = em.HasComponents(entity, ComponentFlags.Species)
                ? em.Species[entity].SpeciesId : 0;

            // Calculate flee direction and find closest threat distance.
            // Stealth-aware: stealthed predators reduce effective detection range. Only predators
            // within FleeRange matter, so query just that neighbourhood from the spatial hash.
            _nearbyPredators.Clear();
            _threatHash.QueryRadius(pos.X, pos.Y, effectiveFleeRange, _nearbyPredators);
            var (fleeDir, hasThreat, closestDistSq) = CalculateFleeVector(
                em, pos.X, pos.Y, _nearbyPredators, fleeRangeSq, mySpeciesId);

            // Always update fear state — even while hunting
            bool hasFear = em.HasComponents(entity, ComponentFlags.Fear);
            FearResponse fearResponse = FearResponse.Flee;
            float fearRatio = 0f;

            if (hasFear)
            {
                ref var fear = ref em.Fears[entity];
                fearResponse = fear.Response;

                if (hasThreat)
                {
                    float proximityFactor = 1f - (closestDistSq / fleeRangeSq);
                    float fearIncrease = fear.AccumulationRate * proximityFactor;
                    fear.Current = MathF.Min(fear.Max, fear.Current + fearIncrease);
                    fear.VigilanceTicks = 100;
                }
                else
                {
                    float decayRate = fear.IsVigilant ? fear.VigilanceDecay : fear.DecayRate;
                    fear.Current = MathF.Max(0, fear.Current - decayRate);
                    if (fear.VigilanceTicks > 0)
                        fear.VigilanceTicks--;
                }

                fearRatio = fear.Ratio;
            }

            // Hunting entities: skip flee action unless fear is extreme
            if (em.HasComponents(entity, ComponentFlags.Predator))
            {
                ref var predator = ref em.Predators[entity];
                if (predator.HasTarget)
                {
                    // A predator rallying to mob its attacker stays committed: fear must NOT pull it
                    // off, or it gets stuck oscillating between approaching (Hunting) and fleeing
                    // (here) and never closes — a swarm hovering uselessly until it starves. If the
                    // mob is actually losing, HuntingSystem's self-damage/no-progress bail clears the
                    // target, after which fear can take over and it flees normally.
                    bool committedToMob = predator.LastAttackedTicks > 0
                                          && predator.TargetEntity == predator.LastAttacker;

                    // Extreme fear overrides hunting — abandon hunt to flee (unless mobbing)
                    if (hasFear && fearRatio > 0.8f && !committedToMob)
                    {
                        if (EcosystemLogger.DecisionLoggingFor(mySpeciesId))
                            EcosystemLogger.Instance!.LogDecision(mySpeciesId, entity, pos.X, pos.Y,
                                "fleeing", "abandon_hunt_fear",
                                FormattableString.Invariant($"fear={fearRatio:F2};target={predator.TargetEntity}"));
                        predator.TargetEntity = -1;
                        predator.Phase = PackPhase.Idle;
                        predator.Role = PackRole.None;
                        // Fall through to flee response below
                    }
                    else
                    {
                        prey.IsFleeing = false;
                        continue; // Committed/hunting — don't flee
                    }
                }
            }

            // Flee stamina: drains while fleeing, recovers at rest (LOD-scaled).
            int tickMult = em.HasComponents(entity, ComponentFlags.SimulationLOD)
                ? em.SimulationLODs[entity].EffectiveInterval : 1;
            float staminaDrain = speciesDef?.FleeStaminaDrain ?? 0.005f;
            float staminaRecover = speciesDef?.FleeStaminaRecovery ?? 0.0025f;
            float tiredFloor = speciesDef?.FleeTiredSpeedFloor ?? 0.5f;

            // Determine behavior based on fear level and response type. The fear-ratio trigger is
            // per-species (skittish prey bolt early, bold ones hold ground). Was a hardcoded 0.5.
            if (hasThreat || (hasFear && fearRatio > (speciesDef?.FleeFearThreshold ?? 0.5f)))
            {
                // Check discomfort level - extreme discomfort may override flee behavior
                float discomfortRatio = 0f;
                if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
                {
                    ref var discomfort = ref em.TerrainDiscomforts[entity];
                    discomfortRatio = discomfort.Ratio;

                    // Only override flee if discomfort is extreme AND we're not panicking
                    if (discomfort.ExceedsThreshold && fearRatio < 0.9f)
                    {
                        if (wasFleeing && EcosystemLogger.DecisionLoggingFor(mySpeciesId))
                            EcosystemLogger.Instance!.LogDecision(mySpeciesId, entity, pos.X, pos.Y,
                                "fleeing", "flee_end",
                                FormattableString.Invariant($"reason=discomfort_override;discomfort={discomfortRatio:F2};fear={fearRatio:F2}"));
                        prey.IsFleeing = false;
                        prey.Stamina = MathF.Min(1f, prey.Stamina + staminaRecover * tickMult);
                        continue;  // Let wander system handle escape
                    }
                }

                prey.IsFleeing = true;

                // Sustained fleeing tires the prey: drain stamina and fade the flee burst toward
                // the tired floor, so prey can't outrun an endless relay of predators.
                prey.Stamina = MathF.Max(0f, prey.Stamina - staminaDrain * tickMult);
                float staminaFactor = tiredFloor + (1f - tiredFloor) * prey.Stamina;

                // Mass-based agility: smaller creatures change direction faster,
                // allowing rabbits to juke while deer commit to a direction.
                float bodyMass = speciesDef?.BodyMass ?? 1f;
                float agility = Math.Clamp(1.5f / bodyMass, 0.25f, 1f);

                // Apply fear response behavior
                switch (fearResponse)
                {
                    case FearResponse.Freeze:
                        ApplyFreezeResponse(ref vel, fearRatio);
                        break;

                    case FearResponse.Panic:
                        ApplyPanicResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio, agility, staminaFactor);
                        break;

                    case FearResponse.Defensive:
                        ApplyDefensiveResponse(entity, ref vel, ref prey, agility, em);
                        break;

                    case FearResponse.Flee:
                    default:
                        ApplyFleeResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio, agility, staminaFactor, speciesDef);
                        break;
                }

                // Log AFTER the response is applied: Defensive resets IsFleeing to false every
                // tick (stand ground), so logging on the pre-switch assignment would emit a
                // "start" line per tick for defensive species instead of one per episode.
                if (prey.IsFleeing && !wasFleeing && EcosystemLogger.DecisionLoggingFor(mySpeciesId))
                    EcosystemLogger.Instance!.LogDecision(mySpeciesId, entity, pos.X, pos.Y,
                        "fleeing", "flee_start", FormattableString.Invariant(
                            $"response={fearResponse};fear={fearRatio:F2};threat_dist={(hasThreat ? MathF.Sqrt(closestDistSq) : -1f):F1};stamina={prey.Stamina:F2}"));
            }
            else
            {
                if (wasFleeing && EcosystemLogger.DecisionLoggingFor(mySpeciesId))
                    EcosystemLogger.Instance!.LogDecision(mySpeciesId, entity, pos.X, pos.Y,
                        "fleeing", "flee_end", FormattableString.Invariant(
                            $"reason=safe;fear={fearRatio:F2};stamina={prey.Stamina:F2}"));
                prey.IsFleeing = false;
                prey.Stamina = MathF.Min(1f, prey.Stamina + staminaRecover * tickMult);
            }
        }
    }

    /// <summary>
    /// Calculate flee vector and return closest threat distance squared.
    /// Filters out predators of the same species (e.g., Sectids don't flee from Sectids).
    /// Stealth-aware: stealthed predators have reduced effective detection range.
    /// </summary>
    private (Vector2 dir, bool hasThreat, float closestDistSq) CalculateFleeVector(
        EntityManager em, float x, float y, List<int> nearby, float maxDistSq, int mySpeciesId)
    {
        float fleeX = 0, fleeY = 0;
        float closestDistSq = float.MaxValue;
        bool hasThreat = false;

        foreach (int p in nearby)
        {
            // Only predators are threats…
            if (!em.HasComponents(p, ComponentFlags.Predator))
                continue;
            // …dormant Sectids aren't (prey neither flee nor fear a sleeping swarm)…
            if (em.HasComponents(p, ComponentFlags.FoodCarrier) && em.FoodCarriers[p].IsHibernating)
                continue;
            // …and same-species predators (swarm mates) aren't.
            int pSpecies = em.HasComponents(p, ComponentFlags.Species) ? em.Species[p].SpeciesId : 0;
            if (mySpeciesId != 0 && pSpecies == mySpeciesId)
                continue;

            ref var ppos = ref em.Positions[p];
            float dx = x - ppos.X;
            float dy = y - ppos.Y;
            float distSq = dx * dx + dy * dy;

            // Stealth reduces effective detection range:
            // At stealth 0 → full range, at stealth 1 → 10% range (nearly invisible).
            // Pouncing predators (stealth reset to 0) are fully visible again.
            float stealth = em.Predators[p].Stealth;
            float effectiveMaxDistSq = maxDistSq;
            if (stealth > 0f)
                effectiveMaxDistSq = maxDistSq * (1f - stealth * 0.9f); // 0.1 at full stealth

            // Terrain concealment of the predator also shrinks how far away prey notice it,
            // letting camouflaged ambushers close the gap (Arctic Fox in snow, Scorpion in
            // desert). Stacks with active stealth.
            if (_worldManager != null && pSpecies != 0)
            {
                float conceal = TerrainProfile.Concealment(
                    SpeciesRegistry.GetById(pSpecies), _worldManager.GetTile(ppos.X, ppos.Y));
                if (conceal > 0f)
                    effectiveMaxDistSq *= MathF.Max(0.1f, 1f - conceal);
            }

            if (distSq < effectiveMaxDistSq && distSq > 0.001f)
            {
                hasThreat = true;
                float weight = 1f / distSq;  // Closer predators have more influence
                fleeX += dx * weight;
                fleeY += dy * weight;

                if (distSq < closestDistSq)
                    closestDistSq = distSq;
            }
        }

        if (hasThreat)
        {
            float len = MathF.Sqrt(fleeX * fleeX + fleeY * fleeY);
            if (len > 0.001f)
                return (new Vector2(fleeX / len, fleeY / len), true, closestDistSq);
        }

        return (Vector2.Zero, false, closestDistSq);
    }

    /// <summary>
    /// Defensive response: hold ground rather than flee.
    /// Predator entities (Boar) dampen velocity and let HuntingSystem handle the counter-attack
    /// via LastAttacker/LastAttackedTicks (set when the attacker struck them). Herbivore entities
    /// (Musk Ox) slow to a near-stop and face the threat — the herd's mass is their defense.
    /// </summary>
    private static void ApplyDefensiveResponse(int entity, ref Velocity vel, ref Prey prey,
        float agility, EntityManager em)
    {
        prey.IsFleeing = false;
        if (em.HasComponents(entity, ComponentFlags.Predator))
        {
            // Pack predator (Boar): hold ground, suppress flee. HuntingSystem counter-attacks
            // via LastAttacker when the pack has enough allies to mob the threat.
            vel.Dx *= 0.75f;
            vel.Dy *= 0.75f;
        }
        else
        {
            // Herbivore (Musk Ox): plant hooves and stand firm. Heavy creatures change
            // direction slowly, so agility is very low; dampen proportionally.
            float brake = 1f - agility * 0.4f;
            vel.Dx *= brake;
            vel.Dy *= brake;
        }
    }

    /// <summary>
    /// Freeze response - stop moving, hoping predator doesn't notice.
    /// Movement decreases as fear increases.
    /// </summary>
    private void ApplyFreezeResponse(ref Velocity vel, float fearRatio)
    {
        // More afraid = more frozen
        float freezeFactor = MathF.Min(1f, fearRatio);
        vel.Dx *= (1f - freezeFactor * 0.9f);  // Reduce to 10% at max fear
        vel.Dy *= (1f - freezeFactor * 0.9f);
    }

    /// <summary>
    /// Panic response - erratic movement, ignores terrain danger.
    /// Speed increases with fear, direction becomes random.
    /// </summary>
    private void ApplyPanicResponse(ref Position pos, ref Velocity vel, ref Wander wander,
                                     ref Prey prey, Vector2 fleeDir, float fearRatio, float discomfortRatio,
                                     float agility, float staminaFactor)
    {
        float panicSpeed = wander.Speed * prey.FleeSpeedMultiplier * staminaFactor * (1f + fearRatio * 0.5f);

        // Add randomness to flee direction based on fear level
        float randomAngle = (float)((_rng.NextDouble() - 0.5) * Math.PI * fearRatio);
        float cos = MathF.Cos(randomAngle);
        float sin = MathF.Sin(randomAngle);

        Vector2 panicDir = new(
            fleeDir.X * cos - fleeDir.Y * sin,
            fleeDir.X * sin + fleeDir.Y * cos
        );

        // Blend toward panic direction — heavier creatures turn slower even in panic
        float targetDx = panicDir.X * panicSpeed;
        float targetDy = panicDir.Y * panicSpeed;
        vel.Dx += (targetDx - vel.Dx) * agility;
        vel.Dy += (targetDy - vel.Dy) * agility;
    }

    /// <summary>
    /// Direction to the nearest water within <see cref="RefugeSearchRadius"/>, or zero if there is
    /// none in reach. Sampled along the 8 compass rays, nearest hit wins — the same cheap shape
    /// the wander system uses to find safe substrate.
    /// </summary>
    private Godot.Vector2 FindRefugeDirection(float x, float y)
    {
        if (_worldManager == null) return Godot.Vector2.Zero;

        float bestDist = float.MaxValue;
        var best = Godot.Vector2.Zero;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);
            for (float d = 1.5f; d <= RefugeSearchRadius; d += 1.5f)
            {
                float sx = x + dx * d;
                float sy = y + dy * d;
                if (!_worldManager.IsInBounds(sx, sy)) break;
                if (!_worldManager.GetTile(sx, sy).IsSubmerged()) continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = new Godot.Vector2(dx, dy);
                }
                break;
            }
        }
        return best;
    }

    /// <summary>
    /// Normal flee response - run away, considering terrain.
    /// </summary>
    private void ApplyFleeResponse(ref Position pos, ref Velocity vel, ref Wander wander,
                                    ref Prey prey, Vector2 fleeDir, float fearRatio, float discomfortRatio,
                                    float agility, float staminaFactor, SpeciesDefinition? speciesDef)
    {
        // Species-aware terrain aversion: an aquatic fish must treat LAND as the thing to avoid
        // (not water), so it won't flee ashore and strand. Falls back to raw tile weight if the
        // species is unknown.
        // Sampled by POSITION, so the world border counts as terrain to avoid: cornered prey now
        // slides along the edge instead of pressing into it until the predator arrives.
        float Aversion(float sx, float sy)
        {
            float edge = _worldManager!.EdgeAversion(sx, sy);
            if (edge >= 1f) return 1f;
            var t = _worldManager.GetTile(sx, sy);
            float terrain = speciesDef != null
                ? TerrainProfile.SteerAversion(speciesDef, t) : t.GetAvoidanceWeight();
            return MathF.Max(edge, terrain);
        }

        // Refuge: a semi-aquatic animal caught ashore runs for the water, which its land-bound
        // pursuers won't follow it into. Without this an otter flees in a straight line across
        // open ground from a faster wolf, which is a losing race every time — the reason otters
        // vanished from ordinary worlds soon after being introduced. The bias is blended with the
        // away-from-predator direction rather than replacing it, so it never runs INTO the threat.
        if (_worldManager != null && speciesDef != null && speciesDef.SemiAquatic
            && !_worldManager.GetTile(pos.X, pos.Y).IsSubmerged())
        {
            var refuge = FindRefugeDirection(pos.X, pos.Y);
            if (refuge.LengthSquared() > 0.01f)
            {
                // Weighted toward the refuge but never against the escape: if the water lies
                // behind the predator the flee vector still dominates.
                var blended = fleeDir * (1f - RefugePull) + refuge * RefugePull;
                if (blended.Dot(fleeDir) > 0f && blended.LengthSquared() > 0.01f)
                    fleeDir = blended.Normalized();
            }
        }

        float fleeSpeed = wander.Speed * prey.FleeSpeedMultiplier * staminaFactor;

        // Speed boost when very afraid
        if (fearRatio > 0.7f)
            fleeSpeed *= 1f + (fearRatio - 0.7f) * 0.5f;

        // If on uncomfortable terrain and we have world info, try to modify flee direction
        // to also escape toward better terrain (but still away from predator)
        if (_worldManager != null && discomfortRatio > 0.3f && fearRatio < 0.8f)
        {
            // Check if fleeing would take us to worse terrain
            float fleeAheadX = pos.X + fleeDir.X * 2f;
            float fleeAheadY = pos.Y + fleeDir.Y * 2f;
            float aheadAvoid = Aversion(fleeAheadX, fleeAheadY);

            // If fleeing leads to worse terrain, try to find a compromise direction
            if (aheadAvoid > 0.5f)
            {
                // Try perpendicular directions to see if either is better
                float perpX = -fleeDir.Y;
                float perpY = fleeDir.X;

                float leftAvoid = Aversion(pos.X + perpX * 2f, pos.Y + perpY * 2f);
                float rightAvoid = Aversion(pos.X - perpX * 2f, pos.Y - perpY * 2f);

                // Blend flee direction with side-step based on discomfort (less adjustment when afraid)
                float blendFactor = discomfortRatio * 0.5f * (1f - fearRatio);
                if (leftAvoid < rightAvoid && leftAvoid < aheadAvoid)
                {
                    fleeDir = new Vector2(
                        fleeDir.X * (1 - blendFactor) + perpX * blendFactor,
                        fleeDir.Y * (1 - blendFactor) + perpY * blendFactor
                    ).Normalized();
                }
                else if (rightAvoid < aheadAvoid)
                {
                    fleeDir = new Vector2(
                        fleeDir.X * (1 - blendFactor) - perpX * blendFactor,
                        fleeDir.Y * (1 - blendFactor) - perpY * blendFactor
                    ).Normalized();
                }
            }
        }

        // Blend toward flee velocity — smaller creatures change direction faster
        float targetDx = fleeDir.X * fleeSpeed;
        float targetDy = fleeDir.Y * fleeSpeed;
        vel.Dx += (targetDx - vel.Dx) * agility;
        vel.Dy += (targetDy - vel.Dy) * agility;
    }
}
