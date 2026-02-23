using System;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Processes movement for all entities with Position and Velocity.
/// Applies terrain speed modifiers and handles wall/corner sliding.
/// </summary>
public sealed class MovementSystem : ISystem
{
    private readonly int _chunkSize;
    private readonly int _worldSizeTiles;
    private readonly WorldManager _worldManager;

    public MovementSystem(int chunkSize, int worldSizeChunks, WorldManager worldManager)
    {
        _chunkSize = chunkSize;
        _worldSizeTiles = chunkSize * worldSizeChunks;
        _worldManager = worldManager;
    }

    public void Process(EntityManager em)
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Velocity;

        foreach (int entity in em.Query(required))
        {
            // LOD gate: skip if not due for update this tick
            if (em.HasComponents(entity, ComponentFlags.SimulationLOD) &&
                em.SimulationLODs[entity].TicksUntilUpdate != 0)
                continue;

            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];

            // Skip if no movement
            if (vel.Dx == 0 && vel.Dy == 0)
                continue;

            // Clamp creature velocity to prevent unbounded accumulation from additive systems
            // Only applies to creatures (entities with Species) — not the player
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                const float maxSpeed = 0.25f;
                float speedSq = vel.Dx * vel.Dx + vel.Dy * vel.Dy;
                if (speedSq > maxSpeed * maxSpeed)
                {
                    float scale = maxSpeed / MathF.Sqrt(speedSq);
                    vel.Dx *= scale;
                    vel.Dy *= scale;
                }
            }

            // Get current tile for speed modifier
            var currentTile = _worldManager.GetTile(pos.X, pos.Y);
            float speedMult = currentTile.GetSpeedMultiplier();

            // Flying creatures ignore terrain speed penalties
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                var speciesDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                if (speciesDef.IsFlying)
                    speedMult = 1f;
            }

            // Calculate movement delta with terrain speed modifier
            float dx = vel.Dx * speedMult;
            float dy = vel.Dy * speedMult;

            // Try to move
            bool moved = TryMove(ref pos, dx, dy);

            // If blocked and moving diagonally, try sliding along walls
            if (!moved && dx != 0 && dy != 0)
            {
                // Try X-axis slide
                if (TryMove(ref pos, dx, 0))
                {
                    moved = true;
                }
                // Try Y-axis slide
                else if (TryMove(ref pos, 0, dy))
                {
                    moved = true;
                }
            }

            // If still blocked (corner case), try nudging perpendicular to escape
            if (!moved)
            {
                // Try small perpendicular movements to escape corners
                float nudge = 0.05f;
                if (dx != 0)
                {
                    // Moving horizontally but blocked - try nudging up/down
                    if (TryMove(ref pos, 0, nudge) || TryMove(ref pos, 0, -nudge))
                    {
                        // Nudged successfully, velocity will move us next frame
                    }
                }
                else if (dy != 0)
                {
                    // Moving vertically but blocked - try nudging left/right
                    if (TryMove(ref pos, nudge, 0) || TryMove(ref pos, -nudge, 0))
                    {
                        // Nudged successfully
                    }
                }
            }

            // Update chunk position if entity has it
            if (em.HasComponents(entity, ComponentFlags.ChunkPosition))
            {
                em.ChunkPositions[entity].Update(in pos, _chunkSize);
            }

            // Dampen creature velocity each frame so stale forces decay naturally.
            // When a behavior system actively sets velocity, it refreshes above this decay.
            // When no system is commanding movement, velocity fades to zero over ~10 frames.
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                const float damping = 0.85f;
                vel.Dx *= damping;
                vel.Dy *= damping;
                // Zero out near-zero velocities to avoid perpetual micro-drift
                if (vel.Dx * vel.Dx + vel.Dy * vel.Dy < 0.0001f)
                {
                    vel.Dx = 0;
                    vel.Dy = 0;
                }
            }
        }
    }

    /// <summary>
    /// Attempts to move an entity by the given delta. Returns true if successful.
    /// </summary>
    private bool TryMove(ref Position pos, float dx, float dy)
    {
        if (dx == 0 && dy == 0)
            return false;

        float newX = pos.X + dx;
        float newY = pos.Y + dy;

        // World bounds clamping
        newX = Math.Clamp(newX, 0f, _worldSizeTiles - 0.01f);
        newY = Math.Clamp(newY, 0f, _worldSizeTiles - 0.01f);

        // Check if destination is walkable
        var destTile = _worldManager.GetTile(newX, newY);
        if (destTile.IsWalkable())
        {
            pos.X = newX;
            pos.Y = newY;
            return true;
        }

        return false;
    }
}
