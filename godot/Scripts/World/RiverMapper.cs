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
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseY;
    private readonly float _warpAmplitude;

    private int _worldSize;
    private float[,] _elevation = null!;
    private int[,] _flow = null!;

    // Pre-computed tile markers
    private bool[,] _isRiver = null!;
    private bool[,] _isLake = null!;
    private bool[,] _isWetlandBank = null!;

    // Tuning parameters
    private const float SourceMinElevation = 0.68f;   // Minimum elevation for river sources
    private const float WaterLevel = 0.40f;            // Elevation at which ocean begins
    private const int MinRiverFlow = 1;                // Flow threshold to become a river
    private const int WideRiverFlow = 3;               // Flow threshold for wide (2-tile) rivers
    private const int MinSourceSpacing = 18;           // Minimum tiles between sources
    private const int MaxRiverSources = 80;            // Max number of river source points
    private const float MaxLakeRise = 0.04f;             // Max water rise above depression bottom
    private const int MaxLakeArea = 80;                  // Max tiles a single lake can occupy

    // 6-direction offsets for staggered hex grid (row-parity variants)
    // Even row (y%2==0): upper/lower diagonal neighbors lean left  (dx = -1)
    // Odd  row (y%2==1): upper/lower diagonal neighbors lean right (dx = +1)
    private static readonly int[] EvenDX = { -1,  1, -1,  0, -1,  0 };
    private static readonly int[] EvenDY = {  0,  0, -1, -1,  1,  1 };
    private static readonly int[]  OddDX = { -1,  1,  0,  1,  0,  1 };
    private static readonly int[]  OddDY = {  0,  0, -1, -1,  1,  1 };

    public RiverMapper(FastNoiseLite elevationNoise, FastNoiseLite warpNoiseX,
                       FastNoiseLite warpNoiseY, float warpAmplitude)
    {
        _elevationNoise = elevationNoise;
        _warpNoiseX = warpNoiseX;
        _warpNoiseY = warpNoiseY;
        _warpAmplitude = warpAmplitude;
    }

    /// <summary>
    /// Run the full river generation pipeline. Must be called before any chunks are generated.
    /// </summary>
    public void Generate(int worldSize, int seed)
    {
        _worldSize = worldSize;
        _elevation = new float[worldSize, worldSize];
        _flow = new int[worldSize, worldSize];
        _isRiver = new bool[worldSize, worldSize];
        _isLake = new bool[worldSize, worldSize];
        _isWetlandBank = new bool[worldSize, worldSize];

        BuildElevationMap();
        var sources = SelectSources(seed);
        TraceRivers(sources);
        MarkRiversAndLakes();
        MarkWetlandBanks();
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
    /// Build the full-world elevation map using the same noise and domain warping
    /// as TerrainGenerator, ensuring rivers align with the actual terrain.
    /// </summary>
    private void BuildElevationMap()
    {
        for (int y = 0; y < _worldSize; y++)
        {
            for (int x = 0; x < _worldSize; x++)
            {
                float warpX = _warpNoiseX.GetNoise2D(x, y) * _warpAmplitude;
                float warpY = _warpNoiseY.GetNoise2D(x, y) * _warpAmplitude;
                float warpedX = x + warpX;
                float warpedY = y + warpY;
                _elevation[x, y] = (_elevationNoise.GetNoise2D(warpedX, warpedY) + 1f) * 0.5f;
            }
        }
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

        // Select sources with minimum spacing
        var sources = new List<(int x, int y)>();
        var usedGrid = new HashSet<(int, int)>(); // Grid cells for spacing check

        foreach (var (cx, cy, _) in candidates)
        {
            if (sources.Count >= MaxRiverSources)
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

            // Find steepest descent among 6 hex neighbors
            int bestNx = -1, bestNy = -1;
            float bestElev = currentElev;

            var (rdx, rdy) = cy % 2 == 0 ? (EvenDX, EvenDY) : (OddDX, OddDY);
            for (int d = 0; d < 6; d++)
            {
                int nx = cx + rdx[d];
                int ny = cy + rdy[d];

                if (nx < 0 || nx >= _worldSize || ny < 0 || ny >= _worldSize)
                {
                    // Edge of world = outflow (treat as ocean)
                    bestNx = nx;
                    bestNy = ny;
                    bestElev = -1f;
                    break;
                }

                if (visited.Contains((nx, ny)))
                    continue;

                float nElev = _elevation[nx, ny];
                if (nElev < bestElev)
                {
                    bestElev = nElev;
                    bestNx = nx;
                    bestNy = ny;
                }
            }

            // Could not find a lower neighbor — we're in a depression
            if (bestNx < 0 || bestElev >= currentElev)
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
