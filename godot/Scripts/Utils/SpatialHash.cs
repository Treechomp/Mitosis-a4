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
    /// Query all entities within radius of a position.
    /// Returns entities that might be in range (broad phase).
    /// </summary>
    public void QueryRadius(float x, float y, float radius, List<int> results)
    {
        results.Clear();

        var (centerCellX, centerCellY) = WorldToCell(x, y);
        int cellRadius = (int)MathF.Ceiling(radius * _invCellSize);

        for (int dx = -cellRadius; dx <= cellRadius; dx++)
        {
            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                long key = HashPosition(centerCellX + dx, centerCellY + dy);
                if (_cells.TryGetValue(key, out var cell))
                {
                    foreach (int entityId in cell)
                    {
                        results.Add(entityId);
                    }
                }
            }
        }
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
