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
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD) &&
                em.SimulationLODs[entity].TicksUntilUpdate != 0)
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

        // Resolve collisions — single pass (position updates synced inline)
        {
            foreach (int entity in em.Query(required))
            {
                // LOD gate: skip if not due for update this tick
                if (em.HasComponents(entity, ComponentFlags.SimulationLOD) &&
                    em.SimulationLODs[entity].TicksUntilUpdate != 0)
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
