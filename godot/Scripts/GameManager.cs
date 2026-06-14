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
/// Inherits Node3D for full 3D rendering with Camera3D and DirectionalLight3D.
/// </summary>
public partial class GameManager : Node3D
{
    // Configuration
    [Export] public int ChunkSize = 32;
    [Export] public int WorldSizeChunks = 9;  // DEBUG: standardized debug world
    [Export] public int WorldSeed = 0;
    [Export] public int TileSize = 16;
    [Export] public float ElevationHeightScale = 64f;  // World units of vertical lift per elevation unit
    // Terrain noise tuning (see World/TerrainSettings.cs) — editable in the inspector.
    [Export] public float ElevationFrequency = 0.012f;        // lower = larger landmasses
    [Export] public float WarpAmplitude = 12f;                // higher = more swirled boundaries
    [Export] public float TerrainDetailFrequency = 0.045f;    // surface relief frequency
    [Export] public float TerrainDetailAmplitude = 0.035f;    // surface relief height (keep < ~0.1)
    [Export] public float TerrainRoughnessFrequency = 0.006f; // size of rugged vs smooth regions
    [Export] public float TerrainRoughnessFloor = 0.15f;      // min detail in smoothest areas (0..1)
    [Export] public int TargetTPS = 20;
    [Export] public int MaxPopulation = 2000;  // DEBUG: cap at 2000 (2500+ causes FPS drop)
    [Export] public int InitialPopulation = 500;  // DEBUG: standardized debug population
    [Export] public float HerbivoreRatio = 0.85f;
    [Export] public float CreaturesPerChunk = 2f;

    // Spectator settings
    [Export] public float PlayerSpeed = 1.0f;
    [Export] public float PlayerSprintMultiplier = 3.0f;
    [Export] public float ZoomMin = 0.1f;   // Multiplied by TileSize*32 for camera size min
    [Export] public float ZoomMax = 5.0f;   // Multiplied by TileSize*32 for camera size max
    [Export] public float ZoomSpeed = 0.15f;

    // Faction spawning — proportions of InitialPopulation
    [Export] public float FaelingShare = 0.04f;
    [Export] public float SectidShare  = 0.10f;  // Need denser starting swarms to reach kill-mass

    // Core systems
    private EntityManager _entityManager = null!;
    private WorldManager  _worldManager  = null!;
    private readonly List<ISystem> _systems = new();
    private readonly Random _rng = new();
    private LODSystem?       _lodSystem;
    private NestSystem?      _nestSystem;
    private CrystalSystem?   _crystalSystem;
    private EcosystemLogger? _ecosystemLogger;

    // Extracted managers
    private EntityFactory    _entityFactory    = null!;
    private WorldSpawner     _worldSpawner     = null!;
    private PlayerController _playerController = null!;
    private RenderingManager _renderingManager = null!;

    // Simulation timing
    private double _simulationAccumulator;
    private double _simulationDt;

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
    private Camera3D? _camera;
    private Label?    _debugLabel;

