using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mitosis.Systems;

/// <summary>
/// Why one species' hunts fail, counted stage by stage.
///
/// WHY: "Sectid kills_made = 0 over 20,000 ticks" says the hunt path produces nothing, and says
/// nothing about WHERE it produces nothing. Between a neighbour query and a corpse there are ten
/// places a candidate can be dropped, and reading the code cannot tell you which one is running
/// hot — every one of them is defensible in isolation. So count them.
///
/// This is a FUNNEL: each row is the same population of candidates, narrowed. If exactly one
/// stage is swallowing everything, its column is where the numbers fall off a cliff.
///
/// DISABLED BY DEFAULT and inert when disabled: every call site is guarded by
/// <see cref="Enabled"/>, which no shipping path sets. It counts and it divides; it never
/// branches the simulation, touches the RNG, or writes a component. Enabling it must not change
/// a single tick of the run — the soak runner proves that by comparing an instrumented run's
/// population curve against an uninstrumented one at the same seed.
/// </summary>
public static class HuntFunnelProbe
{
    /// <summary>Master switch. False everywhere except a diagnostic run.</summary>
    public static bool Enabled;

    /// <summary>The one species counted. Everything else is ignored, hot loop untouched.</summary>
    public static int SpeciesId = -1;

    /// <summary>True when this entity's species is the one under the probe.</summary>
    public static bool Watching(int speciesId) => Enabled && speciesId == SpeciesId;

    // ── Stage counters, reset every flush ────────────────────────────────────
    // Search-level: how often the acquisition block ran at all.
    public static long Searches, TrackSearches;
    public static long DueTicks, HibernatingSkips, SiegeSkips, HadTargetAlready;

    // Candidate-level, in the order the loop applies them.
    public static long Candidates;          // returned by the spatial query
    public static long RejectAvoid;         // recently-abandoned blacklist
    public static long RejectSelfOrDead;
    public static long RejectTerrain;       // prey standing on a tile we cannot enter
    public static long RejectSpecies;       // not prey / own kind / unhuntable / diet list
    public static long RejectMassSolo;      // mass gate while hunting alone
    public static long RejectMassSwarm;     // mass gate with swarm mass applied
    public static long RejectOutOfRange;    // inside the query radius, outside hunt range
    public static long CapHits;             // MaxHuntCandidates cut the loop short
    public static long RejectWaterPath;     // prospective best sat across too much water
    public static long Scored;              // candidates that reached scoring

    // Outcomes.
    public static long Acquired, TrackAcquired;
    public static long AcquiredSpore;           // target was a spore or immature Shroomer
    public static long AcquiredSporeOverLive;   // ... and a live non-spore candidate was available
    public static long EngageInRange;           // target within attack reach on a due tick
    public static long EngageInRangeSpore;
    public static long AttacksAttempted;        // in reach AND off cooldown
    public static long AttacksLanded;           // damage actually applied
    public static long Kills;

    // Distances. Sum + count so the flush can mean them without keeping samples.
    private static double _nearestEligibleSum;
    private static long _nearestEligibleCount;
    public static long SearchesWithNoEligible;

    /// <summary>Nearest scored candidate for one search, or a search that found none.</summary>
    public static void RecordNearestEligible(float dist, bool any)
    {
        if (!any) { SearchesWithNoEligible++; return; }
        _nearestEligibleSum += dist;
        _nearestEligibleCount++;
    }

    /// <summary>
    /// Per-flush census values the hunt path cannot see (they need a world pass). Set by the
    /// runner immediately before <see cref="Flush"/>.
    /// </summary>
    public static int CensusPopulation, CensusHibernating;
    public static float CensusMeanDistToNest = -1f, CensusMeanHungerPct = -1f;

    // ── Output ───────────────────────────────────────────────────────────────

    private static StreamWriter? _out;
    private static int _lastFlushTick;

