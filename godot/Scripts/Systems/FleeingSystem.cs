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
    private readonly Random _rng = new();

    // Pre-allocated arrays for predator positions and species
    private float[] _predatorXs = new float[128];
    private float[] _predatorYs = new float[128];
    private float[] _predatorDistSq = new float[128];  // Store squared distances
    private int[] _predatorSpeciesIds = new int[128];   // Species ID per predator (for same-species filtering)
    private float[] _predatorStealth = new float[128];  // Stealth level per predator (ambush detection reduction)
    private int _predatorCount;

    public FleeingSystem(SpatialHash spatialHash, WorldManager? worldManager = null)
    {
        _spatialHash = spatialHash;
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        // Collect predator positions with species info
        const ComponentFlags predatorRequired = ComponentFlags.Position | ComponentFlags.Predator;
        _predatorCount = 0;

        foreach (int entity in em.Query(predatorRequired))
        {
            // Dormant Sectids aren't a threat — exclude them so prey neither flee nor fear them
            if (em.HasComponents(entity, ComponentFlags.FoodCarrier) && em.FoodCarriers[entity].IsHibernating)
                continue;

            // Resize arrays if needed
            if (_predatorCount >= _predatorXs.Length)
            {
                int newSize = _predatorXs.Length * 2;
                Array.Resize(ref _predatorXs, newSize);
                Array.Resize(ref _predatorYs, newSize);
                Array.Resize(ref _predatorDistSq, newSize);
                Array.Resize(ref _predatorSpeciesIds, newSize);
                Array.Resize(ref _predatorStealth, newSize);
            }

            ref var pos = ref em.Positions[entity];
            _predatorXs[_predatorCount] = pos.X;
            _predatorYs[_predatorCount] = pos.Y;
            _predatorSpeciesIds[_predatorCount] = em.HasComponents(entity, ComponentFlags.Species)
                ? em.Species[entity].SpeciesId : 0;
            // Track predator stealth for ambush detection reduction
            ref var pred = ref em.Predators[entity];
            _predatorStealth[_predatorCount] = pred.Stealth;
            _predatorCount++;
        }

        // Process prey
        const ComponentFlags preyRequired = ComponentFlags.Position | ComponentFlags.Prey | ComponentFlags.Velocity | ComponentFlags.Wander;

        var predXSpan = _predatorXs.AsSpan(0, _predatorCount);
        var predYSpan = _predatorYs.AsSpan(0, _predatorCount);
        var predSpeciesSpan = _predatorSpeciesIds.AsSpan(0, _predatorCount);
        var predStealthSpan = _predatorStealth.AsSpan(0, _predatorCount);

        foreach (int entity in em.Query(preyRequired))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var pos = ref em.Positions[entity];
            ref var prey = ref em.Preys[entity];
            ref var vel = ref em.Velocities[entity];
            ref var wander = ref em.Wanders[entity];

            float fleeRangeSq = prey.FleeRange * prey.FleeRange;

            // Get this prey's species ID to filter out same-species "threats"
            int mySpeciesId = em.HasComponents(entity, ComponentFlags.Species)
                ? em.Species[entity].SpeciesId : 0;

            // Calculate flee direction and find closest threat distance
            // Stealth-aware: stealthed predators reduce effective detection range
            var (fleeDir, hasThreat, closestDistSq) = CalculateFleeVectorWithDistance(
                pos.X, pos.Y,
                predXSpan, predYSpan, predSpeciesSpan, predStealthSpan,
                fleeRangeSq, mySpeciesId);

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

            // Determine behavior based on fear level and response type
            if (hasThreat || (hasFear && fearRatio > 0.5f))
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
                        prey.IsFleeing = false;
                        continue;  // Let wander system handle escape
                    }
                }

                prey.IsFleeing = true;

                // Mass-based agility: smaller creatures change direction faster,
                // allowing rabbits to juke while deer commit to a direction.
                float bodyMass = 1f;
                if (em.HasComponents(entity, ComponentFlags.Species))
                {
                    var speciesDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                    if (speciesDef != null)
                        bodyMass = speciesDef.BodyMass;
                }
                float agility = Math.Clamp(1.5f / bodyMass, 0.25f, 1f);

                // Apply fear response behavior
                switch (fearResponse)
                {
                    case FearResponse.Freeze:
                        ApplyFreezeResponse(ref vel, fearRatio);
                        break;

                    case FearResponse.Panic:
                        ApplyPanicResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio, agility);
                        break;

                    case FearResponse.Defensive:
                        ApplyDefensiveResponse(entity, ref vel, ref prey, agility, em);
                        break;

                    case FearResponse.Flee:
                    default:
                        ApplyFleeResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio, agility);
                        break;
                }
            }
            else
            {
                prey.IsFleeing = false;
            }
        }
    }

    /// <summary>
    /// Calculate flee vector and return closest threat distance squared.
    /// Filters out predators of the same species (e.g., Sectids don't flee from Sectids).
    /// Stealth-aware: stealthed predators have reduced effective detection range.
    /// </summary>
    private (Vector2 dir, bool hasThreat, float closestDistSq) CalculateFleeVectorWithDistance(
        float x, float y,
        ReadOnlySpan<float> predX, ReadOnlySpan<float> predY, ReadOnlySpan<int> predSpecies,
        ReadOnlySpan<float> predStealth,
        float maxDistSq, int mySpeciesId)
    {
        float fleeX = 0, fleeY = 0;
        float closestDistSq = float.MaxValue;
        bool hasThreat = false;

        for (int i = 0; i < predX.Length; i++)
        {
            // Skip same-species predators (swarm mates are not threats)
            if (mySpeciesId != 0 && predSpecies[i] == mySpeciesId)
                continue;

            float dx = x - predX[i];
            float dy = y - predY[i];
            float distSq = dx * dx + dy * dy;

            // Stealth reduces effective detection range:
            // At stealth 0 → full range, at stealth 1 → 10% range (nearly invisible)
            // Pouncing predators (stealth reset to 0) are fully visible again
            float stealth = predStealth[i];
            float effectiveMaxDistSq = maxDistSq;
            if (stealth > 0f)
            {
                float detectionMult = 1f - stealth * 0.9f; // 0.1 at full stealth
                effectiveMaxDistSq = maxDistSq * detectionMult;
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
                                     float agility)
    {
        float panicSpeed = wander.Speed * prey.FleeSpeedMultiplier * (1f + fearRatio * 0.5f);

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
    /// Normal flee response - run away, considering terrain.
    /// </summary>
    private void ApplyFleeResponse(ref Position pos, ref Velocity vel, ref Wander wander,
                                    ref Prey prey, Vector2 fleeDir, float fearRatio, float discomfortRatio,
                                    float agility)
    {
        float fleeSpeed = wander.Speed * prey.FleeSpeedMultiplier;

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
            var aheadTile = _worldManager.GetTile(fleeAheadX, fleeAheadY);

            // If fleeing leads to worse terrain, try to find a compromise direction
            if (aheadTile.GetAvoidanceWeight() > 0.5f)
            {
                // Try perpendicular directions to see if either is better
                float perpX = -fleeDir.Y;
                float perpY = fleeDir.X;

                float leftAvoid = _worldManager.GetTile(pos.X + perpX * 2f, pos.Y + perpY * 2f).GetAvoidanceWeight();
                float rightAvoid = _worldManager.GetTile(pos.X - perpX * 2f, pos.Y - perpY * 2f).GetAvoidanceWeight();

                // Blend flee direction with side-step based on discomfort (less adjustment when afraid)
                float blendFactor = discomfortRatio * 0.5f * (1f - fearRatio);
                if (leftAvoid < rightAvoid && leftAvoid < aheadTile.GetAvoidanceWeight())
                {
                    fleeDir = new Vector2(
                        fleeDir.X * (1 - blendFactor) + perpX * blendFactor,
                        fleeDir.Y * (1 - blendFactor) + perpY * blendFactor
                    ).Normalized();
                }
                else if (rightAvoid < aheadTile.GetAvoidanceWeight())
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
