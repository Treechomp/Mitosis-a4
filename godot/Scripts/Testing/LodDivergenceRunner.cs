using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;
using Mitosis.Components;
using Mitosis.Systems;

namespace Mitosis.Testing;

/// <summary>
/// Records a fingerprint stream for ONE simulation, or compares two recorded streams into a
/// divergence curve. One process does one of those things and then exits.
///
/// TWO PROCESSES, NOT TWO SIMULATIONS. <c>EcosystemLogger.Instance</c>, <c>SimRandom</c>'s seed and
/// <c>HuntFunnelProbe</c>'s counters are static. Two simulations in one process share them, so the
/// second run is not the run it claims to be and a difference between them is partly an artefact
/// of the sharing. The comparison therefore happens over two FILES, afterwards, and the runs that
/// produced them never meet.
///
/// Usage — the recorded curve is three invocations:
/// <code>
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --scenario=predator_prey --tier=Full    --ticks=3000 --sample=5
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --scenario=predator_prey --tier=Minimal --ticks=3000 --sample=5
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --compare=logs/lod_divergence/predator_prey_Full.csv,logs/lod_divergence/predator_prey_Minimal.csv \
///       --curve=docs/lod-divergence-curve.csv
/// </code>
///
/// THIS RUNNER DOES NOT GATE. It exits 0 whenever it produced what it was asked for, and non-zero
/// only when it could not. The gate on the curve is a later stage and needs a tolerance that has
/// been measured rather than chosen; see <see cref="LodDivergenceCurve"/> for why that tolerance
/// must be "no worse than recorded" and not "flat".
/// </summary>
public partial class LodDivergenceRunner : Node
{
    /// <summary>Ticks the recorded run is stepped for. Matches the differential's span by default,
    /// so the two instruments describe the same run.</summary>
    [Export] public int Ticks = 3000;

    /// <summary>Ticks between samples. The onset reading can be no finer than this.</summary>
    [Export] public int SampleInterval = 5;

    /// <summary>Scenario name, without the `.scenario.txt` suffix.</summary>
    [Export] public string Scenario = "";

    /// <summary>Tier every entity is pinned to for this run.</summary>
    [Export] public string Tier = "Full";

    /// <summary>Seed override; 0 takes the scenario's own, and the scenario's 0 means 12345.</summary>
    [Export] public int Seed;

    /// <summary>Where this run's stream is written; empty derives it from scenario and tier.</summary>
    [Export] public string Out = "";

    /// <summary>Two stream paths, comma-separated. Set, this process compares instead of running.</summary>
    [Export] public string ComparePair = "";

    /// <summary>Where the curve is written; empty derives it from the streams.</summary>
    [Export] public string Curve = "";

    private const string ScenarioDir = "res://TestScenarios";
    private const string StreamRoot = "res://logs/lod_divergence";

