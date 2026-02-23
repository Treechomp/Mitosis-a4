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
            // LOD gate: skip if not due for update this tick
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD) &&
                em.SimulationLODs[entity].TicksUntilUpdate != 0)
                continue;

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
                    float targetX, targetY;

                    // Faelings: seek damaged terrain (non-balanced tiles) to restore
                    if (wanderSpeciesDef != null
                        && wanderSpeciesDef.TerraformDir == TerraformDirection.Balanced
                        && _worldManager != null
                        && TryFindDamagedTerrainTarget(pos.X, pos.Y, roamDistance, out targetX, out targetY))
                    {
                        // Target already set toward damaged terrain
                    }
                    else
                    {
                        // Default: random direction
                        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                        float dist = roamDistance * (0.5f + (float)_rng.NextDouble() * 0.5f);
                        targetX = pos.X + MathF.Cos(angle) * dist;
                        targetY = pos.Y + MathF.Sin(angle) * dist;
                    }

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
            // Uses hysteresis: enter escape at ratio > 0.5, exit at ratio < 0.2
            bool needsEscape = false;
            if (em.HasComponents(entity, ComponentFlags.TerrainDiscomfort))
            {
                ref var discomfort = ref em.TerrainDiscomforts[entity];

                // Hysteresis: wide gap prevents oscillation at terrain edges.
                // Enter escape late (0.6) so minor discomfort doesn't trigger;
                // exit early (0.1) so entity commits to reaching safe terrain.
                if (!discomfort.IsEscaping && discomfort.Ratio > 0.6f)
                    discomfort.IsEscaping = true;
                else if (discomfort.IsEscaping && discomfort.Ratio < 0.1f)
                    discomfort.IsEscaping = false;

                if (discomfort.IsEscaping)
                {
                    needsEscape = true;
                    // Find direction to escape (toward lowest avoidance terrain)
                    var escapeDir = FindEscapeDirection(pos.X, pos.Y);
                    if (escapeDir.LengthSquared() > 0.01f)
                    {
                        // Blend escape direction strongly with current direction
                        float escapeUrgency = discomfort.Ratio;  // 0.2 to 1+
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
        float bestScore = 0;

        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);

            // Sample 5 points along this direction
            float dirScore = 0;
            for (float t = 0.2f; t <= 1.0f; t += 0.2f)
            {
                float sampleX = x + dx * roamDistance * t;
                float sampleY = y + dy * roamDistance * t;
                dirScore += GetTerrainDamageScore(sampleX, sampleY);
            }

            if (dirScore > bestScore)
            {
                bestScore = dirScore;
                float dist = roamDistance * (0.5f + (float)_rng.NextDouble() * 0.5f);
                targetX = x + dx * dist;
                targetY = y + dy * dist;
            }
        }

        return bestScore > 2f; // Only seek if significant damage found
    }

    /// <summary>
    /// Score a tile by how far it is from balanced (Grass). Higher = more damaged.
    /// </summary>
    private float GetTerrainDamageScore(float x, float y)
    {
        var tile = _worldManager!.GetTile(x, y);
        return tile switch
        {
            TileType.Arid => 4f,
            TileType.Bog => 4f,
            TileType.Wetland => 3f,
            TileType.Sand => 2.5f,
            TileType.Tundra => 2f,
            TileType.Jungle => 2f,
            TileType.Taiga => 1.5f,
            TileType.Dirt => 1.5f,
            TileType.Forest => 1f,
            TileType.Steppe => 1f,
            TileType.Savanna => 1f,
            TileType.Shrubland => 0.5f,
            _ => 0f // Grass, water, mountain = balanced or untargetable
        };
    }
}
