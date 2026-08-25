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
/// Applies herding/pack cohesion and alignment behaviors to social creatures.
/// Uses spatial hash for efficient neighbor queries.
/// Features:
/// - Follow-the-leader behavior (not just converge on center)
/// - Leader influence radius (only follow nearby recognized leader)
/// - Dynamic leadership (new leader emerges when old one lost/dies)
/// - Group size limits (based on PreferredGroupSize)
/// - Priority system (survival needs override social behavior)
/// </summary>
public sealed class HerdingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly Dictionary<int, int> _groupSizes = new(32);  // groupId -> member count
    private readonly Dictionary<int, (int entity, float score)> _groupLeaders = new(32); // groupId -> (leader entity, score)
    private int _nextGroupId = 1;

    public HerdingSystem(SpatialHash spatialHash)
    {
        _spatialHash = spatialHash;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity |
                                        ComponentFlags.Species | ComponentFlags.Social;

        // Pre-pass: Count group sizes, find leaders (spatial hash already updated)
        _groupSizes.Clear();
        _groupLeaders.Clear();

        foreach (int entity in em.Query(required))
        {
            ref var social = ref em.Socials[entity];

            if (social.GroupId >= 0)
            {
                _groupSizes.TryGetValue(social.GroupId, out int count);
                _groupSizes[social.GroupId] = count + 1;

                // Track highest leadership score per group
                if (!_groupLeaders.TryGetValue(social.GroupId, out var current) ||
                    social.LeadershipScore > current.score)
                {
                    _groupLeaders[social.GroupId] = (entity, social.LeadershipScore);
                }
            }
        }

        // Main pass: Apply social behaviors
        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];
            ref var species = ref em.Species[entity];
            ref var social = ref em.Socials[entity];

            // Skip non-social types
            if (!social.IsSocial)
                continue;

            // Get species definition for social parameters
            var herdSpeciesDef = SpeciesRegistry.GetById(species.SpeciesId);
            float socialRadius = herdSpeciesDef.SocialRadius;
            float leaderInfluenceRadius = herdSpeciesDef.LeaderInfluenceRadius;
            float maxJoinDistance = herdSpeciesDef.MaxJoinDistance;
            float groupSizeTolerance = herdSpeciesDef.GroupSizeTolerance;
            int leaderLostThreshold = herdSpeciesDef.LeaderLostThreshold;

            // Update leadership score based on age (do this first so it's current)
            if (em.HasComponents(entity, ComponentFlags.Age))
            {
                ref var age = ref em.Ages[entity];
                social.LeadershipScore = (float)age.Current / age.MaxLifespan;
            }

            // === PRIORITY CHECK: Survival needs override social behavior ===

            // Priority 1: Terrain escape — herding toward a leader near water
            // would fight the escape direction and cause jitter
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];
                if (discomfort.IsEscaping)
                    continue;
            }

            // Priority 2: Fleeing from predator
            if (em.HasComponents(entity, ComponentFlags.Prey))
            {
                ref var prey = ref em.Preys[entity];
                if (prey.IsFleeing)
                    continue;
            }

            // Priority 3: Actively hunting — herds skip, packs still get cohesion
            bool isPackHunting = false;
            if (em.HasComponents(entity, ComponentFlags.Predator | ComponentFlags.Hunger))
            {
                ref var predator = ref em.Predators[entity];
                ref var hunger = ref em.Hungers[entity];
                float hungerRatio = hunger.Current / hunger.Max;

                if (predator.HasTarget && hungerRatio < herdSpeciesDef.ForageHungerThreshold)
                {
                    // Pack members keep cohesion while hunting — they need to stick together
                    if (social.Type == SocialType.Pack)
                        isPackHunting = true;
                    else
                        continue;
                }

                // A hungry HERD predator (Penguin) must be free to leave the huddle and migrate to
                // its food the moment it wants to forage — not only when near-starving. Gating on a
                // hardcoded 0.4 left penguins stuck: they wanted to food-seek to the water but
                // cohesion dragged them back to the colony (on land) before they could reach a fish,
                // so the colony starved offshore of its food. Release cohesion at the SAME
                // ForageHungerThreshold that triggers the food-seeking roam, so the two never fight.
                if (hungerRatio < herdSpeciesDef.ForageHungerThreshold && social.Type != SocialType.Pack)
                    continue;
            }

            // === SOCIAL BEHAVIOR ===

            _spatialHash.QueryRadius(pos.X, pos.Y, socialRadius, _nearbyEntities);

            int currentGroupId = social.GroupId;
            int currentGroupSize = currentGroupId >= 0 && _groupSizes.TryGetValue(currentGroupId, out int sz) ? sz : 0;

            // === LEADER TRACKING ===
            int recognizedLeader = social.RecognizedLeader;
            float leaderX = 0f, leaderY = 0f;
            float leaderVelX = 0f, leaderVelY = 0f;
            bool hasVisibleLeader = false;

            // Check if current leader is still valid and in range
            if (recognizedLeader >= 0 && em.IsAlive(recognizedLeader))
            {
                ref var leaderPos = ref em.Positions[recognizedLeader];
                float distToLeader = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, leaderPos.X, leaderPos.Y));

                // Keep a leader only while it still outranks us. Leadership score tracks age
                // (age/lifespan, recomputed every tick), so the ordering shifts continuously —
                // but a recognised leader was previously retained for as long as it stayed alive
                // and in range, never re-checked. Stale assignments made from different moments
                // could then form a CYCLE (observed: wolf 40 → 43 → 44 → 40). A cycle has no
                // member that is its own leader, and HuntingSystem identifies the pack leader as
                // exactly that, so such a pack had no leader at all: no shared target, no roles,
                // no convergence. Re-validating makes "my leader outranks me" an invariant, which
                // is a strict order and therefore acyclic by construction.
                // Drop the link the MOMENT the ordering inverts rather than waiting out the
                // lost-leader timer, which would leave a cycle intact for hundreds of ticks.
                if (em.Socials[recognizedLeader].LeadershipScore <= social.LeadershipScore)
                {
                    social.RecognizedLeader = -1;
                    recognizedLeader = -1;
                    social.LeaderLostTicks = leaderLostThreshold; // re-elect on the spot
                }
                else if (distToLeader <= leaderInfluenceRadius)
                {
                    // Leader is in range
                    leaderX = leaderPos.X;
                    leaderY = leaderPos.Y;
                    ref var leaderVel = ref em.Velocities[recognizedLeader];
                    leaderVelX = leaderVel.Dx;
                    leaderVelY = leaderVel.Dy;
                    hasVisibleLeader = true;
                    social.LeaderLostTicks = 0;
                }
                else
                {
                    // RATE-LIKE — compensate. LeaderLostTicks is a stopwatch in ticks measured
                    // against LeaderLostThreshold, so it has to advance by the ticks that really
                    // passed; incrementing by one per due tick stretched the default 40-tick
                    // patience to 800 ticks at Minimal, and a distant herd kept following a leader
                    // it had long since lost sight of instead of electing a new one.
                    social.LeaderLostTicks += DecisionCadence.Elapsed(em, entity);
                }
            }
            else if (recognizedLeader >= 0)
            {
                // Leader died
                social.RecognizedLeader = -1;
                social.LeaderLostTicks = leaderLostThreshold;  // Immediately seek new leader
            }

            // Seek new leader if we've lost ours or don't have one
            if (!hasVisibleLeader && (social.LeaderLostTicks >= leaderLostThreshold || recognizedLeader < 0))
            {
                // Find best leader candidate in range
                float bestLeaderScore = social.LeadershipScore;  // Must be better than self
                int bestLeader = -1;

                foreach (int other in _nearbyEntities)
                {
                    if (other == entity || !em.IsAlive(other))
                        continue;

                    if (!em.HasComponents(other, ComponentFlags.Social | ComponentFlags.Species))
                        continue;

                    ref var otherSpecies = ref em.Species[other];
                    ref var otherSocial = ref em.Socials[other];

                    if (otherSpecies.Type != species.Type || !otherSocial.IsSocial)
                        continue;

                    // Must be in same group (or both ungrouped nearby)
                    if (currentGroupId >= 0 && otherSocial.GroupId != currentGroupId)
                        continue;

                    ref var otherPos = ref em.Positions[other];
                    float dist = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, otherPos.X, otherPos.Y));

                    if (dist <= leaderInfluenceRadius && otherSocial.LeadershipScore > bestLeaderScore)
                    {
                        bestLeaderScore = otherSocial.LeadershipScore;
                        bestLeader = other;
                    }
                }

                if (bestLeader >= 0)
                {
                    social.RecognizedLeader = bestLeader;
                    social.LeaderLostTicks = 0;
                    recognizedLeader = bestLeader;

                    ref var leaderPos = ref em.Positions[recognizedLeader];
                    leaderX = leaderPos.X;
                    leaderY = leaderPos.Y;
                    ref var leaderVel = ref em.Velocities[recognizedLeader];
                    leaderVelX = leaderVel.Dx;
                    leaderVelY = leaderVel.Dy;
                    hasVisibleLeader = true;
                }
            }

            // === GROUP MEMBERSHIP ===

            // Calculate local group info (for spacing, not primary cohesion)
            float localCenterX = 0f, localCenterY = 0f;
            int localNeighborCount = 0;
            int bestGroupToJoin = -1;
            float bestJoinDistance = maxJoinDistance;

            foreach (int other in _nearbyEntities)
            {
                if (other == entity || !em.IsAlive(other))
                    continue;

                if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Social))
                    continue;

                ref var otherSpecies = ref em.Species[other];
                ref var otherSocial = ref em.Socials[other];

                if (otherSpecies.Type != species.Type || !otherSocial.IsSocial)
                    continue;

                ref var otherPos = ref em.Positions[other];
                float dist = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, otherPos.X, otherPos.Y));

                if (currentGroupId >= 0 && otherSocial.GroupId == currentGroupId)
                {
                    localCenterX += otherPos.X;
                    localCenterY += otherPos.Y;
                    localNeighborCount++;
                }
                else if (currentGroupId < 0 && dist < bestJoinDistance)
                {
                    int otherGroupId = otherSocial.GroupId;
                    if (otherGroupId >= 0)
                    {
                        int otherGroupSize = _groupSizes.TryGetValue(otherGroupId, out int gsz) ? gsz : 1;
                        float maxSize = otherSocial.PreferredGroupSize * groupSizeTolerance;

                        if (otherGroupSize < maxSize)
                        {
                            bestGroupToJoin = otherGroupId;
                            bestJoinDistance = dist;
                        }
                    }
                }
            }

            // Handle group joining
            if (currentGroupId < 0)
            {
                if (bestGroupToJoin >= 0)
                {
                    social.GroupId = bestGroupToJoin;
                    social.RecognizedLeader = -1;  // Find leader in new group
                    _groupSizes.TryGetValue(bestGroupToJoin, out int cnt);
                    _groupSizes[bestGroupToJoin] = cnt + 1;
                }
                else
                {
                    // Try to form new group with nearby ungrouped
                    foreach (int other in _nearbyEntities)
                    {
                        if (other == entity || !em.IsAlive(other))
                            continue;
                        if (!em.HasComponents(other, ComponentFlags.Species | ComponentFlags.Social))
                            continue;

                        ref var otherSpecies = ref em.Species[other];
                        ref var otherSocial = ref em.Socials[other];

                        if (otherSpecies.Type != species.Type || !otherSocial.IsSocial)
                            continue;

                        ref var otherPos = ref em.Positions[other];
                        float dist = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, otherPos.X, otherPos.Y));

                        if (dist < socialRadius * 0.5f && otherSocial.GroupId < 0)
                        {
                            int newGroupId = _nextGroupId++;
                            social.GroupId = newGroupId;
                            _groupSizes[newGroupId] = 1;
                            break;
                        }
                    }
                }
            }

            // Check if we should leave an oversized group
            if (currentGroupId >= 0 && currentGroupSize > social.PreferredGroupSize * groupSizeTolerance * 1.2f)
            {
                if (social.LeadershipScore < 0.3f)
                {
                    social.GroupId = -1;
                    social.RecognizedLeader = -1;
                    _groupSizes[currentGroupId] = currentGroupSize - 1;
                    continue;
                }
            }

            // === APPLY SOCIAL FORCES ===

            if (hasVisibleLeader)
            {
                // FOLLOW THE LEADER behavior
                float distToLeader = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, leaderX, leaderY));

                // Distance-based following: maintain spacing, don't crowd leader
                float idealFollowDist = social.Type == SocialType.Pack ? 1.5f : 2f;
                float distanceFactor = MathF.Max(0, 1f - (distToLeader / leaderInfluenceRadius));

                // Size factor (reduce pull in large groups)
                float sizeFactor = 1f;
                if (currentGroupSize >= social.PreferredGroupSize)
                {
                    sizeFactor = MathF.Max(0.2f, 1f - (currentGroupSize - social.PreferredGroupSize) /
                                                      (social.PreferredGroupSize * 0.5f));
                }

                // Packs get much stronger cohesion and alignment than herds
                float packBoost = social.Type == SocialType.Pack ? 3f : 1f;
                // During active pack hunt, cohesion is even stronger to keep the group together
                if (isPackHunting) packBoost *= 2f;

                float effectiveCohesion = social.CohesionStrength * social.GroupAffinity * distanceFactor * sizeFactor * packBoost;

                // RATE-LIKE — compensate. Alignment is an exponential approach to the leader's
                // heading ("close the remaining velocity gap by this fraction"), applied once per
                // DECISION but written as a per-TICK rate. In view it fires twenty times per
                // twenty ticks and all but locks a follower onto the leader's course; at Minimal
                // it fired once and the follower kept most of its own heading, so a pack that
                // holds formation on screen sprayed apart off it — measured on predator_prey as a
                // mean wolf-to-wolf distance of 1.2 tiles at Full against 6.3 at Minimal.
                // BlendRate converts the per-tick rate to the single-decision equivalent (it
                // clamps at 1, so the pack-boosted gains above just saturate into a full snap).
                float effectiveAlignment = DecisionCadence.BlendRate(
                    social.AlignmentStrength * social.GroupAffinity * 1.5f * packBoost,
                    DecisionCadence.Interval(em, entity));

                // Cohesion: move toward leader (but maintain minimum distance).
                // STATE-LIKE — do NOT multiply. This is a pull proportional to the CURRENT gap,
                // feeding a velocity that MovementSystem damps on the same decision cadence, so
                // its steady state (pull / (1 - damping)) is the same at every tier. Scaling it by
                // the tick gap would fire a distant follower past its own leader.
                float cohesionX = 0f, cohesionY = 0f;
                if (distToLeader > idealFollowDist)
                {
                    cohesionX = (leaderX - pos.X) * effectiveCohesion;
                    cohesionY = (leaderY - pos.Y) * effectiveCohesion;
                }

                // Alignment: strongly match leader's direction
                float alignX = (leaderVelX - vel.Dx) * effectiveAlignment;
                float alignY = (leaderVelY - vel.Dy) * effectiveAlignment;

                vel.Dx += cohesionX + alignX;
                vel.Dy += cohesionY + alignY;
            }
            else if (localNeighborCount > 0)
            {
                // No leader visible - fall back to local cohesion
                localCenterX /= localNeighborCount;
                localCenterY /= localNeighborCount;

                float distToCenter = MathF.Sqrt(MathUtils.DistanceSquared(pos.X, pos.Y, localCenterX, localCenterY));
                float distanceFactor = MathF.Max(0, 1f - (distToCenter / (socialRadius * 0.8f)));

                float sizeFactor = 1f;
                if (currentGroupSize >= social.PreferredGroupSize)
                {
                    sizeFactor = MathF.Max(0.1f, 1f - (currentGroupSize - social.PreferredGroupSize) /
                                                      (social.PreferredGroupSize * 0.5f));
                }

                // STATE-LIKE, as above: a gap-proportional pull on a velocity damped at the same
                // cadence, not a quantity accrued per tick.
                float effectiveCohesion = social.CohesionStrength * social.GroupAffinity * distanceFactor * sizeFactor * 0.5f;

                float cohesionX = (localCenterX - pos.X) * effectiveCohesion;
                float cohesionY = (localCenterY - pos.Y) * effectiveCohesion;

                vel.Dx += cohesionX;
                vel.Dy += cohesionY;
            }
            else if (currentGroupId >= 0 && currentGroupSize <= 1)
            {
                // Alone in group - clear it
                social.GroupId = -1;
                social.RecognizedLeader = -1;
            }
        }
    }
}
