using System;
using System.Collections.Generic;
using Mitosis.Utils;

namespace Mitosis.World;

/// <summary>
/// Manages the game world: chunks, terrain, and spatial queries.
/// </summary>
public sealed class WorldManager
{
    public int ChunkSize { get; }
    public int WorldSizeChunks { get; }
    public int WorldSizeTiles => ChunkSize * WorldSizeChunks;
    public int Seed { get; }

    private readonly Dictionary<(int, int), Chunk> _chunks;
    private readonly TerrainGenerator _generator;
    public readonly SpatialHash SpatialHash;

    public WorldManager(int chunkSize = 32, int worldSizeChunks = 16, int seed = 42)
    {
        ChunkSize = chunkSize;
        WorldSizeChunks = worldSizeChunks;
        Seed = seed;

        _chunks = new Dictionary<(int, int), Chunk>(worldSizeChunks * worldSizeChunks);
        _generator = new TerrainGenerator(seed);
        SpatialHash = new SpatialHash(chunkSize);
    }

    /// <summary>
    /// Get a chunk, generating it if it doesn't exist.
    /// </summary>
    public Chunk GetOrGenerateChunk(int chunkX, int chunkY)
    {
        if (!IsValidChunk(chunkX, chunkY))
            return null!;

        var key = (chunkX, chunkY);
        if (_chunks.TryGetValue(key, out var chunk))
            return chunk;

        // Generate new chunk
        chunk = new Chunk(chunkX, chunkY, ChunkSize);
        _generator.GenerateChunk(chunk);
        _chunks[key] = chunk;
        return chunk;
    }

    /// <summary>
    /// Get a chunk if it exists, null otherwise.
    /// </summary>
    public Chunk? GetChunk(int chunkX, int chunkY)
    {
        if (!IsValidChunk(chunkX, chunkY))
            return null;

        return _chunks.GetValueOrDefault((chunkX, chunkY));
    }

    /// <summary>
    /// Check if chunk coordinates are valid.
    /// </summary>
    public bool IsValidChunk(int chunkX, int chunkY)
    {
        return chunkX >= 0 && chunkX < WorldSizeChunks &&
               chunkY >= 0 && chunkY < WorldSizeChunks;
    }

    /// <summary>
    /// Get tile type at world coordinates.
    /// </summary>
    public TileType GetTile(float worldX, float worldY)
    {
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null)
            return TileType.DeepWater;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        return chunk.GetTile(localX, localY);
    }

    /// <summary>
    /// Check if world position is walkable.
    /// </summary>
    public bool IsWalkable(float worldX, float worldY)
    {
        return GetTile(worldX, worldY).IsWalkable();
    }

    /// <summary>
    /// Convert world coordinates to chunk coordinates.
    /// </summary>
    public (int chunkX, int chunkY) WorldToChunk(float worldX, float worldY)
    {
        return ((int)(worldX / ChunkSize), (int)(worldY / ChunkSize));
    }

    /// <summary>
    /// Pre-generate all chunks in the world.
    /// </summary>
    public void PregenerateWorld(Action<int, int>? progressCallback = null)
    {
        int total = WorldSizeChunks * WorldSizeChunks;
        int completed = 0;

        for (int cx = 0; cx < WorldSizeChunks; cx++)
        {
            for (int cy = 0; cy < WorldSizeChunks; cy++)
            {
                GetOrGenerateChunk(cx, cy);
                completed++;
                progressCallback?.Invoke(completed, total);
            }
        }
    }

    /// <summary>
    /// Get all loaded chunks.
    /// </summary>
    public IEnumerable<Chunk> GetLoadedChunks()
    {
        return _chunks.Values;
    }

    /// <summary>
    /// Get number of loaded chunks.
    /// </summary>
    public int LoadedChunkCount => _chunks.Count;

    /// <summary>
    /// Get random walkable positions in a chunk.
    /// </summary>
    public List<(float x, float y)> GetWalkablePositions(Chunk chunk, int count, Random rng)
    {
        var positions = new List<(float, float)>();
        var walkable = new List<(int, int)>();

        // Collect all walkable tiles
        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                if (chunk.IsWalkable(lx, ly))
                    walkable.Add((lx, ly));
            }
        }

        if (walkable.Count == 0)
            return positions;

        // Sample random positions
        int sampleCount = Math.Min(count, walkable.Count);
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = rng.Next(walkable.Count);
            var (lx, ly) = walkable[idx];
            walkable.RemoveAt(idx);

            float worldX = chunk.ChunkX * chunk.Size + lx + 0.5f;
            float worldY = chunk.ChunkY * chunk.Size + ly + 0.5f;
            positions.Add((worldX, worldY));
        }

        return positions;
    }
}
