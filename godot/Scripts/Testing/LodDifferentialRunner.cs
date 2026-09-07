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
/// Differential LOD test. Runs one scenario TWICE from the same seed — once with every entity
/// forced to <see cref="LODLevel.Full"/>, once forced to <see cref="LODLevel.Minimal"/> — and
/// compares whole-run outcome metrics.
///
/// WHY: LOD is allowed to coarsen the TIMING of a decision. It is not allowed to change how much
/// of anything happens. A system that advances a per-tick quantity behind a `DueThisTick` gate
/// must multiply that quantity by <c>SimulationLOD.EffectiveInterval</c>, or the quantity simply
/// runs slower the further the player stands away — Shroomer growth (10-20x fast at low tiers),
/// attack cooldown overshoot and the terraform rate have each broken this rule in turn. The rule
/// was enforced only by prose in docs/implementation/lod-and-performance.md; this is the executable version.
///
/// Full vs Minimal is a 20x difference in decision cadence, so a missing multiplier shows up as
/// an order-of-magnitude gap in an outcome count. The tolerance is deliberately loose (15% by
/// default): the two runs are NOT the same trajectory — a thinned cadence consumes the random
/// streams differently and creatures end up in different places — so exact equality is neither
/// expected nor the target. What is being hunted is a rate that is systematically wrong.
///
/// THE NOISE FLOOR IS NOT OPTIONAL. These scenarios are small and chaotic: run predator_prey at
/// Full tier on seed 42 and again on seed 43 and the kill count goes 17 -> 40. A bare 15% test
/// would therefore fail almost every ecological metric no matter how correct the LOD code is,
/// and a report that always says FAIL says nothing. So each scenario also runs ControlRuns extra
/// Full-tier simulations on shifted seeds, and the largest Full-vs-Full divergence a metric shows
/// there is its noise floor. A metric is only called a failure when LOD moved it further than the
/// dice did — i.e. divergence > tolerance AND divergence > noise floor. The raw
/// tolerance verdict is still written to the CSV (exceeds_tolerance), so nothing is hidden.
///
/// The floor is a sample, not a proof: a metric can pass because this scenario is simply too
/// noisy to measure, which is a statement about the scenario, not a clean bill of health. Metrics
/// with a high floor are the ones to design a quieter scenario for. Mechanical rates — terraform
/// nudges, mean growth scale — have floors of a few percent and are where this test has teeth.
///
/// Run it headless:
///   godot --headless --path godot res://Scenes/LodDifferential.tscn -- --ticks=3000
///   ... --scenario=predator_prey,shroomer_bloom   (default: every scenario in TestScenarios/)
///   ... --tolerance=0.15 --min-count=5 --control-runs=2   (0 = strict tolerance, no floor)
///   ... --record            (rewrite docs/lod-differential-expected.csv, gate nothing)
///
/// THE EXIT CODE IS THE DIFFERENCE FROM THE RECORDED VERDICTS, not the failure count. Forty
/// metrics fail for accepted, written-down reasons, so a runner that exited on the failure count
/// exited 1 on every run and had never once exited 0 — and a gate that is always red carries no
/// signal, because a new regression looks exactly like the forty old ones. The accepted verdicts
/// live in docs/lod-differential-expected.csv; this run exits non-zero only when a metric that
/// was passing has started failing (REGRESSION) or the metric set itself moved (DRIFT). See
/// <see cref="LodExpectedVerdicts"/>.
/// </summary>
public partial class LodDifferentialRunner : Node
{
    // Mirrors GameManager's exports: the harness must build the same world the game would.
    private const int ChunkSize = 32;
    private const int TileSize = 16;

    /// <summary>Ticks each run is stepped for. Both runs get exactly this many.</summary>
    [Export] public int Ticks = 2000;

    /// <summary>Relative divergence at which a metric is called a failure.</summary>
    [Export] public float Tolerance = 0.15f;

    /// <summary>
    /// Metrics where both runs are below this magnitude are reported but cannot fail: 1 death
    /// versus 2 is a 50% divergence and means nothing. Small-sample noise is not a rate bug.
    /// </summary>
    [Export] public int MinCount = 5;

    /// <summary>Comma-separated scenario names; empty runs every file in TestScenarios/.</summary>
    [Export] public string Scenarios = "";

