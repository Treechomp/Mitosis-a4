using System;
using System.Collections.Generic;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Systems;

/// <summary>
/// Updates LOD levels and tick countdown for entities based on distance from player.
/// Must run FIRST each tick — all other systems skip entities where DueThisTick is false.
///
/// Distance computation is cached per spatial-hash cell: all entities in the same cell
/// share one distance calculation (cell-center to player). At cell size 32 and LOD
/// boundaries starting at ~70 tiles, the max error (~22 tiles diagonal) is within
/// the existing 10% hysteresis buffer. This reduces sqrt calls from O(entities)
/// to O(occupied cells) — typically 300-400 vs 10,000+.
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

    // Spatial hash cell size for chunk-based distance caching
    private readonly float _cellSize;
    private readonly float _invCellSize;

    // Per-cell distance cache: cleared each tick, populated lazily as entities are visited.
    // Key = packed (cellX, cellY), Value = distance from cell center to player.
    private readonly Dictionary<long, float> _cellDistCache = new(512);

    // Per-LOD-level entity counts for debug display
    public int CountFull { get; private set; }
    public int CountHigh { get; private set; }
    public int CountMedium { get; private set; }
    public int CountLow { get; private set; }
    public int CountMinimal { get; private set; }

    public LODSystem(SpatialHash spatialHash)
    {
        _cellSize = spatialHash.CellSize;
        _invCellSize = spatialHash.InvCellSize;
    }

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

        // Clear per-cell distance cache for this tick
        _cellDistCache.Clear();

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

            // Map entity position to spatial hash cell
            int cellX = (int)MathF.Floor(pos.X * _invCellSize);
            int cellY = (int)MathF.Floor(pos.Y * _invCellSize);
            long cellKey = ((long)cellX << 32) | (uint)cellY;

            // Get or compute distance for this cell (one sqrt per occupied cell)
            if (!_cellDistCache.TryGetValue(cellKey, out float cellDist))
            {
                float cx = (cellX + 0.5f) * _cellSize;
                float cy = (cellY + 0.5f) * _cellSize;
                cellDist = MathUtils.Distance(_playerX, _playerY, cx, cy);
                _cellDistCache[cellKey] = cellDist;
            }

            // Use cell-center distance (approximate but within hysteresis tolerance)
            lod.DistanceToPlayer = cellDist;

            // Determine LOD level from distance and current visible radius
            // Per-entity hysteresis is preserved: currentLevel is still the entity's own level
            var newLevel = SimulationLOD.GetLevelForDistance(cellDist, visRadius, lod.Level);

            // If level changed (entity moved closer/farther), force immediate update.
            // Also repair a zero/uninitialized TickInterval: a default-constructed
            // SimulationLOD zero-inits TickInterval to 0, and if the entity's real level
            // already equals the default (Full) the level-change branch never fires —
            // leaving TickInterval at 0, which causes divide-by-zero (NaN) downstream.
            if (newLevel != lod.Level || lod.TickInterval <= 0)
            {
                lod.Level = newLevel;
                lod.TickInterval = SimulationLOD.GetTickInterval(newLevel);
                lod.TicksUntilUpdate = 0;
            }

            // Countdown: decrement first, then check if due.
            // When TicksUntilUpdate was 0 (initial spawn or forced by level change),
            // the decrement brings it to -1, which triggers the reset-and-due branch.
            // This guarantees "force immediate" actually fires for ALL tick intervals,
            // not just interval=1.
            lod.TicksUntilUpdate--;
            if (lod.TicksUntilUpdate <= 0)
            {
                dueArray[entity] = true;
                lod.TicksUntilUpdate = lod.TickInterval;
            }
            else
            {
                dueArray[entity] = false;
            }

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
