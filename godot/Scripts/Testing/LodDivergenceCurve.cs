using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mitosis.SpeciesData;

namespace Mitosis.Testing;

/// <summary>
/// How fast two runs of the same scenario come apart.
///
/// WHY A RATE AND NOT AN OUTCOME. The LOD differential compares two runs after 3,000 ticks, by
/// which point they have diverged chaotically: a food-economy change small enough to leave the
/// population totals alone still flips a large block of its verdicts, and a gentler version of the
/// same change flips as many (D16). The count does not scale with the size of the behaviour
/// change, so the gate cannot separate "LOD fidelity got worse" from "the trajectory moved". That
/// is a property of the measurement's shape, not of its thresholds — no tolerance repairs it.
///
/// The rate is the quantity that does separate them. A gameplay change moves both runs alike and
/// leaves the rate of separation where it was; a fidelity change bends it. So this measures two
/// things:
///
///  - ONSET — the first sample tick at which the two worlds are not identical, and what moved
///    first. Diagnostic. Between Full and Minimal it is expected to be early and says little on
///    its own; between two runs of the same tier it is the signal that something is unseeded.
///  - GROWTH — one scalar per sample tick, the curve. This is the artefact.
///
/// WHAT THE CURVE IS NOT. It is not an error measurement, and flatness is not the target. A
/// coarser decision cadence genuinely changes behaviour: a creature that re-steers every twentieth
/// tick arrives somewhere else, and it is supposed to. The curve says what that costs. The gate
/// built on it later must therefore assert that the curve has not WORSENED beyond a stated
/// tolerance — never that it be flat, which would be a demand that LOD do nothing.
///
/// WHY THERE ARE NOW THREE SCALARS AND NOT ONE. The first recorded curve was read back and the
/// combined scalar turned out to be mostly a position measurement: centroid and dispersion carried
/// 74% of it, hunger 18%, population 8%, age under 1%. Position is the most chaotic quantity the
/// simulation produces, which is the class of reading D16 established a gate cannot be built on.
/// Averaging it in unweighted with six other things does not dilute it, it hands it the reading.
/// So the components are split by what they answer:
///
///  - STATE — population, hunger, age. Whether the animals are in the same condition and the same
///    numbers. This is what the game counts, and what a decision cadence is supposed to preserve.
///  - POSITION — centroid, dispersion. WHERE the herd is. Diagnostic: it says the trajectory
///    moved, which a coarser cadence is entitled to do.
///
/// <see cref="Point.Divergence"/> is kept unchanged so curves recorded before the split still
/// compare, but <see cref="Point.StateDivergence"/> is the reading a gate should later be built on.
/// </summary>
public sealed class LodDivergenceCurve
{
    /// <summary>What a component answers. See the class remarks: position is diagnostic, not gate material.</summary>
    public enum Kind { State, Position }

    /// <summary>
    /// The components combined into the scalar, in order. Each is already dimensionless before it
    /// is combined, so no component can dominate by being measured in bigger units.
    /// </summary>
    public static readonly string[] ComponentNames =
    {
        "count", "hunger_mean", "hunger_spread", "age_mean", "age_spread", "centroid", "dispersion"
    };

    /// <summary>Which reading each component belongs to, index for index with <see cref="ComponentNames"/>.</summary>
    public static readonly Kind[] ComponentKinds =
    {
        Kind.State, Kind.State, Kind.State, Kind.State, Kind.State, Kind.Position, Kind.Position
    };

    /// <summary>
    /// The opening slice of the run, where the curve actually rises. Read from the first recorded
    /// curve: divergence climbs through roughly the first tenth and is near-flat after it, so a
    /// single number spanning the whole run averages a rise together with a plateau and describes
    /// neither.
    /// </summary>
    public const double GrowthWindowFraction = 0.10;

    /// <summary>The closing slice, where the curve has settled. The two windows are read separately.</summary>
    public const double PlateauWindowFraction = 0.40;

