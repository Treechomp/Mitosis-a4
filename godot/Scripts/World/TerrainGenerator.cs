using System;
using Godot;

namespace Mitosis.World;

/// <summary>
/// Generates terrain using simplex noise.
/// </summary>
public sealed class TerrainGenerator
{
    private readonly int _seed;
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _moistureNoise;
    private readonly FastNoiseLite _riverNoise;

    public TerrainGenerator(int seed = 42)
    {
        _seed = seed;

        // Elevation noise (larger features)
        _elevationNoise = new FastNoiseLite();
        _elevationNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _elevationNoise.Seed = seed;
        _elevationNoise.Frequency = 0.02f;
        _elevationNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _elevationNoise.FractalOctaves = 4;

        // Moisture noise (smaller features)
        _moistureNoise = new FastNoiseLite();
        _moistureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _moistureNoise.Seed = seed + 1000;
        _moistureNoise.Frequency = 0.03f;
        _moistureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _moistureNoise.FractalOctaves = 3;

        // River noise - low frequency for wide, meandering paths
        _riverNoise = new FastNoiseLite();
        _riverNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _riverNoise.Seed = seed + 3000;
        _riverNoise.Frequency = 0.012f;
        _riverNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _riverNoise.FractalOctaves = 2;
    }

    /// <summary>
    /// Generate terrain for a chunk.
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

                // Get noise values (-1 to 1, normalize to 0-1)
                float elevation = (_elevationNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;
                float moisture = (_moistureNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;

                TileType tile = DetermineTileType(elevation, moisture);

                // Carve rivers and add wetland banks on land tiles
                if (tile.IsSpawnable())
                {
                    float riverVal = _riverNoise.GetNoise2D(worldX, worldY);
                    float absRiver = MathF.Abs(riverVal);

                    // River width varies with elevation (wider in valleys)
                    float threshold = 0.018f + (0.7f - elevation) * 0.02f;
                    threshold = Math.Clamp(threshold, 0.01f, 0.04f);

                    if (absRiver < threshold)
                    {
                        tile = TileType.River;
                    }
                    else if (absRiver < threshold * 2.5f)
                    {
                        // Wetland fringe along river banks
                        tile = TileType.Wetland;
                    }
                }

                chunk.SetTile(localX, localY, tile);
            }
        }
    }

    private static TileType DetermineTileType(float elevation, float moisture)
    {
        // Deep water
        if (elevation < 0.3f)
            return TileType.DeepWater;

        // Shallow water
        if (elevation < 0.4f)
            return TileType.ShallowWater;

        // Beach/sand
        if (elevation < 0.45f)
            return TileType.Sand;

        // Mountains at high elevation
        if (elevation > 0.8f)
            return TileType.Mountain;

        // Land tiles: moisture spectrum determines type
        // Very high moisture -> Wetland
        if (moisture > 0.75f)
            return TileType.Wetland;

        // High moisture -> Forest
        if (moisture > 0.55f)
            return TileType.Forest;

        // Moderate moisture -> Grass
        if (moisture > 0.35f)
            return TileType.Grass;

        // Low moisture -> Sand
        if (moisture > 0.2f)
            return TileType.Sand;

        // Very low moisture -> Arid
        return TileType.Arid;
    }
}
