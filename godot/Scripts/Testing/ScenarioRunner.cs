using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using Mitosis.Systems;
using Mitosis.Utils;
using Mitosis.World;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Testing;

/// <summary>
/// Run ONE scenario headless for a bounded number of ticks and report whether it reached its
/// intended outcome.
///
/// The test scene (F6) runs a scenario forever at 20 TPS in front of a person, which answers
/// "does this look right" but cannot answer "does this finish, and by when". A siege that
/// completes at tick 900 and one that never completes look identical for the first thirty seconds
/// of watching. This is the second question: a fixed budget, a stated expectation, and an exit
/// code — so "Sectids can destroy a crystal" is a claim that can be re-checked rather than
/// re-watched.
///
///   godot --headless --path godot res://Scenes/ScenarioRun.tscn --
///       --scenario=crystal_siege --ticks=4000 --expect=Crystal
///
/// --expect names StructureKinds (Nest, Crystal, MyceliumHeart) of which at least one must be
/// destroyed within the budget; --expect-all requires the kind to be wiped out. The distinction
/// matters because factions REBUILD: a Shroomer bloom that loses its heart founds another once it
/// is large enough, so "the mechanism fired" and "the faction was erased" are different claims and
/// only the first is what a mechanism test should assert. Omit both to just observe.
/// </summary>
public partial class ScenarioRunner : Node
{
    [Export] public string Scenario = "crystal_siege";
    [Export] public int Ticks = 4000;
    /// <summary>Kinds of which at least one must be destroyed within the budget.</summary>
    [Export] public string Expect = "";

    /// <summary>Kinds that must be wiped out entirely within the budget.</summary>
    [Export] public string ExpectAll = "";

    private const int ChunkSize = 32;
    private const int TileSize = 16;
    private const string ScenarioDir = "res://TestScenarios";
    private const string LogRoot = "res://logs";

    public override void _Ready()
    {
        ParseCommandLine();
        // A throw out of _Ready leaves Godot idling with the scene loaded: no exit code, no
        // failure, just a process that never returns. A gate that hangs cannot be waited on and
        // cannot be distinguished from one that is merely slow, so an aborted run reports itself
        // as a failure and quits.
        int exitCode;
        try
        {
            exitCode = Run();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[Scenario] ABORTED: {ex}");
            exitCode = 1;
        }
        GetTree().Quit(exitCode);
    }

    private int Run()
    {
        string path = Path.Combine(ProjectSettings.GlobalizePath(ScenarioDir),
            $"{Scenario}.scenario.txt");
        if (!File.Exists(path))
        {
            GD.PrintErr($"[Scenario] no such scenario: {path}");
            return 1;
        }

        var scenario = TestScenario.Parse(File.ReadAllText(path), Scenario);
        foreach (var w in scenario.Warnings)
            GD.PushWarning($"[Scenario] {w}");

        int seed = scenario.Seed != 0 ? scenario.Seed : 12345;
        int sizeChunks = Math.Max(1, scenario.SizeChunks);
        GD.Print($"[Scenario] {scenario.Name}  seed {seed}  {sizeChunks} chunks  {Ticks} ticks");

        // Same construction as the game, minus everything that exists only to be looked at.
        SimRandom.SetSeed(seed);
        var rng = SimRandom.Create();

        var em = new EntityManager();
        em.OnEntityDying = id => CarrionSystem.SpawnCorpse(em, id);

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

        EcosystemLogger.SnapshotInterval = Math.Max(1, scenario.SnapshotInterval);
        EcosystemLogger.DecisionLoggingEnabled = scenario.LogDecisions;
        EcosystemLogger.DecisionSpeciesFilter = null;
        EcosystemLogger.TerrainLogInterval = Math.Max(0, scenario.TerrainLogInterval);
        EcosystemLogger.NutritionLogInterval = Math.Max(0, scenario.NutritionLogInterval);
        EcosystemLogger.ClearTrackedSpecies();
        var logger = new EcosystemLogger(world, $"{LogRoot}/scenario_{scenario.Name}", budget);
        systems.Add(logger);

        // Scenarios pin the LOD tier so a small world can exercise the coarse tiers; honour it.
        if (scenario.LodOverride.HasValue)
            stack.Lod.SetLevelOverride(scenario.LodOverride.Value);

        world.PregenerateWorld();
        SpeciesToggle.Configure(scenario.DisabledSpecies, scenario.NoFactions);

        var spawner = new ScenarioSpawner(world, factory, em, rng);
        spawner.Run(scenario, stack.Nest, stack.Crystal, stack.Spore);

        int player = factory.SpawnPlayer(sizeChunks * ChunkSize / 2f, sizeChunks * ChunkSize / 2f, world);
        stack.Lod.SetPlayerEntity(player);
        em.SnapshotPositions();

        var initial = CountByKind(em);
        PrintCounts("start ", initial);

        // Tick, recording when each kind first loses a member and when each is wiped out.
        var firstLoss = new Dictionary<StructureKind, int>();
        var allGone = new Dictionary<StructureKind, int>();
        for (int t = 1; t <= Ticks; t++)
        {
            em.SnapshotPositions();
            for (int i = 0; i < systems.Count; i++)
                systems[i].Process(em);
            em.FinalizeNewborns();

            var now = CountByKind(em);
            foreach (var (kind, started) in initial)
            {
                now.TryGetValue(kind, out int left);
                if (left < started && !firstLoss.ContainsKey(kind)) firstLoss[kind] = t;
                if (left == 0 && !allGone.ContainsKey(kind)) allGone[kind] = t;
            }
        }

        var final = CountByKind(em);
        GD.Print($"[Scenario] after {Ticks} ticks:");
        PrintCounts("end   ", final);
        foreach (var (kind, started) in initial)
        {
            final.TryGetValue(kind, out int left);
            string first = firstLoss.TryGetValue(kind, out int f) ? $"t={f}" : "never";
            string gone = allGone.TryGetValue(kind, out int g) ? $"t={g}" : "never";
            GD.Print($"    {kind,-14} {started} -> {left}   first loss {first}, all destroyed {gone}");
        }
        GD.Print($"    structure hits {logger.RunStructureDamageEvents}, " +
                 $"destroyed {logger.RunStructuresDestroyed}");

        int failures = CheckExpectations(initial, firstLoss, allGone);
        logger.Close();
        GD.Print($"[Scenario] logs -> {ProjectSettings.GlobalizePath(LogRoot)}/scenario_{scenario.Name}");
        return failures == 0 ? 0 : 1;
    }

