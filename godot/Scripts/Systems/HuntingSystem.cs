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
/// Processes hunting behavior for predators using spatial hashing.
/// Features:
/// - Hunger-driven hunting with urgency scaling
/// - Terrain discomfort balance
/// - Pack hunting coordination (leader selects target, pack adopts)
/// - Pack tactics: fan out to surround, rush/retreat cycles
/// </summary>
public sealed class HuntingSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly WorldManager? _worldManager;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly List<int> _packMembers = new(8);
    private readonly List<int> _shareBuffer = new(16);
    private readonly List<int> _entitiesToKill = new(16);
    private readonly Dictionary<int, int> _groupTargets = new(16);  // groupId -> target entity
    private const float SporeBodyMass = 0.2f;
    private const float FallbackNutrition = 40f;

    public HuntingSystem(SpatialHash spatialHash, WorldManager? worldManager = null)
    {
        _spatialHash = spatialHash;
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        _entitiesToKill.Clear();
        _groupTargets.Clear();

        // Update spatial hash for all prey (targets for hunting)
        const ComponentFlags preyRequired = ComponentFlags.Position | ComponentFlags.Prey;
        foreach (int entity in em.Query(preyRequired))
        {
            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Also update predator-only entities for pack coordination detection
        // (prey-predators like Sectids are already updated above)
        const ComponentFlags predPosRequired = ComponentFlags.Position | ComponentFlags.Predator | ComponentFlags.Hunger;
        foreach (int entity in em.Query(predPosRequired))
        {
            if (!em.HasComponents(entity, ComponentFlags.Prey))
            {
                ref var pos = ref em.Positions[entity];
                _spatialHash.Update(entity, pos.X, pos.Y);
            }
        }

        // First pass: Any pack member with a target shares it with the group
        // Leaders take priority, but any member can trigger a group hunt
        const ComponentFlags predatorRequired = ComponentFlags.Position | ComponentFlags.Predator | ComponentFlags.Hunger;
        foreach (int entity in em.Query(predatorRequired))
        {
            ref var predator = ref em.Predators[entity];

            if (!predator.HasTarget || !em.IsAlive(predator.TargetEntity))
                continue;

            if (!em.HasComponents(entity, ComponentFlags.Social))
                continue;

            ref var social = ref em.Socials[entity];
            if (social.Type != SocialType.Pack || social.GroupId < 0)
                continue;

            // Leaders always set the group target; non-leaders only if no target set yet
            bool isLeader = social.RecognizedLeader < 0 || social.RecognizedLeader == entity;
            if (isLeader || !_groupTargets.ContainsKey(social.GroupId))
            {
                _groupTargets[social.GroupId] = predator.TargetEntity;
            }
        }

        // Second pass: All predators hunt
        foreach (int entity in em.Query(predatorRequired))
        {
            // LOD gate: skip hunting only for very distant entities (Aggregate)
            // Hunting feeds Sectids and predators — gating too early causes starvation
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Aggregate)
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var predator = ref em.Predators[entity];
            ref var hunger = ref em.Hungers[entity];

            // Get species definition for this predator (all hunting params are per-species)
            SpeciesDefinition speciesDef;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var sp = ref em.Species[entity];
                speciesDef = SpeciesRegistry.GetById(sp.SpeciesId);
            }
            else
            {
                speciesDef = SpeciesRegistry.Get("Wolf"); // fallback
            }

            // Reduce cooldowns
            if (predator.CurrentCooldown > 0)
                predator.CurrentCooldown--;
            if (predator.PhaseTimer > 0)
                predator.PhaseTimer--;

            // Clear target if dead
            if (predator.HasTarget && !em.IsAlive(predator.TargetEntity))
            {
                predator.TargetEntity = -1;
                predator.Phase = PackPhase.Idle;
                predator.Role = PackRole.None;
            }

            // Abandon target if it's too far away (3× hunt range)
            // Prevents cross-map chases when prey roams/flees far from predator
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity))
            {
                float abandonRange = predator.HuntRange * 3f;
                ref var targetPos = ref em.Positions[predator.TargetEntity];
                float abandonDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, targetPos.X, targetPos.Y);
                if (abandonDistSq > abandonRange * abandonRange)
                {
                    predator.TargetEntity = -1;
                    predator.Phase = PackPhase.Idle;
                    predator.Role = PackRole.None;
                }
            }

            float hungerRatio = hunger.Current / hunger.Max;

            // Stop hunting if full
            if (hungerRatio >= speciesDef.HuntThreshold)
            {
                predator.TargetEntity = -1;
                predator.Phase = PackPhase.Idle;
                predator.Role = PackRole.None;
                continue;
            }

            float urgency = 1f - (hungerRatio / speciesDef.HuntThreshold);

            // Check terrain discomfort
            float discomfortRatio = 0f;
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];
                discomfortRatio = discomfort.Ratio;
                float discomfortTolerance = urgency;
                if (discomfort.ExceedsThreshold && discomfortRatio > discomfortTolerance + 0.3f)
                {
                    predator.TargetEntity = -1;
                    predator.Phase = PackPhase.Idle;
                    continue;
                }
            }

            // Calculate modifiers
            float rangeMultiplier = 1f + (urgency * 0.5f);
            if (discomfortRatio > 0.3f)
                rangeMultiplier *= (1f - discomfortRatio * 0.5f);

            // Hungrier = more desperate = faster hunting (up to 1.5x at starvation)
            float speedMultiplier = 1f + (urgency * 0.5f);

            // Check for pack membership and coordination
            // A predator is only "in a pack" if it has SocialType.Pack, a valid group,
            // AND at least one nearby pack member. Otherwise it hunts solo.
            bool isPack = false;
            int groupId = -1;
            if (em.HasComponents(entity, ComponentFlags.Social))
            {
                ref var social = ref em.Socials[entity];
                if (social.Type == SocialType.Pack && social.GroupId >= 0)
                {
                    groupId = social.GroupId;

                    // Count nearby pack members to confirm this is a real pack hunt
                    _packMembers.Clear();
                    _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackCoordinationRadius, _packMembers);
                    int nearbyPackCount = 0;
                    foreach (int other in _packMembers)
                    {
                        if (other == entity || !em.IsAlive(other)) continue;
                        if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social)) continue;
                        ref var otherSocial = ref em.Socials[other];
                        if (otherSocial.GroupId == groupId)
                        {
                            nearbyPackCount++;
                            break;  // At least one is enough
                        }
                    }

                    isPack = nearbyPackCount > 0;

                    // If alone and not already committed to a pack hunt, reset to solo
                    if (!isPack)
                    {
                        if (predator.HasTarget && predator.Role != PackRole.None)
                        {
                            // Already committed to a pack hunt — maintain pack state
                            // even if temporarily out of coordination range (e.g., flanking)
                            isPack = true;
                        }
                        else
                        {
                            predator.Role = PackRole.None;
                            predator.Phase = PackPhase.Idle;
                        }
                    }
                }

                // Adopt pack target — any member's chase triggers group hunt
                // But only if the target is within reasonable range (3× hunt range)
                if (isPack && _groupTargets.TryGetValue(groupId, out int packTarget) && em.IsAlive(packTarget))
                {
                    if (!predator.HasTarget || predator.TargetEntity != packTarget)
                    {
                        ref var ptPos = ref em.Positions[packTarget];
                        float packTargetDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, ptPos.X, ptPos.Y);
                        float adoptRange = predator.HuntRange * 3f;
                        if (packTargetDistSq <= adoptRange * adoptRange)
                        {
                            predator.TargetEntity = packTarget;
                            AssignPackRole(entity, packTarget, em, ref predator, ref social, speciesDef);
                        }
                    }
                }
            }

            // Find target if we don't have one
            if (!predator.HasTarget)
            {
                float effectiveRange = predator.HuntRange * rangeMultiplier;
                float huntRangeSq = effectiveRange * effectiveRange;
                _spatialHash.QueryRadius(pos.X, pos.Y, effectiveRange, _nearbyEntities);

                // Calculate effective hunting mass (solo or pack)
                float effectiveMass = speciesDef.BodyMass;
                float maxHuntRatio = speciesDef.SoloHuntMaxRatio;
                bool isSwarm = speciesDef.SwarmHunter;
                if (isPack)
                {
                    // Count nearby pack/swarm members for effective mass
                    int packSize = 1;
                    foreach (int other in _nearbyEntities)
                    {
                        if (other == entity || !em.IsAlive(other))
                            continue;
                        if (isSwarm)
                        {
                            // Swarm: count ALL nearby same-species (colony-wide bravery)
                            if (em.HasComponents(other, ComponentFlags.Species) &&
                                em.HasComponents(entity, ComponentFlags.Species) &&
                                em.Species[other].SpeciesId == em.Species[entity].SpeciesId)
                                packSize++;
                        }
                        else
                        {
                            // Traditional pack: same-group members only
                            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social))
                                continue;
                            ref var otherSocial = ref em.Socials[other];
                            if (otherSocial.GroupId == groupId)
                                packSize++;
                        }
                    }
                    float exponent = speciesDef.PackHuntMassExponent;
                    effectiveMass *= MathF.Pow(packSize, exponent);
                }
                float maxPreyMass = effectiveMass * maxHuntRatio;

                float bestScore = float.MaxValue;
                int bestPrey = -1;

                foreach (int preyEntity in _nearbyEntities)
                {
                    if (!em.IsAlive(preyEntity))
                        continue;

                    // Swarm hunters can target any living creature (including predators)
                    // Normal hunters can only target entities with the Prey flag
                    bool isValidTarget = em.HasComponents(preyEntity, ComponentFlags.Prey);
                    if (!isValidTarget && isSwarm)
                        isValidTarget = em.HasComponents(preyEntity, ComponentFlags.Energy | ComponentFlags.Species);
                    if (!isValidTarget)
                        continue;

                    // Size-based eligibility: prey must not be too large
                    float preyMass = GetPreyBodyMass(preyEntity, em);
                    if (preyMass > maxPreyMass)
                        continue;  // Too large to hunt

                    // Skip same-species targets (no cannibalism) and unhuntable species
                    if (em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        ref var preySpecies = ref em.Species[preyEntity];
                        var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);

                        // Don't hunt your own kind
                        if (em.HasComponents(entity, ComponentFlags.Species))
                        {
                            ref var mySpecies = ref em.Species[entity];
                            if (preySpecies.SpeciesId == mySpecies.SpeciesId)
                                continue;
                        }

                        if (preyDef.UnhuntableByPredators
                            && speciesDef.Diet == DietType.Carnivore)
                            continue;  // Faelings can't be hunted by carnivores
                    }

                    ref var preyPos = ref em.Positions[preyEntity];
                    float distSq = MathUtils.DistanceSquared(pos.X, pos.Y, preyPos.X, preyPos.Y);

                    if (distSq >= huntRangeSq)
                        continue;

                    float score = distSq;

                    // Terrain penalty
                    if (_worldManager != null)
                    {
                        var preyTile = _worldManager.GetTile(preyPos.X, preyPos.Y);
                        float terrainPenalty = preyTile.GetAvoidanceWeight() * 50f;
                        terrainPenalty *= (1f - urgency * 0.7f);
                        score += terrainPenalty;

                        // Land predators reject targets across water
                        if (!speciesDef.SemiAquatic)
                        {
                            float waterFrac = _worldManager.GetWaterFractionOnPath(
                                pos.X, pos.Y, preyPos.X, preyPos.Y);
                            if (waterFrac > 0.15f)
                                continue; // Too much water between us and prey
                        }
                    }

                    // Preferred prey bias: familiar prey scores better (lower)
                    if (speciesDef.PreferredPrey != null && em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        ref var preySpecies = ref em.Species[preyEntity];
                        var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);
                        if (speciesDef.PreferredPrey.Contains(preyDef.Name))
                            score *= speciesDef.PreferredPreyBias;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPrey = preyEntity;
                    }
                }

                if (bestPrey >= 0)
                {
                    predator.TargetEntity = bestPrey;
                    if (isPack)
                    {
                        _groupTargets[groupId] = bestPrey;
                        predator.Role = PackRole.Leader;
                        predator.Phase = PackPhase.Positioning;
                        predator.PhaseTimer = speciesDef.PositioningDuration;
                    }
                }
            }

            // Hunger-driven tracking: when hungry and no target, search wide range
            if (!predator.HasTarget && hungerRatio < speciesDef.TrackingHungerThreshold
                && em.HasComponents(entity, ComponentFlags.Velocity))
            {
                // Wide-range scan for nearest prey (simulates scent/tracking)
                _nearbyEntities.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.TrackingRange, _nearbyEntities);

                float bestTrackDistSq = float.MaxValue;
                int bestTrackTarget = -1;

                foreach (int preyEntity in _nearbyEntities)
                {
                    if (!em.IsAlive(preyEntity))
                        continue;

                    // Swarm hunters can track any living creature
                    bool isTrackable = em.HasComponents(preyEntity, ComponentFlags.Prey);
                    if (!isTrackable && speciesDef.SwarmHunter)
                        isTrackable = em.HasComponents(preyEntity, ComponentFlags.Energy | ComponentFlags.Species);
                    if (!isTrackable)
                        continue;

                    // Don't track your own species
                    if (em.HasComponents(entity, ComponentFlags.Species) &&
                        em.HasComponents(preyEntity, ComponentFlags.Species))
                    {
                        if (em.Species[entity].SpeciesId == em.Species[preyEntity].SpeciesId)
                            continue;
                    }

                    ref var preyPos2 = ref em.Positions[preyEntity];

                    // Land predators skip tracking targets across water
                    if (!speciesDef.SemiAquatic && _worldManager != null)
                    {
                        float waterFrac = _worldManager.GetWaterFractionOnPath(
                            pos.X, pos.Y, preyPos2.X, preyPos2.Y);
                        if (waterFrac > 0.15f)
                            continue;
                    }

                    float trackDistSq = MathUtils.DistanceSquared(pos.X, pos.Y, preyPos2.X, preyPos2.Y);
                    if (trackDistSq < bestTrackDistSq)
                    {
                        bestTrackDistSq = trackDistSq;
                        bestTrackTarget = preyEntity;
                    }
                }

                if (bestTrackTarget >= 0)
                {
                    // Move toward prey at wander speed (tracking, not chasing)
                    ref var vel = ref em.Velocities[entity];
                    ref var trackPreyPos = ref em.Positions[bestTrackTarget];
                    float tdx = trackPreyPos.X - pos.X;
                    float tdy = trackPreyPos.Y - pos.Y;
                    var trackDir = MathUtils.Normalize(tdx, tdy);
                    float trackSpeed = speciesDef.BaseHuntSpeed * 0.8f;
                    // Blend toward tracking direction — heavier predators commit more
                    float trackAgility = Math.Clamp(1.5f / speciesDef.BodyMass, 0.25f, 1f);
                    vel.Dx += (trackDir.X * trackSpeed - vel.Dx) * trackAgility;
                    vel.Dy += (trackDir.Y * trackSpeed - vel.Dy) * trackAgility;

                    if (!speciesDef.SemiAquatic)
                        SteerAroundWater(ref vel, pos.X, pos.Y);

                    continue;  // Skip normal hunt movement — we're just tracking
                }
            }

            // Hunt the target
            if (predator.HasTarget && em.IsAlive(predator.TargetEntity))
            {
                ref var preyPos = ref em.Positions[predator.TargetEntity];
                float dx = preyPos.X - pos.X;
                float dy = preyPos.Y - pos.Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float distSq = dx * dx + dy * dy;

                // Mass-based agility for direction blending during pursuit
                float huntAgility = Math.Clamp(1.5f / speciesDef.BodyMass, 0.25f, 1f);

                // Attack if in range
                float attackRangeSq = predator.AttackRange * predator.AttackRange;
                if (distSq < attackRangeSq && predator.CurrentCooldown == 0)
                {
                    if (em.HasComponents(predator.TargetEntity, ComponentFlags.Energy))
                    {
                        ref var preyEnergy = ref em.Energies[predator.TargetEntity];
                        preyEnergy.Current -= predator.AttackPower;
                        preyEnergy.RegenCooldown = 60; // 3s combat cooldown at 20 TPS

                        if (preyEnergy.IsDead)
                        {
                            _entitiesToKill.Add(predator.TargetEntity);

                            // Nutrition scales with prey mass (and growth for Shroomers)
                            float nutrition = GetPreyNutrition(predator.TargetEntity, em);

                            // Pack food sharing: killer gets half, rest split among nearby pack
                            if (isPack)
                            {
                                float killerPortion = nutrition * speciesDef.KillerShareRatio;
                                float sharePortion = nutrition - killerPortion;

                                ApplyFoodGain(entity, killerPortion, em, ref hunger);

                                // Find nearby same-species pack members to share with
                                _shareBuffer.Clear();
                                _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackShareRadius, _shareBuffer);
                                int shareCount = 0;
                                foreach (int other in _shareBuffer)
                                {
                                    if (other == entity || !em.IsAlive(other)) continue;
                                    if (!em.HasComponents(other, ComponentFlags.Hunger | ComponentFlags.Social)) continue;
                                    ref var otherSocial = ref em.Socials[other];
                                    if (otherSocial.GroupId == groupId)
                                        shareCount++;
                                }
                                if (shareCount > 0)
                                {
                                    float perMember = sharePortion / shareCount;
                                    foreach (int other in _shareBuffer)
                                    {
                                        if (other == entity || !em.IsAlive(other)) continue;
                                        if (!em.HasComponents(other, ComponentFlags.Hunger | ComponentFlags.Social)) continue;
                                        ref var otherSocial = ref em.Socials[other];
                                        if (otherSocial.GroupId == groupId)
                                        {
                                            ref var otherHunger = ref em.Hungers[other];
                                            ApplyFoodGain(other, perMember, em, ref otherHunger);
                                        }
                                    }
                                }
                                else
                                {
                                    // No pack members nearby — killer gets everything
                                    ApplyFoodGain(entity, sharePortion, em, ref hunger);
                                }
                            }
                            else
                            {
                                // Solo hunter gets all the nutrition
                                ApplyFoodGain(entity, nutrition, em, ref hunger);
                            }

                            predator.TargetEntity = -1;
                            predator.Phase = PackPhase.Idle;
                            predator.Role = PackRole.None;
                        }
                    }
                    predator.CurrentCooldown = predator.AttackCooldown;

                    // After attacking, retreat only if prey still in herd (pack tactic to disperse)
                    // Swarm hunters never retreat — they just keep biting
                    if (isPack && !speciesDef.SwarmHunter && predator.Phase == PackPhase.Rushing)
                    {
                        bool preyIsolated = IsPreyIsolated(predator.TargetEntity, em);
                        if (!preyIsolated)
                        {
                            // Still in herd - retreat to continue harassment
                            predator.Phase = PackPhase.Retreating;
                            predator.PhaseTimer = speciesDef.RetreatDuration;
                        }
                        // If isolated, stay in rushing/chase mode
                    }
                }
                else if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    ref var vel = ref em.Velocities[entity];

                    float huntSpeed = speciesDef.BaseHuntSpeed * speedMultiplier;

                    // Pack tactics based on role and phase
                    if (isPack && predator.Role != PackRole.None)
                    {
                        // Swarm hunters (small body mass) skip positioning/retreat tactics
                        // and just rush directly — prey doesn't flee from them anyway
                        if (speciesDef.BodyMass < 1.0f)
                        {
                            // Direct swarm chase — all rush together
                            var dir = MathUtils.Normalize(dx, dy);
                            float swarmSpeed = huntSpeed * 1.2f;
                            BlendVelocity(ref vel, dir.X * swarmSpeed, dir.Y * swarmSpeed, huntAgility);
                        }
                        else
                        {
                            // Check if prey is isolated from herd
                            bool preyIsolated = IsPreyIsolated(predator.TargetEntity, em);
                            ApplyPackTactics(entity, ref pos, ref vel, ref predator, preyPos.X, preyPos.Y, dist, huntSpeed, em, preyIsolated, speciesDef, huntAgility);
                        }
                    }
                    else
                    {
                        // Solo hunting - direct chase
                        var dir = MathUtils.Normalize(dx, dy);
                        BlendVelocity(ref vel, dir.X * huntSpeed, dir.Y * huntSpeed, huntAgility);
                    }

                    // Land predators steer around water during pursuit
                    if (!speciesDef.SemiAquatic)
                        SteerAroundWater(ref vel, pos.X, pos.Y);
                }
            }
        }

        // Kill dead prey
        foreach (int preyEntity in _entitiesToKill)
        {
            // Faeling death: pass power to crystal for next spawn
            if (em.HasComponents(preyEntity, ComponentFlags.FaelingPower))
            {
                ref var power = ref em.FaelingPowers[preyEntity];
                int crystalId = power.LinkedCrystal;
                if (crystalId >= 0 && em.IsAlive(crystalId) && em.HasComponents(crystalId, ComponentFlags.Crystal))
                {
                    ref var crystal = ref em.Crystals[crystalId];
                    crystal.InheritedPower = power.Power * 0.5f;
                    crystal.LinkedFaeling = -1;
                    crystal.SpawnTimer = crystal.SpawnDelay;
                }
            }

            _spatialHash.Remove(preyEntity);
            em.DestroyEntity(preyEntity);
        }
    }

    /// <summary>
    /// Check if a prey is isolated from its herd (few/no other prey nearby).
    /// </summary>
    private bool IsPreyIsolated(int preyEntity, EntityManager em)
    {
        if (!em.IsAlive(preyEntity))
            return true;

        ref var preyPos = ref em.Positions[preyEntity];
        float isolationRadius = 6f;  // Distance to check for other prey
        int minHerdSize = 2;         // Need at least this many nearby to be "in herd"

        _nearbyEntities.Clear();
        _spatialHash.QueryRadius(preyPos.X, preyPos.Y, isolationRadius, _nearbyEntities);

        int nearbyPreyCount = 0;
        foreach (int other in _nearbyEntities)
        {
            if (other == preyEntity || !em.IsAlive(other))
                continue;

            if (em.HasComponents(other, ComponentFlags.Prey))
                nearbyPreyCount++;

            if (nearbyPreyCount >= minHerdSize)
                return false;  // Still has herd protection
        }

        return true;  // Isolated - few or no other prey nearby
    }

    private void AssignPackRole(int entity, int target, EntityManager em, ref Predator predator, ref Social social,
                                SpeciesDefinition speciesDef)
    {
        // Find other pack members
        ref var pos = ref em.Positions[entity];
        ref var targetPos = ref em.Positions[target];
        _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackCoordinationRadius, _packMembers);

        int flankersCount = 0;
        bool hasLeader = false;

        foreach (int other in _packMembers)
        {
            if (other == entity || !em.IsAlive(other))
                continue;
            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social))
                continue;

            ref var otherSocial = ref em.Socials[other];
            if (otherSocial.GroupId != social.GroupId)
                continue;

            ref var otherPredator = ref em.Predators[other];
            if (otherPredator.Role == PackRole.Leader)
                hasLeader = true;
            else if (otherPredator.Role == PackRole.Flanker)
                flankersCount++;
        }

        // Assign role
        if (!hasLeader && social.LeadershipScore > 0.5f)
        {
            predator.Role = PackRole.Leader;
        }
        else if (flankersCount < 2)
        {
            predator.Role = PackRole.Flanker;
        }
        else
        {
            predator.Role = PackRole.Chaser;
        }

        predator.Phase = PackPhase.Positioning;
        predator.PhaseTimer = speciesDef.PositioningDuration;
    }

    private void ApplyPackTactics(int entity, ref Position pos, ref Velocity vel, ref Predator predator,
                                   float targetX, float targetY, float dist, float huntSpeed, EntityManager em,
                                   bool preyIsolated, SpeciesDefinition speciesDef, float agility)
    {
        float dx = targetX - pos.X;
        float dy = targetY - pos.Y;

        // Get role-specific speed modifier from species definition
        float roleSpeedMult = predator.Role switch
        {
            PackRole.Leader => speciesDef.LeaderSpeedMult,
            PackRole.Flanker => speciesDef.FlankerSpeedMult,
            PackRole.Chaser => speciesDef.ChaserSpeedMult,
            _ => 1.0f
        };

        // Apply role modifier to hunt speed
        float effectiveSpeed = huntSpeed * roleSpeedMult;

        // If prey is isolated, switch to full chase mode - no more retreat/positioning
        if (preyIsolated)
        {
            // Direct chase - prey is separated from herd, go for the kill
            var chaseDir = MathUtils.Normalize(dx, dy);
            BlendVelocity(ref vel, chaseDir.X * effectiveSpeed * 1.2f, chaseDir.Y * effectiveSpeed * 1.2f, agility);

            // Reset phase to rushing (continuous attack)
            if (predator.Phase != PackPhase.Rushing)
            {
                predator.Phase = PackPhase.Rushing;
                predator.PhaseTimer = speciesDef.RushDuration;
            }
            return;
        }

        // Prey still in herd - use rush/retreat tactics to disperse
        // Handle phase transitions
        if (predator.PhaseTimer <= 0)
        {
            switch (predator.Phase)
            {
                case PackPhase.Positioning:
                    predator.Phase = PackPhase.Rushing;
                    predator.PhaseTimer = speciesDef.RushDuration;
                    break;
                case PackPhase.Rushing:
                    predator.Phase = PackPhase.Retreating;
                    predator.PhaseTimer = speciesDef.RetreatDuration;
                    break;
                case PackPhase.Retreating:
                    predator.Phase = PackPhase.Positioning;
                    predator.PhaseTimer = speciesDef.PositioningDuration;
                    break;
            }
        }

        switch (predator.Phase)
        {
            case PackPhase.Positioning:
                // Fan out to surround
                ApplyPositioningMovement(ref pos, ref vel, predator.Role, dx, dy, dist, effectiveSpeed * 0.7f, agility);
                break;

            case PackPhase.Rushing:
                // All rush in together to scatter the herd
                var rushDir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, rushDir.X * effectiveSpeed * 1.3f, rushDir.Y * effectiveSpeed * 1.3f, agility);
                break;

            case PackPhase.Retreating:
                // Back off after harassment - this gives herd time to scatter
                float retreatDist = 5f;
                if (dist < retreatDist)
                {
                    var retreatDir = MathUtils.Normalize(-dx, -dy);
                    BlendVelocity(ref vel, retreatDir.X * effectiveSpeed * 0.8f, retreatDir.Y * effectiveSpeed * 0.8f, agility);
                }
                else
                {
                    // Far enough, go back to positioning for next rush
                    predator.Phase = PackPhase.Positioning;
                    predator.PhaseTimer = speciesDef.PositioningDuration;
                }
                break;

            default:
                // Direct approach
                var dir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, dir.X * effectiveSpeed, dir.Y * effectiveSpeed, agility);
                break;
        }
    }

    private void ApplyPositioningMovement(ref Position pos, ref Velocity vel, PackRole role,
                                          float dx, float dy, float dist, float speed, float agility)
    {
        float targetDist = 5f;  // Ideal distance from prey during positioning

        switch (role)
        {
            case PackRole.Leader:
                // Leader approaches from front, maintains distance
                if (dist > targetDist)
                {
                    var dir = MathUtils.Normalize(dx, dy);
                    BlendVelocity(ref vel, dir.X * speed, dir.Y * speed, agility);
                }
                else
                {
                    // Hold position, circle slowly
                    BlendVelocity(ref vel, -dy * 0.01f, dx * 0.01f, agility);
                }
                break;

            case PackRole.Flanker:
                // Flankers move to the sides
                // Calculate perpendicular position
                float perpX = -dy;
                float perpY = dx;
                float perpLen = MathF.Sqrt(perpX * perpX + perpY * perpY);
                if (perpLen > 0.01f)
                {
                    perpX /= perpLen;
                    perpY /= perpLen;
                }

                // Determine which side (based on current position)
                float side = (pos.X - (pos.X + dx)) * perpY - (pos.Y - (pos.Y + dy)) * perpX;
                float sideSign = side >= 0 ? 1f : -1f;

                // Target position: to the side and at target distance
                float flankX = (pos.X + dx) + perpX * targetDist * sideSign * 0.8f - dx * 0.3f;
                float flankY = (pos.Y + dy) + perpY * targetDist * sideSign * 0.8f - dy * 0.3f;

                float toFlankX = flankX - pos.X;
                float toFlankY = flankY - pos.Y;
                var flankDir = MathUtils.Normalize(toFlankX, toFlankY);
                BlendVelocity(ref vel, flankDir.X * speed, flankDir.Y * speed, agility);
                break;

            case PackRole.Chaser:
                // Chasers follow behind, ready to cut off escape
                if (dist > targetDist * 1.5f)
                {
                    var dir = MathUtils.Normalize(dx, dy);
                    BlendVelocity(ref vel, dir.X * speed * 0.9f, dir.Y * speed * 0.9f, agility);
                }
                else
                {
                    // Stay back a bit
                    var dir = MathUtils.Normalize(-dx, -dy);
                    BlendVelocity(ref vel, dir.X * speed * 0.3f, dir.Y * speed * 0.3f, agility);
                }
                break;

            default:
                var defaultDir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, defaultDir.X * speed, defaultDir.Y * speed, agility);
                break;
        }
    }

    /// <summary>
    /// Blend velocity toward a target using mass-based agility.
    /// Smaller creatures (high agility) snap quickly; heavier ones turn gradually.
    /// </summary>
    private static void BlendVelocity(ref Velocity vel, float targetDx, float targetDy, float agility)
    {
        vel.Dx += (targetDx - vel.Dx) * agility;
        vel.Dy += (targetDy - vel.Dy) * agility;
    }

    /// <summary>
    /// Apply food gain to an entity, handling Sectid food carriers specially.
    /// </summary>
    private static void ApplyFoodGain(int entity, float nutrition, EntityManager em, ref Hunger hunger)
    {
        if (em.HasComponents(entity, ComponentFlags.FoodCarrier))
        {
            ref var carrier = ref em.FoodCarriers[entity];
            float foodGain = MathF.Min(nutrition, carrier.MaxCarry - carrier.FoodCarried);
            carrier.FoodCarried += foodGain;
            // Sectids eat half themselves (need sustenance since they can't graze)
            hunger.Current = MathF.Min(hunger.Max, hunger.Current + nutrition * 0.5f);
        }
        else
        {
            hunger.Current = MathF.Min(hunger.Max, hunger.Current + nutrition);
        }
    }

    /// <summary>
    /// Steer velocity away from water tiles ahead. Checks 2 tiles in the movement
    /// direction; if water is found, tries ±45° and ±90° offsets and picks the clearest.
    /// </summary>
    private void SteerAroundWater(ref Velocity vel, float posX, float posY)
    {
        if (_worldManager == null || (vel.Dx == 0 && vel.Dy == 0)) return;

        float speed = MathF.Sqrt(vel.Dx * vel.Dx + vel.Dy * vel.Dy);
        float nx = vel.Dx / speed;
        float ny = vel.Dy / speed;

        // Look ahead 2 tiles for water
        bool waterAhead = false;
        for (float d = 1f; d <= 2f; d += 1f)
        {
            if (_worldManager.GetTile(posX + nx * d, posY + ny * d).IsWater())
            {
                waterAhead = true;
                break;
            }
        }
        if (!waterAhead) return;

        // Try offset angles and pick the clearest path
        int bestWater = 3;
        float bestAngle = 0f;
        ReadOnlySpan<float> offsets = stackalloc float[]
        {
            MathF.PI / 4, -MathF.PI / 4,
            MathF.PI / 2, -MathF.PI / 2
        };

        foreach (float angle in offsets)
        {
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);
            float rnx = nx * cos - ny * sin;
            float rny = nx * sin + ny * cos;

            int waterCount = 0;
            for (float d = 1f; d <= 2f; d += 1f)
            {
                if (_worldManager.GetTile(posX + rnx * d, posY + rny * d).IsWater())
                    waterCount++;
            }

            if (waterCount < bestWater)
            {
                bestWater = waterCount;
                bestAngle = angle;
                if (waterCount == 0) break; // Clear path found
            }
        }

        if (bestAngle != 0f)
        {
            float cos = MathF.Cos(bestAngle);
            float sin = MathF.Sin(bestAngle);
            vel.Dx = (nx * cos - ny * sin) * speed;
            vel.Dy = (nx * sin + ny * cos) * speed;
        }
    }

    /// <summary>
    /// Get the effective body mass of a prey entity for hunting eligibility.
    /// Spores use a small fixed mass. Growing creatures scale mass with CurrentScale.
    /// </summary>
    private float GetPreyBodyMass(int preyEntity, EntityManager em)
    {
        if (em.HasComponents(preyEntity, ComponentFlags.Spore))
            return SporeBodyMass;

        if (em.HasComponents(preyEntity, ComponentFlags.Species))
        {
            ref var preySpecies = ref em.Species[preyEntity];
            var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);
            float mass = preyDef.BodyMass;

            // Growing creatures (Shroomers) scale mass with current size
            if (em.HasComponents(preyEntity, ComponentFlags.Growth))
            {
                ref var growth = ref em.Growths[preyEntity];
                mass *= growth.CurrentScale;
            }

            return mass;
        }

        return 1f;
    }

    /// <summary>
    /// Get the nutrition value of a prey entity. Scales with growth for growing creatures.
    /// </summary>
    private float GetPreyNutrition(int preyEntity, EntityManager em)
    {
        if (em.HasComponents(preyEntity, ComponentFlags.Spore))
            return SporeBodyMass * 20f;

        if (em.HasComponents(preyEntity, ComponentFlags.Species))
        {
            ref var preySpecies = ref em.Species[preyEntity];
            var preyDef = SpeciesRegistry.GetById(preySpecies.SpeciesId);
            float nutrition = preyDef.EffectiveNutrition;

            if (em.HasComponents(preyEntity, ComponentFlags.Growth))
            {
                ref var growth = ref em.Growths[preyEntity];
                nutrition *= growth.CurrentScale;
            }

            return nutrition;
        }

        return FallbackNutrition;
    }
}
