using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Systems;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis;

/// <summary>
/// Main game manager that runs the simulation.
/// </summary>
public partial class GameManager : Node2D
{
    // Configuration
    [Export] public int ChunkSize = 32;
    [Export] public int WorldSizeChunks = 16;
    [Export] public int WorldSeed = 42;
    [Export] public int TileSize = 16;
    [Export] public int TargetTPS = 20;
    [Export] public int MaxPopulation = 500;
    [Export] public float HerbivoreRatio = 0.85f;
    [Export] public float CreaturesPerChunk = 2f;

    // Core systems
    private EntityManager _entityManager = null!;
    private WorldManager _worldManager = null!;
    private readonly List<ISystem> _systems = new();
    private readonly Random _rng = new();
    private LODSystem? _lodSystem;

    // Simulation timing
    private double _simulationAccumulator;
    private double _simulationDt;

    // Player entity
    private int _playerEntity = -1;
    private Vector2 _cameraTarget;

    // Debug info
    private int _fps;
    private double _fpsTimer;
    private int _frameCount;
    private int _herbivoreCount;
    private int _predatorCount;
    private double _statsTimer;

    // Rendering
    private Camera2D? _camera;
    private Label? _debugLabel;

    public override void _Ready()
    {
        _entityManager = new EntityManager();
        _worldManager = new WorldManager(ChunkSize, WorldSizeChunks, WorldSeed);
        _simulationDt = 1.0 / TargetTPS;

        // Initialize systems - LODSystem must be first to update LOD levels
        var spatialHash = _worldManager.SpatialHash;
        _lodSystem = new LODSystem();
        _systems.Add(_lodSystem);
        _systems.Add(new MovementSystem(ChunkSize, WorldSizeChunks, _worldManager));
        _systems.Add(new TerrainDiscomfortSystem(_worldManager));  // Process discomfort early
        _systems.Add(new HungerSystem());
        _systems.Add(new GrazingSystem(_worldManager));
        _systems.Add(new WanderSystem(_worldManager));
        _systems.Add(new HerdingSystem(spatialHash, socialRadius: 10f));
        _systems.Add(new SeparationSystem(spatialHash, separationRadius: 2.5f, separationStrength: 0.03f));
        _systems.Add(new CollisionSystem(spatialHash, collisionRadiusScale: 0.5f, tileSize: TileSize));
        _systems.Add(new HuntingSystem(spatialHash, _worldManager));
        _systems.Add(new FleeingSystem(spatialHash, _worldManager));
        _systems.Add(new AgingSystem());
        _systems.Add(new ReproductionSystem(_worldManager, MaxPopulation));

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

        // Spawn creatures
        GD.Print("Spawning creatures...");
        int spawnedCount = SpawnCreatures();
        GD.Print($"Spawned {spawnedCount} creatures");

        // Spawn player
        SpawnPlayer();

        // Set player entity for LOD system
        _lodSystem?.SetPlayerEntity(_playerEntity);

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

    // Group ID counter for spawning groups together
    private int _nextSpawnGroupId = 1;

    private int SpawnCreatures()
    {
        int spawned = 0;
        int targetPerChunk = (int)CreaturesPerChunk;

        foreach (var chunk in _worldManager.GetLoadedChunks())
        {
            var positions = _worldManager.GetWalkablePositions(chunk, targetPerChunk * 3, _rng);
            if (positions.Count == 0) continue;

            int posIndex = 0;

            while (posIndex < positions.Count && _entityManager.EntityCount < MaxPopulation)
            {
                var (x, y) = positions[posIndex];
                bool isHerbivore = _rng.NextDouble() < HerbivoreRatio;

                if (isHerbivore)
                {
                    // Spawn a herd together
                    int herdSize = 3 + _rng.Next(0, 5);  // 3-7 members
                    int groupId = _nextSpawnGroupId++;
                    spawned += SpawnGroup(x, y, true, herdSize, groupId, positions, ref posIndex);
                }
                else
                {
                    // Check if this will be a pack or solitary
                    bool isPack = _rng.NextDouble() < 0.6;
                    if (isPack)
                    {
                        // Spawn a pack together
                        int packSize = 2 + _rng.Next(0, 3);  // 2-4 members
                        int groupId = _nextSpawnGroupId++;
                        spawned += SpawnGroup(x, y, false, packSize, groupId, positions, ref posIndex);
                    }
                    else
                    {
                        // Spawn solitary predator
                        SpawnCreature(x, y, false, -1, true);
                        spawned++;
                        posIndex++;
                    }
                }
            }
        }

        return spawned;
    }

    /// <summary>
    /// Spawns a group of creatures around a central position.
    /// </summary>
    private int SpawnGroup(float centerX, float centerY, bool isHerbivore, int groupSize,
                           int groupId, List<(float x, float y)> positions, ref int posIndex)
    {
        int spawned = 0;
        float groupRadius = 3f;  // Spawn within this radius of center

        for (int i = 0; i < groupSize && _entityManager.EntityCount < MaxPopulation; i++)
        {
            float x = centerX, y = centerY;  // Default to center position

            // First member (alpha) spawns at the given position
            if (i == 0 && posIndex < positions.Count)
            {
                (x, y) = positions[posIndex];
                posIndex++;
            }
            else
            {
                // Other members spawn nearby
                // Try to find a walkable position near center
                bool found = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = (float)(_rng.NextDouble() * groupRadius);
                    x = centerX + MathF.Cos(angle) * dist;
                    y = centerY + MathF.Sin(angle) * dist;

                    if (_worldManager.IsWalkable(x, y))
                    {
                        found = true;
                        break;
                    }
                }

                // If we couldn't find a good spot, use the next available position or center
                if (!found)
                {
                    if (posIndex < positions.Count)
                    {
                        (x, y) = positions[posIndex];
                        posIndex++;
                    }
                    else
                    {
                        // Fall back to center position (already set as default)
                        x = centerX;
                        y = centerY;
                    }
                }
            }

            // Spawn with group ID (alpha = first member, higher leadership)
            bool isAlpha = (i == 0);
            SpawnCreature(x, y, isHerbivore, groupId, false, isAlpha);
            spawned++;
        }

        return spawned;
    }

