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
    private readonly Dictionary<int, (float x, float y)> _groupLeaderPositions = new(16);  // groupId -> leader pos
    private readonly Dictionary<int, bool> _groupConverging = new(16);  // groupId -> leader triggered convergence
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

        // Spatial hash already updated by SpatialHashUpdateSystem

        // First pass: Share targets, leader positions, and convergence state within packs
        const ComponentFlags predatorRequired = ComponentFlags.Position | ComponentFlags.Predator | ComponentFlags.Hunger;
        _groupLeaderPositions.Clear();
        _groupConverging.Clear();

        foreach (int entity in em.Query(predatorRequired))
        {
            ref var predator = ref em.Predators[entity];

            if (!em.HasComponents(entity, ComponentFlags.Social))
                continue;

            ref var social = ref em.Socials[entity];
            if (social.Type != SocialType.Pack || social.GroupId < 0)
                continue;

            // Track leader positions and convergence triggers
            if (predator.Role == PackRole.Leader)
            {
                ref var ldrPos = ref em.Positions[entity];
                _groupLeaderPositions[social.GroupId] = (ldrPos.X, ldrPos.Y);

                if (predator.Phase == PackPhase.Converging)
                    _groupConverging[social.GroupId] = true;
            }

            if (!predator.HasTarget || !em.IsAlive(predator.TargetEntity))
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

            // Passive stealth accumulation for ambush predators (builds while idle/wandering)
            if (speciesDef.IsAmbushPredator && !predator.HasTarget && predator.PounceTimer <= 0)
            {
                float currentSpd = 0f;
                if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    ref var v = ref em.Velocities[entity];
                    currentSpd = MathF.Sqrt(v.Dx * v.Dx + v.Dy * v.Dy);
                }
                float stealthLimit = speciesDef.BaseHuntSpeed * speciesDef.AmbushSpeedThreshold;
                if (currentSpd <= stealthLimit)
                {
                    float gain = speciesDef.AmbushStealthGain;
                    if (speciesDef.WaterStealthBonus > 0f && _worldManager != null)
                    {
                        var tile = _worldManager.GetTile(pos.X, pos.Y);
                        if (tile.IsWater())
                            gain += speciesDef.WaterStealthBonus;
                    }
                    predator.Stealth = MathF.Min(1f, predator.Stealth + gain);
                }
                else
                {
                    predator.Stealth = MathF.Max(0f, predator.Stealth - speciesDef.AmbushStealthDecay);
                }
            }

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

            // Only stop hunting when nearly full (95%+)
            // Between HuntThreshold and 95%, predators hunt opportunistically
            // This encourages predation when prey is abundant, keeping herbivores in check
            const float fullThreshold = 0.95f;
            if (hungerRatio >= fullThreshold)
            {
                predator.TargetEntity = -1;
                predator.Phase = PackPhase.Idle;
                predator.Role = PackRole.None;
                continue;
            }

            // Urgency scales with hunger:
            // - Below HuntThreshold: full urgency (desperate hunting, faster, wider range)
            // - Above HuntThreshold: low opportunistic urgency (well-fed, still takes easy kills)
            float urgency;
            if (hungerRatio < speciesDef.HuntThreshold)
            {
                urgency = 1f - (hungerRatio / speciesDef.HuntThreshold);
            }
            else
            {
                float wellFedRange = fullThreshold - speciesDef.HuntThreshold;
                urgency = 0.15f * (1f - (hungerRatio - speciesDef.HuntThreshold) / wellFedRange);
            }

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

                // Propagate convergence: if leader triggered all-in, everyone follows
                if (isPack && _groupConverging.ContainsKey(groupId))
                {
                    predator.Phase = PackPhase.Converging;
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
                        predator.PhaseTimer = speciesDef.ConvergenceTimeout;
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

                // === AMBUSH STEALTH UPDATE ===
                bool isAmbush = speciesDef.IsAmbushPredator;
                if (isAmbush)
                {
                    // Tick down pounce timer
                    if (predator.PounceTimer > 0)
                        predator.PounceTimer--;

                    // Update stealth based on movement speed
                    if (predator.PounceTimer <= 0) // No stealth gain during pounce
                    {
                        float currentSpeed = MathF.Sqrt(
                            em.HasComponents(entity, ComponentFlags.Velocity)
                                ? em.Velocities[entity].Dx * em.Velocities[entity].Dx +
                                  em.Velocities[entity].Dy * em.Velocities[entity].Dy
                                : 0f);
                        float stealthSpeedLimit = speciesDef.BaseHuntSpeed * speciesDef.AmbushSpeedThreshold;

                        if (currentSpeed <= stealthSpeedLimit)
                        {
                            // Gain stealth when slow/still
                            float gain = speciesDef.AmbushStealthGain;

                            // Water tile bonus for semi-aquatic ambushers
                            if (speciesDef.WaterStealthBonus > 0f && _worldManager != null)
                            {
                                var currentTile = _worldManager.GetTile(pos.X, pos.Y);
                                if (currentTile.IsWater())
                                    gain += speciesDef.WaterStealthBonus;
                            }

                            predator.Stealth = MathF.Min(1f, predator.Stealth + gain);
                        }
                        else
                        {
                            // Lose stealth when moving fast
                            predator.Stealth = MathF.Max(0f, predator.Stealth - speciesDef.AmbushStealthDecay);
                        }
                    }
                }

                // Attack if in range
                float attackRangeSq = predator.AttackRange * predator.AttackRange;
                // Pounce attack multiplier: amplified damage during pounce burst
                float attackMult = (isAmbush && predator.PounceTimer > 0) ? speciesDef.PounceAttackMult : 1f;

                if (distSq < attackRangeSq && predator.CurrentCooldown == 0)
                {
                    if (em.HasComponents(predator.TargetEntity, ComponentFlags.Energy))
                    {
                        ref var preyEnergy = ref em.Energies[predator.TargetEntity];
                        preyEnergy.Current -= predator.AttackPower * attackMult;
                        preyEnergy.RegenCooldown = 60; // 3s combat cooldown at 20 TPS

                        // Apply venom DOT if this predator has it
                        if (speciesDef.HasVenom && !em.HasComponents(predator.TargetEntity, ComponentFlags.VenomEffect))
                        {
                            em.VenomEffects[predator.TargetEntity] = new VenomEffect(
                                speciesDef.VenomDamagePerTick, speciesDef.VenomDurationTicks);
                            em.AddComponent(predator.TargetEntity, ComponentFlags.VenomEffect);
                        }

                        if (preyEnergy.IsDead)
                        {
                            _entitiesToKill.Add(predator.TargetEntity);

                            // Log the kill
                            if (em.HasComponents(entity, ComponentFlags.Species) &&
                                em.HasComponents(predator.TargetEntity, ComponentFlags.Species))
                            {
                                ref var predSp = ref em.Species[entity];
                                ref var preySp = ref em.Species[predator.TargetEntity];
                                ref var preyPos = ref em.Positions[predator.TargetEntity];
                                EcosystemLogger.Instance?.LogKill(
                                    predSp.SpeciesId, preySp.SpeciesId,
                                    entity, predator.TargetEntity,
                                    preyPos.X, preyPos.Y);
                            }

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

                    // After attacking, only disruptors retreat (to continue harassment cycle)
                    // Swarm hunters never retreat — they just keep biting
                    if (isPack && !speciesDef.SwarmHunter
                        && predator.Role == PackRole.Disruptor
                        && predator.Phase == PackPhase.Disrupting)
                    {
                        bool preyIsolated = IsPreyIsolated(predator.TargetEntity, em);
                        if (!preyIsolated)
                        {
                            predator.Phase = PackPhase.Retreating;
                            predator.PhaseTimer = speciesDef.RetreatDuration;
                        }
                    }
                }
                else if (em.HasComponents(entity, ComponentFlags.Velocity))
                {
                    ref var vel = ref em.Velocities[entity];

                    float huntSpeed = speciesDef.BaseHuntSpeed * speedMultiplier;

                    // === AMBUSH HUNTING MOVEMENT ===
                    if (isAmbush && !isPack)
                    {
                        float pounceRangeSq = speciesDef.PounceRange * speciesDef.PounceRange;

                        if (predator.PounceTimer > 0)
                        {
                            // POUNCING: explosive burst toward prey
                            var dir = MathUtils.Normalize(dx, dy);
                            float pounceSpeed = speciesDef.BaseHuntSpeed * speciesDef.PounceSpeedMult * speedMultiplier;
                            // During pounce, snap hard toward target (high agility override)
                            BlendVelocity(ref vel, dir.X * pounceSpeed, dir.Y * pounceSpeed, 0.8f);
                        }
                        else if (predator.Stealth >= speciesDef.PounceStealthThreshold && distSq <= pounceRangeSq)
                        {
                            // TRIGGER POUNCE: within range and stealthed enough
                            predator.PounceTimer = speciesDef.PounceDuration;
                            predator.Stealth = 0f; // Stealth breaks on pounce

                            var dir = MathUtils.Normalize(dx, dy);
                            float pounceSpeed = speciesDef.BaseHuntSpeed * speciesDef.PounceSpeedMult * speedMultiplier;
                            BlendVelocity(ref vel, dir.X * pounceSpeed, dir.Y * pounceSpeed, 0.8f);
                        }
                        else if (predator.Stealth > 0.1f)
                        {
                            // STALKING: approach slowly to maintain/build stealth
                            var dir = MathUtils.Normalize(dx, dy);
                            float stalkSpeed = speciesDef.BaseHuntSpeed * speciesDef.AmbushSpeedThreshold * 0.9f;
                            BlendVelocity(ref vel, dir.X * stalkSpeed, dir.Y * stalkSpeed, huntAgility * 0.5f);
                        }
                        else
                        {
                            // NO STEALTH: chase openly (post-pounce or stealth broke)
                            var dir = MathUtils.Normalize(dx, dy);
                            BlendVelocity(ref vel, dir.X * huntSpeed, dir.Y * huntSpeed, huntAgility);
                        }
                    }
                    // Pack tactics based on role and phase
                    else if (isPack && predator.Role != PackRole.None)
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
                    // Ambush predators skip water avoidance when stalking or pouncing
                    if (!speciesDef.SemiAquatic && !(isAmbush && (predator.Stealth > 0.1f || predator.PounceTimer > 0)))
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
        // Find other pack members and count existing roles
        ref var pos = ref em.Positions[entity];
        _spatialHash.QueryRadius(pos.X, pos.Y, speciesDef.PackCoordinationRadius, _packMembers);

        int disruptorCount = 0;
        int flankerCount = 0;
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
                flankerCount++;
            else if (otherPredator.Role == PackRole.Disruptor)
                disruptorCount++;
        }

        // Assign role:
        // 1 leader, 1 disruptor first, then fill flankers, then 2nd disruptor if pack is large
        if (!hasLeader && social.LeadershipScore > 0.5f)
        {
            predator.Role = PackRole.Leader;
        }
        else if (disruptorCount < 1)
        {
            predator.Role = PackRole.Disruptor;  // Always need at least 1 disruptor
        }
        else if (flankerCount < 2)
        {
            predator.Role = PackRole.Flanker;    // Fill flanker positions
        }
        else if (disruptorCount < 2)
        {
            predator.Role = PackRole.Disruptor;  // 2nd disruptor in larger packs
        }
        else
        {
            predator.Role = PackRole.Flanker;    // Extra members flank
        }

        // Set initial phase and timer based on role
        predator.Phase = PackPhase.Positioning;
        predator.PhaseTimer = predator.Role switch
        {
            PackRole.Leader => speciesDef.ConvergenceTimeout,
            PackRole.Disruptor => speciesDef.PositioningDuration,
            _ => 0  // Flankers don't use timer
        };
    }

    private void ApplyPackTactics(int entity, ref Position pos, ref Velocity vel, ref Predator predator,
                                   float targetX, float targetY, float dist, float huntSpeed, EntityManager em,
                                   bool preyIsolated, SpeciesDefinition speciesDef, float agility)
    {
        float dx = targetX - pos.X;
        float dy = targetY - pos.Y;

        float roleSpeedMult = predator.Role switch
        {
            PackRole.Leader => speciesDef.LeaderSpeedMult,
            PackRole.Flanker => speciesDef.FlankerSpeedMult,
            PackRole.Disruptor => speciesDef.ChaserSpeedMult,
            _ => 1.0f
        };
        float effectiveSpeed = huntSpeed * roleSpeedMult;

        // === CONVERGENCE: all-in kill rush ===
        // Triggered by leader, prey isolation, or phase propagation from first pass
        if (predator.Phase == PackPhase.Converging || preyIsolated)
        {
            predator.Phase = PackPhase.Converging;
            var chaseDir = MathUtils.Normalize(dx, dy);
            BlendVelocity(ref vel, chaseDir.X * effectiveSpeed * 1.3f, chaseDir.Y * effectiveSpeed * 1.3f, agility);
            return;
        }

        // === Per-role behavior ===
        switch (predator.Role)
        {
            case PackRole.Leader:
                ApplyLeaderBehavior(entity, ref pos, ref vel, ref predator, targetX, targetY, dx, dy, dist, effectiveSpeed, em, speciesDef, agility);
                break;

            case PackRole.Flanker:
                ApplyFlankerBehavior(entity, ref pos, ref vel, targetX, targetY, effectiveSpeed, em, agility);
                break;

            case PackRole.Disruptor:
                ApplyDisruptorBehavior(ref pos, ref vel, ref predator, dx, dy, dist, effectiveSpeed, speciesDef, agility);
                break;

            default:
                var dir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, dir.X * effectiveSpeed, dir.Y * effectiveSpeed, agility);
                break;
        }
    }

    /// <summary>
    /// Leader holds at observation distance while monitoring flanker positions.
    /// Triggers convergence when flankers are in position or timeout expires.
    /// </summary>
    private void ApplyLeaderBehavior(int entity, ref Position pos, ref Velocity vel, ref Predator predator,
                                      float targetX, float targetY, float dx, float dy, float dist,
                                      float speed, EntityManager em, SpeciesDefinition speciesDef, float agility)
    {
        float holdDist = 6f;

        if (dist > holdDist)
        {
            // Approach to observation distance
            var dir = MathUtils.Normalize(dx, dy);
            BlendVelocity(ref vel, dir.X * speed * 0.7f, dir.Y * speed * 0.7f, agility);
        }
        else
        {
            // Hold position, circle slowly to maintain pressure
            float normDist = MathF.Sqrt(dx * dx + dy * dy);
            if (normDist > 0.01f)
                BlendVelocity(ref vel, -dy / normDist * 0.02f, dx / normDist * 0.02f, agility);
        }

        // Decrement convergence timer
        predator.PhaseTimer--;

        // Check convergence triggers:
        // 1. Timer expired — enough disruption, commit to kill
        // 2. Flanker reached opposite side of prey — escape cut off
        if (predator.PhaseTimer <= 0)
        {
            predator.Phase = PackPhase.Converging;
        }
        else
        {
            int groupId = em.HasComponents(entity, ComponentFlags.Social) ? em.Socials[entity].GroupId : -1;
            if (groupId >= 0 && CheckFlankersInPosition(entity, em, targetX, targetY, pos.X, pos.Y, groupId))
            {
                predator.Phase = PackPhase.Converging;
            }
        }
    }

    /// <summary>
    /// Flankers circle around to the OPPOSITE side of prey from the leader,
    /// cutting off escape routes. Two flankers spread to form a V behind the prey.
    /// </summary>
    private void ApplyFlankerBehavior(int entity, ref Position pos, ref Velocity vel,
                                       float preyX, float preyY, float speed,
                                       EntityManager em, float agility)
    {
        // Find leader position for this pack
        int groupId = em.HasComponents(entity, ComponentFlags.Social) ? em.Socials[entity].GroupId : -1;

        if (groupId < 0 || !_groupLeaderPositions.TryGetValue(groupId, out var leaderPos))
        {
            // No leader info — direct approach as fallback
            float fdx = preyX - pos.X;
            float fdy = preyY - pos.Y;
            var dir = MathUtils.Normalize(fdx, fdy);
            BlendVelocity(ref vel, dir.X * speed, dir.Y * speed, agility);
            return;
        }

        // Direction from leader to prey (the "front" of the attack)
        float frontDx = preyX - leaderPos.x;
        float frontDy = preyY - leaderPos.y;
        float frontLen = MathF.Sqrt(frontDx * frontDx + frontDy * frontDy);
        if (frontLen < 0.01f)
        {
            BlendVelocity(ref vel, 0, 0, agility);
            return;
        }
        frontDx /= frontLen;
        frontDy /= frontLen;

        // Target position: BEHIND the prey (past it from leader's perspective)
        float behindDist = 4f;
        float idealX = preyX + frontDx * behindDist;
        float idealY = preyY + frontDy * behindDist;

        // Spread flankers to opposite sides using perpendicular offset
        float perpX = -frontDy;
        float perpY = frontDx;

        // Determine which side based on current position relative to attack axis
        float relX = pos.X - preyX;
        float relY = pos.Y - preyY;
        float cross = relX * perpY - relY * perpX;
        float sideSign = cross >= 0 ? 1f : -1f;

        float spreadDist = 3f;
        idealX += perpX * sideSign * spreadDist;
        idealY += perpY * sideSign * spreadDist;

        // Move toward ideal flank position
        float toIdealDx = idealX - pos.X;
        float toIdealDy = idealY - pos.Y;
        var flankerDir = MathUtils.Normalize(toIdealDx, toIdealDy);
        BlendVelocity(ref vel, flankerDir.X * speed, flankerDir.Y * speed, agility);
    }

    /// <summary>
    /// Disruptors cycle between rushing toward prey and retreating.
    /// Their job is to scatter the herd so flankers can cut off isolated prey.
    /// Limited to 1-2 per pack.
    /// </summary>
    private void ApplyDisruptorBehavior(ref Position pos, ref Velocity vel, ref Predator predator,
                                         float dx, float dy, float dist, float speed,
                                         SpeciesDefinition speciesDef, float agility)
    {
        switch (predator.Phase)
        {
            case PackPhase.Positioning:
                // Initial approach before first rush
                if (dist > 5f)
                {
                    var dir = MathUtils.Normalize(dx, dy);
                    BlendVelocity(ref vel, dir.X * speed * 0.8f, dir.Y * speed * 0.8f, agility);
                }
                predator.PhaseTimer--;
                if (predator.PhaseTimer <= 0)
                {
                    predator.Phase = PackPhase.Disrupting;
                    predator.PhaseTimer = speciesDef.RushDuration;
                }
                break;

            case PackPhase.Disrupting:
                // Rush toward prey to scatter herd
                var rushDir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, rushDir.X * speed * 1.3f, rushDir.Y * speed * 1.3f, agility);
                predator.PhaseTimer--;
                if (predator.PhaseTimer <= 0)
                {
                    predator.Phase = PackPhase.Retreating;
                    predator.PhaseTimer = speciesDef.RetreatDuration;
                }
                break;

            case PackPhase.Retreating:
                // Back off to let herd scatter
                if (dist < 5f)
                {
                    var retreatDir = MathUtils.Normalize(-dx, -dy);
                    BlendVelocity(ref vel, retreatDir.X * speed * 0.8f, retreatDir.Y * speed * 0.8f, agility);
                }
                predator.PhaseTimer--;
                if (predator.PhaseTimer <= 0)
                {
                    // Next rush cycle
                    predator.Phase = PackPhase.Disrupting;
                    predator.PhaseTimer = speciesDef.RushDuration;
                }
                break;

            default:
                // Fallback: approach
                var defDir = MathUtils.Normalize(dx, dy);
                BlendVelocity(ref vel, defDir.X * speed, defDir.Y * speed, agility);
                break;
        }
    }

    /// <summary>
    /// Check if any flanker has reached the opposite side of the prey from the leader.
    /// Uses dot product: negative means the flanker is behind the prey relative to the leader.
    /// </summary>
    private bool CheckFlankersInPosition(int leaderEntity, EntityManager em,
                                          float preyX, float preyY, float leaderX, float leaderY, int groupId)
    {
        // Direction from prey toward leader
        float pToLdx = leaderX - preyX;
        float pToLdy = leaderY - preyY;
        float pToLLen = MathF.Sqrt(pToLdx * pToLdx + pToLdy * pToLdy);
        if (pToLLen < 0.01f) return false;
        pToLdx /= pToLLen;
        pToLdy /= pToLLen;

        // Check pack members around the prey
        _packMembers.Clear();
        _spatialHash.QueryRadius(preyX, preyY, 15f, _packMembers);

        foreach (int other in _packMembers)
        {
            if (other == leaderEntity || !em.IsAlive(other)) continue;
            if (!em.HasComponents(other, ComponentFlags.Predator | ComponentFlags.Social)) continue;
            ref var otherSocial = ref em.Socials[other];
            if (otherSocial.GroupId != groupId) continue;
            ref var otherPred = ref em.Predators[other];
            if (otherPred.Role != PackRole.Flanker) continue;

            // Dot product of (prey→leader) and (prey→flanker)
            // Negative = flanker is on opposite side = escape route blocked
            ref var flankerPos = ref em.Positions[other];
            float pToFdx = flankerPos.X - preyX;
            float pToFdy = flankerPos.Y - preyY;
            float dot = pToLdx * pToFdx + pToLdy * pToFdy;
            if (dot < 0)
                return true;
        }

        return false;
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
