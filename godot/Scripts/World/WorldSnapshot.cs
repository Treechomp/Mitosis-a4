using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using Mitosis.ECS;
using static Mitosis.ECS.EntityManager;

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

    /// <summary>Invariant-culture interpolation — filenames and reports must not pick up a
    /// comma-decimal locale (same convention as the EcosystemLogger CSVs).</summary>
    private static string Inv(FormattableString f) => FormattableString.Invariant(f);

    /// <summary>
    /// Dump the world diagnostics. Returns the snapshot base name so the post-spawn map
    /// (<see cref="CaptureSpawns"/>) can pair its file with this generation's set.
    /// Written files: biome map, elevation map (stored elevation — ridges/cliffs/detail are
    /// visible), moisture map, temperature map, and the parameter/distribution report.
    /// </summary>
    public static string Capture(
        WorldManager wm, int seed, int chunkSize, int worldSizeChunks,
        TerrainSettings settings, float elevHeightScale,
        string logDir = "res://logs")
    {
        int n = wm.WorldSizeTiles;
        int px = PixelScale(n);

        var img      = Image.CreateEmpty(n * px, n * px, false, Image.Format.Rgb8);
        var elevImg  = Image.CreateEmpty(n * px, n * px, false, Image.Format.Rgb8);
        var moistImg = Image.CreateEmpty(n * px, n * px, false, Image.Format.Rgb8);
        var tempImg  = Image.CreateEmpty(n * px, n * px, false, Image.Format.Rgb8);
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

                PlotTile(img,      tx, ty, px, Chunk.GetTileColor(tile));
                PlotTile(elevImg,  tx, ty, px, ElevColor(wm.GetVertexElevation(tx, ty)));
                PlotTile(moistImg, tx, ty, px, MoistColor(wm.GetVertexMoisture(tx, ty)));
                PlotTile(tempImg,  tx, ty, px, TempColor(wm.GetVertexTemperature(tx, ty)));
            }
        }

        string dir = ProjectSettings.GlobalizePath(logDir);
        Directory.CreateDirectory(dir);
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName =
            Inv($"world_{ts}_seed{seed}_{worldSizeChunks}ch_ef{settings.ElevationFrequency:0.####}") +
            Inv($"_df{settings.DetailFrequency:0.####}_rf{settings.RoughnessFrequency:0.####}") +
            Inv($"_ra{settings.RidgeAmplitude:0.####}");

        string pngPath = Path.Combine(dir, baseName + ".png");
        SavePng(img, pngPath);
        SavePng(elevImg,  Path.Combine(dir, baseName + "_elev.png"));
        SavePng(moistImg, Path.Combine(dir, baseName + "_moist.png"));
        SavePng(tempImg,  Path.Combine(dir, baseName + "_temp.png"));

        File.WriteAllText(Path.Combine(dir, baseName + ".txt"),
            BuildReport(ts, seed, chunkSize, worldSizeChunks, n,
                        settings, elevHeightScale, hist, riverOutlets));

        GD.Print("=========================================");
        GD.Print($"  WORLD SNAPSHOT: {pngPath}");
        GD.Print($"  + _elev/_moist/_temp maps, biome report {baseName}.txt");
        GD.Print("=========================================");
        return baseName;
    }

    /// <summary>
    /// Dump the initial-population map: every renderable entity (creatures, nests, crystals,
    /// the player) as a dot in its species colour over a dimmed biome map. Call right after
    /// world spawning; pairs with the <see cref="Capture"/> files via <paramref name="baseName"/>.
    /// For validating spawn distribution (niche placement, herd spread, faction seeding).
    /// </summary>
    public static void CaptureSpawns(
        WorldManager wm, EntityManager em, string baseName, string logDir = "res://logs")
    {
        int n = wm.WorldSizeTiles;
        int px = PixelScale(n);

        var img = Image.CreateEmpty(n * px, n * px, false, Image.Format.Rgb8);
        for (int ty = 0; ty < n; ty++)
            for (int tx = 0; tx < n; tx++)
                PlotTile(img, tx, ty, px, Chunk.GetTileColor(wm.GetTile(tx, ty)) * 0.30f);

        int size = n * px;
        int dot = Math.Max(2, px + 1); // dot radius in pixels
        foreach (int entity in em.Query(ComponentFlags.Position | ComponentFlags.Renderable))
        {
            var pos = em.Positions[entity];
            Color col = em.Renderables[entity].Color;
            int cx = (int)(pos.X * px);
            int cy = (int)(pos.Y * px);
            for (int dy = -dot / 2; dy <= dot / 2; dy++)
            {
                for (int dx = -dot / 2; dx <= dot / 2; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                        img.SetPixel(x, y, col);
                }
            }
        }

        string dir = ProjectSettings.GlobalizePath(logDir);
        Directory.CreateDirectory(dir);
        SavePng(img, Path.Combine(dir, baseName + "_spawns.png"));
        GD.Print($"  spawn map:      {baseName}_spawns.png");
    }

    /// <summary>Upscale small worlds so the PNG is legible; cap big worlds at 1px/tile.</summary>
    private static int PixelScale(int worldTiles)
        => worldTiles <= 384 ? 3 : (worldTiles <= 768 ? 2 : 1);

    private static void PlotTile(Image img, int tx, int ty, int px, Color col)
    {
        for (int dy = 0; dy < px; dy++)
            for (int dx = 0; dx < px; dx++)
                img.SetPixel(tx * px + dx, ty * px + dy, col);
    }

    private static void SavePng(Image img, string path)
    {
        Error err = img.SavePng(path);
        if (err != Error.Ok)
            GD.PushWarning($"[WorldSnapshot] PNG save failed ({err}) at {path}");
    }

    /// <summary>Stored elevation: sea by depth (blue), land dark→white with altitude.</summary>
    private static Color ElevColor(float e)
    {
        if (e < 0.40f)
        {
            float t = Math.Clamp((0.40f - e) / 0.30f, 0f, 1f);
            return new Color(0.30f, 0.55f, 0.72f).Lerp(new Color(0.05f, 0.13f, 0.38f), t);
        }
        float g = Math.Clamp((e - 0.40f) / 0.55f, 0f, 1f);
        float v = 0.15f + 0.85f * g;
        return new Color(v, v, v);
    }

    /// <summary>Moisture: dry tan → wet deep teal.</summary>
    private static Color MoistColor(float m)
        => new Color(0.78f, 0.66f, 0.40f).Lerp(new Color(0.05f, 0.35f, 0.55f), Math.Clamp(m, 0f, 1f));

    /// <summary>Temperature: cold blue → pale mid → hot red.</summary>
    private static Color TempColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var cold = new Color(0.25f, 0.45f, 0.85f);
        var mid  = new Color(0.90f, 0.90f, 0.75f);
        var hot  = new Color(0.85f, 0.22f, 0.10f);
        return t < 0.5f ? cold.Lerp(mid, t * 2f) : mid.Lerp(hot, (t - 0.5f) * 2f);
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
        sb.AppendLine(Inv($"elevation_frequency:     {s.ElevationFrequency}   (lower = larger landmasses)"));
        sb.AppendLine(Inv($"elevation_height_scale:  {elevHeightScale}"));
        sb.AppendLine(Inv($"warp_amplitude:          {s.WarpAmplitude}"));
        sb.AppendLine(Inv($"detail_frequency:        {s.DetailFrequency}   amplitude: {s.DetailAmplitude}"));
        sb.AppendLine(Inv($"roughness_frequency:     {s.RoughnessFrequency}   floor: {s.RoughnessFloor}"));
        sb.AppendLine(Inv($"ridge_frequency:         {s.RidgeFrequency}   amplitude: {s.RidgeAmplitude}   orogeny_freq: {s.OrogenyFrequency}"));
        sb.AppendLine(Inv($"cliff_frequency:         {s.CliffFrequency}   strength: {s.CliffStrength}   step: {s.CliffStepHeight}"));
        sb.AppendLine($"moisture_frequency:      0.008 (fixed in TerrainGenerator)");
        sb.AppendLine($"temperature_frequency:   0.005 (fixed in TerrainGenerator)");
        sb.AppendLine();

        sb.AppendLine("# Biome distribution (tile type : count : percent), most common first");
        var rows = new List<KeyValuePair<TileType, int>>(hist);
        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (var kv in rows)
            sb.AppendLine(Inv($"  {kv.Key,-13} {kv.Value,8} {Pct(kv.Value),6:0.0}%"));
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
        sb.AppendLine(Inv($"# Flags ( < {MinNichePct:0.0}% coverage = specialist likely can't sustain a population )"));
        FlagNiche(sb, "cold belt",          Pct(cold));
        FlagNiche(sb, "true desert (Arid)", Pct(trueDesert));
        FlagNiche(sb, "open water (deep)",  Pct(openWater));
        FlagNiche(sb, "wetland",            Pct(wetland));
        if (beachSand > trueDesert * 3 && Pct(trueDesert) < MinNichePct)
            sb.AppendLine(Inv($"  NOTE: {Pct(beachSand):0.0}% Sand is almost all shoreline beach — desert ") +
                          "specialists need Arid, which is effectively absent.");

        return sb.ToString();
    }

    private static void AppendNiche(StringBuilder sb, string label, float pct)
        => sb.AppendLine(Inv($"  {label,-46} {pct,6:0.0}%"));

    private static void FlagNiche(StringBuilder sb, string label, float pct)
    {
        if (pct < MinNichePct)
            sb.AppendLine(Inv($"  WARNING: {label} only {pct:0.0}% — too small to support its specialists."));
    }
}
