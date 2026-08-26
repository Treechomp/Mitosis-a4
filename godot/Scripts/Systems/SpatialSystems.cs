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

        // Apply separation forces (spatial hash already updated by SpatialHashUpdateSystem)
        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (!em.DueThisTick[entity])
                continue;

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

            // Apply separation force.
            // STATE-LIKE — do NOT multiply by EffectiveInterval. The impulse is not a per-tick
            // quantity that accumulates; it feeds a velocity that MovementSystem damps on the very
            // same decision cadence (0.85 per due tick, gated identically). Impulse and decay
            // therefore share a clock, and the steady state v = strength/(1-damping) comes out the
            // same at every tier, while movement integrates that velocity every tick regardless.
            // Multiplying here would not compensate for anything — it would put twenty times the
            // separation force on a distant creature and fire it out of its own herd. What LOD
            // does cost is RESPONSIVENESS: the push builds over ~10 decisions, so at Minimal a
            // crowd takes 200 ticks to loosen rather than 10. That is coarser timing, which is
            // what LOD is allowed to buy, not a different amount of spacing.
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

    /// <summary>
    /// How readily an entity is displaced by a collision: inverse of its effective mass, or 0 for
    /// something rooted in place. Growing creatures use their grown mass, so an elder Shroomer is
    /// as immovable as its bulk suggests rather than as light as a sprout.
    /// </summary>
    private static float PushWeight(EntityManager em, int entity)
    {
        // Structures are built into the ground — nests, crystals and mycelium hearts alike.
        if (em.HasComponents(entity, ComponentFlags.Structure)
            || em.HasComponents(entity, ComponentFlags.Nest)
            || em.HasComponents(entity, ComponentFlags.Crystal))
            return 0f;

        float mass = 1f;
        if (em.HasComponents(entity, ComponentFlags.Species))
        {
            var def = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
            if (def != null) mass = def.BodyMass;
            if (em.HasComponents(entity, ComponentFlags.Growth))
                mass *= em.Growths[entity].CurrentScale;
        }
        return 1f / MathF.Max(0.05f, mass);
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

        // Resolve collisions — single pass (position updates synced inline)
        {
            foreach (int entity in em.Query(required))
            {
                // LOD gate: skip if not due for update this tick
                if (!em.DueThisTick[entity])
                    continue;

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

                    // Check for overlap.
                    // STATE-LIKE — do NOT multiply by EffectiveInterval. This resolves a
                    // condition that exists right now (two bodies overlapping by `overlap`), not a
                    // quantity accrued since the last visit: the correction is complete the moment
                    // it is applied, and scaling it by the tick gap would hurl the pair apart by
                    // twenty times the overlap. LOD costs latency here — a distant pair can stay
                    // interpenetrated for up to a full interval before anyone looks — which is a
                    // rendering artifact at a distance nobody is watching, not a rate.
                    if (distSq < minDistSq && distSq > 0.0001f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        float overlap = minDist - dist;

                        // Normalize direction
                        float nx = dx / dist;
                        float ny = dy / dist;

                        // Split the overlap by INVERSE MASS, so heft decides who gives ground.
                        // A flat half-and-half meant body mass told the simulation only what a
                        // corpse was worth to eat: a fox shunted a turtle as easily as the turtle
                        // shunted the fox, and wolves walked a full-grown Shroomer around while
                        // hunting it. Structures are immovable outright — a nest is built into the
                        // ground and should not be shoved across the map by passing traffic.
                        float wA = PushWeight(em, entity);
                        float wB = PushWeight(em, other);
                        float wTotal = wA + wB;
                        if (wTotal <= 0f)
                            continue;   // two immovables: neither yields

                        pos.X += nx * overlap * (wA / wTotal);
                        pos.Y += ny * overlap * (wA / wTotal);
                        otherPos.X -= nx * overlap * (wB / wTotal);
                        otherPos.Y -= ny * overlap * (wB / wTotal);
                    }
                }
            }
        }
    }
}
