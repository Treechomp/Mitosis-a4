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

    /// <summary>
    /// Maximum elevation difference between adjacent positions that counts as a cliff.
    /// Gradual slopes slow movement via slope resistance; only true cliff-faces hard-block.
    /// </summary>
    private const float CliffThreshold = 0.28f;

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
            if (!em.DueThisTick[entity])
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
            bool isFlying = false;
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                var speciesDef = SpeciesRegistry.GetById(em.Species[entity].SpeciesId);
                if (speciesDef.IsFlying)
                {
                    isFlying = true;
                    speedMult = 1f;
                }
                else if (speciesDef.IsAquatic && !currentTile.IsWater())
                {
                    // Beached: an aquatic creature (e.g. Shark, Fish) flounders on land, barely
                    // able to move while it suffocates. Stops them roaming inland after prey/corpses.
                    speedMult *= 0.05f;
                }
                else if (currentTile.IsWater())
                {
                    // Per-species swimming affinity. Water tiles are intrinsically slow for
                    // everyone (DeepWater 0.25, ShallowWater 0.4), so without this an "apex water
                    // predator" Shark crawls. Applying each species' water TerrainSpeedModifier
                    // here lets strong swimmers (Shark/Crocodile/Fish/Penguin) move fast in water
                    // while poor swimmers (Polar Bear) stay slow. Scoped to water only on purpose:
                    // the land TerrainSpeedModifiers stay inert so the tuned land predator/prey
                    // catch balance is not disturbed.
                    speedMult *= speciesDef.GetTerrainSpeedModifier(currentTile);
                }
            }

            // Slope resistance: uphill movement is slower (non-flying only)
            if (!isFlying)
            {
                float currentElev = _worldManager.GetElevation(pos.X, pos.Y);
                float destX = Math.Clamp(pos.X + vel.Dx, 0f, _worldSizeTiles - 0.01f);
                float destY = Math.Clamp(pos.Y + vel.Dy, 0f, _worldSizeTiles - 0.01f);
                float elevRise = _worldManager.GetElevation(destX, destY) - currentElev;
                if (elevRise > 0f)
                    speedMult *= Math.Max(0.25f, 1f - elevRise * 8f);
            }

            // Calculate movement delta with terrain speed modifier
            float dx = vel.Dx * speedMult;
            float dy = vel.Dy * speedMult;

            // Try to move
            bool moved = TryMove(ref pos, dx, dy, isFlying);

            // If blocked and moving diagonally, try sliding along cliff edges
            if (!moved && dx != 0 && dy != 0)
            {
                // Try X-axis slide
                if (TryMove(ref pos, dx, 0, isFlying))
                {
                    moved = true;
                }
                // Try Y-axis slide
                else if (TryMove(ref pos, 0, dy, isFlying))
                {
                    moved = true;
                }
            }

            // If still blocked (corner case), try nudging perpendicular to escape
            if (!moved)
            {
                float nudge = 0.05f;
                if (dx != 0)
                {
                    if (TryMove(ref pos, 0, nudge, isFlying) || TryMove(ref pos, 0, -nudge, isFlying))
                    {
                        // Nudged successfully, velocity will move us next frame
                    }
                }
                else if (dy != 0)
                {
                    if (TryMove(ref pos, nudge, 0, isFlying) || TryMove(ref pos, -nudge, 0, isFlying))
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
    /// Ground creatures are hard-blocked by cliffs (large elevation differences between
    /// adjacent positions). Flying creatures bypass this check entirely.
    /// </summary>
    private bool TryMove(ref Position pos, float dx, float dy, bool isFlying)
    {
        if (dx == 0 && dy == 0)
            return false;

        float newX = pos.X + dx;
        float newY = pos.Y + dy;

        // World bounds clamping
        newX = Math.Clamp(newX, 0f, _worldSizeTiles - 0.01f);
        newY = Math.Clamp(newY, 0f, _worldSizeTiles - 0.01f);

        // Ground creatures are blocked by cliff faces (steep elevation jumps).
        // Gradual slopes just slow movement via the slope resistance in Process().
        if (!isFlying)
        {
            float srcElev = _worldManager.GetElevation(pos.X, pos.Y);
            float destElev = _worldManager.GetElevation(newX, newY);
            if (MathF.Abs(destElev - srcElev) > CliffThreshold)
                return false;
        }

        pos.X = newX;
        pos.Y = newY;
        return true;
    }
}