    /// <summary>
    /// Applies random variation to a base value (±percentage).
    /// </summary>
    private float Vary(float baseValue, float variationPercent = 0.2f)
    {
        float variation = baseValue * variationPercent;
        return baseValue + (float)(_rng.NextDouble() * 2 - 1) * variation;
    }

    /// <summary>
    /// Spawns a creature at the given position.
    /// </summary>
    /// <param name="groupId">Pre-assigned group ID (-1 for no group)</param>
    /// <param name="forceSolitary">Force solitary social type (for predators)</param>
    /// <param name="isAlpha">Whether this is the group leader (higher leadership score)</param>
    private void SpawnCreature(float x, float y, bool isHerbivore, int groupId = -1,
                               bool forceSolitary = false, bool isAlpha = false)
    {
        int entity = _entityManager.CreateEntity();

        _entityManager.Positions[entity] = new Position(x, y);
        _entityManager.AddComponent(entity, ComponentFlags.Position);

        _entityManager.Velocities[entity] = new Velocity();
        _entityManager.AddComponent(entity, ComponentFlags.Velocity);

        _entityManager.ChunkPositions[entity] = new ChunkPosition();
        _entityManager.AddComponent(entity, ComponentFlags.ChunkPosition);

        _entityManager.Ages[entity] = new Age(
            current: _rng.Next(0, 5000),
            maxLifespan: isHerbivore ? 30000 : 24000,
            maturityAge: isHerbivore ? 2000 : 1500
        );
        _entityManager.AddComponent(entity, ComponentFlags.Age);

        _entityManager.Energies[entity] = new Energy(100f);
        _entityManager.AddComponent(entity, ComponentFlags.Energy);

        _entityManager.Reproductions[entity] = new Reproduction(
            hungerThreshold: isHerbivore ? 70f : 75f,
            energyThreshold: isHerbivore ? 80f : 85f,
            cooldown: isHerbivore ? 600 : 800
        );
        _entityManager.AddComponent(entity, ComponentFlags.Reproduction);

        _entityManager.SimulationLODs[entity] = new SimulationLOD();
        _entityManager.AddComponent(entity, ComponentFlags.SimulationLOD);

        if (isHerbivore)
        {
            _entityManager.Species[entity] = new Species(SpeciesType.Herbivore);
            _entityManager.AddComponent(entity, ComponentFlags.Species);

            _entityManager.Hungers[entity] = new Hunger(80f, decayRate: Vary(0.05f, 0.15f));
            _entityManager.AddComponent(entity, ComponentFlags.Hunger);

            // Vary speed and direction change chance (±20%)
            _entityManager.Wanders[entity] = new Wander(Vary(0.03f), Vary(0.005f));
            _entityManager.AddComponent(entity, ComponentFlags.Wander);

            // Vary flee range and speed multiplier (±25%)
            _entityManager.Preys[entity] = new Prey(Vary(6f, 0.25f), Vary(2f, 0.25f));
            _entityManager.AddComponent(entity, ComponentFlags.Prey);

            // Slight size variation for visual diversity
            _entityManager.Renderables[entity] = new Renderable(
                new Color(0.4f, 1f, 0.4f), Vary(8f, 0.15f), ShapeType.Circle);
            _entityManager.AddComponent(entity, ComponentFlags.Renderable);

            // Herbivores are herd animals with high group affinity
            var herbSocial = new Social(
                type: SocialType.Herd,
                groupAffinity: Vary(0.7f, 0.3f),        // 0.5-0.9 (most are social)
                preferredGroupSize: Vary(6f, 0.4f),     // 3.6-8.4 members
                cohesionStrength: Vary(0.025f, 0.2f),
                alignmentStrength: Vary(0.015f, 0.2f)
            );
            herbSocial.GroupId = groupId;
            herbSocial.LeadershipScore = isAlpha ? 0.8f : Vary(0.3f, 0.5f);
            _entityManager.Socials[entity] = herbSocial;
            _entityManager.AddComponent(entity, ComponentFlags.Social);

            // Terrain discomfort with grazing pressure (hungry herbivores feel pressure on non-grazeable terrain)
            _entityManager.TerrainDiscomforts[entity] = new TerrainDiscomfort(
                threshold: Vary(50f, 0.2f),
                decayRate: Vary(2f, 0.2f),
                grazingPressure: Vary(1.5f, 0.3f)   // Herbivores feel hunger pressure on bad terrain
            );
            _entityManager.AddComponent(entity, ComponentFlags.TerrainDiscomfort);
        }
        else
        {
            _entityManager.Species[entity] = new Species(SpeciesType.Carnivore);
            _entityManager.AddComponent(entity, ComponentFlags.Species);

            _entityManager.Hungers[entity] = new Hunger(70f, decayRate: Vary(0.08f, 0.15f));
            _entityManager.AddComponent(entity, ComponentFlags.Hunger);

            // Vary speed and direction change chance (±20%)
            _entityManager.Wanders[entity] = new Wander(Vary(0.06f), Vary(0.01f));
            _entityManager.AddComponent(entity, ComponentFlags.Wander);

            // Vary hunt range, attack range, and attack power (±25%)
            // attackRange varies from 0.6-1.0 tiles (some pounce, some jab from distance)
            _entityManager.Predators[entity] = new Predator(
                huntRange: Vary(12f, 0.25f),
                attackRange: Vary(0.8f, 0.25f),
                attackPower: Vary(30f, 0.25f));
            _entityManager.AddComponent(entity, ComponentFlags.Predator);

            // Slight size variation for visual diversity
            _entityManager.Renderables[entity] = new Renderable(
                new Color(1f, 0.4f, 0.4f), Vary(10f, 0.15f), ShapeType.Triangle);
            _entityManager.AddComponent(entity, ComponentFlags.Renderable);

            // Predators: pack hunters or solitary hunters (determined by caller)
            if (!forceSolitary && groupId >= 0)
            {
                // Pack hunter - spawned as part of a group
                var packSocial = new Social(
                    type: SocialType.Pack,
                    groupAffinity: Vary(0.5f, 0.4f),        // 0.3-0.7
                    preferredGroupSize: Vary(3f, 0.3f),     // 2-4 members
                    cohesionStrength: Vary(0.015f, 0.2f),   // Weaker than herbivores
                    alignmentStrength: Vary(0.01f, 0.2f)
                );
                packSocial.GroupId = groupId;
                packSocial.LeadershipScore = isAlpha ? 0.8f : Vary(0.3f, 0.5f);
                _entityManager.Socials[entity] = packSocial;
            }
            else
            {
                // Solitary hunter - still has Social component but won't group
                _entityManager.Socials[entity] = new Social(
                    type: SocialType.Solitary,
                    groupAffinity: 0f,
                    preferredGroupSize: 0f,
                    cohesionStrength: 0f,
                    alignmentStrength: 0f
                );
            }
            _entityManager.AddComponent(entity, ComponentFlags.Social);

            // Terrain discomfort (no grazing pressure for predators)
            _entityManager.TerrainDiscomforts[entity] = new TerrainDiscomfort(
                threshold: Vary(60f, 0.2f),    // Predators tolerate slightly more discomfort
                decayRate: Vary(2.5f, 0.2f),   // Slightly faster recovery
                grazingPressure: 0f            // No grazing pressure for carnivores
            );
            _entityManager.AddComponent(entity, ComponentFlags.TerrainDiscomfort);
        }
    }

