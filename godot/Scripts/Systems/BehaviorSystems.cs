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
/// </summary>
public sealed class TerrainDiscomfortSystem : ISystem
{
    private readonly WorldManager _worldManager;

    public TerrainDiscomfortSystem(WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.TerrainDiscomfort;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip discomfort for distant entities (Reduced+)
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Reduced)
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var discomfort = ref em.TerrainDiscomforts[entity];

            // Get current tile
            var tile = _worldManager.GetTile(pos.X, pos.Y);
            float tileDiscomfort = tile.GetDiscomfortRate();

            // Add grazing pressure for hungry herbivores on non-grazeable terrain
            if (discomfort.GrazingPressure > 0 && !tile.IsGrazeable())
            {
                // Check hunger level
                if (em.HasComponents(entity, ComponentFlags.Hunger))
                {
                    ref var hunger = ref em.Hungers[entity];
                    // Grazing pressure scales with hunger (hungrier = more pressure)
                    float hungerFactor = 1f - (hunger.Current / hunger.Max);
                    tileDiscomfort += discomfort.GrazingPressure * hungerFactor;
                }
            }

            // Accumulate or decay discomfort
            if (tileDiscomfort > 0)
            {
                // Accumulate discomfort
                discomfort.Current += tileDiscomfort;
            }
            else
            {
                // Decay discomfort on comfortable terrain
                discomfort.Current = MathF.Max(0, discomfort.Current - discomfort.DecayRate);
            }
        }
    }
}

