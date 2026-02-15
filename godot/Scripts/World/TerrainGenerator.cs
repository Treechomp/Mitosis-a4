using System;
using Godot;

namespace Mitosis.World;

/// <summary>
/// Generates terrain using simplex noise with domain warping, temperature gradients,
/// flow-based rivers, and landmark post-processing for diverse, biome-rich worlds.
/// </summary>
public sealed class TerrainGenerator
{
    private readonly int _seed;
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _moistureNoise;
    private readonly FastNoiseLite _temperatureNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseY;
    private readonly FastNoiseLite _landmarkNoise;

    // Domain warping amplitude (in tiles) — larger value means more organic, winding boundaries
    private const float WarpAmplitude = 24f;

    // Flow-based river system (pre-computed before chunk generation)
    private RiverMapper? _riverMapper;
    private int _worldSizeTiles = 512; // Updated by PrecomputeRivers

    public TerrainGenerator(int seed = 42)
    {
        _seed = seed;

        // Elevation noise — continent/landmass scale features
        _elevationNoise = new FastNoiseLite();
        _elevationNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _elevationNoise.Seed = seed;
        _elevationNoise.Frequency = 0.012f;
        _elevationNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _elevationNoise.FractalOctaves = 4;

        // Moisture noise — continent scale so deserts/wetlands form large regions
        _moistureNoise = new FastNoiseLite();
        _moistureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _moistureNoise.Seed = seed + 1000;
        _moistureNoise.Frequency = 0.008f;
        _moistureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _moistureNoise.FractalOctaves = 3;

        // Temperature noise — very large-scale regions with latitude-like gradient
        _temperatureNoise = new FastNoiseLite();
        _temperatureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _temperatureNoise.Seed = seed + 5000;
        _temperatureNoise.Frequency = 0.005f;
        _temperatureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _temperatureNoise.FractalOctaves = 2;

        // Domain warp noise X — distorts coordinates for organic biome boundaries
        _warpNoiseX = new FastNoiseLite();
        _warpNoiseX.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _warpNoiseX.Seed = seed + 7000;
        _warpNoiseX.Frequency = 0.008f;
        _warpNoiseX.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _warpNoiseX.FractalOctaves = 2;

        // Domain warp noise Y
        _warpNoiseY = new FastNoiseLite();
        _warpNoiseY.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _warpNoiseY.Seed = seed + 8000;
        _warpNoiseY.Frequency = 0.008f;
        _warpNoiseY.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _warpNoiseY.FractalOctaves = 2;

        // Landmark noise — used for feature placement (lakes, oases, clearings, etc.)
        _landmarkNoise = new FastNoiseLite();
        _landmarkNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _landmarkNoise.Seed = seed + 9000;
        _landmarkNoise.Frequency = 0.04f;
        _landmarkNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _landmarkNoise.FractalOctaves = 2;
    }

    /// <summary>
    /// Pre-compute the flow-based river map for the entire world.
    /// Must be called before any chunks are generated.
    /// Uses the same elevation noise and domain warping so rivers align with terrain.
    /// </summary>
    public void PrecomputeRivers(int worldSizeTiles)
    {
        _worldSizeTiles = worldSizeTiles;
        _riverMapper = new RiverMapper(_elevationNoise, _warpNoiseX, _warpNoiseY, WarpAmplitude);
        _riverMapper.Generate(worldSizeTiles, _seed);
    }

