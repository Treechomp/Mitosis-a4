using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

namespace Mitosis.World;

/// <summary>
/// One-shot diagnostic dumped at world generation: a PNG of the biome map plus a sidecar text
/// report listing the worldgen parameters AND the per-tile-type biome distribution with
/// niche-coverage flags. Lets us SEE what a parameter set produces and judge whether each
/// species' niche has enough habitat (cold belt for penguins/polar bears, water for fish/sharks,
/// deserts for scorpions/lizards) before blaming balance. Written to the same logs/ directory as
/// the ecosystem CSVs so it's retrieved alongside them.
/// </summary>
public static class WorldSnapshot
{
    /// <summary>Niche-coverage thresholds (% of total tiles) below which we warn.</summary>
    private const float MinNichePct = 2.0f;

    public static void Capture(
        WorldManager wm, int seed, int chunkSize, int worldSizeChunks,
        TerrainSettings settings, float elevHeightScale,
        string logDir = "res://logs")
    {
        int n = wm.WorldSizeTiles;
        // Upscale small worlds so the PNG is legible; cap big worlds at 1px/tile.
        int px = n <= 384 ? 3 : (n <= 768 ? 2 : 1);

        var img = Image.CreateEmpty(n * px, n * px, false, Image.Format.Rgb8);
        var hist = new Dictionary<TileType, int>();
        int riverOutlets = 0; // river tiles touching the sea — 0 means rivers are severed

        for (int ty = 0; ty < n; ty++)
        {
            for (int tx = 0; tx < n; tx++)
            {
                TileType tile = wm.GetTile(tx, ty);
                hist.TryGetValue(tile, out int c);
                hist[tile] = c + 1;

                if (tile == TileType.River &&
                    (IsSea(wm.GetTile(tx - 1, ty)) || IsSea(wm.GetTile(tx + 1, ty)) ||
                     IsSea(wm.GetTile(tx, ty - 1)) || IsSea(wm.GetTile(tx, ty + 1))))
                    riverOutlets++;

                Color col = Chunk.GetTileColor(tile);
                for (int dy = 0; dy < px; dy++)
                    for (int dx = 0; dx < px; dx++)
                        img.SetPixel(tx * px + dx, ty * px + dy, col);
            }
        }

        string dir = ProjectSettings.GlobalizePath(logDir);
        Directory.CreateDirectory(dir);
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName =
            $"world_{ts}_seed{seed}_{worldSizeChunks}ch_ef{settings.ElevationFrequency:0.####}" +
            $"_df{settings.DetailFrequency:0.####}_rf{settings.RoughnessFrequency:0.####}" +
            $"_ra{settings.RidgeAmplitude:0.####}";

        string pngPath = Path.Combine(dir, baseName + ".png");
        Error err = img.SavePng(pngPath);
        if (err != Error.Ok)
            GD.PushWarning($"[WorldSnapshot] PNG save failed ({err}) at {pngPath}");

        File.WriteAllText(Path.Combine(dir, baseName + ".txt"),
            BuildReport(ts, seed, chunkSize, worldSizeChunks, n,
                        settings, elevHeightScale, hist, riverOutlets));

        GD.Print("=========================================");
        GD.Print($"  WORLD SNAPSHOT: {pngPath}");
        GD.Print($"  biome report:   {baseName}.txt");
        GD.Print("=========================================");
    }

    /// <summary>Sea water for the river-outlet metric (not River itself, not land).</summary>
    private static bool IsSea(TileType t)
        => t == TileType.DeepWater || t == TileType.ShallowWater || t == TileType.Reef;