/// <summary>
/// Processes wandering behavior for entities.
/// LOD-aware: only processes entities due for update.
/// Includes terrain avoidance and discomfort-driven escape behavior.
/// </summary>
public sealed class WanderSystem : ISystem
{
    private readonly Random _rng = new();
    private readonly WorldManager? _worldManager;
    private readonly SpatialHash? _spatialHash;
    private readonly float _lookAheadDistance;
    private readonly float _roamArrivalDist;
    private readonly List<int> _nearbyBuffer = new(32);

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
            // Check LOD - skip if not due for update
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (!LODSystem.ShouldUpdate(in lod))
                    continue;
            }

            ref var wander = ref em.Wanders[entity];
            ref var vel = ref em.Velocities[entity];
            ref var pos = ref em.Positions[entity];

            // Get species definition for roaming parameters
            SpeciesDefinition? wanderSpeciesDef = null;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                ref var sp = ref em.Species[entity];
                wanderSpeciesDef = SpeciesRegistry.GetById(sp.SpeciesId);
            }
            float roamDistance = wanderSpeciesDef?.RoamDistance ?? 60f;
            int roamCooldownBase = wanderSpeciesDef?.RoamCooldown ?? 500;

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
            if (wander.RoamCooldown > 0)
                wander.RoamCooldown--;

            // Determine hunger urgency for roaming behavior
            float hungerUrgency = 0f;
            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var hunger = ref em.Hungers[entity];
                float ratio = hunger.Current / hunger.Max;
                hungerUrgency = Math.Clamp(1f - ratio, 0f, 1f);  // 0 = full, 1 = starving
            }

            // Check if we should start roaming
            if (!wander.IsRoaming && wander.RoamCooldown <= 0)
            {
                bool shouldRoam = ShouldStartRoaming(entity, em, ref pos);
                if (shouldRoam)
                {
                    // Pick a distant waypoint
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = roamDistance * (0.5f + (float)_rng.NextDouble() * 0.5f);
                    float targetX = pos.X + MathF.Cos(angle) * dist;
                    float targetY = pos.Y + MathF.Sin(angle) * dist;

                    // Clamp to world bounds
                    int worldSize = _worldManager?.WorldSizeTiles ?? 512;
                    targetX = Math.Clamp(targetX, 2f, worldSize - 2f);
                    targetY = Math.Clamp(targetY, 2f, worldSize - 2f);

                    wander.RoamTargetX = targetX;
                    wander.RoamTargetY = targetY;
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
                }
                else
                {
                    float dist = MathF.Sqrt(distSq);
                    // Roam speed scales with hunger: base 1.5x up to 3x when starving
                    float roamMult = wander.RoamSpeedMultiplier + hungerUrgency * 1.5f;
                    float roamSpeed = wander.Speed * roamMult;

                    // Blend roam direction with terrain avoidance
                    var roamDir = new Vector2(dx / dist, dy / dist);
                    if (_worldManager != null)
                    {
                        var avoidance = GetTerrainAvoidance(pos.X, pos.Y, roamDir);
                        if (avoidance.LengthSquared() > 0.01f)
                            roamDir = (roamDir + avoidance * 2f).Normalized();
                    }

                    wander.CurrentDirection = roamDir;
                    vel.Dx = roamDir.X * roamSpeed;
                    vel.Dy = roamDir.Y * roamSpeed;
                    continue;  // Skip normal wander while roaming
                }
            }

            // === Normal wander logic ===

            // Check if entity has high discomfort - prioritize escaping
            bool needsEscape = false;
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];
                if (discomfort.Ratio > 0.5f)  // More than half way to threshold
                {
                    needsEscape = true;
                    // Find direction to escape (toward lowest avoidance terrain)
                    var escapeDir = FindEscapeDirection(pos.X, pos.Y);
                    if (escapeDir.LengthSquared() > 0.01f)
                    {
                        // Blend escape direction strongly with current direction
                        float escapeUrgency = discomfort.Ratio;  // 0.5 to 1+
                        wander.CurrentDirection = (wander.CurrentDirection * (1 - escapeUrgency) +
                                                   escapeDir * escapeUrgency).Normalized();
                    }
                }
            }

            // Terrain avoidance (proactive - avoid entering bad terrain)
            if (_worldManager != null && !needsEscape)
            {
                var avoidance = GetTerrainAvoidance(pos.X, pos.Y, wander.CurrentDirection);
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
                    float aheadX = pos.X + newDir.X * _lookAheadDistance;
                    float aheadY = pos.Y + newDir.Y * _lookAheadDistance;
                    var aheadTile = _worldManager.GetTile(aheadX, aheadY);

                    // If new direction leads to bad terrain, try to pick a safer one
                    if (aheadTile.GetAvoidanceWeight() > 0.3f)
                    {
                        float bestAvoidance = aheadTile.GetAvoidanceWeight();
                        var bestDir = newDir;

                        for (int i = 0; i < 4; i++)
                        {
                            var testDir = MathUtils.RandomDirection(_rng);
                            float testX = pos.X + testDir.X * _lookAheadDistance;
                            float testY = pos.Y + testDir.Y * _lookAheadDistance;
                            float avoidWeight = _worldManager.GetTile(testX, testY).GetAvoidanceWeight();

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

            vel.Dx = wander.CurrentDirection.X * wander.Speed;
            vel.Dy = wander.CurrentDirection.Y * wander.Speed;
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

        // Predators: roam when hungry and no prey nearby
        if (em.HasComponents(entity, ComponentFlags.Predator | ComponentFlags.Hunger))
        {
            ref var hunger = ref em.Hungers[entity];
            float hungerRatio = hunger.Current / hunger.Max;

            // Only consider roaming when getting hungry (below 70%)
            if (hungerRatio >= 0.7f) return false;

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

        // Herbivores/Terraformers: roam when food competition is too high
        // (too many same-diet creatures competing for the same food tiles)
        if (em.HasComponents(entity, ComponentFlags.Species | ComponentFlags.Hunger))
        {
            ref var hunger = ref em.Hungers[entity];
            float hungerRatio = hunger.Current / hunger.Max;

            // Only consider roaming when hunger is dropping (below 50%)
            if (hungerRatio >= 0.5f) return false;

            float checkRadius = 12f;
            _nearbyBuffer.Clear();
            _spatialHash.QueryRadius(pos.X, pos.Y, checkRadius, _nearbyBuffer);

            ref var mySpecies = ref em.Species[entity];
            var myDef = SpeciesRegistry.GetById(mySpecies.SpeciesId);

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

            // High competition + low hunger = migrate to find better grazing
            float competitionPressure = competitorCount / Math.Max(1f,
                em.HasComponents(entity, ComponentFlags.Social)
                    ? em.Socials[entity].PreferredGroupSize * 2f
                    : 6f);

            if (competitionPressure > 1.0f)
            {
                float roamChance = hungerRatio < 0.3f ? 0.02f : 0.008f;
                return _rng.NextDouble() < roamChance;
            }
        }

        return false;
    }

    /// <summary>
    /// Find the best direction to escape uncomfortable terrain.
    /// </summary>
    private Vector2 FindEscapeDirection(float x, float y)
    {
        if (_worldManager == null)
            return Vector2.Zero;

        float bestAvoidance = float.MaxValue;
        Vector2 bestDir = Vector2.Zero;

        // Sample 8 directions
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            float testX = x + dx * _lookAheadDistance;
            float testY = y + dy * _lookAheadDistance;
            float avoidance = _worldManager.GetTile(testX, testY).GetAvoidanceWeight();

            if (avoidance < bestAvoidance)
            {
                bestAvoidance = avoidance;
                bestDir = new Vector2(dx, dy);
            }
        }

        return bestDir;
    }

    /// <summary>
    /// Calculate avoidance vector based on nearby terrain.
    /// </summary>
    private Vector2 GetTerrainAvoidance(float x, float y, Vector2 currentDir)
    {
        if (_worldManager == null)
            return Vector2.Zero;

        float avoidX = 0f;
        float avoidY = 0f;

        // Sample tiles in the direction of movement
        float aheadX = x + currentDir.X * _lookAheadDistance;
        float aheadY = y + currentDir.Y * _lookAheadDistance;

        var aheadTile = _worldManager.GetTile(aheadX, aheadY);
        float aheadAvoidance = aheadTile.GetAvoidanceWeight();

        if (aheadAvoidance > 0.2f)
        {
            // Push back from bad terrain
            avoidX -= currentDir.X * aheadAvoidance;
            avoidY -= currentDir.Y * aheadAvoidance;
        }

        // Also check perpendicular directions for a better path
        float perpX = -currentDir.Y;
        float perpY = currentDir.X;

        float leftX = x + perpX * _lookAheadDistance;
        float leftY = y + perpY * _lookAheadDistance;
        float rightX = x - perpX * _lookAheadDistance;
        float rightY = y - perpY * _lookAheadDistance;

        float leftAvoidance = _worldManager.GetTile(leftX, leftY).GetAvoidanceWeight();
        float rightAvoidance = _worldManager.GetTile(rightX, rightY).GetAvoidanceWeight();

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
}

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
                    vel.Dx = trackDir.X * trackSpeed;
                    vel.Dy = trackDir.Y * trackSpeed;
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
                            vel.Dx = dir.X * swarmSpeed;
                            vel.Dy = dir.Y * swarmSpeed;
                        }
                        else
                        {
                            // Check if prey is isolated from herd
                            bool preyIsolated = IsPreyIsolated(predator.TargetEntity, em);
                            ApplyPackTactics(entity, ref pos, ref vel, ref predator, preyPos.X, preyPos.Y, dist, huntSpeed, em, preyIsolated, speciesDef);
                        }
                    }
                    else
                    {
                        // Solo hunting - direct chase
                        var dir = MathUtils.Normalize(dx, dy);
                        vel.Dx = dir.X * huntSpeed;
                        vel.Dy = dir.Y * huntSpeed;
                    }
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
                                   bool preyIsolated, SpeciesDefinition speciesDef)
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
            vel.Dx = chaseDir.X * effectiveSpeed * 1.2f;  // Slightly faster in chase mode
            vel.Dy = chaseDir.Y * effectiveSpeed * 1.2f;

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
                ApplyPositioningMovement(ref pos, ref vel, predator.Role, dx, dy, dist, effectiveSpeed * 0.7f);
                break;

            case PackPhase.Rushing:
                // All rush in together to scatter the herd
                var rushDir = MathUtils.Normalize(dx, dy);
                vel.Dx = rushDir.X * effectiveSpeed * 1.3f;
                vel.Dy = rushDir.Y * effectiveSpeed * 1.3f;
                break;

            case PackPhase.Retreating:
                // Back off after harassment - this gives herd time to scatter
                float retreatDist = 5f;
                if (dist < retreatDist)
                {
                    var retreatDir = MathUtils.Normalize(-dx, -dy);
                    vel.Dx = retreatDir.X * effectiveSpeed * 0.8f;
                    vel.Dy = retreatDir.Y * effectiveSpeed * 0.8f;
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
                vel.Dx = dir.X * effectiveSpeed;
                vel.Dy = dir.Y * effectiveSpeed;
                break;
        }
    }

    private void ApplyPositioningMovement(ref Position pos, ref Velocity vel, PackRole role,
                                          float dx, float dy, float dist, float speed)
    {
        float targetDist = 5f;  // Ideal distance from prey during positioning

        switch (role)
        {
            case PackRole.Leader:
                // Leader approaches from front, maintains distance
                if (dist > targetDist)
                {
                    var dir = MathUtils.Normalize(dx, dy);
                    vel.Dx = dir.X * speed;
                    vel.Dy = dir.Y * speed;
                }
                else
                {
                    // Hold position, circle slowly
                    vel.Dx = -dy * 0.01f;  // Perpendicular movement
                    vel.Dy = dx * 0.01f;
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
                vel.Dx = flankDir.X * speed;
                vel.Dy = flankDir.Y * speed;
                break;

            case PackRole.Chaser:
                // Chasers follow behind, ready to cut off escape
                if (dist > targetDist * 1.5f)
                {
                    var dir = MathUtils.Normalize(dx, dy);
                    vel.Dx = dir.X * speed * 0.9f;
                    vel.Dy = dir.Y * speed * 0.9f;
                }
                else
                {
                    // Stay back a bit
                    var dir = MathUtils.Normalize(-dx, -dy);
                    vel.Dx = dir.X * speed * 0.3f;
                    vel.Dy = dir.Y * speed * 0.3f;
                }
                break;

            default:
                var defaultDir = MathUtils.Normalize(dx, dy);
                vel.Dx = defaultDir.X * speed;
                vel.Dy = defaultDir.Y * speed;
                break;
        }
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
            // Resize arrays if needed
            if (_predatorCount >= _predatorXs.Length)
            {
                int newSize = _predatorXs.Length * 2;
                Array.Resize(ref _predatorXs, newSize);
                Array.Resize(ref _predatorYs, newSize);
                Array.Resize(ref _predatorDistSq, newSize);
                Array.Resize(ref _predatorSpeciesIds, newSize);
            }

            ref var pos = ref em.Positions[entity];
            _predatorXs[_predatorCount] = pos.X;
            _predatorYs[_predatorCount] = pos.Y;
            _predatorSpeciesIds[_predatorCount] = em.HasComponents(entity, ComponentFlags.Species)
                ? em.Species[entity].SpeciesId : 0;
            _predatorCount++;

            // Update spatial hash for predators
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Process prey
        const ComponentFlags preyRequired = ComponentFlags.Position | ComponentFlags.Prey | ComponentFlags.Velocity | ComponentFlags.Wander;

        var predXSpan = _predatorXs.AsSpan(0, _predatorCount);
        var predYSpan = _predatorYs.AsSpan(0, _predatorCount);
        var predSpeciesSpan = _predatorSpeciesIds.AsSpan(0, _predatorCount);

        foreach (int entity in em.Query(preyRequired))
        {
            // LOD gate: skip fleeing only for very distant entities (Aggregate)
            // Must match hunting gate — prey needs to flee from active predators
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Aggregate)
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var prey = ref em.Preys[entity];
            ref var vel = ref em.Velocities[entity];
            ref var wander = ref em.Wanders[entity];

            // Skip flee processing for entities actively hunting (e.g., Sectids chasing prey)
            // HuntingSystem already set their velocity — don't override with fleeing
            if (em.HasComponents(entity, ComponentFlags.Predator))
            {
                ref var predator = ref em.Predators[entity];
                if (predator.HasTarget)
                {
                    prey.IsFleeing = false;
                    continue;
                }
            }

            float fleeRangeSq = prey.FleeRange * prey.FleeRange;

            // Get this prey's species ID to filter out same-species "threats"
            // (e.g., Sectids shouldn't flee from other Sectids)
            int mySpeciesId = em.HasComponents(entity, ComponentFlags.Species)
                ? em.Species[entity].SpeciesId : 0;

            // Calculate flee direction and find closest threat distance
            var (fleeDir, hasThreat, closestDistSq) = CalculateFleeVectorWithDistance(
                pos.X, pos.Y,
                predXSpan, predYSpan, predSpeciesSpan,
                fleeRangeSq, mySpeciesId);

            // Update fear state if entity has Fear component
            bool hasFear = em.HasComponents(entity, ComponentFlags.Fear);
            FearResponse fearResponse = FearResponse.Flee;
            float fearRatio = 0f;

            if (hasFear)
            {
                ref var fear = ref em.Fears[entity];
                fearResponse = fear.Response;

                if (hasThreat)
                {
                    // Accumulate fear based on threat proximity
                    // Closer = more fear accumulation
                    float proximityFactor = 1f - (closestDistSq / fleeRangeSq);
                    float fearIncrease = fear.AccumulationRate * proximityFactor;
                    fear.Current = MathF.Min(fear.Max, fear.Current + fearIncrease);

                    // Enter vigilant state
                    fear.VigilanceTicks = 100;  // Will stay alert after threat leaves
                }
                else
                {
                    // Decay fear when safe
                    float decayRate = fear.IsVigilant ? fear.VigilanceDecay : fear.DecayRate;
                    fear.Current = MathF.Max(0, fear.Current - decayRate);

                    // Decrement vigilance
                    if (fear.VigilanceTicks > 0)
                        fear.VigilanceTicks--;
                }

                fearRatio = fear.Ratio;
            }

            // Determine behavior based on fear level and response type
            if (hasThreat || (hasFear && fearRatio > 0.5f))  // React to threats or if still scared
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

                // Apply fear response behavior
                switch (fearResponse)
                {
                    case FearResponse.Freeze:
                        ApplyFreezeResponse(ref vel, fearRatio);
                        break;

                    case FearResponse.Panic:
                        ApplyPanicResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio);
                        break;

                    case FearResponse.Defensive:
                        // TODO: Implement defensive grouping behavior
                        // For now, fall through to normal flee
                        ApplyFleeResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio);
                        break;

                    case FearResponse.Flee:
                    default:
                        ApplyFleeResponse(ref pos, ref vel, ref wander, ref prey, fleeDir, fearRatio, discomfortRatio);
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
    /// </summary>
    private (Vector2 dir, bool hasThreat, float closestDistSq) CalculateFleeVectorWithDistance(
        float x, float y,
        ReadOnlySpan<float> predX, ReadOnlySpan<float> predY, ReadOnlySpan<int> predSpecies,
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

            if (distSq < maxDistSq && distSq > 0.001f)
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
                                     ref Prey prey, Vector2 fleeDir, float fearRatio, float discomfortRatio)
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

        // At high panic, ignore terrain completely
        vel.Dx = panicDir.X * panicSpeed;
        vel.Dy = panicDir.Y * panicSpeed;
    }

    /// <summary>
    /// Normal flee response - run away, considering terrain.
    /// </summary>
    private void ApplyFleeResponse(ref Position pos, ref Velocity vel, ref Wander wander,
                                    ref Prey prey, Vector2 fleeDir, float fearRatio, float discomfortRatio)
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

        vel.Dx = fleeDir.X * fleeSpeed;
        vel.Dy = fleeDir.Y * fleeSpeed;
    }
}