    private const string Header =
        "tick,population,hibernating,mean_dist_to_nest,mean_hunger_pct," +
        "due_ticks,skip_hibernating,skip_siege,had_target," +
        "searches,candidates,rej_avoid,rej_self_dead,rej_terrain,rej_species," +
        "rej_mass_solo,rej_mass_swarm,rej_out_of_range,cap_hits,rej_water_path,scored," +
        "searches_no_eligible,mean_nearest_eligible," +
        "acquired,acquired_spore,acquired_spore_over_live," +
        "track_searches,track_acquired," +
        "engage_in_range,engage_in_range_spore,attacks_attempted,attacks_landed,kills";

    /// <summary>Arm the probe for one species and open its CSV. Call once, before the run.</summary>
    public static void Begin(int speciesId, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _out = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        _out.WriteLine(Header);
        SpeciesId = speciesId;
        _lastFlushTick = 0;
        Reset();
        Enabled = true;
    }

    /// <summary>Write one window's row and start the next. Rows are cumulative per window.</summary>
    public static void Flush(int tick)
    {
        if (_out == null) return;
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3:0.##},{4:0.#},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15}," +
            "{16},{17},{18},{19},{20},{21},{22:0.##},{23},{24},{25},{26},{27},{28},{29},{30},{31},{32}",
            tick, CensusPopulation, CensusHibernating, CensusMeanDistToNest, CensusMeanHungerPct,
            DueTicks, HibernatingSkips, SiegeSkips, HadTargetAlready,
            Searches, Candidates, RejectAvoid, RejectSelfOrDead, RejectTerrain, RejectSpecies,
            RejectMassSolo, RejectMassSwarm, RejectOutOfRange, CapHits, RejectWaterPath, Scored,
            SearchesWithNoEligible,
            _nearestEligibleCount > 0 ? _nearestEligibleSum / _nearestEligibleCount : -1d,
            Acquired, AcquiredSpore, AcquiredSporeOverLive,
            TrackSearches, TrackAcquired,
            EngageInRange, EngageInRangeSpore, AttacksAttempted, AttacksLanded, Kills));
        _lastFlushTick = tick;
        Reset();
    }

    /// <summary>Flush if a window has elapsed. Returns true when a row was written.</summary>
    public static bool FlushIfDue(int tick, int window)
    {
        if (!Enabled || tick - _lastFlushTick < window) return false;
        Flush(tick);
        return true;
    }

    public static void End()
    {
        Enabled = false;
        _out?.Dispose();
        _out = null;
    }

    private static void Reset()
    {
        Searches = TrackSearches = 0;
        DueTicks = HibernatingSkips = SiegeSkips = HadTargetAlready = 0;
        Candidates = RejectAvoid = RejectSelfOrDead = RejectTerrain = RejectSpecies = 0;
        RejectMassSolo = RejectMassSwarm = RejectOutOfRange = CapHits = RejectWaterPath = 0;
        Scored = Acquired = TrackAcquired = AcquiredSpore = AcquiredSporeOverLive = 0;
        EngageInRange = EngageInRangeSpore = AttacksAttempted = AttacksLanded = Kills = 0;
        _nearestEligibleSum = 0;
        _nearestEligibleCount = 0;
        SearchesWithNoEligible = 0;
    }
}

/// <summary>
/// The hunt path's verdict on one prey candidate. Replaces a bool so the funnel probe can say
/// WHICH gate dropped a candidate; the hunt path itself only ever asks whether it is
/// <see cref="Eligible"/>, so the extra detail costs the simulation nothing.
/// </summary>
public enum PreyReject
{
    Eligible = 0,
    SelfOrDead,
    /// <summary>Standing on a tile this hunter cannot enter at all.</summary>
    Terrain,
    /// <summary>Not prey, own kind, unhuntable, off an exclusive diet, or below the fallback tier.</summary>
    Species,
    /// <summary>Heavier than this hunter's (possibly swarm-inflated) mass allowance.</summary>
    Mass
}
