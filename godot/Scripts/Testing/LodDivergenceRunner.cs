using System;
using System.Globalization;
using System.IO;
using Godot;
using Mitosis.Components;

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
/// THREE KINDS OF PAIR, AND TWO OF THEM ARE CONTROLS. A fidelity curve on its own cannot be read:
/// every component is clamped into [0,1], so a curve that flattens has either stopped rising or run
/// out of room, and nothing in the curve says which. Two controls give it a scale.
///
///  - FLOOR — one tier against itself, same seed. Must read exactly zero at every sample. It does
///    not, the instrument is broken and no other reading from it means anything.
///  - CEILING — one tier against itself, DIFFERENT seeds: two worlds with nothing in common. This
///    is what the scalar reads when there is no fidelity left to measure. A fidelity curve sitting
///    near it is reporting "not the same run", not "this much worse".
///  - FIDELITY — two tiers, same seed. The measurement, readable only against the ceiling.
///
/// Usage — the recorded curve and its two controls:
/// <code>
///   # fidelity pair
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --scenario=predator_prey --tier=Full    --ticks=3000 --sample=5 --seed=42
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --scenario=predator_prey --tier=Minimal --ticks=3000 --sample=5 --seed=42
///   # ceiling control: same tier, two seeds
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --scenario=predator_prey --tier=Full --ticks=3000 --sample=5 --seed=99 \
///       --out=res://logs/lod_divergence/ceiling_b.csv
///   # compare, quoting the plateau against the ceiling
///   godot --headless --path godot res://Scenes/LodDivergence.tscn -- \
///       --compare=logs/lod_divergence/predator_prey_Full.csv,logs/lod_divergence/predator_prey_Minimal.csv \
///       --ceiling=logs/lod_divergence/predator_prey_Full.csv,logs/lod_divergence/ceiling_b.csv \
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

    /// <summary>
    /// Two stream paths for the unrelated-worlds control, comma-separated. Set, the compared
    /// curve's plateau is written as a fraction of this pair's plateau — the only form in which a
    /// plateau can be read at all.
    /// </summary>
    [Export] public string CeilingPair = "";

    /// <summary>
    /// Permit a comparison between two different seeds. Off by default because such a pair measures
    /// the two worlds and not the two tiers; on, deliberately, when that IS the measurement — which
    /// is what the ceiling control is.
    /// </summary>
    [Export] public bool AllowUnrelated;

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
            : Path.Combine(ProjectSettings.GlobalizePath(StreamRoot), $"{scenario.Name}_{tier}_{seed}.csv");

        var meta = new FingerprintStream
        {
            Scenario = scenario.Name, Tier = tier.ToString(), Seed = seed,
            Ticks = Ticks, SampleInterval = sample,
        };

        GD.Print($"[LodDiverge] {scenario.Name} tier={tier} seed={seed} {Ticks} ticks, sample every {sample}");

        var sim = ScenarioSimulation.Build(scenario, seed, tier,
            $"{StreamRoot}/{scenario.Name}_{tier}_{seed}_logs");

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

    /// <summary>
    /// Load the two streams named by a comma-separated pair, refusing a pair that cannot be
    /// compared. Returns null and prints why on any refusal.
    /// </summary>
    private static (FingerprintStream A, FingerprintStream B)? LoadPair(string spec, bool allowUnrelated, string what)
    {
        var paths = spec.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length != 2)
        {
            GD.PrintErr($"[LodDiverge] --{what} takes exactly two stream paths, comma-separated.");
            return null;
        }

        var streams = new FingerprintStream[2];
        for (int i = 0; i < 2; i++)
        {
            string p = ProjectSettings.GlobalizePath(paths[i].Trim());
            if (!File.Exists(p)) { GD.PrintErr($"[LodDiverge] no stream at {p}"); return null; }
            streams[i] = SimulationFingerprint.Read(p);
        }

        var (a, b) = (streams[0], streams[1]);
        if (a.Scenario != b.Scenario)
        {
            GD.PrintErr($"[LodDiverge] these streams are not a pair: {a.Scenario} against {b.Scenario}.");
            return null;
        }
        if (a.Seed != b.Seed && !allowUnrelated)
        {
            GD.PrintErr($"[LodDiverge] seeds differ ({a.Seed} against {b.Seed}). A curve between two " +
                        "different worlds measures the worlds, not the tiers. That IS the ceiling " +
                        "control — pass --unrelated to say so deliberately.");
            return null;
        }
        if (a.SampleInterval != b.SampleInterval)
            GD.PushWarning($"[LodDiverge] sample intervals differ ({a.SampleInterval} vs {b.SampleInterval}); " +
                           "only ticks present in both are compared.");
        return (a, b);
    }

    private int RunComparison()
    {
        var pair = LoadPair(ComparePair, AllowUnrelated, "compare");
        if (pair is null) return 1;
        var (a, b) = pair.Value;

        var curve = LodDivergenceCurve.Compare(a, b);
        if (curve.Points.Count == 0)
        {
            GD.PrintErr("[LodDiverge] the two streams share no sampled tick.");
            return 1;
        }

        // The ceiling pair is unrelated by definition, so it never needs the opt-in.
        LodDivergenceCurve? ceiling = null;
        if (CeilingPair.Trim().Length > 0)
        {
            var cp = LoadPair(CeilingPair, allowUnrelated: true, "ceiling");
            if (cp is null) return 1;
            ceiling = LodDivergenceCurve.Compare(cp.Value.A, cp.Value.B);
            if (ceiling.PairKind != "ceiling")
            {
                GD.PrintErr($"[LodDiverge] --ceiling wants one tier against itself on two seeds; " +
                            $"that pair reads as '{ceiling.PairKind}'.");
                return 1;
            }
        }

        string curvePath = Curve.Trim().Length > 0
            ? ProjectSettings.GlobalizePath(Curve)
            : Path.Combine(ProjectSettings.GlobalizePath(StreamRoot),
                           $"curve_{a.Scenario}_{a.Tier}{a.Seed}_vs_{b.Tier}{b.Seed}.csv");
        curve.Write(curvePath, ceiling);

        Report(curve, ceiling);
        GD.Print($"[LodDiverge] curve -> {curvePath}");
        return 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Reporting
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The console reading. Seven component curves, two scalars and two windows, because the one
    /// combined number over the whole run was the thing that hid what this curve is made of.
    /// </summary>
    private static void Report(LodDivergenceCurve curve, LodDivergenceCurve? ceiling)
    {
        var a = curve.A!; var b = curve.B!;
        GD.Print($"\n[LodDiverge] ───────── {a.Scenario}: {a.Tier}/{a.Seed} vs {b.Tier}/{b.Seed} " +
                 $"[{curve.PairKind}] ─────────");
        GD.Print(curve.OnsetTick < 0
            ? "  onset:  never — the two runs stayed identical for the whole span"
            : $"  onset:  tick {curve.OnsetTick} — {curve.OnsetDetail}");

        var growth = curve.Growth;
        var plateau = curve.Plateau;
        GD.Print("  windows (state is the reading; position is diagnostic):");
        foreach (var w in new[] { growth, plateau })
            GD.Print(FormattableString.Invariant(
                $"    {w.Name,-8} t={w.FromTick,5}..{w.ToTick,-5} n={w.Samples,4}  combined {w.Divergence:F4}  state {w.State:F4}  position {w.Position:F4}"));

        GD.Print("  components (mean over run / growth / plateau):");
        var overall = curve.Summarise("all", 0, curve.LastTick);
        for (int i = 0; i < LodDivergenceCurve.ComponentNames.Length; i++)
            GD.Print(FormattableString.Invariant(
                $"    {LodDivergenceCurve.ComponentNames[i],-14} {LodDivergenceCurve.ComponentKinds[i],-8} {overall.Components[i]:F4}  {growth.Components[i]:F4}  {plateau.Components[i]:F4}"));

        if (ceiling is not null)
        {
            var cp = ceiling.Plateau;
            GD.Print(FormattableString.Invariant(
                $"  ceiling (seeds {ceiling.A!.Seed}/{ceiling.B!.Seed}, tier {ceiling.A!.Tier}): combined {cp.Divergence:F4}  state {cp.State:F4}  position {cp.Position:F4}"));
            double fd = LodDivergenceCurve.CeilingFraction(plateau.Divergence, cp.Divergence);
            double fs = LodDivergenceCurve.CeilingFraction(plateau.State, cp.State);
            double fp = LodDivergenceCurve.CeilingFraction(plateau.Position, cp.Position);
            GD.Print(FormattableString.Invariant(
                $"  PLATEAU AS FRACTION OF CEILING: combined {fd:P1}  state {fs:P1}  position {fp:P1}"));
        }

        PrintProfile(curve);
    }

    /// <summary>
    /// A tenth-of-the-run digest of the curve, so its shape is readable from the console without
    /// opening the CSV. The shape is the point: a curve that rises and settles is a bounded cost,
    /// one that keeps climbing is not.
    /// </summary>
    private static void PrintProfile(LodDivergenceCurve curve)
    {
        GD.Print("  curve (each row is a tenth of the run; bar is state):");
        int buckets = Math.Min(10, curve.Points.Count);
        for (int i = 0; i < buckets; i++)
        {
            int from = i * curve.Points.Count / buckets;
            int to = (i + 1) * curve.Points.Count / buckets;
            double all = 0, state = 0;
            for (int k = from; k < to; k++) { all += curve.Points[k].Divergence; state += curve.Points[k].StateDivergence; }
            int n = Math.Max(1, to - from);
            all /= n; state /= n;
            string bar = new string('#', (int)Math.Round(state * 60));
            GD.Print(FormattableString.Invariant(
                $"    t={curve.Points[to - 1].Tick,6}  combined {all:F4}  state {state:F4}  {bar}"));
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
                case "ceiling": CeilingPair = value.Trim(); break;
                case "unrelated": AllowUnrelated = value.Trim().Length == 0 || value.Trim() == "1" || bool.TryParse(value, out bool u) && u; break;
                case "curve": Curve = value.Trim(); break;
                default: GD.PushWarning($"[LodDiverge] ignored argument '{arg}'"); break;
            }
        }
    }
}
