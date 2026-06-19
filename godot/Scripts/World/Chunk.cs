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
    // True while any grazeable tile is below its biome nutrition cap. Lets TileRegenerationSystem
    // skip the full per-tile scan for fully-regenerated chunks (the vast majority in a large,
    // sparse world). Set on consumption; cleared by a regen pass that finds nothing left to top up.
    private bool _hasDepleted = true;
    private readonly float[,] _elevation;
    private readonly float[,] _moisture;
    private readonly float[,] _temperature;

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
        _moisture = new float[size, size];
        _temperature = new float[size, size];
    }

    /// <summary>
    /// Initialize nutrition values after terrain generation. Each grazeable tile starts full at
    /// its biome's nutrition cap (lush grass = 1.0, arid/tundra much lower); others at 0.
    /// </summary>
    public void InitializeNutrition()
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                _nutrition[x, y] = _tiles[x, y].NutritionCap();
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
            // Reset nutrition to the new biome's cap when the tile type changes (full for the
            // new biome, so not depleted relative to its own cap).
            _nutrition[localX, localY] = type.NutritionCap();
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

    /// <summary>Get the moisture parameter (0–1) at a local vertex position.</summary>
    public float GetMoisture(int localX, int localY)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return 0f;
        return _moisture[localX, localY];
    }

    /// <summary>Store the moisture parameter at a local vertex position (chunk generation).</summary>
    public void SetMoisture(int localX, int localY, float value)
    {
        if (localX >= 0 && localX < Size && localY >= 0 && localY < Size)
            _moisture[localX, localY] = value;
    }

    /// <summary>Get the temperature parameter (0–1) at a local vertex position.</summary>
    public float GetTemperature(int localX, int localY)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return 0f;
        return _temperature[localX, localY];
    }

    /// <summary>Store the temperature parameter at a local vertex position (chunk generation).</summary>
    public void SetTemperature(int localX, int localY, float value)
    {
        if (localX >= 0 && localX < Size && localY >= 0 && localY < Size)
            _temperature[localX, localY] = value;
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
        if (consumed > 0f) _hasDepleted = true; // tile is now below max — needs regen
        return consumed;
    }

    /// <summary>
    /// Add nutrition to a grazeable tile (decomposing remains enriching the soil),
    /// clamped to <see cref="MaxNutrition"/>. No effect on non-grazeable tiles.
    /// </summary>
    public void AddNutrition(int localX, int localY, float amount)
    {
        if (localX < 0 || localX >= Size || localY < 0 || localY >= Size)
            return;
        if (!_tiles[localX, localY].IsGrazeable())
            return;
        _nutrition[localX, localY] = MathF.Min(MaxNutrition, _nutrition[localX, localY] + amount);
    }

    /// <summary>
    /// Regenerate nutrition for all grazeable tiles in this chunk.
    /// Called periodically by TileRegenerationSystem.
    /// <param name="tickMultiplier">Number of ticks since last regeneration (rate scaled accordingly).</param>
    /// </summary>
    public void RegenerateNutrition(int tickMultiplier = 1)
    {
        if (!_hasDepleted) return; // fully topped up — nothing to do (skips the whole scan)

        float rate = RegenerationRate * tickMultiplier;
        bool anyStillDepleted = false;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                // Regenerate only up to the tile's biome cap — arid/tundra soil tops out low.
                float cap = _tiles[x, y].NutritionCap();
                if (cap > 0f && _nutrition[x, y] < cap)
                {
                    _nutrition[x, y] = MathF.Min(cap, _nutrition[x, y] + rate);
                    if (_nutrition[x, y] < cap) anyStillDepleted = true;
                }
            }
        }
        _hasDepleted = anyStillDepleted;
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
