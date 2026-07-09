using System;
using System.Collections.Generic;
using Godot;

namespace Mitosis.World;

/// <summary>
/// Pre-computes river and lake placement using elevation-based flow accumulation.
/// Rivers trace downhill from high-elevation sources to ocean/lakes using steepest descent.
/// Flow accumulation determines river width; depressions fill to form lakes.
/// Must be run once before chunk generation so chunks can query the results.
/// </summary>
public sealed class RiverMapper
{
    // Base-elevation sampler shared with TerrainGenerator (SampleBaseElevation) so the flow
    // map is computed on exactly the terrain the chunks will classify — including ridges.
    private readonly Func<int, int, float> _sampleElevation;

    private int _worldSize;
    private int _seed;
    private float[,] _elevation = null!;
    private int[,] _flow = null!;
    // Chamfer distance (tiles) from land to the nearest ocean tile, capped at OceanDistCap.
    // Drives the biome-aware shore pass in TerrainGenerator (beaches by distance, not by
    // elevation band — so flat worlds no longer grow huge beach rings).
    private float[,] _oceanDist = null!;

    // Pre-computed tile markers
    private bool[,] _isRiver = null!;
    private bool[,] _isLake = null!;
    private bool[,] _isWetlandBank = null!;
    // Hydrology→climate coupling: moisture added to the climate field around rivers/lakes
    // (riparian corridors, marshy delta fans). Read by TerrainGenerator before classification.
    private float[,] _moistureBoost = null!;

    // Tuning parameters
    private const float SourceMinElevation = 0.68f;   // Minimum elevation for river sources
    private const float WaterLevel = 0.40f;            // Elevation at which ocean begins
    private const int MinRiverFlow = 1;                // Flow threshold to become a river
    private const int WideRiverFlow = 3;               // Flow threshold for wide (2-tile) rivers
    private const int MinSourceSpacing = 18;           // Minimum tiles between sources
    private const float MaxLakeRise = 0.04f;             // Max water rise above depression bottom
    private const int MaxLakeArea = 80;                  // Max tiles a single lake can occupy

    // Hydrology→climate coupling (see BuildMoistureBoost)
    private const float RiparianBoost     = 0.10f;  // base wetting beside any river
    private const float RiparianFlowBonus = 0.02f;  // extra per unit of flow (capped at 5)
    private const float LakeBoost         = 0.15f;  // wetting beside lakes
    private const float DeltaBoost        = 0.30f;  // river mouths fan out into marshy deltas
    private const float BoostFalloff      = 0.022f; // decay per tile of distance from water

    private const float OceanDistCap = 8f;          // max tracked distance-to-ocean (tiles)

    // 6-direction offsets for staggered hex grid (row-parity variants)
    // Even row (y%2==0): upper/lower diagonal neighbors lean left  (dx = -1)
    // Odd  row (y%2==1): upper/lower diagonal neighbors lean right (dx = +1)
    private static readonly int[] EvenDX = { -1,  1, -1,  0, -1,  0 };
    private static readonly int[] EvenDY = {  0,  0, -1, -1,  1,  1 };
    private static readonly int[]  OddDX = { -1,  1,  0,  1,  0,  1 };
    private static readonly int[]  OddDY = {  0,  0, -1, -1,  1,  1 };

    private readonly float _riverDensity;

    public RiverMapper(Func<int, int, float> sampleElevation, float riverDensity = 1f)
    {
        _sampleElevation = sampleElevation;
        _riverDensity = riverDensity;
    }

    /// <summary>
    /// Run the full river generation pipeline. Must be called before any chunks are generated.
    /// </summary>
    public void Generate(int worldSize, int seed)
    {
        _worldSize = worldSize;
        _seed = seed;
        _elevation = new float[worldSize, worldSize];
        _flow = new int[worldSize, worldSize];
        _isRiver = new bool[worldSize, worldSize];
        _isLake = new bool[worldSize, worldSize];
        _isWetlandBank = new bool[worldSize, worldSize];

        BuildElevationMap();
        ComputeMeanLandSlope();
        BuildOceanDistance();
        var sources = SelectSources(seed);
        TraceRivers(sources);
        MarkRiversAndLakes();
        MarkWetlandBanks();
        BuildMoistureBoost();
    }

