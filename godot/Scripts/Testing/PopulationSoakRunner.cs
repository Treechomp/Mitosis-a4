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
/// Writes logs/population_soak.csv (one row per sample), prints a verdict on the two questions
/// above, and then asserts the whole-game invariants: the exit code is the number of failed
/// assertions, so this is a GATE as well as a measurement. See docs/archive/whole-game-invariants-2026-08.md.
/// </summary>
public partial class PopulationSoakRunner : Node
{
    [Export] public int Ticks = 20000;
    [Export] public int WorldSizeChunks = 36;
    [Export] public int MaxPopulation = 12000;
    [Export] public int InitialPopulation = 2000;
    [Export] public int SampleInterval = 200;
    [Export] public int WorldSeed = 1234;

    /// <summary>Crystals, and therefore Faelings. Mirrors GameManager's export.</summary>
    [Export] public int FaelingCrystalCount = 12;

    /// <summary>Minimum distance between Shroomer hearts. Mirrors GameManager's export.</summary>
    [Export] public float MinHeartSpacing = 50f;

    // ── Whole-game invariant thresholds ───────────────────────────────────────
    // Provisional by design — see docs/archive/whole-game-invariants-2026-08.md. They exist so that "a faction
    // wipes the map in five minutes" is a FAILING BUILD rather than something a person has to
    // notice. Unlike the LOD differential, which carries documented standing exceptions and
    // therefore always exits 1, this gate is binary: if an assertion needs an exception, the
    // threshold is wrong and should be changed deliberately, not excused.

    /// <summary>No faction anchor may be lost before this tick — five minutes of game time.</summary>
    [Export] public int GraceTicks = 6000;

    /// <summary>
    /// Below this the world is static: nobody is contesting any ground.
    ///
    /// Was 0.02, which no healthy run reached — see docs/archive/whole-game-invariants-2026-08.md. Deviation is a
    /// whole-world MEAN over 1.33 million tiles, and the faction populations that exist at 20,000
    /// ticks work perhaps 1-2% of the map, so 0.02 asked for something the game cannot produce.
    /// Measured: 0.0044 while almost nothing is happening, 0.0139-0.0171 while visibly contested.
    /// 0.005 separates those two states, which is what a floor is for.
    /// </summary>
    [Export] public float MinDeviation = 0.005f;

    /// <summary>Above this the world has been converted rather than contested.</summary>
    [Export] public float MaxDeviation = 0.35f;

    // Per-faction population floors. An anchor with no faction is not a faction: the anchor test
    // alone passed a run holding 46 nests and ONE living Sectid. Each faction must clear BOTH its
    // anchor count and its head count. Provisional like every threshold here — see
    // docs/archive/whole-game-invariants-2026-08.md.

    /// <summary>Sectids alive at the end of the run.</summary>
    [Export] public int MinSectidPopulation = 50;

    /// <summary>Shroomers alive at the end of the run.</summary>
    [Export] public int MinShroomerPopulation = 50;

    /// <summary>Faelings alive at the end of the run — keepers are few by design (12 crystals).</summary>
    [Export] public int MinFaelingPopulation = 6;

    /// <summary>
    /// Diagnostic hunt funnel for one species (--funnel=Sectid), written to
    /// logs/population_soak/hunt_funnel.csv every FunnelWindow ticks. Off unless asked for: it
    /// only counts, but a gate should not carry instrumentation it is not using.
    /// </summary>
    [Export] public string FunnelSpecies = "";

    /// <summary>Ticks per funnel row.</summary>
    [Export] public int FunnelWindow = 1000;

    private int _failures;

    private const int ChunkSize = 32;
    private const int TileSize = 16;
    private const string LogRoot = "res://logs";