    private int CheckExpectations(Dictionary<StructureKind, int> initial,
                                   Dictionary<StructureKind, int> firstLoss,
                                   Dictionary<StructureKind, int> allGone)
        => Check(Expect, "at least one", initial, firstLoss)
         + Check(ExpectAll, "all", initial, allGone);

    private int Check(string list, string label, Dictionary<StructureKind, int> initial,
                       Dictionary<StructureKind, int> reached)
    {
        int failures = 0;
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = raw.Trim();
            if (!Enum.TryParse<StructureKind>(name, ignoreCase: true, out var kind))
            {
                GD.PrintErr($"[Scenario] FAIL — '{name}' is not a StructureKind");
                failures++;
                continue;
            }
            if (!initial.ContainsKey(kind) || initial[kind] == 0)
            {
                GD.PrintErr($"[Scenario] FAIL — expected {kind} destroyed, but none existed to destroy");
                failures++;
            }
            else if (reached.TryGetValue(kind, out int tick))
            {
                GD.Print($"[Scenario] PASS — {label} {kind} destroyed by t={tick} " +
                         $"({(float)tick / Ticks:P0} of the budget)");
            }
            else
            {
                GD.PrintErr($"[Scenario] FAIL — {kind} ({label}) survived {Ticks} ticks");
                failures++;
            }
        }
        return failures;
    }

    private static Dictionary<StructureKind, int> CountByKind(EntityManager em)
    {
        var counts = new Dictionary<StructureKind, int>();
        foreach (int e in em.Query(ComponentFlags.Structure))
        {
            if (em.Structures[e].IsDestroyed) continue;
            var kind = em.Structures[e].Kind;
            counts.TryGetValue(kind, out int c);
            counts[kind] = c + 1;
        }
        return counts;
    }

    private static void PrintCounts(string label, Dictionary<StructureKind, int> counts)
    {
        if (counts.Count == 0) { GD.Print($"    {label} no structures"); return; }
        var parts = new List<string>();
        foreach (var (kind, n) in counts) parts.Add($"{kind}={n}");
        parts.Sort(StringComparer.Ordinal);
        GD.Print($"    {label} {string.Join("  ", parts)}");
    }

    private void ParseCommandLine()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.Split('=', 2);
            string key = kv[0].TrimStart('-').ToLowerInvariant();
            string value = kv.Length > 1 ? kv[1] : "";
            switch (key)
            {
                case "scenario": Scenario = value; break;
                case "ticks" when int.TryParse(value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int t): Ticks = Math.Max(1, t); break;
                case "expect": Expect = value; break;
            case "expect-all": ExpectAll = value; break;
                default: GD.PushWarning($"[Scenario] ignored argument '{arg}'"); break;
            }
        }
    }
}