    public bool IsRiver(int x, int y)
    {
        if (x < 0 || x >= _worldSize || y < 0 || y >= _worldSize) return false;
        return _isRiver[x, y];
    }

    public bool IsLake(int x, int y)
    {
        if (x < 0 || x >= _worldSize || y < 0 || y >= _worldSize) return false;
        return _isLake[x, y];
    }

    public bool IsWetlandBank(int x, int y)
    {
        if (x < 0 || x >= _worldSize || y < 0 || y >= _worldSize) return false;
        return _isWetlandBank[x, y];
    }

    /// <summary>
    /// Build the full-world elevation map with the shared base-elevation sampler, ensuring
    /// rivers align with the exact terrain the chunks will be classified from.
    /// </summary>
    private void BuildElevationMap()
    {
        for (int y = 0; y < _worldSize; y++)
        {
            for (int x = 0; x < _worldSize; x++)
            {
                _elevation[x, y] = _sampleElevation(x, y);
            }
        }
    }

    /// <summary>
    /// The cached base elevation (0–1) at a world tile, clamped to world bounds. Chunk
    /// generation reads this instead of re-sampling noise, so terrain and hydrology can
    /// never disagree.
    /// </summary>
    public float GetBaseElevation(int x, int y)
    {
        if (x < 0) x = 0; else if (x >= _worldSize) x = _worldSize - 1;
        if (y < 0) y = 0; else if (y >= _worldSize) y = _worldSize - 1;
        return _elevation[x, y];
    }

    /// <summary>
    /// Local steepness of the base terrain at a tile: the largest elevation difference to a
    /// cardinal neighbour. Used by TerrainGenerator's drainage rule (flat ground holds
    /// moisture, slopes shed it).
    /// </summary>
    public float GetSlope(int x, int y)
    {
        float e = GetBaseElevation(x, y);
        float s = Math.Abs(GetBaseElevation(x - 1, y) - e);
        s = Math.Max(s, Math.Abs(GetBaseElevation(x + 1, y) - e));
        s = Math.Max(s, Math.Abs(GetBaseElevation(x, y - 1) - e));
        s = Math.Max(s, Math.Abs(GetBaseElevation(x, y + 1) - e));
        return s;
    }

    /// <summary>
    /// Mean slope of the generated land, measured per world. The drainage rule pivots on this
    /// (flatter-than-typical ground collects moisture, steeper sheds it) so the world's overall
    /// moisture budget is balanced at ANY elevation frequency. A hardcoded pivot made entire
    /// low-frequency (flat) worlds read as "basins everywhere" — uniformly wetter, deserts
    /// wiped out (seed 1956076603 @ ef 0.004: Arid 0.1%).
    /// </summary>
    public float MeanLandSlope { get; private set; } = 0.025f;

    private void ComputeMeanLandSlope()
    {
        // Strided sampling — this only needs to be representative, not exact.
        double sum = 0;
        int count = 0;
        for (int y = 0; y < _worldSize; y += 4)
        {
            for (int x = 0; x < _worldSize; x += 4)
            {
                if (_elevation[x, y] < WaterLevel) continue;
                sum += GetSlope(x, y);
                count++;
            }
        }
        if (count > 0)
            MeanLandSlope = (float)(sum / count);
    }

    /// <summary>
    /// Distance in tiles (chamfer approximation, capped at 8) from a tile to the nearest
    /// ocean water. 0 on ocean itself. Drives the biome-aware shore pass.
    /// </summary>
    public float GetOceanDistance(int x, int y)
    {
        if (x < 0 || x >= _worldSize || y < 0 || y >= _worldSize) return OceanDistCap;
        return _oceanDist[x, y];
    }

