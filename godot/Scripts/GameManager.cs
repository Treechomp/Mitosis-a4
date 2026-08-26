using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Rendering;
using Mitosis.Systems;
using Mitosis.SpeciesData;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;
using Mitosis.Utils;

namespace Mitosis;

/// <summary>
/// Main game manager that orchestrates the simulation.
/// Delegates to EntityFactory (entity creation), WorldSpawner (population distribution),
/// PlayerController (input/camera), and RenderingManager (visuals).
/// Inherits Node3D for full 3D rendering with Camera3D and DirectionalLight3D.
/// </summary>
public partial class GameManager : Node3D
{
    // Configuration
    [Export] public int ChunkSize = 32;
    [Export] public int WorldSizeChunks = 36;  // standard test world (1152×1152 tiles)
    [Export] public int WorldSeed = 0;
    [Export] public int TileSize = 16;
    [Export] public float ElevationHeightScale = 64f;  // World units of vertical lift per elevation unit
    // Terrain noise tuning (see World/TerrainSettings.cs) — editable in the inspector.
    // Frequencies carry explicit Range hints with a 0.0001 step: Godot's default float step
    // is 0.001, which silently rounded fine-grained values (0.0025 → 0.003) on save.
    [Export(PropertyHint.Range, "0.0001,0.05,0.0001")]
    public float ElevationFrequency = 0.003f;                 // lower = larger landmasses
    [Export(PropertyHint.Range, "0,60,0.5")]
    public float WarpAmplitude = 12f;                         // higher = more swirled boundaries
    [Export(PropertyHint.Range, "0.001,0.2,0.0001")]
    public float TerrainDetailFrequency = 0.045f;             // surface relief frequency
    [Export(PropertyHint.Range, "0,0.15,0.001")]
    public float TerrainDetailAmplitude = 0.035f;             // surface relief height (keep < ~0.1)
    [Export(PropertyHint.Range, "0.0001,0.05,0.0001")]
    public float TerrainRoughnessFrequency = 0.006f;          // size of rugged vs smooth regions
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float TerrainRoughnessFloor = 0.15f;               // min detail in smoothest areas (0..1)
    [Export(PropertyHint.Range, "0.0001,0.02,0.0001")]
    public float TerrainMoistureFrequency = 0.002f;           // humid/arid region scale (keep in scale with elevation)
    [Export(PropertyHint.Range, "0.5,3,0.01")]
    public float TerrainMoistureContrast = 1.81f;             // stretch toward wet/dry extremes (1 = raw noise)
    [Export(PropertyHint.Range, "0.0001,0.05,0.0001")]
    public float TerrainRidgeFrequency = 0.010f;              // ridgeline scale (lower = longer ranges)
    [Export(PropertyHint.Range, "0,0.5,0.005")]
    public float TerrainRidgeAmplitude = 0.2f;                // ridge crest height (0 = no ranges)
    [Export(PropertyHint.Range, "0.0001,0.02,0.0001")]
    public float TerrainOrogenyFrequency = 0.003f;            // mountain-belt scale (lower = fewer, larger ranges)
    [Export(PropertyHint.Range, "0.0001,0.05,0.0001")]
    public float TerrainCliffFrequency = 0.0044f;             // size of terraced mesa/bluff regions
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float TerrainCliffStrength = 1.0f;                 // terracing blend in cliff regions (0..1)
    [Export(PropertyHint.Range, "0.01,0.3,0.005")]
    public float TerrainCliffStepHeight = 0.16f;              // elevation per terrace step
    [Export(PropertyHint.Range, "0,2,0.05")]
    public float TerrainRiverDensity = 0.2f;                  // river-source budget scale (1 = the old dense look)
    [Export] public int TargetTPS = 20;
    [Export] public int MaxPopulation = 12000;   // standard test ceiling (36-chunk world)

    // Per-class shares of MaxPopulation. MaxPopulation is an ENGINEERING number — tick + render
    // cost saturates a thread above ~12k creatures — and splitting it per class is what stops it
    // being spent as an ecological force. One shared pool meant every species' births were
    // throttled by one number, which selects for fast breeders and lets the winner hold everyone
    // else at zero; see PopulationBudget for the measurement. The shares deliberately sum to 0.88:
    // the remaining 12% is headroom for spores, structures and transients, and is not allocated to
    // any class.
    [Export] public float HerbivoreBudgetShare = PopulationBudget.DefaultHerbivoreShare;   // ~5000 of 12000
    [Export] public float PredatorBudgetShare  = PopulationBudget.DefaultPredatorShare;    // ~1500
    [Export] public float FactionBudgetShare   = PopulationBudget.DefaultFactionShare;     // ~4000
    [Export] public int InitialPopulation = 2000; // standard test seed population

