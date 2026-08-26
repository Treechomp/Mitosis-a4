using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
/// Long headless run of the REAL game world — noise worldgen, WorldSpawner seeding, the full
/// system stack — sampling composition and per-class budget state over time.
///
/// WHY: the questions this answers are only answerable over tens of thousands of ticks. Does the
/// predator class hold a share, or does it get filtered out? Does any one faction ratchet upward
/// once the ceiling is reached and never come back down? Watching that in the running game means
/// sitting in front of it for seventeen minutes at 20 TPS, so it never actually got watched — and
/// the composition defects that motivated per-class budgets (a 21.8:1 herbivore:predator ratio,
/// a monotonically rising Sectid share) went unnoticed for exactly that reason.
///
///   godot --headless --path godot res://Scenes/PopulationSoak.tscn -- --ticks=20000
///       --chunks=36 --max-pop=12000 --initial=2000 --sample=200 --seed=1234
///
/// Writes logs/population_soak.csv (one row per sample) and prints a verdict on the two
/// questions above. Exit code is 0 either way — this is a measurement, not a pass/fail gate;
/// composition is a judgement call and the numbers belong in front of a person.
/// </summary>
public partial class PopulationSoakRunner : Node
{
    [Export] public int Ticks = 20000;
    [Export] public int WorldSizeChunks = 36;
    [Export] public int MaxPopulation = 12000;
    [Export] public int InitialPopulation = 2000;
    [Export] public int SampleInterval = 200;
    [Export] public int WorldSeed = 1234;

    private const int ChunkSize = 32;
    private const int TileSize = 16;
    private const string LogRoot = "res://logs";

    public override void _Ready()
    {
        ParseCommandLine();
        Run();
        GetTree().Quit(0);
    }

    private void Run()
    {
        GD.Print($"[Soak] {WorldSizeChunks} chunks ({WorldSizeChunks * ChunkSize} tiles), " +
                 $"cap {MaxPopulation}, seed {InitialPopulation}, {Ticks} ticks, seed {WorldSeed}");

        // ── Build the world exactly as GameManager does, minus everything that only exists to
        // be looked at (camera, lights, meshes, world-snapshot PNGs, debug UI).
        SimRandom.SetSeed(WorldSeed);
        var rng = SimRandom.Create();

        var em = new EntityManager();
        em.OnEntityDying = id => CarrionSystem.SpawnCorpse(em, id);

        var budget = new PopulationBudget(MaxPopulation,
            PopulationBudget.DefaultHerbivoreShare,
            PopulationBudget.DefaultPredatorShare,
            PopulationBudget.DefaultFactionShare);
        em.Budget = budget;

        var world = new WorldManager(ChunkSize, WorldSizeChunks, WorldSeed, new TerrainSettings());

        var factory = new EntityFactory(em, rng);
        factory.SetPopulationCap(MaxPopulation);
        factory.SetBudget(budget);

        var systems = new List<ISystem>();
        var stack = SimulationStack.Build(systems, world, factory,
            ChunkSize, WorldSizeChunks, TileSize, MaxPopulation, budget);

        EcosystemLogger.SnapshotInterval = Math.Max(1, SampleInterval);
        EcosystemLogger.DecisionLoggingEnabled = false;
        EcosystemLogger.DecisionSpeciesFilter = null;
        EcosystemLogger.TerrainLogInterval = 0;
        EcosystemLogger.NutritionLogInterval = 0;
        EcosystemLogger.ClearTrackedSpecies();
        var logger = new EcosystemLogger(world, $"{LogRoot}/population_soak", budget);
        systems.Add(logger);

        GD.Print("[Soak] Generating world...");
        var genClock = System.Diagnostics.Stopwatch.StartNew();
        world.PregenerateWorld();
        GD.Print($"[Soak] {world.LoadedChunkCount} chunks in {genClock.Elapsed.TotalSeconds:F0}s");

        SpeciesToggle.Configure("", false);

        var spawner = new WorldSpawner(world, factory, rng);
        budget.SplitSeed(InitialPopulation,
            out int herbivoreSeed, out int predatorSeed, out int factionSeed);
        int sectidSeed = factionSeed / 2;
        int shroomerSeed = factionSeed - sectidSeed;
        spawner.SpawnCrystals(stack.Crystal, em, faelingEnabled: true, budget);
        spawner.SpawnInitialNests(stack.Nest, em, sectidSeed);
        spawner.SpawnCreatures(herbivoreSeed, predatorSeed, shroomerSeed);

        int player = factory.SpawnPlayer(WorldSizeChunks * ChunkSize / 2f,
                                          WorldSizeChunks * ChunkSize / 2f, world);
        stack.Lod.SetPlayerEntity(player);
        em.SnapshotPositions();

        GD.Print($"[Soak] Seeded {em.CreatureCount} creatures  " +
                 $"(herb {budget.CountFor(PopClass.Herbivore)}, " +
                 $"pred {budget.CountFor(PopClass.Predator)}, " +
                 $"faction {budget.CountFor(PopClass.Faction)})");

        // ── Tick, sampling as we go ──────────────────────────────────────────
        var samples = new List<Sample>(Ticks / Math.Max(1, SampleInterval) + 2);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int t = 1; t <= Ticks; t++)
        {
            em.SnapshotPositions();
            for (int i = 0; i < systems.Count; i++)
                systems[i].Process(em);
            em.FinalizeNewborns();

            if (t % SampleInterval == 0 || t == Ticks)
            {
                samples.Add(Sample.Take(t, em, budget));
                if (t % (SampleInterval * 10) == 0)
                {
                    var s = samples[^1];
                    GD.Print($"  t={t,6}  creatures={s.Total,6}  " +
                             $"herb={s.Herbivores,5}/{budget.BudgetFor(PopClass.Herbivore)}  " +
                             $"pred={s.Predators,5}/{budget.BudgetFor(PopClass.Predator)}  " +
                             $"faction={s.Factions,5}/{budget.BudgetFor(PopClass.Faction)}  " +
                             $"(shr {s.Shroomers} sec {s.Sectids} fae {s.Faelings})  " +
                             $"{clock.Elapsed.TotalSeconds:F0}s");
                }
            }
        }
        clock.Stop();

