using System;
using Godot;

namespace Mitosis.World;

/// <summary>
/// Generates terrain using simplex noise with domain warping, temperature gradients,
/// flow-based rivers, and landmark post-processing for diverse, biome-rich worlds.
/// </summary>
public sealed class TerrainGenerator
{
    private readonly int _seed;
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _moistureNoise;
    private readonly FastNoiseLite _temperatureNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseY;
    private readonly FastNoiseLite _landmarkNoise;
    private readonly FastNoiseLite _detailNoise;     // fine surface relief on top of base elevation
    private readonly FastNoiseLite _roughnessNoise;  // low-freq mask: where detail is strong vs flat
    private readonly FastNoiseLite _ridgeNoise;      // ridged fractal: sharp mountain-range crests
    private readonly FastNoiseLite _orogenyNoise;    // low-freq mask: where ranges form (belts)
    private readonly FastNoiseLite _cliffNoise;      // low-freq mask: terraced mesa/bluff regions

    // Domain warping amplitude (in tiles) — larger value means more organic, winding boundaries.
    private readonly float WarpAmplitude;
    // Surface-detail tuning (see TerrainSettings).
    private readonly float _detailAmplitude;
    private readonly float _roughnessFloor;
    // Ridge/cliff tuning (see TerrainSettings).
    private readonly float _ridgeAmplitude;
    private readonly float _cliffStrength;
    private readonly float _cliffStepHeight;
    // Moisture contrast stretch (see TerrainSettings.MoistureContrast).
    private readonly float _moistureContrast;

    // Flow-based river system (pre-computed before chunk generation)
    private RiverMapper? _riverMapper;
    private int _worldSizeTiles = 512; // Updated by PrecomputeRivers

    // Drainage: how strongly slope sheds climate moisture (flats hold it). See GenerateChunk.
    private const float DrainageFactor = 2.5f;
    // Shore pass: beach width in tiles from the waterline (chamfer distance). See GenerateChunk.
    private const float BeachWidth = 2f;