    /// <summary>
    /// Extra Full-tier runs on shifted seeds, used to measure how much each metric moves for
    /// reasons that have nothing to do with LOD. 0 disables the floor and tests the tolerance
    /// alone — strict, and on these scenarios, mostly noise.
    /// </summary>
    [Export] public int ControlRuns = 2;

    /// <summary>
    /// Rewrite the expected-verdict file from this run and gate nothing.
    ///
    /// Recording is a DELIBERATE ACT and never a side effect of a normal run: a run that quietly
    /// re-recorded its own failures would accept every regression it found, which is the one
    /// thing this whole mechanism exists to prevent.
    /// </summary>
    [Export] public bool Record;

    private const string ScenarioDir = "res://TestScenarios";
    private const string LogRoot = "res://logs";

    public override void _Ready()
    {
        ParseCommandLine();
        GetTree().Quit(RunAll());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Driver
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run every scenario, then either RECORD the verdicts or GATE on how they differ from the
    /// recorded ones. Returns the process exit code.
    /// </summary>
    private int RunAll()
    {
        var files = CollectScenarioFiles();
        if (files.Count == 0)
        {
            GD.PrintErr($"[LodDiff] No scenario files matched (dir={ScenarioDir}, filter='{Scenarios}')");
            return 1;
        }

        GD.Print($"[LodDiff] {files.Count} scenario(s), {Ticks} ticks per run, " +
                 $"tolerance {Tolerance:P0}, min-count {MinCount}, " +
                 $"{ControlRuns} control run(s) per scenario" +
                 (Record ? "  [RECORD MODE — gating nothing]" : ""));

        var summaryRows = new List<string>();
        var verdicts = new List<LodExpectedVerdicts.Row>();
        var scenariosRun = new HashSet<string>(StringComparer.Ordinal);
        int failedScenarios = 0;

        foreach (var file in files)
        {
            var scenario = TestScenario.Parse(File.ReadAllText(file),
                Path.GetFileNameWithoutExtension(file).Replace(".scenario", ""));
            foreach (var w in scenario.Warnings)
                GD.PushWarning($"[LodDiff] {scenario.Name}: {w}");

            // A differential needs both runs to start from the same world and the same dice.
            // seed = 0 means "random per run" in the scenario format, which cannot be compared.
            int seed = scenario.Seed != 0 ? scenario.Seed : 12345;

            GD.Print($"\n[LodDiff] === {scenario.Name} (seed {seed}) ===");
            var full = RunOnce(scenario, seed, LODLevel.Full, "full");
            var minimal = RunOnce(scenario, seed, LODLevel.Minimal, "minimal");

            // Same tier, different dice: how far this scenario wanders on its own.
            var controls = new List<RunMetrics>();
            for (int k = 1; k <= ControlRuns; k++)
                controls.Add(RunOnce(scenario, seed + k, LODLevel.Full, $"control{k}"));

            var comparisons = Compare(full, minimal, controls);
            bool pass = Report(scenario.Name, seed, comparisons, summaryRows, verdicts);
            scenariosRun.Add(scenario.Name);
            if (!pass) failedScenarios++;
        }

        WriteSummary(summaryRows);
        GD.Print($"\n[LodDiff] {files.Count - failedScenarios}/{files.Count} scenarios clean" +
                 $" at {Tolerance:P0} tolerance ({verdicts.Count} metrics).");

        // A filtered run only saw some scenarios, so it cannot tell "this metric is gone" from
        // "this metric was not asked for"; an unfiltered run can, and should.
        bool filtered = Scenarios.Trim().Length > 0;
        return Record
            ? RecordVerdicts(verdicts)
            : GateAgainstExpected(verdicts, filtered ? scenariosRun : null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The gate: recorded verdicts vs this run's
    // ══════════════════════════════════════════════════════════════════════════

    private static int RecordVerdicts(List<LodExpectedVerdicts.Row> verdicts)
    {
        string path = LodExpectedVerdicts.DefaultPath();
        LodExpectedVerdicts.Save(path, verdicts);
        int failing = 0;
        foreach (var v in verdicts)
            if (LodExpectedVerdicts.IsFail(v.Verdict)) failing++;
        GD.Print($"\n[LodDiff] RECORDED {verdicts.Count} verdicts ({failing} FAIL) -> {path}");
        GD.Print("[LodDiff] Commit it. An expected file that is not committed means nothing, and");
        GD.Print("[LodDiff] every FAIL recorded here needs its reason in lod-differential-baseline.md.");
        return 0;
    }

    /// <summary>
    /// Compare this run's verdicts against the recorded contract and decide the exit code.
    ///
    /// Non-zero on REGRESSION (a metric that was passing has started failing) and on DRIFT (the
    /// metric set moved, so the contract no longer describes this test). ZERO on IMPROVEMENT —
    /// good news must not break the build — but loudly, because an exception that has been fixed
    /// and left in the file will hide the next regression in that same metric.
    /// </summary>
    private static int GateAgainstExpected(List<LodExpectedVerdicts.Row> verdicts,
                                            IReadOnlySet<string>? scenariosRun)
    {
        string path = LodExpectedVerdicts.DefaultPath();
        if (!LodExpectedVerdicts.TryLoad(path, out var expected, out string error))
        {
            GD.PrintErr($"\n[LodDiff] {error}");
            GD.PrintErr("[LodDiff] Nothing to compare against, so this run proves nothing. Record it:");
            GD.PrintErr("[LodDiff]   godot --headless --path godot " +
                        "res://Scenes/LodDifferential.tscn -- --ticks=3000 --record");
            GD.PrintErr("[LodDiff] then commit the file, with each FAIL explained in " +
                        "docs/archive/lod-differential-baseline-2026-08.md.");
            return 1;
        }

        var differences = expected.CompareTo(verdicts, scenariosRun);
        int regressions = 0, improvements = 0, drift = 0;
        foreach (var d in differences)
        {
            switch (d.Kind)
            {
                case VerdictChange.Regression: regressions++; break;
                case VerdictChange.Improvement: improvements++; break;
                default: drift++; break;
            }
        }
        int known = verdicts.Count - regressions - improvements;

        if (regressions > 0)
        {
            GD.PrintErr($"\n[LodDiff] {regressions} REGRESSION(S) — these metrics were recorded as " +
                        "passing and are now failing:");
            foreach (var d in differences)
                if (d.Kind == VerdictChange.Regression)
                    GD.PrintErr($"      {d.Scenario,-18} {d.Metric,-32} {d.Expected} -> {d.Actual}");
            GD.PrintErr("[LodDiff] A rate that LOD now moves further than the dice do. Fix it — do " +
                        "NOT re-record.");
        }

        if (improvements > 0)
        {
            GD.Print($"\n[LodDiff] {improvements} IMPROVEMENT(S) — recorded as failing, now passing:");
            foreach (var d in differences)
                if (d.Kind == VerdictChange.Improvement)
                    GD.Print($"      {d.Scenario,-18} {d.Metric,-32} {d.Expected} -> {d.Actual}");
            GD.Print("[LodDiff] RE-RECORD (--record) and delete the exception's entry in " +
                     "docs/archive/lod-differential-baseline-2026-08.md.");
            GD.Print("[LodDiff] A fixed exception left in the file will hide the NEXT regression " +
                     "in that metric.");
        }

        if (drift > 0)
        {
            GD.PrintErr($"\n[LodDiff] {drift} DRIFT — the metric set no longer matches the contract:");
            foreach (var d in differences)
            {
                if (d.Kind == VerdictChange.DriftAdded)
                    GD.PrintErr($"      ADDED    {d.Scenario,-18} {d.Metric,-32} (now {d.Actual})");
                else if (d.Kind == VerdictChange.DriftRemoved)
                    GD.PrintErr($"      REMOVED  {d.Scenario,-18} {d.Metric,-32} (was {d.Expected})");
            }
            GD.PrintErr("[LodDiff] The contract describes a different test than the one that ran. " +
                        "Re-record deliberately.");
        }

        GD.Print($"\n[LodDiff] known {known}  regression {regressions}  " +
                 $"improvement {improvements}  drift {drift}");
        return regressions > 0 || drift > 0 ? 1 : 0;
    }

    private List<string> CollectScenarioFiles()
    {
        string dir = ProjectSettings.GlobalizePath(ScenarioDir);
        var wanted = new List<string>();
        foreach (var raw in Scenarios.Split(',', StringSplitOptions.RemoveEmptyEntries))
            wanted.Add(raw.Trim().ToLowerInvariant());

        var files = new List<string>(Directory.GetFiles(dir, "*.scenario.txt"));
        files.Sort(StringComparer.Ordinal);
        if (wanted.Count == 0) return files;

        var kept = new List<string>();
        foreach (var f in files)
        {
            string name = Path.GetFileNameWithoutExtension(f).Replace(".scenario", "").ToLowerInvariant();
            if (wanted.Contains(name)) kept.Add(f);
        }
        foreach (var w in wanted)
        {
            bool found = false;
            foreach (var f in kept)
                if (Path.GetFileNameWithoutExtension(f).Replace(".scenario", "").ToLowerInvariant() == w)
                    found = true;
            if (!found) GD.PushWarning($"[LodDiff] no scenario named '{w}' in {ScenarioDir}");
        }
        return kept;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // One run
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a complete simulation from the scenario and step it for <see cref="Ticks"/> ticks
    /// with every entity pinned to <paramref name="tier"/>, then read off the end state.
    ///
    /// This is GameManager's construction sequence minus the parts that only exist to be looked
    /// at (camera, lights, meshes, debug UI, world-snapshot PNGs). The SYSTEMS come from
    /// SimulationStack, the same builder the game uses, so the harness cannot drift onto a
    /// different stack than the one that ships.
    /// </summary>
    private RunMetrics RunOnce(TestScenario scenario, int seed, LODLevel tier, string label)
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

        // Logging: the run totals the comparison reads are maintained regardless of these, but
        // the per-run CSVs are what you actually open when a metric diverges, so each run gets
        // its own directory. Decision logging stays off — it is per-decision and enormous, and
        // the two runs make a different number of decisions by construction.
        EcosystemLogger.SnapshotInterval = Math.Max(1, scenario.SnapshotInterval);
        EcosystemLogger.DecisionLoggingEnabled = false;
        EcosystemLogger.DecisionSpeciesFilter = null;
        EcosystemLogger.TerrainLogInterval = Math.Max(0, scenario.TerrainLogInterval);
        EcosystemLogger.NutritionLogInterval = Math.Max(0, scenario.NutritionLogInterval);
        EcosystemLogger.ClearTrackedSpecies();
        var logger = new EcosystemLogger(world, $"{LogRoot}/lod_differential/{scenario.Name}_{label}",
            budget);
        systems.Add(logger);

        // The whole point of the harness: pin the tier instead of deriving it from a distance
        // this world is too small to produce.
        stack.Lod.SetLevelOverride(tier);

        world.PregenerateWorld();
        SpeciesToggle.Configure(scenario.DisabledSpecies, scenario.NoFactions);

        var spawner = new ScenarioSpawner(world, factory, em, rng);
        spawner.Run(scenario, stack.Nest, stack.Crystal, stack.Spore);

        // The player is still the LOD origin in the game; keep it in the world (centred, as the
        // test scene spawns it) so the two runs differ only in the tier they are pinned to.
        int player = factory.SpawnPlayer(sizeChunks * ChunkSize / 2f, sizeChunks * ChunkSize / 2f, world);
        stack.Lod.SetPlayerEntity(player);
        em.SnapshotPositions();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int t = 0; t < Ticks; t++)
        {
            em.SnapshotPositions();
            for (int i = 0; i < systems.Count; i++)
                systems[i].Process(em);
            em.FinalizeNewborns();
        }
        clock.Stop();

        var metrics = Collect(em, logger, tier);
        metrics.WallMs = clock.Elapsed.TotalMilliseconds;
        logger.Close();

        GD.Print($"  [{label}] tier={tier} pop={metrics.TotalPopulation} kills={metrics.TotalKills} " +
                 $"terraform={metrics.TerraformNudges} ({metrics.WallMs / 1000.0:F1}s)");
        return metrics;
    }

    /// <summary>
    /// End-state metrics. Population follows the population-CSV definition: living creatures
    /// only, with spores / nests / crystals counted apart (a spore carries Species(Shroomer) for
    /// type checks and would otherwise inflate the Shroomer column).
    /// </summary>
    private static RunMetrics Collect(EntityManager em, EcosystemLogger logger, LODLevel tier)
    {
        var m = new RunMetrics { Tier = tier };
        int shroomerId = SpeciesRegistry.GetId("Shroomer");
        double growthSum = 0;
        int growthCount = 0;

        foreach (int entity in em.AllEntities())
        {
            if (em.HasComponents(entity, ComponentFlags.Spore)) { m.Spores++; continue; }
            if (em.HasComponents(entity, ComponentFlags.Nest)) { m.Nests++; continue; }
            if (em.HasComponents(entity, ComponentFlags.Crystal)) { m.Crystals++; continue; }
            if (em.HasComponents(entity, ComponentFlags.Carrion)) { m.Corpses++; continue; }
            if (!em.HasComponents(entity, ComponentFlags.Species)) continue;

            int sid = em.Species[entity].SpeciesId;
            m.Population.TryGetValue(sid, out int c);
            m.Population[sid] = c + 1;

            if (sid == shroomerId && em.HasComponents(entity, ComponentFlags.Growth))
            {
                growthSum += em.Growths[entity].CurrentScale;
                growthCount++;
            }
        }

        m.MeanShroomerScale = growthCount > 0 ? (float)(growthSum / growthCount) : 0f;
        m.ShroomerSampled = growthCount;
        MeasureSpacing(em, m);

        Copy(logger.RunBirths, m.Births);
        Copy(logger.RunDeathsStarve, m.DeathsStarve);
        Copy(logger.RunDeathsAge, m.DeathsAge);
        Copy(logger.RunDeathsPred, m.DeathsPred);
        Copy(logger.RunDeathsEnv, m.DeathsEnv);
        Copy(logger.RunKillsMade, m.KillsMade);
        m.TotalKills = logger.RunTotalKills;
        m.TerraformNudges = logger.RunTerraformNudges;
        m.TerraformShifts = logger.RunTerraformShifts;
        m.NutritionConsumed = logger.RunNutritionConsumed;
        m.NutritionRegen = logger.RunNutritionRegen;
        m.NutritionEnriched = logger.RunNutritionEnriched;
        return m;
    }

    /// <summary>
    /// Mean distance from each creature to the nearest other creature of its own species — the
    /// outcome SeparationSystem and CollisionSystem actually produce, and the only way to tell
    /// whether declaring them state-like (no EffectiveInterval multiplier) holds up. If spacing
    /// collapses at Minimal tier, the separation impulse is under-applied there after all.
    ///
    /// Brute force on purpose: the spatial hash is only refreshed for entities that were due, so
    /// at Minimal tier it can be up to a full interval stale — several tiles of drift, which is
    /// the same order as the quantity being measured. This runs once at the end of a run, over a
    /// few hundred creatures, so an exact O(n^2) pass costs nothing worth optimising.
    /// </summary>
    private static void MeasureSpacing(EntityManager em, RunMetrics m)
    {
        var bySpecies = new Dictionary<int, List<(float x, float y)>>();
        foreach (int entity in em.AllEntities())
        {
            // Same exclusions as the population count: structures and spores are not creatures
            // being spaced, and a spore carries Species(Shroomer) for type checks.
            if (em.HasComponents(entity, ComponentFlags.Spore)) continue;
            if (em.HasComponents(entity, ComponentFlags.Nest)) continue;
            if (em.HasComponents(entity, ComponentFlags.Crystal)) continue;
            if (em.HasComponents(entity, ComponentFlags.Carrion)) continue;
            if (!em.HasComponents(entity, ComponentFlags.Species | ComponentFlags.Position)) continue;

            int sid = em.Species[entity].SpeciesId;
            if (!bySpecies.TryGetValue(sid, out var list))
                bySpecies[sid] = list = new List<(float, float)>();
            ref var p = ref em.Positions[entity];
            list.Add((p.X, p.Y));
        }

        foreach (var (sid, list) in bySpecies)
        {
            if (list.Count < 2) continue;   // nothing to be spaced from
            double sum = 0;
            for (int i = 0; i < list.Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < list.Count; j++)
                {
                    if (i == j) continue;
                    float dx = list[i].x - list[j].x;
                    float dy = list[i].y - list[j].y;
                    float d = dx * dx + dy * dy;
                    if (d < best) best = d;
                }
                sum += MathF.Sqrt(best);
            }
            m.MeanNearestNeighbour[sid] = (float)(sum / list.Count);
            m.SpacingSample[sid] = list.Count;
        }
    }

    private static void Copy(IReadOnlyDictionary<int, int> from, Dictionary<int, int> to)
    {
        foreach (var (k, v) in from) to[k] = v;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Comparison
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flatten a run into a flat metric table. Species-scoped counts become
    /// "births.Deer"-style keys so the Full, Minimal and control runs can be compared by name
    /// even when they end up with different species alive.
    /// </summary>
    private static Dictionary<string, double> Flatten(RunMetrics m)
    {
        var t = new Dictionary<string, double>();
        foreach (var (sid, n) in m.Population) t[$"pop.{SpeciesName(sid)}"] = n;
        t["pop.total"] = m.TotalPopulation;
        t["pop.spores"] = m.Spores;
        t["pop.nests"] = m.Nests;
        t["pop.crystals"] = m.Crystals;
        t["pop.corpses"] = m.Corpses;

        foreach (var (sid, n) in m.Births) t[$"births.{SpeciesName(sid)}"] = n;
        foreach (var (sid, n) in m.DeathsStarve) t[$"deaths_starve.{SpeciesName(sid)}"] = n;
        foreach (var (sid, n) in m.DeathsAge) t[$"deaths_age.{SpeciesName(sid)}"] = n;
        foreach (var (sid, n) in m.DeathsPred) t[$"deaths_predation.{SpeciesName(sid)}"] = n;
        foreach (var (sid, n) in m.DeathsEnv) t[$"deaths_environment.{SpeciesName(sid)}"] = n;
        foreach (var (sid, n) in m.KillsMade) t[$"kills.{SpeciesName(sid)}"] = n;

        t["kills.total"] = m.TotalKills;
        t["terraform.nudges"] = m.TerraformNudges;
        t["terraform.shifts"] = m.TerraformShifts;
        t["nutrition.consumed"] = m.NutritionConsumed;
        t["nutrition.regenerated"] = m.NutritionRegen;
        t["nutrition.corpse_enriched"] = m.NutritionEnriched;
        t["growth.shroomer_mean_scale"] = m.MeanShroomerScale;
        foreach (var (sid, d) in m.MeanNearestNeighbour) t[$"spacing.{SpeciesName(sid)}"] = d;
        return t;
    }

    /// <summary>
    /// Compare the Full and Minimal runs metric by metric, and score each against the noise floor
    /// the control runs measured for that same metric.
    /// </summary>
    private List<MetricDiff> Compare(RunMetrics full, RunMetrics minimal, List<RunMetrics> controls)
    {
        var a = Flatten(full);
        var b = Flatten(minimal);
        var controlTables = new List<Dictionary<string, double>>();
        foreach (var c in controls) controlTables.Add(Flatten(c));

        var names = new SortedSet<string>(a.Keys);
        names.UnionWith(b.Keys);
        foreach (var c in controlTables) names.UnionWith(c.Keys);

        // Spacing needs a population to be a statistic at all: the mean nearest-neighbour distance
        // of two crocodiles is just how far apart those two happen to be. Require a real sample in
        // every run being compared, rather than reporting a two-animal number and then having to
        // explain it away. A species that clears the bar in one run and not another is dropped for
        // the same reason — the comparison would be against a phantom 0.
        var everyRun = new List<RunMetrics>(controls) { full, minimal };
        foreach (var name in new List<string>(names))
        {
            if (!name.StartsWith("spacing.", StringComparison.Ordinal)) continue;
            foreach (var run in everyRun)
            {
                if (SpacingSampleFor(run, name) >= MinCount) continue;
                names.Remove(name);
                break;
            }
        }

        var diffs = new List<MetricDiff>(names.Count);
        foreach (var name in names)
        {
            double va = Value(a, name);
            double vb = Value(b, name);

            // Noise floor: the WORST this metric moved between two same-tier runs. Taking the max
            // rather than the mean is the conservative choice for a test that is trying not to
            // cry wolf — one control pair is a small sample of a wide distribution.
            double floor = 0.0;
            foreach (var c in controlTables)
                floor = Math.Max(floor, Divergence(va, Value(c, name)));

            diffs.Add(Score(name, va, vb, floor, controlTables.Count > 0));
        }
        return diffs;
    }

    /// <summary>How many individuals a run's "spacing.&lt;Species&gt;" mean was taken over (0 if none).</summary>
    private static int SpacingSampleFor(RunMetrics run, string metric)
    {
        string species = metric["spacing.".Length..];
        foreach (var (sid, count) in run.SpacingSample)
            if (SpeciesName(sid) == species) return count;
        return 0;
    }

    private static double Value(Dictionary<string, double> table, string name)
        => table.TryGetValue(name, out double v) ? v : 0.0;

    /// <summary>
    /// Relative divergence: |a-b| / max(|a|,|b|). Symmetric — neither run is "the truth", since
    /// Minimal tier is as real to a player as Full — bounded at 1, and defined when either side
    /// is 0.
    /// </summary>
    private static double Divergence(double a, double b)
    {
        double magnitude = Math.Max(Math.Abs(a), Math.Abs(b));
        return magnitude > 0 ? Math.Abs(a - b) / magnitude : 0.0;
    }

    /// <summary>
    /// Score one metric. The raw ratio b/a is carried alongside the divergence because it is what
    /// makes a rate bug legible: the terraform defect reads as ratio 0.25, i.e. four times fewer
    /// nudges — a divergence of "75%" understates what that means.
    /// </summary>
    private MetricDiff Score(string name, double a, double b, double noiseFloor, bool haveFloor)
    {
        double rel = Divergence(a, b);
        bool belowFloor = IsCounted(name) && Math.Max(Math.Abs(a), Math.Abs(b)) < MinCount;
        bool exceedsTolerance = !belowFloor && rel > Tolerance;

        return new MetricDiff
        {
            Name = name,
            Full = a,
            Other = b,
            Relative = rel,
            Ratio = Math.Abs(a) > 1e-9 ? b / a : double.NaN,
            NoiseFloor = noiseFloor,
            HaveFloor = haveFloor,
            // Below MinCount the metric is reported but cannot fail: at single-digit counts the
            // difference between two runs is which way the dice fell, not a rate.
            BelowMinCount = belowFloor,
            ExceedsTolerance = exceedsTolerance,
            // A failure has to beat both bars — the tolerance, and the distance this metric
            // travels on its own between two identically-tiered runs.
            Failed = exceedsTolerance && (!haveFloor || rel > noiseFloor),
        };
    }

    /// <summary>
    /// True for metrics that count events or individuals, where a handful either side is noise
    /// rather than signal. The MinCount floor must NOT be applied to the continuous ones — a mean
    /// growth scale of 1.2 or a mean spacing of 3 tiles is a perfectly measurable quantity that a
    /// count-shaped floor would silently excuse.
    /// </summary>
    private static bool IsCounted(string metric)
        => !metric.StartsWith("nutrition.", StringComparison.Ordinal)
        && !metric.StartsWith("growth.", StringComparison.Ordinal)
        && !metric.StartsWith("spacing.", StringComparison.Ordinal);

    private static string SpeciesName(int sid) => SpeciesRegistry.GetById(sid)?.Name ?? sid.ToString();

    // ══════════════════════════════════════════════════════════════════════════
    // Reporting
    // ══════════════════════════════════════════════════════════════════════════

    private bool Report(string scenarioName, int seed, List<MetricDiff> diffs,
                         List<string> summaryRows, List<LodExpectedVerdicts.Row> verdicts)
    {
        var csv = new StringBuilder();
        csv.AppendLine($"# scenario={scenarioName} seed={seed} ticks={Ticks} " +
                       $"tolerance={Tolerance.ToString("R", CultureInfo.InvariantCulture)} " +
                       $"min_count={MinCount} control_runs={ControlRuns}");
        csv.AppendLine("metric,full,minimal,abs_diff,rel_divergence,ratio,noise_floor," +
                       "exceeds_tolerance,verdict");

        int failures = 0;
        int overTolerance = 0;
        var worst = new List<MetricDiff>();
        foreach (var d in diffs)
        {
            string verdict =
                d.BelowMinCount ? "below_min_count"
                : d.Failed ? "FAIL"
                : d.ExceedsTolerance ? "within_noise"
                : "ok";
            if (d.Failed) { failures++; worst.Add(d); }
            if (d.ExceedsTolerance) overTolerance++;

            csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:0.###},{2:0.###},{3:0.###},{4:0.####},{5},{6:0.####},{7},{8}",
                d.Name, d.Full, d.Other, d.Other - d.Full, d.Relative,
                double.IsNaN(d.Ratio) ? "" : d.Ratio.ToString("0.###", CultureInfo.InvariantCulture),
                d.NoiseFloor, d.ExceedsTolerance ? 1 : 0, verdict));

            summaryRows.Add(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2:0.###},{3:0.###},{4:0.####},{5:0.####},{6}",
                scenarioName, d.Name, d.Full, d.Other, d.Relative, d.NoiseFloor, verdict));
            verdicts.Add(new LodExpectedVerdicts.Row(scenarioName, d.Name, verdict));
        }