    public override void _Ready()
    {
        _entityManager = new EntityManager();
        int seed = WorldSeed != 0 ? WorldSeed : (int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF);
        _worldManager = new WorldManager(ChunkSize, WorldSizeChunks, seed, new TerrainSettings
        {
            ElevationFrequency = ElevationFrequency,
            WarpAmplitude      = WarpAmplitude,
            DetailFrequency    = TerrainDetailFrequency,
            DetailAmplitude    = TerrainDetailAmplitude,
            RoughnessFrequency = TerrainRoughnessFrequency,
            RoughnessFloor     = TerrainRoughnessFloor,
        });
        _simulationDt = 1.0 / TargetTPS;

        // Create extracted managers
        _entityFactory = new EntityFactory(_entityManager, _rng);
        _entityFactory.SetPopulationCap(MaxPopulation);
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

        // Initialize systems — LODSystem must run FIRST to set tick gating
        var spatialHash = _worldManager.SpatialHash;
        _lodSystem = new LODSystem(spatialHash);
        _systems.Add(_lodSystem);
        _systems.Add(new MovementSystem(ChunkSize, WorldSizeChunks, _worldManager));
        _systems.Add(new SpatialHashUpdateSystem(spatialHash));
        _systems.Add(new TerrainDiscomfortSystem(_worldManager));
        _systems.Add(new HungerSystem());
        _systems.Add(new GrazingSystem(_worldManager));
        _systems.Add(new WanderSystem(_worldManager, spatialHash: spatialHash));
        _systems.Add(new HerdingSystem(spatialHash));
        _systems.Add(new SeparationSystem(spatialHash));
        _systems.Add(new CollisionSystem(spatialHash, collisionRadiusScale: 0.5f, tileSize: TileSize));
        _systems.Add(new HuntingSystem(spatialHash, _worldManager));
        _systems.Add(new FleeingSystem(spatialHash, _worldManager));
        _systems.Add(new AgingSystem());
        _systems.Add(new ReproductionSystem(_worldManager, MaxPopulation, spatialHash));
        _systems.Add(new TerraformSystem(_worldManager));
        _systems.Add(new TileRegenerationSystem(_worldManager));

        _nestSystem = new NestSystem(_worldManager, spatialHash, MaxPopulation);
        _systems.Add(_nestSystem);
        var sporeSystem = new SporeSystem(_worldManager, spatialHash, MaxPopulation);
        _systems.Add(sporeSystem);
        _crystalSystem = new CrystalSystem(_worldManager, spatialHash, MaxPopulation);
        _systems.Add(_crystalSystem);

        _ecosystemLogger = new EcosystemLogger();
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

        // Spawn faction structures
        int faelingBudget  = (int)(InitialPopulation * FaelingShare);
        int sectidBudget   = (int)(InitialPopulation * SectidShare);
        int creatureBudget = InitialPopulation - faelingBudget - sectidBudget;

        GD.Print("Spawning faction structures...");
        _worldSpawner.SpawnCrystals(_crystalSystem!, _entityManager, faelingBudget);
        _worldSpawner.SpawnInitialNests(_nestSystem!, _entityManager, sectidBudget);

        GD.Print("Spawning creatures...");
        int spawnedCount = _worldSpawner.SpawnCreatures(creatureBudget, HerbivoreRatio);
        GD.Print($"Spawned {spawnedCount} creatures (+ faction structures from {faelingBudget} Faeling + {sectidBudget} Sectid budget)");

        // Spawn player at world center
        float centerX = WorldSizeChunks * ChunkSize / 2f;
        float centerY = WorldSizeChunks * ChunkSize / 2f;
        int playerEntity = _entityFactory.SpawnPlayer(centerX, centerY, _worldManager);
        _playerController.SetPlayerEntity(playerEntity, TileSize, _camera);
        _lodSystem?.SetPlayerEntity(_playerController.PlayerEntity);

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

        SetupDebugUI();
        GD.Print($"Game ready! {_entityManager.EntityCount} entities");
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
        _simulationAccumulator += delta;
        while (_simulationAccumulator >= _simulationDt)
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
            _simulationAccumulator -= _simulationDt;
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

    public override void _UnhandledInput(InputEvent @event)
    {
        _playerController?.HandleMouseZoom(@event, _camera);

        if (@event is InputEventKey { Pressed: true, Keycode: Key.F3 })
            _showProfiling = !_showProfiling;
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
                $"FPS: {_fps}  |  TPS: {TargetTPS}  |  Entities: {_entityManager.EntityCount}\n" +
                $"LOD: Full={_lodSystem?.CountFull ?? 0}  " +
                $"High={_lodSystem?.CountHigh ?? 0}  " +
                $"Med={_lodSystem?.CountMedium ?? 0}  " +
                $"Low={_lodSystem?.CountLow ?? 0}  " +
                $"Min={_lodSystem?.CountMinimal ?? 0}\n" +
                $"Herbivores: {_herbivoreCount}  Predators: {_predatorCount}  " +
                $"Shroomers: {_shroomerCount}  Sectids: {_sectidCount}  " +
                $"Faelings: {_faelingCount}";

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
        }
    }
}
