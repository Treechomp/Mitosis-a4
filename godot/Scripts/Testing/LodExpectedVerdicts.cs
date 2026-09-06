using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Mitosis.Testing;

/// <summary>
/// How one metric's current verdict compares to the verdict that was recorded for it.
/// </summary>
public enum VerdictChange
{
    /// <summary>Recorded and observed agree. The overwhelming majority; never printed.</summary>
    Known,
    /// <summary>Recorded as passing, observed failing. THE thing this file exists to catch.</summary>
    Regression,
    /// <summary>Recorded as failing, observed passing. Good news that must be re-recorded.</summary>
    Improvement,
    /// <summary>Observed but not recorded — a metric or scenario was added.</summary>
    DriftAdded,
    /// <summary>Recorded but not observed — a metric or scenario was removed or renamed.</summary>
    DriftRemoved
}

/// <summary>
/// The recorded verdict of every (scenario, metric) pair, and the comparison of a run against it.
///
/// WHY: the differential produces forty standing failures for reasons that are written down and
/// accepted, so it exits 1 on every run and has never once exited 0. A gate that is always red
/// carries no signal — a NEW regression is indistinguishable from the forty known ones except by
/// a human diffing a markdown table by eye, which is exactly the failure mode the test was built
/// to replace. (§6.1 stated the LOD rate rule in prose, and it was broken three times anyway.)
///
/// So the forty are written down in a machine-readable file and the gate fires on the DIFFERENCE.
/// The markdown baseline explains WHY each exception exists; this CSV is the contract.
///
/// Comparison is deliberately binary — failing or not failing. `ok`, `within_noise` and
/// `below_min_count` all mean "this metric is not currently reporting a rate bug", and a metric
/// moving between them is not information: `within_noise` and `below_min_count` in particular are
/// both "this metric had nothing to say", and which of the two applies depends on where the dice
/// fell. Gating on that distinction would manufacture exactly the flapping this file removes.
/// The exact verdict is still RECORDED, because it is worth reading; it is just not compared.
/// </summary>
public sealed class LodExpectedVerdicts
{
    /// <summary>One recorded row. Key is (Scenario, Metric); Verdict is the exact string.</summary>
    public readonly record struct Row(string Scenario, string Metric, string Verdict);

    /// <summary>One disagreement between what was recorded and what this run produced.</summary>
    public readonly record struct Difference(
        VerdictChange Kind, string Scenario, string Metric, string Expected, string Actual);

    private const string Header = "scenario,metric,verdict";

    private readonly Dictionary<(string, string), string> _rows = new();

    public int Count => _rows.Count;

    /// <summary>
    /// The committed contract, next to the prose that explains it. Under docs/ rather than logs/
    /// on purpose: logs/ is run output and is not committed, and an expected file that is not
    /// committed means nothing at all.
    /// </summary>
    public static string DefaultPath()
        => Path.GetFullPath(Path.Combine(
            ProjectSettings.GlobalizePath("res://"), "..", "docs", "lod-differential-expected.csv"));

    /// <summary>
    /// Is this verdict a failure? The one distinction the comparison makes. Anything that is not
    /// the literal FAIL verdict is "quiet" — see the class comment for why the three quiet
    /// verdicts are not told apart.
    /// </summary>
    public static bool IsFail(string verdict)
        => string.Equals(verdict, "FAIL", StringComparison.Ordinal);

    public static bool TryLoad(string path, out LodExpectedVerdicts loaded, out string error)
    {
        loaded = new LodExpectedVerdicts();
        error = "";
        if (!File.Exists(path))
        {
            error = $"no expected-verdict file at {path}";
            return false;
        }

        int lineNo = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            // Matched by content, not by line number: the header sits below a comment block, so
            // "line 1" skipped a comment and let the header through as a row named
            // (scenario, metric) — which then reported itself as DRIFT on every run.
            if (string.Equals(line, Header, StringComparison.Ordinal)) continue;

            var parts = line.Split(',');
            if (parts.Length != 3)
            {
                error = $"{path}:{lineNo}: expected 3 columns ({Header}), got {parts.Length}";
                return false;
            }
            loaded._rows[(parts[0].Trim(), parts[1].Trim())] = parts[2].Trim();
        }
        return true;
    }

    /// <summary>
    /// Overwrite the contract with what this run produced. Rows are sorted so that re-recording
    /// an unchanged build is a no-op in git, and a real change is a readable diff.
    /// </summary>
    public static void Save(string path, IReadOnlyList<Row> rows)
    {
        var sorted = new List<Row>(rows);
        sorted.Sort((a, b) =>
        {
            int s = string.CompareOrdinal(a.Scenario, b.Scenario);
            return s != 0 ? s : string.CompareOrdinal(a.Metric, b.Metric);
        });

        var sb = new StringBuilder();
        sb.AppendLine("# Recorded LOD-differential verdicts — the machine-checkable contract.");
        sb.AppendLine("# Regenerate with: ... res://Scenes/LodDifferential.tscn -- --record");
        sb.AppendLine("# Accepting a new exception means re-recording AND writing down the reason");
        sb.AppendLine("# in docs/lod-differential-baseline.md. An unexplained row here is a bug");
        sb.AppendLine("# that has been made invisible.");
        sb.AppendLine(Header);
        foreach (var r in sorted)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Scenario},{r.Metric},{r.Verdict}"));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Every disagreement between the recorded verdicts and this run's.
    ///
    /// <paramref name="scenariosRun"/> scopes the "recorded but not observed" test: a run
    /// filtered with --scenario must not report every metric of every OTHER scenario as removed.
    /// Pass null when the run was unfiltered, so that a deleted or renamed scenario is caught.
    /// </summary>
    public List<Difference> CompareTo(IReadOnlyList<Row> actual, IReadOnlySet<string>? scenariosRun)
    {
        var differences = new List<Difference>();
        var seen = new HashSet<(string, string)>();

        foreach (var a in actual)
        {
            var key = (a.Scenario, a.Metric);
            seen.Add(key);
            if (!_rows.TryGetValue(key, out string? expected))
            {
                differences.Add(new Difference(
                    VerdictChange.DriftAdded, a.Scenario, a.Metric, "-", a.Verdict));
                continue;
            }
            bool wasFail = IsFail(expected);
            bool isFail = IsFail(a.Verdict);
            if (wasFail == isFail) continue;
            differences.Add(new Difference(
                isFail ? VerdictChange.Regression : VerdictChange.Improvement,
                a.Scenario, a.Metric, expected, a.Verdict));
        }

        foreach (var ((scenario, metric), expected) in _rows)
        {
            if (seen.Contains((scenario, metric))) continue;
            if (scenariosRun != null && !scenariosRun.Contains(scenario)) continue;
            differences.Add(new Difference(
                VerdictChange.DriftRemoved, scenario, metric, expected, "-"));
        }

        differences.Sort((x, y) =>
        {
            int k = x.Kind.CompareTo(y.Kind);
            if (k != 0) return k;
            int s = string.CompareOrdinal(x.Scenario, y.Scenario);
            return s != 0 ? s : string.CompareOrdinal(x.Metric, y.Metric);
        });
        return differences;
    }
}