    private void BuildOceanDistance()
    {
        int n = _worldSize;
        _oceanDist = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                _oceanDist[x, y] = _elevation[x, y] < WaterLevel ? 0f : OceanDistCap;

        // Two-pass chamfer min-distance (mirror of the moisture-boost propagation).
        const float diag = 1.4f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float v = _oceanDist[x, y];
                if (v == 0f) continue;
                if (x > 0) v = Math.Min(v, _oceanDist[x - 1, y] + 1f);
                if (y > 0)
                {
                    v = Math.Min(v, _oceanDist[x, y - 1] + 1f);
                    if (x > 0)     v = Math.Min(v, _oceanDist[x - 1, y - 1] + diag);
                    if (x < n - 1) v = Math.Min(v, _oceanDist[x + 1, y - 1] + diag);
                }
                _oceanDist[x, y] = v;
            }
        }
        for (int y = n - 1; y >= 0; y--)
        {
            for (int x = n - 1; x >= 0; x--)
            {
                float v = _oceanDist[x, y];
                if (v == 0f) continue;
                if (x < n - 1) v = Math.Min(v, _oceanDist[x + 1, y] + 1f);
                if (y < n - 1)
                {
                    v = Math.Min(v, _oceanDist[x, y + 1] + 1f);
                    if (x < n - 1) v = Math.Min(v, _oceanDist[x + 1, y + 1] + diag);
                    if (x > 0)     v = Math.Min(v, _oceanDist[x - 1, y + 1] + diag);
                }
                _oceanDist[x, y] = v;
            }
        }
    }

    /// <summary>Extra climate moisture contributed by nearby rivers/lakes/deltas (0 far away).</summary>
    public float GetMoistureBoost(int x, int y)
    {
        if (x < 0 || x >= _worldSize || y < 0 || y >= _worldSize) return 0f;
        return _moistureBoost[x, y];
    }

    /// <summary>
    /// Build the hydrology→climate moisture field: every river/lake tile radiates moisture
    /// into the surrounding land, scaled by how much water it carries — a soft riparian halo
    /// along streams (~5 tiles), wider along high-flow rivers and lakes, and a broad fan
    /// (~13 tiles) around delta mouths where a river meets the sea. TerrainGenerator adds
    /// this to the climate moisture BEFORE classification, so the biomes themselves respond:
    /// wetland/bog margins in wet climates, green riparian corridors through dry ones.
    /// </summary>
    private void BuildMoistureBoost()
    {
        int n = _worldSize;
        _moistureBoost = new float[n, n];

        // Seed source strengths.
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float s = 0f;
                if (_isLake[x, y])
                {
                    s = LakeBoost;
                }
                else if (_isRiver[x, y])
                {
                    s = RiparianBoost + RiparianFlowBonus * Math.Min(_flow[x, y], 5);
                    // Delta: a river tile at shore elevation touching the ocean (or draining
                    // off the world edge).
                    if (_elevation[x, y] < 0.45f && TouchesOcean(x, y))
                        s = DeltaBoost;
                }
                if (s > 0f)
                    _moistureBoost[x, y] = s;
            }
        }

        // Two-pass chamfer propagation of max(strength − distance × falloff): each source
        // spreads a linear-decay halo without a per-source BFS (O(n²) total).
        const float diag = 1.4f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float v = _moistureBoost[x, y];
                if (x > 0) v = Math.Max(v, _moistureBoost[x - 1, y] - BoostFalloff);
                if (y > 0)
                {
                    v = Math.Max(v, _moistureBoost[x, y - 1] - BoostFalloff);
                    if (x > 0)     v = Math.Max(v, _moistureBoost[x - 1, y - 1] - BoostFalloff * diag);
                    if (x < n - 1) v = Math.Max(v, _moistureBoost[x + 1, y - 1] - BoostFalloff * diag);
                }
                _moistureBoost[x, y] = v;
            }
        }
        for (int y = n - 1; y >= 0; y--)
        {
            for (int x = n - 1; x >= 0; x--)
            {
                float v = _moistureBoost[x, y];
                if (x < n - 1) v = Math.Max(v, _moistureBoost[x + 1, y] - BoostFalloff);
                if (y < n - 1)
                {
                    v = Math.Max(v, _moistureBoost[x, y + 1] - BoostFalloff);
                    if (x < n - 1) v = Math.Max(v, _moistureBoost[x + 1, y + 1] - BoostFalloff * diag);
                    if (x > 0)     v = Math.Max(v, _moistureBoost[x - 1, y + 1] - BoostFalloff * diag);
                }
                _moistureBoost[x, y] = v;
            }
        }
    }

    /// <summary>Deterministic per-tile hash in [0,1) — reproducible river meander jitter.</summary>
    private float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263) ^ ((uint)_seed * 2246822519u);
            h ^= h >> 13;
            h *= 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    /// <summary>True if a hex neighbour is ocean (below the waterline) or off the world edge.</summary>
    private bool TouchesOcean(int x, int y)
    {
        var (tdx, tdy) = y % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
        for (int d = 0; d < 6; d++)
        {
            int nx = x + tdx[d];
            int ny = y + tdy[d];
            if (nx < 0 || nx >= _worldSize || ny < 0 || ny >= _worldSize)
                return true; // world edge = outflow
            if (_elevation[nx, ny] < WaterLevel)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Select river source points from high-elevation land tiles.
    /// Sources are spaced apart to avoid too many rivers from the same peak.
    /// </summary>
    private List<(int x, int y)> SelectSources(int seed)
    {
        var rng = new Random(seed + 4000);
        var candidates = new List<(int x, int y, float elev)>();

        // Collect high-elevation land tiles as candidates
        for (int y = 0; y < _worldSize; y++)
        {
            for (int x = 0; x < _worldSize; x++)
            {
                float elev = _elevation[x, y];
                if (elev >= SourceMinElevation && elev < 0.80f) // Below mountain, above mid-elevation
                    candidates.Add((x, y, elev));
            }
        }

        // Sort by elevation (highest first) — prioritize highest peaks
        candidates.Sort((a, b) => b.elev.CompareTo(a.elev));

        // Source budget scales with world size so river density stays roughly constant —
        // the old fixed cap (80) was tuned for ~288-tile debug worlds and left larger maps dry.
        // RiverDensity (TerrainSettings) then scales taste: 1 = the original dense network.
        int maxSources = Math.Clamp((int)(_worldSize / 4f * _riverDensity), 4, 480);

        // Select sources with minimum spacing
        var sources = new List<(int x, int y)>();
        var usedGrid = new HashSet<(int, int)>(); // Grid cells for spacing check

        foreach (var (cx, cy, _) in candidates)
        {
            if (sources.Count >= maxSources)
                break;

            // Check spacing: grid cell = position / MinSourceSpacing
            int gx = cx / MinSourceSpacing;
            int gy = cy / MinSourceSpacing;
            if (usedGrid.Contains((gx, gy)))
                continue;

            // Random selection to add variety (don't use every eligible peak)
            if (rng.NextDouble() > 0.35)
                continue;

            sources.Add((cx, cy));

            // Mark surrounding grid cells as used
            for (int dgy = -1; dgy <= 1; dgy++)
                for (int dgx = -1; dgx <= 1; dgx++)
                    usedGrid.Add((gx + dgx, gy + dgy));
        }

        return sources;
    }

    /// <summary>
    /// Trace each river from source downhill using steepest descent.
    /// When a river reaches a depression (no lower neighbor), attempt to fill it
    /// and continue flowing from the lowest rim point.
    /// </summary>
    private void TraceRivers(List<(int x, int y)> sources)
    {
        foreach (var source in sources)
        {
            TraceOneRiver(source.x, source.y);
        }
    }

    private void TraceOneRiver(int startX, int startY)
    {
        int cx = startX, cy = startY;
        var visited = new HashSet<(int, int)>();
        int maxSteps = _worldSize * 3; // Safety limit

        for (int step = 0; step < maxSteps; step++)
        {
            if (cx < 0 || cx >= _worldSize || cy < 0 || cy >= _worldSize)
                break;

            // Reached ocean — done
            float currentElev = _elevation[cx, cy];
            if (currentElev < WaterLevel)
                break;

            // Mark this tile's flow
            _flow[cx, cy]++;
            visited.Add((cx, cy));

            // Find the descent direction among the 6 hex neighbors. Candidates are limited to
            // TRUE descents (depression detection must stay exact — a false trigger flood-fills
            // a fake lake downhill), but the CHOICE among them is jittered by a deterministic
            // per-tile dither scaled to the world's mean slope: on near-flat terrain pure
            // steepest descent degenerates into the hex layout's row-parity bias (long straight
            // runs with staircase kinks); the dither breaks those near-ties into natural
            // meanders while leaving genuinely steep descents effectively unchanged.
            int bestNx = -1, bestNy = -1;
            float bestScore = float.MaxValue;
            bool outflow = false;

            var (rdx, rdy) = cy % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
            for (int d = 0; d < 6; d++)
            {
                int nx = cx + rdx[d];
                int ny = cy + rdy[d];

                if (nx < 0 || nx >= _worldSize || ny < 0 || ny >= _worldSize)
                {
                    // Edge of world = outflow (treat as ocean). Previously this stored the
                    // out-of-bounds coordinate in bestNx, which a west/north edge (-1) made
                    // indistinguishable from "no candidate" — ending those rivers in a fake
                    // depression instead of flowing off the map.
                    outflow = true;
                    break;
                }

                if (visited.Contains((nx, ny)))
                    continue;

                float nElev = _elevation[nx, ny];
                if (nElev >= currentElev)
                    continue; // only true descents are candidates

                float score = nElev + (Hash01(nx, ny) - 0.5f) * MeanLandSlope;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestNx = nx;
                    bestNy = ny;
                }
            }

            if (outflow)
                break; // river leaves the world

            // Could not find a lower neighbor — we're in a depression
            if (bestNx < 0)
            {
                // Try to fill the depression: find the lowest rim point
                var lakeResult = FillDepression(cx, cy, visited);
                if (lakeResult.HasValue)
                {
                    // Continue river from the overflow point
                    cx = lakeResult.Value.overflowX;
                    cy = lakeResult.Value.overflowY;
                    continue;
                }
                break; // Fully enclosed depression, river ends in a lake
            }

            cx = bestNx;
            cy = bestNy;
        }
    }

    /// <summary>
    /// Fill a depression by flood-filling from the stuck point to find the lowest
    /// rim tile that the water could overflow through. Mark interior as lake tiles.
    /// Returns the overflow point, or null if the depression is fully enclosed.
    /// </summary>
    private (int overflowX, int overflowY)? FillDepression(int startX, int startY,
        HashSet<(int, int)> riverVisited)
    {
        float startElev = _elevation[startX, startY];

        // BFS flood fill: expand from the depression bottom, collecting tiles
        // at or below rising water level, looking for an overflow point.
        // Constrained by MaxLakeRise and MaxLakeArea to prevent flooding the map.
        var filled = new HashSet<(int, int)> { (startX, startY) };
        var frontier = new Queue<(int x, int y)>();
        frontier.Enqueue((startX, startY));

        int bestOverflowX = -1, bestOverflowY = -1;
        float bestOverflowElev = float.MaxValue;
        float waterLevel = startElev;
        float maxWaterLevel = startElev + MaxLakeRise;

        // Rising water: incrementally raise water level to find overflow
        int maxIterations = 40;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            if (filled.Count >= MaxLakeArea) break;

            if (frontier.Count == 0)
            {
                // Raise water level slightly and re-check neighbors
                waterLevel += 0.002f;
                if (waterLevel > maxWaterLevel) break;

                // Snapshot to avoid modifying filled while iterating
                var snapshot = new List<(int, int)>(filled);
                foreach (var (fx, fy) in snapshot)
                {
                    var (fdx, fdy) = fy % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
                    for (int d = 0; d < 6; d++)
                    {
                        int nx = fx + fdx[d];
                        int ny = fy + fdy[d];
                        if (nx < 0 || nx >= _worldSize || ny < 0 || ny >= _worldSize)
                            continue;
                        if (filled.Contains((nx, ny))) continue;

                        float nElev = _elevation[nx, ny];
                        if (nElev <= waterLevel)
                        {
                            frontier.Enqueue((nx, ny));
                            filled.Add((nx, ny));
                        }
                        else if (nElev < bestOverflowElev && !riverVisited.Contains((nx, ny)))
                        {
                            bestOverflowElev = nElev;
                            bestOverflowX = nx;
                            bestOverflowY = ny;
                        }
                    }
                }

                // If we found an overflow point at this water level, use it
                if (bestOverflowElev <= waterLevel + 0.01f && bestOverflowX >= 0)
                {
                    MarkFilledAsLake(filled, waterLevel);
                    return (bestOverflowX, bestOverflowY);
                }
                continue;
            }

            while (frontier.Count > 0 && filled.Count < MaxLakeArea)
            {
                var (px, py) = frontier.Dequeue();

                var (pdx, pdy) = py % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
                for (int d = 0; d < 6; d++)
                {
                    int nx = px + pdx[d];
                    int ny = py + pdy[d];
                    if (nx < 0 || nx >= _worldSize || ny < 0 || ny >= _worldSize)
                        continue;
                    if (filled.Contains((nx, ny))) continue;

                    float nElev = _elevation[nx, ny];
                    if (nElev <= waterLevel)
                    {
                        frontier.Enqueue((nx, ny));
                        filled.Add((nx, ny));
                    }
                    else if (nElev < bestOverflowElev && !riverVisited.Contains((nx, ny)))
                    {
                        bestOverflowElev = nElev;
                        bestOverflowX = nx;
                        bestOverflowY = ny;
                    }
                }
            }

            // Check if overflow found
            if (bestOverflowX >= 0 && bestOverflowElev <= waterLevel + 0.02f)
            {
                MarkFilledAsLake(filled, waterLevel);
                return (bestOverflowX, bestOverflowY);
            }
        }

        // Depression didn't overflow — mark as small terminal lake
        MarkFilledAsLake(filled, waterLevel);
        return null;
    }

    private void MarkFilledAsLake(HashSet<(int, int)> filled, float waterLevel)
    {
        // Only mark tiles that are above ocean level as lakes
        // (tiles below WaterLevel are already ocean)
        foreach (var (x, y) in filled)
        {
            if (_elevation[x, y] >= WaterLevel)
            {
                _isLake[x, y] = true;
                _flow[x, y] = Math.Max(_flow[x, y], 1); // Ensure lake has at least 1 flow
            }
        }
    }

    /// <summary>
    /// Convert flow accumulation into river/lake tile markers.
    /// </summary>
    private void MarkRiversAndLakes()
    {
        for (int y = 0; y < _worldSize; y++)
        {
            for (int x = 0; x < _worldSize; x++)
            {
                // Skip ocean tiles. The cutoff must be the actual waterline (0.40), NOT the old
                // LandLevel (0.45): traces run all the way to the ocean, but marking used to stop
                // 0.05 of elevation early — exactly the Sand/shore band — so every river visibly
                // died at the beach instead of connecting to the sea.
                if (_elevation[x, y] < WaterLevel)
                    continue;

                // Skip mountain tiles (rivers don't flow on mountains)
                if (_elevation[x, y] > 0.80f)
                    continue;

                int flow = _flow[x, y];

                // Lakes already marked during depression filling
                if (_isLake[x, y])
                    continue;

                // River threshold based on flow accumulation
                if (flow >= MinRiverFlow)
                {
                    _isRiver[x, y] = true;

                    // Wide rivers: also mark adjacent tiles for high-flow rivers
                    if (flow >= WideRiverFlow)
                    {
                        var (wdx, wdy) = y % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
                        for (int d = 0; d < 6; d++) // All 6 hex neighbors
                        {
                            int nx = x + wdx[d];
                            int ny = y + wdy[d];
                            if (nx >= 0 && nx < _worldSize && ny >= 0 && ny < _worldSize &&
                                _elevation[nx, ny] >= WaterLevel && _elevation[nx, ny] < 0.80f &&
                                !_isLake[nx, ny])
                            {
                                _isRiver[nx, ny] = true;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Mark wetland banks alongside rivers and lakes.
    /// </summary>
    private void MarkWetlandBanks()
    {
        for (int y = 0; y < _worldSize; y++)
        {
            for (int x = 0; x < _worldSize; x++)
            {
                if (_isRiver[x, y] || _isLake[x, y])
                    continue;

                // Skip non-land tiles (banks extend down the shore band so river mouths get
                // marshy edges instead of bare beach)
                if (_elevation[x, y] < WaterLevel || _elevation[x, y] > 0.80f)
                    continue;

                // Check if adjacent to river or lake
                bool adjacentToWater = false;
                var (wbdx, wbdy) = y % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
                for (int d = 0; d < 6; d++)
                {
                    int nx = x + wbdx[d];
                    int ny = y + wbdy[d];
                    if (nx >= 0 && nx < _worldSize && ny >= 0 && ny < _worldSize)
                    {
                        if (_isRiver[nx, ny] || _isLake[nx, ny])
                        {
                            adjacentToWater = true;
                            break;
                        }
                    }
                }

                if (adjacentToWater)
                    _isWetlandBank[x, y] = true;
            }
        }
    }
}
