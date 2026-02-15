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
                _nutrition[x, y] = _tiles[x, y].IsGrazeable() ? MaxNutrition : 0f;
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
            _nutrition[localX, localY] = type.IsGrazeable() ? MaxNutrition : 0f;
        }
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
    /// </summary>
    public void RegenerateNutrition()
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (_tiles[x, y].IsGrazeable() && _nutrition[x, y] < MaxNutrition)
                {
                    _nutrition[x, y] = MathF.Min(MaxNutrition, _nutrition[x, y] + RegenerationRate);
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
            TileType.Grass => new Color(0.3f, 0.7f, 0.3f),
            TileType.Forest => new Color(0.15f, 0.5f, 0.2f),
            TileType.Mountain => new Color(0.5f, 0.5f, 0.5f),
            TileType.River => new Color(0.15f, 0.45f, 0.75f),
            TileType.Wetland => new Color(0.1f, 0.45f, 0.3f),
            TileType.Arid => new Color(0.75f, 0.6f, 0.35f),
            TileType.Tundra => new Color(0.78f, 0.82f, 0.85f),   // Pale icy blue-gray
            TileType.Ice => new Color(0.85f, 0.92f, 0.97f),      // Near-white ice
            TileType.Savanna => new Color(0.7f, 0.75f, 0.3f),    // Dry yellowish-green
            TileType.Jungle => new Color(0.05f, 0.4f, 0.12f),    // Deep dark green
            TileType.Reef => new Color(0.25f, 0.55f, 0.65f),     // Teal shallow water
            TileType.Lava => new Color(0.8f, 0.25f, 0.05f),      // Bright orange-red
            _ => new Color(1f, 0f, 1f) // Magenta for unknown
        };
    }
}
