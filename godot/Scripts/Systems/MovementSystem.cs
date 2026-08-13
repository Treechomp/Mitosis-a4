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

    /// <summary>
    /// Distance from the map border at which movement bounces back inward. Half a tile — just
    /// enough that a creature never ends up standing exactly on the clamp line.
    /// </summary>
    private const float EdgeBuffer = 0.5f;

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
            // NOT LOD-gated. Movement is integration, not decision-making, and gating it made the
            // world run at different SPEEDS in different places: with velocity held constant, a
            // Low-tier creature covered 13% of the ground a Full-tier one did over the same wall
            // clock, and a Minimal-tier one 6%. Distant herds crawled, distant hunts stalled, and
            // how fast the ecosystem advanced depended on where the player happened to stand.
            //
            // What LOD should buy is fewer DECISIONS — the expensive part — not less motion. So
            // every creature integrates its velocity every tick, and the gate below applies only to
            // the bookkeeping that belongs on the decision cadence (clamp and damping), leaving a
            // distant creature to coast on its last decision instead of stuttering.
            bool due = em.DueThisTick[entity];

            ref var pos = ref em.Positions[entity];
            ref var vel = ref em.Velocities[entity];

            // Skip if no movement
            if (vel.Dx == 0 && vel.Dy == 0)
                continue;

            // Clamp creature velocity to prevent unbounded accumulation from additive systems.
            // Only applies to creatures (entities with Species) — not the player. Behaviour
            // systems only write velocity on a due tick, so this only needs checking then.
            if (due && em.HasComponents(entity, ComponentFlags.Species))
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

            // Terrain speed. Resolved only on a DUE tick and cached: the species lookup, the
            // TerrainProfile probe and the slope elevation sample are the expensive part of moving,
            // and none of it changes appreciably between one decision and the next. Between due
            // ticks the entity coasts on the cached value, so its SPEED stays exact while the
            // lookups stay LOD-gated. (Ungating the lookups too made Movement 37% of the tick.)
            SpeciesDefinition? def = em.HasComponents(entity, ComponentFlags.Species)
                ? SpeciesRegistry.GetById(em.Species[entity].SpeciesId) : null;
            bool isFlying = def?.IsFlying ?? false;
            float speedMult;
            if (due || vel.CachedSpeedMult <= 0f)
            {
                var currentTile = _worldManager.GetTile(pos.X, pos.Y);
                speedMult = currentTile.GetSpeedMultiplier();

                // Flying creatures ignore terrain speed penalties
                if (def != null)
                {
                    var speciesDef = def;
                    if (speciesDef.IsFlying)
                    {
                        speedMult = 1f;
                    }
                    else
                    {
                        // REPLACE semantics: a species' own speed for this tile overrides the tile's
                        // intrinsic grip (applies on every tile — sand/snow/grass grip is the fallback
                        // for tiles the species doesn't list). See TerrainProfile.Speed.
                        speedMult = TerrainProfile.Speed(speciesDef, currentTile);

                        // Wrong element (e.g. an aquatic creature flopping on land, or an insect that
                        // wandered into water): it can barely move while it suffocates/drowns. Keeps
                        // beached fish from roaming inland after prey/corpses.
                        if (TerrainProfile.IsImpassable(speciesDef, currentTile))
                            speedMult *= 0.05f;
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

                vel.CachedSpeedMult = speedMult;
            }
            else
            {
                speedMult = vel.CachedSpeedMult;
            }

            // Calculate movement delta with terrain speed modifier
            float dx = vel.Dx * speedMult;
            float dy = vel.Dy * speedMult;

            // World edge = barrier, not a wall to lean on. TryMove clamps the position, which
            // stops the creature without ever changing where it is trying to go: it kept pushing
            // in the same direction forever, so animals piled up along the border (worst for the
            // aquatic species, since out of bounds reads as DeepWater and looks like open sea).
            // Reflecting the offending velocity component turns it back inward; WanderSystem's
            // edge aversion then steers it properly on its next decision.
            if (em.HasComponents(entity, ComponentFlags.Species))
            {
                if (pos.X + dx < EdgeBuffer && dx < 0f)
                {
                    dx = -dx;
                    vel.Dx = -vel.Dx;
                }
                else if (pos.X + dx > _worldSizeTiles - EdgeBuffer && dx > 0f)
                {
                    dx = -dx;
                    vel.Dx = -vel.Dx;
                }

                if (pos.Y + dy < EdgeBuffer && dy < 0f)
                {
                    dy = -dy;
                    vel.Dy = -vel.Dy;
                }
                else if (pos.Y + dy > _worldSizeTiles - EdgeBuffer && dy > 0f)
                {
                    dy = -dy;
                    vel.Dy = -vel.Dy;
                }
            }

            // Element barrier. Steering keeps a creature in its element by preference; this keeps
            // it there by inertia. The two are not the same thing once LOD thins decisions: a
            // Minimal-tier shark commits to a heading for twenty ticks, and if that heading was
            // chosen a hair before a sandbar came into reach, no amount of look-ahead helps —
            // it is already travelling and has nothing left to decide with until it is ashore.
            // Measured on a 40-shark coastline, extended look-ahead and LOD-compensated turning
            // together cut suffocations from 29 to 15; only refusing the step removes them.
            //
            // Reflected rather than blocked, exactly as with the world edge, so the animal turns
            // back to sea instead of grinding along the shore. Only a crossing OUT of the element
            // is refused — a creature already stranded is free to move, which is how it flops
            // back to water (or fails to, and dies there, which is still a thing that happens).
            if (def != null && !isFlying && (def.IsAquatic || def.AvoidsWater)
                && ((int)(pos.X + dx) != (int)pos.X || (int)(pos.Y + dy) != (int)pos.Y)
                && !TerrainProfile.IsImpassable(def, _worldManager.GetTile(pos.X, pos.Y)))
            {
                if ((int)(pos.X + dx) != (int)pos.X
                    && TerrainProfile.IsImpassable(def, _worldManager.GetTile(pos.X + dx, pos.Y)))
                {
                    dx = -dx;
                    vel.Dx = -vel.Dx;
                }
                if ((int)(pos.Y + dy) != (int)pos.Y
                    && TerrainProfile.IsImpassable(def, _worldManager.GetTile(pos.X, pos.Y + dy)))
                {
                    dy = -dy;
                    vel.Dy = -vel.Dy;
                }
                // Diagonal corner: each axis is clear on its own but the combined step cuts the
                // corner onto the wrong element. Turn back along both.
                if (TerrainProfile.IsImpassable(def, _worldManager.GetTile(pos.X + dx, pos.Y + dy)))
                {
                    dx = -dx; dy = -dy;
                    vel.Dx = -vel.Dx; vel.Dy = -vel.Dy;
                }
            }

            // Try to move
            int prevTileX = (int)pos.X;
            int prevTileY = (int)pos.Y;
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

            // Crossing into a new tile invalidates the cached terrain speed, so the next tick
            // re-resolves it instead of coasting on the old tile's grip.
            //
            // This is what let a beached shark keep sliding: the wrong-element x0.05 penalty is
            // part of the cached value, so an aquatic that washed ashore between decisions kept
            // its full swimming speed for the rest of the interval and drove itself further up
            // the beach — up to twenty ticks of open-water speed spent travelling inland. Two int
            // casts, no world lookup; the re-resolve itself only happens on an actual crossing,
            // which at ~0.05 tiles/tick is roughly once every twenty ticks anyway.
            if ((int)pos.X != prevTileX || (int)pos.Y != prevTileY)
                vel.CachedSpeedMult = 0f;

            // Update chunk position if entity has it
            if (em.HasComponents(entity, ComponentFlags.ChunkPosition))
            {
                em.ChunkPositions[entity].Update(in pos, _chunkSize);
            }

            // Dampen creature velocity on the DECISION cadence, so stale forces decay naturally
            // while a distant creature still coasts between decisions rather than stalling.
            // When a behavior system actively sets velocity, it refreshes above this decay.
            // When no system is commanding movement, velocity fades to zero over ~10 decisions.
            if (due && em.HasComponents(entity, ComponentFlags.Species))
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
        //
        // Only tested when the step actually leaves the current TILE. A creature moves ~0.05
        // tiles per tick, so the overwhelming majority of steps stay inside one tile, where
        // elevation varies smoothly and no cliff can be crossed by definition — cliffs are
        // boundaries between tiles. Skipping those cases removes two elevation samples from
        // nearly every move, which is what the movement cost was almost entirely made of.
        if (!isFlying && ((int)newX != (int)pos.X || (int)newY != (int)pos.Y))
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
