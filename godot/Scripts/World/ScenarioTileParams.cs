using System;

namespace Mitosis.World;

/// <summary>
/// Canonical terrain parameters (elevation, moisture, temperature) for each tile type, used
/// when terrain is authored directly (test scenarios, in-scene painting) instead of generated
/// from noise. Values are chosen so <see cref="TerrainGenerator.DetermineTileType"/> maps them
/// BACK to the same tile type wherever the classifier can produce it — that keeps the
/// continuous colour palette, terraforming (which re-classifies from params), and the
/// classification-mismatch check in the renderer all consistent with the authored tile.
///
/// Exceptions that classification can never produce (Sand and River are post-passes in real
/// worldgen) get nearby-band params; they render with their discrete tile colour (the renderer
/// detects the mismatch) and reclassify into a neighbouring biome if terraformed.
/// </summary>
public static class ScenarioTileParams
{
    /// <summary>
    /// Land elevation must stay inside this band: above the shore/water cutoffs (0.40) and
    /// below the sub-alpine ice / mountain thresholds (0.72+), so classification is driven by
    /// moisture/temperature alone.
    /// </summary>
    public const float MinLandElevation = 0.42f;
    public const float MaxLandElevation = 0.70f;

    /// <summary>Clamp a scenario's requested land elevation into the safe classification band.</summary>
    public static float ClampLandElevation(float elevation)
        => Math.Clamp(elevation, MinLandElevation, MaxLandElevation);

    /// <summary>
    /// Canonical (elevation, moisture, temperature) for a tile type. <paramref name="landElevation"/>
    /// is used for ordinary land tiles (pass the scenario's flat land height, pre-clamped);
    /// water and mountains carry fixed elevations that place them in the right classifier band.
    /// </summary>
    public static (float elevation, float moisture, float temperature) For(
        TileType tile, float landElevation)
    {
        float e = ClampLandElevation(landElevation);
        return tile switch
        {
            // Water: elevations below the land cutoff; the renderer flattens the sea at 0.40
            // and colours by depth. Reef needs warm water.
            TileType.DeepWater    => (0.15f, 0.50f, 0.50f),
            TileType.ShallowWater => (0.36f, 0.50f, 0.50f),
            TileType.Reef         => (0.36f, 0.50f, 0.75f),
            // River is a hydrology post-pass in real worldgen — land-level elevation, discrete
            // colour via the renderer's mismatch check.
            TileType.River        => (e, 0.50f, 0.50f),

            // High ground.
            TileType.Mountain     => (0.85f, 0.50f, 0.40f),
            TileType.Lava         => (0.85f, 0.20f, 0.70f),

            // Cold band (temperature < 0.40).
            TileType.Ice          => (e, 0.60f, 0.15f),
            TileType.Tundra       => (e, 0.25f, 0.30f),
            TileType.Steppe       => (e, 0.40f, 0.30f),
            TileType.Taiga        => (e, 0.60f, 0.30f),

            // Temperate band.
            TileType.Bog          => (e, 0.85f, 0.50f),
            TileType.Wetland      => (e, 0.70f, 0.50f),
            TileType.Forest       => (e, 0.55f, 0.50f),
            TileType.Grass        => (e, 0.40f, 0.50f),
            TileType.Shrubland    => (e, 0.28f, 0.50f),
            TileType.Arid         => (e, 0.05f, 0.50f),

            // Warm/hot band.
            TileType.Dirt         => (e, 0.18f, 0.65f),
            TileType.Savanna      => (e, 0.50f, 0.75f),
            TileType.Jungle       => (e, 0.70f, 0.75f),

            // Beach sand is a shore post-pass in real worldgen; closest dry band.
            TileType.Sand         => (e, 0.08f, 0.55f),

            _ => (e, 0.40f, 0.50f)
        };
    }
}