    public override void _Ready()
    {
        ParseCommandLine();
        Run();
        GetTree().Quit(_failures);
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
        stack.Mycelium.MinHeartSpacing = MinHeartSpacing;

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
        spawner.SpawnCrystals(stack.Crystal, em, faelingEnabled: true, budget, FaelingCrystalCount);
        spawner.SpawnInitialNests(stack.Nest, em, sectidSeed);
        spawner.SpawnCreatures(herbivoreSeed, predatorSeed, shroomerSeed);
        // Hearts after the Shroomers, as GameManager does — a heart is placed on a bloom, not the
        // other way round. Without this the soak has no Shroomer anchor to assert on.
        spawner.SpawnMyceliumHearts(em, MinHeartSpacing);

        if (!string.IsNullOrEmpty(FunnelSpecies))
        {
            string funnelPath = Path.Combine(
                ProjectSettings.GlobalizePath($"{LogRoot}/population_soak"), "hunt_funnel.csv");
            HuntFunnelProbe.Begin(SpeciesRegistry.GetId(FunnelSpecies), funnelPath);
            GD.Print($"[Soak] hunt funnel for {FunnelSpecies} every {FunnelWindow} ticks " +
                     $"-> {funnelPath}");
        }

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

            if (HuntFunnelProbe.Enabled && (t % FunnelWindow == 0 || t == Ticks))
            {
                TakeFunnelCensus(em, world);
                HuntFunnelProbe.Flush(t);
            }

            if (t % SampleInterval == 0 || t == Ticks)
            {
                samples.Add(Sample.Take(t, em, budget, logger));
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
        HuntFunnelProbe.End();
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
        GD.Print($"  anchors: nests {last.Nests}, hearts {last.Hearts}, crystals {last.Crystals}; " +
                 $"{last.StructuresDestroyed} destroyed over the run");
        GD.Print($"  world deviation: {last.Deviation:F4} " +
                 $"(wetter {last.DeviationWetter:F4}, drier {last.DeviationDrier:F4})");
        GD.Print("[Soak] ──────────────────────────");

        CheckInvariants(samples, budget, logger);
    }

    /// <summary>
    /// The three funnel figures that need a world pass rather than a hunt-path hook: how many of
    /// the watched species are asleep, how far each stands from the nearest nest, and how hungry
    /// they are. Runs once per funnel window, over at most a few hundred entities.
    /// </summary>
    private static void TakeFunnelCensus(EntityManager em, WorldManager world)
    {
        var nests = new List<(float x, float y)>(64);
        foreach (int e in em.AllEntities())
        {
            if (!em.HasComponents(e, ComponentFlags.Structure)) continue;
            ref var s = ref em.Structures[e];
            if (s.IsDestroyed || s.Kind != StructureKind.Nest) continue;
            ref var p = ref em.Positions[e];
            nests.Add((p.X, p.Y));
        }

        int pop = 0, hibernating = 0;
        double distSum = 0, hungerSum = 0;
        int distCount = 0;
        foreach (int e in em.AllEntities())
        {
            if (em.HasComponents(e, ComponentFlags.Structure)) continue;
            if (!em.HasComponents(e, ComponentFlags.Species)) continue;
            if (!HuntFunnelProbe.Watching(em.Species[e].SpeciesId)) continue;
            pop++;

            if (em.HasComponents(e, ComponentFlags.FoodCarrier)
                && em.FoodCarriers[e].IsHibernating) hibernating++;

            if (em.HasComponents(e, ComponentFlags.Hunger))
            {
                ref var h = ref em.Hungers[e];
                if (h.Max > 0f) hungerSum += h.Current / h.Max * 100f;
            }

            if (nests.Count == 0) continue;
            ref var pos = ref em.Positions[e];
            float best = float.MaxValue;
            foreach (var (nx, ny) in nests)
            {
                float d = MathUtils.DistanceSquared(pos.X, pos.Y, nx, ny);
                if (d < best) best = d;
            }
            distSum += MathF.Sqrt(best);
            distCount++;
        }

        HuntFunnelProbe.CensusPopulation = pop;
        HuntFunnelProbe.CensusHibernating = hibernating;
        HuntFunnelProbe.CensusMeanDistToNest = distCount > 0 ? (float)(distSum / distCount) : -1f;
        HuntFunnelProbe.CensusMeanHungerPct = pop > 0 ? (float)(hungerSum / pop) : -1f;
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

    // ══════════════════════════════════════════════════════════════════════════
    // Whole-game invariants
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Assert the properties a HEALTHY WHOLE GAME has, and fail the build when it does not.
    ///
    /// This exists because three changes each passed their own acceptance criteria and together
    /// produced a world where one faction wiped every rival anchor inside five minutes. Nothing
    /// could have caught it: the LOD differential compares a build against itself, and the
    /// population soak measured composition. Neither asserted anything about the game as a whole.
    ///
    /// The assertions are deliberately few and deliberately blunt. Each one is a sentence about
    /// the game that ought to be true of every build, not a tuning target.
    /// </summary>
    private void CheckInvariants(List<Sample> samples, PopulationBudget budget,
                                  EcosystemLogger logger)
    {
        if (samples.Count == 0)
        {
            Fail("no samples were taken — the run produced nothing to assert on");
            return;
        }
        var last = samples[^1];
        GD.Print("\n[Soak] ───────── invariants ─────────");

        // 1. Nothing wipes anchors in the opening minutes. A raid should be an event a player can
        //    see coming and answer, and a faction that loses its infrastructure before it has
        //    built any has not been beaten, it has been deleted.
        long destroyedInGrace = 0;
        int graceTick = 0;
        foreach (var s in samples)
        {
            if (s.Tick > GraceTicks) break;
            destroyedInGrace = s.StructuresDestroyed;
            graceTick = s.Tick;
        }
        Assert(destroyedInGrace == 0,
            $"no anchor lost before t={GraceTicks}",
            $"{destroyedInGrace} destroyed by t={graceTick}");

        // 2. All three factions still hold ground at the end AND still exist to hold it. This is
        //    the "is it still a three-way war" test — a two-way war is a different game, and
        //    losing a faction silently is exactly what happened.
        //
        //    Both halves are needed. The anchor test alone passed a run with 46 nests standing
        //    and one living Sectid: the buildings outlive the faction, so counting buildings
        //    reports a dead faction as healthy. The population test alone would not catch a
        //    faction that is alive but has been evicted from every site it holds.
        AssertFaction("Sectid", "nest", last.Nests, last.Sectids, MinSectidPopulation);
        AssertFaction("Shroomer", "heart", last.Hearts, last.Shroomers, MinShroomerPopulation);
        AssertFaction("Faeling", "crystal", last.Crystals, last.Faelings, MinFaelingPopulation);

        // 2b. Sectids caught something. Separated from the population floor because it is a
        //     strictly more diagnostic failure: a low population says the faction is losing,
        //     zero kills says its economy never started. Sectid nests hatch on food carried
        //     home, so a swarm that catches nothing has no births at all — the population floor
        //     then fails as a consequence, several thousand ticks later and further from the
        //     cause. Reading both lines together separates "outfought" from "never ignited".
        int sectidKills = logger.RunKillsMade.TryGetValue(SpeciesRegistry.GetId("Sectid"),
                                                          out int k) ? k : 0;
        Assert(sectidKills > 0, "Sectid: kills_made above zero over the run",
            $"Sectid kills_made={sectidKills}, floor=1");

        // 3. The world is being CONTESTED — neither untouched nor converted. Below the floor
        //    nobody is terraforming anything that matters; above the ceiling somebody has
        //    homogenised the map, which is the failure mode every faction has in common.
        Assert(last.Deviation > MinDeviation,
            $"world deviation above {MinDeviation:F2} (the world is contested, not static)",
            $"deviation={last.Deviation:F4}");
        Assert(last.Deviation < MaxDeviation,
            $"world deviation below {MaxDeviation:F2} (contested, not converted)",
            $"deviation={last.Deviation:F4} (wetter {last.DeviationWetter:F4}, drier {last.DeviationDrier:F4})");

        // 4. No population class exceeds its ceiling. Cheap, and it has caught a real bug before.
        int overBudget = 0;
        foreach (var s in samples)
        {
            if (s.Herbivores > budget.BudgetFor(PopClass.Herbivore)) overBudget++;
            if (s.Predators > budget.BudgetFor(PopClass.Predator)) overBudget++;
            if (s.Factions > budget.BudgetFor(PopClass.Faction)) overBudget++;
        }
        Assert(overBudget == 0, "no class exceeds its population ceiling",
            $"{overBudget} samples over budget");

        GD.Print(_failures == 0
            ? "[Soak] ALL INVARIANTS HOLD"
            : $"[Soak] {_failures} INVARIANT(S) FAILED");
        GD.Print("[Soak] ────────────────────────────");
    }

    /// <summary>
    /// One faction's two survival tests: it holds at least one anchor, and enough of it is alive
    /// to be a faction. Reported as two lines, never one — "which of the two failed" is the whole
    /// diagnostic value, and a combined verdict would hide it.
    /// </summary>
    private void AssertFaction(string faction, string anchor, int anchors,
                                int population, int floor)
    {
        Assert(anchors > 0, $"{faction}: holds at least one {anchor}",
            $"{faction} {anchor}s={anchors}, floor=1");
        Assert(population >= floor, $"{faction}: population at or above {floor}",
            $"{faction} population={population}, floor={floor}");
    }

    private void Assert(bool condition, string claim, string actual)
    {
        if (condition) GD.Print($"  PASS  {claim}  [{actual}]");
        else Fail($"{claim}  [{actual}]");
    }

    private void Fail(string message)
    {
        _failures++;
        GD.PrintErr($"  FAIL  {message}");
    }

    private static string Pct(int part, int whole)
        => whole > 0 ? (part / (double)whole).ToString("P2", CultureInfo.InvariantCulture) : "n/a";

    private void WriteCsv(List<Sample> samples, PopulationBudget budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("tick,total,herbivores,herbivore_budget,predators,predator_budget," +
                      "factions,faction_budget,shroomers,sectids,faelings,corpses," +
                      "refused_herbivore,refused_predator,refused_faction," +
                      "nests,hearts,crystals,structures_destroyed," +
                      "world_deviation,deviation_wetter,deviation_drier");
        foreach (var s in samples)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}," +
                "{15},{16},{17},{18},{19:0.#####},{20:0.#####},{21:0.#####}",
                s.Tick, s.Total,
                s.Herbivores, budget.BudgetFor(PopClass.Herbivore),
                s.Predators, budget.BudgetFor(PopClass.Predator),
                s.Factions, budget.BudgetFor(PopClass.Faction),
                s.Shroomers, s.Sectids, s.Faelings, s.Corpses,
                s.RefusedHerbivore, s.RefusedPredator, s.RefusedFaction,
                s.Nests, s.Hearts, s.Crystals, s.StructuresDestroyed,
                s.Deviation, s.DeviationWetter, s.DeviationDrier));
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
        // Faction ANCHORS — the three things a faction dies without, and the subject of the
        // "everyone still holds one" invariant.
        public readonly int Nests, Hearts, Crystals;
        public readonly long StructuresDestroyed;   // cumulative
        public readonly float Deviation, DeviationWetter, DeviationDrier;

