using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Mitosis.Systems;
using Mitosis.SpeciesData;
using Mitosis.World;

namespace Mitosis.Testing;

/// <summary>
/// Test-scene harness (Scenes/TestScene.tscn, run with F6): builds a small, exactly-specified
/// world from a scenario file instead of noise worldgen, spawns exactly the creatures the
/// scenario lists, and enables the extended system logs (decisions / terrain / nutrition).
/// The full per-tick system stack is the REAL one — same systems, same order as GameManager —
/// so behavior observed here is the behavior of the live game, just on a controlled stage.
///
/// Runtime controls (also shown in the debug overlay):
///   Space       pause / resume            .  single-step one tick (pauses first)
///   ,           cycle simulation speed (20 / 60 / 180 / 5 TPS)
///   P           toggle terrain paint mode
///     [ ]         previous / next brush tile
///     1-5         brush radius 0 / 1 / 2 / 4 / 8
///     LMB drag    paint
///   O           export current terrain as a scenario-ready ASCII map to the logs directory
/// </summary>
public partial class TestSceneManager : GameManager
{
    /// <summary>Scenario file to load (see docs/implementation/tooling-and-tests.md for the format).</summary>
    [Export(PropertyHint.File, "*.txt")]
    public string ScenarioPath = "res://TestScenarios/predator_prey.scenario.txt";

    /// <summary>Inline scenario text — when non-empty it takes precedence over ScenarioPath.</summary>
    [Export(PropertyHint.MultilineText)]
    public string InlineScenario = "";

    private TestScenario _scenario = null!;

    // Paint mode
    private bool _paintMode;
    private bool _painting;
    private static readonly TileType[] BrushTiles = Enum.GetValues<TileType>();
    private int _brushTileIndex = (int)TileType.Grass;
    private static readonly int[] BrushRadii = { 0, 1, 2, 4, 8 };
    private int _brushRadiusIndex;

    // Speed cycling (comma key)
    private static readonly int[] SpeedStepsTps = { 20, 60, 180, 5 };
    private int _speedIndex;

    public override void _Ready()
    {
        _scenario = LoadScenario();
        foreach (var warning in _scenario.Warnings)
            GD.PushWarning($"[Scenario] {warning}");

        // Apply the scenario to the GameManager exports BEFORE base._Ready() builds the world.
        WorldSizeChunks = Math.Max(1, _scenario.SizeChunks);
        WorldSeed = _scenario.Seed;
        TargetTPS = _scenario.Tps;
        MaxPopulation = _scenario.MaxPopulation;
        DisabledSpecies = _scenario.DisabledSpecies;
        DisableFactionSpecies = _scenario.NoFactions;
        if (string.IsNullOrWhiteSpace(TrackSpecies))
            TrackSpecies = _scenario.TrackSpecies;

        // Extended logging configuration — must be set before the EcosystemLogger is
        // constructed (its extra CSV files are only opened when enabled).
        EcosystemLogger.SnapshotInterval = Math.Max(1, _scenario.SnapshotInterval);
        EcosystemLogger.DecisionLoggingEnabled = _scenario.LogDecisions;
        EcosystemLogger.DecisionSpeciesFilter = ParseDecisionFilter(_scenario.DecisionSpecies);
        EcosystemLogger.TerrainLogInterval = Math.Max(0, _scenario.TerrainLogInterval);
        EcosystemLogger.NutritionLogInterval = Math.Max(0, _scenario.NutritionLogInterval);

        GD.Print($"[TestScene] Scenario '{_scenario.Name}': {WorldSizeChunks} chunks " +
                 $"({WorldSizeChunks * ChunkSize}x{WorldSizeChunks * ChunkSize} tiles), " +
                 $"base {_scenario.BaseTile}, {_scenario.Spawns.Count} spawn ops");

        base._Ready();

        // LOD tier override — applied after the stack is built, since LODSystem is constructed
        // in base._Ready(). It has to be an override rather than a camera trick: tier boundaries
        // are multiples of the visible radius, so a scenario world (96-128 tiles) cannot put an
        // entity further than Medium away from a centred player. See LODSystem.SetLevelOverride.
        if (_scenario.LodOverride.HasValue)
        {
            _lodSystem?.SetLevelOverride(_scenario.LodOverride.Value);
            GD.Print($"[TestScene] lod_override = {_scenario.LodOverride.Value} " +
                     $"(every entity forced to that tier, interval " +
                     $"{Components.SimulationLOD.GetTickInterval(_scenario.LodOverride.Value)})");
        }

        GD.Print("[TestScene] Controls: Space=pause  .=step  ,=speed  P=paint  O=export map");
    }

