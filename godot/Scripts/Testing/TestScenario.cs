using System;
using System.Collections.Generic;
using System.Globalization;
using Mitosis.Components;
using Mitosis.SpeciesData;
using Mitosis.World;

namespace Mitosis.Testing;

/// <summary>
/// A parsed test-scenario definition: a small, exactly-specified world + spawn list + logging
/// configuration, used by <c>TestSceneManager</c> to build repeatable scenes for testing
/// individual game systems (hunting, fleeing, terraforming, nutrition, factions…).
///
/// File format — INI-style sections, '#'/';' comments, all keys optional:
///
///   [world]     size_chunks, seed, base_tile, elevation, tps, max_population,
///               disabled_species, no_factions, lod_override, name
///   [terrain]   shape ops painted over the base fill, one per line:
///                 rect   &lt;Tile&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt;
///                 circle &lt;Tile&gt; &lt;cx&gt; &lt;cy&gt; &lt;r&gt;
///                 band   &lt;Tile&gt; x|y &lt;from&gt; &lt;to&gt;      (full-width/height strip)
///   [map]       ASCII tile painting, one row per line ('origin = x,y' to offset; space =
///               keep underlying tile). Char meanings come from [legend] / defaults.
///   [legend]    &lt;char&gt; = &lt;Tile&gt;   overrides/extends the default legend
///   [spawn]     one spawn op per line:
///                 &lt;Species&gt; x&lt;count&gt; @ &lt;x&gt;,&lt;y&gt; [r&lt;radius&gt;] [group | groups=&lt;n&gt;]
///                 nest @ &lt;x&gt;,&lt;y&gt; [colony=&lt;id&gt;] [sectids=&lt;n&gt;]
///                 crystal @ &lt;x&gt;,&lt;y&gt;
///                 heart   @ &lt;x&gt;,&lt;y&gt;   (Shroomer mycelium heart)
///                 spore @ &lt;x&gt;,&lt;y&gt; [x&lt;count&gt;] [r&lt;radius&gt;] [parent=&lt;Species&gt;]
///   [logging]   decisions, decision_species, terrain_interval, nutrition_interval,
///               snapshot_interval, track
///
/// See docs/implementation/tooling-and-tests.md and godot/TestScenarios/ for worked examples.
/// </summary>
public sealed class TestScenario
{
    // ── [world] ───────────────────────────────────────────────────────────────
    public string Name = "unnamed";
    public int SizeChunks = 4;              // 4 chunks = 128×128 tiles
    public int Seed = 12345;                // stat-variation rng; 0 = random per run
    public TileType BaseTile = TileType.Grass;
    public float LandElevation = 0.5f;      // clamped into ScenarioTileParams' safe band
    public int Tps = 20;
    public int MaxPopulation = 4000;
    public string DisabledSpecies = "";
    public bool NoFactions;

    /// <summary>
    /// Force every entity onto one LOD tier regardless of its distance from the player, or
    /// null (the 'none' default) for the normal distance-based assignment.
    ///
    /// Tier boundaries are multiples of the camera's visible radius (69 tiles at the default
    /// radius of 60), and the largest scenario world is 128 tiles across — so its greatest
    /// possible distance-to-player is ~181 tiles and the Low and Minimal tiers are simply
    /// unreachable here. Without this key the harness cannot observe the tiers that hold most
    /// of a real world's population (69% Minimal / 16% Low on a profiled 36-chunk run), which
    /// is exactly where LOD rate-compensation bugs live. See LODSystem.SetLevelOverride.
    /// </summary>
    public LODLevel? LodOverride;

    // ── [terrain] / [map] ─────────────────────────────────────────────────────
    public readonly List<TerrainOp> TerrainOps = new();
    public readonly List<string> MapRows = new();
    public int MapOriginX;
    public int MapOriginY;
    public readonly Dictionary<char, TileType> Legend = new(DefaultLegend);

    // ── [spawn] ───────────────────────────────────────────────────────────────
    public readonly List<SpawnOp> Spawns = new();

    // ── [logging] ─────────────────────────────────────────────────────────────
    public bool LogDecisions = true;
    public string DecisionSpecies = "";     // comma-separated; empty = all species
    public int TerrainLogInterval = 200;    // ticks; 0 = off
    public int NutritionLogInterval = 200;  // ticks; 0 = off
    public int SnapshotInterval = 50;       // population/stats CSV cadence
    public string TrackSpecies = "";        // per-entity TRACKED logging (existing feature)

    /// <summary>Non-fatal parse problems (unknown keys/tiles/species, malformed lines).</summary>
    public readonly List<string> Warnings = new();

    public int WorldSizeTiles(int chunkSize) => SizeChunks * chunkSize;

