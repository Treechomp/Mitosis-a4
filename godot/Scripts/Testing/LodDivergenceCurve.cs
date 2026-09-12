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
/// </summary>
public sealed class LodDivergenceCurve
{
    /// <summary>
    /// The components combined into the scalar, in order. Each is already dimensionless before it
    /// is combined, so no component can dominate by being measured in bigger units.
    /// </summary>
    public static readonly string[] ComponentNames =
    {
        "count", "hunger_mean", "hunger_spread", "age_mean", "age_spread", "centroid", "dispersion"
    };

    public sealed class Point
    {
        public int Tick;
        /// <summary>The scalar: the mean over species of each species' own mean component divergence.</summary>
        public double Divergence;
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

    /// <summary>Divergence at the last sample, and averaged over every sample.</summary>
    public double Final => Points.Count > 0 ? Points[^1].Divergence : 0;
    public double Mean
    {
        get
        {
            if (Points.Count == 0) return 0;
            double sum = 0;
            foreach (var p in Points) sum += p.Divergence;
            return sum / Points.Count;
        }
    }

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
        double divergenceSum = 0;
        int compared = 0;

        foreach (int sid in species)
        {
            a.BySpecies.TryGetValue(sid, out var fa);
            b.BySpecies.TryGetValue(sid, out var fb);
            if (fa.Count == 0 && fb.Count == 0) continue;   // absent from both: nothing to compare

            var components = CompareSpecies(fa, fb);
            double mean = 0;
            for (int i = 0; i < components.Length; i++) { totals[i] += components[i]; mean += components[i]; }
            divergenceSum += mean / components.Length;
            compared++;
        }

        point.SpeciesCompared = compared;
        if (compared > 0)
        {
            point.Divergence = divergenceSum / compared;
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
    /// a reader has to know that — it is why the components are written out alongside the scalar.
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
    public void Write(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LOD divergence curve — how fast two tiers of the same scenario come apart.");
        sb.AppendLine("# The rate is the reading. Flatness is NOT the target: a coarser decision cadence");
        sb.AppendLine("# genuinely changes behaviour, and this measures what that costs. A gate built on this");
        sb.AppendLine("# file asserts the curve has not worsened beyond a stated tolerance, never that it is flat.");
        sb.AppendLine(FormattableString.Invariant(
            $"# scenario={A?.Scenario} seed={A?.Seed} ticks={A?.Ticks} sample={A?.SampleInterval}")
            + FormattableString.Invariant(
            $" quantum={A?.Quantum} a_tier={A?.Tier} b_tier={B?.Tier}"));
        sb.AppendLine(FormattableString.Invariant(
            $"# onset_tick={OnsetTick} onset={OnsetDetail}"));
        sb.AppendLine(FormattableString.Invariant(
            $"# divergence_final={Final:F6} divergence_mean={Mean:F6} samples={Points.Count}"));

        sb.Append("sample_tick,divergence");
        foreach (string c in ComponentNames) sb.Append(',').Append(c);
        sb.AppendLine(",species_compared,checksums_differ,population_a,population_b");

        foreach (var p in Points)
        {
            sb.Append(FormattableString.Invariant($"{p.Tick},{p.Divergence:F6}"));
            foreach (double c in p.Components) sb.Append(FormattableString.Invariant($",{c:F6}"));
            sb.AppendLine(FormattableString.Invariant(
                $",{p.SpeciesCompared},{(p.ChecksumsDiffer ? 1 : 0)},{p.PopulationA},{p.PopulationB}"));
        }

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }
}