    public TerrainGenerator(int seed, TerrainSettings? settings = null)
    {
        _seed = seed;
        settings ??= new TerrainSettings();
        WarpAmplitude = settings.WarpAmplitude;
        _detailAmplitude = settings.DetailAmplitude;
        _roughnessFloor = settings.RoughnessFloor;
        _ridgeAmplitude = settings.RidgeAmplitude;
        _cliffStrength = settings.CliffStrength;
        _cliffStepHeight = settings.CliffStepHeight;
        _moistureContrast = settings.MoistureContrast;

        // Elevation noise — continent/landmass scale features
        _elevationNoise = new FastNoiseLite();
        _elevationNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _elevationNoise.Seed = seed;
        _elevationNoise.Frequency = settings.ElevationFrequency;
        _elevationNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _elevationNoise.FractalOctaves = 4;

        // Moisture noise — must be in scale with the elevation/temperature fields (see
        // TerrainSettings.MoistureFrequency); octaves add local texture inside large regions.
        _moistureNoise = new FastNoiseLite();
        _moistureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _moistureNoise.Seed = seed + 1000;
        _moistureNoise.Frequency = settings.MoistureFrequency;
        _moistureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _moistureNoise.FractalOctaves = 4;

        // Temperature noise — very large-scale regions with latitude-like gradient
        _temperatureNoise = new FastNoiseLite();
        _temperatureNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _temperatureNoise.Seed = seed + 5000;
        _temperatureNoise.Frequency = 0.005f;
        _temperatureNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _temperatureNoise.FractalOctaves = 2;

        // Domain warp noise X — distorts coordinates for organic biome boundaries
        _warpNoiseX = new FastNoiseLite();
        _warpNoiseX.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _warpNoiseX.Seed = seed + 7000;
        _warpNoiseX.Frequency = 0.008f;
        _warpNoiseX.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _warpNoiseX.FractalOctaves = 2;

        // Domain warp noise Y
        _warpNoiseY = new FastNoiseLite();
        _warpNoiseY.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _warpNoiseY.Seed = seed + 8000;
        _warpNoiseY.Frequency = 0.008f;
        _warpNoiseY.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _warpNoiseY.FractalOctaves = 2;

        // Landmark noise — used for feature placement (lakes, oases, clearings, etc.)
        _landmarkNoise = new FastNoiseLite();
        _landmarkNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _landmarkNoise.Seed = seed + 9000;
        _landmarkNoise.Frequency = 0.04f;
        _landmarkNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _landmarkNoise.FractalOctaves = 2;

        // Detail noise — fine surface relief added on top of the base elevation. Kept out of
        // classification so biome boundaries stay on the base shape.
        _detailNoise = new FastNoiseLite();
        _detailNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _detailNoise.Seed = seed + 11000;
        _detailNoise.Frequency = settings.DetailFrequency;
        _detailNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _detailNoise.FractalOctaves = settings.DetailOctaves;

        // Roughness mask — low-frequency field so some regions are rugged and others smooth,
        // breaking the uniform "wrangled fabric" look.
        _roughnessNoise = new FastNoiseLite();
        _roughnessNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _roughnessNoise.Seed = seed + 13000;
        _roughnessNoise.Frequency = settings.RoughnessFrequency;
        _roughnessNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _roughnessNoise.FractalOctaves = 2;

        // Ridge noise — ridged fractal whose crests (values near +1) form sharp, connected
        // ridgelines. Part of the BASE elevation (see SampleBaseElevation), so classification,
        // altitude cooling, and river tracing all respect the ranges.
        _ridgeNoise = new FastNoiseLite();
        _ridgeNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _ridgeNoise.Seed = seed + 15000;
        _ridgeNoise.Frequency = settings.RidgeFrequency;
        _ridgeNoise.FractalType = FastNoiseLite.FractalTypeEnum.Ridged;
        _ridgeNoise.FractalOctaves = 3;

        // Orogeny mask — very low-frequency belts deciding WHERE mountain ranges form, so
        // ridges cluster into a few real ranges instead of stippling every upland.
        _orogenyNoise = new FastNoiseLite();
        _orogenyNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _orogenyNoise.Seed = seed + 16000;
        _orogenyNoise.Frequency = settings.OrogenyFrequency;
        _orogenyNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _orogenyNoise.FractalOctaves = 2;

        // Cliff mask — low-frequency regions where the stored elevation is terraced into
        // mesa/bluff steps (visual + movement relief only; never affects classification).
        _cliffNoise = new FastNoiseLite();
        _cliffNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _cliffNoise.Seed = seed + 17000;
        _cliffNoise.Frequency = settings.CliffFrequency;
        _cliffNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _cliffNoise.FractalOctaves = 2;
    }

