using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.SpeciesData;
using static Mitosis.ECS.EntityManager;

namespace Mitosis.Testing;

/// <summary>
/// What one simulation looked like at one tick, reduced to numbers two runs can be compared on.
///
/// EVERY FIGURE HERE IS ORDER-INDEPENDENT, and that is the whole design constraint. Entity ids
/// stop corresponding between two runs the moment a birth or a death happens in one and not the
/// other: slot 412 is a Deer in one run and a recycled Wolf in the other, so anything that pairs
/// entities by id compares unrelated animals and reports noise as divergence. Sums, means, spreads
/// and a multiset checksum survive that; per-entity diffs do not.
///
/// QUANTITIES ARE CARRIED ALREADY NORMALISED. Hunger is a fraction of that species' own
/// <see cref="SpeciesDefinition.MaxHunger"/> and age a fraction of its own
/// <see cref="SpeciesDefinition.MaxLifespan"/>, so a Shroomer and a Shark contribute on one scale —
/// the convention the trait-drift table in <see cref="PopulationSoakRunner"/> already uses
/// ("means as a fraction of the species value, so a trait reads the same whether it is measured in
/// ticks or tiles"). Positions stay in tiles; they are normalised at comparison time, against the
/// only spatial scale a species actually has (see <see cref="LodDivergenceCurve"/>).
/// </summary>
public struct Fingerprint
{
    public int Count;

    /// <summary>Mean and population standard deviation of hunger, as a fraction of species maximum.</summary>
    public double HungerMean, HungerSpread;

    /// <summary>Mean and population standard deviation of age, as a fraction of species lifespan.</summary>
    public double AgeMean, AgeSpread;

    /// <summary>Mean position, in tiles.</summary>
    public double CentroidX, CentroidY;

    /// <summary>Mean distance from the centroid, in tiles — how spread out this group is.</summary>
    public double Dispersion;

    /// <summary>
    /// Multiset hash over quantised (species, x, y). Two runs agree on this exactly while they are
    /// still the same world and differ once any creature is anywhere else; it is the onset signal,
    /// and it says nothing about how far apart they are, which is what the scalars are for.
    /// </summary>
    public ulong Checksum;
}

/// <summary>One sample tick: the whole population, and each species on its own.</summary>
public sealed class FingerprintSample
{
    public int Tick;
    public Fingerprint Global;
    public readonly Dictionary<int, Fingerprint> BySpecies = new();
}

/// <summary>A recorded run: the parameters it was taken under, and its samples in tick order.</summary>
public sealed class FingerprintStream
{
    public string Scenario = "";
    public string Tier = "";
    public int Seed;
    public int Ticks;
    public int SampleInterval = 1;
    public int Quantum = SimulationFingerprint.PositionQuantum;
    public readonly List<FingerprintSample> Samples = new();
}

/// <summary>
/// Capture, write and read fingerprint streams.
///
/// A stream is written by one process and read by another, never held alongside a second
/// simulation: <c>EcosystemLogger.Instance</c>, <c>SimRandom</c>'s seed and
/// <c>HuntFunnelProbe</c>'s counters are static, so two simulations in one process are two
/// simulations sharing one set of globals and neither is the run it claims to be.
/// </summary>
public static class SimulationFingerprint
{
    /// <summary>
    /// Position quantisation for the checksum, in steps per tile. Both runs execute the same
    /// arithmetic on the same machine, so this is not a tolerance for float noise — it is the
    /// resolution at which two worlds are called the same, and a finer one detects onset sooner.
    /// </summary>
    public const int PositionQuantum = 16;

    private const string Header =
        "tick,scope,species,count,hunger_mean,hunger_spread,age_mean,age_spread," +
        "centroid_x,centroid_y,dispersion,checksum";