        private Sample(int tick, int total, int herb, int pred, int fac,
                       int shr, int sec, int fae, int corpses,
                       long rh, long rp, long rf,
                       int nests, int hearts, int crystals, long destroyed,
                       WorldDeviation dev)
        {
            Tick = tick; Total = total; Herbivores = herb; Predators = pred; Factions = fac;
            Shroomers = shr; Sectids = sec; Faelings = fae; Corpses = corpses;
            RefusedHerbivore = rh; RefusedPredator = rp; RefusedFaction = rf;
            Nests = nests; Hearts = hearts; Crystals = crystals; StructuresDestroyed = destroyed;
            Deviation = dev.Mean; DeviationWetter = dev.Wetter; DeviationDrier = dev.Drier;
        }

        /// <summary>
        /// Class counts come from the budget (the live counts it maintains); the per-faction split
        /// and the anchor counts need an entity pass, since the budget charges all three factions
        /// to one class and does not see structures at all.
        /// </summary>
        public static Sample Take(int tick, EntityManager em, PopulationBudget budget,
                                   EcosystemLogger logger)
        {
            int shr = 0, sec = 0, fae = 0;
            int nests = 0, hearts = 0, crystals = 0;
            foreach (int e in em.AllEntities())
            {
                if (em.HasComponents(e, ComponentFlags.Structure))
                {
                    ref var s = ref em.Structures[e];
                    if (s.IsDestroyed) continue;
                    switch (s.Kind)
                    {
                        case StructureKind.Nest: nests++; break;
                        case StructureKind.Crystal: crystals++; break;
                        case StructureKind.MyceliumHeart: hearts++; break;
                    }
                    continue;
                }
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
                budget.RefusalsFor(PopClass.Faction),
                nests, hearts, crystals, logger.RunStructuresDestroyed,
                // Read the value the logger last wrote to the CSV rather than recomputing, so the
                // assertion and the artefact can never disagree.
                logger.LastDeviation);
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
                case "crystals" when int.TryParse(value, out int v): FaelingCrystalCount = Math.Max(1, v); break;
                case "heart-spacing" when float.TryParse(value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float hs): MinHeartSpacing = Math.Max(0f, hs); break;
                case "grace" when int.TryParse(value, out int v): GraceTicks = Math.Max(0, v); break;
                case "min-sectid" when int.TryParse(value, out int v): MinSectidPopulation = Math.Max(0, v); break;
                case "min-shroomer" when int.TryParse(value, out int v): MinShroomerPopulation = Math.Max(0, v); break;
                case "min-faeling" when int.TryParse(value, out int v): MinFaelingPopulation = Math.Max(0, v); break;
                case "funnel": FunnelSpecies = value; break;
                case "funnel-window" when int.TryParse(value, out int v): FunnelWindow = Math.Max(1, v); break;
                default: GD.PushWarning($"[Soak] ignored argument '{arg}'"); break;
            }
        }
    }
}
