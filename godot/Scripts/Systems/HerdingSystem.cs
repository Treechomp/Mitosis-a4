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

                if (predator.HasTarget && hungerRatio < 0.6f)
                {
                    // Pack members keep cohesion while hunting — they need to stick together
                    if (social.Type == SocialType.Pack)
                        isPackHunting = true;
                    else
                        continue;
                }

                if (hungerRatio < 0.4f && social.Type != SocialType.Pack)
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

                if (distToLeader <= leaderInfluenceRadius)
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
                    // Leader out of range
                    social.LeaderLostTicks++;
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
                float effectiveAlignment = social.AlignmentStrength * social.GroupAffinity * 1.5f * packBoost;

                // Cohesion: move toward leader (but maintain minimum distance)
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
