using System;
using Mitosis.World;

namespace Mitosis.Testing;

/// <summary>
/// Chunk generator for test scenarios: instead of the noise worldgen pipeline, the whole world
/// is an exactly-specified tile map — a base fill, optional shape ops, and an optional ASCII
/// map — flattened to a single land elevation. Each tile's moisture/temperature parameters are
/// set to the canonical values for its type (<see cref="ScenarioTileParams"/>) so terraforming,
/// re-classification, and the continuous colour palette behave exactly as they would on a
/// generated world. Nutrition is initialised by the normal <c>Chunk.InitializeNutrition</c>
/// path when the chunk is created.
/// </summary>
public sealed class ScenarioTerrainGenerator : IChunkGenerator
{
    private readonly TileType[,] _tiles;
    private readonly int _worldSizeTiles;
    private readonly float _landElevation;

    public ScenarioTerrainGenerator(TestScenario scenario, int chunkSize)
    {
        _worldSizeTiles = scenario.WorldSizeTiles(chunkSize);
        _landElevation = ScenarioTileParams.ClampLandElevation(scenario.LandElevation);
        _tiles = new TileType[_worldSizeTiles, _worldSizeTiles];

        // 1. Base fill.
        for (int y = 0; y < _worldSizeTiles; y++)
            for (int x = 0; x < _worldSizeTiles; x++)
                _tiles[x, y] = scenario.BaseTile;

        // 2. Shape ops, in file order (later ops paint over earlier ones).
        foreach (var op in scenario.TerrainOps)
            ApplyOp(op);

        // 3. ASCII map (painted last, so it wins where it overlaps shapes).
        ApplyMap(scenario);
    }

    private void ApplyOp(TerrainOp op)
    {
        switch (op.Kind)
        {
            case TerrainOp.Shape.Rect:
                for (int y = op.B; y < op.B + op.D; y++)
                    for (int x = op.A; x < op.A + op.C; x++)
                        SetTileSafe(x, y, op.Tile);
                break;

            case TerrainOp.Shape.Circle:
            {
                int r = op.C;
                for (int y = op.B - r; y <= op.B + r; y++)
                    for (int x = op.A - r; x <= op.A + r; x++)
                    {
                        int dx = x - op.A, dy = y - op.B;
                        if (dx * dx + dy * dy <= r * r)
                            SetTileSafe(x, y, op.Tile);
                    }
                break;
            }

            case TerrainOp.Shape.Band:
            {
                bool xAxis = op.A == 0; // band spans the full other axis
                int from = Math.Min(op.B, op.C);
                int to = Math.Max(op.B, op.C);
                for (int major = from; major < to; major++)
                    for (int minor = 0; minor < _worldSizeTiles; minor++)
                        SetTileSafe(xAxis ? major : minor, xAxis ? minor : major, op.Tile);
                break;
            }
        }
    }

    private void ApplyMap(TestScenario scenario)
    {
        for (int row = 0; row < scenario.MapRows.Count; row++)
        {
            string line = scenario.MapRows[row];
            for (int col = 0; col < line.Length; col++)
            {
                char c = line[col];
                if (c == ' ') continue; // transparent — keep whatever is underneath
                if (scenario.Legend.TryGetValue(c, out var tile))
                    SetTileSafe(scenario.MapOriginX + col, scenario.MapOriginY + row, tile);
                else
                    scenario.Warnings.Add($"[map] char '{c}' at row {row}, col {col} not in legend — skipped");
            }
        }
    }

    private void SetTileSafe(int x, int y, TileType tile)
    {
        if (x >= 0 && x < _worldSizeTiles && y >= 0 && y < _worldSizeTiles)
            _tiles[x, y] = tile;
    }

    /// <summary>
    /// A scenario world's pristine moisture is the canonical value for the tile the scenario
    /// AUTHORED at these coordinates — the map as written, before any terraforming moved it.
    /// The authored tile map is kept for the life of the generator, so this is a lookup rather
    /// than a re-derivation.
    /// </summary>
    public float PristineMoisture(int worldX, int worldY)
    {
        var tile = (worldX >= 0 && worldY >= 0 && worldX < _worldSizeTiles && worldY < _worldSizeTiles)
            ? _tiles[worldX, worldY] : TileType.DeepWater;
        var (_, moisture, _) = ScenarioTileParams.For(tile, _landElevation);
        return moisture;
    }

    public void GenerateChunk(Chunk chunk)
    {
        int worldOffsetX = chunk.ChunkX * chunk.Size;
        int worldOffsetY = chunk.ChunkY * chunk.Size;

        for (int localY = 0; localY < chunk.Size; localY++)
        {
            for (int localX = 0; localX < chunk.Size; localX++)
            {
                int wx = worldOffsetX + localX;
                int wy = worldOffsetY + localY;
                var tile = (wx < _worldSizeTiles && wy < _worldSizeTiles)
                    ? _tiles[wx, wy] : TileType.DeepWater;

                var (elevation, moisture, temperature) = ScenarioTileParams.For(tile, _landElevation);
                chunk.SetElevation(localX, localY, elevation);
                chunk.SetMoisture(localX, localY, moisture);
                chunk.SetTemperature(localX, localY, temperature);
                chunk.SetTile(localX, localY, tile);
            }
        }
    }
}