    public override void _Ready()
    {
        // A throw out of _Ready leaves Godot idling with the scene loaded: no exit code, no
        // failure, just a process that never returns (D14/D15).
        int exitCode;
        try
        {
            ParseCommandLine();
            exitCode = ComparePair.Trim().Length > 0 ? RunComparison() : RunRecording();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[LodDiverge] ABORTED: {ex}");
            exitCode = 1;
        }
        GetTree().Quit(exitCode);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Recording one run
    // ══════════════════════════════════════════════════════════════════════════

    private int RunRecording()
    {
        if (Scenario.Trim().Length == 0)
        {
            GD.PrintErr("[LodDiverge] --scenario is required when recording.");
            return 1;
        }
        if (!Enum.TryParse<LODLevel>(Tier, ignoreCase: true, out var tier))
        {
            GD.PrintErr($"[LodDiverge] '{Tier}' is not an LOD tier. One of: {string.Join(", ", Enum.GetNames<LODLevel>())}");
            return 1;
        }

        string file = Path.Combine(ProjectSettings.GlobalizePath(ScenarioDir), $"{Scenario}.scenario.txt");
        if (!File.Exists(file))
        {
            GD.PrintErr($"[LodDiverge] no scenario '{Scenario}' in {ScenarioDir}");
            return 1;
        }

        var scenario = TestScenario.Parse(File.ReadAllText(file), Scenario);
        foreach (var w in scenario.Warnings) GD.PushWarning($"[LodDiverge] {scenario.Name}: {w}");

        // Both runs of a pair must start from the same world and the same dice. A scenario seed of
        // 0 means "random per run", which cannot be compared with anything.
        int seed = Seed != 0 ? Seed : (scenario.Seed != 0 ? scenario.Seed : 12345);
        int sample = Math.Max(1, SampleInterval);

        string outPath = Out.Trim().Length > 0
            ? ProjectSettings.GlobalizePath(Out)
            : Path.Combine(ProjectSettings.GlobalizePath(StreamRoot), $"{scenario.Name}_{tier}.csv");

        var meta = new FingerprintStream
        {
            Scenario = scenario.Name, Tier = tier.ToString(), Seed = seed,
            Ticks = Ticks, SampleInterval = sample,
        };

        GD.Print($"[LodDiverge] {scenario.Name} tier={tier} seed={seed} {Ticks} ticks, sample every {sample}");

        var sim = ScenarioSimulation.Build(scenario, seed, tier,
            $"{StreamRoot}/{scenario.Name}_{tier}_logs");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using (var writer = new SimulationFingerprint.Writer(outPath, meta))
        {
            // Tick 0 is the world as spawned, before any system has run: the one sample both runs
            // are guaranteed to agree on, and the baseline the curve rises from.
            writer.Write(SimulationFingerprint.Capture(sim.Entities, 0));

            for (int t = 1; t <= Ticks; t++)
            {
                sim.Tick();
                if (t % sample == 0)
                    writer.Write(SimulationFingerprint.Capture(sim.Entities, t));
            }
        }
        clock.Stop();
        sim.Logger.Close();

        GD.Print($"[LodDiverge] {Ticks} ticks in {clock.Elapsed.TotalSeconds:F1}s -> {outPath}");
        return 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Comparing two recorded runs
    // ══════════════════════════════════════════════════════════════════════════

    private int RunComparison()
    {
        var paths = ComparePair.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length != 2)
        {
            GD.PrintErr("[LodDiverge] --compare takes exactly two stream paths, comma-separated.");
            return 1;
        }

        var streams = new FingerprintStream[2];
        for (int i = 0; i < 2; i++)
        {
            string p = ProjectSettings.GlobalizePath(paths[i].Trim());
            if (!File.Exists(p)) { GD.PrintErr($"[LodDiverge] no stream at {p}"); return 1; }
            streams[i] = SimulationFingerprint.Read(p);
        }

        var (a, b) = (streams[0], streams[1]);
        if (a.Scenario != b.Scenario || a.Seed != b.Seed)
        {
            GD.PrintErr($"[LodDiverge] these streams are not a pair: " +
                        $"{a.Scenario}/seed {a.Seed} against {b.Scenario}/seed {b.Seed}. " +
                        "A curve between two different worlds measures the worlds, not the tiers.");
            return 1;
        }
        if (a.SampleInterval != b.SampleInterval)
            GD.PushWarning($"[LodDiverge] sample intervals differ ({a.SampleInterval} vs {b.SampleInterval}); " +
                           "only ticks present in both are compared.");

        var curve = LodDivergenceCurve.Compare(a, b);
        if (curve.Points.Count == 0)
        {
            GD.PrintErr("[LodDiverge] the two streams share no sampled tick.");
            return 1;
        }

        string curvePath = Curve.Trim().Length > 0
            ? ProjectSettings.GlobalizePath(Curve)
            : Path.Combine(ProjectSettings.GlobalizePath(StreamRoot), $"curve_{a.Scenario}_{a.Tier}_vs_{b.Tier}.csv");
        curve.Write(curvePath);

        GD.Print($"\n[LodDiverge] ───────── {a.Scenario}: {a.Tier} vs {b.Tier}, seed {a.Seed} ─────────");
        GD.Print(curve.OnsetTick < 0
            ? "  onset:  never — the two runs stayed identical for the whole span"
            : $"  onset:  tick {curve.OnsetTick} — {curve.OnsetDetail}");
        GD.Print(FormattableString.Invariant(
            $"  growth: final {curve.Final:F4}, mean {curve.Mean:F4}, over {curve.Points.Count} samples"));
        PrintProfile(curve);
        GD.Print($"[LodDiverge] curve -> {curvePath}");
        return 0;
    }

    /// <summary>
    /// A tenth-of-the-run digest of the curve, so its shape is readable from the console without
    /// opening the CSV. The shape is the point: a curve that rises and settles is a bounded cost,
    /// one that keeps climbing is not.
    /// </summary>
    private static void PrintProfile(LodDivergenceCurve curve)
    {
        GD.Print("  curve (each row is a tenth of the run):");
        int buckets = Math.Min(10, curve.Points.Count);
        for (int i = 0; i < buckets; i++)
        {
            int from = i * curve.Points.Count / buckets;
            int to = (i + 1) * curve.Points.Count / buckets;
            double sum = 0;
            for (int k = from; k < to; k++) sum += curve.Points[k].Divergence;
            double mean = sum / Math.Max(1, to - from);
            GD.Print(FormattableString.Invariant(
                $"    t={curve.Points[to - 1].Tick,6}  {mean:F4}  {new string('#', (int)Math.Round(mean * 60))}"));
        }
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
                case "sample" when int.TryParse(value, out int s): SampleInterval = Math.Max(1, s); break;
                case "seed" when int.TryParse(value, out int s): Seed = s; break;
                case "scenario": Scenario = value.Trim(); break;
                case "tier": Tier = value.Trim(); break;
                case "out": Out = value.Trim(); break;
                case "compare": ComparePair = value.Trim(); break;
                case "curve": Curve = value.Trim(); break;
                default: GD.PushWarning($"[LodDiverge] ignored argument '{arg}'"); break;
            }
        }
    }
}