    /// <summary>
    /// How many times the player may die and come back in one run.
    ///
    /// DELIBERATELY NOT THE STRUCTURE COUNT. Death anchors to a faction structure — nearest nest,
    /// mycelium heart, home crystal — and it is tempting to let "how many anchors exist" be the
    /// answer to "how many continuations do I get". It must not be: crystal count is derived from
    /// map sense-coverage (WorldSpawner.SpawnCrystals) and just rose from 8 to ~132, so tying
    /// lives to anchors would have made the game an order of magnitude more forgiving as a side
    /// effect of a rendering-adjacent fix nobody would think to re-test. Anchors are plentiful;
    /// continuations are three.
    /// </summary>
    [Export] public int FactionLivesPerRun = 3;

    /// <summary>
    /// Faelings in the world, exactly — a crystal holds one Faeling, so this IS the faction's
    /// population. A designed constant on purpose: it was previously derived from keeper sense
    /// coverage, and that derivation answered a PERCEPTION problem with PRESENCE, which is also
    /// force. See WorldSpawner.SpawnCrystals for what 132 immortal raiders did to the map.
    /// </summary>
    [Export] public int FaelingCrystalCount = 12;
    [Export] public float CreaturesPerChunk = 2f;

    // Spectator settings
    [Export] public float PlayerSpeed = 1.0f;
    [Export] public float PlayerSprintMultiplier = 3.0f;
    [Export] public float ZoomMin = 0.1f;   // Multiplied by TileSize*32 for camera size min
    [Export] public float ZoomMax = 5.0f;   // Multiplied by TileSize*32 for camera size max
    [Export] public float ZoomSpeed = 0.15f;

    // Logging — set in Inspector before Play to enable per-entity tracking for one species.
    // Exact species name (e.g. "Wolf", "Sectid", "Deer"). Empty = tracking off.
    [Export] public string TrackSpecies = "";

    // Species toggles — set in Inspector before Play to include/exclude species for a run.
    // Exact species names to DISABLE, separated by comma or newline (e.g. "Shroomer, Wolf").
    // A disabled species never spawns (initial seeding or faction spread). Empty = all enabled.
    [Export(PropertyHint.MultilineText)] public string DisabledSpecies = "";
    // Convenience: disable all faction species (Shroomer, Sectid, Faeling) — e.g. to check whether
    // the ecosystem is viable with no factions present. Combines with DisabledSpecies above.
    [Export] public bool DisableFactionSpecies = false;

    // HerbivoreRatio (0.85), FaelingShare (0.04) and SectidShare (0.10) used to set the starting
    // composition here. They are gone: the seed is now split in the same proportions as the class
    // BUDGETS (PopulationBudget.SplitSeed), so a world starts in the shape its ceilings will let
    // it hold instead of starting at 85% herbivores and spending its first thousands of ticks
    // being filtered toward something else. Faeling numbers are no longer a share at all — see
    // WorldSpawner.SpawnCrystals, where crystal count is derived from map coverage because one
    // crystal sustains exactly one Faeling.

    // Core systems — protected so the test-scene subclass (TestSceneManager) can spawn into
    // and inspect the running simulation.
    protected EntityManager _entityManager = null!;
    protected WorldManager  _worldManager  = null!;
    protected readonly List<ISystem> _systems = new();
    // Assigned in _Ready once the run's seed is known — a field initializer would run before it.
    protected Random _rng = new();
    protected LODSystem?       _lodSystem;
    protected NestSystem?      _nestSystem;
    protected CrystalSystem?   _crystalSystem;
    protected SporeSystem?     _sporeSystem;
    protected EcosystemLogger? _ecosystemLogger;
    protected PopulationBudget? _populationBudget;
    protected FactionLives? _factionLives;

    // Extracted managers
    protected EntityFactory    _entityFactory    = null!;
    protected WorldSpawner     _worldSpawner     = null!;
    protected PlayerController _playerController = null!;
    protected RenderingManager _renderingManager = null!;

    // Simulation timing
    private double _simulationAccumulator;
    private double _simulationDt;

    // Time controls (used by the test-scene subclass): while paused the accumulator is
    // discarded, so unpausing never fast-forwards a backlog of ticks.
    protected bool SimulationPaused;
    private bool _singleStepQueued;

    /// <summary>Run exactly one simulation tick on the next frame while paused.</summary>
    protected void QueueSingleStep() => _singleStepQueued = true;