    // ══════════════════════════════════════════════════════════════════════════
    // Capture
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Read the living population at this instant. Call it at tick end, after every system has run
    /// and newborns have been finalised, so the sample is a settled state and not a half-tick.
    ///
    /// Population is the population-CSV definition — living creatures only, with spores, nests,
    /// crystals and corpses excluded — so this instrument and the outcome gate count the same
    /// animals. (A spore carries Species(Shroomer) for type checks and would otherwise inflate the
    /// Shroomer column.)
    /// </summary>
    public static FingerprintSample Capture(EntityManager em, int tick)
    {
        var sample = new FingerprintSample { Tick = tick };
        var acc = new Dictionary<int, Accumulator>();
        var global = new Accumulator();

        foreach (int entity in em.AllEntities())
        {
            if (em.HasComponents(entity, ComponentFlags.Spore)) continue;
            if (em.HasComponents(entity, ComponentFlags.Nest)) continue;
            if (em.HasComponents(entity, ComponentFlags.Crystal)) continue;
            if (em.HasComponents(entity, ComponentFlags.Carrion)) continue;
            if (!em.HasComponents(entity, ComponentFlags.Species)) continue;

            int sid = em.Species[entity].SpeciesId;
            if (!acc.TryGetValue(sid, out var a)) acc[sid] = a = new Accumulator();

            // Fractions of this individual's own maxima, which are its species' values spread by
            // FounderSpread and then inherited. Dividing by the individual's own maximum rather
            // than the registry's keeps "how hungry is it" meaning the same thing for an animal
            // whose line has drifted to a larger stomach.
            if (em.HasComponents(entity, ComponentFlags.Hunger))
            {
                ref var h = ref em.Hungers[entity];
                if (h.Max > 0f) { a.AddHunger(h.Current / h.Max); global.AddHunger(h.Current / h.Max); }
            }
            if (em.HasComponents(entity, ComponentFlags.Age))
            {
                ref var g = ref em.Ages[entity];
                if (g.MaxLifespan > 0) { a.AddAge((double)g.Current / g.MaxLifespan); global.AddAge((double)g.Current / g.MaxLifespan); }
            }
            if (em.HasComponents(entity, ComponentFlags.Position))
            {
                ref var p = ref em.Positions[entity];
                ulong h = HashPlacement(sid, p.X, p.Y);
                a.AddPosition(p.X, p.Y, h);
                global.AddPosition(p.X, p.Y, h);
            }

            a.Count++;
            global.Count++;
        }

        // Dispersion needs the centroid, so it needs a second pass. The population is a few
        // thousand entities at most and this runs once per sample tick.
        foreach (var a in acc.Values) a.PrepareCentroid();
        global.PrepareCentroid();

        foreach (int entity in em.AllEntities())
        {
            if (em.HasComponents(entity, ComponentFlags.Spore)) continue;
            if (em.HasComponents(entity, ComponentFlags.Nest)) continue;
            if (em.HasComponents(entity, ComponentFlags.Crystal)) continue;
            if (em.HasComponents(entity, ComponentFlags.Carrion)) continue;
            if (!em.HasComponents(entity, ComponentFlags.Species | ComponentFlags.Position)) continue;

            ref var p = ref em.Positions[entity];
            acc[em.Species[entity].SpeciesId].AddDistanceFromCentroid(p.X, p.Y);
            global.AddDistanceFromCentroid(p.X, p.Y);
        }

        sample.Global = global.ToFingerprint();
        foreach (var (sid, a) in acc) sample.BySpecies[sid] = a.ToFingerprint();
        return sample;
    }

    /// <summary>
    /// One creature's contribution to the checksum: its species and its position quantised to
    /// <see cref="PositionQuantum"/> steps per tile, run through a 64-bit mixer. Contributions are
    /// summed, and addition is commutative — which is the point. Two runs match exactly when the
    /// same multiset of creatures stands on the same quantised tiles, whatever order they are
    /// stored in.
    /// </summary>
    private static ulong HashPlacement(int speciesId, float x, float y)
    {
        long qx = (long)Math.Floor(x * PositionQuantum);
        long qy = (long)Math.Floor(y * PositionQuantum);
        ulong h = Mix((ulong)speciesId * 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ (ulong)qx);
        h = Mix(h ^ ((ulong)qy << 1));
        return h;
    }