/// <summary>
/// Processes hunger decay and starvation damage.
/// </summary>
public sealed class HungerSystem : ISystem
{
    private readonly List<int> _toKill = new(32);

    public void Process(EntityManager em)
    {
        _toKill.Clear();
        const ComponentFlags required = ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            // Skip structures (nests, crystals) — they don't eat
            if (em.HasComponents(entity, ComponentFlags.Nest) ||
                em.HasComponents(entity, ComponentFlags.Crystal))
                continue;

            ref var hunger = ref em.Hungers[entity];

            // Decay hunger
            hunger.Current -= hunger.DecayRate;

            // Starvation damage
            if (hunger.IsStarving && em.HasComponents(entity, ComponentFlags.Energy))
            {
                ref var energy = ref em.Energies[entity];
                energy.Current -= hunger.StarvationDamage;

                if (energy.IsDead)
                    _toKill.Add(entity);
            }

            // Conditional energy regen: only when not starving and out of combat
            if (!hunger.IsStarving && em.HasComponents(entity, ComponentFlags.Energy | ComponentFlags.Species))
            {
                ref var energy = ref em.Energies[entity];
                if (energy.RegenCooldown > 0)
                {
                    energy.RegenCooldown--;
                }
                else if (energy.Current < energy.Max)
                {
                    ref var species = ref em.Species[entity];
                    var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                    energy.Current = MathF.Min(energy.Max, energy.Current + speciesDef.EnergyRegenRate);
                }
            }
        }