    /// <summary>Change the simulation rate at runtime (test scenes: fast-forward long runs).</summary>
    protected void SetTargetTps(int tps)
    {
        TargetTPS = Math.Max(1, tps);
        _simulationDt = 1.0 / TargetTPS;
    }

    // Debug info
    private int    _fps;
    private double _fpsTimer;
    private int    _frameCount;
    private int    _herbivoreCount;
    private int    _predatorCount;
    private int    _shroomerCount;
    private int    _sectidCount;
    private int    _faelingCount;
    private int    _nestCount;
    private int    _sporeCount;
    private int    _crystalCount;
    private double _statsTimer;
    private readonly Dictionary<int, int> _perSpeciesCounts = new(32);

    // Profiling
    private readonly Stopwatch _tickStopwatch   = new();
    private readonly Stopwatch _systemStopwatch = new();
    private readonly Stopwatch _renderStopwatch = new();
    private string[] _systemNames    = Array.Empty<string>();
    private double[] _systemTimingsMs = Array.Empty<double>();
    private double   _totalTickMs;
    private double   _renderMs;
    private bool     _showProfiling;
    private const double SmoothingFactor = 0.05;

    // 3D scene refs
    protected Camera3D? _camera;
    private Label?    _debugLabel;
    private Label?    _inspectorLabel;

    /// <summary>Click-to-inspect / species highlight / free camera. See ObservationController.</summary>
    protected Tools.ObservationController? _observation;