        WriteCsv(samples, budget);
        Report(samples, budget, logger);
        logger.Close();
        GD.Print($"[Soak] {Ticks} ticks in {clock.Elapsed.TotalSeconds:F0}s");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Verdict
    // ══════════════════════════════════════════════════════════════════════════

    private void Report(List<Sample> samples, PopulationBudget budget, EcosystemLogger logger)
    {
        if (samples.Count == 0) return;
        var last = samples[^1];

        GD.Print("\n[Soak] ───────── result ─────────");
        GD.Print($"  final composition: {last.Total} creatures — " +
                 $"herbivores {Pct(last.Herbivores, last.Total)}, " +
                 $"predators {Pct(last.Predators, last.Total)}, " +
                 $"factions {Pct(last.Factions, last.Total)} " +
                 $"(Shroomer {last.Shroomers}, Sectid {last.Sectids}, Faeling {last.Faelings})");

        // Predator share, against the 4.1% the global ramp produced.
        double predShareLastQuarter = 0;
        int from = samples.Count * 3 / 4;
        int n = 0;
        for (int i = from; i < samples.Count; i++)
        {
            if (samples[i].Total <= 0) continue;
            predShareLastQuarter += (double)samples[i].Predators / samples[i].Total;
            n++;
        }
        if (n > 0) predShareLastQuarter /= n;
        GD.Print($"  predator share: {predShareLastQuarter:P2} mean over the last quarter " +
                 $"(the global-ramp run measured 4.10%)");

        // The ratchet test. It keys on the FACTION CLASS filling, not the global cap: the ratchet
        // was a competition for one shared pool between throttled and unthrottled spawn paths, so
        // the moment it could bite is the moment the pool those paths draw from is full. Keying on
        // MaxPopulation misses it entirely — a healthy world levels off below the global cap (food
        // and space bind first) while its faction class is pinned to the ceiling.
        //
        // A count that can only rise is the monoculture signature. One that falls back is a
        // faction losing ground to its rivals inside a shared ceiling, which is the intent.
        int factionBudget = budget.BudgetFor(PopClass.Faction);
        int fullFrom = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].Factions >= factionBudget)
            {
                fullFrom = i;
                break;
            }
        }
        if (fullFrom < 0)
        {
            GD.Print($"  ratchet test: the faction class never filled " +
                     $"(peak {PeakFactions(samples)}/{factionBudget}) — nothing to test here, " +
                     "the ceiling was not binding");
        }
        else
        {
            GD.Print($"  ratchet test: faction class full from t={samples[fullFrom].Tick}; " +
                     "monotonic increase (falls=0) is the monoculture signature");
            ReportSeries(samples, fullFrom, "Shroomer", s => s.Shroomers);
            ReportSeries(samples, fullFrom, "Sectid", s => s.Sectids);
            ReportSeries(samples, fullFrom, "Faeling", s => s.Faelings);
        }

        GD.Print($"  budget refusals: herbivore {budget.RefusalsFor(PopClass.Herbivore)}, " +
                 $"predator {budget.RefusalsFor(PopClass.Predator)}, " +
                 $"faction {budget.RefusalsFor(PopClass.Faction)} " +
                 $"(logger total {logger.RunBudgetRefusals})");
        GD.Print("[Soak] ──────────────────────────");
    }

    private static int PeakFactions(List<Sample> samples)
    {
        int peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, s.Factions);
        return peak;
    }

    /// <summary>Rises, falls and deepest drawdown of one faction's count after the ceiling.</summary>
    private static void ReportSeries(List<Sample> samples, int from, string name,
                                      Func<Sample, int> select)
    {
        int rises = 0, falls = 0;
        int peak = select(samples[from]), deepest = 0;
        for (int i = from + 1; i < samples.Count; i++)
        {
            int d = select(samples[i]) - select(samples[i - 1]);
            if (d > 0) rises++;
            else if (d < 0) falls++;
            peak = Math.Max(peak, select(samples[i]));
            deepest = Math.Max(deepest, peak - select(samples[i]));
        }
        GD.Print($"      {name,-9} {select(samples[from]),5} -> {select(samples[^1]),5}   " +
                 $"rose {rises}x, FELL {falls}x, deepest drawdown {deepest} from a peak of {peak}");
    }

    private static string Pct(int part, int whole)
        => whole > 0 ? (part / (double)whole).ToString("P2", CultureInfo.InvariantCulture) : "n/a";

    private void WriteCsv(List<Sample> samples, PopulationBudget budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("tick,total,herbivores,herbivore_budget,predators,predator_budget," +
                      "factions,faction_budget,shroomers,sectids,faelings,corpses," +
                      "refused_herbivore,refused_predator,refused_faction");
        foreach (var s in samples)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}",
                s.Tick, s.Total,
                s.Herbivores, budget.BudgetFor(PopClass.Herbivore),
                s.Predators, budget.BudgetFor(PopClass.Predator),
                s.Factions, budget.BudgetFor(PopClass.Faction),
                s.Shroomers, s.Sectids, s.Faelings, s.Corpses,
                s.RefusedHerbivore, s.RefusedPredator, s.RefusedFaction));
        }
        string dir = ProjectSettings.GlobalizePath(LogRoot);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "population_soak.csv");
        File.WriteAllText(path, sb.ToString());
        GD.Print($"[Soak] samples -> {path}");
    }

    // ══════════════════════════════════════════════════════════════════════════

    private readonly struct Sample
    {
        public readonly int Tick, Total, Herbivores, Predators, Factions;
        public readonly int Shroomers, Sectids, Faelings, Corpses;
        public readonly long RefusedHerbivore, RefusedPredator, RefusedFaction;

        private Sample(int tick, int total, int herb, int pred, int fac,
                       int shr, int sec, int fae, int corpses,
                       long rh, long rp, long rf)
        {
            Tick = tick; Total = total; Herbivores = herb; Predators = pred; Factions = fac;
            Shroomers = shr; Sectids = sec; Faelings = fae; Corpses = corpses;
            RefusedHerbivore = rh; RefusedPredator = rp; RefusedFaction = rf;
        }

        /// <summary>
        /// Class counts come from the budget (the live counts it maintains); the per-faction split
        /// needs an entity pass, since the budget charges all three factions to one class.
        /// </summary>
        public static Sample Take(int tick, EntityManager em, PopulationBudget budget)
        {
            int shr = 0, sec = 0, fae = 0;
            foreach (int e in em.AllEntities())
            {
                if (budget.ClassOf(e) != PopClass.Faction) continue;
                switch (em.Species[e].Type)
                {
                    case SpeciesType.Shroomer: shr++; break;
                    case SpeciesType.Sectid: sec++; break;
                    case SpeciesType.Faeling: fae++; break;
                }
            }
            return new Sample(tick, em.CreatureCount,
                budget.CountFor(PopClass.Herbivore),
                budget.CountFor(PopClass.Predator),
                budget.CountFor(PopClass.Faction),
                shr, sec, fae, em.CarrionCount,
                budget.RefusalsFor(PopClass.Herbivore),
                budget.RefusalsFor(PopClass.Predator),
                budget.RefusalsFor(PopClass.Faction));
        }
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
                case "ticks" when int.TryParse(value, out int v): Ticks = Math.Max(1, v); break;
                case "chunks" when int.TryParse(value, out int v): WorldSizeChunks = Math.Max(1, v); break;
                case "max-pop" when int.TryParse(value, out int v): MaxPopulation = Math.Max(1, v); break;
                case "initial" when int.TryParse(value, out int v): InitialPopulation = Math.Max(0, v); break;
                case "sample" when int.TryParse(value, out int v): SampleInterval = Math.Max(1, v); break;
                case "seed" when int.TryParse(value, out int v): WorldSeed = v; break;
                default: GD.PushWarning($"[Soak] ignored argument '{arg}'"); break;
            }
        }
    }
}
