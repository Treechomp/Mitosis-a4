using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Mitosis.Utils;

/// <summary>
/// Spatial hash for O(1) neighbor queries.
/// Uses List cells (faster iteration than HashSet for small N)
/// with object pooling to avoid GC pressure from cell allocation.
/// </summary>
public sealed class SpatialHash
{
    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly Dictionary<long, List<int>> _cells;

    public float CellSize => _cellSize;
    public float InvCellSize => _invCellSize;
    private readonly Dictionary<int, long> _entityCells;
    private readonly Stack<List<int>> _cellPool;

    // Last inserted position per entity, so QueryRadius can reject candidates that share a
    // cell but lie outside the requested radius. Flat arrays (indexed by entity id, like the
    // ECS component stores) — a dictionary lookup per candidate would cost more than the
    // distance test it enables. ~128 KB total.
    private readonly float[] _posX = new float[ECS.EntityManager.MaxEntities];
    private readonly float[] _posY = new float[ECS.EntityManager.MaxEntities];

    public SpatialHash(float cellSize = 32f)
    {
        _cellSize = cellSize;
        _invCellSize = 1f / cellSize;
        _cells = new Dictionary<long, List<int>>(1024);
        _entityCells = new Dictionary<int, long>(2048);
        _cellPool = new Stack<List<int>>(256);
    }

    /// <summary>
    /// Hash 2D coordinates to a single long key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long HashPosition(int cellX, int cellY)
    {
        // Combine two ints into one long (avoids tuple allocation)
        return ((long)cellX << 32) | (uint)cellY;
    }

