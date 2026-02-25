using System;
using Godot;

namespace Mitosis.World;

/// <summary>
/// A chunk of terrain tiles with per-tile nutrition for grazing depletion and regrowth.
/// </summary>
public sealed class Chunk
{
    public int ChunkX { get; }
    public int ChunkY { get; }
    public int Size { get; }

    private readonly TileType[,] _tiles;
    private readonly float[,] _nutrition;
    private readonly float[,] _elevation;

    /// <summary>
    /// Maximum nutrition a tile can hold (1.0 = fully nourished).
    /// </summary>
    public const float MaxNutrition = 1.0f;

    /// <summary>
    /// Rate at which depleted tiles regenerate nutrition per tick.
    /// </summary>
    public const float RegenerationRate = 0.0005f;

    public Chunk(int chunkX, int chunkY, int size = 32)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
        Size = size;
        _tiles = new TileType[size, size];
        _nutrition = new float[size, size];
        _elevation = new float[size, size];
    }

    /// <summary>
    /// Initialize nutrition values after terrain generation.
    /// Grazeable tiles start at full nutrition; others at 0.
    /// </summary>
    public void InitializeNutrition()
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                var tile = _tiles[x, y];
                if (!tile.IsGrazeable())
                    _nutrition[x, y] = 0f;
                else if (tile == TileType.Tundra)
                    _nutrition[x, y] = 0.2f;  // Sparse arctic vegetation
                else if (tile == TileType.Arid)
                    _nutrition[x, y] = 0.15f; // Sparse desert scrub
                else
                    _nutrition[x, y] = MaxNutrition;
            }
        }
    }

    public TileType GetTile(int localX, int localY)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return TileType.DeepWater;
        return _tiles[localX, localY];
    }

    public void SetTile(int localX, int localY, TileType type)
    {
        if (localX >= 0 && localX < Size && localY >= 0 && localY < Size)
        {
            _tiles[localX, localY] = type;
            // Reset nutrition when tile type changes
            if (!type.IsGrazeable())
                _nutrition[localX, localY] = 0f;
            else if (type == TileType.Tundra)
                _nutrition[localX, localY] = 0.2f;
            else if (type == TileType.Arid)
                _nutrition[localX, localY] = 0.15f;
            else
                _nutrition[localX, localY] = MaxNutrition;
        }
    }

    /// <summary>
    /// Get the raw elevation value (0–1) at a local vertex position.
    /// </summary>
    public float GetElevation(int localX, int localY)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return 0f;
        return _elevation[localX, localY];
    }

    /// <summary>
    /// Store the raw elevation value at a local vertex position.
    /// Called by TerrainGenerator during chunk generation.
    /// </summary>
    public void SetElevation(int localX, int localY, float value)
    {
        if (localX >= 0 && localX < Size && localY >= 0 && localY < Size)
            _elevation[localX, localY] = value;
    }

    /// <summary>
    /// Get the nutrition level at a local tile position (0.0 = depleted, 1.0 = full).
    /// </summary>
    public float GetNutrition(int localX, int localY)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return 0f;
        return _nutrition[localX, localY];
    }

    /// <summary>
    /// Reduce nutrition at a tile by the given amount. Returns actual amount consumed.
    /// </summary>
    public float ConsumeNutrition(int localX, int localY, float amount)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return 0f;

        float available = _nutrition[localX, localY];
        float consumed = MathF.Min(available, amount);
        _nutrition[localX, localY] = available - consumed;
        return consumed;
    }

    /// <summary>
    /// Regenerate nutrition for all grazeable tiles in this chunk.
    /// Called periodically by TileRegenerationSystem.
    /// <param name="tickMultiplier">Number of ticks since last regeneration (rate scaled accordingly).</param>
    /// </summary>
    public void RegenerateNutrition(int tickMultiplier = 1)
    {
        float rate = RegenerationRate * tickMultiplier;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (_tiles[x, y].IsGrazeable() && _nutrition[x, y] < MaxNutrition)
                {
                    _nutrition[x, y] = MathF.Min(MaxNutrition, _nutrition[x, y] + rate);
                }
            }
        }
    }

    public bool IsWalkable(int localX, int localY)
    {
        return GetTile(localX, localY).IsWalkable();
    }

    /// <summary>
    /// Check if a tile is safe to spawn creatures on.
    /// </summary>
    public bool IsSpawnable(int localX, int localY)
    {
        return GetTile(localX, localY).IsSpawnable();
    }

    /// <summary>
    /// Get the biome type at a local tile position.
    /// </summary>
    public BiomeType GetBiome(int localX, int localY)
    {
        return GetTile(localX, localY).GetTypicalBiome();
    }

    /// <summary>
    /// Get the color for rendering a tile type.
    /// </summary>
    public static Color GetTileColor(TileType tile)
    {
        return tile switch
        {
            TileType.DeepWater => new Color(0.1f, 0.2f, 0.5f),
            TileType.ShallowWater => new Color(0.2f, 0.4f, 0.7f),
            TileType.Sand => new Color(0.9f, 0.85f, 0.6f),
            TileType.Dirt => new Color(0.55f, 0.42f, 0.25f),       // Earth brown
            TileType.Grass => new Color(0.3f, 0.7f, 0.3f),
            TileType.Shrubland => new Color(0.52f, 0.58f, 0.32f),  // Olive/dusty green
            TileType.Forest => new Color(0.15f, 0.5f, 0.2f),
            TileType.Taiga => new Color(0.15f, 0.4f, 0.32f),      // Dark blue-green
            TileType.Mountain => new Color(0.5f, 0.5f, 0.5f),
            TileType.River => new Color(0.15f, 0.45f, 0.75f),
            TileType.Wetland => new Color(0.1f, 0.45f, 0.3f),
            TileType.Bog => new Color(0.22f, 0.32f, 0.18f),       // Dark murky green
            TileType.Arid => new Color(0.75f, 0.6f, 0.35f),
            TileType.Tundra => new Color(0.78f, 0.82f, 0.85f),    // Pale icy blue-gray
            TileType.Steppe => new Color(0.62f, 0.68f, 0.42f),    // Pale yellow-green
            TileType.Ice => new Color(0.85f, 0.92f, 0.97f),       // Near-white ice
            TileType.Savanna => new Color(0.7f, 0.75f, 0.3f),     // Dry yellowish-green
            TileType.Jungle => new Color(0.05f, 0.4f, 0.12f),     // Deep dark green
            TileType.Reef => new Color(0.25f, 0.55f, 0.65f),      // Teal shallow water
            TileType.Lava => new Color(0.8f, 0.25f, 0.05f),       // Bright orange-red
            _ => new Color(1f, 0f, 1f) // Magenta for unknown
        };
    }
}