    protected override WorldManager CreateWorld(int seed, TerrainSettings terrainSettings)
        => new(ChunkSize, WorldSizeChunks, seed, new ScenarioTerrainGenerator(_scenario, ChunkSize));

    protected override void PopulateWorld()
    {
        GD.Print("[TestScene] Executing scenario spawn list...");
        var spawner = new ScenarioSpawner(_worldManager, _entityFactory, _entityManager, _rng);
        int creatures = spawner.Run(_scenario, _nestSystem, _crystalSystem, _sporeSystem);
        GD.Print($"[TestScene] Spawned {creatures} creatures from scenario");
    }

    // ── Scenario loading ──────────────────────────────────────────────────────

    private TestScenario LoadScenario()
    {
        if (!string.IsNullOrWhiteSpace(InlineScenario))
            return TestScenario.Parse(InlineScenario, "inline");

        string resolved = ProjectSettings.GlobalizePath(ScenarioPath);
        if (File.Exists(resolved))
            return TestScenario.Parse(File.ReadAllText(resolved),
                Path.GetFileNameWithoutExtension(resolved));

        GD.PushWarning($"[TestScene] Scenario file not found: '{ScenarioPath}' — using built-in default");
        return TestScenario.Parse(DefaultScenario, "builtin_default");
    }

    /// <summary>Minimal fallback so the scene always runs, even with no scenario file.</summary>
    private const string DefaultScenario = """
        [world]
        size_chunks = 3
        base_tile = Grass

        [spawn]
        Deer x20 @ 40,40 r12 group
        Wolf x4 @ 70,70 r4 group
        """;