    public sealed class Point
    {
        public int Tick;
        /// <summary>The scalar: the mean over species of each species' own mean component divergence.</summary>
        public double Divergence;
        /// <summary>The same mean taken over the state components only. The reading that means something.</summary>
        public double StateDivergence;
        /// <summary>The same mean over the position components only. Diagnostic.</summary>
        public double PositionDivergence;
        public double[] Components = new double[ComponentNames.Length];
        public int SpeciesCompared;
        public bool ChecksumsDiffer;
        public int PopulationA, PopulationB;
    }

    public readonly List<Point> Points = new();

    /// <summary>First sample tick at which the two worlds are not identical; -1 if they never differ.</summary>
    public int OnsetTick = -1;

    /// <summary>What moved first at <see cref="OnsetTick"/>. Diagnostic, not a gate.</summary>
    public string OnsetDetail = "";

    public FingerprintStream? A, B;

    /// <summary>
    /// What this pair was recorded to answer, derived from the two streams rather than declared:
    /// two runs of one tier on one seed is the FLOOR (must read exactly zero), two runs of one tier
    /// on different seeds is the CEILING (two unrelated worlds), anything else is a FIDELITY pair.
    /// </summary>
    public string PairKind =>
        A is null || B is null ? "unknown"
        : A.Tier != B.Tier ? "fidelity"
        : A.Seed == B.Seed ? "floor"
        : "ceiling";

    /// <summary>Divergence at the last sample, and averaged over every sample.</summary>
    public double Final => Points.Count > 0 ? Points[^1].Divergence : 0;
    public double Mean => MeanOf(p => p.Divergence);
    public double StateMean => MeanOf(p => p.StateDivergence);
    public double PositionMean => MeanOf(p => p.PositionDivergence);