        foreach (int entity in _toKill)
        {
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
            // Skip structures (nests, crystals don't age)
            if (em.HasComponents(entity, ComponentFlags.Nest) ||
                em.HasComponents(entity, ComponentFlags.Crystal))
                continue;

            ref var age = ref em.Ages[entity];
            age.Current++;

            // Natural death from old age
            if (age.Current >= age.MaxLifespan)
                _toKill.Add(entity);
        }

        foreach (int entity in _toKill)
        {
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

    public GrazingSystem(World.WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species | ComponentFlags.Hunger;

        foreach (int entity in em.Query(required))
        {
            ref var species = ref em.Species[entity];
            ref var pos = ref em.Positions[entity];
            ref var hunger = ref em.Hungers[entity];
            var tile = _worldManager.GetTile(pos.X, pos.Y);

            // Standard herbivore grazing
            if (species.Type == SpeciesType.Herbivore)
            {
                var herbDef = SpeciesRegistry.GetById(species.SpeciesId);
                if (tile.IsGrazeable())
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + herbDef.GrazeNutrition);
                continue;
            }

            // Faction species: feed from their preferred tiles
            if (species.Type == SpeciesType.Shroomer ||
                species.Type == SpeciesType.Sectid ||
                species.Type == SpeciesType.Faeling)
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                if (speciesDef.FeedTiles != null && speciesDef.FeedTiles.Contains(tile))
                {
                    hunger.Current = MathF.Min(hunger.Max, hunger.Current + speciesDef.FeedNutrition);
                }
            }
        }
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

