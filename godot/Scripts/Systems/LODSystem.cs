using System;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Updates LOD levels and tick countdown for entities based on distance from player.
/// Must run FIRST each tick — all other systems skip entities where DueThisTick is false.
///
/// The Full tier covers everything within the camera's visible radius + 15% buffer
/// for player movement. LOD tiers beyond that are spaced as multiples of visible radius.
///
/// Tick gating pattern:
///   - LODSystem counts down TicksUntilUpdate each tick.
///   - When it reaches 0, the entity is "due" — all systems process it.
///   - LODSystem then resets the timer to the interval for the entity's LOD level.
///   - Result: Full=every tick (20 TPS), High=every 2 ticks (10 TPS), etc.
///
/// Populates em.DueThisTick[] so downstream systems can gate with a single array read
/// instead of checking HasComponents(SimulationLOD) + TicksUntilUpdate.
/// </summary>
public sealed class LODSystem : ISystem
{
    private float _playerX;
    private float _playerY;
    private int _playerEntity = -1;
    private float _visibleRadius = 60f; // Default fallback (tiles)

    // Per-LOD-level entity counts for debug display
    public int CountFull { get; private set; }
    public int CountHigh { get; private set; }
    public int CountMedium { get; private set; }
    public int CountLow { get; private set; }
    public int CountMinimal { get; private set; }

    public void SetPlayerEntity(int entity)
    {
        _playerEntity = entity;
    }

    /// <summary>
    /// Update the visible radius based on the camera viewport and zoom.
    /// Called each frame by GameManager before the tick loop.
    /// visibleRadius is the half-diagonal of the viewport in tile units.
    /// </summary>
    public void SetVisibleRadius(float visibleRadius)
    {
        _visibleRadius = MathF.Max(16f, visibleRadius); // Floor at 16 tiles
    }

    public void Process(EntityManager em)
    {
        // Get player position
        if (_playerEntity >= 0 && em.IsAlive(_playerEntity) &&
            em.HasComponents(_playerEntity, ComponentFlags.Position))
        {
            ref var playerPos = ref em.Positions[_playerEntity];
            _playerX = playerPos.X;
            _playerY = playerPos.Y;
        }

        // Reset counts
        CountFull = 0;
        CountHigh = 0;
        CountMedium = 0;
        CountLow = 0;
        CountMinimal = 0;

        // Mark all entities without SimulationLOD as always due.
        // We do this by defaulting to true for all alive entities and then
        // overriding to false for LOD-gated entities that aren't due.
        // This is done in the loop below to avoid a separate pass.

        // First: mark all alive entities as due (covers player, structures, etc.)
        int nextId = em.NextId;
        var dueArray = em.DueThisTick;
        for (int i = 0; i < nextId; i++)
            dueArray[i] = em.IsAlive(i);

        float visRadius = _visibleRadius;
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.SimulationLOD;

        foreach (int entity in em.Query(required))
        {
            ref var pos = ref em.Positions[entity];
            ref var lod = ref em.SimulationLODs[entity];

            // Calculate distance to player
            lod.DistanceToPlayer = MathUtils.Distance(_playerX, _playerY, pos.X, pos.Y);

            // Determine LOD level from distance and current visible radius
            var newLevel = SimulationLOD.GetLevelForDistance(lod.DistanceToPlayer, visRadius);

            // If level changed (entity moved closer/farther), force immediate update
            if (newLevel != lod.Level)
            {
                lod.Level = newLevel;
                lod.TickInterval = SimulationLOD.GetTickInterval(newLevel);
                lod.TicksUntilUpdate = 0;
            }

            // Countdown and reset: when timer hits 0, entity is due this tick.
            // Reset timer so it counts down again for the next interval.
            if (lod.TicksUntilUpdate == 0)
                lod.TicksUntilUpdate = lod.TickInterval;
            lod.TicksUntilUpdate--;
            // After this: TicksUntilUpdate == 0 means "process this tick"

            // Update DueThisTick flag (override the default true set above)
            dueArray[entity] = lod.TicksUntilUpdate == 0;

            // Track counts per level
            switch (lod.Level)
            {
                case LODLevel.Full: CountFull++; break;
                case LODLevel.High: CountHigh++; break;
                case LODLevel.Medium: CountMedium++; break;
                case LODLevel.Low: CountLow++; break;
                case LODLevel.Minimal: CountMinimal++; break;
            }
        }
    }
}
