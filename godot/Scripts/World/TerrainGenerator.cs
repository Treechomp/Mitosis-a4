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
    private const float WarpAmplitude = 30f;

    // Flow-based river system (pre-computed before chunk generation)
    private RiverMapper? _riverMapper;
    private int _worldSizeTiles = 512; // Updated by PrecomputeRivers

    public TerrainGenerator(int seed)
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
        _moistureNoise.FractalOctaves = 4;   // extra octave → patchier moisture → more biome variety

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
                chunk.SetElevation(localX, localY, elevation);
                chunk.SetMoisture(localX, localY, moisture);
                chunk.SetTemperature(localX, localY, temperature);
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

    /// <summary>
    /// Classify a terrain point into a discrete <see cref="TileType"/> from its continuous
    /// climate parameters (each 0–1). Public so the renderer can detect tiles overridden
    /// away from their climate classification (rivers, landmarks, terraform) and colour them
    /// discretely while pure-climate land uses the continuous palette.
    /// </summary>
    public static TileType DetermineTileType(float elevation, float moisture, float temperature)
    {
        // ── Water ──────────────────────────────────────────────────────────────────────
        if (elevation < 0.30f)
            return TileType.DeepWater;

        if (elevation < 0.40f)
        {
            // Reefs grow in warm, clear, shallow coastal water.
            // Lowered threshold (0.70→0.62) and slightly wider elevation band so reef
            // actually appears in tropical coastlines.
            if (temperature > 0.62f && elevation > 0.33f)
                return TileType.Reef;
            return TileType.ShallowWater;
        }

        // Slightly wider beach band (0.45→0.46) for more visible sand strips.
        if (elevation < 0.46f)
            return TileType.Sand;

        // ── Mountains / high ground ─────────────────────────────────────────────────────
        if (elevation > 0.80f)
        {
            if (temperature < 0.22f) return TileType.Ice;                   // frozen peaks
            if (temperature > 0.55f && moisture < 0.32f) return TileType.Lava; // volcanic
            return TileType.Mountain;
        }

        // Sub-alpine ice cap: cold enough at altitude that ground stays frozen.
        if (elevation > 0.72f && temperature < 0.26f)
            return TileType.Ice;

        // ── Arctic / polar (very cold) ─────────────────────────────────────────────────
        // Expanded boundary 0.20→0.22 so ice sheets are slightly larger.
        if (temperature < 0.22f)
        {
            if (moisture > 0.55f || elevation < 0.57f) return TileType.Ice;
            return TileType.Tundra;
        }

        // ── Cold temperate ─────────────────────────────────────────────────────────────
        // Expanded 0.35→0.40: the cold band now claims 18% of the temperature range
        // instead of 15%, giving Taiga/Steppe/Tundra meaningful world coverage.
        // Added polar-desert Ice at the dry extreme and Bog at the very wet extreme.
        if (temperature < 0.40f)
        {
            if (moisture > 0.72f) return TileType.Wetland;
            if (moisture > 0.54f) return TileType.Taiga;
            if (moisture > 0.33f) return TileType.Steppe;
            if (moisture > 0.17f) return TileType.Tundra;
            return TileType.Ice;  // polar desert — very dry, very cold
        }

        // ── Tropical (hot) ─────────────────────────────────────────────────────────────
        if (temperature > 0.75f)
        {
            if (moisture > 0.62f) return TileType.Jungle;
            if (moisture > 0.40f) return TileType.Savanna;
            if (moisture > 0.26f) return TileType.Dirt;
            if (moisture > 0.15f) return TileType.Sand;
            return TileType.Arid;
        }

        // ── Warm temperate ─────────────────────────────────────────────────────────────
        // Jungle removed: temp 0.60–0.75 is too cool for true jungle; dense moisture
        // here becomes Wetland (temperate rainforest / swamp) instead.
        if (temperature > 0.60f)
        {
            if (moisture > 0.70f) return TileType.Wetland;
            if (moisture > 0.54f) return TileType.Forest;
            if (moisture > 0.36f) return temperature > 0.67f ? TileType.Savanna : TileType.Grass;
            if (moisture > 0.24f) return TileType.Shrubland;
            if (moisture > 0.13f) return TileType.Dirt;
            return TileType.Arid;
        }

        // ── Temperate ──────────────────────────────────────────────────────────────────
        // Bog threshold 0.82→0.76 so bogs actually appear.
        // Steppe added at the dry end (0.11–0.22) — previously invisible in temperate.
        if (moisture > 0.76f) return TileType.Bog;
        if (moisture > 0.65f) return TileType.Wetland;
        if (moisture > 0.50f) return TileType.Forest;
        if (moisture > 0.33f) return TileType.Grass;
        if (moisture > 0.22f) return TileType.Shrubland;
        if (moisture > 0.11f) return TileType.Steppe;
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
                    // Sample moisture with the same domain warp used in classification,
                    // so the oasis test matches the moisture that produced this tile.
                    float oWarpX = _warpNoiseX.GetNoise2D(worldX, worldY) * WarpAmplitude;
                    float oWarpY = _warpNoiseY.GetNoise2D(worldX, worldY) * WarpAmplitude;
                    float moisture = (_moistureNoise.GetNoise2D(worldX + oWarpX, worldY + oWarpY) + 1f) * 0.5f;
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

                // --- Taiga Clearings: cold meadows (steppe patches) inside taiga ---
                if (currentTile == TileType.Taiga)
                {
                    if (lmNoise < -0.68f)
                    {
                        chunk.SetTile(localX, localY, TileType.Steppe);
                        continue;
                    }
                }

                // --- Jungle Clearings: savanna patches inside jungle ---
                if (currentTile == TileType.Jungle)
                {
                    if (lmNoise < -0.70f)
                    {
                        chunk.SetTile(localX, localY, TileType.Savanna);
                        continue;
                    }
                }

                // --- Permafrost spots: ice patches within tundra ---
                if (currentTile == TileType.Tundra)
                {
                    if (lmNoise > 0.72f)
                    {
                        chunk.SetTile(localX, localY, TileType.Ice);
                        continue;
                    }
                }

                // --- Shrubland dry patches: steppe outcrops in shrubland ---
                if (currentTile == TileType.Shrubland)
                {
                    if (lmNoise > 0.74f)
                    {
                        chunk.SetTile(localX, localY, TileType.Steppe);
                        continue;
                    }
                }

                // --- Surface Caves: walkable grass centers in mountain edges ---
                if (currentTile == TileType.Mountain)
                {
                    // Use the actual stored (domain-warped) elevation for this tile rather
                    // than re-sampling unwarped noise, so caves sit on genuine low mountains.
                    float elevation = chunk.GetElevation(localX, localY);
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