    private void SpawnPlayer()
    {
        float centerX = WorldSizeChunks * ChunkSize / 2f;
        float centerY = WorldSizeChunks * ChunkSize / 2f;

        // Find walkable position near center
        for (int attempts = 0; attempts < 100; attempts++)
        {
            float x = centerX + _rng.Next(-10, 10);
            float y = centerY + _rng.Next(-10, 10);

            if (_worldManager.IsWalkable(x, y))
            {
                _playerEntity = _entityManager.CreateEntity();

                _entityManager.Positions[_playerEntity] = new Position(x, y);
                _entityManager.AddComponent(_playerEntity, ComponentFlags.Position);

                _entityManager.Velocities[_playerEntity] = new Velocity();
                _entityManager.AddComponent(_playerEntity, ComponentFlags.Velocity);

                _entityManager.Renderables[_playerEntity] = new Renderable(
                    new Color(1f, 1f, 0f), 12f, ShapeType.Circle);
                _entityManager.AddComponent(_playerEntity, ComponentFlags.Renderable);

                _cameraTarget = new Vector2(x * TileSize, y * TileSize);
                if (_camera != null)
                    _camera.Position = _cameraTarget;

                return;
            }
        }
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
        HandleInput();

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
        UpdateCamera(delta);

        // Update stats periodically
        UpdateStats(delta);

        // Request redraw
        QueueRedraw();
    }

