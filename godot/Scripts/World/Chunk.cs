using Godot;

namespace Mitosis.World;

/// <summary>
/// A chunk of terrain tiles.
/// </summary>
public sealed class Chunk
{
    public int ChunkX { get; }
    public int ChunkY { get; }
    public int Size { get; }

    private readonly TileType[,] _tiles;

    public Chunk(int chunkX, int chunkY, int size = 32)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
        Size = size;
        _tiles = new TileType[size, size];
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
            _tiles[localX, localY] = type;
    }

    public bool IsWalkable(int localX, int localY)
    {
        return GetTile(localX, localY).IsWalkable();
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
            _ => new Color(1f, 0f, 1f) // Magenta for unknown
        };
    }
}