    public override void _Ready()
    {
        _entityManager = new EntityManager();
        // Any death, from any cause, leaves a corpse (CarrionSystem.SpawnCorpse self-filters
        // structures/spores/Faelings). Centralised here so future death causes need no extra wiring.
        _entityManager.OnEntityDying = id => CarrionSystem.SpawnCorpse(_entityManager, id);
        int seed = WorldSeed != 0 ? WorldSeed : (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);

        // Fix every simulation random stream to this run's seed, BEFORE anything that draws from
        // one is constructed. Without it each system time-seeded itself and the same world seed
        // replayed differently every time — which makes a seed meaningless to the player and makes
        // two runs of the same test scenario incomparable (any difference might be the change
        // under test or might be the dice). An explicit WorldSeed now reproduces a run exactly.
        SimRandom.SetSeed(WorldSeed != 0 ? seed : 0);
        _rng = SimRandom.Create();
        var terrainSettings = new TerrainSettings
        {
            ElevationFrequency = ElevationFrequency,
            WarpAmplitude      = WarpAmplitude,
            DetailFrequency    = TerrainDetailFrequency,
            DetailAmplitude    = TerrainDetailAmplitude,
            RoughnessFrequency = TerrainRoughnessFrequency,
            RoughnessFloor     = TerrainRoughnessFloor,
            MoistureFrequency  = TerrainMoistureFrequency,
            MoistureContrast   = TerrainMoistureContrast,
            RidgeFrequency     = TerrainRidgeFrequency,
            RidgeAmplitude     = TerrainRidgeAmplitude,
            OrogenyFrequency   = TerrainOrogenyFrequency,
            CliffFrequency     = TerrainCliffFrequency,
            CliffStrength      = TerrainCliffStrength,
            CliffStepHeight    = TerrainCliffStepHeight,
            RiverDensity       = TerrainRiverDensity,
        };
        _worldManager = CreateWorld(seed, terrainSettings);
        _simulationDt = 1.0 / TargetTPS;

        // Per-class population ceilings. Built before anything spawns, and attached to the
        // EntityManager so the live counts are maintained in the same place as CreatureCount —
        // there is no second bookkeeping path to fall out of step.
        _populationBudget = new PopulationBudget(MaxPopulation,
            HerbivoreBudgetShare, PredatorBudgetShare, FactionBudgetShare);
        _entityManager.Budget = _populationBudget;

        // Respawn allowance, tracked apart from the structures a respawn anchors to. Nothing
        // consumes it yet — player control is a later change — but the count belongs to the run,
        // not to the world, and this is where the run is set up.
        _factionLives = new FactionLives(FactionLivesPerRun);

        // Create extracted managers
        _entityFactory = new EntityFactory(_entityManager, _rng);
        _entityFactory.SetPopulationCap(MaxPopulation);
        _entityFactory.SetBudget(_populationBudget);
        _worldSpawner = new WorldSpawner(_worldManager, _entityFactory, _rng);
        _playerController = new PlayerController(_entityManager)
        {
            PlayerSpeed           = PlayerSpeed,
            PlayerSprintMultiplier= PlayerSprintMultiplier,
            ZoomMin               = ZoomMin,
            ZoomMax               = ZoomMax,
            ZoomSpeed             = ZoomSpeed,
            TileSize              = TileSize
        };
        _renderingManager = new RenderingManager(_entityManager, _worldManager,
            ChunkSize, WorldSizeChunks, TileSize, ElevationHeightScale);

        // Initialize systems. Order lives in SimulationStack, which the headless LOD
        // differential harness builds from too — one list, so the harness cannot be testing a
        // different stack from the one the game runs.
        var stack = SimulationStack.Build(_systems, _worldManager, _entityFactory,
            ChunkSize, WorldSizeChunks, TileSize, MaxPopulation, _populationBudget);
        _lodSystem     = stack.Lod;
        _nestSystem    = stack.Nest;
        _sporeSystem   = stack.Spore;
        _crystalSystem = stack.Crystal;

        // The logger is per-run configuration rather than simulation, and runs last.
        _ecosystemLogger = new EcosystemLogger(_worldManager, budget: _populationBudget);
        if (!string.IsNullOrWhiteSpace(TrackSpecies))
        {
            EcosystemLogger.TrackedSpeciesId = SpeciesRegistry.GetId(TrackSpecies);
            GD.Print($"[Logger] Tracking species: {TrackSpecies} (id={EcosystemLogger.TrackedSpeciesId})");
        }
        _systems.Add(_ecosystemLogger);

        _systemNames    = new string[_systems.Count];
        _systemTimingsMs = new double[_systems.Count];
        for (int i = 0; i < _systems.Count; i++)
            _systemNames[i] = _systems[i].GetType().Name;

        // --- 3D scene setup ---
        // Camera
        _camera = GetNode<Camera3D>("Camera3D");
        _camera.Projection = Camera3D.ProjectionType.Orthogonal;

        // Far plane: must exceed CameraDistance (= CameraSize*2) at maximum zoom,
        // plus the world's half-diagonal so terrain edges are never clipped.
        // Default Godot far (4000) is too small once zoom pushes CameraDistance to 5000+.
        float maxCamDist  = ZoomMax * TileSize * 32f * 2f;
        float worldRadius = WorldSizeChunks * ChunkSize * TileSize * 1.5f;
        _camera.Far  = maxCamDist + worldRadius + ElevationHeightScale + 500f;
        _camera.Near = 1f;

        // Directional light: warm sunlight from upper-right
        var dirLight = new DirectionalLight3D();
        dirLight.LightColor  = new Color(1f, 0.95f, 0.85f);
        dirLight.LightEnergy = 1.0f;
        dirLight.RotationDegrees = new Vector3(-50f, 45f, 0f);
        AddChild(dirLight);

        // World environment: sky color + soft ambient so shadows aren't pitch black
        var worldEnv = new WorldEnvironment();
        var env = new Godot.Environment();
        env.BackgroundMode  = Godot.Environment.BGMode.Color;
        env.BackgroundColor = new Color(0.4f, 0.6f, 0.9f);
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor  = new Color(0.35f, 0.4f, 0.5f);
        env.AmbientLightEnergy = 0.6f;
        worldEnv.Environment = env;
        AddChild(worldEnv);

        // Generate world
        GD.Print("Generating world...");
        _worldManager.PregenerateWorld((completed, total) =>
        {
            if (completed % 100 == 0)
                GD.Print($"Generated {completed}/{total} chunks");
        });
        GD.Print($"World generated: {_worldManager.LoadedChunkCount} chunks");

        // Diagnostic: dump biome/elevation/moisture/temperature map PNGs + parameter/distribution
        // report so worldgen output can be inspected (and niche coverage validated) before
        // blaming species balance. The returned base name pairs the post-spawn map below.
        string snapshotName = WorldSnapshot.Capture(_worldManager, seed, ChunkSize, WorldSizeChunks,
            terrainSettings, ElevationHeightScale);

        // Apply species enable/disable toggles before any spawning.
        var unknownSpecies = SpeciesToggle.Configure(DisabledSpecies, DisableFactionSpecies);
        foreach (var name in unknownSpecies)
            GD.PushWarning($"[SpeciesToggle] Unknown species name ignored: '{name}'");
        GD.Print(SpeciesToggle.DisabledCount > 0
            ? $"[SpeciesToggle] Disabled {SpeciesToggle.DisabledCount} species: {string.Join(", ", SpeciesToggle.DisabledNames)}"
            : "[SpeciesToggle] All species enabled");

        PopulateWorld();

        // Spawn player at world center
        float centerX = WorldSizeChunks * ChunkSize / 2f;
        float centerY = WorldSizeChunks * ChunkSize / 2f;
        int playerEntity = _entityFactory.SpawnPlayer(centerX, centerY, _worldManager);
        _playerController.SetPlayerEntity(playerEntity, TileSize, _camera);
        _lodSystem?.SetPlayerEntity(_playerController.PlayerEntity);

        // Diagnostic: dump the initial-population map (species-coloured dots over a dimmed
        // biome map) for validating spawn distribution against the niche placement rules.
        WorldSnapshot.CaptureSpawns(_worldManager, _entityManager, snapshotName);

        // Seed render-interpolation previous positions so nothing streaks on the first frame.
        _entityManager.SnapshotPositions();

        // Terrain meshes (behind entities in scene tree)
        _renderingManager.InitializeChunkMeshes(this);
        // Terrain shader is unshaded and computes its own Lambert term, so point its sun
        // at the real DirectionalLight (toward-sun = +Z basis, since lights face -Z).
        _renderingManager.SetSunDirection(dirLight.GlobalTransform.Basis.Z);

        // Entity MultiMesh nodes
        var shapeMMIs = _renderingManager.CreateMultiMeshInstances();
        foreach (var mmi in shapeMMIs)
            AddChild(mmi);

        _observation = new Tools.ObservationController(_entityManager, _worldManager);

        SetupDebugUI();
        GD.Print($"Game ready! {_entityManager.EntityCount} entities");
        GD.Print("Observation: LMB=inspect  Tab=cycle species  G=jump to next  " +
                 "H=highlight selected species  F=free camera  Esc=clear");
    }