    public TerraformSystem(World.WorldManager worldManager)
    {
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Terraform;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip terraform for distant entities (Statistical+)
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Statistical)
                    continue;
            }

            ref var terraform = ref em.Terraforms[entity];

            // Cooldown
            if (terraform.CurrentCooldown > 0)
            {
                terraform.CurrentCooldown--;
                continue;
            }

            terraform.CurrentCooldown = terraform.Cooldown;

            // Roll against strength probability
            if ((float)_rng.NextDouble() > terraform.Strength)
                continue;

            ref var pos = ref em.Positions[entity];

            // Pick a random tile within influence radius
            float offsetX = ((float)_rng.NextDouble() * 2f - 1f) * terraform.Radius;
            float offsetY = ((float)_rng.NextDouble() * 2f - 1f) * terraform.Radius;
            float targetX = pos.X + offsetX;
            float targetY = pos.Y + offsetY;

            var currentTile = _worldManager.GetTile(targetX, targetY);
            if (!currentTile.IsTerraformable())
                continue;

            // Determine transformation based on direction
            TileType? newTile = terraform.Direction switch
            {
                TerraformDirection.Wetter => currentTile.ShiftWetter(),
                TerraformDirection.Drier => currentTile.ShiftDrier(),
                TerraformDirection.Balanced => currentTile.ShiftBalanced(),
                _ => null
            };

            if (newTile.HasValue)
                _worldManager.SetTile(targetX, targetY, newTile.Value);
        }
    }
}

