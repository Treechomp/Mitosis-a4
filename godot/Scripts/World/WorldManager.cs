using System;
using System.Collections.Generic;
using Mitosis.Components;
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
    private readonly IChunkGenerator _generator;
    public readonly SpatialHash SpatialHash;

    public WorldManager(int chunkSize, int worldSizeChunks, int seed, TerrainSettings? terrainSettings = null)
        : this(chunkSize, worldSizeChunks, seed,
               CreateNoiseGenerator(seed, terrainSettings, chunkSize * worldSizeChunks))
    {
    }

    /// <summary>
    /// Construct with an explicit chunk generator (test scenes supply a scenario-driven one
    /// instead of the noise pipeline). The generator must be fully initialised — the noise
    /// path pre-computes its river map in <see cref="CreateNoiseGenerator"/>.
    /// </summary>
    public WorldManager(int chunkSize, int worldSizeChunks, int seed, IChunkGenerator generator)
    {
        ChunkSize = chunkSize;
        WorldSizeChunks = worldSizeChunks;
        Seed = seed;

        _chunks = new Dictionary<(int, int), Chunk>(worldSizeChunks * worldSizeChunks);
        _generator = generator;
        SpatialHash = new SpatialHash(chunkSize);
    }

    private static TerrainGenerator CreateNoiseGenerator(int seed, TerrainSettings? settings,
        int worldSizeTiles)
    {
        var generator = new TerrainGenerator(seed, settings);
        // Pre-compute flow-based rivers before any chunks are generated
        generator.PrecomputeRivers(worldSizeTiles);
        return generator;
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
        if (worldX < 0f || worldY < 0f) return TileType.DeepWater; // out of world
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
        if (worldX < 0f || worldY < 0f) return false;
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
    /// Faction terraforming: nudge the moisture parameter at a world position in the given
    /// direction, then re-derive the tile type from the new params. Elevation and temperature
    /// are unchanged, so terraforming only walks along the moisture spectrum within the tile's
    /// climate band (it never turns land into water or mountains). The continuous terrain
    /// colour follows automatically because it reads the params. Returns true if anything
    /// changed (so the chunk is re-coloured). See docs/3d-terrain-plan.md (Phase 2b).
    /// </summary>
    public bool Terraform(float worldX, float worldY, TerraformDirection direction, float step)
    {
        if (worldX < 0f || worldY < 0f) return false;
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);
        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return false;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;

        var tile = chunk.GetTile(localX, localY);
        if (!tile.IsTerraformable()) return false;

        float m = chunk.GetMoisture(localX, localY);
        float nm = direction switch
        {
            TerraformDirection.Wetter   => m + step,
            TerraformDirection.Drier    => m - step,
            TerraformDirection.Balanced => MoveToward(m, 0.4f, step), // 0.4 ≈ grass band
            _ => m
        };
        nm = Math.Clamp(nm, 0f, 1f);
        if (nm == m) return false;

        chunk.SetMoisture(localX, localY, nm);

        // Re-derive the discrete gameplay tile from the new params (Chunk.SetTile also resets
        // nutrition for the new type). Even if the type doesn't cross a threshold, the colour
        // still shifts, so the chunk is always marked dirty when moisture changed.
        var newTile = TerrainGenerator.DetermineTileType(
            chunk.GetElevation(localX, localY), nm, chunk.GetTemperature(localX, localY));
        if (newTile != tile)
            chunk.SetTile(localX, localY, newTile);

        // Terrain tracking (test scenes): every moisture nudge is counted; tile-class shifts
        // additionally get an event row. Central here so ALL terraform paths (roaming
        // terraformers, nest hatches, crystal pulses) are captured.
        Systems.EcosystemLogger.Instance?.LogTerraform(worldX, worldY, direction, tile, newTile, m, nm);

        DirtyChunks.Add((chunkX, chunkY));
        return true;
    }

    /// <summary>
    /// Test-scene tile painting: set a tile's type AND its moisture/temperature parameters to
    /// the canonical values for that type (see <see cref="ScenarioTileParams"/>), so the
    /// continuous palette, terraforming, and re-classification all agree with the painted type.
    /// Elevation is left untouched — chunk mesh geometry is built once and only colours are
    /// rebuilt on dirty chunks, so an elevation edit would desync gameplay from the visuals.
    /// Painted water/mountain therefore renders flat (discrete colour) but behaves correctly.
    /// </summary>
    public bool PaintTile(float worldX, float worldY, TileType type)
    {
        if (worldX < 0f || worldY < 0f) return false;
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);
        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return false;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        var (_, moisture, temperature) = ScenarioTileParams.For(type, chunk.GetElevation(localX, localY));
        chunk.SetMoisture(localX, localY, moisture);
        chunk.SetTemperature(localX, localY, temperature);
        chunk.SetTile(localX, localY, type);
        DirtyChunks.Add((chunkX, chunkY));
        return true;
    }

    private static float MoveToward(float value, float target, float step)
        => value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    /// <summary>
    /// Get the elevation value (0–1) at world coordinates, interpolated across
    /// the terrain mesh triangle that contains the point.
    ///
    /// The terrain mesh splits each grid quad into two triangles whose diagonal
    /// alternates by row parity (matching the terrain mesh triangulation):
    ///   Even rows (diagonal TL→BR): lower-left triangle if fx + fy ≤ 1
    ///   Odd  rows (diagonal BL→TR): lower    triangle if fy ≤ fx
    ///
    /// Barycentric interpolation within the containing triangle produces an
    /// elevation that exactly matches the GPU-rendered mesh surface, so entities
    /// ride the terrain smoothly instead of snapping at tile boundaries.
    /// </summary>
    public float GetElevation(float worldX, float worldY)
    {
        // Clamp to valid range to avoid out-of-bounds lookups at edges
        float maxCoord = WorldSizeTiles - 1.001f;
        float cx = Math.Clamp(worldX, 0f, maxCoord);
        float cy = Math.Clamp(worldY, 0f, maxCoord);

        int ix = (int)MathF.Floor(cx);
        int iy = (int)MathF.Floor(cy);
        float fx = cx - ix;
        float fy = cy - iy;

        // Sample elevation at the four corners of this grid quad
        float eBL = GetElevationAt(ix,     iy);
        float eBR = GetElevationAt(ix + 1, iy);
        float eTL = GetElevationAt(ix,     iy + 1);
        float eTR = GetElevationAt(ix + 1, iy + 1);

        // Interpolate within the correct triangle (matches mesh triangulation)
        if (iy % 2 == 0)
        {
            // Even row — diagonal TL→BR (from (0,1) to (1,0))
            if (fx + fy <= 1f)
            {
                // Triangle BL, TL, BR
                return eBL + (eTL - eBL) * fy + (eBR - eBL) * fx;
            }
            else
            {
                // Triangle BR, TL, TR
                return eTR + (eTL - eTR) * (1f - fx) + (eBR - eTR) * (1f - fy);
            }
        }
        else
        {
            // Odd row — diagonal BL→TR (from (0,0) to (1,1))
            if (fy <= fx)
            {
                // Triangle BL, TR, BR
                return eBR + (eBL - eBR) * (1f - fx) + (eTR - eBR) * fy;
            }
            else
            {
                // Triangle BL, TL, TR
                return eTL + (eBL - eTL) * (1f - fy) + (eTR - eTL) * fx;
            }
        }
    }

    /// <summary>
    /// Raw per-vertex elevation lookup at integer grid coordinates.
    /// Handles chunk boundary crossings.
    /// </summary>
    private float GetElevationAt(int worldX, int worldY)
    {
        int chunkX = worldX / ChunkSize;
        int chunkY = worldY / ChunkSize;
        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return 0f;
        int localX = worldX - chunkX * ChunkSize;
        int localY = worldY - chunkY * ChunkSize;
        if (localX >= chunk.Size || localY >= chunk.Size) return 0f;
        return chunk.GetElevation(localX, localY);
    }

    /// <summary>
    /// Raw per-vertex elevation (0–1) at integer world grid coordinates, clamped to world
    /// bounds (no interpolation). Used for seam-free analytic terrain normals: because the
    /// result is a pure function of world position, a vertex shared by adjacent chunks
    /// resolves to the same elevation (and therefore the same normal) in both.
    /// </summary>
    public float GetVertexElevation(int worldX, int worldY)
    {
        if (worldX < 0) worldX = 0;
        else if (worldX >= WorldSizeTiles) worldX = WorldSizeTiles - 1;
        if (worldY < 0) worldY = 0;
        else if (worldY >= WorldSizeTiles) worldY = WorldSizeTiles - 1;

        int chunkX = worldX / ChunkSize;
        int chunkY = worldY / ChunkSize;
        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return 0f;
        return chunk.GetElevation(worldX - chunkX * ChunkSize, worldY - chunkY * ChunkSize);
    }

    /// <summary>Clamped per-vertex moisture (0–1) at integer world coords (for mesh edges).</summary>
    public float GetVertexMoisture(int worldX, int worldY)
    {
        if (worldX < 0) worldX = 0; else if (worldX >= WorldSizeTiles) worldX = WorldSizeTiles - 1;
        if (worldY < 0) worldY = 0; else if (worldY >= WorldSizeTiles) worldY = WorldSizeTiles - 1;
        int chunkX = worldX / ChunkSize;
        int chunkY = worldY / ChunkSize;
        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return 0f;
        return chunk.GetMoisture(worldX - chunkX * ChunkSize, worldY - chunkY * ChunkSize);
    }

    /// <summary>Clamped per-vertex temperature (0–1) at integer world coords (for mesh edges).</summary>
    public float GetVertexTemperature(int worldX, int worldY)
    {
        if (worldX < 0) worldX = 0; else if (worldX >= WorldSizeTiles) worldX = WorldSizeTiles - 1;
        if (worldY < 0) worldY = 0; else if (worldY >= WorldSizeTiles) worldY = WorldSizeTiles - 1;
        int chunkX = worldX / ChunkSize;
        int chunkY = worldY / ChunkSize;
        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return 0f;
        return chunk.GetTemperature(worldX - chunkX * ChunkSize, worldY - chunkY * ChunkSize);
    }

    /// <summary>
    /// Get the nutrition level at world coordinates (0.0 = depleted, 1.0 = full).
    /// </summary>
    public float GetNutrition(float worldX, float worldY)
    {
        if (worldX < 0f || worldY < 0f) return 0f;
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
        if (worldX < 0f || worldY < 0f) return 0f;
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null)
            return 0f;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        float consumed = chunk.ConsumeNutrition(localX, localY, amount);
        // Nutrition tracking (test scenes): aggregate all grazing/fertility consumption.
        if (consumed > 0f)
            Systems.EcosystemLogger.Instance?.CountNutritionConsumed(consumed);
        return consumed;
    }

    /// <summary>
    /// Add nutrition to a grazeable tile (e.g. a decomposing corpse enriching the soil).
    /// Clamped to the tile's maximum; no effect on non-grazeable tiles.
    /// </summary>
    public void AddNutrition(float worldX, float worldY, float amount)
    {
        if (worldX < 0f || worldY < 0f || amount <= 0f) return;
        int chunkX = (int)(worldX / ChunkSize);
        int chunkY = (int)(worldY / ChunkSize);

        var chunk = GetChunk(chunkX, chunkY);
        if (chunk == null) return;

        int localX = (int)worldX % ChunkSize;
        int localY = (int)worldY % ChunkSize;
        float added = chunk.AddNutrition(localX, localY, amount);
        // Nutrition tracking (test scenes): corpse decomposition enriching the soil.
        if (added > 0f)
            Systems.EcosystemLogger.Instance?.CountNutritionEnriched(added);
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
    /// <param name="deepOnly">When true, count only deep water — land creatures wade shallow/
    /// river freely, so only deep crossings should deter them. When false, count all water
    /// (for species that can't swim at all, e.g. insects).</param>
    public float GetWaterFractionOnPath(float x1, float y1, float x2, float y2, bool deepOnly = false)
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
            var t = GetTile(sx, sy);
            if (deepOnly ? t.IsDeepWater() : t.IsWater())
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
    /// Get random spawnable positions in a chunk (land tiles, excludes water/lava/etc).
    /// </summary>
    public List<(float x, float y)> GetWalkablePositions(Chunk chunk, int count, Random rng)
    {
        var positions = new List<(float, float)>();
        var walkable = new List<(int, int)>();

        // Collect all spawnable land tiles
        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                if (chunk.IsSpawnable(lx, ly))
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

        // Niche placement: a non-aquatic predator with HuntTerrain (Penguin/Crocodile/Polar Bear
        // → water) should START near its hunting grounds, not stranded inland where it starves
        // before food-seeking can help it migrate. Keep only spawn tiles within reach of a
        // HuntTerrain tile; fall back to the full set if this chunk has none, so spawning never
        // fails. (Aquatic species already spawn in water — i.e. on their food.)
        if (species.HuntTerrain != null && !species.IsAquatic)
        {
            var near = new List<(int lx, int ly, BiomeType biome, TileType tile)>();
            foreach (var s in spawnable)
            {
                float wx = chunk.ChunkX * chunk.Size + s.lx + 0.5f;
                float wy = chunk.ChunkY * chunk.Size + s.ly + 0.5f;
                if (HasTerrainNear(wx, wy, species.HuntTerrain, 9f))
                    near.Add(s);
            }
            if (near.Count > 0)
                spawnable = near;
        }

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
    /// True if any of the given tile types is within `maxRadius` tiles of (x, y). Sampled on
    /// concentric rings (cheap, one-time at spawn) — used for niche-aware spawn placement.
    /// </summary>
    private bool HasTerrainNear(float x, float y, List<TileType> types, float maxRadius)
    {
        for (float r = 3f; r <= maxRadius; r += 3f)
        {
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4f;
                if (types.Contains(GetTile(x + MathF.Cos(a) * r, y + MathF.Sin(a) * r)))
                    return true;
            }
        }
        return false;
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