    /// <summary>Default ASCII-map legend. A [legend] section overrides/extends these.</summary>
    public static readonly Dictionary<char, TileType> DefaultLegend = new()
    {
        ['~'] = TileType.DeepWater,
        ['='] = TileType.ShallowWater,
        ['r'] = TileType.River,
        ['.'] = TileType.Grass,
        [','] = TileType.Shrubland,
        ['f'] = TileType.Forest,
        ['w'] = TileType.Wetland,
        ['B'] = TileType.Bog,
        ['m'] = TileType.Mountain,
        ['s'] = TileType.Sand,
        ['d'] = TileType.Dirt,
        ['a'] = TileType.Arid,
        ['t'] = TileType.Tundra,
        ['p'] = TileType.Steppe,
        ['g'] = TileType.Taiga,
        ['i'] = TileType.Ice,
        ['v'] = TileType.Savanna,
        ['j'] = TileType.Jungle,
        ['L'] = TileType.Lava,
        ['R'] = TileType.Reef,
    };

    // ══════════════════════════════════════════════════════════════════════════
    // Parsing
    // ══════════════════════════════════════════════════════════════════════════

    public static TestScenario Parse(string text, string name)
    {
        var s = new TestScenario { Name = name };
        string section = "";

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            // Map rows keep leading/trailing spaces (space = transparent); everything else trims.
            bool inMap = section == "map";
            string trimmed = line.Trim();

            if (!inMap || trimmed.StartsWith('[') || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) continue;
                if (trimmed.StartsWith('[') && trimmed.EndsWith("]"))
                {
                    section = trimmed[1..^1].Trim().ToLowerInvariant();
                    continue;
                }
                // Strip inline comments ("value   # note") — a '#' preceded by whitespace.
                // Map rows are exempt (their characters are data); a legend line can't use
                // '#' as a paint char since a leading '#' is a whole-line comment.
                trimmed = StripInlineComment(trimmed);
            }

