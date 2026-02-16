using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Rendering;
using Mitosis.Systems;
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
    [Export] public int WorldSizeChunks = 24;
    [Export] public int WorldSeed = 0;
    [Export] public int TileSize = 16;
    [Export] public int TargetTPS = 20;
    [Export] public int MaxPopulation = 15000;
    [Export] public int InitialPopulation = 1500;  // Starting population (separate from max)
    [Export] public float HerbivoreRatio = 0.85f;
    [Export] public float CreaturesPerChunk = 2f;

    // Spectator settings
    [Export] public float PlayerSpeed = 1.0f;         // Base movement speed (tiles per tick)
    [Export] public float PlayerSprintMultiplier = 3.0f;  // Speed when holding shift
    [Export] public float ZoomMin = 0.1f;             // Maximum zoom out
    [Export] public float ZoomMax = 5.0f;             // Maximum zoom in
    [Export] public float ZoomSpeed = 0.15f;          // Zoom sensitivity

    // Faction spawning
    [Export] public int CrystalCount = 5;             // Number of Faeling crystals in the world
    [Export] public int InitialNestsPerColony = 2;    // Starting Sectid nests per colony
    [Export] public int InitialSectidColonies = 3;    // Starting Sectid colonies

    // Core systems
    private EntityManager _entityManager = null!;
    private WorldManager _worldManager = null!;
    private readonly List<ISystem> _systems = new();
    private readonly Random _rng = new();
    private LODSystem? _lodSystem;
    private NestSystem? _nestSystem;
    private CrystalSystem? _crystalSystem;

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

        // Initialize systems - LODSystem must be first to update LOD levels
        var spatialHash = _worldManager.SpatialHash;
        _lodSystem = new LODSystem();
        _systems.Add(_lodSystem);
        _systems.Add(new MovementSystem(ChunkSize, WorldSizeChunks, _worldManager));
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
        _nestSystem = new NestSystem(_worldManager, spatialHash);
        _systems.Add(_nestSystem);
        var sporeSystem = new SporeSystem(_worldManager, spatialHash);
        _systems.Add(sporeSystem);
        _crystalSystem = new CrystalSystem(_worldManager, spatialHash);
        _systems.Add(_crystalSystem);

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

        // Spawn faction structures (crystals and nests) before creatures
        GD.Print("Spawning faction structures...");
        _worldSpawner.SpawnCrystals(_crystalSystem!, _entityManager, CrystalCount);
        _worldSpawner.SpawnInitialNests(_nestSystem!, _entityManager, InitialNestsPerColony, InitialSectidColonies);

        // Spawn creatures (herbivores, predators, initial Shroomers)
        // Sectids and Faelings are spawned by their respective systems
        GD.Print("Spawning creatures...");
        int spawnedCount = _worldSpawner.SpawnCreatures(InitialPopulation, HerbivoreRatio);
        GD.Print($"Spawned {spawnedCount} creatures");

        // Spawn player
        float centerX = WorldSizeChunks * ChunkSize / 2f;
        float centerY = WorldSizeChunks * ChunkSize / 2f;
        int playerEntity = _entityFactory.SpawnPlayer(centerX, centerY, _worldManager);
        _playerController.SetPlayerEntity(playerEntity, TileSize, _camera);

        // Set player entity for LOD system
        _lodSystem?.SetPlayerEntity(_playerController.PlayerEntity);

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

        // Handle input
        _playerController.HandleInput();
        _playerController.HandleZoomInput(_camera);

        // Fixed timestep simulation
        _simulationAccumulator += delta;
        while (_simulationAccumulator >= _simulationDt)
        {
            foreach (var system in _systems)
            {
                system.Process(_entityManager);
            }
            _simulationAccumulator -= _simulationDt;
        }

        // Update camera
        _playerController.UpdateCamera(_camera, delta);

        // Update stats periodically
        UpdateStats(delta);

        // Update entity MultiMesh buffers for rendering
        if (_camera != null)
            _renderingManager.UpdateEntityMultiMeshes(_camera);

        // Request redraw (for terrain and debug overlay)
        QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        _playerController.HandleMouseZoom(@event, _camera);
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

        // Count entities by type
        _herbivoreCount = 0;
        _predatorCount = 0;
        _shroomerCount = 0;
        _sectidCount = 0;
        _faelingCount = 0;
        _nestCount = 0;
        _sporeCount = 0;
        _crystalCount = 0;

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

        // Update debug label
        if (_debugLabel != null)
        {
            _debugLabel.Text = $"FPS: {_fps}\n" +
                              $"Entities: {_entityManager.EntityCount}\n" +
                              $"Herbivores: {_herbivoreCount}\n" +
                              $"Predators: {_predatorCount}\n" +
                              $"Shroomers: {_shroomerCount} (spores: {_sporeCount})\n" +
                              $"Sectids: {_sectidCount} (nests: {_nestCount})\n" +
                              $"Faelings: {_faelingCount} (crystals: {_crystalCount})\n" +
                              $"TPS: {TargetTPS}";
        }
    }
}