    /// <summary>
    /// Convert world position to cell coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int x, int y) WorldToCell(float x, float y)
    {
        return ((int)MathF.Floor(x * _invCellSize), (int)MathF.Floor(y * _invCellSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<int> RentCell()
    {
        if (_cellPool.TryPop(out var cell))
        {
            cell.Clear();
            return cell;
        }
        return new List<int>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnCell(List<int> cell)
    {
        cell.Clear();
        _cellPool.Push(cell);
    }

    /// <summary>
    /// Remove an entity from a cell using swap-and-remove-last (O(1)).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapRemove(List<int> cell, int entityId)
    {
        for (int i = cell.Count - 1; i >= 0; i--)
        {
            if (cell[i] == entityId)
            {
                int last = cell.Count - 1;
                if (i != last)
                    cell[i] = cell[last];
                cell.RemoveAt(last);
                return;
            }
        }
    }

    /// <summary>
    /// Insert or update an entity's position in the spatial hash.
    /// </summary>
    public void Update(int entityId, float x, float y)
    {
        var (cellX, cellY) = WorldToCell(x, y);
        long newKey = HashPosition(cellX, cellY);

        // Record the exact position for radius filtering in QueryRadius. Must happen even on
        // the same-cell fast path below, or a mover that stays inside its cell would be
        // filtered against a stale position.
        if ((uint)entityId < (uint)_posX.Length)
        {
            _posX[entityId] = x;
            _posY[entityId] = y;
        }

        // Check if entity already exists and is in the same cell
        if (_entityCells.TryGetValue(entityId, out long oldKey))
        {
            if (oldKey == newKey) return; // Same cell, no update needed

            // Remove from old cell
            if (_cells.TryGetValue(oldKey, out var oldCell))
            {
                SwapRemove(oldCell, entityId);
                if (oldCell.Count == 0)
                {
                    _cells.Remove(oldKey);
                    ReturnCell(oldCell);
                }
            }
        }

        // Add to new cell
        if (!_cells.TryGetValue(newKey, out var cell))
        {
            cell = RentCell();
            _cells[newKey] = cell;
        }
        cell.Add(entityId);
        _entityCells[entityId] = newKey;
    }

    /// <summary>
    /// Remove an entity from the spatial hash.
    /// </summary>
    public void Remove(int entityId)
    {
        if (_entityCells.TryGetValue(entityId, out long key))
        {
            _entityCells.Remove(entityId);
            if (_cells.TryGetValue(key, out var cell))
            {
                SwapRemove(cell, entityId);
                if (cell.Count == 0)
                {
                    _cells.Remove(key);
                    ReturnCell(cell);
                }
            }
        }
    }

    /// <summary>
    /// Query all entities within <paramref name="radius"/> of a position — an EXACT circle,
    /// not a cell block.
    ///
    /// This used to return the raw cell contents ("broad phase"), leaving the precise distance
    /// test to the caller. Most callers didn't do it, and because cells are 32 tiles wide the
    /// scanned block is at minimum 3×3 cells (96×96 tiles) for ANY radius up to 32 — so a
    /// nominal 2.5-tile query could reach ~45 tiles. That silently gave several systems
    /// world-scale reach: fungivores ate blooms across the map, Shroomer spore AoE damaged
    /// enemies far outside its radius, and every "how many X are near me" count (crowding,
    /// competition, herd protection, pack presence, keeper sensing) was measured over the cell
    /// block instead of its intended radius.
    ///
    /// Filtering here rather than at ~28 call sites makes the safe behaviour the default one,
    /// and shrinks result lists so callers' own per-candidate work drops too.
    /// </summary>
    /// <summary>
    /// Neighbour-query volume since the counters were last reset. Two increments on a hot path,
    /// which is nothing against the work the query itself does, and it is the only direct read of
    /// the quantity `23ee09d` established as the dominant term in tick cost.
    /// </summary>
    public static long QueryCalls;
    public static long QueryResults;

    /// <summary>Widest sweep a single query may make, in cells. A legitimate query is a creature's
    /// senses; nothing in the roster sees a hundred cells.</summary>
    private const int MaxCellRadius = 128;
    private static bool _warnedUnboundedQuery;

    /// <summary>Entities in the fullest cell. A single cell holding thousands is what a non-finite
    /// position looks like from here: every such entity hashes to the same garbage cell, and every
    /// query whose range touches it walks the lot.</summary>
    public int MaxBucketOccupancy()
    {
        int max = 0;
        foreach (var cell in _cells.Values)
            if (cell.Count > max) max = cell.Count;
        return max;
    }

    public int CellCount => _cells.Count;

    public void QueryRadius(float x, float y, float radius, List<int> results)
    {
        results.Clear();

        var (centerCellX, centerCellY) = WorldToCell(x, y);
        int cellRadius = (int)MathF.Ceiling(radius * _invCellSize);
        float radiusSq = radius * radius;

        // A query radius is derived from creature state, and creature state can go wrong. An
        // infinite or NaN radius turns the cell sweep below into a loop over billions of cells
        // that never returns — one tick that never ends, which reads as a hang rather than as the
        // value error it is. Refuse it loudly and carry on with a sane bound.
        if (!float.IsFinite(radius) || cellRadius > MaxCellRadius)
        {
            if (!_warnedUnboundedQuery)
            {
                _warnedUnboundedQuery = true;
                Godot.GD.PushWarning(
                    $"[SpatialHash] query radius {radius} at ({x}, {y}) spans {cellRadius} cells — clamped. " +
                    "Something upstream has produced a non-finite or absurd radius.");
            }
            cellRadius = MaxCellRadius;
            if (!float.IsFinite(radiusSq)) radiusSq = float.MaxValue;
        }

        for (int dx = -cellRadius; dx <= cellRadius; dx++)
        {
            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                long key = HashPosition(centerCellX + dx, centerCellY + dy);
                if (_cells.TryGetValue(key, out var cell))
                {
                    foreach (int entityId in cell)
                    {
                        float ex = _posX[entityId] - x;
                        float ey = _posY[entityId] - y;
                        if (ex * ex + ey * ey <= radiusSq)
                            results.Add(entityId);
                    }
                }
            }
        }

        QueryCalls++;
        QueryResults += results.Count;
    }

    /// <summary>
    /// Get all entities in a specific cell.
    /// </summary>
    public IEnumerable<int> GetEntitiesInCell(int cellX, int cellY)
    {
        long key = HashPosition(cellX, cellY);
        if (_cells.TryGetValue(key, out var cell))
            return cell;
        return Array.Empty<int>();
    }

    /// <summary>
    /// Clear all entities from the spatial hash.
    /// </summary>
    public void Clear()
    {
        foreach (var cell in _cells.Values)
            ReturnCell(cell);
        _cells.Clear();
        _entityCells.Clear();
    }

    /// <summary>
    /// Get the number of entities in the spatial hash.
    /// </summary>
    public int Count => _entityCells.Count;
}