            switch (section)
            {
                case "world":   s.ParseWorldLine(trimmed);   break;
                case "terrain": s.ParseTerrainLine(trimmed); break;
                case "legend":  s.ParseLegendLine(trimmed);  break;
                case "spawn":   s.ParseSpawnLine(trimmed);   break;
                case "logging": s.ParseLoggingLine(trimmed); break;
                case "map":     s.ParseMapLine(line);        break;
                default:
                    s.Warnings.Add($"Line outside a known section ignored: '{trimmed}'");
                    break;
            }
        }

        // Blank lines between the last map row and the next section are separators, not
        // transparent rows (interior blank rows ARE kept — they mean a full base-tile row).
        while (s.MapRows.Count > 0 && s.MapRows[^1].Trim().Length == 0)
            s.MapRows.RemoveAt(s.MapRows.Count - 1);

        return s;
    }

    private static string StripInlineComment(string line)
    {
        for (int i = 1; i < line.Length; i++)
        {
            if (line[i] == '#' && char.IsWhiteSpace(line[i - 1]))
                return line[..i].TrimEnd();
        }
        return line;
    }

    private void ParseWorldLine(string line)
    {
        if (!SplitKeyValue(line, out var key, out var value))
        {
            Warnings.Add($"[world] expected 'key = value': '{line}'");
            return;
        }
        switch (key)
        {
            case "name":             Name = value; break;
            case "size_chunks":      SizeChunks = ParseInt(value, SizeChunks, key); break;
            case "seed":             Seed = ParseInt(value, Seed, key); break;
            case "base_tile":        BaseTile = ParseTile(value) ?? BaseTile; break;
            case "elevation":        LandElevation = ParseFloat(value, LandElevation, key); break;
            case "tps":              Tps = ParseInt(value, Tps, key); break;
            case "max_population":   MaxPopulation = ParseInt(value, MaxPopulation, key); break;
            case "disabled_species": DisabledSpecies = value; break;
            case "no_factions":      NoFactions = ParseBool(value); break;
            case "lod_override":     LodOverride = ParseLodOverride(value); break;
            default: Warnings.Add($"[world] unknown key '{key}'"); break;
        }
    }

    private void ParseTerrainLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { Warnings.Add($"[terrain] malformed op: '{line}'"); return; }

        var tile = ParseTile(parts[1]);
        if (tile == null) return; // ParseTile already warned

        switch (parts[0].ToLowerInvariant())
        {
            case "rect" when parts.Length >= 6:
                TerrainOps.Add(TerrainOp.Rect(tile.Value,
                    ParseInt(parts[2], 0, "rect x"), ParseInt(parts[3], 0, "rect y"),
                    ParseInt(parts[4], 0, "rect w"), ParseInt(parts[5], 0, "rect h")));
                break;
            case "circle" when parts.Length >= 5:
                TerrainOps.Add(TerrainOp.Circle(tile.Value,
                    ParseInt(parts[2], 0, "circle cx"), ParseInt(parts[3], 0, "circle cy"),
                    ParseInt(parts[4], 0, "circle r")));
                break;
            case "band" when parts.Length >= 5:
                bool xAxis = parts[2].Equals("x", StringComparison.OrdinalIgnoreCase);
                TerrainOps.Add(TerrainOp.Band(tile.Value, xAxis,
                    ParseInt(parts[3], 0, "band from"), ParseInt(parts[4], 0, "band to")));
                break;
            default:
                Warnings.Add($"[terrain] unknown/malformed op: '{line}'");
                break;
        }
    }

    private void ParseLegendLine(string line)
    {
        if (!SplitKeyValue(line, out var key, out var value) || key.Length != 1)
        {
            Warnings.Add($"[legend] expected '<char> = <Tile>': '{line}'");
            return;
        }
        var tile = ParseTile(value);
        if (tile != null) Legend[key[0]] = tile.Value;
    }

    private void ParseMapLine(string line)
    {
        // 'origin = x,y' offsets where the ASCII rows are stamped into the world.
        string trimmed = line.Trim();
        if (trimmed.StartsWith("origin", StringComparison.OrdinalIgnoreCase)
            && SplitKeyValue(trimmed, out _, out var value))
        {
            var xy = value.Split(',');
            if (xy.Length == 2)
            {
                MapOriginX = ParseInt(xy[0].Trim(), 0, "map origin x");
                MapOriginY = ParseInt(xy[1].Trim(), 0, "map origin y");
                return;
            }
        }
        if (trimmed.Length == 0 && MapRows.Count == 0) return; // leading blank lines
        MapRows.Add(line);
    }

    private void ParseSpawnLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { Warnings.Add($"[spawn] malformed line: '{line}'"); return; }

        var op = new SpawnOp();
        string head = parts[0];

        if (head.Equals("nest", StringComparison.OrdinalIgnoreCase))
            op.Kind = SpawnKind.Nest;
        else if (head.Equals("crystal", StringComparison.OrdinalIgnoreCase))
            op.Kind = SpawnKind.Crystal;
        else if (head.Equals("spore", StringComparison.OrdinalIgnoreCase))
            op.Kind = SpawnKind.Spore;
        else if (head.Equals("heart", StringComparison.OrdinalIgnoreCase))
            op.Kind = SpawnKind.MyceliumHeart;
        else
        {
            op.Kind = SpawnKind.Creature;
            op.Species = ResolveSpeciesName(head);
            if (op.Species == null)
            {
                Warnings.Add($"[spawn] unknown species '{head}' — line skipped");
                return;
            }
        }

        bool havePos = false;
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p == "@") continue;

            if (p.StartsWith('@')) p = p[1..];

            if (p.Contains(',') && !p.Contains('='))
            {
                var xy = p.Split(',');
                if (xy.Length == 2)
                {
                    op.X = ParseFloat(xy[0], 0f, "spawn x");
                    op.Y = ParseFloat(xy[1], 0f, "spawn y");
                    havePos = true;
                }
            }
            else if (p.Length > 1 && (p[0] == 'x' || p[0] == 'X') && char.IsDigit(p[1]))
                op.Count = ParseInt(p[1..], 1, "spawn count");
            else if (p.Length > 1 && (p[0] == 'r' || p[0] == 'R') && char.IsDigit(p[1]))
                op.Radius = ParseFloat(p[1..], 0f, "spawn radius");
            else if (p.Equals("group", StringComparison.OrdinalIgnoreCase))
                op.Groups = 1;
            else if (p.StartsWith("groups=", StringComparison.OrdinalIgnoreCase))
                op.Groups = ParseInt(p["groups=".Length..], 0, "spawn groups");
            else if (p.StartsWith("colony=", StringComparison.OrdinalIgnoreCase))
                op.ColonyId = ParseInt(p["colony=".Length..], 1, "nest colony");
            else if (p.StartsWith("sectids=", StringComparison.OrdinalIgnoreCase))
                op.StarterSectids = ParseInt(p["sectids=".Length..], 0, "nest sectids");
            else if (p.StartsWith("parent=", StringComparison.OrdinalIgnoreCase))
                op.Species = ResolveSpeciesName(p["parent=".Length..]);
            else
                Warnings.Add($"[spawn] unknown token '{p}' in '{line}'");
        }

        if (!havePos)
        {
            Warnings.Add($"[spawn] missing '@ x,y' position: '{line}' — line skipped");
            return;
        }
        Spawns.Add(op);
    }

    private void ParseLoggingLine(string line)
    {
        if (!SplitKeyValue(line, out var key, out var value))
        {
            Warnings.Add($"[logging] expected 'key = value': '{line}'");
            return;
        }
        switch (key)
        {
            case "decisions":          LogDecisions = ParseBool(value); break;
            case "decision_species":   DecisionSpecies = value; break;
            case "terrain_interval":   TerrainLogInterval = ParseInt(value, TerrainLogInterval, key); break;
            case "nutrition_interval": NutritionLogInterval = ParseInt(value, NutritionLogInterval, key); break;
            case "snapshot_interval":  SnapshotInterval = ParseInt(value, SnapshotInterval, key); break;
            case "track":              TrackSpecies = value; break;
            default: Warnings.Add($"[logging] unknown key '{key}'"); break;
        }
    }

    // ── Small parse helpers ───────────────────────────────────────────────────

    private static bool SplitKeyValue(string line, out string key, out string value)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) { key = ""; value = ""; return false; }
        key = line[..eq].Trim().ToLowerInvariant();
        value = line[(eq + 1)..].Trim();
        return key.Length > 0;
    }

    private int ParseInt(string value, int fallback, string what)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return v;
        Warnings.Add($"'{what}': not an integer: '{value}' (using {fallback})");
        return fallback;
    }

    private float ParseFloat(string value, float fallback, string what)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            return v;
        Warnings.Add($"'{what}': not a number: '{value}' (using {fallback})");
        return fallback;
    }

    private static bool ParseBool(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || value == "1" || value.Equals("on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parse a [world] lod_override value: a tier name (Full/High/Medium/Low/Minimal), or
    /// 'none' (equivalently 'off'/empty) for the normal distance-based assignment. Anything
    /// else warns and falls back to 'none' rather than silently testing a tier nobody asked for.
    /// </summary>
    private LODLevel? ParseLodOverride(string value)
    {
        if (value.Length == 0
            || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;

        // Enum.TryParse also accepts raw numbers ("3") and undefined values, so the
        // IsDefined check is what actually restricts this to the five named tiers.
        if (Enum.TryParse<LODLevel>(value, ignoreCase: true, out var level) && Enum.IsDefined(level))
            return level;

        Warnings.Add($"[world] lod_override: unknown tier '{value}' " +
                     "(expected Full, High, Medium, Low, Minimal or none) — using none");
        return null;
    }

    private TileType? ParseTile(string name)
    {
        if (Enum.TryParse<TileType>(name, ignoreCase: true, out var tile))
            return tile;
        Warnings.Add($"Unknown tile type '{name}'");
        return null;
    }

    /// <summary>Case-insensitive species-name resolution against the registry ("polar bear" → "Polar Bear").</summary>
    private string? ResolveSpeciesName(string name)
    {
        foreach (var registered in SpeciesRegistry.GetAllNames())
            if (registered.Equals(name, StringComparison.OrdinalIgnoreCase))
                return registered;
        // Allow underscores in place of spaces so spawn tokens don't need quoting.
        string spaced = name.Replace('_', ' ');
        foreach (var registered in SpeciesRegistry.GetAllNames())
            if (registered.Equals(spaced, StringComparison.OrdinalIgnoreCase))
                return registered;
        return null;
    }
}

/// <summary>A terrain paint operation from a scenario's [terrain] section.</summary>
public readonly struct TerrainOp
{
    public enum Shape { Rect, Circle, Band }

    public readonly Shape Kind;
    public readonly TileType Tile;
    public readonly int A, B, C, D; // rect: x,y,w,h · circle: cx,cy,r,- · band: axis(0=x),from,to,-

    private TerrainOp(Shape kind, TileType tile, int a, int b, int c, int d)
    {
        Kind = kind; Tile = tile; A = a; B = b; C = c; D = d;
    }

    public static TerrainOp Rect(TileType t, int x, int y, int w, int h) => new(Shape.Rect, t, x, y, w, h);
    public static TerrainOp Circle(TileType t, int cx, int cy, int r)    => new(Shape.Circle, t, cx, cy, r, 0);
    public static TerrainOp Band(TileType t, bool xAxis, int from, int to)
        => new(Shape.Band, t, xAxis ? 0 : 1, from, to, 0);
}

public enum SpawnKind { Creature, Nest, Crystal, Spore, MyceliumHeart }

/// <summary>A spawn operation from a scenario's [spawn] section.</summary>
public sealed class SpawnOp
{
    public SpawnKind Kind;
    public string? Species;      // creature species, or spore parent (defaults to Shroomer)
    public int Count = 1;
    public float X, Y;
    public float Radius;         // scatter radius around (X, Y); 0 = exact point
    public int Groups;           // 0 = scattered individuals; 1 = one group; n = n groups
    public int ColonyId = 1;     // nests
    public int StarterSectids;   // nests: sectids spawned around the nest
}