    private double MeanOf(Func<Point, double> select)
    {
        if (Points.Count == 0) return 0;
        double sum = 0;
        foreach (var p in Points) sum += select(p);
        return sum / Points.Count;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Windows
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One slice of the curve, averaged. The curve has two regimes and a reader needs both: the
    /// rise says how quickly two worlds part, the plateau says how far apart they end up sitting.
    /// </summary>
    public readonly struct Window
    {
        public readonly string Name;
        public readonly int FromTick, ToTick, Samples;
        public readonly double Divergence, State, Position;
        public readonly double[] Components;

        public Window(string name, int fromTick, int toTick, int samples,
                      double divergence, double state, double position, double[] components)
        {
            Name = name; FromTick = fromTick; ToTick = toTick; Samples = samples;
            Divergence = divergence; State = state; Position = position; Components = components;
        }
    }

    /// <summary>Average every reading over the samples in [fromTick, toTick].</summary>
    public Window Summarise(string name, int fromTick, int toTick)
    {
        var components = new double[ComponentNames.Length];
        double divergence = 0, state = 0, position = 0;
        int n = 0;

        foreach (var p in Points)
        {
            if (p.Tick < fromTick || p.Tick > toTick) continue;
            divergence += p.Divergence;
            state += p.StateDivergence;
            position += p.PositionDivergence;
            for (int i = 0; i < components.Length; i++) components[i] += p.Components[i];
            n++;
        }

        if (n > 0)
        {
            divergence /= n; state /= n; position /= n;
            for (int i = 0; i < components.Length; i++) components[i] /= n;
        }
        return new Window(name, fromTick, toTick, n, divergence, state, position, components);
    }

    /// <summary>Last sampled tick, which is the run length the windows are cut from.</summary>
    public int LastTick => Points.Count > 0 ? Points[^1].Tick : 0;

    /// <summary>The opening rise. See <see cref="GrowthWindowFraction"/>.</summary>
    public Window Growth => Summarise("growth", 0, (int)Math.Round(LastTick * GrowthWindowFraction));

    /// <summary>The settled tail. See <see cref="PlateauWindowFraction"/>.</summary>
    public Window Plateau => Summarise("plateau", (int)Math.Round(LastTick * (1.0 - PlateauWindowFraction)), LastTick);

    /// <summary>
    /// This curve's plateau expressed as a fraction of an unrelated-worlds plateau.
    ///
    /// THE ONE NUMBER THAT SAYS WHETHER ANY OF THIS MEASURES ANYTHING. Every component is clamped
    /// into [0,1] and the centroid term saturates by construction, so a curve that flattens has two
    /// possible explanations that no amount of staring at it separates: the two worlds stopped
    /// parting, or the scalar ran out of room. The ceiling control — one tier against itself on two
    /// different seeds — is what the scalar reads for two worlds with nothing in common. A fidelity
    /// curve sitting near that value is not reporting a degree of infidelity, it is reporting "not
    /// the same run", and no tolerance placed on it can mean anything.
    /// </summary>
    public static double CeilingFraction(double plateau, double ceilingPlateau)
        => ceilingPlateau > 0 ? plateau / ceilingPlateau : double.NaN;

    // ══════════════════════════════════════════════════════════════════════════
    // Comparison
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compare two recorded streams tick by tick. Only ticks sampled in both are compared; a
    /// stream that stopped early simply shortens the curve rather than inventing points.
    /// </summary>
    public static LodDivergenceCurve Compare(FingerprintStream a, FingerprintStream b)
    {
        var curve = new LodDivergenceCurve { A = a, B = b };
        var byTick = new Dictionary<int, FingerprintSample>(b.Samples.Count);
        foreach (var s in b.Samples) byTick[s.Tick] = s;

        foreach (var sa in a.Samples)
        {
            if (!byTick.TryGetValue(sa.Tick, out var sb)) continue;
            var point = ComparePoint(sa, sb);
            curve.Points.Add(point);

            if (curve.OnsetTick < 0 && point.ChecksumsDiffer)
            {
                curve.OnsetTick = point.Tick;
                curve.OnsetDetail = DescribeOnset(sa, sb, point);
            }
        }
        return curve;
    }

    private static Point ComparePoint(FingerprintSample a, FingerprintSample b)
    {
        var point = new Point
        {
            Tick = a.Tick,
            ChecksumsDiffer = a.Global.Checksum != b.Global.Checksum,
            PopulationA = a.Global.Count,
            PopulationB = b.Global.Count,
        };

        var species = new HashSet<int>(a.BySpecies.Keys);
        species.UnionWith(b.BySpecies.Keys);

        var totals = new double[ComponentNames.Length];
        double divergenceSum = 0, stateSum = 0, positionSum = 0;
        int compared = 0;

        foreach (int sid in species)
        {
            a.BySpecies.TryGetValue(sid, out var fa);
            b.BySpecies.TryGetValue(sid, out var fb);
            if (fa.Count == 0 && fb.Count == 0) continue;   // absent from both: nothing to compare

            var components = CompareSpecies(fa, fb);
            double all = 0, state = 0, position = 0;
            int stateN = 0, positionN = 0;
            for (int i = 0; i < components.Length; i++)
            {
                totals[i] += components[i];
                all += components[i];
                if (ComponentKinds[i] == Kind.State) { state += components[i]; stateN++; }
                else { position += components[i]; positionN++; }
            }
            divergenceSum += all / components.Length;
            stateSum += stateN > 0 ? state / stateN : 0;
            positionSum += positionN > 0 ? position / positionN : 0;
            compared++;
        }

        point.SpeciesCompared = compared;
        if (compared > 0)
        {
            point.Divergence = divergenceSum / compared;
            point.StateDivergence = stateSum / compared;
            point.PositionDivergence = positionSum / compared;
            for (int i = 0; i < totals.Length; i++) point.Components[i] = totals[i] / compared;
        }
        return point;
    }

    /// <summary>
    /// One species' divergence, component by component, each on that species' own scale.
    ///
    /// EVERY COMPONENT LANDS IN [0,1], AND THAT IS LOAD-BEARING. They are combined by an unweighted
    /// mean, so a component that can reach 6 while the others cannot pass 1 does not contribute to
    /// the scalar, it becomes the scalar. The first recording of this curve showed exactly that:
    /// unbounded centroid distance carried the whole reading and the other six were rounding error.
    ///
    /// Hunger and age arrive already divided by that species' own maximum, so they are compared as
    /// they stand. Population is divided by the larger of the two counts — the species' own
    /// numbers, so a herd of two thousand and a pack of twenty weigh the same. Spread is divided by
    /// the larger of the two spreads. Centroid distance is divided by the two spreads together,
    /// and saturates there: once the centroids are further apart than the populations are wide the
    /// two runs have put this species on different ground, and there is no further fidelity
    /// question that more distance answers. The curve therefore saturates rather than climbing, and
    /// a reader has to know that — it is why the components are written out alongside the scalar,
    /// and why the plateau is reported against a measured ceiling rather than on its own.
    ///
    /// The spatial scales are measured, not declared. No static field on a species says how widely
    /// it spreads; that is a property of the species AND the moment, and only the run has it. A
    /// one-tile floor keeps a group that has collapsed to a point from dividing by zero.
    ///
    /// A species alive in one run and gone in the other is total disagreement: every component is
    /// 1. Averaging the other six over an absent population would read as agreement about animals
    /// that are not there.
    /// </summary>
    private static double[] CompareSpecies(in Fingerprint a, in Fingerprint b)
    {
        var d = new double[ComponentNames.Length];
        if (a.Count == 0 || b.Count == 0)
        {
            Array.Fill(d, 1.0);
            return d;
        }

        double countScale = Math.Max(a.Count, b.Count);
        double spreadScale = Math.Max(Math.Max(a.Dispersion, b.Dispersion), 1.0);
        double apartScale = Math.Max(a.Dispersion + b.Dispersion, 1.0);
        double dx = a.CentroidX - b.CentroidX;
        double dy = a.CentroidY - b.CentroidY;

        d[0] = Math.Abs(a.Count - b.Count) / countScale;
        d[1] = Math.Abs(a.HungerMean - b.HungerMean);
        d[2] = Math.Abs(a.HungerSpread - b.HungerSpread);
        d[3] = Math.Abs(a.AgeMean - b.AgeMean);
        d[4] = Math.Abs(a.AgeSpread - b.AgeSpread);
        d[5] = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy) / apartScale);
        d[6] = Math.Min(1.0, Math.Abs(a.Dispersion - b.Dispersion) / spreadScale);
        return d;
    }

    /// <summary>
    /// Which species stopped matching first, and which component had moved furthest by then.
    /// Onset is a pointer at where to look, not a verdict: at the tick two worlds first differ the
    /// difference is by construction tiny, so the component named here is the earliest sign and
    /// not necessarily the cause.
    /// </summary>
    private static string DescribeOnset(FingerprintSample a, FingerprintSample b, Point point)
    {
        var moved = new List<string>();
        foreach (var (sid, fa) in a.BySpecies)
        {
            b.BySpecies.TryGetValue(sid, out var fb);
            if (fa.Checksum != fb.Checksum) moved.Add(SpeciesRegistry.GetById(sid)?.Name ?? sid.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var (sid, fb) in b.BySpecies)
            if (!a.BySpecies.ContainsKey(sid)) moved.Add(SpeciesRegistry.GetById(sid)?.Name ?? sid.ToString(CultureInfo.InvariantCulture));
        moved.Sort(StringComparer.Ordinal);

        int largest = 0;
        for (int i = 1; i < point.Components.Length; i++)
            if (point.Components[i] > point.Components[largest]) largest = i;

        string names = moved.Count == 0 ? "(none — global checksum only)" : string.Join(", ", moved);
        return FormattableString.Invariant(
            $"{moved.Count} species differ ({names}); largest component {ComponentNames[largest]}={point.Components[largest]:F6}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The artefact
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Write the curve. The header records everything needed to say what was measured; the commit
    /// it was measured at belongs in docs/changelog.md, with every other measured figure in this
    /// project.
    /// </summary>
    /// <param name="ceiling">The unrelated-worlds control this curve's plateau is quoted against.
    /// Null writes the plateau unqualified, which is a number without a scale.</param>
    public void Write(string path, LodDivergenceCurve? ceiling = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LOD divergence curve — how fast two tiers of the same scenario come apart.");
        sb.AppendLine("# The rate is the reading. Flatness is NOT the target: a coarser decision cadence");
        sb.AppendLine("# genuinely changes behaviour, and this measures what that costs. A gate built on this");
        sb.AppendLine("# file asserts the curve has not worsened beyond a stated tolerance, never that it is flat.");
        sb.AppendLine("# 'state' (count/hunger/age) is the reading; 'position' (centroid/dispersion) is diagnostic.");
        sb.AppendLine(FormattableString.Invariant(
            $"# pair={PairKind} scenario={A?.Scenario} ticks={A?.Ticks} sample={A?.SampleInterval} quantum={A?.Quantum}"));
        sb.AppendLine(FormattableString.Invariant(
            $"# a_tier={A?.Tier} a_seed={A?.Seed} b_tier={B?.Tier} b_seed={B?.Seed}"));
        sb.AppendLine(FormattableString.Invariant(
            $"# onset_tick={OnsetTick} onset={OnsetDetail}"));
        sb.AppendLine(FormattableString.Invariant(
            $"# divergence_final={Final:F6} divergence_mean={Mean:F6} state_mean={StateMean:F6} position_mean={PositionMean:F6} samples={Points.Count}"));

        foreach (var w in new[] { Growth, Plateau })
            sb.AppendLine(FormattableString.Invariant(
                $"# window_{w.Name}={w.FromTick}..{w.ToTick} n={w.Samples} divergence={w.Divergence:F6} state={w.State:F6} position={w.Position:F6}"));

        var overall = Summarise("all", 0, LastTick);
        {
            var g = Growth; var pl = Plateau;
            for (int i = 0; i < ComponentNames.Length; i++)
                sb.AppendLine(FormattableString.Invariant(
                    $"# component_{ComponentNames[i]} kind={ComponentKinds[i]} mean={overall.Components[i]:F6} growth={g.Components[i]:F6} plateau={pl.Components[i]:F6}"));
        }

        if (ceiling is not null)
        {
            var cp = ceiling.Plateau;
            var mp = Plateau;
            sb.AppendLine(FormattableString.Invariant(
                $"# ceiling_plateau divergence={cp.Divergence:F6} state={cp.State:F6} position={cp.Position:F6}"));
            double fd = CeilingFraction(mp.Divergence, cp.Divergence);
            double fs = CeilingFraction(mp.State, cp.State);
            double fp = CeilingFraction(mp.Position, cp.Position);
            sb.AppendLine(FormattableString.Invariant(
                $"# ceiling_fraction divergence={fd:F4} state={fs:F4} position={fp:F4}"));
        }

        sb.Append("sample_tick,divergence,state,position");
        foreach (string c in ComponentNames) sb.Append(',').Append(c);
        sb.AppendLine(",species_compared,checksums_differ,population_a,population_b");

        foreach (var p in Points)
        {
            sb.Append(FormattableString.Invariant(
                $"{p.Tick},{p.Divergence:F6},{p.StateDivergence:F6},{p.PositionDivergence:F6}"));
            foreach (double c in p.Components) sb.Append(FormattableString.Invariant($",{c:F6}"));
            sb.AppendLine(FormattableString.Invariant(
                $",{p.SpeciesCompared},{(p.ChecksumsDiffer ? 1 : 0)},{p.PopulationA},{p.PopulationB}"));
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }
}