        string dir = ProjectSettings.GlobalizePath(LogRoot);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"lod_differential_{scenarioName}.csv");
        File.WriteAllText(path, csv.ToString());

        worst.Sort((x, y) => y.Relative.CompareTo(x.Relative));
        if (failures == 0)
        {
            string qualifier = ControlRuns > 0
                ? "but none beyond this scenario's own run-to-run noise"
                : "(no control runs — nothing measured the noise floor)";
            GD.Print($"  PASS — {diffs.Count} metrics; {overTolerance} over {Tolerance:P0} " +
                     $"{qualifier} -> {path}");
        }
        else
        {
            GD.Print($"  FAIL — {failures}/{diffs.Count} metrics beyond {Tolerance:P0} " +
                     $"AND beyond the noise floor -> {path}");
            int shown = Math.Min(10, worst.Count);
            for (int i = 0; i < shown; i++)
            {
                var d = worst[i];
                GD.Print(string.Format(CultureInfo.InvariantCulture,
                    "      {0,-32} full={1,9:0.##} min={2,9:0.##}  div={3,5:P0}  " +
                    "ratio={4,6:0.###}  noise={5:P0}",
                    d.Name, d.Full, d.Other, d.Relative, d.Ratio, d.NoiseFloor));
            }
            if (worst.Count > shown) GD.Print($"      ... and {worst.Count - shown} more");
        }
        return failures == 0;
    }

    private void WriteSummary(List<string> rows)
    {
        string dir = ProjectSettings.GlobalizePath(LogRoot);
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("scenario,metric,full,minimal,rel_divergence,noise_floor,verdict");
        foreach (var r in rows) sb.AppendLine(r);
        string path = Path.Combine(dir, "lod_differential_summary.csv");
        File.WriteAllText(path, sb.ToString());
        GD.Print($"[LodDiff] summary -> {path}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Command line
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Arguments after the '--' separator, so they don't collide with Godot's own.</summary>
    private void ParseCommandLine()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.Split('=', 2);
            string key = kv[0].TrimStart('-').ToLowerInvariant();
            string value = kv.Length > 1 ? kv[1] : "";
            switch (key)
            {
                case "ticks" when int.TryParse(value, out int t): Ticks = Math.Max(1, t); break;
                case "tolerance" when float.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float tol): Tolerance = Math.Max(0f, tol); break;
                case "min-count" when int.TryParse(value, out int mc): MinCount = Math.Max(0, mc); break;
                case "scenario":
                case "scenarios": Scenarios = value; break;
                case "control-runs" when int.TryParse(value, out int cr): ControlRuns = Math.Max(0, cr); break;
                case "record": Record = value.Length == 0 || value != "false"; break;
                default: GD.PushWarning($"[LodDiff] ignored argument '{arg}'"); break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Data
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class RunMetrics
    {
        public LODLevel Tier;
        public readonly Dictionary<int, int> Population = new();
        public readonly Dictionary<int, int> Births = new();
        public readonly Dictionary<int, int> DeathsStarve = new();
        public readonly Dictionary<int, int> DeathsAge = new();
        public readonly Dictionary<int, int> DeathsPred = new();
        public readonly Dictionary<int, int> DeathsEnv = new();
        public readonly Dictionary<int, int> KillsMade = new();
        public int Spores, Nests, Crystals, Corpses;
        public int TotalKills;
        public int TerraformNudges, TerraformShifts;
        public float NutritionConsumed, NutritionRegen, NutritionEnriched;
        public float MeanShroomerScale;
        public int ShroomerSampled;
        /// <summary>Species id -> mean distance to the nearest same-species creature (tiles).</summary>
        public readonly Dictionary<int, float> MeanNearestNeighbour = new();
        /// <summary>Species id -> how many individuals that mean was taken over.</summary>
        public readonly Dictionary<int, int> SpacingSample = new();
        public double WallMs;

        public int TotalPopulation
        {
            get { int t = 0; foreach (var v in Population.Values) t += v; return t; }
        }
    }

    private sealed class MetricDiff
    {
        public string Name = "";
        public double Full;
        public double Other;          // the Minimal-tier run
        public double Relative;       // |full-minimal| / max(|full|,|minimal|)
        public double Ratio;          // minimal / full — what "5x fewer" looks like
        public double NoiseFloor;     // worst same-tier divergence across the control runs
        public bool HaveFloor;        // false when ControlRuns = 0 (tolerance-only mode)
        public bool BelowMinCount;    // too few events either side to mean anything
        public bool ExceedsTolerance; // the raw verdict the tolerance alone would give
        public bool Failed;           // over tolerance AND over the noise floor
    }
}
