namespace Mitosis.World;

/// <summary>
/// Fills a chunk's tiles and terrain parameters (elevation/moisture/temperature).
/// <see cref="TerrainGenerator"/> is the noise-based implementation the game uses;
/// the test-scene branch plugs in <c>ScenarioTerrainGenerator</c> to build small,
/// exactly-specified worlds for system tests (see Scripts/Testing/).
/// </summary>
public interface IChunkGenerator
{
    void GenerateChunk(Chunk chunk);
}
