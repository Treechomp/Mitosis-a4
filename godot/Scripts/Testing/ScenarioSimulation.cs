using System;
using System.Collections.Generic;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Systems;
using Mitosis.Utils;
using Mitosis.World;

namespace Mitosis.Testing;

/// <summary>
/// One scenario assembled into a runnable simulation, pinned to one LOD tier.
///
/// WHY IT IS ITS OWN CLASS. Two harnesses have to build the *same* simulation or neither can be
/// compared with the other: <see cref="LodDifferentialRunner"/> measures the end state of a
/// Full run against a coarse one, and <see cref="LodDivergenceRunner"/> measures how fast the two
/// separate. A hand-copied second assembly would let the two drift apart silently — the same
/// failure <see cref="SimulationStack"/> exists to prevent one level down, where it settles which
/// systems run and in what order. This settles what they run *over*.
///
/// This is <c>GameManager</c>'s construction sequence minus the parts that only exist to be looked
/// at (camera, lights, meshes, debug UI, world-snapshot PNGs).
///
/// Construction order is load-bearing and must not be rearranged: the seed is fixed before any
/// system that draws from a random stream is built, because the streams are handed out in
/// construction order.
/// </summary>
public sealed class ScenarioSimulation
{
    /// <summary>Mirrors GameManager's exports: the harness must build the same world the game would.</summary>
    public const int ChunkSize = 32;
    public const int TileSize = 16;

    private readonly List<ISystem> _systems;

    public EntityManager Entities { get; }
    public WorldManager World { get; }
    public SimulationStack Stack { get; }
    public EcosystemLogger Logger { get; }
    public PopulationBudget Budget { get; }

    private ScenarioSimulation(List<ISystem> systems, EntityManager entities, WorldManager world,
                               SimulationStack stack, EcosystemLogger logger, PopulationBudget budget)
    {
        _systems = systems;
        Entities = entities;
        World = world;
        Stack = stack;
        Logger = logger;
        Budget = budget;
    }

    /// <summary>
    /// Build the world, the systems and the spawns, pin every entity to <paramref name="tier"/>,
    /// and leave the simulation ready for its first <see cref="Tick"/>.
    /// </summary>
    /// <param name="logDir">Per-run log directory. Each run gets its own, because the per-run CSVs
    /// are what you open when a number surprises you and two runs would otherwise overwrite.</param>
    public static ScenarioSimulation Build(TestScenario scenario, int seed, LODLevel tier, string logDir)
    {
        int sizeChunks = Math.Max(1, scenario.SizeChunks);

        // Fix every system's random stream BEFORE the systems that draw from one are built.
        SimRandom.SetSeed(seed);
        var rng = SimRandom.Create();

        var em = new EntityManager();
        em.OnEntityDying = id => CarrionSystem.SpawnCorpse(em, id);

        // Same per-class ceilings the game builds, at the scenario's own MaxPopulation. Without
        // this the harness would be comparing tiers inside a world the game no longer runs — and
        // the budget's refusal path is itself LOD-adjacent, since a refused spawn is a spawn that
        // did not happen at whichever tier asked for it.
        var budget = new PopulationBudget(scenario.MaxPopulation,
            PopulationBudget.DefaultHerbivoreShare,
            PopulationBudget.DefaultPredatorShare,
            PopulationBudget.DefaultFactionShare);
        em.Budget = budget;

        var world = new WorldManager(ChunkSize, sizeChunks, seed,
            new ScenarioTerrainGenerator(scenario, ChunkSize));

        var factory = new EntityFactory(em, rng);
        factory.SetPopulationCap(scenario.MaxPopulation);
        factory.SetBudget(budget);

        var systems = new List<ISystem>();
        var stack = SimulationStack.Build(systems, world, factory,
            ChunkSize, sizeChunks, TileSize, scenario.MaxPopulation, budget);

        // Decision logging stays off — it is per-decision and enormous, and two runs at different
        // tiers make a different number of decisions by construction.
        EcosystemLogger.SnapshotInterval = Math.Max(1, scenario.SnapshotInterval);
        EcosystemLogger.DecisionLoggingEnabled = false;
        EcosystemLogger.DecisionSpeciesFilter = null;
        EcosystemLogger.TerrainLogInterval = Math.Max(0, scenario.TerrainLogInterval);
        EcosystemLogger.NutritionLogInterval = Math.Max(0, scenario.NutritionLogInterval);
        EcosystemLogger.ClearTrackedSpecies();
        var logger = new EcosystemLogger(world, logDir, budget);
        systems.Add(logger);

        // The whole point of both harnesses: pin the tier instead of deriving it from a distance
        // this world is too small to produce.
        stack.Lod.SetLevelOverride(tier);

        world.PregenerateWorld();
        SpeciesToggle.Configure(scenario.DisabledSpecies, scenario.NoFactions);

        var spawner = new ScenarioSpawner(world, factory, em, rng);
        spawner.Run(scenario, stack.Nest, stack.Crystal, stack.Spore);

        // The player is still the LOD origin in the game; keep it in the world (centred, as the
        // test scene spawns it) so two runs differ only in the tier they are pinned to.
        int player = factory.SpawnPlayer(sizeChunks * ChunkSize / 2f, sizeChunks * ChunkSize / 2f, world);
        stack.Lod.SetPlayerEntity(player);
        em.SnapshotPositions();

        return new ScenarioSimulation(systems, em, world, stack, logger, budget);
    }

    /// <summary>Advance the simulation by one tick, exactly as the game's tick does.</summary>
    public void Tick()
    {
        Entities.SnapshotPositions();
        for (int i = 0; i < _systems.Count; i++)
            _systems[i].Process(Entities);
        Entities.FinalizeNewborns();
    }
}