/// <summary>
/// Applies separation forces to prevent entities from clustering.
/// </summary>
public sealed class SeparationSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly List<int> _nearbyEntities = new(64);

    public SeparationSystem(SpatialHash spatialHash)
    {
        _spatialHash = spatialHash;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity | ComponentFlags.Species;

        // First pass: update spatial hash positions for all creatures
        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);
        }

        // Second pass: apply separation forces
        foreach (int entity in em.Query(required))
        {
            // Check LOD - skip if not due for update
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (!LODSystem.ShouldUpdate(in lod))
                    continue;
            }

            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];
            ref var species = ref em.Species[entity];

            // Get species-specific separation parameters
            var sepSpeciesDef = SpeciesRegistry.GetById(species.SpeciesId);
            float separationRadius = sepSpeciesDef.SeparationRadius;
            float separationStrength = sepSpeciesDef.SeparationStrength;

            // Query nearby entities
            _spatialHash.QueryRadius(pos.X, pos.Y, separationRadius, _nearbyEntities);

            float separationX = 0f;
            float separationY = 0f;
            int neighborCount = 0;

            foreach (int other in _nearbyEntities)
            {
                if (other == entity || !em.IsAlive(other))
                    continue;

                // Only separate from same species
                if (!em.HasComponents(other, ComponentFlags.Species))
                    continue;

                ref var otherSpecies = ref em.Species[other];
                if (otherSpecies.Type != species.Type)
                    continue;

                ref var otherPos = ref em.Positions[other];
                float dx = pos.X - otherPos.X;
                float dy = pos.Y - otherPos.Y;
                float distSq = dx * dx + dy * dy;

                if (distSq > 0.001f && distSq < separationRadius * separationRadius)
                {
                    float dist = MathF.Sqrt(distSq);
                    float factor = 1f - (dist / separationRadius); // Stronger when closer
                    separationX += (dx / dist) * factor;
                    separationY += (dy / dist) * factor;
                    neighborCount++;
                }
            }

            // Apply separation force
            if (neighborCount > 0)
            {
                separationX /= neighborCount;
                separationY /= neighborCount;

                vel.Dx += separationX * separationStrength;
                vel.Dy += separationY * separationStrength;
            }
        }
    }
}

/// <summary>
/// Hard collision resolution to prevent entities from overlapping.
/// Runs after movement to resolve any overlaps.
/// </summary>
public sealed class CollisionSystem : ISystem
{
    private readonly SpatialHash _spatialHash;
    private readonly List<int> _nearbyEntities = new(64);
    private readonly float _collisionRadiusScale;
    private readonly float _tileSize;

    public CollisionSystem(SpatialHash spatialHash, float collisionRadiusScale = 0.5f, float tileSize = 16f)
    {
        _spatialHash = spatialHash;
        _collisionRadiusScale = collisionRadiusScale;
        _tileSize = tileSize; // Convert pixel sizes to tile/world units
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

        // Resolve collisions - multiple passes for better resolution
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (int entity in em.Query(required))
            {
                // LOD gate: skip collision for distant entities (Reduced+)
                if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
                {
                    ref var lod = ref em.SimulationLODs[entity];
                    if (lod.Level >= LODLevel.Reduced)
                        continue;
                }

                ref var pos = ref em.Positions[entity];
                ref var rend = ref em.Renderables[entity];
                // Convert pixel size to tile units
                float radius = (rend.Size * _collisionRadiusScale) / _tileSize;

                // Update spatial hash position
                _spatialHash.Update(entity, pos.X, pos.Y);

                // Query nearby entities (search radius in tile units)
                _spatialHash.QueryRadius(pos.X, pos.Y, radius * 4f, _nearbyEntities);

                foreach (int other in _nearbyEntities)
                {
                    if (other <= entity || !em.IsAlive(other))
                        continue;

                    if (!em.HasComponents(other, ComponentFlags.Position | ComponentFlags.Renderable))
                        continue;

                    ref var otherPos = ref em.Positions[other];
                    ref var otherRend = ref em.Renderables[other];
                    float otherRadius = (otherRend.Size * _collisionRadiusScale) / _tileSize;

                    float dx = pos.X - otherPos.X;
                    float dy = pos.Y - otherPos.Y;
                    float distSq = dx * dx + dy * dy;
                    float minDist = radius + otherRadius;
                    float minDistSq = minDist * minDist;

                    // Check for overlap
                    if (distSq < minDistSq && distSq > 0.0001f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        float overlap = minDist - dist;

                        // Normalize direction
                        float nx = dx / dist;
                        float ny = dy / dist;

                        // Push both entities apart (half the overlap each)
                        float push = overlap * 0.5f;
                        pos.X += nx * push;
                        pos.Y += ny * push;
                        otherPos.X -= nx * push;
                        otherPos.Y -= ny * push;
                    }
                }
            }
        }
    }
}

