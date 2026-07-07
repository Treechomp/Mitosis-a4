namespace Mitosis.World;

/// <summary>
/// Tunable terrain-generation parameters, surfaced as GameManager [Export] fields so the look
/// can be adjusted in the editor without code changes. Defaults reproduce the original terrain
/// plus modest surface detail. See docs/3d-terrain-plan.md (terrain shape).
/// </summary>
public sealed class TerrainSettings
{
    // --- Base shape (used for biome classification) ---
    /// <summary>Base elevation frequency. Lower = larger landmasses/features.</summary>
    public float ElevationFrequency = 0.012f;
    /// <summary>Moisture-field frequency. Must stay in scale with the elevation/temperature
    /// fields: too high and every climate zone contains the full wet↔dry spectrum in small
    /// patches, so no coherent desert/rainforest/bog REGION can ever form — biomes come out
    /// as fine-grained speckle. Lower = large humid belts and arid basins.</summary>
    public float MoistureFrequency = 0.003f;
    /// <summary>Contrast stretch applied to the raw moisture noise around 0.5. FBM output
    /// concentrates near the middle, starving the classification extremes (Arid needs &lt; ~0.3,
    /// Bog &gt; 0.76). 1 = raw noise; ~1.25 reaches real deserts and real bogs.</summary>
    public float MoistureContrast = 1.25f;
    /// <summary>Domain-warp amplitude in tiles. Higher = more swirled/stretched ("fabric")
    /// boundaries; lower = straighter, calmer shapes.</summary>
    public float WarpAmplitude = 12f;

    // --- Ridged mountain ranges (part of the BASE elevation: classification, temperature,
    //     rivers and movement all see them — ranges classify as Mountain/Ice and shed rivers) ---
    /// <summary>Ridge noise frequency. Lower = longer, larger ridgelines.</summary>
    public float RidgeFrequency = 0.010f;
    /// <summary>Max elevation (0..1) a ridge crest adds on top of the base shape. Ridges only
    /// rise from uplands inside orogeny belts, so this is the crest height of major ranges.
    /// 0 disables ridges entirely (restores the pure-FBM landscape).</summary>
    public float RidgeAmplitude = 0.18f;
    /// <summary>Orogeny-belt mask frequency. Lower = fewer, larger mountain-range regions.</summary>
    public float OrogenyFrequency = 0.0035f;

    // --- Terraced cliffs (applied to the STORED/rendered elevation only, like surface detail:
    //     biome classification and river tracing use the base shape, so cliffs are relief —
    //     visible mesas/bluffs whose steep risers slow movement via slope resistance) ---
    /// <summary>Cliff-region mask frequency. Lower = fewer, larger mesa/bluff regions.</summary>
    public float CliffFrequency = 0.005f;
    /// <summary>How strongly terracing is applied where the cliff mask is active (0 = off,
    /// 1 = fully stepped). Also scales the riser height a step face can reach.</summary>
    public float CliffStrength = 1.0f;
    /// <summary>Elevation (0..1) per terrace step. Bigger steps = taller but rarer cliff faces
    /// (at low elevation frequencies a large step yields only a couple of terrace lines per
    /// landmass). Keep below the 0.18 spawn-slope / 0.28 movement-cliff thresholds.</summary>
    public float CliffStepHeight = 0.08f;

    // --- Surface detail (added to the rendered/stored elevation only, NOT to classification,
    //     so biome boundaries stay on the base shape; gives relief + a little slope) ---
    /// <summary>Detail noise frequency (finer texture as it rises).</summary>
    public float DetailFrequency = 0.045f;
    /// <summary>Detail FBM octaves.</summary>
    public int DetailOctaves = 3;
    /// <summary>Max elevation (0..1 scale) added by detail. Keep well below the 0.28 cliff
    /// threshold; larger values add more relief but also more slope (slower movement uphill).</summary>
    public float DetailAmplitude = 0.035f;

    // --- Roughness mask: a low-frequency field that modulates detail amplitude so some regions
    //     are rugged and others smooth (breaks the uniform "everywhere the same" look) ---
    /// <summary>Roughness-mask frequency. Lower = larger rugged/smooth regions.</summary>
    public float RoughnessFrequency = 0.006f;
    /// <summary>Minimum detail fraction in the smoothest regions (0 = some areas fully flat,
    /// 1 = uniform detail everywhere).</summary>
    public float RoughnessFloor = 0.15f;
}