    private static string BuildReport(
        string ts, int seed, int chunkSize, int worldSizeChunks, int n,
        TerrainSettings s, float elevHeightScale,
        Dictionary<TileType, int> hist, int riverOutlets)
    {
        int total = n * n;
        float Pct(int count) => 100f * count / total;
        int Count(params TileType[] types)
        {
            int s = 0;
            foreach (var t in types)
                if (hist.TryGetValue(t, out int c)) s += c;
            return s;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Mitosis world snapshot");
        sb.AppendLine($"timestamp:               {ts}");
        sb.AppendLine($"seed:                    {seed}");
        sb.AppendLine($"size:                    {worldSizeChunks} chunks x {chunkSize} = {n}x{n} tiles ({total} total)");
        sb.AppendLine($"elevation_frequency:     {s.ElevationFrequency}   (lower = larger landmasses)");
        sb.AppendLine($"elevation_height_scale:  {elevHeightScale}");
        sb.AppendLine($"warp_amplitude:          {s.WarpAmplitude}");
        sb.AppendLine($"detail_frequency:        {s.DetailFrequency}   amplitude: {s.DetailAmplitude}");
        sb.AppendLine($"roughness_frequency:     {s.RoughnessFrequency}   floor: {s.RoughnessFloor}");
        sb.AppendLine($"ridge_frequency:         {s.RidgeFrequency}   amplitude: {s.RidgeAmplitude}   orogeny_freq: {s.OrogenyFrequency}");
        sb.AppendLine($"cliff_frequency:         {s.CliffFrequency}   strength: {s.CliffStrength}   step: {s.CliffStepHeight}");
        sb.AppendLine($"moisture_frequency:      0.008 (fixed in TerrainGenerator)");
        sb.AppendLine($"temperature_frequency:   0.005 (fixed in TerrainGenerator)");
        sb.AppendLine();

        sb.AppendLine("# Biome distribution (tile type : count : percent), most common first");
        var rows = new List<KeyValuePair<TileType, int>>(hist);
        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (var kv in rows)
            sb.AppendLine($"  {kv.Key,-13} {kv.Value,8} {Pct(kv.Value),6:0.0}%");
        sb.AppendLine();

        // Niche roll-ups — the coverage that actually gates each specialist's habitat.
        // Deliberately split: DeepWater (the connected-sea niche a Shark needs) from total water,
        // and Arid (true hot-dry desert) from Sand (which is mostly shoreline beach rings — NOT
        // desert habitat; counting them together gives a false "desert is fine" reading).
        int openWater  = Count(TileType.DeepWater);
        int allWater   = Count(TileType.DeepWater, TileType.ShallowWater, TileType.River, TileType.Reef);
        int cold       = Count(TileType.Ice, TileType.Tundra, TileType.Taiga);
        int trueDesert = Count(TileType.Arid);
        int beachSand  = Count(TileType.Sand);
        int grazeable  = Count(TileType.Grass, TileType.Savanna, TileType.Shrubland, TileType.Steppe,
                              TileType.Forest, TileType.Jungle, TileType.Taiga, TileType.Tundra);
        int wetland    = Count(TileType.Wetland, TileType.Bog);

        int riverTiles = Count(TileType.River);
        sb.AppendLine("# River connectivity");
        sb.AppendLine($"  river tiles: {riverTiles}   outlet tiles touching the sea: {riverOutlets}");
        if (riverTiles > 0 && riverOutlets == 0)
            sb.AppendLine("  WARNING: no river reaches the sea — rivers are severed from open water.");
        sb.AppendLine();

        sb.AppendLine("# Niche coverage (gates which specialists have a home)");
        AppendNiche(sb, "open water  (Shark — needs connected deep water)", Pct(openWater));
        AppendNiche(sb, "all water   (Fish/Croc/Penguin food)", Pct(allWater));
        AppendNiche(sb, "cold        (Penguin/Polar Bear/Arctic Fox/Musk Ox)", Pct(cold));
        AppendNiche(sb, "true desert (Arid — Scorpion/Camel/Lizard/Snake)", Pct(trueDesert));
        AppendNiche(sb, "shoreline sand (mostly beach rings, NOT desert)", Pct(beachSand));
        AppendNiche(sb, "wetland     (Frog/Crocodile)", Pct(wetland));
        AppendNiche(sb, "grazeable   (herbivore base)", Pct(grazeable));
        sb.AppendLine();
        sb.AppendLine($"# Flags ( < {MinNichePct:0.0}% coverage = specialist likely can't sustain a population )");
        FlagNiche(sb, "cold belt",          Pct(cold));
        FlagNiche(sb, "true desert (Arid)", Pct(trueDesert));
        FlagNiche(sb, "open water (deep)",  Pct(openWater));
        FlagNiche(sb, "wetland",            Pct(wetland));
        if (beachSand > trueDesert * 3 && Pct(trueDesert) < MinNichePct)
            sb.AppendLine($"  NOTE: {Pct(beachSand):0.0}% Sand is almost all shoreline beach — desert " +
                          "specialists need Arid, which is effectively absent.");

        return sb.ToString();
    }

    private static void AppendNiche(StringBuilder sb, string label, float pct)
        => sb.AppendLine($"  {label,-46} {pct,6:0.0}%");

    private static void FlagNiche(StringBuilder sb, string label, float pct)
    {
        if (pct < MinNichePct)
            sb.AppendLine($"  WARNING: {label} only {pct:0.0}% — too small to support its specialists.");
    }
}