/// <summary>
/// Handles reproduction for mature, well-fed entities.
/// </summary>
public sealed class ReproductionSystem : ISystem
{
    private readonly World.WorldManager _worldManager;
    private readonly SpatialHash _spatialHash;
    private readonly int _maxPopulation;
    private readonly Random _rng = new();
    private readonly List<(float x, float y, SpeciesType speciesType, int speciesId)> _toSpawn = new(32);
    private readonly List<int> _nearbyBuffer = new(64);

    public ReproductionSystem(World.WorldManager worldManager, int maxPopulation = 500,
                               SpatialHash? spatialHash = null)
    {
        _worldManager = worldManager;
        _maxPopulation = maxPopulation;
        _spatialHash = spatialHash!;
    }

    public void Process(EntityManager em)
    {
        _toSpawn.Clear();

        // Don't reproduce if at population cap
        if (em.EntityCount >= _maxPopulation)
            return;

        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Species |
                                         ComponentFlags.Hunger | ComponentFlags.Energy |
                                         ComponentFlags.Age | ComponentFlags.Reproduction;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip reproduction only for very distant entities (Aggregate)
            // Aging kills at all distances — reproduction must also run to maintain balance
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (lod.Level >= LODLevel.Aggregate)
                    continue;
            }

            ref var reproduction = ref em.Reproductions[entity];

            // Reduce cooldown
            if (reproduction.CurrentCooldown > 0)
            {
                reproduction.CurrentCooldown--;
                continue;
            }

            ref var age = ref em.Ages[entity];
            if (!age.IsMature)
                continue;

            ref var species = ref em.Species[entity];