    /// <summary>
    /// Build the world manager. Overridden by TestSceneManager to plug in a scenario-defined
    /// terrain generator instead of the noise pipeline.
    /// </summary>
    protected virtual WorldManager CreateWorld(int seed, TerrainSettings terrainSettings)
        => new(ChunkSize, WorldSizeChunks, seed, terrainSettings);

    /// <summary>
    /// Populate the freshly generated world. The default distributes InitialPopulation across
    /// the map via WorldSpawner; TestSceneManager overrides this to execute a scenario's exact
    /// spawn list instead. Species toggles are already applied when this runs.
    /// </summary>
    protected virtual void PopulateWorld()
    {
        // Seed each class in proportion to its CEILING, not to a separate set of ratios. The two
        // used to disagree — 85% herbivores seeded against a shared pool — and the reconciliation
        // was done by the global birth ramp, i.e. by the exclusion filter this change removed.
        _populationBudget!.SplitSeed(InitialPopulation,
            out int herbivoreSeed, out int predatorSeed, out int factionSeed);

        bool faelingEnabled = SpeciesToggle.IsEnabled(SpeciesRegistry.GetId("Faeling"));
        bool sectidEnabled  = SpeciesToggle.IsEnabled(SpeciesRegistry.GetId("Sectid"));
        bool shroomerEnabled = SpeciesToggle.IsEnabled(SpeciesRegistry.GetId("Shroomer"));

        // Sectids and Shroomers split the faction seed; Faelings take no part of it, because a
        // Faeling exists only while a crystal holds it (CrystalSystem links exactly one to each)
        // and crystals are placed for coverage, not from a population share.
        int factionClaimants = (sectidEnabled ? 1 : 0) + (shroomerEnabled ? 1 : 0);
        int sectidSeed   = sectidEnabled   && factionClaimants > 0 ? factionSeed / factionClaimants : 0;
        int shroomerSeed = shroomerEnabled && factionClaimants > 0 ? factionSeed - sectidSeed : 0;

        // A disabled faction's seed folds back into the prey base, so a "no-faction" run still
        // starts with a full InitialPopulation of everything else.
        if (factionClaimants == 0)
            herbivoreSeed += factionSeed;

        GD.Print("Spawning faction structures...");
        _worldSpawner.SpawnCrystals(_crystalSystem!, _entityManager, faelingEnabled,
            _populationBudget, FaelingCrystalCount);
        _worldSpawner.SpawnInitialNests(_nestSystem!, _entityManager, sectidSeed);

        GD.Print("Spawning creatures...");
        int spawnedCount = _worldSpawner.SpawnCreatures(herbivoreSeed, predatorSeed, shroomerSeed);

        // Hearts go in after the Shroomers, because a heart is placed on a bloom rather than the
        // bloom being grown around a heart.
        _worldSpawner.SpawnMyceliumHearts(_entityManager);
        GD.Print($"Spawned {spawnedCount} creatures — seed split from class budgets: " +
                 $"{herbivoreSeed} herbivore, {predatorSeed} predator, " +
                 $"{shroomerSeed} Shroomer + {sectidSeed} Sectid (Faelings come from crystals)");
    }