    /// <summary>Clamped smoothstep of t over [edge0, edge1].</summary>
    private static float SmoothStep(float edge0, float edge1, float t)
    {
        t = Math.Clamp((t - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The canonical base elevation at a world tile: domain-warped FBM plus ridged mountain
    /// ranges. This is THE authority every consumer must share — chunk classification,
    /// temperature/altitude cooling, and the RiverMapper's full-world flow map — so rivers,
    /// biomes, and ranges always agree. Surface detail and cliff terracing are rendering/
    /// movement relief added later and deliberately NOT part of this value.
    /// </summary>
    public float SampleBaseElevation(int worldX, int worldY)
    {
        float warpX = _warpNoiseX.GetNoise2D(worldX, worldY) * WarpAmplitude;
        float warpY = _warpNoiseY.GetNoise2D(worldX, worldY) * WarpAmplitude;
        return SampleBaseElevationWarped(worldX + warpX, worldY + warpY);
    }

    /// <summary>Base elevation from already-warped coordinates (chunk gen reuses its warp).</summary>
    private float SampleBaseElevationWarped(float warpedX, float warpedY)
    {
        float elevation = (_elevationNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f;

        if (_ridgeAmplitude > 0f)
        {
            // Ridged fractal: crests approach 1. Cubing sharpens so only the crest line lifts.
            float ridge = (_ridgeNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f;
            ridge = ridge * ridge * ridge;
            // Ranges form only inside orogeny belts, and only grow out of existing uplands —
            // never straight out of a sea or a plain, so coasts and lowlands stay believable.
            float belt = SmoothStep(0.55f, 0.80f, (_orogenyNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f);
            float upland = SmoothStep(0.50f, 0.64f, elevation);
            elevation += _ridgeAmplitude * ridge * belt * upland;
        }

        return Math.Clamp(elevation, 0f, 1f);
    }

    /// <summary>
    /// Pre-compute the flow-based river map for the entire world.
    /// Must be called before any chunks are generated.
    /// Uses the same elevation noise and domain warping so rivers align with terrain.
    /// </summary>
    public void PrecomputeRivers(int worldSizeTiles)
    {
        _worldSizeTiles = worldSizeTiles;
        _riverMapper = new RiverMapper(SampleBaseElevation);
        _riverMapper.Generate(worldSizeTiles, _seed);
    }

    /// <summary>
    /// Generate terrain for a chunk with domain warping and temperature-based biomes.
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

                // Apply domain warping for organic biome boundaries
                float warpX = _warpNoiseX.GetNoise2D(worldX, worldY) * WarpAmplitude;
                float warpY = _warpNoiseY.GetNoise2D(worldX, worldY) * WarpAmplitude;
                float warpedX = worldX + warpX;
                float warpedY = worldY + warpY;

                // Base elevation: read the RiverMapper's precomputed full-world map when
                // available (identical values by construction — it was built with
                // SampleBaseElevation — and guarantees rivers/biomes can never drift apart),
                // else sample directly (worlds generated without a river pre-pass).
                float elevation = _riverMapper?.GetBaseElevation(worldX, worldY)
                                  ?? SampleBaseElevationWarped(warpedX, warpedY);
                float moisture = (_moistureNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f;
                // Contrast-stretch the CLIMATE moisture around the midpoint so the wet/dry
                // extremes (Arid, Bog) actually occur — raw FBM clusters near 0.5. Applied
                // before the hydrology feedback so riparian/delta boosts aren't exaggerated.
                moisture = Math.Clamp(0.5f + (moisture - 0.5f) * _moistureContrast, 0f, 1f);

                if (_riverMapper != null)
                {
                    // Two-way hydrology→biome coupling, applied BEFORE classification so the
                    // biomes themselves respond to the generated water features:
                    // 1. Rivers/lakes wet their surroundings — wetland/bog margins in wet
                    //    climates, green riparian corridors through dry ones, broad marsh
                    //    fans at deltas.
                    moisture += _riverMapper.GetMoistureBoost(worldX, worldY);
                    // 2. Drainage — slopes shed water, flats hold it — so swamps settle into
                    //    flat lowland basins instead of scattering wherever moisture noise
                    //    peaks, and hillsides dry toward forest/scrub. Pivoted on the world's
                    //    MEASURED mean land slope (not a constant): a flat low-frequency world
                    //    must not read as "basins everywhere" and drown its deserts.
                    moisture += Math.Clamp(
                        (_riverMapper.MeanLandSlope - _riverMapper.GetSlope(worldX, worldY)) * DrainageFactor,
                        -0.20f, 0.08f);
                    moisture = Math.Clamp(moisture, 0f, 1f);
                }

                float temperature = GetTemperature(worldX, worldY, elevation);

                TileType tile = DetermineTileType(elevation, moisture, temperature);

                // Apply flow-based rivers, lakes, and wetland banks. Water overrides apply to
                // ANY dry land tile — including the Sand shore band and Ice (a river crossing an
                // arctic sheet stays continuous instead of vanishing beneath it) — but never to
                // tiles that are already water, or to Mountain/Lava. Previously this was gated on
                // IsSpawnable(), which excluded Sand/Ice and (together with the RiverMapper's old
                // 0.45 marking cutoff) severed every river from the sea at the beach.
                if (_riverMapper != null && !tile.IsWater() &&
                    tile != TileType.Reef && tile != TileType.Mountain && tile != TileType.Lava)
                {
                    if (_riverMapper.IsLake(worldX, worldY))
                    {
                        tile = TileType.ShallowWater;
                    }
                    else if (_riverMapper.IsRiver(worldX, worldY))
                    {
                        tile = TileType.River;
                    }
                    else if (_riverMapper.IsWetlandBank(worldX, worldY) && tile.IsSpawnable())
                    {
                        // Banks stay restricted to ordinary land — a Wetland bank punched into
                        // an ice sheet would read as a thaw ring around every frozen river.
                        tile = TileType.Wetland;
                    }
                    else if (elevation < 0.55f && temperature >= 0.25f &&
                             _riverMapper.GetOceanDistance(worldX, worldY) <= BeachWidth)
                    {
                        // Biome-aware shore pass (replaces the old 0.40–0.43 elevation beach
                        // band): a NARROW band measured in tiles from actual sea water, typed
                        // by local climate — marshy shores where the climate (or a river
                        // delta) is very wet, sand everywhere else. Frozen coasts get no
                        // beach (ice/tundra runs to the waterline), and steep coasts
                        // (elevation ≥ 0.55 right at the sea) keep their biome as rocky
                        // cliff shoreline. Distance-based width means flat worlds no longer
                        // grow huge beach rings.
                        tile = moisture > 0.68f ? TileType.Wetland : TileType.Sand;
                    }
                }

                chunk.SetTile(localX, localY, tile);

                // Surface relief: add roughness-modulated detail to the stored/rendered
                // elevation, faded out over water so the sea stays flat. Classification above
                // used the base elevation, so biome boundaries are unaffected.
                float roughness01 = (_roughnessNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;
                float roughnessFactor = _roughnessFloor + (1f - _roughnessFloor) * roughness01;
                float landFade = SmoothStep(0.40f, 0.48f, elevation); // fade in over the shore
                float detail = _detailNoise.GetNoise2D(worldX, worldY) * _detailAmplitude * roughnessFactor * landFade * tile.GetRuggedness();
                float storedElevation = elevation + detail;

                // Terraced cliffs: in mesa/bluff regions (low-freq mask) the stored elevation is
                // quantised into flat treads joined by short, steep risers. Applied on top of the
                // base shape like detail — classification, temperature, and rivers are untouched;
                // creatures feel the risers as strong slope resistance. Fades in above the shore
                // so beaches and river mouths stay gentle.
                // (step-height guard: 0 must disable terracing, not divide by zero below)
                if (_cliffStrength > 0f && _cliffStepHeight > 0.01f && elevation > 0.46f)
                {
                    float cliffMask = SmoothStep(0.60f, 0.80f, (_cliffNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f);
                    if (cliffMask > 0f)
                    {
                        float band = elevation / _cliffStepHeight;
                        int stepIdx = (int)band;
                        // Flat tread for 85% of each band; the top 15% carries the whole riser —
                        // the face concentrates into ~1 tile at typical slopes, so it reads as an
                        // actual cliff in 3D instead of a soft ramp.
                        float riser = SmoothStep(0.85f, 1f, band - stepIdx);
                        float terraced = (stepIdx + riser) * _cliffStepHeight;
                        float w = cliffMask * _cliffStrength * SmoothStep(0.46f, 0.52f, elevation);
                        // Blend toward the terrace and damp surface detail so treads read flat.
                        storedElevation = elevation + (terraced - elevation) * w + detail * (1f - 0.8f * w);
                    }
                }
                storedElevation = Math.Clamp(storedElevation, 0f, 1f);

                chunk.SetElevation(localX, localY, storedElevation);
                chunk.SetMoisture(localX, localY, moisture);
                chunk.SetTemperature(localX, localY, temperature);
            }
        }

        // Post-processing pass: landmarks
        ApplyLandmarks(chunk, worldOffsetX, worldOffsetY);
    }

    /// <summary>
    /// Compute temperature at a world position. Combines noise with a latitude-like
    /// gradient so cold biomes cluster near the top and hot biomes near the bottom.
    /// Elevation also cools temperature (mountains are cold).
    /// Returns 0-1 where 0 = cold, 1 = hot.
    /// </summary>
    private float GetTemperature(int worldX, int worldY, float elevation)
    {
        // Noise component (large-scale regional variation)
        float noiseTemp = (_temperatureNoise.GetNoise2D(worldX, worldY) + 1f) * 0.5f;

        // Gradient gives pole-to-equator feel; noise adds regional variation.
        // Gradient-weighted toward the latitude band so the cold (top) and hot (bottom) extremes
        // reliably produce polar and desert regions, instead of noise washing them out to thin,
        // sparse patches. This gives biome-specialist species (Penguin/Polar Bear/Arctic Fox/Musk
        // Ox; Camel/Scorpion/Lizard) enough of their home biome — and their prey — to be viable.
        float gradientWeight = 0.52f;
        float noiseWeight = 0.48f;
        float gradient = (float)worldY / _worldSizeTiles;
        gradient = Math.Clamp(gradient, 0f, 1f);

        float temp = noiseTemp * noiseWeight + gradient * gradientWeight;

        // Altitude cooling: high elevation reduces temperature
        float altitudePenalty = Math.Max(0f, (elevation - 0.65f)) * 1.5f;
        temp -= altitudePenalty;

        return Math.Clamp(temp, 0f, 1f);
    }

    /// <summary>
    /// Classify a terrain point into a discrete <see cref="TileType"/> from its continuous
    /// climate parameters (each 0–1). Public so the renderer can detect tiles overridden
    /// away from their climate classification (rivers, landmarks, terraform) and colour them
    /// discretely while pure-climate land uses the continuous palette.
    /// </summary>
    public static TileType DetermineTileType(float elevation, float moisture, float temperature)
    {
        // ── Water ──────────────────────────────────────────────────────────────────────
        if (elevation < 0.30f)
            return TileType.DeepWater;

        if (elevation < 0.40f)
        {
            // Reefs grow in warm, clear, shallow coastal water.
            // Lowered threshold (0.70→0.62) and slightly wider elevation band so reef
            // actually appears in tropical coastlines.
            if (temperature > 0.62f && elevation > 0.33f)
                return TileType.Reef;
            return TileType.ShallowWater;
        }

        // NOTE: there is no elevation "beach band" any more. Shores/beaches are a POST-pass in
        // GenerateChunk (distance-to-ocean, typed by climate) so they stay narrow on flat
        // worlds and match their biome. Land classification runs straight from the waterline.

        // ── Mountains / high ground ─────────────────────────────────────────────────────
        if (elevation > 0.80f)
        {
            if (temperature < 0.22f) return TileType.Ice;                   // frozen peaks
            if (temperature > 0.55f && moisture < 0.32f) return TileType.Lava; // volcanic
            return TileType.Mountain;
        }

        // Sub-alpine ice cap: cold enough at altitude that ground stays frozen.
        if (elevation > 0.72f && temperature < 0.26f)
            return TileType.Ice;

        // ── Arctic / polar (very cold) ─────────────────────────────────────────────────
        // Expanded boundary 0.20→0.22 so ice sheets are slightly larger.
        if (temperature < 0.22f)
        {
            if (moisture > 0.55f || elevation < 0.57f) return TileType.Ice;
            return TileType.Tundra;
        }

        // ── Cold temperate ─────────────────────────────────────────────────────────────
        // Expanded 0.35→0.40: the cold band now claims 18% of the temperature range
        // instead of 15%, giving Taiga/Steppe/Tundra meaningful world coverage.
        // Added polar-desert Ice at the dry extreme and Bog at the very wet extreme.
        if (temperature < 0.40f)
        {
            if (moisture > 0.72f) return TileType.Wetland;
            if (moisture > 0.54f) return TileType.Taiga;
            if (moisture > 0.33f) return TileType.Steppe;
            if (moisture > 0.17f) return TileType.Tundra;
            return TileType.Ice;  // polar desert — very dry, very cold
        }

        // ── Tropical (hot) ─────────────────────────────────────────────────────────────
        // Hot threshold widened 0.75→0.72 and the dry end now becomes REAL desert: hot + low
        // moisture is Arid (the niche that was effectively absent — Arid only formed at the rare
        // moisture<0.15 tail), with a thin Dirt semi-arid fringe. The old hot-dry Sand bucket is
        // gone (deserts are Arid, not beach). Deserts are now primarily a hot-zone phenomenon.
        if (temperature > 0.72f)
        {
            if (moisture > 0.60f) return TileType.Jungle;
            if (moisture > 0.42f) return TileType.Savanna;
            if (moisture > 0.30f) return TileType.Dirt;
            return TileType.Arid;
        }

        // ── Warm temperate ─────────────────────────────────────────────────────────────
        // Jungle removed: temp 0.60–0.72 is too cool for true jungle; dense moisture
        // here becomes Wetland (temperate rainforest / swamp) instead. Arid stays rare here
        // (only the very dry extreme) so deserts concentrate in the hot zone above.
        if (temperature > 0.60f)
        {
            if (moisture > 0.70f) return TileType.Wetland;
            if (moisture > 0.54f) return TileType.Forest;
            if (moisture > 0.36f) return temperature > 0.67f ? TileType.Savanna : TileType.Grass;
            if (moisture > 0.22f) return TileType.Shrubland;
            if (moisture > 0.14f) return TileType.Dirt;
            return TileType.Arid;
        }

        // ── Temperate ──────────────────────────────────────────────────────────────────
        // Bog threshold 0.82→0.76 so bogs actually appear.
        // Steppe added at the dry end (0.11–0.22) — previously invisible in temperate.
        if (moisture > 0.76f) return TileType.Bog;
        if (moisture > 0.65f) return TileType.Wetland;
        if (moisture > 0.50f) return TileType.Forest;
        if (moisture > 0.33f) return TileType.Grass;
        if (moisture > 0.22f) return TileType.Shrubland;
        if (moisture > 0.11f) return TileType.Steppe;
        return TileType.Arid;
    }

    /// <summary>
    /// Post-processing pass to add terrain landmarks: oases, clearings, and caves.
    /// Lakes are now handled by the RiverMapper flow system, so the landmark pass
    /// focuses on non-hydrological features only.
    /// </summary>
    private void ApplyLandmarks(Chunk chunk, int worldOffsetX, int worldOffsetY)
    {
        for (int localY = 0; localY < chunk.Size; localY++)
        {
            for (int localX = 0; localX < chunk.Size; localX++)
            {
                int worldX = worldOffsetX + localX;
                int worldY = worldOffsetY + localY;

                var currentTile = chunk.GetTile(localX, localY);
                float lmNoise = _landmarkNoise.GetNoise2D(worldX, worldY);

                // --- Oases: small grass/water patches in desert ---
                if (currentTile == TileType.Arid || currentTile == TileType.Sand)
                {
                    // Read the stored moisture (it already includes the hydrology boost and
                    // drainage), so the oasis test matches the moisture that produced this
                    // tile — and riverside desert naturally sprouts oases.
                    float moisture = chunk.GetMoisture(localX, localY);
                    if (lmNoise > 0.7f && moisture > 0.35f)
                    {
                        chunk.SetTile(localX, localY, TileType.Grass);
                        continue;
                    }
                }

                // --- Forest Clearings: grass gaps inside dense forest ---
                if (currentTile == TileType.Forest)
                {
                    if (lmNoise < -0.65f)
                    {
                        chunk.SetTile(localX, localY, TileType.Grass);
                        continue;
                    }
                }

                // --- Taiga Clearings: cold meadows (steppe patches) inside taiga ---
                if (currentTile == TileType.Taiga)
                {
                    if (lmNoise < -0.68f)
                    {
                        chunk.SetTile(localX, localY, TileType.Steppe);
                        continue;
                    }
                }

                // --- Jungle Clearings: savanna patches inside jungle ---
                if (currentTile == TileType.Jungle)
                {
                    if (lmNoise < -0.70f)
                    {
                        chunk.SetTile(localX, localY, TileType.Savanna);
                        continue;
                    }
                }

                // --- Permafrost spots: ice patches within tundra ---
                if (currentTile == TileType.Tundra)
                {
                    if (lmNoise > 0.72f)
                    {
                        chunk.SetTile(localX, localY, TileType.Ice);
                        continue;
                    }
                }

                // --- Shrubland dry patches: steppe outcrops in shrubland ---
                if (currentTile == TileType.Shrubland)
                {
                    if (lmNoise > 0.74f)
                    {
                        chunk.SetTile(localX, localY, TileType.Steppe);
                        continue;
                    }
                }

                // --- Surface Caves: walkable grass centers in mountain edges ---
                if (currentTile == TileType.Mountain)
                {
                    // Use the actual stored (domain-warped) elevation for this tile rather
                    // than re-sampling unwarped noise, so caves sit on genuine low mountains.
                    float elevation = chunk.GetElevation(localX, localY);
                    if (lmNoise > 0.75f && elevation < 0.85f)
                    {
                        chunk.SetTile(localX, localY, TileType.Grass);
                        continue;
                    }
                }
            }
        }
    }
}
