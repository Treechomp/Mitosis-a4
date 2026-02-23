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

namespace Mitosis;

/// <summary>
/// Main game manager that orchestrates the simulation.
/// Delegates to EntityFactory (entity creation), WorldSpawner (population distribution),
/// PlayerController (input/camera), and RenderingManager (visuals).
/// </summary>
public partial class GameManager : Node2D
{
    // Configuration
    [Export] public int ChunkSize = 32;
    [Export] public int WorldSizeChunks = 9;  // DEBUG: standardized debug world
    [Export] public int WorldSeed = 0;
    [Export] public int TileSize = 16;
    [Export] public int TargetTPS = 20;
    [Export] public bool EnableStatisticalSim = false;  // Toggle statistical sim from inspector
    [Export] public int MaxPopulation = 2000;  // DEBUG: cap at 2000 (2500+ causes FPS drop)
    [Export] public int InitialPopulation = 500;  // DEBUG: standardized debug population
    [Export] public float HerbivoreRatio = 0.85f;
    [Export] public float CreaturesPerChunk = 2f;

    // Spectator settings
    [Export] public float PlayerSpeed = 1.0f;         // Base movement speed (tiles per tick)
    [Export] public float PlayerSprintMultiplier = 3.0f;  // Speed when holding shift
    [Export] public float ZoomMin = 0.1f;             // Maximum zoom out
    [Export] public float ZoomMax = 5.0f;             // Maximum zoom in
    [Export] public float ZoomSpeed = 0.15f;          // Zoom sensitivity

    // Faction spawning — proportions of InitialPopulation
    [Export] public float FaelingShare = 0.04f;   // Fraction of initial pop as Faelings (crystals + spawns)
    [Export] public float SectidShare = 0.06f;    // Fraction of initial pop as Sectids (nests + starters)

    // Core systems
    private EntityManager _entityManager = null!;
    private WorldManager _worldManager = null!;
    private readonly List<ISystem> _systems = new();
    private readonly Random _rng = new();
    private LODSystem? _lodSystem;
    private NestSystem? _nestSystem;
    private CrystalSystem? _crystalSystem;
    private StatisticalSimSystem? _statSimSystem;
    private EcosystemLogger? _ecosystemLogger;

    // Extracted managers
    private EntityFactory _entityFactory = null!;
    private WorldSpawner _worldSpawner = null!;
    private PlayerController _playerController = null!;
    private RenderingManager _renderingManager = null!;

    // Simulation timing
    private double _simulationAccumulator;
    private double _simulationDt;

    // Debug info
    private int _fps;
    private double _fpsTimer;
    private int _frameCount;
    private int _herbivoreCount;
    private int _predatorCount;
    private int _shroomerCount;
    private int _sectidCount;
    private int _faelingCount;
    private int _nestCount;
    private int _sporeCount;
    private int _crystalCount;
    private double _statsTimer;
    private readonly Dictionary<int, int> _perSpeciesCounts = new(32);  // speciesId -> count

    // Profiling
    private readonly Stopwatch _tickStopwatch = new();
    private readonly Stopwatch _systemStopwatch = new();
    private readonly Stopwatch _renderStopwatch = new();
    private string[] _systemNames = Array.Empty<string>();
    private double[] _systemTimingsMs = Array.Empty<double>();
    private double _totalTickMs;
    private double _renderMs;
    private bool _showProfiling;
    private const double SmoothingFactor = 0.05; // ~20 sample EMA window

    // Rendering
    private Camera2D? _camera;
    private Label? _debugLabel;

