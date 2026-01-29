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

    public TerrainGenerator(int seed = 42)
    {
        _seed = seed;

        // Elevation noise (larger features)
        _elevationNoise = new FastNoiseLite();
        _elevationNoise.SetNoiseType(FastNoiseLite.NoiseTypeEnum.OpenSimplex2);
        _elevationNoise.SetSeed(seed);
        _elevationNoise.SetFrequency(0.02f);
        _elevationNoise.SetFractalType(FastNoiseLite.FractalTypeEnum.Fbm);
        _elevationNoise.SetFractalOctaves(4);

        // Moisture noise (smaller features)
        _moistureNoise = new FastNoiseLite();
        _moistureNoise.SetNoiseType(FastNoiseLite.NoiseTypeEnum.OpenSimplex2);
        _moistureNoise.SetSeed(seed + 1000);
        _moistureNoise.SetFrequency(0.03f);
        _moistureNoise.SetFractalType(FastNoiseLite.FractalTypeEnum.Fbm);
        _moistureNoise.SetFractalOctaves(3);
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

        // Forest or grass based on moisture
        if (moisture > 0.55f)
            return TileType.Forest;

        return TileType.Grass;
    }
}