    private static HashSet<int>? ParseDecisionFilter(string speciesCsv)
    {
        if (string.IsNullOrWhiteSpace(speciesCsv)) return null;
        var filter = new HashSet<int>();
        foreach (var raw in speciesCsv.Split(','))
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            bool known = false;
            foreach (var registered in SpeciesRegistry.GetAllNames())
            {
                if (registered.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    filter.Add(SpeciesRegistry.GetId(registered));
                    known = true;
                    break;
                }
            }
            if (!known)
                GD.PushWarning($"[TestScene] decision_species: unknown species '{name}' ignored");
        }
        return filter.Count > 0 ? filter : null;
    }

    // ── Input: time controls + paint mode ─────────────────────────────────────

    /// <summary>
    /// Paint mode owns the left mouse button, so click-to-inspect steps aside while it is on.
    /// Everything else (species cycling, jump, free camera) stays available throughout.
    /// </summary>
    protected override void HandleObservationInput(InputEvent @event)
    {
        if (_paintMode && @event is InputEventMouseButton) return;
        base.HandleObservationInput(@event);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        base._UnhandledInput(@event); // F3 profiling + wheel zoom + observation tools

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
            HandleKey(key.Keycode);

        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _painting = _paintMode && mb.Pressed;
            if (_painting)
                PaintAtScreen(mb.Position);
        }
        else if (_painting && @event is InputEventMouseMotion motion)
        {
            PaintAtScreen(motion.Position);
        }
    }

    private void HandleKey(Key keycode)
    {
        switch (keycode)
        {
            case Key.Space:
                SimulationPaused = !SimulationPaused;
                break;

            case Key.Period:
                SimulationPaused = true;
                QueueSingleStep();
                break;

            case Key.Comma:
                _speedIndex = (_speedIndex + 1) % SpeedStepsTps.Length;
                SetTargetTps(SpeedStepsTps[_speedIndex]);
                GD.Print($"[TestScene] TPS -> {TargetTPS}");
                break;

            case Key.P:
                _paintMode = !_paintMode;
                _painting = false;
                break;

            case Key.O:
                ExportAsciiMap();
                break;

            case Key.Bracketleft when _paintMode:
                _brushTileIndex = (_brushTileIndex - 1 + BrushTiles.Length) % BrushTiles.Length;
                break;

            case Key.Bracketright when _paintMode:
                _brushTileIndex = (_brushTileIndex + 1) % BrushTiles.Length;
                break;

            case >= Key.Key1 and <= Key.Key5 when _paintMode:
                _brushRadiusIndex = (int)(keycode - Key.Key1);
                break;
        }
    }

    // ── Terrain painting ──────────────────────────────────────────────────────

    /// <summary>
    /// Screen position → grid tile: cast the camera ray onto the horizontal plane at the
    /// scenario's land elevation (test terrain is flat, so the plane hit IS the terrain hit),
    /// then paint the brush disc via WorldManager.PaintTile (canonical params + dirty chunk).
    /// </summary>
    private void PaintAtScreen(Vector2 screenPos)
    {
        if (_camera == null) return;

        Vector3 origin = _camera.ProjectRayOrigin(screenPos);
        Vector3 dir = _camera.ProjectRayNormal(screenPos);
        float planeY = ScenarioTileParams.ClampLandElevation(_scenario.LandElevation)
                       * ElevationHeightScale;
        if (Math.Abs(dir.Y) < 1e-5f) return;
        float t = (planeY - origin.Y) / dir.Y;
        if (t < 0f) return;
        Vector3 hit = origin + dir * t;

        var (gridX, gridY) = Utils.GridCoordinates.WorldToGrid(hit.X, hit.Z, TileSize);
        var brush = BrushTiles[_brushTileIndex];
        int radius = BrushRadii[_brushRadiusIndex];

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                _worldManager.PaintTile((int)gridX + dx, (int)gridY + dy, brush);
            }
        }
    }

    /// <summary>
    /// Dump the current terrain as a scenario-ready [map]+[legend] text file into the logs
    /// directory, so a hand-painted layout can be pasted into a scenario and re-run.
    /// </summary>
    private void ExportAsciiMap()
    {
        // Invert the default legend (covers every current TileType; '?' guards future ones).
        var tileToChar = new Dictionary<TileType, char>();
        foreach (var (c, tile) in TestScenario.DefaultLegend)
            tileToChar.TryAdd(tile, c);
        foreach (var tile in Enum.GetValues<TileType>())
            tileToChar.TryAdd(tile, '?');

        int size = _worldManager.WorldSizeTiles;
        var usedTiles = new HashSet<TileType>();
        var sb = new System.Text.StringBuilder(size * (size + 1) + 512);
        sb.AppendLine("# Exported from TestScene paint mode — paste into a scenario file.");
        sb.AppendLine("[map]");
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var tile = _worldManager.GetTile(x, y);
                usedTiles.Add(tile);
                sb.Append(tileToChar[tile]);
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("[legend]");
        foreach (var tile in usedTiles)
            sb.AppendLine($"{tileToChar[tile]} = {tile}");

        string dir = EcosystemLogger.LogDirectory
                     ?? ProjectSettings.GlobalizePath("res://logs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"painted_map_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());
        GD.Print($"[TestScene] Terrain exported: {path}");
    }

    // ── Debug overlay ─────────────────────────────────────────────────────────

    protected override string ExtraDebugText()
    {
        string state = SimulationPaused ? "PAUSED (Space=resume, .=step)" : $"running @ {TargetTPS} TPS";
        string text = $"[TestScene] {_scenario.Name}  |  {state}  |  [,]=speed [P]=paint [O]=export map";
        if (_paintMode)
        {
            text += $"\nPAINT: {BrushTiles[_brushTileIndex]}  r={BrushRadii[_brushRadiusIndex]}" +
                    "  |  [ ]=tile  1-5=radius  LMB=paint";
        }
        return text;
    }
}