    public override void _Ready()
    {
        _entityManager = new EntityManager();
        // Seed 0 means randomize each run; any other value gives a reproducible world
        int seed = WorldSeed != 0 ? WorldSeed : (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
        _worldManager = new WorldManager(ChunkSize, WorldSizeChunks, seed);
        _simulationDt = 1.0 / TargetTPS;

        // Create extracted managers
        _entityFactory = new EntityFactory(_entityManager, _rng);
        _entityFactory.SetPopulationCap(MaxPopulation);
        _worldSpawner = new WorldSpawner(_worldManager, _entityFactory, _rng);
        _playerController = new PlayerController(_entityManager)
        {
            PlayerSpeed = PlayerSpeed,
            PlayerSprintMultiplier = PlayerSprintMultiplier,
            ZoomMin = ZoomMin,
            ZoomMax = ZoomMax,
            ZoomSpeed = ZoomSpeed,
            TileSize = TileSize
        };
        _renderingManager = new RenderingManager(_entityManager, _worldManager,
            ChunkSize, WorldSizeChunks, TileSize);

        // Initialize systems
        // DEBUG: LODSystem disabled — all entities run at Full LOD (no gating)
        var spatialHash = _worldManager.SpatialHash;
        // _lodSystem = new LODSystem();
        // _systems.Add(_lodSystem);
        _systems.Add(new MovementSystem(ChunkSize, WorldSizeChunks, _worldManager));
        _systems.Add(new SpatialHashUpdateSystem(spatialHash));    // Sync all positions once
        _systems.Add(new TerrainDiscomfortSystem(_worldManager));  // Process discomfort early
        _systems.Add(new HungerSystem());
        _systems.Add(new GrazingSystem(_worldManager));
        _systems.Add(new WanderSystem(_worldManager, spatialHash: spatialHash));  // Roaming params from species
        _systems.Add(new HerdingSystem(spatialHash));
        _systems.Add(new SeparationSystem(spatialHash));
        _systems.Add(new CollisionSystem(spatialHash, collisionRadiusScale: 0.5f, tileSize: TileSize));
        _systems.Add(new HuntingSystem(spatialHash, _worldManager));
        _systems.Add(new FleeingSystem(spatialHash, _worldManager));
        _systems.Add(new AgingSystem());
        _systems.Add(new ReproductionSystem(_worldManager, MaxPopulation, spatialHash));
        _systems.Add(new TerraformSystem(_worldManager));
        _systems.Add(new TileRegenerationSystem(_worldManager));

        // Faction systems
        _nestSystem = new NestSystem(_worldManager, spatialHash, MaxPopulation);
        _systems.Add(_nestSystem);
        var sporeSystem = new SporeSystem(_worldManager, spatialHash, MaxPopulation);
        _systems.Add(sporeSystem);
        _crystalSystem = new CrystalSystem(_worldManager, spatialHash, MaxPopulation);
        _systems.Add(_crystalSystem);

        if (EnableStatisticalSim)
            _statSimSystem = new StatisticalSimSystem(
                _worldManager, _entityFactory, spatialHash, _nestSystem,
                MaxPopulation, ChunkSize);

        // DEBUG: Ecosystem logger — writes CSV logs to godot/logs/ (see latest_events.csv, latest_population.csv)
        _ecosystemLogger = new EcosystemLogger();
        if (_statSimSystem != null)
            _ecosystemLogger.SetStatisticalSimSystem(_statSimSystem);
        _systems.Add(_ecosystemLogger);

        // Initialize profiling arrays (does not include StatisticalSimSystem — it runs separately)
        _systemNames = new string[_systems.Count];
        _systemTimingsMs = new double[_systems.Count];
        for (int i = 0; i < _systems.Count; i++)
            _systemNames[i] = _systems[i].GetType().Name;

        // Get camera reference
        _camera = GetNode<Camera2D>("Camera2D");

        // Setup world
        GD.Print("Generating world...");
        _worldManager.PregenerateWorld((completed, total) =>
        {
            if (completed % 100 == 0)
                GD.Print($"Generated {completed}/{total} chunks");
        });
        GD.Print($"World generated: {_worldManager.LoadedChunkCount} chunks");

        // Spawn faction structures proportional to InitialPopulation
        int faelingBudget = (int)(InitialPopulation * FaelingShare);
        int sectidBudget = (int)(InitialPopulation * SectidShare);
        int creatureBudget = InitialPopulation - faelingBudget - sectidBudget;

        GD.Print("Spawning faction structures...");
        _worldSpawner.SpawnCrystals(_crystalSystem!, _entityManager, faelingBudget);
        _worldSpawner.SpawnInitialNests(_nestSystem!, _entityManager, sectidBudget);

        // Spawn creatures (herbivores, predators, initial Shroomers)
        GD.Print("Spawning creatures...");
        int spawnedCount = _worldSpawner.SpawnCreatures(creatureBudget, HerbivoreRatio);
        GD.Print($"Spawned {spawnedCount} creatures (+ faction structures from {faelingBudget} Faeling + {sectidBudget} Sectid budget)");

        // Spawn player
        float centerX = WorldSizeChunks * ChunkSize / 2f;
        float centerY = WorldSizeChunks * ChunkSize / 2f;
        int playerEntity = _entityFactory.SpawnPlayer(centerX, centerY, _worldManager);
        _playerController.SetPlayerEntity(playerEntity, TileSize, _camera);

        // DEBUG: LOD system disabled — skip player entity setup
        // _lodSystem?.SetPlayerEntity(_playerController.PlayerEntity);

        // Setup entity rendering via MultiMesh
        var shapeMMIs = _renderingManager.CreateMultiMeshInstances();
        foreach (var mmi in shapeMMIs)
            AddChild(mmi);

        // Setup debug UI
        SetupDebugUI();

        GD.Print($"Game ready! {_entityManager.EntityCount} entities");
    }

    private void SetupDebugUI()
    {
        var canvasLayer = new CanvasLayer();
        canvasLayer.Layer = 100; // Above everything
        AddChild(canvasLayer);

        _debugLabel = new Label();
        _debugLabel.Position = new Vector2(10, 10);
        _debugLabel.AddThemeColorOverride("font_color", Colors.White);
        _debugLabel.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _debugLabel.AddThemeConstantOverride("shadow_offset_x", 1);
        _debugLabel.AddThemeConstantOverride("shadow_offset_y", 1);
        // Use monospace font for aligned profiling columns
        var monoFont = new SystemFont();
        monoFont.FontNames = new string[] { "Courier New", "monospace", "Consolas" };
        _debugLabel.AddThemeFontOverride("font", monoFont);
        _debugLabel.AddThemeFontSizeOverride("font_size", 14);
        canvasLayer.AddChild(_debugLabel);
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

        // Guard against partial initialization (e.g. world-gen failure)
        if (_playerController == null) return;

        // Handle input
        _playerController.HandleInput();
        _playerController.HandleZoomInput(_camera);

        // Update statistical sim with player position
        if (_statSimSystem != null && _playerController.PlayerEntity >= 0 &&
            _entityManager.HasComponents(_playerController.PlayerEntity, ComponentFlags.Position))
        {
            ref var playerPos = ref _entityManager.Positions[_playerController.PlayerEntity];
            _statSimSystem.SetPlayerPosition(playerPos.X, playerPos.Y);
        }

        // Fixed timestep simulation
        _simulationAccumulator += delta;
        while (_simulationAccumulator >= _simulationDt)
        {
            _tickStopwatch.Restart();
            for (int i = 0; i < _systems.Count; i++)
            {
                _systemStopwatch.Restart();
                _systems[i].Process(_entityManager);
                _systemStopwatch.Stop();
                double ms = _systemStopwatch.Elapsed.TotalMilliseconds;
                _systemTimingsMs[i] += (ms - _systemTimingsMs[i]) * SmoothingFactor;
            }

            // Statistical simulation runs after all entity systems
            _statSimSystem?.Process(_entityManager);

            _tickStopwatch.Stop();
            double tickMs = _tickStopwatch.Elapsed.TotalMilliseconds;
            _totalTickMs += (tickMs - _totalTickMs) * SmoothingFactor;

            _simulationAccumulator -= _simulationDt;
        }

        // Update camera
        _playerController.UpdateCamera(_camera, delta);

        // Update stats periodically
        UpdateStats(delta);

        // Update entity MultiMesh buffers for rendering
        _renderStopwatch.Restart();
        if (_camera != null)
            _renderingManager.UpdateEntityMultiMeshes(_camera);
        _renderStopwatch.Stop();
        {
            double ms = _renderStopwatch.Elapsed.TotalMilliseconds;
            _renderMs += (ms - _renderMs) * SmoothingFactor;
        }

        // Request redraw (for terrain and debug overlay)
        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        _playerController?.HandleMouseZoom(@event, _camera);

        if (@event is InputEventKey { Pressed: true, Keycode: Key.F3 })
        {
            _showProfiling = !_showProfiling;
        }
    }

    public override void _Draw()
    {
        if (_camera == null) return;

        _renderingManager.DrawTerrain(this, _camera);

        // Entities are rendered via MultiMeshInstance2D children (updated in _Process)
    }

    private void UpdateStats(double delta)
    {
        _statsTimer += delta;
        if (_statsTimer < 0.5) return;
        _statsTimer = 0;

        // Count entities by type and per-species
        _herbivoreCount = 0;
        _predatorCount = 0;
        _shroomerCount = 0;
        _sectidCount = 0;
        _faelingCount = 0;
        _nestCount = 0;
        _sporeCount = 0;
        _crystalCount = 0;
        _perSpeciesCounts.Clear();

        foreach (int entity in _entityManager.AllEntities())
        {
            if (_entityManager.HasComponents(entity, ComponentFlags.Nest))
            { _nestCount++; continue; }
            if (_entityManager.HasComponents(entity, ComponentFlags.Crystal))
            { _crystalCount++; continue; }
            if (_entityManager.HasComponents(entity, ComponentFlags.Spore))
            { _sporeCount++; continue; }

            if (_entityManager.HasComponents(entity, ComponentFlags.Species))
            {
                ref var species = ref _entityManager.Species[entity];

                // Per-species count
                _perSpeciesCounts.TryGetValue(species.SpeciesId, out int cnt);
                _perSpeciesCounts[species.SpeciesId] = cnt + 1;

                switch (species.Type)
                {
                    case SpeciesType.Herbivore: _herbivoreCount++; break;
                    case SpeciesType.Carnivore: _predatorCount++; break;
                    case SpeciesType.Shroomer: _shroomerCount++; break;
                    case SpeciesType.Sectid: _sectidCount++; break;
                    case SpeciesType.Faeling: _faelingCount++; break;
                }
            }
        }

        // Merge statistical populations into per-species counts
        _statSimSystem?.AccumulateSpeciesPopulations(_perSpeciesCounts);

        // Update debug label
        if (_debugLabel != null)
        {
            int statPop = _statSimSystem?.TotalStatisticalPopulation ?? 0;
            int statChunks = _statSimSystem?.StatisticalChunkCount ?? 0;
            int worldPop = _entityManager.EntityCount + statPop;
            _debugLabel.Text = $"FPS: {_fps}  |  TPS: {TargetTPS}  |  World pop: {worldPop}\n" +
                              $"Nearby: {_entityManager.EntityCount} entities  |  " +
                              $"Distant: {statPop} in {statChunks} chunks\n" +
                              $"Herbivores: {_herbivoreCount}  Predators: {_predatorCount}  " +
                              $"Shroomers: {_shroomerCount}  Sectids: {_sectidCount}  " +
                              $"Faelings: {_faelingCount}";

            if (_showProfiling)
            {
                _debugLabel.Text += $"\n\n--- Profiling (F3 to hide) ---\n" +
                                   $"Tick: {_totalTickMs:F2} ms  |  Render: {_renderMs:F2} ms  |  " +
                                   $"Budget: {1000.0 / TargetTPS:F1} ms/tick\n";

                // Sort systems by cost (descending) via index array
                Span<int> indices = stackalloc int[_systems.Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                // Simple insertion sort (small N)
                for (int i = 1; i < indices.Length; i++)
                {
                    int key = indices[i];
                    double keyVal = _systemTimingsMs[key];
                    int j = i - 1;
                    while (j >= 0 && _systemTimingsMs[indices[j]] < keyVal)
                    {
                        indices[j + 1] = indices[j];
                        j--;
                    }
                    indices[j + 1] = key;
                }

                for (int i = 0; i < indices.Length; i++)
                {
                    int idx = indices[i];
                    double ms = _systemTimingsMs[idx];
                    double pct = _totalTickMs > 0 ? ms / _totalTickMs * 100 : 0;
                    string bar = new string('|', (int)(pct / 2)); // 50 chars = 100%
                    _debugLabel.Text += $"  {_systemNames[idx],-28} {ms,6:F2} ms  {pct,5:F1}%  {bar}\n";
                }

                // Per-species world population (entities + statistical)
                _debugLabel.Text += "\n--- World Population by Species ---\n";
                foreach (string name in SpeciesRegistry.GetAllNames())
                {
                    int id = SpeciesRegistry.GetId(name);
                    _perSpeciesCounts.TryGetValue(id, out int count);
                    if (count > 0)
                        _debugLabel.Text += $"  {name,-16} {count,5}\n";
                    else
                        _debugLabel.Text += $"  {name,-16}     0  EXTINCT\n";
                }
            }
            else
            {
                _debugLabel.Text += $"\n[F3] profiling";
            }
        }
    }
}
