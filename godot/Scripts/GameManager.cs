using System;
using System.Collections.Generic;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
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
    [Export] public int InitialPopulation = 200;  // Starting population (separate from max)
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
        _systems.Add(new WanderSystem(_worldManager, spatialHash: spatialHash));
        _systems.Add(new HerdingSystem(spatialHash, socialRadius: 10f));
        _systems.Add(new SeparationSystem(spatialHash, separationRadius: 2.5f, separationStrength: 0.03f));
        _systems.Add(new CollisionSystem(spatialHash, collisionRadiusScale: 0.5f, tileSize: TileSize));
        _systems.Add(new HuntingSystem(spatialHash, _worldManager,
            huntThreshold: 0.75f));  // Hunt when below 75% hunger
        _systems.Add(new FleeingSystem(spatialHash, _worldManager));
        _systems.Add(new AgingSystem());
        _systems.Add(new ReproductionSystem(_worldManager, MaxPopulation));
        _systems.Add(new TerraformSystem(_worldManager));

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
        SpawnCrystals();
        SpawnInitialNests();

        // Spawn creatures (herbivores, predators, initial Shroomers)
        // Sectids and Faelings are spawned by their respective systems
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

        // Collect all species by category
        // Sectids spawn from nests, Faelings from crystals — only Shroomers use normal spawning
        var herbivoreSpecies = new List<SpeciesDefinition>(SpeciesRegistry.GetHerbivores());
        var predatorSpecies = new List<SpeciesDefinition>(SpeciesRegistry.GetPredators());
        var terraformerSpecies = new List<SpeciesDefinition>();
        foreach (var tf in SpeciesRegistry.GetTerraformers())
        {
            if (!tf.NestBreeder && !tf.CrystalSpawned)
                terraformerSpecies.Add(tf); // Only Shroomers
        }

        // Budget all categories from InitialPopulation (not additive)
        float terraformerShare = terraformerSpecies.Count > 0 ? 0.12f : 0f;
        float predatorShare = predatorSpecies.Count > 0 ? (1f - HerbivoreRatio) * (1f - terraformerShare) : 0f;
        float herbivoreShare = 1f - predatorShare - terraformerShare;

        int targetHerbivores = (int)(InitialPopulation * herbivoreShare);
        int targetPredators = (int)(InitialPopulation * predatorShare);
        int targetTerraformers = InitialPopulation - targetHerbivores - targetPredators;

        GD.Print($"Spawn targets: {targetHerbivores} herbivores, {targetPredators} predators, {targetTerraformers} terraformers (total {InitialPopulation})");

        // Get all chunks and shuffle
        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);

        // Scale position sampling based on target size
        int baseSamples = Math.Max(6, InitialPopulation / allChunks.Count + 2);

        // Track which chunks have prey spawned in them (for predator placement)
        var preyChunks = new List<Chunk>();

        // === Phase 1: Spawn herbivores spread across the world ===
        // Different herbivore species can share territory — this is natural.
        spawned += SpawnCategory(herbivoreSpecies, targetHerbivores, allChunks, preyChunks, baseSamples);

        // === Phase 2: Spawn terraformers in their preferred biomes ===
        ShuffleList(allChunks);
        spawned += SpawnCategory(terraformerSpecies, targetTerraformers, allChunks, preyChunks, baseSamples);

        // === Phase 3: Spawn predators NEAR existing prey populations ===
        // Predators need food — place them in or adjacent to chunks with prey.
        if (predatorSpecies.Count > 0 && preyChunks.Count > 0)
        {
            // Build a list of chunks near prey: the prey chunks themselves + their neighbors
            var predatorCandidateChunks = new List<Chunk>();
            var addedChunks = new HashSet<(int, int)>();

            foreach (var preyChunk in preyChunks)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int cx = preyChunk.ChunkX + dx;
                        int cy = preyChunk.ChunkY + dy;
                        if (addedChunks.Add((cx, cy)))
                        {
                            var neighbor = _worldManager.GetChunk(cx, cy);
                            if (neighbor != null)
                                predatorCandidateChunks.Add(neighbor);
                        }
                    }
                }
            }

            int perSpecies = targetPredators / predatorSpecies.Count;
            int remainder = targetPredators % predatorSpecies.Count;

            foreach (var species in predatorSpecies)
            {
                int target = perSpecies + (remainder-- > 0 ? 1 : 0);
                int speciesSpawned = 0;

                // Round-robin: one group/solo per chunk per pass
                const int maxPasses = 10;
                for (int pass = 0; pass < maxPasses && speciesSpawned < target; pass++)
                {
                    ShuffleList(predatorCandidateChunks);
                    bool spawnedAnyThisPass = false;

                    foreach (var chunk in predatorCandidateChunks)
                    {
                        if (speciesSpawned >= target) break;

                        var positions = _worldManager.GetSpawnablePositionsForSpecies(
                            chunk, baseSamples, _rng, species);
                        if (positions.Count == 0) continue;

                        var (x, y, _, _) = positions[0];

                        bool isPack = _rng.NextDouble() < species.PackHunterChance
                                      && species.DefaultSocialType == SocialType.Pack;

                        if (!isPack)
                        {
                            SpawnCreature(x, y, species, -1, true);
                            speciesSpawned++;
                            spawned++;
                        }
                        else
                        {
                            int packSize = (int)species.PreferredGroupSize + _rng.Next(-1, 2);
                            packSize = Math.Max(2, packSize);
                            packSize = Math.Min(packSize, target - speciesSpawned);

                            int posIndex = 0;
                            int gid = _nextSpawnGroupId++;
                            int gs = SpawnGroup(x, y, species, packSize, gid, ref posIndex);
                            speciesSpawned += gs;
                            spawned += gs;
                        }

                        spawnedAnyThisPass = true;
                    }

                    if (!spawnedAnyThisPass) break;
                }

                GD.Print($"  {species.Name}: spawned {speciesSpawned}/{target}");
            }
        }

        return spawned;
    }

    /// <summary>
    /// Spawns a category of species spread across chunks using round-robin distribution.
    /// Places at most one group per chunk per pass, then re-shuffles and cycles again.
    /// This ensures entities are spread across the entire world, not clumped in early chunks.
    /// </summary>
    private int SpawnCategory(List<SpeciesDefinition> speciesList, int totalTarget,
                               List<Chunk> chunks, List<Chunk> preyChunks, int samplesPerChunk)
    {
        if (speciesList.Count == 0 || totalTarget <= 0) return 0;

        int spawned = 0;
        int perSpecies = totalTarget / speciesList.Count;
        int remainder = totalTarget % speciesList.Count;

        foreach (var species in speciesList)
        {
            int target = perSpecies + (remainder-- > 0 ? 1 : 0);
            int speciesSpawned = 0;
            var preyChunkSet = new HashSet<(int, int)>();

            // Round-robin: keep cycling through shuffled chunks, one group per chunk per pass
            const int maxPasses = 10;  // Safety limit
            for (int pass = 0; pass < maxPasses && speciesSpawned < target; pass++)
            {
                ShuffleList(chunks);
                bool spawnedAnyThisPass = false;

                foreach (var chunk in chunks)
                {
                    if (speciesSpawned >= target) break;

                    var positions = _worldManager.GetSpawnablePositionsForSpecies(
                        chunk, samplesPerChunk, _rng, species);
                    if (positions.Count == 0) continue;

                    // One group per chunk per pass
                    var (x, y, _, _) = positions[0];

                    int groupSize = (int)species.PreferredGroupSize + _rng.Next(-2, 3);
                    groupSize = Math.Max(2, groupSize);
                    groupSize = Math.Min(groupSize, target - speciesSpawned);

                    int posIndex = 0;
                    int gid = _nextSpawnGroupId++;
                    int gs = SpawnGroup(x, y, species, groupSize, gid, ref posIndex);
                    speciesSpawned += gs;
                    spawned += gs;
                    spawnedAnyThisPass |= gs > 0;

                    // Track prey chunks (deduplicated)
                    if (gs > 0 && species.IsPrey && preyChunkSet.Add((chunk.ChunkX, chunk.ChunkY)))
                        preyChunks.Add(chunk);
                }

                if (!spawnedAnyThisPass) break;  // No valid chunks left for this species
            }

            GD.Print($"  {species.Name}: spawned {speciesSpawned}/{target}");
        }

        return spawned;
    }

    /// <summary>
    /// Fisher-Yates shuffle for any list.
    /// </summary>
    private void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Spawns a group of creatures around a central position.
    /// </summary>
    private int SpawnGroup(float centerX, float centerY, SpeciesDefinition species, int groupSize,
                           int groupId, ref int posIndex)
    {
        int spawned = 0;
        float groupRadius = 3f;  // Spawn within this radius of center

        // Increment posIndex for the center position we're using
        posIndex++;

        for (int i = 0; i < groupSize; i++)
        {
            float x = centerX, y = centerY;  // Default to center position

            // First member (alpha) spawns at the given position
            if (i == 0)
            {
                x = centerX;
                y = centerY;
            }
            else
            {
                // Other members spawn nearby - try to find a spawnable position for this species
                bool found = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = (float)(_rng.NextDouble() * groupRadius);
                    x = centerX + MathF.Cos(angle) * dist;
                    y = centerY + MathF.Sin(angle) * dist;

                    if (_worldManager.IsSpawnableForSpecies(x, y, species))
                    {
                        found = true;
                        break;
                    }
                }

                // If we couldn't find a spawnable spot, use center
                if (!found)
                {
                    x = centerX;
                    y = centerY;
                }
            }

            // Spawn with group ID (alpha = first member, higher leadership)
            bool isAlpha = (i == 0);
            SpawnCreature(x, y, species, groupId, false, isAlpha);
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
    /// Spawns a creature at the given position using a species definition.
    /// </summary>
    /// <param name="species">The species definition to use</param>
    /// <param name="groupId">Pre-assigned group ID (-1 for no group)</param>
    /// <param name="forceSolitary">Force solitary social type</param>
    /// <param name="isAlpha">Whether this is the group leader (higher leadership score)</param>
    private void SpawnCreature(float x, float y, SpeciesDefinition species, int groupId = -1,
                               bool forceSolitary = false, bool isAlpha = false)
    {
        int entity = _entityManager.CreateEntity();
        float variation = species.StatVariation;

        // Core components
        _entityManager.Positions[entity] = new Position(x, y);
        _entityManager.AddComponent(entity, ComponentFlags.Position);

        _entityManager.Velocities[entity] = new Velocity();
        _entityManager.AddComponent(entity, ComponentFlags.Velocity);

        _entityManager.ChunkPositions[entity] = new ChunkPosition();
        _entityManager.AddComponent(entity, ComponentFlags.ChunkPosition);

        // Age with variation
        _entityManager.Ages[entity] = new Age(
            current: _rng.Next(0, species.MaturityAge * 2),  // Start at random age
            maxLifespan: (int)Vary(species.MaxLifespan, variation),
            maturityAge: (int)Vary(species.MaturityAge, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Age);

        _entityManager.Energies[entity] = new Energy(100f);
        _entityManager.AddComponent(entity, ComponentFlags.Energy);

        // Reproduction from species
        _entityManager.Reproductions[entity] = new Reproduction(
            hungerThreshold: Vary(species.ReproHungerThreshold, variation),
            energyThreshold: Vary(species.ReproEnergyThreshold, variation),
            cooldown: (int)Vary(species.ReproCooldown, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Reproduction);

        _entityManager.SimulationLODs[entity] = new SimulationLOD();
        _entityManager.AddComponent(entity, ComponentFlags.SimulationLOD);

        // Species type based on diet
        SpeciesType speciesType = species.Diet switch
        {
            DietType.Herbivore => SpeciesType.Herbivore,
            DietType.Carnivore => SpeciesType.Carnivore,
            DietType.Omnivore => SpeciesType.Carnivore,
            DietType.Terraformer => species.Name switch
            {
                "Shroomer" => SpeciesType.Shroomer,
                "Sectid" => SpeciesType.Sectid,
                "Faeling" => SpeciesType.Faeling,
                _ => SpeciesType.Faeling
            },
            _ => SpeciesType.Herbivore
        };
        _entityManager.Species[entity] = new Species(speciesType, 0, SpeciesRegistry.GetId(species.Name));
        _entityManager.AddComponent(entity, ComponentFlags.Species);

        // Hunger from species
        _entityManager.Hungers[entity] = new Hunger(
            current: Vary(species.MaxHunger * 0.8f, variation),
            max: Vary(species.MaxHunger, variation),
            decayRate: Vary(species.HungerDecayRate, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Hunger);

        // Wander behavior from species
        _entityManager.Wanders[entity] = new Wander(
            speed: Vary(species.BaseWanderSpeed, variation),
            changeDirectionChance: Vary(species.DirectionChangeChance, variation)
        );
        _entityManager.AddComponent(entity, ComponentFlags.Wander);

        // Visuals from species (slight size variation)
        _entityManager.Renderables[entity] = new Renderable(
            species.BaseColor,
            Vary(species.BaseSize, 0.15f),
            species.Shape
        );
        _entityManager.AddComponent(entity, ComponentFlags.Renderable);

        // Terrain discomfort from species
        _entityManager.TerrainDiscomforts[entity] = new TerrainDiscomfort(
            threshold: Vary(species.DiscomfortThreshold, variation),
            decayRate: Vary(species.DiscomfortDecayRate, variation),
            grazingPressure: species.CanGraze ? Vary(species.GrazingPressure, variation) : 0f
        );
        _entityManager.AddComponent(entity, ComponentFlags.TerrainDiscomfort);

        // Terraform component for faction species
        if (species.Diet == DietType.Terraformer)
        {
            _entityManager.Terraforms[entity] = new Terraform(
                direction: species.TerraformDir,
                radius: Vary(species.TerraformRadius, variation),
                strength: Vary(species.TerraformStrength, variation),
                cooldown: (int)Vary(species.TerraformCooldown, variation)
            );
            _entityManager.AddComponent(entity, ComponentFlags.Terraform);
        }

        // Diet-specific components
        if (species.IsPrey)
        {
            // Prey component for fleeing
            _entityManager.Preys[entity] = new Prey(
                fleeRange: Vary(species.FleeRange, variation),
                fleeSpeedMultiplier: Vary(species.FleeSpeedMultiplier, variation)
            );
            _entityManager.AddComponent(entity, ComponentFlags.Prey);

            // Fear component for nuanced threat response
            _entityManager.Fears[entity] = new Fear(
                threshold: Vary(species.FearThreshold, variation),
                max: Vary(species.FearMax, variation),
                accumulationRate: Vary(species.FearAccumulationRate, variation),
                decayRate: Vary(species.FearDecayRate, variation),
                vigilanceDecay: Vary(species.FearVigilanceDecay, variation),
                response: species.DefaultFearResponse
            );
            _entityManager.AddComponent(entity, ComponentFlags.Fear);
        }

        if (species.IsPredator)
        {
            // Predator component for hunting
            _entityManager.Predators[entity] = new Predator(
                huntRange: Vary(species.HuntRange, variation),
                attackRange: Vary(species.AttackRange, variation),
                attackPower: Vary(species.AttackPower, variation),
                attackCooldown: (int)Vary(species.AttackCooldown, variation)
            );
            _entityManager.AddComponent(entity, ComponentFlags.Predator);
        }

        // Social behavior based on species default and group assignment
        SocialType socialType = species.DefaultSocialType;
        if (forceSolitary)
        {
            socialType = SocialType.Solitary;
        }

        var social = new Social(
            type: socialType,
            groupAffinity: socialType == SocialType.Solitary ? 0f : Vary(species.GroupAffinity, variation),
            preferredGroupSize: socialType == SocialType.Solitary ? 0f : Vary(species.PreferredGroupSize, variation),
            cohesionStrength: socialType == SocialType.Solitary ? 0f : Vary(species.CohesionStrength, variation),
            alignmentStrength: socialType == SocialType.Solitary ? 0f : Vary(species.AlignmentStrength, variation)
        );
        social.GroupId = groupId;
        social.LeadershipScore = isAlpha ? 0.8f : Vary(0.3f, 0.5f);
        _entityManager.Socials[entity] = social;
        _entityManager.AddComponent(entity, ComponentFlags.Social);

        // === Faction-specific components ===

        // Sectids: add Predator + FoodCarrier (they hunt AND carry food to nests)
        if (species.NestBreeder)
        {
            if (!species.IsPredator) // Don't double-add if already a predator
            {
                _entityManager.Predators[entity] = new Predator(
                    huntRange: Vary(species.HuntRange, variation),
                    attackRange: Vary(species.AttackRange, variation),
                    attackPower: Vary(species.AttackPower, variation),
                    attackCooldown: (int)Vary(species.AttackCooldown, variation)
                );
                _entityManager.AddComponent(entity, ComponentFlags.Predator);
            }
            _entityManager.FoodCarriers[entity] = new FoodCarrier(species.MaxCarryFood);
            _entityManager.AddComponent(entity, ComponentFlags.FoodCarrier);
        }

        // Shroomers: add Growth component (they grow over their lifetime)
        if (species.SporeReproducer)
        {
            _entityManager.Growths[entity] = new Growth(maxScale: 4f, growthRate: 0.00008f);
            _entityManager.AddComponent(entity, ComponentFlags.Growth);
        }

        // Faelings spawned from crystals get their components from CrystalSystem,
        // not from GameManager, so no special handling needed here.
    }

    /// <summary>
    /// Spawns Faeling crystals spread across the world on grass tiles.
    /// </summary>
    private void SpawnCrystals()
    {
        if (_crystalSystem == null) return;

        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);

        int spawned = 0;
        int chunkIndex = 0;

        while (spawned < CrystalCount && chunkIndex < allChunks.Count)
        {
            var chunk = allChunks[chunkIndex++];
            var positions = _worldManager.GetSpawnablePositionsForSpecies(
                chunk, 4, _rng, SpeciesRegistry.Get("Faeling"));
            if (positions.Count == 0) continue;

            var (x, y, _, _) = positions[0];
            _crystalSystem.SpawnCrystal(_entityManager, x, y);
            spawned++;
            GD.Print($"  Crystal spawned at ({x:F0}, {y:F0})");
        }

        GD.Print($"  Total crystals: {spawned}/{CrystalCount}");
    }

    /// <summary>
    /// Spawns initial Sectid nests on dry tiles with starter Sectids around each.
    /// </summary>
    private void SpawnInitialNests()
    {
        if (_nestSystem == null) return;

        var allChunks = new List<Chunk>(_worldManager.GetLoadedChunks());
        ShuffleList(allChunks);
        var sectidDef = SpeciesRegistry.Get("Sectid");

        int totalNests = 0;
        int totalSectids = 0;
        int chunkIndex = 0;

        for (int colony = 0; colony < InitialSectidColonies; colony++)
        {
            int colonyId = colony + 1;
            int nestsThisColony = 0;

            while (nestsThisColony < InitialNestsPerColony && chunkIndex < allChunks.Count)
            {
                var chunk = allChunks[chunkIndex++];
                var positions = _worldManager.GetSpawnablePositionsForSpecies(
                    chunk, 4, _rng, sectidDef);
                if (positions.Count == 0) continue;

                var (x, y, _, _) = positions[0];
                int nestEntity = _nestSystem.SpawnNest(_entityManager, x, y, colonyId);
                nestsThisColony++;
                totalNests++;

                // Spawn starter Sectids around each nest
                int starterCount = 4 + _rng.Next(0, 3);
                for (int i = 0; i < starterCount; i++)
                {
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    float dist = 2f + (float)(_rng.NextDouble() * 4f);
                    float sx = x + MathF.Cos(angle) * dist;
                    float sy = y + MathF.Sin(angle) * dist;

                    if (_worldManager.IsWalkable(sx, sy))
                    {
                        SpawnCreature(sx, sy, sectidDef, colonyId);
                        totalSectids++;
                    }
                }
            }

            GD.Print($"  Colony {colonyId}: {nestsThisColony} nests");
        }

        GD.Print($"  Total nests: {totalNests}, starter Sectids: {totalSectids}");
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

    private void HandleInput()
    {
        if (_playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var vel = ref _entityManager.Velocities[_playerEntity];

        // Calculate effective speed (with sprint modifier)
        float speed = PlayerSpeed;
        if (Input.IsKeyPressed(Key.Shift))
            speed *= PlayerSprintMultiplier;

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

        // Keyboard zoom (+/- keys)
        if (Input.IsActionJustPressed("zoom_in"))
            ApplyZoom(1f + ZoomSpeed);
        if (Input.IsActionJustPressed("zoom_out"))
            ApplyZoom(1f - ZoomSpeed);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Mouse wheel zoom
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed)
        {
            if (mouseEvent.ButtonIndex == MouseButton.WheelUp)
                ApplyZoom(1f + ZoomSpeed);
            else if (mouseEvent.ButtonIndex == MouseButton.WheelDown)
                ApplyZoom(1f - ZoomSpeed);
        }
    }

    private void ApplyZoom(float factor)
    {
        if (_camera == null) return;

        var minZoom = new Vector2(ZoomMin, ZoomMin);
        var maxZoom = new Vector2(ZoomMax, ZoomMax);
        _camera.Zoom = (_camera.Zoom * factor).Clamp(minZoom, maxZoom);
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