    /// <summary>splitmix64's finaliser — cheap, and it spreads a small change over every bit.</summary>
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private sealed class Accumulator
    {
        public int Count;
        private double _hungerSum, _hungerSq; private int _hungerN;
        private double _ageSum, _ageSq; private int _ageN;
        private double _xSum, _ySum; private int _posN;
        private double _distSum;
        private ulong _checksum;
        private double _cx, _cy;

        public void AddHunger(double v) { _hungerSum += v; _hungerSq += v * v; _hungerN++; }
        public void AddAge(double v) { _ageSum += v; _ageSq += v * v; _ageN++; }

        public void AddPosition(double x, double y, ulong hash)
        {
            _xSum += x; _ySum += y; _posN++;
            unchecked { _checksum += hash; }
        }

        public void PrepareCentroid()
        {
            _cx = _posN > 0 ? _xSum / _posN : 0;
            _cy = _posN > 0 ? _ySum / _posN : 0;
        }

        public void AddDistanceFromCentroid(double x, double y)
            => _distSum += Math.Sqrt((x - _cx) * (x - _cx) + (y - _cy) * (y - _cy));

        public Fingerprint ToFingerprint() => new()
        {
            Count = Count,
            HungerMean = Mean(_hungerSum, _hungerN),
            HungerSpread = Spread(_hungerSum, _hungerSq, _hungerN),
            AgeMean = Mean(_ageSum, _ageN),
            AgeSpread = Spread(_ageSum, _ageSq, _ageN),
            CentroidX = _cx,
            CentroidY = _cy,
            Dispersion = _posN > 0 ? _distSum / _posN : 0,
            Checksum = _checksum,
        };

        private static double Mean(double sum, int n) => n > 0 ? sum / n : 0;

        /// <summary>
        /// Population standard deviation. Clamped at zero before the root: with sums of squares
        /// the variance of an almost-uniform group can land a hair below zero and produce NaN,
        /// which then propagates through every later comparison.
        /// </summary>
        private static double Spread(double sum, double sumSq, int n)
        {
            if (n <= 0) return 0;
            double mean = sum / n;
            return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Stream I/O
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Append-as-you-go writer, so a run that aborts still leaves the samples it took. Floats are
    /// written with <see cref="CultureInfo.InvariantCulture"/>, as every CSV in this project is,
    /// so a comma-decimal locale cannot corrupt the columns.
    /// </summary>
    public sealed class Writer : IDisposable
    {
        private readonly StreamWriter _out;

        public Writer(string path, FingerprintStream meta)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _out = new StreamWriter(path, append: false);
            _out.WriteLine("# lod-divergence fingerprint stream");
            _out.WriteLine(FormattableString.Invariant(
                $"# scenario={meta.Scenario} seed={meta.Seed} ticks={meta.Ticks} tier={meta.Tier}")
                + FormattableString.Invariant(
                $" sample={meta.SampleInterval} quantum={meta.Quantum}"));
            _out.WriteLine(Header);
        }

        public void Write(FingerprintSample s)
        {
            Row(s.Tick, "global", "", s.Global);
            foreach (var (sid, f) in s.BySpecies)
                Row(s.Tick, "species", SpeciesRegistry.GetById(sid)?.Name ?? sid.ToString(CultureInfo.InvariantCulture), f);
        }

        /// <summary>One row, columns in <see cref="Header"/> order, every field formatted
        /// invariantly so a comma-decimal locale cannot split a column in two.</summary>
        private void Row(int tick, string scope, string species, in Fingerprint f)
        {
            var c = CultureInfo.InvariantCulture;
            _out.WriteLine(string.Join(',',
                tick.ToString(c), scope, species, f.Count.ToString(c),
                f.HungerMean.ToString("F6", c), f.HungerSpread.ToString("F6", c),
                f.AgeMean.ToString("F6", c), f.AgeSpread.ToString("F6", c),
                f.CentroidX.ToString("F4", c), f.CentroidY.ToString("F4", c),
                f.Dispersion.ToString("F4", c), f.Checksum.ToString(c)));
        }

        public void Dispose() => _out.Dispose();
    }

    /// <summary>Read a stream back. Species are keyed by name, since that is what was written.</summary>
    public static FingerprintStream Read(string path)
    {
        var stream = new FingerprintStream();
        FingerprintSample? current = null;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) { ParseMeta(line, stream); continue; }
            if (line.StartsWith("tick,", StringComparison.Ordinal)) continue;

            var c = line.Split(',');
            if (c.Length < 12) continue;

            int tick = int.Parse(c[0], CultureInfo.InvariantCulture);
            if (current == null || current.Tick != tick)
            {
                current = new FingerprintSample { Tick = tick };
                stream.Samples.Add(current);
            }

            var f = new Fingerprint
            {
                Count = int.Parse(c[3], CultureInfo.InvariantCulture),
                HungerMean = Num(c[4]), HungerSpread = Num(c[5]),
                AgeMean = Num(c[6]), AgeSpread = Num(c[7]),
                CentroidX = Num(c[8]), CentroidY = Num(c[9]),
                Dispersion = Num(c[10]),
                Checksum = ulong.Parse(c[11], CultureInfo.InvariantCulture),
            };

            if (c[1] == "global") current.Global = f;
            else current.BySpecies[SpeciesRegistry.GetId(c[2])] = f;
        }
        return stream;
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static void ParseMeta(string line, FingerprintStream stream)
    {
        foreach (string token in line.TrimStart('#', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = token.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "scenario": stream.Scenario = kv[1]; break;
                case "tier": stream.Tier = kv[1]; break;
                case "seed": stream.Seed = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "ticks": stream.Ticks = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "sample": stream.SampleInterval = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
                case "quantum": stream.Quantum = int.Parse(kv[1], CultureInfo.InvariantCulture); break;
            }
        }
    }
}