    /// <summary>
    /// Generate terrain for a chunk with domain warping and temperature-based biomes.
    /// </summary>
    public void GenerateChunk(Chunk chunk)
    {
        int worldOffsetX = chunk.ChunkX * chunk.Size;
        int worldOffsetY = chunk.ChunkY * chunk.Size;

        for (int localY = 0; localY < chunk.Size; localY++)
        {
            for (int localX = 0; localX < chunk.Size; localX++)
            {
                int worldX = worldOffsetX + localX;
                int worldY = worldOffsetY + localY;

                // Apply domain warping for organic biome boundaries
                float warpX = _warpNoiseX.GetNoise2D(worldX, worldY) * WarpAmplitude;
                float warpY = _warpNoiseY.GetNoise2D(worldX, worldY) * WarpAmplitude;
                float warpedX = worldX + warpX;
                float warpedY = worldY + warpY;

                // Get noise values with warped coordinates (-1 to 1, normalize to 0-1)
                float elevation = (_elevationNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f;
                float moisture = (_moistureNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f;
                float temperature = GetTemperature(worldX, worldY, elevation);

                TileType tile = DetermineTileType(elevation, moisture, temperature);

                // Apply flow-based rivers, lakes, and wetland banks on land tiles
                if (_riverMapper != null && tile.IsSpawnable())
                {
                    if (_riverMapper.IsLake(worldX, worldY))
                    {
                        tile = TileType.ShallowWater;
                    }
                    else if (_riverMapper.IsRiver(worldX, worldY))
                    {
                        tile = TileType.River;
                    }
                    else if (_riverMapper.IsWetlandBank(worldX, worldY))
                    {
                        tile = TileType.Wetland;
                    }
                }

                chunk.SetTile(localX, localY, tile);
            }
        }

        // Post-processing pass: landmarks
        ApplyLandmarks(chunk, worldOffsetX, worldOffsetY);
    }

    /// <summary>
    /// Compute temperature at a world position. Combines noise with a latitude-like
    /// gradient so cold biomes cluster near the top and hot biomes near the bottom.
    /// Elevation also cools temperature (mountains are cold).
    /// Returns 0-1 where 0 = cold, 1 = hot.
    /// </summary>
    private float GetTemperature(int worldX, int worldY, float elevation)
    {
        // Noise component (large-scale regional variation)
        float noiseTemp = (_temperatureNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;

        // Gradient gives pole-to-equator feel; noise adds regional variation.
        // Increased gradient weight for clearer biome banding on larger maps.
        float gradientWeight = 0.4f;
        float noiseWeight = 0.6f;
        float gradient = (float)worldY / _worldSizeTiles;
        gradient = Math.Clamp(gradient, 0f, 1f);

        float temp = noiseTemp * noiseWeight + gradient * gradientWeight;

        // Altitude cooling: high elevation reduces temperature
        float altitudePenalty = Math.Max(0f, (elevation - 0.65f)) * 1.5f;
        temp -= altitudePenalty;

        return Math.Clamp(temp, 0f, 1f);
    }

    private static TileType DetermineTileType(float elevation, float moisture, float temperature)
    {
        // Deep water
        if (elevation < 0.3f)
            return TileType.DeepWater;

        // Shallow water (with reef variant in warm coastal areas)
        if (elevation < 0.4f)
        {
            // Reef generates in warm, shallow coastal zones
            if (temperature > 0.7f && elevation > 0.35f)
                return TileType.Reef;
            return TileType.ShallowWater;
        }

        // Beach/sand
        if (elevation < 0.45f)
            return TileType.Sand;

        // Mountains at high elevation
        if (elevation > 0.8f)
        {
            // Volcanic vents: rare hot spots in mountain zones
            if (temperature > 0.65f && moisture < 0.3f)
                return TileType.Lava;
            return TileType.Mountain;
        }

        // === Arctic biomes (cold temperature) ===
        if (temperature < 0.2f)
        {
            // Ice forms at very cold + low elevation (frozen water/ground)
            if (moisture > 0.6f || elevation < 0.55f)
                return TileType.Ice;
            return TileType.Tundra;
        }

        if (temperature < 0.35f)
        {
            // Tundra: cold but not frozen
            if (moisture > 0.7f)
                return TileType.Wetland; // Cold wetlands still possible
            return TileType.Tundra;
        }

        // === Tropical biomes (hot temperature) ===
        if (temperature > 0.75f)
        {
            if (moisture > 0.65f)
                return TileType.Jungle;
            if (moisture > 0.4f)
                return TileType.Savanna;
            if (moisture > 0.2f)
                return TileType.Sand;
            return TileType.Arid;
        }

        if (temperature > 0.6f)
        {
            // Warm-temperate: Savanna replaces Grass in drier warm areas
            if (moisture > 0.7f)
                return TileType.Jungle;
            if (moisture > 0.55f)
                return TileType.Forest;
            if (moisture > 0.35f)
            {
                // Transition zone: warm grassland becomes savanna
                return temperature > 0.67f ? TileType.Savanna : TileType.Grass;
            }
            if (moisture > 0.2f)
                return TileType.Sand;
            return TileType.Arid;
        }

        // === Temperate biomes (middle temperature) ===
        if (moisture > 0.75f)
            return TileType.Wetland;
        if (moisture > 0.55f)
            return TileType.Forest;
        if (moisture > 0.35f)
            return TileType.Grass;
        if (moisture > 0.2f)
            return TileType.Sand;
        return TileType.Arid;
    }

    /// <summary>
    /// Post-processing pass to add terrain landmarks: oases, clearings, and caves.
    /// Lakes are now handled by the RiverMapper flow system, so the landmark pass
    /// focuses on non-hydrological features only.
    /// </summary>
    private void ApplyLandmarks(Chunk chunk, int worldOffsetX, int worldOffsetY)
    {
        for (int localY = 0; localY < chunk.Size; localY++)
        {
            for (int localX = 0; localX < chunk.Size; localX++)
            {
                int worldX = worldOffsetX + localX;
                int worldY = worldOffsetY + localY;

                var currentTile = chunk.GetTile(localX, localY);
                float lmNoise = _landmarkNoise.GetNoise2D(worldX, worldY);

                // --- Oases: small grass/water patches in desert ---
                if (currentTile == TileType.Arid || currentTile == TileType.Sand)
                {
                    float moisture = (_moistureNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;
                    if (lmNoise > 0.7f && moisture > 0.35f)
                    {
                        chunk.SetTile(localX, localY, TileType.Grass);
                        continue;
                    }
                }

                // --- Forest Clearings: grass gaps inside dense forest ---
                if (currentTile == TileType.Forest)
                {
                    if (lmNoise < -0.65f)
                    {
                        chunk.SetTile(localX, localY, TileType.Grass);
                        continue;
                    }
                }

                // --- Jungle Clearings: savanna patches inside jungle ---
                if (currentTile == TileType.Jungle)
                {
                    if (lmNoise < -0.7f)
                    {
                        chunk.SetTile(localX, localY, TileType.Savanna);
                        continue;
                    }
                }

                // --- Surface Caves: walkable grass centers in mountain edges ---
                if (currentTile == TileType.Mountain)
                {
                    float elevation = (_elevationNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;
                    if (lmNoise > 0.75f && elevation < 0.85f)
                    {
                        chunk.SetTile(localX, localY, TileType.Grass);
                        continue;
                    }
                }
            }
        }
    }
}