    private void UpdateStats(double delta)
    {
        _statsTimer += delta;
        if (_statsTimer < 0.5) return;
        _statsTimer = 0;

        // Count entities by type
        _herbivoreCount = 0;
        _predatorCount = 0;

        const ComponentFlags speciesRequired = ComponentFlags.Species;
        foreach (int entity in _entityManager.Query(speciesRequired))
        {
            ref var species = ref _entityManager.Species[entity];
            if (species.Type == SpeciesType.Herbivore)
                _herbivoreCount++;
            else if (species.Type == SpeciesType.Carnivore)
                _predatorCount++;
        }

        // Update debug label
        if (_debugLabel != null)
        {
            _debugLabel.Text = $"FPS: {_fps}\n" +
                              $"Entities: {_entityManager.EntityCount}\n" +
                              $"Herbivores: {_herbivoreCount}\n" +
                              $"Predators: {_predatorCount}\n" +
                              $"TPS: {TargetTPS}";
        }
    }

    private void HandleInput()
    {
        if (_playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var vel = ref _entityManager.Velocities[_playerEntity];
        const float speed = 0.2f;

        vel.Dx = 0;
        vel.Dy = 0;

        if (Input.IsActionPressed("move_up")) vel.Dy = -speed;
        if (Input.IsActionPressed("move_down")) vel.Dy = speed;
        if (Input.IsActionPressed("move_left")) vel.Dx = -speed;
        if (Input.IsActionPressed("move_right")) vel.Dx = speed;

        // Normalize diagonal movement
        if (vel.Dx != 0 && vel.Dy != 0)
        {
            vel.Dx *= 0.707f;
            vel.Dy *= 0.707f;
        }

        // Zoom
        if (Input.IsActionJustPressed("zoom_in") && _camera != null)
            _camera.Zoom = (_camera.Zoom * 1.2f).Clamp(new Vector2(0.25f, 0.25f), new Vector2(4f, 4f));
        if (Input.IsActionJustPressed("zoom_out") && _camera != null)
            _camera.Zoom = (_camera.Zoom / 1.2f).Clamp(new Vector2(0.25f, 0.25f), new Vector2(4f, 4f));
    }

    private void UpdateCamera(double delta)
    {
        if (_camera == null || _playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var pos = ref _entityManager.Positions[_playerEntity];
        _cameraTarget = new Vector2(pos.X * TileSize, pos.Y * TileSize);

        // Smooth camera follow
        _camera.Position = _camera.Position.Lerp(_cameraTarget, (float)(5.0 * delta));
    }

    public override void _Draw()
    {
        if (_camera == null) return;

        // Get visible area in world coordinates
        var viewportSize = GetViewportRect().Size;
        var cameraPos = _camera.Position;
        var zoom = _camera.Zoom;

        float halfWidth = viewportSize.X / (2 * zoom.X);
        float halfHeight = viewportSize.Y / (2 * zoom.Y);

        float minWorldX = (cameraPos.X - halfWidth) / TileSize;
        float maxWorldX = (cameraPos.X + halfWidth) / TileSize;
        float minWorldY = (cameraPos.Y - halfHeight) / TileSize;
        float maxWorldY = (cameraPos.Y + halfHeight) / TileSize;

        // Draw visible chunks
        int minChunkX = Math.Max(0, (int)(minWorldX / ChunkSize));
        int maxChunkX = Math.Min(WorldSizeChunks - 1, (int)(maxWorldX / ChunkSize));
        int minChunkY = Math.Max(0, (int)(minWorldY / ChunkSize));
        int maxChunkY = Math.Min(WorldSizeChunks - 1, (int)(maxWorldY / ChunkSize));

        for (int cx = minChunkX; cx <= maxChunkX; cx++)
        {
            for (int cy = minChunkY; cy <= maxChunkY; cy++)
            {
                var chunk = _worldManager.GetChunk(cx, cy);
                if (chunk == null) continue;

                DrawChunk(chunk);
            }
        }

        // Draw entities
        DrawEntities();

        // Draw debug info (in screen space)
        DrawDebugInfo();
    }

    private void DrawChunk(Chunk chunk)
    {
        float chunkWorldX = chunk.ChunkX * ChunkSize * TileSize;
        float chunkWorldY = chunk.ChunkY * ChunkSize * TileSize;

        for (int ly = 0; ly < chunk.Size; ly++)
        {
            for (int lx = 0; lx < chunk.Size; lx++)
            {
                var tileType = chunk.GetTile(lx, ly);
                var color = Chunk.GetTileColor(tileType);

                float x = chunkWorldX + lx * TileSize;
                float y = chunkWorldY + ly * TileSize;

                DrawRect(new Rect2(x, y, TileSize, TileSize), color);
            }
        }
    }

    private void DrawEntities()
    {
        const ComponentFlags required = ComponentFlags.Position | ComponentFlags.Renderable;

        foreach (int entity in _entityManager.Query(required))
        {
            ref var pos = ref _entityManager.Positions[entity];
            ref var rend = ref _entityManager.Renderables[entity];

            float screenX = pos.X * TileSize;
            float screenY = pos.Y * TileSize;

            switch (rend.Shape)
            {
                case ShapeType.Circle:
                    DrawCircle(new Vector2(screenX, screenY), rend.Size, rend.Color);
                    break;
                case ShapeType.Triangle:
                    DrawTriangle(new Vector2(screenX, screenY), rend.Size, rend.Color);
                    break;
                case ShapeType.Square:
                    DrawRect(new Rect2(screenX - rend.Size / 2, screenY - rend.Size / 2, rend.Size, rend.Size), rend.Color);
                    break;
            }
        }
    }

    private void DrawTriangle(Vector2 center, float size, Color color)
    {
        var points = new Vector2[]
        {
            center + new Vector2(0, -size),
            center + new Vector2(-size * 0.866f, size * 0.5f),
            center + new Vector2(size * 0.866f, size * 0.5f)
        };
        DrawPolygon(points, new Color[] { color, color, color });
    }

    private void DrawDebugInfo()
    {
        // This would need to be drawn in a CanvasLayer for proper screen-space rendering
        // For now, we'll just output to console periodically
    }
}