            // Skip faction species that reproduce via special systems
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                if (speciesDef.NestBreeder || speciesDef.SporeReproducer || speciesDef.CrystalSpawned)
                    continue;
            }

            ref var hunger = ref em.Hungers[entity];
            ref var energy = ref em.Energies[entity];

            // Check thresholds
            if (hunger.Current < reproduction.HungerThreshold ||
                energy.Current < reproduction.EnergyThreshold)
                continue;

            // Check population cap again
            if (em.EntityCount + _toSpawn.Count >= _maxPopulation)
                break;

            ref var pos = ref em.Positions[entity];

            // Local density suppression — skip if too many same-species nearby
            // Prevents exponential population explosions in well-fed areas
            if (_spatialHash != null)
            {
                var speciesDef = SpeciesRegistry.GetById(species.SpeciesId);
                float densityRadius = speciesDef.SocialRadius > 0 ? speciesDef.SocialRadius * 1.5f : 15f;
                _nearbyBuffer.Clear();
                _spatialHash.QueryRadius(pos.X, pos.Y, densityRadius, _nearbyBuffer);
                int sameSpeciesCount = 0;
                foreach (int other in _nearbyBuffer)
                {
                    if (other == entity || !em.IsAlive(other)) continue;
                    if (!em.HasComponents(other, ComponentFlags.Species)) continue;
                    if (em.Species[other].SpeciesId == species.SpeciesId)
                        sameSpeciesCount++;
                }
                // Suppress reproduction when local density exceeds 2× preferred group size
                float maxLocal = MathF.Max(6f, speciesDef.PreferredGroupSize * 2f);
                if (sameSpeciesCount >= (int)maxLocal)
                    continue;
            }

            // Find spawn position
            float spawnX = pos.X + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;
            float spawnY = pos.Y + ((float)_rng.NextDouble() * 2 - 1) * reproduction.SpawnRadius;

            // Only spawn on walkable tiles
            if (!_worldManager.IsWalkable(spawnX, spawnY))
                continue;

            // Pay reproduction cost
            hunger.Current -= reproduction.HungerCost;
            energy.Current -= reproduction.EnergyCost;
            reproduction.CurrentCooldown = reproduction.Cooldown;

            // Queue offspring for spawning (inherit parent species)
            for (int i = 0; i < reproduction.OffspringCount; i++)
            {
                _toSpawn.Add((spawnX, spawnY, species.Type, species.SpeciesId));
            }
        }

        // Spawn offspring
        foreach (var (x, y, speciesType, speciesId) in _toSpawn)
        {
            SpawnCreature(em, x, y, speciesType, speciesId);
        }
    }

    private void SpawnCreature(EntityManager em, float x, float y, SpeciesType speciesType, int speciesId)
    {
        int entity = em.CreateEntity();

        em.Positions[entity] = new Position(x, y);
        em.AddComponent(entity, ComponentFlags.Position);

        em.Velocities[entity] = new Velocity();
        em.AddComponent(entity, ComponentFlags.Velocity);

        em.ChunkPositions[entity] = new ChunkPosition();
        em.AddComponent(entity, ComponentFlags.ChunkPosition);

        em.SimulationLODs[entity] = new SimulationLOD();
        em.AddComponent(entity, ComponentFlags.SimulationLOD);

        // Use SpeciesRegistry to inherit parent stats where possible
        var speciesDef = SpeciesRegistry.GetById(speciesId);

        em.Species[entity] = new Species(speciesType, 0, speciesId);
        em.AddComponent(entity, ComponentFlags.Species);

        em.Ages[entity] = new Age(
            current: 0,
            maxLifespan: (int)speciesDef.MaxLifespan,
            maturityAge: (int)speciesDef.MaturityAge
        );
        em.AddComponent(entity, ComponentFlags.Age);

        em.Energies[entity] = new Energy(speciesDef.MaxEnergy, speciesDef.MaxEnergy);
        em.AddComponent(entity, ComponentFlags.Energy);

        em.Hungers[entity] = new Hunger(
            current: speciesDef.MaxHunger * 0.6f,
            max: speciesDef.MaxHunger,
            decayRate: speciesDef.HungerDecayRate
        );
        em.AddComponent(entity, ComponentFlags.Hunger);

        em.Reproductions[entity] = new Reproduction(
            hungerThreshold: speciesDef.ReproHungerThreshold,
            energyThreshold: speciesDef.ReproEnergyThreshold,
            hungerCost: speciesDef.ReproHungerCost,
            energyCost: speciesDef.ReproEnergyCost,
            cooldown: (int)speciesDef.ReproCooldown,
            offspringCount: speciesDef.OffspringCount,
            spawnRadius: speciesDef.SpawnRadius
        );
        em.AddComponent(entity, ComponentFlags.Reproduction);

        em.Wanders[entity] = new Wander(
            speed: speciesDef.BaseWanderSpeed,
            changeDirectionChance: speciesDef.DirectionChangeChance
        );
        em.AddComponent(entity, ComponentFlags.Wander);

        em.Renderables[entity] = new Renderable(
            speciesDef.BaseColor, speciesDef.BaseSize, speciesDef.Shape);
        em.AddComponent(entity, ComponentFlags.Renderable);

        // Prey behavior (herbivores and faction species are prey)
        if (speciesDef.IsPrey)
        {
            em.Preys[entity] = new Prey(speciesDef.FleeRange, speciesDef.FleeSpeedMultiplier);
            em.AddComponent(entity, ComponentFlags.Prey);
        }

        // Predator behavior
        if (speciesDef.IsPredator)
        {
            em.Predators[entity] = new Predator(
                speciesDef.HuntRange, speciesDef.AttackRange, speciesDef.AttackPower,
                (int)speciesDef.AttackCooldown);
            em.AddComponent(entity, ComponentFlags.Predator);
        }

        // Terraform for faction species
        if (speciesDef.Diet == DietType.Terraformer)
        {
            em.Terraforms[entity] = new Terraform(
                direction: speciesDef.TerraformDir,
                radius: speciesDef.TerraformRadius,
                strength: speciesDef.TerraformStrength,
                cooldown: speciesDef.TerraformCooldown
            );
            em.AddComponent(entity, ComponentFlags.Terraform);
        }
    }
}

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

        // Pre-pass: Count group sizes, find leaders, update spatial hash
        _groupSizes.Clear();
        _groupLeaders.Clear();

        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            ref var social = ref em.Socials[entity];
            _spatialHash.Update(entity, pos.X, pos.Y);

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
            // Check LOD - skip if not due for update
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD))
            {
                ref var lod = ref em.SimulationLODs[entity];
                if (!LODSystem.ShouldUpdate(in lod))
                    continue;
            }

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

            // Priority 1: High terrain discomfort - need to escape
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];
                if (discomfort.Ratio > 0.6f)
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