    /// <summary>
    /// One overlay line per budget class: live count against its ceiling, and how many spawns that
    /// class has been refused so far. The refusal counter is the point — a class sitting on its
    /// ceiling is the engine, not the ecology, deciding what the world contains, and that should be
    /// visible at a glance rather than inferred from a composition that looks wrong.
    /// </summary>
    private string BudgetOverlayText()
    {
        if (_populationBudget == null) return "";
        return "Budget:  " +
               $"herb {_populationBudget.CountFor(PopClass.Herbivore)}/{_populationBudget.BudgetFor(PopClass.Herbivore)}" +
               $" (refused {_populationBudget.RefusalsFor(PopClass.Herbivore)})   " +
               $"pred {_populationBudget.CountFor(PopClass.Predator)}/{_populationBudget.BudgetFor(PopClass.Predator)}" +
               $" (refused {_populationBudget.RefusalsFor(PopClass.Predator)})   " +
               $"faction {_populationBudget.CountFor(PopClass.Faction)}/{_populationBudget.BudgetFor(PopClass.Faction)}" +
               $" (refused {_populationBudget.RefusalsFor(PopClass.Faction)})";
    }

    private void SetupDebugUI()
    {
        var canvasLayer = new CanvasLayer();
        canvasLayer.Layer = 100;
        AddChild(canvasLayer);

        _debugLabel = new Label();
        _debugLabel.Position = new Vector2(10, 10);
        _debugLabel.AddThemeColorOverride("font_color", Colors.White);
        _debugLabel.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _debugLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _debugLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        var monoFont = new SystemFont();
        monoFont.FontNames = new string[] { "Courier New", "monospace", "Consolas" };
        _debugLabel.AddThemeFontOverride("font", monoFont);
        _debugLabel.AddThemeFontSizeOverride("font_size", 14);
        canvasLayer.AddChild(_debugLabel);

        // Inspector panel, anchored top-right so it never covers the stats/profiling readout.
        _inspectorLabel = new Label();
        _inspectorLabel.Position = new Vector2(10, 10);
        _inspectorLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _inspectorLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _inspectorLabel.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.75f));
        _inspectorLabel.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _inspectorLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _inspectorLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        _inspectorLabel.AddThemeFontOverride("font", monoFont);
        _inspectorLabel.AddThemeFontSizeOverride("font_size", 13);
        canvasLayer.AddChild(_inspectorLabel);
    }

    public override void _Process(double delta)
    {
        // FPS counter
        _frameCount++;
        _fpsTimer += delta;
        if (_fpsTimer >= 1.0)
        {
            _fps = _frameCount;
            _frameCount = 0;
            _fpsTimer = 0;
        }

        if (_playerController == null) return;

        _playerController.HandleInput();
        _playerController.HandleZoomInput(_camera);

        // LOD visible radius from orthographic camera size
        if (_lodSystem != null && _camera != null)
        {
            var viewport = GetViewport();
            if (viewport != null)
            {
                var viewportSize = viewport.GetVisibleRect().Size;
                float aspectRatio = viewportSize.X / viewportSize.Y;
                // Camera.Size = visible height in world units
                float halfH = _playerController.CameraSize / (2f * TileSize);
                float halfW = halfH * aspectRatio;
                // Multiply by ~1.5 to account for isometric tilt (camera sees more terrain depth)
                float visibleRadius = MathF.Sqrt(halfW * halfW + halfH * halfH) * 1.5f;
                _lodSystem.SetVisibleRadius(visibleRadius);
            }
        }

        // Fixed-timestep simulation
        if (SimulationPaused)
        {
            // Discard accumulated time so unpausing doesn't fast-forward a tick backlog.
            _simulationAccumulator = 0;
            if (_singleStepQueued)
            {
                RunSimulationTick();
                _singleStepQueued = false;
            }
        }
        else
        {
            _simulationAccumulator += delta;
            while (_simulationAccumulator >= _simulationDt)
            {
                RunSimulationTick();
                _simulationAccumulator -= _simulationDt;
            }
        }

        _playerController.UpdateCamera(_camera, delta);

        UpdateStats(delta);

        _renderingManager.UpdateDirtyChunkMeshes();

        // Fraction into the next tick, for smooth inter-tick entity rendering.
        float renderAlpha = (float)(_simulationAccumulator / _simulationDt);
        if (renderAlpha < 0f) renderAlpha = 0f; else if (renderAlpha > 1f) renderAlpha = 1f;

        _renderStopwatch.Restart();
        if (_camera != null)
            _renderingManager.UpdateEntityMultiMeshes(_camera, renderAlpha);
        _renderStopwatch.Stop();
        {
            double ms = _renderStopwatch.Elapsed.TotalMilliseconds;
            _renderMs += (ms - _renderMs) * SmoothingFactor;
        }
    }

    /// <summary>One simulation tick: snapshot render positions, run every system, finalize.</summary>
    private void RunSimulationTick()
    {
        _entityManager.SnapshotPositions();   // previous-tick positions for render lerp
        _tickStopwatch.Restart();
        for (int i = 0; i < _systems.Count; i++)
        {
            _systemStopwatch.Restart();
            _systems[i].Process(_entityManager);
            _systemStopwatch.Stop();
            double ms = _systemStopwatch.Elapsed.TotalMilliseconds;
            _systemTimingsMs[i] += (ms - _systemTimingsMs[i]) * SmoothingFactor;
        }
        _tickStopwatch.Stop();
        _entityManager.FinalizeNewborns();    // entities spawned this tick: prev = spawn pos
        double tickMs = _tickStopwatch.Elapsed.TotalMilliseconds;
        _totalTickMs += (tickMs - _totalTickMs) * SmoothingFactor;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        _playerController?.HandleMouseZoom(@event, _camera);

        if (@event is InputEventKey { Pressed: true, Keycode: Key.F3 })
            _showProfiling = !_showProfiling;

        HandleObservationInput(@event);
    }

    /// <summary>
    /// Observation controls. Virtual so a subclass can suppress them while its own modal tools
    /// are active (the test scene's terrain painting also uses the left mouse button).
    /// </summary>
    protected virtual void HandleObservationInput(InputEvent @event)
    {
        if (_observation == null || _camera == null) return;

        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
        {
            if (!_observation.SelectAtScreen(_camera, mb.Position, TileSize, ElevationHeightScale))
                _observation.Validate();
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        switch (key.Keycode)
        {
            case Key.Tab:
                _observation.CycleSpecies(key.ShiftPressed ? -1 : 1);
                break;

            case Key.H:
                _observation.HighlightSelectedSpecies();
                break;

            case Key.G:
                // Jump to the next member of the highlighted species. Camera goes free so the
                // view can sit anywhere on the map, not just where the player creature can walk.
                if (_observation.TryGetNextMember(out float jx, out float jy, out _))
                    _playerController?.FocusOn(jx, jy);
                break;

            case Key.F:
                if (_playerController != null)
                {
                    if (_playerController.FreeCamera) _playerController.FreeCamera = false;
                    else _playerController.EnterFreeCameraAtPlayer();
                }
                break;

            case Key.Escape:
                _observation.ClearSelection();
                break;
        }
    }

    private void UpdateStats(double delta)
    {
        _statsTimer += delta;
        if (_statsTimer < 0.5) return;
        _statsTimer = 0;

        _herbivoreCount = 0; _predatorCount = 0; _shroomerCount = 0;
        _sectidCount = 0;    _faelingCount  = 0; _nestCount     = 0;
        _sporeCount  = 0;    _crystalCount  = 0;
        _perSpeciesCounts.Clear();

        foreach (int entity in _entityManager.AllEntities())
        {
            if (_entityManager.HasComponents(entity, ComponentFlags.Nest))
            { _nestCount++;    continue; }
            if (_entityManager.HasComponents(entity, ComponentFlags.Crystal))
            { _crystalCount++; continue; }
            if (_entityManager.HasComponents(entity, ComponentFlags.Spore))
            { _sporeCount++;   continue; }

            if (_entityManager.HasComponents(entity, ComponentFlags.Species))
            {
                ref var species = ref _entityManager.Species[entity];
                _perSpeciesCounts.TryGetValue(species.SpeciesId, out int cnt);
                _perSpeciesCounts[species.SpeciesId] = cnt + 1;

                switch (species.Type)
                {
                    case SpeciesType.Herbivore: _herbivoreCount++; break;
                    case SpeciesType.Carnivore: _predatorCount++;  break;
                    case SpeciesType.Shroomer:  _shroomerCount++;  break;
                    case SpeciesType.Sectid:    _sectidCount++;    break;
                    case SpeciesType.Faeling:   _faelingCount++;   break;
                }
            }
        }

        if (_debugLabel != null)
        {
            _debugLabel.Text =
                // Show what the cap ACTUALLY limits. This read EntityCount, which includes
                // corpses, while MaxPopulation limits CreatureCount, which excludes them — so a
                // world sitting exactly on its ceiling displayed ~12% over it and looked like a
                // runaway. Corpses are now reported separately instead of being folded into the
                // number the cap is compared against.
                $"FPS: {_fps}  |  TPS: {TargetTPS}  |  " +
                $"Creatures: {_entityManager.CreatureCount}/{MaxPopulation}" +
                $"  (+{_entityManager.CarrionCount} carrion)\n" +
                $"LOD: Full={_lodSystem?.CountFull ?? 0}  " +
                $"High={_lodSystem?.CountHigh ?? 0}  " +
                $"Med={_lodSystem?.CountMedium ?? 0}  " +
                $"Low={_lodSystem?.CountLow ?? 0}  " +
                $"Min={_lodSystem?.CountMinimal ?? 0}\n" +
                $"Herbivores: {_herbivoreCount}  Predators: {_predatorCount}  " +
                $"Shroomers: {_shroomerCount}  Sectids: {_sectidCount}  " +
                $"Faelings: {_faelingCount}\n" +
                BudgetOverlayText();

            if (_showProfiling)
            {
                _debugLabel.Text +=
                    $"\n\n--- Profiling (F3 to hide) ---\n" +
                    $"Tick: {_totalTickMs:F2} ms  |  Render: {_renderMs:F2} ms  |  " +
                    $"Budget: {1000.0 / TargetTPS:F1} ms/tick\n";

                Span<int> indices = stackalloc int[_systems.Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                for (int i = 1; i < indices.Length; i++)
                {
                    int key    = indices[i];
                    double keyVal = _systemTimingsMs[key];
                    int j = i - 1;
                    while (j >= 0 && _systemTimingsMs[indices[j]] < keyVal)
                    { indices[j + 1] = indices[j]; j--; }
                    indices[j + 1] = key;
                }

                for (int i = 0; i < indices.Length; i++)
                {
                    int    idx = indices[i];
                    double ms  = _systemTimingsMs[idx];
                    double pct = _totalTickMs > 0 ? ms / _totalTickMs * 100 : 0;
                    string bar = new string('|', (int)(pct / 2));
                    _debugLabel.Text += $"  {_systemNames[idx],-28} {ms,6:F2} ms  {pct,5:F1}%  {bar}\n";
                }

                _debugLabel.Text += "\n--- World Population by Species ---\n";
                foreach (string name in SpeciesRegistry.GetAllNames())
                {
                    int id = SpeciesRegistry.GetId(name);
                    _perSpeciesCounts.TryGetValue(id, out int count);
                    _debugLabel.Text += count > 0
                        ? $"  {name,-16} {count,5}\n"
                        : $"  {name,-16}     0  EXTINCT\n";
                }
            }
            else
            {
                _debugLabel.Text += $"\n[F3] profiling";
            }

            string extra = ExtraDebugText();
            if (extra.Length > 0)
                _debugLabel.Text += "\n" + extra;
        }

        UpdateInspectorPanel();
    }

    /// <summary>
    /// Extra lines appended to the debug overlay. Overridden by TestSceneManager to show
    /// pause/speed state and the paint-mode brush.
    /// </summary>
    protected virtual string ExtraDebugText() => "";

    /// <summary>Right-hand observation panel: highlighted species tally + selected creature.</summary>
    private void UpdateInspectorPanel()
    {
        if (_inspectorLabel == null || _observation == null) return;

        _observation.Validate();
        var sb = new System.Text.StringBuilder(600);

        if (_observation.HasHighlight)
        {
            sb.Append("HIGHLIGHT: ").Append(_observation.HighlightedSpeciesName)
              .Append("  (").Append(_observation.CountSpecies(_observation.HighlightedSpeciesId))
              .Append(" alive)   [G] jump\n\n");
        }

        string inspector = _observation.BuildInspectorText();
        if (inspector.Length > 0) sb.Append(inspector);
        else if (sb.Length == 0)
            sb.Append("[LMB] inspect   [Tab] species   [H] highlight selected   [F] free cam");

        if (_playerController is { FreeCamera: true }) sb.Append("\n[FREE CAMERA]");

        _inspectorLabel.Text = sb.ToString();

        // Feed the render overlay so highlighted/selected creatures stand out in the world.
        _renderingManager.HighlightSpeciesId = _observation.HighlightedSpeciesId;
        _renderingManager.HighlightActive = _observation.HasHighlight;
        _renderingManager.SelectedEntity = _observation.SelectedEntity;
    }
}
