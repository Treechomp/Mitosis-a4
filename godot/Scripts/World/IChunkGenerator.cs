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

    /// <summary>
    /// The moisture this tile would have in an UNTOUCHED world — what the generator would produce
    /// for these coordinates if nothing had ever terraformed them.
    ///
    /// This is the reference every measure of "how far has the world been pushed from itself"
    /// needs, and it is recomputable rather than stored: worldgen is a pure function of
    /// coordinates plus fixed noise state, so the pristine value of any tile can be recovered at
    /// any time. Storing a second full-world moisture field would cost megabytes to answer a
    /// question that can be asked on demand.
    ///
    /// Also the target of <c>TerraformDirection.Restore</c>, which is why it lives on the
    /// interface rather than on TerrainGenerator alone — a scenario world has a pristine state
    /// too (the terrain the scenario authored), and restoration has to mean the same thing there.
    /// </summary>
    float PristineMoisture(int worldX, int worldY);
}
