using System;
using System.Collections.Generic;
using Mitosis.SpeciesData;
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

    public WorldManager(int chunkSize, int worldSizeChunks, int seed)
    {
        ChunkSize = chunkSize;
        WorldSizeChunks = worldSizeChunks;
        Seed = seed;

        _chunks = new Dictionary<(int, int), Chunk>(worldSizeChunks * worldSizeChunks);
        _generator = new TerrainGenerator(seed);
        SpatialHash = new SpatialHash(chunkSize);

        // Pre-compute flow-based rivers before any chunks are generated
        _generator.PrecomputeRivers(WorldSizeTiles);
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
        chunk.InitializeNutrition();
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
    /// Set a tile type at world coordinates. Returns true if successful.
    /// </summary>
    /// <summary>
    /// Tracks which chunks have been modified since last render.
    /// </summary>
    public readonly HashSet<(int, int)> DirtyChunks = new();

    public bool SetTile(float worldX, float worldY, TileType type)
    {
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null)
            return false;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        chunk.SetTile(localX, localY, type);
        DirtyChunks.Add((chunkX, chunkY));
        return true;
    }

    /// <summary>
    /// Get the raw elevation value (0–1) at world coordinates.
    /// </summary>
    public float GetElevation(float worldX, float worldY)
    {
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null)
            return 0f;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        return chunk.GetElevation(localX, localY);
    }

    /// <summary>
    /// Get the nutrition level at world coordinates (0.0 = depleted, 1.0 = full).
    /// </summary>
    public float GetNutrition(float worldX, float worldY)
    {
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null)
            return 0f;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        return chunk.GetNutrition(localX, localY);
    }

    /// <summary>
    /// Consume nutrition at world coordinates. Returns actual amount consumed.
    /// </summary>
    public float ConsumeNutrition(float worldX, float worldY, float amount)
    {
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null)
            return 0f;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        return chunk.ConsumeNutrition(localX, localY, amount);
    }

    /// <summary>
    /// Check if world position is walkable.
    /// </summary>
    public bool IsWalkable(float worldX, float worldY)
    {
        return GetTile(worldX, worldY).IsWalkable();
    }

    /// <summary>
    /// Check if any water tiles exist within the given radius of a position.
    /// </summary>
    public bool HasWaterNearby(float worldX, float worldY, int radius)
    {
        int cx = (int)worldX;
        int cy = (int)worldY;
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                if (GetTile(cx + dx, cy + dy).IsWater())
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Count water tiles along a line from (x1,y1) to (x2,y2).
    /// Returns the fraction of sampled tiles that are water (0.0 to 1.0).
    /// Samples one tile per unit distance for efficiency.
    /// </summary>
    public float GetWaterFractionOnPath(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist < 1f) return 0f;

        int steps = (int)dist; // Sample every ~1 tile
        if (steps < 1) steps = 1;

        float stepX = dx / steps;
        float stepY = dy / steps;
        int waterCount = 0;

        for (int i = 0; i <= steps; i++)
        {
            float sx = x1 + stepX * i;
            float sy = y1 + stepY * i;
            if (GetTile(sx, sy).IsWater())
                waterCount++;
        }

        return (float)waterCount / (steps + 1);
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

    /// <summary>
    /// Get random spawnable positions in a chunk (excludes water, cliffs, etc).
    /// Returns positions with their biome and tile type for species filtering.
    /// </summary>
    public List<(float x, float y, BiomeType biome, TileType tile)> GetSpawnablePositions(Chunk chunk, int count, Random rng)
    {
        var positions = new List<(float, float, BiomeType, TileType)>();
        var spawnable = new List<(int lx, int ly, BiomeType biome, TileType tile)>();

        // Collect all spawnable tiles with their biomes and tile types
        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                if (chunk.IsSpawnable(lx, ly))
                {
                    var biome = chunk.GetBiome(lx, ly);
                    var tile = chunk.GetTile(lx, ly);
                    spawnable.Add((lx, ly, biome, tile));
                }
            }
        }

        if (spawnable.Count == 0)
            return positions;

        // Sample random positions
        int sampleCount = Math.Min(count, spawnable.Count);
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = rng.Next(spawnable.Count);
            var (lx, ly, biome, tile) = spawnable[idx];
            spawnable.RemoveAt(idx);

            float worldX = chunk.ChunkX * chunk.Size + lx + 0.5f;
            float worldY = chunk.ChunkY * chunk.Size + ly + 0.5f;
            positions.Add((worldX, worldY, biome, tile));
        }

        return positions;
    }

    /// <summary>
    /// Get random spawnable positions in a chunk for a specific species.
    /// Uses the species' CanSpawnOnTile() method to filter valid tiles.
    /// </summary>
    public List<(float x, float y, BiomeType biome, TileType tile)> GetSpawnablePositionsForSpecies(
        Chunk chunk, int count, Random rng, SpeciesDefinition species)
    {
        var positions = new List<(float, float, BiomeType, TileType)>();
        var spawnable = new List<(int lx, int ly, BiomeType biome, TileType tile)>();

        // Collect all tiles that this species can spawn on
        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                var tile = chunk.GetTile(lx, ly);
                if (!species.CanSpawnOnTile(tile))
                    continue;

                // Skip steep slopes for ground-dwelling species
                if (!species.IsAquatic && !species.IsFlying)
                {
                    float e = chunk.GetElevation(lx, ly);
                    float maxSlope = 0f;
                    if (lx > 0)              maxSlope = Math.Max(maxSlope, Math.Abs(chunk.GetElevation(lx - 1, ly) - e));
                    if (lx < chunk.Size - 1) maxSlope = Math.Max(maxSlope, Math.Abs(chunk.GetElevation(lx + 1, ly) - e));
                    if (ly > 0)              maxSlope = Math.Max(maxSlope, Math.Abs(chunk.GetElevation(lx, ly - 1) - e));
                    if (ly < chunk.Size - 1) maxSlope = Math.Max(maxSlope, Math.Abs(chunk.GetElevation(lx, ly + 1) - e));
                    if (maxSlope > 0.18f)
                        continue;
                }

                var biome = chunk.GetBiome(lx, ly);
                spawnable.Add((lx, ly, biome, tile));
            }
        }

        if (spawnable.Count == 0)
            return positions;

        // Sample random positions
        int sampleCount = Math.Min(count, spawnable.Count);
        for (int i = 0; i < sampleCount; i++)
        {
            int idx = rng.Next(spawnable.Count);
            var (lx, ly, biome, tile) = spawnable[idx];
            spawnable.RemoveAt(idx);

            float worldX = chunk.ChunkX * chunk.Size + lx + 0.5f;
            float worldY = chunk.ChunkY * chunk.Size + ly + 0.5f;
            positions.Add((worldX, worldY, biome, tile));
        }

        return positions;
    }

    /// <summary>
    /// Check if a position is spawnable (safe land, not water).
    /// </summary>
    public bool IsSpawnable(float worldX, float worldY)
    {
        return GetTile(worldX, worldY).IsSpawnable();
    }

    /// <summary>
    /// Check if a species can spawn at a world position.
    /// </summary>
    public bool IsSpawnableForSpecies(float worldX, float worldY, SpeciesDefinition species)
    {
        return species.CanSpawnOnTile(GetTile(worldX, worldY));
    }

    /// <summary>
    /// Get the biome at a world position.
    /// </summary>
    public BiomeType GetBiome(float worldX, float worldY)
    {
        return GetTile(worldX, worldY).GetTypicalBiome();
    }
}
